using System.Security.Cryptography;
using System.Text;

namespace AiMentor.Infrastructure;

/// <summary>携带显式密钥版本的工作流认证密文，支持无停机轮换。</summary>
public sealed record ProtectedWorkflowState(string KeyVersion, string Ciphertext);

/// <summary>为 Agent 检查点提供版本化认证加密，并兼容升级前的记忆密钥密文。</summary>
public interface IWorkflowStateCipher
{
    string ActiveKeyVersion { get; }
    ProtectedWorkflowState Protect(string plaintext, string context);
    string Unprotect(string? keyVersion, string ciphertext, string context);
    bool RequiresReencryption(string? keyVersion);
}

/// <summary>
/// 使用每版本独立派生的 AES-GCM 密钥保护工作流状态；读取旧版本后由存储层在线重加密为活动版本。
/// </summary>
public sealed class AesGcmWorkflowStateCipher : IWorkflowStateCipher
{
    private const string EnvelopeVersion = "wf1";
    private readonly Dictionary<string, byte[]> _keys;
    private readonly IMemoryCipher? _legacyCipher;

    public AesGcmWorkflowStateCipher(string activeKeyVersion, IReadOnlyDictionary<string, byte[]> masterKeys,
        IMemoryCipher? legacyCipher = null)
    {
        ActiveKeyVersion = ValidateVersion(activeKeyVersion);
        if (masterKeys.Count == 0 || !masterKeys.ContainsKey(ActiveKeyVersion))
            throw new ArgumentException("工作流密钥环必须包含活动密钥版本。", nameof(masterKeys));
        _keys = masterKeys.ToDictionary(pair => ValidateVersion(pair.Key), pair => DeriveKey(pair.Value, pair.Key),
            StringComparer.Ordinal);
        _legacyCipher = legacyCipher;
    }

    /// <inheritdoc />
    public string ActiveKeyVersion { get; }

    /// <inheritdoc />
    public ProtectedWorkflowState Protect(string plaintext, string context)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var source = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[source.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_keys[ActiveKeyVersion], tag.Length);
        aes.Encrypt(nonce, source, ciphertext, tag, AdditionalData(ActiveKeyVersion, context));
        return new ProtectedWorkflowState(ActiveKeyVersion, string.Join('.', EnvelopeVersion,
            Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag)));
    }

    /// <inheritdoc />
    public string Unprotect(string? keyVersion, string ciphertext, string context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ciphertext);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        if (string.IsNullOrWhiteSpace(keyVersion))
        {
            if (_legacyCipher is null)
                throw new CryptographicException("工作流检查点使用旧密文，但未配置兼容读取密钥。");
            return _legacyCipher.Unprotect(ciphertext, context);
        }
        if (!_keys.TryGetValue(keyVersion, out var key))
            throw new CryptographicException($"工作流密钥版本 {keyVersion} 不在当前密钥环中。");
        var parts = ciphertext.Split('.', StringSplitOptions.None);
        if (parts.Length != 4 || !string.Equals(parts[0], EnvelopeVersion, StringComparison.Ordinal))
            throw new CryptographicException("不支持的工作流密文格式。");
        var nonce = Convert.FromBase64String(parts[1]);
        var encrypted = Convert.FromBase64String(parts[2]);
        var tag = Convert.FromBase64String(parts[3]);
        var plaintext = new byte[encrypted.Length];
        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(nonce, encrypted, tag, plaintext, AdditionalData(keyVersion, context));
        return Encoding.UTF8.GetString(plaintext);
    }

    /// <inheritdoc />
    public bool RequiresReencryption(string? keyVersion) =>
        !string.Equals(keyVersion, ActiveKeyVersion, StringComparison.Ordinal);

    private static byte[] DeriveKey(byte[] masterKey, string keyVersion)
    {
        if (masterKey.Length != 32)
            throw new ArgumentException("每个工作流主密钥必须正好为 32 字节。", nameof(masterKey));
        return HMACSHA256.HashData(masterKey,
            Encoding.UTF8.GetBytes($"AiMentor.Workflow.Encryption.{EnvelopeVersion}.{keyVersion}"));
    }

    private static byte[] AdditionalData(string keyVersion, string context) =>
        Encoding.UTF8.GetBytes($"{EnvelopeVersion}\n{keyVersion}\n{context}");

    private static string ValidateVersion(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64
        && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.')
            ? value : throw new ArgumentException("工作流密钥版本格式无效。", nameof(value));
}
