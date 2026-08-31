using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fw.Rt.Rooms;

public sealed class RoomTicketClaims
{
    public int Version { get; init; }
    public string GameId { get; init; } = "";
    public string RoomId { get; init; } = "";
    public long ExpiresAtUnixSeconds { get; init; }
    public string Nonce { get; init; } = "";
    public string CreatePayload { get; init; } = "";
    public string Purpose { get; init; } = RoomTicketPurpose.Play;
    public IReadOnlyList<string> Permissions { get; init; } = Array.Empty<string>();
}

public static class RoomTicketPurpose
{
    public const string Play = "play";
    public const string Spectate = "spectate";
}

public static class RoomTicket
{
    public const int Version = 3;
    public const int MinimumSupportedVersion = 2;
    private const int MaxCreatePayloadBytes = 1024;
    private const int MaxPermissions = 16;
    private const int MaxPermissionLength = 64;

    public static string Create(
        string secret,
        string gameId,
        string roomId,
        DateTimeOffset expiresAt,
        string? nonce = null,
        string createPayload = "",
        string purpose = RoomTicketPurpose.Play,
        IReadOnlyCollection<string>? permissions = null
    )
    {
        ValidateIdentity(secret, gameId, roomId);
        ValidateCreatePayload(createPayload);
        string resolvedPurpose = NormalizePurpose(purpose);
        string[] resolvedPermissions = NormalizePermissions(permissions);
        string resolvedNonce = string.IsNullOrWhiteSpace(nonce)
            ? Base64Url(RandomNumberGenerator.GetBytes(16))
            : nonce;
        var payload = new TicketPayload
        {
            Version = Version,
            GameId = gameId,
            RoomId = roomId,
            ExpiresAtUnixSeconds = expiresAt.ToUnixTimeSeconds(),
            Nonce = resolvedNonce,
            CreatePayload = createPayload,
            Purpose = resolvedPurpose,
            Permissions = resolvedPermissions,
        };
        string encodedPayload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        string signature = Sign(secret, encodedPayload);
        return encodedPayload + "." + signature;
    }

    public static bool TryValidate(
        string ticket,
        string secret,
        string expectedGameId,
        string expectedRoomId,
        DateTimeOffset now,
        out RoomTicketClaims claims
    )
    {
        claims = new RoomTicketClaims();
        if (
            string.IsNullOrWhiteSpace(ticket)
            || ticket.Length > 2048
            || string.IsNullOrEmpty(secret)
            || string.IsNullOrEmpty(expectedGameId)
            || string.IsNullOrEmpty(expectedRoomId)
        )
        {
            return false;
        }

        int separator = ticket.IndexOf('.');
        if (separator <= 0 || separator != ticket.LastIndexOf('.') || separator >= ticket.Length - 1)
        {
            return false;
        }

        string encodedPayload = ticket[..separator];
        string encodedSignature = ticket[(separator + 1)..];
        byte[] suppliedSignature;
        byte[] expectedSignature;
        TicketPayload? payload;
        try
        {
            suppliedSignature = FromBase64Url(encodedSignature);
            expectedSignature = FromBase64Url(Sign(secret, encodedPayload));
            payload = JsonSerializer.Deserialize<TicketPayload>(FromBase64Url(encodedPayload));
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            return false;
        }
        string createPayload = payload?.CreatePayload ?? "";
        string purpose = payload?.Version == 2
            ? RoomTicketPurpose.Play
            : payload?.Purpose ?? "";
        string[] permissions = payload?.Permissions ?? Array.Empty<string>();

        if (
            payload == null
            || suppliedSignature.Length != expectedSignature.Length
            || !CryptographicOperations.FixedTimeEquals(suppliedSignature, expectedSignature)
            || !SupportsVersion(payload.Version)
            || !string.Equals(payload.GameId, expectedGameId, StringComparison.Ordinal)
            || !string.Equals(payload.RoomId, expectedRoomId, StringComparison.Ordinal)
            || payload.ExpiresAtUnixSeconds <= now.ToUnixTimeSeconds()
            || string.IsNullOrWhiteSpace(payload.Nonce)
            || payload.Nonce.Length > 128
            || Encoding.UTF8.GetByteCount(createPayload) > MaxCreatePayloadBytes
            || !IsValidPurpose(purpose)
            || !AreValidPermissions(permissions)
        )
        {
            return false;
        }

        claims = new RoomTicketClaims
        {
            Version = payload.Version,
            GameId = payload.GameId,
            RoomId = payload.RoomId,
            ExpiresAtUnixSeconds = payload.ExpiresAtUnixSeconds,
            Nonce = payload.Nonce,
            CreatePayload = createPayload,
            Purpose = purpose,
            Permissions = permissions,
        };
        return true;
    }

