using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 新建集合视图。打开即聚焦名称框;名称可用时 Enter 等同于「创建集合」。
/// 右侧命令预览的高度跟着脚本行数走(设计稿里代码框紧贴内容,提示框紧随其后)——
/// 编辑器放进 StackPanel 拿到的是无限高,自己不会收缩,只能按文档高度显式给。
/// </summary>
public sealed partial class NewCollectionDialogView : UserControl
{
    /// <summary>预览框的上下限:太矮看不出是代码框,太高会把提示框挤出对话框。</summary>
    private const double MinPreview = 40;
    private const double MaxPreview = 330;

    /// <summary>用给定的视图模型初始化。</summary>
    internal NewCollectionDialogView(NewCollectionDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _ = NameBox.Focus();
            FitPreview();
        });
        NameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && viewModel.CanCreate)
            {
                viewModel.CreateCommand.Execute(null);
                e.Handled = true;
            }
        };
        PreviewEditor.Editor.TextArea.TextView.VisualLinesChanged += (_, _) => FitPreview();
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public NewCollectionDialogView()
    {
        InitializeComponent();
    }

    private void FitPreview()
    {
        // 文档高度 + 编辑器上下内边距(6 + 6)+ 一点余量,避免最后一行贴边。
        double wanted = PreviewEditor.Editor.TextArea.TextView.DocumentHeight + 20;
        double height = Math.Clamp(wanted, MinPreview, MaxPreview);
        // 放得下就不要滚动条(那一道竖线在只读预览里只是噪音);放不下才让它出来。
        PreviewEditor.Editor.VerticalScrollBarVisibility = wanted > MaxPreview
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden;
        if (Math.Abs(PreviewEditor.Height - height) > 0.5)
        {
            PreviewEditor.Height = height;
        }
    }
}
