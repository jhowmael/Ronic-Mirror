using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace RonicMirror;

/// <summary>
/// Conecta o app ao core-server como "fonte" (ws://127.0.0.1:PORT/source).
/// Envia JPEG (binário) e JSON (texto); recebe eventos de controle (texto).
/// </summary>
internal sealed class SourceClient : IDisposable
{
    public event Action<JsonElement>? Message;
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _ws;
    private int _port;
    private bool _started;

    public void Connect(int port)
    {
        _port = port;
        if (_started) return;
        _started = true;
        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/source"), ct);
                _ws = ws;
                ConnectionChanged?.Invoke(true);
                await SendJsonAsync(new { type = "hello", role = "source", platform = "windows", version = AppInfo.Version });
                await ReceiveLoopAsync(ws, ct);
            }
            catch { /* servidor ainda subindo ou caiu: tenta de novo em 1 s */ }
            finally
            {
                bool wasOpen = ReferenceEquals(_ws, ws);
                _ws = null;
                ws.Dispose();
                if (wasOpen) ConnectionChanged?.Invoke(false);
            }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            if (result.MessageType == WebSocketMessageType.Text)
            {
                try
                {
                    using var doc = JsonDocument.Parse(message.ToArray());
                    Message?.Invoke(doc.RootElement.Clone());
                }
                catch (JsonException) { }
            }
            message.SetLength(0);
        }
    }

    /// <summary>Envia um frame JPEG. Bloqueia até concluir; descarta se outra mensagem estiver sendo enviada.</summary>
    public bool TrySendFrame(byte[] jpeg)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open || !_sendLock.Wait(0)) return false;
        try
        {
            ws.SendAsync(jpeg, WebSocketMessageType.Binary, true, _cts.Token).GetAwaiter().GetResult();
            return true;
        }
        catch { return false; }
        finally { _sendLock.Release(); }
    }

    public async Task SendJsonAsync(object payload)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await _sendLock.WaitAsync();
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token); }
        catch { }
        finally { _sendLock.Release(); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _ws?.Abort(); } catch { }
    }
}
