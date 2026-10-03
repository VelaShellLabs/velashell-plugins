using System.Globalization;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>「管道输出」表的一列。列宽可拖(列头分隔线),同一列的格子绑的就是这一个宽度。</summary>
/// <param name="name">字段名。</param>
/// <param name="width">初始列宽。</param>
/// <param name="isNumeric">数值列(右对齐)。</param>
/// <param name="isGhost">被停用阶段影响的列(只在停用的阶段里出现,表里没有数据)。</param>
/// <param name="typeName">主要类型(列头提示)。</param>
internal sealed class PipelineColumn(string name, double width, bool isNumeric, bool isGhost, string typeName) : ObservableObject
{

    /// <summary>字段名。</summary>
    public string Name { get; } = name;

    /// <summary>列宽(拖分隔线改,双击分隔线按内容自适应)。</summary>
    public double Width
    {
        get;
        set => SetProperty(ref field, Math.Clamp(value, 40, 1200));
    } = width;

    /// <summary>数值列(右对齐)。</summary>
    public bool IsNumeric { get; } = isNumeric;

    /// <summary>被停用阶段影响的提示列。</summary>
    public bool IsGhost { get; } = isGhost;

    /// <summary>主要类型。</summary>
    public string TypeName { get; } = typeName;

    /// <summary>列头提示(<c>qty · Int32</c>)。</summary>
    public string Tip => IsGhost ? Name : $"{Name} · {TypeName}";
}

/// <summary>表里的一格。</summary>
/// <param name="Text">文字。</param>
/// <param name="Token">颜色令牌。</param>
/// <param name="AlignRight">右对齐(数值)。</param>
/// <param name="Column">所在列(宽度跟着它走)。</param>
/// <param name="IsGhost">"(阶段 6 已停用)" 那种提示格。</param>
internal sealed record PipelineCell(string Text, string Token, bool AlignRight, PipelineColumn Column, bool IsGhost)
{
    /// <summary>对齐方式。</summary>
    public Avalonia.Layout.HorizontalAlignment Alignment =>
        AlignRight ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Left;
}

/// <summary>表里的一行。</summary>
/// <param name="Number">行号(1 起)。</param>
/// <param name="Cells">各格。</param>
internal sealed record PipelineRow(int Number, IReadOnlyList<PipelineCell> Cells);

/// <summary>一列"被停用阶段影响"的提示列。</summary>
/// <param name="Name">字段名(<c>product.name</c>)。</param>
/// <param name="StageNumber">停用的那个阶段的序号。</param>
internal sealed record PipelineGhost(string Name, int StageNumber);

/// <summary>输出预览卡里的一行(<c>_id: 66f5c2a1b04e97d2…</c>)。</summary>
/// <param name="Key">键名(带冒号)。</param>
/// <param name="Value">值的摘要。</param>
/// <param name="Token">值的颜色令牌。</param>
internal sealed record PipelineMiniField(string Key, string Value, string Token);

/// <summary>输出预览里的一张小文档卡。</summary>
/// <param name="Fields">头几个字段。</param>
internal sealed record PipelineMiniDoc(IReadOnlyList<PipelineMiniField> Fields);

/// <summary>
/// 「管道输出」表与输出预览卡的排版:类型化列、BSON 着色、停用阶段的提示列。
/// </summary>
internal static class PipelineResults
{
    /// <summary>行高(设计稿的输出表是 24px 行)。</summary>
    public const double RowHeight = 24;

    /// <summary>行号列宽。</summary>
    public const double NumberWidth = 40;

    /// <summary>最多出几列(再宽的文档也不该把表拉成一条横幅)。</summary>
    private const int MaxColumns = 40;

    /// <summary>
    /// 那些"重塑文档"的阶段:它们之后,前面停用的阶段加的字段反正也不会出现在输出里,
    /// 不必为它们画提示列。
    /// </summary>
    private static readonly HashSet<string> Reshaping =
    [
with(StringComparer.Ordinal),         "$group", "$project", "$replaceRoot", "$replaceWith", "$bucket", "$bucketAuto", "$count", "$facet", "$sortByCount"
    ];

