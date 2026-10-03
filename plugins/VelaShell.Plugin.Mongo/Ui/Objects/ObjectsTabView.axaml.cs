using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 对象列表视图。代码只做视图该做的事:双击打开、右键菜单(按选中对象的种类现建)、
/// Enter 打开 / Delete 删除。
/// </summary>
public sealed partial class ObjectsTabView : UserControl
{
    private readonly ObjectsTabViewModel? _viewModel;

    /// <summary>用给定的视图模型初始化。</summary>
    internal ObjectsTabView(ObjectsTabViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        foreach (ListBox list in new[] { DetailsList, GridList, CompactList })
        {
            list.DoubleTapped += OnDoubleTapped;
            list.ContextRequested += OnContextRequested;
            list.KeyDown += OnListKeyDown;
        }
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public ObjectsTabView()
    {
        InitializeComponent();
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel is not null && (e.Source as Control)?.DataContext is ObjectItem item)
        {
            _viewModel.SelectedItem = item;
            _viewModel.OpenSelected();
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel?.SelectedItem is null || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Enter:
                _viewModel.OpenSelected();
                e.Handled = true;
                break;
            case Key.Delete:
                _viewModel.DropCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 右键菜单现建:集合、视图、桶能做的事各不相同,能不能做由命令自己的 CanExecute 说了算 ——
    /// 不能做的项直接不出现,而不是一排灰掉的菜单。
    /// </summary>
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_viewModel is null || (e.Source as Control)?.DataContext is not ObjectItem item)
        {
            return;
        }
        _viewModel.SelectedItem = item;
        Loc loc = _viewModel.Loc;
        var items = new List<Control>();

        void Add(string label, string icon, ICommand command, bool danger = false)
        {
            if (!command.CanExecute(null))
            {
                return;
            }
            items.Add(MenuKit.Command(label, icon, command, danger: danger));
        }

        void Separator()
        {
            if (items.Count > 0 && items[^1] is not Avalonia.Controls.Separator)
            {
                items.Add(new Avalonia.Controls.Separator());
            }
        }

        Add(_viewModel.OpenLabel, item.Kind == ObjectKind.Bucket ? "Mongo.hard-drive" : "Mongo.folder-open", _viewModel.OpenCommand);
        Add(loc["Tree_Design"], "Mongo.pencil-ruler", _viewModel.DesignCommand);
        Add(loc["Tree_Pipeline"], "Mongo.workflow", _viewModel.PipelineCommand);
        if (item.Kind != ObjectKind.Bucket)
        {
            Add(loc["Tree_NewQuery"], "Mongo.file-code", _viewModel.QueryCommand);
        }
        Separator();
        Add(loc["Tree_Import"], "Mongo.download", _viewModel.ImportCommand);
        Add(loc["Tree_Export"], "Mongo.upload", _viewModel.ExportCommand);
        Add(loc["Tree_CopyName"], "Mongo.copy", _viewModel.CopyNameCommand);
        Add(loc["Obj_CopyStructure"], "Mongo.code", _viewModel.CopyStructureCommand);
        Separator();
        Add(loc["Tree_Empty"], "Mongo.eraser", _viewModel.EmptyCommand, danger: true);
        Add(loc["Tree_Drop"], "Mongo.trash-2", _viewModel.DropCommand, danger: true);
        if (items.Count > 0 && items[^1] is Avalonia.Controls.Separator)
        {
            items.RemoveAt(items.Count - 1);
        }
        if (items.Count == 0)
        {
            return;
        }
        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(e.Source as Control ?? this);
        e.Handled = true;
    }
}
