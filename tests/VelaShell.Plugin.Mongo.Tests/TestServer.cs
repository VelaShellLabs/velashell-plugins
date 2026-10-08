using VelaShell.Plugin.Mongo.Core;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 打真实 MongoDB 的测试共用的那台服务器。
/// <para>
/// 按仓库惯例**按环境早退跳过**:本机没有 <c>127.0.0.1:27017</c> 时报 Inconclusive 而不是失败
/// (与 Redis 插件的集成测试同一口径)。地址可用环境变量 <c>VELASHELL_MONGO_TEST_URI</c> 改。
/// 测试只碰自己建的库(名字带随机后缀),收尾只删自己 —— 绝不 dropDatabase 别人的库。
/// </para>
/// </summary>
internal static class TestServer
{
    /// <summary>连接串。</summary>
    public static string Uri =>
        Environment.GetEnvironmentVariable("VELASHELL_MONGO_TEST_URI") is { Length: > 0 } uri
            ? uri
            : "mongodb://127.0.0.1:27017/?directConnection=true&serverSelectionTimeoutMS=1500";

    private static bool? _available;

    /// <summary>服务器在不在(只探一次)。</summary>
    public static async Task<bool> IsAvailableAsync()
    {
        if (_available is { } known)
        {
            return known;
        }
        try
        {
            await using MongoConnection connection = await MongoConnection.ConnectAsync(Uri, CancellationToken.None);
            _available = true;
        }
        catch (Exception)
        {
            _available = false;
        }
        return _available.Value;
    }

    /// <summary>没有服务器就 Inconclusive。</summary>
    public static async Task RequireAsync()
    {
        if (!await IsAvailableAsync())
        {
            Assert.Inconclusive($"No MongoDB at {Uri}; set VELASHELL_MONGO_TEST_URI to run this test.");
        }
    }

    /// <summary>按宿主的方式开一条连接(走 <see cref="MongoConnection.ConnectAsync(WorkspaceConnectRequest, MongoSettings, CancellationToken)" />)。</summary>
    public static async Task<MongoConnection> OpenAsync(IDictionary<string, string>? settings = null)
    {
        var url = new MongoDB.Driver.MongoUrl(Uri);
        var request = new WorkspaceConnectRequest
        {
            SessionId = "test",
            Host = url.Server.Host,
            Port = url.Server.Port,
            DisplayName = "mongo-test",
            Settings = new Dictionary<string, string>(settings ?? new Dictionary<string, string>(), StringComparer.Ordinal)
            {
                ["directConnection"] = "true"
            }
        };
        return await MongoConnection.ConnectAsync(request, MongoSettings.From(request), CancellationToken.None);
    }
}
