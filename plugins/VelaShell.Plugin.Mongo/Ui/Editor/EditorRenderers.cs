using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 编辑器的装饰层:整行标记的底色与左侧色条、诊断波浪线、行尾提示框与行尾小字。
/// <para>
/// 画在 <see cref="KnownLayer.Background" />(文字之下):底色与波浪线都不该盖住字。
/// 行尾的提示文字也画在这里 —— 它在行末空白处,本来就没有字可盖。
/// </para>
/// </summary>
internal sealed class DecorationRenderer(Func<string, IBrush> brush, Func<FontFamily> font, Func<double> fontSize) : IBackgroundRenderer
{
    /// <summary>诊断。</summary>
    public IReadOnlyList<EditorDiagnostic> Diagnostics { get; set; } = [];

    /// <summary>整行标记。</summary>
    public IReadOnlyList<LineMark> LineMarks { get; set; } = [];

    /// <inheritdoc />
    public KnownLayer Layer => KnownLayer.Background;

    /// <inheritdoc />
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid || textView.Document is not { } document)
        {
            return;
        }
        double width = textView.Bounds.Width;
        var marksByLine = LineMarks.GroupBy(static m => m.Line).ToDictionary(static g => g.Key, static g => g.First());

        foreach (VisualLine visualLine in textView.VisualLines)
        {
            int lineNumber = visualLine.FirstDocumentLine.LineNumber;
            double top = visualLine.VisualTop - textView.ScrollOffset.Y;
            if (marksByLine.TryGetValue(lineNumber, out LineMark? mark))
            {
                string token = Token(mark.Kind);
                drawingContext.FillRectangle(ThemeBrushes.GetDim(token, 0.12), new Rect(0, top, width, visualLine.Height));
                drawingContext.FillRectangle(brush(token), new Rect(0, top, 2, visualLine.Height));
                if (mark.Annotation is { Length: > 0 } note)
                {
                    DrawTrailing(textView, drawingContext, visualLine, top, note, brush(token), null);
                }
            }
        }

        foreach (EditorDiagnostic diagnostic in Diagnostics)
        {
            int start = Math.Clamp(diagnostic.Offset, 0, document.TextLength);
            int length = Math.Clamp(diagnostic.Length, 1, Math.Max(1, document.TextLength - start));
            var segment = new TextSegment { StartOffset = start, Length = length };
            IBrush stroke = brush(diagnostic.Severity switch
            {
                DiagnosticSeverity.Warning => "VelaWarning",
                DiagnosticSeverity.Info => "VelaInfo",
                _ => "VelaError"
            });
            foreach (Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            {
                DrawWave(drawingContext, stroke, rect);
            }
            // 行尾提示:错误给一个淡红底的小框(设计稿 03 第 6 行),其余只给文字。
            DocumentLine line = document.GetLineByOffset(start);
            if (textView.GetVisualLine(line.LineNumber) is { } visual)
            {
                double top = visual.VisualTop - textView.ScrollOffset.Y;
                // 修复说明("Alt+↵ 修复")用三级字色跟在消息后面,与设计稿 03 第 6 行一致。
                DrawTrailing(textView, drawingContext, visual, top, diagnostic.Message, stroke,
                    diagnostic.Severity == DiagnosticSeverity.Error ? ThemeBrushes.GetDim("VelaError", 0.12) : null,
                    icon: diagnostic.Severity == DiagnosticSeverity.Error,
                    suffix: diagnostic.FixLabel,
                    suffixBrush: brush("VelaTextTertiary"));
            }
        }
    }

    private void DrawTrailing(TextView textView, DrawingContext context, VisualLine visualLine, double top, string text,
        IBrush foreground, IBrush? background, bool icon = false, string? suffix = null, IBrush? suffixBrush = null)
    {
        TextLine last = visualLine.TextLines[^1];
        double x = visualLine.GetTextLineVisualXPosition(last, visualLine.VisualLengthWithEndOfLineMarker) - textView.ScrollOffset.X + 24;
        var formatted = new FormattedText(
            (icon ? "⊗ " : "") + text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(font()),
            Math.Max(9, fontSize() - 1),
            foreground);
        FormattedText? tail = suffix is { Length: > 0 }
            ? new FormattedText("  " + suffix, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(font()),
                Math.Max(9, fontSize() - 1), suffixBrush ?? foreground)
            : null;
        double y = top + ((visualLine.Height - formatted.Height) / 2);
        if (background is not null)
        {
            double width = formatted.Width + (tail?.Width ?? 0);
            context.FillRectangle(background, new Rect(x - 6, top + 2, width + 12, visualLine.Height - 4), 3);
        }
        context.DrawText(formatted, new Point(x, y));
        if (tail is not null)
        {
            context.DrawText(tail, new Point(x + formatted.Width, y));
        }
    }

    private static void DrawWave(DrawingContext context, IBrush brush, Rect rect)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            double y = rect.Bottom - 1;
            double x = rect.Left;
            g.BeginFigure(new Point(x, y), false);
            bool up = true;
            while (x < rect.Right)
            {
                x += 2;
                g.LineTo(new Point(Math.Min(x, rect.Right), up ? y - 2 : y));
                up = !up;
            }
            g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(brush, 1), geometry);
    }

    private static string Token(LineMarkKind kind) => kind switch
    {
        LineMarkKind.Modified => "VelaWarning",
        LineMarkKind.Added => "VelaStatusConnected",
        LineMarkKind.Removed => "VelaError",
        _ => "VelaAccent"
    };
}

/// <summary>
/// 行号左边那一列小圆点:有错误的行一个红点(设计稿 03 第 6 行)、改过的行一个橙点。
/// </summary>
internal sealed class MarkerMargin(Func<string, IBrush> brush) : AbstractMargin
{
    private readonly Dictionary<int, string> _dots = [];

    /// <summary>重算圆点(诊断与整行标记变了之后)。</summary>
    public void Update(TextDocument? document, IReadOnlyList<EditorDiagnostic> diagnostics, IReadOnlyList<LineMark> marks)
    {
        _dots.Clear();
        foreach (LineMark mark in marks)
        {
            if (mark.Kind == LineMarkKind.Modified)
            {
                _dots[mark.Line] = "VelaWarning";
            }
        }
        if (document is not null)
        {
            foreach (EditorDiagnostic diagnostic in diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error))
            {
                int offset = Math.Clamp(diagnostic.Offset, 0, document.TextLength);
                _dots[document.GetLineByOffset(offset).LineNumber] = "VelaError";
            }
        }
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        oldTextView?.VisualLinesChanged -= OnVisualLinesChanged;
        base.OnTextViewChanged(oldTextView, newTextView);
        newTextView?.VisualLinesChanged += OnVisualLinesChanged;
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize) => new(10, 0);

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        if (TextView is not { VisualLinesValid: true } view || _dots.Count == 0)
        {
            return;
        }
        foreach (VisualLine line in view.VisualLines)
        {
            if (_dots.TryGetValue(line.FirstDocumentLine.LineNumber, out string? token))
            {
                double y = line.VisualTop - view.ScrollOffset.Y + (line.Height / 2);
                context.DrawEllipse(brush(token), null, new Point(5, y), 3, 3);
            }
        }
    }
}
