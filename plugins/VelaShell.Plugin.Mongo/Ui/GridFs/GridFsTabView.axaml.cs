using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// GridFS 文件管理视图。代码后置只做视图模型做不了的事:
/// 双击 / 键盘手势、从系统拖进来的文件(拖放事件与数据格式只有控件层看得到)与点拖放区选文件、
/// 桶下拉与行右键菜单(弹出层在独立可视树里,菜单现建最省事)、以及"交给系统打开"(只有 TopLevel 拿得到 Launcher)。
/// </summary>
public sealed partial class GridFsTabView : UserControl
{
    private readonly GridFsTabViewModel? _viewModel;

    /// <summary>用给定的视图模型初始化。</summary>
    internal GridFsTabView(GridFsTabViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Launcher = LaunchAsync;

        FileList.DoubleTapped += OnRowDoubleTapped;
        TileList.DoubleTapped += OnRowDoubleTapped;
        FileList.ContextRequested += OnRowContextRequested;
        TileList.ContextRequested += OnRowContextRequested;
        BucketButton.Click += OnBucketClick;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);

        // 只读的 metadata 框按行数定高(见 MetadataHeight);AvaloniaEdit 默认还会多留半屏的"文档下方空白",
        // 于是内容明明放得下也挂着一根滚动条。这里收掉它,滚轮仍能滚超长的 metadata。
        MetaView.Editor.Options.AllowScrollBelowDocument = false;
        MetaView.Editor.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden;
        MetaView.Editor.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden;

        ListArea.AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        ListArea.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        ListArea.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        ListArea.AddHandler(DragDrop.DropEvent, OnDrop);
        DropZone.Tapped += OnDropZoneTapped;
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public GridFsTabView()
    {
        InitializeComponent();
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel is not null && (e.Source as Control)?.DataContext is GridFsEntry entry && e.Source is not CheckBox)
        {
            _viewModel.OpenEntryCommand.Execute(entry);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 面板内的键盘手势:Enter 打开、Backspace 上一级、Delete 删除、F2 改名、Ctrl+A 全选。
    /// 焦点在搜索框里时一个都不抢 —— 那里的 Backspace / Delete / Ctrl+A 是编辑文字。
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null || e.Handled || e.Source is TextBox || IsInsideEditor(e.Source))
        {
            return;
        }
        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Enter when _viewModel.SelectedEntry is { } entry:
                _viewModel.OpenEntryCommand.Execute(entry);
                break;
            case Key.Back:
                _viewModel.UpCommand.Execute(null);
                break;
            case Key.Delete:
                _viewModel.DeleteCommand.Execute(null);
                break;
            case Key.F2:
                _viewModel.RenameCommand.Execute(null);
                break;
            case Key.A when control:
                _viewModel.AllChecked = true;
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private static bool IsInsideEditor(object? source)
    {
        for (var element = source as Avalonia.StyledElement; element is not null; element = element.Parent)
        {
            if (element is CodeEditor)
            {
                return true;
            }
        }
        return false;
    }

