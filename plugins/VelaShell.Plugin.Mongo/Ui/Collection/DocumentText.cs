using System.Globalization;
using System.Text;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>排版好的文档文本 + 每一行对应的字段路径(行标记"已修改 · 原值"要知道某个路径落在第几行)。</summary>
/// <param name="Text">全文。</param>
/// <param name="LinePaths">第 i 行(0 起)是哪个字段的起始行;收尾括号行为 <see langword="null" />。</param>
internal sealed record PrintedCard(string Text, IReadOnlyList<string?> LinePaths)
{
    /// <summary>某个路径(或它最近的、印出来了的祖先)所在的行号(1 起);整份文档的头一行兜底。</summary>
    public int LineOf(string path)
    {
        string? probe = path;
        while (probe is not null)
        {
            for (int i = 0; i < LinePaths.Count; i++)
            {
                if (LinePaths[i] == probe)
                {
                    return i + 1;
                }
            }
            int dot = probe.LastIndexOf('.');
            probe = dot > 0 ? probe[..dot] : null;
        }
        return 1;
    }
}

/// <summary>
/// JSON 卡片的排版。不直接用 <see cref="BsonText.Pretty" />:那是给复制 / 导出的"标准"形态,
/// 这里要的是"读着舒服 + 知道每行是哪个字段" —— 短的标量容器压成一行(<c>customer: { id: …, name: "李娜" }</c>),
/// 长的才展开;同时记下每一行的路径给行标记用。三种扩展 JSON 写法共用一套排版,只是标量的写法不同。
/// <para>
/// 只读卡片用<b>预览</b>写法(给了 <c>fold</c>):对象数组折成 <c>[ …2 项 ]</c>、嵌套里的 ObjectId 与长字符串截短 ——
/// 一页几十张卡片,每张都把 items 全摊开,一屏只看得到一两份文档。完整文本在「编辑」「复制」与检查器的 JSON 页里。
/// </para>
/// </summary>
internal static class CardPrinter
{
    private const int InlineLimit = 88;

    /// <summary>排版一份文档。</summary>
    /// <param name="document">文档。</param>
    /// <param name="mode">扩展 JSON 写法。</param>
    /// <param name="fold">预览写法里对象数组的折叠文字(<c>…{0} 项</c>);<see langword="null" /> = 完整写法(编辑用)。</param>
    public static PrintedCard Print(BsonDocument document, EjsonMode mode, Func<int, string>? fold = null)
    {
        var builder = new StringBuilder();
        var lines = new List<string?> { null };
        new Writer(builder, lines, mode, fold).Container(document, null, 0);
        return new(builder.ToString(), lines);
    }

    /// <summary>单行写法(容器写成 <c>{ a: 1, b: 2 }</c>,与 mongosh 的单行输出一致)。</summary>
    internal static string Inline(BsonValue value, EjsonMode mode) => new Writer(new StringBuilder(), [], mode, null).Inline(value, nested: false);

    private sealed class Writer(StringBuilder b, List<string?> lines, EjsonMode mode, Func<int, string>? fold)
    {
        private bool Compact => fold is not null;

        /// <summary>写一个容器(多行)。</summary>
        public void Container(BsonValue container, string? path, int depth)
        {
            bool isArray = container is BsonArray;
            IReadOnlyList<(string Name, BsonValue Value)> children = container is BsonDocument doc
                ? [.. doc.Elements.Select(static e => (e.Name, e.Value))]
                : [.. container.AsBsonArray.Select(static (v, i) => (i.ToString(CultureInfo.InvariantCulture), v))];
            b.Append(isArray ? '[' : '{');
            if (children.Count == 0)
            {
                b.Append(isArray ? ']' : '}');
                return;
            }
            for (int i = 0; i < children.Count; i++)
            {
                (string name, BsonValue value) = children[i];
                string childPath = BsonPath.Join(path, name);
                b.Append('\n');
                lines.Add(childPath);
                Indent(depth + 1);
                if (!isArray)
                {
                    b.Append(Key(name)).Append(": ");
                }
                Value(value, childPath, depth + 1);
                if (i < children.Count - 1)
                {
                    b.Append(',');
                }
            }
            b.Append('\n');
            lines.Add(null);
            Indent(depth);
            b.Append(isArray ? ']' : '}');
        }

