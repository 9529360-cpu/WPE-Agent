namespace WPE.Tests;

public sealed class RuntimeStateRestoreActivationBoundaryTests
{
    [Fact]
    public void RestoreActivationOwnsOnlyLocalDataGenerationMutation()
    {
        var root = Root();
        var service = File.ReadAllText(Path.Combine(
            root, "Services", "Backup", "RuntimeStateRestoreService.cs"));
        var recovery = File.ReadAllText(Path.Combine(
            root, "Services", "Backup", "RuntimeStateRestoreRecovery.cs"));

        foreach (var source in new[] { service, recovery })
        foreach (var forbidden in new[]
        {
            "ServiceLocator",
            "AutoTradingAgent",
            "TradingExecutionGateway",
            "ReliableOrderExecutor",
            "IExchangeAdapter",
            "EmergencyCloseAllAsync",
            "ChangeAuthorizationModeAsync",
            "Mainnet"
        })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);

        Assert.Contains("AcquireExclusiveMaintenanceLease", service, StringComparison.Ordinal);
        Assert.Contains("VerifyToStagingAsync", service, StringComparison.Ordinal);
        Assert.Contains("CreateUnderExclusiveLeaseAsync", service, StringComparison.Ordinal);
        Assert.Contains("Directory.Move(_layout.DataDirectory, rollback)", service, StringComparison.Ordinal);
        Assert.Contains("Directory.Move(stage, _layout.DataDirectory)", service, StringComparison.Ordinal);
        Assert.Contains("VerifyPlaintextDataDirectoryAsync", service, StringComparison.Ordinal);
        Assert.Contains("_securityRestoreEvidence.CommitAsync", service, StringComparison.Ordinal);
        Assert.Contains("RuntimeStateRestorePhase.Committed", service, StringComparison.Ordinal);
        var evidenceCommit = service.IndexOf("_securityRestoreEvidence.CommitAsync", StringComparison.Ordinal);
        var generationCommit = service.IndexOf("journal = journal with { Phase = RuntimeStateRestorePhase.Committed }", StringComparison.Ordinal);
        Assert.True(evidenceCommit >= 0 && generationCommit > evidenceCommit);
        Assert.Contains("RecoverUnderExclusiveLease", recovery, StringComparison.Ordinal);
    }

    [Fact]
    public void SecurityStorageEvidenceIsAuthenticatedReadVerifyThenAppendEvidence()
    {
        var source = File.ReadAllText(Path.Combine(
            Root(), "Services", "Backup", "RuntimeStateSecurityStorageRestoreEvidence.cs"));

        Assert.Contains("SqliteOpenMode.ReadOnly", source, StringComparison.Ordinal);
        Assert.Contains("EncryptedRecordCodec.Decode", source, StringComparison.Ordinal);
        Assert.Contains("_encryption.Decrypt", source, StringComparison.Ordinal);
        Assert.Contains("BackupRestoreDrillPlanner.CreatePlan", source, StringComparison.Ordinal);
        Assert.Contains("SecurityStorageRuntime", source, StringComparison.Ordinal);
        Assert.Contains("security-storage.restore-committed", source, StringComparison.Ordinal);

        foreach (var forbidden in new[]
        {
            "ServiceLocator",
            "AutoTradingAgent",
            "TradingExecutionGateway",
            "IExchangeAdapter",
            "Mainnet"
        })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    private static string Root() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", ".."));
}
