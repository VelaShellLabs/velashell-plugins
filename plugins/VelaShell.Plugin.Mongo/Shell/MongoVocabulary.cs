namespace VelaShell.Plugin.Mongo.Shell;

/// <summary>词条的种类(决定补全图标与颜色,与设计稿 09「补全项类型」一致)。</summary>
public enum VocabularyKind
{
    /// <summary>聚合阶段(<c>layers</c>,强调色)。</summary>
    Stage,

    /// <summary>查询运算符(<c>sigma</c>,洋红)。</summary>
    QueryOperator,

    /// <summary>更新运算符。</summary>
    UpdateOperator,

    /// <summary>聚合表达式 / 累加器。</summary>
    Expression,

    /// <summary>集合方法(<c>parentheses</c>)。</summary>
    Method,

    /// <summary>游标方法。</summary>
    CursorMethod,

    /// <summary>代码片段(<c>square-code</c>)。</summary>
    Snippet
}

/// <summary>一条词汇:名字、种类、两种语言的一句说明、语法示例、插入文本。</summary>
/// <param name="Name">名字(<c>$sort</c>、<c>aggregate</c>)。</param>
/// <param name="Kind">种类。</param>
/// <param name="En">英文说明。</param>
/// <param name="Zh">中文说明。</param>
/// <param name="Syntax">语法示例。</param>
/// <param name="Insert">插入文本(<c>|</c> 是光标落点);缺省按种类拼。</param>
public sealed record VocabularyEntry(string Name, VocabularyKind Kind, string En, string Zh, string Syntax, string? Insert = null)
{
    /// <summary>按语言取说明。</summary>
    public string Describe(bool chinese) => chinese ? Zh : En;

    /// <summary>官方文档地址。</summary>
    public string DocsUrl => Kind switch
    {
        VocabularyKind.Stage => $"https://www.mongodb.com/docs/manual/reference/operator/aggregation/{Name.TrimStart('$')}/",
        VocabularyKind.QueryOperator => $"https://www.mongodb.com/docs/manual/reference/operator/query/{Name.TrimStart('$')}/",
        VocabularyKind.UpdateOperator => $"https://www.mongodb.com/docs/manual/reference/operator/update/{Name.TrimStart('$')}/",
        VocabularyKind.Expression => $"https://www.mongodb.com/docs/manual/reference/operator/aggregation/{Name.TrimStart('$')}/",
        VocabularyKind.Method => $"https://www.mongodb.com/docs/manual/reference/method/db.collection.{Name}/",
        VocabularyKind.CursorMethod => $"https://www.mongodb.com/docs/manual/reference/method/cursor.{Name}/",
        _ => "https://www.mongodb.com/docs/mongodb-shell/"
    };

    /// <summary>补全时插入的文本。</summary>
    public string InsertText => Insert ?? Kind switch
    {
        VocabularyKind.Method or VocabularyKind.CursorMethod => Name + "(|)",
        VocabularyKind.Stage => Name + ": { | }",
        _ => Name + ": |"
    };
}

