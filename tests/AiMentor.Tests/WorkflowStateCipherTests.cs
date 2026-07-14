using System.Security.Cryptography;
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
