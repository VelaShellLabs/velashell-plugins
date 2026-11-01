using Amazon.S3.Model;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Plugin.S3;

/// <summary>
/// 对象**保存着的**校验和 → SDK 要的摘要(小写十六进制),给 <see cref="VelaShell.PluginSdk.Protocols.IProtocolChecksum" /> 用。
/// <para>
/// 只认上传时带上、由服务端存下来的完整对象摘要(<c>x-amz-checksum-sha256</c> / <c>-sha512</c> / <c>-sha1</c> /
/// <c>-md5</c>,HEAD 带上 <c>x-amz-checksum-mode: ENABLED</c> 才会回)。契约要求「只能用服务器给的摘要」,
/// 下面几种情形因此一律是「这个文件给不出」(null),而不是去凑一个值:
/// </para>
/// <list type="bullet">
///   <item>对象没带这种校验和 —— 本插件自己上传的对象就不带:<see cref="S3ClientFactory" /> 用的是
///   <c>RequestChecksumCalculation.WHEN_REQUIRED</c>(兼容不认 flexible checksums 的 S3 兼容实现);</item>
///   <item>分段上传的<b>组合摘要</b>(<c>x-amz-checksum-type: COMPOSITE</c>,或值带 <c>-分段数</c> 后缀)——
///   那是「各分段摘要」的摘要,不是整个对象的摘要,拿去和本地文件比只会把相同的内容判成不同;</item>
///   <item>值解不开,或解出来的长度与算法对不上。</item>
/// </list>
/// <para>
/// <b>ETag 不当 MD5 用</b>:它只在「单次 PUT、没用 SSE-KMS / SSE-C」时恰好是内容的 MD5,
/// 分段上传、KMS 加密与不少兼容实现上都不是 —— 那是猜,不是服务端给的摘要。
/// </para>
/// </summary>
internal static class S3StoredChecksum
{
    /// <summary>组合摘要的 <c>x-amz-checksum-type</c> 取值。</summary>
    private const string CompositeType = "COMPOSITE";

    /// <summary>该算法的摘要字节数;S3 不保存这种校验和(含 <see cref="RemoteChecksumAlgorithm.Unknown" />)时为 null。</summary>
    /// <param name="algorithm">请求的算法。</param>
    /// <returns>摘要字节数,或 null。</returns>
    public static int? DigestLength(RemoteChecksumAlgorithm algorithm) =>
        algorithm switch
        {
            RemoteChecksumAlgorithm.Sha256 => 32,
            RemoteChecksumAlgorithm.Sha512 => 64,
            RemoteChecksumAlgorithm.Sha1 => 20,
            RemoteChecksumAlgorithm.Md5 => 16,
            _ => null,
        };

    /// <summary>从 HEAD 的响应里取所请求算法的摘要;给不出时为 null。</summary>
    /// <param name="metadata">HEAD(带 <c>ChecksumMode = ENABLED</c>)的响应。</param>
    /// <param name="algorithm">请求的算法。</param>
    /// <returns>小写十六进制摘要,或 null。</returns>
    public static string? FromMetadata(GetObjectMetadataResponse metadata, RemoteChecksumAlgorithm algorithm)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        string? stored = algorithm switch
        {
            RemoteChecksumAlgorithm.Sha256 => metadata.ChecksumSHA256,
            RemoteChecksumAlgorithm.Sha512 => metadata.ChecksumSHA512,
            RemoteChecksumAlgorithm.Sha1 => metadata.ChecksumSHA1,
            RemoteChecksumAlgorithm.Md5 => metadata.ChecksumMD5,
            _ => null,
        };
        return ToLowerHex(stored, metadata.ChecksumType?.Value, algorithm);
    }

    /// <summary>把服务端回的 base64 校验和换成小写十六进制;组合摘要、解不开或长度不对时为 null。</summary>
    /// <param name="stored">服务端回的值(base64)。</param>
    /// <param name="checksumType"><c>x-amz-checksum-type</c> 的值;老服务端不回这个头时为 null。</param>
    /// <param name="algorithm">请求的算法。</param>
    /// <returns>小写十六进制摘要,或 null。</returns>
    public static string? ToLowerHex(string? stored, string? checksumType, RemoteChecksumAlgorithm algorithm)
    {
        if (DigestLength(algorithm) is not { } length || string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }
        // 组合摘要两种认法都要:新服务端回 x-amz-checksum-type,老的(以及部分兼容实现)只在值后面
        // 缀 "-<分段数>"。'-' 不在标准 base64 字母表里,完整对象的摘要不会带它。
        if (string.Equals(checksumType, CompositeType, StringComparison.OrdinalIgnoreCase) || stored.Contains('-', StringComparison.Ordinal))
        {
            return null;
        }
        Span<byte> digest = stackalloc byte[64];
        return Convert.TryFromBase64String(stored.Trim(), digest, out int written) && written == length
            ? Convert.ToHexStringLower(digest[..written])
            : null;
    }
}
