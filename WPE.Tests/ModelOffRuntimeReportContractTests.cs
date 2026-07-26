using WpeAgent.ModelOff;

namespace WPE.Tests;

public sealed class ModelOffRuntimeReportContractTests
{
    private static readonly DateTimeOffset GeneratedAt=new(2026,7,23,15,0,0,TimeSpan.Zero);

    [Fact]
    public void GoldenReportHasStableBytesHashAndOrder()
    {
        var first=RuntimeReportSerializerV1.Serialize(ReadyReport());
        var second=RuntimeReportSerializerV1.Serialize(ReadyReport(reverse:true));
        Assert.Equal(first.Utf8Bytes,second.Utf8Bytes);Assert.Equal(first.Sha256,second.Sha256);
        Assert.Equal("c8e4046db4786c3113ddc3378e496c3e2fb1300cfc607738d01bec726a19232d",first.Sha256);
        Assert.Contains("\"schema\":\"wpe.runtime-report/1.0\"",first.Json);Assert.Contains("\"status\":\"ready\"",first.Json);
        Assert.True(first.Json.IndexOf("\"capability\":\"market\"",StringComparison.Ordinal)<first.Json.IndexOf("\"capability\":\"audit\"",StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(RuntimeReportCapabilityStateV1.Unknown)]
    [InlineData(RuntimeReportCapabilityStateV1.Stale)]
    [InlineData(RuntimeReportCapabilityStateV1.Missing)]
    [InlineData(RuntimeReportCapabilityStateV1.Conflicting)]
    [InlineData(RuntimeReportCapabilityStateV1.Unsupported)]
    [InlineData(RuntimeReportCapabilityStateV1.Incomplete)]
    public void EveryRequiredOutputRejectionStateIsNotReady(RuntimeReportCapabilityStateV1 state)
    {
        var outputs=Outputs();outputs[0]=outputs[0] with{State=state,RefusalCodes=[]};
        var report=RuntimeReportBuilderV1.Create("cycle-report",GeneratedAt,outputs,Terminals());
        Assert.Equal(RuntimeReportStatusV1.NotReady,report.Status);Assert.Contains($"report.output-{state.ToString().ToLowerInvariant()}",report.ReasonCodes);
    }

    [Fact]
    public void MissingAndDuplicateRequiredOutputsFailClosed()
    {
        var missing=RuntimeReportBuilderV1.Create("cycle-report",GeneratedAt,Outputs().Skip(1).ToArray(),Terminals());
        Assert.Equal(RuntimeReportStatusV1.NotReady,missing.Status);Assert.Contains("report.required-output-missing",missing.ReasonCodes);
        var duplicate=RuntimeReportBuilderV1.Create("cycle-report",GeneratedAt,[..Outputs(),Outputs()[0]],Terminals());
        Assert.Equal(RuntimeReportCapabilityStateV1.Conflicting,duplicate.Outputs.Single(x=>x.Capability==RuntimeReportCapabilityV1.Market).State);
    }

    [Fact]
    public void MalformedOrFutureReadyMetadataBecomesIncomplete()
    {
        foreach(var malformed in new[]{
            Outputs()[0] with{OutputId=" "},Outputs()[0] with{CanonicalSha256="bad"},Outputs()[0] with{SourceId=null},
            Outputs()[0] with{AsOfUtc=null},Outputs()[0] with{AsOfUtc=GeneratedAt.AddTicks(1)},Outputs()[0] with{AsOfUtc=GeneratedAt.ToOffset(TimeSpan.FromHours(9))}})
        {
            var outputs=Outputs();outputs[0]=malformed;var report=RuntimeReportBuilderV1.Create("cycle-report",GeneratedAt,outputs,Terminals());
            Assert.Equal(RuntimeReportStatusV1.NotReady,report.Status);Assert.Equal(RuntimeReportCapabilityStateV1.Incomplete,report.Outputs[0].State);
        }
    }

    [Fact]
    public void MissingFailedOrConflictingTerminalReasonIsNotReady()
    {
        var missing=RuntimeReportBuilderV1.Create("cycle-report",GeneratedAt,Outputs(),Terminals().Skip(1).ToArray());
        Assert.Equal(RuntimeReportStatusV1.NotReady,missing.Status);Assert.Contains("report.terminal-missing",missing.ReasonCodes);
        var failed=Terminals();failed[0]=failed[0] with{State=RuntimeReportTerminalStateV1.Failed,ReasonCode="risk.failed"};
        Assert.Equal(RuntimeReportStatusV1.NotReady,RuntimeReportBuilderV1.Create("cycle-report",GeneratedAt,Outputs(),failed).Status);
        var conflicting=RuntimeReportBuilderV1.Create("cycle-report",GeneratedAt,Outputs(),[..Terminals(),Terminals()[0]]);
        Assert.Contains("report.terminal-conflicting",conflicting.ReasonCodes);
    }

    [Fact]
    public void ExplicitGeneratedAtAndDefinedEnumsAreRequired()
    {
        Assert.Throws<ArgumentException>(()=>RuntimeReportBuilderV1.Create("cycle-report",default,Outputs(),Terminals()));
        Assert.Throws<ArgumentException>(()=>RuntimeReportBuilderV1.Create("cycle-report",GeneratedAt.ToOffset(TimeSpan.FromHours(9)),Outputs(),Terminals()));
        var outputs=Outputs();outputs[0]=outputs[0] with{State=(RuntimeReportCapabilityStateV1)999};
        Assert.Throws<ArgumentException>(()=>RuntimeReportBuilderV1.Create("cycle-report",GeneratedAt,outputs,Terminals()));
    }

    [Fact]
    public void SerializerRecomputesForgedReadyStatusAndContainsNoModelFields()
    {
        var outputs=Outputs().Skip(1).ToArray();var forged=new RuntimeReportV1("cycle-report",GeneratedAt,RuntimeReportStatusV1.Ready,outputs,Terminals(),[]);
        var document=RuntimeReportSerializerV1.Serialize(forged);
        Assert.Contains("\"status\":\"not_ready\"",document.Json);Assert.Contains("report.required-output-missing",document.Json);
        Assert.DoesNotContain("model",document.Json,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("prompt",document.Json,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("response",document.Json,StringComparison.OrdinalIgnoreCase);
    }

    private static RuntimeReportV1 ReadyReport(bool reverse=false)
    {
        var outputs=Outputs();var terminals=Terminals();if(reverse){Array.Reverse(outputs);Array.Reverse(terminals);}
        return RuntimeReportBuilderV1.Create("cycle-report",GeneratedAt,outputs,terminals);
    }
    private static RuntimeReportOutputV1[] Outputs()=>Enum.GetValues<RuntimeReportCapabilityV1>().Select((capability,index)=>new RuntimeReportOutputV1(
        capability,RuntimeReportCapabilityStateV1.Ready,$"output-{capability.ToString().ToLowerInvariant()}",new string((char)('a'+index%6),64),$"source-{capability.ToString().ToLowerInvariant()}",GeneratedAt.AddMinutes(-index-1),[])).ToArray();
    private static RuntimeReportTerminalV1[] Terminals()=>Enum.GetValues<RuntimeReportTerminalStageV1>().Select(stage=>new RuntimeReportTerminalV1(stage,RuntimeReportTerminalStateV1.Succeeded,$"{stage.ToString().ToLowerInvariant()}.succeeded")).ToArray();
}
