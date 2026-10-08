using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Analysis;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Tests;

// 测试方法的名字就是它的说明(一句完整的句子),不再逐个写 XML 注释。

/// <summary>
/// 客户端 <c>$jsonSchema</c> 预检:常用关键字逐个过一遍,外加"认不出的一律放行"这条底线 ——
/// 预检误报一条,用户就会去改一个其实合法的值。
/// </summary>
[TestClass]
public sealed class JsonSchemaValidatorTests
{
    private static readonly Loc Zh = new("zh-CN");
    private static readonly Loc En = new("en");

    private static IReadOnlyList<SchemaViolation> Check(string schema, string document, Loc? loc = null) =>
        JsonSchemaValidator.Validate(ShellJson.ParseDocument(schema), ShellJson.ParseDocument(document), loc ?? Zh);

    private static SchemaViolation Single(string schema, string document, Loc? loc = null)
    {
        IReadOnlyList<SchemaViolation> found = Check(schema, document, loc);
        Assert.HasCount(1, found, string.Join(" | ", found.Select(static v => v.Message)));
        return found[0];
    }

    // ── 取 schema ───────────────────────────────────────────────────────────

    [TestMethod]
    public void Extract_accepts_the_whole_validator_the_bare_schema_and_an_and_clause()
    {
        Assert.IsNotNull(JsonSchemaValidator.Extract(ShellJson.ParseDocument("{ $jsonSchema: { required: ['a'] } }")));
        Assert.IsNotNull(JsonSchemaValidator.Extract(ShellJson.ParseDocument("{ bsonType: 'object', required: ['a'] }")));
        Assert.IsNotNull(JsonSchemaValidator.Extract(ShellJson.ParseDocument("{ $and: [ { status: 'x' }, { $jsonSchema: { required: ['a'] } } ] }")));
        Assert.IsNull(JsonSchemaValidator.Extract(ShellJson.ParseDocument("{ status: { $in: ['paid', 'shipped'] } }")));
    }

    [TestMethod]
    public void A_query_expression_validator_is_not_evaluated() => Assert.HasCount(0, Check("{ status: { $in: ['paid'] } }", "{ status: 'refunded' }"));

    [TestMethod]
    public void A_valid_document_has_no_violations()
    {
        const string schema = """
            { $jsonSchema: {
                bsonType: 'object', required: ['orderNo', 'total'],
                properties: {
                  orderNo: { bsonType: 'string', pattern: '^SO\\d{4}-\\d{5}$' },
                  total: { bsonType: 'decimal', minimum: 0 },
                  customer: { bsonType: 'object', properties: { level: { enum: ['普通', 'VIP', 'SVIP'] } } },
                  tags: { bsonType: 'array', items: { bsonType: 'string' }, uniqueItems: true }
                } } }
            """;
        const string doc = "{ orderNo: 'SO2609-10403', total: NumberDecimal('8740.00'), customer: { level: 'SVIP' }, tags: ['企业', '开票'] }";
        Assert.HasCount(0, Check(schema, doc));
    }

    // ── 类型 ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void BsonType_mismatch_names_the_expected_and_actual_type()
    {
        SchemaViolation v = Single("{ properties: { discount: { bsonType: 'double' } } }", "{ discount: 'x' }");
        Assert.AreEqual("discount", v.Path);
        Assert.AreEqual("bsonType", v.Keyword);
        Assert.AreEqual("discount 须为 double 类型(当前为 string)", v.Message);
    }

    [TestMethod]
    public void BsonType_accepts_an_array_of_names_and_the_number_alias()
    {
        Assert.HasCount(0, Check("{ properties: { a: { bsonType: ['int', 'null'] } } }", "{ a: null }"));
        Assert.HasCount(0, Check("{ properties: { a: { bsonType: ['int', 'null'] } } }", "{ a: 3 }"));
        Assert.HasCount(1, Check("{ properties: { a: { bsonType: ['int', 'null'] } } }", "{ a: 'x' }"));
        foreach (string value in new[] { "1", "NumberLong('2')", "1.5", "NumberDecimal('2.50')" })
        {
            Assert.HasCount(0, Check("{ properties: { n: { bsonType: 'number' } } }", $"{{ n: {value} }}"), value);
        }
        Assert.HasCount(1, Check("{ properties: { n: { bsonType: 'number' } } }", "{ n: '1' }"));
    }

