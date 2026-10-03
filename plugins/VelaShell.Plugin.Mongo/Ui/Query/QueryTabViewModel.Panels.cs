using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Shell;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>片段页的一项:词汇表里的代码片段,或用户保存的查询。</summary>
/// <param name="Label">名字。</param>
/// <param name="Description">一句说明 / 保存时间。</param>
/// <param name="Text">片段正文(<c>|</c> 是光标落点)或保存的整段脚本。</param>
/// <param name="IsSaved">是不是保存的查询。</param>
internal sealed record QuerySnippet(string Label, string Description, string Text, bool IsSaved)
{
    /// <summary>图标。</summary>
    public string IconKey => IsSaved ? "Mongo.bookmark" : "Mongo.square-code";

    /// <summary>图标颜色。</summary>
    public string IconToken => IsSaved ? "VelaInfo" : "VelaWarning";

    /// <summary>预览(单行)。</summary>
    public string Preview => QueryTabViewModel.OneLine(Text.Replace("|", "", StringComparison.Ordinal));
}

/// <summary>历史页的一行。</summary>
/// <param name="Entry">历史记录。</param>
internal sealed record QueryHistoryRow(QueryHistoryEntry Entry)
{
    /// <summary>时间(今天只给时分秒,往前给日期)。</summary>
    public string Time => Entry.At.LocalDateTime.Date == DateTime.Today
        ? Entry.At.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
        : Entry.At.LocalDateTime.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>库。</summary>
    public string Database => Entry.Database;

    /// <summary>耗时。</summary>
    public string Elapsed => Entry.ElapsedMs.ToString(CultureInfo.InvariantCulture) + " ms";

    /// <summary>成功与否。</summary>
    public bool Ok => Entry.Ok;

    /// <summary>状态点颜色。</summary>
    public string DotToken => Entry.Ok ? "VelaStatusConnected" : "VelaError";

    /// <summary>语句(单行)。</summary>
    public string Preview => QueryTabViewModel.OneLine(Entry.Text);
}

internal sealed partial class QueryTabViewModel
{
    private readonly DispatcherTimer _helperTimer;
    private (string Database, string Collection)? _helperTarget;

    /// <summary>片段页:词汇表片段 + 保存的查询。</summary>
    public ObservableCollection<QuerySnippet> Snippets { get; } = [];

    /// <summary>历史页(新的在前)。</summary>
    public ObservableCollection<QueryHistoryRow> History { get; } = [];

    /// <summary>历史为空。</summary>
    public bool HistoryEmpty => History.Count == 0;

