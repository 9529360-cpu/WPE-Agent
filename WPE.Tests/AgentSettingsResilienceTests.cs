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
    public void LegacyRemoteBrainFields_AreIgnoredAndNotRePersisted()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath,
            """
            {
              "AiMode": 1,
              "ActiveBrain": "DeepSeek",
              "Brains": {
                "DeepSeek": {
                  "Provider": "DeepSeek",
                  "Endpoint": "https://api.deepseek.com/chat/completions",
                  "Model": "deepseek-chat",
                  "EncryptedKey": "legacy-encrypted-key",
                  "IsLocal": false
                }
              }
            }
            """,
            Encoding.UTF8);

        var store=new AgentSettingsStore(SettingsPath);
        var loaded=store.Load();
        store.Save(loaded);
        var saved=File.ReadAllText(SettingsPath);

        Assert.Null(store.LastLoadDiagnostic);
        Assert.DoesNotContain("\"AiMode\"",saved,StringComparison.Ordinal);
        Assert.DoesNotContain("\"ActiveBrain\"",saved,StringComparison.Ordinal);
        Assert.DoesNotContain("\"Brains\"",saved,StringComparison.Ordinal);
        Assert.DoesNotContain("DeepSeek",saved,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("legacy-encrypted-key",saved,StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultBinanceTestnetCreatesInactiveLocalMcpDerivative()
    {
        var store=new AgentSettingsStore(SettingsPath);
        var settings=Settings(ExchangeEnvironment.Testnet);

        store.Save(settings);
        var loaded=store.Load();

        var native=Assert.Single(
            loaded.Exchanges,
            profile=>profile.ProviderId.Equals("binance-futures",StringComparison.OrdinalIgnoreCase));
        var mcp=Assert.Single(
            loaded.Exchanges,
            profile=>profile.ProviderId.Equals("binance-mcp-local",StringComparison.OrdinalIgnoreCase));

        Assert.Equal(native.Id,loaded.ActiveExecutionConnectionId);
        Assert.Equal(native.Id,mcp.UpstreamConnectionId);
        Assert.True(mcp.Enabled);
        Assert.False(mcp.ExecutionEnabled);
        Assert.True(mcp.IsTestnet);
        Assert.Equal(native.Endpoint,mcp.Endpoint);
        Assert.Empty(mcp.EncryptedCredentials);
    }

    [Fact]
    public void ExistingLocalMcpWithoutUpstreamIsBackfilledFromSingleNativeTestnet()
    {
        var store=new AgentSettingsStore(SettingsPath);
        var settings=Settings(ExchangeEnvironment.Testnet);
        settings.Exchanges=
        [
            new()
            {
                Id="native-one",
                ProviderId="binance-futures",
                DisplayName="Native",
                IsTestnet=true,
                Endpoint="https://testnet.binancefuture.com",
                ExecutionEnabled=true
            },
            new()
            {
                Id="mcp-one",
                ProviderId="binance-mcp-local",
                DisplayName="MCP",
                IsTestnet=true,
                Endpoint="https://testnet.binancefuture.com",
                ExecutionEnabled=false
            }
        ];
        settings.ActiveExecutionConnectionId="native-one";

        store.Save(settings);
        var loaded=store.Load();

        var mcp=Assert.Single(
            loaded.Exchanges,
            profile=>profile.ProviderId.Equals("binance-mcp-local",StringComparison.OrdinalIgnoreCase));
        Assert.Equal("native-one",mcp.UpstreamConnectionId);
        Assert.Equal("native-one",loaded.ActiveExecutionConnectionId);
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
    [InlineData("binance-mcp-local","https://testnet.binancefuture.com")]
    [InlineData("okx","https://www.okx.com")]
    [InlineData("okx-mcp","https://www.okx.com")]
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
    [InlineData("binance-mcp-local")]
    [InlineData("okx")]
    [InlineData("okx-mcp")]
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

    [Fact]
    public void SettingsStore_DoesNotExposeAmbientDesktopCredentialImport()
    {
        var methods = typeof(AgentSettingsStore).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

        Assert.DoesNotContain(methods, method => method.Name.StartsWith("ImportDesktop", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static AgentSettings Settings(ExchangeEnvironment environment) => new()
    {
        Environment = environment
    };

    private static ExchangeConnectionProfile Profile(string endpoint, bool isTestnet) => new()
    {
        ProviderId = "binance-futures",
        IsTestnet = isTestnet,
        Endpoint = endpoint
    };
}
