using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>视图里用到的转换器(一律 <c>{x:Static ui:Converters.Xxx}</c> 引用)。</summary>
public static class Converters
{
    /// <summary>令牌名 → 画刷(跟随换肤,见 <see cref="ThemeBrushes" />)。</summary>
    public static IValueConverter Token { get; } =
        new FuncValueConverter<string?, IBrush>(static token => ThemeBrushes.Get(token ?? "VelaTextSecondary", Brushes.Gray));

    /// <summary>BSON 类型 → 画刷。</summary>
    public static IValueConverter KindBrush { get; } =
        new FuncValueConverter<BsonKind, IBrush>(static kind => ThemeBrushes.Get(BsonKinds.ColorToken(kind), Brushes.Gray));

    /// <summary>BSON 类型 → 显示名。</summary>
    public static IValueConverter KindName { get; } =
        new FuncValueConverter<BsonKind, string>(static kind => BsonKinds.Name(kind));

    /// <summary>值等于参数(字符串比较)→ true。<c>Classes.active</c> 这类绑定用。</summary>
    public static IValueConverter IsEqual { get; } = new EqualsConverter(false);

    /// <summary>值不等于参数 → true。</summary>
    public static IValueConverter IsNotEqual { get; } = new EqualsConverter(true);

    /// <summary>非空(对象非 null、字符串非空)→ true。</summary>
    public static IValueConverter NotEmpty { get; } =
        new FuncValueConverter<object?, bool>(static v => v is string s ? s.Length > 0 : v is not null);

    /// <summary>为空 → true。</summary>
    public static IValueConverter IsEmpty { get; } =
        new FuncValueConverter<object?, bool>(static v => v is string s ? s.Length == 0 : v is null);

    /// <summary>布尔取反。</summary>
    public static IValueConverter Not { get; } = new FuncValueConverter<bool, bool>(static v => !v);

    /// <summary>数字 &gt; 0 → true。</summary>
    public static IValueConverter Positive { get; } =
        new FuncValueConverter<object?, bool>(static v => v switch
        {
            int i => i > 0,
            long l => l > 0,
            double d => d > 0,
            _ => false
        });

    /// <summary>左缩进(double)→ <c>Thickness(左, 0, 10, 0)</c>(树行的内边距)。</summary>
    public static IValueConverter LeftPadding { get; } =
        new FuncValueConverter<double, Thickness>(static left => new(left, 0, 10, 0));

    /// <summary>字节数 → <c>42.1 GB</c>。</summary>
    public static IValueConverter Bytes { get; } =
        new FuncValueConverter<long, string>(static b => BsonText.Bytes(b));

    /// <summary>千分位整数。</summary>
    public static IValueConverter Grouped { get; } =
        new FuncValueConverter<long, string>(static n => BsonText.Grouped(n));

    /// <summary>0–1 的比例 → 百分比文字(<c>62%</c>)。</summary>
    public static IValueConverter Percent { get; } =
        new FuncValueConverter<double, string>(static p => (p * 100).ToString("0", CultureInfo.InvariantCulture) + "%");

    /// <summary>布尔 → 不透明度(true = 1,false = 0.4;禁用而不是隐藏的那种弱化)。</summary>
    public static IValueConverter Dim { get; } =
        new FuncValueConverter<bool, double>(static on => on ? 1d : 0.4d);

    private sealed class EqualsConverter(bool negate) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool equal = string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);
            return negate ? !equal : equal;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true ? parameter ?? "" : Avalonia.Data.BindingOperations.DoNothing;
    }
}
