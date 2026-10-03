using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using VelaShell.Plugin.Mongo.Shell;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 查询编辑器里那块 <c>CodeEditor</c> 的门面:视图实现它,视图模型借它取选区、替换文本、弹补全。
/// 单测里没有视图,视图模型照常工作(选区为空、替换直接改 <see cref="QueryTabViewModel.Text" />)。
/// </summary>
internal interface IQueryEditor
{
    /// <summary>选区(没选中时长度为 0)。</summary>
    (int Offset, int Length) Selection { get; }

    /// <summary>
    /// 替换一段文本(进撤销栈)。<paramref name="literal" /> 为 <see langword="false" /> 时第一个 <c>|</c> 是替换后的光标落点;
    /// 为 <see langword="true" /> 时原样插入 —— 格式化、回填历史时文本里的 <c>||</c>、正则的 <c>a|b</c> 不能被当成光标标记吃掉。
    /// </summary>
    void Replace(int offset, int length, string text, bool literal);

    /// <summary>把焦点给编辑区。</summary>
    void Focus();
}

/// <summary>
/// 查询编辑器(设计稿 03 / 14)。
/// <para>
/// 上半是 mongosh 脚本编辑器(补全、诊断、code lens)与右侧字段 / 片段 / 历史面板;下半是结果区:
/// 每条产出文档的语句一个「结果 N」页签(网格 / 树 / JSON),外加「执行计划」与「消息」。
/// 一次运行按语句**逐条**执行、逐条计时,每条都打上同一个 <c>comment</c> 标记 ——「停止」靠它在服务器上 killOp。
/// </para>
/// </summary>
internal sealed partial class QueryTabViewModel : WorkspaceTab
{
    private readonly int _number;
    private readonly ShellExecutor _executor;
    private string _text;
    private string _baseline;
    private string _database;
    private bool _runOnLoad;
    private int _caretOffset;
    private int _caretLine = 1;
    private int _caretColumn = 1;
    private int _maxTimeMs;
    private string _helperTab = "fields";
    private string _resultView = "grid";
    private QueryPane? _selectedPane;
    private string? _savedName;
    private bool _loaded;

    /// <summary>构造。</summary>
    /// <param name="workspace">外壳服务。</param>
    /// <param name="database">起始库。</param>
    /// <param name="text">起始文本;没有为空编辑器。</param>
    /// <param name="run">加载后立即运行(别的分区「在新查询中打开 / 查看执行计划」用)。</param>
    /// <param name="number">编号(标题「查询 N」)。</param>
    public QueryTabViewModel(IMongoWorkspace workspace, string database, string? text, bool run, int number)
        : base(workspace)
    {
        _number = number;
        _database = database;
        _text = text ?? "";
        _baseline = _text;
        _runOnLoad = run;
        _maxTimeMs = workspace.Connection.Settings.MaxTimeMs;
        Title = workspace.Loc.Format("Query_Title", number);
        Scope = "@" + database;
        _executor = new ShellExecutor(workspace.Connection, new QueryGuard(workspace), workspace.Loc);

        Messages = new MessagesPane(Loc);
        Explain = new ExplainPane(this);
        Panes = [Explain, Messages];
        SelectPane(Messages);

        RunCommand = new AsyncCommand(() => RunAsync(all: true), () => !IsRunning);
        RunCurrentCommand = new AsyncCommand(() => RunAsync(all: false), () => !IsRunning);
        // 停止不随运行状态置灰(设计稿里它常亮,只有图标在运行时变红);没在跑时点它什么也不做。
        StopCommand = new AsyncCommand(StopAsync);
        History.CollectionChanged += (_, _) => RaisePropertyChanged(nameof(HistoryEmpty));
        ExplainCommand = new AsyncCommand(() => ExplainCurrentAsync(null), () => !IsRunning);
        FormatCommand = new RelayCommand(Format);
        SaveCommand = new AsyncCommand(SaveAsync);
        HistoryCommand = new RelayCommand(() => HelperTab = "history");
        ExportCodeCommand = new RelayCommand(() => ExportCode(null, CodeTarget.CSharp));
        SelectPaneCommand = new RelayCommand<QueryPane>(SelectPane);
        ExportResultCommand = new AsyncCommand(ExportSelectedAsync, () => _selectedPane is QueryResultSet or ExplainPane { HasPlan: true });
        CopyResultCommand = new AsyncCommand(CopySelectedAsync, () => _selectedPane is not null);
        TogglePinCommand = new RelayCommand(() => (_selectedPane as QueryResultSet)?.TogglePinCommand.Execute(null), () => _selectedPane is QueryResultSet);
        SelectDatabaseCommand = new RelayCommand<string>(db => Database = db);
        SetMaxTimeCommand = new RelayCommand<int>(ms => MaxTimeMs = ms);
        ApplyFixCommand = new RelayCommand(() => ApplyQuickFix());
        InsertFieldCommand = new RelayCommand<SampledField>(InsertField);
        UseSnippetCommand = new RelayCommand<QuerySnippet>(UseSnippet);
        UseHistoryCommand = new RelayCommand<QueryHistoryRow>(UseHistory);
        LensRunCommand = new AsyncCommand<ShellStatement>(s => RunStatementsAsync([s]));
        LensExplainCommand = new AsyncCommand<ShellStatement>(s => ExplainCurrentAsync(s));
        LensCopyCodeCommand = new AsyncCommand<ShellStatement>(CopyAsCSharpAsync);
        LensPipelineCommand = new RelayCommand<ShellStatement>(OpenInPipelineBuilder);

        _diagnosticTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _diagnosticTimer.Tick += (_, _) =>
        {
            _diagnosticTimer.Stop();
            Analyze();
        };
        _helperTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _helperTimer.Tick += (_, _) =>
        {
            _helperTimer.Stop();
            _ = RefreshHelperAsync();
        };
        Analyze();
        UpdateStatus();
    }

