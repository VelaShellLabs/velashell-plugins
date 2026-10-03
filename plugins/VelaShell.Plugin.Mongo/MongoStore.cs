using System.Globalization;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.TimeSeries;

namespace VelaShell.Plugin.Mongo;

/// <summary>一条查询历史。</summary>
/// <param name="At">执行时间。</param>
/// <param name="Database">执行时的库。</param>
/// <param name="Text">语句原文。</param>
/// <param name="ElapsedMs">耗时。</param>
/// <param name="Ok">是否成功。</param>
internal sealed record QueryHistoryEntry(DateTimeOffset At, string Database, string Text, long ElapsedMs, bool Ok);

/// <summary>一项保存的东西(查询、管道、导入 / 导出配置)。</summary>
/// <param name="Name">名字。</param>
/// <param name="Content">内容原文。</param>
/// <param name="SavedAt">保存时间。</param>
internal sealed record SavedItem(string Name, string Content, DateTimeOffset SavedAt);

/// <summary>
/// 插件私有的持久化:筛选历史与收藏、查询历史、保存的查询 / 管道 / 导入导出配置。
/// <para>
/// 查询历史落时序库不只是"下次还能翻到" —— 它同时回答了"谁在什么时候对生产库跑了什么",
/// 与 Redis 插件的控制台历史同一个考虑。其余的小状态走 <c>Storage</c>(JSON 键值)。
/// </para>
/// <para>
/// 全部方法**对不可用的后端静默降级**:headless 宿主(单测)没有数据库 —— 那时历史只在本次会话内有效,
/// 而不是让面板打不开。
/// </para>
/// </summary>
/// <param name="context">插件上下文。</param>
internal sealed class MongoStore(IPluginContext context)
{
    private const string HistoryMeasurement = "query_history";
    private const int HistoryLimit = 300;
    private const int FilterHistoryLimit = 30;

    private readonly TimeSeriesClock _clock = new();
    private ITimeSeries? _history;
    private bool _historyUnavailable;

