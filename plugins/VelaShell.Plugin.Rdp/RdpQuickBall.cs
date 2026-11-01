using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace VelaShell.Plugin.Rdp;

/// <summary>
/// One entry of the remote-keys fan. <see cref="Ring"/> 0 is the inner ring (most used), 1 the outer ring.
/// <see cref="Invoke"/> returns false to keep the fan open — an action that asked for a second click to confirm.
/// </summary>
internal sealed record RdpQuickAction(string Id, string LabelKey, string? Combo, string Icon, int Ring, Func<Task<bool>> Invoke);

/// <summary>
/// The remote-keys floating button (an "assistive touch" ball): a semi-transparent circle docked to the left or
/// right edge of the desktop. Dragging moves it and it snaps back to the nearer edge; a click fans its actions
/// out on two semicircles facing into the view, over a dimmed backdrop. Clicking the backdrop (or Esc, handled
/// by the view) folds the fan without anything reaching the remote desktop.
/// </summary>
internal sealed class RdpQuickBall : Canvas
{
    internal const double BallSize = 40, ItemSize = 36, BoxWidth = 80, BoxHeight = 60, EdgeGap = 10;
    // Radii chosen so a label never touches the neighbouring circle (inner items 84px apart, outer 111px).
    private const double InnerRadius = 110, OuterRadius = 180, DragThreshold = 4, IdleOpacity = 0.55, MinScale = 0.6;
    private static readonly TimeSpan Motion = TimeSpan.FromMilliseconds(180);
    private static readonly Geometry BallIcon = Geometry.Parse(
        "M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2Z M6 8h.01 M10 8h.01 M14 8h.01 M18 8h.01 M8 12h.01 M12 12h.01 M16 12h.01 M7 16h10"); // Lucide "keyboard"
    private static readonly Geometry CloseIcon = Geometry.Parse("M18 6 6 18 M6 6l12 12"); // Lucide "x"
    private static readonly ITransform FoldedTransform = Transform(default, 0.3);

    private readonly Border _scrim;
    private readonly Border _ball;
    private readonly Button _ballButton;
    private readonly RdpGlyph _ballGlyph;
    private readonly List<Item> _items = [];
    private readonly Transitions _ballMotion;
    private readonly Transitions _ballFade;
    private Loc _loc;
    private bool _right = true;
    private double _yFraction = 0.4;
    private Point _center;
    private Point? _press;
    private Vector _grab;
    private bool _dragging, _dragged, _pointerOver;
    private string? _armed;

