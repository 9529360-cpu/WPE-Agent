namespace WPE.Tests;

public sealed class RuntimeStateRestoreVerificationBoundaryTests
{
    [Fact]
    public void VerifierCannotMutateActiveRuntimeState()
    {
        var source=File.ReadAllText(Path.Combine(
            Root(),"Services","Backup","RuntimeStateBackupVerifier.cs"));

        foreach(var forbidden in new[]{
            "AppDataPaths.DataDirectory",
            "DataRootMaintenanceLease",
            "RuntimeProcessBootstrap",
            "ServiceLocator",
            "AutoTradingAgent",
            "IExchangeAdapter",
            "EmergencyCloseAllAsync",
            "ChangeAuthorizationModeAsync",
            "Mainnet"
        })
            Assert.DoesNotContain(forbidden,source,StringComparison.Ordinal);

        Assert.Contains("VerifyToStagingAsync",source,StringComparison.Ordinal);
        Assert.Contains("Restore staging directory must not already exist",source,StringComparison.Ordinal);
        Assert.Contains("ValidateExactBackupFileSet",source,StringComparison.Ordinal);
        Assert.Contains("EncryptedBackupPolicy.CanRestore",source,StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.FixedTimeEquals",source,StringComparison.Ordinal);
        Assert.Contains("Directory.Delete(stagingRoot, true)",source,StringComparison.Ordinal);
    }

    [Fact]
    public void BackupAndRestoreShareOneAuthoritativeInventory()
    {
        var root=Root();
        var service=File.ReadAllText(Path.Combine(root,"Services","Backup","RuntimeStateBackupService.cs"));
        var verifier=File.ReadAllText(Path.Combine(root,"Services","Backup","RuntimeStateBackupVerifier.cs"));
        var contracts=File.ReadAllText(Path.Combine(root,"Services","Backup","RuntimeStateBackupContracts.cs"));

        Assert.Contains("RuntimeStateBackupInventoryV1.AuthoritativeDatabases",service,StringComparison.Ordinal);
        Assert.Contains("RuntimeStateBackupInventoryV1.AuthoritativeFiles",service,StringComparison.Ordinal);
        Assert.Contains("RuntimeStateBackupInventoryV1.KindFor",verifier,StringComparison.Ordinal);
        Assert.Contains("public static class RuntimeStateBackupInventoryV1",contracts,StringComparison.Ordinal);
    }

    private static string Root()=>Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,"..","..","..",".."));
}
