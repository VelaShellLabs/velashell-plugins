using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 设计稿 10 连接对话框右侧栏的数据来源(<see cref="ConnectionProbe" />):
/// 脱敏连接串的预览、按步骤的连接测试与发现的成员。
/// </summary>
[TestClass]
public sealed class ConnectionProbeTests
{
    private static readonly Loc Zh = new("zh-CN");

    private static WorkspaceConnectRequest Draft(string host, int port, string user, Dictionary<string, string> settings) => new()
    {
        SessionId = "preview",
        Host = host,
        Port = port,
        Username = user,
        Settings = settings
    };

    private static string Text(ConnectionPreview? preview) => preview?.Text ?? "";

    private static MongoProfile Profile(string host, int port, string user, Dictionary<string, string> settings) =>
        new() { Name = "probe", Host = host, Port = port, Username = user, Settings = settings };

    /// <summary>设计稿 10 那一条:副本集三台、ops_reader、默认库 shop、SCRAM-SHA-256、primaryPreferred;口令永远是 ****。</summary>
    [TestMethod]
    public void Preview_matches_the_design_and_is_a_valid_redacted_uri()
    {
        ConnectionPreview? preview = ConnectionProbe.Preview(Draft("10.20.3.21", 27017, "ops_reader", new()
        {
            ["hosts"] = "10.20.3.22:27017,10.20.3.23:27017",
            ["replicaSet"] = "rs0",
            ["database"] = "shop",
            ["authSource"] = "admin",
            ["authMechanism"] = "SCRAM-SHA-256",
            ["readPreference"] = "primaryPreferred"
        }), Zh);

        Assert.AreEqual(
            "mongodb://ops_reader:****@10.20.3.21:27017,10.20.3.22:27017,10.20.3.23:27017/shop?replicaSet=rs0&authSource=admin&authMechanism=SCRAM-SHA-256&readPreference=primaryPreferred",
            Text(preview));
        Assert.AreEqual("连接字符串", preview!.Title);
        Assert.IsTrue(preview.Spans.Any(static s => s is { Role: PreviewRole.Secret, Text: ":****@" }));
        Assert.AreEqual(3, preview.Spans.Count(static s => s.Role == PreviewRole.Host));
        Assert.AreEqual("/shop", preview.Spans.Single(static s => s.Role == PreviewRole.Path).Text);
        _ = new MongoUrl(Text(preview).Replace("****", "x", StringComparison.Ordinal));
    }

    /// <summary>没有库名时参数前补 <c>/?</c>,没有认证时不写用户段;SRV 用 mongodb+srv 且不带端口。</summary>
    [TestMethod]
    public void Preview_handles_no_database_no_user_and_srv()
    {
        Assert.AreEqual("mongodb://127.0.0.1:27017/?directConnection=true",
            Text(ConnectionProbe.Preview(Draft("127.0.0.1", 27017, "", new() { ["directConnection"] = "true" }), Zh)));
        Assert.AreEqual("mongodb+srv://cluster0.example.net",
            Text(ConnectionProbe.Preview(Draft("cluster0.example.net", 27017, "", new() { ["topology"] = "srv", ["tls"] = "true" }), Zh)));
        Assert.IsNull(ConnectionProbe.Preview(Draft(" ", 27017, "", []), Zh), "nothing to preview before a host is typed");
    }

    /// <summary>URI 形态填了一半(解析不了)时不抛,原样脱敏摆出来;填完整的照常拆开着色。</summary>
    [TestMethod]
    public void Preview_never_throws_on_a_half_typed_uri()
    {
        ConnectionPreview? half = ConnectionProbe.Preview(Draft("mongodb://ops:secret@", 0, "", new() { ["topology"] = "uri" }), Zh);
        Assert.IsNotNull(half);
        Assert.IsFalse(Text(half).Contains("secret", StringComparison.Ordinal), "a typed password never shows up, even half parsed");
        Assert.AreEqual("mongodb://ops:****@", Text(half));
        Assert.AreEqual("mongodb://ops:****",
            Text(ConnectionProbe.Preview(Draft("mongodb://ops:secr", 0, "", new() { ["topology"] = "uri" }), Zh)),
            "still typing the password, before the @");
        Assert.AreEqual("mongodb://db1:270",
            Text(ConnectionProbe.Preview(Draft("mongodb://db1:270", 0, "", new() { ["topology"] = "uri" }), Zh)),
            "a half-typed port is not a password");

        ConnectionPreview? full = ConnectionProbe.Preview(
            Draft("mongodb://10.0.0.1:27017,10.0.0.2:27017/shop?replicaSet=rs0", 0, "", new() { ["topology"] = "uri" }), Zh);
        StringAssert.StartsWith(Text(full), "mongodb://10.0.0.1:27017,10.0.0.2:27017/shop");
    }

