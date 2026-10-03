using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 带语法高亮、补全、诊断与整行标记的代码编辑框(查询编辑器、JSON 视图、管道阶段、验证规则、
/// 文档编辑器的 JSON 页共用)。
/// <para>
/// 里面是 AvaloniaEdit 的 <see cref="TextEditor" />(与 DockerPanel 同一条路:只在编译期引用,
/// 运行时回落宿主那一份)。外面只露出界面要的那几样:文本(双向)、语言、只读、诊断、整行标记、
/// 补全来源、光标位置。为什么不是 TextBox:TextBox 的整块文本共用一个前景色,按 token 着色、
/// 波浪线、补全弹层它都给不了 —— 而查询编辑器的价值恰恰就在这几样上。
/// </para>
/// </summary>
public sealed class CodeEditor : UserControl
{
    private static readonly Lock RegistrationGate = new();
    private static bool _registered;
    private readonly DecorationRenderer _decorations;
    private readonly MarkerMargin _markers;
    private readonly Popup _popup;
    private readonly CompletionPopupView _popupView;
    private readonly EventHandler<ResourcesChangedEventArgs> _onAppResourcesChanged;
    private CompletionSession? _session;
    private CancellationTokenSource? _completionRequest;
    private bool _syncing;

    /// <summary>文本(双向)。</summary>
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<CodeEditor, string?>(nameof(Text), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>语言。</summary>
    public static readonly StyledProperty<CodeLanguage> LanguageProperty =
        AvaloniaProperty.Register<CodeEditor, CodeLanguage>(nameof(Language), CodeLanguage.Shell);

    /// <summary>只读。</summary>
    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<CodeEditor, bool>(nameof(IsReadOnly));

    /// <summary>自动折行。</summary>
    public static readonly StyledProperty<bool> WordWrapProperty =
        AvaloniaProperty.Register<CodeEditor, bool>(nameof(WordWrap));

    /// <summary>显示行号。</summary>
    public static readonly StyledProperty<bool> ShowLineNumbersProperty =
        AvaloniaProperty.Register<CodeEditor, bool>(nameof(ShowLineNumbers), true);

    /// <summary>高亮当前行。</summary>
    public static readonly StyledProperty<bool> HighlightCurrentLineProperty =
        AvaloniaProperty.Register<CodeEditor, bool>(nameof(HighlightCurrentLine), true);

    /// <summary>诊断。</summary>
    public static readonly StyledProperty<IReadOnlyList<EditorDiagnostic>?> DiagnosticsProperty =
        AvaloniaProperty.Register<CodeEditor, IReadOnlyList<EditorDiagnostic>?>(nameof(Diagnostics));

    /// <summary>整行标记。</summary>
    public static readonly StyledProperty<IReadOnlyList<LineMark>?> LineMarksProperty =
        AvaloniaProperty.Register<CodeEditor, IReadOnlyList<LineMark>?>(nameof(LineMarks));

    /// <summary>补全来源;为 null 即不提供补全。</summary>
    public static readonly StyledProperty<Func<CompletionRequest, Task<CompletionSet?>>?> CompletionProviderProperty =
        AvaloniaProperty.Register<CodeEditor, Func<CompletionRequest, Task<CompletionSet?>>?>(nameof(CompletionProvider));

    /// <summary>
    /// 按内容定高(放在 StackPanel 这种给无限高的容器里时用:metadata、阶段体、命令预览)。
    /// 开着时不出竖向滚动条,高度 = 行数 × 行高 + 内边距,并受 <see cref="MaxFitLines" /> 限制。
    /// </summary>
    public static readonly StyledProperty<bool> FitContentProperty =
        AvaloniaProperty.Register<CodeEditor, bool>(nameof(FitContent));

    /// <summary>按内容定高时最多显示几行(超出则出滚动条)。</summary>
    public static readonly StyledProperty<int> MaxFitLinesProperty =
        AvaloniaProperty.Register<CodeEditor, int>(nameof(MaxFitLines), 30);

    /// <summary>光标位置(双向:视图模型可以读,也可以设来移动光标)。</summary>
    public static readonly StyledProperty<int> CaretOffsetProperty =
        AvaloniaProperty.Register<CodeEditor, int>(nameof(CaretOffset), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>光标所在行(1 起,只读)。</summary>
    public static readonly StyledProperty<int> CaretLineProperty =
        AvaloniaProperty.Register<CodeEditor, int>(nameof(CaretLine), 1, defaultBindingMode: Avalonia.Data.BindingMode.OneWayToSource);

    /// <summary>光标所在列(1 起,只读)。</summary>
    public static readonly StyledProperty<int> CaretColumnProperty =
        AvaloniaProperty.Register<CodeEditor, int>(nameof(CaretColumn), 1, defaultBindingMode: Avalonia.Data.BindingMode.OneWayToSource);

    /// <summary>构造。</summary>
    public CodeEditor()
    {
        Editor = new TextEditor
        {
            Background = Brushes.Transparent,
            BorderThickness = new(0),
            Padding = new(8, 6),
            ShowLineNumbers = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Editor.Options.IndentationSize = 2;
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.HighlightCurrentLine = true;
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        // 文档末尾之下不留空白可滚:小块编辑器(阶段体、metadata、筛选框)里那一截空白只会让内容跑出视野。
        Editor.Options.AllowScrollBelowDocument = false;
        Editor.TextArea.TextView.LinkTextForegroundBrush = Brushes.Transparent;

        _decorations = new DecorationRenderer(Brush, () => FontFamily, () => FontSize);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_decorations);
        _markers = new MarkerMargin(Brush);
        Editor.TextArea.LeftMargins.Insert(0, _markers);

        Editor.TextChanged += (_, _) =>
        {
            if (FitContent)
            {
                InvalidateMeasure();
            }
            if (_syncing)
            {
                return;
            }
            _syncing = true;
            SetCurrentValue(TextProperty, Editor.Text);
            _syncing = false;
        };
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            AvaloniaEdit.Document.TextLocation location = Editor.TextArea.Caret.Location;
            _syncing = true;
            SetCurrentValue(CaretOffsetProperty, Editor.CaretOffset);
            _syncing = false;
            SetCurrentValue(CaretLineProperty, location.Line);
            SetCurrentValue(CaretColumnProperty, location.Column);
            CaretMoved?.Invoke(this, EventArgs.Empty);
            if (_session is not null && (Editor.CaretOffset < _session.ReplaceOffset))
            {
                ClosePopup();
            }
        };
        Editor.TextArea.TextEntered += OnTextEntered;
        Editor.TextArea.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        Editor.TextArea.LostFocus += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_popupView?.IsKeyboardFocusWithin != true && !Editor.TextArea.IsKeyboardFocusWithin)
            {
                ClosePopup();
            }
        });

        // DataContext 显式置空:弹层挂在本控件下面,不设就继承宿主视图的视图模型(集合标签、检查器…),
        // 而弹层的编译绑定按 CompletionSession 转型 —— 每个编辑框一出现就是一串绑定错误。打开时才换成补全会话。
        _popupView = new CompletionPopupView { DataContext = null };
        _popupView.ItemClicked += item =>
        {
            if (_session is not null)
            {
                _session.Selected = item;
                Accept();
                _ = Editor.TextArea.Focus();
            }
        };
        _popup = new Popup
        {
            Child = _popupView,
            PlacementTarget = Editor.TextArea.TextView,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.BottomLeft,
            PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight,
            IsLightDismissEnabled = false
        };

        var host = new Panel();
        host.Children.Add(Editor);
        host.Children.Add(_popup);
        Content = host;
        Focusable = false;

        // 默认字体与前景按**样式优先级**绑:调用方在 AXAML 里写的 FontSize / FontFamily(本地值)要能盖过它。
        _ = Bind(FontFamilyProperty, this.GetResourceObservable("VelaUiMonoFont"), Avalonia.Data.BindingPriority.Style);
        _ = Bind(FontSizeProperty, this.GetResourceObservable("VelaFontSize12"), Avalonia.Data.BindingPriority.Style);
        _ = Bind(ForegroundProperty, this.GetResourceObservable("VelaTextPrimary"), Avalonia.Data.BindingPriority.Style);

        // 高亮配色是一次性取值(xshd 要 Color 不要 Brush),换肤时必须有人叫醒它 ——
        // 应用级资源变更覆盖全部三种换肤情形(与 DockerPanel 的 CodeEditor 同一个理由)。
        ActualThemeVariantChanged += (_, _) => ApplyLanguage();
        _onAppResourcesChanged = (_, _) => ApplyLanguage();
    }

    /// <summary>光标动了。</summary>
    public event EventHandler? CaretMoved;

    /// <summary>里面那个编辑器(查询编辑器要直接用它取选区、按语句定位)。</summary>
    public TextEditor Editor { get; }

    /// <inheritdoc cref="TextProperty" />
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <inheritdoc cref="LanguageProperty" />
    public CodeLanguage Language
    {
        get => GetValue(LanguageProperty);
        set => SetValue(LanguageProperty, value);
    }

    /// <inheritdoc cref="IsReadOnlyProperty" />
    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    /// <inheritdoc cref="WordWrapProperty" />
    public bool WordWrap
    {
        get => GetValue(WordWrapProperty);
        set => SetValue(WordWrapProperty, value);
    }

    /// <inheritdoc cref="ShowLineNumbersProperty" />
    public bool ShowLineNumbers
    {
        get => GetValue(ShowLineNumbersProperty);
        set => SetValue(ShowLineNumbersProperty, value);
    }

    /// <inheritdoc cref="HighlightCurrentLineProperty" />
    public bool HighlightCurrentLine
    {
        get => GetValue(HighlightCurrentLineProperty);
        set => SetValue(HighlightCurrentLineProperty, value);
    }

    /// <inheritdoc cref="DiagnosticsProperty" />
    public IReadOnlyList<EditorDiagnostic>? Diagnostics
    {
        get => GetValue(DiagnosticsProperty);
        set => SetValue(DiagnosticsProperty, value);
    }

    /// <inheritdoc cref="LineMarksProperty" />
    public IReadOnlyList<LineMark>? LineMarks
    {
        get => GetValue(LineMarksProperty);
        set => SetValue(LineMarksProperty, value);
    }

    /// <inheritdoc cref="CompletionProviderProperty" />
    public Func<CompletionRequest, Task<CompletionSet?>>? CompletionProvider
    {
        get => GetValue(CompletionProviderProperty);
        set => SetValue(CompletionProviderProperty, value);
    }

    /// <inheritdoc cref="FitContentProperty" />
    public bool FitContent
    {
        get => GetValue(FitContentProperty);
        set => SetValue(FitContentProperty, value);
    }

    /// <inheritdoc cref="MaxFitLinesProperty" />
    public int MaxFitLines
    {
        get => GetValue(MaxFitLinesProperty);
        set => SetValue(MaxFitLinesProperty, value);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        Size measured = base.MeasureOverride(availableSize);
        if (!FitContent)
        {
            return measured;
        }
        double lineHeight = Editor.TextArea.TextView.DefaultLineHeight;
        if (double.IsNaN(lineHeight) || lineHeight <= 0)
        {
            lineHeight = FontSize * 1.4;
        }
        int lines = Math.Clamp(Editor.Document.LineCount, 1, Math.Max(1, MaxFitLines));
        double height = (lines * lineHeight) + Editor.Padding.Top + Editor.Padding.Bottom + 2;
        return new Size(measured.Width, Math.Min(height, availableSize.Height));
    }

    /// <inheritdoc cref="CaretOffsetProperty" />
    public int CaretOffset
    {
        get => GetValue(CaretOffsetProperty);
        set => SetValue(CaretOffsetProperty, value);
    }

    /// <inheritdoc cref="CaretLineProperty" />
    public int CaretLine
    {
        get => GetValue(CaretLineProperty);
        set => SetValue(CaretLineProperty, value);
    }

    /// <inheritdoc cref="CaretColumnProperty" />
    public int CaretColumn
    {
        get => GetValue(CaretColumnProperty);
        set => SetValue(CaretColumnProperty, value);
    }

    /// <summary>选中的文本(没选中为空串)。</summary>
    public string SelectedText => Editor.SelectedText;

    /// <summary>在光标处插入文本(<c>|</c> 标出插入后光标落点)。</summary>
    public void InsertAtCaret(string text) => Replace(Editor.CaretOffset, 0, text);

    /// <summary>替换一段文本(<c>|</c> 标出替换后光标落点)。</summary>
    public void Replace(int offset, int length, string text)
    {
        int caret = text.IndexOf('|', StringComparison.Ordinal);
        string clean = caret >= 0 ? text.Remove(caret, 1) : text;
        offset = Math.Clamp(offset, 0, Editor.Document.TextLength);
        length = Math.Clamp(length, 0, Editor.Document.TextLength - offset);
        Editor.Document.Replace(offset, length, clean);
        Editor.CaretOffset = offset + (caret >= 0 ? caret : clean.Length);
    }

    /// <summary>
    /// 原样替换一段文本(不认 <c>|</c> 光标标记 —— <c>a || b</c>、正则里的竖线都按字面写进去)。
    /// </summary>
    /// <param name="offset">起点。</param>
    /// <param name="length">长度。</param>
    /// <param name="text">新文本。</param>
    /// <param name="caretInText">替换后光标落在新文本里的位置;为 null 则落在末尾。</param>
    public void ReplaceRaw(int offset, int length, string text, int? caretInText = null)
    {
        offset = Math.Clamp(offset, 0, Editor.Document.TextLength);
        length = Math.Clamp(length, 0, Editor.Document.TextLength - offset);
        Editor.Document.Replace(offset, length, text);
        Editor.CaretOffset = offset + Math.Clamp(caretInText ?? text.Length, 0, text.Length);
    }

    /// <summary>选中一段。</summary>
    public void Select(int offset, int length)
    {
        offset = Math.Clamp(offset, 0, Editor.Document.TextLength);
        Editor.Select(offset, Math.Clamp(length, 0, Editor.Document.TextLength - offset));
    }

    /// <summary>滚到某一行。</summary>
    public void ScrollToLine(int line) => Editor.ScrollToLine(Math.Max(1, line));

    /// <summary>把焦点给编辑区。</summary>
    public void FocusEditor() => Editor.TextArea.Focus();

    /// <summary>
    /// 应用光标所在(或光标所在行第一条)诊断的一键修复;没有可修的返回 false。
    /// </summary>
    public bool TryApplyFix()
    {
        if (Diagnostics is not { Count: > 0 } diagnostics)
        {
            return false;
        }
        int caret = Editor.CaretOffset;
        int line = Editor.Document.GetLineByOffset(caret).LineNumber;
        EditorDiagnostic? fix = diagnostics.FirstOrDefault(d => d.FixText is not null && caret >= d.Offset && caret <= d.Offset + d.Length)
                                ?? diagnostics.FirstOrDefault(d => d.FixText is not null
                                                                   && d.Offset <= Editor.Document.TextLength
                                                                   && Editor.Document.GetLineByOffset(d.Offset).LineNumber == line);
        if (fix is null)
        {
            return false;
        }
        ReplaceRaw(fix.Offset, fix.Length, fix.FixText!);
        return true;
    }

    /// <summary>主动弹出补全(Ctrl+Space 的同一条路)。</summary>
    public void RequestCompletion() => _ = RequestCompletionAsync(explicitRequest: true);

    /// <summary>
    /// 补全弹层开着。外面在编辑区上另挂按键处理的(筛选框的「Enter 即查找」)要先看它:
    /// 同一元素上的隧道处理器按**注册的逆序**被调用,后挂的会抢在这里的 Enter / Tab 接受补全之前。
    /// </summary>
    public bool IsCompletionOpen => _session is not null;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty && !_syncing)
        {
            _syncing = true;
            string incoming = Text ?? "";
            // 只在真不一样时才写:同一份文本写回去会把光标顶到开头,用户打一个字就跳一次。
            if (Editor.Text != incoming)
            {
                Editor.Text = incoming;
            }
            _syncing = false;
        }
        else if (change.Property == CaretOffsetProperty && !_syncing)
        {
            Editor.CaretOffset = Math.Clamp(CaretOffset, 0, Editor.Document.TextLength);
        }
        else if (change.Property == LanguageProperty || change.Property == ForegroundProperty
                 || change.Property == FontFamilyProperty || change.Property == FontSizeProperty)
        {
            ApplyLanguage();
        }
        else if (change.Property == IsReadOnlyProperty)
        {
            Editor.IsReadOnly = IsReadOnly;
        }
        else if (change.Property == WordWrapProperty)
        {
            Editor.WordWrap = WordWrap;
        }
        else if (change.Property == ShowLineNumbersProperty)
        {
            Editor.ShowLineNumbers = ShowLineNumbers;
        }
        else if (change.Property == FitContentProperty || change.Property == MaxFitLinesProperty)
        {
            Editor.VerticalScrollBarVisibility = FitContent && Editor.Document.LineCount <= MaxFitLines
                ? ScrollBarVisibility.Disabled
                : ScrollBarVisibility.Auto;
            InvalidateMeasure();
        }
        else if (change.Property == HighlightCurrentLineProperty)
        {
            Editor.Options.HighlightCurrentLine = HighlightCurrentLine;
        }
        else if (change.Property == DiagnosticsProperty || change.Property == LineMarksProperty)
        {
            _decorations.Diagnostics = Diagnostics ?? [];
            _decorations.LineMarks = LineMarks ?? [];
            _markers.Update(Editor.Document, _decorations.Diagnostics, _decorations.LineMarks);
            Editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Application.Current is { } app)
        {
            app.ResourcesChanged += _onAppResourcesChanged;
        }
        ApplyLanguage();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (Application.Current is { } app)
        {
            app.ResourcesChanged -= _onAppResourcesChanged;
        }
        ClosePopup();
        base.OnDetachedFromVisualTree(e);
    }

    // ── 补全 ────────────────────────────────────────────────────────────────

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (CompletionProvider is null || IsReadOnly || string.IsNullOrEmpty(e.Text))
        {
            return;
        }
        char typed = e.Text[^1];
        if (_session is not null)
        {
            if (IsWordChar(typed) || typed is '"' or '\'')
            {
                if (!_session.Filter(CurrentPrefix()))
                {
                    ClosePopup();
                }
                else
                {
                    _popupView.Reveal(_session.Selected);
                }
                return;
            }
            ClosePopup();
        }
        // 自动弹出的时机:敲下 $ / . / 引号 / 冒号后的空格,或在一个词的第一个字母。
        if (typed is '$' or '.' or '"' or '\'' || (IsWordChar(typed) && CurrentPrefix().Length == 1) || typed == ' ' && PreviousNonSpace() == ':')
        {
            _ = RequestCompletionAsync(explicitRequest: false);
        }
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (control && e.Key == Key.Space)
        {
            if (_session is not null)
            {
                _session.ToggleDetail();
            }
            else
            {
                _ = RequestCompletionAsync(explicitRequest: true);
            }
            e.Handled = true;
            return;
        }
        // Alt+⏎:应用光标所在诊断的一键修复(行尾提示里写着的那个)。
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Alt) && !IsReadOnly && TryApplyFix())
        {
            e.Handled = true;
            return;
        }
        if (_session is null)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Down:
                _session.Move(1);
                _popupView.Reveal(_session.Selected);
                e.Handled = true;
                break;
            case Key.Up:
                _session.Move(-1);
                _popupView.Reveal(_session.Selected);
                e.Handled = true;
                break;
            case Key.PageDown:
                _session.Move(8);
                _popupView.Reveal(_session.Selected);
                e.Handled = true;
                break;
            case Key.PageUp:
                _session.Move(-8);
                _popupView.Reveal(_session.Selected);
                e.Handled = true;
                break;
            case Key.Enter or Key.Tab:
                Accept();
                e.Handled = true;
                break;
            case Key.Escape:
                ClosePopup();
                e.Handled = true;
                break;
            case Key.Back:
                Dispatcher.UIThread.Post(() =>
                {
                    if (_session is not null && (Editor.CaretOffset <= _session.ReplaceOffset || !_session.Filter(CurrentPrefix())))
                    {
                        ClosePopup();
                    }
                });
                break;
        }
    }

    private async Task RequestCompletionAsync(bool explicitRequest)
    {
        if (CompletionProvider is not { } provider)
        {
            return;
        }
        _completionRequest?.Cancel();
        _completionRequest?.Dispose();
        var cts = new CancellationTokenSource();
        _completionRequest = cts;
        CompletionSet? set;
        try
        {
            set = await provider(new CompletionRequest(Editor.Text, Editor.CaretOffset, explicitRequest)).ConfigureAwait(true);
        }
        catch (Exception)
        {
            return;
        }
        if (cts.IsCancellationRequested || set is null || set.Items.Count == 0)
        {
            return;
        }
        int prefixLength = Math.Max(0, Editor.CaretOffset - set.ReplaceOffset);
        string prefix = Editor.Document.GetText(Math.Clamp(set.ReplaceOffset, 0, Editor.Document.TextLength),
            Math.Min(prefixLength, Editor.Document.TextLength - set.ReplaceOffset));
        var session = new CompletionSession(set) { InitialPrefix = prefix };
        if (prefix.Length > 0 && !session.Filter(prefix))
        {
            return;
        }
        _session = session;
        _popupView.DataContext = session;
        PlacePopup();
        _popup.IsOpen = true;
    }

    private void PlacePopup()
    {
        Rect caret = Editor.TextArea.Caret.CalculateCaretRectangle();
        Vector scroll = Editor.TextArea.TextView.ScrollOffset;
        _popup.PlacementRect = new Rect(caret.X - scroll.X, caret.Y - scroll.Y, Math.Max(1, caret.Width), caret.Height);
    }

    private void Accept()
    {
        if (_session?.Selected is not { } item)
        {
            ClosePopup();
            return;
        }
        int start = _session.ReplaceOffset;
        int length = Math.Max(0, Editor.CaretOffset - start);
        ClosePopup();
        Replace(start, length, item.InsertText ?? item.Label);
    }

    private void ClosePopup()
    {
        _completionRequest?.Cancel();
        _session = null;
        _popup.IsOpen = false;
    }

    private string CurrentPrefix()
    {
        if (_session is null)
        {
            int start = Editor.CaretOffset;
            while (start > 0 && IsWordChar(Editor.Document.GetCharAt(start - 1)))
            {
                start--;
            }
            return Editor.Document.GetText(start, Editor.CaretOffset - start);
        }
        int from = Math.Clamp(_session.ReplaceOffset, 0, Editor.Document.TextLength);
        return Editor.Document.GetText(from, Math.Max(0, Editor.CaretOffset - from));
    }

    private char PreviousNonSpace()
    {
        for (int i = Editor.CaretOffset - 1; i >= 0; i--)
        {
            char c = Editor.Document.GetCharAt(i);
            if (!char.IsWhiteSpace(c))
            {
                return c;
            }
        }
        return '\0';
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '.';

    // ── 配色 ────────────────────────────────────────────────────────────────

    private IBrush Brush(string token) => ThemeBrushes.Get(token, Brushes.Gray);

    private void ApplyLanguage()
    {
        Editor.FontFamily = FontFamily;
        Editor.FontSize = FontSize;
        if (Foreground is { } foreground)
        {
            Editor.Foreground = foreground;
        }
        Editor.TextArea.Caret.CaretBrush = Brush("VelaAccent");
        Editor.TextArea.SelectionBrush = ThemeBrushes.GetDim("VelaAccent", 0.25);
        Editor.TextArea.SelectionBorder = null;
        Editor.TextArea.SelectionForeground = null;
        Editor.LineNumbersForeground = Brush("VelaTextMuted");
        Editor.TextArea.TextView.CurrentLineBackground = ThemeBrushes.GetDim("VelaTextPrimary", 0.04);
        Editor.TextArea.TextView.CurrentLineBorder = null;
        Editor.SyntaxHighlighting = Definition(Language) is { } definition ? Recolor(definition) : null;
    }

    private IHighlightingDefinition Recolor(IHighlightingDefinition definition)
    {
        foreach (HighlightingColor color in definition.NamedHighlightingColors)
        {
            if (Role(color.Name) is { } token && Resource(token) is ISolidColorBrush brush)
            {
                color.Foreground = new SimpleHighlightingBrush(brush.Color);
            }
        }
        return definition;
    }

    /// <summary>xshd 里的角色名 → 宿主令牌名(与设计稿的语法着色逐条对应,见 Syntax/Mongosh.xshd 头注)。</summary>
    internal static string? Role(string name) => name switch
    {
        "Comment" or "Null" => "VelaTextTertiary",
        "String" => "VelaShellCyan",
        "FieldPath" => "VelaWarning",
        "Number" => "VelaShellGreen",
        "Keyword" => "VelaAccent",
        "Method" or "Bool" => "VelaShellYellow",
        "Operator" => "VelaShellMagenta",
        "Constructor" => "VelaInfo",
        "ObjectIdCtor" => "VelaShellBlue",
        "Key" => "VelaTextSecondary",
        _ => null
    };

    private object? Resource(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out object? value) ? value : null;

    /// <summary>
    /// 语法定义随插件自带(嵌入资源),注册名带 <c>Mongo.</c> 前缀:<see cref="HighlightingManager.Instance" />
    /// 是整个进程共享的,宿主与 DockerPanel 也往里注册 —— 不加前缀就会互相顶掉。
    /// </summary>
    private static IHighlightingDefinition? Definition(CodeLanguage language)
    {
        if (language == CodeLanguage.None)
        {
            return null;
        }
        EnsureRegistered();
        return HighlightingManager.Instance.GetDefinition(language == CodeLanguage.Json ? "Mongo.Json" : "Mongo.Mongosh");
    }

    private static void EnsureRegistered()
    {
        lock (RegistrationGate)
        {
            if (_registered)
            {
                return;
            }
            _registered = true;
            System.Reflection.Assembly assembly = typeof(CodeEditor).Assembly;
            foreach ((string resource, string[] extensions) in new[]
                     {
                         ("VelaShell.Plugin.Mongo.Syntax.Mongosh.xshd", new[] { ".mongodb.js" }),
                         ("VelaShell.Plugin.Mongo.Syntax.Json.xshd", new[] { ".ejson" })
                     })
            {
                try
                {
                    using Stream? stream = assembly.GetManifestResourceStream(resource);
                    if (stream is null)
                    {
                        continue;
                    }
                    using var reader = XmlReader.Create(stream);
                    IHighlightingDefinition definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                    HighlightingManager.Instance.RegisterHighlighting(definition.Name, extensions, definition);
                }
                catch (Exception)
                {
                    // 一份定义坏掉不该让编辑器打不开 —— 那一种退化成纯文本,其余照常。
                }
            }
        }
    }
}
