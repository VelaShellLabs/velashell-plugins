using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace VelaShell.Plugin.Mongo.Core;

/// <summary>
/// TLS 证书校验的记录器与放行器(与 Redis / S3 插件同一套做法)。
/// <para>
/// 刻意**不在校验回调里同步等用户点按钮**:那要把异步对话框阻塞成同步,极易死锁。
/// 回调只做两件事 —— 命中已信任指纹或自备 CA 就放行,否则把看到的证书与失败原因**记下来**并拒绝;
/// 连接随即失败,由提供方把记录组装成 <c>ProtocolCertificateTrustException</c> 交给宿主,
/// 宿主弹出共用的信任提示,用户确认后指纹落进会话配置、重连即通。
/// </para>
/// </summary>
/// <param name="trustedThumbprint">用户此前确认信任的指纹;为空表示还没信任过。</param>
internal sealed class TlsTrust(string? trustedThumbprint)
{
    /// <summary>最近一次校验失败时看到的证书;没失败过为 <see langword="null" />。</summary>
    public X509Certificate2? SeenCertificate { get; private set; }

    /// <summary>最近一次校验失败的原因。</summary>
    public SslPolicyErrors PolicyErrors { get; private set; }

    /// <summary>校验:系统链通过、命中已信任指纹、或由自备 CA 签发,三者任一即放行。</summary>
    /// <param name="certificate">服务器证书。</param>
    /// <param name="chain">系统构建的证书链。</param>
    /// <param name="errors">系统校验错误。</param>
    /// <param name="caCertificates">连接设置里自备的 CA;没有为 <see langword="null" />。</param>
    /// <returns>是否接受。</returns>
    public bool Validate(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors, X509Certificate2Collection? caCertificates)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }
        if (certificate is null)
        {
            PolicyErrors = errors;
            return false;
        }
        X509Certificate2 typed = certificate as X509Certificate2
                                 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        if (!string.IsNullOrEmpty(trustedThumbprint)
            && string.Equals(typed.Thumbprint, trustedThumbprint, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // 自备 CA:只放过"链不完整"这一类错误;主机名不匹配仍然是错 —— 那正是中间人的样子。
        if (caCertificates is { Count: > 0 } && errors == SslPolicyErrors.RemoteCertificateChainErrors)
        {
            using var custom = new X509Chain();
            custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            custom.ChainPolicy.CustomTrustStore.AddRange(caCertificates);
            if (custom.Build(typed))
            {
                return true;
            }
        }
        SeenCertificate = typed;
        PolicyErrors = errors;
        return false;
    }
}