    /// <summary>对本机 mongod 跑一遍:TCP / 认证 / hello / 权限都过,成员表里有一台主节点,结论是「连接成功 · N ms」。</summary>
    [TestMethod]
    public async Task Probe_walks_the_steps_and_lists_members()
    {
        await TestServer.RequireAsync();
        var url = new MongoUrl(TestServer.Uri);
        var provider = new MongoConnector(new VelaShell.PluginSdk.Testing.TestPluginContext(), Zh);
        var seen = new List<ProbeStep>();
        ProbeReport report = await provider.ProbeAsync(Profile(url.Server.Host, url.Server.Port, "",
            new() { ["directConnection"] = "true" }), "", new SyncProgress(seen.Add), CancellationToken.None);

        Assert.IsTrue(report.Succeeded, string.Join("; ", report.Steps.Select(static s => $"{s.Key}={s.State}:{s.Detail}")));
        CollectionAssert.AreEqual(new[] { "tcp", "auth", "hello", "privileges" }, report.Steps.Select(static s => s.Key).ToArray());
        Assert.IsTrue(report.Steps.Take(3).All(static s => s.State == ProbeState.Passed));
        StringAssert.StartsWith(report.Summary, "连接成功 · ");
        Assert.AreEqual("发现的成员", report.EndpointsTitle);
        Assert.IsTrue(report.Endpoints.Any(static e => e.Role is "PRIMARY" or "STANDALONE" && e.Tone == Tone.Success));
        Assert.IsTrue(seen.Any(static s => s is { Key: "tcp", State: ProbeState.Running }), "each step reports when it starts");
        Assert.IsTrue(seen.Count > report.Steps.Count, "progress arrives step by step, not only at the end");
    }

    /// <summary>端口不通:TCP 失败,后面的步骤一律「未执行」,结论点名失败的那一步;不抛异常。</summary>
    [TestMethod]
    public async Task Probe_reports_a_closed_port_without_throwing()
    {
        var provider = new MongoConnector(new VelaShell.PluginSdk.Testing.TestPluginContext(), Zh);
        ProbeReport report = await provider.ProbeAsync(Profile("127.0.0.1", 1, "",
            new() { ["directConnection"] = "true", ["connectTimeout"] = "1000" }), "", null, CancellationToken.None);

        Assert.IsFalse(report.Succeeded);
        Assert.AreEqual("tcp", report.FirstFailure?.Key);
        Assert.IsTrue(report.Steps.Skip(1).All(static s => s.State == ProbeState.Skipped));
        Assert.AreEqual("TCP 连接失败", report.Summary);
    }

    /// <summary>
    /// 只授了 read 的账号(种子数据里的 <c>ops_reader</c>):认证一步写出用户,权限检查一步是警告。
    /// 服务器上没有这个账号时 Inconclusive。
    /// </summary>
    [TestMethod]
    public async Task Probe_warns_about_a_read_only_user()
    {
        await TestServer.RequireAsync();
        var url = new MongoUrl(TestServer.Uri);
        var provider = new MongoConnector(new VelaShell.PluginSdk.Testing.TestPluginContext(), Zh);
        ProbeReport report = await provider.ProbeAsync(Profile(url.Server.Host, url.Server.Port, "ops_reader",
            new() { ["directConnection"] = "true", ["database"] = "shop", ["authSource"] = "admin" }), "reader-pass", null, CancellationToken.None);
        if (!report.Succeeded)
        {
            throw new AssertInconclusiveException("ops_reader is not seeded: " + report.FirstFailure?.Detail);
        }

        ProbeStep auth = report.Steps.Single(static s => s.Key == "auth");
        ProbeStep privileges = report.Steps.Single(static s => s.Key == "privileges");
        StringAssert.Contains(auth.Detail, "ops_reader@admin");
        Assert.AreEqual(ProbeState.Warning, privileges.State, privileges.Detail);
        StringAssert.Contains(privileges.Detail, "read@shop");
    }

    private sealed class SyncProgress(Action<ProbeStep> report) : IProgress<ProbeStep>
    {
        public void Report(ProbeStep value) => report(value);
    }
}
