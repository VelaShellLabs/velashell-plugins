using System.Security.Cryptography;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Plugin.S3.Tests;

/// <summary>
/// 服务器端摘要(SDK 2.1.3 的可选面 <see cref="IProtocolChecksum" />)打到环回服务器上。
/// <para>
/// 守的是契约里最容易被「好心」破坏的两条:摘要**只能是服务端存着的那份**(没存就是 null,
/// 绝不为凑一个值把对象 GET 回来算);分段上传的**组合摘要**不是整个对象的摘要,不能交出去 ——
/// 宿主拿它与本地文件逐字比,交出去就会把内容相同的文件判成不同、整份重传。
/// </para>
/// </summary>
[TestClass]
[TestCategory("S3")]
public sealed class S3ChecksumTests
{
    private const string AccessKey = "AKIAIOSFODNN7EXAMPLE";
    private const string SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private const string Bucket = "test-bucket";
    private const string SessionKey = "checksum-session";

    private LoopbackS3Server _server = null!;
    private S3ProtocolFileSystem _service = null!;

    [TestInitialize]
    public async Task SetUpAsync()
    {
        _server = new(AccessKey, SecretKey);
        _server.AddBucket(Bucket);
        _service = new();
        _ = await _service.OpenSessionAsync(SessionKey, new()
        {
            Endpoint = "127.0.0.1",
            Port = _server.Port,
            AccessKeyId = AccessKey,
            SecretAccessKey = SecretKey,
            Settings = new()
            {
                UseTls = false,
                Region = _server.Region,
                AddressingStyle = S3AddressingStyle.Path,
            },
        });
    }

    [TestCleanup]
    public async Task TearDownAsync()
    {
        await _service.DisposeAsync();
        _server.Dispose();
    }

    private IProtocolChecksum Checksums => _service;

    private void AssertAllRequestsSigned() =>
        Assert.AreEqual(0, _server.SignatureFailures,
            "服务器重算 SigV4 后与客户端发来的签名对不上:客户端签的和发的不是同一份请求。");

    /// <summary>没有一次 GET 打到这个桶里的对象上 —— 摘要是 HEAD 取回来的,不是下载下来算的。</summary>
    private void AssertNoObjectWasDownloaded() =>
        Assert.IsFalse(_server.Requests.Any(r => r.StartsWith($"GET /{Bucket}/", StringComparison.Ordinal)),
            "为了凑摘要把对象读了回来:" + string.Join(" | ", _server.Requests));

    [TestMethod]
    [DataRow(RemoteChecksumAlgorithm.Sha256, "x-amz-checksum-sha256")]
    [DataRow(RemoteChecksumAlgorithm.Sha512, "x-amz-checksum-sha512")]
    [DataRow(RemoteChecksumAlgorithm.Sha1, "x-amz-checksum-sha1")]
    [DataRow(RemoteChecksumAlgorithm.Md5, "x-amz-checksum-md5")]
    public async Task ComputeChecksums_ReturnsTheFullObjectChecksumTheServerStored(RemoteChecksumAlgorithm algorithm, string header)
    {
        byte[] content = "the quick brown fox"u8.ToArray();
        byte[] digest = algorithm switch
        {
            RemoteChecksumAlgorithm.Sha256 => SHA256.HashData(content),
            RemoteChecksumAlgorithm.Sha512 => SHA512.HashData(content),
            RemoteChecksumAlgorithm.Sha1 => SHA1.HashData(content),
            _ => MD5.HashData(content),
        };
        _server.AddObject(Bucket, "docs/fox.txt", content, new Dictionary<string, string>
        {
            [header] = Convert.ToBase64String(digest),
            ["x-amz-checksum-type"] = "FULL_OBJECT",
        });

        IReadOnlyDictionary<string, string?> result =
            await Checksums.ComputeChecksumsAsync(SessionKey, algorithm, ["/test-bucket/docs/fox.txt"]);

        // 契约的写法:小写十六进制、不带前缀(服务端给的是 base64,由实现换算)。
        Assert.AreEqual(Convert.ToHexStringLower(digest), result["/test-bucket/docs/fox.txt"]);
        AssertNoObjectWasDownloaded();
        AssertAllRequestsSigned();
    }

