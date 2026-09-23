using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Authentication;

namespace WpeAgent.ExchangeMcp;

[SupportedOSPlatform("windows")]
internal sealed class ProtectedTokenCache : ITokenCache
{
    private static readonly JsonSerializerOptions JsonOptions=new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly byte[] _entropy;
    private readonly SemaphoreSlim _gate=new(1,1);

    public ProtectedTokenCache(string cacheKey)
    {
        if(!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WPE MCP persistent OAuth tokens require Windows DPAPI.");

        var directory=Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WPE Agent",
            "Mcp");
        Directory.CreateDirectory(directory);
        _path=Path.Combine(directory,$"{Safe(cacheKey)}.tokens.bin");
        _entropy=SHA256.HashData(Encoding.UTF8.GetBytes("WPE.ExchangeMcp|"+cacheKey));
    }

    public async ValueTask StoreTokensAsync(TokenContainer tokens,CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var json=JsonSerializer.SerializeToUtf8Bytes(tokens,JsonOptions);
            var temporary=_path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                var protectedBytes=ProtectedData.Protect(json,_entropy,DataProtectionScope.CurrentUser);
                await File.WriteAllBytesAsync(temporary,protectedBytes,cancellationToken);
                File.Move(temporary,_path,true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(json);
                if(File.Exists(temporary))File.Delete(temporary);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if(!File.Exists(_path))return null;
            try
            {
                var protectedBytes=await File.ReadAllBytesAsync(_path,cancellationToken);
                var json=ProtectedData.Unprotect(protectedBytes,_entropy,DataProtectionScope.CurrentUser);
                try
                {
                    return JsonSerializer.Deserialize<TokenContainer>(json,JsonOptions);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(json);
                }
            }
            catch(Exception ex) when(ex is CryptographicException or JsonException or IOException)
            {
                throw new InvalidOperationException(
                    "The Binance MCP authorization cache could not be read. Re-authorize the connection.",
                    ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Safe(string value)
    {
        var chars=value.Select(ch=>char.IsLetterOrDigit(ch)||ch is '-' or '_'?ch:'-').ToArray();
        return new string(chars).Trim('-');
    }
}