/// <summary>
/// MongoDB 的词汇表:聚合阶段、查询 / 更新 / 表达式运算符、集合与游标方法、代码片段。
/// 查询编辑器、筛选栏、管道构建器(阶段下拉)、验证规则编辑器的补全都从这里取,
/// 所以同一个 <c>$match</c> 在四处的说明一字不差。
/// </summary>
public static class MongoVocabulary
{
    /// <summary>聚合阶段。</summary>
    public static IReadOnlyList<VocabularyEntry> Stages { get; } =
    [
        new("$match", VocabularyKind.Stage, "Filters documents. Put it first so it can use an index.", "筛选文档。放在管道最前面才能用上索引。", "{ $match: { <query> } }"),
        new("$project", VocabularyKind.Stage, "Reshapes documents: include, exclude or compute fields.", "重塑文档:保留、排除或计算字段。", "{ $project: { <field>: 1 | 0 | <expr> } }"),
        new("$addFields", VocabularyKind.Stage, "Adds computed fields, keeping existing ones.", "添加计算字段,保留原有字段。", "{ $addFields: { <field>: <expr> } }"),
        new("$set", VocabularyKind.Stage, "Alias of $addFields.", "$addFields 的别名。", "{ $set: { <field>: <expr> } }"),
        new("$unset", VocabularyKind.Stage, "Removes fields.", "移除字段。", "{ $unset: [ \"<field>\" ] }", "$unset: [ \"|\" ]"),
        new("$group", VocabularyKind.Stage, "Groups by _id and computes accumulators per group.", "按 _id 分组并计算每组的累加器。", "{ $group: { _id: <expr>, <field>: { <accumulator>: <expr> } } }"),
        new("$sort", VocabularyKind.Stage, "Sorts the input documents. Can use an index when placed early.", "按字段对输入文档排序后依次输出。可利用索引的排序应尽量放在管道前部。", "{ $sort: { <field>: 1 | -1 } }"),
        new("$sortByCount", VocabularyKind.Stage, "Groups by an expression and sorts by count, descending.", "按表达式分组并按计数降序。", "{ $sortByCount: <expr> }", "$sortByCount: \"$|\""),
        new("$limit", VocabularyKind.Stage, "Passes the first n documents.", "只放行前 n 份文档。", "{ $limit: <n> }", "$limit: |"),
        new("$skip", VocabularyKind.Stage, "Skips the first n documents.", "跳过前 n 份文档。", "{ $skip: <n> }", "$skip: |"),
        new("$unwind", VocabularyKind.Stage, "Deconstructs an array field into one document per element.", "把数组字段展开成每个元素一份文档。", "{ $unwind: { path: \"$<array>\" } }", "$unwind: { path: \"$|\" }"),
        new("$lookup", VocabularyKind.Stage, "Left outer join with another collection in the same database.", "与同库另一个集合做左外连接。", "{ $lookup: { from, localField, foreignField, as } }", "$lookup: { from: \"|\", localField: \"\", foreignField: \"\", as: \"\" }"),
        new("$graphLookup", VocabularyKind.Stage, "Recursive lookup across a graph of documents.", "在文档图上递归查找。", "{ $graphLookup: { from, startWith, connectFromField, connectToField, as } }"),
        new("$facet", VocabularyKind.Stage, "Runs several sub-pipelines over the same input.", "对同一批输入并行跑多条子管道。", "{ $facet: { <name>: [ <stages> ] } }"),
        new("$bucket", VocabularyKind.Stage, "Groups into buckets by boundaries.", "按边界分桶。", "{ $bucket: { groupBy, boundaries, default, output } }"),
        new("$bucketAuto", VocabularyKind.Stage, "Groups into an even number of buckets.", "自动均分成若干桶。", "{ $bucketAuto: { groupBy, buckets } }"),
        new("$count", VocabularyKind.Stage, "Outputs the number of documents.", "输出文档数。", "{ $count: \"<field>\" }", "$count: \"|\""),
        new("$replaceRoot", VocabularyKind.Stage, "Replaces the document with an embedded one.", "用嵌入文档替换整份文档。", "{ $replaceRoot: { newRoot: <expr> } }"),
        new("$replaceWith", VocabularyKind.Stage, "Alias of $replaceRoot.", "$replaceRoot 的简写。", "{ $replaceWith: <expr> }"),
        new("$sample", VocabularyKind.Stage, "Randomly selects n documents.", "随机抽取 n 份文档。", "{ $sample: { size: <n> } }", "$sample: { size: | }"),
        new("$out", VocabularyKind.Stage, "Writes the result to a collection (replaces it).", "把结果写进一个集合(整体替换)。", "{ $out: \"<collection>\" }", "$out: \"|\""),
        new("$merge", VocabularyKind.Stage, "Merges the result into a collection.", "把结果合并进一个集合。", "{ $merge: { into, on, whenMatched, whenNotMatched } }"),
        new("$unionWith", VocabularyKind.Stage, "Appends documents from another collection.", "追加另一个集合的文档。", "{ $unionWith: { coll, pipeline } }"),
        new("$setWindowFields", VocabularyKind.Stage, "Window functions over partitions.", "在分区上做窗口计算。", "{ $setWindowFields: { partitionBy, sortBy, output } }"),
        new("$densify", VocabularyKind.Stage, "Fills gaps in a sequence.", "补齐序列里的空档。", "{ $densify: { field, range } }"),
        new("$fill", VocabularyKind.Stage, "Fills null and missing values.", "填充 null 与缺失值。", "{ $fill: { output } }"),
        new("$geoNear", VocabularyKind.Stage, "Sorts by distance from a point (must be first).", "按到某点的距离排序(必须是第一个阶段)。", "{ $geoNear: { near, distanceField } }"),
        new("$redact", VocabularyKind.Stage, "Restricts content by document-level access rules.", "按文档级规则裁剪内容。", "{ $redact: <expr> }"),
        new("$collStats", VocabularyKind.Stage, "Collection statistics.", "集合统计。", "{ $collStats: { storageStats: {} } }"),
        new("$indexStats", VocabularyKind.Stage, "Per-index usage statistics.", "各索引的使用统计。", "{ $indexStats: {} }", "$indexStats: {}"),
        new("$documents", VocabularyKind.Stage, "Literal documents as input.", "以字面量文档为输入。", "{ $documents: [ <docs> ] }")
    ];

