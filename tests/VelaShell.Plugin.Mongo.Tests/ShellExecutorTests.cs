using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Analysis;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Shell;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 执行器打真实服务器:每个测试自己建一个 <c>velashell_query_*</c> 库,收尾删掉 —— 不碰 shop。
/// 守的是"语句 → 驱动调用"这一层:修饰链、结果上限、写前护栏、explain、按 comment 终止。
/// </summary>
[TestClass]
public sealed class ShellExecutorTests
{
    private sealed class StubGuard : IShellGuard
    {
        public bool Writable { get; set; } = true;

        public bool Answer { get; set; } = true;

        public bool ConfirmWrites { get; set; } = true;

        public bool DisableDropDatabase { get; set; } = true;

        public List<ShellConfirmation> Asked { get; } = [];

        public bool EnsureWritable(string database) => Writable;

        public Task<bool> ConfirmAsync(ShellConfirmation confirmation)
        {
            Asked.Add(confirmation);
            return Task.FromResult(Answer);
        }
    }

    private static async Task<(MongoConnection Connection, string Database)> OpenAsync()
    {
        await TestServer.RequireAsync();
        MongoConnection connection = await TestServer.OpenAsync();
        string database = "velashell_query_" + Guid.NewGuid().ToString("N")[..8];
        return (connection, database);
    }

    private static async Task<ShellResult> RunAsync(ShellExecutor executor, ShellSession session, string text, ShellRunOptions? options = null)
    {
        ShellCommand command = ShellParser.Parse(ShellScript.Split(text)[0]);
        return await executor.ExecuteAsync(command, session, options ?? new ShellRunOptions { MaxTimeMs = 10_000 });
    }

    [TestMethod]
    public async Task Execute_ReadAndWriteRoundTrip()
    {
        (MongoConnection connection, string database) = await OpenAsync();
        await using (connection)
        {
            try
            {
                var guard = new StubGuard { ConfirmWrites = false };
                var executor = new ShellExecutor(connection, guard, new Loc("zh-CN"));
                var session = new ShellSession(database);

                ShellResult inserted = await RunAsync(executor, session,
                    "db.orders.insertMany([{ n: 1, s: \"paid\", tags: [\"a\"] }, { n: 2, s: \"paid\" }, { n: 3, s: \"open\" }, { n: 4, s: \"paid\" }])");
                Assert.AreEqual(ShellResultKind.Documents, inserted.Kind);
                Assert.HasCount(4, inserted.Documents[0]["insertedIds"].AsBsonArray);

                ShellResult find = await RunAsync(executor, session, "db.orders.find({ s: \"paid\" }, { _id: 0, n: 1 }).sort({ n: -1 }).skip(1).limit(2)");
                Assert.HasCount(2, find.Documents);
                Assert.AreEqual(2, find.Documents[0]["n"].AsInt32);
                Assert.AreEqual(1, find.Documents[1]["n"].AsInt32);
                Assert.IsFalse(find.Documents[0].Contains("_id"));
                Assert.IsNotNull(find.Query);
                Assert.AreEqual(2, find.Query.Limit);

                ShellResult count = await RunAsync(executor, session, "db.orders.find({ s: \"paid\" }).count()");
                Assert.AreEqual(3L, count.Documents[0]["count"].ToInt64());
                ShellResult countDocuments = await RunAsync(executor, session, "db.orders.countDocuments({ n: { $gt: 1 } })");
                Assert.AreEqual(3L, countDocuments.Documents[0]["count"].ToInt64());
                ShellResult distinct = await RunAsync(executor, session, "db.orders.distinct(\"s\")");
                Assert.HasCount(2, distinct.Documents);

                ShellResult aggregate = await RunAsync(executor, session,
                    "db.orders.aggregate([{ $group: { _id: \"$s\", c: { $sum: 1 } } }, { $sort: { c: -1 } }])");
                Assert.AreEqual("paid", aggregate.Documents[0]["_id"].AsString);
                Assert.AreEqual(3, aggregate.Documents[0]["c"].AsInt32);
                Assert.IsNotNull(aggregate.Pipeline);

                ShellResult update = await RunAsync(executor, session, "db.orders.updateMany({ s: \"paid\" }, { $set: { flag: true } })");
                Assert.AreEqual(3L, update.Documents[0]["modifiedCount"].ToInt64());
                ShellResult findOne = await RunAsync(executor, session, "db.orders.findOneAndUpdate({ n: 3 }, { $inc: { n: 10 } }, { returnNewDocument: true })");
                Assert.AreEqual(13, findOne.Documents[0]["n"].AsInt32);
                ShellResult deleted = await RunAsync(executor, session, "db.orders.deleteOne({ n: 13 })");
                Assert.AreEqual(1L, deleted.Documents[0]["deletedCount"].ToInt64());

                ShellResult index = await RunAsync(executor, session, "db.orders.createIndex({ s: 1, n: -1 })");
                Assert.AreEqual("Documents", index.Kind.ToString());
                ShellResult indexes = await RunAsync(executor, session, "db.orders.getIndexes()");
                Assert.IsTrue(indexes.Documents.Any(static d => d["name"] == "s_1_n_-1"));

                ShellResult show = await RunAsync(executor, session, "show collections");
                Assert.IsTrue(show.Documents.Any(static d => d["name"] == "orders"));
                ShellResult names = await RunAsync(executor, session, "db.getCollectionNames()");
                Assert.HasCount(1, names.Documents);
                ShellResult ping = await RunAsync(executor, session, "db.runCommand({ ping: 1 })");
                Assert.AreEqual(1.0, ping.Documents[0]["ok"].ToDouble());

                ShellResult use = await RunAsync(executor, session, "use(\"velashell_other\")");
                Assert.AreEqual(ShellResultKind.Message, use.Kind);
                Assert.AreEqual("velashell_other", session.Database);
            }
            finally
            {
                await connection.Client.DropDatabaseAsync(database);
            }
        }
    }

