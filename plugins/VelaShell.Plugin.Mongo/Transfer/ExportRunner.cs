using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Ui;

namespace VelaShell.Plugin.Mongo.Transfer;

/// <summary>导出的一个来源集合。</summary>
/// <param name="Info">集合信息。</param>
/// <param name="Columns">CSV / Excel 的列;<see langword="null" /> = 运行时按抽样自动生成(多集合导出)。</param>
internal sealed record ExportSource(CollectionInfo Info, IReadOnlyList<ExportColumn>? Columns = null);

/// <summary>一次导出要做的事。</summary>
internal sealed record ExportJob
{
    /// <summary>库名。</summary>
    public required string Database { get; init; }

    /// <summary>来源集合。</summary>
    public required IReadOnlyList<ExportSource> Sources { get; init; }

    /// <summary>格式。</summary>
    public required ExportFormat Format { get; init; }

    /// <summary>目标:单文件路径,或目录(<see cref="TargetIsFolder" />)。</summary>
    public required string Target { get; init; }

    /// <summary>目标是目录(BSON 转储、多集合的 JSON / CSV:一个集合一个文件)。</summary>
    public bool TargetIsFolder { get; init; }

    /// <summary>筛选条件。</summary>
    public BsonDocument Filter { get; init; } = [];

    /// <summary>排序。</summary>
    public BsonDocument? Sort { get; init; }

    /// <summary>投影(JSON / Shell 只导出勾选的字段时用)。</summary>
    public BsonDocument? Projection { get; init; }

    /// <summary>条数上限;0 = 不限。</summary>
    public int Limit { get; init; }

    /// <summary>CSV 选项。</summary>
    public CsvOptions Csv { get; init; } = new();

    /// <summary>JSON 选项。</summary>
    public JsonOptions Json { get; init; } = new();

    /// <summary>Excel 选项。</summary>
    public ExcelOptions Excel { get; init; } = new();

    /// <summary>转储选项。</summary>
    public DumpOptions Dump { get; init; } = new();

    /// <summary>Shell 脚本选项。</summary>
    public ShellOptions Shell { get; init; } = new();

    /// <summary>预计总文档数(进度条分母;未知为 0)。</summary>
    public long EstimatedTotal { get; init; }

    /// <summary>某种格式的扩展名。</summary>
    public static string Extension(ExportFormat format, JsonOptions json) => format switch
    {
        ExportFormat.Json => json.Lines ? ".jsonl" : ".json",
        ExportFormat.Csv => ".csv",
        ExportFormat.Excel => ".xlsx",
        ExportFormat.Shell => ".js",
        _ => ".bson"
    };
}

/// <summary>
/// 执行导出:游标逐批拉、写出器逐份写,**从不把整个集合装进内存**。
/// <para>
/// 单文件先写到 <c>目标.part</c>,成功后再改名覆盖 —— 中途取消或失败时,旧文件原封不动,
/// 不会留下一个看起来完整、其实只写了一半的导出文件。
/// </para>
/// </summary>
internal static class ExportRunner
{
    /// <summary>打在导出游标上的 comment(在 currentOp 里认得出是谁)。</summary>
    public const string Comment = "velashell-export";

    /// <summary>跑一次导出。</summary>
    public static async Task<ExportResult> RunAsync(
        MongoConnection connection,
        ExportJob job,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var state = new RunState(job, progress, watch);
        var files = new List<string>();
        var created = new List<string>();
        try
        {
            if (job.Format == ExportFormat.BsonDump)
            {
                await DumpAsync(connection, job, state, files, created, cancellationToken).ConfigureAwait(false);
            }
            else if (job.TargetIsFolder)
            {
                Directory.CreateDirectory(job.Target);
                foreach (ExportSource source in job.Sources)
                {
                    string path = Path.Combine(job.Target, TransferText.SafeFileName(source.Info.Name) + ExportJob.Extension(job.Format, job.Json));
                    await WriteFileAsync(connection, job, [source], path, state, created, cancellationToken).ConfigureAwait(false);
                    files.Add(path);
                }
            }
            else
            {
                await WriteFileAsync(connection, job, job.Sources, job.Target, state, created, cancellationToken).ConfigureAwait(false);
                files.Add(job.Target);
            }
        }
        catch
        {
            // 失败或取消:删掉这一次新建的文件(.part 与转储文件);已经改名落定的完整文件保留。
            foreach (string path in created)
            {
                TryDelete(path);
            }
            throw;
        }
        state.Report(force: true);
        return new(state.Documents, state.Bytes, watch.Elapsed, files);
    }

