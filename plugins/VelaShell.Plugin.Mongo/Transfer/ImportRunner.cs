using System.Diagnostics;
using System.Globalization;
using System.Text;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Transfer;

/// <summary>一次导入要做的事。</summary>
internal sealed record ImportJob
{
    /// <summary>库名。</summary>
    public required string Database { get; init; }

    /// <summary>集合名。</summary>
    public required string Collection { get; init; }

    /// <summary>源文件。</summary>
    public required ImportSource Source { get; init; }

    /// <summary>映射。</summary>
    public required IReadOnlyList<ImportColumn> Columns { get; init; }

    /// <summary>规则(含写入方式与匹配键)。</summary>
    public required ImportRules Rules { get; init; }

    /// <summary>出错即停(否则跳过该行并写进错误报告)。</summary>
    public bool StopOnError { get; init; }

    /// <summary>批大小。</summary>
    public int BatchSize { get; init; } = 1000;

    /// <summary>
    /// 批内按顺序写(<c>ordered: true</c>):第一处服务器错误之后的同批文档不再写。
    /// 默认 <see langword="false" /> —— 一行撞了唯一索引不该连累同一批的其余行。
    /// </summary>
    public bool Ordered { get; init; }

    /// <summary>upsert / 替换前,把将被覆盖的文档备份到 JSONL。</summary>
    public bool Backup { get; init; }

    /// <summary>预计总行数(进度条分母)。</summary>
    public long EstimatedTotal { get; init; }

    /// <summary>错误报告与备份文件放哪儿。</summary>
    public required string ReportDirectory { get; init; }
}

/// <summary>导入进度。</summary>
internal sealed record ImportProgress(long Rows, long Total, long Inserted, long Updated, long Skipped, TimeSpan Elapsed);

/// <summary>导入结果。</summary>
internal sealed record ImportResult(
    long Rows,
    long Inserted,
    long Updated,
    long Skipped,
    TimeSpan Elapsed,
    string? ErrorReport,
    string? BackupFile,
    bool Stopped);

