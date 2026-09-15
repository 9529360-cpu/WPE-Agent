using 币安量化机器人.Services;

namespace WPE.Tests;

public sealed class NotificationServiceSecurityTests
{
    [Theory]
    [InlineData("https://oapi.dingtalk.com/robot/send?access_token=abc")]
    [InlineData("https://OAPI.DINGTALK.COM/robot/send?access_token=abc&timestamp=123&sign=xyz")]
    public void OfficialCustomRobotWebhooks_AreAllowed(string webhook)
    {
        Assert.True(NotificationService.IsAllowedDingTalkWebhook(webhook));
    }

    [Theory]
    [InlineData("http://oapi.dingtalk.com/robot/send?access_token=abc")]
    [InlineData("https://example.com/robot/send?access_token=abc")]
    [InlineData("https://oapi.dingtalk.com.evil.example/robot/send?access_token=abc")]
    [InlineData("https://user@oapi.dingtalk.com/robot/send?access_token=abc")]
    [InlineData("https://oapi.dingtalk.com:444/robot/send?access_token=abc")]
    [InlineData("https://oapi.dingtalk.com/robot/send/extra?access_token=abc")]
    [InlineData("https://oapi.dingtalk.com/robot/send?foo=bar")]
    [InlineData("https://oapi.dingtalk.com/robot/send?access_token=")]
    [InlineData("https://oapi.dingtalk.com/robot/send?access_token=abc#fragment")]
    [InlineData("https://127.0.0.1/robot/send?access_token=abc")]
    public void NonOfficialOrMalformedWebhooks_AreRejected(string webhook)
    {
        Assert.False(NotificationService.IsAllowedDingTalkWebhook(webhook));
    }

    [Fact]
    public async Task SendDingTalkAsync_RejectsDisallowedWebhookBeforeNetworkAccess()
    {
        var service = new NotificationService();

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => service.SendDingTalkAsync("http://127.0.0.1:1/internal", "test"));

        Assert.Equal("webhook", error.ParamName);
    }
}