    [TestMethod]
    public async Task Execute_CapsAtOneThousandDocuments()
    {
        (MongoConnection connection, string database) = await OpenAsync();
        await using (connection)
        {
            try
            {
                await connection.Collection(database, "many").InsertManyAsync(Enumerable.Range(0, 1005).Select(static i => new BsonDocument("i", i)));
                var executor = new ShellExecutor(connection, new StubGuard(), new Loc("zh-CN"));
                var session = new ShellSession(database);

                ShellResult all = await RunAsync(executor, session, "db.many.find({})");
                Assert.HasCount(1000, all.Documents);
                Assert.IsTrue(all.Truncated, "超过上限要标出来,界面据此提示「只取前 1000 份」");

                ShellResult limited = await RunAsync(executor, session, "db.many.find({}).limit(20)");
                Assert.HasCount(20, limited.Documents);
                Assert.IsFalse(limited.Truncated);

                ShellResult pipeline = await RunAsync(executor, session, "db.many.aggregate([{ $match: {} }])");
                Assert.HasCount(1000, pipeline.Documents);
                Assert.IsTrue(pipeline.Truncated);
            }
            finally
            {
                await connection.Client.DropDatabaseAsync(database);
            }
        }
    }

    [TestMethod]
    public async Task Execute_GuardBlocksAndConfirms()
    {
        (MongoConnection connection, string database) = await OpenAsync();
        await using (connection)
        {
            try
            {
                IMongoCollection<BsonDocument> items = connection.Collection(database, "items");
                await items.InsertManyAsync([new BsonDocument("k", 1), new BsonDocument("k", 1), new BsonDocument("k", 2)]);
                var guard = new StubGuard { Writable = false };
                var executor = new ShellExecutor(connection, guard, new Loc("zh-CN"));
                var session = new ShellSession(database);

                ShellResult blocked = await RunAsync(executor, session, "db.items.insertOne({ k: 3 })");
                Assert.IsTrue(blocked.Declined, "只读时写操作必须被拦下");
                long afterBlocked = await items.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
                Assert.AreEqual(3L, afterBlocked);

                guard.Writable = true;
                guard.Answer = false;
                ShellResult declined = await RunAsync(executor, session, "db.items.deleteMany({ k: 1 })");
                Assert.IsTrue(declined.Declined);
                Assert.HasCount(1, guard.Asked);
                Assert.AreEqual(ShellConfirmKind.DeleteMany, guard.Asked[0].Kind);
                Assert.AreEqual(2L, guard.Asked[0].Affected, "确认框里要写清会删几份");
                long afterDeclined = await items.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
                Assert.AreEqual(3L, afterDeclined);

                guard.Answer = true;
                ShellResult deleted = await RunAsync(executor, session, "db.items.deleteMany({ k: 1 })");
                Assert.AreEqual(2L, deleted.Documents[0]["deletedCount"].ToInt64());

                ShellResult deleteOne = await RunAsync(executor, session, "db.items.deleteOne({ k: 2 })");
                Assert.AreEqual(1L, deleteOne.Documents[0]["deletedCount"].ToInt64());
                Assert.HasCount(2, guard.Asked, "deleteOne 不需要确认");

                ShellResult drop = await RunAsync(executor, session, "db.items.drop()");
                Assert.AreEqual(ShellConfirmKind.Drop, guard.Asked[^1].Kind);
                Assert.IsTrue(drop.ChangesCatalog);

                _ = await Assert.ThrowsExactlyAsync<ShellExecutionException>(() => RunAsync(executor, session, "db.dropDatabase()"));
            }
            finally
            {
                await connection.Client.DropDatabaseAsync(database);
            }
        }
    }