    /// <summary>查询运算符。</summary>
    public static IReadOnlyList<VocabularyEntry> QueryOperators { get; } =
    [
        new("$eq", VocabularyKind.QueryOperator, "Equal to.", "等于。", "{ <field>: { $eq: <value> } }"),
        new("$ne", VocabularyKind.QueryOperator, "Not equal to.", "不等于。", "{ <field>: { $ne: <value> } }"),
        new("$gt", VocabularyKind.QueryOperator, "Greater than.", "大于。", "{ <field>: { $gt: <value> } }"),
        new("$gte", VocabularyKind.QueryOperator, "Greater than or equal.", "大于等于。", "{ <field>: { $gte: <value> } }"),
        new("$lt", VocabularyKind.QueryOperator, "Less than.", "小于。", "{ <field>: { $lt: <value> } }"),
        new("$lte", VocabularyKind.QueryOperator, "Less than or equal.", "小于等于。", "{ <field>: { $lte: <value> } }"),
        new("$in", VocabularyKind.QueryOperator, "Matches any value in the array.", "匹配数组中的任一值。", "{ <field>: { $in: [ <v1>, <v2> ] } }", "$in: [ | ]"),
        new("$nin", VocabularyKind.QueryOperator, "Matches none of the values.", "不匹配数组中的任何值。", "{ <field>: { $nin: [ <v1> ] } }", "$nin: [ | ]"),
        new("$and", VocabularyKind.QueryOperator, "All clauses match.", "所有条件都满足。", "{ $and: [ { <q1> }, { <q2> } ] }", "$and: [ { | } ]"),
        new("$or", VocabularyKind.QueryOperator, "Any clause matches.", "任一条件满足。", "{ $or: [ { <q1> }, { <q2> } ] }", "$or: [ { | } ]"),
        new("$nor", VocabularyKind.QueryOperator, "No clause matches.", "所有条件都不满足。", "{ $nor: [ { <q1> } ] }", "$nor: [ { | } ]"),
        new("$not", VocabularyKind.QueryOperator, "Inverts an operator expression.", "取反一个运算符表达式。", "{ <field>: { $not: { <op> } } }"),
        new("$exists", VocabularyKind.QueryOperator, "Field is present (or absent).", "字段存在(或不存在)。", "{ <field>: { $exists: true } }", "$exists: true|"),
        new("$type", VocabularyKind.QueryOperator, "Field is of a BSON type.", "字段为某种 BSON 类型。", "{ <field>: { $type: \"string\" } }", "$type: \"|\""),
        new("$regex", VocabularyKind.QueryOperator, "Matches a regular expression.", "匹配正则表达式。", "{ <field>: { $regex: /pattern/i } }", "$regex: /|/"),
        new("$expr", VocabularyKind.QueryOperator, "Uses aggregation expressions in a query.", "在查询里使用聚合表达式。", "{ $expr: { $gt: [ \"$a\", \"$b\" ] } }", "$expr: { | }"),
        new("$jsonSchema", VocabularyKind.QueryOperator, "Validates against a JSON schema.", "按 JSON Schema 校验。", "{ $jsonSchema: { bsonType: \"object\" } }", "$jsonSchema: { bsonType: \"object\", | }"),
        new("$all", VocabularyKind.QueryOperator, "Array contains all values.", "数组包含全部这些值。", "{ <field>: { $all: [ <v1>, <v2> ] } }", "$all: [ | ]"),
        new("$elemMatch", VocabularyKind.QueryOperator, "An array element matches all conditions.", "数组中有一个元素满足全部条件。", "{ <field>: { $elemMatch: { <q> } } }", "$elemMatch: { | }"),
        new("$size", VocabularyKind.QueryOperator, "Array has n elements.", "数组恰有 n 个元素。", "{ <field>: { $size: <n> } }"),
        new("$mod", VocabularyKind.QueryOperator, "Modulo match.", "取模匹配。", "{ <field>: { $mod: [ <divisor>, <remainder> ] } }"),
        new("$text", VocabularyKind.QueryOperator, "Full-text search (needs a text index).", "全文检索(需要 text 索引)。", "{ $text: { $search: \"<words>\" } }", "$text: { $search: \"|\" }"),
        new("$geoWithin", VocabularyKind.QueryOperator, "Within a geometry.", "落在某个几何范围内。", "{ <field>: { $geoWithin: { $geometry } } }"),
        new("$near", VocabularyKind.QueryOperator, "Near a point, sorted by distance.", "靠近某点,按距离排序。", "{ <field>: { $near: { $geometry } } }")
    ];

