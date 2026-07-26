using System.Reflection;
using System.Xml.Linq;
using WpeAgent.Plugins;
using 币安量化机器人.Services;

namespace WPE.Tests;

public sealed class AppDataAndVersionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wpe-path-tests-{Guid.NewGuid():N}");

    public AppDataAndVersionTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Layout_KeepsEveryMutablePathInsidePerUserRoot()
    {
        var layout = new AppDataLayout(Path.Combine(_root, "local-app-data", "WPE Agent"));

        foreach (var path in new[]
        {
            layout.DataFile("agent-settings.json"), layout.DataFile("agent.db"), layout.LogFile("app.log"),
            layout.BackupFile("backup.zip"), layout.RuntimeFile("state.json"), layout.TestArtifactFile("suite/report.json")
        })
            Assert.StartsWith(layout.RootDirectory + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);

        Assert.Throws<InvalidOperationException>(() => layout.DataFile("../escape.txt"));
        Assert.Throws<ArgumentException>(() => layout.LogFile(Path.Combine(_root, "absolute.log")));
    }

    [Fact]
    public void PortableMigration_IsAtomicIdempotentAndNeverCopiesPlaintextSecrets()
    {
        var install = Path.Combine(_root, "read-only-install");
        var legacy = Path.Combine(install, "Data");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "agent-settings.json"), "{\"encryptedApiKey\":\"dpapi-cipher\"}");
        File.WriteAllText(Path.Combine(legacy, "agent.db"), "sqlite-data");
        File.WriteAllText(Path.Combine(legacy, "agent.db-wal"), "wal-data");
        File.WriteAllText(Path.Combine(legacy, "API.txt"), "PLAINTEXT-SECRET-MUST-NOT-MIGRATE");
        File.WriteAllText(Path.Combine(legacy, "secret.txt"), "ANOTHER-PLAINTEXT-SECRET");
        foreach (var file in Directory.EnumerateFiles(legacy)) File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);

        var layout = new AppDataLayout(Path.Combine(_root, "local-app-data", "WPE Agent"));
        File.WriteAllText(layout.DataFile("agent-settings.json"), "existing-wins");
        var before = Directory.EnumerateFiles(install, "*", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllText);

        var first = LegacyPortableDataMigrator.Migrate(legacy, layout);
        var second = LegacyPortableDataMigrator.Migrate(legacy, layout);

        Assert.Equal("existing-wins", File.ReadAllText(layout.DataFile("agent-settings.json")));
        Assert.Equal("sqlite-data", File.ReadAllText(layout.DataFile("agent.db")));
        Assert.Equal("wal-data", File.ReadAllText(layout.DataFile("agent.db-wal")));
        Assert.False(File.Exists(layout.DataFile("API.txt")));
        Assert.False(File.Exists(layout.DataFile("secret.txt")));
        Assert.Equal(2, first.Copied);
        Assert.Equal(0, second.Copied);
        Assert.All(before, pair => Assert.Equal(pair.Value, File.ReadAllText(pair.Key)));

        var diagnostic = File.ReadAllText(layout.RuntimeFile("portable-data-migration-v1.json"));
        Assert.DoesNotContain("PLAINTEXT-SECRET-MUST-NOT-MIGRATE", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("ANOTHER-PLAINTEXT-SECRET", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductVersion_ComesFromProjectGeneratedAssemblyMetadata()
    {
        var assembly = typeof(AppDataPaths).Assembly;
        var assemblyVersion = assembly.GetName().Version?.ToString(3);
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
        var project = FindApplicationProject();
        var declared = XDocument.Load(project).Descendants().Single(x => x.Name.LocalName == "Version").Value.Trim();

        Assert.Equal(declared, assemblyVersion);
        Assert.Equal(declared, informational);
        Assert.Equal(declared, PluginPhase0.HostVersion);
        Assert.Equal("3.6.0", declared);
    }

    private static string FindApplicationProject()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var projects = directory.GetFiles("*.csproj", SearchOption.TopDirectoryOnly)
                .Where(x => !x.Name.Equals("WPE.Tests.csproj", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (projects.Length == 1) return projects[0].FullName;
        }
        throw new InvalidOperationException("Application project file was not found.");
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch { }
    }
}