    // ── 拖放 ───────────────────────────────────────────────────────────────

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        bool files = e.DataTransfer.TryGetFiles()?.Length > 0;
        e.DragEffects = files ? DragDropEffects.Copy : DragDropEffects.None;
        DropFrame.Classes.Set("over", files);
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => DropFrame.Classes.Set("over", false);

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropFrame.Classes.Set("over", false);
        e.Handled = true;
        if (_viewModel is null)
        {
            return;
        }
        // 文件与文件夹都收:文件夹在视图模型里递归展开、保留相对路径。
        List<string> paths = [.. (e.DataTransfer.TryGetFiles() ?? [])
            .Select(static item => item.TryGetLocalPath())
            .Where(static p => p is not null)
            .Select(static p => p!)];
        if (paths.Count > 0)
        {
            _ = _viewModel.UploadPathsAsync(paths);
        }
    }

    /// <summary>点拖放区的空白处 = 「上传文件」;框里的「选择文件 / 选择文件夹」链接自己处理,这里不再重复弹。</summary>
    private void OnDropZoneTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel is null || (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().Any() != false)
        {
            return;
        }
        e.Handled = true;
        _viewModel.UploadFilesCommand.Execute(null);
    }

    // ── 菜单 ───────────────────────────────────────────────────────────────

    /// <summary>桶下拉:同库的桶(当前的打勾)+ 回到根目录 + 新建存储桶。</summary>
    private async void OnBucketClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }
        Loc loc = _viewModel.Loc;
        IReadOnlyList<Core.GridFsBucketInfo> buckets = await _viewModel.ListBucketsAsync().ConfigureAwait(true);
        var items = new List<Control>
        {
            new MenuItem
            {
                Header = loc.Format("Fs_BucketsOf", _viewModel.Database),
                IsEnabled = false
            }
        };
        foreach (Core.GridFsBucketInfo bucket in buckets)
        {
            string name = bucket.Name;
            items.Add(new MenuItem
            {
                Header = name,
                Icon = new Glyph
                {
                    Key = name == _viewModel.Bucket.Name ? "Mongo.check" : "Mongo.hard-drive",
                    Size = 13,
                    Brush = ThemeBrushes.Get(name == _viewModel.Bucket.Name ? "VelaAccent" : "VelaWarning", Avalonia.Media.Brushes.Gray)
                },
                Command = new RelayCommand(() => _viewModel.SwitchBucket(name))
            });
        }
        items.Add(new Separator());
        items.Add(new MenuItem
        {
            Header = loc["Fs_GoRoot"],
            Icon = new Glyph { Key = "Mongo.corner-left-up", Size = 13, Brush = ThemeBrushes.Get("VelaTextTertiary", Avalonia.Media.Brushes.Gray) },
            Command = _viewModel.NavigateCommand,
            CommandParameter = ""
        });
        items.Add(new MenuItem
        {
            Header = loc["Fs_NewBucket"] + "…",
            Icon = new Glyph { Key = "Mongo.folder-plus", Size = 13, Brush = ThemeBrushes.Get("VelaTextTertiary", Avalonia.Media.Brushes.Gray) },
            Command = _viewModel.NewBucketCommand
        });
        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(BucketButton);
    }

    /// <summary>行右键:打开 / 下载 / 外部打开 / 重命名 / 删除(右键先把这一行选中,菜单作用于它或已勾选的那批)。</summary>
    private void OnRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_viewModel is null || (e.Source as Control)?.DataContext is not GridFsEntry entry || !entry.CanCheck)
        {
            return;
        }
        _viewModel.SelectedEntry = entry;
        Loc loc = _viewModel.Loc;
        var items = new List<Control>();

        void Add(string label, string icon, System.Windows.Input.ICommand command, object? parameter = null, bool danger = false)
        {
            items.Add(MenuKit.Command(label, icon, command, parameter, danger));
        }

        if (entry.IsFolder)
        {
            Add(loc["Fs_OpenFolder"], "Mongo.folder-open", _viewModel.OpenEntryCommand, entry);
        }
        else
        {
            Add(loc["Fs_OpenExternal"], "Mongo.external-link", _viewModel.OpenEntryCommand, entry);
        }
        Add(loc["Fs_Download"], "Mongo.download", _viewModel.DownloadCommand);
        Add(loc["Fs_Rename"], "Mongo.pencil-line", _viewModel.RenameCommand);
        items.Add(new Separator());
        Add(loc["Fs_Delete"], "Mongo.trash-2", _viewModel.DeleteCommand, danger: true);
        new ContextMenu { ItemsSource = items }.Open(e.Source as Control ?? FileList);
        e.Handled = true;
    }

    // ── 外部打开 ───────────────────────────────────────────────────────────

    /// <summary>交给系统默认程序打开:先走 TopLevel 的 Launcher,不行再退回 shell 执行。</summary>
    private async Task<bool> LaunchAsync(string path)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher
                && await launcher.LaunchFileInfoAsync(new FileInfo(path)).ConfigureAwait(true))
            {
                return true;
            }
            using Process? process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            // UseShellExecute 交给已在运行的程序打开时会返回 null,那也算成功。
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