/// <summary>
/// 执行导入:读一条换算一条,攒满一批 <c>bulkWrite</c> 一次(默认 <c>ordered: false</c> ——
/// 一行撞了唯一索引不该连累同一批的其余 999 行)。
/// <para>
/// 写不进去的行(换算失败、服务器拒绝)逐行落进一份 JSONL 错误报告:行号、原因、原始内容,
/// 修好了能直接拿那份文件再导一次。
/// </para>
/// </summary>
internal static class ImportRunner
{
    /// <summary>跑一次导入。</summary>
    public static async Task<ImportResult> RunAsync(
        MongoConnection connection,
        ImportJob job,
        Loc loc,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        IMongoCollection<BsonDocument> collection = connection.Collection(job.Database, job.Collection);
        var converter = new ImportConverter(job.Columns, job.Rules, loc);
        var report = new ReportFiles(job);
        var batch = new List<(ConvertedRow Row, ImportRecord Record, WriteModel<BsonDocument> Model)>(job.BatchSize);
        long rows = 0, inserted = 0, updated = 0, skipped = 0;
        bool stopped = false;
        TimeSpan lastReport = TimeSpan.Zero;
        using IImportReader reader = job.Source.Open();
        try
        {
            while (!stopped && reader.TryRead(out ImportRecord record))
            {
                rows++;
                ConvertedRow row = converter.Convert(record);
                if (row.Status == ImportRowStatus.Error)
                {
                    skipped++;
                    report.Error(row.Line, [.. row.Issues.Where(static i => i.IsError).Select(i => ImportIssueText.Describe(i, loc))], record, reader.Header);
                    if (job.StopOnError)
                    {
                        stopped = true;
                        break;
                    }
                    continue;
                }
                batch.Add((row, record, Model(row.Document, job.Rules)));
                if (batch.Count >= job.BatchSize)
                {
                    (long i, long u, long s, bool stop) = await FlushAsync(collection, job, batch, report, reader.Header, loc, cancellationToken).ConfigureAwait(false);
                    inserted += i;
                    updated += u;
                    skipped += s;
                    stopped = stop;
                }
                if (watch.Elapsed - lastReport > TimeSpan.FromMilliseconds(150))
                {
                    lastReport = watch.Elapsed;
                    progress?.Report(new(rows, job.EstimatedTotal, inserted, updated, skipped, watch.Elapsed));
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            if (!stopped && batch.Count > 0)
            {
                (long i, long u, long s, bool stop) = await FlushAsync(collection, job, batch, report, reader.Header, loc, cancellationToken).ConfigureAwait(false);
                inserted += i;
                updated += u;
                skipped += s;
                stopped = stop;
            }
        }
        finally
        {
            report.Dispose();
        }
        progress?.Report(new(rows, job.EstimatedTotal, inserted, updated, skipped, watch.Elapsed));
        return new(rows, inserted, updated, skipped, watch.Elapsed, report.ErrorPath, report.BackupPath, stopped);
    }

    /// <summary>一份文档 → 写模型。</summary>
    internal static WriteModel<BsonDocument> Model(BsonDocument document, ImportRules rules)
    {
        if (rules.Mode == ImportWriteMode.Insert || rules.MatchKey is not { Length: > 0 } key)
        {
            return new InsertOneModel<BsonDocument>(document);
        }
        BsonValue keyValue = BsonPath.Get(document, key) ?? BsonNull.Value;
        var filter = new BsonDocument(key, keyValue);
        if (rules.Mode == ImportWriteMode.Replace)
        {
            BsonDocument replacement = document;
            if (key != "_id" && document.Contains("_id"))
            {
                // 命中的文档保留它自己的 _id(改 _id 会被服务器拒:ImmutableField)。
                replacement = document.DeepClone().AsBsonDocument;
                replacement.Remove("_id");
            }
            return new ReplaceOneModel<BsonDocument>(filter, replacement) { IsUpsert = true };
        }
        var set = new BsonDocument(document.Where(e => e.Name != "_id" && e.Name != key));
        var update = new BsonDocument();
        if (set.ElementCount > 0)
        {
            update["$set"] = set;
        }
        var onInsert = new BsonDocument();
        if (key != "_id" && document.TryGetValue("_id", out BsonValue id))
        {
            onInsert["_id"] = id;
        }
        if (set.ElementCount == 0 && onInsert.ElementCount == 0)
        {
            onInsert[key] = keyValue;
        }
        if (onInsert.ElementCount > 0)
        {
            update["$setOnInsert"] = onInsert;
        }
        return new UpdateOneModel<BsonDocument>(filter, update) { IsUpsert = true };
    }

    private static async Task<(long Inserted, long Updated, long Skipped, bool Stop)> FlushAsync(
        IMongoCollection<BsonDocument> collection,
        ImportJob job,
        List<(ConvertedRow Row, ImportRecord Record, WriteModel<BsonDocument> Model)> batch,
        ReportFiles report,
        IReadOnlyList<string> header,
        Loc loc,
        CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return (0, 0, 0, false);
        }
        if (job.Backup && job.Rules.Mode != ImportWriteMode.Insert && job.Rules.MatchKey is { Length: > 0 } key)
        {
            var keys = new BsonArray(batch.Select(b => BsonPath.Get(b.Row.Document, key)).OfType<BsonValue>().Distinct());
            if (keys.Count > 0)
            {
                using IAsyncCursor<BsonDocument> cursor = await collection.FindAsync(
                    new BsonDocument(key, new BsonDocument("$in", keys)), cancellationToken: cancellationToken).ConfigureAwait(false);
                while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
                {
                    foreach (BsonDocument existing in cursor.Current)
                    {
                        report.Backup(existing);
                    }
                }
            }
        }
        long inserted = 0, updated = 0, skipped = 0;
        bool stop = false;
        try
        {
            BulkWriteResult<BsonDocument> result = await collection.BulkWriteAsync(
                batch.Select(static b => b.Model),
                new BulkWriteOptions { IsOrdered = job.Ordered || job.StopOnError },
                cancellationToken).ConfigureAwait(false);
            (inserted, updated) = Count(result);
        }
        catch (MongoBulkWriteException<BsonDocument> ex)
        {
            if (ex.Result is { IsAcknowledged: true } partial)
            {
                (inserted, updated) = Count(partial);
            }
            foreach (BulkWriteError error in ex.WriteErrors)
            {
                if (error.Index >= 0 && error.Index < batch.Count)
                {
                    (ConvertedRow row, ImportRecord record, _) = batch[error.Index];
                    report.Error(row.Line, [$"E{error.Code.ToString(CultureInfo.InvariantCulture)}: {error.Message}"], record, header);
                }
                skipped++;
            }
            if (job.Ordered || job.StopOnError)
            {
                // ordered 批次在第一个错误处停下:它之后的那些也没写进去 —— 同样记进错误报告,修好了能补导。
                int firstError = ex.WriteErrors.Count > 0 ? ex.WriteErrors.Min(static e => e.Index) : batch.Count;
                for (int i = firstError + 1; i < batch.Count; i++)
                {
                    if (ex.WriteErrors.Any(e => e.Index == i))
                    {
                        continue;
                    }
                    (ConvertedRow row, ImportRecord record, _) = batch[i];
                    report.Error(row.Line, [loc.Format("Imp_NotWritten", batch[firstError].Row.Line)], record, header);
                    skipped++;
                }
            }
            stop = job.StopOnError;
        }
        batch.Clear();
        return (inserted, updated, skipped, stop);
    }

    private static (long Inserted, long Updated) Count(BulkWriteResult<BsonDocument> result) =>
        result.IsAcknowledged
            ? (result.InsertedCount + result.Upserts.Count, result.MatchedCount)
            : (0, 0);

    /// <summary>错误报告与备份文件(惰性创建:没有错误就不留一个空文件)。</summary>
    private sealed class ReportFiles(ImportJob job) : IDisposable
    {
        private readonly string _stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        private StreamWriter? _errors;
        private StreamWriter? _backup;

        public string? ErrorPath { get; private set; }

        public string? BackupPath { get; private set; }

        public void Error(long line, IReadOnlyList<string> reasons, ImportRecord record, IReadOnlyList<string> header)
        {
            if (_errors is null)
            {
                ErrorPath = Path.Combine(job.ReportDirectory, $"{Path.GetFileNameWithoutExtension(job.Source.Path)}.errors-{_stamp}.jsonl");
                _ = Directory.CreateDirectory(job.ReportDirectory);
                _errors = new StreamWriter(ErrorPath, append: false, new UTF8Encoding(false));
            }
            var entry = new BsonDocument
            {
                { "line", line },
                { "errors", new BsonArray(reasons) }
            };
            if (record.Cells is { } cells)
            {
                var row = new BsonDocument();
                for (int i = 0; i < cells.Length; i++)
                {
                    string name = i < header.Count ? header[i] : $"column{i + 1}";
                    if (!row.Contains(name))
                    {
                        row[name] = cells[i];
                    }
                }
                entry["row"] = row;
            }
            else if (record.Document is { } document)
            {
                entry["row"] = document;
            }
            _errors.WriteLine(entry.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.RelaxedExtendedJson }));
        }