    [TestMethod]
    public async Task ComputeChecksums_GivesNullForEverythingThatHasNoStoredChecksum()
    {
        // 本插件自己上传的对象就是这样:客户端按 WHEN_REQUIRED 计算校验和,对象上什么都不存。
        _server.AddObject(Bucket, "plain.bin", new byte[4096]);
        _server.AddObject(Bucket, "logs/a.log", "a");
        string[] paths = ["/test-bucket/plain.bin", "/test-bucket/logs", "/test-bucket", "/", "/test-bucket/missing.txt"];

        IReadOnlyDictionary<string, string?> result =
            await Checksums.ComputeChecksumsAsync(SessionKey, RemoteChecksumAlgorithm.Sha256, paths);

        // 单个文件给不出只是那一项为 null —— 不抛,一个文件不该让整批回退;键就是请求里的路径原样。
        Assert.AreSequenceEqual(paths, [.. result.Keys], Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        Assert.IsTrue(result.Values.All(static value => value is null), string.Join(", ", result));
        AssertNoObjectWasDownloaded();
        AssertAllRequestsSigned();
    }

    [TestMethod]
    public async Task ComputeChecksums_RefusesTheCompositeChecksumOfAMultipartUpload()
    {
        // 分段上传的 SHA-256 是「各分段摘要」的摘要。新服务端用 x-amz-checksum-type 标明,
        // 老的只在值后面缀 "-<分段数>" —— 两种都得认出来。
        string partsDigest = Convert.ToBase64String(SHA256.HashData("parts"u8));
        _server.AddObject(Bucket, "big-typed.iso", new byte[16], new Dictionary<string, string>
        {
            ["x-amz-checksum-sha256"] = partsDigest,
            ["x-amz-checksum-type"] = "COMPOSITE",
        });
        _server.AddObject(Bucket, "big-suffixed.iso", new byte[16], new Dictionary<string, string>
        {
            ["x-amz-checksum-sha256"] = partsDigest + "-3",
        });

        IReadOnlyDictionary<string, string?> result = await Checksums.ComputeChecksumsAsync(
            SessionKey, RemoteChecksumAlgorithm.Sha256, ["/test-bucket/big-typed.iso", "/test-bucket/big-suffixed.iso"]);

        Assert.IsNull(result["/test-bucket/big-typed.iso"]);
        Assert.IsNull(result["/test-bucket/big-suffixed.iso"]);
        AssertNoObjectWasDownloaded();
    }

    [TestMethod]
    public async Task ComputeChecksums_DoesNotPassTheEtagOffAsAnMd5()
    {
        // 环回服务器的 ETag 恰好就是内容的 MD5(单次 PUT 时的常见情形),但对象没存 x-amz-checksum-md5。
        // ETag 在分段上传、SSE-KMS / SSE-C 与不少兼容实现上都不是 MD5 —— 拿它充数就是猜。
        _server.AddObject(Bucket, "etag-only.txt", "hello");

        IReadOnlyDictionary<string, string?> result = await Checksums.ComputeChecksumsAsync(
            SessionKey, RemoteChecksumAlgorithm.Md5, ["/test-bucket/etag-only.txt"]);

        Assert.IsNull(result["/test-bucket/etag-only.txt"]);
    }

    [TestMethod]
    public async Task ComputeChecksums_RejectsAnUnspecifiedAlgorithmForTheWholeSession()
    {
        _server.AddObject(Bucket, "a.txt", "a");

        // Unknown 是缺字段的线上数据读出来的值,不是合法请求:按不支持整体拒绝,绝不当成某个默认算法。
        _ = await Assert.ThrowsExactlyAsync<ProtocolUnsupportedException>(
            () => Checksums.ComputeChecksumsAsync(SessionKey, RemoteChecksumAlgorithm.Unknown, ["/test-bucket/a.txt"]));
    }

    [TestMethod]
    public async Task ComputeChecksums_UnknownSession_ThrowsSessionNotFound()
    {
        _ = await Assert.ThrowsExactlyAsync<PluginSessionNotFoundException>(
            () => Checksums.ComputeChecksumsAsync("no-such-session", RemoteChecksumAlgorithm.Sha256, ["/test-bucket/a.txt"]));
    }

    [TestMethod]
    public void TheProtocolDoesNotOfferToSetModificationTimes()
    {
        // 刻意不兼实现 IProtocolLastWriteTime:S3 的 LastModified 是服务端写入时刻,协议上没有改它的操作;
        // 「复制对象自身 + 改元数据」会重写对象、换掉版本号与 ETag,LastModified 照样是复制那一刻。
        // 不实现,宿主就如实报「设不了修改时间」。这一条写成断言,免得日后有人「补齐」成那个不等价的做法。
        Assert.IsNotInstanceOfType<IProtocolLastWriteTime>(_service);
    }
}
