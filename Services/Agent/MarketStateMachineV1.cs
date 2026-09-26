using System.Text.Json;
using System.Text.Json.Serialization;

namespace 币安量化机器人.Services.Agent;

public enum MarketStateLifecycleV1
{
    Unavailable,
    Observing,
    Balance,
    Compression,
    Impulse,
    Pullback,
    ReversalAttempt,
    Developing,
    Triggered,
    Confirmed
}

public enum MarketStateTransitionKindV1
{
    Initial,
    Stable,
    StructureUnavailable,
    StructureRecovered,
    BiasReversal,
    SetupInvalidated,
    ScenarioFlip,
    ScenarioChanged,
    ConfirmationGained,
    ConfirmationLost,
    PhaseChanged,
    EventChanged,
    LifecycleChanged
}

public sealed record MarketStateTransitionV1(
    long Sequence,
    DateTime ObservedAtUtc,
    MarketStateTransitionKindV1 Kind,
    MarketStructureBias FromBias,
    MarketStructureBias ToBias,
    MarketStructurePhase FromPhase,
    MarketStructurePhase ToPhase,
    MarketStructureScenario FromScenario,
    MarketStructureScenario ToScenario,
    MarketStructureEvent FromEvent,
    MarketStructureEvent ToEvent,
    string Summary);

public sealed record MarketStateSnapshotV1(
    string Schema,
    string Symbol,
    DateTime ObservedAtUtc,
    bool Available,
    decimal LastPrice,
    decimal StructuralSupport,
    decimal StructuralResistance,
    MarketStructureBias Bias,
    MarketStructurePhase Phase,
    MarketStructureScenario Scenario,
    MarketStructureEvent Event,
    MarketStateLifecycleV1 Lifecycle,
    bool TriggerPresent,
    bool ConfirmationPresent,
    long ObservationCount,
    int BiasStreak,
    int PhaseStreak,
    int ScenarioStreak,
    int EventStreak,
    int ConfirmationStreak,
    DateTime BiasEstablishedAtUtc,
    DateTime ScenarioEstablishedAtUtc,
    DateTime LastTransitionAtUtc,
    DateTime? LastConfirmationAtUtc,
    MarketStateTransitionKindV1 TransitionKind,
    MarketStateTransitionV1[] RecentTransitions)
{
    public const string CurrentSchema = "wpe.market-state/1.0";
}

public static class MarketStateMachineV1
{
    public static MarketStateSnapshotV1 Advance(
        string symbol,
        DateTime observedAtUtc,
        decimal lastPrice,
        MarketStructureRead structure,
        MarketStateSnapshotV1? previous)
    {
        ArgumentNullException.ThrowIfNull(structure);
        symbol = NormalizeSymbol(symbol);
        observedAtUtc = AsUtc(observedAtUtc);

        if (previous is not null &&
            string.Equals(previous.Symbol, symbol, StringComparison.Ordinal) &&
            observedAtUtc <= previous.ObservedAtUtc)
            return previous;

        var lifecycle = ResolveLifecycle(structure);
        var transitionKind = ResolveTransition(previous, structure, lifecycle);
        var observationCount = previous is null ? 1 : previous.ObservationCount + 1;

        var biasStreak = Same(previous?.Bias, structure.HigherTimeframeBias) ? previous!.BiasStreak + 1 : 1;
        var phaseStreak = Same(previous?.Phase, structure.Phase) ? previous!.PhaseStreak + 1 : 1;
        var scenarioStreak = Same(previous?.Scenario, structure.Scenario) ? previous!.ScenarioStreak + 1 : 1;
        var eventStreak = Same(previous?.Event, structure.FifteenMinute.Event) ? previous!.EventStreak + 1 : 1;
        var confirmationStreak = structure.ConfirmationPresent
            ? previous is not null && previous.ConfirmationPresent ? previous.ConfirmationStreak + 1 : 1
            : 0;

        var biasEstablishedAt = previous is not null && previous.Bias == structure.HigherTimeframeBias
            ? previous.BiasEstablishedAtUtc
            : observedAtUtc;
        var scenarioEstablishedAt = previous is not null && previous.Scenario == structure.Scenario
            ? previous.ScenarioEstablishedAtUtc
            : observedAtUtc;
        var lastTransitionAt = transitionKind == MarketStateTransitionKindV1.Stable && previous is not null
            ? previous.LastTransitionAtUtc
            : observedAtUtc;
        var lastConfirmationAt = structure.ConfirmationPresent
            ? observedAtUtc
            : previous?.LastConfirmationAtUtc;

        var transitions = previous?.RecentTransitions ?? Array.Empty<MarketStateTransitionV1>();
        if (transitionKind != MarketStateTransitionKindV1.Stable)
        {
            var transition = BuildTransition(observationCount, observedAtUtc, transitionKind, previous, structure, lifecycle);
            transitions = transitions.Append(transition).TakeLast(12).ToArray();
        }

        return new(
            MarketStateSnapshotV1.CurrentSchema,
            symbol,
            observedAtUtc,
            structure.Available,
            lastPrice,
            structure.StructuralSupport,
            structure.StructuralResistance,
            structure.HigherTimeframeBias,
            structure.Phase,
            structure.Scenario,
            structure.FifteenMinute.Event,
            lifecycle,
            structure.TriggerPresent,
            structure.ConfirmationPresent,
            observationCount,
            biasStreak,
            phaseStreak,
            scenarioStreak,
            eventStreak,
            confirmationStreak,
            biasEstablishedAt,
            scenarioEstablishedAt,
            lastTransitionAt,
            lastConfirmationAt,
            transitionKind,
            transitions);
    }

