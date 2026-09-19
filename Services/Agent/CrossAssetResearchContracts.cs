using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace WpeAgent.CrossAssetResearch;

public enum ResearchAssetClass { Crypto, Equity }
public enum CrossAssetResearchStatus { Available, Unsupported }
public enum CrossAssetStrategyLifecycle { Draft }
public enum CorporateActionKind { Split, CashDividend, StockDividend, SymbolChange, Delisting }
public enum MultipleTestingCorrectionMethod { Bonferroni, BenjaminiHochberg, Conservative }

public sealed record ResearchDataAuthorization(
    string ProviderId,
    string DatasetId,
    string LicenseReference,
    bool HistoricalUseAllowed,
    DateTimeOffset ValidUntilUtc);

public sealed record TradingSessionDefinition(
    string TimeZoneId,
    TimeOnly OpensAt,
    TimeOnly ClosesAt,
    IReadOnlySet<DayOfWeek> TradingDays,
    bool IsContinuous = false);

public sealed record ResearchCostModel(
    decimal CommissionRate,
    decimal SlippageRate,
    decimal FixedCostPerTrade = 0,
    decimal BorrowRatePerDay = 0)
{
    public decimal RoundTripVariableRate => 2 * (CommissionRate + SlippageRate);
}

public static class TradingRealityCostAuthorityV1
{
    public const string Schema = "wpe.trading-reality-cost/1.0";
    public static readonly ResearchCostModel Default = new(.0004m, .0003m);
    public static readonly string CanonicalSha256 = CreateHash();
    public static string Identity => Schema + ":" + CanonicalSha256;

    private static string CreateHash()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("borrow_rate_per_day", Default.BorrowRatePerDay);
            writer.WriteNumber("commission_rate", Default.CommissionRate);
            writer.WriteNumber("fixed_cost_per_trade", Default.FixedCostPerTrade);
            writer.WriteString("schema", Schema);
            writer.WriteNumber("slippage_rate", Default.SlippageRate);
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }
}

public sealed record CorporateActionEvent(
    CorporateActionKind Kind,
    DateTimeOffset EffectiveAtUtc,
    decimal Factor = 1,
    decimal CashAmount = 0,
    string Reference = "");

public sealed record CrossAssetResearchObservation(
    DateTimeOffset TimestampUtc,
    decimal Close,
    double TargetExposure,
    bool IsAdjustedForCorporateActions = false,
    DateTimeOffset? DataAvailableAtUtc = null,
    DateTimeOffset? SignalGeneratedAtUtc = null);

public sealed record ResearchEvidenceArtifact(
    string InputDatasetHash,
    string ParameterHash,
    string CodeVersion,
    string StrategyVersion,
    int Seed,
    DateTimeOffset OutOfSampleStartsAtUtc,
    DateTimeOffset OutOfSampleEndsAtUtc,
    ResearchCostModel CostModel,
    string CorporateActionAdjustmentVersion,
    string SessionVersion,
    DateTimeOffset GeneratedAtUtc,
    string ArtifactHash);

public sealed record CrossMarketVenueClock(
    string VenueId,
    string SessionVersion,
    string TimeZoneId);

public sealed record CrossMarketAlignmentPoint(
    DateTimeOffset AnchorUtc,
    IReadOnlyDictionary<string, DateTimeOffset> VenueObservationTimesUtc);

public sealed record CrossMarketAlignmentPolicy(
    TimeSpan SynchronizationWindow,
    TimeSpan MaximumTimestampDeviation,
    bool AllowForwardFill = false);

public sealed record MultipleHypothesisEvidence(
    int TrialCount,
    string SearchSpaceHash,
    string SelectionRule,
    string HoldoutDatasetHash,
    bool HoldoutUntouched,
    bool HoldoutUsedForSelection,
    int HoldoutEvaluationCount,
    MultipleTestingCorrectionMethod CorrectionMethod,
    double NominalAlpha,
    IReadOnlyList<double> TrialPValues,
    int SelectedTrialIndex);

public sealed record CrossAssetValidationPolicy(
    double TrainingFraction = .65,
    int MinimumObservations = 120,
    int MinimumOutOfSampleObservations = 30,
    int WalkForwardFolds = 4,
    int MonteCarloRuns = 250,
    double MaximumDrawdown = .30,
    double MaximumMonteCarloLossProbability = .50,
    bool RequirePositiveOutOfSampleReturn = true,
    TimeSpan? MaximumDataStaleness = null,
    int MaximumParameterTrials = 20,
    int MinimumObservationsPerParameterTrial = 30,
    int OosPurgeObservations = 5,
    int OosEmbargoObservations = 5);

public sealed record CrossAssetResearchRequest(
    string ResearchId,
    string StrategyId,
    string StrategyVersion,
    string InstrumentId,
    ResearchAssetClass AssetClass,
    IReadOnlyList<CrossAssetResearchObservation> Observations,
    TradingSessionDefinition? Session,
    ResearchCostModel? Costs,
    IReadOnlyList<CorporateActionEvent> CorporateActions,
    ResearchDataAuthorization? DataAuthorization,
    CrossAssetValidationPolicy Policy,
    DateTimeOffset? DataAsOfUtc = null,
    bool CorporateActionCoverageConfirmed = false,
    bool HistoricalUniverseMembershipConfirmed = false,
    string InstrumentCurrency = "",
    string CostCurrency = "",
    int ParameterTrials = 1,
    string CodeVersion = "",
    string CorporateActionAdjustmentVersion = "",
    string SessionVersion = "",
    ResearchEvidenceArtifact? EvidenceArtifact = null,
    bool RequiresCrossMarketAlignment = false,
    IReadOnlyList<CrossMarketVenueClock>? VenueClocks = null,
    IReadOnlyList<CrossMarketAlignmentPoint>? AlignmentPoints = null,
    CrossMarketAlignmentPolicy? AlignmentPolicy = null,
    MultipleHypothesisEvidence? HypothesisEvidence = null);

public sealed record CrossAssetBacktestMetrics(
    int Observations,
    int Trades,
    double TotalReturn,
    double OutOfSampleReturn,
    double MaximumDrawdown,
    double Sharpe,
    double WalkForwardScore,
    double MonteCarloLossProbability);

public sealed record CrossAssetResearchResult(
    string ResearchId,
    string StrategyId,
    CrossAssetResearchStatus Status,
    CrossAssetStrategyLifecycle Lifecycle,
    bool Passed,
    CrossAssetBacktestMetrics? Metrics,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<string> InputReferences,
    DateTimeOffset EvaluatedAtUtc);
