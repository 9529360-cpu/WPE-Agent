using System.Text.Json.Nodes;

namespace WPE.Tests;

public sealed class ModelOffCapabilityMaturityTests
{
    private static readonly string[] CapabilityIds =
    [
        "orchestrator", "market-data", "news", "macro", "technical", "fundamental", "strategy",
        "backtest", "risk", "execution", "position", "review-post-trade", "teacher"
    ];

    private static readonly string[] AggregateIds =
    [
        "market", "research", "strategy", "risk", "execution", "recovery", "audit"
    ];

    private static readonly IReadOnlyDictionary<string, string[]> ExpectedMappings =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["orchestrator"] = ["market", "research", "strategy", "risk", "execution", "recovery", "audit"],
            ["market-data"] = ["market"],
            ["news"] = ["research"],
            ["macro"] = ["research"],
            ["technical"] = ["research"],
            ["fundamental"] = ["research"],
            ["strategy"] = ["strategy"],
            ["backtest"] = ["research"],
            ["risk"] = ["risk"],
            ["execution"] = ["execution"],
            ["position"] = ["execution", "recovery"],
            ["review-post-trade"] = ["audit"],
            ["teacher"] = ["research"]
        };

    [Fact]
    public void CanonicalInventoryIsCurrentAndValid()
    {
        var errors = Validate(LoadCanonical());

        Assert.Empty(errors);
    }

    [Fact]
    public void PostTradeAcceptanceIsBoundedAndDoesNotUpgradeAuditAggregate()
    {
        var root=LoadCanonical();var postTrade=root["capabilities"]!.AsArray().Single(Capability("review-post-trade"));var audit=root["aggregates"]!.AsArray().Single(node=>String(node,"id")=="audit");
        Assert.Equal("yes",String(postTrade,"maturity"));Assert.True(Bool(postTrade,"implemented"));Assert.True(Bool(postTrade,"accepted"));Assert.Contains("no broad causal-performance claim",String(postTrade,"acceptance_scope"),StringComparison.Ordinal);
        Assert.Equal("yes",String(audit,"maturity"));Assert.True(Bool(audit,"accepted"));Assert.Contains("upstream business-decision certification",String(audit,"acceptance_scope"),StringComparison.Ordinal);
    }

    [Fact]
    public void PositionAcceptanceRemainsSeparatelyBoundedFromRecoveryAggregate()
    {
        var root=LoadCanonical();var position=root["capabilities"]!.AsArray().Single(Capability("position"));var aggregates=root["aggregates"]!.AsArray();
        Assert.Equal("yes",String(position,"maturity"));Assert.True(Bool(position,"implemented"));Assert.True(Bool(position,"accepted"));Assert.Contains("same-cycle mutation invalidation",String(position,"acceptance_scope"),StringComparison.Ordinal);Assert.Contains("no live Testnet provider certification",String(position,"acceptance_scope"),StringComparison.Ordinal);
        var recovery=aggregates.Single(node=>String(node,"id")=="recovery");Assert.Equal("yes",String(recovery,"maturity"));Assert.True(Bool(recovery,"accepted"));Assert.Contains("restart reconciliation",String(recovery,"acceptance_scope"),StringComparison.Ordinal);
    }

    [Fact]
    public void AuditAggregateAcceptanceRemainsSeparatelyCandidateBound()
    {
        var root=LoadCanonical();var orchestrator=root["capabilities"]!.AsArray().Single(Capability("orchestrator"));var aggregates=root["aggregates"]!.AsArray();
        Assert.Equal("yes",String(orchestrator,"maturity"));Assert.True(Bool(orchestrator,"implemented"));Assert.True(Bool(orchestrator,"accepted"));Assert.Contains("atomic append-only output/handoff persistence",String(orchestrator,"acceptance_scope"),StringComparison.Ordinal);Assert.Contains("no live Testnet mutation certification",String(orchestrator,"acceptance_scope"),StringComparison.Ordinal);
        var audit=aggregates.Single(node=>String(node,"id")=="audit");Assert.Equal("yes",String(audit,"maturity"));Assert.True(Bool(audit,"accepted"));Assert.Contains("seven-output/six-handoff",String(audit,"acceptance_scope"),StringComparison.Ordinal);Assert.True(IsSha(String(audit,"evidence_set_sha256")));
    }

    [Fact]
    public void StrategyAggregateAcceptanceIsCandidateBoundAndExplicitlyNonMutating()
    {
        var aggregate=LoadCanonical()["aggregates"]!.AsArray().Single(node=>String(node,"id")=="strategy");Assert.Equal("yes",String(aggregate,"maturity"));Assert.True(Bool(aggregate,"accepted"));Assert.Contains("24 consecutive closed one-minute shadow observations",String(aggregate,"acceptance_scope"),StringComparison.Ordinal);Assert.Contains("excludes Mainnet, mutation, credentials",String(aggregate,"acceptance_scope"),StringComparison.Ordinal);Assert.True(IsSha(String(aggregate,"evidence_set_sha256")));
    }

    [Fact]
    public void RiskAggregateAcceptanceIsAuthoritativeCandidateBoundAndNonMutating()
    {
        var aggregate=LoadCanonical()["aggregates"]!.AsArray().Single(node=>String(node,"id")=="risk");Assert.Equal("yes",String(aggregate,"maturity"));Assert.True(Bool(aggregate,"accepted"));Assert.Contains("authoritative Binance Futures Testnet permission, account, position, market and trading-rule",String(aggregate,"acceptance_scope"),StringComparison.Ordinal);Assert.Contains("excludes Mainnet, mutation, credential disclosure, synthetic account substitution",String(aggregate,"acceptance_scope"),StringComparison.Ordinal);Assert.True(IsSha(String(aggregate,"evidence_set_sha256")));
    }

    [Fact]
    public void ExecutionAggregateAcceptanceIsCandidateBoundAndLeavesNoTestnetResidue()
    {
        var aggregate=LoadCanonical()["aggregates"]!.AsArray().Single(node=>String(node,"id")=="execution");Assert.Equal("yes",String(aggregate,"maturity"));Assert.True(Bool(aggregate,"accepted"));Assert.Contains("shared-gateway mutation routing and idempotency",String(aggregate,"acceptance_scope"),StringComparison.Ordinal);Assert.Contains("zero position/protection residue",String(aggregate,"acceptance_scope"),StringComparison.Ordinal);Assert.Contains("excludes Mainnet",String(aggregate,"acceptance_scope"),StringComparison.Ordinal);Assert.True(IsSha(String(aggregate,"evidence_set_sha256")));
    }

    [Fact]
    public void RecoveryAggregateAcceptanceIsCandidateBoundAndProvesNoResubmit()
    {
        var aggregate=LoadCanonical()["aggregates"]!.AsArray().Single(node=>String(node,"id")=="recovery");Assert.Equal("yes",String(aggregate,"maturity"));Assert.True(Bool(aggregate,"accepted"));Assert.Contains("fresh capability revalidation before cancellation",String(aggregate,"acceptance_scope"),StringComparison.Ordinal);Assert.Contains("no resubmission",String(aggregate,"acceptance_scope"),StringComparison.Ordinal);Assert.Contains("zero position/order residue",String(aggregate,"acceptance_scope"),StringComparison.Ordinal);Assert.Contains("excludes Mainnet",String(aggregate,"acceptance_scope"),StringComparison.Ordinal);Assert.True(IsSha(String(aggregate,"evidence_set_sha256")));
    }

    [Fact]
    public void AuditAggregateAcceptanceBindsRestartDurabilityWithoutMutationClaims()
    {
        var aggregate=LoadCanonical()["aggregates"]!.AsArray().Single(node=>String(node,"id")=="audit");Assert.Equal("yes",String(aggregate,"maturity"));Assert.True(Bool(aggregate,"accepted"));Assert.Contains("verified unchanged after SQLite restart and idempotent replay",String(aggregate,"acceptance_scope"),StringComparison.Ordinal);Assert.Contains("excludes Mainnet, mutation, credentials",String(aggregate,"acceptance_scope"),StringComparison.Ordinal);Assert.Contains("upstream business-decision certification",String(aggregate,"acceptance_scope"),StringComparison.Ordinal);Assert.True(IsSha(String(aggregate,"evidence_set_sha256")));
    }

    [Fact]
    public void MarketAggregateAcceptanceIsExplicitlyBoundedAndEvidenceBound()
    {
        var root=LoadCanonical();var capability=root["capabilities"]!.AsArray().Single(Capability("market-data"));var aggregate=root["aggregates"]!.AsArray().Single(node=>String(node,"id")=="market");
        Assert.Equal("yes",String(capability,"maturity"));Assert.True(Bool(capability,"implemented"));Assert.True(Bool(capability,"accepted"));Assert.Contains("Testnet provider-bound canonical provenance",String(capability,"acceptance_scope"),StringComparison.Ordinal);Assert.Contains("no raw HTTP response retention or live provider certification",String(capability,"acceptance_scope"),StringComparison.Ordinal);
        Assert.Equal("yes",String(aggregate,"maturity"));Assert.True(Bool(aggregate,"accepted"));Assert.Contains("Binance Futures Testnet",String(aggregate,"acceptance_scope"),StringComparison.Ordinal);Assert.Contains("excludes Mainnet",String(aggregate,"acceptance_scope"),StringComparison.Ordinal);Assert.True(IsSha(String(aggregate,"evidence_set_sha256")));
    }

    [Fact]
    public void TechnicalAcceptanceIsBoundedAndDoesNotUpgradeResearchAggregate()
    {
        var root=LoadCanonical();var capability=root["capabilities"]!.AsArray().Single(Capability("technical"));var aggregate=root["aggregates"]!.AsArray().Single(node=>String(node,"id")=="research");
        Assert.Equal("yes",String(capability,"maturity"));Assert.True(Bool(capability,"implemented"));Assert.True(Bool(capability,"accepted"));Assert.Contains("deterministic weighted signal aggregation",String(capability,"acceptance_scope"),StringComparison.Ordinal);Assert.Contains("no independent recomputation or live certification",String(capability,"acceptance_scope"),StringComparison.Ordinal);AssertResearchAggregate(aggregate);
    }

    [Fact]
    public void NewsAcceptanceIsBoundedAndDoesNotUpgradeResearchAggregate()
    {
        var root=LoadCanonical();var capability=root["capabilities"]!.AsArray().Single(Capability("news"));var aggregate=root["aggregates"]!.AsArray().Single(node=>String(node,"id")=="research");
        Assert.Equal("yes",String(capability,"maturity"));Assert.True(Bool(capability,"implemented"));Assert.True(Bool(capability,"accepted"));Assert.Contains("allowlisted publisher/HTTPS-host identity",String(capability,"acceptance_scope"),StringComparison.Ordinal);Assert.Contains("no live source availability certification or narrative truth claim",String(capability,"acceptance_scope"),StringComparison.Ordinal);
        AssertResearchAggregate(aggregate);
    }

    [Fact]
    public void StrategyCapabilityAndAggregateAcceptancesRemainSeparatelyBounded()
    {
        var root=LoadCanonical();var capability=root["capabilities"]!.AsArray().Single(Capability("strategy"));var aggregate=root["aggregates"]!.AsArray().Single(node=>String(node,"id")=="strategy");
        Assert.Equal("yes",String(capability,"maturity"));Assert.True(Bool(capability,"implemented"));Assert.True(Bool(capability,"accepted"));Assert.Contains("exact approved/promoted research strategy version",String(capability,"acceptance_scope"),StringComparison.Ordinal);Assert.Contains("no autonomous strategy discovery",String(capability,"acceptance_scope"),StringComparison.Ordinal);
        Assert.Equal("yes",String(aggregate,"maturity"));Assert.True(Bool(aggregate,"accepted"));Assert.Contains("24 consecutive closed one-minute shadow observations",String(aggregate,"acceptance_scope"),StringComparison.Ordinal);Assert.True(IsSha(String(aggregate,"evidence_set_sha256")));
    }

    [Fact]
    public void MacroAcceptanceIsBoundedAndDoesNotUpgradeResearchAggregate()
    {
        var root=LoadCanonical();var capability=root["capabilities"]!.AsArray().Single(Capability("macro"));var aggregate=root["aggregates"]!.AsArray().Single(node=>String(node,"id")=="research");
        Assert.Equal("yes",String(capability,"maturity"));Assert.True(Bool(capability,"implemented"));Assert.True(Bool(capability,"accepted"));Assert.Contains("two allowlisted official BLS series",String(capability,"acceptance_scope"),StringComparison.Ordinal);Assert.Contains("no forecast, causal trading claim or target-machine live availability certification",String(capability,"acceptance_scope"),StringComparison.Ordinal);
        AssertResearchAggregate(aggregate);
    }

    [Fact]
    public void FundamentalAcceptanceIsBoundedAndDoesNotUpgradeResearchAggregate()
    {
        var root=LoadCanonical();var capability=root["capabilities"]!.AsArray().Single(Capability("fundamental"));var aggregate=root["aggregates"]!.AsArray().Single(node=>String(node,"id")=="research");
        Assert.Equal("yes",String(capability,"maturity"));Assert.True(Bool(capability,"implemented"));Assert.True(Bool(capability,"accepted"));Assert.Contains("Binance Futures Testnet venue-instrument facts",String(capability,"acceptance_scope"),StringComparison.Ordinal);Assert.Contains("no protocol, issuer, tokenomics, on-chain, valuation or investment conclusion",String(capability,"acceptance_scope"),StringComparison.Ordinal);
        AssertResearchAggregate(aggregate);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("stale")]
    [InlineData("core-partial-implemented")]
    [InlineData("core-no-accepted")]
    public void ValidatorRejectsInvalidAuthorityClaims(string mutation)
    {
        var root = LoadCanonical();
        var capabilities = root["capabilities"]!.AsArray();

        switch (mutation)
        {
            case "missing":
                capabilities.RemoveAt(0);
                break;
            case "duplicate":
                capabilities.Add(capabilities[0]!.DeepClone());
                break;
            case "unknown":
                capabilities[0]!["aggregate_ids"]![0] = "unknown-aggregate";
                break;
            case "stale":
                root["authority"]!["status"] = "historical";
                break;
            case "core-partial-implemented":
                capabilities.Single(Capability("fundamental"))!["maturity"] = "partial";
                capabilities.Single(Capability("fundamental"))!["implemented"] = true;
                break;
            case "core-no-accepted":
                capabilities.Single(Capability("fundamental"))!["maturity"] = "no";
                capabilities.Single(Capability("fundamental"))!["accepted"] = true;
                break;
        }

        Assert.NotEmpty(Validate(root));
    }

    [Theory]
    [InlineData("wrong-parent")]
    [InlineData("missing-parent")]
    [InlineData("extra-parent")]
    [InlineData("duplicate-parent")]
    public void ValidatorRejectsAnyExactMappingDrift(string mutation)
    {
        var root = LoadCanonical();
        var capabilities = root["capabilities"]!.AsArray();

        switch (mutation)
        {
            case "wrong-parent":
                capabilities.Single(Capability("macro"))!["aggregate_ids"] = new JsonArray("market");
                break;
            case "missing-parent":
                capabilities.Single(Capability("orchestrator"))!["aggregate_ids"]!.AsArray().RemoveAt(0);
                break;
            case "extra-parent":
                capabilities.Single(Capability("market-data"))!["aggregate_ids"]!.AsArray().Add("research");
                break;
            case "duplicate-parent":
                capabilities.Single(Capability("market-data"))!["aggregate_ids"]!.AsArray().Add("market");
                break;
        }

        Assert.NotEmpty(Validate(root));
    }

    [Theory]
    [InlineData("core_no_or_partial_is_implemented", "weakened")]
    [InlineData("core_no_or_partial_is_accepted", "weakened")]
    [InlineData("yes_is_bounded_to_acceptance_scope", "weakened")]
    [InlineData("core_no_or_partial_is_implemented", "missing")]
    [InlineData("core_no_or_partial_is_accepted", "wrong-type")]
    [InlineData("yes_is_bounded_to_acceptance_scope", "missing")]
    public void ValidatorRejectsMissingMistypedOrWeakenedRefusalPolicy(string ruleName, string mutation)
    {
        var root = LoadCanonical();
        var rules = root["rules"]!.AsObject();

        switch (mutation)
        {
            case "weakened":
                rules[ruleName] = ruleName == "yes_is_bounded_to_acceptance_scope" ? false : true;
                break;
            case "missing":
                rules.Remove(ruleName);
                break;
            case "wrong-type":
                rules[ruleName] = "false";
                break;
        }

        Assert.NotEmpty(Validate(root));
    }

    private static JsonObject LoadCanonical() =>
        JsonNode.Parse(File.ReadAllText(FindInventoryPath()))!.AsObject();

    private static string FindInventoryPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "Docs", "product", "model-off-capability-maturity.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Could not locate the canonical model-off capability inventory.");
    }

    private static Func<JsonNode?, bool> Capability(string id) =>
        node => String(node, "id") == id;

    private static IReadOnlyList<string> Validate(JsonObject root)
    {
        var errors = new List<string>();
        var authority = root["authority"] as JsonObject;
        var rules = root["rules"] as JsonObject;
        var aggregates = root["aggregates"] as JsonArray;
        var capabilities = root["capabilities"] as JsonArray;

        Require(root["schema_version"]?.GetValue<string>() == "wpe.model-off-capability-maturity/1.0", "unknown schema", errors);
        Require(String(authority, "id") == "MO-01" && String(authority, "status") == "current" && String(authority, "as_of") == "2026-07-27", "stale authority", errors);
        Require(String(authority, "target") == "private_autonomous_testnet" && Bool(authority, "human_per_cycle_approval") == false, "wrong Testnet authority", errors);
        Require(String(authority, "mainnet") == "disabled" && String(authority, "teacher") == "bounded_current", "unsafe authority", errors);
        Require(Int(rules, "retained_capability_count") == 13 && Int(rules, "aggregate_count") == 7, "wrong declared counts", errors);
        Require(Bool(rules, "aggregation_upgrades_maturity") == false, "aggregation upgrade enabled", errors);
        Require(Bool(rules, "core_no_or_partial_is_implemented") == false, "core no/partial implementation refusal weakened", errors);
        Require(Bool(rules, "core_no_or_partial_is_accepted") == false, "core no/partial acceptance refusal weakened", errors);
        Require(Bool(rules, "yes_is_bounded_to_acceptance_scope") == true, "yes acceptance scope boundary weakened", errors);

        if (aggregates is null || capabilities is null)
        {
            errors.Add("missing inventory arrays");
            return errors;
        }

        var aggregateIds = aggregates.Select(node => String(node, "id")).ToArray();
        Require(aggregateIds.Length == 7 && aggregateIds.Distinct(StringComparer.Ordinal).Count() == 7, "missing or duplicate aggregate", errors);
        Require(aggregateIds.ToHashSet(StringComparer.Ordinal).SetEquals(AggregateIds), "unknown aggregate inventory", errors);
        var marketAggregate=aggregates.SingleOrDefault(node=>String(node,"id")=="market");
        Require(String(marketAggregate,"maturity")=="yes"&&Bool(marketAggregate,"accepted")==true&&!string.IsNullOrWhiteSpace(String(marketAggregate,"acceptance_scope"))&&String(marketAggregate,"acceptance_scope")!.Contains("Binance Futures Testnet",StringComparison.Ordinal)&&String(marketAggregate,"acceptance_scope")!.Contains("excludes Mainnet",StringComparison.Ordinal)&&IsSha(String(marketAggregate,"evidence_set_sha256")),"Market aggregate acceptance is unbounded or unproven",errors);
        var researchAggregate=aggregates.SingleOrDefault(node=>String(node,"id")=="research");
        Require(String(researchAggregate,"maturity")=="yes"&&Bool(researchAggregate,"accepted")==true&&!string.IsNullOrWhiteSpace(String(researchAggregate,"acceptance_scope"))&&String(researchAggregate,"acceptance_scope")!.Contains("model-off BTCUSDT Research cycle",StringComparison.Ordinal)&&String(researchAggregate,"acceptance_scope")!.Contains("excludes Mainnet",StringComparison.Ordinal)&&IsSha(String(researchAggregate,"evidence_set_sha256")),"Research aggregate acceptance is unbounded or unproven",errors);
        var strategyAggregate=aggregates.SingleOrDefault(node=>String(node,"id")=="strategy");
        Require(String(strategyAggregate,"maturity")=="yes"&&Bool(strategyAggregate,"accepted")==true&&!string.IsNullOrWhiteSpace(String(strategyAggregate,"acceptance_scope"))&&String(strategyAggregate,"acceptance_scope")!.Contains("24 consecutive closed one-minute shadow observations",StringComparison.Ordinal)&&String(strategyAggregate,"acceptance_scope")!.Contains("excludes Mainnet, mutation, credentials",StringComparison.Ordinal)&&IsSha(String(strategyAggregate,"evidence_set_sha256")),"Strategy aggregate acceptance is unbounded or unproven",errors);
        var riskAggregate=aggregates.SingleOrDefault(node=>String(node,"id")=="risk");
        Require(String(riskAggregate,"maturity")=="yes"&&Bool(riskAggregate,"accepted")==true&&!string.IsNullOrWhiteSpace(String(riskAggregate,"acceptance_scope"))&&String(riskAggregate,"acceptance_scope")!.Contains("authoritative Binance Futures Testnet permission, account, position, market and trading-rule",StringComparison.Ordinal)&&String(riskAggregate,"acceptance_scope")!.Contains("excludes Mainnet, mutation, credential disclosure, synthetic account substitution",StringComparison.Ordinal)&&IsSha(String(riskAggregate,"evidence_set_sha256")),"Risk aggregate acceptance is unbounded or unproven",errors);
        var executionAggregate=aggregates.SingleOrDefault(node=>String(node,"id")=="execution");
        Require(String(executionAggregate,"maturity")=="yes"&&Bool(executionAggregate,"accepted")==true&&!string.IsNullOrWhiteSpace(String(executionAggregate,"acceptance_scope"))&&String(executionAggregate,"acceptance_scope")!.Contains("shared-gateway mutation routing and idempotency",StringComparison.Ordinal)&&String(executionAggregate,"acceptance_scope")!.Contains("zero position/protection residue",StringComparison.Ordinal)&&String(executionAggregate,"acceptance_scope")!.Contains("excludes Mainnet",StringComparison.Ordinal)&&IsSha(String(executionAggregate,"evidence_set_sha256")),"Execution aggregate acceptance is unbounded or unproven",errors);
        var recoveryAggregate=aggregates.SingleOrDefault(node=>String(node,"id")=="recovery");
        Require(String(recoveryAggregate,"maturity")=="yes"&&Bool(recoveryAggregate,"accepted")==true&&!string.IsNullOrWhiteSpace(String(recoveryAggregate,"acceptance_scope"))&&String(recoveryAggregate,"acceptance_scope")!.Contains("fresh capability revalidation before cancellation",StringComparison.Ordinal)&&String(recoveryAggregate,"acceptance_scope")!.Contains("no resubmission",StringComparison.Ordinal)&&String(recoveryAggregate,"acceptance_scope")!.Contains("excludes Mainnet",StringComparison.Ordinal)&&IsSha(String(recoveryAggregate,"evidence_set_sha256")),"Recovery aggregate acceptance is unbounded or unproven",errors);
        var auditAggregate=aggregates.SingleOrDefault(node=>String(node,"id")=="audit");
        Require(String(auditAggregate,"maturity")=="yes"&&Bool(auditAggregate,"accepted")==true&&!string.IsNullOrWhiteSpace(String(auditAggregate,"acceptance_scope"))&&String(auditAggregate,"acceptance_scope")!.Contains("seven-output/six-handoff",StringComparison.Ordinal)&&String(auditAggregate,"acceptance_scope")!.Contains("verified unchanged after SQLite restart and idempotent replay",StringComparison.Ordinal)&&String(auditAggregate,"acceptance_scope")!.Contains("excludes Mainnet, mutation, credentials",StringComparison.Ordinal)&&IsSha(String(auditAggregate,"evidence_set_sha256")),"Audit aggregate acceptance is unbounded or unproven",errors);

        var capabilityIds = capabilities.Select(node => String(node, "id")).ToArray();
        Require(capabilityIds.Length == 13 && capabilityIds.Distinct(StringComparer.Ordinal).Count() == 13, "missing or duplicate capability", errors);
        Require(capabilityIds.ToHashSet(StringComparer.Ordinal).SetEquals(CapabilityIds), "unknown capability inventory", errors);

        foreach (var capability in capabilities)
        {
            var capabilityId = String(capability, "id");
            var maturity = String(capability, "maturity");
            var core = Bool(capability, "core");
            var implemented = Bool(capability, "implemented");
            var accepted = Bool(capability, "accepted");
            Require(maturity is "no" or "partial" or "yes", "unknown maturity", errors);
            var mappings = capability?["aggregate_ids"] as JsonArray;
            var actualMappings = mappings?.Select(StringValue).ToArray() ?? [];
            var hasExactMapping = capabilityId is not null &&
                ExpectedMappings.TryGetValue(capabilityId, out var expectedMappings) &&
                actualMappings.Length == expectedMappings.Length &&
                actualMappings.Distinct(StringComparer.Ordinal).Count() == actualMappings.Length &&
                actualMappings.ToHashSet(StringComparer.Ordinal).SetEquals(expectedMappings);
            Require(hasExactMapping, $"exact aggregate mapping mismatch for {capabilityId ?? "<missing>"}", errors);
            if (core == true && maturity is "no" or "partial")
            {
                Require(implemented == false && accepted == false, "core no/partial claimed implemented or accepted", errors);
            }

            if (maturity == "yes")
            {
                Require(implemented == true && accepted == true && !string.IsNullOrWhiteSpace(String(capability, "acceptance_scope")), "unbounded yes claim", errors);
            }
        }

        var teacher = capabilities.Single(Capability("teacher"));
        Require(Bool(teacher,"core")==false&&String(teacher,"lifecycle")=="current"&&String(teacher,"maturity")=="yes"&&Bool(teacher,"implemented")==true&&Bool(teacher,"accepted")==true&&!string.IsNullOrWhiteSpace(String(teacher,"acceptance_scope")),"Teacher bounded acceptance is invalid",errors);
        return errors;
    }

    private static string? String(JsonNode? node, string name) => StringValue(node?[name]);
    private static string? StringValue(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
    private static bool? Bool(JsonNode? node, string name) => node?[name] is JsonValue value && value.TryGetValue<bool>(out var result) ? result : null;
    private static int? Int(JsonNode? node, string name) => node?[name] is JsonValue value && value.TryGetValue<int>(out var result) ? result : null;
    private static bool IsSha(string? value)=>value is{Length:64}&&value.All(x=>x is>='0'and<='9'or>='a'and<='f');
    private static void AssertResearchAggregate(JsonNode? aggregate){Assert.Equal("yes",String(aggregate,"maturity"));Assert.True(Bool(aggregate,"accepted"));Assert.Contains("model-off BTCUSDT Research cycle",String(aggregate,"acceptance_scope"),StringComparison.Ordinal);Assert.True(IsSha(String(aggregate,"evidence_set_sha256")));}

    private static void Require(bool condition, string error, ICollection<string> errors)
    {
        if (!condition)
        {
            errors.Add(error);
        }
    }
}
