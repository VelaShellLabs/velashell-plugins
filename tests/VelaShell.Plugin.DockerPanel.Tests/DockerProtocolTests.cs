using System.Buffers.Binary;
using System.Text;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Tests;

/// <summary>
/// 线上协议那一层的测试。
/// <para>
/// 这些是**改走 HTTP API 之后新出现的失败模式**:8 字节帧头解错会把日志劈成两半、
/// tar 的校验和算反了 daemon 会说文件损坏、拉取失败时 HTTP 仍然是 200。
/// 它们都不会在编译期被发现,而且在真机上表现成"偶尔少几行"这类最难查的现象。
/// </para>
/// </summary>
[TestClass]
public class DockerProtocolTests
{
    // ── 多路复用帧 ────────────────────────────────────────────

    private static byte[] Frame(DockerStreamKind kind, string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var frame = new byte[8 + payload.Length];
        frame[0] = (byte)kind;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4, 4), payload.Length);
        payload.CopyTo(frame, 8);
        return frame;
    }

    [TestMethod]
    public async Task Decoder_SplitsStdoutAndStderrIntoSeparateStreams()
    {
        byte[] data = [.. Frame(DockerStreamKind.StdOut, "hello\n"), .. Frame(DockerStreamKind.StdErr, "boom\n")];
        List<DockerLogLine> lines = [];
        await new DockerFrameDecoder(tty: false, timestamps: false)
            .ReadAsync(new MemoryStream(data), lines.Add, CancellationToken.None);

        Assert.HasCount(2, lines);
        Assert.AreEqual(DockerStreamKind.StdOut, lines[0].Kind);
        Assert.AreEqual("hello", lines[0].Text);
        // 标准错误单独一条流:合并进 stdout 会让"命令失败了"和"命令没有输出"长得一模一样。
        Assert.AreEqual(DockerStreamKind.StdErr, lines[1].Kind);
        Assert.AreEqual("boom", lines[1].Text);
    }

    [TestMethod]
    public async Task Decoder_KeepsPayloadIntactWhenItContainsNewlines()
    {
        // 帧长度字段里出现 0x0A 是常态 —— 按行切分会把一帧劈成两半,这正是不能用
        // 文本行模型承载这条流的原因。
        var data = Frame(DockerStreamKind.StdOut, "a\nb\nc\n");
        List<DockerLogLine> lines = [];
        await new DockerFrameDecoder(tty: false, timestamps: false)
            .ReadAsync(new MemoryStream(data), lines.Add, CancellationToken.None);

        Assert.AreSequenceEqual(["a", "b", "c"], [.. lines.Select(l => l.Text)]);
    }

    [TestMethod]
    public async Task Decoder_HoldsBackHalfLinesUntilTheyAreComplete()
    {
        byte[] data = [.. Frame(DockerStreamKind.StdOut, "par"), .. Frame(DockerStreamKind.StdOut, "tial\n")];
        List<DockerLogLine> lines = [];
        await new DockerFrameDecoder(tty: false, timestamps: false)
            .ReadAsync(new MemoryStream(data), lines.Add, CancellationToken.None);

        // 半行开头的日志比少几行更难读 —— 跨帧的半行必须攒起来。
        Assert.HasCount(1, lines);
        Assert.AreEqual("partial", lines[0].Text);
    }

    [TestMethod]
    public async Task Decoder_FlushesTheLastLineWithoutTrailingNewline()
    {
        var data = Frame(DockerStreamKind.StdOut, "no newline at eof");
        List<DockerLogLine> lines = [];
        await new DockerFrameDecoder(tty: false, timestamps: false)
            .ReadAsync(new MemoryStream(data), lines.Add, CancellationToken.None);

        Assert.HasCount(1, lines);
        Assert.AreEqual("no newline at eof", lines[0].Text);
    }

    [TestMethod]
    public async Task Decoder_SurvivesUtf8CharactersSplitAcrossFrames()
    {
        // "容" 是三个字节;把它劈在两帧之间,天真的实现会吐出两个 U+FFFD。
        var full = Encoding.UTF8.GetBytes("容器\n");
        var first = Frame(DockerStreamKind.StdOut, "");
        var head = BuildFrame(DockerStreamKind.StdOut, full.AsSpan()[..2]);
        var tail = BuildFrame(DockerStreamKind.StdOut, full.AsSpan()[2..]);
        List<DockerLogLine> lines = [];
        await new DockerFrameDecoder(tty: false, timestamps: false)
            .ReadAsync(new MemoryStream([.. head, .. tail]), lines.Add, CancellationToken.None);

        Assert.HasCount(1, lines);
        Assert.AreEqual("容器", lines[0].Text);
        Assert.IsFalse(lines[0].Text.Contains('�'), "UTF-8 序列被跨帧劈开时不应产生替换字符。");
        Assert.HasCount(8, first);
    }

    private static byte[] BuildFrame(DockerStreamKind kind, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[8 + payload.Length];
        frame[0] = (byte)kind;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4, 4), payload.Length);
        payload.CopyTo(frame.AsSpan(8));
        return frame;
    }

    [TestMethod]
    public async Task Decoder_InTtyModeTreatsEverythingAsRawStdout()
    {
        // 有 TTY 时没有帧头 —— 按 8 字节头去解会把前八个字符吃掉。
        var data = Encoding.UTF8.GetBytes("raw tty line\n");
        List<DockerLogLine> lines = [];
        await new DockerFrameDecoder(tty: true, timestamps: false)
            .ReadAsync(new MemoryStream(data), lines.Add, CancellationToken.None);

        Assert.HasCount(1, lines);
        Assert.AreEqual("raw tty line", lines[0].Text);
    }

    [TestMethod]
    public void TrySplitTimestamp_PullsTheRfc3339PrefixOutOfTheLine()
    {
        Assert.IsTrue(DockerFrameDecoder.TrySplitTimestamp("2026-08-21T09:41:22.118000000Z hello world", out var stamp, out var rest));
        Assert.AreEqual("hello world", rest);
        Assert.AreEqual(2026, stamp.Year);
    }

    [TestMethod]
    public void TrySplitTimestamp_LeavesOrdinaryLinesAlone()
    {
        // 正文里恰好有空格的普通行不能被当成带时间戳的行切掉一段。
        Assert.IsFalse(DockerFrameDecoder.TrySplitTimestamp("GET /health 200", out _, out var rest));
        Assert.AreEqual("GET /health 200", rest);
    }

    // ── tar ───────────────────────────────────────────────────

    [TestMethod]
    public async Task Tar_RoundTripsASingleFile()
    {
        var content = Encoding.UTF8.GetBytes("server {\n  listen 80;\n}\n");
        var archive = TarUtil.CreateSingleFile("default.conf", content);
        var entry =
            await TarUtil.ReadFirstFileAsync(new MemoryStream(archive), 1 << 20, CancellationToken.None);

        Assert.IsNotNull(entry);
        Assert.AreEqual("default.conf", entry.Value.Name);
        Assert.AreSequenceEqual(content, entry.Value.Content);
    }

    [TestMethod]
    public void Tar_HeaderIsBlockAlignedAndEndsWithTwoZeroBlocks()
    {
        var archive = TarUtil.CreateSingleFile("a.txt", "x"u8);
        // 512 头 + 512 补齐的内容 + 两个全零块。长度不对的话 daemon 会说归档被截断。
        Assert.HasCount(512 * 4, archive);
        Assert.IsTrue(archive[^1024..].All(b => b == 0));
    }

    [TestMethod]
    public void Tar_ChecksumIsComputedOverSpacesInTheChecksumField()
    {
        var archive = TarUtil.CreateSingleFile("a.txt", "x"u8);
        uint expected = 0;
        for (var i = 0; i < 512; i++)
        {
            // 求和时校验和字段要按 8 个空格计 —— 顺序反了 tar 会说文件损坏。
            expected += i is >= 148 and < 156 ? (byte)' ' : archive[i];
        }
        uint stored = 0;
        for (var i = 148; i < 155; i++)
        {
            if (archive[i] is >= (byte)'0' and <= (byte)'7')
            {
                stored = (stored * 8) + (uint)(archive[i] - '0');
            }
        }
        Assert.AreEqual(expected, stored);
    }

    [TestMethod]
    public async Task Tar_RefusesFilesLargerThanTheEditingCap()
    {
        var archive = TarUtil.CreateSingleFile("big.bin", new byte[4096]);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            TarUtil.ReadFirstFileAsync(new MemoryStream(archive), 1024, CancellationToken.None));
    }

    [TestMethod]
    public async Task Tar_ExtractStreamsTheFileOutWithoutTheEditingCap()
    {
        // "另存为"这条路不该受在线编辑那个 2 MB 上限管 ——
        // 几百兆的日志正是用户最想取下来的东西。
        var payload = new byte[300_000];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }
        var archive = TarUtil.CreateSingleFile("app.log", payload);
        var output = new MemoryStream();

        var written = await TarUtil.ExtractFirstFileAsync(new MemoryStream(archive), output, CancellationToken.None);

        Assert.AreEqual(payload.Length, written);
        Assert.AreSequenceEqual(payload, output.ToArray());
    }

    [TestMethod]
    public async Task Tar_ExtractReportsZeroWhenTheArchiveHoldsNoRegularFile()
    {
        // 目录条目走的是另一条路(整包存成 tar),这里返回 0 而不是抛。
        var empty = new byte[1024];
        Assert.AreEqual(0, await TarUtil.ExtractFirstFileAsync(new MemoryStream(empty), new MemoryStream(),
            CancellationToken.None));
    }

    [TestMethod]
    public async Task Tar_ExtractRefusesToSilentlyTruncateATruncatedStream()
    {
        var archive = TarUtil.CreateSingleFile("a.txt", new byte[2048]);
        // 砍掉后半段:一个静默截断的文件比一句"下载失败"危险得多。
        var truncated = archive[..(512 + 600)];

        await Assert.ThrowsExactlyAsync<EndOfStreamException>(() =>
            TarUtil.ExtractFirstFileAsync(new MemoryStream(truncated), new MemoryStream(), CancellationToken.None));
    }

    // ── 查询串 ────────────────────────────────────────────────

    [TestMethod]
    public void Query_SkipsNullsAndEscapesValues()
    {
        var query = DockerClient.Query(("a", "1"), ("skip", null), ("path", "/etc/nginx/conf.d"));
        Assert.AreEqual("?a=1&path=%2Fetc%2Fnginx%2Fconf.d", query);
    }

    [TestMethod]
    public void Filters_GroupsRepeatedKeysIntoOneArray()
    {
        var filters = DockerClient.Filters(("label", "a=1"), ("label", "b=2"));
        Assert.IsNotNull(filters);
        Assert.Contains("\"label\"", filters);
        Assert.Contains("a=1", filters);
        Assert.Contains("b=2", filters);
    }

    // ── ls 解析 ───────────────────────────────────────────────

    [TestMethod]
    public void ParseLsLine_ReadsAnOrdinaryFile()
    {
        var entry = DockerClient.ParseLsLine(
            "-rw-r--r-- 1 root root 1842 2026-08-21 09:12 default.conf", "/etc/nginx/conf.d");

        Assert.IsNotNull(entry);
        Assert.AreEqual("default.conf", entry.Value.Name);
        Assert.AreEqual("/etc/nginx/conf.d/default.conf", entry.Value.FullPath);
        Assert.AreEqual(1842, entry.Value.Size);
        Assert.IsFalse(entry.Value.IsDirectory);
    }

    [TestMethod]
    public void ParseLsLine_KeepsSpacesInsideFileNames()
    {
        var entry = DockerClient.ParseLsLine(
            "-rw-r--r-- 1 root root 10 2026-08-21 09:12 my file.txt", "/tmp");

        Assert.IsNotNull(entry);
        // 按"第八段"取名字会把这个文件截成 "my" —— 必须取第七段之后的全部。
        Assert.AreEqual("my file.txt", entry.Value.Name);
    }

    [TestMethod]
    public void ParseLsLine_SplitsSymlinkTargets()
    {
        var entry = DockerClient.ParseLsLine(
            "lrwxrwxrwx 1 root root 7 2026-08-21 09:12 sh -> busybox", "/bin");

        Assert.IsNotNull(entry);
        Assert.IsTrue(entry.Value.IsSymlink);
        Assert.AreEqual("sh", entry.Value.Name);
        Assert.AreEqual("busybox", entry.Value.LinkTarget);
    }

    [TestMethod]
    public void ParseLsLine_IgnoresTotalsAndDotEntries()
    {
        Assert.IsNull(DockerClient.ParseLsLine("total 12", "/tmp"));
        Assert.IsNull(DockerClient.ParseLsLine("", "/tmp"));
        Assert.IsNull(DockerClient.ParseLsLine("drwxr-xr-x 2 root root 4096 2026-08-21 09:12 .", "/tmp"));
    }

    [TestMethod]
    public void ShellQuote_NeutralisesEmbeddedSingleQuotes()
    {
        // 列目录是这一层唯一一处把用户输入拼进 shell 的地方,引用错了就是命令注入。
        Assert.AreEqual("'/tmp/it'\\''s here'", Sh.Quote("/tmp/it's here"));
    }

    // ── 连不上的分类 ──────────────────────────────────────────

    /// <summary>一条永远连不上的传输,用来把 <c>Unreachable</c> 的分类逼出来。</summary>
    private sealed class FailingTransport(string message) : IDockerTransport
    {
        public string Description => "/var/run/docker.sock (SSH 隧道)";

        public Task<Stream> ConnectAsync(CancellationToken cancellationToken = default) =>
            throw new IOException(message);
    }

    private static async Task<DockerUnreachableException> PingFailureAsync(string transportMessage)
    {
        await using var client = new DockerClient(
            DockerEndpoint.Local("测试"), new FailingTransport(transportMessage));
        return await Assert.ThrowsExactlyAsync<DockerUnreachableException>(() => client.PingAsync());
    }

    [TestMethod]
    public async Task Unreachable_ClassifiesSshChannelOpenFailureAsSocketMissing()
    {
        // SDK 报的是 ConnectFailed —— 中间没有空格。只匹配 "connect failed" 会漏掉它,
        // 于是最常见的一种失败反而落进 Unknown,界面上只剩一个"重试",没有任何出路。
        var ex = await PingFailureAsync(
            "Failed to open channel - ConnectFailed - open failed. (docker:80)");

        Assert.AreEqual(DockerUnreachableReason.SocketMissing, ex.Reason);
    }

    [TestMethod]
    public async Task Unreachable_PrefersPermissionDeniedOverChannelFailure()
    {
        // 两者同时出现时按"没权限"报:它是明说的,而"通道开不起来"是笼统的。
        var ex = await PingFailureAsync(
            "open failed: connect /var/run/docker.sock: permission denied");

        Assert.AreEqual(DockerUnreachableReason.PermissionDenied, ex.Reason);
    }
}
