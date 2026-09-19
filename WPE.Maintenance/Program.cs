using System.Text.Json;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Backup;

namespace WpeAgent.Maintenance;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<int> Main(string[] args)
    {
        string? operation = null;
        try
        {
            if (!OperatingSystem.IsWindows())
                return Fail("platform", "WPE maintenance requires Windows current-user DPAPI.");

            var parsed = Parse(args);
            operation = parsed.Operation;
            var dataRoot = RequireAbsolutePath(parsed.Options, "data-root");
            var layout = new AppDataLayout(dataRoot);

            return operation switch
            {
                "backup" => await BackupAsync(layout, parsed.Options).ConfigureAwait(false),
                "verify" => await VerifyAsync(layout, parsed.Options).ConfigureAwait(false),
                "restore" => await RestoreAsync(layout, parsed.Options).ConfigureAwait(false),
                _ => Fail("arguments", Usage())
            };
        }
        catch (ArgumentException ex)
        {
            return Fail("arguments", Safe(ex.Message));
        }
        catch (Exception ex)
        {
            return Fail(operation ?? "maintenance", Safe(ex.Message));
        }
    }

    private static async Task<int> BackupAsync(
        AppDataLayout layout,
        IReadOnlyDictionary<string, string> options)
    {
        RequireOnly(options, "data-root", "destination");
        var destination = RequireAbsolutePath(options, "destination");
        var result = await new RuntimeStateBackupService(layout)
            .CreateAsync(destination)
            .ConfigureAwait(false);

        Write(new
        {
            operation = "backup",
            success = true,
            result.BackupId,
            result.Directory,
            itemCount = result.Descriptor.Items.Count,
            result.Descriptor.ItemsSha256,
            createdAtUtc = result.Descriptor.Manifest.CreatedAtUtc
        });
        return 0;
    }

    private static async Task<int> VerifyAsync(
        AppDataLayout layout,
        IReadOnlyDictionary<string, string> options)
    {
        RequireOnly(options, "data-root", "backup");
        var backup = RequireAbsolutePath(options, "backup");
        var staging = Path.Combine(
            layout.RuntimeDirectory,
            "maintenance-verify-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await new RuntimeStateBackupVerifier()
                .VerifyToStagingAsync(backup, staging)
                .ConfigureAwait(false);
            Write(new
            {
                operation = "verify",
                success = true,
                result.BackupId,
                itemCount = result.Descriptor.Items.Count,
                result.Descriptor.ItemsSha256,
                result.VerifiedAtUtc
            });
            return 0;
        }
        finally
        {
            DeleteDirectoryOrFail(staging);
        }
    }

    private static async Task<int> RestoreAsync(
        AppDataLayout layout,
        IReadOnlyDictionary<string, string> options)
    {
        RequireOnly(options, "data-root", "backup", "confirm-backup-id");
        var backup = RequireAbsolutePath(options, "backup");
        var expectedBackupId = Require(options, "confirm-backup-id");
        if (!expectedBackupId.StartsWith("wpe-state-", StringComparison.Ordinal) ||
            expectedBackupId.Length > 96)
            throw new ArgumentException("A valid exact --confirm-backup-id is required.");

        var result = await new RuntimeStateRestoreService(layout)
            .RestoreAsync(backup, expectedBackupId)
            .ConfigureAwait(false);

        Write(new
        {
            operation = "restore",
            success = true,
            result.RestoreId,
            result.BackupId,
            result.SafetyBackupId,
            result.SafetyBackupDirectory,
            result.CommittedAtUtc,
            result.RestoredItemCount,
            result.ItemsSha256,
            result.SecurityStorageEvidenceSha256
        });
        return 0;
    }

    private static ParsedCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            throw new ArgumentException(Usage());

        var operation = args[0].Trim().ToLowerInvariant();
        if (operation is not ("backup" or "verify" or "restore"))
            throw new ArgumentException(Usage());

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Count; index += 2)
        {
            var name = args[index];
            if (!name.StartsWith("--", StringComparison.Ordinal) ||
                name.Length <= 2 ||
                index + 1 >= args.Count)
                throw new ArgumentException(Usage());

            var key = name[2..];
            if (!options.TryAdd(key, args[index + 1]))
                throw new ArgumentException($"Duplicate option --{key} is not allowed.");
        }

        return new(operation, options);
    }

    private static string RequireAbsolutePath(
        IReadOnlyDictionary<string, string> options,
        string key)
    {
        var value = Require(options, key);
        if (!Path.IsPathFullyQualified(value))
            throw new ArgumentException($"--{key} must be an absolute path.");
        return Path.GetFullPath(value);
    }

    private static string Require(
        IReadOnlyDictionary<string, string> options,
        string key)
    {
        if (!options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Missing required option --{key}.");
        return value.Trim();
    }

    private static void RequireOnly(
        IReadOnlyDictionary<string, string> options,
        params string[] allowed)
    {
        var set = allowed.ToHashSet(StringComparer.Ordinal);
        var unexpected = options.Keys.FirstOrDefault(key => !set.Contains(key));
        if (unexpected is not null)
            throw new ArgumentException($"Unsupported option --{unexpected}.");
    }

    private static int Fail(string operation, string message)
    {
        Write(new { operation, success = false, message });
        return operation == "platform" ? 3 : operation == "arguments" ? 2 : 4;
    }

    private static void Write(object value)
        => Console.WriteLine(JsonSerializer.Serialize(value, Json));

    private static string Safe(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "Maintenance operation failed." : value.Trim();
        text = text.Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= 500 ? text : text[..500];
    }

    private static string Usage()
        => "Usage: WPE.Maintenance backup --data-root <absolute> --destination <absolute> | " +
           "verify --data-root <absolute> --backup <absolute> | " +
           "restore --data-root <absolute> --backup <absolute> --confirm-backup-id <exact-id>";

    private static void DeleteDirectoryOrFail(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            Directory.Delete(path, true);
        }
        catch
        {
            throw new InvalidOperationException(
                "Maintenance verification staging cleanup failed.");
        }
        if (Directory.Exists(path))
            throw new InvalidOperationException(
                "Maintenance verification staging cleanup failed.");
    }

    private sealed record ParsedCommand(
        string Operation,
        IReadOnlyDictionary<string, string> Options);
}
