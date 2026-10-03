using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Analysis;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Transfer;

/// <summary>导入的写入方式(设计稿 20「写入方式」分段)。</summary>
internal enum ImportWriteMode
{
    /// <summary>插入(重复键算错误)。</summary>
    Insert,

    /// <summary>按键 upsert:命中则 <c>$set</c> 合并,不命中则插入。</summary>
    Upsert,

    /// <summary>替换:命中则整份替换,不命中则插入。</summary>
    Replace
}

/// <summary>一行的状态。</summary>
internal enum ImportRowStatus
{
    /// <summary>可导入。</summary>
    Ok,

    /// <summary>可导入,但做过自动修正。</summary>
    Warning,

    /// <summary>不能导入。</summary>
    Error
}

/// <summary>源列 → 目标字段的一条映射。</summary>
internal sealed class ImportColumn
{
    /// <summary>源列名(CSV 表头 / JSON 顶层字段名)。</summary>
    public required string Source { get; init; }

    /// <summary>CSV 的列下标;JSON / BSON 为 -1。</summary>
    public int Index { get; init; } = -1;

    /// <summary>导入这一列(否则跳过)。</summary>
    public bool Include { get; set; } = true;

    /// <summary>目标字段路径(可以是 <c>customer.name</c>)。</summary>
    public string Target { get; set; } = "";

    /// <summary>目标类型;<see langword="null" /> = 保持原值(JSON / BSON 来源)。</summary>
    public BsonKind? Kind { get; set; }
}

/// <summary>一个问题。</summary>
/// <param name="Kind">种类。</param>
/// <param name="IsError">错误(这一行不导入)还是警告。</param>
/// <param name="Field">字段路径。</param>
/// <param name="Column">在预览表里是第几列(已导入的列里的下标);不对应某一列为 -1。</param>
/// <param name="Detail">细节。</param>
internal sealed record ImportIssue(ImportIssueKind Kind, bool IsError, string Field, int Column, string Detail);

/// <summary>换算好的一行。</summary>
/// <param name="Line">源文件行号。</param>
/// <param name="Document">要写的文档。</param>
/// <param name="Issues">问题。</param>
/// <param name="Values">各导入列换算后的值(预览表按列显示;缺失为 <see langword="null" />)。</param>
/// <param name="Raw">各导入列的原文(出错的单元格显示原文)。</param>
internal sealed record ConvertedRow(long Line, BsonDocument Document, IReadOnlyList<ImportIssue> Issues, BsonValue?[] Values, string?[] Raw)
{
    /// <summary>状态。</summary>
    public ImportRowStatus Status => Issues.Any(static i => i.IsError) ? ImportRowStatus.Error
        : Issues.Count > 0 ? ImportRowStatus.Warning
        : ImportRowStatus.Ok;
}

/// <summary>
/// 导入要守的规则:来自目标集合的验证规则(<c>required</c>、<c>enum</c>、其余关键字),
/// 加上写入方式带来的约束(upsert / 替换时匹配键必填)。
/// </summary>
internal sealed record ImportRules
{
    /// <summary>必填字段(顶层名或点路径)。</summary>
    public IReadOnlyList<string> Required { get; init; } = [];

    /// <summary>枚举(路径 → 允许的值)。</summary>
    public IReadOnlyDictionary<string, BsonArray> Enums { get; init; } = new Dictionary<string, BsonArray>();

    /// <summary>属性声明(路径 → $jsonSchema 的那一节;默认目标类型用)。</summary>
    public IReadOnlyDictionary<string, BsonDocument> Properties { get; init; } = new Dictionary<string, BsonDocument>();

    /// <summary>整个 validator(交给客户端预检查其余关键字)。</summary>
    public BsonDocument? Validator { get; init; }

    /// <summary>validator 违规算错误(<c>validationAction: error</c>)还是警告(<c>warn</c>)。</summary>
    public bool ValidationErrors { get; init; } = true;

    /// <summary>用户指定的日期格式。</summary>
    public string? DateFormat { get; init; }

    /// <summary>写入方式。</summary>
    public ImportWriteMode Mode { get; init; } = ImportWriteMode.Insert;

    /// <summary>upsert / 替换的匹配键。</summary>
    public string? MatchKey { get; init; }

    /// <summary>空单元格不写字段(否则写空字符串)。</summary>
    public bool IgnoreBlanks { get; init; } = true;

