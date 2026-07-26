using \u5E01\u5B89\u91CF\u5316\u673A\u5668\u4EBA.Services.Security;

namespace WPE.Tests;

public sealed class BackupRestoreDrillPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PlanBindsManifestInventoryTargetTimeAndAuditCorrelation()
    {
        var request = ValidRequest();

        var plan = BackupRestoreDrillPlanner.CreatePlan(request, Now);

        Assert.True(plan.Allowed);
        var artifact = Assert.IsType<BackupRestoreDrillArtifact>(plan.Artifact);
        Assert.Equal(request.BackupId, artifact.BackupId);
        Assert.Equal(request.Manifest.ManifestSha256, artifact.ManifestSha256);
        Assert.Equal(request.RestoreTarget, artifact.RestoreTarget);
        Assert.Equal(request.AuditCorrelationId, artifact.AuditCorrelationId);
        Assert.Equal(64, artifact.PlanSha256.Length);
        Assert.DoesNotContain("payload", artifact.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("backup-id", "drill.backup-id-mismatch")]
    [InlineData("expired", "drill.expired-or-invalid-window")]
    [InlineData("target", "drill.restore-target-not-empty")]
    [InlineData("key", "drill.required-key-missing")]
    [InlineData("version", "drill.envelope-version-unsupported")]
    public void InvalidPlanInputsFailClosed(string mutation, string reasonCode)
    {
        var request = ValidRequest();
        request = mutation switch
        {
            "backup-id" => request with { BackupId = "other-backup" },
            "expired" => request with { ExpiresAtUtc = Now },
            "target" => request with { RestoreTargetIsEmpty = false },
            "key" => request with { AvailableKeys = Array.Empty<StorageKeyReference>() },
            _ => request with { Inventory = [request.Inventory[0] with { EnvelopeVersion = 9 }] }
        };

        var plan = BackupRestoreDrillPlanner.CreatePlan(request, Now);

        Assert.False(plan.Allowed);
        Assert.Equal(reasonCode, plan.ReasonCode);
        Assert.Null(plan.Artifact);
    }

    [Fact]
    public void ManifestTamperingIsRejected()
    {
        var request = ValidRequest();
        request = request with { Manifest = request.Manifest with { BackupId = "tampered" }, BackupId = "tampered" };

        var plan = BackupRestoreDrillPlanner.CreatePlan(request, Now);

        Assert.False(plan.Allowed);
        Assert.Equal("backup.manifest-integrity-failed", plan.ReasonCode);
    }

    [Fact]
    public void VerificationRequiresEveryEvidenceGate()
    {
        var request = ValidRequest();
        var artifact = BackupRestoreDrillPlanner.CreatePlan(request, Now).Artifact!;
        var evidence = ValidEvidence(artifact, request.Inventory) with { AuditRecorded = false };

        var result = BackupRestoreDrillPlanner.Verify(evidence, Now.AddMinutes(1));

        Assert.False(result.Allowed);
        Assert.Equal("drill.verification-incomplete", result.ReasonCode);
    }

    [Fact]
    public void VerificationRejectsTamperedArtifactAndInventory()
    {
        var request = ValidRequest();
        var artifact = BackupRestoreDrillPlanner.CreatePlan(request, Now).Artifact!;

        Assert.Equal("drill.plan-integrity-failed", BackupRestoreDrillPlanner.Verify(
            ValidEvidence(artifact with { RecordCount = 8 }, request.Inventory), Now.AddMinutes(1)).ReasonCode);

        var changedInventory = new[] { request.Inventory[0] with { RecordSetSha256 = new('D', 64) } };
        Assert.Equal("drill.inventory-mismatch", BackupRestoreDrillPlanner.Verify(
            ValidEvidence(artifact, changedInventory), Now.AddMinutes(1)).ReasonCode);
    }

    [Fact]
    public void VerificationRejectsExpiredPlanOrWrongTarget()
    {
        var request = ValidRequest();
        var artifact = BackupRestoreDrillPlanner.CreatePlan(request, Now).Artifact!;

        Assert.Equal("drill.plan-expired", BackupRestoreDrillPlanner.Verify(
            ValidEvidence(artifact, request.Inventory), artifact.ExpiresAtUtc).ReasonCode);
        Assert.Equal("drill.restore-target-mismatch", BackupRestoreDrillPlanner.Verify(
            ValidEvidence(artifact, request.Inventory) with { RestoreTarget = "other-target" }, Now.AddMinutes(1)).ReasonCode);
    }

    [Fact]
    public void CompleteEvidenceProducesHashOnlyVerificationArtifact()
    {
        var request = ValidRequest();
        var artifact = BackupRestoreDrillPlanner.CreatePlan(request, Now).Artifact!;

        var result = BackupRestoreDrillPlanner.Verify(
            ValidEvidence(artifact, request.Inventory), Now.AddMinutes(1));

        Assert.True(result.Allowed);
        Assert.Equal("drill.verified", result.ReasonCode);
        Assert.Equal(artifact.PlanSha256, result.PlanSha256);
        Assert.Equal(request.AuditCorrelationId, result.AuditCorrelationId);
    }

    private static BackupRestoreDrillRequest ValidRequest()
    {
        var keyReference = new StorageKeyReference("backup-key", 3);
        var file = new EncryptedBackupFile("backup.enc", 128, new('A', 64));
        var manifest = new EncryptedBackupManifest(
            EncryptedBackupPolicy.CurrentFormatVersion,
            "backup-20260722",
            Now.AddMinutes(-5),
            keyReference,
            EncryptedBackupPolicy.RequiredCipherSuite,
            [file],
            string.Empty);
        manifest = manifest with { ManifestSha256 = EncryptedBackupPolicy.ComputeManifestHash(manifest) };
        var inventory = new BackupEnvelopeInventory(1, keyReference, 7, new('B', 64));
        var recoveryKey = new StorageKeyDescriptor(
            keyReference.KeyId,
            keyReference.Version,
            StorageKeyState.Active,
            PlatformKeyStoreKind.WindowsDpapiCurrentUser,
            Now.AddDays(-1));

        return new(
            manifest.BackupId,
            manifest,
            true,
            recoveryKey,
            new Dictionary<string, string> { [file.LogicalName] = file.Sha256 },
            [inventory],
            [keyReference],
            7,
            manifest.CreatedAtUtc,
            Now.AddHours(1),
            "restore-staging-01",
            true,
            "audit-correlation-01");
    }

    private static BackupRestoreDrillEvidence ValidEvidence(
        BackupRestoreDrillArtifact artifact,
        IReadOnlyList<BackupEnvelopeInventory> inventory)
        => new(artifact, inventory, artifact.RecordCount, artifact.RestoreTarget, true, true, true, true);
}
