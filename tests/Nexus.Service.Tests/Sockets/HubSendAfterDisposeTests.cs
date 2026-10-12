using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
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
        await socket.SecondReceive.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var broadcast = hub.BroadcastTopicAsync("t", new byte[] { 1 });
        await socket.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        socket.EndReceive();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
        socket.ReleaseSend();

        await broadcast.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WebSocketHub_QueuedSenderCompletesWhenClientDisposed()
    {
        var hub = new WebSocketHub();
        using var cts = new CancellationTokenSource();
        var socket = new GatedSocket();
        var loop = hub.HandleClientAsync(socket, null, cts.Token);
        await WaitFor(() => hub.ClientCount == 1);

        var first = hub.BroadcastBinaryAsync(new byte[] { 1 });
        await socket.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = hub.BroadcastBinaryAsync(new byte[] { 2 });

        socket.EndReceive();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));

        // The first send is still blocked; the queued one must not wait on a disposed lock forever.
        await queued.WaitAsync(TimeSpan.FromSeconds(5));
        socket.ReleaseSend();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MultiplexHub_QueuedSenderCompletesWhenClientDisposed()
    {
        var hub = new MultiplexHub();
        using var cts = new CancellationTokenSource();
        var socket = new GatedSocket();
        var loop = hub.HandleClientAsync(socket, "sess", MultiplexHub.ClientTransport.Lan, cts.Token);
        await WaitFor(() => hub.ClientCount == 1);
        socket.InjectSubscribe("t");
        await socket.SecondReceive.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var first = hub.BroadcastTopicAsync("t", new byte[] { 1 });
        await socket.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = hub.BroadcastTopicAsync("t", new byte[] { 2 });

        socket.EndReceive();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));

        await queued.WaitAsync(TimeSpan.FromSeconds(5));
        socket.ReleaseSend();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
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
        private readonly Channel<string?> _incoming = Channel.CreateUnbounded<string?>();
        private WebSocketState _state = WebSocketState.Open;
        private int _receives;

        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondReceive { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseSend() => _sendGate.TrySetResult();
        public void EndReceive() => _incoming.Writer.TryWrite(null);
        public void InjectSubscribe(string topic) => _incoming.Writer.TryWrite("{\"sub\":[\"" + topic + "\"]}");

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            // The hub handles a message fully before asking for the next one.
            if (Interlocked.Increment(ref _receives) == 2)
            {
                SecondReceive.TrySetResult();
            }

            var msg = await _incoming.Reader.ReadAsync(cancellationToken);
            if (msg is null)
            {
                _state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(msg);
            bytes.CopyTo(buffer.Array!, buffer.Offset);
            return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
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