        public void Backup(BsonDocument document)
        {
            if (_backup is null)
            {
                BackupPath = Path.Combine(job.ReportDirectory, $"{job.Database}.{job.Collection}.backup-{_stamp}.jsonl");
                _ = Directory.CreateDirectory(job.ReportDirectory);
                _backup = new StreamWriter(BackupPath, append: false, new UTF8Encoding(false));
            }
            // Canonical:备份是要能原样恢复的,类型一个都不能丢(Int64 / Decimal128 / 日期)。
            _backup.WriteLine(document.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.CanonicalExtendedJson }));
        }

        public void Dispose()
        {
            _errors?.Dispose();
            _backup?.Dispose();
        }
    }
}

/// <summary>问题 → 一句人话(错误报告与右栏共用)。</summary>
internal static class ImportIssueText
{
    /// <summary>组标题(<c>缺少必填字段 orderNo</c>、<c>日期无法解析</c>)。</summary>
    public static string Title(ImportIssueKind kind, string field, string detail, Loc loc) => kind switch
    {
        ImportIssueKind.MissingRequired => loc.Format("Imp_IssueMissing", field),
        ImportIssueKind.BadDate => loc["Imp_IssueBadDate"],
        ImportIssueKind.BadNumber => loc.Format("Imp_IssueBadNumber", field),
        ImportIssueKind.BadValue => loc.Format("Imp_IssueBadValue", field),
        ImportIssueKind.NumberFromText => loc.Format("Imp_IssueNumberText", detail.Split('|') is [_, var type] ? type : "Number"),
        ImportIssueKind.EnumCase => loc["Imp_IssueEnumCase"],
        ImportIssueKind.EnumMismatch => loc.Format("Imp_IssueEnumMismatch", field),
        ImportIssueKind.Parse => loc["Imp_IssueParse"],
        _ => detail.Length > 0 ? detail : loc.Format("Imp_IssueSchema", field)
    };

    /// <summary>一条问题的完整描述(错误报告里的一行)。</summary>
    public static string Describe(ImportIssue issue, Loc loc)
    {
        string title = Title(issue.Kind, issue.Field, issue.Detail, loc);
        return issue.Kind switch
        {
            ImportIssueKind.BadDate => $"{title}: {issue.Field} ({issue.Detail})",
            ImportIssueKind.BadNumber or ImportIssueKind.BadValue or ImportIssueKind.EnumMismatch or ImportIssueKind.Parse => $"{title}: {issue.Detail}",
            _ => title
        };
    }
}