    /// <summary>字段页的集合名。</summary>
    public string HelperCollection
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(HasHelperCollection));
            }
        }
    } = "";

    /// <summary>有没有可抽样的集合。</summary>
    public bool HasHelperCollection => HelperCollection.Length > 0;

    /// <summary>字段页的集合在哪个库(集合名后面的 <c>@shop</c>:抽的是哪个库里的这个集合,一眼看清)。</summary>
    public string HelperDatabase
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>那个库里没有这个集合时的提示;有为空。</summary>
    public string HelperMissing
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(HasHelperMissing));
            }
        }
    } = "";

    /// <summary>那个库里没有这个集合。</summary>
    public bool HasHelperMissing => HelperMissing.Length > 0;

    /// <summary>「抽样 1,000」。</summary>
    public string HelperSampleText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>字段页的字段。</summary>
    public IReadOnlyList<SampledField> HelperFields
    {
        get;
        private set => SetProperty(ref field, value);
    } = [];

    /// <summary>字段页在抽样。</summary>
    public bool HelperBusy
    {
        get;
        private set => SetProperty(ref field, value);
    }

    private void ScheduleHelper()
    {
        _helperTimer?.Stop();
        _helperTimer?.Start();
        OnCaretSettled();
    }

    /// <summary>
    /// 字段页跟着光标走:光标所在语句作用的集合;那条语句没有集合(<c>use</c> 之类)就保持上一个,
    /// 一开始取脚本里第一条有集合的语句。
    /// </summary>
    internal async Task RefreshHelperAsync()
    {
        IReadOnlyList<ShellStatement> statements = ShellScript.Split(_text);
        (string? collection, string? database) = Target(ShellScript.At(statements, _caretOffset));
        if (collection is null && _helperTarget is null)
        {
            foreach (ShellStatement statement in statements)
            {
                (collection, database) = Target(statement);
                if (collection is not null)
                {
                    break;
                }
            }
        }
        if (collection is null || database is null || _helperTarget == (database, collection))
        {
            return;
        }
        _helperTarget = (database, collection);
        HelperCollection = collection;
        HelperDatabase = "@" + database;
        HelperMissing = "";
        HelperBusy = true;
        SampledSchema? schema = await SchemaAsync(database, collection).ConfigureAwait(true);
        IReadOnlyList<CollectionInfo> known = await CollectionsAsync(database).ConfigureAwait(true);
        if (_helperTarget != (database, collection))
        {
            return;
        }
        HelperBusy = false;
        // 列不出集合(没权限、超时)就不下结论。
        HelperMissing = _collections.ContainsKey(database) && known.All(c => c.Name != collection)
            ? Loc.Format("Query_CollectionMissing", collection, database)
            : "";
        HelperFields = schema?.Fields ?? [];
        HelperSampleText = schema is null ? "" : Loc.Format("Query_SampledCount", BsonText.Grouped(schema.Sampled));
    }

    private (string? Collection, string? Database) Target(ShellStatement? statement)
    {
        if (statement is null)
        {
            return (null, null);
        }
        (string? collection, _, string? database) = ShellCompletion.Head(statement.Text);
        if (collection is null && ShellParser.TryParse(statement, out ShellCommand? command, out _) && command?.Collection is { } parsed)
        {
            collection = parsed;
            database = command.Database;
        }
        return (collection, database ?? DatabaseAt(statement.Offset));
    }

    private async Task LoadSnippetsAsync()
    {
        IReadOnlyList<SavedItem> saved = await Workspace.Store.LoadSavedAsync("query", Workspace.ConnectionKey).ConfigureAwait(true);
        Snippets.Clear();
        foreach (SavedItem item in saved)
        {
            Snippets.Add(new QuerySnippet(item.Name, item.SavedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), item.Content, true));
        }
        foreach (VocabularyEntry entry in MongoVocabulary.Snippets)
        {
            Snippets.Add(new QuerySnippet(entry.Name, entry.Describe(Loc.IsChinese), entry.InsertText, false));
        }
    }

    private async Task LoadHistoryAsync()
    {
        IReadOnlyList<QueryHistoryEntry> entries = await Workspace.Store.LoadQueryHistoryAsync(Workspace.ConnectionKey).ConfigureAwait(true);
        if (entries.Count == 0)
        {
            // 宿主没有时序库(headless)时历史只在本会话内:保留内存里那份。
            return;
        }
        History.Clear();
        foreach (QueryHistoryEntry entry in entries)
        {
            History.Add(new QueryHistoryRow(entry));
        }
    }

    /// <summary>字段页双击:在光标处插入字段路径。</summary>
    private void InsertField(SampledField field)
    {
        ReplaceText(Math.Clamp(_caretOffset, 0, _text.Length), 0, field.Path, literal: true);
        Editor?.Focus();
    }

    /// <summary>
    /// 用一个片段:保存的查询整段替换编辑器;集合方法片段插在光标处 —— 光标前不是 <c>db.coll.</c> 时先补上它。
    /// </summary>
    private void UseSnippet(QuerySnippet snippet)
    {
        if (snippet.IsSaved)
        {
            _savedName = snippet.Label;
            _baseline = snippet.Text;
            ReplaceText(0, _text.Length, snippet.Text, literal: true);
            IsModified = false;
            UpdateStatus();
            return;
        }
        int caret = Math.Clamp(_caretOffset, 0, _text.Length);
        string before = _text[..caret];
        bool afterMember = System.Text.RegularExpressions.Regex.IsMatch(before, @"db\.[\w$.]+\.\s*$|\)\.\s*$");
        string text = snippet.Text;
        if (!afterMember)
        {
            string collection = HelperCollection.Length > 0 ? HelperCollection : "collection";
            string prefix = (before.Length > 0 && !before.EndsWith('\n') ? "\n" : "") + "db." + collection + ".";
            text = prefix + text;
        }
        ReplaceText(caret, 0, text);
        Editor?.Focus();
    }

    /// <summary>历史页单击:回填编辑器(并切到当时的库)。</summary>
    private void UseHistory(QueryHistoryRow row)
    {
        ReplaceText(0, _text.Length, row.Entry.Text, literal: true);
        if (row.Entry.Database.Length > 0)
        {
            Database = row.Entry.Database;
        }
        Editor?.Focus();
    }

    /// <summary>保存:第一次问名字,之后同名覆盖。</summary>
    private async Task SaveAsync()
    {
        if (_savedName is null)
        {
            Workspace.ShowDialog(new SaveQueryDialogViewModel(Workspace, Loc.Format("Query_Title", _number), SaveAsAsync));
            return;
        }
        await SaveAsAsync(_savedName).ConfigureAwait(true);
    }

    /// <summary>以某个名字保存。</summary>
    internal async Task SaveAsAsync(string name)
    {
        await Workspace.Store.SaveItemAsync("query", Workspace.ConnectionKey, new SavedItem(name, _text, DateTimeOffset.Now)).ConfigureAwait(true);
        _savedName = name;
        _baseline = _text;
        IsModified = false;
        UpdateStatus();
        await LoadSnippetsAsync().ConfigureAwait(true);
        Workspace.Toast(new ToastRequest { Title = Loc.Format("Query_Saved", name), Kind = ToastKind.Success });
    }

    // ── 导出为代码 / code lens ──────────────────────────────────────────────

    /// <summary>示例代码里的连接串(只有端点,不带凭据)。</summary>
    private string SampleConnectionString => "mongodb://" + Workspace.Connection.Endpoint;

    private (ShellCommand? Command, string Database) CommandAt(ShellStatement? statement)
    {
        statement ??= ShellScript.At(ShellScript.Split(_text), _caretOffset);
        if (statement is null)
        {
            return (null, _database);
        }
        if (!ShellParser.TryParse(statement, out ShellCommand? command, out ShellParseException? error))
        {
            Workspace.Toast(new ToastRequest { Title = Describe(error!), Kind = ToastKind.Warning });
            return (null, _database);
        }
        return (command, DatabaseAt(statement.Offset));
    }

    /// <summary>「导出为代码」:当前语句 → 选语言的对话框。</summary>
    internal void ExportCode(ShellStatement? statement, CodeTarget target)
    {
        (ShellCommand? command, string database) = CommandAt(statement);
        if (command is null)
        {
            if (statement is null && ShellScript.Split(_text).Count == 0)
            {
                Workspace.Toast(new ToastRequest { Title = Loc["Query_NothingToExport"], Kind = ToastKind.Info });
            }
            return;
        }
        Workspace.ShowDialog(new CodeExportDialogViewModel(Workspace, command, database, SampleConnectionString, target));
    }

    /// <summary>code lens「复制为 C#」。</summary>
    private async Task CopyAsCSharpAsync(ShellStatement statement)
    {
        (ShellCommand? command, string database) = CommandAt(statement);
        if (command is null)
        {
            return;
        }
        await Workspace.CopyAsync(CodeExport.Generate(command, database, CodeTarget.CSharp, SampleConnectionString)).ConfigureAwait(true);
        Workspace.Toast(new ToastRequest { Title = Loc.Format("Query_CodeCopied", "C#"), Kind = ToastKind.Success });
    }

    /// <summary>code lens「在管道构建器中打开」(只对 aggregate)。</summary>
    private void OpenInPipelineBuilder(ShellStatement statement)
    {
        (ShellCommand? command, string database) = CommandAt(statement);
        if (command is { Kind: ShellCommandKind.Collection, Collection: { } collection, Method.Name: "aggregate" })
        {
            Workspace.OpenPipeline(command.Database ?? database, collection, command.Method.Arg(0) as BsonArray);
        }
    }

    /// <summary>code lens 那一行该出现哪几个按钮。</summary>
    internal (bool Explainable, bool Pipeline) LensShape(ShellStatement statement)
    {
        (string? collection, string? method, _) = ShellCompletion.Head(statement.Text);
        bool explainable = collection is not null && method is "find" or "findOne" or "aggregate" or "countDocuments" or "distinct"
            or "updateOne" or "updateMany" or "deleteOne" or "deleteMany";
        return (explainable, method == "aggregate");
    }
}