    private static MarketStateLifecycleV1 ResolveLifecycle(MarketStructureRead structure)
    {
        if (!structure.Available) return MarketStateLifecycleV1.Unavailable;
        if (structure.Scenario != MarketStructureScenario.None && structure.TriggerPresent && structure.ConfirmationPresent)
            return MarketStateLifecycleV1.Confirmed;
        if (structure.Scenario != MarketStructureScenario.None && structure.TriggerPresent)
            return MarketStateLifecycleV1.Triggered;
        if (structure.Scenario != MarketStructureScenario.None)
            return MarketStateLifecycleV1.Developing;

        return structure.Phase switch
        {
            MarketStructurePhase.Compression => MarketStateLifecycleV1.Compression,
            MarketStructurePhase.Balance => MarketStateLifecycleV1.Balance,
            MarketStructurePhase.BullishImpulse or MarketStructurePhase.BearishImpulse => MarketStateLifecycleV1.Impulse,
            MarketStructurePhase.BullishPullback or MarketStructurePhase.BearishPullback => MarketStateLifecycleV1.Pullback,
            MarketStructurePhase.BullishReversalAttempt or MarketStructurePhase.BearishReversalAttempt => MarketStateLifecycleV1.ReversalAttempt,
            _ => MarketStateLifecycleV1.Observing
        };
    }

    private static MarketStateTransitionKindV1 ResolveTransition(
        MarketStateSnapshotV1? previous,
        MarketStructureRead current,
        MarketStateLifecycleV1 lifecycle)
    {
        if (previous is null) return MarketStateTransitionKindV1.Initial;
        if (previous.Available && !current.Available) return MarketStateTransitionKindV1.StructureUnavailable;
        if (!previous.Available && current.Available) return MarketStateTransitionKindV1.StructureRecovered;

        if (OppositeBias(previous.Bias, current.HigherTimeframeBias))
            return MarketStateTransitionKindV1.BiasReversal;

        if (previous.ConfirmationPresent &&
            (!current.ConfirmationPresent ||
             current.Scenario == MarketStructureScenario.None ||
             OppositeScenario(previous.Scenario, current.Scenario)))
            return MarketStateTransitionKindV1.SetupInvalidated;

        if (OppositeScenario(previous.Scenario, current.Scenario))
            return MarketStateTransitionKindV1.ScenarioFlip;
        if (previous.Scenario != current.Scenario)
            return MarketStateTransitionKindV1.ScenarioChanged;
        if (!previous.ConfirmationPresent && current.ConfirmationPresent)
            return MarketStateTransitionKindV1.ConfirmationGained;
        if (previous.ConfirmationPresent && !current.ConfirmationPresent)
            return MarketStateTransitionKindV1.ConfirmationLost;
        if (previous.Phase != current.Phase)
            return MarketStateTransitionKindV1.PhaseChanged;
        if (previous.Event != current.FifteenMinute.Event)
            return MarketStateTransitionKindV1.EventChanged;
        if (previous.Lifecycle != lifecycle)
            return MarketStateTransitionKindV1.LifecycleChanged;
        return MarketStateTransitionKindV1.Stable;
    }

    private static MarketStateTransitionV1 BuildTransition(
        long sequence,
        DateTime observedAtUtc,
        MarketStateTransitionKindV1 kind,
        MarketStateSnapshotV1? previous,
        MarketStructureRead current,
        MarketStateLifecycleV1 lifecycle)
    {
        var fromBias = previous?.Bias ?? MarketStructureBias.Unknown;
        var fromPhase = previous?.Phase ?? MarketStructurePhase.Unknown;
        var fromScenario = previous?.Scenario ?? MarketStructureScenario.None;
        var fromEvent = previous?.Event ?? MarketStructureEvent.None;
        var summary =
            $"{kind}: bias {fromBias}->{current.HigherTimeframeBias}; " +
            $"phase {fromPhase}->{current.Phase}; scenario {fromScenario}->{current.Scenario}; " +
            $"event {fromEvent}->{current.FifteenMinute.Event}; lifecycle {(previous?.Lifecycle.ToString() ?? "None")}->{lifecycle}";

        return new(
            sequence,
            observedAtUtc,
            kind,
            fromBias,
            current.HigherTimeframeBias,
            fromPhase,
            current.Phase,
            fromScenario,
            current.Scenario,
            fromEvent,
            current.FifteenMinute.Event,
            summary);
    }

