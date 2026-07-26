using WpeAgent.Security;

namespace WPE.Tests;

public sealed class DogfoodDiagnosticPolicyTests
{
    private static readonly DateTimeOffset Now=new(2026,7,23,13,0,0,TimeSpan.Zero);

    [Fact]
    public void DisabledByDefaultAndRemoteOrMainnetFailClosedWithAudit()
    {
        var disabled=Setup(false);Assert.Equal("diagnostic.disabled",disabled.Policy.Authorize(Request()).Code);
        var remote=Setup();Assert.Equal("diagnostic.remote-forbidden",remote.Policy.Authorize(Request() with{IsLocalTransport=false}).Code);
        var mainnet=Setup();Assert.Equal("diagnostic.mainnet-forbidden",mainnet.Policy.Authorize(Request() with{Environment="Mainnet"}).Code);
        Assert.Single(disabled.Audit.Events);Assert.Single(remote.Audit.Events);Assert.Single(mainnet.Audit.Events);
    }

    [Fact]
    public void AuthenticationTamperExpiryEnvironmentMismatchAndLeastPrivilegeAreRejected()
    {
        var tamper=Setup(verifier:false);Assert.Equal("diagnostic.authentication-invalid",tamper.Policy.Authorize(Request()).Code);
        var expired=Setup();Assert.Equal("diagnostic.authority-expired",expired.Policy.Authorize(Request(Grant() with{ExpiresAtUtc=Now})).Code);
        var mismatch=Setup();Assert.Equal("diagnostic.environment-mismatch",mismatch.Policy.Authorize(Request(Grant() with{Environment="Sandbox"})).Code);
        var privilege=Setup();Assert.Equal("diagnostic.permission-denied",privilege.Policy.Authorize(Request(Grant([DogfoodDiagnosticPermission.ReadSanitizedAudit]))).Code);
        var broad=Setup();Assert.Equal("diagnostic.permission-invalid",broad.Policy.Authorize(Request(Grant([DogfoodDiagnosticPermission.ReadHealth,DogfoodDiagnosticPermission.TriggerEmergencyStop]))).Code);
    }

    [Fact]
    public void ReplayAndRevocationFailClosedAndEveryDecisionIsAudited()
    {
        var setup=Setup();var request=Request();Assert.True(setup.Policy.Authorize(request).Allowed);Assert.Equal("diagnostic.replay",setup.Policy.Authorize(request).Code);
        var revokedGrant=Grant() with{GrantId="grant-revoked"};setup.Policy.Revoke(revokedGrant.GrantId);Assert.Equal("diagnostic.authority-revoked",setup.Policy.Authorize(Request(revokedGrant) with{RequestId="request-revoked"}).Code);
        Assert.Equal(3,setup.Audit.Events.Count);Assert.All(setup.Audit.Events,x=>{Assert.NotEmpty(x.RequestId);Assert.NotEmpty(x.Code);Assert.Equal(Now,x.RecordedAtUtc);});
    }

    [Fact]
    public async Task IdenticalConcurrentReplayAllowsExactlyOneRequest()
    {
        const int concurrency=12;
        using var barrier=new Barrier(concurrency);
        var audit=new ConcurrentAudit();
        var policy=new DogfoodDiagnosticPolicy(true,new BarrierVerifier(barrier),audit,()=>Now);
        var request=Request() with{RequestId="qaa-0043-identical-replay"};

        var decisions=await Task.WhenAll(Enumerable.Range(0,concurrency).Select(_=>Task.Run(()=>policy.Authorize(request))));

        Assert.Single(decisions,x=>x.Allowed);
        Assert.Equal(concurrency-1,decisions.Count(x=>x.Code=="diagnostic.replay"));
        Assert.Equal(concurrency,audit.Count);
    }

    [Fact]
    public void UnknownActionFailsClosedAndIsAudited()
    {
        var setup=Setup();var result=setup.Policy.Authorize(Request() with{Action=(DogfoodDiagnosticAction)999});
        Assert.False(result.Allowed);Assert.Equal("diagnostic.action-unknown",result.Code);Assert.Single(setup.Audit.Events);
    }

