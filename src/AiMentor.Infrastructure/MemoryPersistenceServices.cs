using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiMentor.Application;
using AiMentor.Domain;

namespace AiMentor.Infrastructure;

/// <summary>配置单实例加密记忆快照的绝对或相对落盘路径。</summary>
public sealed class EncryptedFileMemoryStoreOptions
{
    public required string FilePath { get; init; }
    public int MaximumPendingProposalsPerTenant { get; init; } = 10_000;
}

/// <summary>隔离记忆正文加密与不可逆键指纹，避免存储层接触明文实现细节。</summary>
public interface IMemoryCipher
{
    string ActiveKeyVersion { get; }
    string Protect(string plaintext, string context);
    string Unprotect(string protectedValue, string context);
    bool RequiresReencryption(string protectedValue);
    bool SupportsCiphertextVersion(string envelopeVersion, string keyVersion);
    string Fingerprint(string value, string? context = null);
}

/// <summary>使用版本化密钥环完成 AES-GCM 认证加密，并用独立稳定密钥生成 HMAC 指纹。</summary>
public sealed class AesGcmMemoryCipher : IMemoryCipher
{
    private const string LegacyEnvelopeVersion = "v1";
    private const string EnvelopeVersion = "mem1";
    private readonly Dictionary<string, byte[]> _encryptionKeys;
    private readonly byte[]? _legacyEncryptionKey;
    private readonly byte[] _fingerprintKey;

    public AesGcmMemoryCipher(byte[] masterKey)
        : this("v1", new Dictionary<string, byte[]> { ["v1"] = masterKey }, masterKey)
    {
    }

    public AesGcmMemoryCipher(string activeKeyVersion, IReadOnlyDictionary<string, byte[]> masterKeys,
        byte[] fingerprintMasterKey)
    {
        ActiveKeyVersion = ValidateVersion(activeKeyVersion);
        if (masterKeys.Count == 0 || !masterKeys.ContainsKey(ActiveKeyVersion))
            throw new ArgumentException("记忆密钥环必须包含活动密钥版本。", nameof(masterKeys));
        _encryptionKeys = masterKeys.ToDictionary(
            pair => ValidateVersion(pair.Key),
            pair => DeriveKey(pair.Value, $"AiMentor.Memory.Encryption.{EnvelopeVersion}.{pair.Key}"),
            StringComparer.Ordinal);
        _legacyEncryptionKey = masterKeys.TryGetValue("v1", out var legacyMasterKey)
            ? DeriveKey(legacyMasterKey, "AiMentor.Memory.Encryption.v1")
            : null;
        _fingerprintKey = DeriveKey(fingerprintMasterKey, "AiMentor.Memory.Fingerprint.v1");
    }

    /// <inheritdoc />
    public string ActiveKeyVersion { get; }

    public string Protect(string plaintext, string context)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var source = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[source.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_encryptionKeys[ActiveKeyVersion], tag.Length);
        aes.Encrypt(nonce, source, ciphertext, tag, AdditionalData(ActiveKeyVersion, context));
        return string.Join('.', EnvelopeVersion, ActiveKeyVersion, Convert.ToBase64String(nonce),
            Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
    }

