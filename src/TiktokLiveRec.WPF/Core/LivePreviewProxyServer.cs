using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace TiktokLiveRec.Core;

internal sealed partial class LivePreviewProxyServer : IDisposable
{
    private const string LoopbackHost = "127.0.0.1";
    private static readonly Lazy<LivePreviewProxyServer> _instance = new(() => new LivePreviewProxyServer());
    private static readonly HttpClient _httpClient = CreateHttpClient();

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _listenTask;
    private readonly int _port;
    private bool _disposed;

    private LivePreviewProxyServer()
    {
        _port = AllocatePort();
        _listener.Prefixes.Add($"http://{LoopbackHost}:{_port}/");
        _listener.Start();
        _listenTask = Task.Run(ListenLoopAsync);
    }

    public static LivePreviewProxyServer Instance => _instance.Value;

    public string CreateProxyUrl(string upstreamUrl, PreviewStreamKind streamKind)
    {
        string encodedUrl = Uri.EscapeDataString(upstreamUrl);
        string kind = streamKind == PreviewStreamKind.Hls ? "hls" : "flv";
        return $"http://{LoopbackHost}:{_port}/proxy/{kind}?url={encodedUrl}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        _cts.Dispose();
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext? context = null;

            try
            {
                context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context));
            }
            catch (HttpListenerException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                if (_cts.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            AddCorsHeaders(context.Response);

            if (context.Request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                context.Response.Close();
                return;
            }

            string? targetUrl = context.Request.QueryString["url"];
            if (string.IsNullOrWhiteSpace(targetUrl) || !Uri.TryCreate(targetUrl, UriKind.Absolute, out Uri? upstreamUri))
            {
                await WriteErrorAsync(context.Response, HttpStatusCode.BadRequest, "无效的直播流地址");
                return;
            }

            bool isHls = context.Request.Url?.AbsolutePath.Contains("/proxy/hls", StringComparison.OrdinalIgnoreCase) == true;
            if (isHls)
            {
                await ProxyHlsAsync(context.Response, upstreamUri, context.Request.Url!);
                return;
            }

            await ProxyBinaryStreamAsync(context.Response, upstreamUri);
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(context.Response, HttpStatusCode.BadGateway, $"预览代理失败: {ex.Message}");
        }
    }

    private static async Task ProxyHlsAsync(HttpListenerResponse response, Uri upstreamUri, Uri requestUri)
    {
        using HttpResponseMessage upstreamResponse = await SendUpstreamAsync(upstreamUri, HttpCompletionOption.ResponseHeadersRead);
        string content = await upstreamResponse.Content.ReadAsStringAsync();
        string rewritten = RewriteHlsManifest(content, upstreamUri, requestUri);

        response.StatusCode = (int)upstreamResponse.StatusCode;
        response.ContentType = "application/vnd.apple.mpegurl";
        byte[] bytes = Encoding.UTF8.GetBytes(rewritten);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static async Task ProxyBinaryStreamAsync(HttpListenerResponse response, Uri upstreamUri)
    {
        using HttpResponseMessage upstreamResponse = await SendUpstreamAsync(upstreamUri, HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode = (int)upstreamResponse.StatusCode;
        response.SendChunked = true;

        if (upstreamResponse.Content.Headers.ContentType?.ToString() is string contentType && !string.IsNullOrWhiteSpace(contentType))
        {
            response.ContentType = contentType;
        }

        await using Stream input = await upstreamResponse.Content.ReadAsStreamAsync();
        await input.CopyToAsync(response.OutputStream);
        response.Close();
    }

    private static async Task<HttpResponseMessage> SendUpstreamAsync(Uri upstreamUri, HttpCompletionOption completionOption)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, upstreamUri);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:109.0) Gecko/20100101 Firefox/115.0");
        request.Headers.Referrer = new Uri("https://live.douyin.com/");
        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.8,zh-TW;q=0.7,en-US;q=0.3");

        string cookie = Configurations.CookieChina.Get();
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        HttpResponseMessage response = await _httpClient.SendAsync(request, completionOption);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private static string RewriteHlsManifest(string manifest, Uri upstreamUri, Uri requestUri)
    {
        StringBuilder builder = new();
        string baseUrl = $"{requestUri.Scheme}://{requestUri.Authority}/proxy/hls?url=";

        foreach (string rawLine in manifest.Split(['\r', '\n'], StringSplitOptions.None))
        {
            string line = rawLine.Trim();

            if (string.IsNullOrWhiteSpace(line))
            {
                builder.AppendLine(rawLine);
                continue;
            }

            if (line.StartsWith("#EXT-X-KEY", StringComparison.OrdinalIgnoreCase) || line.StartsWith("#EXT-X-MAP", StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine(RewriteAttributeUri(rawLine, upstreamUri, baseUrl));
                continue;
            }

            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                builder.AppendLine(rawLine);
                continue;
            }

            Uri absoluteUri = new(upstreamUri, line);
            builder.AppendLine(baseUrl + Uri.EscapeDataString(absoluteUri.ToString()));
        }

        return builder.ToString();
    }

    private static string RewriteAttributeUri(string line, Uri upstreamUri, string baseUrl)
    {
        Match match = ManifestUriRegex().Match(line);
        if (!match.Success)
        {
            return line;
        }

        string originalValue = match.Groups["uri"].Value;
        Uri absoluteUri = new(upstreamUri, originalValue);
        string proxied = baseUrl + Uri.EscapeDataString(absoluteUri.ToString());
        return line.Replace(originalValue, proxied, StringComparison.Ordinal);
    }

    private static void AddCorsHeaders(HttpListenerResponse response)
    {
        response.AddHeader("Access-Control-Allow-Origin", "*");
        response.AddHeader("Access-Control-Allow-Methods", "GET, OPTIONS");
        response.AddHeader("Access-Control-Allow-Headers", "*");
        response.AddHeader("Cache-Control", "no-store");
    }

    private static async Task WriteErrorAsync(HttpListenerResponse response, HttpStatusCode statusCode, string message)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "text/plain; charset=utf-8";
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static HttpClient CreateHttpClient()
    {
        SocketsHttpHandler handler = new()
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    private static int AllocatePort()
    {
        using System.Net.Sockets.TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [GeneratedRegex("URI=\"(?<uri>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ManifestUriRegex();
}
