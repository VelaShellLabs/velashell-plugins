using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Transfer;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>导出格式卡片(设计稿 19「格式」那一排五张)。</summary>
internal sealed class ExportFormatCard : ObservableObject
{
    private readonly Action<ExportFormat> _select;
    private bool _isSelected;

    /// <summary>构造。</summary>
    public ExportFormatCard(ExportFormat format, string title, string subtitle, string iconKey, Action<ExportFormat> select)
    {
        Format = format;
        Title = title;
        Subtitle = subtitle;
        IconKey = iconKey;
        _select = select;
    }

    /// <summary>格式。</summary>
    public ExportFormat Format { get; }

    /// <summary>标题(<c>CSV</c>)。</summary>
    public string Title { get; }

    /// <summary>说明(<c>扁平化嵌套字段</c>)。</summary>
    public string Subtitle { get; }

    /// <summary>图标。</summary>
    public string IconKey { get; }

    /// <summary>选中(强调色描边 + AccentDim 底)。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                RaisePropertyChanged(nameof(IconToken));
                if (value)
                {
                    _select(Format);
                }
            }
        }
    }

    /// <summary>图标颜色:选中强调色,否则三级灰。</summary>
    public string IconToken => _isSelected ? "VelaAccent" : "VelaTextTertiary";

    /// <summary>不触发回调地同步选中态。</summary>
    internal void Sync(bool selected)
    {
        if (_isSelected != selected)
        {
            _isSelected = selected;
            RaisePropertiesChanged(nameof(IsSelected), nameof(IconToken));
        }
    }
}

/// <summary>多集合导出时的一个来源集合(第一步的勾选列表)。</summary>
internal sealed class ExportSourceRow : ObservableObject
{
    private readonly Action _changed;
    private bool _isChecked;
    private string _countText = "";

    /// <summary>构造。</summary>
    public ExportSourceRow(CollectionInfo info, bool isChecked, Action changed)
    {
        Info = info;
        _isChecked = isChecked;
        _changed = changed;
    }

    /// <summary>集合信息。</summary>
    public CollectionInfo Info { get; }

    /// <summary>名字。</summary>
    public string Name => Info.Name;

    /// <summary>图标。</summary>
    public string IconKey => Info.Kind switch
    {
        CollectionKind.View => "Mongo.eye",
        CollectionKind.TimeSeries => "Mongo.chart-no-axes-column",
        _ => "Mongo.table-2"
    };

    /// <summary>图标颜色。</summary>
    public string IconToken => Info.Kind == CollectionKind.View ? "VelaShellMagenta" : "VelaInfo";

    /// <summary>勾选。</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                _changed();
            }
        }
    }

    /// <summary>估算文档数(<c>1,500</c>)。</summary>
    public string CountText
    {
        get => _countText;
        set => SetProperty(ref _countText, value);
    }

    /// <summary>估算文档数。</summary>
    public long Count { get; set; }

    /// <summary>数据量(字节,collStats 的 size;视图为 0)。整库导出的文件大小按它估。</summary>
    public long Bytes { get; set; }
}

/// <summary>字段表的一行(设计稿 19:☑ / 拖动 / 字段路径 / 类型 / CSV 列名 / 转换)。</summary>
internal sealed class ExportFieldRow : ObservableObject
{
    private readonly Action _changed;
    private bool _include;
    private string _header;
    private XferOption _conversion;
    private bool _nullAsEmpty = true;

    /// <summary>构造。</summary>
    public ExportFieldRow(XferField field, bool include, string header, CellConversion conversion, Loc loc, Action changed)
    {
        Field = field;
        _include = include;
        _header = header;
        _changed = changed;
        Loc = loc;
        Choices = [.. XferField.ChoicesFor(field.Dominant).Select(c => new XferOption(c, ConversionName(c, loc)))];
        _conversion = Choices.FirstOrDefault(c => (CellConversion)c.Value == conversion) ?? Choices[0];
    }

    /// <summary>抽样字段。</summary>
    public XferField Field { get; }

    private Loc Loc { get; }

    /// <summary>路径。</summary>
    public string Path => Field.Path;

    /// <summary>类型文字(<c>String | Null</c>)。</summary>
    public string KindsText => Field.KindsText;

