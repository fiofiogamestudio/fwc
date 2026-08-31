using System.Net;

namespace Fw.Rt.Net;

public static class NetTransportType
{
    public const string Udp = "udp";
    public const string Tcp = "tcp";

    public static bool TryNormalize(string? value, out string transport)
    {
        transport = (value ?? "").Trim().ToLowerInvariant();
        if (transport.Length == 0)
        {
            transport = Udp;
        }
        return transport is Udp or Tcp;
    }
}

public enum NetDelivery
{
    Unreliable,
    UnreliableSequenced,
    ReliableOrdered,
    ReliableUnordered,
    ReliableSequenced,
}

public enum NetTransportRole
{
    None,
    Client,
    Server,
    Unconnected,
}

public sealed record NetTransportOptions
{
    public string ConnectionKey { get; init; } = "fw-net";
    public int MaxConnections { get; init; } = 32;
    public int MaxQueuedMessages { get; init; } = 8192;
    public int PollIntervalMilliseconds { get; init; } = 2;
    public int DisconnectTimeoutMilliseconds { get; init; } = 10_000;
}

public sealed record NetReceivedMessage(
    IPEndPoint RemoteEndPoint,
    byte[] Payload,
    byte Channel,
    NetDelivery Delivery,
    bool Connected
);

public sealed record NetTransportSnapshot(
    NetTransportRole Role,
    bool Running,
    bool Connected,
    int Peers,
    long SentMessages,
    long SentBytes,
    long ReceivedMessages,
    long ReceivedBytes,
    long SentDatagrams,
    long SentWireBytes,
    long ReceivedDatagrams,
    long ReceivedWireBytes,
    long PacketLoss,
    long SendErrors,
    long ReceiveErrors,
    long QueueDrops
);

public interface INetTransport : IDisposable
{
    NetTransportRole Role { get; }
    bool IsRunning { get; }
    bool IsConnected { get; }
    IPEndPoint? ServerEndPoint { get; }

    bool StartServer(int port, NetTransportOptions? options = null);
    bool StartClient(string host, int port, NetTransportOptions? options = null);
    bool StartUnconnected(NetTransportOptions? options = null);
    bool Send(
        IPEndPoint remoteEndPoint,
        ReadOnlySpan<byte> payload,
        byte channel,
        NetDelivery delivery
    );
    bool SendToServer(ReadOnlySpan<byte> payload, byte channel, NetDelivery delivery);
    bool SendUnconnected(IPEndPoint remoteEndPoint, ReadOnlySpan<byte> payload);
    bool Disconnect(IPEndPoint remoteEndPoint);
    IReadOnlyList<NetReceivedMessage> Receive(int maxMessages);
    NetTransportSnapshot Snapshot();
    void Close();
}
