using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace Fw.Rt.Net;

public sealed class TcpNetTransport : INetTransport
{
    private const byte ChannelCount = 8;
    private const int FrameHeaderSize = 6;
    private const int HandshakeHeaderSize = 6;
    private const int MaxConnectionKeyBytes = 1024;
    private const int MaxPayloadBytes = 4 * 1024 * 1024;
    private static readonly byte[] HandshakeMagic = [0x46, 0x57, 0x54, 0x31];

    private readonly object _lifecycleLock = new();
    private readonly ConcurrentDictionary<string, Peer> _peers = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<NetReceivedMessage> _received = new();
    private CancellationTokenSource? _cancellation;
    private TcpListener? _listener;
    private Task? _lifecycleTask;
    private Peer? _serverPeer;
    private NetTransportOptions _options = new();
    private NetTransportRole _role;
    private IPEndPoint? _serverEndPoint;
    private long _sentMessages;
    private long _sentBytes;
    private long _receivedMessages;
    private long _receivedBytes;
    private long _sentFrames;
    private long _sentWireBytes;
    private long _receivedFrames;
    private long _receivedWireBytes;
    private long _sendErrors;
    private long _receiveErrors;
    private long _queueDrops;
    private int _queuedMessages;

    public NetTransportRole Role => _role;
    public bool IsRunning => _cancellation is { IsCancellationRequested: false };
    public bool IsConnected => _role switch
    {
        NetTransportRole.Client => _serverPeer?.IsOpen == true,
        NetTransportRole.Server => IsRunning,
        _ => false,
    };
    public IPEndPoint? ServerEndPoint => _serverEndPoint;

    public bool StartServer(int port, NetTransportOptions? options = null)
    {
        if (port < 0 || port > ushort.MaxValue)
        {
            return false;
        }
        lock (_lifecycleLock)
        {
            CloseLocked();
            _options = ValidateOptions(options);
            _role = NetTransportRole.Server;
            _cancellation = new CancellationTokenSource();
            try
            {
                _listener = CreateListener(port);
                _listener.Start(_options.MaxConnections);
                _lifecycleTask = Task.Run(() => AcceptLoopAsync(_cancellation.Token));
                return true;
            }
            catch (Exception error) when (IsTransportException(error))
            {
                Interlocked.Increment(ref _receiveErrors);
                CloseLocked();
                return false;
            }
        }
    }

