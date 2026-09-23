using System.Text.Json;
using WpeAgent.ModelOff;
using WpeAgent.Notifications;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class MarketTeacherBriefTests : IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,7,27,2,0,0,TimeSpan.Zero);
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"wpe-teacher-"+Guid.NewGuid().ToString("N"));
    private string DatabasePath=>Path.Combine(_directory,"agent.db");
    public MarketTeacherBriefTests()=>Directory.CreateDirectory(_directory);
    public void Dispose(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();try{Directory.Delete(_directory,true);}catch(IOException){}}

    [Fact]
    public void BriefExplainsVerifiedFactsWithoutQuizScoreOrOrderInstruction()
    {
        var brief=MarketTeacherBriefComposerV1.Compose(Research());
        Assert.Contains("CPI tracks average consumer price change",brief.Content,StringComparison.Ordinal);
        Assert.Contains("not investment advice",brief.Content,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quiz",brief.Content,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("score",brief.Content,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("buy",brief.Content,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sell",brief.Content,StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64,brief.Sha256.Length);
        Assert.Equal("wpe.market-teacher-persona/1.0",brief.PersonaVersion);
        Assert.Equal(48,MarketTeacherBriefComposerV1.Persona.PersonaAge);
        Assert.Contains("evidence-first",MarketTeacherBriefComposerV1.Persona.Traits);
        Assert.Contains("no-quiz-or-scoring",MarketTeacherBriefComposerV1.Persona.Boundaries);
    }

    [Fact]
    public void ChineseBriefUsesExperiencedPlainSpokenVoiceWithoutChangingFacts()
    {
        var brief=MarketTeacherBriefComposerV1.Compose(Research(),"zh_CN");
        Assert.Contains("先看事实，不急着下结论",brief.Content,StringComparison.Ordinal);
        Assert.Contains("CPI 衡量消费者价格的总体变化",brief.Content,StringComparison.Ordinal);
        Assert.Contains("334.1",brief.Content,StringComparison.Ordinal);
        Assert.Contains("不要当成买卖按钮",brief.Content,StringComparison.Ordinal);
        Assert.DoesNotContain("考试",brief.Content,StringComparison.Ordinal);
    }

    [Fact]
    public async Task DailyPublisherQueuesAtMostOneBriefPerUtcDay()
    {
        var store=new AgentSqliteStore(DatabasePath);var observer=new RecordingObserver();
        Assert.True(await MarketTeacherBriefPublisherV1.PublishDailyAsync(store,observer,Research(),"binance-futures","Testnet",CancellationToken.None));
        Assert.False(await MarketTeacherBriefPublisherV1.PublishDailyAsync(store,observer,Research(),"binance-futures","Testnet",CancellationToken.None));
        var value=Assert.Single(observer.Events);Assert.Equal(NotificationEventKind.MarketBrief,value.Kind);Assert.NotEmpty(value.Content!);
        var formatted=new NotificationFormatter().Format(value);Assert.Contains("Market notes",formatted.Text,StringComparison.Ordinal);
    }

    [Fact]
    public void IneligibleResearchCannotProduceBrief()
    {
        var blocked=Research() with{Status=ModelOffOutputStatusV1.Blocked,Decision=new("block",false,["research.invalid"])};
        Assert.Throws<ArgumentException>(()=>MarketTeacherBriefComposerV1.Compose(blocked));
    }

    [Fact]
    public void ProductionTradingLoopDoesNotPublishMarketTeacherBriefs()
    {
        var source=File.ReadAllText(Path.Combine(ProjectRoot(),"Services","AutoTradingAgent.cs"));
        Assert.DoesNotContain("MarketTeacherBriefPublisherV1.PublishDailyAsync",source,StringComparison.Ordinal);
        Assert.DoesNotContain("NotificationEventKind.MarketBrief",source,StringComparison.Ordinal);
    }

    [Fact]
    public void SecureSettingsPersistExplicitMarketBriefConsent()
    {
        var path=Path.Combine(_directory,"settings.json");var store=new AgentSettingsStore(path);
        var service=new NotificationConfigurationService(store,new NoopCleaner(),new TestProtector());
        service.SaveConfirmed(new(true,new HashSet<NotificationEventKind>([NotificationEventKind.MarketBrief]),false,"22:00","07:00","UTC",new(false,"",""),new(false,"","","","")),true,"SAVE NOTIFICATION CONFIG");
        Assert.Contains(NotificationEventKind.MarketBrief.ToString(),store.Load().Notification.EventKinds);
    }

    [Fact]
    public void DesktopSettingsExposeMarketBriefInEverySupportedLanguage()
    {
        var source=File.ReadAllText(Path.Combine(ProjectRoot(),"SetupWindow.xaml.cs"));
        Assert.Contains("_notifyMarketBrief",source,StringComparison.Ordinal);
        foreach(var language in new[]{"zh_CN","zh_TW","ja_JP","ko_KR","it_IT"})Assert.Contains(language,source,StringComparison.Ordinal);
    }

    private static ModelOffAgentOutputV1 Research()=>new(ModelOffAgentV1.Research,"research","cycle",Now,Now,"wpe.live-cycle-snapshot/1.0",new("wpe.live-research","1.0"),
        [new("market",ModelOffSourceKindV1.Audit,Now,Now,ModelOffSourceStatusV1.Available,"sha256:"+new string('a',64))],ModelOffOutputStatusV1.Succeeded,new(ModelOffUncertaintyLevelV1.None,[],[]),
        JsonSerializer.SerializeToElement(new{validations=new[]{new{Symbol="BTCUSDT"}},macro_observations=new[]{new{IndicatorId="CUUR0000SA0",ObservationAtUtc=new DateTimeOffset(2026,6,1,0,0,0,TimeSpan.Zero),Revision=2,Geography="US",Frequency="monthly",Unit="index",Value=334.1m,SourceArtifactHash=new string('b',64),FirstObservedAtUtc=Now.AddMinutes(-2)}}}),[],new("publish_research",true,[]),[],ModelOffFixedTemplatesV1.SummaryVersion);

    private sealed class RecordingObserver:IConfirmedNotificationObserver
    {public List<ConfirmedNotificationEvent> Events{get;}=[];public Task ObserveAsync(ConfirmedNotificationEvent value,CancellationToken ct){Events.Add(value);return Task.CompletedTask;}}
    private sealed class NoopCleaner:ILegacyNotificationSecretCleaner{public void ClearAfterConfirmedMigration(){}}
    private sealed class TestProtector:INotificationSecretProtector{public string Protect(string value)=>"protected:"+value;}
    private static string ProjectRoot()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
}
