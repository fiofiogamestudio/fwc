using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Fw.Rt.Rooms;

public sealed class RoomDirectoryException : Exception
{
    public RoomDirectoryException(string message, int statusCode = 0)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

public sealed class RoomDirectoryClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Uri _baseUri;

    public RoomDirectoryClient(string baseUrl, HttpClient? httpClient = null)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? parsed))
        {
            throw new ArgumentException("Room directory URL must be absolute.", nameof(baseUrl));
        }
        if (parsed.Scheme != Uri.UriSchemeHttps && !(parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback))
        {
            throw new ArgumentException("Non-loopback room directories must use HTTPS.", nameof(baseUrl));
        }
        _baseUri = new Uri(parsed.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        _http = httpClient ?? CreateHttpClient();
        _ownsHttp = httpClient == null;
    }

    public async Task<IReadOnlyList<RoomInfo>> ListAsync(
        string gameId,
        int protocolVersion,
        CancellationToken cancellationToken = default
    )
    {
        string path = "rooms?game_id=" + Uri.EscapeDataString(gameId)
            + "&protocol_version=" + protocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Get, Resolve(path));
        return await SendAsync<RoomInfo[]>(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RoomRegistrationResult> RegisterAsync(
        RoomRegistration registration,
        string registrationSecret,
        CancellationToken cancellationToken = default
    )
    {
        using var request = JsonRequest(HttpMethod.Post, "rooms/register", registration);
        Authorize(request, registrationSecret);
        return await SendAsync<RoomRegistrationResult>(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task HeartbeatAsync(
        string roomId,
        string heartbeatToken,
        RoomHeartbeat heartbeat,
        CancellationToken cancellationToken = default
    )
    {
        using var request = JsonRequest(
            HttpMethod.Post,
            "rooms/" + Uri.EscapeDataString(roomId) + "/heartbeat",
            heartbeat
        );
        Authorize(request, heartbeatToken);
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task UnregisterAsync(
        string roomId,
        string heartbeatToken,
        CancellationToken cancellationToken = default
    )
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            Resolve("rooms/" + Uri.EscapeDataString(roomId))
        );
        Authorize(request, heartbeatToken);
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RoomJoin> JoinAsync(string roomId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            Resolve("rooms/" + Uri.EscapeDataString(roomId) + "/join")
        );
        return await SendAsync<RoomJoin>(request, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private HttpRequestMessage JsonRequest<T>(HttpMethod method, string path, T value)
    {
        return new HttpRequestMessage(method, Resolve(path))
        {
            Content = JsonContent.Create(value, options: JsonOptions),
        };
    }

    private static void Authorize(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private Uri Resolve(string path)
    {
        return new Uri(_baseUri, path);
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await Error(response, cancellationToken).ConfigureAwait(false);
        }
        T? result = await response.Content
            .ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return result ?? throw new RoomDirectoryException("Room directory returned an empty response.");
    }

    private async Task SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await Error(response, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<RoomDirectoryException> Error(
        HttpResponseMessage response,
        CancellationToken cancellationToken
    )
    {
        string detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string message = string.IsNullOrWhiteSpace(detail)
            ? $"Room directory request failed with HTTP {(int)response.StatusCode}."
            : detail.Trim();
        return new RoomDirectoryException(message, (int)response.StatusCode);
    }
}
