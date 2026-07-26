using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WpeAgent.ModelOff;

[JsonConverter(typeof(JsonStringEnumConverter<RuntimeReportCapabilityV1>))]
public enum RuntimeReportCapabilityV1 { Market,Research,Strategy,Risk,Execution,Recovery,Audit }

[JsonConverter(typeof(JsonStringEnumConverter<RuntimeReportCapabilityStateV1>))]
public enum RuntimeReportCapabilityStateV1 { Ready,Unknown,Stale,Missing,Conflicting,Unsupported,Incomplete,Failed }

[JsonConverter(typeof(JsonStringEnumConverter<RuntimeReportTerminalStageV1>))]
public enum RuntimeReportTerminalStageV1 { Risk,Execution,Recovery,Audit }

[JsonConverter(typeof(JsonStringEnumConverter<RuntimeReportTerminalStateV1>))]
public enum RuntimeReportTerminalStateV1 { Succeeded,Blocked,Failed,Unknown }

[JsonConverter(typeof(JsonStringEnumConverter<RuntimeReportStatusV1>))]
public enum RuntimeReportStatusV1 { Ready,NotReady }

public sealed record RuntimeReportOutputV1(
    RuntimeReportCapabilityV1 Capability,
    RuntimeReportCapabilityStateV1 State,
    string? OutputId,
    string? CanonicalSha256,
    string? SourceId,
    DateTimeOffset? AsOfUtc,
    IReadOnlyList<string> RefusalCodes);

public sealed record RuntimeReportTerminalV1(
    RuntimeReportTerminalStageV1 Stage,
    RuntimeReportTerminalStateV1 State,
    string ReasonCode);

public sealed record RuntimeReportV1(
    string CycleId,
    DateTimeOffset GeneratedAtUtc,
    RuntimeReportStatusV1 Status,
    IReadOnlyList<RuntimeReportOutputV1> Outputs,
    IReadOnlyList<RuntimeReportTerminalV1> Terminals,
    IReadOnlyList<string> ReasonCodes)
{
    public const string Schema="wpe.runtime-report/1.0";
}

public sealed record RuntimeReportDocumentV1(byte[] Utf8Bytes,string Sha256)
{
    public string Json=>Encoding.UTF8.GetString(Utf8Bytes);
}

public static class RuntimeReportBuilderV1
{
    public static RuntimeReportV1 Create(string cycleId,DateTimeOffset generatedAtUtc,IReadOnlyList<RuntimeReportOutputV1> outputs,IReadOnlyList<RuntimeReportTerminalV1> terminals)
    {
        RequireToken(cycleId,nameof(cycleId));
        if(generatedAtUtc==default||generatedAtUtc.Offset!=TimeSpan.Zero)throw new ArgumentException("Runtime report generation time must be supplied explicitly in UTC.",nameof(generatedAtUtc));
        ArgumentNullException.ThrowIfNull(outputs);ArgumentNullException.ThrowIfNull(terminals);
        if(outputs.Any(x=>x is null)||terminals.Any(x=>x is null))throw new ArgumentException("Runtime report entries cannot be null.");

        var normalizedOutputs=Enum.GetValues<RuntimeReportCapabilityV1>().Select(capability=>NormalizeOutput(capability,outputs.Where(x=>x.Capability==capability).ToArray(),generatedAtUtc)).ToArray();
        var normalizedTerminals=Enum.GetValues<RuntimeReportTerminalStageV1>().Select(stage=>NormalizeTerminal(stage,terminals.Where(x=>x.Stage==stage).ToArray())).ToArray();
        if(outputs.Any(x=>!Enum.IsDefined(x.Capability)||!Enum.IsDefined(x.State))||terminals.Any(x=>!Enum.IsDefined(x.Stage)||!Enum.IsDefined(x.State)))
            throw new ArgumentException("Runtime report enum values must be defined.");
        var reasons=normalizedOutputs.Where(x=>x.State!=RuntimeReportCapabilityStateV1.Ready).SelectMany(x=>x.RefusalCodes)
            .Concat(normalizedTerminals.Where(x=>x.State!=RuntimeReportTerminalStateV1.Succeeded).Select(x=>x.ReasonCode))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var ready=normalizedOutputs.All(x=>x.State==RuntimeReportCapabilityStateV1.Ready)&&normalizedTerminals.All(x=>x.State==RuntimeReportTerminalStateV1.Succeeded);
        return new(cycleId,generatedAtUtc,ready?RuntimeReportStatusV1.Ready:RuntimeReportStatusV1.NotReady,normalizedOutputs,normalizedTerminals,reasons);
    }