    [Fact]
    public void EmergencyStopIsStickyRevokesGrantAndOnlyVerifiedLkgRollbackCanProceed()
    {
        var setup=Setup();var stopGrant=Grant([DogfoodDiagnosticPermission.TriggerEmergencyStop]);
        var stopped=setup.Policy.Authorize(Request(stopGrant,DogfoodDiagnosticAction.TriggerEmergencyStop));Assert.True(stopped.Allowed);Assert.True(stopped.EmergencyStopActive);
        Assert.Equal("diagnostic.authority-revoked",setup.Policy.Authorize(Request(stopGrant) with{RequestId="after-stop"}).Code);
        var freshRead=Grant() with{GrantId="fresh-read"};Assert.Equal("diagnostic.emergency-stop-blocked",setup.Policy.Authorize(Request(freshRead) with{RequestId="fresh-read-request"}).Code);
        var rollbackGrant=Grant([DogfoodDiagnosticPermission.RequestVerifiedLkgRollback]) with{GrantId="rollback-grant"};
        var rollback=setup.Policy.Authorize(Request(rollbackGrant,DogfoodDiagnosticAction.RequestVerifiedLkgRollback) with{RequestId="rollback-request",LastKnownGood=Lkg()});
        Assert.True(rollback.Allowed);Assert.Equal("diagnostic.lkg-rollback-authorized",rollback.Code);Assert.Equal("1.0.0",rollback.AuthorizedLkgVersion);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("stale")]
    [InlineData("mutable")]
    [InlineData("migration")]
    [InlineData("userdata")]
    public void RollbackRequiresFreshImmutableVerifiedCompatibleLkg(string mutation)
    {
        var lkg=Lkg();lkg=mutation switch{"hash"=>lkg with{HashesVerified=false},"stale"=>lkg with{VerifiedAtUtc=Now.AddMinutes(-11)},"mutable"=>lkg with{ImmutableRoot="C:\\build\\bin\\Release"},"migration"=>lkg with{ConfigurationCompatible=false},"userdata"=>lkg with{UserDataRollbackRequired=true},_=>lkg};
        var setup=Setup();var grant=Grant([DogfoodDiagnosticPermission.RequestVerifiedLkgRollback]);var result=setup.Policy.Authorize(Request(grant,DogfoodDiagnosticAction.RequestVerifiedLkgRollback) with{LastKnownGood=lkg});
        Assert.False(result.Allowed);Assert.StartsWith("diagnostic.",result.Code);Assert.Single(setup.Audit.Events);
    }

    [Fact]
    public void SlashFormMutableLkgPathIsRejected()
    {
        var result=AuthorizeRollback(Lkg() with{ImmutableRoot="C:/mutable/bin/Release"});
        Assert.False(result.Allowed);
        Assert.Equal("diagnostic.lkg-mutable",result.Code);
    }

    [Fact]
    public void SixtyFourCharacterNonHexManifestHashIsRejected()
    {
        var result=AuthorizeRollback(Lkg() with{ManifestSha256=new string('z',64)});
        Assert.False(result.Allowed);
        Assert.Equal("diagnostic.lkg-invalid",result.Code);
    }

    private static DogfoodDiagnosticDecision AuthorizeRollback(VerifiedLastKnownGood lkg)
    {
        var setup=Setup();
        var grant=Grant([DogfoodDiagnosticPermission.RequestVerifiedLkgRollback]);
        return setup.Policy.Authorize(Request(grant,DogfoodDiagnosticAction.RequestVerifiedLkgRollback) with{LastKnownGood=lkg});
    }

    private static SetupResult Setup(bool enabled=true,bool verifier=true){var audit=new Audit();return new(new(enabled,new Verifier(verifier),audit,()=>Now),audit);}
    private static DogfoodDiagnosticGrant Grant(IReadOnlyList<DogfoodDiagnosticPermission>? permissions=null)=>new("grant-1","local-user","device-1","Testnet",Now.AddMinutes(-1),Now.AddMinutes(5),permissions??[DogfoodDiagnosticPermission.ReadHealth],"os-auth-proof-reference");
    private static DogfoodDiagnosticRequest Request(DogfoodDiagnosticGrant? grant=null,DogfoodDiagnosticAction action=DogfoodDiagnosticAction.ReadHealth)=>new("request-1",action,"local-user","device-1","Testnet",true,grant??Grant());
    private static VerifiedLastKnownGood Lkg()=>new("1.0.0","C:\\WPE-Dogfood\\versions\\1.0.0",new string('a',64),Now.AddMinutes(-1),true,true,true,false);
    private sealed class Verifier(bool valid):ILocalDogfoodAuthenticationVerifier{public bool Verify(DogfoodDiagnosticGrant grant)=>valid;}
    private sealed class BarrierVerifier(Barrier barrier):ILocalDogfoodAuthenticationVerifier{public bool Verify(DogfoodDiagnosticGrant grant){barrier.SignalAndWait();return true;}}
    private sealed class Audit:IDogfoodDiagnosticAuditSink{public List<DogfoodDiagnosticAuditEvent> Events{get;}=[];public void Record(DogfoodDiagnosticAuditEvent auditEvent)=>Events.Add(auditEvent);}
    private sealed class ConcurrentAudit:IDogfoodDiagnosticAuditSink{private int _count;public int Count=>_count;public void Record(DogfoodDiagnosticAuditEvent auditEvent)=>Interlocked.Increment(ref _count);}
    private sealed record SetupResult(DogfoodDiagnosticPolicy Policy,Audit Audit);
}
