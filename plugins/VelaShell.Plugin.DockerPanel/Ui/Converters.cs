using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Media;
using System.Globalization;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>
/// 面板用到的值转换器。
/// <para>
/// 全部走宿主的 <c>Vela*</c> 令牌:转换器只负责"这一行是什么状态",
/// 具体是哪个颜色由主题字典决定。
/// </para>
/// <para>
/// 取色一律经 <see cref="ThemeBrush" /> —— 转换器只在**绑定源**变化时才跑,而主题不是绑定源,
/// 所以换肤时这里的代码根本不会被再调一次。<see cref="ThemeBrushes" /> 的办法是让画刷对象
/// 保持不变、只改它的颜色,于是绑定一次都不用重算。别在这个文件里直接去 <c>TryFindResource</c>
/// 拿画刷:那样拿到的是宿主当前那一格字典里的实例,宿主换主题是整格替换,它会就此停在旧色上。
/// </para>
/// </summary>
public static class Converters
{
    /// <summary>状态色 → 画刷。参数可选 <c>dim</c>,取半透明底色。</summary>
    public static readonly IValueConverter ToneBrush = new ToneBrushConverter();

    /// <summary>状态色 → 图标资源键。</summary>
    public static readonly IValueConverter ToneIcon = new FuncValueConverter<RowTone, string>(tone => tone switch
    {
        RowTone.Ok => "Icon.circle-check",
        RowTone.Warn => "Icon.triangle-alert",
        RowTone.Danger => "Docker.circle-x",
        RowTone.Busy => "Icon.refresh-cw",
        _ => "Icon.circle-help"
    });

    /// <summary>后果说明的严重度 → 画刷。</summary>
    public static readonly IValueConverter SeverityBrush = new SeverityBrushConverter();

    /// <summary>后果说明的严重度 → 图标。</summary>
    public static readonly IValueConverter SeverityIcon = new FuncValueConverter<int, string>(severity => severity switch
    {
        1 => "Icon.shield",
        2 => "Icon.triangle-alert",
        3 => "Docker.circle-x",
        _ => "Icon.info"
    });

    /// <summary>反馈语气 → 画刷。</summary>
    public static readonly IValueConverter FeedbackBrush = new FeedbackBrushConverter();

    /// <summary>0–1 的比例 → 星形宽度(进度条的已填充段)。</summary>
    public static readonly IValueConverter RatioStar =
        new FuncValueConverter<double, GridLength>(v => new(Math.Clamp(v, 0, 1), GridUnitType.Star));

    /// <summary>0–1 的比例 → 剩余段的星形宽度。</summary>
    public static readonly IValueConverter RatioRestStar =
        new FuncValueConverter<double, GridLength>(v => new(1 - Math.Clamp(v, 0, 1), GridUnitType.Star));

    /// <summary>把 0–100 的采样值换算成 sparkline 里的柱高(最高 16)。</summary>
    public static readonly IValueConverter SampleHeight =
        new FuncValueConverter<double, double>(v => Math.Max(2, Math.Clamp(v, 0, 100) / 100 * 16));

    /// <summary>把 0–100 的采样值换算成抽屉里那张卡片的柱高(最高 30)。</summary>
    public static readonly IValueConverter CardSampleHeight =
        new FuncValueConverter<double, double>(v => Math.Max(2, Math.Clamp(v, 0, 100) / 100 * 30));

    /// <summary>把 0–100 的采样值换算成趋势图里的柱高(最高 96)。</summary>
    public static readonly IValueConverter TrendHeight =
        new FuncValueConverter<double, double>(v => Math.Max(2, Math.Clamp(v, 0, 100) / 100 * 96));

    /// <summary>0–1 的比例 → 剩下那一段的权重(配合 <see cref="WeightedStackPanel" />)。</summary>
    public static readonly IValueConverter RemainingWeight =
        new FuncValueConverter<double, double>(v => Math.Max(0.0001, 1 - Math.Clamp(v, 0, 1)));

    /// <summary>
    /// 值为 0(含极小的浮点残留)为 true。进度条据此在"不知道分母"时退化成不确定型。
    /// </summary>
    public static readonly IValueConverter IsZero =
        new FuncValueConverter<double, bool>(v => v <= 0.0001);

    /// <summary>
    /// 来源序号 → 一个稳定的颜色。
    /// <para>
    /// 用宿主已有的语义色轮着来,不自造调色板 —— 它们在明暗两套主题下都已经调过对比度,
    /// 而临时挑的六个 hex 在亮色模式下多半有一半看不清。
    /// </para>
    /// </summary>
    public static readonly IValueConverter SourceBrush = new SourceBrushConverter();

    /// <summary>差异行的底色:增绿、删红、改黄,没变透明。</summary>
    public static readonly IValueConverter DiffBackground = new DiffBackgroundConverter();

