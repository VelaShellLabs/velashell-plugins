using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 自带视图的视图模型。各功能区自己的小对话框(导出为代码、新建存储桶、编辑 metadata…)实现它,
/// 就能经 <see cref="IWorkbench.ShowDialog" /> 挂到覆盖层上,而不必回来改下面那张表。
/// </summary>
internal interface IViewFactory
{
    /// <summary>建视图(只调一次)。</summary>
    Control CreateView();
}

/// <summary>
/// 视图模型 → 视图。一张显式的表,而不是按命名约定反射:
/// 插件跑在独立的装载上下文里,反射找类型在裁剪 / 单文件发布下都可能找空;
/// 而一张表在编译期就保证了每个视图模型都有视图。
/// </summary>
internal sealed class ViewLocator : IDataTemplate
{
    /// <summary>共享实例。</summary>
    public static ViewLocator Instance { get; } = new();

    /// <summary>
    /// 对话框视图按视图模型缓存:确认框会临时顶掉当前对话框、确认完再放回来,
    /// 重建视图的话滚动位置、焦点、编辑器光标与撤销栈全丢(文档编辑器里改到一半弹一次确认就前功尽弃)。
    /// 弱引用表:对话框关掉、视图模型被回收,视图随之回收。
    /// </summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DialogViewModel, Control> _dialogViews = [];

    /// <inheritdoc />
    public Control? Build(object? param) =>
        param is DialogViewModel dialog ? _dialogViews.GetValue(dialog, d => Create(d)!) : Create(param);

    private static Control? Create(object? param) => param switch
    {
        ObjectsTabViewModel vm => new ObjectsTabView(vm),
        CollectionTabViewModel vm => new CollectionTabView(vm),
        QueryTabViewModel vm => new QueryTabView(vm),
        PipelineTabViewModel vm => new PipelineTabView(vm),
        GridFsTabViewModel vm => new GridFsTabView(vm),
        DesignTabViewModel vm => new DesignTabView(vm),
        MonitorTabViewModel vm => new MonitorTabView(vm),
        ProfilerTabViewModel vm => new ProfilerTabView(vm),
        UsersTabViewModel vm => new UsersTabView(vm),
        ConfirmDialogViewModel vm => new ConfirmDialogView(vm),
        ConnectionStateTabViewModel vm => new ConnectionStateTabView(vm),
        ConnectionDialogViewModel vm => new ConnectionDialogView(vm),
        NewCollectionDialogViewModel vm => new NewCollectionDialogView(vm),
        DocumentEditorDialogViewModel vm => new DocumentEditorDialogView(vm),
        ConflictDialogViewModel vm => new ConflictDialogView(vm),
        ExportWizardViewModel vm => new ExportWizardView(vm),
        ImportWizardViewModel vm => new ImportWizardView(vm),
        TransferWizardViewModel vm => new TransferWizardView(vm),
        IViewFactory factory => factory.CreateView(),
        _ => new TextBlock { Text = param?.GetType().Name ?? "" }
    };

    /// <inheritdoc />
    public bool Match(object? data) => data is WorkspaceTab or DialogViewModel;
}

/// <summary>
/// 标签页内容宿主:每个标签的视图**只建一次**,切换标签只是换可见性。
/// <para>
/// 用一个 <c>ContentControl</c> 绑当前标签的话,每次切换都会把视图拆掉重建 ——
/// 查询编辑器丢光标与撤销栈、网格丢滚动位置、监控页的图表从头再画。
/// 这一点在设计稿的使用方式下是致命的:用户在「orders」「查询 1」「fs」之间来回切是常态。
/// </para>
/// </summary>
internal sealed class TabContentHost : Panel
{
    private readonly Dictionary<WorkspaceTab, Control> _views = [];

    /// <summary>标签集合。</summary>
    public static readonly StyledProperty<IEnumerable<WorkspaceTab>?> TabsProperty =
        AvaloniaProperty.Register<TabContentHost, IEnumerable<WorkspaceTab>?>(nameof(Tabs));

    /// <summary>当前标签。</summary>
    public static readonly StyledProperty<WorkspaceTab?> ActiveProperty =
        AvaloniaProperty.Register<TabContentHost, WorkspaceTab?>(nameof(Active));

    /// <summary>标签集合。</summary>
    public IEnumerable<WorkspaceTab>? Tabs
    {
        get => GetValue(TabsProperty);
        set => SetValue(TabsProperty, value);
    }

    /// <summary>当前标签。</summary>
    public WorkspaceTab? Active
    {
        get => GetValue(ActiveProperty);
        set => SetValue(ActiveProperty, value);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TabsProperty)
        {
            if (change.OldValue is INotifyCollectionChanged old)
            {
                old.CollectionChanged -= OnTabsChanged;
            }
            if (change.NewValue is INotifyCollectionChanged next)
            {
                next.CollectionChanged += OnTabsChanged;
            }
            Sync();
        }
        else if (change.Property == ActiveProperty)
        {
            Sync();
        }
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Sync();

    private void Sync()
    {
        HashSet<WorkspaceTab> alive = [.. Tabs ?? []];
        foreach (WorkspaceTab gone in _views.Keys.Where(t => !alive.Contains(t)).ToList())
        {
            Children.Remove(_views[gone]);
            _views.Remove(gone);
        }
        if (Active is { } active && !_views.ContainsKey(active))
        {
            Control view = ViewLocator.Instance.Build(active) ?? new Panel();
            view.DataContext = active;
            _views[active] = view;
            Children.Add(view);
        }
        foreach ((WorkspaceTab tab, Control view) in _views)
        {
            view.IsVisible = ReferenceEquals(tab, Active);
        }
    }
}
