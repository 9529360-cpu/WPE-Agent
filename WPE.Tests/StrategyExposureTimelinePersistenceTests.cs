using Microsoft.Data.Sqlite;
using 币安量化机器人.Core.Strategy;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class StrategyExposureTimelinePersistenceTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-timeline-artifact-"+Guid.NewGuid().ToString("N"));
    private string Database=>Path.Combine(_directory,"agent.db");

    [Fact]
    public async Task CanonicalTimelinePersistenceIsRestartSafeIdempotentAndAppendOnly()
    {
        var artifact=Artifact();
        var store=new AgentSqliteStore(Database);

        Assert.True(await store.SaveStrategyExposureTimelineAsync(artifact,default));
        Assert.False(await new AgentSqliteStore(Database).SaveStrategyExposureTimelineAsync(artifact,default));

        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.SaveStrategyExposureTimelineAsync(
                artifact with{CanonicalBytes=[..artifact.CanonicalBytes,0]},
                default));

        await using var connection=new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        foreach(var sql in new[]
        {
            "UPDATE strategy_exposure_timeline_artifacts SET decision_count=1",
            "DELETE FROM strategy_exposure_timeline_artifacts"
        })
        {
            await using var command=connection.CreateCommand();
            command.CommandText=sql;
            await Assert.ThrowsAsync<SqliteException>(()=>command.ExecuteNonQueryAsync());
        }

        await using var count=connection.CreateCommand();
        count.CommandText="SELECT COUNT(*) FROM strategy_exposure_timeline_artifacts WHERE canonical_sha256=$hash";
        count.Parameters.AddWithValue("$hash",artifact.CanonicalSha256);
        Assert.Equal(1,Convert.ToInt32(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public void ProductionResearchPersistsTimelineBeforeValidationResult()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
        var source=File.ReadAllText(Path.Combine(root,"Services","Agent","StrategyResearchAgent.cs"));
        var create=source.IndexOf("var timelineArtifact=_engine.TimelineArtifact",StringComparison.Ordinal);
        var verify=source.IndexOf("validation.TimelineSha256",create,StringComparison.Ordinal);
        var saveTimeline=source.IndexOf("SaveStrategyExposureTimelineAsync",verify,StringComparison.Ordinal);
        var saveValidation=source.IndexOf("SaveStrategyValidationAsync",saveTimeline,StringComparison.Ordinal);

        Assert.True(create>=0&&verify>create);
        Assert.True(saveTimeline>verify);
        Assert.True(saveValidation>saveTimeline);
    }

    private static StrategyExposureTimelineArtifactV1 Artifact()
    {
        var registry=new DeterministicStrategyRegistry();
        var family=StrategyFamily.TrendBreakout;
        var profile=new StrategyProfile
        {
            Id="BTCUSDT-timeline-persistence",
            Version=registry.BindProfileVersion(family,"timeline-v1"),
            Symbol="BTCUSDT",
            Family=family,
            Parameters=LocalStrategyParameters.For(family,0)
        };
        var candles=Enumerable.Range(0,220).Select(i=>
        {
            var open=100m+i*.2m+(decimal)Math.Sin(i/7d);
            var close=open+(i%9<5?.35m:-.25m);
            return new CandleEvidence(
                new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc).AddHours(i),
                open,
                Math.Max(open,close)+.4m,
                Math.Min(open,close)-.4m,
                close,
                1000m+i,
                100000m+i,
                100,
                500m);
        }).ToArray();
        return StrategyExposureTimelineV1.CreateArtifact(
            registry.Resolve(family).BuildResearchTimeline(profile,candles,[]))
            ??throw new InvalidOperationException("Test timeline artifact was unavailable.");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if(Directory.Exists(_directory))Directory.Delete(_directory,true);
    }
}
