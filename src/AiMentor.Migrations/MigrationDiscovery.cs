using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AiMentor.Migrations;

public sealed record MigrationScript(
    int Version,
    string FileName,
    string FullPath,
    string Sha256,
    string SqlText);

public static partial class MigrationDiscovery
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static IReadOnlyList<MigrationScript> Discover(string migrationsRoot)
    {
        if (string.IsNullOrWhiteSpace(migrationsRoot))
            throw new MigrationException("MIGRATION_ROOT_MISSING", "必须提供迁移目录。");

        var fullRoot = Path.GetFullPath(migrationsRoot);
        if (!Directory.Exists(fullRoot))
            throw new MigrationException("MIGRATION_ROOT_NOT_FOUND", "迁移目录不存在。");

        var sqlFiles = Directory.EnumerateFiles(fullRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(Path.GetExtension(path), ".sql", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var invalidFile = sqlFiles.Select(path => Path.GetFileName(path) ?? string.Empty)
            .FirstOrDefault(fileName => !MigrationFileName().IsMatch(fileName));
        if (invalidFile is not null)
        {
            // 静默跳过 SQL 会让发布包与数据库版本分叉，因此未知命名必须阻断整个批次。
            throw new MigrationException("MIGRATION_FILE_NAME_INVALID", $"SQL 文件 {invalidFile} 不符合 NNN_*.sql 命名规则。");
        }

        var candidates = sqlFiles
            .Select(CreateCandidate)
            .OrderBy(candidate => candidate.Version)
            .ThenBy(candidate => candidate.FileName, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 0)
            throw new MigrationException("MIGRATION_SET_EMPTY", "迁移目录中没有 NNN_*.sql 脚本。");

        var duplicate = candidates.GroupBy(candidate => candidate.Version)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new MigrationException("MIGRATION_VERSION_DUPLICATE", $"迁移版本 {duplicate.Key:000} 不唯一。");

        for (var index = 0; index < candidates.Length; index++)
        {
            var expectedVersion = index + 1;
            if (candidates[index].Version != expectedVersion)
                throw new MigrationException("MIGRATION_VERSION_GAP", $"迁移版本必须从 001 连续，缺少 {expectedVersion:000}。");
        }

        return candidates;
    }

    private static MigrationScript CreateCandidate(string path)
    {
        var fileName = Path.GetFileName(path);
        var match = MigrationFileName().Match(fileName);
        if (!match.Success)
            throw new MigrationException("MIGRATION_FILE_NAME_INVALID", $"SQL 文件 {fileName} 命名无效。");

        var version = int.Parse(match.Groups["version"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        var bytes = File.ReadAllBytes(path);
        string sqlText;
        try
        {
            sqlText = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new MigrationException("MIGRATION_ENCODING_INVALID", $"迁移 {fileName} 不是有效 UTF-8。", exception);
        }

        if (sqlText.Length > 0 && sqlText[0] == '\uFEFF') sqlText = sqlText[1..];
        var checksum = Convert.ToHexString(SHA256.HashData(bytes));
        return new MigrationScript(version, fileName, Path.GetFullPath(path), checksum, sqlText);
    }

    [GeneratedRegex("^(?<version>[0-9]{3})_.+\\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationFileName();
}