    /// <summary>类型色点的令牌。</summary>
    public string KindToken => BsonKinds.ColorToken(Field.Nullable && Field.Kinds.Count > 1 && Field.Dominant == BsonKind.String ? BsonKind.Null : Field.Dominant);

    /// <summary>导出这一列。</summary>
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

    /// <summary>没勾的行淡一点(设计稿 0.6)。</summary>
    public double RowOpacity => _include ? 1 : 0.6;

    /// <summary>列名。</summary>
    public string Header
    {
        get => _header;
        set
        {
            if (SetProperty(ref _header, value))
            {
                _changed();
            }
        }
    }

    /// <summary>可选的换算。</summary>
    public IReadOnlyList<XferOption> Choices { get; }

    /// <summary>有多种换算可选(显示下拉;否则显示一段说明文字)。</summary>
    public bool HasChoices => Choices.Count > 1;

    /// <summary>当前换算。</summary>
    public XferOption ConversionOption
    {
        get => _conversion;
        set
        {
            if (value is not null && SetProperty(ref _conversion, value))
            {
                RaisePropertyChanged(nameof(ConversionText));
                _changed();
            }
        }
    }

    /// <summary>当前换算的值。</summary>
    public CellConversion Conversion => (CellConversion)_conversion.Value;

    /// <summary>只有一种换算时的说明(<c>十六进制字符串</c> / <c>null → 空</c> / <c>—</c>)。</summary>
    public string ConversionText
    {
        get
        {
            if (Conversion != CellConversion.None)
            {
                return _conversion.Label;
            }
            if (Field.Kinds.Contains(BsonKind.Null))
            {
                return _nullAsEmpty ? Loc["Exp_NullToEmpty"] : Loc["Exp_NullToNull"];
            }
            return "—";
        }
    }

    /// <summary>换算说明是不是"有内容"(否则用更淡的颜色)。</summary>
    public bool ConversionActive => Conversion != CellConversion.None || Field.Kinds.Contains(BsonKind.Null);

    /// <summary>全局"空值"选项变了。</summary>
    internal void SetNullMode(bool nullAsEmpty)
    {
        if (_nullAsEmpty != nullAsEmpty)
        {
            _nullAsEmpty = nullAsEmpty;
            RaisePropertyChanged(nameof(ConversionText));
        }
    }

    /// <summary>换算的名字。</summary>
    internal static string ConversionName(CellConversion conversion, Loc loc) => conversion switch
    {
        CellConversion.Hex => loc["Exp_ConvHex"],
        CellConversion.Fixed2 => loc["Exp_ConvFixed2"],
        CellConversion.LocalTime => loc.Format("Exp_ConvLocal", TransferText.LocalOffset()),
        CellConversion.IsoTime => loc["Exp_ConvIso"],
        CellConversion.Json => loc["Exp_ConvJson"],
        _ => loc["Exp_ConvNone"]
    };

    /// <summary>导出列。</summary>
    public ExportColumn ToColumn() => new(Path, Header.Trim().Length > 0 ? Header.Trim() : Path, Conversion);
}

/// <summary>输出预览的一行。</summary>
/// <param name="Text">文本。</param>
/// <param name="IsHeader">表头(强调色)。</param>
internal sealed record XferPreviewLine(string Text, bool IsHeader);

/// <summary>保存的导出配置(MongoStore 的 <c>saved-export</c>)。</summary>
internal static class ExportProfile
{
    /// <summary>存储里的种类名。</summary>
    public const string Kind = "export";

    /// <summary>导出向导当前的设置 → 配置原文。</summary>
    public static string Serialize(BsonDocument profile) => profile.ToJson(new MongoDB.Bson.IO.JsonWriterSettings
    {
        OutputMode = MongoDB.Bson.IO.JsonOutputMode.RelaxedExtendedJson
    });

    /// <summary>配置原文 → 文档;坏的返回 <see langword="null" />。</summary>
    public static BsonDocument? Parse(string content)
    {
        try
        {
            return BsonDocument.Parse(content);
        }
        catch (Exception ex) when (ex is FormatException or BsonSerializationException or InvalidOperationException)
        {
            return null;
        }
    }
}
