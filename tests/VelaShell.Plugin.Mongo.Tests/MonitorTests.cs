using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Analysis;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Ui;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>服务器监控:serverStatus 解析、率值、分桶、尖峰、副本集解析,以及设计稿 11 的截图。</summary>
[TestClass]
public sealed class MonitorTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    // ── 解析与率值 ────────────────────────────────────────────────────────

    [TestMethod]
    public void Server_status_is_parsed_into_counters()
    {
        var s = ServerSample.Parse(Status(uptime: 100, query: 10, insert: 5, bytesIn: 1000), T0);
        Assert.AreEqual(128, s.Connections);
        Assert.AreEqual(384, s.ConnectionsAvailable);
        Assert.AreEqual(10, s.Query);
        Assert.AreEqual(5, s.Insert);
        Assert.AreEqual(4L << 30, s.CacheMax);
        Assert.AreEqual("WiredTiger", s.Engine);
        Assert.AreEqual("7.0", s.Fcv);
        Assert.AreEqual(3, s.IndexBuilds, "completion is counted from phases.commit, not total");
    }

    [TestMethod]
    public void Rates_are_counter_deltas_over_time()
    {
        var a = ServerSample.Parse(Status(uptime: 100, query: 100, insert: 10, bytesIn: 1000), T0);
        var b = ServerSample.Parse(Status(uptime: 102, query: 300, getmore: 20, insert: 50, bytesIn: 5000), T0.AddSeconds(2));
        var r = RateSample.Between(a, b);
        Assert.IsNotNull(r);
        Assert.AreEqual(110, r.Query, 1e-9, "query + getmore");
        Assert.AreEqual(20, r.Insert, 1e-9);
        Assert.AreEqual(2000, r.BytesIn, 1e-9);
        Assert.AreEqual(130, r.Total, 1e-9);
    }

    [TestMethod]
    public void A_restart_yields_no_rate()
    {
        var a = ServerSample.Parse(Status(uptime: 1000, query: 5000), T0);
        var b = ServerSample.Parse(Status(uptime: 3, query: 2), T0.AddSeconds(2));
        Assert.IsNull(RateSample.Between(a, b));
    }

    // ── 分桶 ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void Short_history_is_spread_over_the_bars_it_has()
    {
        var history = new SampleHistory();
        for (int i = 1; i <= 10; i++)
        {
            history.Add(Rate(T0.AddSeconds(i * 2), query: i));
        }
        BucketSet set = history.Buckets(TimeSpan.FromMinutes(15), 30, T0.AddSeconds(20), TimeSpan.FromSeconds(2));
        Assert.AreEqual(10, set.Buckets.Count, "one bar per sample while the window is still filling");
        Assert.AreEqual(2, set.BarSeconds, 1e-9);
        Assert.IsTrue(set.Covered < TimeSpan.FromMinutes(15));
        Assert.AreEqual(10, set.Buckets[^1].Query, 1e-9);
    }

    [TestMethod]
    public void A_full_window_uses_fixed_aligned_bars()
    {
        var history = new SampleHistory();
        DateTime now = T0.AddMinutes(20);
        for (DateTime t = T0; t <= now; t = t.AddSeconds(2))
        {
            history.Add(Rate(t, query: 6));
        }
        BucketSet set = history.Buckets(TimeSpan.FromMinutes(15), 30, now, TimeSpan.FromSeconds(2));
        Assert.AreEqual(30, set.Buckets.Count);
        Assert.AreEqual(30, set.BarSeconds, 1e-9, "15 minutes / 30 bars = 30 s per bar, as in the design");
        Assert.AreEqual(0, set.Buckets[0].Start.Second % 30, "bucket edges are aligned so bars do not shift each sample");
        Assert.IsTrue(set.Buckets.All(static b => b.Samples == 0 || Math.Abs(b.Query - 6) < 1e-9));
    }

    [TestMethod]
    public void History_is_trimmed_to_six_hours()
    {
        var history = new SampleHistory();
        history.Add(Rate(T0, 1));
        history.Add(Rate(T0.AddHours(7), 2));
        Assert.AreEqual(1, history.Rates.Count);
    }

    [TestMethod]
    public void A_jump_over_the_recent_mean_is_a_spike()
    {
        var history = new SampleHistory();
        for (int i = 0; i < 20; i++)
        {
            history.Add(Rate(T0.AddSeconds(i * 2), query: 100));
        }
        Assert.IsNull(history.Spike(static r => r.Total, 20));
        history.Add(Rate(T0.AddSeconds(42), query: 150));
        double? jump = history.Spike(static r => r.Total, 20);
        Assert.IsNotNull(jump);
        Assert.AreEqual(0.5, jump.Value, 1e-6);
    }

    [TestMethod]
    public void Tiny_absolute_changes_on_an_idle_server_are_not_spikes()
    {
        var history = new SampleHistory();
        for (int i = 0; i < 20; i++)
        {
            history.Add(Rate(T0.AddSeconds(i * 2), query: 1));
        }
        history.Add(Rate(T0.AddSeconds(42), query: 5));
        Assert.IsNull(history.Spike(static r => r.Total, 20), "1 → 5 ops/s is +400% but not worth an event");
    }

    // ── 副本集 ────────────────────────────────────────────────────────────

    [TestMethod]
    public void Replica_lag_is_measured_against_the_primary()
    {
        DateTime primary = T0;
        var status = new BsonDocument
        {
            { "set", "rs0" },
            { "majorityVoteCount", 2 },
            { "members", new BsonArray
                {
                    Member("10.20.3.21:27017", "PRIMARY", 1, primary, self: true),
                    Member("10.20.3.22:27017", "SECONDARY", 1, primary.AddMilliseconds(-800)),
                    Member("10.20.3.23:27017", "SECONDARY", 1, primary.AddMilliseconds(-1100))
                }
            }
        };
        var config = new BsonDocument("config", new BsonDocument("members", new BsonArray
        {
            new BsonDocument { { "host", "10.20.3.21:27017" }, { "priority", 2 }, { "votes", 1 } },
            new BsonDocument { { "host", "10.20.3.22:27017" }, { "priority", 1 }, { "votes", 1 } },
            new BsonDocument { { "host", "10.20.3.23:27017" }, { "priority", 1 }, { "votes", 1 } }
        }));
        var snapshot = ReplicaSnapshot.Parse(status, config);
        Assert.AreEqual("rs0", snapshot.SetName);
        Assert.IsTrue(snapshot.MajorityHealthy);
        Assert.AreEqual("10.20.3.21:27017", snapshot.Primary);
        Assert.IsNull(snapshot.Members[0].LagSeconds);
        Assert.AreEqual(0.8, snapshot.Members[1].LagSeconds!.Value, 1e-6);
        Assert.AreEqual(2, snapshot.Members[0].Priority, 1e-9);
        Assert.AreEqual("10.20.3.23:27017", snapshot.Slowest?.Name);
    }

    [TestMethod]
    public void Losing_two_of_three_voters_loses_the_majority()
    {
        var status = new BsonDocument
        {
            { "set", "rs0" },
            { "members", new BsonArray
                {
                    Member("a:27017", "PRIMARY", 1, T0, self: true),
                    Member("b:27017", "(not reachable/healthy)", 0, null),
                    Member("c:27017", "(not reachable/healthy)", 0, null)
                }
            }
        };
        Assert.IsFalse(ReplicaSnapshot.Parse(status, null).MajorityHealthy);
    }

    [TestMethod]
    public void Default_port_is_dropped_from_member_names()
    {
        Assert.AreEqual("10.20.3.21", MonitorFormat.Host("10.20.3.21:27017"));
        Assert.AreEqual("db1:27018", MonitorFormat.Host("db1:27018"));
    }

    // ── 真实服务器 ────────────────────────────────────────────────────────

    [TestMethod]
    public void Sampling_a_real_server_fills_cards_and_the_snapshot() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        bench.Session.OpenMonitor();
        var tab = (MonitorTabViewModel)bench.ViewModel.ActiveTab!;
        tab.IntervalOverride = TimeSpan.FromMilliseconds(250);
        await WaitForSamplesAsync(tab, 3);

        Assert.AreNotEqual("—", tab.Connections.Value);
        Assert.AreNotEqual("—", tab.Ops.Value);
        Assert.IsTrue(tab.ServerLine.StartsWith("uptime ", StringComparison.Ordinal), tab.ServerLine);
        Assert.AreEqual(4, tab.OpsSeries.Count);
        StringAssert.Contains(tab.StatusText, "serverStatus");

        BsonDocument snapshot = tab.BuildSnapshot();
        Assert.IsTrue(snapshot["serverStatus"].IsBsonDocument);
        Assert.IsTrue(snapshot["series"].AsBsonArray.Count >= 3);

        // 切走即停:不再攒采样。
        bench.ViewModel.ActiveTab = bench.ViewModel.Tabs.First(t => !ReferenceEquals(t, tab));
        await Screens.PumpAsync(20);
        int frozen = tab.SampleCount;
        await Screens.PumpAsync(60);
        Assert.AreEqual(frozen, tab.SampleCount, "sampling must stop while the tab is hidden");
    });

    [TestMethod]
    [TestCategory("Screenshots")]
    public void Board11_Monitor_renders() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using MongoConnection load = await TestServer.OpenAsync();
        string db = "velashell_monitor_" + Guid.NewGuid().ToString("N")[..8];
        using var stop = new CancellationTokenSource();
        Task? workload = null;
        try
        {
            // 自己的临时库:一批文档 + level 1 profiler + 一条真慢的查询,让事件栏有"慢查询"可数。
            IMongoCollection<BsonDocument> orders = load.Collection(db, "orders");
            await orders.InsertManyAsync(Enumerable.Range(0, 3000).Select(static i => new BsonDocument
            {
                { "n", i }, { "level", i % 10 == 0 ? "SVIP" : "VIP" }, { "total", i * 3 }
            }));
            _ = await load.RunCommandAsync(db, new BsonDocument("profile", 1));
            List<BsonDocument> slow = await orders.Find(new BsonDocument("$where", "sleep(150) || true")).Limit(1).ToListAsync();
            Assert.AreEqual(1, slow.Count);

            await using Workbench bench = await Screens.OpenWorkbenchAsync();
            if (bench.ViewModel.VisibleNodes.FirstOrDefault(n => n.Name == "orders") is { } node)
            {
                bench.ViewModel.SelectedNode = node;
            }
            bench.Session.OpenMonitor();
            var tab = (MonitorTabViewModel)bench.ViewModel.ActiveTab!;
            tab.IntervalOverride = TimeSpan.FromMilliseconds(400);
            workload = Task.Run(() => RunWorkloadAsync(load, db, stop.Token));
            await WaitForSamplesAsync(tab, 32);
            _ = await load.Collection(db, "orders").Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(new BsonDocument { { "level", 1 }, { "total", -1 } }));
            await WaitForSamplesAsync(tab, 36);
            await Screens.PumpAsync(40);

            WriteableBitmap? frame = Screens.Capture(bench.Window, "11-monitor");
            Assert.IsNotNull(frame);
            Assert.IsTrue(tab.SampleCount >= 15);
            Assert.IsTrue(tab.OpsSeries[0].Values.Count >= 15, "one bar per sample while the window fills");
            Assert.IsTrue(tab.Events.Any(e => e.Kind == "slow"), "the slow query in our temp database shows up as an event");
            Assert.IsTrue(tab.HasMembers, "the local test server is a one-member replica set");

            // 大窗口(用户报的 2000 × 1370 那种):两行图表平分高度,存储 Top 的集合名列宽够放下最长的名字,不被省略。
            bench.Window.Width = 1990;
            bench.Window.Height = 1360;
            await Screens.PumpAsync(40);
            _ = Screens.Capture(bench.Window, "11-monitor-large");
            MonitorTabView view = bench.Window.GetVisualDescendants().OfType<MonitorTabView>().First(static v => v.IsEffectivelyVisible);
            BarChart[] charts = [.. view.GetVisualDescendants().OfType<BarChart>()];
            Assert.HasCount(2, charts);
            Assert.IsLessThan(40, Math.Abs(charts[0].Bounds.Height - charts[1].Bounds.Height), "the two chart rows share the height");
            Grid[] storageRows = [.. view.GetVisualDescendants().OfType<Grid>().Where(static g => TableColumns.GetRow(g) == "storage")];
            Assert.IsNotEmpty(storageRows);
            foreach (Grid row in storageRows)
            {
                TextBlock name = row.Children.OfType<TextBlock>().First();
                Assert.IsFalse(name.TextLayout.TextLines.Any(static l => l.HasCollapsed), $"collection name trimmed: {tab.Storage[0].Name}");
            }
        }
        finally
        {
            await stop.CancelAsync();
            if (workload is not null)
            {
                await workload;
            }
            await load.Client.DropDatabaseAsync(db);
        }
    });

    /// <summary>泵消息直到攒够 <paramref name="count" /> 个率值(至多 60 秒)。</summary>
    internal static async Task WaitForSamplesAsync(MonitorTabViewModel tab, int count)
    {
        var watch = Stopwatch.StartNew();
        while (tab.SampleCount < count && watch.Elapsed < TimeSpan.FromSeconds(60))
        {
            await Screens.PumpAsync(10);
        }
        Assert.IsTrue(tab.SampleCount >= count, $"only {tab.SampleCount} samples after {watch.Elapsed}");
    }

    /// <summary>
    /// 给临时库一段起伏的真实负载(查多写少,隔一阵来一波),让操作 / 秒的堆叠柱有形状可看。
    /// </summary>
    private static async Task RunWorkloadAsync(MongoConnection connection, string db, CancellationToken token)
    {
        IMongoCollection<BsonDocument> orders = connection.Collection(db, "orders");
        var random = new Random(11);
        int round = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                round++;
                int burst = round % 9 == 0 ? 3 : 1;
                for (int i = 0; i < 12 * burst; i++)
                {
                    _ = await orders.Find(new BsonDocument("n", random.Next(3000))).FirstOrDefaultAsync(token);
                }
                for (int i = 0; i < 2 * burst; i++)
                {
                    await orders.InsertOneAsync(new BsonDocument { { "n", 10_000 + random.Next(1000) }, { "level", "NEW" }, { "total", 1 } }, cancellationToken: token);
                }
                _ = await orders.UpdateOneAsync(new BsonDocument("n", random.Next(3000)), new BsonDocument("$inc", new BsonDocument("total", 1)), cancellationToken: token);
                if (round % 3 == 0)
                {
                    _ = await orders.DeleteOneAsync(new BsonDocument("level", "NEW"), token);
                }
                await Task.Delay(40, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static BsonDocument Status(long uptime, long query = 0, long getmore = 0, long insert = 0, long bytesIn = 0) => new()
    {
        { "uptime", uptime },
        { "connections", new BsonDocument { { "current", 128 }, { "available", 384 } } },
        { "opcounters", new BsonDocument { { "query", query }, { "getmore", getmore }, { "insert", insert }, { "update", 0L }, { "delete", 0L }, { "command", 9L } } },
        { "network", new BsonDocument { { "bytesIn", bytesIn }, { "bytesOut", 0L } } },
        { "storageEngine", new BsonDocument("name", "wiredTiger") },
        { "featureCompatibilityVersion", new BsonDocument { { "major", 7 }, { "minor", 0 } } },
        { "indexBuilds", new BsonDocument { { "total", 5 }, { "phases", new BsonDocument("commit", 3) } } },
        { "wiredTiger", new BsonDocument("cache", new BsonDocument
            {
                { "maximum bytes configured", 4L << 30 },
                { "bytes currently in the cache", 3L << 30 },
                { "tracked dirty bytes in the cache", 1L << 20 },
                { "unmodified pages evicted", 10L },
                { "modified pages evicted", 2L }
            })
        }
    };

    private static RateSample Rate(DateTime t, double query) => new(t, query, 0, 0, 0, 0, 0, 0, 10);

    private static BsonDocument Member(string name, string state, int health, DateTime? applied, bool self = false)
    {
        var doc = new BsonDocument { { "name", name }, { "stateStr", state }, { "health", health } };
        if (applied is { } a)
        {
            doc["lastAppliedWallTime"] = a;
        }
        if (self)
        {
            doc["self"] = true;
        }
        else
        {
            doc["lastHeartbeat"] = T0.AddSeconds(-2);
        }
        return doc;
    }
}
