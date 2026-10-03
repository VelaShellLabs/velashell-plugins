using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 网格 / 树 / 检查器右键菜单的零件(设计稿 13:图标 + 文字 + 右侧快捷键小字,底部「筛选将生成 …」)。
/// 菜单在 code-behind 里现建:菜单项要直达视图模型的方法,写在 AXAML 里就得从弹出层的独立可视树绕回数据上下文。
/// </summary>
internal static class CollectionMenus
{
    /// <summary>一项菜单。</summary>
    /// <param name="label">文字。</param>
    /// <param name="icon">图标键。</param>
    /// <param name="hint">右侧小字(快捷键、路径);没有为 <see langword="null" />。</param>
    /// <param name="action">点了做什么。</param>
    /// <param name="danger">危险操作(红字红图标)。</param>
    /// <param name="accent">首项强调(设计稿「编辑值」的强调色图标)。</param>
    /// <param name="enabled">可用。</param>
    public static MenuItem Item(string label, string icon, string? hint, Action action, bool danger = false, bool accent = false, bool enabled = true)
    {
        string token = danger ? "VelaError" : accent ? "VelaAccent" : "VelaTextTertiary";
        var item = new MenuItem
        {
            Header = Header(label, hint, danger),
            Icon = new Glyph { Key = icon, Size = 12, Brush = ThemeBrushes.Get(token, Brushes.Gray) },
            IsEnabled = enabled
        };
        if (danger)
        {
            item.Classes.Add("danger");
        }
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>带子菜单的一项(「修改类型 ›」)。</summary>
    public static MenuItem Submenu(string label, string icon, IEnumerable<Control> children, bool enabled = true) => new()
    {
        Header = Header(label, null, false),
        Icon = new Glyph { Key = icon, Size = 12, Brush = ThemeBrushes.Get("VelaTextTertiary", Brushes.Gray) },
        ItemsSource = children.ToList(),
        IsEnabled = enabled
    };

    /// <summary>
    /// 类型菜单(设计稿 01 检查器右侧那个):色块 + 类型名,当前类型打勾。
    /// </summary>
    public static IEnumerable<Control> KindItems(BsonKind current, Action<BsonKind> pick)
    {
        foreach (BsonKind kind in BsonKinds.Editable)
        {
            var row = new DockPanel { MinWidth = 120 };
            if (kind == current)
            {
                var check = new Glyph { Key = "Mongo.check", Size = 11, Brush = ThemeBrushes.Get("VelaAccent", Brushes.Gray) };
                DockPanel.SetDock(check, Dock.Right);
                row.Children.Add(check);
            }
            row.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new Rectangle
                    {
                        Width = 8,
                        Height = 8,
                        RadiusX = 2,
                        RadiusY = 2,
                        Fill = ThemeBrushes.Get(BsonKinds.ColorToken(kind), Brushes.Gray),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = BsonKinds.MenuName(kind),
                        Classes = { "mono" },
                        Foreground = ThemeBrushes.Get(kind == current ? "VelaTextPrimary" : "VelaTextSecondary", Brushes.Gray)
                    }
                }
            });
            var item = new MenuItem { Header = row };
            BsonKind captured = kind;
            item.Click += (_, _) => pick(captured);
            yield return item;
        }
    }

    /// <summary>分隔线。</summary>
    public static Separator Separator() => new();

    /// <summary>
    /// 菜单底部的「筛选将生成 { "items.sku": "SKU-7710" }」:点它等于「按此值筛选」。
    /// </summary>
    public static MenuItem FilterHint(Loc loc, string path, BsonValue? value, Action apply)
    {
        var item = new MenuItem
        {
            Header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = loc["Cw_MenuFilterPreview"], Classes = { "ui", "small", "muted" } },
                    new TextBlock
                    {
                        Text = CollectionTabViewModel.FilterFor(path, value),
                        Classes = { "mono", "small", "trim" },
                        MaxWidth = 300,
                        Foreground = ThemeBrushes.Get("VelaShellCyan", Brushes.Gray)
                    }
                }
            }
        };
        item.Classes.Add("hint");
        item.Click += (_, _) => apply();
        return item;
    }

    /// <summary>装成一个菜单并在 <paramref name="target" /> 上打开。</summary>
    public static void Open(Control target, IEnumerable<Control> items)
    {
        var menu = new ContextMenu { ItemsSource = items.ToList() };
        menu.Classes.Add("cw");
        menu.Open(target);
    }

    private static Control Header(string label, string? hint, bool danger)
    {
        var grid = new Grid { ColumnDefinitions = [with("*,Auto")], MinWidth = 190 };
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        if (danger)
        {
            text.Foreground = ThemeBrushes.Get("VelaError", Brushes.Red);
        }
        grid.Children.Add(text);
        if (!string.IsNullOrEmpty(hint))
        {
            var right = new TextBlock
            {
                Text = hint,
                Classes = { "mono", "small", "muted" },
                Margin = new Thickness(16, 0, 0, 0),
                MaxWidth = 140,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
        }
        return grid;
    }
}