    internal RdpQuickBall(IEnumerable<RdpQuickAction> actions, Loc loc)
    {
        _loc = loc;
        ClipToBounds = true;
        _scrim = new Border { Name = "RdpQuickBallScrim", IsVisible = false, Background = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)) };
        _scrim.PointerPressed += (_, e) => { Collapse(); e.Handled = true; };
        Children.Add(_scrim);
        foreach (RdpQuickAction action in actions)
        {
            Item item = BuildItem(action);
            _items.Add(item);
            Children.Add(item.Box);
        }

        _ballFade = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(120) } };
        _ballMotion = new Transitions
        {
            new DoubleTransition { Property = LeftProperty, Duration = Motion, Easing = new CubicEaseOut() },
            new DoubleTransition { Property = TopProperty, Duration = Motion, Easing = new CubicEaseOut() },
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(120) }
        };
        _ballGlyph = new RdpGlyph { Data = BallIcon, Width = 18, Height = 18 };
        _ballButton = new Button
        {
            Name = "RdpQuickBall", Focusable = false, Width = BallSize, Height = BallSize, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(BallSize / 2),
            Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Content = _ballGlyph
        };
        RdpChrome.AddIconButtonStyles(_ballButton, "VelaTextSecondary", "VelaTextPrimary");
        _ballButton.Click += OnBallClick;
        // Tunnel + handledEventsToo: the Button marks presses handled, but a drag still has to be seen.
        _ballButton.AddHandler(PointerPressedEvent, OnBallPressed, RoutingStrategies.Tunnel, true);
        _ballButton.AddHandler(PointerMovedEvent, OnBallMoved, RoutingStrategies.Tunnel, true);
        _ballButton.AddHandler(PointerReleasedEvent, OnBallReleased, RoutingStrategies.Tunnel, true);
        _ball = Circle(_ballButton, BallSize);
        _ball.Opacity = IdleOpacity;
        _ball.Transitions = _ballMotion;
        _ball.PointerEntered += (_, _) => { _pointerOver = true; UpdateBallOpacity(); };
        _ball.PointerExited += (_, _) => { _pointerOver = false; UpdateBallOpacity(); };
        Children.Add(_ball);
        SizeChanged += (_, _) => { if (IsExpanded) { Collapse(); } Place(animate: false); };
        ApplyLocale(loc);
    }

    internal bool IsExpanded { get; private set; }

    /// <summary>How much the open fan (radii and items) is shrunk to fit the view; 1 when there is room.</summary>
    internal double FanScale { get; private set; } = 1;

    /// <summary>Raised after the fan folds, so the view can hand the keyboard back to the desktop.</summary>
    internal Action? Folded { get; set; }

    /// <summary>Where each action's circle centre lands when expanded (canvas coordinates); for layout tests.</summary>
    internal IReadOnlyDictionary<string, Point> Targets { get; private set; } = new Dictionary<string, Point>();

    internal Point BallCenter => _center;

    internal bool IsDockedRight => _right;

    internal bool IsArmed(string id) => IsExpanded && _armed == id;

    /// <summary>Marks an action as waiting for a confirming second click (accent ring, "click again" label).</summary>
    internal void Arm(string id)
    {
        _armed = id;
        foreach (Item item in _items) { ShowArmed(item, item.Action.Id == id); }
    }

    internal void Toggle()
    {
        if (IsExpanded) { Collapse(); } else { Expand(); }
    }

    internal void Expand()
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (IsExpanded || w <= 0 || h <= 0) { return; }
        IsExpanded = true;
        _armed = null;
        double scale = FanScale = ScaleFor(w, h);
        // The fan (items included) scales as one, so the spacing that keeps labels clear holds in small views too.
        double reach = OuterRadius * Math.Sin(72 * Math.PI / 180);
        Point rest = Resting(w, h);
        double top = scale * (reach + ItemSize / 2) + EdgeGap, bottom = h - scale * (reach + BoxHeight - ItemSize / 2) - EdgeGap;
        _center = new Point(rest.X, top <= bottom ? Math.Clamp(rest.Y, top, bottom) : h / 2);
        SetBall(_center);
        _scrim.Width = w;
        _scrim.Height = h;
        _scrim.IsVisible = true;
        _ballGlyph.Data = CloseIcon;
        _ball[!Border.BorderBrushProperty] = new DynamicResourceExtension("VelaAccent");
        UpdateBallOpacity();

        var targets = new Dictionary<string, Point>();
        var offsets = new List<(Item Item, Vector Offset)>();
        foreach (IGrouping<int, Item> ring in _items.GroupBy(item => item.Action.Ring))
        {
            Item[] members = [.. ring];
            double radius = (ring.Key == 0 ? InnerRadius : OuterRadius) * scale, step = 180.0 / members.Length;
            for (int i = 0; i < members.Length; i++)
            {
                // Top (270°) round through the inward side (180°) to the bottom (90°); y grows downwards.
                double angle = (270 - step * (i + 0.5)) * Math.PI / 180;
                var offset = new Vector(radius * Math.Cos(angle) * (_right ? 1 : -1), radius * Math.Sin(angle));
                offsets.Add((members[i], offset));
                targets[members[i].Action.Id] = _center + offset;
            }
        }
        Targets = targets;
        foreach ((Item item, Vector offset) in offsets)
        {
            ShowArmed(item, false);
            SetLeft(item.Box, _center.X - BoxWidth / 2);
            SetTop(item.Box, _center.Y - ItemSize / 2);
            item.Box.RenderTransform = FoldedTransform;
            item.Box.Opacity = 0;
            item.Box.IsVisible = true;
            item.Box.IsHitTestVisible = true;
            item.Offset = offset;
        }
        // Next frame: move them out, so the transitions run from the folded state just applied.
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsExpanded) { return; }
            foreach (Item item in _items)
            {
                item.Box.RenderTransform = Transform(item.Offset, FanScale);
                item.Box.Opacity = 1;
            }
        });
    }

    internal void Collapse()
    {
        if (!IsExpanded) { return; }
        IsExpanded = false;
        _armed = null;
        _scrim.IsVisible = false;
        _ballGlyph.Data = BallIcon;
        _ball[!Border.BorderBrushProperty] = new DynamicResourceExtension("VelaBorderSecondary");
        foreach (Item item in _items)
        {
            item.Box.IsHitTestVisible = false;
            item.Box.RenderTransform = FoldedTransform;
            item.Box.Opacity = 0;
            ShowArmed(item, false);
        }
        DispatcherTimer.RunOnce(() =>
        {
            if (IsExpanded) { return; }
            foreach (Item item in _items) { item.Box.IsVisible = false; }
        }, Motion);
        Place();
        UpdateBallOpacity();
        Folded?.Invoke();
    }

    internal void ApplyLocale(Loc loc)
    {
        _loc = loc;
        ToolTip.SetTip(_ballButton, loc["QuickBall"]);
        Avalonia.Automation.AutomationProperties.SetName(_ballButton, loc["QuickBall"]);
        foreach (Item item in _items)
        {
            string label = loc[item.Action.LabelKey];
            string tip = item.Action.Combo is { } combo ? label + " · " + combo : loc[item.Action.LabelKey + "Tip"];
            ToolTip.SetTip(item.Button, tip);
            Avalonia.Automation.AutomationProperties.SetName(item.Button, label);
            ShowArmed(item, IsArmed(item.Action.Id));
        }
    }

    private void ShowArmed(Item item, bool armed)
    {
        item.Label.Text = armed ? _loc["QuickArmed"] : _loc[item.Action.LabelKey];
        item.Circle[!Border.BorderBrushProperty] = new DynamicResourceExtension(armed ? "VelaAccent" : "VelaBorderSecondary");
        item.Circle.BorderThickness = new Thickness(armed ? 2 : 1);
    }

    private Item BuildItem(RdpQuickAction action)
    {
        var button = new Button
        {
            Name = "Quick" + action.Id, Focusable = false, Width = ItemSize, Height = ItemSize, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(ItemSize / 2),
            Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Content = new RdpGlyph { Data = Geometry.Parse(action.Icon), Width = 16, Height = 16 }
        };
        RdpChrome.AddIconButtonStyles(button, "VelaTextPrimary", "VelaAccent");
        Border circle = Circle(button, ItemSize);
        circle.HorizontalAlignment = HorizontalAlignment.Center;
        var label = new TextBlock { TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = BoxWidth - 10 };
        label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("VelaTextPrimary");
        label[!TextBlock.FontFamilyProperty] = new DynamicResourceExtension("VelaUiFont");
        label[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize10");
        // The label sits on the remote desktop's own pixels, so it gets a surface pill to stay readable in both themes.
        var pill = new Border { Child = label, Padding = new Thickness(5, 1), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        pill[!Border.BackgroundProperty] = new DynamicResourceExtension("VelaBgSurface");
        var box = new StackPanel
        {
            Width = BoxWidth, Height = BoxHeight, IsVisible = false, IsHitTestVisible = false, Opacity = 0,
            RenderTransform = FoldedTransform,
            RenderTransformOrigin = new RelativePoint(0.5, ItemSize / 2 / BoxHeight, RelativeUnit.Relative),
            Transitions = new Transitions
            {
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = Motion, Easing = new CubicEaseOut() },
                new DoubleTransition { Property = OpacityProperty, Duration = Motion }
            },
            Children = { circle, pill }
        };
        var item = new Item(action, box, circle, button, label);
        button.Click += async (_, _) => await InvokeAsync(item);
        return item;
    }

    private async Task InvokeAsync(Item item)
    {
        if (!IsExpanded) { return; }
        bool fold;
        try { fold = await item.Action.Invoke(); }
        catch (Exception) { fold = true; }
        if (fold) { Collapse(); }
    }

    /// <summary>translate + scale built directly: string parsing chokes on values such as 1E-14px (cos 90°).</summary>
    private static ITransform Transform(Vector offset, double scale)
    {
        var builder = TransformOperations.CreateBuilder(2);
        builder.AppendTranslate(offset.X, offset.Y);
        builder.AppendScale(scale, scale);
        return builder.Build();
    }

    private static Border Circle(Button button, double size)
    {
        var circle = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), BorderThickness = new Thickness(1), Child = button };
        circle[!Border.BackgroundProperty] = new DynamicResourceExtension("VelaBgSurface");
        circle[!Border.BorderBrushProperty] = new DynamicResourceExtension("VelaBorderSecondary");
        circle[!Border.BoxShadowProperty] = new DynamicResourceExtension("VelaShadowWindow");
        return circle;
    }

    private static double ScaleFor(double w, double h)
    {
        // Fit the outer ring with its labels above and below the centre, and inwards from the docked edge.
        double vertical = (h / 2 - EdgeGap) / (OuterRadius * Math.Sin(72 * Math.PI / 180) + BoxHeight - ItemSize / 2);
        double horizontal = (w - 2 * EdgeGap - BallSize / 2) / (OuterRadius + BoxWidth / 2);
        return Math.Clamp(Math.Min(vertical, horizontal), MinScale, 1);
    }

    private Point Resting(double w, double h) => new(
        _right ? w - EdgeGap - BallSize / 2 : EdgeGap + BallSize / 2,
        Math.Clamp(_yFraction * h, EdgeGap + BallSize / 2, Math.Max(EdgeGap + BallSize / 2, h - EdgeGap - BallSize / 2)));

    private void Place(bool animate = true)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) { return; }
        _center = Resting(w, h);
        SetBall(_center, animate);
    }

    /// <summary>
    /// Positions the ball. Only snaps and fan moves glide: the first placement and resizes jump, otherwise the
    /// position transition would start from the unset Canvas.Left/Top (laid out as 0) and fly in from the corner.
    /// </summary>
    private void SetBall(Point center, bool animate = true)
    {
        if (!animate) { _ball.Transitions = _ballFade; }
        SetLeft(_ball, center.X - BallSize / 2);
        SetTop(_ball, center.Y - BallSize / 2);
        if (!animate && !_dragging) { _ball.Transitions = _ballMotion; }
    }

    private void UpdateBallOpacity() => _ball.Opacity = IsExpanded || _pointerOver || _dragging ? 1 : IdleOpacity;

    private void OnBallClick(object? sender, RoutedEventArgs e)
    {
        if (_dragged) { _dragged = false; return; }
        Toggle();
    }

    private void OnBallPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragged = false;
        _press = !IsExpanded && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ? e.GetPosition(this) : null;
        if (_press is { } press) { _grab = press - _center; }
    }

    private void OnBallMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press) { return; }
        Point point = e.GetPosition(this);
        if (!_dragging && (Math.Abs(point.X - press.X) > DragThreshold || Math.Abs(point.Y - press.Y) > DragThreshold))
        {
            _dragging = true;
            _ball.Transitions = _ballFade;
            UpdateBallOpacity();
        }
        if (!_dragging) { return; }
        DragTo(point - _grab);
        e.Handled = true;
    }

    private void OnBallReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_press is null) { return; }
        _press = null;
        if (!_dragging) { return; }
        _dragging = false;
        _dragged = true;
        Drop();
    }

    /// <summary>Moves the ball to follow a drag (clamped inside the view).</summary>
    internal void DragTo(Point center)
    {
        double w = Bounds.Width, h = Bounds.Height, r = BallSize / 2;
        _center = new Point(Math.Clamp(center.X, r, Math.Max(r, w - r)), Math.Clamp(center.Y, r, Math.Max(r, h - r)));
        SetBall(_center);
    }

    /// <summary>Ends a drag: the ball snaps to the nearer side edge and keeps its height as a fraction of the view.</summary>
    internal void Drop()
    {
        _right = _center.X > Bounds.Width / 2;
        _yFraction = Bounds.Height > 0 ? _center.Y / Bounds.Height : 0.4;
        _ball.Transitions = _ballMotion;
        Place();
        UpdateBallOpacity();
    }

    private sealed class Item(RdpQuickAction action, StackPanel box, Border circle, Button button, TextBlock label)
    {
        internal RdpQuickAction Action { get; } = action;
        internal StackPanel Box { get; } = box;
        internal Border Circle { get; } = circle;
        internal Button Button { get; } = button;
        internal TextBlock Label { get; } = label;
        internal Vector Offset { get; set; }
    }
}