        private void Value(BsonValue value, string path, int depth)
        {
            if (Compact && value is BsonArray { Count: > 0 } array && array.Any(static v => v is BsonDocument or BsonArray))
            {
                b.Append("[ ").Append(fold!(array.Count)).Append(" ]");
                return;
            }
            if (value is BsonDocument or BsonArray && !Fits(value))
            {
                Container(value, path, depth);
                return;
            }
            b.Append(Inline(value, nested: false));
        }

        /// <summary>单行写法;<paramref name="nested" /> 为真时(容器内部)预览写法会截短 ObjectId 与长字符串。</summary>
        public string Inline(BsonValue value, bool nested)
        {
            switch (value)
            {
                case BsonDocument doc:
                    return doc.ElementCount == 0
                        ? "{}"
                        : "{ " + string.Join(", ", doc.Elements.Select(e => Key(e.Name) + ": " + Inline(e.Value, nested: true))) + " }";
                case BsonArray array:
                    return array.Count == 0 ? "[]" : "[ " + string.Join(", ", array.Select(v => Inline(v, nested: true))) + " ]";
                case BsonObjectId id when Compact && nested && mode == EjsonMode.Shell:
                    return $"ObjectId(\"{id.Value.ToString()[..12]}…\")";
                case BsonString text when Compact && nested && text.Value.Length > 24:
                    return BsonText.Quote(text.Value[..20] + "…");
                default:
                    return mode == EjsonMode.Shell ? BsonText.Literal(value) : BsonText.Compact(value, mode);
            }
        }

        /// <summary>压成一行放得下吗:只含标量、且不超过一行的宽度。</summary>
        private bool Fits(BsonValue value)
        {
            bool scalarsOnly = value switch
            {
                BsonDocument doc => doc.Elements.All(static e => e.Value is not (BsonDocument or BsonArray)),
                BsonArray array => array.All(static v => v is not (BsonDocument or BsonArray)),
                _ => true
            };
            return scalarsOnly && Inline(value, nested: false).Length <= InlineLimit;
        }

        private string Key(string name) => mode == EjsonMode.Shell ? BsonText.FieldName(name) : BsonText.Quote(name);

        private void Indent(int depth) => b.Append(' ', depth * 2);
    }
}


/// <summary>
/// 在一段(可能写了一半的)mongosh / JSON 文本里认出"光标落在哪个字段的键或值上",以及每个键在第几行。
/// 容错地扫:字符串、注释跳过,括号配对,键可以是裸标识符或带引号 —— 编辑器里边打字边问,文本多半不完整。
/// </summary>
internal static class CaretPathScanner
{
    /// <summary>扫描结果。</summary>
    /// <param name="Path">光标所在的字段路径(数组下标保留,<c>items.0.sku</c>);在根上为空串。</param>
    /// <param name="InValue">光标在值的位置(冒号之后)。</param>
    /// <param name="TokenStart">光标所在那个词 / 值的起点(补全的替换起点)。</param>
    /// <param name="ParentPath">光标所在的对象的路径(键位置补全时列它的子字段)。</param>
    /// <param name="KeyLines">每个键路径首次出现的行号(1 起)。</param>
    internal sealed record Result(string Path, bool InValue, int TokenStart, string ParentPath, IReadOnlyDictionary<string, int> KeyLines);

    private sealed class Frame(string path, bool isArray)
    {
        /// <summary>这一层的路径。</summary>
        public string Path { get; } = path;

        /// <summary>是数组(元素按下标计)。</summary>
        public bool IsArray { get; } = isArray;

        /// <summary>当前键(对象里)。</summary>
        public string? Key { get; set; }

