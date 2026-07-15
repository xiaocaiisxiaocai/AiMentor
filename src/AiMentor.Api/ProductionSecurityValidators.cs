using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace AiMentor.Api;

/// <summary>集中执行生产启动前必须失败关闭的路径与 OIDC 元数据校验。</summary>
public static class ProductionSecurityValidators
{
    /// <summary>判断候选路径本身或其符号链接最终目标是否位于指定根目录。</summary>
    public static bool IsPathWithin(string? rootPath, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return false;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        if (Contains(root, candidate)) return true;

        var resolvedRoot = ResolveLinks(root);
        var resolvedCandidate = ResolveLinks(candidate);
        return Contains(resolvedRoot, resolvedCandidate);
    }

    /// <summary>验证 discovery 结果不会把授权码、客户端机密或签名验证导向非可信端点。</summary>
    public static void ValidateOidcDiscovery(OpenIdConnectConfiguration metadata, string expectedAuthority)
    {
        var expectedIssuer = RequireHttpsUri(expectedAuthority, "OIDC authority");
        var discoveredIssuer = RequireHttpsUri(metadata.Issuer, "OIDC issuer");
        if (!SameIssuer(expectedIssuer, discoveredIssuer))
            throw new InvalidOperationException("运营台 OIDC discovery issuer 与配置的 authority 不一致。");

        _ = RequireHttpsUri(metadata.AuthorizationEndpoint, "OIDC authorization endpoint");
        _ = RequireHttpsUri(metadata.TokenEndpoint, "OIDC token endpoint");
        _ = RequireHttpsUri(metadata.JwksUri, "OIDC JWKS endpoint");
        ValidateOptionalHttpsUri(metadata.EndSessionEndpoint, "OIDC end-session endpoint");
        ValidateOptionalHttpsUri(metadata.UserInfoEndpoint, "OIDC userinfo endpoint");
        if (metadata.SigningKeys.Count == 0)
            throw new InvalidOperationException("运营台 OIDC discovery 未提供可用签名密钥。");
    }

    /// <summary>验证密钥环证书具备 RSA 私钥；活动证书还必须处于有效期内。</summary>
    public static void ValidateDataProtectionCertificate(
        X509Certificate2 certificate, bool requireCurrentlyValid)
    {
        using var privateKey = certificate.GetRSAPrivateKey();
        if (!certificate.HasPrivateKey || privateKey is null)
            throw new InvalidOperationException("运营台 Data Protection 证书必须包含 RSA 私钥。");
        if (!requireCurrentlyValid) return;
        var now = DateTime.UtcNow;
        if (certificate.NotBefore.ToUniversalTime() > now || certificate.NotAfter.ToUniversalTime() <= now)
            throw new InvalidOperationException("运营台 Data Protection 活动证书尚未生效或已经过期。");
    }

    /// <summary>验证共享目录已由发布流程预置集群标识且当前实例具备真实读写能力。</summary>
    public static void ValidateDataProtectionDirectory(string directoryPath, string expectedClusterId)
    {
        if (!Directory.Exists(directoryPath))
            throw new InvalidOperationException("运营台 Data Protection 共享目录不存在或尚未挂载。");
        if (!Guid.TryParseExact(expectedClusterId, "D", out var expectedId))
            throw new InvalidOperationException("运营台 Data Protection 集群标识必须是标准 GUID。");
        var markerPath = Path.Combine(directoryPath, ".aimentor-cluster-id");
        if (!File.Exists(markerPath)
            || (File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0
            || !Guid.TryParseExact(File.ReadAllText(markerPath).Trim(), "D", out var actualId)
            || actualId != expectedId)
            throw new InvalidOperationException("运营台 Data Protection 共享目录缺少匹配的预置集群标识。");

        var probePath = Path.Combine(directoryPath, $".aimentor-write-probe-{Guid.NewGuid():N}");
        try
        {
            using var probe = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                FileOptions.DeleteOnClose | FileOptions.WriteThrough);
            probe.WriteByte(1);
            probe.Flush(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("运营台 Data Protection 共享目录不可写。", exception);
        }
    }

    /// <summary>逐个实例化历史持久化密钥，确保旧解密证书未被过早移除。</summary>
    public static void ValidateExistingDataProtectionKeys(IServiceProvider services, string expectedFingerprint)
    {
        try
        {
            var allKeys = services.GetRequiredService<IKeyManager>().GetAllKeys()
                .OrderBy(key => key.KeyId.ToString("D"), StringComparer.Ordinal)
                .ToArray();
            var usableKeys = allKeys.Where(key => !key.IsRevoked).ToArray();
            if (usableKeys.Length == 0)
                throw new InvalidOperationException("运营台 Data Protection 没有预置的未撤销密钥。");
            foreach (var key in usableKeys)
                _ = key.CreateEncryptor()
                    ?? throw new InvalidOperationException("Data Protection 历史密钥无法创建加密器。");
            var actualFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join('\n', allKeys.Select(key => key.KeyId.ToString("D"))))));
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actualFingerprint),
                    Encoding.ASCII.GetBytes(expectedFingerprint.ToUpperInvariant())))
                throw new InvalidOperationException("运营台 Data Protection key ring 与部署指纹不一致。");
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            throw new InvalidOperationException("运营台 Data Protection 历史密钥解密探测失败。", exception);
        }
    }

    /// <summary>供受控发布流程计算全部已预置 key 标识的非敏感指纹。</summary>
    public static string GetDataProtectionKeyRingFingerprint(IServiceProvider services)
    {
        var keys = services.GetRequiredService<IKeyManager>().GetAllKeys()
            .Select(key => key.KeyId.ToString("D"))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        if (keys.Length == 0)
            throw new InvalidOperationException("Data Protection key ring 为空。");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', keys))));
    }

    private static bool Contains(string root, string candidate)
    {
        // 对所有平台采用保守的大小写不敏感比较，避免大小写不敏感挂载被错误放行。
        const StringComparison comparison = StringComparison.OrdinalIgnoreCase;
        return candidate.Equals(root, comparison)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison)
            || (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar
                && candidate.StartsWith(root + Path.AltDirectorySeparatorChar, comparison));
    }

    private static string ResolveLinks(string path)
    {
        var root = Path.GetPathRoot(path)
            ?? throw new InvalidOperationException("无法解析生产安全路径的根目录。");
        var current = root;
        var remainder = path[root.Length..];
        foreach (var segment in remainder.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, segment);
            FileSystemInfo? item = Directory.Exists(next)
                ? new DirectoryInfo(next)
                : File.Exists(next) ? new FileInfo(next) : null;
            current = item?.ResolveLinkTarget(true)?.FullName ?? next;
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    private static Uri RequireHttpsUri(string? value, string label)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException($"运营台 {label} 必须是无用户信息、查询和片段的绝对 HTTPS URI。");
        return uri;
    }

    private static void ValidateOptionalHttpsUri(string? value, string label)
    {
        if (!string.IsNullOrWhiteSpace(value)) _ = RequireHttpsUri(value, label);
    }

    private static bool SameIssuer(Uri expected, Uri discovered) =>
        expected.Scheme.Equals(discovered.Scheme, StringComparison.OrdinalIgnoreCase)
        && expected.IdnHost.Equals(discovered.IdnHost, StringComparison.OrdinalIgnoreCase)
        && expected.Port == discovered.Port
        && expected.AbsolutePath.TrimEnd('/').Equals(
            discovered.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
}