    /// <summary>键盘选中项的底色。</summary>
    public static readonly IValueConverter ActiveBackground = new ActiveBackgroundConverter();

    /// <summary>非空字符串为 true。</summary>
    public static readonly IValueConverter NotEmpty =
        new FuncValueConverter<string?, bool>(v => !string.IsNullOrEmpty(v));

    /// <summary>取反。</summary>
    public static readonly IValueConverter Not = new FuncValueConverter<bool, bool>(v => !v);

    /// <summary>文件树里的图标色:目录用强调色,文件退到弱化色。</summary>
    public static readonly IValueConverter FileIconBrush =
        new FuncValueConverter<bool, IBrush>(isDirectory => isDirectory
            ? ThemeBrush("VelaAccent", Brushes.MediumPurple)
            : ThemeBrush("VelaTextTertiary", Brushes.Gray));

    /// <summary>「这一行是当前打开的那个」→ 选中底色,否则透明。</summary>
    public static readonly IValueConverter CurrentRowBackground =
        new FuncValueConverter<bool, IBrush>(current => current
            ? ThemeBrush("VelaAccentDim", Brushes.Transparent)
            : Brushes.Transparent);

    /// <summary>「换行」开关 → 文本换行模式。关掉时长行横向截断,不折成好几屏。</summary>
    public static readonly IValueConverter WrapMode =
        new FuncValueConverter<bool, TextWrapping>(v => v ? TextWrapping.Wrap : TextWrapping.NoWrap);

    /// <summary>
    /// 「换行」开关 → 横向滚动条。
    /// <para>
    /// 两者必须互斥:开着横向滚动,正文就是在无限宽里量的,<c>TextWrapping.Wrap</c> 永远折不了行;
    /// 关掉横向滚动而又不折行,超出去的那半行则彻底没办法看到。
    /// </para>
    /// </summary>
    public static readonly IValueConverter WrapScroll =
        new FuncValueConverter<bool, ScrollBarVisibility>(wrap =>
            wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);

    /// <summary>
    /// 提示文字:空的就**不给提示**。
    /// <para>
    /// <c>ToolTip.Tip</c> 只看是不是 <see langword="null" />:空字符串照样会弹一个空框出来。
    /// 而绑上去的那些文字有一半天生可能是空的(没有端口的容器、没有禁用理由的选项、
    /// 还没打开文件的编辑器),于是鼠标一悬停就冒出一个空白小方块。
    /// </para>
    /// </summary>
    public static readonly IValueConverter TipText =
        new FuncValueConverter<string?, object?>(text => string.IsNullOrWhiteSpace(text) ? null : text);

    /// <summary>最大化 / 还原是同一颗键,图标要跟着当前状态走 —— 否则铺开之后没人看得出怎么回去。</summary>
    public static readonly IValueConverter MaximizeIcon =
        new FuncValueConverter<bool, string>(max => max ? "Docker.minimize-2" : "Docker.maximize-2");

    /// <summary>
    /// 控件宽度 ≥ 参数(像素)才为真。
    /// <para>
    /// 抽屉是用户拖出来的,同一条工具条要在 440 和 1400 两种宽度下都成立 ——
    /// 靠它把次要的那几颗按钮按宽度逐级收起,而不是让它们溢出到屏幕外。
    /// </para>
    /// </summary>
    public static readonly IValueConverter WiderThan = new WiderThanConverter();

    /// <summary>
    /// CPU 高负载 → 警示色,否则强调色。
    /// <para>
    /// 行内 sparkline 与它旁边那个百分比共用这一条:两者必须同时变色,
    /// 否则会出现"柱子是黄的、数字是紫的"这种自相矛盾的一行。
    /// </para>
    /// </summary>
    public static readonly IValueConverter HotAccent =
        new FuncValueConverter<bool, IBrush>(hot => hot
            ? ThemeBrush("VelaWarning", Brushes.Orange)
            : ThemeBrush("VelaAccent", Brushes.MediumPurple));

    /// <summary>CPU 高负载 → 警示色,否则常规数值色。给 sparkline 旁边那个百分比用。</summary>
    public static readonly IValueConverter HotText =
        new FuncValueConverter<bool, IBrush>(hot => hot
            ? ThemeBrush("VelaWarning", Brushes.Orange)
            : ThemeBrush("VelaTextSecondary", Brushes.Gray));

    /// <summary>
    /// 按令牌取画刷。**全部**转换器的取色都必须走这一条。
    /// <para>
    /// 拿到的是一支会跟着宿主换肤就地改色的画刷(见 <see cref="ThemeBrushes" />)。
    /// 直接把宿主资源里的画刷返回出去是不行的:转换器只在绑定源变化时才跑,主题不是绑定源,
    /// 而宿主换主题是整格替换字典 —— 返回出去的那个对象会永远停在旧主题的颜色上。
    /// </para>
    /// </summary>
    internal static IBrush ThemeBrush(string key, IBrush fallback) => ThemeBrushes.Get(key, fallback);

