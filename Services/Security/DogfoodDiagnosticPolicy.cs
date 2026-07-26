namespace WpeAgent.Security;

public enum DogfoodDiagnosticPermission
{
    ReadHealth,
    ReadSanitizedAudit,
    TriggerEmergencyStop,
    RequestVerifiedLkgRollback
}

public enum DogfoodDiagnosticAction
{
    ReadHealth,
    ReadSanitizedAudit,
    TriggerEmergencyStop,
    RequestVerifiedLkgRollback
}

public sealed record DogfoodDiagnosticGrant(
    string GrantId,
    string PrincipalId,
    string DeviceId,
    string Environment,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<DogfoodDiagnosticPermission> Permissions,
    string AuthenticationProof);

public sealed record VerifiedLastKnownGood(
    string Version,
    string ImmutableRoot,
    string ManifestSha256,
    DateTimeOffset VerifiedAtUtc,
    bool HashesVerified,
    bool RollbackPreflightPassed,
    bool ConfigurationCompatible,
    bool UserDataRollbackRequired);

public sealed record DogfoodDiagnosticRequest(
    string RequestId,
    DogfoodDiagnosticAction Action,
    string PrincipalId,
    string DeviceId,
    string Environment,
    bool IsLocalTransport,
    DogfoodDiagnosticGrant? Grant,
    VerifiedLastKnownGood? LastKnownGood=null);

public sealed record DogfoodDiagnosticDecision(bool Allowed,string Code,bool EmergencyStopActive=false,string? AuthorizedLkgVersion=null);

public sealed record DogfoodDiagnosticAuditEvent(
    string RequestId,
    string GrantId,
    string PrincipalId,
    string DeviceId,
    string Environment,
    DogfoodDiagnosticAction Action,
    bool Allowed,
    string Code,
    DateTimeOffset RecordedAtUtc,
    string? LkgVersion);

public interface ILocalDogfoodAuthenticationVerifier
{
    bool Verify(DogfoodDiagnosticGrant grant);
}

public interface IDogfoodDiagnosticAuditSink
{
    void Record(DogfoodDiagnosticAuditEvent auditEvent);
}

public sealed class DogfoodDiagnosticPolicy
{
    public static readonly TimeSpan MaximumGrantLifetime=TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaximumLkgVerificationAge=TimeSpan.FromMinutes(10);
    private readonly bool _enabled;
    private readonly ILocalDogfoodAuthenticationVerifier _authentication;
    private readonly IDogfoodDiagnosticAuditSink _audit;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _stateLock=new();
    private readonly HashSet<string> _consumedRequests=new(StringComparer.Ordinal);
    private readonly HashSet<string> _revokedGrants=new(StringComparer.Ordinal);
    private bool _emergencyStop;

    public DogfoodDiagnosticPolicy(bool enabled,ILocalDogfoodAuthenticationVerifier authentication,IDogfoodDiagnosticAuditSink audit,Func<DateTimeOffset>? utcNow=null)
    {
        _enabled=enabled;
        _authentication=authentication??throw new ArgumentNullException(nameof(authentication));
        _audit=audit??throw new ArgumentNullException(nameof(audit));
        _utcNow=utcNow??(()=>DateTimeOffset.UtcNow);
    }

    public bool EmergencyStopActive{get{lock(_stateLock)return _emergencyStop;}}

    public void Revoke(string grantId)
    {
        if(!string.IsNullOrWhiteSpace(grantId))lock(_stateLock)_revokedGrants.Add(grantId);
    }

    public DogfoodDiagnosticDecision Authorize(DogfoodDiagnosticRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now=_utcNow().ToUniversalTime();
        var code=Validate(request,now);
        var allowed=false;
        string? lkgVersion=null;
        if(code=="diagnostic.allowed")
        {
            lock(_stateLock)
            {
                if(!_consumedRequests.Add(request.RequestId))code="diagnostic.replay";
                else
                {
                    allowed=true;
                    if(request.Action==DogfoodDiagnosticAction.TriggerEmergencyStop)
                    {
                        _emergencyStop=true;
                        if(request.Grant is not null)_revokedGrants.Add(request.Grant.GrantId);
                        code="diagnostic.emergency-stop-active";
                    }
                    else if(request.Action==DogfoodDiagnosticAction.RequestVerifiedLkgRollback)
                    {
                        lkgVersion=request.LastKnownGood!.Version;
                        code="diagnostic.lkg-rollback-authorized";
                    }
                }
            }
        }
        bool emergencyStop;
        lock(_stateLock)emergencyStop=_emergencyStop;
        var decision=new DogfoodDiagnosticDecision(allowed,code,emergencyStop,lkgVersion);
        Record(request,decision,now);
        return decision;
    }

