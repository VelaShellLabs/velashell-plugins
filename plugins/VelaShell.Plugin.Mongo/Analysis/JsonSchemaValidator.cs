using System.Globalization;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Analysis;

/// <summary>一条违反验证规则的地方。</summary>
/// <param name="Path">字段路径(<c>discount</c>、<c>customer.level</c>、<c>items.0.qty</c>);整份文档为空串。</param>
/// <param name="Keyword">违反的关键字(<c>minimum</c>、<c>required</c>、<c>enum</c>、<c>bsonType</c>…)。</param>
/// <param name="Message">给人看的一句话(<c>discount 须 ≥ 0</c>)。</param>
internal sealed record SchemaViolation(string Path, string Keyword, string Message);

/// <summary>
/// 客户端的 <c>$jsonSchema</c> 校验(文档编辑器的实时校验、验证规则页的"试写文档")。
/// <para>
/// 只是**预检**:最终以服务器的校验为准。覆盖 MongoDB 支持的常用关键字子集 —— bsonType / type / enum /
/// required / properties / patternProperties / additionalProperties / 数值与长度边界 / pattern /
/// 数组的 items 与个数 / allOf · anyOf · oneOf · not。认不出的关键字、认不出的类型名一律**放行**:
/// 预检误报一条"违反规则",用户就会去改一个其实合法的值;漏报只是把拒绝推迟到服务器那一步。
/// </para>
/// <para>
/// 数组元素的路径带下标(<c>items.0.qty</c>),与文档编辑器表单里数组项的行路径一致 ——
/// 这样一条违规能直接落到具体那一行上。
/// </para>
/// </summary>
internal static class JsonSchemaValidator
{
    /// <summary>正则关键字的超时:用户写的 pattern 可能有灾难性回溯,预检不值得为它卡住界面。</summary>
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>认作"这是一份 schema 本身"的关键字(区分整个 validator 与其中的 schema)。</summary>
    private static readonly HashSet<string> SchemaKeywords = new(StringComparer.Ordinal)
    {
        "bsonType", "type", "properties", "required", "additionalProperties", "patternProperties",
        "items", "enum", "minimum", "maximum", "minLength", "maxLength", "pattern", "anyOf", "allOf", "oneOf", "not",
        "minItems", "maxItems", "minProperties", "maxProperties", "title", "description"
    };

