using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fw.Rt.Rooms;

public sealed class RoomTicketClaims
{
    public string GameId { get; init; } = "";
    public string RoomId { get; init; } = "";
    public long ExpiresAtUnixSeconds { get; init; }
    public string Nonce { get; init; } = "";
}

public static class RoomTicket
{
    public const int Version = 1;

    public static string Create(
        string secret,
        string gameId,
        string roomId,
        DateTimeOffset expiresAt,
        string? nonce = null
    )
    {
        ValidateIdentity(secret, gameId, roomId);
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

        if (
            payload == null
            || suppliedSignature.Length != expectedSignature.Length
            || !CryptographicOperations.FixedTimeEquals(suppliedSignature, expectedSignature)
            || payload.Version != Version
            || !string.Equals(payload.GameId, expectedGameId, StringComparison.Ordinal)
            || !string.Equals(payload.RoomId, expectedRoomId, StringComparison.Ordinal)
            || payload.ExpiresAtUnixSeconds <= now.ToUnixTimeSeconds()
            || string.IsNullOrWhiteSpace(payload.Nonce)
            || payload.Nonce.Length > 128
        )
        {
            return false;
        }

        claims = new RoomTicketClaims
        {
            GameId = payload.GameId,
            RoomId = payload.RoomId,
            ExpiresAtUnixSeconds = payload.ExpiresAtUnixSeconds,
            Nonce = payload.Nonce,
        };
        return true;
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
    }
}