    /// <summary>更新运算符。</summary>
    public static IReadOnlyList<VocabularyEntry> UpdateOperators { get; } =
    [
        new("$set", VocabularyKind.UpdateOperator, "Sets field values.", "设置字段值。", "{ $set: { <field>: <value> } }", "$set: { | }"),
        new("$unset", VocabularyKind.UpdateOperator, "Removes fields.", "删除字段。", "{ $unset: { <field>: \"\" } }", "$unset: { |: \"\" }"),
        new("$inc", VocabularyKind.UpdateOperator, "Increments by a number.", "按数值递增。", "{ $inc: { <field>: <n> } }", "$inc: { | }"),
        new("$mul", VocabularyKind.UpdateOperator, "Multiplies by a number.", "乘以一个数。", "{ $mul: { <field>: <n> } }", "$mul: { | }"),
        new("$rename", VocabularyKind.UpdateOperator, "Renames a field.", "重命名字段。", "{ $rename: { <old>: \"<new>\" } }", "$rename: { | }"),
        new("$setOnInsert", VocabularyKind.UpdateOperator, "Sets only when an upsert inserts.", "仅在 upsert 插入时设置。", "{ $setOnInsert: { <field>: <value> } }", "$setOnInsert: { | }"),
        new("$min", VocabularyKind.UpdateOperator, "Updates if the value is smaller.", "新值更小时才更新。", "{ $min: { <field>: <value> } }", "$min: { | }"),
        new("$max", VocabularyKind.UpdateOperator, "Updates if the value is larger.", "新值更大时才更新。", "{ $max: { <field>: <value> } }", "$max: { | }"),
        new("$currentDate", VocabularyKind.UpdateOperator, "Sets the field to the current date.", "设为当前时间。", "{ $currentDate: { <field>: true } }", "$currentDate: { |: true }"),
        new("$push", VocabularyKind.UpdateOperator, "Appends to an array.", "向数组追加。", "{ $push: { <field>: <value> } }", "$push: { | }"),
        new("$addToSet", VocabularyKind.UpdateOperator, "Adds to an array unless present.", "不存在时才加入数组。", "{ $addToSet: { <field>: <value> } }", "$addToSet: { | }"),
        new("$pop", VocabularyKind.UpdateOperator, "Removes the first or last element.", "移除首个或末个元素。", "{ $pop: { <field>: 1 | -1 } }", "$pop: { | }"),
        new("$pull", VocabularyKind.UpdateOperator, "Removes matching elements.", "移除匹配的元素。", "{ $pull: { <field>: <cond> } }", "$pull: { | }"),
        new("$pullAll", VocabularyKind.UpdateOperator, "Removes all listed values.", "移除列出的全部值。", "{ $pullAll: { <field>: [ <v> ] } }", "$pullAll: { | }")
    ];

