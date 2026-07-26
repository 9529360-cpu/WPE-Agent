using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using 币安量化机器人.Services;
using 币安量化机器人.Services.Agent;
using 币安量化机器人.Services.Exchange;

namespace WPE.Tests;

public sealed class AgentSettingsResilienceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpe-settings-tests-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "agent-settings.json");

    [Theory]
    [InlineData("{")]
    [InlineData("{\"Environment\":")]
    [InlineData("not-json")]
    public void CorruptSettings_ReturnSafeUnconfiguredDefaultsAndDiagnostic(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, content, Encoding.UTF8);
        var store = new AgentSettingsStore(SettingsPath);

        var loaded = store.Load();

        Assert.Equal(ExchangeEnvironment.Testnet, loaded.Environment);
        Assert.False(loaded.SetupCompleted);
        Assert.Empty(loaded.Testnet.EncryptedApiKey);
        Assert.Empty(loaded.Mainnet.EncryptedApiKey);
        var diagnostic = Assert.IsType<AgentSettingsLoadDiagnostic>(store.LastLoadDiagnostic);
        Assert.Equal(nameof(JsonException), diagnostic.ErrorType);
        Assert.Equal(SettingsPath, diagnostic.ConfigurationPath);
        if (content == "not-json") Assert.DoesNotContain(content, diagnostic.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Encrypted", diagnostic.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_IsAtomicAndSuccessfulLoadClearsDiagnostic()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "{", Encoding.UTF8);
        var store = new AgentSettingsStore(SettingsPath);
        _ = store.Load();
        Assert.NotNull(store.LastLoadDiagnostic);

        store.Save(Settings(ExchangeEnvironment.Testnet));
        var loaded = store.Load();

        Assert.Null(store.LastLoadDiagnostic);
        Assert.Equal(ExchangeEnvironment.Testnet, loaded.Environment);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void EmptySymbols_DoNotFallBackToDefaultMarkets()
    {
        var store = new AgentSettingsStore(SettingsPath);
        var settings = Settings(ExchangeEnvironment.Testnet);
        settings.Symbols = [];

        store.Save(settings);

        Assert.Empty(store.Load().Symbols);
    }

    [Fact]
    public void ConfiguredSymbols_ArePreservedDuringNormalization()
    {
        var store = new AgentSettingsStore(SettingsPath);
        var settings = Settings(ExchangeEnvironment.Testnet);
        settings.Symbols = ["solusdt", "XRPUSDT"];

        store.Save(settings);

        Assert.Equal(["SOLUSDT", "XRPUSDT"], store.Load().Symbols);
    }

    [Fact]
    public void InvalidDpapiCipher_IsNotSwallowedOrLoggedAsMissing()
    {
        var store = new AgentSettingsStore(SettingsPath);
        var settings = Settings(ExchangeEnvironment.Testnet);
        var profile = store.GetActiveExchange(settings);
        var invalidCipher = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        profile.EncryptedCredentials["apiKey"] = invalidCipher;
        profile.EncryptedCredentials["secret"] = SecretVaultService.Encrypt("test-secret-do-not-log");

        var error = Assert.Throws<CryptographicException>(() => store.GetExchangeCredentials(profile));

        Assert.DoesNotContain(invalidCipher, error.ToString(), StringComparison.Ordinal);
        Assert.Null(store.LastLoadDiagnostic);
    }

    [Fact]
    public void EnvironmentCredentialSlots_RemainStrictlyIsolated()
    {
        var store = new AgentSettingsStore(SettingsPath);
        var settings = Settings(ExchangeEnvironment.Testnet);
        store.SaveExchangeCredentials(settings, "testnet-key", "testnet-secret");
        settings.Environment = ExchangeEnvironment.Mainnet;
        store.SaveExchangeCredentials(settings, "mainnet-key", "mainnet-secret");

        settings.Environment = ExchangeEnvironment.Testnet;
        Assert.Equal(("testnet-key", "testnet-secret"), store.GetCredentials(settings));
        settings.Environment = ExchangeEnvironment.Mainnet;
        Assert.Equal(("mainnet-key", "mainnet-secret"), store.GetCredentials(settings));
    }

    [Fact]
    public void UnconfiguredMainnet_DoesNotFallBackToTestnetCredentials()
    {
        var store = new AgentSettingsStore(SettingsPath);
        var settings = Settings(ExchangeEnvironment.Testnet);
        store.SaveExchangeCredentials(settings, "testnet-key", "testnet-secret");
        settings.Environment = ExchangeEnvironment.Mainnet;

        Assert.Equal((string.Empty, string.Empty), store.GetCredentials(settings));
    }

    [Theory]
    [InlineData("https://fapi.binance.com")]
    [InlineData("https://example.com")]
    [InlineData("http://testnet.binancefuture.com")]
    [InlineData("https://testnet.binancefuture.com.evil.example")]
    [InlineData("https://testnet.binancefuture.com:444")]
    public void BinanceTestnetProfile_RejectsNonAllowlistedOrigin(string endpoint)
    {
        var profile = Profile(endpoint, isTestnet: true);
        Assert.Throws<InvalidOperationException>(() => AgentSettingsStore.ValidateExchangeProfile(profile));
    }

    [Fact]
    public void BinanceTestnetProfile_AcceptsOnlyOfficialOrigin()
    {
        AgentSettingsStore.ValidateExchangeProfile(Profile("https://testnet.binancefuture.com", isTestnet: true));
    }

    [Theory]
    [InlineData("okx","https://www.okx.com")]
    [InlineData("bybit","https://api-testnet.bybit.com")]
    [InlineData("gate","https://api-testnet.gateapi.io")]
    [InlineData("bitget","https://api.bitget.com")]
    public void MultiExchangeTestnetProfile_AcceptsOfficialOrigin(string providerId,string endpoint)
    {
        AgentSettingsStore.ValidateExchangeProfile(new ExchangeConnectionProfile
        {
            ProviderId=providerId,
            IsTestnet=true,
            Endpoint=endpoint
        });
    }

    [Theory]
    [InlineData("okx")]
    [InlineData("bybit")]
    [InlineData("gate")]
    [InlineData("bitget")]
    public void MultiExchangeTestnetProfile_RejectsUntrustedOrigin(string providerId)
    {
        var profile=new ExchangeConnectionProfile
        {
            ProviderId=providerId,
            IsTestnet=true,
            Endpoint="https://example.invalid"
        };

        Assert.Throws<InvalidOperationException>(()=>AgentSettingsStore.ValidateExchangeProfile(profile));
    }

    [Fact]
    public void BinanceStream_DefaultsToOfficialTestnetWebSocket()
    {
        var client = new BinanceStreamClient();
        var endpoint = typeof(BinanceStreamClient)
            .GetField("_streamEndpoint", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(client);

        Assert.Equal("wss://stream.binancefuture.com/stream", endpoint);
    }

    [Fact]
    public void EnvironmentSave_SynchronizesSlotAndProfile()
    {
        var store = new AgentSettingsStore(SettingsPath);
        var settings = Settings(ExchangeEnvironment.Testnet);
        var profile = store.GetActiveExchange(settings);

        store.SaveEnvironmentExchange(settings, profile, "new-key", "new-secret");

        Assert.Equal(profile.EncryptedCredentials["apiKey"], settings.Testnet.EncryptedApiKey);
        Assert.Equal(("new-key", "new-secret"), store.GetCredentials(settings));
        Assert.Empty(settings.Mainnet.EncryptedApiKey);
    }

    [Theory]
    [InlineData("\u5e01\u5b89API.txt")]
    [InlineData("\u5e01\u5b89API\u6a21\u677f.txt")]
    public void DesktopTestnetImport_AcceptsKnownUnicodeNamesAndPersistsOnlyEncryptedTestnetCredentials(string fileName)
    {
        const string apiKey = "fake-testnet-api-key";
        const string apiSecret = "fake-testnet-api-secret";
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, fileName), $"  # fake credentials only\n\n API_KEY = {apiKey} \n // ignored\n API_SECRET = {apiSecret}\n", Encoding.UTF8);
        var store = new AgentSettingsStore(SettingsPath, null, _directory);
        var settings = Settings(ExchangeEnvironment.Mainnet);

        store.ImportDesktopTestnetIfEmpty(settings);

        Assert.NotEqual(apiKey, settings.Testnet.EncryptedApiKey);
        Assert.NotEqual(apiSecret, settings.Testnet.EncryptedApiSecret);
        Assert.Equal(apiKey, SecretVaultService.Decrypt(settings.Testnet.EncryptedApiKey));
        Assert.Equal(apiSecret, SecretVaultService.Decrypt(settings.Testnet.EncryptedApiSecret));
        Assert.Empty(settings.Mainnet.EncryptedApiKey);
        Assert.Empty(settings.Mainnet.EncryptedApiSecret);
        Assert.Equal(ExchangeEnvironment.Mainnet, settings.Environment);
        Assert.Equal((string.Empty, string.Empty), store.GetCredentials(settings));
        var importedProfile = Assert.Single(settings.Exchanges);
        Assert.True(importedProfile.IsTestnet);
        Assert.Equal("https://testnet.binancefuture.com", importedProfile.Endpoint);
        Assert.Equal(settings.Testnet.EncryptedApiKey, importedProfile.EncryptedCredentials["apiKey"]);
        Assert.Equal(settings.Testnet.EncryptedApiSecret, importedProfile.EncryptedCredentials["secret"]);
        var persisted = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain(apiKey, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(apiSecret, persisted, StringComparison.Ordinal);
        var loaded = store.Load();
        Assert.Empty(loaded.Mainnet.EncryptedApiKey);
        Assert.Empty(loaded.Mainnet.EncryptedApiSecret);
    }

    [Theory]
    [InlineData("API_KEY=fake-key\nAPI_KEY=duplicate-key\nAPI_SECRET=fake-secret")]
    [InlineData("API_KEY=\nAPI_SECRET=fake-secret")]
    [InlineData("API_KEY=fake-key\nAPI_SECRET=")]
    [InlineData("API_KEY=fake-key\nAPI_SECRET=fake-secret\nPASSPHRASE=extra-sensitive-value")]
    public void DesktopTestnetImport_RejectsAmbiguousOrAdditionalCredentialData(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "\u5e01\u5b89API\u6a21\u677f.txt"), content, Encoding.UTF8);
        var store = new AgentSettingsStore(SettingsPath, null, _directory);
        var settings = Settings(ExchangeEnvironment.Testnet);

        store.ImportDesktopTestnetIfEmpty(settings);

        Assert.Empty(settings.Testnet.EncryptedApiKey);
        Assert.Empty(settings.Testnet.EncryptedApiSecret);
        Assert.False(File.Exists(SettingsPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static AgentSettings Settings(ExchangeEnvironment environment) => new()
    {
        Environment = environment,
        Brains = new(StringComparer.OrdinalIgnoreCase) { ["DeepSeek"] = new BrainSlot() }
    };

    private static ExchangeConnectionProfile Profile(string endpoint, bool isTestnet) => new()
    {
        ProviderId = "binance-futures",
        IsTestnet = isTestnet,
        Endpoint = endpoint
    };
}
