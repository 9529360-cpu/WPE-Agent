using System.Text.Json;
using WpeAgent.ExchangeMcp;

var options=new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented=false
};

using var cts=new CancellationTokenSource();
Console.CancelKeyPress+=(sender,eventArgs)=>
{
    eventArgs.Cancel=true;
    cts.Cancel();
};

var provider=Arg(args,"--provider")??"okx";
var mode=Arg(args,"--mode")??(provider.Equals("okx",StringComparison.OrdinalIgnoreCase)?"public":"agentic");
var operation=Arg(args,"--operation")??"discover";
var allowWrite=args.Any(value=>value.Equals("--allow-write",StringComparison.OrdinalIgnoreCase));
var authorize=args.Any(value=>value.Equals("--authorize",StringComparison.OrdinalIgnoreCase));
var profile=OfficialExchangeMcpProfiles.Resolve(provider,mode,allowWrite);

try
{
    await using var session=new McpExchangeSession(profile,authorize);

    if(operation.Equals("bridge",StringComparison.OrdinalIgnoreCase))
    {
        await RunBridgeAsync(session,options,cts.Token);
        return;
    }

    if(operation.Equals("discover",StringComparison.OrdinalIgnoreCase))
    {
        var tools=await session.ListToolsAsync(cts.Token);
        WriteJson(new
        {
            ok=true,
            provider=profile.ProviderId,
            profile.DisplayName,
            profile.ReadOnly,
            profile.RequiresAuthorization,
            toolCount=tools.Count,
            tools
        },options);
        return;
    }

    if(operation.Equals("call",StringComparison.OrdinalIgnoreCase))
    {
        var tool=Arg(args,"--tool")??throw new ArgumentException("--tool is required for call.");
        var argumentsJson=ReadArgumentsJson(args);
        var arguments=ParseArguments(argumentsJson);
        var result=await session.CallToolAsync(tool,arguments,cts.Token);
        WriteJson(new
        {
            ok=!result.IsError,
            provider=profile.ProviderId,
            tool,
            result.IsError,
            result.Text,
            result.StructuredContent
        },options);
        Environment.ExitCode=result.IsError?2:0;
        return;
    }

    throw new ArgumentOutOfRangeException(nameof(operation),"Operation must be discover, call, or bridge.");
}
catch(OperationCanceledException) when(cts.IsCancellationRequested)
{
    Environment.ExitCode=130;
}
catch(Exception ex)
{
    WriteJson(new
    {
        ok=false,
        provider=profile.ProviderId,
        errorType=ex.GetType().Name,
        error=ex.Message
    },options);
    Environment.ExitCode=1;
}

static async Task RunBridgeAsync(
    McpExchangeSession session,
    JsonSerializerOptions options,
    CancellationToken ct)
{
    while(!ct.IsCancellationRequested)
    {
        var line=await Console.In.ReadLineAsync(ct);
        if(line is null)return;
        if(string.IsNullOrWhiteSpace(line))continue;

        BridgeRequest? request=null;
        try
        {
            request=JsonSerializer.Deserialize<BridgeRequest>(line,options)
                ??throw new InvalidOperationException("Bridge request is empty.");

            object payload=request.Method switch
            {
                "listTools"=>await session.ListToolsAsync(ct),
                "callTool"=>await session.CallToolAsync(
                    request.Tool??throw new InvalidOperationException("tool is required"),
                    request.Arguments??new Dictionary<string,object?>(),
                    ct),
                _=>throw new InvalidOperationException($"Unsupported bridge method '{request.Method}'.")
            };

            WriteJson(new BridgeResponse(request.Id,true,payload,null),options);
        }
        catch(Exception ex) when(ex is not OperationCanceledException)
        {
            WriteJson(new BridgeResponse(
                request?.Id??string.Empty,
                false,
                null,
                new BridgeError(ex.GetType().Name,ex.Message)),
                options);
        }
    }
}

static string ReadArgumentsJson(string[] values)
{
    if(Arg(values,"--arguments-base64") is {Length:>0} encoded)
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    return Arg(values,"--arguments-json")??"{}";
}

static Dictionary<string,object?> ParseArguments(string json)
{
    using var document=JsonDocument.Parse(json);
    if(document.RootElement.ValueKind!=JsonValueKind.Object)
        throw new ArgumentException("--arguments-json must be a JSON object.");

    var result=new Dictionary<string,object?>(StringComparer.Ordinal);
    foreach(var property in document.RootElement.EnumerateObject())
        result[property.Name]=property.Value.Clone();
    return result;
}

static string? Arg(string[] values,string name)
{
    for(var i=0;i<values.Length-1;i++)
        if(values[i].Equals(name,StringComparison.OrdinalIgnoreCase))
            return values[i+1];
    return null;
}

static void WriteJson(object value,JsonSerializerOptions options)
{
    Console.Out.WriteLine(JsonSerializer.Serialize(value,options));
    Console.Out.Flush();
}

internal sealed record BridgeRequest(
    string Id,
    string Method,
    string? Tool,
    Dictionary<string,object?>? Arguments);

internal sealed record BridgeError(string Type,string Message);
internal sealed record BridgeResponse(string Id,bool Ok,object? Result,BridgeError? Error);