    private static bool Same<T>(T? previous, T current) where T : struct, Enum =>
        previous.HasValue && EqualityComparer<T>.Default.Equals(previous.Value, current);

    private static bool OppositeBias(MarketStructureBias before, MarketStructureBias after) =>
        before == MarketStructureBias.Bullish && after == MarketStructureBias.Bearish ||
        before == MarketStructureBias.Bearish && after == MarketStructureBias.Bullish;

    private static bool OppositeScenario(MarketStructureScenario before, MarketStructureScenario after) =>
        IsLong(before) && IsShort(after) || IsShort(before) && IsLong(after);

    private static bool IsLong(MarketStructureScenario value) => value is
        MarketStructureScenario.TrendPullbackLong or
        MarketStructureScenario.RangeReversionLong or
        MarketStructureScenario.BreakoutRetestLong;

    private static bool IsShort(MarketStructureScenario value) => value is
        MarketStructureScenario.TrendPullbackShort or
        MarketStructureScenario.RangeReversionShort or
        MarketStructureScenario.BreakoutRetestShort;

    private static string NormalizeSymbol(string symbol)
    {
        var value = (symbol ?? string.Empty).Trim().ToUpperInvariant();
        if (value.Length is 0 or > 32 || value.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_'))
            throw new ArgumentException("Market-state symbol is invalid.", nameof(symbol));
        return value;
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}

public sealed class DurableMarketStateStoreV1
{
    private const string AggregateKey = "market.state.v1:last";
    private readonly AgentSqliteStore _store;
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();

    public DurableMarketStateStoreV1(AgentSqliteStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<IReadOnlyDictionary<string, MarketStateSnapshotV1>> ObserveAsync(
        IReadOnlyDictionary<string, MarketEvidence> markets,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(markets);
        var result = new Dictionary<string, MarketStateSnapshotV1>(StringComparer.OrdinalIgnoreCase);

        foreach (var market in markets.Values.Where(x => x is not null).OrderBy(x => x.Symbol, StringComparer.Ordinal))
        {
            var structure = MarketStructureIntelligence.Analyze(market);
            var previous = await LoadAsync(market.Symbol, ct);
            var next = MarketStateMachineV1.Advance(market.Symbol, market.CollectedAt, market.Price, structure, previous);
            if (previous is null || next.ObservationCount != previous.ObservationCount || next.ObservedAtUtc != previous.ObservedAtUtc)
                await SaveAsync(next, ct);
            result[next.Symbol] = next;
        }

        await _store.SetStateAsync(AggregateKey, JsonSerializer.Serialize(result.Values.OrderBy(x => x.Symbol).ToArray(), Json), ct);
        return result;
    }

    public async Task<MarketStateSnapshotV1?> LoadAsync(string symbol, CancellationToken ct)
    {
        var key = StateKey(symbol);
        var raw = await _store.GetStateAsync(key, ct);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var value = JsonSerializer.Deserialize<MarketStateSnapshotV1>(raw, Json);
            if (value is null ||
                value.Schema != MarketStateSnapshotV1.CurrentSchema ||
                !string.Equals(value.Symbol, NormalizeSymbol(symbol), StringComparison.Ordinal) ||
                value.ObservationCount <= 0)
                return null;
            return value;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task SaveAsync(MarketStateSnapshotV1 state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Schema != MarketStateSnapshotV1.CurrentSchema)
            throw new InvalidOperationException("Unsupported market-state schema.");
        return _store.SetStateAsync(StateKey(state.Symbol), JsonSerializer.Serialize(state, Json), ct);
    }

    public static string StateKey(string symbol) => $"market.state.v1:{NormalizeSymbol(symbol)}";

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static string NormalizeSymbol(string symbol)
    {
        var value = (symbol ?? string.Empty).Trim().ToUpperInvariant();
        if (value.Length is 0 or > 32 || value.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_'))
            throw new ArgumentException("Market-state symbol is invalid.", nameof(symbol));
        return value;
    }
}

public static class MarketStateEvidenceOverlayV1
{
    public static EvidencePack Attach(
        EvidencePack source,
        IReadOnlyDictionary<string, MarketStateSnapshotV1> marketStates)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(marketStates);
        return new EvidencePack
        {
            CollectedAt = source.CollectedAt,
            Account = source.Account,
            Positions = source.Positions,
            Markets = source.Markets,
            News = source.News,
            Fundamentals = source.Fundamentals,
            SourceRuns = source.SourceRuns,
            MarketStates = marketStates,
            MissingSources = source.MissingSources,
            Completeness = source.Completeness
        };
    }
}
