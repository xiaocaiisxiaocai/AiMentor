using AiMentor.Migrations;
using Xunit;

namespace AiMentor.Tests;

public sealed class MigrationDiscoveryTests
{
    [Fact]
    public void DiscoverRejectsMissingVersion()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "001_first.sql"), "SELECT 1;");
        File.WriteAllText(Path.Combine(directory.Path, "003_third.sql"), "SELECT 3;");

        var error = Assert.Throws<MigrationException>(() => MigrationDiscovery.Discover(directory.Path));

        Assert.Equal("MIGRATION_VERSION_GAP", error.Code);
    }

    [Fact]
    public void DiscoverRejectsDuplicateVersion()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "001_first.sql"), "SELECT 1;");
        File.WriteAllText(Path.Combine(directory.Path, "001_alias.sql"), "SELECT 2;");

        var error = Assert.Throws<MigrationException>(() => MigrationDiscovery.Discover(directory.Path));

        Assert.Equal("MIGRATION_VERSION_DUPLICATE", error.Code);
    }

    [Fact]
    public void DiscoverRejectsSqlFileOutsideVersionedNamingConvention()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "001_first.sql"), "SELECT 1;");
        File.WriteAllText(Path.Combine(directory.Path, "manual_patch.SQL"), "SELECT 2;");

        var error = Assert.Throws<MigrationException>(() => MigrationDiscovery.Discover(directory.Path));

        Assert.Equal("MIGRATION_FILE_NAME_INVALID", error.Code);
    }

    [Fact]
    public void LedgerValidationRejectsTamperedChecksum()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "001_first.sql");
        File.WriteAllText(path, "SELECT 1;");
        var original = Assert.Single(MigrationDiscovery.Discover(directory.Path));
        File.WriteAllText(path, "SELECT 2;");
        var tampered = Assert.Single(MigrationDiscovery.Discover(directory.Path));
        var ledger = new[]
        {
            new AppliedMigration(original.Version, original.FileName, original.Sha256, "release-a",
                DateTimeOffset.UtcNow)
        };

        var error = Assert.Throws<MigrationException>(() => MigrationLedgerValidator.Validate([tampered], ledger));

        Assert.Equal("MIGRATION_CHECKSUM_MISMATCH", error.Code);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aimentor-migrations-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, true);
    }
}