    [TestMethod]
    public async Task Explain_FindAndAggregateParse()
    {
        (MongoConnection connection, string database) = await OpenAsync();
        await using (connection)
        {
            try
            {
                IMongoCollection<BsonDocument> orders = connection.Collection(database, "orders");
                await orders.InsertManyAsync(Enumerable.Range(0, 300).Select(static i => new BsonDocument
                {
                    { "status", i % 3 == 0 ? "paid" : "open" },
                    { "total", (299 - i) * 10 },
                    { "createdAt", new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i) }
                }));
                _ = await orders.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument { { "status", 1 }, { "createdAt", -1 } }));
                var executor = new ShellExecutor(connection, new StubGuard(), new Loc("zh-CN"));
                ShellCommand find = ShellParser.Parse(ShellScript.Split(
                    "db.orders.find({ status: \"paid\", total: { $gte: 500 } }).sort({ createdAt: -1 }).limit(20)")[0]);

                ShellResult result = await executor.ExplainAsync(find, database, "allPlansExecution", new ShellRunOptions { MaxTimeMs = 10_000 }, null);
                Assert.AreEqual(ShellResultKind.Explain, result.Kind);
                var plan = ExplainPlan.Parse(result.Explain!);
                Assert.AreEqual("IXSCAN", plan.Stages[0].Name);
                Assert.AreEqual("status_1_createdAt_-1", plan.Stages[0].IndexName);
                Assert.IsTrue(plan.SortFromIndex, "排序由索引提供");
                Assert.IsTrue(plan.Stages.Any(static s => s.Name == "FETCH" && s.Discarded > 0));
                Assert.AreEqual(20L, plan.NReturned);
                Assert.IsNotNull(plan.Advice);
                Assert.AreEqual("{ \"status\" : 1, \"createdAt\" : -1, \"total\" : 1 }", plan.Advice.Keys.ToString());
                Assert.IsTrue(plan.Candidates[0].Winner);

                ShellResult hinted = await executor.ExplainAsync(find, database, "executionStats", new ShellRunOptions(), new BsonString("_id_"));
                var hintedPlan = ExplainPlan.Parse(hinted.Explain!);
                Assert.IsTrue(hintedPlan.Stages.Any(static s => s.IndexName == "_id_"), "hint 要落到 explain 命令里");

                ShellCommand aggregate = ShellParser.Parse(ShellScript.Split(
                    "db.orders.aggregate([{ $match: { status: \"paid\" } }, { $group: { _id: \"$status\", n: { $sum: 1 } } }])")[0]);
                ShellResult aggregateResult = await executor.ExplainAsync(aggregate, database, "executionStats", new ShellRunOptions(), null);
                var aggregatePlan = ExplainPlan.Parse(aggregateResult.Explain!);
                Assert.IsTrue(aggregatePlan.IsAggregate);
                Assert.IsTrue(aggregatePlan.Stages.Any(static s => s.Name is "IXSCAN" or "COLLSCAN"));
                Assert.IsTrue(aggregatePlan.Stages.Any(static s => s.Name.Contains("GROUP", StringComparison.OrdinalIgnoreCase)));
            }
            finally
            {
                await connection.Client.DropDatabaseAsync(database);
            }
        }
    }

    [TestMethod]
    public async Task Stop_KillsTheTaggedOperationOnTheServer()
    {
        (MongoConnection connection, string database) = await OpenAsync();
        await using (connection)
        {
            try
            {
                await connection.Collection(database, "slow").InsertManyAsync(Enumerable.Range(0, 5).Select(static i => new BsonDocument("i", i)));
                var executor = new ShellExecutor(connection, new StubGuard(), new Loc("zh-CN"));
                const string comment = "velashell-query-test-kill";
                ShellCommand slow = ShellParser.Parse(ShellScript.Split("db.slow.find({ $where: \"sleep(1500) || true\" })")[0]);
                Task<ShellResult> running = executor.ExecuteAsync(slow, new ShellSession(database), new ShellRunOptions { Comment = comment });

                int killed = 0;
                for (int attempt = 0; attempt < 40 && killed == 0; attempt++)
                {
                    await Task.Delay(100);
                    killed = await connection.KillByCommentAsync(comment);
                }
                Assert.IsGreaterThan(0, killed, "comment 标记要能在 currentOp 里找到这条查询");
                _ = await Assert.ThrowsAsync<MongoException>(() => running);
            }
            finally
            {
                await connection.Client.DropDatabaseAsync(database);
            }
        }
    }
}
