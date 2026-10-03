using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Transfer;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>字段映射表的一行:源列 → 目标字段路径 + 目标类型。</summary>
internal sealed class ImportMappingRow : ObservableObject
{
    private readonly Action _changed;
    private bool _include = true;
    private string _target;
    private XferOption _kind;
    private bool _isRequired;
    private bool _isUnique;

    /// <summary>构造。</summary>
    public ImportMappingRow(string source, int index, string sample, string target, IReadOnlyList<XferOption> kinds, XferOption kind, Action changed)
    {
        Source = source;
        Index = index;
        Sample = sample;
        _target = target;
        Kinds = kinds;
        _kind = kind;
        _changed = changed;
    }

    /// <summary>源列名。</summary>
    public string Source { get; }

    /// <summary>CSV 列下标(JSON 为 -1)。</summary>
    public int Index { get; }

    /// <summary>样例值。</summary>
    public string Sample { get; }

    /// <summary>导入这一列。</summary>
    public bool Include
    {
        get => _include;
        set
        {
            if (SetProperty(ref _include, value))
            {
                RaisePropertyChanged(nameof(RowOpacity));
                _changed();
            }
        }
    }

    /// <summary>没勾的行淡一点。</summary>
    public double RowOpacity => _include ? 1 : 0.55;

    /// <summary>目标字段路径。</summary>
    public string Target
    {
        get => _target;
        set
        {
            if (SetProperty(ref _target, value))
            {
                _changed();
            }
        }
    }

    /// <summary>可选的目标类型。</summary>
    public IReadOnlyList<XferOption> Kinds { get; }

    /// <summary>目标类型。</summary>
    public XferOption Kind
    {
        get => _kind;
        set
        {
            if (value is not null && SetProperty(ref _kind, value))
            {
                RaisePropertyChanged(nameof(KindToken));
                _changed();
            }
        }
    }

    /// <summary>类型色点。</summary>
    public string KindToken => _kind.Value is BsonKind k ? BsonKinds.ColorToken(k) : "VelaTextMuted";

    /// <summary>目标字段是 validator 的必填项。</summary>
    public bool IsRequired
    {
        get => _isRequired;
        set => SetProperty(ref _isRequired, value);
    }

    /// <summary>目标字段上有唯一索引。</summary>
    public bool IsUnique
    {
        get => _isUnique;
        set => SetProperty(ref _isUnique, value);
    }

    /// <summary>→ 核心层的映射。</summary>
    public ImportColumn ToColumn() => new()
    {
        Source = Source,
        Index = Index,
        Include = _include,
        Target = _target.Trim(),
        Kind = _kind.Value as BsonKind?
    };
}

/// <summary>预览表的一列(设计稿 20:字段名 + 类型色块)。</summary>
/// <param name="Name">目标字段。</param>
/// <param name="KindName">类型名。</param>
/// <param name="KindToken">类型色。</param>
/// <param name="Width">列宽。</param>
internal sealed record ImportPreviewColumn(string Name, string KindName, string KindToken, double Width);

/// <summary>预览表的一个单元格。</summary>
/// <param name="Text">显示的文字。</param>
/// <param name="Token">文字颜色令牌。</param>
/// <param name="Width">列宽。</param>
internal sealed record ImportPreviewCell(string Text, string Token, double Width);

/// <summary>预览表的一行(行号、状态图标、各列)。</summary>
/// <param name="Line">源文件行号。</param>
/// <param name="Status">状态。</param>
/// <param name="Cells">单元格。</param>
internal sealed record ImportPreviewRow(long Line, ImportRowStatus Status, IReadOnlyList<ImportPreviewCell> Cells)
{
    /// <summary>行号文字。</summary>
    public string LineText => Line.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>状态图标。</summary>
    public string IconKey => Status switch
    {
        ImportRowStatus.Error => "Mongo.circle-x",
        ImportRowStatus.Warning => "Mongo.triangle-alert",
        _ => "Mongo.circle-check"
    };

    /// <summary>状态图标色。</summary>
    public string IconToken => Status switch
    {
        ImportRowStatus.Error => "VelaError",
        ImportRowStatus.Warning => "VelaWarning",
        _ => "VelaStatusConnected"
    };

    /// <summary>警告行(橙淡底)。</summary>
    public bool IsWarning => Status == ImportRowStatus.Warning;

    /// <summary>错误行(红淡底)。</summary>
    public bool IsError => Status == ImportRowStatus.Error;
}

/// <summary>右栏「问题」的一组。</summary>
/// <param name="Title">标题(<c>缺少必填字段 orderNo</c>)。</param>
/// <param name="Detail">细节(<c>4 行 · 如 第 6 行</c>)。</param>
/// <param name="IsError">错误还是警告。</param>
internal sealed record ImportIssueRow(string Title, string Detail, bool IsError)
{
    /// <summary>图标。</summary>
    public string IconKey => IsError ? "Mongo.circle-x" : "Mongo.triangle-alert";

    /// <summary>图标色。</summary>
    public string IconToken => IsError ? "VelaError" : "VelaWarning";
}

/// <summary>预览的筛选芯片。</summary>
internal enum ImportFilter
{
    /// <summary>全部。</summary>
    All,

    /// <summary>可导入(没有任何问题)。</summary>
    Ok,

    /// <summary>警告。</summary>
    Warning,

    /// <summary>错误。</summary>
    Error
}

/// <summary>导入预览的显示规则(单元格文字与颜色)。</summary>
internal static class ImportPreviewFormat
{
    /// <summary>某类型的列宽。</summary>
    public static double Width(BsonKind? kind, int maxText) => kind switch
    {
        BsonKind.ObjectId => 176,
        BsonKind.Date => 132,
        BsonKind.Int32 or BsonKind.Int64 or BsonKind.Double or BsonKind.Decimal128 => 92,
        BsonKind.Boolean => 70,
        _ => Math.Clamp(maxText * 6.2 + 18, 70, 220)
    };

    /// <summary>一个换算好的值的显示文字。</summary>
    public static string Text(BsonValue? value) => BsonKinds.Of(value) switch
    {
        BsonKind.Missing => "",
        BsonKind.Decimal128 => value!.AsDecimal128.ToString(),
        BsonKind.Double => BsonText.FormatDouble(value!.AsDouble, forceDecimalPoint: false),
        BsonKind.Int32 => value!.AsInt32.ToString(System.Globalization.CultureInfo.InvariantCulture),
        BsonKind.Int64 => value!.AsInt64.ToString(System.Globalization.CultureInfo.InvariantCulture),
        BsonKind.Date => CellFormatter.LocalDate(value!),
        BsonKind.String => BsonText.OneLine(value!.AsString),
        _ => BsonText.Cell(value)
    };

    /// <summary>一个值的颜色:字符串用主文字色(设计稿 20),其余按 BSON 类型色。</summary>
    public static string Token(BsonValue? value) => BsonKinds.Of(value) switch
    {
        BsonKind.String => "VelaTextPrimary",
        BsonKind.Missing => "VelaTextMuted",
        var kind => BsonKinds.ColorToken(kind)
    };
}
