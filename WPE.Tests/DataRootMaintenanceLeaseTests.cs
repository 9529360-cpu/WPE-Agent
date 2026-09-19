using 币安量化机器人.Services.Backup;

namespace WPE.Tests;

public sealed class DataRootMaintenanceLeaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-maintenance-lease-" + Guid.NewGuid().ToString("N"));
    private string LeasePath => Path.Combine(_directory, DataRootMaintenanceLease.LeaseFileName);

    public DataRootMaintenanceLeaseTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void MultipleRuntimeProcessesMayShareLeaseButMaintenanceRequiresExclusivity()
    {
        using var first = DataRootMaintenanceLease.AcquireProcessLease(LeasePath);
        using var second = DataRootMaintenanceLease.AcquireProcessLease(LeasePath);

        var error = Assert.Throws<InvalidOperationException>(() =>
            DataRootMaintenanceLease.AcquireExclusiveMaintenanceLease(LeasePath));
        Assert.Contains("data root is in use", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExclusiveMaintenanceBlocksRuntimeAndBecomesAvailableAfterRuntimeStops()
    {
        using (var runtime = DataRootMaintenanceLease.AcquireProcessLease(LeasePath))
        {
            Assert.Throws<InvalidOperationException>(() =>
                DataRootMaintenanceLease.AcquireExclusiveMaintenanceLease(LeasePath));
        }

        using var maintenance = DataRootMaintenanceLease.AcquireExclusiveMaintenanceLease(LeasePath);
        Assert.Throws<InvalidOperationException>(() =>
            DataRootMaintenanceLease.AcquireProcessLease(LeasePath));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { }
    }
}
