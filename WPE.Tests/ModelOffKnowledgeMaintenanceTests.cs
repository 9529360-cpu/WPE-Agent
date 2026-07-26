using System.Text;
using Microsoft.Data.Sqlite;
using WpeAgent.FinancialEvidence;
using WpeAgent.Knowledge;

namespace WPE.Tests;

public sealed class ModelOffKnowledgeMaintenanceTests : IDisposable
{
    private static readonly DateTimeOffset ObservedAt=new(2026,7,22,1,2,3,TimeSpan.Zero);
    private static readonly DateTimeOffset AsOf=ObservedAt.AddMinutes(1);
    private readonly string _dir=Path.Combine(Path.GetTempPath(),"wpe-knowledge-"+Guid.NewGuid().ToString("N"));
    private string Db=>Path.Combine(_dir,"knowledge.sqlite");
    public ModelOffKnowledgeMaintenanceTests()=>Directory.CreateDirectory(_dir);
    public void Dispose(){SqliteConnection.ClearAllPools();try{Directory.Delete(_dir,true);}catch(IOException){}}

    [Fact]
    public async Task AuthorizedCanonicalFactPersistsAcrossRestartWithVersionSourceAndTime()
    {
        var record=Record("fact-1","1",42);var request=Request();var saved=await Store().IngestAsync(Corpus(record),request);
        var result=await Store().QueryAsync(request);Assert.True(saved.Accepted);Assert.Equal(KnowledgeQueryOutcomeV1.Current,result.Outcome);Assert.Equal(record.RecordHash,result.Record!.RecordHash);Assert.Equal("1",result.Record.RecordVersion);Assert.Equal("local-fixture",result.Record.Draft.SourceProvider);Assert.Equal(record.ObservedAt,result.Record.ObservedAt);
    }

    [Fact]
    public async Task CorrectionAndRetractionFormAppendOnlyChainAndUnknownAbstains()
    {
        var store=Store();var first=Record("fact-1","1",42);Assert.True((await store.IngestAsync(Corpus(first),Request())).Accepted);
        var correction=Record("fact-2","2",43,first.ExactRef());var correctionResult=await store.IngestAsync(Corpus(correction),Request());Assert.True(correctionResult.Accepted,correctionResult.ReasonCode);
        var corrected=await store.QueryAsync(Request());Assert.Equal(correction.RecordHash,corrected.Record!.RecordHash);Assert.Equal(new[]{KnowledgeMutationKindV1.Fact,KnowledgeMutationKindV1.Correction},corrected.Chain.Select(x=>x.Kind));
        var retraction=Record("retract-1","3",0,withdraw:correction.ExactRef());Assert.True((await store.IngestAsync(Corpus(retraction),Request())).Accepted);
        var result=await Store().QueryAsync(Request());Assert.Equal(KnowledgeQueryOutcomeV1.Abstained,result.Outcome);Assert.Null(result.Record);Assert.Contains("knowledge.unknown",result.ReasonCodes);Assert.Equal(3,result.Chain.Count);
        await using var c=new SqliteConnection($"Data Source={Db}");await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText="UPDATE knowledge_events SET kind='Fact'";await Assert.ThrowsAsync<SqliteException>(()=>q.ExecuteNonQueryAsync());
    }

    [Theory]
    [InlineData("Stale")]
    [InlineData("Withdrawn")]
    [InlineData("Conflicted")]
    [InlineData("Unentitled")]
    public async Task IneligibleRecordsNeverBecomeCurrentTruth(string state)
    {
        var record=FinancialEvidenceRecordContractRedTests.CreateRecord(state,"bad");var forged=new AuthorizedLocalCorpusResultV1(true,[record],[]);var saved=await Store().IngestAsync(forged,Request());Assert.False(saved.Accepted);Assert.Equal(KnowledgeQueryOutcomeV1.Abstained,(await Store().QueryAsync(Request())).Outcome);
    }