    /// <summary>
    /// 按当前主题变体查一个**非颜色**资源(图标几何)。
    /// <para>
    /// 颜色不要走这里 —— 走 <see cref="ThemeBrush" />。几何不随主题变,取到就一直有效。
    /// </para>
    /// <para>
    /// 必须带上 <c>ActualThemeVariant</c>:宿主的资源分两族,不带主题的那个重载按
    /// <c>ThemeVariant.Default</c> 查,写在 <c>ThemeDictionaries</c> 分支下的那一族一个都查不到。
    /// </para>
    /// </summary>
    internal static object? Lookup(string key)
    {
        return Application.Current is not { } app ? null : app.TryFindResource(key, app.ActualThemeVariant, out var value) ? value : null;
    }

    private sealed class ToneBrushConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var dim = (parameter as string) == "dim";
            var key = value switch
            {
                RowTone.Ok => dim ? "VelaShellGreenDim" : "VelaStatusConnected",
                RowTone.Warn => dim ? "VelaShellYellowDim" : "VelaWarning",
                RowTone.Danger => dim ? "VelaShellRedDim" : "VelaError",
                RowTone.Busy => dim ? "VelaAccentDim" : "VelaAccent",
                _ => dim ? "VelaShellSubtleDim" : "VelaTextTertiary"
            };
            return ThemeBrush(key, Brushes.Gray);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class WiderThanConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is double width && !double.IsNaN(width) &&
            double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var least) &&
            width >= least;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class DiffBackgroundConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value switch
            {
                RowTone.Ok => ThemeBrush("VelaShellGreenDim", Brushes.Transparent),
                RowTone.Danger => ThemeBrush("VelaShellRedDim", Brushes.Transparent),
                RowTone.Warn => ThemeBrush("VelaShellYellowDim", Brushes.Transparent),
                _ => Brushes.Transparent
            };

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class ActiveBackgroundConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true ? ThemeBrush("VelaAccentDim", Brushes.Transparent) : Brushes.Transparent;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class SourceBrushConverter : IValueConverter
    {
        /// <summary>轮换用的令牌。六个之后回头 —— 同屏合并超过六条流本来就读不动了。</summary>
        private static readonly string[] Palette =
        [
            "VelaAccent", "VelaStatusConnected", "VelaInfo",
            "VelaWarning", "VelaShellMagenta", "VelaShellCyan"
        ];

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var index = value is int i && i >= 0 ? i : 0;
            return ThemeBrush(Palette[index % Palette.Length], Brushes.Gray);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class SeverityBrushConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            ThemeBrush(value switch
            {
                1 => "VelaStatusConnected",
                2 => "VelaWarning",
                3 => "VelaError",
                _ => "VelaTextTertiary"
            }, Brushes.Gray);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class FeedbackBrushConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var dim = (parameter as string) == "dim";
            var key = value switch
            {
                FeedbackKind.Success => dim ? "VelaShellGreenDim" : "VelaStatusConnected",
                FeedbackKind.Warning => dim ? "VelaShellYellowDim" : "VelaWarning",
                FeedbackKind.Error => dim ? "VelaShellRedDim" : "VelaError",
                _ => dim ? "VelaAccentDim" : "VelaAccent"
            };
            return ThemeBrush(key, Brushes.Gray);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}

