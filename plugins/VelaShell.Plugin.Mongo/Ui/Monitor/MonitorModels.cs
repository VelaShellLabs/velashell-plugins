using System.Globalization;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>顶部六张指标卡之一(设计稿 11:连接 / 操作每秒 / 网络 / 缓存 / 复制延迟 / Oplog 窗口)。</summary>
internal sealed class MonitorKpi : ObservableObject
{
    private string _value = "—";
    private string _unit = "";
    private string _foot = "";
    private bool _isAlert;
    private bool _footWarn;

    /// <summary>构造。</summary>
    /// <param name="title">标题(<c>连接</c>)。</param>
    /// <param name="iconKey">图标。</param>
    public MonitorKpi(string title, string iconKey)
    {
        Title = title;
        IconKey = iconKey;
    }

    /// <summary>标题。</summary>
    public string Title { get; }

    /// <summary>图标。</summary>
    public string IconKey { get; }

    /// <summary>"偏高"之类的角标文字。</summary>
    public string AlertText { get; init; } = "";

    /// <summary>大号数字(<c>128</c>、<c>1,236</c>)。</summary>
    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    /// <summary>数字后面的小字(<c>/ 512</c>、<c>MB/s</c>)。</summary>
    public string Unit
    {
        get => _unit;
        set => SetProperty(ref _unit, value);
    }

    /// <summary>底行(<c>当前 · 可用 384</c>)。</summary>
    public string Foot
    {
        get => _foot;
        set => SetProperty(ref _foot, value);
    }

    /// <summary>告警(橙色描边 + 角标;复制延迟超过阈值时)。</summary>
    public bool IsAlert
    {
        get => _isAlert;
        set
        {
            if (SetProperty(ref _isAlert, value))
            {
                RaisePropertyChanged(nameof(IconToken));
            }
        }
    }

    /// <summary>底行要不要用警告色(Oplog 窗口偏短)。</summary>
    public bool FootWarn
    {
        get => _footWarn;
        set => SetProperty(ref _footWarn, value);
    }

    /// <summary>图标颜色:告警时跟着变橙。</summary>
    public string IconToken => _isAlert ? "VelaWarning" : "VelaTextTertiary";

    /// <summary>一次把三段文字都换掉。</summary>
    public void Set(string value, string unit, string foot)
    {
        Value = value;
        Unit = unit;
        Foot = foot;
    }
}

/// <summary>图例里的一项(<c>■ query 982</c>)。</summary>
/// <param name="Name">名字。</param>
/// <param name="Token">颜色令牌。</param>
/// <param name="Value">当前值。</param>
internal sealed record MonitorLegend(string Name, string Token, string Value);

/// <summary>副本集成员卡的一行。</summary>
/// <param name="Name">地址。</param>
/// <param name="State">状态。</param>
/// <param name="StateToken">状态文字颜色。</param>
/// <param name="DotToken">状态点颜色。</param>
/// <param name="LagRatio">延迟条比例。</param>
/// <param name="LagToken">延迟条颜色。</param>
/// <param name="LagText">延迟文字(主节点为 <c>—</c>)。</param>
/// <param name="LagTextToken">延迟文字颜色。</param>
/// <param name="Meta">底行(<c>优先级 2 · 投票 1 · 心跳 2 s 前</c>)。</param>
/// <param name="LagLabel">「复制延迟」字样(随语言)。</param>
internal sealed record MonitorMember(
    string Name,
    string State,
    string StateToken,
    string DotToken,
    double LagRatio,
    string LagToken,
    string LagText,
    string LagTextToken,
    string Meta,
    string LagLabel);

/// <summary>存储 Top 的一行。</summary>
/// <param name="Prefix">库名前缀(<c>shop.</c>,淡色);Top 全在一个库里时为空。</param>
/// <param name="Collection">集合名(亮色)。</param>
/// <param name="DataRatio">数据段比例(相对最大的那一行)。</param>
/// <param name="IndexRatio">索引段比例。</param>
/// <param name="SizeText">合计(<c>44.0 GB</c>)。</param>
/// <param name="Tip">悬停提示(完整命名空间,数据与索引各多少)。</param>
internal sealed record MonitorStorageRow(string Prefix, string Collection, double DataRatio, double IndexRatio, string SizeText, string Tip)
{
    /// <summary>显示名(<c>shop.orders</c>;Top 全在一个库里时只有集合名)。</summary>
    public string Name => Prefix + Collection;
}