    public static bool SupportsVersion(int version)
    {
        return version is >= MinimumSupportedVersion and <= Version;
    }

    private static void ValidateIdentity(string secret, string gameId, string roomId)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("Room ticket secret cannot be empty.", nameof(secret));
        }
        if (string.IsNullOrWhiteSpace(gameId))
        {
            throw new ArgumentException("Room ticket game id cannot be empty.", nameof(gameId));
        }
        if (string.IsNullOrWhiteSpace(roomId))
        {
            throw new ArgumentException("Room ticket room id cannot be empty.", nameof(roomId));
        }
    }

    private static void ValidateCreatePayload(string createPayload)
    {
        ArgumentNullException.ThrowIfNull(createPayload);
        if (Encoding.UTF8.GetByteCount(createPayload) > MaxCreatePayloadBytes)
        {
            throw new ArgumentException(
                $"Room create payload cannot exceed {MaxCreatePayloadBytes} UTF-8 bytes.",
                nameof(createPayload)
            );
        }
    }

    private static string NormalizePurpose(string purpose)
    {
        string value = (purpose ?? "").Trim().ToLowerInvariant();
        return IsValidPurpose(value)
            ? value
            : throw new ArgumentException("Room ticket purpose is invalid.", nameof(purpose));
    }

    private static bool IsValidPurpose(string purpose)
    {
        return purpose is RoomTicketPurpose.Play or RoomTicketPurpose.Spectate;
    }

    private static string[] NormalizePermissions(IReadOnlyCollection<string>? permissions)
    {
        string[] values = (permissions ?? Array.Empty<string>())
            .Select(value => (value ?? "").Trim().ToLowerInvariant())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (!AreValidPermissions(values))
        {
            throw new ArgumentException("Room ticket permissions are invalid.", nameof(permissions));
        }
        return values;
    }

    private static bool AreValidPermissions(IReadOnlyCollection<string> permissions)
    {
        return permissions.Count <= MaxPermissions
            && permissions.All(value =>
                !string.IsNullOrWhiteSpace(value)
                && value.Length <= MaxPermissionLength
                && value.All(character =>
                    char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'
                )
            );
    }

    private static string Sign(string secret, string encodedPayload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Base64Url(hmac.ComputeHash(Encoding.ASCII.GetBytes(encodedPayload)));
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] FromBase64Url(string value)
    {
        string normalized = value.Replace('-', '+').Replace('_', '/');
        int remainder = normalized.Length % 4;
        if (remainder == 1)
        {
            throw new FormatException("Invalid base64url length.");
        }
        if (remainder > 0)
        {
            normalized = normalized.PadRight(normalized.Length + (4 - remainder), '=');
        }
        return Convert.FromBase64String(normalized);
    }

    private sealed class TicketPayload
    {
        [JsonPropertyName("v")]
        public int Version { get; init; }

        [JsonPropertyName("g")]
        public string GameId { get; init; } = "";

        [JsonPropertyName("r")]
        public string RoomId { get; init; } = "";

        [JsonPropertyName("e")]
        public long ExpiresAtUnixSeconds { get; init; }

        [JsonPropertyName("n")]
        public string Nonce { get; init; } = "";

        [JsonPropertyName("c")]
        public string CreatePayload { get; init; } = "";

        [JsonPropertyName("p")]
        public string Purpose { get; init; } = RoomTicketPurpose.Play;

        [JsonPropertyName("a")]
        public string[] Permissions { get; init; } = Array.Empty<string>();
    }
}
