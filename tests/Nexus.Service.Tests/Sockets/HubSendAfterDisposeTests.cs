using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.Sockets;

/// <summary>
/// A send in flight while the receive loop tears the client down must finish
/// without the write-lock release throwing; broadcasts are fire-and-forget, so
/// a throw there surfaces as an unobserved task exception.
/// </summary>
public sealed class HubSendAfterDisposeTests
{
    [Fact]
    public async Task WebSocketHub_BroadcastCompletesWhenClientDisposedMidSend()
    {
        var hub = new WebSocketHub();
        using var cts = new CancellationTokenSource();
        var socket = new GatedSocket();
        var loop = hub.HandleClientAsync(socket, null, cts.Token);
        await WaitFor(() => hub.ClientCount == 1);

        var broadcast = hub.BroadcastBinaryAsync(new byte[] { 1 });
        await socket.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        socket.EndReceive();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
        socket.ReleaseSend();

        await broadcast.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MultiplexHub_BroadcastCompletesWhenClientDisposedMidSend()
    {
        var hub = new MultiplexHub();
        using var cts = new CancellationTokenSource();
        var socket = new GatedSocket();
        var loop = hub.HandleClientAsync(socket, "sess", MultiplexHub.ClientTransport.Lan, cts.Token);
        await WaitFor(() => hub.ClientCount == 1);

        socket.InjectSubscribe("t");
        await WaitFor(() => socket.SubscribeConsumed);

        var broadcast = hub.BroadcastTopicAsync("t", new byte[] { 1 });
        await socket.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        socket.EndReceive();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
        socket.ReleaseSend();

        await broadcast.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitFor(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition never met");
            await Task.Delay(10);
        }
    }

    private sealed class GatedSocket : WebSocket
    {
        private readonly TaskCompletionSource _sendGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _end = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _pending;
        private WebSocketState _state = WebSocketState.Open;

        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SubscribeConsumed { get; private set; }

        public void ReleaseSend() => _sendGate.TrySetResult();
        public void EndReceive() => _end.TrySetResult();
        public void InjectSubscribe(string topic) => _pending = "{\"sub\":[\"" + topic + "\"]}";

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            while (true)
            {
                if (_pending is { } msg)
                {
                    _pending = null;
                    var bytes = System.Text.Encoding.UTF8.GetBytes(msg);
                    bytes.CopyTo(buffer.Array!, buffer.Offset);
                    SubscribeConsumed = true;
                    return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
                }

                if (_end.Task.IsCompleted)
                {
                    _state = WebSocketState.CloseReceived;
                    return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
                }

                await Task.Delay(5, CancellationToken.None);
            }
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken)
        {
            SendStarted.TrySetResult();
            return _sendGate.Task;
        }

        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() { }
    }
}
