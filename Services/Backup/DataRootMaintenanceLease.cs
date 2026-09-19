using System.IO;

namespace 币安量化机器人.Services.Backup;

public static class DataRootMaintenanceLease
{
    public const string LeaseFileName = "data-root-maintenance.lock";

    public static FileStream AcquireProcessLease(string leasePath)
        => Open(leasePath, FileShare.ReadWrite);

    public static FileStream AcquireExclusiveMaintenanceLease(string leasePath)
        => Open(leasePath, FileShare.None);

    private static FileStream Open(string leasePath, FileShare share)
    {
        if (string.IsNullOrWhiteSpace(leasePath))
            throw new ArgumentException("Data-root lease path is required.", nameof(leasePath));

        var fullPath = Path.GetFullPath(leasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        try
        {
            return new FileStream(
                fullPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                share,
                bufferSize: 64,
                FileOptions.WriteThrough);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                share == FileShare.None
                    ? "The WPE data root is in use; maintenance requires all WPE processes to stop."
                    : "The WPE data root is under exclusive maintenance.",
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException("The WPE data-root maintenance lease is unavailable.", ex);
        }
    }
}
