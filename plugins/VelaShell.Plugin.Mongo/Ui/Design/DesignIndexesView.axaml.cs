using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 集合设计 · 索引页。代码里只做两件 AXAML 不好做的事:
/// 索引编辑器字段表里字段 / 方向下拉的菜单(弹出层在独立可视树里,菜单项要直达行模型,现建最省事),
/// 以及字段行按住序号列的把手拖动排序(行尾的上移 / 下移按钮走视图模型命令)。
/// </summary>
public sealed partial class DesignIndexesView : UserControl
{
    /// <summary>字段表的行距(28 高、行间无空隙)。</summary>
    private const double RowPitch = 28;

    private NewKeyRow? _dragging;

    /// <summary>构造。</summary>
    public DesignIndexesView()
    {
        InitializeComponent();
        AddHandler(Button.ClickEvent, OnClick);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private DesignTabViewModel? ViewModel => DataContext as DesignTabViewModel;

    private void OnClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button button || ViewModel is not { } vm || button.DataContext is not NewKeyRow row)
        {
            return;
        }
        if (button.Name == "FieldPick")
        {
            ShowFieldMenu(button, vm, row);
            e.Handled = true;
        }
        else if (button.Name == "DirectionPick")
        {
            ShowDirectionMenu(button, vm, row);
            e.Handled = true;
        }
    }

    /// <summary>字段下拉:抽样到的字段,带类型色块与类型名。</summary>
    private static void ShowFieldMenu(Control anchor, DesignTabViewModel vm, NewKeyRow row)
    {
        var items = new List<Control>();
        foreach (FieldOption option in vm.FieldOptions)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(new Rectangle
            {
                Width = 7,
                Height = 7,
                RadiusX = 2,
                RadiusY = 2,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = ThemeBrushes.Get(option.Token, Brushes.Gray)
            });
            header.Children.Add(Label(anchor, option.Path, "VelaUiMonoFont", "VelaTextPrimary", "VelaFontSize11"));
            header.Children.Add(Label(anchor, option.KindName, "VelaUiFont", "VelaTextMuted", "VelaFontSize10"));
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => row.Field = option.Path;
            items.Add(item);
        }
        if (items.Count == 0)
        {
            items.Add(new MenuItem
            {
                Header = vm.IsAnalyzing ? vm.Loc["Design_Analyzing"] : vm.Loc["Design_NoSampleFields"],
                IsEnabled = false
            });
        }
        Open(anchor, items);
    }

    /// <summary>方向下拉:1 升序 / -1 降序。</summary>
    private static void ShowDirectionMenu(Control anchor, DesignTabViewModel vm, NewKeyRow row)
    {
        var items = new List<Control>();
        foreach (int direction in new[] { 1, -1 })
        {
            var item = new MenuItem
            {
                Header = vm.DirectionLabel(direction),
                Icon = new Glyph
                {
                    Key = direction > 0 ? "Mongo.arrow-up" : "Mongo.arrow-down",
                    Size = 12,
                    Brush = ThemeBrushes.Get(direction > 0 ? "VelaStatusConnected" : "VelaWarning", Brushes.Gray)
                }
            };
            item.Click += (_, _) => row.Direction = direction;
            items.Add(item);
        }
        Open(anchor, items);
    }

    /// <summary>
    /// 菜单里的一段文字。弹出层不在本视图的可视树里,本视图引入的文字类(<c>mono</c> 之类)够不着它,
    /// 字体、字号、颜色只能直接按宿主令牌设。
    /// </summary>
    private static TextBlock Label(Control anchor, string text, string font, string token, string size) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        FontFamily = anchor.TryFindResource(font, out object? f) && f is FontFamily family ? family : FontFamily.Default,
        FontSize = anchor.TryFindResource(size, out object? s) && s is double d ? d : 11,
        Foreground = ThemeBrushes.Get(token, Brushes.Gray)
    };

    private static void Open(Control anchor, List<Control> items)
    {
        var menu = new ContextMenu
        {
            ItemsSource = items,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            PlacementTarget = anchor
        };
        menu.Open(anchor);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source || ViewModel is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        Border? grip = source.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(static b => b.Name == "Grip");
        if (grip?.DataContext is NewKeyRow row)
        {
            _dragging = row;
            e.Pointer.Capture(grip);
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging is not { } row || ViewModel is not { } vm)
        {
            return;
        }
        double y = e.GetPosition(KeyRows).Y;
        int target = Math.Clamp((int)Math.Floor(y / RowPitch), 0, vm.NewKeys.Count - 1);
        int current = vm.NewKeys.IndexOf(row);
        if (current >= 0 && target != current)
        {
            vm.MoveKey(current, target);
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging is null)
        {
            return;
        }
        _dragging = null;
        e.Pointer.Capture(null);
    }
}