    /// <summary>
    /// 校验一份文档。<paramref name="validator" /> 可以是整个 <c>validator</c>(含 <c>$jsonSchema</c>)
    /// 或其中的 schema 本身。纯查询表达式写的 validator(<c>{ status: { $in: […] } }</c>)这里不评估,返回空。
    /// </summary>
    public static IReadOnlyList<SchemaViolation> Validate(BsonDocument validator, BsonDocument document, Loc loc)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(loc);
        if (Extract(validator) is not { } schema)
        {
            return [];
        }
        var walker = new Walker(loc, collect: true);
        walker.Check(schema, document, "");
        return walker.Found;
    }

    /// <summary>
    /// 从 validator 里取出 schema:<c>$jsonSchema</c> 键、<c>$and</c> 里的 <c>$jsonSchema</c>,
    /// 或者它本身就是一份 schema。都不是返回 <see langword="null" />。
    /// </summary>
    public static BsonDocument? Extract(BsonDocument validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        if (validator.TryGetValue("$jsonSchema", out BsonValue direct))
        {
            return direct as BsonDocument;
        }
        if (validator.TryGetValue("$and", out BsonValue and) && and is BsonArray clauses)
        {
            foreach (BsonDocument clause in clauses.OfType<BsonDocument>())
            {
                if (clause.TryGetValue("$jsonSchema", out BsonValue nested) && nested is BsonDocument found)
                {
                    return found;
                }
            }
            return null;
        }
        return validator.Names.Any(SchemaKeywords.Contains) ? validator : null;
    }

    /// <summary>
    /// 某条路径上的子 schema(文档编辑器的枚举下拉、类型提示用)。
    /// 数组下标那一段走 <c>items</c>;不带下标的数组路径(Schema 抽样那种 <c>items.sku</c>)也认。
    /// </summary>
    /// <param name="validator">整个 validator 或 schema。</param>
    /// <param name="segments">路径分段。</param>
    /// <returns>子 schema;路径上任何一段没有声明则为 <see langword="null" />。</returns>
    public static BsonDocument? SchemaAt(BsonDocument validator, IReadOnlyList<string> segments)
    {
        BsonDocument? node = Extract(validator);
        foreach (string segment in segments)
        {
            if (node is null)
            {
                return null;
            }
            bool isIndex = int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out int index);
            if (isIndex && node.TryGetValue("items", out BsonValue items))
            {
                node = items switch
                {
                    BsonDocument one => one,
                    BsonArray tuple when index < tuple.Count => tuple[index] as BsonDocument,
                    _ => null
                };
                continue;
            }
            if (Property(node, segment) is { } property)
            {
                node = property;
                continue;
            }
            // 数组里的对象:schema 写在 items.properties 下。
            node = node.TryGetValue("items", out BsonValue element) && element is BsonDocument elementSchema
                ? Property(elementSchema, segment)
                : null;
        }
        return node;
    }

    /// <summary>某条路径上声明的 <c>enum</c>;没有为 <see langword="null" />。</summary>
    public static BsonArray? EnumAt(BsonDocument validator, IReadOnlyList<string> segments) =>
        SchemaAt(validator, segments) is { } schema && schema.TryGetValue("enum", out BsonValue values) ? values as BsonArray : null;

    private static BsonDocument? Property(BsonDocument schema, string name) =>
        schema.TryGetValue("properties", out BsonValue properties) && properties is BsonDocument map
        && map.TryGetValue(name, out BsonValue sub)
            ? sub as BsonDocument
            : null;

    /// <summary>一趟递归校验。<c>collect = false</c> 时只数违规(anyOf / oneOf / not 的试探分支用)。</summary>
    private sealed class Walker(Loc loc, bool collect)
    {
        private readonly List<SchemaViolation> _found = [];

        public List<SchemaViolation> Found => _found;

        public int Count { get; private set; }

        public void Check(BsonDocument schema, BsonValue value, string path)
        {
            CheckType(schema, value, path);
            CheckEnum(schema, value, path);
            if (value.IsNumeric)
            {
                CheckNumber(schema, value, path);
            }
            else if (value.IsString)
            {
                CheckString(schema, value.AsString, path);
            }
            else if (value is BsonDocument document)
            {
                CheckObject(schema, document, path);
            }
            else if (value is BsonArray array)
            {
                CheckArray(schema, array, path);
            }
            CheckCombinators(schema, value, path);
        }

        private void Add(string path, string keyword, string message)
        {
            Count++;
            if (collect)
            {
                _found.Add(new(path, keyword, message));
            }
        }

        private string Name(string path) => path.Length == 0 ? loc["Schema_WholeDocument"] : path;

        // ── 类型 ────────────────────────────────────────────────────────────

        private void CheckType(BsonDocument schema, BsonValue value, string path)
        {
            if (schema.TryGetValue("bsonType", out BsonValue bsonType) && !AnyName(bsonType, name => MatchesBsonType(name, value)))
            {
                Add(path, "bsonType", loc.Format("Schema_MustBeType", Name(path), Names(bsonType), BsonTypeName(value)));
            }
            if (schema.TryGetValue("type", out BsonValue jsonType) && !AnyName(jsonType, name => MatchesJsonType(name, value)))
            {
                Add(path, "type", loc.Format("Schema_MustBeType", Name(path), Names(jsonType), BsonTypeName(value)));
            }
        }

        /// <summary>类型关键字可以是一个名字,也可以是名字数组(任一命中即可)。</summary>
        private static bool AnyName(BsonValue names, Func<string, bool> matches) => names switch
        {
            BsonString one => matches(one.Value),
            BsonArray many => many.Count == 0 || many.Any(n => !n.IsString || matches(n.AsString)),
            _ => true
        };

        private static string Names(BsonValue names) => names switch
        {
            BsonString one => one.Value,
            BsonArray many => string.Join(" | ", many.Select(static n => n.IsString ? n.AsString : n.ToString())),
            _ => names.ToString() ?? ""
        };

        /// <summary>
        /// bsonType 别名与 BSON 类型的对应;认不出的别名放行(返回 true)——
        /// 包括 <c>dbPointer</c> 这类界面里根本编辑不出来的类型。
        /// </summary>
        private static bool MatchesBsonType(string name, BsonValue value) => name switch
        {
            "double" => value.BsonType == BsonType.Double,
            "string" => value.BsonType == BsonType.String,
            "object" => value.BsonType == BsonType.Document,
            "array" => value.BsonType == BsonType.Array,
            "binData" => value.BsonType == BsonType.Binary,
            "undefined" => value.BsonType == BsonType.Undefined,
            "objectId" => value.BsonType == BsonType.ObjectId,
            "bool" => value.BsonType == BsonType.Boolean,
            "date" => value.BsonType == BsonType.DateTime,
            "null" => value.BsonType == BsonType.Null,
            "regex" => value.BsonType == BsonType.RegularExpression,
            "javascript" => value.BsonType == BsonType.JavaScript,
            "javascriptWithScope" => value.BsonType == BsonType.JavaScriptWithScope,
            "symbol" => value.BsonType == BsonType.Symbol,
            "int" => value.BsonType == BsonType.Int32,
            "timestamp" => value.BsonType == BsonType.Timestamp,
            "long" => value.BsonType == BsonType.Int64,
            "decimal" => value.BsonType == BsonType.Decimal128,
            "minKey" => value.BsonType == BsonType.MinKey,
            "maxKey" => value.BsonType == BsonType.MaxKey,
            "number" => value.IsNumeric,
            _ => true
        };

        /// <summary>JSON Schema 的 <c>type</c>(MongoDB 只认 JSON 那几种;<c>integer</c> 它不支持,这里按整数值宽松处理)。</summary>
        private static bool MatchesJsonType(string name, BsonValue value) => name switch
        {
            "object" => value.BsonType == BsonType.Document,
            "array" => value.BsonType == BsonType.Array,
            "number" => value.IsNumeric,
            "boolean" => value.BsonType == BsonType.Boolean,
            "string" => value.BsonType == BsonType.String,
            "null" => value.BsonType == BsonType.Null,
            "integer" => value.BsonType is BsonType.Int32 or BsonType.Int64
                         || (value.IsNumeric && TryDecimal(value, out decimal d) && decimal.Truncate(d) == d),
            _ => true
        };

        /// <summary>报错时说"当前是什么":用 bsonType 的别名,与规则里写的对得上。</summary>
        private static string BsonTypeName(BsonValue value) => value.BsonType switch
        {
            BsonType.Double => "double",
            BsonType.String => "string",
            BsonType.Document => "object",
            BsonType.Array => "array",
            BsonType.Binary => "binData",
            BsonType.ObjectId => "objectId",
            BsonType.Boolean => "bool",
            BsonType.DateTime => "date",
            BsonType.Null => "null",
            BsonType.RegularExpression => "regex",
            BsonType.Int32 => "int",
            BsonType.Int64 => "long",
            BsonType.Decimal128 => "decimal",
            BsonType.Timestamp => "timestamp",
            _ => value.BsonType.ToString()
        };

        // ── 枚举 ────────────────────────────────────────────────────────────

        private void CheckEnum(BsonDocument schema, BsonValue value, string path)
        {
            if (schema.TryGetValue("enum", out BsonValue options) && options is BsonArray allowed && !allowed.Any(o => Same(o, value)))
            {
                string list = string.Join(" · ", allowed.Take(8).Select(static o => o.IsString ? o.AsString : BsonText.Literal(o)))
                              + (allowed.Count > 8 ? " …" : "");
                Add(path, "enum", loc.Format("Schema_NotInEnum", Name(path), list));
            }
        }

        // ── 数值 ────────────────────────────────────────────────────────────

        private void CheckNumber(BsonDocument schema, BsonValue value, string path)
        {
            // MongoDB 的 $jsonSchema 是 draft 4:exclusiveMinimum 是修饰 minimum 的布尔。
            // 数字写法(draft 6 起)它不认,但写了也不该误报 —— 当作独立的开区间边界。
            bool exclusiveMin = schema.TryGetValue("exclusiveMinimum", out BsonValue exMin) && exMin is BsonBoolean { Value: true };
            bool exclusiveMax = schema.TryGetValue("exclusiveMaximum", out BsonValue exMax) && exMax is BsonBoolean { Value: true };
            if (schema.TryGetValue("minimum", out BsonValue min) && min.IsNumeric)
            {
                int cmp = Compare(value, min);
                if (exclusiveMin ? cmp <= 0 : cmp < 0)
                {
                    Add(path, "minimum", loc.Format(exclusiveMin ? "Schema_MinValueExclusive" : "Schema_MinValue", Name(path), Number(min)));
                }
            }
            if (exMin is not null && exMin.IsNumeric && Compare(value, exMin) <= 0)
            {
                Add(path, "exclusiveMinimum", loc.Format("Schema_MinValueExclusive", Name(path), Number(exMin)));
            }
            if (schema.TryGetValue("maximum", out BsonValue max) && max.IsNumeric)
            {
                int cmp = Compare(value, max);
                if (exclusiveMax ? cmp >= 0 : cmp > 0)
                {
                    Add(path, "maximum", loc.Format(exclusiveMax ? "Schema_MaxValueExclusive" : "Schema_MaxValue", Name(path), Number(max)));
                }
            }
            if (exMax is not null && exMax.IsNumeric && Compare(value, exMax) >= 0)
            {
                Add(path, "exclusiveMaximum", loc.Format("Schema_MaxValueExclusive", Name(path), Number(exMax)));
            }
            if (schema.TryGetValue("multipleOf", out BsonValue step) && step.IsNumeric
                && TryDecimal(value, out decimal v) && TryDecimal(step, out decimal s) && s != 0 && v % s != 0)
            {
                Add(path, "multipleOf", loc.Format("Schema_NotMultipleOf", Name(path), Number(step)));
            }
        }

        private static string Number(BsonValue value) => value.BsonType switch
        {
            BsonType.Double => BsonText.FormatDouble(value.AsDouble, forceDecimalPoint: false),
            BsonType.Decimal128 => value.AsDecimal128.ToString(),
            _ => value.ToInt64().ToString(CultureInfo.InvariantCulture)
        };

        // ── 字符串 ──────────────────────────────────────────────────────────

        private void CheckString(BsonDocument schema, string text, string path)
        {
            // 长度按字符(码点)算,与服务器一致 —— 不是 UTF-16 码元,也不是字节。
            int length = text.EnumerateRunes().Count();
            if (schema.TryGetValue("minLength", out BsonValue minLength) && minLength.IsNumeric && length < minLength.ToInt64())
            {
                Add(path, "minLength", loc.Format("Schema_TooShort", Name(path), minLength.ToInt64()));
            }
            if (schema.TryGetValue("maxLength", out BsonValue maxLength) && maxLength.IsNumeric && length > maxLength.ToInt64())
            {
                Add(path, "maxLength", loc.Format("Schema_TooLong", Name(path), maxLength.ToInt64()));
            }
            if (schema.TryGetValue("pattern", out BsonValue pattern) && pattern.IsString && Matches(pattern.AsString, text) == false)
            {
                Add(path, "pattern", loc.Format("Schema_PatternMismatch", Name(path), pattern.AsString));
            }
        }

        /// <summary>正则匹配;写坏的 pattern 或超时返回 <see langword="null" />(放行)。</summary>
        private static bool? Matches(string pattern, string text)
        {
            try
            {
                return Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, PatternTimeout);
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
        }

        // ── 对象 ────────────────────────────────────────────────────────────

        private void CheckObject(BsonDocument schema, BsonDocument document, string path)
        {
            if (schema.TryGetValue("required", out BsonValue required) && required is BsonArray names)
            {
                foreach (BsonValue name in names)
                {
                    if (name.IsString && !document.Contains(name.AsString))
                    {
                        Add(BsonPath.Join(path, name.AsString), "required", loc.Format("Schema_MissingRequired", BsonPath.Join(path, name.AsString)));
                    }
                }
            }
            if (schema.TryGetValue("minProperties", out BsonValue minProps) && minProps.IsNumeric && document.ElementCount < minProps.ToInt64())
            {
                Add(path, "minProperties", loc.Format("Schema_TooFewFields", Name(path), minProps.ToInt64()));
            }
            if (schema.TryGetValue("maxProperties", out BsonValue maxProps) && maxProps.IsNumeric && document.ElementCount > maxProps.ToInt64())
            {
                Add(path, "maxProperties", loc.Format("Schema_TooManyFields", Name(path), maxProps.ToInt64()));
            }

            BsonDocument? properties = schema.TryGetValue("properties", out BsonValue p) ? p as BsonDocument : null;
            BsonDocument? patterns = schema.TryGetValue("patternProperties", out BsonValue pp) ? pp as BsonDocument : null;
            BsonValue? additional = schema.TryGetValue("additionalProperties", out BsonValue ap) ? ap : null;
            foreach (BsonElement element in document)
            {
                string childPath = BsonPath.Join(path, element.Name);
                bool declared = false;
                if (properties is not null && properties.TryGetValue(element.Name, out BsonValue sub))
                {
                    declared = true;
                    if (sub is BsonDocument subSchema)
                    {
                        Check(subSchema, element.Value, childPath);
                    }
                }
                if (patterns is not null)
                {
                    foreach (BsonElement rule in patterns)
                    {
                        if (Matches(rule.Name, element.Name) == true)
                        {
                            declared = true;
                            if (rule.Value is BsonDocument patternSchema)
                            {
                                Check(patternSchema, element.Value, childPath);
                            }
                        }
                    }
                }
                if (declared)
                {
                    continue;
                }
                // additionalProperties: false 时连 _id 也要在 properties 里声明 —— 服务器就是这么判的,
                // 预检照抄,免得用户在这里看着没问题、一保存被拒。
                if (additional is BsonBoolean { Value: false })
                {
                    Add(childPath, "additionalProperties", loc.Format("Schema_FieldNotAllowed", childPath));
                }
                else if (additional is BsonDocument additionalSchema)
                {
                    Check(additionalSchema, element.Value, childPath);
                }
            }
        }

        // ── 数组 ────────────────────────────────────────────────────────────

        private void CheckArray(BsonDocument schema, BsonArray array, string path)
        {
            if (schema.TryGetValue("minItems", out BsonValue minItems) && minItems.IsNumeric && array.Count < minItems.ToInt64())
            {
                Add(path, "minItems", loc.Format("Schema_TooFewItems", Name(path), minItems.ToInt64()));
            }
            if (schema.TryGetValue("maxItems", out BsonValue maxItems) && maxItems.IsNumeric && array.Count > maxItems.ToInt64())
            {
                Add(path, "maxItems", loc.Format("Schema_TooManyItems", Name(path), maxItems.ToInt64()));
            }
            // 两两比较是平方级:超长数组不做(预检宁可漏报,不卡界面)。
            if (schema.TryGetValue("uniqueItems", out BsonValue unique) && unique is BsonBoolean { Value: true } && array.Count <= 2000
                && HasDuplicate(array))
            {
                Add(path, "uniqueItems", loc.Format("Schema_DuplicateItems", Name(path)));
            }
            if (!schema.TryGetValue("items", out BsonValue items))
            {
                return;
            }
            if (items is BsonDocument each)
            {
                for (int i = 0; i < array.Count; i++)
                {
                    Check(each, array[i], BsonPath.Join(path, i.ToString(CultureInfo.InvariantCulture)));
                }
            }
            else if (items is BsonArray tuple)
            {
                for (int i = 0; i < array.Count; i++)
                {
                    string itemPath = BsonPath.Join(path, i.ToString(CultureInfo.InvariantCulture));
                    if (i < tuple.Count)
                    {
                        if (tuple[i] is BsonDocument positional)
                        {
                            Check(positional, array[i], itemPath);
                        }
                        continue;
                    }
                    if (schema.TryGetValue("additionalItems", out BsonValue extra))
                    {
                        if (extra is BsonBoolean { Value: false })
                        {
                            Add(path, "additionalItems", loc.Format("Schema_ExtraItems", Name(path), tuple.Count));
                            break;
                        }
                        if (extra is BsonDocument extraSchema)
                        {
                            Check(extraSchema, array[i], itemPath);
                        }
                    }
                }
            }
        }

        private static bool HasDuplicate(BsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                for (int j = i + 1; j < array.Count; j++)
                {
                    if (Same(array[i], array[j]))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // ── 组合 ────────────────────────────────────────────────────────────

        private void CheckCombinators(BsonDocument schema, BsonValue value, string path)
        {
            if (schema.TryGetValue("allOf", out BsonValue allOf) && allOf is BsonArray all)
            {
                foreach (BsonDocument sub in all.OfType<BsonDocument>())
                {
                    Check(sub, value, path);
                }
            }
            if (schema.TryGetValue("anyOf", out BsonValue anyOf) && anyOf is BsonArray any && any.Count > 0
                && !any.OfType<BsonDocument>().Any(sub => Passes(sub, value, path)))
            {
                Add(path, "anyOf", loc.Format("Schema_NoAnyOf", Name(path)));
            }
            if (schema.TryGetValue("oneOf", out BsonValue oneOf) && oneOf is BsonArray one && one.Count > 0)
            {
                int passed = one.OfType<BsonDocument>().Count(sub => Passes(sub, value, path));
                if (passed != 1)
                {
                    Add(path, "oneOf", loc.Format("Schema_NotOneOf", Name(path), passed));
                }
            }
            if (schema.TryGetValue("not", out BsonValue not) && not is BsonDocument negated && Passes(negated, value, path))
            {
                Add(path, "not", loc.Format("Schema_MatchesNot", Name(path)));
            }
        }

        /// <summary>试探一个分支:只数违规,不收集。</summary>
        private bool Passes(BsonDocument schema, BsonValue value, string path)
        {
            var probe = new Walker(loc, collect: false);
            probe.Check(schema, value, path);
            return probe.Count == 0;
        }
    }

    // ── 比较 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 与服务器比较语义一致的相等:数值跨类型按值比(<c>1</c> 与 <c>1.0</c> 相等,
    /// enum 里写 <c>1</c>、文档里存 <c>NumberLong(1)</c> 不该报错),其余按 BSON 相等。
    /// </summary>
    internal static bool Same(BsonValue a, BsonValue b) =>
        a.IsNumeric && b.IsNumeric ? Compare(a, b) == 0 : a.Equals(b);

    /// <summary>数值比较:能用 decimal 就用 decimal(Decimal128 与 Int64 不丢精度),否则退回 double。</summary>
    internal static int Compare(BsonValue a, BsonValue b)
    {
        if (TryDecimal(a, out decimal x) && TryDecimal(b, out decimal y))
        {
            return x.CompareTo(y);
        }
        return a.ToDouble().CompareTo(b.ToDouble());
    }

    private static bool TryDecimal(BsonValue value, out decimal result)
    {
        result = 0;
        try
        {
            switch (value.BsonType)
            {
                case BsonType.Int32:
                    result = value.AsInt32;
                    return true;
                case BsonType.Int64:
                    result = value.AsInt64;
                    return true;
                case BsonType.Double:
                    double d = value.AsDouble;
                    if (double.IsNaN(d) || double.IsInfinity(d) || Math.Abs(d) > 7.9e28)
                    {
                        return false;
                    }
                    result = (decimal)d;
                    return true;
                case BsonType.Decimal128:
                    result = Decimal128.ToDecimal(value.AsDecimal128);
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentException or InvalidOperationException)
        {
            // Decimal128 的 NaN / 无穷 / 超出 decimal 范围:退回 double 比较。
            return false;
        }
    }
}
