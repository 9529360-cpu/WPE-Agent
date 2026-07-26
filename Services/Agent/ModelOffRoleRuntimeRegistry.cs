using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace WpeAgent.ModelOff;

public sealed record AcceptedModelOffRoleManifestV1(
    ModelOffRoleRuntimeManifestV1 Manifest,
    bool Accepted,
    bool Revoked);

public sealed record ModelOffRegistryCapabilityV1(
    string Id,
    IReadOnlyList<ModelOffRuntimeRoleV1> Roles,
    string Maturity,
    bool Implemented,
    bool Accepted,
    string Lifecycle,
    string? AcceptanceScope);

public sealed record ModelOffRoleRuntimeRegistryV1(
    string SchemaVersion,
    string Environment,
    bool MainnetEnabled,
    IReadOnlyList<ModelOffRoleRuntimeManifestV1> Roles,
    IReadOnlyList<ModelOffRegistryCapabilityV1> Capabilities,
    byte[] CanonicalBytes,
    string Sha256)
{
    public const string Schema = "wpe.model-off-role-runtime-registry/1.0";
}

public sealed record ModelOffRoleRuntimeRegistryResultV1(
    bool Valid,
    ModelOffRoleRuntimeRegistryV1? Registry,
    IReadOnlyList<string> Errors);

public static class ModelOffRoleRuntimeRegistryBuilderV1
{
    private static readonly string[] MutationChain = ["RiskGate", "TradingExecutionGateway", "ReliableOrderExecutor"];

    private static readonly ModelOffRegistryCapabilityV1[] CapabilityMatrix =
    [
        Capability("orchestrator", AllRoles(), "yes", true, true, "current", "deterministic seven-role canonical chain with mandatory fail-closed pre-authorization gate and atomic append-only output/handoff persistence; no live Testnet mutation certification"),
        Capability("market-data", [ModelOffRuntimeRoleV1.Market], "yes", true, true, "current", "deterministic confirmed-candle market facts with source-time freshness, Testnet provider-bound canonical provenance and complete realtime enrichment gates; no raw HTTP response retention or live provider certification"),
        Capability("news", [ModelOffRuntimeRoleV1.Research], "partial"),
        Capability("macro", [ModelOffRuntimeRoleV1.Research], "partial"),
        Capability("technical", [ModelOffRuntimeRoleV1.Research], "partial"),
        Capability("fundamental", [ModelOffRuntimeRoleV1.Research], "partial"),
        Capability("strategy", [ModelOffRuntimeRoleV1.Strategy], "partial"),
        Capability("backtest", [ModelOffRuntimeRoleV1.Research], "yes", true, true, "current", "basic deterministic backtest component only"),
        Capability("risk", [ModelOffRuntimeRoleV1.Risk], "yes", true, true, "current", "covered crypto Risk Gate only"),
        Capability("execution", [ModelOffRuntimeRoleV1.Execution], "yes", true, true, "current", "deterministic execution contract boundary only; no Testnet claim"),
        Capability("position", [ModelOffRuntimeRoleV1.Execution, ModelOffRuntimeRoleV1.Recovery], "yes", true, true, "current", "deterministic position quantity, protection and external-isolation gates with same-cycle mutation invalidation; no live Testnet provider certification"),
        Capability("review-post-trade", [ModelOffRuntimeRoleV1.Audit], "yes", true, true, "current", "deterministic confirmed-close accounting with idempotent PnL, fee, funding, slippage and bounded strategy identity; no broad causal-performance claim"),
        Capability("teacher", [ModelOffRuntimeRoleV1.Research], "yes", true, true, "current", "deterministic daily market brief from eligible canonical Research; opt-in notification only")
    ];

    public static ModelOffRoleRuntimeRegistryResultV1 CreateDefault() =>
        Compose(ModelOffRoleRuntimeCatalogV1.CreateAll().Select(x => new AcceptedModelOffRoleManifestV1(x, true, false)).ToArray());

