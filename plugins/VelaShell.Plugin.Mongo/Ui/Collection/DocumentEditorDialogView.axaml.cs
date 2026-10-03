using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 文档编辑器的视图。code-behind 只做视图模型做不了的那几样:读剪贴板、拖动排序的指针跟踪、
/// 字段名补全弹层的键盘导航、日历弹层、把焦点放到新加的字段上,以及 Ctrl+S / Ctrl+Enter / Ctrl+F。
/// </summary>
public sealed partial class DocumentEditorDialogView : UserControl
{
    private DocumentEditorDialogViewModel? _subscribed;
    private TopLevel? _topLevel;
    private DocumentEditorRow? _dragRow;
    private DocumentEditorRow? _dropRow;
    private bool _dropAfter;
    private TextBox? _suggestBox;
    private DocumentEditorRow? _suggestRow;

    /// <summary>用给定的视图模型初始化。</summary>
    internal DocumentEditorDialogView(DocumentEditorDialogViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public DocumentEditorDialogView()
    {
        InitializeComponent();
    }

    private DocumentEditorDialogViewModel? ViewModel => DataContext as DocumentEditorDialogViewModel;

    // ── 生命周期:订阅"把焦点放到新字段上" ───────────────────────────────────

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe(ViewModel);
        // 快捷键挂在窗口的隧道上:焦点落在空处(关掉查找框、点了不可聚焦的地方)时 Ctrl+S 照样要能保存。
        // 是否接手由 OwnsKeyboard 判断 —— 宿主切到别的标签页时这个视图不可见,不能抢别人的 Ctrl+S。
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // 外壳弹确认框时会把这个视图摘下来、确认完再建一个新的:订阅跟着视图走,不跟视图模型走。
        Subscribe(null);
        _topLevel?.RemoveHandler(KeyDownEvent, OnPreviewKeyDown);
        _topLevel = null;
        NamePopup.IsOpen = false;
        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (this.IsAttachedToVisualTree())
        {
            Subscribe(ViewModel);
        }
    }