    [TestMethod]
    public void BsonType_aliases_match_their_types()
    {
        (string Alias, string Value)[] cases =
        [
            ("objectId", "ObjectId('66f5c2a1d38b5e1a0c7fe3b7')"),
            ("date", "ISODate('2026-09-27T02:30:00Z')"),
            ("bool", "true"),
            ("long", "NumberLong('5')"),
            ("int", "5"),
            ("decimal", "NumberDecimal('1.0')"),
            ("object", "{ a: 1 }"),
            ("array", "[1]"),
            ("null", "null"),
            ("string", "'s'"),
            ("double", "2.5")
        ];
        foreach ((string alias, string value) in cases)
        {
            Assert.HasCount(0, Check($"{{ properties: {{ v: {{ bsonType: '{alias}' }} }} }}", $"{{ v: {value} }}"), alias);
        }
        Assert.HasCount(1, Check("{ properties: { v: { bsonType: 'int' } } }", "{ v: NumberLong('5') }"), "long is not int");
    }

    [TestMethod]
    public void Unknown_type_names_and_unknown_keywords_pass()
    {
        Assert.HasCount(0, Check("{ properties: { v: { bsonType: 'dbPointer', 'x-ui': { widget: 'slider' }, format: 'email' } } }", "{ v: 1 }"));
        Assert.HasCount(0, Check("{ properties: { v: { type: 'whatever' } } }", "{ v: 1 }"));
    }

    [TestMethod]
    public void Json_type_keyword_is_checked()
    {
        Assert.HasCount(0, Check("{ properties: { v: { type: 'number' } } }", "{ v: NumberDecimal('1') }"));
        Assert.HasCount(0, Check("{ properties: { v: { type: ['string', 'null'] } } }", "{ v: null }"));
        Assert.HasCount(0, Check("{ properties: { v: { type: 'boolean' } } }", "{ v: false }"));
        Assert.AreEqual("type", Single("{ properties: { v: { type: 'string' } } }", "{ v: 1 }").Keyword);
        Assert.HasCount(0, Check("{ properties: { v: { type: 'integer' } } }", "{ v: 4.0 }"));
        Assert.HasCount(1, Check("{ properties: { v: { type: 'integer' } } }", "{ v: 4.5 }"));
    }

    // ── 枚举 ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Enum_violation_on_a_nested_field_reports_the_dotted_path()
    {
        SchemaViolation v = Single(
            "{ $jsonSchema: { properties: { customer: { properties: { level: { enum: ['普通', 'VIP', 'SVIP'] } } } } } }",
            "{ customer: { level: '钻石' } }");
        Assert.AreEqual("customer.level", v.Path);
        Assert.AreEqual("enum", v.Keyword);
        Assert.AreEqual("customer.level 不在枚举中(普通 · VIP · SVIP)", v.Message);
        StringAssert.StartsWith(Single(
            "{ properties: { customer: { properties: { level: { enum: ['VIP'] } } } } }",
            "{ customer: { level: 'x' } }", En).Message, "customer.level is not in the enum");
    }

    [TestMethod]
    public void Enum_compares_numbers_by_value_across_types()
    {
        Assert.HasCount(0, Check("{ properties: { n: { enum: [1, 2] } } }", "{ n: NumberLong('2') }"));
        Assert.HasCount(0, Check("{ properties: { n: { enum: [1.0] } } }", "{ n: 1 }"));
        Assert.HasCount(1, Check("{ properties: { n: { enum: [1, 2] } } }", "{ n: 3 }"));
    }

