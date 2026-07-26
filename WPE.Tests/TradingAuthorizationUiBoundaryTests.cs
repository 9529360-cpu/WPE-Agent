namespace WPE.Tests;

public sealed class TradingAuthorizationUiBoundaryTests
{
    [Fact]
    public void WpfSetup_AutoAuthorizationUsesOnlyReadinessBoundTestnetTransition()
    {
        var setup=Source("SetupWindow.xaml.cs");
        var finish=Slice(setup,"private async void Finish_Click","private void Environment_Changed");

        Assert.Equal(1,Count(setup,"ChangeAuthorizationModeAfterReadinessAsync("));
        Assert.DoesNotContain("ChangeAuthorizationModeAsync(",setup,StringComparison.Ordinal);
        Assert.Contains("_last?.Ready!=true",finish,StringComparison.Ordinal);
        Assert.Contains("_settings.Environment!=ExchangeEnvironment.Testnet",finish,StringComparison.Ordinal);
        Assert.Contains("!profile.IsTestnet",finish,StringComparison.Ordinal);
        Assert.Contains("_settings.AuthorizationMode!=TradingAuthorizationMode.Auto",finish,StringComparison.Ordinal);
        Assert.DoesNotContain("TradingAuthorizationMode.Review",finish,StringComparison.Ordinal);
        Assert.DoesNotContain("Mainnet",finish,StringComparison.Ordinal);
    }

    [Fact]
    public void ModeAuditSchema_DoesNotPersistSettingsOrCredentialFields()
    {
        var store=Source("Services","Agent","AgentSqliteStore.cs");
        var start=store.IndexOf("CREATE TABLE IF NOT EXISTS trading_authorization_mode_audits",StringComparison.Ordinal);var end=store.IndexOf(';',start);

        Assert.True(start>=0&&end>start);var schema=store[start..end];
        Assert.Contains("old_mode",schema,StringComparison.Ordinal);Assert.Contains("new_mode",schema,StringComparison.Ordinal);Assert.Contains("user_id",schema,StringComparison.Ordinal);Assert.Contains("device_id",schema,StringComparison.Ordinal);Assert.Contains("changed_at",schema,StringComparison.Ordinal);Assert.Contains("reason",schema,StringComparison.Ordinal);
        Assert.DoesNotContain("api_key",schema,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("encrypted",schema,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("secret",schema,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("settings_json",schema,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetupModeAndHistoricalReviewControlsAreFailClosed()
    {
        var setup=Source("SetupWindow.xaml.cs");
        var rendered=Slice(setup,"protected override void OnContentRendered","private void LoadSettings");
        var saveMode=Slice(setup,"private void SaveAuthorizationMode_Click","private async Task RefreshPendingReviewsAsync");

        Assert.Contains("AuthorizationModeBox.IsEnabled=false",rendered,StringComparison.Ordinal);
        Assert.Contains("AuthorizationReasonBox.IsEnabled=false",rendered,StringComparison.Ordinal);
        Assert.Contains("ApprovePendingReviewButton.Visibility=Visibility.Collapsed",rendered,StringComparison.Ordinal);
        Assert.Contains("RejectPendingReviewButton.Visibility=Visibility.Collapsed",rendered,StringComparison.Ordinal);
        Assert.Contains("RevokePendingReviewButton.Visibility=Visibility.Collapsed",rendered,StringComparison.Ordinal);
        Assert.Contains("Authorization mode is read-only after setup.",saveMode,StringComparison.Ordinal);
        Assert.DoesNotContain("ChangeAuthorizationMode",saveMode,StringComparison.Ordinal);
    }

    [Fact]
    public void HistoricalReviewActionsCannotApproveRejectRevokeOrExecuteOrders()
    {
        var setup=Source("SetupWindow.xaml.cs");
        var handler=Slice(setup,"private Task HandlePendingReviewAsync","private async void ApprovePendingReview_Click");
        var xaml=Source("SetupWindow.xaml");

        Assert.Contains("DisablePendingReviewActions",handler,StringComparison.Ordinal);
        Assert.Contains("Task.CompletedTask",handler,StringComparison.Ordinal);
        Assert.DoesNotContain("_reviewApprovals",handler,StringComparison.Ordinal);
        Assert.DoesNotContain("HandleAsync",handler,StringComparison.Ordinal);
        Assert.DoesNotContain("TradingExecutionGateway",setup,StringComparison.Ordinal);
        Assert.DoesNotContain("ReliableOrderExecutor",setup,StringComparison.Ordinal);
        foreach(var forbidden in new[]{"Quantity","Entry price","EntryPrice","Stop loss","StopLoss","Take profit","TakeProfit","Order type","OrderType"})
            Assert.DoesNotContain(forbidden,xaml,StringComparison.OrdinalIgnoreCase);
    }

    private static string Source(params string[] parts)=>File.ReadAllText(Path.Combine([Root(),..parts]));
    private static string Root()=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
    private static string Slice(string source,string startMarker,string endMarker)
    {
        var start=source.IndexOf(startMarker,StringComparison.Ordinal);var end=source.IndexOf(endMarker,start+startMarker.Length,StringComparison.Ordinal);
        Assert.True(start>=0&&end>start,$"Missing source boundary: {startMarker} -> {endMarker}");return source[start..end];
    }
    private static int Count(string source,string value)=>source.Split(value,StringSplitOptions.None).Length-1;
}
