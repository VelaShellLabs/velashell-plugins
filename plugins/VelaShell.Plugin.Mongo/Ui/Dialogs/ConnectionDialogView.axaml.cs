using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>新建 / 编辑 MongoDB 连接(设计稿 10)。</summary>
public sealed partial class ConnectionDialogView : UserControl
{
    private readonly ConnectionDialogViewModel? _viewModel;

    /// <summary>用给定的视图模型初始化。</summary>
    /// <param name="viewModel">视图模型。</param>
    internal ConnectionDialogView(ConnectionDialogViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PreviewSpans.CollectionChanged += OnPreviewChanged;
        ActualThemeVariantChanged += (_, _) => RenderPreview();
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => NameBox.Focus());
        DetachedFromVisualTree += (_, _) => viewModel.PreviewSpans.CollectionChanged -= OnPreviewChanged;
        RenderPreview();
    }

    /// <summary>设计器用的无参构造。</summary>
    public ConnectionDialogView()
    {
        InitializeComponent();
    }

    private void OnPreviewChanged(object? sender, NotifyCollectionChangedEventArgs e) => RenderPreview();

    /// <summary>
    /// 连接串按角色着色(设计稿 10:scheme 灰、用户名强调色、口令占位灰、主机绿、库与参数值品红 / 黄)。
    /// 行内着色绑不了集合,就在这里拼 <see cref="Run" />;颜色取宿主令牌,换肤时重拼。
    /// </summary>
    private void RenderPreview()
    {
        if (_viewModel is null)
        {
            return;
        }
        InlineCollection inlines = PreviewBlock.Inlines ??= [];
        inlines.Clear();
        foreach (PreviewSpanViewModel span in _viewModel.PreviewSpans)
        {
            inlines.Add(new Run(span.Text) { Foreground = Brush(span.Role) });
        }
    }

    private IBrush? Brush(PreviewRole role)
    {
        string token = role switch
        {
            PreviewRole.Scheme or PreviewRole.Secret or PreviewRole.Key => "VelaTextTertiary",
            PreviewRole.User => "VelaAccent",
            PreviewRole.Host => "VelaShellGreen",
            PreviewRole.Path => "VelaShellYellow",
            PreviewRole.Value => "VelaShellMagenta",
            _ => "VelaTextSecondary"
        };
        return this.TryFindResource(token, ActualThemeVariant, out object? value) ? value as IBrush : null;
    }
}