    // ── 数值 ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Minimum_message_matches_the_design()
    {
        SchemaViolation v = Single("{ $jsonSchema: { properties: { discount: { bsonType: 'double', minimum: 0 } } } }", "{ discount: -20.0 }");
        Assert.AreEqual("discount", v.Path);
        Assert.AreEqual("minimum", v.Keyword);
        Assert.AreEqual("discount 须 ≥ 0", v.Message);
        Assert.AreEqual("discount must be ≥ 0",
            Single("{ properties: { discount: { minimum: 0 } } }", "{ discount: -1 }", En).Message);
    }

    [TestMethod]
    public void Boundaries_are_inclusive_unless_exclusive_is_set()
    {
        Assert.HasCount(0, Check("{ properties: { n: { minimum: 0, maximum: 10 } } }", "{ n: 0 }"));
        Assert.HasCount(0, Check("{ properties: { n: { minimum: 0, maximum: 10 } } }", "{ n: 10 }"));
        Assert.AreEqual("n 须 > 0", Single("{ properties: { n: { minimum: 0, exclusiveMinimum: true } } }", "{ n: 0 }").Message);
        Assert.AreEqual("n 须 < 10", Single("{ properties: { n: { maximum: 10, exclusiveMaximum: true } } }", "{ n: 10 }").Message);
        Assert.AreEqual("n 须 ≤ 10", Single("{ properties: { n: { maximum: 10 } } }", "{ n: 10.5 }").Message);
        // draft 6 的数字写法 MongoDB 不认,但也不该误报或漏报。
        Assert.HasCount(1, Check("{ properties: { n: { exclusiveMinimum: 5 } } }", "{ n: 5 }"));
    }

    [TestMethod]
    public void Decimal_and_long_bounds_compare_without_losing_precision()
    {
        Assert.AreEqual("price 须 ≥ 0", Single("{ properties: { price: { bsonType: 'decimal', minimum: 0 } } }", "{ price: NumberDecimal('-0.01') }").Message);
        Assert.HasCount(0, Check("{ properties: { price: { minimum: NumberDecimal('0.10') } } }", "{ price: NumberDecimal('0.10') }"));
        Assert.HasCount(1, Check("{ properties: { id: { maximum: NumberLong('9007199254740993') } } }", "{ id: NumberLong('9007199254740994') }"));
    }

    [TestMethod]
    public void MultipleOf_is_checked()
    {
        Assert.HasCount(0, Check("{ properties: { n: { multipleOf: 0.5 } } }", "{ n: 2.5 }"));
        Assert.AreEqual("multipleOf", Single("{ properties: { n: { multipleOf: 5 } } }", "{ n: 12 }").Keyword);
    }

    [TestMethod]
    public void Number_keywords_ignore_non_numbers() => Assert.HasCount(0, Check("{ properties: { n: { minimum: 0, maxLength: 1 } } }", "{ n: 'not a number' }").Where(static v => v.Keyword == "minimum").ToList());

    // ── 字符串 ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void String_length_counts_characters_not_bytes_or_code_units()
    {
        Assert.HasCount(0, Check("{ properties: { name: { maxLength: 2 } } }", "{ name: '张伟' }"));
        Assert.HasCount(0, Check("{ properties: { name: { maxLength: 1 } } }", "{ name: '😀' }"));
        Assert.AreEqual("name 至少 3 个字符", Single("{ properties: { name: { minLength: 3 } } }", "{ name: '张伟' }").Message);
        Assert.AreEqual("maxLength", Single("{ properties: { name: { maxLength: 1 } } }", "{ name: 'ab' }").Keyword);
    }