    /// <summary>从集合的 validator 拆出规则。只认 <c>$jsonSchema</c>(查询表达式式的 validator 只交给服务器判)。</summary>
    public static ImportRules FromValidator(BsonDocument? validator, string validationAction)
    {
        if (validator is null || !validator.TryGetValue("$jsonSchema", out BsonValue raw) || raw is not BsonDocument schema)
        {
            return new() { Validator = validator, ValidationErrors = validationAction != "warn" };
        }
        var required = new List<string>();
        var enums = new Dictionary<string, BsonArray>(StringComparer.Ordinal);
        var properties = new Dictionary<string, BsonDocument>(StringComparer.Ordinal);
        Collect(schema, null, required, enums, properties, depth: 0);
        return new()
        {
            Required = required,
            Enums = enums,
            Properties = properties,
            Validator = validator,
            ValidationErrors = validationAction != "warn"
        };
    }

    private static void Collect(BsonDocument schema, string? prefix, List<string> required, Dictionary<string, BsonArray> enums,
        Dictionary<string, BsonDocument> properties, int depth)
    {
        if (schema.TryGetValue("required", out BsonValue req) && req is BsonArray names)
        {
            // 嵌套对象的 required 只在父对象存在时才生效 —— 这里只收顶层,避免误报。
            if (prefix is null)
            {
                required.AddRange(names.Where(static n => n.IsString).Select(static n => n.AsString));
            }
        }
        if (schema.TryGetValue("properties", out BsonValue props) && props is BsonDocument map && depth < 4)
        {
            foreach (BsonElement property in map)
            {
                if (property.Value is not BsonDocument definition)
                {
                    continue;
                }
                string path = BsonPath.Join(prefix, property.Name);
                properties[path] = definition;
                if (definition.TryGetValue("enum", out BsonValue values) && values is BsonArray array)
                {
                    enums[path] = array;
                }
                Collect(definition, path, required, enums, properties, depth + 1);
            }
        }
    }
}

/// <summary>
/// 把读出来的一条记录换算成要写的文档,并把一路上的问题记下来(dry-run 与正式导入用同一个)。
/// </summary>
/// <param name="columns">映射。</param>
/// <param name="rules">规则。</param>
/// <param name="loc">文案表(客户端 $jsonSchema 预检的消息)。</param>
internal sealed class ImportConverter(IReadOnlyList<ImportColumn> columns, ImportRules rules, Loc loc)
{
    private readonly ImportColumn[] _included = [.. columns.Where(static c => c.Include && c.Target.Trim().Length > 0)];
    private readonly HashSet<string> _excludedSources = [.. columns.Where(static c => !c.Include).Select(static c => c.Source)];

    /// <summary>导入的列(预览表的列)。</summary>
    public IReadOnlyList<ImportColumn> Included => _included;

    /// <summary>换算一条。</summary>
    public ConvertedRow Convert(ImportRecord record)
    {
        var issues = new List<ImportIssue>();
        var values = new BsonValue?[_included.Length];
        var raw = new string?[_included.Length];
        if (record.Error is { } parseError)
        {
            issues.Add(new(ImportIssueKind.Parse, true, "", -1, parseError));
            return new(record.Line, [], issues, values, raw);
        }
        BsonDocument document = record.Cells is { } cells ? FromCells(cells, issues, values, raw) : FromDocument(record.Document!, issues, values, raw);
        Check(document, issues);
        return new(record.Line, document, issues, values, raw);
    }

    private BsonDocument FromCells(string[] cells, List<ImportIssue> issues, BsonValue?[] values, string?[] raw)
    {
        var document = new BsonDocument();
        for (int i = 0; i < _included.Length; i++)
        {
            ImportColumn column = _included[i];
            string text = column.Index >= 0 && column.Index < cells.Length ? cells[column.Index] : "";
            raw[i] = text;
            if (text.Length == 0 && rules.IgnoreBlanks)
            {
                continue;
            }
            ConvertOutcome outcome = ImportValues.FromText(text, column.Kind ?? BsonKind.String, rules.DateFormat);
            Apply(document, column, i, outcome, issues, values);
        }
        return document;
    }

    private BsonDocument FromDocument(BsonDocument source, List<ImportIssue> issues, BsonValue?[] values, string?[] raw)
    {
        BsonDocument document = source.DeepClone().AsBsonDocument;
        foreach (string excluded in _excludedSources)
        {
            document.Remove(excluded);
        }
        for (int i = 0; i < _included.Length; i++)
        {
            ImportColumn column = _included[i];
            if (!source.TryGetValue(column.Source, out BsonValue value))
            {
                continue;
            }
            raw[i] = value is BsonString s ? s.Value : BsonText.Inline(value);
            string target = column.Target.Trim();
            if (target != column.Source)
            {
                document.Remove(column.Source);
            }
            ConvertOutcome outcome = column.Kind is { } kind ? ImportValues.FromValue(value, kind, rules.DateFormat) : ConvertOutcome.Ok(value);
            if (outcome.Value is null)
            {
                document.Remove(target);
            }
            Apply(document, column, i, outcome, issues, values);
        }
        return document;
    }

