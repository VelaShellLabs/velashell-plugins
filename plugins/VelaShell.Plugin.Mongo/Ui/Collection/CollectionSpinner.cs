using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 长查询卡片里的转圈(设计稿 22:VelaBorderSecondary 圆环 + 一段强调色弧)。
/// 只在可见时转 —— 隐藏的标签页里一直跑计时器是白耗电。
/// </summary>
public sealed class CollectionSpinner : Control
{
    private readonly DispatcherTimer _timer;
    private double _angle;

    /// <summary>构造。</summary>
    public CollectionSpinner()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) =>
        {
            if (!IsEffectivelyVisible)
            {
                return;
            }
            _angle = (_angle + 12) % 360;
            InvalidateVisual();
        };
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Sync();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            Sync();
        }
    }

    private void Sync()
    {
        if (IsVisible && VisualRoot is not null)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        double size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 4)
        {
            return;
        }
        double radius = size / 2 - 1.5;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var track = new Pen(ThemeBrushes.Get("VelaBorderSecondary", Brushes.Gray), 3);
        context.DrawEllipse(null, track, center, radius, radius);
        var arc = new StreamGeometry();
        using (StreamGeometryContext g = arc.Open())
        {
            double start = _angle * Math.PI / 180;
            double end = start + Math.PI / 2;
            g.BeginFigure(new Point(center.X + radius * Math.Cos(start), center.Y + radius * Math.Sin(start)), false);
            g.ArcTo(new Point(center.X + radius * Math.Cos(end), center.Y + radius * Math.Sin(end)), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(ThemeBrushes.Get("VelaAccent", Brushes.MediumPurple), 3, lineCap: PenLineCap.Round), arc);
    }
}
