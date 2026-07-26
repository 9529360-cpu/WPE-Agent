namespace WPE.Tests;

public sealed class SetupWindowTradingReviewInteractionTests
{
    private static string Source()=>File.ReadAllText(Path.Combine(Root(),"SetupWindow.xaml.cs"));
    private static string Root()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));

    [Fact]
    public void Setup_DisablesLegacyReviewMutationsAndModeEditing()
    {
        var source=Source();
        Assert.Contains("AuthorizationModeBox.IsEnabled=false",source,StringComparison.Ordinal);
        Assert.Contains("ApprovePendingReviewButton.Visibility=Visibility.Collapsed",source,StringComparison.Ordinal);
        Assert.Contains("RejectPendingReviewButton.Visibility=Visibility.Collapsed",source,StringComparison.Ordinal);
        Assert.Contains("RevokePendingReviewButton.Visibility=Visibility.Collapsed",source,StringComparison.Ordinal);
        Assert.Contains("Historical Review records are read-only",source,StringComparison.Ordinal);
        Assert.DoesNotContain("_reviewApprovals.HandleAsync",source,StringComparison.Ordinal);
    }

    [Fact]
    public void Finish_ChangesToAutoOnlyAfterSuccessfulReadinessAndTestnetFacts()
    {
        var source=Source();var finish=source[source.IndexOf("Finish_Click",StringComparison.Ordinal)..source.IndexOf("Environment_Changed",StringComparison.Ordinal)];
        Assert.True(finish.IndexOf("_last?.Ready!=true",StringComparison.Ordinal)<finish.IndexOf("ChangeAuthorizationModeAfterReadinessAsync",StringComparison.Ordinal));
        Assert.Contains("ExchangeEnvironment.Testnet",finish,StringComparison.Ordinal);
        Assert.Contains("!profile.IsTestnet",finish,StringComparison.Ordinal);
        Assert.Contains("ChangeAuthorizationModeAfterReadinessAsync",finish,StringComparison.Ordinal);
        Assert.Contains("Initial setup readiness verified",finish,StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Approve")]
    [InlineData("Reject")]
    [InlineData("Revoke")]
    public void LegacyHandlers_AreFailClosed(string action)
    {
        var source=Source();var start=source.IndexOf("HandlePendingReviewAsync",StringComparison.Ordinal);var end=source.IndexOf("private async void ApprovePendingReview_Click",start,StringComparison.Ordinal);var method=source[start..end];
        Assert.Contains("Task.CompletedTask",method,StringComparison.Ordinal);Assert.DoesNotContain(action+"Async",method,StringComparison.Ordinal);
    }
}
