using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 用户与角色视图。只做视图该做的事:Ctrl+S 保存,以及「+ 添加角色」的下拉菜单在代码里现建 ——
/// 候选随当前编辑器的状态变(已有的不再列出),而且弹出层在独立的可视树里,
/// 写在 AXAML 里就得从弹层绕回编辑器的数据上下文。
/// </summary>
public sealed partial class UsersTabView : UserControl
{
    /// <summary>用给定的视图模型初始化。</summary>
    internal UsersTabView(UsersTabViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        // Ctrl+S 保存当前编辑器(与集合工作台的「Ctrl+S 应用」同一个手势)。
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.S || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                return;
            }
            ICommand? save = viewModel.ShowRoles ? viewModel.RoleEditor?.SaveCommand : viewModel.UserEditor?.SaveCommand;
            if (save?.CanExecute(null) == true)
            {
                save.Execute(null);
                e.Handled = true;
            }
        };
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public UsersTabView()
    {
        InitializeComponent();
    }

    /// <summary>「+ 添加角色」:自定义角色在前(强调色盾牌),其余内置角色在后(灰盾牌),中间一条分隔线。</summary>
    private void OnAddRoleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }
        (IReadOnlyList<RoleRef> roles, ICommand? command, Loc? loc) = button.DataContext switch
        {
            UserEditorViewModel user => (user.AddableRoles, (ICommand)user.AddRoleCommand, user.Loc),
            RoleEditorViewModel role => (role.AddableRoles, role.AddRoleCommand, role.Loc),
            _ => ([], null, null)
        };
        if (command is null || loc is null)
        {
            return;
        }
        var items = new List<Control>();
        foreach (RoleRef role in roles.Where(static r => !BuiltinRoles.IsBuiltin(r.Role)))
        {
            items.Add(Item(role, "Mongo.shield-check", "VelaAccent", command));
        }
        List<RoleRef> builtin = [.. roles.Where(static r => BuiltinRoles.IsBuiltin(r.Role))];
        if (items.Count > 0 && builtin.Count > 0)
        {
            items.Add(new Separator());
        }
        foreach (RoleRef role in builtin)
        {
            items.Add(Item(role, "Mongo.shield", "VelaTextTertiary", command));
        }
        if (items.Count == 0)
        {
            items.Add(new MenuItem { Header = loc["Users_NoAddable"], IsEnabled = false });
        }
        var flyout = new MenuFlyout { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedLeft };
        flyout.ShowAt(button);
        e.Handled = true;
    }

    private static MenuItem Item(RoleRef role, string icon, string token, ICommand command) => new()
    {
        Header = role.ToString(),
        Icon = new Glyph { Key = icon, Size = 13, Brush = ThemeBrushes.Get(token, Brushes.Gray) },
        Command = command,
        CommandParameter = role
    };
}