    public string Unprotect(string protectedValue, string context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedValue);
        var parts = protectedValue.Split('.', StringSplitOptions.None);
        byte[] key;
        string keyVersion;
        int offset;
        bool legacyEnvelope;
        if (parts.Length == 4 && string.Equals(parts[0], LegacyEnvelopeVersion, StringComparison.Ordinal))
        {
            key = _legacyEncryptionKey
                ?? throw new CryptographicException("记忆密文使用旧 v1 密钥，但当前密钥环未保留该版本。");
            keyVersion = LegacyEnvelopeVersion;
            offset = 1;
            legacyEnvelope = true;
        }
        else if (parts.Length == 5 && string.Equals(parts[0], EnvelopeVersion, StringComparison.Ordinal)
                 && _encryptionKeys.TryGetValue(parts[1], out var versionedKey))
        {
            key = versionedKey;
            keyVersion = parts[1];
            offset = 2;
            legacyEnvelope = false;
        }
        else
        {
            throw new CryptographicException("不支持的记忆密文格式或密钥版本。");
        }
        var nonce = Convert.FromBase64String(parts[offset]);
        var ciphertext = Convert.FromBase64String(parts[offset + 1]);
        var tag = Convert.FromBase64String(parts[offset + 2]);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(nonce, ciphertext, tag, plaintext,
            legacyEnvelope
                ? LegacyAdditionalData(context)
                : AdditionalData(keyVersion, context));
        return Encoding.UTF8.GetString(plaintext);
    }

    /// <inheritdoc />
    public bool RequiresReencryption(string protectedValue)
    {
        var parts = protectedValue.Split('.', StringSplitOptions.None);
        return parts.Length != 5
            || !string.Equals(parts[0], EnvelopeVersion, StringComparison.Ordinal)
            || !string.Equals(parts[1], ActiveKeyVersion, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public bool SupportsCiphertextVersion(string envelopeVersion, string keyVersion) =>
        string.Equals(envelopeVersion, LegacyEnvelopeVersion, StringComparison.Ordinal)
            ? _legacyEncryptionKey is not null
            : string.Equals(envelopeVersion, EnvelopeVersion, StringComparison.Ordinal)
              && _encryptionKeys.ContainsKey(keyVersion);

    public string Fingerprint(string value, string? context = null)
    {
        using var hmac = new HMACSHA256(_fingerprintKey);
        var normalized = value.Trim().ToUpperInvariant();
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(
            string.IsNullOrWhiteSpace(context) ? normalized : $"{context}\n{normalized}")));
    }

    private static byte[] DeriveKey(byte[] masterKey, string purpose)
    {
        if (masterKey.Length != 32) throw new ArgumentException("记忆主密钥必须正好为 32 字节。", nameof(masterKey));
        return HMACSHA256.HashData(masterKey, Encoding.UTF8.GetBytes(purpose));
    }

    private static byte[] AdditionalData(string keyVersion, string context) =>
        Encoding.UTF8.GetBytes($"{EnvelopeVersion}\n{keyVersion}\n{context}");

    private static byte[] LegacyAdditionalData(string context) =>
        Encoding.UTF8.GetBytes($"{LegacyEnvelopeVersion}\n{context}");

    private static string ValidateVersion(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64
        && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_')
            ? value : throw new ArgumentException("记忆密钥版本格式无效。", nameof(value));
}

