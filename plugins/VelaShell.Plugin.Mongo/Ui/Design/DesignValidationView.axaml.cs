using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit.Rendering;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 集合设计 · 验证规则页。代码里做两件事:
/// 三个下拉菜单(validationLevel、validationAction、历史版本 —— 弹出层在独立可视树里,菜单项直接绑视图模型的命令最省事),
/// 以及规则编辑器里关键字的悬停说明卡。
/// </summary>
public sealed partial class DesignValidationView : UserControl
{
    /// <summary>构造。</summary>
    public DesignValidationView()
    {
        InitializeComponent();
        LevelPick.Click += (_, _) => ShowChoices(LevelPick, ["off", "moderate", "strict"], static vm => vm.RuleLevel, static vm => vm.SetLevelCommand,
            static (vm, value) => vm.Loc["Design_Level_" + value]);
        ActionPick.Click += (_, _) => ShowChoices(ActionPick, ["warn", "error"], static vm => vm.RuleAction, static vm => vm.SetActionCommand,
            static (vm, value) => vm.Loc["Design_Action_" + value]);
        HistoryPick.Click += OnHistory;
        TextView view = RuleEditor.Editor.TextArea.TextView;
        view.AddHandler(TextView.PointerHoverEvent, OnRuleHover);
        view.AddHandler(TextView.PointerHoverStoppedEvent, (_, _) => HoverDoc.IsOpen = false);
        RuleEditor.Editor.TextChanged += (_, _) => HoverDoc.IsOpen = false;
        RuleEditor.PointerExited += (_, _) => HoverDoc.IsOpen = false;
    }

    private DesignTabViewModel? ViewModel => DataContext as DesignTabViewModel;

    /// <summary>悬停在一个词上:是 <c>$jsonSchema</c> 关键字就弹说明卡。</summary>
    private void OnRuleHover(object? sender, PointerEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }
        TextView view = RuleEditor.Editor.TextArea.TextView;
        if (view.GetPositionFloor(e.GetPosition(view) + view.ScrollOffset) is not { } position)
        {
            return;
        }
        string text = RuleEditor.Editor.Document.Text;
        int offset = RuleEditor.Editor.Document.GetOffset(position.Location);
        if (WordAt(text, offset) is not { Length: > 0 } word || vm.KeywordHover(word) is not { } hover)
        {
            HoverDoc.IsOpen = false;
            return;
        }
        HoverTitle.Text = hover.Title;
        HoverBadge.Text = hover.Badge;
        HoverBody.Text = hover.Body;
        HoverDoc.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>光标处的那个标识符(含 <c>$</c>)。</summary>
    private static string WordAt(string text, int offset)
    {
        static bool Part(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';
        int start = Math.Clamp(offset, 0, text.Length);
        int end = start;
        while (start > 0 && Part(text[start - 1]))
        {
            start--;
        }
        while (end < text.Length && Part(text[end]))
        {
            end++;
        }
        return text[start..end];
    }

    /// <summary>一组取值的下拉:当前值打勾,每项右侧一句说明。</summary>
    private void ShowChoices(Button anchor, string[] values, Func<DesignTabViewModel, string> current,
        Func<DesignTabViewModel, RelayCommand<string>> command, Func<DesignTabViewModel, string, string> describe)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }
        var items = new List<Control>();
        foreach (string value in values)
        {
            items.Add(new MenuItem
            {
                Header = $"{value}  ·  {describe(vm, value)}",
                Command = command(vm),
                CommandParameter = value,
                Icon = value == current(vm) ? Check() : null
            });
        }
        Open(anchor, items);
    }

    /// <summary>历史版本:每次应用规则前自动存的旧规则,点一项放回编辑器(不应用)。</summary>
    private void OnHistory(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }
        var items = new List<Control>();
        foreach (RuleVersion version in vm.RuleHistory)
        {
            items.Add(new MenuItem
            {
                Header = version.Label,
                Command = vm.RestoreVersionCommand,
                CommandParameter = version,
                Icon = new Glyph { Key = "Mongo.history", Size = 12, Brush = ThemeBrushes.Get("VelaTextTertiary", Brushes.Gray) }
            });
        }
        if (items.Count == 0)
        {
            items.Add(new MenuItem { Header = vm.Loc["Design_NoHistory"], IsEnabled = false });
        }
        Open(HistoryPick, items);
    }

    private static Glyph Check() => new() { Key = "Mongo.check", Size = 12, Brush = ThemeBrushes.Get("VelaAccent", Brushes.Gray) };

    private static void Open(Control anchor, List<Control> items) =>
        new ContextMenu { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedLeft, PlacementTarget = anchor }.Open(anchor);
}
