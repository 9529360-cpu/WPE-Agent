using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class TeacherV2AcceptanceTests
{
    private static readonly DateTimeOffset Start=new(2026,7,27,0,0,0,TimeSpan.Zero);
    [Fact] public void SustainedArtifactRequiresFourDistinctSamplesAcrossFifteenMinutes()
    {
        var facts=new[]{Fact(0,"a"),Fact(5,"b"),Fact(10,"c"),Fact(15,"d")};var artifact=TeacherSourceAcceptanceCanonicalizerV2.Create("3.6.0",Hash("candidate"),"BTCUSDT",facts);Assert.Equal(4,artifact.Samples.Count);Assert.False(artifact.CredentialUsed);Assert.False(artifact.MutationAttempted);Assert.Equal(64,artifact.CanonicalSha256.Length);Assert.Throws<InvalidOperationException>(()=>TeacherSourceAcceptanceCanonicalizerV2.Create("3.6.0",Hash("candidate"),"BTCUSDT",facts[..3]));Assert.Throws<InvalidOperationException>(()=>TeacherSourceAcceptanceCanonicalizerV2.Create("3.6.0",Hash("candidate"),"BTCUSDT",[Fact(0,"a"),Fact(1,"b"),Fact(2,"c"),Fact(3,"d")]));
    }
    [Fact] public void ProductionExposesCredentialFreeAcceptanceCommand(){var source=File.ReadAllText(Path.Combine(ProjectRoot(),"App.xaml.cs"));Assert.Contains("--teacher-v2-acceptance",source,StringComparison.Ordinal);var runner=File.ReadAllText(Path.Combine(ProjectRoot(),"Services","Agent","TeacherV2AcceptanceRunner.cs"));Assert.DoesNotContain("TradingExecutionGateway",runner,StringComparison.Ordinal);Assert.DoesNotContain("ApiKey",runner,StringComparison.OrdinalIgnoreCase);Assert.Contains("mutationAttempted=false",runner,StringComparison.Ordinal);}
    private static TeacherCryptoMarketFactV2 Fact(int minute,string salt){var time=Start.AddMinutes(minute);return TeacherCryptoMarketFactCanonicalizerV2.Create("binance-futures-public","BTCUSDT",time.AddSeconds(-1),time,100m+minute,99.9m+minute,.0001m,time.AddHours(2),1000m+minute,1m,1000m,[Hash(salt+"1"),Hash(salt+"2"),Hash(salt+"3")]);}private static string Hash(string value)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();private static string ProjectRoot()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
}