    private static RuntimeReportOutputV1 NormalizeOutput(RuntimeReportCapabilityV1 capability,RuntimeReportOutputV1[] values,DateTimeOffset generatedAtUtc)
    {
        if(values.Length==0)return new(capability,RuntimeReportCapabilityStateV1.Missing,null,null,null,null,["report.required-output-missing"]);
        if(values.Length>1)return new(capability,RuntimeReportCapabilityStateV1.Conflicting,null,null,null,null,["report.required-output-conflicting"]);
        var value=values[0];var reasons=(value.RefusalCodes??[]).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var metadataValid=Token(value.OutputId)&&Sha256(value.CanonicalSha256)&&Token(value.SourceId)&&value.AsOfUtc is{Offset:var offset}&&offset==TimeSpan.Zero&&value.AsOfUtc<=generatedAtUtc;
        if(value.State==RuntimeReportCapabilityStateV1.Ready&&!metadataValid)
            return value with{State=RuntimeReportCapabilityStateV1.Incomplete,RefusalCodes=[..reasons,"report.required-output-incomplete"]};
        if(value.State!=RuntimeReportCapabilityStateV1.Ready&&reasons.Length==0)
            reasons=[$"report.output-{value.State.ToString().ToLowerInvariant()}"];
        return value with{RefusalCodes=reasons};
    }

    private static RuntimeReportTerminalV1 NormalizeTerminal(RuntimeReportTerminalStageV1 stage,RuntimeReportTerminalV1[] values)
    {
        if(values.Length==0)return new(stage,RuntimeReportTerminalStateV1.Unknown,"report.terminal-missing");
        if(values.Length>1)return new(stage,RuntimeReportTerminalStateV1.Blocked,"report.terminal-conflicting");
        var value=values[0];
        if(!Token(value.ReasonCode))return new(stage,RuntimeReportTerminalStateV1.Blocked,"report.terminal-reason-invalid");
        return value;
    }

    private static void RequireToken(string? value,string name){if(!Token(value))throw new ArgumentException("A stable runtime report identity is required.",name);}
    private static bool Token(string? value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=160&&value.All(x=>char.IsAsciiLetterOrDigit(x)||x is '-' or '_' or '.' or ':');
    private static bool Sha256(string? value)=>value is{Length:64}&&value.All(x=>x is>='0'and<='9'or>='a'and<='f');
}

public static class RuntimeReportSerializerV1
{
    public static RuntimeReportDocumentV1 Serialize(RuntimeReportV1 report)
    {
        ArgumentNullException.ThrowIfNull(report);
        report=RuntimeReportBuilderV1.Create(report.CycleId,report.GeneratedAtUtc,report.Outputs,report.Terminals);
        using var stream=new MemoryStream();
        using(var writer=new Utf8JsonWriter(stream,new JsonWriterOptions{Indented=false}))
        {
            writer.WriteStartObject();writer.WriteString("cycle_id",report.CycleId);writer.WriteString("generated_at_utc",Utc(report.GeneratedAtUtc));
            writer.WritePropertyName("outputs");writer.WriteStartArray();foreach(var output in report.Outputs.OrderBy(x=>x.Capability))WriteOutput(writer,output);writer.WriteEndArray();
            writer.WritePropertyName("reason_codes");WriteStrings(writer,report.ReasonCodes);writer.WriteString("schema",RuntimeReportV1.Schema);writer.WriteString("status",report.Status==RuntimeReportStatusV1.Ready?"ready":"not_ready");
            writer.WritePropertyName("terminals");writer.WriteStartArray();foreach(var terminal in report.Terminals.OrderBy(x=>x.Stage))WriteTerminal(writer,terminal);writer.WriteEndArray();writer.WriteEndObject();
        }
        var bytes=stream.ToArray();return new(bytes,Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static void WriteOutput(Utf8JsonWriter writer,RuntimeReportOutputV1 value)
    {
        writer.WriteStartObject();writer.WriteString("as_of_utc",value.AsOfUtc is null?null:Utc(value.AsOfUtc.Value));writer.WriteString("canonical_sha256",value.CanonicalSha256);
        writer.WriteString("capability",Token(value.Capability));writer.WriteString("output_id",value.OutputId);writer.WritePropertyName("refusal_codes");WriteStrings(writer,value.RefusalCodes);
        writer.WriteString("source_id",value.SourceId);writer.WriteString("state",Token(value.State));writer.WriteEndObject();
    }
    private static void WriteTerminal(Utf8JsonWriter writer,RuntimeReportTerminalV1 value){writer.WriteStartObject();writer.WriteString("reason_code",value.ReasonCode);writer.WriteString("stage",Token(value.Stage));writer.WriteString("state",Token(value.State));writer.WriteEndObject();}
    private static void WriteStrings(Utf8JsonWriter writer,IEnumerable<string> values){writer.WriteStartArray();foreach(var value in values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))writer.WriteStringValue(value);writer.WriteEndArray();}
    private static string Token<T>(T value)where T:struct,Enum=>value.ToString().ToLowerInvariant();
    private static string Utc(DateTimeOffset value)=>value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",CultureInfo.InvariantCulture);
}
