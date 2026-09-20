using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using WpeAgent.RuntimeContracts;
using 币安量化机器人.Services;

namespace WpeAgent
{
    public partial class ReferenceUiWindow : Window
    {
        internal const string TrustedWebViewHost = "wpe-reference.local";
        private readonly Func<string> _stateJson;
        private readonly Action _openSetup;
        private readonly Action _openNotificationSetup;
        private readonly Func<System.Threading.Tasks.Task<bool>> _startAgent;
        private readonly Func<string,HistoricalCollectionKindV1,string,System.Threading.Tasks.Task<string>>? _readHistoricalPage;
        private DispatcherTimer? _runtimeTimer;

        public ReferenceUiWindow(
            Func<string> stateJson,
            Action? openSetup = null,
            Action? openNotificationSetup = null,
            Func<System.Threading.Tasks.Task<bool>>? startAgent = null,
            Func<string,HistoricalCollectionKindV1,string,System.Threading.Tasks.Task<string>>? readHistoricalPage = null)
        {
            _stateJson = stateJson;
            _openSetup = openSetup ?? (() => { });
            _openNotificationSetup = openNotificationSetup ?? _openSetup;
            _startAgent = startAgent ?? (() => System.Threading.Tasks.Task.FromResult(false));
            _readHistoricalPage = readHistoricalPage;
            InitializeComponent();
            Loaded += async (_, _) => await InitializeAsync();
            Closed += (_, _) => StopRuntimeTimer();
        }

        private async System.Threading.Tasks.Task InitializeAsync()
        {
            var root = Path.Combine(AppContext.BaseDirectory, "WebUi", "out");
            if (!Directory.Exists(root))
            {
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = "Reference UI assets are not installed.",
                    Foreground = System.Windows.Media.Brushes.White,
                    Margin = new Thickness(32),
                    FontSize = 18
                };
                return;
            }

