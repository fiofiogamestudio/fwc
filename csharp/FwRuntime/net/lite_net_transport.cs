using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;

namespace Fw.Rt.Net;

public sealed class LiteNetTransport : INetTransport
{
    private const byte ChannelCount = 8;

    private readonly object _lifecycleLock = new();
    private readonly ConcurrentDictionary<string, NetPeer> _peers = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<NetReceivedMessage> _received = new();
    private EventBasedNetListener? _listener;
    private NetManager? _manager;
    private NetPeer? _serverPeer;
    private NetTransportOptions _options = new();
    private NetTransportRole _role;
    private IPEndPoint? _serverEndPoint;
    private long _sentMessages;
    private long _sentBytes;
    private long _receivedMessages;
    private long _receivedBytes;
    private long _sendErrors;
    private long _receiveErrors;
    private long _queueDrops;
    private int _queuedMessages;
    private long _nextConnectAttemptTimestamp;

    public NetTransportRole Role => _role;
    public bool IsRunning => _manager?.IsRunning == true;
    public bool IsConnected => _role switch
    {
        NetTransportRole.Client => _serverPeer?.ConnectionState == ConnectionState.Connected,
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
            _role = NetTransportRole.Server;
            _options = ValidateOptions(options);
            CreateManager();
            try
            {
                if (_manager!.Start(port))
                {
                    return true;
                }
            }
            catch (Exception error) when (IsTransportException(error))
            {
                Interlocked.Increment(ref _receiveErrors);
            }
            CloseLocked();
            return false;
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
            _role = NetTransportRole.Client;
            _options = ValidateOptions(options);
            try
            {
                _serverEndPoint = ResolveEndPoint(host, port);
                CreateManager();
                if (!_manager!.Start())
                {
                    CloseLocked();
                    return false;
                }
                _serverPeer = _manager.Connect(_serverEndPoint, _options.ConnectionKey);
                return _serverPeer != null;
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
        lock (_lifecycleLock)
        {
            CloseLocked();
            _role = NetTransportRole.Unconnected;
            _options = ValidateOptions(options);
            CreateManager();
            try
            {
                if (_manager!.Start())
                {
                    return true;
                }
            }
            catch (Exception error) when (IsTransportException(error))
            {
                Interlocked.Increment(ref _receiveErrors);
            }
            CloseLocked();
            return false;
        }
    }

    public bool Send(
        IPEndPoint remoteEndPoint,
        ReadOnlySpan<byte> payload,
        byte channel,
        NetDelivery delivery
    )
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        if (payload.IsEmpty || channel >= ChannelCount || !IsRunning)
        {
            return false;
        }
        if (!_peers.TryGetValue(EndPointKey(remoteEndPoint), out NetPeer? peer)
            || peer.ConnectionState != ConnectionState.Connected)
        {
            return false;
        }
        return Send(peer, payload, channel, delivery);
    }

    public bool SendToServer(ReadOnlySpan<byte> payload, byte channel, NetDelivery delivery)
    {
        if (_role != NetTransportRole.Client || payload.IsEmpty || channel >= ChannelCount)
        {
            return false;
        }
        NetPeer? peer = _serverPeer;
        if (peer?.ConnectionState != ConnectionState.Connected)
        {
            TryReconnect();
            return false;
        }
        return Send(peer, payload, channel, delivery);
    }

    public bool SendUnconnected(IPEndPoint remoteEndPoint, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        LiteNetManager? manager = _manager;
        if (manager?.IsRunning != true || payload.IsEmpty)
        {
            return false;
        }
        try
        {
            bool sent = manager.SendUnconnectedMessage(payload, remoteEndPoint);
            if (sent)
            {
                Interlocked.Increment(ref _sentMessages);
                Interlocked.Add(ref _sentBytes, payload.Length);
            }
            else
            {
                Interlocked.Increment(ref _sendErrors);
            }
            return sent;
        }
        catch (Exception error) when (IsTransportException(error))
        {
            Interlocked.Increment(ref _sendErrors);
            return false;
        }
    }