    /// <summary>聚合表达式与累加器。</summary>
    public static IReadOnlyList<VocabularyEntry> Expressions { get; } =
    [
        new("$sum", VocabularyKind.Expression, "Sum (accumulator or array expression).", "求和(累加器或数组表达式)。", "{ $sum: <expr> }", "$sum: |"),
        new("$avg", VocabularyKind.Expression, "Average.", "平均值。", "{ $avg: <expr> }", "$avg: \"$|\""),
        new("$min", VocabularyKind.Expression, "Minimum.", "最小值。", "{ $min: <expr> }", "$min: \"$|\""),
        new("$max", VocabularyKind.Expression, "Maximum.", "最大值。", "{ $max: <expr> }", "$max: \"$|\""),
        new("$first", VocabularyKind.Expression, "First value in the group.", "组内第一个值。", "{ $first: <expr> }", "$first: \"$|\""),
        new("$last", VocabularyKind.Expression, "Last value in the group.", "组内最后一个值。", "{ $last: <expr> }", "$last: \"$|\""),
        new("$push", VocabularyKind.Expression, "Collects values into an array.", "把值收进数组。", "{ $push: <expr> }", "$push: \"$|\""),
        new("$addToSet", VocabularyKind.Expression, "Collects distinct values.", "收集去重后的值。", "{ $addToSet: <expr> }", "$addToSet: \"$|\""),
        new("$count", VocabularyKind.Expression, "Counts documents in the group.", "统计组内文档数。", "{ $count: {} }", "$count: {}"),
        new("$add", VocabularyKind.Expression, "Adds numbers or a date and milliseconds.", "数相加,或日期加毫秒。", "{ $add: [ <e1>, <e2> ] }", "$add: [ | ]"),
        new("$subtract", VocabularyKind.Expression, "Subtracts.", "相减。", "{ $subtract: [ <e1>, <e2> ] }", "$subtract: [ | ]"),
        new("$multiply", VocabularyKind.Expression, "Multiplies.", "相乘。", "{ $multiply: [ <e1>, <e2> ] }", "$multiply: [ | ]"),
        new("$divide", VocabularyKind.Expression, "Divides.", "相除。", "{ $divide: [ <e1>, <e2> ] }", "$divide: [ | ]"),
        new("$round", VocabularyKind.Expression, "Rounds to a place.", "四舍五入到某位。", "{ $round: [ <number>, <place> ] }", "$round: [ |, 2 ]"),
        new("$concat", VocabularyKind.Expression, "Concatenates strings.", "拼接字符串。", "{ $concat: [ <s1>, <s2> ] }", "$concat: [ | ]"),
        new("$toUpper", VocabularyKind.Expression, "Uppercases a string.", "转大写。", "{ $toUpper: <expr> }"),
        new("$toLower", VocabularyKind.Expression, "Lowercases a string.", "转小写。", "{ $toLower: <expr> }"),
        new("$split", VocabularyKind.Expression, "Splits a string.", "切分字符串。", "{ $split: [ <string>, <delimiter> ] }"),
        new("$dateToString", VocabularyKind.Expression, "Formats a date.", "格式化日期。", "{ $dateToString: { format: \"%Y-%m-%d\", date: <expr> } }", "$dateToString: { format: \"%Y-%m-%d\", date: \"$|\" }"),
        new("$dateTrunc", VocabularyKind.Expression, "Truncates a date to a unit.", "把日期截到某个单位。", "{ $dateTrunc: { date: <expr>, unit: \"day\" } }"),
        new("$year", VocabularyKind.Expression, "Year of a date.", "日期的年份。", "{ $year: <date> }"),
        new("$month", VocabularyKind.Expression, "Month of a date.", "日期的月份。", "{ $month: <date> }"),
        new("$dayOfMonth", VocabularyKind.Expression, "Day of month.", "日期的日。", "{ $dayOfMonth: <date> }"),
        new("$cond", VocabularyKind.Expression, "If / then / else.", "条件表达式。", "{ $cond: { if, then, else } }", "$cond: { if: |, then: , else: }"),
        new("$ifNull", VocabularyKind.Expression, "Replacement for null or missing.", "null 或缺失时的替代值。", "{ $ifNull: [ <expr>, <replacement> ] }", "$ifNull: [ |, null ]"),
        new("$switch", VocabularyKind.Expression, "Multi-branch condition.", "多分支条件。", "{ $switch: { branches, default } }"),
        new("$size", VocabularyKind.Expression, "Array length.", "数组长度。", "{ $size: <array> }", "$size: \"$|\""),
        new("$arrayElemAt", VocabularyKind.Expression, "Element at an index.", "取数组某个下标的元素。", "{ $arrayElemAt: [ <array>, <idx> ] }"),
        new("$filter", VocabularyKind.Expression, "Filters an array.", "过滤数组。", "{ $filter: { input, as, cond } }"),
        new("$map", VocabularyKind.Expression, "Maps an array.", "映射数组。", "{ $map: { input, as, in } }"),
        new("$reduce", VocabularyKind.Expression, "Reduces an array.", "归约数组。", "{ $reduce: { input, initialValue, in } }"),
        new("$toString", VocabularyKind.Expression, "Converts to string.", "转字符串。", "{ $toString: <expr> }"),
        new("$toInt", VocabularyKind.Expression, "Converts to Int32.", "转 Int32。", "{ $toInt: <expr> }"),
        new("$toDecimal", VocabularyKind.Expression, "Converts to Decimal128.", "转 Decimal128。", "{ $toDecimal: <expr> }"),
        new("$toDate", VocabularyKind.Expression, "Converts to Date.", "转日期。", "{ $toDate: <expr> }"),
        new("$sortArray", VocabularyKind.Expression, "Sorts an array by a sort spec.", "按排序规则给数组排序。", "{ $sortArray: { input: <array>, sortBy: { <field>: 1 } } }",
            "$sortArray: { input: \"$|\", sortBy: { _id: 1 } }"),
        new("$concatArrays", VocabularyKind.Expression, "Concatenates arrays.", "拼接数组。", "{ $concatArrays: [ <a1>, <a2> ] }", "$concatArrays: [ | ]"),
        new("$slice", VocabularyKind.Expression, "A subset of an array.", "取数组的一段。", "{ $slice: [ <array>, <n> ] }", "$slice: [ \"$|\", 3 ]"),
        new("$setUnion", VocabularyKind.Expression, "Union of arrays as sets.", "数组按集合求并。", "{ $setUnion: [ <a1>, <a2> ] }", "$setUnion: [ | ]"),
        new("$objectToArray", VocabularyKind.Expression, "Turns a document into k/v pairs.", "把文档拆成键值对数组。", "{ $objectToArray: <doc> }", "$objectToArray: \"$|\""),
        new("$arrayToObject", VocabularyKind.Expression, "Builds a document from k/v pairs.", "由键值对数组组成文档。", "{ $arrayToObject: <array> }", "$arrayToObject: \"$|\""),
        new("$literal", VocabularyKind.Expression, "A value taken literally.", "按字面量取值。", "{ $literal: <value> }")
    ];

