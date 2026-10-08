using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Analysis;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 执行计划解读(设计稿 14):线性阶段流、各阶段自身耗时、丢弃数、候选计划、ESR 建议;
/// find / aggregate($cursor 包裹)/ 分片三种形态。用 MongoDB 7 的真实输出形状手写的样本,不连服务器。
/// </summary>
[TestClass]
public sealed class ExplainPlanTests
{
    /// <summary>设计稿 14 那条 find:IXSCAN status_1_createdAt_-1 → FETCH(total 过滤丢 312)→ LIMIT → PROJECTION。</summary>
    private static BsonDocument DesignFind() => BsonDocument.Parse("""
        {
          "explainVersion": "1",
          "queryPlanner": {
            "namespace": "shop.orders",
            "parsedQuery": { "$and": [ { "status": { "$eq": "paid" } }, { "createdAt": { "$gte": { "$date": "2026-08-27T00:00:00Z" } } }, { "total": { "$gte": 500 } } ] },
            "winningPlan": {
              "stage": "PROJECTION_DEFAULT", "transformBy": { "orderNo": 1, "customer.name": 1, "total": 1 },
              "inputStage": { "stage": "LIMIT", "limitAmount": 100,
                "inputStage": { "stage": "FETCH", "filter": { "total": { "$gte": 500 } },
                  "inputStage": { "stage": "IXSCAN", "keyPattern": { "status": 1, "createdAt": -1 }, "indexName": "status_1_createdAt_-1", "direction": "forward",
                    "indexBounds": { "status": [ "[\"paid\", \"paid\"]" ], "createdAt": [ "[new Date(9223372036854775807), new Date(1787788800000)]" ] } } } } },
            "rejectedPlans": [
              { "stage": "LIMIT", "inputStage": { "stage": "SORT", "inputStage": { "stage": "FETCH", "inputStage": { "stage": "IXSCAN", "indexName": "customer.id_1" } } } },
              { "stage": "LIMIT", "inputStage": { "stage": "SORT", "inputStage": { "stage": "COLLSCAN" } } }
            ]
          },
          "executionStats": {
            "executionSuccess": true, "nReturned": 100, "executionTimeMillis": 38, "totalKeysExamined": 412, "totalDocsExamined": 412,
            "executionStages": {
              "stage": "PROJECTION_DEFAULT", "nReturned": 100, "executionTimeMillisEstimate": 38, "works": 413, "isEOF": 1, "transformBy": { "orderNo": 1, "customer.name": 1, "total": 1 },
              "inputStage": { "stage": "LIMIT", "nReturned": 100, "executionTimeMillisEstimate": 36, "works": 413, "isEOF": 1, "limitAmount": 100,
                "inputStage": { "stage": "FETCH", "nReturned": 100, "executionTimeMillisEstimate": 35, "works": 412, "isEOF": 0, "docsExamined": 412, "filter": { "total": { "$gte": 500 } },
                  "inputStage": { "stage": "IXSCAN", "nReturned": 412, "executionTimeMillisEstimate": 6, "works": 412, "isEOF": 0, "keysExamined": 412, "indexName": "status_1_createdAt_-1",
                    "keyPattern": { "status": 1, "createdAt": -1 }, "direction": "forward",
                    "indexBounds": { "status": [ "[\"paid\", \"paid\"]" ], "createdAt": [ "[new Date(9223372036854775807), new Date(1787788800000)]" ] } } } } },
            "allPlansExecution": [
              { "nReturned": 100, "executionTimeMillisEstimate": 0, "totalKeysExamined": 101, "totalDocsExamined": 101, "score": 2.0003,
                "executionStages": { "stage": "PROJECTION_DEFAULT", "works": 101, "inputStage": { "stage": "LIMIT", "inputStage": { "stage": "FETCH", "inputStage": { "stage": "IXSCAN", "indexName": "status_1_createdAt_-1" } } } } },
              { "nReturned": 0, "executionTimeMillisEstimate": 0, "totalKeysExamined": 101, "totalDocsExamined": 101, "score": 1.0002,
                "executionStages": { "stage": "LIMIT", "works": 101, "inputStage": { "stage": "SORT", "inputStage": { "stage": "FETCH", "inputStage": { "stage": "IXSCAN", "indexName": "customer.id_1" } } } } },
              { "nReturned": 3, "executionTimeMillisEstimate": 0, "totalKeysExamined": 0, "totalDocsExamined": 101, "score": 1.0001,
                "executionStages": { "stage": "LIMIT", "works": 101, "inputStage": { "stage": "SORT", "inputStage": { "stage": "COLLSCAN" } } } }
            ]
          },
          "command": { "find": "orders", "filter": { "status": "paid", "total": { "$gte": 500 }, "createdAt": { "$gte": { "$date": "2026-08-27T00:00:00Z" } } },
                       "projection": { "orderNo": 1, "customer.name": 1, "total": 1 }, "sort": { "createdAt": -1 }, "limit": 100, "$db": "shop" },
          "ok": 1
        }
        """);

