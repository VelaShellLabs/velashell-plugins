using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// JSON 视图:卡片编辑器里的 Ctrl+S 更新文档 / Esc 取消 / Alt+↑↓ 移动字段,点卡片换大纲,点大纲跳到那一行。
/// </summary>
public sealed partial class JsonCardsView : UserControl
{
    private CollectionTabViewModel? _viewModel;

    /// <summary>构造。</summary>
    public JsonCardsView()
    {
        InitializeComponent();
        CardList.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        CardList.AddHandler(PointerPressedEvent, OnCardPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        OutlineList.AddHandler(PointerPressedEvent, OnOutlinePressed, RoutingStrategies.Tunnel);
    }

    /// <summary>卡片列表(测试用)。</summary>
    internal ListBox List => CardList;

    /// <summary>接上视图模型。</summary>
    internal void Attach(CollectionTabViewModel viewModel) => _viewModel = viewModel;

    /// <summary>
    /// 编辑器里的键:冒泡阶段接 —— 补全弹层开着时 Esc / Enter 先被编辑器自己的隧道处理器拿走(关弹层 / 接受补全),
    /// 这里只接剩下的。
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (GridPaneView.Find<JsonCardViewModel>(e.Source) is not { IsEditing: true } card || EditorOf(e.Source) is not { } editor)
        {
            return;
        }
        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        switch (e.Key)
        {
            case Key.S when control:
                e.Handled = true;
                if (_viewModel?.FormatOnSave == true)
                {
                    card.Format();
                }
                _ = card.Update();
                break;
            case Key.Escape:
                e.Handled = true;
                card.CancelEdit();
                break;
            case Key.Up or Key.Down when alt:
                e.Handled = true;
                MoveLine(editor, e.Key == Key.Up ? -1 : 1);
                break;
        }
    }

    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is not null && GridPaneView.Find<JsonCardViewModel>(e.Source) is { } card && !ReferenceEquals(_viewModel.FocusedCard, card)
            && _viewModel.Cards.All(static c => !c.IsEditing))
        {
            _viewModel.FocusedCard = card;
        }
    }

    /// <summary>点大纲一项:编辑中的卡片把光标移到那一行;只读卡片滚到可见。</summary>
    private void OnOutlinePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel?.FocusedCard is not { } card || GridPaneView.Find<JsonOutlineItem>(e.Source) is not { } item)
        {
            return;
        }
        if (card.IsEditing && CardList.ContainerFromItem(card) is Control container
            && container.GetVisualDescendants().OfType<CodeEditor>().FirstOrDefault(static c => c.Classes.Contains("edit")) is { } editor)
        {
            DocumentLine line = editor.Editor.Document.GetLineByNumber(Math.Clamp(item.Line, 1, editor.Editor.Document.LineCount));
            editor.Editor.CaretOffset = line.Offset + (editor.Editor.Document.GetText(line).Length - editor.Editor.Document.GetText(line).TrimStart().Length);
            editor.ScrollToLine(item.Line);
            editor.FocusEditor();
        }
        else
        {
            CardList.ScrollIntoView(card);
        }
        e.Handled = true;
    }

    private static CodeEditor? EditorOf(object? source)
    {
        for (var visual = source as Avalonia.Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is CodeEditor editor)
            {
                return editor.Classes.Contains("edit") ? editor : null;
            }
        }
        return null;
    }

    /// <summary>
    /// Alt+↑ / ↓:把光标所在行与上 / 下一行对调(移动一个字段)。对调后补上缺的逗号 ——
    /// 行尾多出来的逗号无所谓(解析器容忍行尾逗号),少了才是语法错误。
    /// </summary>
    private static void MoveLine(CodeEditor editor, int direction)
    {
        TextDocument document = editor.Editor.Document;
        DocumentLine current = document.GetLineByOffset(editor.Editor.CaretOffset);
        int targetNumber = current.LineNumber + direction;
        if (targetNumber < 1 || targetNumber > document.LineCount)
        {
            return;
        }
        DocumentLine target = document.GetLineByNumber(targetNumber);
        string currentText = document.GetText(current);
        string targetText = document.GetText(target);
        if (IsBracketLine(currentText) || IsBracketLine(targetText))
        {
            return;
        }
        int column = editor.Editor.CaretOffset - current.Offset;
        (DocumentLine first, DocumentLine second) = direction < 0 ? (target, current) : (current, target);
        string firstText = document.GetText(first);
        string secondText = document.GetText(second);
        string swapped = EnsureComma(secondText) + "\n" + EnsureComma(firstText);
        document.Replace(first.Offset, second.EndOffset - first.Offset, swapped);
        DocumentLine landed = document.GetLineByNumber(targetNumber);
        editor.Editor.CaretOffset = Math.Min(landed.Offset + column, landed.EndOffset);
    }

    private static bool IsBracketLine(string text) => text.Trim() is "{" or "}" or "[" or "]" or "}," or "],";

    private static string EnsureComma(string text)
    {
        string trimmed = text.TrimEnd();
        return trimmed.Length == 0 || trimmed.EndsWith(',') || trimmed.EndsWith('{') || trimmed.EndsWith('[') ? text : trimmed + ",";
    }
}
