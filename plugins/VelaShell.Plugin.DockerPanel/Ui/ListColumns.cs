using Avalonia.Controls;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>
/// 一张列表的列宽。
/// <para>
/// 列宽是一组 <see cref="GridLength" />,**列头和每一行的 ColumnDefinitions 绑同一份实例** ——
/// Avalonia 没有 <c>SharedSizeGroup</c>,这是让列头与几百行单元格始终对齐的唯一办法;
/// 与宿主文件浏览器的列拖拽同一路数。
/// </para>
/// <para>
/// 各页的列不一样,所以宽度写成具名属性(界面绑得到、编译期查得出);
/// 而拖拽代码只按 <b>key</b> 认列,这几个方法就是它与具体页面之间的全部接口。
/// </para>
/// </summary>
public abstract class ListColumns : ObservableObject
{
    /// <summary>列与列之间那条拖拽轨道的宽度。与 XAML 里的 6 是同一个数。</summary>
    public const double TrackWidth = 6;

    /// <summary>可拖的列,次序与界面一致。</summary>
    public abstract IReadOnlyList<string> Keys { get; }

    /// <summary>按名字读一列的宽度。</summary>
    public abstract double Get(string key);

    /// <summary>按名字写一列的宽度。</summary>
    public abstract void Set(string key, double width);

    /// <summary>某一列的下限。再窄就只剩省略号了。</summary>
    public abstract double Min(string key);

    /// <summary>某一列双击自适应时的上限 —— 一个超长的镜像名不该把整张表挤没。</summary>
    public virtual double MaxAutoFit(string key) => 640;

    /// <summary>单元格里除文字之外还占着的宽度:图标、徽标、sparkline、右侧留白。</summary>
    public virtual double Padding(string key) => 18;

    /// <summary>用户拖出来的列宽只可能是像素值 —— 星形和 Auto 在这里没有意义。</summary>
    protected GridLength Clamp(GridLength value, string key)
    {
        var min = Min(key);
        return new(Math.Max(min, value.IsAbsolute ? value.Value : min));
    }
}
