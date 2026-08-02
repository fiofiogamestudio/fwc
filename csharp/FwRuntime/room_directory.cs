using System.Security.Cryptography;

namespace Fw.Rt.Rooms;

public static class RoomStatus
{
    public const string Open = "open";
    public const string Full = "full";
    public const string Closed = "closed";
}

public sealed class RoomInfo
{
    public string RoomId { get; init; } = "";
    public string GameId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string MapKey { get; init; } = "";
    public string Region { get; init; } = "";
    public int Players { get; init; }
    public int Capacity { get; init; }
    public int ProtocolVersion { get; init; }
    public string Status { get; init; } = RoomStatus.Open;
    public long UpdatedAtUnixSeconds { get; init; }
    public Dictionary<string, string> Tags { get; init; } = new(StringComparer.Ordinal);
}

public sealed class RoomRegistration
{
    public string RoomId { get; init; } = "";
    public string GameId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string MapKey { get; init; } = "";
    public string Region { get; init; } = "";
    public int Capacity { get; init; }
    public int ProtocolVersion { get; init; }
    public Dictionary<string, string> Tags { get; init; } = new(StringComparer.Ordinal);
}

public sealed class RoomHeartbeat
{
    public int Players { get; init; }
    public string Status { get; init; } = RoomStatus.Open;
}

public sealed class RoomRegistrationResult
{
    public RoomInfo Room { get; init; } = new();
    public string HeartbeatToken { get; init; } = "";
    public string AdmissionSecret { get; init; } = "";
    public int HeartbeatIntervalMilliseconds { get; init; }
}

public sealed class RoomJoin
{
    public RoomInfo Room { get; init; } = new();
    public string Ticket { get; init; } = "";
    public long ExpiresAtUnixSeconds { get; init; }
}