            var webViewDataDirectory = Path.Combine(AppDataPaths.RuntimeDirectory, "WebView2");
            Directory.CreateDirectory(webViewDataDirectory);
            var webViewEnvironment = await CoreWebView2Environment.CreateAsync(userDataFolder: webViewDataDirectory);
            await ReferenceBrowser.EnsureCoreWebView2Async(webViewEnvironment);
            ReferenceBrowser.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (!IsTrustedWebViewSource(e.Uri)) e.Cancel = true;
            };
            ReferenceBrowser.CoreWebView2.WebMessageReceived += async (_, e) =>
            {
                try
                {
                    if (!IsTrustedWebViewSource(e.Source)) return;
                    if (TryGetHostCommand(e.WebMessageAsJson, out var command))
                    {
                        if (command == "open-settings") Dispatcher.Invoke(_openSetup);
                        else if (command == "open-notification-settings") Dispatcher.Invoke(_openNotificationSetup);
                        else if (command == "agent-start")
                            await _startAgent();
                        else if (command == "agent-stop")
                            await AutoTradingAgent.StopAsync();
                        return;
                    }
                    if (_readHistoricalPage is not null && TryGetHistoricalPageRequest(e.WebMessageAsJson, out var request))
                    {
                        var responseJson=await _readHistoricalPage(request.RequestId,request.Kind,request.Cursor);
                        if (ReferenceBrowser.CoreWebView2 is not null)
                            await ReferenceBrowser.CoreWebView2.ExecuteScriptAsync($"window.dispatchEvent(new CustomEvent('wpe-history-page', {{detail: {responseJson}}}));");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"WPE WebView bridge message rejected: {ex.GetType().Name}");
                }
            };
            ReferenceBrowser.CoreWebView2.SetVirtualHostNameToFolderMapping(
                TrustedWebViewHost, root, CoreWebView2HostResourceAccessKind.Allow);
            ReferenceBrowser.CoreWebView2.Navigate($"https://{TrustedWebViewHost}/index.html");
            ReferenceBrowser.NavigationCompleted += (_, _) => PushRuntimeState();
            _runtimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _runtimeTimer.Tick += (_, _) => PushRuntimeState();
            _runtimeTimer.Start();
        }

        internal static bool IsTrustedWebViewSource(string? source)
        {
            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return false;
            return uri.Scheme == Uri.UriSchemeHttps &&
                uri.IsDefaultPort &&
                string.Equals(uri.Host, TrustedWebViewHost, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrEmpty(uri.UserInfo);
        }

        internal static bool TryGetHostCommand(string json, out string command)
        {
            command = string.Empty;
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("type", out var type) ||
                    type.ValueKind != System.Text.Json.JsonValueKind.String)
                    return false;
                command = type.GetString() ?? string.Empty;
                return command is "open-settings" or "open-notification-settings" or "agent-start" or "agent-stop";
            }
            catch (System.Text.Json.JsonException) { return false; }
        }

        internal sealed record HistoricalPageRequest(string RequestId,HistoricalCollectionKindV1 Kind,string Cursor);

        internal static bool TryGetHistoricalPageRequest(string json,out HistoricalPageRequest request)
        {
            request=new(string.Empty,HistoricalCollectionKindV1.Orders,string.Empty);
            try
            {
                using var document=System.Text.Json.JsonDocument.Parse(json);
                var root=document.RootElement;
                if(root.ValueKind!=System.Text.Json.JsonValueKind.Object)return false;
                var names=new HashSet<string>(StringComparer.Ordinal);
                foreach(var property in root.EnumerateObject())if(!names.Add(property.Name))return false;
                if(names.Count!=4||!names.SetEquals(["type","requestId","collection","cursor"]))return false;
                if(!root.TryGetProperty("type",out var type)||type.ValueKind!=System.Text.Json.JsonValueKind.String||type.GetString()!="history-page")return false;
                if(!root.TryGetProperty("requestId",out var requestIdValue)||requestIdValue.ValueKind!=System.Text.Json.JsonValueKind.String)return false;
                var requestId=requestIdValue.GetString()??string.Empty;
                if(requestId.Length is <1 or >64||requestId.Any(ch=>!((ch>='A'&&ch<='Z')||(ch>='a'&&ch<='z')||(ch>='0'&&ch<='9')||ch is '-' or '_')))return false;
                if(!root.TryGetProperty("cursor",out var cursorValue)||cursorValue.ValueKind!=System.Text.Json.JsonValueKind.String)return false;
                var cursor=cursorValue.GetString()??string.Empty;if(cursor.Length is <1 or >2048)return false;
                if(!root.TryGetProperty("collection",out var collectionValue)||collectionValue.ValueKind!=System.Text.Json.JsonValueKind.String)return false;
                var kind=collectionValue.GetString() switch
                {
                    "orders"=>HistoricalCollectionKindV1.Orders,
                    "equity"=>HistoricalCollectionKindV1.Equity,
                    "backtests"=>HistoricalCollectionKindV1.Backtests,
                    "skillCalls"=>HistoricalCollectionKindV1.SkillCalls,
                    "auditEvents"=>HistoricalCollectionKindV1.AuditEvents,
                    "postTradeReviews"=>HistoricalCollectionKindV1.PostTradeReviews,
                    "reconciliations"=>HistoricalCollectionKindV1.Reconciliations,
                    _=>(HistoricalCollectionKindV1?)null
                };
                if(kind is null)return false;
                request=new(requestId,kind.Value,cursor);
                return true;
            }
            catch(System.Text.Json.JsonException){return false;}
        }

        internal static bool IsTrustedRuntimeJson(string json, bool requireAuthority, DateTime? nowUtc = null)
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !root.TryGetProperty("contractVersion", out var version) || version.GetString() != "1.0" ||
                    !root.TryGetProperty("environment", out var environment) || environment.GetString() != "Testnet" ||
                    !root.TryGetProperty("generatedAtUtc", out var generated) || !generated.TryGetDateTime(out var generatedAt) ||
                    !root.TryGetProperty("sourceUpdatedAtUtc", out var source) || !source.TryGetDateTime(out var sourceAt) ||
                    !root.TryGetProperty("freshness", out var freshness) ||
                    !freshness.TryGetProperty("fresh", out var fresh) || !fresh.GetBoolean() ||
                    !freshness.TryGetProperty("staleAfterSeconds", out var staleAfter) || !staleAfter.TryGetDouble(out var threshold) || threshold <= 0 ||
                    !root.TryGetProperty("connectionStatus", out var connection) ||
                    !connection.TryGetProperty("state", out var connectionState) || connectionState.GetString() != "Available" ||
                    !connection.TryGetProperty("value", out var connectionValue) ||
                    !connectionValue.TryGetProperty("providerId", out var provider) || string.IsNullOrWhiteSpace(provider.GetString()) ||
                    !connectionValue.TryGetProperty("environment", out var providerEnvironment) || providerEnvironment.GetString() != "Testnet") return false;

                generatedAt = generatedAt.ToUniversalTime();
                sourceAt = sourceAt.ToUniversalTime();
                var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
                if (generatedAt > now.AddSeconds(5) || sourceAt > generatedAt || now - sourceAt > TimeSpan.FromSeconds(threshold)) return false;
                if (!requireAuthority) return true;
                return connectionValue.TryGetProperty("ready", out var ready) && ready.GetBoolean() &&
                    connectionValue.TryGetProperty("exchangeConnected", out var connected) && connected.GetBoolean() &&
                    connectionValue.TryGetProperty("tradePermission", out var permission) && permission.ValueKind == System.Text.Json.JsonValueKind.True;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
            {
                return false;
            }
        }

        private void PushRuntimeState()
        {
            if (ReferenceBrowser.CoreWebView2 is null) return;
            var json = _stateJson();
            _ = ReferenceBrowser.CoreWebView2.ExecuteScriptAsync(
                $"window.dispatchEvent(new CustomEvent('wpe-runtime', {{detail: {json}}}));");
        }

        private void StopRuntimeTimer()
        {
            if (_runtimeTimer is null) return;
            _runtimeTimer.Stop();
            _runtimeTimer = null;
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Maximize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}