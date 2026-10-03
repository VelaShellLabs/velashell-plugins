using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Secrets;
using VelaShell.PluginSdk.Storage;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Mongo.Core;

/// <summary>
/// 一条已保存的 MongoDB 连接。由插件自己管(<see cref="MongoProfileStore" />),不进宿主的会话树:
/// 工作台从命令面板打开,连接列在对象树的根上(Navicat 的习惯),新建 / 编辑走设计稿 10 的对话框。
/// <para>
/// 设置以字符串键值存(键见 <see cref="MongoSettings" /> 的常量),与 <see cref="MongoSettings.From" />
/// 同一份解析;口令不在这里 —— 它进宿主的加密密钥库(<see cref="ISecretsApi" />),按 <see cref="Id" /> 取。
/// </para>
/// </summary>
internal sealed class MongoProfile
{
    /// <summary>不变的 id(改名、改主机都不变;查询历史与收藏按它归档)。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>显示名。</summary>
    public string Name { get; set; } = "";

    /// <summary>分组(对象树里不分组显示,留给以后与导入导出用)。</summary>
    public string Group { get; set; } = "";

    /// <summary>主机(主机列表形态的第一台;SRV 是域名;连接字符串形态是整串)。</summary>
    public string Host { get; set; } = "";

    /// <summary>端口(SRV / 连接字符串形态不用)。</summary>
    public int Port { get; set; } = 27017;

    /// <summary>用户名;空 = 匿名。</summary>
    public string Username { get; set; } = "";

    /// <summary>其余设置(键见 <see cref="MongoSettings" />)。</summary>
    public Dictionary<string, string> Settings { get; set; } = [with(StringComparer.Ordinal)];

    /// <summary>最近一次连上的时间(对象树按它排,常用的在上面)。</summary>
    public DateTimeOffset? LastConnectedAt { get; set; }

    /// <summary>解析好的设置。</summary>
    public MongoSettings Parsed => MongoSettings.From(ToRequest(""));

    /// <summary>环境标记(对象树上的色点、生产连接默认只读)。</summary>
    public MongoEnvironment Environment => Parsed.Environment;

    /// <summary>拼成驱动侧的连接请求(口令另给;走跳板时再由隧道改写)。</summary>
    /// <param name="password">口令。</param>
    /// <returns>请求。</returns>
    public WorkspaceConnectRequest ToRequest(string password) => new()
    {
        SessionId = Id,
        Host = Host,
        Port = Port,
        Username = Username,
        Password = password,
        Settings = new Dictionary<string, string>(Settings, StringComparer.Ordinal),
        DisplayName = Name
    };

    /// <summary>深拷贝(对话框改的是副本,点了保存才写回)。</summary>
    /// <returns>副本。</returns>
    public MongoProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Group = Group,
        Host = Host,
        Port = Port,
        Username = Username,
        Settings = new Dictionary<string, string>(Settings, StringComparer.Ordinal),
        LastConnectedAt = LastConnectedAt
    };

    /// <summary>某个设置;没配过给 <paramref name="fallback" />。</summary>
    /// <param name="key">键。</param>
    /// <param name="fallback">缺省。</param>
    /// <returns>值。</returns>
    public string Get(string key, string fallback = "") =>
        Settings.TryGetValue(key, out string? value) ? value : fallback;

    /// <summary>写一个设置;空串表示「没配过」,直接去掉这个键(让 <see cref="MongoSettings.From" /> 用缺省)。</summary>
    /// <param name="key">键。</param>
    /// <param name="value">值。</param>
    public void Set(string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            _ = Settings.Remove(key);
        }
        else
        {
            Settings[key] = value;
        }
    }
}

/// <summary>
/// 已保存连接的读写:列表进插件存储(<see cref="IPluginStorage" />,JSON),口令进宿主的加密密钥库。
/// 与 <see cref="MongoStore" /> 不同,这里**不静默吞错**:存不下一条连接得让用户知道,而不是下次打开发现它没了。
/// </summary>
/// <param name="context">插件上下文。</param>
internal sealed class MongoProfileStore(IPluginContext context)
{
    private const string ProfilesKey = "connections";

    private static string SecretName(string id) => $"password:{id}";

    /// <summary>全部已保存的连接。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>连接列表。</returns>
    public async Task<IReadOnlyList<MongoProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        List<MongoProfile>? list = await context.Storage.GetAsync<List<MongoProfile>>(ProfilesKey, cancellationToken).ConfigureAwait(false);
        return list ?? [];
    }

    /// <summary>新建或覆盖一条(按 id)。口令为 <see langword="null" /> 表示不动已存的口令。</summary>
    /// <param name="profile">连接。</param>
    /// <param name="password">口令;空串 = 删掉已存的口令。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务。</returns>
    public async Task SaveAsync(MongoProfile profile, string? password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        List<MongoProfile> list = [.. await LoadAsync(cancellationToken).ConfigureAwait(false)];
        int index = list.FindIndex(p => p.Id == profile.Id);
        if (index >= 0)
        {
            list[index] = profile.Clone();
        }
        else
        {
            list.Add(profile.Clone());
        }
        await context.Storage.SetAsync(ProfilesKey, list, cancellationToken).ConfigureAwait(false);
        if (password is null)
        {
            return;
        }
        if (password.Length == 0)
        {
            _ = await context.Secrets.DeleteAsync(SecretName(profile.Id), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await context.Secrets.SetAsync(SecretName(profile.Id), password, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>删掉一条(连同口令)。</summary>
    /// <param name="id">连接 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务。</returns>
    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        List<MongoProfile> list = [.. await LoadAsync(cancellationToken).ConfigureAwait(false)];
        if (list.RemoveAll(p => p.Id == id) > 0)
        {
            await context.Storage.SetAsync(ProfilesKey, list, cancellationToken).ConfigureAwait(false);
        }
        _ = await context.Secrets.DeleteAsync(SecretName(id), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>取口令;没存过是空串。</summary>
    /// <param name="id">连接 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>口令。</returns>
    public async Task<string> GetPasswordAsync(string id, CancellationToken cancellationToken = default) =>
        await context.Secrets.GetAsync(SecretName(id), cancellationToken).ConfigureAwait(false) ?? "";
}