    private string Validate(DogfoodDiagnosticRequest request,DateTimeOffset now)
    {
        if(!_enabled)return "diagnostic.disabled";
        if(string.IsNullOrWhiteSpace(request.RequestId))return "diagnostic.replay";
        lock(_stateLock)if(_consumedRequests.Contains(request.RequestId))return "diagnostic.replay";
        if(!Enum.IsDefined(request.Action))return "diagnostic.action-unknown";
        if(!request.IsLocalTransport)return "diagnostic.remote-forbidden";
        if(!string.Equals(request.Environment,"Testnet",StringComparison.Ordinal))return "diagnostic.mainnet-forbidden";
        var grant=request.Grant;
        if(grant is null)return "diagnostic.authority-missing";
        lock(_stateLock)if(_revokedGrants.Contains(grant.GrantId))return "diagnostic.authority-revoked";
        if(!_authentication.Verify(grant))return "diagnostic.authentication-invalid";
        if(string.IsNullOrWhiteSpace(grant.GrantId)||string.IsNullOrWhiteSpace(grant.AuthenticationProof))return "diagnostic.authentication-invalid";
        if(!string.Equals(grant.PrincipalId,request.PrincipalId,StringComparison.Ordinal)||!string.Equals(grant.DeviceId,request.DeviceId,StringComparison.Ordinal))return "diagnostic.identity-mismatch";
        if(!string.Equals(grant.Environment,request.Environment,StringComparison.Ordinal))return "diagnostic.environment-mismatch";
        if(grant.IssuedAtUtc.Offset!=TimeSpan.Zero||grant.ExpiresAtUtc.Offset!=TimeSpan.Zero||grant.IssuedAtUtc>now||grant.ExpiresAtUtc<=now||grant.ExpiresAtUtc-grant.IssuedAtUtc>MaximumGrantLifetime)return "diagnostic.authority-expired";
        if(grant.Permissions is null||grant.Permissions.Count!=1)return "diagnostic.permission-invalid";
        var required=Required(request.Action);
        if(!grant.Permissions.Contains(required))return "diagnostic.permission-denied";
        lock(_stateLock)if(_emergencyStop&&request.Action!=DogfoodDiagnosticAction.RequestVerifiedLkgRollback)return "diagnostic.emergency-stop-blocked";
        if(request.Action==DogfoodDiagnosticAction.RequestVerifiedLkgRollback)return ValidateLkg(request.LastKnownGood,now);
        return "diagnostic.allowed";
    }

    private static string ValidateLkg(VerifiedLastKnownGood? lkg,DateTimeOffset now)
    {
        if(lkg is null)return "diagnostic.lkg-missing";
        if(string.IsNullOrWhiteSpace(lkg.Version)||string.IsNullOrWhiteSpace(lkg.ImmutableRoot)||lkg.ManifestSha256.Length!=64||lkg.ManifestSha256.Any(c=>!Uri.IsHexDigit(c)))return "diagnostic.lkg-invalid";
        var normalizedRoot=lkg.ImmutableRoot.Replace('/','\\');
        if(normalizedRoot.Contains("\\bin\\",StringComparison.OrdinalIgnoreCase)||normalizedRoot.Contains("\\obj\\",StringComparison.OrdinalIgnoreCase))return "diagnostic.lkg-mutable";
        if(lkg.VerifiedAtUtc.Offset!=TimeSpan.Zero||lkg.VerifiedAtUtc>now||now-lkg.VerifiedAtUtc>MaximumLkgVerificationAge)return "diagnostic.lkg-stale";
        if(!lkg.HashesVerified||!lkg.RollbackPreflightPassed||!lkg.ConfigurationCompatible)return "diagnostic.lkg-unverified";
        if(lkg.UserDataRollbackRequired)return "diagnostic.user-data-rollback-forbidden";
        return "diagnostic.allowed";
    }

    private void Record(DogfoodDiagnosticRequest request,DogfoodDiagnosticDecision decision,DateTimeOffset now)
    {
        _audit.Record(new(request.RequestId,request.Grant?.GrantId??"missing",request.PrincipalId,request.DeviceId,request.Environment,request.Action,decision.Allowed,decision.Code,now,decision.AuthorizedLkgVersion));
    }

    private static DogfoodDiagnosticPermission Required(DogfoodDiagnosticAction action)=>action switch
    {
        DogfoodDiagnosticAction.ReadHealth=>DogfoodDiagnosticPermission.ReadHealth,
        DogfoodDiagnosticAction.ReadSanitizedAudit=>DogfoodDiagnosticPermission.ReadSanitizedAudit,
        DogfoodDiagnosticAction.TriggerEmergencyStop=>DogfoodDiagnosticPermission.TriggerEmergencyStop,
        DogfoodDiagnosticAction.RequestVerifiedLkgRollback=>DogfoodDiagnosticPermission.RequestVerifiedLkgRollback,
        _=>throw new ArgumentOutOfRangeException(nameof(action))
    };
}