    /// <summary>排出列与行。</summary>
    public static (IReadOnlyList<PipelineColumn> Columns, IReadOnlyList<PipelineRow> Rows) Build(
        IReadOnlyList<BsonDocument> documents,
        IReadOnlyList<PipelineGhost> ghosts,
        Loc loc)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (BsonDocument doc in documents)
        {
            foreach (BsonElement element in doc)
            {
                if (names.Count < MaxColumns && seen.Add(element.Name))
                {
                    names.Add(element.Name);
                }
            }
        }
        var columns = new List<PipelineColumn>();
        foreach (string name in names)
        {
            var kinds = documents.Take(200)
                .Select(d => BsonKinds.Of(d.GetValue(name, null)))
                .Where(static k => k is not BsonKind.Missing and not BsonKind.Null)
                .GroupBy(static k => k)
                .OrderByDescending(static g => g.Count())
                .Select(static g => g.Key)
                .ToList();
            BsonKind main = kinds.Count > 0 ? kinds[0] : BsonKind.Null;
            int longest = Math.Max(name.Length, documents.Take(200).Select(d => Cell(d.GetValue(name, null)).Text.Length).DefaultIfEmpty(0).Max());
            columns.Add(new(name, WidthFor(longest), BsonKinds.IsNumeric(main), isGhost: false, BsonKinds.Name(main)));
        }
        var ghostCells = new List<(PipelineGhost Ghost, string Text)>();
        foreach (PipelineGhost ghost in ghosts)
        {
            if (seen.Contains(ghost.Name))
            {
                continue;
            }
            string note = loc.Format("Pipe_GhostCell", ghost.StageNumber);
            columns.Add(new(ghost.Name, Math.Max(WidthFor(ghost.Name.Length), WidthFor(note.Length + 6)), false, isGhost: true, ""));
            ghostCells.Add((ghost, note));
        }