/// <summary>事件栏的一条(由采样推断:复制延迟超阈值、操作量尖峰、慢查询、索引构建完成…)。</summary>
internal sealed class MonitorEvent : ObservableObject
{
    private string _detail;

    /// <summary>构造。</summary>
    public MonitorEvent(DateTime time, string kind, string iconKey, string iconToken, string title, string detail)
    {
        Time = time;
        Kind = kind;
        IconKey = iconKey;
        IconToken = iconToken;
        Title = title;
        _detail = detail;
    }

    /// <summary>发生时刻(UTC)。</summary>
    public DateTime Time { get; }

    /// <summary>右侧时间(本地 <c>HH:mm</c>)。</summary>
    public string TimeText => Time.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>种类(去重与"持续中"更新用)。</summary>
    public string Kind { get; }

    /// <summary>图标。</summary>
    public string IconKey { get; }

    /// <summary>图标颜色令牌。</summary>
    public string IconToken { get; }

    /// <summary>标题。</summary>
    public string Title { get; }

    /// <summary>说明(持续中的事件会更新,如复制延迟的"持续 3 分钟")。</summary>
    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }
}

/// <summary>监控页的数字格式(与设计稿一致:千分位、等宽、单位单独一段小字)。</summary>
internal static class MonitorFormat
{
    /// <summary>每秒次数:≥ 10 取整加千分位,小于 10 留一位小数(空闲库上 0.5 次 / 秒也看得见)。</summary>
    public static string Rate(double value) => value >= 10 || value == 0
        ? Math.Round(value).ToString("N0", CultureInfo.InvariantCulture)
        : value.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>选一个让数值落在 1–1024 之间的字节单位(按 <paramref name="reference" /> 选,两个数共用一个单位)。</summary>
    public static (double Divisor, string Unit) ByteUnit(double reference)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double divisor = 1;
        int unit = 0;
        while (reference / divisor >= 1024 && unit < units.Length - 1)
        {
            divisor *= 1024;
            unit++;
        }
        return (divisor, units[unit]);
    }

    /// <summary>一位小数(<c>18.4</c>);整数不带小数点。</summary>
    public static string One(double value) =>
        Math.Abs(value - Math.Round(value)) < 0.05 && value >= 10
            ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>秒数:<c>0.8 s</c>、<c>41.2 s</c>、<c>3 min</c>。</summary>
    public static string Seconds(double seconds) => seconds < 100
        ? seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s"
        : Math.Round(seconds / 60).ToString("0", CultureInfo.InvariantCulture) + " min";

    /// <summary>
    /// 纵轴上限:取能被 3 整除成"好读刻度"的值(<see cref="BarChart" /> 画 1/3、2/3、满格三条网格线)。
    /// 按图表自己的取整,上限 200 时刻度是 66.7 / 133.3;这里先把每格取整到 1 / 1.5 / 2 / 2.5 / 3 / 4 / 5 / 6 / 8 × 10ⁿ,
    /// 上限就是 1.5k → 500 / 1k / 1.5k(设计稿 11 的纵轴)。
    /// </summary>
    public static double NiceThirds(double max)
    {
        if (max <= 0)
        {
            return 3;
        }
        double step = max / 3;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(step)));
        foreach (double m in new[] { 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 })
        {
            if (m * magnitude >= step - 1e-9)
            {
                return m * magnitude * 3;
            }
        }
        return 30 * magnitude;
    }

    /// <summary>
    /// 去掉默认端口(<c>10.20.3.21:27017</c> → <c>10.20.3.21</c>):成员卡与指标卡上一排 :27017 只是噪声,
    /// 非默认端口照留 —— 那时端口就是区分成员的信息。
    /// </summary>
    public static string Host(string address) =>
        address.EndsWith(":27017", StringComparison.Ordinal) ? address[..^6] : address;
}
