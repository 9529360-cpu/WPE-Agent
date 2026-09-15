using System.Text.Json;
using 币安量化机器人.Services.Access;

namespace WPE.Tests;

public sealed class LocalAccountSecurityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-local-account-security-" + Guid.NewGuid().ToString("N"));
    private string AccountsPath => Path.Combine(_directory, "local-accounts.json");

    [Fact]
    public void AccountCreation_IsBootstrapOnly()
    {
        var service = new LocalAccountService(_directory);

        var first = service.Create("owner", "OwnerPassword123");
        var second = service.Create("intruder", "IntruderPassword123");

        Assert.True(first.Result.Success);
        Assert.False(second.Result.Success);
        Assert.Empty(second.RecoveryCode);
        Assert.False(service.CanCreateInitialAccount);
        Assert.True(service.Login("owner", "OwnerPassword123", remember: false).Success);
        Assert.False(service.Login("intruder", "IntruderPassword123", remember: false).Success);
    }

    [Fact]
    public void CorruptAccountStore_FailsClosedAndCannotBeReinitialized()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(AccountsPath, "{not-json");
        var service = new LocalAccountService(_directory);

        Assert.False(service.CanCreateInitialAccount);
        Assert.Throws<InvalidDataException>(() => service.Create("attacker", "AttackerPassword123"));
        Assert.Throws<InvalidDataException>(() => service.Login("owner", "OwnerPassword123", remember: false));
        Assert.Equal("{not-json", File.ReadAllText(AccountsPath));
    }

    [Fact]
    public void PersistedKdfDowngrade_IsRejectedBeforePasswordVerification()
    {
        var service = new LocalAccountService(_directory);
        Assert.True(service.Create("owner", "OwnerPassword123").Result.Success);

        var accounts = JsonSerializer.Deserialize<List<LocalAccountRecord>>(File.ReadAllText(AccountsPath))!;
        accounts[0].Iterations = 1;
        File.WriteAllText(AccountsPath, JsonSerializer.Serialize(accounts));

        Assert.False(service.CanCreateInitialAccount);
        Assert.Throws<InvalidDataException>(() => service.Login("owner", "OwnerPassword123", remember: false));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