        var rows = new List<PipelineRow>(documents.Count);
        for (int r = 0; r < documents.Count; r++)
        {
            BsonDocument doc = documents[r];
            var cells = new List<PipelineCell>(columns.Count);
            int g = 0;
            foreach (PipelineColumn column in columns)
            {
                if (column.IsGhost)
                {
                    cells.Add(new(ghostCells[g++].Text, "VelaTextMuted", false, column, IsGhost: true));
                    continue;
                }
                (string text, string token) = Cell(doc.GetValue(column.Name, null));
                BsonKind kind = BsonKinds.Of(doc.GetValue(column.Name, null));
                cells.Add(new(text, token, BsonKinds.IsNumeric(kind), column, IsGhost: false));
            }
            rows.Add(new(r + 1, cells));
        }
        return (columns, rows);
    }

    /// <summary>
    /// 停用阶段会产出、但输出里没有的字段 —— 设计稿里 <c>product.name</c> 列写着"(阶段 6 已停用)",
    /// 让人一眼看出"这一列空着不是数据问题,是我把 $lookup 关了"。
    /// </summary>
    /// <param name="stages">全部阶段(运算符、解析出的值、是否启用)。</param>
    /// <param name="lookupSample">给 <c>$lookup.from</c> 集合抽一个字段名(<c>product</c> → <c>product.name</c>);没有返回 <see langword="null" />。</param>
    public static IReadOnlyList<PipelineGhost> Ghosts(
        IReadOnlyList<(string Operator, BsonValue? Value, bool Enabled)> stages,
        Func<string, string?>? lookupSample = null)
    {
        int lastReshape = -1;
        for (int i = 0; i < stages.Count; i++)
        {
            if (stages[i].Enabled && Reshaping.Contains(stages[i].Operator))
            {
                lastReshape = i;
            }
        }
        var ghosts = new List<PipelineGhost>();
        for (int i = lastReshape + 1; i < stages.Count; i++)
        {
            (string op, BsonValue? value, bool enabled) = stages[i];
            if (enabled || value is not BsonDocument body)
            {
                continue;
            }
            switch (op)
            {
                case "$lookup" or "$graphLookup" when body.TryGetValue("as", out BsonValue asValue) && asValue.IsString:
                    {
                        string field = asValue.AsString;
                        string? sub = body.TryGetValue("from", out BsonValue from) && from.IsString ? lookupSample?.Invoke(from.AsString) : null;
                        ghosts.Add(new(sub is null ? field : $"{field}.{sub}", i + 1));
                        break;
                    }
                case "$addFields" or "$set":
                    ghosts.AddRange(body.Names.Select(n => new PipelineGhost(n, i + 1)));
                    break;
                case "$setWindowFields" when body.TryGetValue("output", out BsonValue output) && output is BsonDocument outputs:
                    ghosts.AddRange(outputs.Names.Select(n => new PipelineGhost(n, i + 1)));
                    break;
                case "$unwind" when body.TryGetValue("includeArrayIndex", out BsonValue index) && index.IsString:
                    ghosts.Add(new(index.AsString, i + 1));
                    break;
            }
        }
        return ghosts;
    }

    /// <summary>一格的文字与颜色:字符串带引号(与设计稿一致),数值给千分位,容器给摘要。</summary>
    public static (string Text, string Token) Cell(BsonValue? value)
    {
        BsonKind kind = BsonKinds.Of(value);
        return kind switch
        {
            BsonKind.Missing => ("—", "VelaTextMuted"),
            BsonKind.String => (BsonText.Quote(Clip(BsonText.OneLine(value!.AsString), 120)), "VelaShellCyan"),
            _ => (Clip(BsonText.Cell(value), 160), BsonKinds.ColorToken(kind))
        };
    }

    /// <summary>输出预览卡:头几个字段的摘要(ObjectId 缩成 16 位 + 省略号,容器写成 <c>Array[3]</c>)。</summary>
    public static PipelineMiniDoc Mini(BsonDocument doc, int maxFields = 4)
    {
        var fields = new List<PipelineMiniField>();
        foreach (BsonElement element in doc.Take(maxFields))
        {
            BsonValue v = element.Value;
            BsonKind kind = BsonKinds.Of(v);
            string text = kind switch
            {
                BsonKind.ObjectId => Ellipsis(v.AsObjectId.ToString(), 16),
                BsonKind.String => BsonText.Quote(Clip(BsonText.OneLine(v.AsString), 60)),
                BsonKind.Int32 => v.AsInt32.ToString(CultureInfo.InvariantCulture),
                BsonKind.Int64 => v.AsInt64.ToString(CultureInfo.InvariantCulture),
                BsonKind.Double => BsonText.FormatDouble(v.AsDouble, forceDecimalPoint: false),
                BsonKind.Decimal128 => v.AsDecimal128.ToString(),
                BsonKind.Array => $"Array[{v.AsBsonArray.Count}]",
                BsonKind.Object => $"Object{{{v.AsBsonDocument.ElementCount}}}",
                BsonKind.Date => BsonText.FormatDate(v),
                _ => Clip(BsonText.Inline(v), 60)
            };
            fields.Add(new(element.Name + ":", text, BsonKinds.ColorToken(kind)));
        }
        return new(fields);
    }

    /// <summary>耗时文字:一秒以内给毫秒,以上给一位小数的秒(<c>1.4 s</c>)。</summary>
    public static string Elapsed(TimeSpan elapsed) =>
        elapsed.TotalMilliseconds < 1000
            ? $"{Math.Max(0, (int)Math.Round(elapsed.TotalMilliseconds))} ms"
            : $"{elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s";

    /// <summary>按字符数估一个等宽列宽(11px 等宽 ≈ 6.7px/字),夹在 90–320 之间。</summary>
    private static double WidthFor(int characters) => Math.Clamp(Math.Ceiling(characters * 6.7 + 26), 90, 320);

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static string Ellipsis(string text, int keep) => text.Length <= keep ? text : text[..keep] + "…";
}
