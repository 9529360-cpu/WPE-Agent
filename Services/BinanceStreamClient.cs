using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using 币安量化机器人.Models;

namespace 币安量化机器人.Services;

public class BinanceStreamClient : IAsyncDisposable
{
    private const string StreamEndpoint = "wss://fstream.binance.com/stream";
    private static readonly TimeSpan[] RetrySchedule =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60)
    };

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private IReadOnlyList<string> _currentSymbols = Array.Empty<string>();

    public event Action<MiniTickerUpdate>? MiniTickerReceived;
    public event Action<string>? ConnectionStatusChanged;
    private readonly Serilog.ILogger _logger = Serilog.Log.ForContext<BinanceStreamClient>();
    private readonly IRawStreamRecorder? _recorder;

    // Add optional recorder via constructor for testing / recording
    public BinanceStreamClient(IRawStreamRecorder? recorder = null)
    {
        _recorder = recorder;
    }

    public async Task ConnectMiniTickerAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default)
    {
        var requestedSymbols = symbols.Select(s => s.ToLowerInvariant()).Distinct().ToArray();
        if (requestedSymbols.Length == 0)
            throw new InvalidOperationException("必须至少订阅一个交易对");

        await StopInternalAsync().ConfigureAwait(false);

        _currentSymbols = requestedSymbols;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConnectionStatusChanged?.Invoke("正在连接 Binance 行情流...");
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];
        var builder = new StringBuilder();
        int attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                attempt = 0;

                if (_socket is null)
                    continue;

                builder.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new WebSocketException("服务端关闭了连接");

                    builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                var raw = builder.ToString();
                // record raw
                try { _recorder?.Record("miniTicker", raw); } catch { }
                HandleMessage(raw);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                ConnectionStatusChanged?.Invoke($"行情流异常：{ex.Message}");
                await StopSocketAsync().ConfigureAwait(false);

                attempt++;
                var delay = RetrySchedule[Math.Min(attempt - 1, RetrySchedule.Length - 1)];
                ConnectionStatusChanged?.Invoke($"{delay.TotalSeconds:F0}s 后尝试重连...");
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_socket is { State: WebSocketState.Open })
            return;

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_socket is { State: WebSocketState.Open })
                return;

            await StopSocketAsync().ConfigureAwait(false);

            var socket = new ClientWebSocket();
            var stream = string.Join('/', _currentSymbols.Select(s => $"{s}@miniTicker"));
            var uri = new Uri($"{StreamEndpoint}?streams={stream}");
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            _socket = socket;
            ConnectionStatusChanged?.Invoke("已连接 Binance 行情流");
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data))
                return;

            // Safely read properties using TryGetProperty to avoid exceptions when fields are missing
            if (!TryGetString(data, "s", out var symbol) || string.IsNullOrEmpty(symbol))
            {
                ConnectionStatusChanged?.Invoke("行情流异常：data.symbol 缺失");
                return;
            }

            var last = TryGetDouble(data, "c");
            var index = TryGetDouble(data, "p");
            var change = TryGetDouble(data, "P");
            var volume = TryGetDouble(data, "v");
            var high = TryGetDouble(data, "h");
            var low = TryGetDouble(data, "l");

            var update = new MiniTickerUpdate(symbol, last, index, change, volume, high, low);
            MiniTickerReceived?.Invoke(update);
        }
        catch (JsonException je)
        {
            _logger?.Error(je, "JSON parse error in stream message");
            ConnectionStatusChanged?.Invoke($"行情流异常（JSON 解析）: {je.Message}");
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Unhandled exception processing stream message");
            ConnectionStatusChanged?.Invoke($"行情流异常：{ex.Message}");
        }

        static bool TryGetString(JsonElement el, string name, out string? value)
        {
            value = null;
            if (!el.TryGetProperty(name, out var prop))
                return false;
            if (prop.ValueKind == JsonValueKind.String)
            {
                value = prop.GetString();
                return true;
            }
            // try convert number to string
            if (prop.ValueKind == JsonValueKind.Number)
            {
                value = prop.GetRawText();
                return true;
            }
            return false;
        }

        static double TryGetDouble(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var prop))
                return 0d;
            try
            {
                if (prop.ValueKind == JsonValueKind.Number)
                    return prop.GetDouble();
                if (prop.ValueKind == JsonValueKind.String)
                {
                    var s = prop.GetString();
                    if (double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v))
                        return v;
                }
            }
            catch
            {
                // ignore parse errors and return 0
            }
            return 0d;
        }
    }

    // Expose a testable entry which records raw and processes message (keeps backward compatibility)
    public void ProcessRawMessage(string raw)
    {
        try { _recorder?.Record("miniTicker", raw); } catch { }
        HandleMessage(raw);
    }

    public async Task StopAsync()
    {
        await StopInternalAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task StopInternalAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        await StopSocketAsync().ConfigureAwait(false);
        _currentSymbols = Array.Empty<string>();
        ConnectionStatusChanged?.Invoke("已停止订阅");
    }

    private async Task StopSocketAsync()
    {
        if (_socket is null)
            return;

        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "stop", CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // ignore cleanup exceptions
        }
        finally
        {
            _socket.Dispose();
            _socket = null;
        }
    }
}
