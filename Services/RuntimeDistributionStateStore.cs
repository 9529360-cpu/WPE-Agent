using WpeAgent.ProfessionalDistribution;
using WpeAgent.RuntimeContracts;

namespace WpeAgent.RuntimeServices;

/// <summary>Single host-owned, minimized read projection of professional distribution approval evidence.</summary>
public sealed class RuntimeDistributionStateStore
{
    private readonly object _gate = new();
    private RuntimeDistributionState _current = RuntimeDistributionState.DefaultDenied(DateTime.UtcNow);

    public RuntimeDistributionState Read()
    {
        lock (_gate) return _current;
    }

    public void Publish(
        IReadOnlyList<DistributionApprovalReceipt>? receipts,
        DistributionRetentionPolicy? retention,
        CurrentDistributionApprovalBinding? currentBinding,
        DateTime asOfUtc)
    {
        asOfUtc = asOfUtc.ToUniversalTime();
        var read = DistributionReadProjector.Project(receipts, retention, asOfUtc);
        var binding = DistributionVersionBindingPolicy.Evaluate(receipts, currentBinding, asOfUtc);
        var head = read.Status != DistributionReadStatus.Error && receipts is { Count: > 0 } ? receipts[^1] : null;
        var reasons = read.ReasonCodes.Concat(binding.ReasonCodes).Distinct(StringComparer.Ordinal).ToArray();
        var status = read.Status == DistributionReadStatus.Error
            ? DistributionReadStatus.Error
            : read.Status == DistributionReadStatus.Authorized
                ? DistributionReadStatus.Authorized
                : DistributionReadStatus.Denied;
        var allowed = read.Allowed && binding.Allowed;
        if (status == DistributionReadStatus.Authorized && !binding.Allowed)
            status = DistributionReadStatus.Denied;

        var value = new RuntimeDistributionV1(
            status.ToString(), allowed,
            read.ApprovalRoles.Select(role => role.ToString()).ToArray(),
            read.ValidUntilUtc?.ToUniversalTime(), read.Withdrawn,
            read.ReceiptHash, head?.PolicyHash, head?.ContentFactsHash,
            head?.ConsentScopeHash, head?.ConsentVersion, head?.SuitabilityVersion,
            read.AuditCorrelationHash, reasons, asOfUtc);
        lock (_gate) _current = new(RuntimeCollectionState.Available, value, null);
    }
}

public sealed record RuntimeDistributionState(
    RuntimeCollectionState State,
    RuntimeDistributionV1 Value,
    string? Message)
{
    public static RuntimeDistributionState DefaultDenied(DateTime asOfUtc) => new(
        RuntimeCollectionState.Available,
        new("Denied", false, [], null, false, null, null, null, null, null, null, null,
            ["LEDGER_EMPTY"], asOfUtc.ToUniversalTime()),
        null);
}
