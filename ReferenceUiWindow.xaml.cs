using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using 币安量化机器人.Services;

namespace WpeAgent
{
    public partial class ReferenceUiWindow : Window
    {
        private readonly Func<string> _stateJson;
        private readonly Action _openSetup;

        public ReferenceUiWindow(Func<string> stateJson, Action? openSetup = null)
        {
            _stateJson = stateJson;
            _openSetup = openSetup ?? (() => { });
            InitializeComponent();
            Loaded += async (_, _) => await InitializeAsync();
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

            await ReferenceBrowser.EnsureCoreWebView2Async();
            ReferenceBrowser.CoreWebView2.WebMessageReceived += async (_, e) =>
            {
                try
                {
                    if (!TryGetHostCommand(e.WebMessageAsJson, out var command)) return;
                    if (command == "open-settings") Dispatcher.Invoke(_openSetup);
                    else if (command == "agent-start" && IsTrustedRuntimeJson(_stateJson(), requireAuthority: true))
                        AutoTradingAgent.StartDefault();
                    else if (command == "agent-stop" && IsTrustedRuntimeJson(_stateJson(), requireAuthority: true))
                        await AutoTradingAgent.StopAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"WPE WebView bridge message rejected: {ex.GetType().Name}");
                }
            };
            ReferenceBrowser.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "wpe-reference.local", root, CoreWebView2HostResourceAccessKind.Allow);
            ReferenceBrowser.CoreWebView2.Navigate("https://wpe-reference.local/index.html");
            ReferenceBrowser.NavigationCompleted += (_, _) => PushRuntimeState();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) => PushRuntimeState();
            timer.Start();
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
                return command is "open-settings" or "agent-start" or "agent-stop";
            }
            catch (System.Text.Json.JsonException) { return false; }
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
    }
}
