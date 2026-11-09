using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Remy.Core.Cleanup;

/// <summary>Bounded HTTP transport (cleanup.md §1.7): 2 MiB response cap, redirects rejected, no cookies, status-only errors.</summary>
public sealed class CleanupHttpTransport : IDisposable
{
    public const int MaxResponseBytes = 2_097_152;

    private readonly HttpClient _client;

    /// <param name="handler">Optional handler (tests). Must not follow redirects. When null a SocketsHttpHandler is created.</param>
    public CleanupHttpTransport(HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static bool IsLoopbackHost(string host)
    {
        var h = host.Trim().ToLowerInvariant();
        return h is "localhost" or "127.0.0.1" or "::1" or "[::1]";
    }

    /// <summary>Allowed: http/https, host present, no userinfo.</summary>
    public static Uri RequireValidUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new CleanupFailureException(RemyCleanupFailure.InvalidConfiguration);
        return uri;
    }

    public async Task<byte[]> SendAsync(HttpMethod method, string url, string? jsonBody, string? bearerKey,
        TimeSpan timeout, CancellationToken ct, IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        var uri = RequireValidUri(url);
        // Never send a credential in clear text to a non-loopback host.
        if (!string.IsNullOrWhiteSpace(bearerKey) && uri.Scheme == Uri.UriSchemeHttp && !IsLoopbackHost(uri.Host))
            throw new CleanupFailureException(RemyCleanupFailure.InvalidConfiguration);

        using var req = new HttpRequestMessage(method, uri);
        if (jsonBody is not null) req.Content = new StringContent(jsonBody, new UTF8Encoding(false), "application/json");
        if (!string.IsNullOrWhiteSpace(bearerKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerKey.Trim());
        if (extraHeaders is not null)
            foreach (var (k, v) in extraHeaders) req.Headers.TryAddWithoutValidation(k, v);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            var status = (int)resp.StatusCode;
            if (status >= 300 && status <= 399) throw new CleanupFailureException(RemyCleanupFailure.InvalidConfiguration);
            if (RemyCleanupFailure.FromHttpStatus(status) is { } f) throw new CleanupFailureException(f);

            if (resp.Content.Headers.ContentLength is { } len && len > MaxResponseBytes)
                throw new CleanupFailureException(RemyCleanupFailure.ResponseTooLarge);

            await using var stream = await resp.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            using var ms = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var n = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                if (n == 0) break;
                if (ms.Length + n > MaxResponseBytes) throw new CleanupFailureException(RemyCleanupFailure.ResponseTooLarge);
                ms.Write(buffer, 0, n);
            }
            return ms.ToArray();
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new CleanupFailureException(RemyCleanupFailure.Timeout);
        }
        catch (HttpRequestException ex)
        {
            throw new CleanupFailureException(RemyCleanupFailure.Of(RemyCleanupFailure.Transport), ex);
        }
    }

    public void Dispose() => _client.Dispose();
}