    public bool StartClient(string host, int port, NetTransportOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(host) || port <= 0 || port > ushort.MaxValue)
        {
            return false;
        }
        lock (_lifecycleLock)
        {
            CloseLocked();
            _options = ValidateOptions(options);
            _role = NetTransportRole.Client;
            try
            {
                _serverEndPoint = ResolveEndPoint(host, port);
                _cancellation = new CancellationTokenSource();
                _lifecycleTask = Task.Run(
                    () => ConnectLoopAsync(_serverEndPoint, _cancellation.Token)
                );
                return true;
            }
            catch (Exception error) when (IsTransportException(error))
            {
                Interlocked.Increment(ref _receiveErrors);
                CloseLocked();
                return false;
            }
        }
    }

    public bool StartUnconnected(NetTransportOptions? options = null)
    {
        _ = options;
        Close();
        return false;
    }

    public bool Send(
        IPEndPoint remoteEndPoint,
        ReadOnlySpan<byte> payload,
        byte channel,
        NetDelivery delivery
    )
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        return _peers.TryGetValue(EndPointKey(remoteEndPoint), out Peer? peer)
            && Queue(peer, payload, channel, delivery);
    }

    public bool SendToServer(ReadOnlySpan<byte> payload, byte channel, NetDelivery delivery)
    {
        return _role == NetTransportRole.Client
            && _serverPeer is { } peer
            && Queue(peer, payload, channel, delivery);
    }

    public bool SendUnconnected(IPEndPoint remoteEndPoint, ReadOnlySpan<byte> payload)
    {
        _ = remoteEndPoint;
        _ = payload;
        return false;
    }

    public bool Disconnect(IPEndPoint remoteEndPoint)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        if (!_peers.TryGetValue(EndPointKey(remoteEndPoint), out Peer? peer))
        {
            return false;
        }
        peer.Close();
        return true;
    }

    public IReadOnlyList<NetReceivedMessage> Receive(int maxMessages)
    {
        if (maxMessages <= 0 || _received.IsEmpty)
        {
            return [];
        }
        var output = new List<NetReceivedMessage>(Math.Min(maxMessages, _received.Count));
        while (output.Count < maxMessages && _received.TryDequeue(out NetReceivedMessage? message))
        {
            Interlocked.Decrement(ref _queuedMessages);
            output.Add(message);
        }
        return output;
    }

    public NetTransportSnapshot Snapshot()
    {
        return new NetTransportSnapshot(
            _role,
            IsRunning,
            IsConnected,
            _peers.Count,
            Interlocked.Read(ref _sentMessages),
            Interlocked.Read(ref _sentBytes),
            Interlocked.Read(ref _receivedMessages),
            Interlocked.Read(ref _receivedBytes),
            Interlocked.Read(ref _sentFrames),
            Interlocked.Read(ref _sentWireBytes),
            Interlocked.Read(ref _receivedFrames),
            Interlocked.Read(ref _receivedWireBytes),
            0,
            Interlocked.Read(ref _sendErrors),
            Interlocked.Read(ref _receiveErrors),
            Interlocked.Read(ref _queueDrops)
        );
    }

    public void Close()
    {
        lock (_lifecycleLock)
        {
            CloseLocked();
        }
    }

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                if (_peers.Count >= _options.MaxConnections)
                {
                    client.Dispose();
                    continue;
                }
                _ = Task.Run(() => AcceptPeerAsync(client, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
                return;
            }
            catch (Exception error) when (IsTransportException(error))
            {
                client?.Dispose();
                if (!cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _receiveErrors);
                }
            }
        }
    }

    private async Task AcceptPeerAsync(TcpClient client, CancellationToken cancellationToken)
    {
        Configure(client);
        try
        {
            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeTimeout.CancelAfter(Math.Min(_options.DisconnectTimeoutMilliseconds, 5000));
            if (!await ReadHandshakeAsync(client.GetStream(), handshakeTimeout.Token).ConfigureAwait(false))
            {
                client.Dispose();
                return;
            }
            await RunPeerAsync(client, cancellationToken, isServerPeer: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
        }
        catch (Exception error) when (IsTransportException(error))
        {
            client.Dispose();
            if (!cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _receiveErrors);
            }
        }
    }

    private async Task ConnectLoopAsync(IPEndPoint endPoint, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = new TcpClient(endPoint.AddressFamily);
                Configure(client);
                await client.ConnectAsync(endPoint.Address, endPoint.Port, cancellationToken)
                    .ConfigureAwait(false);
                await WriteHandshakeAsync(client.GetStream(), cancellationToken).ConfigureAwait(false);
                await RunPeerAsync(client, cancellationToken, isServerPeer: true).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
                return;
            }
            catch (Exception error) when (IsTransportException(error))
            {
                client?.Dispose();
                if (!cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _receiveErrors);
                }
            }
            try
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunPeerAsync(
        TcpClient client,
        CancellationToken cancellationToken,
        bool isServerPeer
    )
    {
        if (client.Client.RemoteEndPoint is not IPEndPoint remoteEndPoint)
        {
            client.Dispose();
            return;
        }
        var peer = new Peer(client, remoteEndPoint, _options.MaxQueuedMessages, cancellationToken);
        string key = EndPointKey(remoteEndPoint);
        if (!_peers.TryAdd(key, peer))
        {
            peer.Dispose();
            return;
        }
        if (isServerPeer)
        {
            _serverPeer = peer;
            _serverEndPoint = new IPEndPoint(remoteEndPoint.Address, remoteEndPoint.Port);
        }
        try
        {
            Task reader = ReadLoopAsync(peer);
            Task writer = WriteLoopAsync(peer);
            await Task.WhenAny(reader, writer).ConfigureAwait(false);
        }
        finally
        {
            peer.Dispose();
            _peers.TryRemove(key, out _);
            if (ReferenceEquals(_serverPeer, peer))
            {
                _serverPeer = null;
            }
        }
    }

    private async Task ReadLoopAsync(Peer peer)
    {
        byte[] header = new byte[FrameHeaderSize];
        try
        {
            while (!peer.Token.IsCancellationRequested)
            {
                if (!await ReadExactAsync(peer.Stream, header, peer.Token).ConfigureAwait(false))
                {
                    return;
                }
                int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header));
                byte channel = header[4];
                if (length <= 0
                    || length > MaxPayloadBytes
                    || channel >= ChannelCount
                    || !Enum.IsDefined((NetDelivery)header[5]))
                {
                    Interlocked.Increment(ref _receiveErrors);
                    return;
                }
                byte[] payload = new byte[length];
                if (!await ReadExactAsync(peer.Stream, payload, peer.Token).ConfigureAwait(false))
                {
                    return;
                }
                if (!TryEnqueue(new NetReceivedMessage(
                    new IPEndPoint(peer.EndPoint.Address, peer.EndPoint.Port),
                    payload,
                    channel,
                    (NetDelivery)header[5],
                    true
                )))
                {
                    return;
                }
                Interlocked.Increment(ref _receivedMessages);
                Interlocked.Add(ref _receivedBytes, payload.Length);
                Interlocked.Increment(ref _receivedFrames);
                Interlocked.Add(ref _receivedWireBytes, payload.Length + FrameHeaderSize);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error) when (IsTransportException(error))
        {
            if (!peer.Token.IsCancellationRequested)
            {
                Interlocked.Increment(ref _receiveErrors);
            }
        }
    }

    private async Task WriteLoopAsync(Peer peer)
    {
        byte[] header = new byte[FrameHeaderSize];
        try
        {
            await foreach (OutboundFrame frame in peer.Outbound.Reader.ReadAllAsync(peer.Token))
            {
                BinaryPrimitives.WriteUInt32BigEndian(header, (uint)frame.Payload.Length);
                header[4] = frame.Channel;
                header[5] = (byte)frame.Delivery;
                await peer.Stream.WriteAsync(header, peer.Token).ConfigureAwait(false);
                await peer.Stream.WriteAsync(frame.Payload, peer.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _sentMessages);
                Interlocked.Add(ref _sentBytes, frame.Payload.Length);
                Interlocked.Increment(ref _sentFrames);
                Interlocked.Add(ref _sentWireBytes, frame.Payload.Length + FrameHeaderSize);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error) when (IsTransportException(error))
        {
            if (!peer.Token.IsCancellationRequested)
            {
                Interlocked.Increment(ref _sendErrors);
            }
        }
    }

    private bool Queue(
        Peer peer,
        ReadOnlySpan<byte> payload,
        byte channel,
        NetDelivery delivery
    )
    {
        if (!peer.IsOpen || payload.IsEmpty || payload.Length > MaxPayloadBytes || channel >= ChannelCount)
        {
            return false;
        }
        var frame = new OutboundFrame(payload.ToArray(), channel, delivery);
        if (peer.Outbound.Writer.TryWrite(frame))
        {
            return true;
        }
        Interlocked.Increment(ref _queueDrops);
        if (delivery is NetDelivery.ReliableOrdered
            or NetDelivery.ReliableUnordered
            or NetDelivery.ReliableSequenced)
        {
            peer.Close();
        }
        return false;
    }

    private bool TryEnqueue(NetReceivedMessage message)
    {
        int queued = Interlocked.Increment(ref _queuedMessages);
        if (queued <= _options.MaxQueuedMessages)
        {
            _received.Enqueue(message);
            return true;
        }
        Interlocked.Decrement(ref _queuedMessages);
        Interlocked.Increment(ref _queueDrops);
        return false;
    }

    private async Task WriteHandshakeAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] key = Encoding.UTF8.GetBytes(_options.ConnectionKey);
        byte[] header = new byte[HandshakeHeaderSize];
        HandshakeMagic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), checked((ushort)key.Length));
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(key, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ReadHandshakeAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[HandshakeHeaderSize];
        if (!await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false)
            || !header.AsSpan(0, HandshakeMagic.Length).SequenceEqual(HandshakeMagic))
        {
            return false;
        }
        int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        if (length <= 0 || length > MaxConnectionKeyBytes)
        {
            return false;
        }
        byte[] key = new byte[length];
        return await ReadExactAsync(stream, key, cancellationToken).ConfigureAwait(false)
            && Encoding.UTF8.GetString(key) == _options.ConnectionKey;
    }

    private static async Task<bool> ReadExactAsync(
        NetworkStream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }
            offset += read;
        }
        return true;
    }

    private void CloseLocked()
    {
        _cancellation?.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
        }
        foreach (Peer peer in _peers.Values)
        {
            peer.Dispose();
        }
        _peers.Clear();
        _serverPeer = null;
        _serverEndPoint = null;
        _listener = null;
        _lifecycleTask = null;
        _cancellation?.Dispose();
        _cancellation = null;
        while (_received.TryDequeue(out _))
        {
        }
        Interlocked.Exchange(ref _queuedMessages, 0);
        _role = NetTransportRole.None;
        ResetCounters();
    }

    private void ResetCounters()
    {
        Interlocked.Exchange(ref _sentMessages, 0);
        Interlocked.Exchange(ref _sentBytes, 0);
        Interlocked.Exchange(ref _receivedMessages, 0);
        Interlocked.Exchange(ref _receivedBytes, 0);
        Interlocked.Exchange(ref _sentFrames, 0);
        Interlocked.Exchange(ref _sentWireBytes, 0);
        Interlocked.Exchange(ref _receivedFrames, 0);
        Interlocked.Exchange(ref _receivedWireBytes, 0);
        Interlocked.Exchange(ref _sendErrors, 0);
        Interlocked.Exchange(ref _receiveErrors, 0);
        Interlocked.Exchange(ref _queueDrops, 0);
    }

    private static TcpListener CreateListener(int port)
    {
        var listener = new TcpListener(IPAddress.IPv6Any, port);
        listener.Server.DualMode = true;
        return listener;
    }

    private static void Configure(TcpClient client)
    {
        client.NoDelay = true;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
    }

    private static NetTransportOptions ValidateOptions(NetTransportOptions? options)
    {
        NetTransportOptions value = options ?? new NetTransportOptions();
        int keyBytes = Encoding.UTF8.GetByteCount(value.ConnectionKey);
        if (keyBytes <= 0
            || keyBytes > MaxConnectionKeyBytes
            || value.MaxConnections <= 0
            || value.MaxQueuedMessages <= 0
            || value.PollIntervalMilliseconds <= 0
            || value.DisconnectTimeoutMilliseconds <= 0)
        {
            throw new ArgumentException(
                "Network transport options must contain positive limits and a valid connection key.",
                nameof(options)
            );
        }
        return value;
    }

    private static IPEndPoint ResolveEndPoint(string host, int port)
    {
        if (IPAddress.TryParse(host, out IPAddress? parsed))
        {
            return new IPEndPoint(parsed, port);
        }
        IPAddress[] addresses = Dns.GetHostAddresses(host);
        IPAddress? address = addresses.FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault();
        return address == null
            ? throw new SocketException((int)SocketError.HostNotFound)
            : new IPEndPoint(address, port);
    }

    private static string EndPointKey(IPEndPoint endPoint)
    {
        return endPoint.ToString();
    }

    private static bool IsTransportException(Exception error)
    {
        return error is IOException
            or ObjectDisposedException
            or SocketException
            or InvalidOperationException;
    }

    private sealed record OutboundFrame(byte[] Payload, byte Channel, NetDelivery Delivery);

    private sealed class Peer : IDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private int _closed;
        private int _disposed;

        public Peer(
            TcpClient client,
            IPEndPoint endPoint,
            int maxQueuedMessages,
            CancellationToken parentToken
        )
        {
            Client = client;
            EndPoint = new IPEndPoint(endPoint.Address, endPoint.Port);
            Stream = client.GetStream();
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            Outbound = Channel.CreateBounded<OutboundFrame>(new BoundedChannelOptions(maxQueuedMessages)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
        }

        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
        public IPEndPoint EndPoint { get; }
        public Channel<OutboundFrame> Outbound { get; }
        public CancellationToken Token => _cancellation.Token;
        public bool IsOpen => Volatile.Read(ref _closed) == 0 && Client.Connected;

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }
            Outbound.Writer.TryComplete();
            _cancellation.Cancel();
            Client.Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            Close();
            _cancellation.Dispose();
        }
    }
}