    [Fact]
    public async Task RevokedRetractedConflictMalformedMissingSourceAndHashMismatchFailClosed()
    {
        var store=Store();var valid=Record("fact-1","1",42);Assert.True((await store.IngestAsync(Corpus(valid),Request())).Accepted);
        Assert.False((await store.IngestAsync(new(false,[valid],["revoked"]),Request())).Accepted);
        Assert.False((await store.IngestAsync(new(true,[Record("conflict","1",99)],[]),Request())).Accepted);
        var missing=valid with{Draft=valid.Draft with{SourceProvider=""}};Assert.False((await store.IngestAsync(new(true,[missing],[]),Request())).Accepted);
        var tampered=valid with{Draft=valid.Draft with{Payload=Encoding.UTF8.GetBytes("{}")}};Assert.False((await store.IngestAsync(new(true,[tampered],[]),Request())).Accepted);
        var badHash=valid with{RecordHash="sha256:"+new string('0',64)};Assert.False((await store.IngestAsync(new(true,[badHash],[]),Request())).Accepted);
        Assert.Equal(valid.RecordHash,(await store.QueryAsync(Request())).Record!.RecordHash);
    }

    [Fact]
    public async Task IdentityConflictBadReferenceAndUnknownQueryAreDeterministic()
    {
        var store=Store();var first=Record("fact-1","1",42);var initial=await store.IngestAsync(Corpus(first),Request());var duplicate=await store.IngestAsync(Corpus(first),Request());Assert.True(initial.Accepted);Assert.True(duplicate.Idempotent);
        var changed=Record("fact-1","1",43);Assert.Equal("knowledge.identity-conflict",(await store.IngestAsync(Corpus(changed),Request())).ReasonCode);
        var fake=first.ExactRef() with{RecordHash="sha256:"+new string('f',64)};Assert.Equal("knowledge.reference-not-current",(await store.IngestAsync(Corpus(Record("fix","2",44,fake)),Request())).ReasonCode);
        var unknown=Request() with{InstrumentId="ETH-USDT"};var a=await store.QueryAsync(unknown);var b=await Store().QueryAsync(unknown);Assert.Equal(KnowledgeQueryOutcomeV1.Abstained,a.Outcome);Assert.Equal(a.ReasonCodes,b.ReasonCodes);
        var ambiguous=Request() with{CollectionTypes=new HashSet<FinancialEvidenceCollectionTypeV1>{FinancialEvidenceCollectionTypeV1.ApprovedFact,FinancialEvidenceCollectionTypeV1.SpecialistAnalysis}};Assert.Contains("knowledge.query-ambiguous",(await store.QueryAsync(ambiguous)).ReasonCodes);
    }

    [Fact]
    public async Task QueryOrderAndHistoricalVersionAreStable()
    {
        var store=Store();var first=Record("z","1",42,effective:ObservedAt);await store.IngestAsync(Corpus(first),Request());var second=Record("a","2",43,first.ExactRef(),effective:ObservedAt.AddMinutes(1));var correction=await store.IngestAsync(Corpus(second),Request(asOf:AsOf));Assert.True(correction.Accepted,correction.ReasonCode);
        var historical=await store.QueryAsync(Request(asOf:ObservedAt.AddSeconds(30)));var current=await Store().QueryAsync(Request());Assert.Equal(first.RecordHash,historical.Record!.RecordHash);Assert.Equal(second.RecordHash,current.Record!.RecordHash);Assert.Equal(current.Chain.Select(x=>x.RecordHash), (await Store().QueryAsync(Request())).Chain.Select(x=>x.RecordHash));
    }

    private DeterministicKnowledgeStore Store()=>new(Db);
    private static AuthorizedLocalCorpusResultV1 Corpus(FinancialEvidenceRecordV1 record)=>new(true,[record],[]);
    private static FinancialEvidenceRetrievalRequestV1 Request(DateTimeOffset? asOf=null)=>FinancialEvidenceRecordContractRedTests.CreateRequest(asOf:asOf);
    private static FinancialEvidenceRecordV1 Record(string id,string version,int value,FinancialEvidenceRecordRefV1? supersedes=null,FinancialEvidenceRecordRefV1? withdraw=null,DateTimeOffset? effective=null)
    {
        var source=FinancialEvidenceRecordContractRedTests.CreateRecord("Approved",id);var draft=source.Draft with{RecordVersion=version,Payload=Encoding.UTF8.GetBytes($"{{\"fact\":{value}}}"),EffectiveAt=effective??source.Draft.EffectiveAt,SupersedesRecordRefs=supersedes is null?[]:[supersedes],WithdrawalRecordRef=withdraw};return FinancialEvidenceRecordV1.Create(draft);
    }
}
