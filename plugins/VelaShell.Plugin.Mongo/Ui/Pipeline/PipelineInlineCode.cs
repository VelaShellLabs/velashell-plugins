using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 一行按 token 着色的 mongosh 代码(折叠态阶段卡片里的 <c>{ path: "$items" }</c>)。
/// <para>
/// 只是一个 <see cref="TextBlock" />:把文本切成记号,每个记号一个 <see cref="Run" />。
/// 画刷取自 <see cref="ThemeBrushes" />(长期有效、换肤时就地改色),所以换主题不必重建。
/// </para>
/// </summary>
public sealed class PipelineInlineCode : TextBlock
{
    /// <summary>代码。</summary>
    public static readonly StyledProperty<string?> CodeProperty =
        AvaloniaProperty.Register<PipelineInlineCode, string?>(nameof(Code));

    /// <summary>整体压成弱色(停用的阶段)。</summary>
    public static readonly StyledProperty<bool> MutedProperty =
        AvaloniaProperty.Register<PipelineInlineCode, bool>(nameof(Muted));

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(TextBlock);

    /// <inheritdoc cref="CodeProperty" />
    public string? Code
    {
        get => GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    /// <inheritdoc cref="MutedProperty" />
    public bool Muted
    {
        get => GetValue(MutedProperty);
        set => SetValue(MutedProperty, value);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CodeProperty || change.Property == MutedProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        string code = Code ?? "";
        var inlines = new InlineCollection();
        foreach (PipelineToken token in PipelineTokens.Tokenize(code))
        {
            string color = Muted ? "VelaTextMuted" : PipelineTokens.ColorToken(token.Kind);
            inlines.Add(new Run(code.Substring(token.Start, token.Length)) { Foreground = ThemeBrushes.Get(color, Brushes.Gray) });
        }
        Inlines = inlines;
    }
}
