using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WpeAgent.ModelOff;

namespace WPE.Tests;

public sealed class ModelOffAuditCoverageTests : IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,7,23,12,0,0,TimeSpan.Zero);
    private static readonly Type StoreType=typeof(ModelOffAgentOutputV1).Assembly.GetTypes().Single(x=>x.Name=="AgentSqliteStore");
    private readonly string _dir=Path.Combine(Path.GetTempPath(),"wpe-model-off-audit-"+Guid.NewGuid().ToString("N"));
    private string Db=>Path.Combine(_dir,"agent.db");
    public ModelOffAuditCoverageTests()=>Directory.CreateDirectory(_dir);
    public void Dispose(){SqliteConnection.ClearAllPools();try{Directory.Delete(_dir,true);}catch(IOException){}}

    [Fact]
    public async Task PersistsCanonicalCoverageAcrossRestartWithoutModelFields()
    {
        var output=Output("out-b","cycle-a",Now.AddMinutes(-1));var document=ModelOffCanonicalSerializerV1.Serialize(output);
        var saved=await Invoke(Store(()=>Now),"SaveModelOffCanonicalAuditAsync",output,document,CancellationToken.None);
        var rows=Rows(await Invoke(Store(()=>Now.AddHours(1)),"GetModelOffCanonicalAuditsAsync","cycle-a",CancellationToken.None));
        Assert.True(Property<bool>(saved,"Succeeded"));Assert.False(Property<bool>(saved,"Idempotent"));var row=Assert.Single(rows);
        Assert.Equal(ModelOffAgentOutputV1.Schema,Property<string>(row,"Schema"));Assert.Equal(output.TemplateVersion,Property<string>(row,"TemplateVersion"));Assert.Equal(document.Sha256,Property<string>(row,"CanonicalSha256"));
        Assert.Equal("succeeded",Property<string>(row,"Status"));Assert.Equal("strategy",Property<string>(row,"OutputKind"));Assert.Equal(Now.AddMinutes(-1),Property<DateTimeOffset>(row,"AsOfUtc"));Assert.Equal(Now,Property<DateTimeOffset>(row,"RecordedAtUtc"));Assert.Equal(document.Utf8Bytes,Property<byte[]>(row,"CanonicalBytes"));
        var sources=Property<string>(row,"SourcesJson");Assert.DoesNotContain("model",sources,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("prompt",sources,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("explanation",sources,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IdenticalWriteIsIdempotentButIdentityDriftFailsClosed()
    {
        var store=Store(()=>Now);var output=Output("same","cycle-a",Now.AddMinutes(-2));var document=ModelOffCanonicalSerializerV1.Serialize(output);
        Assert.Equal("audit.persisted",Property<string>(await Invoke(store,"SaveModelOffCanonicalAuditAsync",output,document,CancellationToken.None),"Code"));
        var repeated=await Invoke(store,"SaveModelOffCanonicalAuditAsync",output,document,CancellationToken.None);Assert.True(Property<bool>(repeated,"Succeeded"));Assert.True(Property<bool>(repeated,"Idempotent"));
        var changed=output with{Decision=output.Decision with{Action="reduce"}};var conflict=await Invoke(store,"SaveModelOffCanonicalAuditAsync",changed,ModelOffCanonicalSerializerV1.Serialize(changed),CancellationToken.None);
        Assert.False(Property<bool>(conflict,"Succeeded"));Assert.Equal("audit.identity-conflict",Property<string>(conflict,"Code"));
    }

    [Fact]
    public async Task RejectsTamperedMalformedUnknownAndMissingInputs()
    {
        var store=Store(()=>Now);var valid=Output("valid","cycle-a",Now.AddMinutes(-1));var doc=ModelOffCanonicalSerializerV1.Serialize(valid);
        await Rejects(store,valid,doc with{Utf8Bytes=Encoding.UTF8.GetBytes("{}")});await Rejects(store,valid,doc with{Sha256=new string('0',64)});
        await Rejects(store,valid with{Agent=(ModelOffAgentV1)999},doc);await Rejects(store,valid with{Status=(ModelOffOutputStatusV1)999},doc);
        await Rejects(store,valid with{TemplateVersion=" "},doc);await Rejects(store,valid with{Sources=[]},doc);
    }

    [Fact]
    public async Task ReadsInDeterministicAsOfThenIdentityOrder()
    {
        var store=Store(()=>Now);
        foreach(var output in new[]{Output("z","cycle-a",Now.AddMinutes(-1)),Output("b","cycle-a",Now.AddMinutes(-2)),Output("a","cycle-a",Now.AddMinutes(-2))})await Invoke(store,"SaveModelOffCanonicalAuditAsync",output,ModelOffCanonicalSerializerV1.Serialize(output),CancellationToken.None);
        var first=Rows(await Invoke(store,"GetModelOffCanonicalAuditsAsync","cycle-a",CancellationToken.None));var second=Rows(await Invoke(Store(()=>Now),"GetModelOffCanonicalAuditsAsync","cycle-a",CancellationToken.None));
        Assert.Equal(new[]{"a","b","z"},first.Select(x=>Property<string>(x,"OutputId")));Assert.Equal(first.Select(x=>Property<string>(x,"CanonicalSha256")),second.Select(x=>Property<string>(x,"CanonicalSha256")));
    }

    [Fact]
    public async Task MigrationCreatesStrictTableAndPreservesAdjacentSqliteTables()
    {
        _=Store(()=>Now);await using var c=new SqliteConnection($"Data Source={Db}");await c.OpenAsync();
        await using(var insert=c.CreateCommand()){insert.CommandText="INSERT INTO agent_state(key,value,updated_at) VALUES('adjacent','ok','2026-07-23T12:00:00.000Z')";Assert.Equal(1,await insert.ExecuteNonQueryAsync());}
        await using var q=c.CreateCommand();q.CommandText="PRAGMA table_info(model_off_canonical_audits)";await using var r=await q.ExecuteReaderAsync();var names=new List<string>();while(await r.ReadAsync())names.Add(r.GetString(1));
        Assert.Equal(new[]{"output_id","cycle_id","schema","template_version","canonical_sha256","status","output_kind","sources_json","as_of_utc","recorded_at_utc","canonical_bytes"},names);
    }

    [Fact]
    public void CanonicalPayloadRejectsModelNarrativeFields()
    {
        using var facts=JsonDocument.Parse("{\"model_response\":\"not canonical\"}");var output=Output("bad","cycle-a",Now.AddMinutes(-1)) with{Facts=facts.RootElement.Clone()};
        Assert.Throws<InvalidOperationException>(()=>ModelOffCanonicalSerializerV1.Serialize(output));
    }

    private object Store(Func<DateTimeOffset> clock)=>Activator.CreateInstance(StoreType,BindingFlags.Instance|BindingFlags.NonPublic,null,[Db,clock],null)!;
    private static async Task<object> Invoke(object target,string name,params object[] args){var task=(Task)StoreType.GetMethod(name)!.Invoke(target,args)!;await task;return task.GetType().GetProperty("Result")!.GetValue(task)!;}
    private static IReadOnlyList<object> Rows(object value)=>((IEnumerable)value).Cast<object>().ToArray();
    private static T Property<T>(object value,string name)=>(T)value.GetType().GetProperty(name)!.GetValue(value)!;
    private static async Task Rejects(object store,ModelOffAgentOutputV1 output,ModelOffCanonicalDocumentV1 document)=>await Assert.ThrowsAsync<InvalidOperationException>(()=>Invoke(store,"SaveModelOffCanonicalAuditAsync",output,document,CancellationToken.None));

    private static ModelOffAgentOutputV1 Output(string id,string cycle,DateTimeOffset asOf)
    {
        using var facts=JsonDocument.Parse("{\"signal\":\"hold\"}");
        return new(ModelOffAgentV1.Strategy,id,cycle,Now,Now,"wpe.strategy-input/1.0",new("strategy-rules","1.0"),[new("market",ModelOffSourceKindV1.Market,asOf,asOf.AddSeconds(1),ModelOffSourceStatusV1.Available,"sha256:"+new string('a',64))],ModelOffOutputStatusV1.Succeeded,new(ModelOffUncertaintyLevelV1.None,[],[]),facts.RootElement.Clone(),[],new("hold",true,[]),[],ModelOffFixedTemplatesV1.SummaryVersion);
    }
}