    private static async Task WriteFileAsync(
        MongoConnection connection,
        ExportJob job,
        IReadOnlyList<ExportSource> sources,
        string path,
        RunState state,
        List<string> created,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        string part = path + ".part";
        created.Add(part);
        var counting = new CountingStream(new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20));
        state.Attach(counting);
        using (ExportWriter writer = CreateWriter(job, counting))
        {
            foreach (ExportSource source in sources)
            {
                IReadOnlyList<ExportColumn> columns = source.Columns
                    ?? await AutoColumnsAsync(connection, job, source.Info.Name, cancellationToken).ConfigureAwait(false);
                state.Collection = source.Info.Name;
                writer.Begin(source.Info.Name, columns);
                await StreamAsync(connection.Collection(job.Database, source.Info.Name), job, writer.Write, state, cancellationToken)
                    .ConfigureAwait(false);
                writer.End();
            }
            writer.Complete();
        }
        state.Detach();
        File.Move(part, path, overwrite: true);
        created.Remove(part);
    }

    private static async Task DumpAsync(
        MongoConnection connection,
        ExportJob job,
        RunState state,
        List<string> files,
        List<string> created,
        CancellationToken cancellationToken)
    {
        bool gzip = job.Dump.Gzip;
        foreach (ExportSource source in job.Sources)
        {
            CollectionInfo info = source.Info;
            state.Collection = info.Name;
            IReadOnlyList<BsonDocument> indexes = info.Kind == CollectionKind.View
                ? []
                : await connection.ListIndexesAsync(job.Database, info.Name, cancellationToken).ConfigureAwait(false);
            Guid? uuid = await UuidAsync(connection, job.Database, info.Name, cancellationToken).ConfigureAwait(false);
            if (info.Kind != CollectionKind.View)
            {
                string data = BsonDump.DataPath(job.Target, job.Database, info.Name, gzip);
                created.Add(data);
                var counting = new CountingStream(BsonDump.OpenWrite(data, gzip));
                state.Attach(counting);
                using (var writer = new BsonExportWriter(counting))
                {
                    await StreamAsync(connection.Collection(job.Database, info.Name), job with { Projection = null }, writer.Write, state, cancellationToken)
                        .ConfigureAwait(false);
                    writer.End();
                }
                state.Detach();
                files.Add(data);
            }
            string metadataPath = BsonDump.MetadataPath(job.Target, job.Database, info.Name, gzip);
            created.Add(metadataPath);
            await using (Stream metadata = BsonDump.OpenWrite(metadataPath, gzip))
            await using (var text = new StreamWriter(metadata, new System.Text.UTF8Encoding(false)))
            {
                await text.WriteAsync(BsonDump.MetadataJson(BsonDump.Metadata(info, indexes, uuid))).ConfigureAwait(false);
            }
            files.Add(metadataPath);
            // 这个集合完整落盘了:之后再取消也不删它(用户拿到的是"转储到一半"的目录,但每个文件都是好的)。
            created.Clear();
        }
    }

    private static async Task<Guid?> UuidAsync(MongoConnection connection, string database, string collection, CancellationToken cancellationToken)
    {
        try
        {
            using IAsyncCursor<BsonDocument> cursor = await connection.Database(database).ListCollectionsAsync(
                new ListCollectionsOptions { Filter = new BsonDocument("name", collection) }, cancellationToken).ConfigureAwait(false);
            BsonDocument? first = await cursor.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (first?.GetValue("info", new BsonDocument()) is BsonDocument info
                && info.TryGetValue("uuid", out BsonValue uuid) && uuid is BsonBinaryData binary
                && binary.SubType is BsonBinarySubType.UuidStandard or BsonBinarySubType.UuidLegacy)
            {
                return new Guid(binary.Bytes, bigEndian: true);
            }
        }
        catch (MongoException)
        {
        }
        return null;
    }

    private static async Task StreamAsync(
        IMongoCollection<BsonDocument> collection,
        ExportJob job,
        Action<BsonDocument> write,
        RunState state,
        CancellationToken cancellationToken)
    {
        var options = new FindOptions<BsonDocument>
        {
            BatchSize = 1000,
            Comment = Comment
        };
        if (job.Sort is { ElementCount: > 0 } sort)
        {
            options.Sort = sort;
        }
        if (job.Projection is { ElementCount: > 0 } projection)
        {
            options.Projection = projection;
        }
        if (job.Limit > 0)
        {
            options.Limit = job.Limit;
        }
        using IAsyncCursor<BsonDocument> cursor = await collection.FindAsync(job.Filter, options, cancellationToken).ConfigureAwait(false);
        while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (BsonDocument document in cursor.Current)
            {
                write(document);
                state.Documents++;
            }
            cancellationToken.ThrowIfCancellationRequested();
            state.Report(force: false);
        }
    }

    /// <summary>多集合导出时每个集合的列:抽样 200 份,按 CSV 的嵌套选项展开。</summary>
    private static async Task<IReadOnlyList<ExportColumn>> AutoColumnsAsync(
        MongoConnection connection,
        ExportJob job,
        string collection,
        CancellationToken cancellationToken)
    {
        if (job.Format is not (ExportFormat.Csv or ExportFormat.Excel))
        {
            return [];
        }
        IReadOnlyList<BsonDocument> sample = await SchemaSampler.SampleAsync(
            connection.Collection(job.Database, collection), job.Filter, 200, cancellationToken).ConfigureAwait(false);
        bool flatten = job.Format == ExportFormat.Excel || job.Csv.Nested == NestedMode.Flatten;
        return
        [
            .. SchemaSampler.Analyze(sample, flatten)
                .Select(static f => new ExportColumn(f.Path, f.Path, f.DefaultConversion))
        ];
    }

    /// <summary>按格式建写出器。</summary>
    internal static ExportWriter CreateWriter(ExportJob job, Stream output) => job.Format switch
    {
        ExportFormat.Json => new JsonExportWriter(output, job.Json),
        ExportFormat.Csv => new CsvExportWriter(output, job.Csv),
        ExportFormat.Excel => new ExcelExportWriter(output, job.Excel),
        ExportFormat.Shell => new ShellExportWriter(output, job.Database, job.Shell),
        _ => new BsonExportWriter(output)
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>一次运行的计数与节流上报。</summary>
    private sealed class RunState(ExportJob job, IProgress<ExportProgress>? progress, Stopwatch watch)
    {
        private CountingStream? _current;
        private long _finishedBytes;
        private TimeSpan _lastReport = TimeSpan.FromSeconds(-1);

        public long Documents { get; set; }

        public string Collection { get; set; } = "";

        public long Bytes => _finishedBytes + (_current?.Written ?? 0);

        public void Attach(CountingStream stream) => _current = stream;

        public void Detach()
        {
            _finishedBytes += _current?.Written ?? 0;
            _current = null;
        }

        public void Report(bool force)
        {
            if (progress is null)
            {
                return;
            }
            TimeSpan now = watch.Elapsed;
            if (!force && now - _lastReport < TimeSpan.FromMilliseconds(120))
            {
                return;
            }
            _lastReport = now;
            progress.Report(new(Documents, job.EstimatedTotal, Bytes, now, Collection));
        }
    }
}