    [TestMethod]
    public void Find_LinearFlowIsAccessPathFirst()
    {
        var plan = ExplainPlan.Parse(DesignFind());
        Assert.AreEqual("IXSCAN,FETCH,LIMIT,PROJECTION_DEFAULT", string.Join(",", plan.Stages.Select(static s => s.Name)));
        Assert.AreEqual("IXSCAN status_1_createdAt_-1", plan.Summary);
        Assert.AreEqual("shop.orders", plan.Namespace);
        Assert.IsFalse(plan.IsAggregate);
        Assert.IsTrue(plan.HasExecutionStats);
    }

    [TestMethod]
    public void Find_OwnTimesAndDiscards()
    {
        var plan = ExplainPlan.Parse(DesignFind());
        Assert.AreEqual(6L, plan.Stages[0].OwnMs);
        Assert.AreEqual(29L, plan.Stages[1].OwnMs, "FETCH 自己的耗时 = 35 − 6");
        Assert.AreEqual(1L, plan.Stages[2].OwnMs);
        Assert.AreEqual(2L, plan.Stages[3].OwnMs);
        ExplainStage fetch = plan.Stages[1];
        Assert.AreEqual(312L, fetch.Discarded, "412 份读出、100 份留下");
        Assert.AreEqual(100d / 412, fetch.Selectivity!.Value, 1e-9);
        Assert.AreEqual(ExplainTone.Warning, fetch.Tone);
        Assert.AreEqual(ExplainTone.Good, plan.Stages[0].Tone);
        Assert.AreEqual("status_1_createdAt_-1\n{ status: [\"paid\",\"paid\"], createdAt: [MaxKey, 2026-08-27] }", plan.Stages[0].Detail);
    }

    [TestMethod]
    public void Find_SummaryMetrics()
    {
        var plan = ExplainPlan.Parse(DesignFind());
        Assert.AreEqual(38L, plan.TotalMs);
        Assert.AreEqual(100L, plan.NReturned);
        Assert.AreEqual(412L, plan.TotalKeysExamined);
        Assert.AreEqual(4.12, plan.ExaminedRatio!.Value, 1e-9);
        Assert.IsTrue(plan.SortFromIndex);
        Assert.IsFalse(plan.HasInMemorySort);
        Assert.IsFalse(plan.IsCovered);
    }

    [TestMethod]
    public void Find_CandidatesMatchedToTrialStats()
    {
        var plan = ExplainPlan.Parse(DesignFind());
        Assert.HasCount(3, plan.Candidates);
        Assert.IsTrue(plan.Candidates[0].Winner);
        Assert.AreEqual("IXSCAN status_1_createdAt_-1 → FETCH → LIMIT → PROJECTION_DEFAULT", plan.Candidates[0].Chain);
        Assert.AreEqual(2.0003, plan.Candidates[0].Score);
        Assert.AreEqual(101L, plan.Candidates[0].Works);
        Assert.AreEqual("IXSCAN customer.id_1 → FETCH → SORT → LIMIT", plan.Candidates[1].Chain);
        Assert.AreEqual(1.0002, plan.Candidates[1].Score);
        Assert.AreEqual(3L, plan.Candidates[2].NReturned);
    }