/// <summary>
/// 单实例本地持久化适配器。每次变更都写入临时文件后原子替换，键和值始终以密文落盘。
/// </summary>
public sealed class EncryptedFileMemoryStore : IMemoryStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;
    private readonly IMemoryCipher _cipher;
    private readonly int _maximumPendingProposalsPerTenant;

    public EncryptedFileMemoryStore(EncryptedFileMemoryStoreOptions options, IMemoryCipher cipher)
    {
        if (string.IsNullOrWhiteSpace(options.FilePath)) throw new ArgumentException("记忆文件路径不能为空。", nameof(options));
        _filePath = Path.GetFullPath(options.FilePath);
        if (options.MaximumPendingProposalsPerTenant <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "记忆提案容量必须大于 0。");
        _maximumPendingProposalsPerTenant = options.MaximumPendingProposalsPerTenant;
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        _cipher = cipher;
    }

    public async Task SaveProposalAsync(MemoryProposal proposal, CancellationToken cancellationToken = default)
    {
        await MutateAsync(snapshot =>
        {
            snapshot.Proposals.RemoveAll(item => item.Status != MemoryProposalStatus.PendingApproval
                || item.ApprovalExpiresAt <= proposal.CreatedAt || item.MemoryExpiresAt <= proposal.CreatedAt);
            if (snapshot.Proposals.Count(item => string.Equals(item.TenantId, proposal.TenantId,
                    StringComparison.Ordinal)) >= _maximumPendingProposalsPerTenant)
                throw new MemoryStoreCapacityException();
            if (snapshot.Proposals.Any(item => string.Equals(item.Id, proposal.Id, StringComparison.Ordinal)))
                throw new InvalidOperationException("记忆提案标识发生冲突。");
            snapshot.Proposals.Add(ToStored(proposal));
            return true;
        }, cancellationToken);
    }

    public async Task<MemoryStoreResult<MemoryRecord>> ApproveAsync(string proposalId, AccessContext access,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        return await MutateAsync(snapshot =>
        {
            var proposal = snapshot.Proposals.FirstOrDefault(item => item.Id == proposalId && OwnedBy(item, access));
            if (proposal is null) return new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.NotFound);
            if (proposal.Status != MemoryProposalStatus.PendingApproval)
            {
                snapshot.Proposals.Remove(proposal);
                return new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.Conflict);
            }
            if (proposal.ApprovalExpiresAt <= now || proposal.MemoryExpiresAt <= now)
            {
                snapshot.Proposals.Remove(proposal);
                return new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.Expired);
            }

            snapshot.Memories.RemoveAll(item => item.ExpiresAt <= now);
            if (snapshot.Memories.Any(item => OwnedBy(item, access) && item.Scope == proposal.Scope
                && string.Equals(item.SessionId, proposal.SessionId, StringComparison.Ordinal)
                && string.Equals(item.KeyHash, proposal.KeyHash, StringComparison.Ordinal)))
            {
                snapshot.Proposals.Remove(proposal);
                return new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.AlreadyExists);
            }

            var memory = new MemoryRecord(Guid.NewGuid().ToString("N"), proposal.TenantId, proposal.SubjectId,
                proposal.Scope, proposal.SessionId, _cipher.Unprotect(proposal.KeyCipher, Context(proposal, "key")),
                _cipher.Unprotect(proposal.ValueCipher, Context(proposal, "value")), 1, now, now, proposal.MemoryExpiresAt);
            snapshot.Memories.Add(ToStored(memory));
            snapshot.Proposals.Remove(proposal);
            return new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.Success, memory);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryRecord>> ListActiveAsync(AccessContext access, MemoryScope? scope,
        string? sessionId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        return await ReadAsync(snapshot => snapshot.Memories
            .Where(item => item.ExpiresAt > now && OwnedBy(item, access))
            .Where(item => scope is null || item.Scope == scope)
            .Where(item => sessionId is null || string.Equals(item.SessionId, sessionId, StringComparison.Ordinal))
            .OrderBy(item => item.Scope)
            .ThenBy(item => item.KeyHash, StringComparer.Ordinal)
            .Select(FromStored)
            .ToArray(), cancellationToken);
    }

    public async Task<MemoryStoreResult<MemoryRecord>> UpdateAsync(string memoryId, AccessContext access,
        int expectedVersion, string value, DateTimeOffset? expiresAt, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        return await MutateAsync(snapshot =>
        {
            var memory = snapshot.Memories.FirstOrDefault(item => item.Id == memoryId && OwnedBy(item, access));
            if (memory is null || memory.ExpiresAt <= now)
                return new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.NotFound);
            if (memory.Version != expectedVersion)
                return new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.Conflict);
            if (expiresAt > memory.ExpiresAt)
                return new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.RetentionExceeded);
            memory.ValueCipher = _cipher.Protect(value, Context(memory, "value"));
            memory.Version++;
            memory.UpdatedAt = now;
            memory.ExpiresAt = expiresAt ?? memory.ExpiresAt;
            return new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.Success, FromStored(memory));
        }, cancellationToken);
    }

    public async Task<MemoryStoreResult<bool>> DeleteAsync(string memoryId, AccessContext access, int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        return await MutateAsync(snapshot =>
        {
            var memory = snapshot.Memories.FirstOrDefault(item => item.Id == memoryId && OwnedBy(item, access));
            if (memory is null) return new MemoryStoreResult<bool>(MemoryStoreStatus.NotFound);
            if (memory.Version != expectedVersion) return new MemoryStoreResult<bool>(MemoryStoreStatus.Conflict);
            snapshot.Memories.Remove(memory);
            return new MemoryStoreResult<bool>(MemoryStoreStatus.Success, true);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MemoryTargetState> ProbeTargetStateAsync(string memoryId, AccessContext access,
        int expectedVersion, CancellationToken cancellationToken = default)
    {
        return await ReadAsync(snapshot =>
        {
            var memory = snapshot.Memories.FirstOrDefault(item => item.Id == memoryId);
            if (memory is null) return MemoryTargetState.Absent;
            if (!OwnedBy(memory, access)) return MemoryTargetState.Inaccessible;
            return memory.Version == expectedVersion
                ? MemoryTargetState.PresentAtExpectedVersion
                : MemoryTargetState.PresentAtDifferentVersion;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MemoryStoreResult<MemoryRecord>> RestoreCompensationAsync(string memoryId,
        AccessContext access, int expectedVersion, string previousValue, DateTimeOffset previousExpiresAt,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        return await MutateAsync(snapshot =>
        {
            var memory = snapshot.Memories.FirstOrDefault(item => item.Id == memoryId && OwnedBy(item, access));
            if (memory is null) return new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.NotFound);
            if (memory.Version != expectedVersion)
                return new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.Conflict);
            // 旧值来自已认证上下文绑定的加密补偿快照，不接受 API 提供的任意延长期限。
            memory.ValueCipher = _cipher.Protect(previousValue, Context(memory, "value"));
            memory.Version++;
            memory.UpdatedAt = now;
            memory.ExpiresAt = previousExpiresAt;
            return new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.Success, FromStored(memory));
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MemoryCorrectionCompensationState> ProbeCorrectionCompensationAsync(string memoryId,
        AccessContext access, int expectedVersionAfterForward, string previousValue,
        DateTimeOffset previousExpiresAt, CancellationToken cancellationToken = default)
    {
        return await ReadAsync(snapshot =>
        {
            var memory = snapshot.Memories.FirstOrDefault(item => item.Id == memoryId);
            if (memory is null || !OwnedBy(memory, access))
                return MemoryCorrectionCompensationState.Inaccessible;
            if (memory.Version == expectedVersionAfterForward)
                return MemoryCorrectionCompensationState.NotApplied;
            return memory.Version == expectedVersionAfterForward + 1
                && string.Equals(_cipher.Unprotect(memory.ValueCipher, Context(memory, "value")), previousValue,
                    StringComparison.Ordinal)
                && memory.ExpiresAt.Equals(previousExpiresAt)
                    ? MemoryCorrectionCompensationState.Applied
                    : MemoryCorrectionCompensationState.Changed;
        }, cancellationToken);
    }

    public void Dispose() => _gate.Dispose();

    private async Task<T> ReadAsync<T>(Func<StoreSnapshot, T> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return action(await LoadAsync(cancellationToken));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> MutateAsync<T>(Func<StoreSnapshot, T> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await LoadAsync(cancellationToken);
            var result = action(snapshot);
            await SaveAsync(snapshot, cancellationToken);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<StoreSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return new StoreSnapshot();
        await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var snapshot = await JsonSerializer.DeserializeAsync<StoreSnapshot>(stream, JsonOptions, cancellationToken)
            is { SchemaVersion: 1 } loaded
                ? loaded
                : throw new InvalidDataException("记忆持久化文件为空或版本不受支持。");
        // 兼容早期全局指纹和旧密钥密文：在内存中升级，下一次变更会原子落盘。
        foreach (var proposal in snapshot.Proposals)
        {
            var keyContext = Context(proposal, "key");
            var valueContext = Context(proposal, "value");
            var key = _cipher.Unprotect(proposal.KeyCipher, keyContext);
            proposal.KeyHash = _cipher.Fingerprint(key, FingerprintContext(proposal));
            if (_cipher.RequiresReencryption(proposal.KeyCipher))
                proposal.KeyCipher = _cipher.Protect(key, keyContext);
            if (_cipher.RequiresReencryption(proposal.ValueCipher))
                proposal.ValueCipher = _cipher.Protect(
                    _cipher.Unprotect(proposal.ValueCipher, valueContext), valueContext);
        }
        foreach (var memory in snapshot.Memories)
        {
            var keyContext = Context(memory, "key");
            var valueContext = Context(memory, "value");
            var key = _cipher.Unprotect(memory.KeyCipher, keyContext);
            memory.KeyHash = _cipher.Fingerprint(key, FingerprintContext(memory));
            if (_cipher.RequiresReencryption(memory.KeyCipher))
                memory.KeyCipher = _cipher.Protect(key, keyContext);
            if (_cipher.RequiresReencryption(memory.ValueCipher))
                memory.ValueCipher = _cipher.Protect(
                    _cipher.Unprotect(memory.ValueCipher, valueContext), valueContext);
        }
        return snapshot;
    }

    private async Task SaveAsync(StoreSnapshot snapshot, CancellationToken cancellationToken)
    {
        // 永不原地覆盖正式文件：进程中断时最多遗留临时文件，不会留下半截 JSON。
        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, _filePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private StoredProposal ToStored(MemoryProposal proposal) => new()
    {
        Id = proposal.Id,
        TenantId = proposal.TenantId,
        SubjectId = proposal.SubjectId,
        Scope = proposal.Scope,
        SessionId = proposal.SessionId,
        KeyCipher = _cipher.Protect(proposal.Key, Context(proposal, "key")),
        KeyHash = _cipher.Fingerprint(proposal.Key, FingerprintContext(proposal)),
        ValueCipher = _cipher.Protect(proposal.Value, Context(proposal, "value")),
        CreatedAt = proposal.CreatedAt,
        ApprovalExpiresAt = proposal.ApprovalExpiresAt,
        MemoryExpiresAt = proposal.MemoryExpiresAt,
        Status = proposal.Status
    };

    private StoredMemory ToStored(MemoryRecord memory) => new()
    {
        Id = memory.Id,
        TenantId = memory.TenantId,
        SubjectId = memory.SubjectId,
        Scope = memory.Scope,
        SessionId = memory.SessionId,
        KeyCipher = _cipher.Protect(memory.Key, Context(memory, "key")),
        KeyHash = _cipher.Fingerprint(memory.Key, FingerprintContext(memory)),
        ValueCipher = _cipher.Protect(memory.Value, Context(memory, "value")),
        Version = memory.Version,
        CreatedAt = memory.CreatedAt,
        UpdatedAt = memory.UpdatedAt,
        ExpiresAt = memory.ExpiresAt
    };

    private MemoryRecord FromStored(StoredMemory memory) => new(memory.Id, memory.TenantId, memory.SubjectId,
        memory.Scope, memory.SessionId, _cipher.Unprotect(memory.KeyCipher, Context(memory, "key")),
        _cipher.Unprotect(memory.ValueCipher, Context(memory, "value")),
        memory.Version, memory.CreatedAt, memory.UpdatedAt, memory.ExpiresAt);

    private static string Context(MemoryProposal memory, string field) => string.Join('\u001f', memory.TenantId,
        memory.SubjectId, memory.Scope, memory.SessionId ?? string.Empty, memory.Id, field);

    private static string Context(MemoryRecord memory, string field) => string.Join('\u001f', memory.TenantId,
        memory.SubjectId, memory.Scope, memory.SessionId ?? string.Empty, memory.Id, field);

    private static string Context(StoredOwner memory, string field) => string.Join('\u001f', memory.TenantId,
        memory.SubjectId, memory.Scope, memory.SessionId ?? string.Empty, memory.Id, field);

    private static string FingerprintContext(MemoryProposal memory) => string.Join('\u001f', memory.TenantId,
        memory.SubjectId, memory.Scope, memory.SessionId ?? string.Empty);

    private static string FingerprintContext(MemoryRecord memory) => string.Join('\u001f', memory.TenantId,
        memory.SubjectId, memory.Scope, memory.SessionId ?? string.Empty);

    private static string FingerprintContext(StoredOwner memory) => string.Join('\u001f', memory.TenantId,
        memory.SubjectId, memory.Scope, memory.SessionId ?? string.Empty);

    private static bool OwnedBy(StoredOwner item, AccessContext access) =>
        string.Equals(item.TenantId, access.TenantId, StringComparison.Ordinal)
        && string.Equals(item.SubjectId, access.SubjectId, StringComparison.Ordinal);

    private sealed class StoreSnapshot
    {
        public StoreSnapshot() { }
        public int SchemaVersion { get; init; } = 1;
        public List<StoredProposal> Proposals { get; init; } = [];
        public List<StoredMemory> Memories { get; init; } = [];
    }

    private abstract class StoredOwner
    {
        public required string Id { get; init; }
        public required string TenantId { get; init; }
        public required string SubjectId { get; init; }
        public MemoryScope Scope { get; init; }
        public string? SessionId { get; init; }
        public required string KeyCipher { get; set; }
        public required string KeyHash { get; set; }
        public required string ValueCipher { get; set; }
    }

    private sealed class StoredProposal : StoredOwner
    {
        public StoredProposal() { }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset ApprovalExpiresAt { get; init; }
        public DateTimeOffset MemoryExpiresAt { get; init; }
        public MemoryProposalStatus Status { get; set; }
    }

    private sealed class StoredMemory : StoredOwner
    {
        public StoredMemory() { }
        public int Version { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
    }
}
