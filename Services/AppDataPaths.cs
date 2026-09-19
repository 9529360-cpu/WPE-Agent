using System.IO;
using System.Text.Json;

namespace 币安量化机器人.Services;

public sealed class AppDataLayout
{
    public AppDataLayout(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("Application data root is required.", nameof(rootDirectory));
        RootDirectory = Path.GetFullPath(rootDirectory);
        DataDirectory = Ensure(Path.Combine(RootDirectory, "Data"));
        LogsDirectory = Ensure(Path.Combine(RootDirectory, "Logs"));
        BackupsDirectory = Ensure(Path.Combine(RootDirectory, "Backups"));
        RuntimeDirectory = Ensure(Path.Combine(RootDirectory, "Runtime"));
        ExportsDirectory = Ensure(Path.Combine(RootDirectory, "Exports"));
        TestArtifactsDirectory = Ensure(Path.Combine(RuntimeDirectory, "TestArtifacts"));
    }

    public string RootDirectory { get; }
    public string DataDirectory { get; }
    public string LogsDirectory { get; }
    public string BackupsDirectory { get; }
    public string RuntimeDirectory { get; }
    public string ExportsDirectory { get; }
    public string TestArtifactsDirectory { get; }

    public string DataFile(string relativePath) => ResolveInside(DataDirectory, relativePath);
    public string LogFile(string relativePath) => ResolveInside(LogsDirectory, relativePath);
    public string BackupFile(string relativePath) => ResolveInside(BackupsDirectory, relativePath);
    public string RuntimeFile(string relativePath) => ResolveInside(RuntimeDirectory, relativePath);
    public string TestArtifactFile(string relativePath) => ResolveInside(TestArtifactsDirectory, relativePath);

    private static string ResolveInside(string directory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new ArgumentException("Application data paths must be non-empty and relative.", nameof(relativePath));
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(directory, relativePath));
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Application data path escapes its assigned directory.");
        var parent = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        return resolved;
    }

    private static string Ensure(string path) { Directory.CreateDirectory(path); return Path.GetFullPath(path); }
}

public sealed record LegacyMigrationItem(string RelativePath, string Status, string? ErrorType = null);
public sealed record LegacyMigrationResult(DateTimeOffset CompletedAtUtc, IReadOnlyList<LegacyMigrationItem> Items)
{
    public int Copied => Items.Count(x => x.Status == "copied");
    public int Skipped => Items.Count(x => x.Status == "conflict" || x.Status == "excluded");
    public int Failed => Items.Count(x => x.Status == "failed");
}

public static class LegacyPortableDataMigrator
{
    private static readonly HashSet<string> AllowedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "agent-settings.json", "appsettings.json", "ui-preferences.json", "agent-memory.json", "ui-settings.json",
        "local-accounts.json", "local-session.dat", "device-license.dat", "llm-calls.jsonl", "llm-cache.jsonl"
    };
    private static readonly string[] DatabaseSuffixes =
    {
        ".db", ".db-wal", ".db-shm", ".sqlite", ".sqlite-wal", ".sqlite-shm", ".sqlite3", ".sqlite3-wal", ".sqlite3-shm"
    };

    public static LegacyMigrationResult Migrate(string legacyDataDirectory, AppDataLayout target)
    {
        var reportPath = target.RuntimeFile("portable-data-migration-v1.json");
        if (File.Exists(reportPath))
        {
            try
            {
                var completed = JsonSerializer.Deserialize<LegacyMigrationResult>(File.ReadAllText(reportPath));
                if (completed is not null && completed.Failed == 0)
                    return new LegacyMigrationResult(DateTimeOffset.UtcNow, [new("portable-data-migration-v1", "already-completed")]);
            }
            catch (JsonException) { }
        }
        var items = new List<LegacyMigrationItem>();
        if (!Directory.Exists(legacyDataDirectory)) return WriteReport(target, items);

        var legacyRoot = Path.GetFullPath(legacyDataDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string[] sources;
        try { sources = Directory.GetFiles(legacyDataDirectory, "*", SearchOption.AllDirectories); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            items.Add(new(".", "failed", ex.GetType().Name));
            return WriteReport(target, items);
        }
        foreach (var source in sources)
        {
            var fullSource = Path.GetFullPath(source);
            if (!fullSource.StartsWith(legacyRoot, StringComparison.OrdinalIgnoreCase)) continue;
            var relative = Path.GetRelativePath(legacyDataDirectory, fullSource).Replace('\\', '/');
            if (!IsAllowed(relative)) { items.Add(new(relative, "excluded")); continue; }
            try
            {
                var destination = target.DataFile(relative);
                if (File.Exists(destination)) { items.Add(new(relative, "conflict")); continue; }
                CopyAtomically(fullSource, destination);
                items.Add(new(relative, "copied"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                items.Add(new(relative, "failed", ex.GetType().Name));
            }
        }
        return WriteReport(target, items);
    }

    private static bool IsAllowed(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        if (normalized.Contains("../", StringComparison.Ordinal) || normalized.Equals("API.txt", StringComparison.OrdinalIgnoreCase)) return false;
        if (!normalized.Contains('/') && AllowedFiles.Contains(normalized)) return true;
        return DatabaseSuffixes.Any(suffix => normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static void CopyAtomically(string source, string destination)
    {
        var temp = destination + ".migration-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            File.Move(temp, destination, false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static LegacyMigrationResult WriteReport(AppDataLayout target, IReadOnlyList<LegacyMigrationItem> items)
    {
        var result = new LegacyMigrationResult(DateTimeOffset.UtcNow, items);
        var path = target.RuntimeFile("portable-data-migration-v1.json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
        return result;
    }
}

public static class AppDataPaths
{
    public const string DataRootEnvironmentVariable = "WPE_AGENT_DATA_ROOT";

    private static readonly Lazy<AppDataLayout> Current = new(() => new AppDataLayout(
        ResolveRootDirectory(
            Environment.GetEnvironmentVariable(DataRootEnvironmentVariable),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))));

    internal static string ResolveRootDirectory(string? configuredRoot, string localApplicationData)
    {
        if (string.IsNullOrWhiteSpace(configuredRoot))
            return Path.GetFullPath(Path.Combine(localApplicationData, "WPE Agent"));
        if (!Path.IsPathRooted(configuredRoot))
            throw new InvalidOperationException($"{DataRootEnvironmentVariable} must be an absolute path.");
        return Path.GetFullPath(configuredRoot);
    }

    public static string RootDirectory => Current.Value.RootDirectory;
    public static string DataDirectory => Current.Value.DataDirectory;
    public static string LogsDirectory => Current.Value.LogsDirectory;
    public static string BackupsDirectory => Current.Value.BackupsDirectory;
    public static string RuntimeDirectory => Current.Value.RuntimeDirectory;
    public static string ExportsDirectory => Current.Value.ExportsDirectory;
    public static string TestArtifactsDirectory => Current.Value.TestArtifactsDirectory;
    public static string File(string name) => Current.Value.DataFile(name);
    public static string LogFile(string name) => Current.Value.LogFile(name);
    public static string BackupFile(string name) => Current.Value.BackupFile(name);
    public static string RuntimeFile(string name) => Current.Value.RuntimeFile(name);
    public static string TestArtifactFile(string name) => Current.Value.TestArtifactFile(name);
    public static LegacyMigrationResult MigrateLegacyPortableData(string? installDirectory = null) =>
        LegacyPortableDataMigrator.Migrate(Path.Combine(installDirectory ?? AppContext.BaseDirectory, "Data"), Current.Value);
}