    [TestMethod]
    public void Find_EsrAdvicePutsTheRangeLast()
    {
        var plan = ExplainPlan.Parse(DesignFind());
        Assert.IsNotNull(plan.Advice);
        Assert.AreEqual("Query_AdviceFetchFilter", plan.Advice.ReasonKey);
        Assert.AreEqual("total", plan.Advice.ReasonArgument);
        Assert.AreEqual("status,createdAt,total", string.Join(",", plan.Advice.Keys.Names));
        Assert.AreEqual(-1, plan.Advice.Keys["createdAt"].AsInt32, "排序字段保留方向");
        Assert.AreEqual(100L, plan.Advice.ExpectedKeys);
    }

    [TestMethod]
    public void Aggregate_CursorWrapperPlusPipelineStages()
    {
        var explain = BsonDocument.Parse("""
            {
              "explainVersion": "1",
              "stages": [
                { "$cursor": {
                    "queryPlanner": { "namespace": "shop.orders", "winningPlan": { "stage": "PROJECTION_SIMPLE", "inputStage": { "stage": "IXSCAN", "indexName": "status_1_createdAt_-1" } }, "rejectedPlans": [] },
                    "executionStats": { "nReturned": 1284, "executionTimeMillis": 20, "totalKeysExamined": 1284, "totalDocsExamined": 1284,
                      "executionStages": { "stage": "PROJECTION_SIMPLE", "nReturned": 1284, "executionTimeMillisEstimate": 20,
                        "inputStage": { "stage": "FETCH", "nReturned": 1284, "executionTimeMillisEstimate": 18, "docsExamined": 1284,
                          "inputStage": { "stage": "IXSCAN", "nReturned": 1284, "executionTimeMillisEstimate": 5, "keysExamined": 1284, "indexName": "status_1_createdAt_-1" } } } } },
                  "nReturned": 1284, "executionTimeMillisEstimate": 22 },
                { "$group": { "_id": "$customer.id" }, "nReturned": 24, "executionTimeMillisEstimate": 100, "usedDisk": false },
                { "$sort": { "sortKey": { "sum": -1 } }, "nReturned": 24, "executionTimeMillisEstimate": 112, "usedDisk": false }
              ],
              "command": { "aggregate": "orders", "pipeline": [ { "$match": { "status": "paid" } }, { "$group": { "_id": "$customer.id" } }, { "$sort": { "sum": -1 } } ] },
              "ok": 1
            }
            """);
        var plan = ExplainPlan.Parse(explain);
        Assert.IsTrue(plan.IsAggregate);
        Assert.AreEqual("IXSCAN,FETCH,PROJECTION_SIMPLE,$group,$sort", string.Join(",", plan.Stages.Select(static s => s.Name)));
        Assert.AreEqual(24L, plan.NReturned, "聚合的返回数取最后一个管道阶段");
        Assert.AreEqual(112L, plan.TotalMs);
        Assert.AreEqual(80L, plan.Stages[3].OwnMs, "$group 自己的耗时 = 100 − 20");
        Assert.AreEqual(12L, plan.Stages[4].OwnMs);
        Assert.IsTrue(plan.HasInMemorySort, "$group 之后的 $sort 是内存排序");
        Assert.IsFalse(plan.QueryLayerSort);
        Assert.IsNull(plan.QuerySort, "$group 之后的 $sort 不参与索引建议");
        Assert.AreEqual("$sort", plan.Tree[0].Name, "树:最后一个管道阶段是根");
        Assert.AreEqual(2, plan.Tree.First(static s => s.Name == "PROJECTION_SIMPLE").Depth);
    }

