using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Analysis;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Shell;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>code lens 上「上次 38 ms · 100 条」的那份记录。</summary>
/// <param name="ElapsedMs">耗时。</param>
/// <param name="Count">返回的文档数;不是查询为 <see langword="null" />。</param>
/// <param name="Ok">成功与否。</param>
internal sealed record LensRecord(long ElapsedMs, int? Count, bool Ok);

internal sealed partial class QueryTabViewModel
{
    private readonly Dictionary<string, LensRecord> _lens = [with(StringComparer.Ordinal)];
    private CancellationTokenSource? _runCts;
    private string? _runComment;
    private string _runningText = "";
    private string? _lastRunSummary;
    private string _execState = "idle";

    /// <summary>正在运行。</summary>
    public bool IsRunning
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RunCommand.RaiseCanExecuteChanged();
                RunCurrentCommand.RaiseCanExecuteChanged();
                RaisePropertyChanged(nameof(StopIconToken));
                RaisePropertyChanged(nameof(ExecText));
                ExplainCommand.RaiseCanExecuteChanged();
                RaisePropertyChanged(nameof(ExecIconKey));
                RaisePropertyChanged(nameof(ExecIconToken));
            }
        }
    }

    /// <summary>状态条左侧的主文字(<c>2 条语句执行完成</c> / <c>正在执行 1/2…</c>)。</summary>
    public string ExecText
    {
        get => IsRunning ? _runningText : field;
        private set
        {
            field = value;
            RaisePropertyChanged();
        }
    } = "";

    /// <summary>各语句耗时(<c>find 38 ms · aggregate 112 ms</c>)。</summary>
    public string ExecTimes
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>最近一条查询的计划摘要(<c>IXSCAN status_1_createdAt_-1 · 扫描 1,284 / 返回 24</c>)。</summary>
    public string PlanText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>停止按钮的图标颜色(运行时红)。</summary>
    public string StopIconToken => IsRunning ? "VelaError" : "VelaTextMuted";

    /// <summary>状态条图标。</summary>
    public string ExecIconKey => IsRunning ? "Mongo.loader-circle" : _execState switch
    {
        "ok" => "Mongo.circle-check",
        "error" => "Mongo.circle-x",
        "stopped" => "Mongo.octagon-x",
        _ => "Mongo.info"
    };

    /// <summary>状态条图标颜色。</summary>
    public string ExecIconToken => IsRunning ? "VelaAccent" : _execState switch
    {
        "ok" => "VelaStatusConnected",
        "error" => "VelaError",
        "stopped" => "VelaWarning",
        _ => "VelaTextMuted"
    };

    /// <summary>某条语句上次运行的记录(code lens 用);没跑过为 <see langword="null" />。</summary>
    internal LensRecord? LensFor(ShellStatement statement) => _lens.GetValueOrDefault(LensKey(statement.Text));

    /// <summary>
    /// code lens 记录的键:去掉空白与引号。改个缩进、给键补上引号(Alt+↵ 修复)之后,「上次 38 ms · 100 条」还认得是同一条语句。
    /// </summary>
    private static string LensKey(string text) => new([.. text.Where(static c => !char.IsWhiteSpace(c) && c is not ('"' or '\''))]);

    /// <summary>运行:全部(有选区时只跑选区)或光标所在的那条。</summary>
    public async Task RunAsync(bool all)
    {
        if (IsRunning)
        {
            return;
        }
        IReadOnlyList<ShellStatement> statements = ShellScript.Split(_text);
        (int selOffset, int selLength) = Editor?.Selection ?? (0, 0);
        IReadOnlyList<ShellStatement> targets;
        if (selLength > 0 && selOffset + selLength <= _text.Length)
        {
            // 选区:只跑选中的那一段(按选区内容重新切分,偏移换回整段脚本坐标)。
            targets = [.. ShellScript.Split(_text.Substring(selOffset, selLength)).Select(s => s with { Offset = s.Offset + selOffset })];
        }
        else if (all)
        {
            targets = statements;
        }
        else
        {
            targets = ShellScript.At(statements, CaretOffset) is { } current ? [current] : [];
        }
        await RunStatementsAsync(targets).ConfigureAwait(true);
    }

    /// <summary>逐条运行一组语句。</summary>
    internal async Task RunStatementsAsync(IReadOnlyList<ShellStatement> targets)
    {
        if (IsRunning)
        {
            return;
        }
        if (targets.Count == 0)
        {
            _execState = "idle";
            ExecText = Loc["Query_NothingToRun"];
            RaisePropertyChanged(nameof(ExecIconKey));
            return;
        }
        var session = new ShellSession(DatabaseAt(targets[0].Offset));
        using var cts = new CancellationTokenSource();
        _runCts = cts;
        _runComment = "velashell-query-" + Guid.NewGuid().ToString("N")[..10];
        var options = new ShellRunOptions { MaxTimeMs = _maxTimeMs, Comment = _runComment };
        // 未固定的旧结果让位给这一次;固定的留着。
        foreach (QueryResultSet stale in Panes.OfType<QueryResultSet>().Where(static p => !p.IsPinned).ToList())
        {
            _ = Panes.Remove(stale);
        }
        int nextNumber = Panes.OfType<QueryResultSet>().Select(static p => p.Number).DefaultIfEmpty(0).Max() + 1;
        var timings = new List<string>();
        QueryResultSet? lastSet = null;
        ShellResult? lastQuery = null;
        ShellCommand? lastQueryCommand = null;
        bool explained = false;
        int completed = 0;
        int position = 0;
        string state = "ok";
        IsRunning = true;
        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                ShellStatement statement = targets[i];
                position = i + 1;
                _runningText = Loc.Format("Query_Running", i + 1, targets.Count);
                RaisePropertyChanged(nameof(ExecText));
                string echo = OneLine(statement.Text);
                ShellCommand command;
                try
                {
                    command = ShellParser.Parse(statement);
                }
                catch (ShellParseException ex)
                {
                    Messages.Messages.Add(new QueryMessage(DateTime.Now, QueryMessageKind.Error, Describe(ex), echo, null));
                    state = "error";
                    break;
                }
                ShellResult result;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    ShellRunOptions run = options;
                    result = await Task.Run(() => _executor.ExecuteAsync(command, session, run, cts.Token), cts.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    Messages.Messages.Add(new QueryMessage(DateTime.Now, QueryMessageKind.Warning, Loc["Query_MsgStopped"], echo, null));
                    await RecordHistoryAsync(session.Database, statement.Text, watch.ElapsedMilliseconds, ok: false).ConfigureAwait(true);
                    state = "stopped";
                    break;
                }
                catch (Exception ex) when (ex is MongoException or TimeoutException or ShellExecutionException or FormatException or InvalidOperationException)
                {
                    string message = ex is ShellExecutionException ? ex.Message : MongoConnector.Describe(ex);
                    Messages.Messages.Add(new QueryMessage(DateTime.Now, QueryMessageKind.Error, message, echo, Loc.Format("Common_Ms", watch.ElapsedMilliseconds)));
                    _lens[LensKey(statement.Text)] = new LensRecord(watch.ElapsedMilliseconds, null, false);
                    await RecordHistoryAsync(session.Database, statement.Text, watch.ElapsedMilliseconds, ok: false).ConfigureAwait(true);
                    state = "error";
                    break;
                }
                long ms = (long)result.Elapsed.TotalMilliseconds;
                string op = command.Operation;
                // use 只是切换会话的库:不计入"N 条语句执行完成"、不进耗时与消息(设计稿 03 的脚本里有 use,状态条写的是 2 条)。
                if (command.Kind != ShellCommandKind.Use)
                {
                    completed++;
                    timings.Add($"{op} {Loc.Format("Common_Ms", ms)}");
                    _lens[LensKey(statement.Text)] = new LensRecord(ms, result.Kind == ShellResultKind.Documents ? result.Documents.Count : null, !result.Declined);
                    await RecordHistoryAsync(result.Database, statement.Text, ms, ok: !result.Declined).ConfigureAwait(true);
                    string prefix = result.Collection is { } coll ? $"{coll}.{op}" : op;
                    Messages.Messages.Add(new QueryMessage(DateTime.Now,
                        result.Declined ? QueryMessageKind.Warning : QueryMessageKind.Ok,
                        $"{prefix}: {result.Message}", echo, Loc.Format("Common_Ms", ms)));
                }
                if (result.Truncated)
                {
                    Messages.Messages.Add(new QueryMessage(DateTime.Now, QueryMessageKind.Warning,
                        Loc.Format("Query_TruncatedNotice", result.Documents.Count), echo, null));
                }

                switch (result.Kind)
                {
                    case ShellResultKind.Documents:
                        {
                            var set = new QueryResultSet(Loc, Workspace.Connection.Settings.Ejson, nextNumber++, result, statement) { ViewMode = ResultView };
                            Panes.Insert(Panes.IndexOf(Explain), set);
                            lastSet = set;
                            if (command.Method?.Name is "find" or "findOne" or "aggregate" && command.Kind == ShellCommandKind.Collection
                                && command.Modifier("count") is null)
                            {
                                lastQuery = result;
                                lastQueryCommand = command;
                            }
                            break;
                        }
                    case ShellResultKind.Explain when result.Explain is { } explain:
                        Explain.Begin(command, result.Database);
                        ShowExplain(explain);
                        explained = true;
                        break;
                }
                if (command.Kind == ShellCommandKind.Use)
                {
                    Database = session.Database;
                }
                if (result.ChangesCatalog)
                {
                    ForgetCollections(result.Database);
                    _ = Workspace.RefreshTreeAsync(result.Database);
                }
            }
        }
        finally
        {
            _runCts = null;
            _runComment = null;
            IsRunning = false;
        }

        _execState = state;
        ExecText = state switch
        {
            "ok" when completed == 0 => Loc.Format("Query_MsgUse", session.Database),
            "ok" => Loc.Format("Query_ExecDone", completed),
            "stopped" => Loc.Format("Query_ExecStopped", position - 1, targets.Count),
            _ => Loc.Format("Query_ExecFailed", position, targets.Count)
        };
        RaisePropertyChanged(nameof(ExecIconKey));
        RaisePropertyChanged(nameof(ExecIconToken));
        ExecTimes = string.Join(" · ", timings);
        RaisePropertyChanged(nameof(Statements));
        LensChanged?.Invoke();

        if (state != "ok")
        {
            SelectPane(Messages);
        }
        else if (explained && lastSet is null)
        {
            SelectPane(Explain);
        }
        else if (lastSet is not null)
        {
            SelectPane(lastSet);
        }
        else
        {
            SelectPane(Messages);
        }

        int rows = lastSet?.Documents.Count ?? 0;
        _lastRunSummary = lastSet is null ? null : Loc.Format("Query_StatusRun", BsonText.Grouped(rows), Loc.Format("Common_Ms", (long)lastSet.Result.Elapsed.TotalMilliseconds));
        UpdateStatus();
        PlanText = "";
        if (lastQuery is not null && lastQueryCommand is not null && lastQuery.Elapsed < TimeSpan.FromSeconds(2))
        {
            _ = ProbePlanAsync(lastQueryCommand, lastQuery.Database);
        }
    }

    /// <summary>停止:放弃等待,并在服务器上按 comment 标记 killOp。</summary>
    public async Task StopAsync()
    {
        if (_runCts is not { } cts)
        {
            return;
        }
        string? comment = _runComment;
        await cts.CancelAsync().ConfigureAwait(true);
        if (comment is null)
        {
            return;
        }
        try
        {
            int killed = await Workspace.Connection.KillByCommentAsync(comment).ConfigureAwait(true);
            if (killed > 0)
            {
                Messages.Messages.Add(new QueryMessage(DateTime.Now, QueryMessageKind.Warning, Loc.Format("Query_MsgKilled", killed), null, null));
            }
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Messages.Messages.Add(new QueryMessage(DateTime.Now, QueryMessageKind.Warning, Loc.Format("Query_MsgKillFailed", MongoConnector.Describe(ex)), null, null));
        }
    }

    /// <summary>
    /// 跑完一条查询后,后台用 explain 把计划摘要补到状态条上(设计稿 03 底部「IXSCAN … · 扫描 1,284 / 返回 24」)。
    /// 只对本来就快的查询做(上一跑不到 2 秒):explain executionStats 会再执行一遍,慢查询不值得为一行状态多跑一次。
    /// </summary>
    private async Task ProbePlanAsync(ShellCommand command, string database)
    {
        try
        {
            var options = new ShellRunOptions { MaxTimeMs = 5000 };
            ShellResult result = await Task.Run(() => _executor.ExplainAsync(command, database, "executionStats", options, null)).ConfigureAwait(true);
            if (result.Explain is not { } explain)
            {
                return;
            }
            var plan = ExplainPlan.Parse(explain);
            long examined = Math.Max(plan.TotalKeysExamined ?? 0, plan.TotalDocsExamined ?? 0);
            PlanText = plan.NReturned is { } returned
                ? $"{plan.Summary} · {Loc.Format("Query_PlanScan", BsonText.Grouped(examined), BsonText.Grouped(returned))}"
                : plan.Summary;
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException or ShellExecutionException or OperationCanceledException)
        {
            // 状态条上少一行计划摘要而已,不打扰用户。
        }
    }

    // ── 执行计划 ────────────────────────────────────────────────────────────

    /// <summary>F6:对光标所在语句(或指定语句)跑 explain;光标不在可解释的语句上时取第一条可解释的。</summary>
    public async Task ExplainCurrentAsync(ShellStatement? statement)
    {
        IReadOnlyList<ShellStatement> statements = ShellScript.Split(_text);
        ShellCommand? command = null;
        if (statement is not null)
        {
            command = TryCommand(statement);
        }
        else
        {
            if (ShellScript.At(statements, CaretOffset) is { } current)
            {
                command = TryCommand(current);
            }
            if (command is null || !Explainable(command))
            {
                command = statements.Select(TryCommand).FirstOrDefault(static c => c is not null && Explainable(c));
            }
        }
        if (command is null || !Explainable(command))
        {
            Workspace.Toast(new ToastRequest { Title = Loc["Query_NothingToExplain"], Kind = ToastKind.Info });
            return;
        }
        await ExplainAsync(command, DatabaseAt(command.Statement.Offset)).ConfigureAwait(true);
    }

    /// <summary>对一条语句跑 explain 并显示在「执行计划」页签。</summary>
    internal async Task ExplainAsync(ShellCommand command, string database)
    {
        Explain.Begin(command, database);
        SelectPane(Explain);
        // executionStats 一律按 allPlansExecution 去要:后者是前者的超集,多出来的只是候选计划的试运行数据
        // (那本来就跑过了,不额外花服务器的时间),而候选计划列表的 works / score 只有它给得出来。
        string verbosity = Explain.Verbosity == "executionStats" ? "allPlansExecution" : Explain.Verbosity;
        var options = new ShellRunOptions { MaxTimeMs = _maxTimeMs };
        try
        {
            ShellResult result = await Task.Run(() => _executor.ExplainAsync(command, database, verbosity, options, null)).ConfigureAwait(true);
            if (result.Explain is { } explain)
            {
                ShowExplain(explain);
                Messages.Messages.Add(new QueryMessage(DateTime.Now, QueryMessageKind.Info, result.Message, OneLine(command.Statement.Text),
                    Loc.Format("Common_Ms", (long)result.Elapsed.TotalMilliseconds)));
            }
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException or ShellExecutionException)
        {
            string message = ex is ShellExecutionException ? ex.Message : MongoConnector.Describe(ex);
            Explain.Fail(message);
            Messages.Messages.Add(new QueryMessage(DateTime.Now, QueryMessageKind.Error, message, OneLine(command.Statement.Text), null));
        }
    }

    private void ShowExplain(BsonDocument explain)
    {
        var plan = ExplainPlan.Parse(explain);
        Explain.Show(plan);
        if (plan.NReturned is { } returned)
        {
            long examined = Math.Max(plan.TotalKeysExamined ?? 0, plan.TotalDocsExamined ?? 0);
            PlanText = $"{plan.Summary} · {Loc.Format("Query_PlanScan", BsonText.Grouped(examined), BsonText.Grouped(returned))}";
        }
    }

    /// <summary>code lens 的数据变了(跑完一次)。</summary>
    internal event Action? LensChanged;

    /// <summary>「创建该索引…」:确认之后直接 createIndexes,然后重跑一次执行计划看效果。</summary>
    internal async Task CreateSuggestedIndexAsync(BsonDocument keys)
    {
        if (Explain.Command is not { Collection: { } collection } command)
        {
            return;
        }
        string database = Explain.Database ?? _database;
        string name = ShellExecutor.IndexName(keys);
        bool confirmed = await Workspace.ConfirmAsync(new ConfirmRequest
        {
            Title = Loc["Query_CreateIndexTitle"],
            Message = Loc.Format("Query_CreateIndexBody", $"{database}.{collection}"),
            ConfirmLabel = Loc["Query_CreateIndexConfirm"],
            IconKey = "Mongo.key-round",
            Danger = false,
            Facts =
            [
                new ConfirmFact(Loc["Query_ConfirmNamespace"], $"{database}.{collection}"),
                new ConfirmFact(Loc["Query_CreateIndexKeys"], BsonText.Literal(keys)),
                new ConfirmFact(Loc["Query_CreateIndexName"], name)
            ]
        }).ConfigureAwait(true);
        if (!confirmed || !Workspace.EnsureWritable(database))
        {
            return;
        }
        try
        {
            _ = await Workspace.Connection.RunCommandAsync(database, new BsonDocument
            {
                { "createIndexes", collection },
                { "indexes", new BsonArray { new BsonDocument { { "key", keys }, { "name", name } } } }
            }).ConfigureAwait(true);
            Workspace.Toast(new ToastRequest
            {
                Title = Loc.Format("Query_IndexCreated", name),
                Kind = ToastKind.Success,
                ActionLabel = Loc["Query_OpenIndexes"],
                Action = () =>
                {
                    Workspace.OpenDesign(database, collection, DesignPage.Indexes);
                    return Task.CompletedTask;
                },
                Duration = TimeSpan.FromSeconds(8)
            });
            Messages.Messages.Add(new QueryMessage(DateTime.Now, QueryMessageKind.Ok, Loc.Format("Query_IndexCreated", name), null, null));
            await ExplainAsync(command, database).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Report(ex);
        }
    }

    /// <summary>「用 hint 对比」:选一个索引,带 hint 再 explain 一次,与当前计划并排比较。</summary>
    internal Task CompareWithHintAsync()
    {
        if (Explain.Command is not { } command || Explain.Plan is not { } plan)
        {
            return Task.CompletedTask;
        }
        Workspace.ShowDialog(new HintCompareDialogViewModel(Workspace, _executor, command, Explain.Database ?? _database, plan, _maxTimeMs));
        return Task.CompletedTask;
    }

    private static bool Explainable(ShellCommand command) =>
        command.Kind == ShellCommandKind.Collection && command.Method?.Name is "find" or "findOne" or "aggregate" or "countDocuments" or "distinct"
            or "updateOne" or "updateMany" or "replaceOne" or "deleteOne" or "deleteMany" or "findOneAndUpdate" or "findOneAndReplace" or "findOneAndDelete";

    private static ShellCommand? TryCommand(ShellStatement statement) =>
        ShellParser.TryParse(statement, out ShellCommand? command, out _) ? command : null;

    private async Task RecordHistoryAsync(string database, string text, long ms, bool ok)
    {
        var entry = new QueryHistoryEntry(DateTimeOffset.Now, database, text, ms, ok);
        History.Insert(0, new QueryHistoryRow(entry));
        while (History.Count > 300)
        {
            History.RemoveAt(History.Count - 1);
        }
        await Workspace.Store.AppendQueryHistoryAsync(Workspace.ConnectionKey, entry).ConfigureAwait(true);
    }

    /// <summary>解析错误 → 一句给人看的话。</summary>
    private string Describe(ShellParseException ex) => Loc.Format(ex.MessageKey, ex.Argument);

    /// <summary>语句压成一行(消息页、历史页)。</summary>
    internal static string OneLine(string text)
    {
        string line = string.Join(' ', text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Select(static l => l.Trim()));
        return line.Length > 160 ? line[..159] + "…" : line;
    }

    // ── 结果区工具 ──────────────────────────────────────────────────────────

    private async Task ExportSelectedAsync()
    {
        switch (SelectedPane)
        {
            case QueryResultSet { Result.Query: { } query } set when set.Result.Operation is "find":
                Workspace.ShowDialog(new ExportWizardViewModel(Workspace, query.Database, query.Collection, query with { Limit = query.Limit }, null));
                return;
            case QueryResultSet set:
                await SaveJsonAsync(set.ToJson(), $"{set.Result.Collection ?? set.Result.Database}-{set.Result.Operation}.json").ConfigureAwait(true);
                return;
            case ExplainPane { RawText: { } raw }:
                await SaveJsonAsync(raw, "explain.json").ConfigureAwait(true);
                return;
        }
    }

    private async Task SaveJsonAsync(string json, string suggested)
    {
        string? path = await Workspace.PickSaveFileAsync(Loc["Query_ExportJsonTitle"], suggested, [FileKind.Json]).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }
        try
        {
            await File.WriteAllTextAsync(path, json).ConfigureAwait(true);
            Workspace.Toast(new ToastRequest { Title = Loc.Format("Query_Exported", Path.GetFileName(path)), Kind = ToastKind.Success });
        }
        catch (IOException ex)
        {
            Workspace.Toast(new ToastRequest { Title = Loc.Format("Common_Failed", ex.Message), Kind = ToastKind.Error });
        }
        catch (UnauthorizedAccessException ex)
        {
            Workspace.Toast(new ToastRequest { Title = Loc.Format("Common_Failed", ex.Message), Kind = ToastKind.Error });
        }
    }

    private async Task CopySelectedAsync()
    {
        string? text = SelectedPane switch
        {
            QueryResultSet set => set.ToJson(),
            ExplainPane explain => explain.RawText,
            MessagesPane messages => string.Join('\n', messages.Messages.Select(static m => $"{m.Time}  {m.Text}{(m.HasStatement ? "  " + m.Statement : "")}")),
            _ => null
        };
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        await Workspace.CopyAsync(text).ConfigureAwait(true);
        Workspace.Toast(new ToastRequest { Title = Loc["Common_Copied"], Kind = ToastKind.Success });
    }

    /// <summary>格式化一个统计数(网格状态行、code lens)。</summary>
    internal static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
