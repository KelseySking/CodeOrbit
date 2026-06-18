using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CodeOrbit.Contracts;
using CodeOrbit.Core.Services;

namespace CodeOrbit.Hub;

public sealed class CodeOrbitRealtimeHub
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<Guid, WebSocket> _sockets = new();
    private readonly EventLogger? _logger;

    public CodeOrbitRealtimeHub(EventLogger? logger = null)
    {
        _logger = logger;
    }

    public async Task AcceptAsync(WebSocket socket, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _sockets[id] = socket;
        _logger?.Write("CodeOrbitRealtimeHub", "client-connected", new Dictionary<string, string?>
        {
            ["clientId"] = id.ToString(),
            ["clientCount"] = _sockets.Count.ToString()
        });
        var buffer = new byte[1024];

        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        finally
        {
            _sockets.TryRemove(id, out _);
            _logger?.Write("CodeOrbitRealtimeHub", "client-disconnected", new Dictionary<string, string?>
            {
                ["clientId"] = id.ToString(),
                ["clientCount"] = _sockets.Count.ToString()
            });
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None);
            socket.Dispose();
        }
    }

    public Task PublishAsync(string type, object? data, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new HubEventDto(type, DateTimeOffset.UtcNow, data),
            JsonOptions);
        _logger?.Write("CodeOrbitRealtimeHub", "publish", new Dictionary<string, string?>
        {
            ["type"] = type,
            ["clientCount"] = _sockets.Count.ToString()
        });
        return BroadcastAsync(payload, ct);
    }

    private async Task BroadcastAsync(byte[] payload, CancellationToken ct)
    {
        foreach (var (id, socket) in _sockets.ToArray())
        {
            if (socket.State != WebSocketState.Open)
            {
                _sockets.TryRemove(id, out _);
                continue;
            }

            try
            {
                using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                sendCts.CancelAfter(SendTimeout);
                await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, sendCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _sockets.TryRemove(id, out _);
                TryAbort(socket);
            }
            catch
            {
                _sockets.TryRemove(id, out _);
                TryAbort(socket);
            }
        }
    }

    private static void TryAbort(WebSocket socket)
    {
        try
        {
            socket.Abort();
        }
        catch
        {
        }
    }
}
