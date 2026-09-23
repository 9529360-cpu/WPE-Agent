using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace 币安量化机器人.Services.Exchange.Mcp;

public sealed class ExchangeMcpBridgeClient : IExchangeMcpToolClient
{
    private static readonly JsonSerializerOptions JsonOptions=new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive=true
    };

    private readonly string _provider;
    private readonly string _mode;
    private readonly bool _allowWrite;
    private readonly bool _authorize;
    private readonly string _sidecarPath;
    private readonly IReadOnlyDictionary<string,string?> _environment;
    private readonly SemaphoreSlim _gate=new(1,1);
    private readonly object _diagnosticSync=new();
    private readonly Queue<string> _diagnostics=new();
    private Process? _process;
    private Task? _stderrPump;

    public ExchangeMcpBridgeClient(
        string provider,
        string mode,
        bool allowWrite=false,
        bool authorize=false,
        string? sidecarPath=null,
        IReadOnlyDictionary<string,string?>? environment=null)
    {
        _provider=Required(provider,nameof(provider)).ToLowerInvariant();
        _mode=Required(mode,nameof(mode)).ToLowerInvariant();
        _allowWrite=allowWrite;
        _authorize=authorize;
        _sidecarPath=sidecarPath??ExchangeMcpSidecarLocator.Resolve();
        _environment=environment??new Dictionary<string,string?>();
    }

    public string ProviderId=>$"{_provider}-mcp";
    public bool ReadOnly=>!_allowWrite;

    public async Task<IReadOnlyList<ExchangeMcpToolDescriptor>> ListToolsAsync(CancellationToken ct)
    {
        var result=await SendAsync("listTools",null,null,ct);
        return result.Deserialize<ExchangeMcpToolDescriptor[]>(JsonOptions)
            ??Array.Empty<ExchangeMcpToolDescriptor>();
    }

    public async Task<ExchangeMcpToolResult> CallToolAsync(
        string tool,
        IReadOnlyDictionary<string,object?> arguments,
        CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(tool))
            throw new ArgumentException("MCP tool name is required.",nameof(tool));

        var result=await SendAsync("callTool",tool,arguments,ct);
        return result.Deserialize<ExchangeMcpToolResult>(JsonOptions)
            ??throw new InvalidOperationException("Exchange MCP sidecar returned an empty tool result.");
    }

    private async Task<JsonElement> SendAsync(
        string method,
        string? tool,
        IReadOnlyDictionary<string,object?>? arguments,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureStarted();
            var process=_process??throw new InvalidOperationException("Exchange MCP sidecar did not start.");
            if(process.HasExited)
                throw Failure($"Exchange MCP sidecar exited before request with code {process.ExitCode}.");

            var id=Guid.NewGuid().ToString("N");
            var request=JsonSerializer.Serialize(new
            {
                id,
                method,
                tool,
                arguments
            },JsonOptions);

            await process.StandardInput.WriteLineAsync(request.AsMemory(),ct);
            await process.StandardInput.FlushAsync(ct);

            var line=await process.StandardOutput.ReadLineAsync(ct);
            if(string.IsNullOrWhiteSpace(line))
                throw Failure("Exchange MCP sidecar closed its response stream.");

            using var document=JsonDocument.Parse(line);
            var root=document.RootElement;
            var responseId=String(root,"id");
            if(!string.Equals(id,responseId,StringComparison.Ordinal))
                throw Failure($"Exchange MCP response id mismatch. expected={id} actual={responseId}");

            if(root.TryGetProperty("ok",out var ok)&&ok.ValueKind==JsonValueKind.True)
            {
                if(!root.TryGetProperty("result",out var result))
                    throw Failure("Exchange MCP response did not contain a result.");
                return result.Clone();
            }

            var error=root.TryGetProperty("error",out var errorElement)
                ?String(errorElement,"message")
                :string.Empty;
            throw Failure(string.IsNullOrWhiteSpace(error)
                ?"Exchange MCP request failed."
                :error);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureStarted()
    {
        if(_process is {HasExited:false})return;

        if(!File.Exists(_sidecarPath))
            throw new FileNotFoundException("WPE Exchange MCP sidecar was not found.",_sidecarPath);

        var start=new ProcessStartInfo
        {
            FileName=_sidecarPath.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)?"dotnet":_sidecarPath,
            UseShellExecute=false,
            RedirectStandardInput=true,
            RedirectStandardOutput=true,
            RedirectStandardError=true,
            CreateNoWindow=true
        };

        if(_sidecarPath.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(_sidecarPath);

        foreach(var item in _environment)
        {
            if(string.IsNullOrWhiteSpace(item.Value))start.Environment.Remove(item.Key);
            else start.Environment[item.Key]=item.Value;
        }

        start.ArgumentList.Add("--provider");
        start.ArgumentList.Add(_provider);
        start.ArgumentList.Add("--mode");
        start.ArgumentList.Add(_mode);
        start.ArgumentList.Add("--operation");
        start.ArgumentList.Add("bridge");
        if(_allowWrite)start.ArgumentList.Add("--allow-write");
        if(_authorize)start.ArgumentList.Add("--authorize");

        _process=Process.Start(start)
            ??throw new InvalidOperationException("Failed to start WPE Exchange MCP sidecar.");
        _stderrPump=PumpStderrAsync(_process);
    }

    private async Task PumpStderrAsync(Process process)
    {
        try
        {
            while(await process.StandardError.ReadLineAsync() is { } line)
            {
                lock(_diagnosticSync)
                {
                    _diagnostics.Enqueue(line);
                    while(_diagnostics.Count>40)_diagnostics.Dequeue();
                }
            }
        }
        catch
        {
            // Diagnostics must never break the trading process.
        }
    }

    private InvalidOperationException Failure(string message)
    {
        string suffix;
        lock(_diagnosticSync)
            suffix=_diagnostics.Count==0
                ?string.Empty
                :$" Sidecar diagnostics: {string.Join(" | ",_diagnostics.TakeLast(5))}";
        return new InvalidOperationException(message+suffix);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if(_process is null)return;

            try{_process.StandardInput.Close();}catch{}
            if(!_process.HasExited)
            {
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try{await _process.WaitForExitAsync(timeout.Token);}
                catch(OperationCanceledException)
                {
                    try{_process.Kill(entireProcessTree:true);}catch{}
                }
            }

            if(_stderrPump is not null)
            {
                try{await _stderrPump.WaitAsync(TimeSpan.FromSeconds(1));}catch{}
            }

            _process.Dispose();
            _process=null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private static string Required(string value,string name)=>
        !string.IsNullOrWhiteSpace(value)
            ?value.Trim()
            :throw new ArgumentException($"{name} is required.",name);

    private static string String(JsonElement element,string property)=>
        element.ValueKind==JsonValueKind.Object&&
        element.TryGetProperty(property,out var value)&&
        value.ValueKind==JsonValueKind.String
            ?value.GetString()??string.Empty
            :string.Empty;
}

public static class ExchangeMcpSidecarLocator
{
    public const string EnvironmentVariable="WPE_EXCHANGE_MCP_PATH";

    public static string Resolve()
    {
        var configured=Environment.GetEnvironmentVariable(EnvironmentVariable);
        if(!string.IsNullOrWhiteSpace(configured)&&File.Exists(configured))
            return Path.GetFullPath(configured);

        foreach(var candidate in AdjacentCandidates())
            if(File.Exists(candidate))return candidate;

        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        for(var depth=0;directory is not null&&depth<8;depth++,directory=directory.Parent)
        {
            var development=Path.Combine(
                directory.FullName,
                "WPE.ExchangeMcp",
                "bin",
                "Release",
                "net8.0",
                "WPE.ExchangeMcp.dll");
            if(File.Exists(development))return development;
        }

        throw new FileNotFoundException(
            $"WPE Exchange MCP sidecar is unavailable. Set {EnvironmentVariable} or place the sidecar beside WPE.");
    }

    private static IEnumerable<string> AdjacentCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory,"WPE.ExchangeMcp.exe");
        yield return Path.Combine(AppContext.BaseDirectory,"WPE.ExchangeMcp.dll");
        yield return Path.Combine(AppContext.BaseDirectory,"exchange-mcp","WPE.ExchangeMcp.exe");
        yield return Path.Combine(AppContext.BaseDirectory,"exchange-mcp","WPE.ExchangeMcp.dll");
    }
}