public sealed class RoomDirectoryStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _rooms = new(StringComparer.Ordinal);
    private readonly string _secret;
    private readonly TimeSpan _staleAfter;
    private readonly TimeSpan _ticketLifetime;

    public RoomDirectoryStore(string secret, TimeSpan staleAfter, TimeSpan ticketLifetime)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("Room directory secret cannot be empty.", nameof(secret));
        }
        if (staleAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(staleAfter));
        }
        if (ticketLifetime <= TimeSpan.Zero || ticketLifetime > staleAfter)
        {
            throw new ArgumentOutOfRangeException(nameof(ticketLifetime));
        }
        _secret = secret;
        _staleAfter = staleAfter;
        _ticketLifetime = ticketLifetime;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _rooms.Count;
            }
        }
    }

    public RoomRegistrationResult Register(
        RoomRegistration registration,
        string registrationSecret,
        string remoteHost,
        DateTimeOffset now
    )
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (!SecureEquals(_secret, registrationSecret))
        {
            throw new UnauthorizedAccessException("Room registration secret is invalid.");
        }

        RoomInfo room = Normalize(registration, remoteHost, now);
        string heartbeatToken = Base64Url(RandomNumberGenerator.GetBytes(24));
        string admissionSecret = Base64Url(RandomNumberGenerator.GetBytes(32));
        lock (_gate)
        {
            SweepLocked(now);
            if (_rooms.ContainsKey(room.RoomId))
            {
                throw new InvalidOperationException("An active room already uses this id.");
            }
            _rooms[room.RoomId] = new Entry(room, heartbeatToken, admissionSecret, now);
        }
        return new RoomRegistrationResult
        {
            Room = Clone(room),
            HeartbeatToken = heartbeatToken,
            AdmissionSecret = admissionSecret,
            HeartbeatIntervalMilliseconds = RecommendedHeartbeatMilliseconds(),
        };
    }

    public bool Heartbeat(string roomId, string heartbeatToken, RoomHeartbeat heartbeat, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);
        lock (_gate)
        {
            SweepLocked(now);
            if (
                !_rooms.TryGetValue(roomId, out Entry? entry)
                || !SecureEquals(entry.HeartbeatToken, heartbeatToken)
            )
            {
                return false;
            }

            int players = Math.Clamp(heartbeat.Players, 0, entry.Room.Capacity);
            string status = NormalizeStatus(heartbeat.Status, players, entry.Room.Capacity);
            entry.Room = Copy(entry.Room, players, status, now);
            entry.LastSeen = now;
            return true;
        }
    }

    public bool Remove(string roomId, string heartbeatToken)
    {
        lock (_gate)
        {
            if (
                !_rooms.TryGetValue(roomId, out Entry? entry)
                || !SecureEquals(entry.HeartbeatToken, heartbeatToken)
            )
            {
                return false;
            }
            return _rooms.Remove(roomId);
        }
    }

    public IReadOnlyList<RoomInfo> List(string gameId, int protocolVersion, DateTimeOffset now)
    {
        lock (_gate)
        {
            SweepLocked(now);
            return _rooms.Values
                .Select(entry => entry.Room)
                .Where(room =>
                    string.Equals(room.GameId, gameId, StringComparison.Ordinal)
                    && room.ProtocolVersion == protocolVersion
                    && room.Status != RoomStatus.Closed
                )
                .OrderBy(room => room.Status == RoomStatus.Full)
                .ThenBy(room => room.Players)
                .ThenBy(room => room.Name, StringComparer.Ordinal)
                .Select(Clone)
                .ToArray();
        }
    }

    public RoomJoin? Join(string roomId, DateTimeOffset now)
    {
        lock (_gate)
        {
            SweepLocked(now);
            if (
                !_rooms.TryGetValue(roomId, out Entry? entry)
                || entry.Room.Status != RoomStatus.Open
                || entry.Room.Players >= entry.Room.Capacity
            )
            {
                return null;
            }

            DateTimeOffset expiresAt = now.Add(_ticketLifetime);
            return new RoomJoin
            {
                Room = Clone(entry.Room),
                Ticket = RoomTicket.Create(
                    entry.AdmissionSecret,
                    entry.Room.GameId,
                    entry.Room.RoomId,
                    expiresAt
                ),
                ExpiresAtUnixSeconds = expiresAt.ToUnixTimeSeconds(),
            };
        }
    }

    public int Sweep(DateTimeOffset now)
    {
        lock (_gate)
        {
            return SweepLocked(now);
        }
    }

    private int SweepLocked(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now.Subtract(_staleAfter);
        string[] stale = _rooms
            .Where(pair => pair.Value.LastSeen < cutoff)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (string roomId in stale)
        {
            _rooms.Remove(roomId);
        }
        return stale.Length;
    }

    private int RecommendedHeartbeatMilliseconds()
    {
        double milliseconds = Math.Floor(_staleAfter.TotalMilliseconds / 3.0);
        return (int)Math.Clamp(milliseconds, 1.0, int.MaxValue);
    }

    private static RoomInfo Normalize(RoomRegistration value, string remoteHost, DateTimeOffset now)
    {
        string roomId = Required(value.RoomId, nameof(value.RoomId), 64);
        string gameId = Required(value.GameId, nameof(value.GameId), 64);
        string name = Required(value.Name, nameof(value.Name), 96);
        string host = Required(
            string.IsNullOrWhiteSpace(value.Host) ? remoteHost : value.Host,
            nameof(value.Host),
            255
        );
        string mapKey = Required(value.MapKey, nameof(value.MapKey), 64);
        string region = Optional(value.Region, 32);
        if (!roomId.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'))
        {
            throw new ArgumentException("Room id contains unsupported characters.", nameof(value));
        }
        if (value.Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Room port must be between 1 and 65535.");
        }
        if (value.Capacity is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Room capacity must be between 1 and 1024.");
        }
        if (value.ProtocolVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Room protocol version must be positive.");
        }

        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (
            (string key, string tagValue) in (value.Tags ?? new Dictionary<string, string>()).Take(16)
        )
        {
            string normalizedKey = Required(key, "tag key", 32);
            tags[normalizedKey] = Optional(tagValue, 96);
        }
        return new RoomInfo
        {
            RoomId = roomId,
            GameId = gameId,
            Name = name,
            Host = host,
            Port = value.Port,
            MapKey = mapKey,
            Region = region,
            Players = 0,
            Capacity = value.Capacity,
            ProtocolVersion = value.ProtocolVersion,
            Status = RoomStatus.Open,
            UpdatedAtUnixSeconds = now.ToUnixTimeSeconds(),
            Tags = tags,
        };
    }

    private static string NormalizeStatus(string status, int players, int capacity)
    {
        if (string.Equals(status, RoomStatus.Closed, StringComparison.Ordinal))
        {
            return RoomStatus.Closed;
        }
        return players >= capacity ? RoomStatus.Full : RoomStatus.Open;
    }

    private static RoomInfo Copy(RoomInfo room, int players, string status, DateTimeOffset now)
    {
        return new RoomInfo
        {
            RoomId = room.RoomId,
            GameId = room.GameId,
            Name = room.Name,
            Host = room.Host,
            Port = room.Port,
            MapKey = room.MapKey,
            Region = room.Region,
            Players = players,
            Capacity = room.Capacity,
            ProtocolVersion = room.ProtocolVersion,
            Status = status,
            UpdatedAtUnixSeconds = now.ToUnixTimeSeconds(),
            Tags = new Dictionary<string, string>(room.Tags, StringComparer.Ordinal),
        };
    }

    private static RoomInfo Clone(RoomInfo room)
    {
        return Copy(
            room,
            room.Players,
            room.Status,
            DateTimeOffset.FromUnixTimeSeconds(room.UpdatedAtUnixSeconds)
        );
    }

    private static string Required(string? value, string name, int maxLength)
    {
        string normalized = (value ?? "").Trim();
        if (normalized.Length == 0 || normalized.Length > maxLength)
        {
            throw new ArgumentException($"{name} must contain 1 to {maxLength} characters.", name);
        }
        return normalized;
    }

    private static string Optional(string? value, int maxLength)
    {
        string normalized = (value ?? "").Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static bool SecureEquals(string left, string right)
    {
        byte[] leftHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(left));
        byte[] rightHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(right));
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private sealed class Entry(
        RoomInfo room,
        string heartbeatToken,
        string admissionSecret,
        DateTimeOffset lastSeen
    )
    {
        public RoomInfo Room { get; set; } = room;
        public string HeartbeatToken { get; } = heartbeatToken;
        public string AdmissionSecret { get; } = admissionSecret;
        public DateTimeOffset LastSeen { get; set; } = lastSeen;
    }
}
