using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Analysis;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Shell;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>对比表的一行。</summary>
/// <param name="Label">指标。</param>
/// <param name="Current">当前计划的值。</param>
/// <param name="Hinted">带 hint 的值。</param>
/// <param name="Winner">哪边更好:-1 当前、1 hint、0 一样或不可比。</param>
internal sealed record HintCompareRow(string Label, string Current, string Hinted, int Winner)
{
    /// <summary>当前计划更好。</summary>
    public bool CurrentBetter => Winner < 0;

    /// <summary>hint 更好。</summary>
    public bool HintedBetter => Winner > 0;
}

/// <summary>
/// 「用 hint 对比」:选一个索引,带 hint 再 explain(executionStats)一次,与当前获胜计划逐项并排 ——
/// 规划器的选择不一定最优(试运行只跑了 101 个工作单元),这是验证"换个索引会不会更快"最直接的办法。
/// </summary>
internal sealed class HintCompareDialogViewModel : DialogViewModel, IViewFactory
{
    private readonly ShellExecutor _executor;
    private readonly ShellCommand _command;
    private readonly string _database;
    private readonly ExplainPlan _current;
    private readonly int _maxTimeMs;

    /// <summary>构造。</summary>
    public HintCompareDialogViewModel(IMongoWorkspace workspace, ShellExecutor executor, ShellCommand command, string database, ExplainPlan current, int maxTimeMs)
        : base(workspace)
    {
        _executor = executor;
        _command = command;
        _database = command.Database ?? database;
        _current = current;
        _maxTimeMs = maxTimeMs;
        Title = workspace.Loc["Query_HintTitle"];
        Subtitle = $"{_database}.{command.Collection}";
        CompareCommand = new AsyncCommand(CompareAsync, () => SelectedIndex is not null && !IsBusy);
        _ = LoadIndexesAsync();
        Rows = Build(null);
    }

    /// <inheritdoc />
    public override string IconKey => "Mongo.git-compare";

    /// <inheritdoc />
    public override double Width => 620;

    /// <summary>集合的索引名。</summary>
    public ObservableCollection<string> Indexes { get; } = [];

    /// <summary>选中的索引。</summary>
    public string? SelectedIndex
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                CompareCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>跑着。</summary>
    public bool IsBusy
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                CompareCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>失败原因。</summary>
    public string? Error
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(HasError));
            }
        }
    }

    /// <summary>有没有失败。</summary>
    public bool HasError => Error is not null;

    /// <summary>当前计划一列的标题。</summary>
    public string CurrentTitle => Loc.Format("Query_HintCurrent", _current.Summary);

    /// <summary>hint 一列的标题。</summary>
    public string HintedTitle
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>对比表。</summary>
    public IReadOnlyList<HintCompareRow> Rows
    {
        get;
        private set => SetProperty(ref field, value);
    } = [];

    /// <summary>对比。</summary>
    public AsyncCommand CompareCommand { get; }

    private async Task LoadIndexesAsync()
    {
        try
        {
            IReadOnlyList<BsonDocument> indexes = await Workspace.Connection.ListIndexesAsync(_database, _command.Collection!).ConfigureAwait(true);
            string? used = _current.Stages.FirstOrDefault(static s => s.IndexName is not null)?.IndexName;
            foreach (BsonDocument index in indexes)
            {
                string name = index.GetValue("name", "").ToString() ?? "";
                if (name.Length > 0 && index.GetValue("key", null) is BsonDocument key && !key.Values.Any(static v => v.IsString))
                {
                    Indexes.Add(name);
                }
            }
            // 默认选一个**不是**当前在用的索引 —— 对比的意义在于"换一个"。
            SelectedIndex = Indexes.FirstOrDefault(n => n != used && n != "_id_") ?? Indexes.FirstOrDefault();
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Error = MongoConnector.Describe(ex);
        }
    }

    private async Task CompareAsync()
    {
        if (SelectedIndex is not { } index)
        {
            return;
        }
        IsBusy = true;
        Error = null;
        try
        {
            var options = new ShellRunOptions { MaxTimeMs = _maxTimeMs };
            ShellResult result = await Task.Run(() => _executor.ExplainAsync(_command, _database, "executionStats", options, new BsonString(index)))
                .ConfigureAwait(true);
            if (result.Explain is { } explain)
            {
                var hinted = ExplainPlan.Parse(explain);
                HintedTitle = Loc.Format("Query_HintWith", index);
                Rows = Build(hinted);
            }
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException or ShellExecutionException)
        {
            Error = ex is ShellExecutionException ? ex.Message : MongoConnector.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private IReadOnlyList<HintCompareRow> Build(ExplainPlan? hinted)
    {
        static string N(long? v) => v is { } n ? BsonText.Grouped(n) : "—";
        static int Lower(double? a, double? b) => a is null || b is null || Math.Abs(a.Value - b.Value) < 0.0001 ? 0 : a < b ? -1 : 1;
        string Ms(long? v) => v is { } n ? Loc.Format("Common_Ms", n) : "—";
        string Ratio(double? r) => r is { } x ? x.ToString(x >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture) + " ×" : "—";
        string Sort(ExplainPlan? p) => p is null ? "—" : p.QueryLayerSort ? Loc["Common_Yes"] : Loc["Common_No"];
        return
        [
            new HintCompareRow(Loc["Query_HintPlan"], _current.Summary, hinted?.Summary ?? "—", 0),
            new HintCompareRow(Loc["Query_ExplainTotal"], Ms(_current.TotalMs), Ms(hinted?.TotalMs), Lower(_current.TotalMs, hinted?.TotalMs)),
            new HintCompareRow(Loc["Query_ExplainReturned"], N(_current.NReturned), N(hinted?.NReturned), 0),
            new HintCompareRow(Loc["Query_ExplainKeys"], N(_current.TotalKeysExamined), N(hinted?.TotalKeysExamined),
                Lower(_current.TotalKeysExamined, hinted?.TotalKeysExamined)),
            new HintCompareRow(Loc["Query_ExplainDocs"], N(_current.TotalDocsExamined), N(hinted?.TotalDocsExamined),
                Lower(_current.TotalDocsExamined, hinted?.TotalDocsExamined)),
            new HintCompareRow(Loc["Query_ExplainRatio"], Ratio(_current.ExaminedRatio), Ratio(hinted?.ExaminedRatio),
                Lower(_current.ExaminedRatio, hinted?.ExaminedRatio)),
            new HintCompareRow(Loc["Query_ExplainInMemorySort"], Sort(_current), Sort(hinted),
                hinted is null || _current.QueryLayerSort == hinted.QueryLayerSort ? 0 : _current.QueryLayerSort ? 1 : -1)
        ];
    }

    /// <inheritdoc />
    public Control CreateView() => new HintCompareDialogView(this);
}
