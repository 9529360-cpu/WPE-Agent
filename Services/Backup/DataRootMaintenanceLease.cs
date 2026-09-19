using System.IO;

namespace 币安量化机器人.Services.Backup;

public enum DataRootLeaseMode
{
    ProcessShared,
    ExclusiveMaintenance
}

public sealed class DataRootMaintenanceLeaseHandle : IDisposable
{
    private FileStream? _stream;

    internal DataRootMaintenanceLeaseHandle(
        string leasePath,
        DataRootLeaseMode mode,
        FileStream stream)
    {
        LeasePath = Path.GetFullPath(leasePath);
        Mode = mode;
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public string LeasePath { get; }
    public DataRootLeaseMode Mode { get; }
    public bool IsHeld => Volatile.Read(ref _stream) is not null;

    internal void RequireExclusiveFor(string leasePath)
    {
        if (!IsHeld ||
            Mode != DataRootLeaseMode.ExclusiveMaintenance ||
            !string.Equals(LeasePath, Path.GetFullPath(leasePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("An active exclusive WPE data-root maintenance lease is required.");
    }

    public void Dispose()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        stream?.Dispose();
    }
}

public static class DataRootMaintenanceLease
{
    public const string LeaseFileName = "data-root-maintenance.lock";

    public static DataRootMaintenanceLeaseHandle AcquireProcessLease(string leasePath)
        => Open(leasePath, DataRootLeaseMode.ProcessShared, FileShare.ReadWrite);

    public static DataRootMaintenanceLeaseHandle AcquireExclusiveMaintenanceLease(string leasePath)
        => Open(leasePath, DataRootLeaseMode.ExclusiveMaintenance, FileShare.None);

    private static DataRootMaintenanceLeaseHandle Open(
        string leasePath,
        DataRootLeaseMode mode,
        FileShare share)
    {
        if (string.IsNullOrWhiteSpace(leasePath))
            throw new ArgumentException("Data-root lease path is required.", nameof(leasePath));

        var fullPath = Path.GetFullPath(leasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        try
        {
            var stream = new FileStream(
                fullPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                share,
                bufferSize: 64,
                FileOptions.WriteThrough);
            return new(fullPath, mode, stream);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                mode == DataRootLeaseMode.ExclusiveMaintenance
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