    private void Apply(BsonDocument document, ImportColumn column, int index, ConvertOutcome outcome, List<ImportIssue> issues, BsonValue?[] values)
    {
        string target = column.Target.Trim();
        if (outcome.Issue is { } kind)
        {
            string detail = kind == ImportIssueKind.NumberFromText && column.Kind is { } k ? $"{outcome.Detail}|{BsonKinds.Name(k)}" : outcome.Detail;
            issues.Add(new(kind, outcome.IsError, target, index, detail));
        }
        if (outcome.Value is not { } value)
        {
            return;
        }
        // 枚举:不在列表里但忽略大小写能对上 → 规范成列表里的写法(警告);完全对不上 → 错误。
        if (value is BsonString text && rules.Enums.TryGetValue(target, out BsonArray? allowed) && !allowed.Contains(text))
        {
            BsonValue? match = allowed.FirstOrDefault(a => a.IsString && string.Equals(a.AsString, text.Value, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                issues.Add(new(ImportIssueKind.EnumCase, false, target, index, $"{text.Value} → {match.AsString}"));
                value = match;
            }
            else
            {
                issues.Add(new(ImportIssueKind.EnumMismatch, rules.ValidationErrors, target, index, text.Value));
            }
        }
        values[index] = value;
        BsonPath.Set(document, target, value);
    }

    private void Check(BsonDocument document, List<ImportIssue> issues)
    {
        IEnumerable<string> required = rules.Required;
        if (rules.Mode != ImportWriteMode.Insert && rules.MatchKey is { Length: > 0 } key && !rules.Required.Contains(key))
        {
            required = required.Append(key);
        }
        foreach (string field in required)
        {
            if (BsonPath.Get(document, field) is null or BsonNull)
            {
                int column = Array.FindIndex(_included, c => c.Target.Trim() == field);
                issues.Add(new(ImportIssueKind.MissingRequired, true, field, column, ""));
            }
        }
        if (rules.Validator is { } validator && !issues.Any(static i => i.IsError))
        {
            foreach (SchemaViolation violation in JsonSchemaValidator.Validate(validator, document, loc))
            {
                if (violation.Keyword is "required" or "enum")
                {
                    continue;
                }
                int column = Array.FindIndex(_included, c => c.Target.Trim() == violation.Path);
                issues.Add(new(ImportIssueKind.Schema, rules.ValidationErrors, violation.Path, column, violation.Message));
            }
        }
    }
}

/// <summary>右栏「问题」的一组。</summary>
/// <param name="Kind">种类。</param>
/// <param name="IsError">错误还是警告。</param>
/// <param name="Field">字段。</param>
/// <param name="Rows">涉及多少行。</param>
/// <param name="FirstLine">第一处的行号。</param>
/// <param name="Detail">第一处的细节。</param>
internal sealed record ImportIssueGroup(ImportIssueKind Kind, bool IsError, string Field, int Rows, long FirstLine, string Detail);

/// <summary>dry-run 的统计。</summary>
internal static class ImportDryRun
{
    /// <summary>把若干行的问题按(种类, 字段)分组;错误在前。</summary>
    public static IReadOnlyList<ImportIssueGroup> Group(IEnumerable<ConvertedRow> rows)
    {
        var groups = new Dictionary<(ImportIssueKind, string, bool), (int Rows, long First, string Detail)>();
        var order = new List<(ImportIssueKind, string, bool)>();
        foreach (ConvertedRow row in rows)
        {
            foreach (ImportIssue issue in row.Issues.DistinctBy(static i => (i.Kind, i.Field, i.IsError)))
            {
                var key = (issue.Kind, issue.Field, issue.IsError);
                if (groups.TryGetValue(key, out var existing))
                {
                    groups[key] = (existing.Rows + 1, existing.First, existing.Detail);
                }
                else
                {
                    groups[key] = (1, row.Line, issue.Detail);
                    order.Add(key);
                }
            }
        }
        return
        [
            .. order.Select(k => new ImportIssueGroup(k.Item1, k.Item3, k.Item2, groups[k].Rows, groups[k].First, groups[k].Detail))
                .OrderBy(static g => g.IsError ? 0 : 1)
        ];
    }
}
