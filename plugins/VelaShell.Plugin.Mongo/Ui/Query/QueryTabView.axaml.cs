using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Shell;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 查询编辑器的视图。代码里做的是 AXAML 做不了或做起来绕的几件事:
/// 快捷键(要抢在编辑区之前)、编辑器门面(选区 / 替换)、code lens 叠加层(跟着可视行走)、
/// 连接、库与 maxTimeMS 的下拉菜单、结果网格的右键菜单。
/// <para>
/// 编辑器与下方结果区的分界只听用户拖的那条分隔条:切到「执行计划」页不再自动把编辑器收矮
/// (设计稿 14 画的是 230 高的编辑器)—— 结果页与执行计划页是同一块面板的两个页签,
/// 一切页高度就跳、切回来又被重置成默认值,用户刚拖好的高度就白拖了。
/// </para>
/// </summary>
public sealed partial class QueryTabView : UserControl, IQueryEditor
{
    private readonly QueryTabViewModel? _viewModel;
    private bool _lensQueued;

    /// <summary>用给定的视图模型初始化。</summary>
    internal QueryTabView(QueryTabViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        viewModel.Editor = this;
        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.LensChanged += QueueLenses;

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        ConnectionButton.Click += (_, _) => OpenConnectionMenu();
        DatabaseButton.Click += (_, _) => OpenDatabaseMenu();
        TimeoutButton.Click += (_, _) => OpenTimeoutMenu();
        ProblemsButton.Click += (_, _) => JumpToProblem();
        FieldList.DoubleTapped += (_, _) =>
        {
            if (FieldList.SelectedItem is SampledField field)
            {
                viewModel.InsertFieldCommand.Execute(field);
            }
        };
        SnippetList.Tapped += (_, _) =>
        {
            if (SnippetList.SelectedItem is QuerySnippet snippet)
            {
                viewModel.UseSnippetCommand.Execute(snippet);
                SnippetList.SelectedItem = null;
            }
        };
        HistoryList.Tapped += (_, _) =>
        {
            if (HistoryList.SelectedItem is QueryHistoryRow row)
            {
                viewModel.UseHistoryCommand.Execute(row);
                HistoryList.SelectedItem = null;
            }
        };
        AddHandler(ResultGrid.CellActivatedEvent, OnCellActivated);

        // 字段页 → 编辑器的拖放:按住字段拖进编辑区,在落点插入字段路径(与双击同一件事,只是位置由鼠标定)。
        FieldList.AddHandler(PointerPressedEvent, OnFieldPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        FieldList.AddHandler(PointerMovedEvent, OnFieldMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        DragDrop.SetAllowDrop(EditorHost, true);
        EditorHost.AddHandler(DragDrop.DragOverEvent, OnEditorDragOver, RoutingStrategies.Tunnel);
        EditorHost.AddHandler(DragDrop.DropEvent, OnEditorDrop, RoutingStrategies.Tunnel);

        // 设计稿 03 的行号槽更宽、行号与代码之间隔着 20px;共享的 CodeEditor 是紧凑排法(网格的 JSON 视图、
        // 管道阶段框都用它)—— 只在查询编辑器这一个实例上前后各垫一条空白边距,不动共享控件。
        Editor.Editor.TextArea.LeftMargins.Insert(0, new Border { Width = 8 });
        Editor.Editor.TextArea.LeftMargins.Add(new Border { Width = 12 });

        TextView view = Editor.Editor.TextArea.TextView;
        view.VisualLinesChanged += (_, _) => QueueLenses();
        view.ScrollOffsetChanged += (_, _) => QueueLenses();
        Editor.CaretMoved += (_, _) => viewModel.OnCaretSettled();
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public QueryTabView()
    {
        InitializeComponent();
    }

    /// <summary>里面那块代码编辑器(测试与截图要直接摆弄光标、弹补全)。</summary>
    internal CodeEditor CodeEditor => Editor;

    // ── IQueryEditor ────────────────────────────────────────────────────────

    /// <inheritdoc />
    (int Offset, int Length) IQueryEditor.Selection => (Editor.Editor.SelectionStart, Editor.Editor.SelectionLength);

    /// <inheritdoc />
    void IQueryEditor.Replace(int offset, int length, string text, bool literal)
    {
        if (!literal)
        {
            Editor.Replace(offset, length, text);
            return;
        }
        TextDocument document = Editor.Editor.Document;
        offset = Math.Clamp(offset, 0, document.TextLength);
        length = Math.Clamp(length, 0, document.TextLength - offset);
        int caret = Editor.Editor.CaretOffset;
        bool whole = offset == 0 && length == document.TextLength;
        document.Replace(offset, length, text);
        // 整段替换(格式化、回填历史)时光标留在原处附近,而不是被顶到末尾;局部插入落在插入内容之后。
        Editor.Editor.CaretOffset = whole
            ? Math.Clamp(caret, 0, document.TextLength)
            : Math.Clamp(offset + text.Length, 0, document.TextLength);
    }

    /// <inheritdoc />
    void IQueryEditor.Focus() => Editor.FocusEditor();

    // ── 快捷键 ──────────────────────────────────────────────────────────────

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }
        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.F5:
                vm.RunCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter when control:
                vm.RunCurrentCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F6:
                vm.ExplainCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F when alt && shift:
                vm.FormatCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter when alt:
                vm.ApplyFixCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.S when control:
                vm.SaveCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape when vm.IsRunning:
                vm.StopCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    // ── 菜单 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 连接下拉:插件里全部已保存的连接。当前这条打勾;其余前面一个状态点(绿 = 连着、灰 = 没连、红 = 上次没连上),
    /// 没连着的名字后面一行小字写明 —— 选它会先连上再切过去。
    /// </summary>
    private void OpenConnectionMenu()
    {
        if (_viewModel is not { } vm || vm.ConnectionChoices is not { Count: > 0 } choices)
        {
            return;
        }
        var items = choices.Select(entry =>
        {
            bool current = vm.IsCurrentConnection(entry);
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(new TextBlock
            {
                Text = entry.Name,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = Resource<FontFamily>("VelaUiMonoFont") ?? FontFamily.Default
            });
            string? state = entry.State switch
            {
                ConnectionState.Connected => null,
                ConnectionState.Failed => vm.Loc["Query_ConnFailed"],
                _ => vm.Loc["Query_ConnNotConnected"]
            };
            if (state is not null)
            {
                header.Children.Add(new TextBlock
                {
                    Text = state,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = Resource<FontFamily>("VelaUiFont") ?? FontFamily.Default,
                    FontSize = Resource<double>("VelaFontSize10") is > 0 and var size ? size : 10,
                    Foreground = ThemeBrushes.Get("VelaTextMuted", Brushes.Gray)
                });
            }
            Control icon = current
                ? new Glyph { Key = "Mongo.check", Size = 12, Brush = ThemeBrushes.Get("VelaAccent", Brushes.Gray) }
                : new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = ThemeBrushes.Get(entry.State switch
                    {
                        ConnectionState.Connected => "VelaStatusConnected",
                        ConnectionState.Failed => "VelaError",
                        _ => "VelaTextMuted"
                    }, Brushes.Gray)
                };
            return (Control)new MenuItem
            {
                Header = header,
                Icon = icon,
                Command = vm.SwitchConnectionCommand,
                CommandParameter = entry
            };
        }).ToList();
        new ContextMenu { ItemsSource = items }.Open(ConnectionButton);
    }

    private void OpenDatabaseMenu()
    {
        if (_viewModel is not { } vm)
        {
            return;
        }
        var items = vm.Databases.Order(StringComparer.Ordinal).Select(db => (Control)new MenuItem
        {
            Header = db,
            FontFamily = Resource<FontFamily>("VelaUiMonoFont") ?? FontFamily.Default,
            Icon = new Glyph
            {
                Key = db == vm.Database ? "Mongo.check" : "Mongo.database",
                Size = 12,
                Brush = ThemeBrushes.Get(db == vm.Database ? "VelaAccent" : "VelaWarning", Brushes.Gray)
            },
            Command = vm.SelectDatabaseCommand,
            CommandParameter = db
        }).ToList();
        new ContextMenu { ItemsSource = items }.Open(DatabaseButton);
    }

    private void OpenTimeoutMenu()
    {
        if (_viewModel is not { } vm)
        {
            return;
        }
        int[] presets = [5_000, 15_000, 30_000, 60_000, 120_000, 300_000, 0];
        IEnumerable<int> values = presets.Contains(vm.MaxTimeMs) ? presets : [vm.MaxTimeMs, .. presets];
        var items = values.Select(ms => (Control)new MenuItem
        {
            Header = "maxTimeMS " + QueryTabViewModel.FormatDuration(ms, vm.Loc),
            Icon = new Glyph
            {
                Key = ms == vm.MaxTimeMs ? "Mongo.check" : "Mongo.timer",
                Size = 12,
                Brush = ThemeBrushes.Get(ms == vm.MaxTimeMs ? "VelaAccent" : "VelaTextTertiary", Brushes.Gray)
            },
            Command = vm.SetMaxTimeCommand,
            CommandParameter = ms
        }).ToList();
        new ContextMenu { ItemsSource = items }.Open(TimeoutButton);
    }

    /// <summary>网格:右键弹菜单(复制值 / 文档 / 筛选条件),双击或 Ctrl+C 复制单元格(行号列复制整份文档)。</summary>
    private void OnCellActivated(object? sender, ResultCellEventArgs e)
    {
        if (_viewModel is not { } vm || vm.SelectedPane is not QueryResultSet set || e.DocumentIndex < 0 || e.DocumentIndex >= set.Documents.Count)
        {
            return;
        }
        // 网格可能已经钻进了某个数组 / 对象:值与所属文档都以事件带来的为准,路径是从文档根算起的。
        BsonDocument document = e.Document ?? set.Documents[e.DocumentIndex];
        BsonValue? value = e.Value;
        string documentText = BsonText.Pretty(document, vm.Workspace.Connection.Settings.Ejson);
        if (e.Activated)
        {
            _ = vm.Workspace.CopyAsync(value is null ? documentText : CopyText(value));
            vm.Workspace.Toast(new ToastRequest { Title = vm.Loc["Common_Copied"], Kind = ToastKind.Success, Duration = TimeSpan.FromSeconds(2) });
            return;
        }
        var items = new List<Control>();
        if (value is BsonDocument or BsonArray && e.Source is ResultGrid grid && e.GridRow >= 0 && e.GridColumn >= 0)
        {
            (int row, int column) = (e.GridRow, e.GridColumn);
            items.Add(Item(vm.Loc["Cw_MenuDrill"], "Mongo.table-2", () =>
            {
                _ = grid.DrillInto(row, column);
                return Task.CompletedTask;
            }));
            items.Add(new Separator());
        }
        if (value is not null && e.Column is { } path)
        {
            items.Add(Item(vm.Loc["Query_MenuCopyValue"], "Mongo.copy", () => vm.Workspace.CopyAsync(CopyText(value))));
            items.Add(Item(vm.Loc["Query_MenuCopyFilter"], "Mongo.funnel",
                () => vm.Workspace.CopyAsync(BsonText.Literal(new BsonDocument(GridDrill.FilterPath(path), value)))));
        }
        items.Add(Item(vm.Loc["Query_MenuCopyDocument"], "Mongo.braces", () => vm.Workspace.CopyAsync(documentText)));
        new ContextMenu { ItemsSource = items }.Open(e.Source as Control ?? this);
        e.Handled = true;
    }

    private static string CopyText(BsonValue value) => value.IsString ? value.AsString : BsonText.Literal(value);

    private static MenuItem Item(string header, string icon, Func<Task> action) => new()
    {
        Header = header,
        Icon = new Glyph { Key = icon, Size = 13, Brush = ThemeBrushes.Get("VelaTextTertiary", Brushes.Gray) },
        Command = new AsyncCommand(action)
    };

    private void JumpToProblem()
    {
        if (_viewModel?.Diagnostics.FirstOrDefault() is not { } diagnostic)
        {
            return;
        }
        Editor.Select(diagnostic.Offset, diagnostic.Length);
        int line = Editor.Editor.Document.GetLineByOffset(Math.Clamp(diagnostic.Offset, 0, Editor.Editor.Document.TextLength)).LineNumber;
        Editor.ScrollToLine(line);
        Editor.FocusEditor();
    }

    // ── 字段拖进编辑器 ──────────────────────────────────────────────────────

    private PointerPressedEventArgs? _fieldPress;
    private Point _fieldPressPoint;

    private void OnFieldPressed(object? sender, PointerPressedEventArgs e)
    {
        _fieldPress = e.GetCurrentPoint(FieldList).Properties.IsLeftButtonPressed ? e : null;
        _fieldPressPoint = e.GetPosition(FieldList);
    }

    private async void OnFieldMoved(object? sender, PointerEventArgs e)
    {
        if (_fieldPress is not { } press || !e.GetCurrentPoint(FieldList).Properties.IsLeftButtonPressed)
        {
            return;
        }
        Point now = e.GetPosition(FieldList);
        if (Math.Abs(now.X - _fieldPressPoint.X) + Math.Abs(now.Y - _fieldPressPoint.Y) < 6
            || (press.Source as Control)?.DataContext is not SampledField field)
        {
            return;
        }
        _fieldPress = null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(field.Path));
        try
        {
            _ = await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy);
        }
        catch (InvalidOperationException)
        {
            // 平台不支持拖放(headless)时退化成什么都不做 —— 双击插入照样可用。
        }
    }

    private void OnEditorDragOver(object? sender, DragEventArgs e)
    {
        bool text = e.DataTransfer.TryGetText() is { Length: > 0 };
        e.DragEffects = text ? DragDropEffects.Copy : DragDropEffects.None;
        if (text && Editor.Editor.GetPositionFromPoint(e.GetPosition(Editor.Editor)) is { } position)
        {
            // 拖动时光标跟着落点走,松手前就看得出会插在哪。
            Editor.Editor.TextArea.Caret.Position = position;
        }
        e.Handled = true;
    }

    private void OnEditorDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetText() is not { Length: > 0 } text)
        {
            return;
        }
        TextDocument document = Editor.Editor.Document;
        int offset = Editor.Editor.GetPositionFromPoint(e.GetPosition(Editor.Editor)) is { } position
            ? document.GetOffset(position.Location)
            : Editor.Editor.CaretOffset;
        document.Insert(Math.Clamp(offset, 0, document.TextLength), text);
        Editor.Editor.CaretOffset = Math.Clamp(offset + text.Length, 0, document.TextLength);
        Editor.FocusEditor();
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueryTabViewModel.Statements))
        {
            QueueLenses();
        }
    }

    // ── code lens ───────────────────────────────────────────────────────────

    private void QueueLenses()
    {
        if (_lensQueued)
        {
            return;
        }
        _lensQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _lensQueued = false;
            UpdateLenses();
        }, DispatcherPriority.Render);
    }

    /// <summary>
    /// 每条查询语句上方一行「▷ 运行 · 执行计划 · 复制为 C# · 上次 38 ms · 100 条」。
    /// <para>
    /// AvaloniaEdit 没有"在两行之间插一行装饰"的机制(插进文档的行会变成用户的文本,改行高会把光标和选区一起撑高),
    /// 所以 code lens 是编辑器上方的一层叠加:语句上一行是空行时画在那一行里(脚本里语句之间本来就隔着空行);
    /// 否则画在语句首行的行尾。按钮可点,随滚动与改动重排。
    /// </para>
    /// </summary>
    private void UpdateLenses()
    {
        LensLayer.Children.Clear();
        if (_viewModel is not { } vm)
        {
            return;
        }
        TextView view = Editor.Editor.TextArea.TextView;
        TextDocument document = Editor.Editor.Document;
        if (!view.VisualLinesValid || document.TextLength == 0)
        {
            return;
        }
        foreach (ShellStatement statement in vm.Statements)
        {
            if (statement.Offset >= document.TextLength || !statement.Text.StartsWith("db", StringComparison.Ordinal))
            {
                continue;
            }
            DocumentLine first = document.GetLineByOffset(statement.Offset);
            bool above = first.LineNumber > 1 && document.GetText(document.GetLineByNumber(first.LineNumber - 1)).Trim().Length == 0;
            int hostLine = above ? first.LineNumber - 1 : first.LineNumber;
            if (view.GetVisualLine(hostLine) is not { } visual)
            {
                continue;
            }
            double x;
            if (above)
            {
                VisualLine statementLine = view.GetVisualLine(first.LineNumber) ?? visual;
                int column = statementLine.GetVisualColumn(statement.Offset - first.Offset);
                x = statementLine.GetVisualPosition(column, VisualYPosition.LineTop).X;
            }
            else
            {
                x = visual.GetTextLineVisualXPosition(visual.TextLines[^1], visual.VisualLengthWithEndOfLineMarker) + 24;
            }
            double y = visual.VisualTop - view.ScrollOffset.Y;
            x -= view.ScrollOffset.X;
            if (view.TranslatePoint(new Point(x, y), LensLayer) is not { } origin || origin.Y < -visual.Height || origin.Y > LensLayer.Bounds.Height)
            {
                continue;
            }
            StackPanel lens = BuildLens(vm, statement);
            Canvas.SetLeft(lens, origin.X);
            Canvas.SetTop(lens, origin.Y + Math.Max(0, (visual.Height - 16) / 2));
            LensLayer.Children.Add(lens);
        }
    }

    private StackPanel BuildLens(QueryTabViewModel vm, ShellStatement statement)
    {
        Loc loc = vm.Loc;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Height = 16 };
        (bool explainable, bool pipeline) = vm.LensShape(statement);
        panel.Children.Add(LensButton("Mongo.play", loc["Query_LensRun"], vm.LensRunCommand, statement));
        if (explainable)
        {
            panel.Children.Add(LensButton("Mongo.gauge", loc["Query_LensExplain"], vm.LensExplainCommand, statement));
        }
        if (pipeline)
        {
            panel.Children.Add(LensButton("Mongo.workflow", loc["Query_LensPipeline"], vm.LensPipelineCommand, statement));
        }
        panel.Children.Add(LensButton("Mongo.code-xml", loc["Query_LensCopyCs"], vm.LensCopyCodeCommand, statement));
        if (vm.LensFor(statement) is { } record)
        {
            string ms = loc.Format("Common_Ms", record.ElapsedMs);
            string text = !record.Ok ? loc.Format("Query_LensFailed", ms)
                : record.Count is { } count ? loc.Format("Query_LensLast", ms, QueryTabViewModel.Count(count))
                : loc.Format("Query_LensLastPlain", ms);
            panel.Children.Add(LensLabel(record.Ok ? "Mongo.circle-check" : "Mongo.circle-x", text, record.Ok ? "VelaTextMuted" : "VelaError"));
        }
        return panel;
    }

    private Button LensButton(string icon, string text, System.Windows.Input.ICommand command, ShellStatement statement)
    {
        var button = new Button
        {
            Theme = Resource<Avalonia.Styling.ControlTheme>("MongoToolButton"),
            Height = 16,
            Padding = new Thickness(2, 0),
            Command = command,
            CommandParameter = statement,
            Content = LensLabel(icon, text, "VelaTextMuted"),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        return button;
    }

    private StackPanel LensLabel(string icon, string text, string token)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new Glyph { Key = icon, Size = 9, Brush = ThemeBrushes.Get(token, Brushes.Gray) });
        label.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = Resource<FontFamily>("VelaUiFont") ?? FontFamily.Default,
            FontSize = Resource<double?>("VelaFontSize9") ?? 9,
            Foreground = ThemeBrushes.Get(token, Brushes.Gray),
            VerticalAlignment = VerticalAlignment.Center
        });
        return label;
    }

    private T? Resource<T>(string key) => this.TryFindResource(key, out object? value) && value is T typed ? typed : default;
}
