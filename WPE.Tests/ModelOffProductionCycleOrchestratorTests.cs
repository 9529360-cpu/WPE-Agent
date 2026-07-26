using Microsoft.Data.Sqlite;
using WpeAgent.ModelOff;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ModelOffProductionCycleOrchestratorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wpe-production-cycle-" + Guid.NewGuid().ToString("N"));
    public ModelOffProductionCycleOrchestratorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public async Task ValidCanonicalInputsCreateOrderedSevenAgentCycleWithoutMutation()
    {
        var inputs = Recycle(await Inputs("valid"), "valid");
        var result = await Run("valid", inputs);

        Assert.True(result.EligibleForRiskIncrease);
        Assert.Equal("model-off.production-cycle-ready", result.Code);
        Assert.Equal(Enum.GetValues<ModelOffAgentV1>(), result.Outputs.Keys.OrderBy(x => x));
        Assert.Equal(7, result.Documents.Count);
        Assert.Equal(7, result.AuditCoverage.Count);
        Assert.Equal(6, result.Handoffs.Count);
        Assert.Equal("no_mutation", result.Outputs[ModelOffAgentV1.Execution].Decision.Action);
        Assert.False(result.Outputs[ModelOffAgentV1.Execution].Facts.GetProperty("mutation_attempted").GetBoolean());
        Assert.Equal("no_resubmit", result.Outputs[ModelOffAgentV1.Recovery].Decision.Action);
        Assert.False(result.Outputs[ModelOffAgentV1.Recovery].Facts.GetProperty("resubmit_allowed").GetBoolean());
        Assert.Equal(7, await Count("production-valid.db", "model_off_canonical_audits"));
        Assert.Equal(6, await Count("production-valid.db", "model_off_canonical_handoffs"));
        var replay = await Run("valid", inputs);
        Assert.True(replay.EligibleForRiskIncrease);
        Assert.Equal("model-off.production-cycle-ready", replay.Code);
        Assert.Equal(7, await Count("production-valid.db", "model_off_canonical_audits"));
        Assert.Equal(6, await Count("production-valid.db", "model_off_canonical_handoffs"));
    }

    [Fact]
    public async Task HandoffIdentityConflictRollsBackTheWholeProductionCycle()
    {
        const string cycle = "production-atomic";
        var database = "production-" + cycle + ".db";
        _ = Store("production-" + cycle);
        await Execute(database, "INSERT INTO model_off_canonical_handoffs(handoff_id,cycle_id,from_agent,to_agent,canonical_output_id,canonical_sha256,status,recorded_at_utc,handoff_sha256,canonical_bytes) VALUES($id,$cycle,'market','research','conflict',$hash,'blocked',$at,$hash,X'01')",
            ("$id", cycle + "-production-handoff-1"), ("$cycle", cycle), ("$hash", new string('a', 64)), ("$at", Now.ToString("O")));

        var result = await Run(cycle, Recycle(await Inputs("atomic"), cycle));

        Assert.False(result.EligibleForRiskIncrease);
        Assert.Equal("audit.handoff-identity-conflict", result.Code);
        Assert.Equal(0, await Count(database, "model_off_canonical_audits"));
        Assert.Equal(1, await Count(database, "model_off_canonical_handoffs"));
    }

    [Fact]
    public async Task PersistedProductionEvidenceIsDatabaseAppendOnly()
    {
        const string cycle = "production-append-only";
        var database = "production-" + cycle + ".db";
        var result = await Run(cycle, Recycle(await Inputs("append-only"), cycle));
        Assert.True(result.EligibleForRiskIncrease);

        await Assert.ThrowsAsync<SqliteException>(() => Execute(database, "UPDATE model_off_canonical_audits SET status='blocked'"));
        await Assert.ThrowsAsync<SqliteException>(() => Execute(database, "DELETE FROM model_off_canonical_handoffs"));
        Assert.Equal(7, await Count(database, "model_off_canonical_audits"));
        Assert.Equal(6, await Count(database, "model_off_canonical_handoffs"));
    }

    [Fact]
    public async Task ReorderedInputsProduceIdenticalCanonicalOutputs()
    {
        var inputs = await Inputs("order");
        var first = await Run("order", Recycle(inputs, "order"), storeSuffix: "first");
        var second = await Run("order", Recycle(inputs, "order").Reverse().ToArray(), storeSuffix: "second");

        foreach (var role in Enum.GetValues<ModelOffAgentV1>())
            Assert.Equal(first.Documents[role].Sha256, second.Documents[role].Sha256);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unexpected-role")]
    [InlineData("wrong-cycle")]
    [InlineData("tampered")]
    [InlineData("stale")]
    [InlineData("mainnet")]
    public async Task InvalidUpstreamStateFailsClosedAndStillAudits(string defect)
    {
        var inputs = (await Inputs(defect)).ToList();
        var cycle = "production-" + defect;
        inputs = Recycle(inputs, cycle).ToList();
        if (defect == "missing") inputs.RemoveAll(x => x.Output.Agent == ModelOffAgentV1.Research);
        if (defect == "duplicate") inputs.Add(inputs.Single(x => x.Output.Agent == ModelOffAgentV1.Research));
        if (defect == "unexpected-role")
        {
            var risk = inputs.Single(x => x.Output.Agent == ModelOffAgentV1.Risk);
            var unexpected = risk.Output with { Agent = ModelOffAgentV1.Execution, OutputId = cycle + "-unexpected-execution" };
            inputs.Add(new(unexpected, ModelOffCanonicalSerializerV1.Serialize(unexpected)));
        }
        if (defect == "wrong-cycle") inputs[0] = inputs[0] with { Output = inputs[0].Output with { CycleId = "other-cycle" } };
        if (defect == "tampered") inputs[0] = inputs[0] with { Document = inputs[0].Document with { Sha256 = new string('a', 64) } };
        if (defect == "stale")
        {
            var value = inputs[0].Output;
            inputs[0] = inputs[0] with { Output = value with { GeneratedAtUtc = Now.AddMinutes(-10) } };
            inputs[0] = inputs[0] with { Document = ModelOffCanonicalSerializerV1.Serialize(inputs[0].Output) };
        }
        var result = await Run(cycle, inputs, defect == "mainnet");

        Assert.False(result.EligibleForRiskIncrease);
        Assert.Equal("model-off.production-cycle-blocked", result.Code);
        Assert.Equal(7, result.Outputs.Count);
        Assert.Equal(6, result.Handoffs.Count);
        Assert.Equal("record_blocked_audit", result.Outputs[ModelOffAgentV1.Audit].Decision.Action);
        Assert.Equal("no_mutation", result.Outputs[ModelOffAgentV1.Execution].Decision.Action);
        Assert.False(result.Outputs[ModelOffAgentV1.Execution].Facts.GetProperty("mutation_attempted").GetBoolean());
        Assert.False(result.Outputs[ModelOffAgentV1.Recovery].Facts.GetProperty("resubmit_allowed").GetBoolean());
    }

    [Fact]
    public async Task OptionalModelAttachmentCannotAffectProductionInputOrOutput()
    {
        var inputs = await Inputs("model-absence");
        var source = File.ReadAllText(Path.Combine(ProjectRoot(), "Services", "Agent", "ModelOffProductionCycleOrchestratorV1.cs"));
        Assert.DoesNotContain("HttpClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IAssistantProvider", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteAsync", source, StringComparison.Ordinal);
        Assert.True((await Run("production-model-absence", Recycle(inputs, "production-model-absence"))).EligibleForRiskIncrease);
    }

    private async Task<IReadOnlyList<ModelOffProductionInputV1>> Inputs(string name)
    {
        var fixtureCycle = "fixture-" + name;
        var request = ModelOffAutonomousLoopTests.Request(fixtureCycle) with { EvaluationTimeUtc = Now };
        var fixture = await AutoTradingAgent.RunModelOffFixtureCycleAsync(request, Store("fixture-" + name), null, CancellationToken.None);
        return new[] { ModelOffAgentV1.Market, ModelOffAgentV1.Research, ModelOffAgentV1.Strategy, ModelOffAgentV1.Risk }
            .Select(role => new ModelOffProductionInputV1(fixture.Outputs[role], fixture.Documents[role])).ToArray();
    }

    private static IReadOnlyList<ModelOffProductionInputV1> Recycle(IEnumerable<ModelOffProductionInputV1> values, string cycle)
    {
        ModelOffCanonicalDocumentV1? previous = null;
        var result = new List<ModelOffProductionInputV1>();
        foreach (var value in values.OrderBy(x => x.Output.Agent))
        {
            var output = value.Output with
            {
                OutputId = $"{cycle}-{value.Output.Agent.ToString().ToLowerInvariant()}",
                CycleId = cycle,
                GeneratedAtUtc = Now,
                EvaluationTimeUtc = Now,
                Sources = previous is null ? value.Output.Sources.Select(x => x with { AsOfUtc = Now, ReceivedAtUtc = Now }).ToArray() :
                    [new($"{cycle}-upstream", ModelOffSourceKindV1.Audit, Now, Now, ModelOffSourceStatusV1.Available, "sha256:" + previous.Sha256)]
            };
            var document = ModelOffCanonicalSerializerV1.Serialize(output);
            result.Add(new(output, document)); previous = document;
        }
        return result;
    }

    private Task<ModelOffProductionCycleResultV1> Run(string cycle, IReadOnlyList<ModelOffProductionInputV1> inputs, bool mainnet = false, string? storeSuffix = null) =>
        new ModelOffProductionCycleOrchestratorV1(Store("production-" + cycle + (storeSuffix is null ? "" : "-" + storeSuffix))).RunAsync(
            new(cycle, Now, inputs, mainnet), CancellationToken.None);
    private AgentSqliteStore Store(string name) => new(Path.Combine(_dir, name + ".db"), () => Now);
    private async Task<long> Count(string database,string table)
    {
        await using var connection=new SqliteConnection("Data Source="+Path.Combine(_dir,database));await connection.OpenAsync();
        await using var command=connection.CreateCommand();command.CommandText="SELECT COUNT(*) FROM "+table;return (long)(await command.ExecuteScalarAsync())!;
    }
    private async Task Execute(string database,string sql,params (string Name,object Value)[] parameters)
    {
        await using var connection=new SqliteConnection("Data Source="+Path.Combine(_dir,database));await connection.OpenAsync();
        await using var command=connection.CreateCommand();command.CommandText=sql;foreach(var parameter in parameters)command.Parameters.AddWithValue(parameter.Name,parameter.Value);await command.ExecuteNonQueryAsync();
    }
    private static string ProjectRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}
