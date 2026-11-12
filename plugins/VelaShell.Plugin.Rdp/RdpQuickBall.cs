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
using Avalonia.Styling;
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
    private readonly Panel _ball;
    private readonly Button _ballButton;
    private readonly RdpGlyph _keysGlyph, _closeGlyph;
    private readonly Border _accentRing, _halo;
    private readonly List<Item> _items = [];
    private readonly Transitions _ballMotion;
    private readonly Transitions _ballFade;
    private Loc _loc;
    private double _yFraction = 0.4;
    private Point? _press;
    private Vector _grab;
    private bool _dragging, _dragged;
    private string? _armed;

    internal RdpQuickBall(IEnumerable<RdpQuickAction> actions, Loc loc)
    {
        _loc = loc;
        ClipToBounds = true;
        Styles.AddRange(ItemStyles());
        _scrim = new Border { Name = "RdpQuickBallScrim", IsVisible = false, Background = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)) };
        _scrim.PointerPressed += (_, e) => { Collapse(); e.Handled = true; };
        Children.Add(_scrim);
        foreach (RdpQuickAction action in actions)
        {
            Item item = BuildItem(action);
            _items.Add(item);
            Children.Add(item.Box);
        }

        _ballFade = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(120) }];
        _ballMotion =
        [
            new DoubleTransition { Property = LeftProperty, Duration = Motion, Easing = new CubicEaseOut() },
            new DoubleTransition { Property = TopProperty, Duration = Motion, Easing = new CubicEaseOut() },
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(120) }
        ];
        // Keyboard and × share the button and cross-fade with a quarter turn when the fan opens or folds.
        _keysGlyph = Glyph(BallIcon, 18);
        _closeGlyph = Glyph(CloseIcon, 16);
        _ballButton = new Button
        {
            Name = "RdpQuickBall",
            Focusable = false,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(BallSize / 2),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new Panel { Children = { _keysGlyph, _closeGlyph } }
        };
        // Hover: the translucent accent fill and accent glyph (the host's accent-pill language, DESIGN.md §5.1).
        RdpChrome.AddIconButtonStyles(_ballButton, "VelaTextPrimary", "VelaAccent", "VelaAccentDim");
        _ballButton.Click += OnBallClick;
        // Tunnel + handledEventsToo: the Button marks presses handled, but a drag still has to be seen.
        _ballButton.AddHandler(PointerPressedEvent, OnBallPressed, RoutingStrategies.Tunnel, true);
        _ballButton.AddHandler(PointerMovedEvent, OnBallMoved, RoutingStrategies.Tunnel, true);
        _ballButton.AddHandler(PointerReleasedEvent, OnBallReleased, RoutingStrategies.Tunnel, true);
        var body = new Border { Name = "RdpQuickBallBody", CornerRadius = new CornerRadius(BallSize / 2), Child = _ballButton };
        body[!Border.BackgroundProperty] = new DynamicResourceExtension("VelaBgSurface");
        body[!Border.BoxShadowProperty] = new DynamicResourceExtension("VelaShadowWindow");
        // The rings are separate layers above the button: its hover fill can never cover them (it used to
        // overflow a bordered circle by a pixel and hide half the ring). All of them follow the user's accent.
        _halo = Ring("RdpQuickBallHalo", 2, "VelaAccent");
        _halo.RenderTransform = Transform(default, 1);
        _accentRing = Ring("RdpQuickBallRing", 2, "VelaAccent");
        _ball = new Panel
        {
            Width = BallSize,
            Height = BallSize,
            Opacity = IdleOpacity,
            Transitions = _ballMotion,
            Children = { _halo, body, Ring(null, 1, "VelaBorderSecondary"), _accentRing }
        };
        _ball.PointerEntered += (_, _) => SetPointerOver(true);
        _ball.PointerExited += (_, _) => SetPointerOver(false);
        Children.Add(_ball);
        UpdateBall();
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

    internal Point BallCenter { get; private set; }

    internal bool IsDockedRight { get; private set; } = true;

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
        BallCenter = new Point(rest.X, top <= bottom ? Math.Clamp(rest.Y, top, bottom) : h / 2);
        SetBall(BallCenter);
        _scrim.Width = w;
        _scrim.Height = h;
        _scrim.IsVisible = true;
        UpdateBall();

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
                var offset = new Vector(radius * Math.Cos(angle) * (IsDockedRight ? 1 : -1), radius * Math.Sin(angle));
                offsets.Add((members[i], offset));
                targets[members[i].Action.Id] = BallCenter + offset;
            }
        }
        Targets = targets;
        foreach ((Item item, Vector offset) in offsets)
        {
            ShowArmed(item, false);
            SetLeft(item.Box, BallCenter.X - BoxWidth / 2);
            SetTop(item.Box, BallCenter.Y - ItemSize / 2);
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
        foreach (Item item in _items)
        {
            item.Box.IsHitTestVisible = false;
            item.Box.RenderTransform = FoldedTransform;
            item.Box.Opacity = 0;
            ShowArmed(item, false);
        }
        _ = DispatcherTimer.RunOnce(() =>
        {
            if (IsExpanded) { return; }
            foreach (Item item in _items) { item.Box.IsVisible = false; }
        }, Motion);
        Place();
        UpdateBall();
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
        item.Face.Classes.Set("armed", armed);
    }

    private Item BuildItem(RdpQuickAction action)
    {
        var button = new Button
        {
            Name = "Quick" + action.Id,
            Focusable = false,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(ItemSize / 2),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = Glyph(Geometry.Parse(action.Icon), 16)
        };
        RdpChrome.AddIconButtonStyles(button, "VelaTextPrimary", "VelaAccent");
        // Same layering as the ball: fill + button, then the outline and the accent ring above it. Hover and
        // "armed" (waiting for a confirming click) are style states of the face (see ItemStyles).
        var body = new Border { CornerRadius = new CornerRadius(ItemSize / 2), Child = button };
        body[!Border.BackgroundProperty] = new DynamicResourceExtension("VelaBgSurface");
        body[!Border.BoxShadowProperty] = new DynamicResourceExtension("VelaShadowWindow");
        Border accent = Ring(null, 2, "VelaAccent", ItemSize);
        accent.Classes.Add("accent");
        var circle = new Panel
        {
            Width = ItemSize,
            Height = ItemSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            Transitions = [new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(140), Easing = new CubicEaseOut() }],
            Children = { body, Ring(null, 1, "VelaBorderSecondary", ItemSize), accent }
        };
        circle.Classes.Add("quick-item");
        var label = new TextBlock { TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = BoxWidth - 10 };
        label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("VelaTextPrimary");
        label[!TextBlock.FontFamilyProperty] = new DynamicResourceExtension("VelaUiFont");
        label[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize10");
        // The label sits on the remote desktop's own pixels, so it gets a surface pill to stay readable in both themes.
        var pill = new Border { Child = label, Padding = new Thickness(5, 1), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        pill[!Border.BackgroundProperty] = new DynamicResourceExtension("VelaBgSurface");
        var box = new StackPanel
        {
            Width = BoxWidth,
            Height = BoxHeight,
            IsVisible = false,
            IsHitTestVisible = false,
            Opacity = 0,
            RenderTransform = FoldedTransform,
            RenderTransformOrigin = new RelativePoint(0.5, ItemSize / 2 / BoxHeight, RelativeUnit.Relative),
            Transitions =
            [
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = Motion, Easing = new CubicEaseOut() },
                new DoubleTransition { Property = OpacityProperty, Duration = Motion }
            ],
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
    private static TransformOperations Transform(Vector offset, double scale)
    {
        TransformOperations.Builder builder = TransformOperations.CreateBuilder(2);
        builder.AppendTranslate(offset.X, offset.Y);
        builder.AppendScale(scale, scale);
        return builder.Build();
    }

    private static TransformOperations Rotation(double degrees)
    {
        TransformOperations.Builder builder = TransformOperations.CreateBuilder(1);
        builder.AppendRotate(degrees * Math.PI / 180);
        return builder.Build();
    }

    private static RdpGlyph Glyph(Geometry data, double size) => new()
    {
        Data = data,
        Width = size,
        Height = size,
        RenderTransform = Rotation(0),
        Transitions =
        [
            new DoubleTransition { Property = OpacityProperty, Duration = Motion },
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(260), Easing = new BackEaseOut() }
        ]
    };

    /// <summary>A hit-test-transparent circular outline drawn above a button, so its hover fill cannot cover it.</summary>
    private static Border Ring(string? name, double thickness, string token, double size = BallSize)
    {
        var ring = new Border
        {
            Name = name,
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            BorderThickness = new Thickness(thickness),
            IsHitTestVisible = false,
            Transitions =
            [
                new DoubleTransition { Property = OpacityProperty, Duration = Motion },
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = new CubicEaseOut() }
            ]
        };
        ring[!Border.BorderBrushProperty] = new DynamicResourceExtension(token);
        return ring;
    }

    /// <summary>
    /// Hover and "armed" for the fan items, as styles so they can change without code: the accent ring fades
    /// in and the circle grows a little. Rest values are styles too, so the states can override them.
    /// </summary>
    private static IEnumerable<Style> ItemStyles()
    {
        static Style Set(Func<Selector?, Selector> selector, AvaloniaProperty property, object value) =>
            new(selector) { Setters = { new Setter(property, value) } };
        yield return Set(static x => x.OfType<Panel>().Class("quick-item"), RenderTransformProperty, Transform(default, 1));
        yield return Set(static x => x.OfType<Panel>().Class("quick-item").Class(":pointerover"), RenderTransformProperty, Transform(default, 1.08));
        yield return Set(static x => x.OfType<Panel>().Class("quick-item").Child().OfType<Border>().Class("accent"), OpacityProperty, 0d);
        yield return Set(static x => x.OfType<Panel>().Class("quick-item").Class(":pointerover").Child().OfType<Border>().Class("accent"), OpacityProperty, 1d);
        yield return Set(static x => x.OfType<Panel>().Class("quick-item").Class("armed").Child().OfType<Border>().Class("accent"), OpacityProperty, 1d);
    }

    internal bool IsBallPointerOver { get; private set; }

    /// <summary>Hover state of the ball (pointer entered/left); internal so headless tests can drive it.</summary>
    internal void SetPointerOver(bool over)
    {
        IsBallPointerOver = over;
        UpdateBall();
    }

    /// <summary>
    /// One place for the ball's look, so every state change animates from wherever it is:
    /// rest — dimmed keyboard; hover — full opacity, accent ring at 70%, a soft accent halo growing outwards;
    /// open — × (rotated in), solid accent ring; open + hover — the × turns a further quarter.
    /// </summary>
    private void UpdateBall()
    {
        bool hot = IsBallPointerOver || _dragging;
        _ball.Opacity = IsExpanded || hot ? 1 : IdleOpacity;
        _accentRing.Opacity = IsExpanded ? 1 : hot ? 0.7 : 0;
        _halo.Opacity = hot ? 0.35 : 0;
        _halo.RenderTransform = Transform(default, hot ? 1.3 : 1);
        _keysGlyph.Opacity = IsExpanded ? 0 : 1;
        _keysGlyph.RenderTransform = Rotation(IsExpanded ? 90 : 0);
        _closeGlyph.Opacity = IsExpanded ? 1 : 0;
        _closeGlyph.RenderTransform = Rotation(!IsExpanded ? -90 : hot ? 90 : 0);
    }

    private static double ScaleFor(double w, double h)
    {
        // Fit the outer ring with its labels above and below the centre, and inwards from the docked edge.
        double vertical = (h / 2 - EdgeGap) / (OuterRadius * Math.Sin(72 * Math.PI / 180) + BoxHeight - ItemSize / 2);
        double horizontal = (w - 2 * EdgeGap - BallSize / 2) / (OuterRadius + BoxWidth / 2);
        return Math.Clamp(Math.Min(vertical, horizontal), MinScale, 1);
    }

    private Point Resting(double w, double h) => new(
        IsDockedRight ? w - EdgeGap - BallSize / 2 : EdgeGap + BallSize / 2,
        Math.Clamp(_yFraction * h, EdgeGap + BallSize / 2, Math.Max(EdgeGap + BallSize / 2, h - EdgeGap - BallSize / 2)));

    private void Place(bool animate = true)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) { return; }
        BallCenter = Resting(w, h);
        SetBall(BallCenter, animate);
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

    private void OnBallClick(object? sender, RoutedEventArgs e)
    {
        if (_dragged) { _dragged = false; return; }
        Toggle();
    }

    private void OnBallPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragged = false;
        _press = !IsExpanded && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ? e.GetPosition(this) : null;
        if (_press is { } press) { _grab = press - BallCenter; }
    }

    private void OnBallMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press) { return; }
        Point point = e.GetPosition(this);
        if (!_dragging && (Math.Abs(point.X - press.X) > DragThreshold || Math.Abs(point.Y - press.Y) > DragThreshold))
        {
            _dragging = true;
            _ball.Transitions = _ballFade;
            UpdateBall();
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
        BallCenter = new Point(Math.Clamp(center.X, r, Math.Max(r, w - r)), Math.Clamp(center.Y, r, Math.Max(r, h - r)));
        SetBall(BallCenter);
    }

    /// <summary>Ends a drag: the ball snaps to the nearer side edge and keeps its height as a fraction of the view.</summary>
    internal void Drop()
    {
        IsDockedRight = BallCenter.X > Bounds.Width / 2;
        _yFraction = Bounds.Height > 0 ? BallCenter.Y / Bounds.Height : 0.4;
        _ball.Transitions = _ballMotion;
        Place();
        UpdateBall();
    }

    private sealed class Item(RdpQuickAction action, StackPanel box, Panel face, Button button, TextBlock label)
    {
        internal RdpQuickAction Action { get; } = action;
        internal StackPanel Box { get; } = box;
        internal Panel Face { get; } = face;
        internal Button Button { get; } = button;
        internal TextBlock Label { get; } = label;
        internal Vector Offset { get; set; }
    }
}
