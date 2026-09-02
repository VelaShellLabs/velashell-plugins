using System.Text;

namespace VelaShell.Plugin.Redis.Tests;

/// <summary>
/// 值的解码链。
/// <para>
/// 这一层守的是两条:<b>解得开的要真解开</b>,<b>解不开的要如实说</b>。
/// 后半句同样重要 —— 一个把 Zstd 悄悄当成"没压过"处理的客户端,会把一段乱码
/// 当成值展示给你,而你完全看不出哪一步出了错。
/// </para>
/// </summary>
[TestClass]
public sealed class RedisValueCodecTests
{
    private static readonly byte[] Json = Encoding.UTF8.GetBytes("{\"version\":7,\"currency\":\"CNY\"}");

    // ── 解压 ──────────────────────────────────────────────────────

    /// <summary>内置的三种压缩:压回去再解开,必须一个字节不差 —— 这是"可写回"的前提。</summary>
    [TestMethod]
    public void BundledCompressions_RoundTripExactly()
    {
        foreach (RedisCompression codec in new[]
                 { RedisCompression.GZip, RedisCompression.Deflate, RedisCompression.Brotli })
        {
            Assert.IsTrue(RedisValueCodec.TryCompress(Json, codec, out byte[] packed, out string? packError),
                $"{codec} 压缩失败:{packError}");
            Assert.IsTrue(RedisValueCodec.TryDecompress(packed, codec, out byte[] back, out string? unpackError),
                $"{codec} 解压失败:{unpackError}");
            CollectionAssert.AreEqual(Json, back, $"{codec} 往返丢字节。");
            Assert.IsTrue(RedisValueCodec.IsReversible(codec), $"{codec} 两个方向都做得到,应判为可逆。");
        }
    }

    /// <summary>不解压这一档是恒等变换,而且可逆。</summary>
    [TestMethod]
    public void None_IsIdentityAndReversible()
    {
        Assert.IsTrue(RedisValueCodec.TryDecompress(Json, RedisCompression.None, out byte[] same, out _));
        CollectionAssert.AreEqual(Json, same);
        Assert.IsTrue(RedisValueCodec.IsReversible(RedisCompression.None));
    }

    /// <summary>
    /// 未内置的解码器**如实拒绝**:既不假装解开了,也不悄悄当成没压过。
    /// <para>这三种各需要一个额外的第三方依赖(本仓库对随插件分发的依赖是逐个过许可证的)。</para>
    /// </summary>
    [TestMethod]
    public void UnbundledCompressions_RefuseHonestly()
    {
        foreach (RedisCompression codec in new[]
                 { RedisCompression.Zstd, RedisCompression.Lz4, RedisCompression.Snappy })
        {
            Assert.IsFalse(RedisValueCodec.IsAvailable(codec));
            Assert.IsFalse(RedisValueCodec.IsReversible(codec), "解不开的东西谈不上写得回去。");
            Assert.IsFalse(RedisValueCodec.TryDecompress([1, 2, 3], codec, out _, out string? error));
            Assert.IsNotNull(error);
            Assert.Contains("not bundled", error, "拒绝的理由必须说清是「没带」,而不是「坏了」。");
        }
    }

    /// <summary>魔数识别:认得出来是第一步,能不能解开是第二步,两件事分开。</summary>
    [TestMethod]
    public void DetectCompression_ReadsMagicBytes()
    {
        RedisValueCodec.TryCompress(Json, RedisCompression.GZip, out byte[] gzip, out _);
        RedisValueCodec.TryCompress(Json, RedisCompression.Deflate, out byte[] zlib, out _);

        Assert.AreEqual(RedisCompression.GZip, RedisValueCodec.DetectCompression(gzip));
        Assert.AreEqual(RedisCompression.Deflate, RedisValueCodec.DetectCompression(zlib));
        Assert.AreEqual(RedisCompression.Zstd, RedisValueCodec.DetectCompression([0x28, 0xB5, 0x2F, 0xFD, 0x00]));
        Assert.AreEqual(RedisCompression.Lz4, RedisValueCodec.DetectCompression([0x04, 0x22, 0x4D, 0x18, 0x00]));
        Assert.AreEqual(RedisCompression.None, RedisValueCodec.DetectCompression(Json));
    }

    /// <summary>
    /// 有 GZip 魔数但解不开的字节,不该被当成 GZip。
    /// <para>这正是面板"只有真的解开了才作数"那条规则的依据:否则用户会对着一个空编辑框
    /// 和一句"解码失败",而它其实是一段可以照常按转义编辑的普通字节。</para>
    /// </summary>
    [TestMethod]
    public void GzipMagic_WithoutValidBody_FailsToDecompress()
    {
        byte[] fake = [0x1F, 0x8B, 0x08, 0x00, 0xC3, 0x28, 0x00, 0x03];

        Assert.AreEqual(RedisCompression.GZip, RedisValueCodec.DetectCompression(fake), "魔数是认得出来的。");
        Assert.IsFalse(RedisValueCodec.TryDecompress(fake, RedisCompression.GZip, out _, out string? error));
        Assert.IsNotNull(error);
    }

    // ── 反序列化 ──────────────────────────────────────────────────

