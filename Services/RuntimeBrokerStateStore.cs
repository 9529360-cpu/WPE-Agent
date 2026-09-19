using WpeAgent.RuntimeContracts;
using 币安量化机器人.Services.Brokers;

namespace WpeAgent.RuntimeServices;

/// <summary>
/// Host-owned, read-only projection of the latest already-verified equity broker capability.
/// Publishing a capability never probes a provider and never grants mutation authority.
/// </summary>
public sealed class RuntimeBrokerStateStore
{
    public static readonly TimeSpan StaleAfter=TimeSpan.FromMinutes(5);
    private const string UnsupportedMessage="No paper or sandbox equity broker is connected.";
    private readonly object _gate=new();
    private readonly Func<DateTimeOffset> _utcNow;
    private RuntimeBrokerState _current=RuntimeBrokerState.Unsupported(UnsupportedMessage);

    public RuntimeBrokerStateStore(Func<DateTimeOffset>? utcNow=null)=>_utcNow=utcNow??(()=>DateTimeOffset.UtcNow);

    public RuntimeBrokerState Read()=>Read(_utcNow());

    public RuntimeBrokerState Read(DateTimeOffset asOfUtc)
    {
        var asOf=asOfUtc.ToUniversalTime();
        lock(_gate)
        {
            if(_current.State!=RuntimeCollectionState.Available)return _current;
            if(_current.UpdatedAt is null||_current.UpdatedAt.Value>asOf)
                return RuntimeBrokerState.Error("Broker capability timestamp is invalid.",asOf);
            if(asOf-_current.UpdatedAt.Value>StaleAfter)
                return new(RuntimeCollectionState.Stale,null,_current.UpdatedAt,"Broker capability projection is stale.");
            return _current;
        }
    }

    public void Publish(string providerId,BrokerEnvironment environment,BrokerCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        var now=_utcNow().ToUniversalTime();
        var checkedAt=capability.CheckedAtUtc.ToUniversalTime();
        providerId=(providerId??string.Empty).Trim();

        if(!ValidToken(providerId,80)||!Enum.IsDefined(environment)||checkedAt>now)
        {
            lock(_gate)_current=RuntimeBrokerState.Error("Broker capability metadata is invalid.",now);
            return;
        }

        var state=capability.Status switch
        {
            BrokerCapabilityStatus.Available=>RuntimeCollectionState.Available,
            BrokerCapabilityStatus.Stale=>RuntimeCollectionState.Stale,
            BrokerCapabilityStatus.Error=>RuntimeCollectionState.Error,
            _=>RuntimeCollectionState.Unsupported
        };

        if(state!=RuntimeCollectionState.Available)
        {
            var message=state switch
            {
                RuntimeCollectionState.Stale=>"Broker capability projection is stale.",
                RuntimeCollectionState.Error=>"Broker capability could not be verified.",
                _=>UnsupportedMessage
            };
            lock(_gate)_current=new(state,null,checkedAt,message);
            return;
        }

        if(!capability.CanReadAccounts&&!capability.CanReadPositions&&!capability.CanReadOrders||
           (capability.CanSubmitOrders||capability.CanCancelOrders)&&!capability.CanReadOrders)
        {
            lock(_gate)_current=RuntimeBrokerState.Error("Broker capability flags are inconsistent.",checkedAt);
            return;
        }

        var reason=string.IsNullOrWhiteSpace(capability.ReasonCode)?"BROKER_CAPABILITY_AVAILABLE":capability.ReasonCode.Trim();
        if(!ValidCode(reason,120))
        {
            lock(_gate)_current=RuntimeBrokerState.Error("Broker capability reason code is invalid.",checkedAt);
            return;
        }

        var value=new RuntimeBrokerCapabilityV1(
            providerId,environment.ToString(),
            capability.CanReadAccounts,capability.CanReadPositions,capability.CanReadOrders,
            capability.CanSubmitOrders,capability.CanCancelOrders,checkedAt,reason);
        lock(_gate)_current=new(RuntimeCollectionState.Available,value,checkedAt,null);
    }

    public void MarkUnsupported(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        lock(_gate)_current=RuntimeBrokerState.Unsupported(message);
    }

    public void PublishError(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        lock(_gate)_current=RuntimeBrokerState.Error("Broker capability could not be verified.",_utcNow().ToUniversalTime());
    }

    private static bool ValidToken(string value,int max)=>
        value.Length is >0&&value.Length<=max&&
        value.All(ch=>char.IsAsciiLetterOrDigit(ch)||ch is '.' or '_' or '-');

    private static bool ValidCode(string value,int max)=>
        value.Length is >0&&value.Length<=max&&
        value.All(ch=>ch is >= 'A' and <= 'Z'||ch is >= '0' and <= '9'||ch is '.' or '_' or '-');
}

public sealed record RuntimeBrokerState(
    RuntimeCollectionState State,
    RuntimeBrokerCapabilityV1? Value,
    DateTimeOffset? UpdatedAt,
    string? Message)
{
    public static RuntimeBrokerState Unsupported(string message)=>new(RuntimeCollectionState.Unsupported,null,null,message);
    public static RuntimeBrokerState Error(string message,DateTimeOffset? at=null)=>new(RuntimeCollectionState.Error,null,at??DateTimeOffset.UtcNow,message);
}
