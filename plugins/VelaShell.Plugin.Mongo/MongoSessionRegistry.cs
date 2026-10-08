using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo;

/// <summary>一条开着的 MongoDB 会话(数据传输的源 / 目标候选)。</summary>
/// <param name="Name">会话名(用户给连接起的名字)。</param>
/// <param name="Connection">连接。</param>
internal sealed record MongoSessionEntry(string Name, MongoConnection Connection)
{
    /// <summary>环境标记(传输向导上的「生产 / 测试」徽章)。</summary>
    public MongoEnvironment Environment => Connection.Settings.Environment;
}

/// <summary>
/// 本插件开着的全部会话。
/// <para>
/// 数据传输要选"目标连接",而插件**读不到**宿主的会话库(那里有凭据,明令不出宿主)。
/// 所以目标只能从"本插件此刻开着的会话"里挑 —— 用户先在宿主里把目标库也连上,
/// 凭据就仍然只经宿主之手;另一条路是手填一条连接串,那是用户自己交出来的。
/// </para>
/// </summary>
internal sealed class MongoSessionRegistry
{
    /// <summary>本插件进程内唯一的一份:数据传输向导的「目标连接」从这里挑此刻开着的会话。</summary>
    public static MongoSessionRegistry Shared { get; } = new();

    private readonly Lock _gate = new();
    private readonly List<MongoSessionEntry> _entries = [];

    /// <summary>会话集合变了(开 / 关)。</summary>
    public event Action? Changed;

    /// <summary>当前全部会话(快照)。</summary>
    public IReadOnlyList<MongoSessionEntry> Snapshot()
    {
        lock (_gate)
        {
            return [.. _entries];
        }
    }

    /// <summary>登记;返回值释放即注销。</summary>
    public IDisposable Add(MongoSessionEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
        Changed?.Invoke();
        return new Registration(this, entry);
    }

    private void Remove(MongoSessionEntry entry)
    {
        lock (_gate)
        {
            _ = _entries.Remove(entry);
        }
        Changed?.Invoke();
    }

    private sealed class Registration(MongoSessionRegistry owner, MongoSessionEntry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Remove(entry);
            }
        }
    }
}