    /// <summary>集合方法(<c>db.coll.xxx()</c>)。</summary>
    public static IReadOnlyList<VocabularyEntry> Methods { get; } =
    [
        new("find", VocabularyKind.Method, "Queries documents; returns a cursor.", "查询文档,返回游标。", "db.coll.find(<filter>, <projection>)", "find({ | })"),
        new("findOne", VocabularyKind.Method, "Returns the first matching document.", "返回第一份匹配的文档。", "db.coll.findOne(<filter>)", "findOne({ | })"),
        new("aggregate", VocabularyKind.Method, "Runs an aggregation pipeline.", "执行聚合管道。", "db.coll.aggregate([ <stages> ])", "aggregate([\n  { | }\n])"),
        new("countDocuments", VocabularyKind.Method, "Exact count matching a filter.", "按条件精确计数。", "db.coll.countDocuments(<filter>)", "countDocuments({ | })"),
        new("estimatedDocumentCount", VocabularyKind.Method, "Fast count from metadata.", "按元数据快速估算总数。", "db.coll.estimatedDocumentCount()", "estimatedDocumentCount()"),
        new("distinct", VocabularyKind.Method, "Distinct values of a field.", "某字段的去重值。", "db.coll.distinct(\"<field>\", <filter>)", "distinct(\"|\")"),
        new("insertOne", VocabularyKind.Method, "Inserts one document.", "插入一份文档。", "db.coll.insertOne(<doc>)", "insertOne({ | })"),
        new("insertMany", VocabularyKind.Method, "Inserts several documents.", "插入多份文档。", "db.coll.insertMany([ <docs> ])", "insertMany([ { | } ])"),
        new("updateOne", VocabularyKind.Method, "Updates the first matching document.", "更新第一份匹配的文档。", "db.coll.updateOne(<filter>, <update>)", "updateOne({ | }, { $set: {} })"),
        new("updateMany", VocabularyKind.Method, "Updates all matching documents.", "更新所有匹配的文档。", "db.coll.updateMany(<filter>, <update>)", "updateMany({ | }, { $set: {} })"),
        new("replaceOne", VocabularyKind.Method, "Replaces one document.", "替换一份文档。", "db.coll.replaceOne(<filter>, <doc>)", "replaceOne({ | }, {})"),
        new("deleteOne", VocabularyKind.Method, "Deletes the first matching document.", "删除第一份匹配的文档。", "db.coll.deleteOne(<filter>)", "deleteOne({ | })"),
        new("deleteMany", VocabularyKind.Method, "Deletes all matching documents.", "删除所有匹配的文档。", "db.coll.deleteMany(<filter>)", "deleteMany({ | })"),
        new("findOneAndUpdate", VocabularyKind.Method, "Updates and returns a document.", "更新并返回一份文档。", "db.coll.findOneAndUpdate(<filter>, <update>, <options>)", "findOneAndUpdate({ | }, { $set: {} })"),
        new("bulkWrite", VocabularyKind.Method, "Several writes in one round trip.", "一次往返执行多条写操作。", "db.coll.bulkWrite([ <ops> ])", "bulkWrite([ | ])"),
        new("createIndex", VocabularyKind.Method, "Creates an index.", "创建索引。", "db.coll.createIndex(<keys>, <options>)", "createIndex({ |: 1 })"),
        new("dropIndex", VocabularyKind.Method, "Drops an index.", "删除索引。", "db.coll.dropIndex(\"<name>\")", "dropIndex(\"|\")"),
        new("getIndexes", VocabularyKind.Method, "Lists indexes.", "列出索引。", "db.coll.getIndexes()", "getIndexes()"),
        new("stats", VocabularyKind.Method, "Collection statistics.", "集合统计。", "db.coll.stats()", "stats()"),
        new("drop", VocabularyKind.Method, "Drops the collection.", "删除集合。", "db.coll.drop()", "drop()"),
        new("renameCollection", VocabularyKind.Method, "Renames the collection.", "重命名集合。", "db.coll.renameCollection(\"<new>\")", "renameCollection(\"|\")")
    ];

