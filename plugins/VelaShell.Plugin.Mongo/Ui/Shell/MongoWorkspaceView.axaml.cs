using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 工作台外壳的视图。只做视图该做的事:装配、键盘手势、右键菜单,
/// 以及把剪贴板与文件选择框(只有 TopLevel 才拿得到)借给视图模型。
/// </summary>
public sealed partial class MongoWorkspaceView : UserControl, IViewServices
{
    private readonly MongoWorkspaceViewModel _viewModel;

    /// <summary>用给定的视图模型初始化。</summary>
    /// <param name="viewModel">视图模型。</param>
    internal MongoWorkspaceView(MongoWorkspaceViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
        viewModel.ViewServices = this;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        TreeList.DoubleTapped += OnTreeDoubleTapped;
        TreeList.ContextRequested += OnTreeContextRequested;
    }

    /// <summary>headless 测试与设计器用的无参构造(不会被宿主走到)。</summary>
    public MongoWorkspaceView()
    {
        _viewModel = null!;
        InitializeComponent();
    }

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is TreeNode node)
        {
            _viewModel.OpenNodeCommand.Execute(node);
        }
    }

    /// <summary>
    /// 树的右键菜单在代码里按节点种类现建:数据库、集合、视图、桶要的菜单各不相同,
    /// 而且菜单项要直达外壳的命令 —— 写在 AXAML 里就得从弹出层的独立可视树里绕回外壳的数据上下文。
    /// </summary>
    private void OnTreeContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is not TreeNode node)
        {
            return;
        }
        _viewModel.SelectedNode = node;
        List<Control> items = TreeMenuItems(node);
        if (items.Count == 0)
        {
            return;
        }
        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(e.Source as Control ?? TreeList);
        e.Handled = true;
    }

    /// <summary>对象树某一行的右键菜单项(不可用的命令不列;测试直接读它)。</summary>
    internal List<Control> TreeMenuItems(TreeNode node)
    {
        Loc loc = _viewModel.Loc;
        var items = new List<Control>();

        void Add(string label, string icon, System.Windows.Input.ICommand command, bool danger = false)
        {
            if (!command.CanExecute(node))
            {
                return;
            }
            items.Add(MenuKit.Command(label, icon, command, node, danger));
        }

        void Separator() => MenuKit.Separator(items);

        switch (node.Kind)
        {
            case NodeKind.Collection or NodeKind.View:
                Add(loc["Tree_Open"], "Mongo.table-2", _viewModel.OpenNodeCommand);
                Add(loc["Tree_Design"], "Mongo.pencil-ruler", _viewModel.DesignNodeCommand);
                Add(loc["Tree_Pipeline"], "Mongo.workflow", _viewModel.PipelineNodeCommand);
                Add(loc["Tree_NewQuery"], "Mongo.file-code", _viewModel.QueryNodeCommand);
                Separator();
                // 在哪一组上点就能在哪一组里新建:集合行给「新建集合 / 以它为源新建视图」,视图行给「新建视图」。
                if (node.Kind == NodeKind.Collection)
                {
                    Add(loc["Tree_NewCollection"], "Mongo.plus", _viewModel.NewCollectionNodeCommand);
                }
                Add(loc["Tree_NewView"], "Mongo.eye", _viewModel.NewViewNodeCommand);
                Separator();
                Add(loc["Tree_Import"], "Mongo.download", _viewModel.ImportNodeCommand);
                Add(loc["Tree_Export"], "Mongo.upload", _viewModel.ExportNodeCommand);
                Add(loc["Tree_CopyName"], "Mongo.copy", _viewModel.CopyNameCommand);
                Separator();
                Add(loc["Tree_Empty"], "Mongo.eraser", _viewModel.EmptyCollectionCommand, danger: true);
                Add(loc["Tree_Drop"], "Mongo.trash-2", _viewModel.DropCollectionCommand, danger: true);
                break;
            case NodeKind.Database:
                Add(loc["Tree_Objects"], "Mongo.layout-grid", _viewModel.OpenNodeCommand);
                Add(loc["Tree_NewQuery"], "Mongo.file-code", _viewModel.QueryNodeCommand);
                Add(loc["Tree_Profiler"], "Mongo.timer", _viewModel.ProfilerNodeCommand);
                Separator();
                Add(loc["Tree_NewCollection"], "Mongo.plus", _viewModel.NewCollectionNodeCommand);
                Add(loc["Tree_NewView"], "Mongo.eye", _viewModel.NewViewNodeCommand);
                Add(loc["Tree_NewBucket"], "Mongo.folder-plus", _viewModel.NewBucketNodeCommand);
                Separator();
                Add(loc["Tree_Export"], "Mongo.upload", _viewModel.ExportNodeCommand);
                Add(loc["Tree_CopyName"], "Mongo.copy", _viewModel.CopyNameCommand);
                Add(loc["Tree_Refresh"], "Mongo.refresh-cw", _viewModel.RefreshNodeCommand);
                if (_viewModel.CanOfferDropDatabase)
                {
                    Separator();
                    Add(loc["Tree_DropDb"], "Mongo.trash-2", _viewModel.DropDatabaseCommand, danger: true);
                }
                break;
            case NodeKind.Bucket:
                Add(loc["Tree_Open"], "Mongo.hard-drive", _viewModel.OpenNodeCommand);
                Add(loc["Tree_UploadFiles"], "Mongo.upload", _viewModel.UploadFilesNodeCommand);
                Add(loc["Tree_UploadFolder"], "Mongo.folder-up", _viewModel.UploadFolderNodeCommand);
                Separator();
                Add(loc["Tree_NewBucket"], "Mongo.folder-plus", _viewModel.NewBucketNodeCommand);
                Add(loc["Tree_CopyName"], "Mongo.copy", _viewModel.CopyNameCommand);
                Separator();
                Add(loc["Tree_DropBucket"], "Mongo.trash-2", _viewModel.DropCollectionCommand, danger: true);
                break;
            case NodeKind.Folder:
                // 分组文件夹上的右键:先给「在这一组里新建」,再是刷新(设计稿 01 的对象树只画了刷新,新建是后补的入口)。
                switch (node.Folder)
                {
                    case FolderKind.Collections:
                        Add(loc["Tree_NewCollection"], "Mongo.plus", _viewModel.NewCollectionNodeCommand);
                        break;
                    case FolderKind.Views:
                        Add(loc["Tree_NewView"], "Mongo.eye", _viewModel.NewViewNodeCommand);
                        break;
                    case FolderKind.Buckets:
                        Add(loc["Tree_NewBucket"], "Mongo.folder-plus", _viewModel.NewBucketNodeCommand);
                        break;
                    case FolderKind.Users:
                        Add(loc["Tree_Open"], "Mongo.user", _viewModel.OpenNodeCommand);
                        break;
                }
                Separator();
                Add(loc["Tree_Refresh"], "Mongo.refresh-cw", _viewModel.RefreshNodeCommand);
                break;
            case NodeKind.Group:
                Add(loc["Conn_New"], "Mongo.plus", _viewModel.NewConnectionCommand);
                break;
            case NodeKind.Connection when node.Owner is { State: ConnectionState.Connected }:
                Add(loc["Tree_NewQuery"], "Mongo.file-code", _viewModel.QueryNodeCommand);
                Add(_viewModel.SystemDatabasesTip, _viewModel.ShowSystemDatabases ? "Mongo.eye-off" : "Mongo.eye",
                    _viewModel.ToggleSystemDatabasesCommand);
                Add(loc["Nav_Refresh"], "Mongo.refresh-cw", _viewModel.RefreshNodeCommand);
                Add(loc["Nav_CollapseAll"], "Mongo.list-collapse", _viewModel.CollapseAllCommand);
                Separator();
                Add(loc["Conn_Edit"], "Mongo.pencil", _viewModel.EditConnectionCommand);
                Add(loc["Conn_Duplicate"], "Mongo.copy", _viewModel.DuplicateConnectionCommand);
                Add(loc["Conn_Disconnect"], "Mongo.unplug", _viewModel.DisconnectNodeCommand);
                Separator();
                Add(loc["Conn_Delete"], "Mongo.trash-2", _viewModel.DeleteConnectionCommand, danger: true);
                break;
            case NodeKind.Connection:
                // 没连着(或正在连 / 没连上):先给「连接」,再是管理这条连接本身。
                Add(loc["Conn_Connect"], "Mongo.plug", _viewModel.ConnectNodeCommand);
                Add(loc["Conn_Disconnect"], "Mongo.unplug", _viewModel.DisconnectNodeCommand);
                Separator();
                Add(loc["Conn_Edit"], "Mongo.pencil", _viewModel.EditConnectionCommand);
                Add(loc["Conn_Duplicate"], "Mongo.copy", _viewModel.DuplicateConnectionCommand);
                Separator();
                Add(loc["Conn_Delete"], "Mongo.trash-2", _viewModel.DeleteConnectionCommand, danger: true);
                break;
            default:
                Add(loc["Tree_Open"], "Mongo.external-link", _viewModel.OpenNodeCommand);
                Add(loc["Tree_CopyName"], "Mongo.copy", _viewModel.CopyNameCommand);
                break;
        }
        // 末组的项全都不可用时会剩一条孤零零的分隔线收尾。
        if (items.Count > 0 && items[^1] is Separator)
        {
            items.RemoveAt(items.Count - 1);
        }
        return items;
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e) =>
        // 点遮罩不关对话框:向导填了一半、编辑器改了一半时误点一下就全没了,代价太大。
        e.Handled = true;

    /// <summary>
    /// 面板级快捷键:Ctrl+F 筛选对象树、Ctrl+W 关标签、Ctrl+T 新查询、F5 刷新当前标签、Esc 关对话框。
    /// 在输入框里时不抢字符键。
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (_viewModel.Dialog is { } dialog)
        {
            if (e.Key == Key.Escape && !e.Handled)
            {
                // 不直接关:有未保存修改 / 正在执行时 RequestClose 会先问一句。
                dialog.RequestCloseCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }
        switch (e.Key)
        {
            case Key.F when control && e.KeyModifiers.HasFlag(KeyModifiers.Shift):
            case Key.F when control && IsWithin(TreeList):
                _ = TreeFilterBox.Focus();
                TreeFilterBox.SelectAll();
                e.Handled = true;
                return;
            case Key.W when control && _viewModel.ActiveTab is { } tab:
                _ = _viewModel.CloseTabAsync(tab);
                e.Handled = true;
                return;
            case Key.T when control:
                _viewModel.NewQueryCommand.Execute(null);
                e.Handled = true;
                return;
            case Key.F5 when _viewModel.ActiveTab is { } active && active is not QueryTabViewModel:
                _ = active.RefreshAsync();
                e.Handled = true;
                return;
            case Key.Enter when ReferenceEquals(e.Source, TreeList) || IsWithin(TreeList):
                if (_viewModel.SelectedNode is { } node && e.Source is not TextBox)
                {
                    _viewModel.OpenNodeCommand.Execute(node);
                    e.Handled = true;
                }
                return;
            default:
                return;
        }
    }

    private static bool IsWithin(Control container) =>
        TopLevel.GetTopLevel(container)?.FocusManager?.GetFocusedElement() is Control focused
        && (ReferenceEquals(focused, container) || container.IsVisualAncestorOf(focused));

    // ── IViewServices ───────────────────────────────────────────────────────

    /// <inheritdoc />
    Task IViewServices.CopyAsync(string text) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text).ConfigureAwait(true);
            }
        });

    /// <inheritdoc />
    async Task<string?> IViewServices.PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<FileKind> kinds)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage)
        {
            return null;
        }
        IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = [.. kinds.Select(static k => new FilePickerFileType(k.Name) { Patterns = k.Patterns })]
        }).ConfigureAwait(true);
        return file?.TryGetLocalPath();
    }

    /// <inheritdoc />
    async Task<IReadOnlyList<string>> IViewServices.PickOpenFilesAsync(string title, IReadOnlyList<FileKind> kinds, bool multiple)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage)
        {
            return [];
        }
        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiple,
            FileTypeFilter = [.. kinds.Select(static k => new FilePickerFileType(k.Name) { Patterns = k.Patterns })]
        }).ConfigureAwait(true);
        return [.. files.Select(static f => f.TryGetLocalPath()).Where(static p => p is not null).Select(static p => p!)];
    }

    /// <inheritdoc />
    async Task<string?> IViewServices.PickFolderAsync(string title)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage)
        {
            return null;
        }
        IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        }).ConfigureAwait(true);
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }
}