    /// <summary>当前库(工具栏右侧的下拉;脚本里的 <c>use</c> 也会改它)。</summary>
    public string Database
    {
        get => _database;
        set
        {
            if (!string.IsNullOrEmpty(value) && SetProperty(ref _database, value))
            {
                Scope = "@" + value;
                if (!Databases.Contains(value))
                {
                    Databases.Add(value);
                }
                ScheduleHelper();
            }
        }
    }

    /// <summary>脚本全文。</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value ?? ""))
            {
                IsModified = _text != _baseline;
                _diagnosticTimer.Stop();
                _diagnosticTimer.Start();
                UpdateStatus();
            }
        }
    }

    /// <inheritdoc />
    public override TabKind Kind => TabKind.Query;

    /// <inheritdoc />
    public override string Key => $"query:{_number}";

    /// <inheritdoc />
    public override string IconKey => "Mongo.file-code";

    /// <inheritdoc />
    public override string IconToken => "VelaAccent";

    /// <summary>编辑器门面(视图挂上来;单测里为 <see langword="null" />)。</summary>
    internal IQueryEditor? Editor { get; set; }

    /// <summary>光标位置(与编辑器双向绑定)。</summary>
    public int CaretOffset
    {
        get => _caretOffset;
        set
        {
            if (SetProperty(ref _caretOffset, value))
            {
                ScheduleHelper();
            }
        }
    }

    /// <summary>光标行(1 起)。</summary>
    public int CaretLine
    {
        get => _caretLine;
        set
        {
            if (SetProperty(ref _caretLine, value))
            {
                RaisePropertyChanged(nameof(CaretText));
            }
        }
    }

    /// <summary>光标列(1 起)。</summary>
    public int CaretColumn
    {
        get => _caretColumn;
        set
        {
            if (SetProperty(ref _caretColumn, value))
            {
                RaisePropertyChanged(nameof(CaretText));
            }
        }
    }

    /// <summary>状态条右侧的 <c>Ln 12, Col 8</c>。</summary>
    public string CaretText => string.Create(CultureInfo.InvariantCulture, $"Ln {_caretLine}, Col {_caretColumn}");

    /// <summary>已知的库(下拉)。</summary>
    public ObservableCollection<string> Databases { get; } = [];

    /// <summary>默认 maxTimeMS(工具栏芯片;取连接设置,可临时改)。</summary>
    public int MaxTimeMs
    {
        get => _maxTimeMs;
        set
        {
            if (SetProperty(ref _maxTimeMs, Math.Max(0, value)))
            {
                RaisePropertyChanged(nameof(MaxTimeText));
            }
        }
    }

    /// <summary>芯片上的字(<c>maxTimeMS 30s</c>)。</summary>
    public string MaxTimeText => "maxTimeMS " + FormatDuration(_maxTimeMs, Loc);

    /// <summary>右侧面板页签(<c>fields</c> / <c>snippets</c> / <c>history</c>)。</summary>
    public string HelperTab
    {
        get => _helperTab;
        set
        {
            if (SetProperty(ref _helperTab, value))
            {
                RaisePropertyChanged(nameof(IsFieldsTab));
                RaisePropertyChanged(nameof(IsSnippetsTab));
                RaisePropertyChanged(nameof(IsHistoryTab));
                if (value == "history")
                {
                    _ = LoadHistoryAsync();
                }
            }
        }
    }

    /// <summary>字段页。</summary>
    public bool IsFieldsTab => _helperTab == "fields";

    /// <summary>片段页。</summary>
    public bool IsSnippetsTab => _helperTab == "snippets";

    /// <summary>历史页。</summary>
    public bool IsHistoryTab => _helperTab == "history";

    /// <summary>结果的看法(<c>grid</c> / <c>tree</c> / <c>json</c>),所有结果页签共用。</summary>
    public string ResultView
    {
        get => _resultView;
        set
        {
            if (SetProperty(ref _resultView, value))
            {
                foreach (QueryResultSet set in Panes.OfType<QueryResultSet>())
                {
                    set.ViewMode = value;
                }
            }
        }
    }

    /// <summary>结果区页签(结果在前,执行计划与消息固定在最后)。</summary>
    public ObservableCollection<QueryPane> Panes { get; }

    /// <summary>执行计划页签。</summary>
    public ExplainPane Explain { get; }

    /// <summary>消息页签。</summary>
    public MessagesPane Messages { get; }

    /// <summary>选中的页签。</summary>
    public QueryPane? SelectedPane
    {
        get => _selectedPane;
        private set
        {
            if (SetProperty(ref _selectedPane, value))
            {
                RaisePropertyChanged(nameof(IsResultSelected));
                RaisePropertyChanged(nameof(IsExplainSelected));
                ExportResultCommand?.RaiseCanExecuteChanged();
                CopyResultCommand?.RaiseCanExecuteChanged();
                TogglePinCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>选中的是一份结果(网格 / 树 / JSON 切换只对结果有意义)。</summary>
    public bool IsResultSelected => _selectedPane is QueryResultSet;

    /// <summary>选中的是执行计划。</summary>
    public bool IsExplainSelected => _selectedPane is ExplainPane;

    /// <summary>运行全部(F5;有选区时只运行选区)。</summary>
    public AsyncCommand RunCommand { get; }

    /// <summary>运行光标所在语句(Ctrl+↵)。</summary>
    public AsyncCommand RunCurrentCommand { get; }

    /// <summary>停止(取消等待 + 服务器端 killOp)。</summary>
    public AsyncCommand StopCommand { get; }

    /// <summary>执行计划(F6)。</summary>
    public AsyncCommand ExplainCommand { get; }

    /// <summary>格式化(Alt+Shift+F)。</summary>
    public RelayCommand FormatCommand { get; }

    /// <summary>保存。</summary>
    public AsyncCommand SaveCommand { get; }

    /// <summary>历史(切到右侧历史页)。</summary>
    public RelayCommand HistoryCommand { get; }

    /// <summary>导出为代码。</summary>
    public RelayCommand ExportCodeCommand { get; }

    /// <summary>选一个结果页签。</summary>
    public RelayCommand<QueryPane> SelectPaneCommand { get; }

    /// <summary>导出选中的结果。</summary>
    public AsyncCommand ExportResultCommand { get; }

    /// <summary>复制选中的结果。</summary>
    public AsyncCommand CopyResultCommand { get; }

    /// <summary>固定 / 取消固定选中的结果。</summary>
    public RelayCommand TogglePinCommand { get; }

    /// <summary>切换当前库。</summary>
    public RelayCommand<string> SelectDatabaseCommand { get; }

    /// <summary>改默认 maxTimeMS。</summary>
    public RelayCommand<int> SetMaxTimeCommand { get; }

    /// <summary>快捷修复(Alt+↵)。</summary>
    public RelayCommand ApplyFixCommand { get; }

    /// <summary>插入字段路径(字段页双击)。</summary>
    public RelayCommand<SampledField> InsertFieldCommand { get; }

    /// <summary>用一个片段 / 保存的查询。</summary>
    public RelayCommand<QuerySnippet> UseSnippetCommand { get; }

    /// <summary>回填一条历史。</summary>
    public RelayCommand<QueryHistoryRow> UseHistoryCommand { get; }

    /// <summary>code lens「运行」。</summary>
    public AsyncCommand<ShellStatement> LensRunCommand { get; }

    /// <summary>code lens「执行计划」。</summary>
    public AsyncCommand<ShellStatement> LensExplainCommand { get; }

    /// <summary>code lens「复制为 C#」。</summary>
    public AsyncCommand<ShellStatement> LensCopyCodeCommand { get; }

    /// <summary>code lens「在管道构建器中打开」。</summary>
    public RelayCommand<ShellStatement> LensPipelineCommand { get; }

    /// <inheritdoc />
    public override async Task LoadAsync()
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        foreach (string db in Workspace.Databases)
        {
            if (!Databases.Contains(db))
            {
                Databases.Add(db);
            }
        }
        if (!Databases.Contains(_database))
        {
            Databases.Add(_database);
        }
        _ = LoadDatabasesAsync();
        _ = LoadSnippetsAsync();
        _ = LoadHistoryAsync();
        _ = RefreshHelperAsync();
        if (_runOnLoad)
        {
            _runOnLoad = false;
            await RunAsync(all: true).ConfigureAwait(true);
        }
    }

    /// <inheritdoc />
    public override Task RefreshAsync() => RunAsync(all: true);

    /// <inheritdoc />
    public override void Dispose()
    {
        _diagnosticTimer.Stop();
        _helperTimer.Stop();
        _runCts?.Cancel();
        base.Dispose();
    }

    private async Task LoadDatabasesAsync()
    {
        try
        {
            IReadOnlyList<Core.DatabaseInfo> databases = await Workspace.Connection.ListDatabasesAsync().ConfigureAwait(true);
            foreach (Core.DatabaseInfo info in databases)
            {
                if (!Databases.Contains(info.Name))
                {
                    Databases.Add(info.Name);
                }
            }
        }
        catch (Exception ex) when (ex is MongoDB.Driver.MongoException or TimeoutException)
        {
            // 没有 listDatabases 权限时下拉里只有对象树已知的库 —— 照样能用。
            Workspace.Log.Info($"Listing databases for the query editor failed: {ex.Message}");
        }
    }

    private void SelectPane(QueryPane? pane)
    {
        if (pane is null)
        {
            return;
        }
        foreach (QueryPane p in Panes)
        {
            p.IsSelected = ReferenceEquals(p, pane);
        }
        SelectedPane = pane;
        if (pane is QueryResultSet set)
        {
            set.ViewMode = _resultView;
        }
    }

    /// <summary>标签页给宿主状态栏的那一行。</summary>
    private void UpdateStatus()
    {
        var parts = new List<string>();
        if (_lastRunSummary is { Length: > 0 } run)
        {
            parts.Add(run);
        }
        if (IsModified || _savedName is null && _text.Length > 0)
        {
            parts.Add(Loc.Format("Query_StatusUnsaved", _number));
        }
        else if (_savedName is { } name)
        {
            parts.Add(Loc.Format("Query_StatusSaved", name));
        }
        StatusText = string.Join(" · ", parts);
    }

    /// <summary>毫秒 → <c>30s</c> / <c>500ms</c> / <c>5min</c> / 不限。</summary>
    internal static string FormatDuration(int ms, Loc loc) => ms switch
    {
        <= 0 => loc["Query_Unlimited"],
        < 1000 => ms.ToString(CultureInfo.InvariantCulture) + "ms",
        _ when ms % 60_000 == 0 => (ms / 60_000).ToString(CultureInfo.InvariantCulture) + "min",
        _ when ms % 1000 == 0 => (ms / 1000).ToString(CultureInfo.InvariantCulture) + "s",
        _ => (ms / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + "s"
    };

    /// <summary>某个位置上生效的库:它之前最后一条 <c>use</c>;没有就是下拉里的当前库。</summary>
    internal string DatabaseAt(int offset) => DatabaseAt(_text, offset);

    /// <summary>失败的统一出口:写进消息页并弹提示(AsyncCommand 会吞异常,不自己报就没人知道)。</summary>
    private void Report(Exception ex, string? statement = null)
    {
        string message = ex switch
        {
            ShellExecutionException or ShellParseException => ex.Message,
            _ => MongoConnector.Describe(ex)
        };
        Messages.Messages.Add(new QueryMessage(DateTime.Now, QueryMessageKind.Error, message, statement, null));
        Workspace.Toast(new ToastRequest { Title = Loc.Format("Common_Failed", message), Kind = ToastKind.Error });
    }
}
