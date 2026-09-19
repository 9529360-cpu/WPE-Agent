using WpeAgent.TradingAuthorization;

namespace 币安量化机器人.Services.Agent;

public sealed record ExecutionRealityIntentAuthorityV1(
    bool Bound,
    string Code,
    string? StrategyId,
    string? StrategyVersion,
    string? ArtifactHash,
    string? IntentHash,
    int? IntentSequence,
    DateTimeOffset? ArtifactCreatedAtUtc,
    DateTimeOffset? ExecutionStartedAtUtc);

public sealed partial class AgentSqliteStore
{
    public async Task<ExecutionRealityIntentAuthorityV1> GetExecutionRealityIntentAuthorityAsync(
        string correlationId,
        ExecutionIntent intent,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("Correlation id is required.", nameof(correlationId));
        ArgumentNullException.ThrowIfNull(intent);

        var item = await GetAutomaticExecutionAsync(correlationId, ct);
        if (item is null)
            return DenyIntent("automatic-artifact-missing");
        if (!item.ArtifactValid || item.Artifact is null)
            return DenyIntent("automatic-artifact-invalid");
        if (item.CanonicalArtifactBytes is null || item.CanonicalArtifactBytes.Length == 0
            || string.IsNullOrWhiteSpace(item.ArtifactHash)
            || string.IsNullOrWhiteSpace(item.IntentHash))
            return DenyIntent("automatic-artifact-canonical-evidence-missing");
        if (!item.RiskReceiptValid || item.RiskReceipt is null)
            return DenyIntent("automatic-risk-receipt-invalid");

        var artifact = item.Artifact;
        var receipt = item.RiskReceipt;
        if (!string.Equals(artifact.CorrelationId, correlationId, StringComparison.Ordinal))
            return DenyIntent("automatic-artifact-correlation-mismatch");
        if (!string.Equals(artifact.Environment, "Testnet", StringComparison.Ordinal))
            return DenyIntent("automatic-artifact-non-testnet");
        if (artifact.ContractVersion != DurableExecutionArtifactV2.Version)
            return DenyIntent("automatic-artifact-version-mismatch");

        var candidates = artifact.Intents
            .Where(x => string.Equals(x.ClientOrderId, intent.ClientOrderId, StringComparison.Ordinal))
            .ToArray();
        if (candidates.Length == 0)
            return DenyIntent("automatic-intent-missing");
        if (candidates.Length != 1)
            return DenyIntent("automatic-intent-conflict");

        var snapshot = candidates[0];
        if (!IntentMatches(snapshot, intent))
            return DenyIntent("automatic-intent-mismatch");

        var events = await GetAutomaticExecutionEventsAsync(correlationId, 100, ct);
        var executing = events.Where(x => x.ToStatus == AutomaticExecutionQueueStatus.Executing).ToArray();
        if (executing.Length == 0)
            return DenyIntent("automatic-executing-event-missing");
        if (executing.Length != 1)
            return DenyIntent("automatic-executing-event-conflict");
        if (executing[0].OccurredAtUtc < artifact.CreatedAtUtc)
            return DenyIntent("automatic-executing-event-time-invalid");
        if (!receipt.Approved
            || receipt.RevokedAtUtc is not null
            || !string.Equals(receipt.CorrelationId, artifact.CorrelationId, StringComparison.Ordinal)
            || !string.Equals(receipt.IntentHash, item.IntentHash, StringComparison.Ordinal)
            || receipt.ArtifactHash is null
            || !string.Equals(receipt.ArtifactHash, item.ArtifactHash, StringComparison.Ordinal))
            return DenyIntent("automatic-risk-receipt-mismatch");
        if (receipt.IssuedAtUtc.Offset != TimeSpan.Zero
            || receipt.ExpiresAtUtc.Offset != TimeSpan.Zero
            || receipt.IssuedAtUtc > executing[0].OccurredAtUtc
            || receipt.ExpiresAtUtc <= executing[0].OccurredAtUtc)
            return DenyIntent("automatic-risk-receipt-time-invalid");

        return new(
            true,
            "automatic-intent-bound",
            artifact.StrategyId,
            artifact.StrategyVersion,
            item.ArtifactHash,
            item.IntentHash,
            snapshot.Sequence,
            artifact.CreatedAtUtc,
            executing[0].OccurredAtUtc);
    }

    private static bool IntentMatches(DurableExecutionIntentSnapshotV1 snapshot, ExecutionIntent intent) =>
        string.Equals(snapshot.Symbol, intent.Symbol, StringComparison.Ordinal)
        && string.Equals(snapshot.Side, intent.Side.ToString(), StringComparison.Ordinal)
        && snapshot.Quantity == intent.Quantity
        && snapshot.ReduceOnly == intent.ReduceOnly
        && snapshot.StopLoss == intent.StopLoss
        && snapshot.TakeProfit == intent.TakeProfit
        && string.Equals(snapshot.ClientOrderId, intent.ClientOrderId, StringComparison.Ordinal)
        && string.Equals(snapshot.Action, intent.Action.ToString(), StringComparison.Ordinal)
        && string.Equals(snapshot.OrderType, intent.OrderType.ToString(), StringComparison.Ordinal)
        && snapshot.LimitPrice == intent.LimitPrice
        && snapshot.ExpectedPrice == intent.ExpectedPrice;

    private static ExecutionRealityIntentAuthorityV1 DenyIntent(string code) =>
        new(false, code, null, null, null, null, null, null, null);
}