    [TestMethod]
    public void CollectionScan_AdvisesAnIndex()
    {
        var explain = BsonDocument.Parse("""
            {
              "queryPlanner": { "namespace": "shop.reviews", "winningPlan": { "stage": "SORT", "sortPattern": { "at": -1 }, "inputStage": { "stage": "COLLSCAN", "filter": { "rating": { "$eq": 5 } }, "direction": "forward" } }, "rejectedPlans": [] },
              "executionStats": { "nReturned": 40, "executionTimeMillis": 3, "totalKeysExamined": 0, "totalDocsExamined": 200,
                "executionStages": { "stage": "SORT", "nReturned": 40, "executionTimeMillisEstimate": 3, "sortPattern": { "at": -1 },
                  "inputStage": { "stage": "COLLSCAN", "nReturned": 40, "executionTimeMillisEstimate": 1, "docsExamined": 200, "filter": { "rating": { "$eq": 5 } }, "direction": "forward" } } },
              "command": { "find": "reviews", "filter": { "rating": 5 }, "sort": { "at": -1 } }
            }
            """);
        var plan = ExplainPlan.Parse(explain);
        Assert.IsTrue(plan.IsCollectionScan);
        Assert.IsTrue(plan.QueryLayerSort);
        Assert.AreEqual(ExplainTone.Bad, plan.Stages[0].Tone);
        Assert.AreEqual("Query_AdviceCollScan", plan.Advice!.ReasonKey);
        Assert.AreEqual("rating,at", string.Join(",", plan.Advice.Keys.Names));
        Assert.AreEqual(5.0, plan.ExaminedRatio!.Value, 1e-9);
        Assert.HasCount(1, plan.Candidates);
    }

    [TestMethod]
    public void Sharded_TakesTheFirstShardUnderTheMerge()
    {
        var explain = BsonDocument.Parse("""
            {
              "queryPlanner": { "winningPlan": { "stage": "SINGLE_SHARD", "shards": [
                { "shardName": "rs0", "winningPlan": { "stage": "FETCH", "inputStage": { "stage": "IXSCAN", "indexName": "a_1" } }, "rejectedPlans": [] } ] } },
              "executionStats": { "nReturned": 5, "executionTimeMillis": 2, "totalKeysExamined": 5, "totalDocsExamined": 5,
                "executionStages": { "stage": "SINGLE_SHARD", "nReturned": 5, "executionTimeMillis": 2, "shards": [
                  { "shardName": "rs0", "executionStages": { "stage": "FETCH", "nReturned": 5, "executionTimeMillisEstimate": 1, "docsExamined": 5,
                    "inputStage": { "stage": "IXSCAN", "nReturned": 5, "executionTimeMillisEstimate": 0, "keysExamined": 5, "indexName": "a_1" } } } ] } },
              "command": { "find": "t", "filter": { "a": 1 } }
            }
            """);
        var plan = ExplainPlan.Parse(explain);
        Assert.AreEqual("IXSCAN,FETCH,SINGLE_SHARD", string.Join(",", plan.Stages.Select(static s => s.Name)));
        Assert.AreEqual("IXSCAN a_1", plan.Summary);
        Assert.IsNull(plan.Advice, "已经用上 a_1 索引,没有可建议的");
    }

    [TestMethod]
    public void QueryPlannerOnly_HasNoStatsButStillHasAFlow()
    {
        var explain = BsonDocument.Parse("""
            { "queryPlanner": { "winningPlan": { "stage": "COUNT_SCAN", "indexName": "status_1_createdAt_-1" }, "rejectedPlans": [] },
              "command": { "count": "orders", "query": { "status": "paid" } } }
            """);
        var plan = ExplainPlan.Parse(explain);
        Assert.IsFalse(plan.HasExecutionStats);
        Assert.HasCount(1, plan.Stages);
        Assert.IsNull(plan.TotalMs);
        Assert.IsNull(plan.ExaminedRatio);
    }
}