/// <summary>
/// 按资源键取图标几何。
/// <para>
/// 图标名是数据(视图模型给的字符串),而 <c>StreamGeometry</c> 在资源字典里 ——
/// 这个转换器把两者接上,免得每处都写一个 <c>DataTrigger</c> 大表。
/// </para>
/// </summary>
public sealed class IconLookupConverter : IValueConverter
{
    /// <summary>单例。</summary>
    public static readonly IconLookupConverter Instance = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is not string key || key.Length == 0 ? null : Converters.Lookup(key);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 会丢数据那一档的闸门用红边框,一般破坏性用常规描边。
/// <para>
/// 边框颜色是两档之间**唯一**不靠文字的区分 —— 用户在读标题之前就该感觉到分量不同。
/// </para>
/// </summary>
public sealed class DataLossBorderConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is true ? "VelaError" : "VelaBorderSecondary";
        return Converters.ThemeBrush(key, Brushes.Gray);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>只读字段的底色比可编辑的暗一档 —— 不用读提示就知道这一格改不了。</summary>
public sealed class ReadOnlyBackgroundConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is true ? "VelaBgSurface" : "VelaBgInput";
        return Converters.ThemeBrush(key, Brushes.Transparent);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>危险开关的说明文字用警示色。</summary>
public sealed class DangerTextConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is true ? "VelaError" : "VelaTextTertiary";
        return Converters.ThemeBrush(key, Brushes.Gray);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>跟随中的按钮用绿底,停了就是透明。</summary>
public sealed class FollowBackgroundConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is not true ? Brushes.Transparent : (object)(Converters.ThemeBrush("VelaShellGreenDim", Brushes.Transparent));
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>跟随中的文字与图标用绿色。</summary>
public sealed class FollowForegroundConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is true ? "VelaStatusConnected" : "VelaTextSecondary";
        return Converters.ThemeBrush(key, Brushes.Gray);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 搜索命中的行整行加一层淡黄底。
/// <para>
/// 不做行内高亮是有意的:日志行是纯文本(<c>SelectableTextBlock</c> 才能复制),
/// 把它拆成几段富文本会同时毁掉选中与复制 —— 而那两件事在日志里比高亮更常用。
/// </para>
/// </summary>
public sealed class MatchBackgroundConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is not true ? Brushes.Transparent : (object)(Converters.ThemeBrush("VelaShellYellowDim", Brushes.Transparent));
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>标准错误的行用红色,标准输出用终端前景色。</summary>
public sealed class LogLineForegroundConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is true ? "VelaShellRed" : "VelaShellWhite";
        return Converters.ThemeBrush(key, Brushes.Gray);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 合并日志正文的颜色:按级别分色。
/// <para>
/// 刻意<b>不</b>把 INFO 染成绿色。日志里绝大多数行都是 INFO,整屏绿字既刺眼又等于没分色 ——
/// 分色的意义是让少数几行跳出来。所以只有 WARN 与 ERROR 换色,DEBUG 压暗,
/// 其余保持正文色。stderr 一律按错误算:它走的是哪条流,这件事本身就是信息。
/// </para>
/// </summary>
public sealed class LogBodyBrushConverter : IMultiValueConverter
{
    /// <inheritdoc />
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var isError = values.Count > 0 && values[0] is true;
        var level = values.Count > 1 && values[1] is LogLevel l ? l : LogLevel.None;
        var key = isError || level == LogLevel.Error ? "VelaShellRed"
            : level == LogLevel.Warn ? "VelaWarning"
            : level == LogLevel.Debug ? "VelaTextTertiary"
            : "VelaShellWhite";
        return Converters.ThemeBrush(key, Brushes.Gray);
    }
}

/// <summary>仓库凭据状态的图标:拿到了凭据用锁,没拿到用警示 —— 它决定拉取会不会 401。</summary>
public sealed class AuthIconConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Docker.lock" : "Icon.triangle-alert";

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>仓库凭据状态的颜色。</summary>
public sealed class AuthBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is true ? "VelaStatusConnected" : "VelaWarning";
        return Converters.ThemeBrush(key, Brushes.Gray);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>悬空镜像用虚线圈,有标签的用层叠图标。</summary>
public sealed class ImageIconConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Docker.circle-dashed" : "Icon.layers";

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>执行记录的行色:命令行绿、标准错误红、其余是终端前景色。</summary>
public sealed class OutputLineForegroundConverter : IMultiValueConverter
{
    /// <inheritdoc />
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var isError = values.Count > 0 && values[0] is true;
        var isCommand = values.Count > 1 && values[1] is true;
        var level = values.Count > 2 && values[2] is LogLevel l ? l : LogLevel.None;
        // 命令本身 > 走 stderr > 正文里认出来的级别。最后这一档平时不会触发
        // (compose 自己的输出没有级别),但 up 的时候服务把 ERROR 打到 stdout 是常有的事。
        var key = isCommand ? "VelaStatusConnected"
            : isError || level == LogLevel.Error ? "VelaShellRed"
            : level == LogLevel.Warn ? "VelaWarning"
            : "VelaShellWhite";
        return Converters.ThemeBrush(key, Brushes.Gray);
    }
}

/// <summary>按资源键取画刷(堆叠占用条的每一段各用一个令牌)。</summary>
public sealed class ResourceBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is not string key ? Brushes.Transparent : (object)(Converters.ThemeBrush(key, Brushes.Transparent));
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>危险档的回收卡片用红边,其余用常规边。</summary>
public sealed class PruneBorderConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is RowTone.Danger ? "VelaShellRedDim" : "VelaBorderPrimary";
        return Converters.ThemeBrush(key, Brushes.Gray);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>高负载的条与数字转警示色。</summary>
public sealed class HotBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is true ? "VelaGaugeWarn" : "VelaGaugeCpu";
        return Converters.ThemeBrush(key, Brushes.Gray);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
