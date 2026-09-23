using System.Diagnostics;
using System.Net;
using System.Text;
using ModelContextProtocol.Authentication;

namespace WpeAgent.ExchangeMcp;

internal static class BrowserOAuthAuthorization
{
    public const int DefaultPort = 11791;

    public static ClientOAuthOptions Create(
        bool interactive,
        string cacheKey,
        int port=DefaultPort)
    {
        var metadataValue=Environment.GetEnvironmentVariable("WPE_BINANCE_MCP_CLIENT_METADATA_URI");
        if(!Uri.TryCreate(metadataValue,UriKind.Absolute,out var metadataUri)||
           metadataUri.Scheme!=Uri.UriSchemeHttps)
            throw new InvalidOperationException(
                "Binance MCP requires an approved HTTPS OAuth Client ID Metadata Document. " +
                "Configure WPE_BINANCE_MCP_CLIENT_METADATA_URI with the Binance-approved client metadata URL.");

        var configuredRedirect=Environment.GetEnvironmentVariable("WPE_BINANCE_MCP_REDIRECT_URI");
        var redirectUri=!string.IsNullOrWhiteSpace(configuredRedirect)
            ?new Uri(configuredRedirect,UriKind.Absolute)
            :new Uri($"http://127.0.0.1:{port}/callback",UriKind.Absolute);

        return new ClientOAuthOptions
        {
            ClientMetadataDocumentUri=metadataUri,
            RedirectUri=redirectUri,
            TokenCache=OperatingSystem.IsWindows()
                ?new ProtectedTokenCache(cacheKey)
                :throw new PlatformNotSupportedException("Binance MCP token persistence requires Windows DPAPI."),
            AuthorizationCallbackHandler=(context,ct)=>
                HandleAuthorizationAsync(context,interactive,ct)
        };
    }

    private static async Task<AuthorizationResult?> HandleAuthorizationAsync(
        AuthorizationCallbackContext context,
        bool interactive,
        CancellationToken ct)
    {
        if(!interactive)
            throw new InvalidOperationException(
                "Binance MCP authorization is required. Re-run the sidecar with --authorize.");

        var prefix=$"{context.RedirectUri.Scheme}://{context.RedirectUri.Host}:{context.RedirectUri.Port}/";
        using var listener=new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        try
        {
            try
            {
                Process.Start(new ProcessStartInfo(context.AuthorizationUri.ToString())
                {
                    UseShellExecute=true
                });
            }
            catch(Exception ex)
            {
                Console.Error.WriteLine($"Open this authorization URL in a browser: {context.AuthorizationUri}");
                Console.Error.WriteLine($"Browser launch failed: {ex.Message}");
            }

            var request=await listener.GetContextAsync().WaitAsync(ct);
            var query=ParseQuery(request.Request.Url?.Query??string.Empty);

            if(query.TryGetValue("error",out var error))
                throw new InvalidOperationException(
                    $"Binance MCP authorization failed: {error} {query.GetValueOrDefault("error_description")}".Trim());

            var code=query.GetValueOrDefault("code");
            if(string.IsNullOrWhiteSpace(code))
                throw new InvalidOperationException("Binance MCP authorization callback did not include a code.");

            var html="<html><body><h2>WPE authorization received.</h2><p>You can close this window.</p></body></html>";
            var bytes=Encoding.UTF8.GetBytes(html);
            request.Response.StatusCode=200;
            request.Response.ContentType="text/html; charset=utf-8";
            request.Response.ContentLength64=bytes.Length;
            await request.Response.OutputStream.WriteAsync(bytes,ct);
            request.Response.Close();

            return new AuthorizationResult
            {
                Code=query.GetValueOrDefault("code"),
                State=query.GetValueOrDefault("state"),
                Iss=query.GetValueOrDefault("iss")
            };
        }
        finally
        {
            listener.Stop();
        }
    }

    private static Dictionary<string,string> ParseQuery(string query)
    {
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var segment in query.TrimStart('?').Split('&',StringSplitOptions.RemoveEmptyEntries))
        {
            var parts=segment.Split('=',2);
            var key=Uri.UnescapeDataString(parts[0].Replace('+',' '));
            var value=parts.Length==2
                ?Uri.UnescapeDataString(parts[1].Replace('+',' '))
                :string.Empty;
            if(key.Length>0)result[key]=value;
        }
        return result;
    }
}