    public bool Disconnect(IPEndPoint remoteEndPoint)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        if (!_peers.TryGetValue(EndPointKey(remoteEndPoint), out NetPeer? peer)
            || peer.ConnectionState != ConnectionState.Connected)
        {
            return false;
        }
        try
        {
            peer.Disconnect();
            return true;
        }
        catch (Exception error) when (IsTransportException(error))
        {
            Interlocked.Increment(ref _sendErrors);
            return false;
        }
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
        NetStatistics? statistics = _manager?.Statistics;
        return new NetTransportSnapshot(
            _role,
            IsRunning,
            IsConnected,
            _peers.Count,
            Interlocked.Read(ref _sentMessages),
            Interlocked.Read(ref _sentBytes),
            Interlocked.Read(ref _receivedMessages),
            Interlocked.Read(ref _receivedBytes),
            statistics?.PacketsSent ?? 0,
            statistics?.BytesSent ?? 0,
            statistics?.PacketsReceived ?? 0,
            statistics?.BytesReceived ?? 0,
            statistics?.PacketLoss ?? 0,
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

    private void CreateManager()
    {
        _listener = new EventBasedNetListener();
        _listener.ConnectionRequestEvent += OnConnectionRequest;
        _listener.PeerConnectedEvent += OnPeerConnected;
        _listener.PeerDisconnectedEvent += OnPeerDisconnected;
        _listener.NetworkReceiveEvent += OnNetworkReceive;
        _listener.NetworkReceiveUnconnectedEvent += OnNetworkReceiveUnconnected;
        _listener.NetworkErrorEvent += OnNetworkError;
        _manager = new NetManager(_listener)
        {
            AutoRecycle = true,
            ChannelsCount = ChannelCount,
            DisconnectTimeout = _options.DisconnectTimeoutMilliseconds,
            UnsyncedEvents = true,
            UnconnectedMessagesEnabled = true,
            BroadcastReceiveEnabled = true,
            EnableStatistics = true,
            UpdateTime = _options.PollIntervalMilliseconds,
        };
    }

    private void OnConnectionRequest(ConnectionRequest request)
    {
        if (_role != NetTransportRole.Server || _peers.Count >= _options.MaxConnections)
        {
            request.Reject();
            return;
        }
        request.AcceptIfKey(_options.ConnectionKey);
    }

    private void OnPeerConnected(NetPeer peer)
    {
        _peers[EndPointKey(peer)] = peer;
        if (_role == NetTransportRole.Client)
        {
            _serverPeer = peer;
            _serverEndPoint = CloneEndPoint(peer);
        }
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        _peers.TryRemove(EndPointKey(peer), out _);
        _ = disconnectInfo;
        if (ReferenceEquals(peer, _serverPeer))
        {
            _serverPeer = null;
        }
    }

    private void OnNetworkReceive(
        NetPeer peer,
        NetPacketReader reader,
        byte channel,
        DeliveryMethod delivery
    )
    {
        try
        {
            byte[] bytes = reader.GetRemainingBytes();
            var message = new NetReceivedMessage(
                CloneEndPoint(peer),
                bytes,
                channel,
                FromLiteDelivery(delivery),
                true
            );
            if (!TryEnqueue(message))
            {
                if (IsReliable(delivery))
                {
                    peer.Disconnect();
                }
                return;
            }
            Interlocked.Increment(ref _receivedMessages);
            Interlocked.Add(ref _receivedBytes, bytes.Length);
        }
        catch (Exception error) when (IsTransportException(error))
        {
            Interlocked.Increment(ref _receiveErrors);
        }
    }

    private void OnNetworkReceiveUnconnected(
        IPEndPoint remoteEndPoint,
        NetPacketReader reader,
        UnconnectedMessageType messageType
    )
    {
        if (messageType != UnconnectedMessageType.BasicMessage)
        {
            return;
        }
        try
        {
            byte[] bytes = reader.GetRemainingBytes();
            var message = new NetReceivedMessage(
                CloneEndPoint(remoteEndPoint),
                bytes,
                0,
                NetDelivery.Unreliable,
                false
            );
            if (!TryEnqueue(message))
            {
                return;
            }
            Interlocked.Increment(ref _receivedMessages);
            Interlocked.Add(ref _receivedBytes, bytes.Length);
        }
        catch (Exception error) when (IsTransportException(error))
        {
            Interlocked.Increment(ref _receiveErrors);
        }
    }

    private void OnNetworkError(IPEndPoint _, SocketError __)
    {
        Interlocked.Increment(ref _receiveErrors);
    }

    private bool Send(NetPeer peer, ReadOnlySpan<byte> payload, byte channel, NetDelivery delivery)
    {
        try
        {
            peer.Send(payload, channel, ToLiteDelivery(delivery));
            Interlocked.Increment(ref _sentMessages);
            Interlocked.Add(ref _sentBytes, payload.Length);
            return true;
        }
        catch (Exception error) when (IsTransportException(error))
        {
            Interlocked.Increment(ref _sendErrors);
            return false;
        }
    }

    private void CloseLocked()
    {
        try
        {
            _manager?.Stop();
        }
        catch (Exception error) when (IsTransportException(error))
        {
            Interlocked.Increment(ref _receiveErrors);
        }
        _manager = null;
        _listener = null;
        _serverPeer = null;
        _serverEndPoint = null;
        _nextConnectAttemptTimestamp = 0;
        _peers.Clear();
        while (_received.TryDequeue(out _))
        {
        }
        Interlocked.Exchange(ref _queuedMessages, 0);
        _role = NetTransportRole.None;
        ResetCounters();
    }

    private void TryReconnect()
    {
        LiteNetManager? manager = _manager;
        IPEndPoint? endPoint = _serverEndPoint;
        if (_role != NetTransportRole.Client || manager?.IsRunning != true || endPoint == null)
        {
            return;
        }
        long now = Environment.TickCount64;
        if (now < Interlocked.Read(ref _nextConnectAttemptTimestamp))
        {
            return;
        }
        lock (_lifecycleLock)
        {
            if (_serverPeer != null || _manager?.IsRunning != true || _serverEndPoint == null)
            {
                return;
            }
            Interlocked.Exchange(ref _nextConnectAttemptTimestamp, now + 250);
            try
            {
                _serverPeer = _manager.Connect(_serverEndPoint, _options.ConnectionKey);
            }
            catch (Exception error) when (IsTransportException(error))
            {
                Interlocked.Increment(ref _receiveErrors);
            }
        }
    }

    private static NetTransportOptions ValidateOptions(NetTransportOptions? options)
    {
        NetTransportOptions value = options ?? new NetTransportOptions();
        if (string.IsNullOrWhiteSpace(value.ConnectionKey)
            || value.MaxConnections <= 0
            || value.MaxQueuedMessages <= 0
            || value.PollIntervalMilliseconds <= 0
            || value.DisconnectTimeoutMilliseconds <= 0)
        {
            throw new ArgumentException("Network transport options must contain positive limits and a connection key.", nameof(options));
        }
        return value;
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

    private void ResetCounters()
    {
        Interlocked.Exchange(ref _sentMessages, 0);
        Interlocked.Exchange(ref _sentBytes, 0);
        Interlocked.Exchange(ref _receivedMessages, 0);
        Interlocked.Exchange(ref _receivedBytes, 0);
        Interlocked.Exchange(ref _sendErrors, 0);
        Interlocked.Exchange(ref _receiveErrors, 0);
        Interlocked.Exchange(ref _queueDrops, 0);
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

    private static IPEndPoint CloneEndPoint(IPEndPoint endPoint)
    {
        return new IPEndPoint(endPoint.Address, endPoint.Port);
    }

    private static string EndPointKey(IPEndPoint endPoint)
    {
        return endPoint.ToString();
    }

    private static DeliveryMethod ToLiteDelivery(NetDelivery delivery)
    {
        return delivery switch
        {
            NetDelivery.Unreliable => DeliveryMethod.Unreliable,
            NetDelivery.UnreliableSequenced => DeliveryMethod.Sequenced,
            NetDelivery.ReliableOrdered => DeliveryMethod.ReliableOrdered,
            NetDelivery.ReliableUnordered => DeliveryMethod.ReliableUnordered,
            NetDelivery.ReliableSequenced => DeliveryMethod.ReliableSequenced,
            _ => throw new ArgumentOutOfRangeException(nameof(delivery)),
        };
    }

    private static NetDelivery FromLiteDelivery(DeliveryMethod delivery)
    {
        return delivery switch
        {
            DeliveryMethod.Unreliable => NetDelivery.Unreliable,
            DeliveryMethod.Sequenced => NetDelivery.UnreliableSequenced,
            DeliveryMethod.ReliableOrdered => NetDelivery.ReliableOrdered,
            DeliveryMethod.ReliableUnordered => NetDelivery.ReliableUnordered,
            DeliveryMethod.ReliableSequenced => NetDelivery.ReliableSequenced,
            _ => throw new ArgumentOutOfRangeException(nameof(delivery)),
        };
    }

    private static bool IsTransportException(Exception error)
    {
        return error is SocketException
            or ObjectDisposedException
            or InvalidOperationException
            or ArgumentException
            or TooBigPacketException;
    }

    private static bool IsReliable(DeliveryMethod delivery)
    {
        return delivery is DeliveryMethod.ReliableOrdered
            or DeliveryMethod.ReliableUnordered
            or DeliveryMethod.ReliableSequenced;
    }
}
