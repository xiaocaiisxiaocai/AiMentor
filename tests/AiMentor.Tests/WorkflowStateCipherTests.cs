using System.Security.Cryptography;
using System.Text;
using AiMentor.Infrastructure;
using Xunit;

namespace AiMentor.Tests;

public sealed class WorkflowStateCipherTests
{
    [Fact]
    public void NewCipherShouldReadOldKeyAndRejectUnknownOrWrongContext()
    {
        var v1 = RandomNumberGenerator.GetBytes(32);
        var v2 = RandomNumberGenerator.GetBytes(32);
        var oldCipher = new AesGcmWorkflowStateCipher("2026-01", new Dictionary<string, byte[]>
        {
            ["2026-01"] = v1
        });
        var encrypted = oldCipher.Protect("sensitive-checkpoint", "agent-run:run-1");
        var rotated = new AesGcmWorkflowStateCipher("2026-07", new Dictionary<string, byte[]>
        {
            ["2026-01"] = v1,
            ["2026-07"] = v2
        });

        Assert.Equal("sensitive-checkpoint",
            rotated.Unprotect(encrypted.KeyVersion, encrypted.Ciphertext, "agent-run:run-1"));
        Assert.True(rotated.RequiresReencryption(encrypted.KeyVersion));
        Assert.ThrowsAny<CryptographicException>(() =>
            rotated.Unprotect(encrypted.KeyVersion, encrypted.Ciphertext, "agent-run:run-2"));
        var withoutOldKey = new AesGcmWorkflowStateCipher("2026-07", new Dictionary<string, byte[]>
        {
            ["2026-07"] = v2
        });
        Assert.ThrowsAny<CryptographicException>(() =>
            withoutOldKey.Unprotect(encrypted.KeyVersion, encrypted.Ciphertext, "agent-run:run-1"));
    }

    [Fact]
    public void LegacyCheckpointShouldRequireExplicitCompatibilityCipher()
    {
        var masterKey = RandomNumberGenerator.GetBytes(32);
        var legacy = new AesGcmMemoryCipher(masterKey);
        var legacyPayload = legacy.Protect("legacy-checkpoint", "agent-run:run-1");
        var compatible = new AesGcmWorkflowStateCipher("v2", new Dictionary<string, byte[]>
        {
            ["v2"] = RandomNumberGenerator.GetBytes(32)
        }, legacy);

        Assert.Equal("legacy-checkpoint", compatible.Unprotect(null, legacyPayload, "agent-run:run-1"));
        Assert.True(compatible.RequiresReencryption(null));
    }
}

public sealed class MemoryCipherKeyRingTests
{
    [Fact]
    public void RotatedCipherReadsOldVersionWritesActiveVersionAndKeepsFingerprintStable()
    {
        var v1 = RandomNumberGenerator.GetBytes(32);
        var v2 = RandomNumberGenerator.GetBytes(32);
        var fingerprintKey = RandomNumberGenerator.GetBytes(32);
        var oldCipher = new AesGcmMemoryCipher("2026-01", new Dictionary<string, byte[]>
        {
            ["2026-01"] = v1
        }, fingerprintKey);
        var oldPayload = oldCipher.Protect("sensitive-memory", "tenant-a:memory-1:value");
        var rotated = new AesGcmMemoryCipher("2026-07", new Dictionary<string, byte[]>
        {
            ["2026-01"] = v1,
            ["2026-07"] = v2
        }, fingerprintKey);

        Assert.Equal("sensitive-memory",
            rotated.Unprotect(oldPayload, "tenant-a:memory-1:value"));
        Assert.True(rotated.RequiresReencryption(oldPayload));
        var activePayload = rotated.Protect("sensitive-memory", "tenant-a:memory-1:value");
        Assert.StartsWith("mem1.2026-07.", activePayload, StringComparison.Ordinal);
        Assert.False(rotated.RequiresReencryption(activePayload));
        Assert.Equal("sensitive-memory",
            rotated.Unprotect(activePayload, "tenant-a:memory-1:value"));
        Assert.Equal(oldCipher.Fingerprint("Answer.Format", "tenant-a:user-a"),
            rotated.Fingerprint("Answer.Format", "tenant-a:user-a"));
        Assert.ThrowsAny<CryptographicException>(() =>
            rotated.Unprotect(oldPayload, "tenant-b:memory-1:value"));

        var withoutOldKey = new AesGcmMemoryCipher("2026-07", new Dictionary<string, byte[]>
        {
            ["2026-07"] = v2
        }, fingerprintKey);
        Assert.ThrowsAny<CryptographicException>(() =>
            withoutOldKey.Unprotect(oldPayload, "tenant-a:memory-1:value"));
        Assert.Throws<ArgumentException>(() => new AesGcmMemoryCipher("2026.07",
            new Dictionary<string, byte[]> { ["2026.07"] = v2 }, fingerprintKey));
        Assert.Throws<ArgumentException>(() => new AesGcmMemoryCipher("2026_07",
            new Dictionary<string, byte[]>
            {
                ["2026_07"] = v2,
                ["2026.01"] = v1
            }, fingerprintKey));
    }

    [Fact]
    public void VersionedRingReadsLegacyV1EnvelopeOnlyWhenV1KeyIsRetained()
    {
        var v1 = RandomNumberGenerator.GetBytes(32);
        var v2 = RandomNumberGenerator.GetBytes(32);
        const string context = "tenant-a:legacy-memory:value";
        var legacyPayload = LegacyProtect(v1, "legacy-memory", context);
        var compatible = new AesGcmMemoryCipher("2026-07", new Dictionary<string, byte[]>
        {
            ["v1"] = v1,
            ["2026-07"] = v2
        }, RandomNumberGenerator.GetBytes(32));

        Assert.Equal("legacy-memory", compatible.Unprotect(legacyPayload, context));
        Assert.True(compatible.RequiresReencryption(legacyPayload));
        var withoutLegacy = new AesGcmMemoryCipher("2026-07", new Dictionary<string, byte[]>
        {
            ["2026-07"] = v2
        }, RandomNumberGenerator.GetBytes(32));
        Assert.ThrowsAny<CryptographicException>(() => withoutLegacy.Unprotect(legacyPayload, context));
    }

    private static string LegacyProtect(byte[] masterKey, string plaintext, string context)
    {
        var key = HMACSHA256.HashData(masterKey, Encoding.UTF8.GetBytes("AiMentor.Memory.Encryption.v1"));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var source = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[source.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, source, ciphertext, tag, Encoding.UTF8.GetBytes($"v1\n{context}"));
        return string.Join('.', "v1", Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext),
            Convert.ToBase64String(tag));
    }
}