    /// <summary>MessagePack:<c>{"a":1,"b":[true,null]}</c> 的规范编码。</summary>
    [TestMethod]
    public void MsgPack_DecodesMapsArraysAndScalars()
    {
        byte[] raw = [0x82, 0xA1, 0x61, 0x01, 0xA1, 0x62, 0x92, 0xC3, 0xC0];

        Assert.IsTrue(RedisValueCodec.TryDeserialize(raw, RedisSerialization.MsgPack, out string text, out string? error), error);
        Assert.AreEqual("{\"a\": 1, \"b\": [true, null]}", text);
    }

    /// <summary>解不完整就不算 —— 半截的 MessagePack 不该被"尽力而为"地解出一半。</summary>
    [TestMethod]
    public void MsgPack_TruncatedDocument_IsRejected() =>
        Assert.IsFalse(RedisValueCodec.TryDeserialize([0x82, 0xA1, 0x61], RedisSerialization.MsgPack, out _, out _));

    /// <summary>
    /// Protobuf 没有 schema 也能转储:字段号 + 线类型 + 值,足够回答"这个键里存了什么"。
    /// <para>样本:字段 1 = varint 150,字段 2 = 字符串 "hi"。</para>
    /// </summary>
    [TestMethod]
    public void Protobuf_DumpsFieldNumbersAndValues()
    {
        byte[] raw = [0x08, 0x96, 0x01, 0x12, 0x02, 0x68, 0x69];

        Assert.IsTrue(RedisValueCodec.TryDeserialize(raw, RedisSerialization.Protobuf, out string text, out string? error), error);
        Assert.Contains("1: 150", text);
        Assert.Contains("\"hi\"", text);
        Assert.Contains("varint", text, "线类型要写出来 —— 没有 schema 时它是唯一的类型线索。");
    }

    /// <summary>废弃的 group 线类型(3/4)一出现就说明这不是 protobuf,不猜。</summary>
    [TestMethod]
    public void Protobuf_GroupWireType_IsRejected() =>
        Assert.IsFalse(RedisValueCodec.TryDeserialize([0x0B, 0x00], RedisSerialization.Protobuf, out _, out _));

    /// <summary>PHP <c>serialize()</c>:数组、字符串(按**字节**计长)、布尔。</summary>
    [TestMethod]
    public void Php_DecodesArraysStringsAndBooleans()
    {
        // 张三 在 UTF-8 下是 6 字节 —— PHP 的 s: 长度算的正是字节数,不是字符数。
        byte[] raw = Encoding.UTF8.GetBytes("a:2:{s:4:\"name\";s:6:\"张三\";i:1;b:1;}");

        Assert.IsTrue(RedisValueCodec.TryDeserialize(raw, RedisSerialization.Php, out string text, out string? error), error);
        Assert.Contains("\"name\": \"张三\"", text);
        Assert.Contains("1: true", text);
    }

    /// <summary>未内置的两种(Java / Pickle)同样如实拒绝。</summary>
    [TestMethod]
    public void UnbundledSerializations_RefuseHonestly()
    {
        foreach (RedisSerialization codec in new[] { RedisSerialization.Java, RedisSerialization.Pickle })
        {
            Assert.IsFalse(RedisValueCodec.IsAvailable(codec));
            Assert.IsFalse(RedisValueCodec.TryDeserialize([0xAC, 0xED, 0x00, 0x05], codec, out _, out string? error));
            Assert.Contains("not bundled", error!);
        }
    }

    /// <summary>Java 的魔数认得出来(即便解不开)—— 说明行据此告诉用户这是什么。</summary>
    [TestMethod]
    public void DetectSerialization_RecognisesJavaMagic() =>
        Assert.AreEqual(RedisSerialization.Java,
            RedisValueCodec.DetectSerialization([0xAC, 0xED, 0x00, 0x05, 0x74, 0x00, 0x01, 0x61]));

    /// <summary>
    /// **普通文本一律不猜**。一段 JSON 恰好也能被 protobuf 的线格式勉强解开,
    /// 而那个结论毫无意义,还会把一个本来可以直接编辑的值变成只读转储。
    /// </summary>
    [TestMethod]
    public void DetectSerialization_LeavesPlainTextAlone() =>
        Assert.AreEqual(RedisSerialization.None, RedisValueCodec.DetectSerialization(Json));

    /// <summary>PHP 的 serialize() 是文本,靠首两个字符认。</summary>
    [TestMethod]
    public void DetectSerialization_RecognisesPhpText() =>
        Assert.AreEqual(RedisSerialization.Php,
            RedisValueCodec.DetectSerialization(Encoding.UTF8.GetBytes("a:1:{i:0;b:1;}")));

    // ── JSON 视图 ─────────────────────────────────────────────────

    /// <summary>合法 JSON 缩进排版。</summary>
    [TestMethod]
    public void TryPrettyJson_IndentsValidJson()
    {
        Assert.IsTrue(RedisValueCodec.TryPrettyJson("{\"a\":1}", out string pretty, out _));
        Assert.Contains("\n", pretty);
        Assert.Contains("\"a\": 1", pretty);
    }

    /// <summary>不是 JSON 就说不是,并把解析错误带出来给界面显示 —— 不静默吞掉。</summary>
    [TestMethod]
    public void TryPrettyJson_ReportsInvalidJson()
    {
        Assert.IsFalse(RedisValueCodec.TryPrettyJson("{oops", out _, out string? error));
        Assert.IsNotNull(error);
    }

    /// <summary>空文本不算错(新建键时值可以是空的)。</summary>
    [TestMethod]
    public void TryPrettyJson_EmptyText_IsNotAnError() =>
        Assert.IsTrue(RedisValueCodec.TryPrettyJson("   ", out _, out _));
}
