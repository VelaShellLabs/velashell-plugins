namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>
/// 右侧详情抽屉的尺寸与形态。四个页面(容器 / 镜像 / 卷 / 网络)共用这一份逻辑。
/// <para>
/// 宽度存在**页面**上而不是抽屉自己的视图模型上:换一行就是换一个抽屉视图模型,
/// 宽度跟着它走的话,用户每点一行都得重拖一次。
/// </para>
/// <para>
/// 与视图是双向的:开 / 还原时由视图把这个宽度写进列定义,用户拖完(<c>DragCompleted</c>)
/// 再由视图把拖出来的实际宽度写回来 —— 与宿主侧栏那两条分割条一样。
/// 上界由列定义的 MaxWidth 兜着,因为"面板有多宽"只有视图知道。
/// </para>
/// </summary>
public sealed class DrawerState : ObservableObject
{
    /// <summary>再窄就摆不下抽屉头里那一行了。</summary>
    public const double MinWidth = 360;

    /// <summary>抽屉宽度(用户拖出来的)。设计稿四个页面都是 440。</summary>
    public double Width
    {
        get;
        set => SetField(ref field, Math.Max(MinWidth, value));
    } = 440;

    /// <summary>最大化:占满整个页签。</summary>
    public bool Maximized
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                OnPropertiesChanged(nameof(CanResize), nameof(ListVisible));
            }
        }
    }

    /// <summary>抽屉开着没有。由页面在选中项变化时写。</summary>
    public bool IsOpen
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                // 抽屉没了,最大化也就该没了 —— 不然下一次开抽屉会莫名其妙地直接铺满整页。
                if (!value)
                {
                    Maximized = false;
                }
                OnPropertyChanged(nameof(CanResize));
            }
        }
    }

    /// <summary>分割条要不要露出来。</summary>
    public bool CanResize => IsOpen && !Maximized;

    /// <summary>列表那一块要不要露出来(抽屉铺满时盖住它)。</summary>
    public bool ListVisible => !Maximized;

    /// <summary>
    /// 把抽屉至少撑到这么宽。
    /// <para>
    /// 容器的文件页那种三栏在 440px 里根本摆不开;但"撑满整个页签"又太狠 ——
    /// 撑到够用就停,列表还在旁边,分割条也还在,用户随时能往回拖。
    /// </para>
    /// </summary>
    public void EnsureAtLeast(double width) => Width = Math.Max(Width, width);

    /// <summary>切换最大化。抽屉头上那颗键绑它。</summary>
    public RelayCommand ToggleMaximizeCommand => field ??= new(_ =>
    {
        Maximized = !Maximized;
        return Task.CompletedTask;
    });
}
