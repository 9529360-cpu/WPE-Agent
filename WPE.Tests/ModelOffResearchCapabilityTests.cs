using System.Text.Json;
using WpeAgent.ModelOff;
using 币安量化机器人.Services.Agent;

namespace WPE.Tests;

public sealed class ModelOffResearchCapabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 11, 20, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ModelOffResearchCapabilityV1.News, ModelOffSourceKindV1.News)]
    [InlineData(ModelOffResearchCapabilityV1.Technical, ModelOffSourceKindV1.Market)]
    [InlineData(ModelOffResearchCapabilityV1.Backtest, ModelOffSourceKindV1.Strategy)]
    public void SupportedCapabilitiesProduceVersionedCanonicalRepeatableOutputs(ModelOffResearchCapabilityV1 capability, ModelOffSourceKindV1 kind)
    {
        var input = capability switch
        {
            ModelOffResearchCapabilityV1.Technical => TechnicalInput(),
            ModelOffResearchCapabilityV1.Backtest => BacktestInput(),
            _ => Input(capability, [Source(kind)])
        };
        var first = DeterministicResearchCapabilityProducerV1.Produce(input);
        var second = DeterministicResearchCapabilityProducerV1.Produce(input);
        var firstDocument = ModelOffCanonicalSerializerV1.Serialize(first);
        var secondDocument = ModelOffCanonicalSerializerV1.Serialize(second);

        Assert.Equal(ModelOffOutputStatusV1.Succeeded, first.Status);
        Assert.Equal("record_research", first.Decision.Action);
        Assert.True(ModelOffEligibilityV1.IsEligibleForDownstream(first));
        Assert.Equal(Now, first.GeneratedAtUtc);
        Assert.Equal(firstDocument.Sha256, secondDocument.Sha256);
        Assert.Equal(firstDocument.Utf8Bytes, secondDocument.Utf8Bytes);
        Assert.DoesNotContain("recommend", firstDocument.Json, StringComparison.OrdinalIgnoreCase);
        if(capability!=ModelOffResearchCapabilityV1.Backtest)
            Assert.DoesNotContain("probability", firstDocument.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("caus", firstDocument.Json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FundamentalCapabilityRecordsOnlyValidatedObservedFacts()
    {
        var output=DeterministicMemoryService.ProduceFundamentalModelOff(FundamentalInput());Assert.Equal(ModelOffOutputStatusV1.Succeeded,output.Status);Assert.True(ModelOffEligibilityV1.IsEligibleForDownstream(output));Assert.Equal("record_research",output.Decision.Action);Assert.Equal("BTCUSDT",output.Facts.GetProperty("symbol").GetString());Assert.DoesNotContain("forecast",ModelOffCanonicalSerializerV1.Serialize(output).Json,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FundamentalCapabilityRejectsFabricatedCanonicalHash()
    {
        var input=FundamentalInput();using var facts=JsonDocument.Parse(input.Facts.GetRawText());var values=facts.RootElement.EnumerateObject().ToDictionary(x=>x.Name,x=>x.Value.Clone());values["canonicalSha256"]=JsonSerializer.SerializeToElement(new string('f',64));var tampered=input with{Facts=JsonSerializer.SerializeToElement(values.ToDictionary(x=>x.Key,x=>(object)x.Value))};var output=DeterministicMemoryService.ProduceFundamentalModelOff(tampered);Assert.Equal(ModelOffOutputStatusV1.Abstained,output.Status);Assert.Contains("research.invalid.fundamental_canonical_hash",output.Decision.ReasonCodes);
    }

    [Theory]
    [InlineData("other-provider")]
    [InlineData("lowercase-symbol")]
    public void FundamentalCapabilityRejectsSelfConsistentUnsupportedIdentity(string fixture)
    {
        var input=fixture=="other-provider"?FundamentalInput(provider:"other"):FundamentalInput(symbol:"btcusdt",baseAsset:"btc",quoteAsset:"usdt");
        var output=DeterministicMemoryService.ProduceFundamentalModelOff(input);Assert.Equal(ModelOffOutputStatusV1.Abstained,output.Status);Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(output));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("hash")]
    [InlineData("rsi")]
    [InlineData("future")]
    [InlineData("stale")]
    [InlineData("source")]
    [InlineData("symbol-mismatch")]
    [InlineData("symbol-case")]
    [InlineData("symbol-space")]
    public void InvalidTechnicalFactsAbstain(string fixture)
    {
        var input=TechnicalInput();var facts=TechnicalFacts(rsi:fixture=="rsi"?101:55,observedAt:fixture switch{"future"=>Now.AddSeconds(1),"stale"=>Now.AddMinutes(-6),_=>Now},schema:fixture=="schema"?"wpe.technical-assessment/2.0":"wpe.technical-assessment/1.0",hash:fixture=="hash"?"bad":new string('a',64),symbol:fixture switch{"symbol-case"=>"btcusdt","symbol-space"=>" BTCUSDT",_=>"BTCUSDT"},marketSymbol:fixture=="symbol-mismatch"?"ETHUSDT":"BTCUSDT");if(fixture=="source")input=input with{Sources=[Source(ModelOffSourceKindV1.Market)]};input=input with{Facts=facts};
        var output=StrategyResearchAgent.ProduceTechnicalModelOff(input);Assert.Equal(ModelOffOutputStatusV1.Abstained,output.Status);Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(output));Assert.NotEmpty(output.Decision.ReasonCodes);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("symbol")]
    [InlineData("strategy")]
    [InlineData("strategy-id")]
    [InlineData("future")]
    [InlineData("stale")]
    [InlineData("trades")]
    [InlineData("metric")]
    [InlineData("lifecycle")]
    [InlineData("hash")]
    [InlineData("source")]
    public void InvalidBacktestFactsAbstain(string fixture)
    {
        var input=BacktestInput(fixture);
        var output=StrategyResearchAgent.ProduceBacktestModelOff(input);
        Assert.Equal(ModelOffOutputStatusV1.Abstained,output.Status);
        Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(output));
        Assert.Contains(output.Decision.ReasonCodes,reason=>reason.StartsWith("research.invalid.backtest",StringComparison.Ordinal));
    }

    [Fact]
    public void BacktestFactsAreCanonicalRepeatableAndBoundToExactStrategySource()
    {
        var input=BacktestInput();
        var first=StrategyResearchAgent.ProduceBacktestModelOff(input);
        var second=StrategyResearchAgent.ProduceBacktestModelOff(input);
        Assert.Equal(ModelOffOutputStatusV1.Succeeded,first.Status);
        Assert.True(ModelOffEligibilityV1.IsEligibleForDownstream(first));
        Assert.Equal(ModelOffCanonicalSerializerV1.Serialize(first).Sha256,ModelOffCanonicalSerializerV1.Serialize(second).Sha256);
        Assert.Equal("BTCUSDT",first.Facts.GetProperty("symbol").GetString());
        Assert.Equal("trend-alpha",first.Facts.GetProperty("strategyId").GetString());
        Assert.Equal("trend-v1",first.Facts.GetProperty("strategyVersion").GetString());
    }

    [Fact]
    public void MacroFactsProduceCanonicalRepeatableOutputWithoutModelInference()
    {
        var input = MacroInput();

        var first = DeterministicMemoryService.ProduceMacroModelOff(input);
        var second = DeterministicMemoryService.ProduceMacroModelOff(input);
        var firstDocument = ModelOffCanonicalSerializerV1.Serialize(first);
        var secondDocument = ModelOffCanonicalSerializerV1.Serialize(second);

        Assert.Equal(ModelOffOutputStatusV1.Succeeded, first.Status);
        Assert.True(ModelOffEligibilityV1.IsEligibleForDownstream(first));
        Assert.Equal("record_research", first.Decision.Action);
        Assert.Equal("CPI_ALL_ITEMS", first.Facts.GetProperty("indicatorId").GetString());
        Assert.Equal(firstDocument.Sha256, secondDocument.Sha256);
        Assert.DoesNotContain("forecast", firstDocument.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("caus", firstDocument.Json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("missing-source")]
    [InlineData("wrong-source")]
    [InlineData("wrong-schema")]
    [InlineData("missing-indicator")]
    [InlineData("invalid-value")]
    [InlineData("non-utc")]
    [InlineData("future-release")]
    [InlineData("temporal-order")]
    [InlineData("source-before-release")]
    public void InvalidMacroFactsAbstainAndRemainIneligible(string fixture)
    {
        var input = MacroInput();
        input = fixture switch
        {
            "missing-source" => input with { Sources = [] },
            "wrong-source" => input with { Sources = [Source(ModelOffSourceKindV1.News)] },
            "wrong-schema" => input with { Facts = MacroFacts(schema: "wpe.macro-facts/2.0") },
            "missing-indicator" => input with { Facts = MacroFacts(indicatorId: " ") },
            "invalid-value" => input with { Facts = MacroFacts(value: "not-a-number") },
            "non-utc" => input with { Facts = MacroFacts(releasedAt: Now.ToOffset(TimeSpan.FromHours(9)).AddMinutes(-3)) },
            "future-release" => input with { Facts = MacroFacts(releasedAt: Now.AddMinutes(1)) },
            "temporal-order" => input with { Facts = MacroFacts(observedAt: Now.AddMinutes(-1), releasedAt: Now.AddMinutes(-3)) },
            "source-before-release" => input with { Sources = [Source(ModelOffSourceKindV1.Macro) with { AsOfUtc = Now.AddMinutes(-4) }] },
            _ => throw new ArgumentOutOfRangeException(nameof(fixture))
        };

        var output = DeterministicMemoryService.ProduceMacroModelOff(input);

        Assert.Equal(ModelOffOutputStatusV1.Abstained, output.Status);
        Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(output));
        Assert.NotEmpty(output.Decision.ReasonCodes);
    }

    [Theory]
    [InlineData("missing-sources")]
    [InlineData("missing-required-kind")]
    [InlineData("stale")]
    [InlineData("unknown")]
    [InlineData("unsupported")]
    [InlineData("inconsistent")]
    [InlineData("invalid")]
    [InlineData("error")]
    [InlineData("future-as-of")]
    [InlineData("future-received")]
    [InlineData("missing-source-id")]
    [InlineData("missing-artifact-hash")]
    [InlineData("missing-facts")]
    public void KnownRejectionFixturesFailClosed(string fixture)
    {
        var source = Source(ModelOffSourceKindV1.News);
        IReadOnlyList<ModelOffSourceV1> sources = fixture switch
        {
            "missing-sources" => [],
            "missing-required-kind" => [source with { Kind = ModelOffSourceKindV1.Market }],
            "stale" => [source with { Status = ModelOffSourceStatusV1.Stale }],
            "unknown" => [source with { Status = ModelOffSourceStatusV1.Unknown }],
            "unsupported" => [source with { Status = ModelOffSourceStatusV1.Unsupported }],
            "inconsistent" => [source with { Status = ModelOffSourceStatusV1.Inconsistent }],
            "invalid" => [source with { Status = ModelOffSourceStatusV1.Invalid }],
            "error" => [source with { Status = ModelOffSourceStatusV1.Error }],
            "future-as-of" => [source with { AsOfUtc = Now.AddSeconds(1) }],
            "future-received" => [source with { ReceivedAtUtc = Now.AddSeconds(1) }],
            "missing-source-id" => [source with { SourceId = " " }],
            "missing-artifact-hash" => [source with { ArtifactHash = null }],
            "missing-facts" => [source],
            _ => throw new ArgumentOutOfRangeException(nameof(fixture))
        };

        var input = Input(ModelOffResearchCapabilityV1.News, sources);
        if (fixture == "missing-facts") input = input with { Facts = JsonSerializer.SerializeToElement(new { }) };
        var output = DeterministicResearchCapabilityProducerV1.Produce(input);
        Assert.Equal(ModelOffOutputStatusV1.Abstained, output.Status);
        Assert.False(output.Decision.EligibleForDownstream);
        Assert.False(ModelOffEligibilityV1.IsEligibleForDownstream(output));
        Assert.NotEmpty(output.Decision.ReasonCodes);
        var refusal = ModelOffCanonicalSerializerV1.Serialize(output);
        Assert.NotEmpty(refusal.Sha256);
        Assert.Contains("\"status\":\"abstained\"", refusal.Json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("output")]
    [InlineData("cycle")]
    [InlineData("schema")]
    [InlineData("method")]
    [InlineData("version")]
    [InlineData("wrong-schema-version")]
    [InlineData("wrong-method")]
    [InlineData("wrong-method-version")]
    [InlineData("non-utc")]
    [InlineData("non-object-facts")]
    public void MalformedContractsAreRejected(string fixture)
    {
        var input = Input(ModelOffResearchCapabilityV1.News, [Source(ModelOffSourceKindV1.News)]);
        input = fixture switch
        {
            "output" => input with { OutputId = " " },
            "cycle" => input with { CycleId = " " },
            "schema" => input with { InputSchema = "research" },
            "method" => input with { MethodId = " " },
            "version" => input with { MethodVersion = " " },
            "wrong-schema-version" => input with { InputSchema = "wpe.research-input/2.0" },
            "wrong-method" => input with { MethodId = "wpe.other-method" },
            "wrong-method-version" => input with { MethodVersion = "2.0" },
            "non-utc" => input with { EvaluationTimeUtc = Now.ToOffset(TimeSpan.FromHours(9)) },
            "non-object-facts" => input with { Facts = JsonSerializer.SerializeToElement(42) },
            _ => throw new ArgumentOutOfRangeException(nameof(fixture))
        };
        Assert.Throws<ArgumentException>(() => DeterministicResearchCapabilityProducerV1.Produce(input));
    }

    [Fact]
    public void AdjacentInvariantsPreserveExplicitFactsAndCanonicalSourceOrdering()
    {
        var later = Source(ModelOffSourceKindV1.News) with { SourceId = "z", ArtifactHash = "bb", AsOfUtc = Now.AddMinutes(-1) };
        var earlier = Source(ModelOffSourceKindV1.News) with { SourceId = "a", ArtifactHash = "aa", AsOfUtc = Now.AddMinutes(-2) };
        var input = Input(ModelOffResearchCapabilityV1.News, [later, earlier]);
        var reversed = input with { Sources = [earlier, later] };
        var first = ModelOffCanonicalSerializerV1.Serialize(DeterministicMemoryService.ProduceNewsModelOff(input));
        var second = ModelOffCanonicalSerializerV1.Serialize(DeterministicMemoryService.ProduceNewsModelOff(reversed));

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal("observed", input.Facts.GetProperty("state").GetString());
    }

    [Fact]
    public void CapabilitySpecificEntryPointsRejectCrossCapabilityCalls()
    {
        var news = Input(ModelOffResearchCapabilityV1.News, [Source(ModelOffSourceKindV1.News)]);
        Assert.Throws<ArgumentException>(() => StrategyResearchAgent.ProduceTechnicalModelOff(news));
        Assert.Throws<ArgumentException>(() => StrategyResearchAgent.ProduceBacktestModelOff(news));
        Assert.Throws<ArgumentException>(() => DeterministicMemoryService.ProduceMacroModelOff(news));
        Assert.Throws<ArgumentException>(() => DeterministicMemoryService.ProduceFundamentalModelOff(news));
    }

    [Fact]
    public void CapabilitySpecificEntryPointsProduceTheSameCanonicalContract()
    {
        var technical = TechnicalInput();
        var backtest = BacktestInput();
        var news = Input(ModelOffResearchCapabilityV1.News, [Source(ModelOffSourceKindV1.News)]);
        var macro = MacroInput();
        var fundamental=FundamentalInput();

        Assert.Equal(ModelOffOutputStatusV1.Succeeded, StrategyResearchAgent.ProduceTechnicalModelOff(technical).Status);
        Assert.Equal(ModelOffOutputStatusV1.Succeeded, StrategyResearchAgent.ProduceBacktestModelOff(backtest).Status);
        Assert.Equal(ModelOffOutputStatusV1.Succeeded, DeterministicMemoryService.ProduceNewsModelOff(news).Status);
        Assert.Equal(ModelOffOutputStatusV1.Succeeded, DeterministicMemoryService.ProduceMacroModelOff(macro).Status);
        Assert.Equal(ModelOffOutputStatusV1.Succeeded, DeterministicMemoryService.ProduceFundamentalModelOff(fundamental).Status);
    }

    private static ModelOffResearchInputV1 Input(ModelOffResearchCapabilityV1 capability, IReadOnlyList<ModelOffSourceV1> sources) => new(
        capability, $"{capability.ToString().ToLowerInvariant()}-output", "cycle-1", Now,
        "wpe.research-input/1.0", $"wpe.{capability.ToString().ToLowerInvariant()}-method", "1.0",
        sources, JsonSerializer.SerializeToElement(new { state = "observed" }), []);

    private static ModelOffSourceV1 Source(ModelOffSourceKindV1 kind) =>
        new("fixture", kind, Now.AddMinutes(-2), Now.AddMinutes(-1), ModelOffSourceStatusV1.Available, "aa");

    private static ModelOffResearchInputV1 MacroInput() => new(
        ModelOffResearchCapabilityV1.Macro, "macro-output", "cycle-1", Now,
        "wpe.research-input/1.0", "wpe.macro-method", "1.0",
        [Source(ModelOffSourceKindV1.Macro)], MacroFacts(), []);
    private static ModelOffResearchInputV1 TechnicalInput()=>new(ModelOffResearchCapabilityV1.Technical,"technical-output","cycle-1",Now,"wpe.research-input/1.0","wpe.technical-method","1.0",[Source(ModelOffSourceKindV1.Market) with{SourceId="market:BTCUSDT",AsOfUtc=Now,ArtifactHash="sha256:"+new string('a',64)}],TechnicalFacts(),[]);
    private static JsonElement TechnicalFacts(double rsi=55,DateTimeOffset? observedAt=null,string schema="wpe.technical-assessment/1.0",string? hash=null,string symbol="BTCUSDT",string marketSymbol="BTCUSDT")=>JsonSerializer.SerializeToElement(new{schema,symbol,marketSymbol,observedAtUtc=observedAt??Now,marketEvidenceSha256=hash??new string('a',64),rsi,trend15m=.1,trend1h=.2,trend4h=.3});

    private static ModelOffResearchInputV1 FundamentalInput(string provider="binance-futures",string symbol="BTCUSDT",string baseAsset="BTC",string quoteAsset="USDT"){var fact=CryptoInstrumentFundamentalCanonicalizerV1.Create(provider,"Testnet",symbol,symbol,baseAsset,quoteAsset,quoteAsset,"PERPETUAL","TRADING",Now.AddYears(-2),Now.AddMinutes(-1),new string('a',64));return new(ModelOffResearchCapabilityV1.Fundamental,"fundamental-output","cycle-1",Now,"wpe.research-input/1.0","wpe.fundamental-method","1.0",[Source(ModelOffSourceKindV1.Fundamental) with{AsOfUtc=Now.AddMinutes(-1),ArtifactHash="sha256:"+fact.CanonicalSha256}],JsonSerializer.SerializeToElement(new{schema=fact.Schema,providerId=fact.ProviderId,environment=fact.Environment,symbol=fact.Symbol,nativeSymbol=fact.NativeSymbol,baseAsset=fact.BaseAsset,quoteAsset=fact.QuoteAsset,marginAsset=fact.MarginAsset,contractType=fact.ContractType,tradingStatus=fact.TradingStatus,onboardAtUtc=fact.OnboardAtUtc,observedAtUtc=fact.ObservedAtUtc,sourceArtifactSha256=fact.SourceArtifactSha256,canonicalSha256=fact.CanonicalSha256}),[]);}

    private static ModelOffResearchInputV1 BacktestInput(string? fixture=null)
    {
        var validatedAt=fixture switch{"future"=>Now.AddSeconds(1),"stale"=>Now.AddHours(-25),_=>Now.AddMinutes(-2)};
        var symbol=fixture=="symbol"?"btcusdt":"BTCUSDT";
        var strategyId=fixture=="strategy-id"?" ":"trend-alpha";
        var strategy=fixture=="strategy"?" ":"trend-v1";
        var sampleSize=1000;
        var trades=fixture=="trades"?1001:80;
        var winRate=fixture=="metric"?1.1:.58;
        var approved=fixture!="lifecycle";
        var promoted=true;
        var fact=BacktestValidationCanonicalizerV1.Create(symbol,strategyId,strategy,validatedAt,sampleSize,trades,40,180,winRate,1.5,.002,.12,1.2,.08,.7,.2,.78,approved,promoted);
        var hash=fixture=="hash"?new string('f',64):fact.CanonicalSha256;
        var facts=JsonSerializer.SerializeToElement(new{schema=fixture=="schema"?"wpe.backtest-validation/2.0":fact.Schema,symbol=fact.Symbol,strategyId=fact.StrategyId,strategyVersion=fact.StrategyVersion,validatedAtUtc=fact.ValidatedAtUtc,sampleSize=fact.SampleSize,trades=fact.Trades,outOfSampleTrades=fact.OutOfSampleTrades,coverageDays=fact.CoverageDays,winRate=fact.WinRate,profitFactor=fact.ProfitFactor,expectancy=fact.Expectancy,maxDrawdown=fact.MaxDrawdown,sharpe=fact.Sharpe,outOfSampleReturn=fact.OutOfSampleReturn,walkForwardScore=fact.WalkForwardScore,monteCarloLossProbability=fact.MonteCarloLossProbability,qualityScore=fact.QualityScore,approved=fact.Approved,promoted=fact.Promoted,canonicalSha256=hash});
        var source=Source(ModelOffSourceKindV1.Strategy) with{SourceId=fixture=="source"?"backtest:ETHUSDT:trend-alpha:trend-v1":$"backtest:{symbol}:{strategyId}:{strategy}",AsOfUtc=validatedAt,ArtifactHash="sha256:"+hash};
        return new(ModelOffResearchCapabilityV1.Backtest,"backtest-output","cycle-1",Now,"wpe.research-input/1.0","wpe.backtest-method","1.0",[source],facts,[]);
    }

    private static JsonElement MacroFacts(
        string schema = "wpe.macro-facts/1.0",
        string indicatorId = "CPI_ALL_ITEMS",
        object? value = null,
        DateTimeOffset? observedAt = null,
        DateTimeOffset? releasedAt = null) => JsonSerializer.SerializeToElement(new
        {
            schema,
            indicatorId,
            geography = "US",
            frequency = "monthly",
            unit = "index",
            observationAtUtc = observedAt ?? Now.AddDays(-30),
            releasedAtUtc = releasedAt ?? Now.AddMinutes(-3),
            value = value ?? 319.7m
        });
}