    [TestMethod]
    public void Pattern_is_checked_and_a_broken_pattern_passes()
    {
        Assert.HasCount(0, Check("{ properties: { sku: { pattern: '^SKU-\\\\d+$' } } }", "{ sku: 'SKU-7710' }"));
        SchemaViolation v = Single("{ properties: { sku: { pattern: '^SKU-\\\\d+$' } } }", "{ sku: 'X-1' }");
        Assert.AreEqual("pattern", v.Keyword);
        StringAssert.StartsWith(v.Message, "sku 不匹配");
        Assert.HasCount(0, Check("{ properties: { sku: { pattern: '([' } } }", "{ sku: 'anything' }"));
    }

    // ── 对象 ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Required_reports_each_missing_field_with_its_path()
    {
        IReadOnlyList<SchemaViolation> found = Check(
            "{ $jsonSchema: { required: ['orderNo', 'total'], properties: { customer: { required: ['id'] } } } }",
            "{ total: 1, customer: { name: 'x' } }");
        Assert.HasCount(2, found);
        Assert.AreEqual("orderNo", found[0].Path);
        Assert.AreEqual("required", found[0].Keyword);
        Assert.AreEqual("缺少必填字段 orderNo", found[0].Message);
        Assert.AreEqual("customer.id", found[1].Path);
        Assert.AreEqual("缺少必填字段 customer.id", found[1].Message);
    }

    [TestMethod]
    public void AdditionalProperties_false_flags_undeclared_fields_including_id()
    {
        IReadOnlyList<SchemaViolation> found = Check(
            "{ additionalProperties: false, properties: { name: {} } }", "{ _id: 1, name: 'a', extra: true }");
        CollectionAssert.AreEquivalent(new[] { "_id", "extra" }, found.Select(static v => v.Path).ToArray());
        Assert.IsTrue(found.All(static v => v.Keyword == "additionalProperties"));
        Assert.HasCount(0, Check("{ additionalProperties: false, properties: { _id: {}, name: {} } }", "{ _id: 1, name: 'a' }"));
    }

    [TestMethod]
    public void AdditionalProperties_schema_and_patternProperties_are_applied()
    {
        Assert.AreEqual("x", Single("{ properties: { a: {} }, additionalProperties: { bsonType: 'int' } }", "{ a: 's', x: 's' }").Path);
        Assert.HasCount(0, Check("{ additionalProperties: false, patternProperties: { '^attr_': { bsonType: 'string' } } }", "{ attr_color: 'red' }"));
        Assert.AreEqual("attr_size", Single("{ patternProperties: { '^attr_': { bsonType: 'string' } } }", "{ attr_size: 3 }").Path);
    }

    [TestMethod]
    public void Min_and_max_properties_are_checked()
    {
        Assert.AreEqual("minProperties", Single("{ properties: { meta: { minProperties: 2 } } }", "{ meta: { a: 1 } }").Keyword);
        Assert.AreEqual("maxProperties", Single("{ properties: { meta: { maxProperties: 1 } } }", "{ meta: { a: 1, b: 2 } }").Keyword);
    }

    // ── 数组 ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Items_schema_reports_the_indexed_path()
    {
        SchemaViolation v = Single(
            "{ properties: { items: { items: { properties: { qty: { bsonType: 'int', minimum: 1 } } } } } }",
            "{ items: [ { qty: 2 }, { qty: 0 } ] }");
        Assert.AreEqual("items.1.qty", v.Path);
        Assert.AreEqual("items.1.qty 须 ≥ 1", v.Message);
    }

    [TestMethod]
    public void Tuple_items_and_additionalItems_are_checked()
    {
        const string schema = "{ properties: { point: { items: [ { bsonType: 'double' }, { bsonType: 'double' } ], additionalItems: false } } }";
        Assert.HasCount(0, Check(schema, "{ point: [1.5, 2.5] }"));
        Assert.AreEqual("point.1", Single(schema, "{ point: [1.5, 'x'] }").Path);
        Assert.AreEqual("additionalItems", Single(schema, "{ point: [1.5, 2.5, 3.5] }").Keyword);
    }