        /// <summary>已过冒号、在值的位置。</summary>
        public bool InValue { get; set; }

        /// <summary>当前下标(数组里)。</summary>
        public int Index { get; set; }
    }

    /// <summary>扫到 <paramref name="caret" /> 为止(<paramref name="caret" /> 取 -1 扫全文,只要行号表)。</summary>
    public static Result Scan(string text, int caret)
    {
        int end = caret < 0 ? text.Length : Math.Min(caret, text.Length);
        var stack = new Stack<Frame>();
        var lines = new Dictionary<string, int>(StringComparer.Ordinal);
        int line = 1;
        int i = 0;
        int tokenStart = 0;
        while (i < (caret < 0 ? text.Length : end))
        {
            char c = text[i];
            if (c == '\n')
            {
                line++;
                i++;
                tokenStart = i;
                continue;
            }
            if (c is '"' or '\'')
            {
                int close = ShellJson.SkipString(text, i);
                if (stack.TryPeek(out Frame? frame) && !frame.IsArray && !frame.InValue)
                {
                    // 带引号的键。
                    string key = text[(i + 1)..Math.Max(i + 1, close - 1)];
                    frame.Key = key;
                    lines.TryAdd(Join(frame.Path, key), line);
                }
                tokenStart = i;
                i = close;
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] is '/' or '*')
            {
                int stop = text[i + 1] == '/' ? text.IndexOf('\n', i) : text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = stop < 0 ? text.Length : (text[i + 1] == '/' ? stop : stop + 2);
                continue;
            }
            if (c is '{' or '[')
            {
                string path = "";
                if (stack.TryPeek(out Frame? parent))
                {
                    path = parent.IsArray
                        ? Join(parent.Path, parent.Index.ToString(CultureInfo.InvariantCulture))
                        : Join(parent.Path, parent.Key ?? "");
                }
                stack.Push(new Frame(path, c == '['));
                i++;
                tokenStart = i;
                continue;
            }
            if (c is '}' or ']')
            {
                if (stack.Count > 0)
                {
                    stack.Pop();
                }
                i++;
                tokenStart = i;
                continue;
            }
            if (c == ':' && stack.TryPeek(out Frame? obj) && !obj.IsArray)
            {
                obj.InValue = true;
                i++;
                tokenStart = i;
                continue;
            }
            if (c == ',' && stack.TryPeek(out Frame? current))
            {
                if (current.IsArray)
                {
                    current.Index++;
                }
                else
                {
                    current.Key = null;
                    current.InValue = false;
                }
                i++;
                tokenStart = i;
                continue;
            }
            if (ShellJson.IsIdentifierStart(c) && stack.TryPeek(out Frame? holder) && !holder.IsArray && !holder.InValue)
            {
                int start = i;
                while (i < text.Length && (ShellJson.IsIdentifierPart(text[i]) || text[i] == '.'))
                {
                    i++;
                }
                holder.Key = text[start..i];
                lines.TryAdd(Join(holder.Path, holder.Key), line);
                tokenStart = start;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                i++;
                tokenStart = i;
                continue;
            }
            i++;
        }

        if (!stack.TryPeek(out Frame? top))
        {
            return new("", false, end, "", lines);
        }
        // 光标所在的那个词:往回找到空白 / 标点为止。
        int wordStart = end;
        while (wordStart > 0 && !char.IsWhiteSpace(text[wordStart - 1]) && text[wordStart - 1] is not (',' or ':' or '{' or '[' or '(' ))
        {
            wordStart--;
        }
        if (top.IsArray)
        {
            return new(Join(top.Path, top.Index.ToString(CultureInfo.InvariantCulture)), true, wordStart, top.Path, lines);
        }
        return top.InValue
            ? new(Join(top.Path, top.Key ?? ""), true, wordStart, top.Path, lines)
            : new(top.Path, false, wordStart, top.Path, lines);
    }

    private static string Join(string parent, string name) => parent.Length == 0 ? name : name.Length == 0 ? parent : $"{parent}.{name}";
}
