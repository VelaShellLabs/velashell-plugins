using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>
/// 容器文件页的视图。
/// <para>
/// 代码后置做两件视图模型做不了的事:把编辑器的光标位置报给状态条,
/// 以及接住拖进来的文件 —— 拖放的事件与数据格式只有控件层看得到。
/// </para>
/// </summary>
public sealed partial class ContainerFilesView : UserControl
{
    /// <summary>建视图。</summary>
    public ContainerFilesView()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (this.FindControl<TextBox>("Editor") is { } editor)
        {
            // 光标位置要在**每次选区变化**时报,不只是敲字时 —— 点一下也会移动光标。
            editor.PropertyChanged += (_, args) =>
            {
                if (args.Property == TextBox.CaretIndexProperty
                    && DataContext is ContainerFilesViewModel viewModel)
                {
                    ReportCaret(viewModel, editor);
                }
            };
        }
    }

    private static void ReportCaret(ContainerFilesViewModel viewModel, TextBox editor)
    {
        var text = editor.Text ?? "";
        var caret = Math.Clamp(editor.CaretIndex, 0, text.Length);
        var line = 1;
        var lineStart = 0;
        for (var i = 0; i < caret; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }
        viewModel.ReportCaret(line, caret - lineStart + 1);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var acceptable = DataContext is ContainerFilesViewModel { CanPickFiles: true }
                          && e.DataTransfer.TryGetFiles()?.Length > 0;
        e.DragEffects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ContainerFilesViewModel viewModel)
        {
            return;
        }
        // 只接第一个:一次拖十个文件进容器,失败到一半留下的半套文件比什么都不做更糟。
        var file = e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>().FirstOrDefault();
        if (file is not null)
        {
            _ = viewModel.UploadDroppedAsync(file);
        }
        e.Handled = true;
    }
}
