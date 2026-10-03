using Avalonia.Threading;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Shell;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 查询编辑器的写护栏:把执行器的 <see cref="IShellGuard" /> 接到外壳的 <c>EnsureWritable</c> 与确认框上。
/// <para>
/// 执行器在后台线程上跑,而确认框与提示都要上 UI 线程 —— 这里统一封送,执行器那边不必知道线程这回事。
/// 批量更新 / 删除的确认框里写清命名空间、条件与预计影响的文档数;<c>drop</c> 与 <c>dropDatabase</c> 要手打名称
/// (设计稿 22「保护与确认」)。
/// </para>
/// </summary>
internal sealed class QueryGuard(IMongoWorkspace workspace) : IShellGuard
{
    /// <inheritdoc />
    public bool ConfirmWrites => workspace.Guard.ConfirmWrites;

    /// <inheritdoc />
    public bool DisableDropDatabase => workspace.Guard.DisableDropDatabase;

    /// <inheritdoc />
    public bool EnsureWritable(string database) =>
        Dispatcher.UIThread.CheckAccess()
            ? workspace.EnsureWritable(database)
            : Dispatcher.UIThread.InvokeAsync(() => workspace.EnsureWritable(database)).GetAwaiter().GetResult();

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(ShellConfirmation confirmation)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return await workspace.ConfirmAsync(Request(confirmation)).ConfigureAwait(true);
        }
        return await Dispatcher.UIThread.InvokeAsync(() => workspace.ConfirmAsync(Request(confirmation)));
    }

    private ConfirmRequest Request(ShellConfirmation c)
    {
        Loc loc = workspace.Loc;
        string ns = c.Collection is null ? c.Database : $"{c.Database}.{c.Collection}";
        var facts = new List<ConfirmFact> { new(loc["Query_ConfirmNamespace"], ns) };
        if (c.Filter is { } filter)
        {
            facts.Add(new ConfirmFact(loc["Query_ConfirmFilter"], Clip(BsonText.Literal(filter), 60)));
        }
        if (c.Affected is { } affected)
        {
            facts.Add(new ConfirmFact(loc["Query_ConfirmAffected"], BsonText.Grouped(affected)));
        }
        if (c.Detail is { } detail)
        {
            facts.Add(new ConfirmFact(loc["Query_ConfirmIndex"], detail));
        }
        return c.Kind switch
        {
            ShellConfirmKind.UpdateMany => new ConfirmRequest
            {
                Title = loc["Query_ConfirmUpdateManyTitle"],
                Message = loc.Format("Query_ConfirmUpdateManyBody", ns),
                ConfirmLabel = loc["Query_ConfirmRun"],
                IconKey = "Mongo.pencil-line",
                Facts = facts,
                TypeToConfirm = workspace.Guard.IsProduction ? c.Collection : null
            },
            ShellConfirmKind.DeleteMany => new ConfirmRequest
            {
                Title = loc["Query_ConfirmDeleteManyTitle"],
                Message = loc.Format("Query_ConfirmDeleteManyBody", ns),
                ConfirmLabel = loc["Query_ConfirmDelete"],
                Facts = facts,
                TypeToConfirm = workspace.Guard.IsProduction ? c.Collection : null
            },
            ShellConfirmKind.BulkWrite => new ConfirmRequest
            {
                Title = loc["Query_ConfirmBulkTitle"],
                Message = loc.Format("Query_ConfirmBulkBody", ns, c.Affected ?? 0),
                ConfirmLabel = loc["Query_ConfirmRun"],
                IconKey = "Mongo.pencil-line",
                Facts = [facts[0]]
            },
            ShellConfirmKind.Drop => new ConfirmRequest
            {
                Title = loc["Query_ConfirmDropTitle"],
                Message = loc.Format("Query_ConfirmDropBody", ns),
                ConfirmLabel = loc["Query_ConfirmDropLabel"],
                Facts = c.Affected is { } docs ? [facts[0], new ConfirmFact(loc["Query_ConfirmDocs"], BsonText.Grouped(docs))] : [facts[0]],
                TypeToConfirm = c.Collection
            },
            ShellConfirmKind.DropIndex => new ConfirmRequest
            {
                Title = loc["Query_ConfirmDropIndexTitle"],
                Message = loc.Format("Query_ConfirmDropIndexBody", c.Detail ?? "", ns),
                ConfirmLabel = loc["Query_ConfirmDelete"],
                Facts = facts
            },
            _ => new ConfirmRequest
            {
                Title = loc["Query_ConfirmDropDatabaseTitle"],
                Message = loc.Format("Query_ConfirmDropDatabaseBody", c.Database),
                ConfirmLabel = loc["Query_ConfirmDropLabel"],
                Facts = [facts[0]],
                TypeToConfirm = c.Database
            }
        };
    }

    /// <summary>长文本截短(确认框的事实行只有一行宽)。</summary>
    internal static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