    /// <summary>读一个键(JSON 反序列化);失败或缺失返回 <see langword="default" />。</summary>
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            return await context.Storage.GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.Log.Info($"Reading '{key}' failed: {ex.Message}");
            return default;
        }
    }

    /// <summary>写一个键;失败只记日志 —— 存不下历史不该影响正在做的事。</summary>
    public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        try
        {
            await context.Storage.SetAsync(key, value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.Log.Info($"Saving '{key}' failed: {ex.Message}");
        }
    }

    /// <summary>某个命名空间的筛选历史(新的在前)。</summary>
    public async Task<IReadOnlyList<string>> LoadFilterHistoryAsync(string connectionKey, string ns) =>
        await GetAsync<string[]>(Key("filters", connectionKey, ns)).ConfigureAwait(false) ?? [];

    /// <summary>记一条筛选历史(去重、置顶、截断)。</summary>
    public async Task AddFilterHistoryAsync(string connectionKey, string ns, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter.Trim() == "{}")
        {
            return;
        }
        IReadOnlyList<string> existing = await LoadFilterHistoryAsync(connectionKey, ns).ConfigureAwait(false);
        string[] next = [filter, .. existing.Where(f => f != filter).Take(FilterHistoryLimit - 1)];
        await SetAsync(Key("filters", connectionKey, ns), next).ConfigureAwait(false);
    }

    /// <summary>某个命名空间收藏的筛选。</summary>
    public async Task<IReadOnlyList<string>> LoadFilterFavoritesAsync(string connectionKey, string ns) =>
        await GetAsync<string[]>(Key("favorites", connectionKey, ns)).ConfigureAwait(false) ?? [];

    /// <summary>保存收藏的筛选。</summary>
    public Task SaveFilterFavoritesAsync(string connectionKey, string ns, IReadOnlyList<string> filters) =>
        SetAsync(Key("favorites", connectionKey, ns), filters.ToArray());

    /// <summary>某类保存项(<c>query</c> / <c>pipeline</c> / <c>export</c> / <c>import</c>)。</summary>
    public async Task<IReadOnlyList<SavedItem>> LoadSavedAsync(string kind, string connectionKey) =>
        await GetAsync<SavedItem[]>(Key("saved-" + kind, connectionKey, "")).ConfigureAwait(false) ?? [];

    /// <summary>保存一项(同名覆盖)。</summary>
    public async Task SaveItemAsync(string kind, string connectionKey, SavedItem item)
    {
        IReadOnlyList<SavedItem> existing = await LoadSavedAsync(kind, connectionKey).ConfigureAwait(false);
        SavedItem[] next = [item, .. existing.Where(i => i.Name != item.Name)];
        await SetAsync(Key("saved-" + kind, connectionKey, ""), next).ConfigureAwait(false);
    }

    /// <summary>追加一条查询历史。</summary>
    public async Task AppendQueryHistoryAsync(string connectionKey, QueryHistoryEntry entry)
    {
        if (await OpenHistoryAsync().ConfigureAwait(false) is not { } series)
        {
            return;
        }
        try
        {
            await series.WriteAsync(new(
                _clock.Next(),
                new Dictionary<string, string> { ["conn"] = connectionKey, ["db"] = entry.Database },
                new Dictionary<string, TimeSeriesValue>
                {
                    ["text"] = TimeSeriesValue.FromText(entry.Text),
                    ["ms"] = TimeSeriesValue.FromInteger(entry.ElapsedMs),
                    ["ok"] = TimeSeriesValue.FromFlag(entry.Ok)
                })).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.Log.Info($"Appending query history failed: {ex.Message}");
        }
    }

    /// <summary>读回查询历史(新的在前)。</summary>
    public async Task<IReadOnlyList<QueryHistoryEntry>> LoadQueryHistoryAsync(string connectionKey)
    {
        if (await OpenHistoryAsync().ConfigureAwait(false) is not { } series)
        {
            return [];
        }
        try
        {
            IReadOnlyList<TimeSeriesPoint> points = await series.QueryAsync(new()
            {
                Tags = new Dictionary<string, string> { ["conn"] = connectionKey },
                Descending = true,
                Limit = HistoryLimit
            }).ConfigureAwait(false);
            return
            [
                .. points.Select(static p => new QueryHistoryEntry(
                    p.Timestamp,
                    p.Tag("db") ?? "",
                    p.Text("text") ?? "",
                    p.Integer("ms", 0),
                    !p.Fields.TryGetValue("ok", out TimeSeriesValue ok) || ok.AsFlag()))
            ];
        }
        catch (Exception ex)
        {
            context.Log.Info($"Reading query history failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>惰性打开时序表。**只试一次** —— headless 宿主上它每次都会抛。</summary>
    private async Task<ITimeSeries?> OpenHistoryAsync()
    {
        if (_history is not null)
        {
            return _history;
        }
        if (_historyUnavailable)
        {
            return null;
        }
        try
        {
            _history = await context.TimeSeries.OpenAsync(new(HistoryMeasurement,
            [
                TimeSeriesColumn.Tag("conn"),
                TimeSeriesColumn.Tag("db"),
                TimeSeriesColumn.Field("text", TimeSeriesValueKind.Text),
                TimeSeriesColumn.Field("ms", TimeSeriesValueKind.Integer),
                TimeSeriesColumn.Field("ok", TimeSeriesValueKind.Flag)
            ])).ConfigureAwait(false);
            return _history;
        }
        catch (Exception ex)
        {
            _historyUnavailable = true;
            context.Log.Info($"Query history is not persisted: {ex.Message}");
            return null;
        }
    }

    private static string Key(string kind, string connectionKey, string ns) =>
        string.Create(CultureInfo.InvariantCulture, $"{kind}:{connectionKey}:{ns}");
}