    [TestMethod]
    public void Item_counts_and_uniqueness_are_checked()
    {
        Assert.AreEqual("minItems", Single("{ properties: { tags: { minItems: 1 } } }", "{ tags: [] }").Keyword);
        Assert.AreEqual("maxItems", Single("{ properties: { tags: { maxItems: 1 } } }", "{ tags: ['a', 'b'] }").Keyword);
        Assert.AreEqual("tags 有重复项", Single("{ properties: { tags: { uniqueItems: true } } }", "{ tags: ['企业', '企业'] }").Message);
        Assert.AreEqual("uniqueItems", Single("{ properties: { n: { uniqueItems: true } } }", "{ n: [1, 1.0] }").Keyword);
        Assert.HasCount(0, Check("{ properties: { n: { uniqueItems: true } } }", "{ n: [1, 2, '1'] }"));
    }

    // ── 组合 ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Combinators_are_evaluated()
    {
        const string anyOf = "{ properties: { v: { anyOf: [ { bsonType: 'int' }, { bsonType: 'string', minLength: 2 } ] } } }";
        Assert.HasCount(0, Check(anyOf, "{ v: 3 }"));
        Assert.HasCount(0, Check(anyOf, "{ v: 'ab' }"));
        Assert.AreEqual("v 不满足 anyOf 中的任何一项", Single(anyOf, "{ v: 'a' }").Message);

        const string oneOf = "{ properties: { v: { oneOf: [ { minimum: 0 }, { maximum: 10 } ] } } }";
        Assert.HasCount(0, Check(oneOf, "{ v: -5 }"));
        Assert.AreEqual("v 须恰好满足 oneOf 中的一项(满足了 2 项)", Single(oneOf, "{ v: 5 }").Message);

        Assert.AreEqual("not", Single("{ properties: { v: { not: { bsonType: 'null' } } } }", "{ v: null }").Keyword);
        Assert.HasCount(0, Check("{ properties: { v: { not: { bsonType: 'null' } } } }", "{ v: 1 }"));

        IReadOnlyList<SchemaViolation> all = Check("{ properties: { v: { allOf: [ { minimum: 5 }, { multipleOf: 2 } ] } } }", "{ v: 3 }");
        CollectionAssert.AreEquivalent(new[] { "minimum", "multipleOf" }, all.Select(static v => v.Keyword).ToArray());
    }

    [TestMethod]
    public void The_root_level_is_named_as_the_document()
    {
        SchemaViolation v = Single("{ bsonType: 'object', minProperties: 5 }", "{ a: 1 }");
        Assert.AreEqual("", v.Path);
        Assert.AreEqual("文档 至少要有 5 个字段", v.Message);
    }

    // ── 路径上的子 schema ──────────────────────────────────────────────────

    [TestMethod]
    public void SchemaAt_walks_properties_and_items()
    {
        BsonDocument validator = ShellJson.ParseDocument("""
            { $jsonSchema: { properties: {
                customer: { properties: { level: { enum: ['普通', 'VIP', 'SVIP'] } } },
                items: { items: { properties: { unit: { enum: ['件', '箱'] } } } } } } }
            """);
        BsonArray? level = JsonSchemaValidator.EnumAt(validator, ["customer", "level"]);
        Assert.IsNotNull(level);
        Assert.HasCount(3, level);
        Assert.AreEqual("箱", JsonSchemaValidator.EnumAt(validator, ["items", "0", "unit"])![1].AsString);
        Assert.AreEqual("件", JsonSchemaValidator.EnumAt(validator, ["items", "unit"])![0].AsString);
        Assert.IsNull(JsonSchemaValidator.EnumAt(validator, ["customer", "name"]));
        Assert.IsNull(JsonSchemaValidator.SchemaAt(validator, ["nope", "deeper"]));
    }

    [TestMethod]
    public void Arguments_are_required()
    {
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => JsonSchemaValidator.Validate(null!, [], Zh));
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => JsonSchemaValidator.Validate([], null!, Zh));
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => JsonSchemaValidator.Validate([], [], null!));
    }
}