    public static ModelOffRoleRuntimeRegistryResultV1 Compose(IReadOnlyList<AcceptedModelOffRoleManifestV1>? registrations)
    {
        var errors = new List<string>();
        if (registrations is null || registrations.Count != 7)
            return Invalid("registry.requires-seven-accepted-roles");
        if (registrations.Any(x => x is null || x.Manifest is null))
            return Invalid("registry.manifest-missing");
        if (registrations.Any(x => !x.Accepted)) errors.Add("registry.manifest-not-accepted");
        if (registrations.Any(x => x.Revoked)) errors.Add("registry.manifest-revoked");

        var manifests = registrations.Select(x => x.Manifest).ToArray();
        ModelOffRoleRuntimeValidationV1 validation;
        try
        {
            validation = ModelOffRoleRuntimeValidatorV1.ValidateSet(manifests);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NullReferenceException)
        {
            return Invalid("registry.manifest-malformed");
        }
        errors.AddRange(validation.Errors.Select(x => $"registry.{x}"));
        if (manifests.Select(x => x.Role).Distinct().Count() != 7) errors.Add("registry.role-not-unique");

        foreach (var manifest in manifests.Where(x => x.Role is ModelOffRuntimeRoleV1.Execution or ModelOffRuntimeRoleV1.Recovery))
        {
            if (manifest.MutationBoundary is null ||
                !manifest.MutationBoundary.ExternalMutation ||
                !string.Equals(manifest.MutationBoundary.Environment, "Testnet", StringComparison.Ordinal) ||
                !manifest.MutationBoundary.RequiredChain.SequenceEqual(MutationChain, StringComparer.Ordinal) ||
                !manifest.MutationBoundary.TimeOfUseRevalidation)
                errors.Add($"registry.{manifest.Role}.mutation-routing-invalid");
        }

        if (errors.Count != 0)
            return new(false, null, errors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

        var roles = manifests.OrderBy(x => x.Role).ToArray();
        var capabilities = CapabilityMatrix.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        var bytes = Serialize(roles, capabilities);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new(true, new(ModelOffRoleRuntimeRegistryV1.Schema, "Testnet", false, roles, capabilities, bytes, hash), []);
    }

    private static ModelOffRoleRuntimeRegistryResultV1 Invalid(string error) => new(false, null, [error]);

    private static byte[] Serialize(
        IReadOnlyList<ModelOffRoleRuntimeManifestV1> roles,
        IReadOnlyList<ModelOffRegistryCapabilityV1> capabilities)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", ModelOffRoleRuntimeRegistryV1.Schema);
            writer.WriteString("environment", "Testnet");
            writer.WriteBoolean("mainnet_enabled", false);
            writer.WritePropertyName("roles");
            writer.WriteStartArray();
            foreach (var role in roles) WriteRole(writer, role);
            writer.WriteEndArray();
            writer.WritePropertyName("capabilities");
            writer.WriteStartArray();
            foreach (var capability in capabilities) WriteCapability(writer, capability);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteRole(Utf8JsonWriter writer, ModelOffRoleRuntimeManifestV1 value)
    {
        writer.WriteStartObject();
        writer.WriteString("schema_version", value.SchemaVersion);
        writer.WriteString("role", value.Role.ToString().ToLowerInvariant());
        WriteArray(writer, "triggers", value.Triggers.OrderBy(x => x.Id, StringComparer.Ordinal), x =>
        {
            writer.WriteStartObject(); writer.WriteString("id", x.Id); writer.WriteString("input_schema", x.InputSchema); writer.WriteEndObject();
        });
        WriteStrings(writer, "states", value.States.OrderBy(x => x).Select(Name));
        writer.WriteString("initial_state", Name(value.InitialState));
        WriteStrings(writer, "terminal_states", value.TerminalStates.OrderBy(x => x).Select(Name));
        writer.WriteString("input_schema", value.InputSchema);
        writer.WriteString("output_schema", value.OutputSchema);
        WriteArray(writer, "commands", value.Commands.OrderBy(x => x.Id, StringComparer.Ordinal), x =>
        {
            writer.WriteStartObject(); writer.WriteString("id", x.Id); writer.WriteString("handler", x.Handler);
            writer.WriteString("input_schema", x.InputSchema); writer.WriteString("output_schema", x.OutputSchema);
            writer.WriteNumber("maximum_items", x.MaximumItems); writer.WriteNumber("timeout_milliseconds", x.TimeoutMilliseconds);
            WriteStrings(writer, "allowed_states", x.AllowedStates.OrderBy(y => y).Select(Name));
            writer.WriteString("success_state", Name(x.SuccessState)); writer.WriteString("failure_state", Name(x.FailureState)); writer.WriteEndObject();
        });
        WriteArray(writer, "memory_queries", value.MemoryQueries.OrderBy(x => x.Id, StringComparer.Ordinal), x =>
        {
            writer.WriteStartObject(); writer.WriteString("id", x.Id); writer.WriteString("schema", x.Schema);
            writer.WriteNumber("limit", x.Limit); writer.WriteNumber("maximum_age_ticks", x.MaximumAge.Ticks);
            writer.WriteBoolean("canonical_only", x.CanonicalOnly); writer.WriteEndObject();
        });
        WriteStrings(writer, "permissions", value.Permissions.OrderBy(x => x).Select(Name));
        writer.WriteString("maximum_authority", Name(value.MaximumAuthority));
        writer.WritePropertyName("recovery"); writer.WriteStartObject(); writer.WriteNumber("retry_limit", value.Recovery.RetryLimit);
        writer.WriteString("resume_from", value.Recovery.ResumeFrom); writer.WriteBoolean("fail_closed", value.Recovery.FailClosed);
        writer.WriteBoolean("emergency_stop", value.Recovery.EmergencyStop); writer.WriteEndObject();
        writer.WritePropertyName("audit"); writer.WriteStartObject(); writer.WriteString("schema", value.Audit.Schema);
        writer.WriteBoolean("record_input_hash", value.Audit.RecordInputHash); writer.WriteBoolean("record_output_hash", value.Audit.RecordOutputHash);
        writer.WriteBoolean("record_state_transitions", value.Audit.RecordStateTransitions); writer.WriteBoolean("append_only", value.Audit.AppendOnly); writer.WriteEndObject();
        writer.WritePropertyName("model_policy"); writer.WriteStartObject(); writer.WriteBoolean("required_for_routine_work", value.ModelPolicy.RequiredForRoutineWork);
        writer.WriteBoolean("attachment_authoritative", value.ModelPolicy.AttachmentAuthoritative); writer.WriteBoolean("attachment_used_for_decision", value.ModelPolicy.AttachmentUsedForDecision);
        writer.WriteString("unknown_work_outcome", value.ModelPolicy.UnknownWorkOutcome); writer.WriteEndObject();
        WriteArray(writer, "model_off_tests", value.ModelOffTests.OrderBy(x => x.Id, StringComparer.Ordinal), x =>
        {
            writer.WriteStartObject(); writer.WriteString("id", x.Id); writer.WriteString("fixture", x.Fixture); writer.WriteEndObject();
        });
        writer.WritePropertyName("mutation_boundary"); writer.WriteStartObject(); writer.WriteBoolean("external_mutation", value.MutationBoundary.ExternalMutation);
        writer.WriteString("environment", value.MutationBoundary.Environment); WriteStrings(writer, "required_chain", value.MutationBoundary.RequiredChain);
        writer.WriteBoolean("time_of_use_revalidation", value.MutationBoundary.TimeOfUseRevalidation); writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteCapability(Utf8JsonWriter writer, ModelOffRegistryCapabilityV1 value)
    {
        writer.WriteStartObject(); writer.WriteString("id", value.Id);
        WriteStrings(writer, "roles", value.Roles.OrderBy(x => x).Select(x => x.ToString().ToLowerInvariant()));
        writer.WriteString("maturity", value.Maturity); writer.WriteBoolean("implemented", value.Implemented);
        writer.WriteBoolean("accepted", value.Accepted); writer.WriteString("lifecycle", value.Lifecycle);
        if (value.AcceptanceScope is null) writer.WriteNull("acceptance_scope"); else writer.WriteString("acceptance_scope", value.AcceptanceScope);
        writer.WriteEndObject();
    }

    private static void WriteArray<T>(Utf8JsonWriter writer, string name, IEnumerable<T> values, Action<T> write)
    {
        writer.WritePropertyName(name); writer.WriteStartArray(); foreach (var value in values) write(value); writer.WriteEndArray();
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values) =>
        WriteArray(writer, name, values, writer.WriteStringValue);

    private static string Name<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();
    private static ModelOffRuntimeRoleV1[] AllRoles() => Enum.GetValues<ModelOffRuntimeRoleV1>();
    private static ModelOffRegistryCapabilityV1 Capability(string id, ModelOffRuntimeRoleV1[] roles, string maturity, bool implemented = false, bool accepted = false, string lifecycle = "current", string? scope = null) =>
        new(id, roles, maturity, implemented, accepted, lifecycle, scope);
}