    private void Subscribe(DocumentEditorDialogViewModel? viewModel)
    {
        if (_subscribed is not null)
        {
            _subscribed.FocusRequested -= OnFocusRequested;
            _subscribed.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _subscribed = viewModel;
        if (viewModel is not null)
        {
            viewModel.FocusRequested += OnFocusRequested;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 点「查找字段」打开查找框时直接把光标放进去。
        if (e.PropertyName == nameof(DocumentEditorDialogViewModel.IsSearching) && _subscribed is { IsSearching: true })
        {
            Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Loaded);
        }
    }

    private void OnFocusRequested(DocumentEditorRow row) =>
        // 新行要等列表布局完、容器实现出来才找得到它的字段名框。
        Dispatcher.UIThread.Post(() => FocusRow(row, retry: true), DispatcherPriority.Loaded);

    private void FocusRow(DocumentEditorRow row, bool retry)
    {
        if (FormList.ContainerFromItem(row) is not Control container)
        {
            if (retry)
            {
                FormList.ScrollIntoView(row);
                Dispatcher.UIThread.Post(() => FocusRow(row, retry: false), DispatcherPriority.Loaded);
            }
            return;
        }
        container.BringIntoView();
        TextBox? box = container.GetVisualDescendants().OfType<TextBox>()
                           .FirstOrDefault(static t => t.Name == "KeyBox" && t.IsEffectivelyVisible)
                       ?? container.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(static t => t.IsEffectivelyVisible && !t.IsReadOnly);
        if (box is null)
        {
            return;
        }
        box.Focus();
        // 占位名(newField)全选:一打字就替换掉。
        box.SelectAll();
    }

    // ── 键盘 ────────────────────────────────────────────────────────────────

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || e.Handled || !OwnsKeyboard())
        {
            return;
        }
        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (NamePopup.IsOpen && _suggestBox is { IsFocused: true } && HandleSuggestKey(e.Key))
        {
            e.Handled = true;
            return;
        }
        if (control && e.Key == Key.S)
        {
            if (vm.SaveCommand.CanExecute(null))
            {
                vm.SaveCommand.Execute(null);
            }
            e.Handled = true;
        }
        else if (control && e.Key == Key.Enter)
        {
            if (vm.IsFormMode)
            {
                if (FocusedRow() is { } row)
                {
                    vm.AddAfter(row);
                }
                else
                {
                    vm.AddFieldAtEnd();
                }
            }
            e.Handled = true;
        }
        else if (control && e.Key == Key.F && vm.IsFormMode)
        {
            if (vm.IsSearching)
            {
                Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Loaded);
            }
            vm.IsSearching = true;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsFocused)
        {
            vm.IsSearching = false;
            e.Handled = true;
        }
    }

    /// <summary>
    /// 键盘归不归这个对话框:它得看得见(宿主没切到别的标签),且焦点在它里面或哪儿都不在
    /// (对话框是模态的,焦点"不在任何控件上"时按键只可能是冲着它来的)。
    /// </summary>
    private bool OwnsKeyboard()
    {
        if (!IsEffectivelyVisible)
        {
            return false;
        }
        object? focused = _topLevel?.FocusManager?.GetFocusedElement();
        return focused is null or TopLevel || (focused is Visual visual && (ReferenceEquals(visual, this) || this.IsVisualAncestorOf(visual)));
    }

    /// <summary>焦点所在的那一行(焦点控件的数据上下文)。</summary>
    private DocumentEditorRow? FocusedRow()
    {
        for (Visual? visual = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
             visual is not null && !ReferenceEquals(visual, this);
             visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: DocumentEditorRow row })
            {
                return row;
            }
        }
        return null;
    }

    // ── 剪贴板 ──────────────────────────────────────────────────────────────

    private async void OnPasteClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }
        string? text = null;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                text = await clipboard.TryGetTextAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            // 剪贴板被别的进程占着之类:当作空,交给视图模型提示"剪贴板里没有文档"。
            text = null;
        }
        vm.ApplyPastedText(text);
    }

    // ── 拖动排序 ────────────────────────────────────────────────────────────

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: DocumentEditorRow row } grip || !row.CanDrag
            || !e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)
        {
            return;
        }
        _dragRow = row;
        _dropRow = null;
        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private void OnGripMoved(object? sender, PointerEventArgs e)
    {
        if (_dragRow is null)
        {
            return;
        }
        Point at = e.GetPosition(FormList);
        _dropRow = null;
        foreach (Control container in FormList.GetRealizedContainers())
        {
            if (container.DataContext is not DocumentEditorRow row || container.TranslatePoint(default, FormList) is not { } origin)
            {
                continue;
            }
            double top = origin.Y;
            double height = container.Bounds.Height;
            if (at.Y >= top && at.Y < top + height)
            {
                _dropRow = row;
                _dropAfter = at.Y > top + (height / 2);
                DropLine.Width = FormList.Bounds.Width;
                Canvas.SetTop(DropLine, (_dropAfter ? top + height : top) - 1);
                DropLine.IsVisible = !ReferenceEquals(row, _dragRow);
                break;
            }
        }
        if (_dropRow is null)
        {
            DropLine.IsVisible = false;
        }
    }

    private void OnGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragRow is { } row && _dropRow is { } target && !ReferenceEquals(row, target))
        {
            ViewModel?.Move(row, target, _dropAfter);
        }
        EndDrag();
        e.Pointer.Capture(null);
    }

    private void OnGripCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag();

    private void EndDrag()
    {
        _dragRow = null;
        _dropRow = null;
        DropLine.IsVisible = false;
    }

    // ── 字段名补全 ──────────────────────────────────────────────────────────

    private void OnKeyBoxGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (sender is not TextBox { DataContext: DocumentEditorRow row } box || ViewModel is not { } vm)
        {
            return;
        }
        _suggestBox = box;
        _suggestRow = row;
        // 只给新加的字段主动弹:点进一个已有字段的名字多半是想改个字母,弹一屏建议是打扰。
        if (row.OriginalValue is null)
        {
            _ = SuggestAfterSampleAsync(vm, box);
        }
    }

    private async Task SuggestAfterSampleAsync(DocumentEditorDialogViewModel vm, TextBox box)
    {
        await vm.EnsureSampleAsync(reportErrors: false).ConfigureAwait(true);
        if (ReferenceEquals(_suggestBox, box) && box.IsFocused)
        {
            UpdateSuggestions();
        }
    }

    private void OnKeyBoxTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is TextBox { IsFocused: true } box && ReferenceEquals(box, _suggestBox))
        {
            UpdateSuggestions();
        }
    }

    private void OnKeyBoxLostFocus(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_suggestBox is not { IsFocused: true })
            {
                NamePopup.IsOpen = false;
            }
        }, DispatcherPriority.Background);

    private void UpdateSuggestions()
    {
        if (ViewModel is not { } vm || _suggestBox is not { } box || _suggestRow is not { } row)
        {
            return;
        }
        IReadOnlyList<DocumentEditorSuggestion> suggestions = vm.SuggestNames(row, box.Text ?? "");
        if (suggestions.Count == 0)
        {
            NamePopup.IsOpen = false;
            return;
        }
        SuggestList.ItemsSource = suggestions;
        SuggestList.SelectedIndex = 0;
        NamePopup.PlacementTarget = box;
        NamePopup.IsOpen = true;
    }

    private bool HandleSuggestKey(Key key)
    {
        int count = SuggestList.ItemCount;
        switch (key)
        {
            case Key.Down:
                SuggestList.SelectedIndex = count == 0 ? -1 : (SuggestList.SelectedIndex + 1) % count;
                return true;
            case Key.Up:
                SuggestList.SelectedIndex = count == 0 ? -1 : (SuggestList.SelectedIndex - 1 + count) % count;
                return true;
            case Key.Enter or Key.Tab:
                AcceptSuggestion();
                return true;
            case Key.Escape:
                NamePopup.IsOpen = false;
                return true;
            default:
                return false;
        }
    }

    private void OnSuggestTapped(object? sender, TappedEventArgs e) => AcceptSuggestion();

    private void AcceptSuggestion()
    {
        if (ViewModel is { } vm && _suggestRow is { } row && SuggestList.SelectedItem is DocumentEditorSuggestion suggestion)
        {
            vm.AcceptSuggestion(row, suggestion);
            if (_suggestBox is { } box)
            {
                box.CaretIndex = box.Text?.Length ?? 0;
            }
        }
        NamePopup.IsOpen = false;
    }

    // ── 芯片行的「+ String」 ────────────────────────────────────────────────

    private void OnItemBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty && e.NewValue is true && sender is TextBox box)
        {
            Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Loaded);
        }
    }

    private void OnItemKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: DocumentEditorRow row })
        {
            return;
        }
        if (e.Key == Key.Enter)
        {
            row.CommitItemCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            row.CancelItemCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnItemLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: DocumentEditorRow row })
        {
            row.FinishItem();
        }
    }

    // ── 日历 ────────────────────────────────────────────────────────────────

    private void OnCalendarClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DocumentEditorRow row } button || !row.IsValueEditable)
        {
            return;
        }
        DateTime day = row.LocalDate ?? DateTime.Now;
        var calendar = new Calendar
        {
            SelectionMode = CalendarSelectionMode.SingleDate,
            SelectedDate = day.Date,
            DisplayDate = day.Date
        };
        var flyout = new Flyout { Content = calendar, Placement = PlacementMode.BottomEdgeAlignedLeft };
        calendar.SelectedDatesChanged += (_, _) =>
        {
            if (calendar.SelectedDate is { } picked)
            {
                row.PickDate(picked);
                flyout.Hide();
            }
        };
        flyout.ShowAt(button);
    }
}
