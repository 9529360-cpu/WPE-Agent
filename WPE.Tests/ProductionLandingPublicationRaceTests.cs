using System.Security.Cryptography;
using WpeAgent.ProductionLanding;

namespace WPE.Tests;

public sealed class ProductionLandingPublicationRaceTests:IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,7,23,12,0,0,TimeSpan.Zero);
    private readonly string _root=Path.Combine(Path.GetTempPath(),"wpe-landing-"+Guid.NewGuid().ToString("N"));
    public ProductionLandingPublicationRaceTests()=>Directory.CreateDirectory(_root);
    public void Dispose(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();try{Directory.Delete(_root,true);}catch{}}

    [Theory]
    [InlineData(PublicationStage.LeaseAcquired)]
    [InlineData(PublicationStage.WriteFlushed)]
    [InlineData(PublicationStage.PrivateRenameComplete)]
    [InlineData(PublicationStage.BeforeFinalSerialization)]
    public async Task RevocationFirstAtEveryPrecommitStageExposesNothing(PublicationStage stop)
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var resume=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher=Publisher(async(stage,ct)=>{if(stage==stop){entered.SetResult();await resume.Task.WaitAsync(ct);}});await Register(publisher);var publish=publisher.PublishAsync(Request());await entered.Task;
        Assert.True(await Publisher().RevokeAsync("publication-1"));resume.SetResult();var result=await publish;
        Assert.False(result.Published);Assert.Null(await Publisher().ReadCommittedAsync("publication-1"));var state=await Publisher().InspectAsync("publication-1");Assert.False(state.ManifestVisible);Assert.Equal("available",state.TokenStatus);Assert.NotEqual("released",state.LeaseStatus);
    }

    [Fact]
    public async Task FinalValidationAndCommitSerializeAgainstRevocation()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var resume=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher=Publisher(async(stage,ct)=>{if(stage==PublicationStage.FinalValidationComplete){entered.SetResult();await resume.Task.WaitAsync(ct);}});await Register(publisher);var publish=publisher.PublishAsync(Request());await entered.Task;
        var revoke=Task.Run(()=>Publisher().RevokeAsync("publication-1"));await Task.Delay(100);Assert.False(revoke.IsCompleted);resume.SetResult();Assert.True((await publish).Published);Assert.True(await revoke);
        Assert.Equal(Bytes,await Publisher().ReadCommittedAsync("publication-1"));var state=await Publisher().InspectAsync("publication-1");Assert.True(state.ManifestVisible);Assert.Equal("consumed",state.TokenStatus);Assert.Equal("released",state.LeaseStatus);Assert.False((await publisher.PublishAsync(Request())).Published);
    }

    [Fact]
    public async Task RestartRecoveryDoesNotResumeOrAdoptSealedObject()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var never=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var cancel=new CancellationTokenSource();
        var publisher=Publisher(async(stage,ct)=>{if(stage==PublicationStage.PrivateRenameComplete){entered.SetResult();await never.Task.WaitAsync(ct);}});await Register(publisher);var publish=publisher.PublishAsync(Request(),cancel.Token);await entered.Task;cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>publish);
        var restarted=Publisher();await restarted.RecoverFailClosedAsync();Assert.Null(await restarted.ReadCommittedAsync("publication-1"));var state=await restarted.InspectAsync("publication-1");Assert.False(state.ManifestVisible);Assert.Equal("available",state.TokenStatus);Assert.Equal("failed",state.LeaseStatus);
    }

    [Fact]
    public async Task ExpiredLeaseAndDigestMismatchFailClosed()
    {
        var publisher=Publisher();await Register(publisher);var expired=Request() with{LeaseExpiresAtUtc=Now};Assert.False((await publisher.PublishAsync(expired)).Published);
        var mismatch=Request() with{Sha256=new string('0',64)};Assert.False((await publisher.PublishAsync(mismatch)).Published);Assert.Null(await publisher.ReadCommittedAsync("publication-1"));
    }

    [Fact]
    public async Task SealedNamespaceIsNeverAReadableManifest()
    {
        var publisher=Publisher();await Register(publisher);Assert.True((await publisher.PublishAsync(Request())).Published);Assert.Empty(Directory.GetFiles(_root,"*.manifest",SearchOption.AllDirectories));Assert.Empty(Directory.GetFiles(_root,"*.json",SearchOption.AllDirectories));Assert.Equal(Bytes,await publisher.ReadCommittedAsync("publication-1"));
    }

    private static readonly byte[] Bytes="canonical-production-report"u8.ToArray();
    private ProductionLandingPublisher Publisher(Func<PublicationStage,CancellationToken,Task>? stage=null)=>new(_root,()=>Now,stage);
    private static ProductionPublicationRequest Request()=>new("publication-1","fence-v7","token-1","local-authority",Now.AddMinutes(5),Bytes,Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant());
    private static Task Register(ProductionLandingPublisher p)=>p.RegisterLocalAuthorityAsync("publication-1","fence-v7","token-1","local-authority");
}