    /// <summary>游标方法(<c>.sort()</c>、<c>.limit()</c>…)。</summary>
    public static IReadOnlyList<VocabularyEntry> CursorMethods { get; } =
    [
        new("sort", VocabularyKind.CursorMethod, "Sort order.", "排序。", ".sort({ <field>: 1 | -1 })", "sort({ |: -1 })"),
        new("limit", VocabularyKind.CursorMethod, "Maximum documents.", "最多返回几份。", ".limit(<n>)", "limit(|)"),
        new("skip", VocabularyKind.CursorMethod, "Skips documents.", "跳过几份。", ".skip(<n>)", "skip(|)"),
        new("project", VocabularyKind.CursorMethod, "Projection.", "投影。", ".project({ <field>: 1 })", "project({ | })"),
        new("hint", VocabularyKind.CursorMethod, "Forces an index.", "强制使用某个索引。", ".hint(\"<index>\")", "hint(\"|\")"),
        new("maxTimeMS", VocabularyKind.CursorMethod, "Server-side time limit.", "服务器端超时。", ".maxTimeMS(<ms>)", "maxTimeMS(|)"),
        new("collation", VocabularyKind.CursorMethod, "String comparison rules.", "字符串比较规则。", ".collation({ locale: \"zh\" })", "collation({ locale: \"|\" })"),
        new("explain", VocabularyKind.CursorMethod, "Shows the query plan.", "显示执行计划。", ".explain(\"executionStats\")", "explain(\"executionStats\")"),
        new("count", VocabularyKind.CursorMethod, "Counts the cursor's documents.", "统计游标的文档数。", ".count()", "count()"),
        new("toArray", VocabularyKind.CursorMethod, "Collects into an array.", "收集成数组。", ".toArray()", "toArray()"),
        new("comment", VocabularyKind.CursorMethod, "Tags the operation in logs and currentOp.", "在日志与 currentOp 里打标记。", ".comment(\"<text>\")", "comment(\"|\")")
    ];

