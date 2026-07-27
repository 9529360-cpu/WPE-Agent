using WpeAgent.RuntimeContracts;
using 币安量化机器人.Services.Agent;

namespace WpeAgent.RuntimeServices;

public sealed class RuntimeTeacherStateStore
{
    public static readonly TimeSpan StaleAfter=TimeSpan.FromMinutes(5);
    private readonly AgentSqliteStore database;private readonly object gate=new();private RuntimeTeacherState current=RuntimeTeacherState.Unsupported("Teacher persistence is not connected.");
    public RuntimeTeacherStateStore(AgentSqliteStore database){this.database=database;RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();}
    public RuntimeTeacherState Read(){lock(gate)return current;}
    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var content=await database.GetTeacherRuntimeContentAsync(50,ct);
            var lessons=content.Lessons.Select(x=>new RuntimeTeacherLessonV1(x.LessonId,x.Kind.ToString(),x.TimeZoneId,x.ScheduledForUtc,x.GeneratedAtUtc,x.Language,x.TeachingLevel,x.PersonaVersion,x.Blocks.Select(b=>new RuntimeTeacherBlockV1(b.BlockId,b.Kind.ToString(),b.Heading,b.Content,b.EvidenceHashes,b.Sha256)).ToArray(),x.ContentSha256,false)).ToArray();
            var recommendations=content.Recommendations.Select(x=>new RuntimeTeacherRecommendationV1(x.RecommendationId,x.Version,x.Instrument,x.AssetClass,x.State.ToString(),x.Horizon,x.IssuedAtUtc,x.ExpiresAtUtc,x.Thesis,x.EvidenceHashes,x.ConfirmationConditions,x.InvalidationConditions,x.MaterialRisks,x.SupersedesId,false)).ToArray();
            var corrections=content.Corrections.Select(x=>new RuntimeTeacherCorrectionV1(x.CorrectionId,x.SupersededLessonId,x.ReasonCode,x.IssuedAtUtc,x.ReplacementBlocks.Select(b=>new RuntimeTeacherBlockV1(b.BlockId,b.Kind.ToString(),b.Heading,b.Content,b.EvidenceHashes,b.Sha256)).ToArray(),x.Sha256)).ToArray();
            var outcomes=content.Outcomes.Select(x=>new RuntimeTeacherOutcomeV1(x.OutcomeId,x.RecommendationId,x.RecommendationVersion,x.Instrument,x.Benchmark,x.Horizon,x.IssuedAtUtc,x.EvaluatedAtUtc,x.InstrumentReturnPct,x.BenchmarkReturnPct,x.RelativeReturnPct,x.MaximumFavorableExcursionPct,x.MaximumAdverseExcursionPct,x.InvalidatedBeforeHorizon,x.ProcessState,x.CanonicalSha256)).ToArray();
            lock(gate)current=new(RuntimeCollectionState.Available,lessons,recommendations,corrections,outcomes,DateTimeOffset.UtcNow,null);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){lock(gate)current=RuntimeTeacherState.Error($"Teacher projection failed: {ex.Message}");}
    }
}

public sealed record RuntimeTeacherState(RuntimeCollectionState State,IReadOnlyList<RuntimeTeacherLessonV1> Lessons,IReadOnlyList<RuntimeTeacherRecommendationV1> Recommendations,IReadOnlyList<RuntimeTeacherCorrectionV1> Corrections,IReadOnlyList<RuntimeTeacherOutcomeV1> Outcomes,DateTimeOffset? UpdatedAt,string? Message)
{
    public static RuntimeTeacherState Unsupported(string message)=>new(RuntimeCollectionState.Unsupported,[],[],[],[],null,message);
    public static RuntimeTeacherState Error(string message)=>new(RuntimeCollectionState.Error,[],[],[],[],DateTimeOffset.UtcNow,message);
}
