using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 集合工作台的视图:装配三种数据视图、查询栏的键盘手势(Enter / Ctrl+Enter 查找)、
/// 历史下拉,以及标签级快捷键 Ctrl+S 应用 · Esc 放弃。
/// </summary>
public sealed partial class CollectionTabView : UserControl
{
    private readonly CollectionTabViewModel? _viewModel;

    /// <summary>用给定的视图模型初始化。</summary>
    internal CollectionTabView(CollectionTabViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        GridPane.Attach(viewModel);
        TreePane.Attach(viewModel);
        JsonPane.Attach(viewModel);
        FilterEditor.CompletionProvider = request =>
            Task.FromResult(viewModel.Sample.Complete(viewModel.Loc, request.Text, request.CaretOffset, filterMode: true));
        // 单行筛选框:Enter 就是查找(补全弹层开着时 Enter 先被编辑器自己拿去接受补全 ——
        // 它的隧道处理器注册得比这里早,已处理的事件这里收不到)。
        FilterEditor.Editor.TextArea.AddHandler(KeyDownEvent, OnFilterKeyDown, RoutingStrategies.Tunnel);
        FilterEditor.Editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        FilterEditor.Editor.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        FilterEditor.Editor.Padding = new Avalonia.Thickness(8, 5, 8, 0);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public CollectionTabView()
    {
        InitializeComponent();
    }

    /// <summary>网格面板(测试用)。</summary>
    internal GridPaneView Grid => GridPane;

    /// <summary>树面板(测试用)。</summary>
    internal DocTreeView Tree => TreePane;

    /// <summary>JSON 面板(测试用)。</summary>
    internal JsonCardsView Json => JsonPane;

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null || e.Key != Key.Enter)
        {
            return;
        }
        e.Handled = true;
        _viewModel.FindCommand.Execute(null);
    }

    private void OnOptionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel is not null)
        {
            e.Handled = true;
            _viewModel.FindCommand.Execute(null);
        }
    }

    private void OnPageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel is not null)
        {
            e.Handled = true;
            _viewModel.GoToPageCommand.Execute(null);
        }
    }

    /// <summary>
    /// 标签级快捷键:Ctrl+S 应用、Esc 放弃、Ctrl+Enter 查找。挂在冒泡阶段 ——
    /// 单元格编辑器、JSON 卡片编辑器自己的 Esc / Ctrl+S 先处理掉,这里只接剩下的。
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null || e.Handled)
        {
            return;
        }
        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.S when control:
                _viewModel.ApplyCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter when control:
                _viewModel.FindCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape when _viewModel.HasPending && !IsTextInput(e.Source):
                _viewModel.DiscardCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>历史 / 收藏下拉:收藏在前(星标),历史在后;点一条即套用并查找。</summary>
    private void OnHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }
        var items = new List<Control>();
        foreach (CollectionFilterItem item in _viewModel.HistoryItems.Take(30))
        {
            items.Add(new MenuItem
            {
                Header = new TextBlock
                {
                    Text = item.Text,
                    Classes = { "mono", "trim" },
                    MaxWidth = 520
                },
                Icon = new Glyph { Key = item.IconKey, Size = 12, Brush = ThemeBrushes.Get(item.IconToken, Avalonia.Media.Brushes.Gray) },
                Command = _viewModel.ApplyHistoryCommand,
                CommandParameter = item
            });
        }
        if (items.Count == 0)
        {
            items.Add(new MenuItem { Header = _viewModel.Loc["Cw_HistoryEmpty"], IsEnabled = false });
        }
        var menu = new ContextMenu
        {
            ItemsSource = items,
            Classes = { "cw" },
            Placement = PlacementMode.BottomEdgeAlignedRight,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        menu.Open(HistoryButton);
    }

    /// <summary>事件来自文本输入(文本框、代码编辑器)—— 那里的 Esc 是"不打了",不是"放弃全部修改"。</summary>
    private static bool IsTextInput(object? source)
    {
        for (var visual = source as Avalonia.Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is TextBox or CodeEditor)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>焦点在不在某个控件里面(子面板判断键盘归属用)。</summary>
    internal static bool IsWithin(Control container) =>
        TopLevel.GetTopLevel(container)?.FocusManager?.GetFocusedElement() is Control focused
        && (ReferenceEquals(focused, container) || container.IsVisualAncestorOf(focused));
}