    /// <summary>代码片段。</summary>
    public static IReadOnlyList<VocabularyEntry> Snippets { get; } =
    [
        new("find 分页…", VocabularyKind.Snippet, "Paged find with sort.", "带排序的分页查询。", "find(...).sort(...).skip(...).limit(...)",
            "find({ | }).sort({ _id: -1 }).skip(0).limit(50)"),
        new("group by…", VocabularyKind.Snippet, "Group and count by a field.", "按字段分组计数。", "aggregate([{ $group }, { $sort }])",
            "aggregate([\n  { $group: { _id: \"$|\", n: { $sum: 1 } } },\n  { $sort: { n: -1 } }\n])"),
        new("update set…", VocabularyKind.Snippet, "Set fields on matching documents.", "给匹配的文档设置字段。", "updateMany(filter, { $set })",
            "updateMany({ | }, { $set: {  } })"),
        new("lookup join…", VocabularyKind.Snippet, "Join with another collection.", "与另一个集合连接。", "aggregate([{ $lookup }, { $unwind }])",
            "aggregate([\n  { $lookup: { from: \"|\", localField: \"\", foreignField: \"_id\", as: \"joined\" } },\n  { $unwind: \"$joined\" }\n])")
    ];

    /// <summary>某个词条在补全里的图标与颜色(设计稿 09「补全项类型」)。</summary>
    public static (string Icon, string Token) Look(VocabularyKind kind) => kind switch
    {
        VocabularyKind.Stage => ("Mongo.layers", "VelaAccent"),
        VocabularyKind.QueryOperator or VocabularyKind.UpdateOperator or VocabularyKind.Expression => ("Mongo.sigma", "VelaShellMagenta"),
        VocabularyKind.Method or VocabularyKind.CursorMethod => ("Mongo.parentheses", "VelaShellYellow"),
        _ => ("Mongo.square-code", "VelaWarning")
    };

    /// <summary>某个种类在补全右侧的类别文字。</summary>
    public static string Category(VocabularyKind kind, bool chinese) => kind switch
    {
        VocabularyKind.Stage => chinese ? "聚合阶段" : "stage",
        VocabularyKind.QueryOperator => chinese ? "查询运算符" : "query operator",
        VocabularyKind.UpdateOperator => chinese ? "更新运算符" : "update operator",
        VocabularyKind.Expression => chinese ? "表达式" : "expression",
        VocabularyKind.Method => chinese ? "方法" : "method",
        VocabularyKind.CursorMethod => chinese ? "游标方法" : "cursor method",
        _ => chinese ? "代码片段" : "snippet"
    };

    /// <summary>把词条变成补全项。</summary>
    public static Ui.CompletionItem ToCompletion(VocabularyEntry entry, bool chinese)
    {
        (string icon, string token) = Look(entry.Kind);
        return new()
        {
            Label = entry.Name,
            InsertText = entry.InsertText,
            IconKey = icon,
            IconToken = token,
            Category = Category(entry.Kind, chinese),
            Badge = Category(entry.Kind, chinese),
            Description = entry.Describe(chinese),
            Syntax = entry.Syntax,
            DocsUrl = entry.DocsUrl
        };
    }
}
