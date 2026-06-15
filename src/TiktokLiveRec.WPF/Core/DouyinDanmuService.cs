#nullable disable
using Microsoft.ClearScript.V8;
using ProtoBuf;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using TiktokLiveRec.Models;
using Cookie = System.Net.Cookie;

namespace TiktokLiveRec.Core;

internal sealed class DouyinDanmuService : IDouyinDanmuService
{
    private const string Version = "1.0.15";
    private static int _chatDebugCount;
    private static readonly Regex RoomIdRegex = new("(?:\"roomId\":\"|roomId\\\\\":\\\\\")([0-9]+)", RegexOptions.Compiled);
    private static readonly Regex UniqueIdRegex = new("(?:\"user_unique_id\":\"|user_unique_id\\\\\":\\\\\")([0-9]+)", RegexOptions.Compiled);
    private static readonly Regex NicknameRegex = new("(?:\"nickname\":\"|nickname\\\\\":\\\\\")([^\"\\\\]+)", RegexOptions.Compiled);
    private static readonly Regex AvatarRegex = new("(?:\"avatar_thumb\":\\{\"url_list\":\\[\"|avatar_thumb\\\\\":\\{\\\\\"url_list\\\\\":\\[\\\\\")([^\"\\\\]+)", RegexOptions.Compiled);
    private static readonly Regex StatusRegex = new("(?:\"status\":|status\\\\\":)([0-9])", RegexOptions.Compiled);
    private static readonly Regex FetchInternalExtRegex = new("(internal_src:dim\\|[^\\u0000-\\u001F]+)", RegexOptions.Compiled);
    private static readonly Regex FetchCursorRegex = new("(r-\\d+_d-\\d+_u-\\d+)", RegexOptions.Compiled);

    private readonly SemaphoreSlim _switchLock = new(1, 1);
    private readonly CookieContainer _cookieContainer = new();
    private readonly HttpClient _httpClient;
    // Passive user-id -> nickname/avatar cache, filled from messages that carry a full User
    // (Chat/Member/Like/Social...). Used to backfill the nickname for WebcastLightGiftMessage,
    // which only carries a numeric user_id.
    private readonly ConcurrentDictionary<ulong, DanmuUserInfo> _userCache = [];
    private readonly V8ScriptEngine _signEngine;
    private readonly V8ScriptEngine _aBogusEngine;
    private CancellationTokenSource? _lifetimeCts;
    private ClientWebSocket? _webSocket;
    private string? _currentRoomUrl;
    private string? _currentRoomNickname;
    private RoomConnectInfo? _currentConnectInfo;
    private Task? _supervisorTask;
    private ulong _lastLogId;
    private string _currentCursor = string.Empty;
    private string _currentInternalExt = string.Empty;
    private DateTimeOffset _lastReceiveAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHeartbeatSentAt = DateTimeOffset.MinValue;
    private DateTimeOffset _sessionStartedAt = DateTimeOffset.MinValue;
    private TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(5);
    private int _reconnectAttempt;
    private static readonly TimeSpan SessionRotationInterval = TimeSpan.FromMinutes(28);

    public event Action<DanmuMessage>? MessageReceived;
    public event Action<string, DanmuConnectionState>? ConnectionStateChanged;
    public event Action<string, string>? ErrorOccurred;

    public DouyinDanmuService()
    {
        HttpClientHandler handler = new()
        {
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = _cookieContainer,
            UseCookies = true,
            UseProxy = Configurations.IsUseProxy.Get() && !string.IsNullOrWhiteSpace(Configurations.ProxyUrl.Get()),
            Proxy = Configurations.IsUseProxy.Get() && !string.IsNullOrWhiteSpace(Configurations.ProxyUrl.Get())
                ? new WebProxy($"http://{Configurations.ProxyUrl.Get()}")
                : null,
        };

        _httpClient = new(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", GetUserAgent());
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://live.douyin.com/");

        string signScriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Danmu", "sign.js");
        if (!File.Exists(signScriptPath))
        {
            signScriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sign.js");
        }

        string aBogusScriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Danmu", "a_bogus.js");
        if (!File.Exists(aBogusScriptPath))
        {
            aBogusScriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "a_bogus.js");
        }

        _signEngine = new V8ScriptEngine();
        _signEngine.Execute(File.ReadAllText(signScriptPath, Encoding.UTF8));
        _aBogusEngine = new V8ScriptEngine();
        _aBogusEngine.Execute(File.ReadAllText(aBogusScriptPath, Encoding.UTF8));
    }

    public async Task SwitchRoomAsync(string? roomUrl, string? roomNickname, CancellationToken cancellationToken = default)
    {
        await _switchLock.WaitAsync(cancellationToken);
        try
        {
            await DisconnectInternalAsync();

            _currentRoomUrl = roomUrl;
            _currentRoomNickname = roomNickname;
            ResetRuntimeState();

            if (string.IsNullOrWhiteSpace(roomUrl))
            {
                return;
            }

            if (!roomUrl.Contains("douyin", StringComparison.OrdinalIgnoreCase))
            {
                ConnectionStateChanged?.Invoke(roomUrl, DanmuConnectionState.Unsupported);
                return;
            }

            _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            RoomConnectInfo info = await GetRoomConnectInfoAsync(roomUrl, _lifetimeCts.Token);

            // Warm up the gift catalog so the first gift message can resolve a name.
            // Fire-and-forget: it runs in the background and never blocks the connection.
            _ = DouyinGiftCatalog.Instance.LoadAsync();

            if (info.Status != 2)
            {
                _currentConnectInfo = info;
                ConnectionStateChanged?.Invoke(roomUrl, DanmuConnectionState.WaitingForLive);
                return;
            }

            _currentConnectInfo = info;
            _currentCursor = info.Cursor;
            _currentInternalExt = info.InternalExt;
            _supervisorTask = Task.Run(() => RunSupervisorAsync(info, _lifetimeCts.Token), _lifetimeCts.Token);
        }
        finally
        {
            _switchLock.Release();
        }
    }

    public Task DisconnectAsync() => DisconnectInternalAsync();

    private async Task DisconnectInternalAsync()
    {
        CancellationTokenSource? lifetimeCts = _lifetimeCts;
        Task? supervisorTask = _supervisorTask;
        ClientWebSocket? webSocket = _webSocket;

        _lifetimeCts = null;
        _supervisorTask = null;
        _webSocket = null;
        _currentConnectInfo = null;

        try
        {
            lifetimeCts?.Cancel();
        }
        catch
        {
        }

        if (webSocket != null)
        {
            try
            {
                if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "close", CancellationToken.None);
                }
            }
            catch
            {
            }

            webSocket.Dispose();
        }

        if (supervisorTask != null)
        {
            try
            {
                await supervisorTask;
            }
            catch
            {
            }
        }

        lifetimeCts?.Dispose();
        Debug.WriteLine("[Danmu] Disconnected");
    }

    private async Task<RoomConnectInfo> GetRoomConnectInfoAsync(string roomUrl, CancellationToken cancellationToken)
    {
        Uri roomUri = new(roomUrl);
        string roomIdAlias = roomUri.Segments.Last().Trim('/');
        string normalized = $"https://live.douyin.com/{roomIdAlias}";

        string html = await RequestHtmlAsync(normalized, cancellationToken);
        if (string.IsNullOrWhiteSpace(Extract(UniqueIdRegex, html)))
        {
            html = await RequestHtmlAsync(normalized, cancellationToken);
        }

        string normalizedHtml = NormalizeHtmlPayload(html);

        RoomConnectInfo info = new()
        {
            RoomUrl = roomUrl,
            RoomNickname = _currentRoomNickname ?? Extract(NicknameRegex, normalizedHtml),
            RoomId = Extract(RoomIdRegex, normalizedHtml),
            UniqueId = Extract(UniqueIdRegex, normalizedHtml),
            Avatar = DecodeUrl(Extract(AvatarRegex, normalizedHtml)),
            Status = int.TryParse(Extract(StatusRegex, normalizedHtml), out int status) ? status : 4,
        };

        if (string.IsNullOrWhiteSpace(info.RoomId) || string.IsNullOrWhiteSpace(info.UniqueId))
        {
            throw new InvalidOperationException("无法解析直播间连接信息。");
        }

        (string cursor, string internalExt) = await FetchImInfoAsync(info.RoomId, info.UniqueId, cancellationToken);
        info.Cursor = cursor;
        info.InternalExt = internalExt;
        return info;
    }

    private async Task<string> RequestHtmlAsync(string url, CancellationToken cancellationToken)
    {
        await EnsureDouyinCookiesAsync(cancellationToken);

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");

        string cookie = BuildCookieHeader();
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task EnsureDouyinCookiesAsync(CancellationToken cancellationToken)
    {
        if (_cookieContainer.GetCookies(new Uri("https://live.douyin.com/"))["ttwid"] != null)
        {
            return;
        }

        using HttpRequestMessage request = new(HttpMethod.Get, "https://live.douyin.com/");
        request.Headers.TryAddWithoutValidation("User-Agent", GetUserAgent());
        request.Headers.TryAddWithoutValidation("Referer", "https://live.douyin.com/");
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        _ = await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private string BuildCookieHeader()
    {
        List<string> cookies = [];
        string configured = Configurations.CookieChina.Get();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            cookies.Add(configured.Trim().TrimEnd(';'));
        }

        CookieCollection containerCookies = _cookieContainer.GetCookies(new Uri("https://live.douyin.com/"));
        Cookie? ttwid = containerCookies["ttwid"];
        if (ttwid != null && !string.IsNullOrWhiteSpace(ttwid.Value))
        {
            cookies.Add($"ttwid={ttwid.Value}");
        }

        // Add fields that Douyin requires (since ~Dec 2025) to receive gift messages.
        // x-web-secsdk-uid: a per-session id; missing it can cause the server to filter out
        // sensitive messages (gifts) even when other cookie fields are present.
        AppendRequiredGiftCookies(cookies);

        cookies.Add($"msToken={GenerateMsToken(182)}");
        cookies.Add("__ac_nonce=0123407cc00a9e438deb4");
        return string.Join("; ", cookies.Distinct());
    }

    /// <summary>
    /// Appends cookie fields that Douyin requires to deliver gift messages (added ~Dec 2025).
    /// Only adds fields the user hasn't already supplied via CookieChina.
    /// </summary>
    private static void AppendRequiredGiftCookies(List<string> cookies)
    {
        string joined = string.Join(';', cookies);
        if (!joined.Contains("x-web-secsdk-uid", StringComparison.Ordinal))
        {
            cookies.Add($"x-web-secsdk-uid={Guid.NewGuid():D}");
        }
        if (!joined.Contains("__live_version__", StringComparison.Ordinal))
        {
            cookies.Add("__live_version__=%221.1.4.7838%22");
        }
        if (!joined.Contains("live_use_vvc", StringComparison.Ordinal))
        {
            cookies.Add("live_use_vvc=%22false%22");
        }
    }

    private async Task<(string cursor, string internalExt)> FetchImInfoAsync(string roomId, string uniqueId, CancellationToken cancellationToken)
    {
        string msToken = GenerateMsToken(182);
        Dictionary<string, string?> parameters = new()
        {
            ["aid"] = "6383",
            ["app_name"] = "douyin_web",
            ["browser_language"] = "zh-CN",
            ["browser_name"] = "Mozilla",
            ["browser_online"] = "true",
            ["browser_platform"] = "Win32",
            ["browser_version"] = GetUserAgent(),
            ["cookie_enabled"] = "true",
            ["cursor"] = string.Empty,
            ["device_platform"] = "web",
            ["did_rule"] = "3",
            ["endpoint"] = "live_pc",
            ["fetch_rule"] = "1",
            ["identity"] = "audience",
            ["insert_task_id"] = string.Empty,
            ["internal_ext"] = string.Empty,
            ["last_rtt"] = "0",
            ["live_id"] = "1",
            ["live_reason"] = string.Empty,
            ["need_persist_msg_count"] = "15",
            ["resp_content_type"] = "protobuf",
            ["screen_height"] = "1080",
            ["screen_width"] = "1920",
            ["support_wrds"] = "1",
            ["tz_name"] = "Asia/Shanghai",
            ["version_code"] = "180800",
            ["msToken"] = msToken,
            ["room_id"] = roomId,
            ["user_unique_id"] = uniqueId,
            ["live_pc"] = roomId,
        };
        string queryString = BuildQueryString(parameters);
        parameters["a_bogus"] = GetABogus(queryString);
        queryString = BuildQueryString(parameters);
        using HttpRequestMessage request = new(HttpMethod.Get, $"https://live.douyin.com/webcast/im/fetch/?{queryString}");
        request.Headers.TryAddWithoutValidation("User-Agent", GetUserAgent());
        request.Headers.TryAddWithoutValidation("Referer", $"https://live.douyin.com/{roomId}");
        request.Headers.TryAddWithoutValidation("Accept", "application/protobuffer,application/octet-stream;q=0.9,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        string cookie = BuildCookieHeader();
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            byte[] content = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            try
            {
                using MemoryStream stream = new(content);
                DouyinResponse parsed = Serializer.Deserialize<DouyinResponse>(stream);
                if (!string.IsNullOrWhiteSpace(parsed.Cursor) && !string.IsNullOrWhiteSpace(parsed.InternalExt))
                {
                    LogFetchImInfo("primary", roomId, uniqueId, content, parsed.Cursor, parsed.InternalExt);
                    return (parsed.Cursor, parsed.InternalExt);
                }
            }
            catch (Exception ex)
            {
                LogFetchImInfo("primary-exception", roomId, uniqueId, content, string.Empty, ex.Message);
            }

            if (TryExtractFetchInfo(content, out string extractedCursor, out string extractedInternalExt))
            {
                LogFetchImInfo("fallback-extract", roomId, uniqueId, content, extractedCursor, extractedInternalExt);
                return (extractedCursor, extractedInternalExt);
            }
        }
        catch (Exception ex)
        {
            LogFetchImInfo("http-exception", roomId, uniqueId, [], string.Empty, ex.Message);
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string fbCursor = $"r-{now}_d-1_u-1";
        string fbInternalExt = $"internal_src:dim|wss_push_room_id:{roomId}|wss_push_did:{uniqueId}|first_req_ms:{now}|fetch_time:{now}|seq:1|wss_info:0-{now}-0-0";
        LogFetchImInfo("fallback-generated", roomId, uniqueId, [], fbCursor, fbInternalExt);
        return (fbCursor, fbInternalExt);
    }

    private static void LogFetchImInfo(string source, string roomId, string uniqueId, byte[] content, string cursor, string internalExt)
    {
        try
        {
            string debugDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TiktokLiveRec", "debug_proto");
            Directory.CreateDirectory(debugDir);
            StringBuilder sb = new();
            sb.AppendLine($"=== [{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] FetchImInfo source={source} roomId={roomId} uniqueId={uniqueId} ===");
            sb.AppendLine($"  rawContentLen={content.Length}");
            sb.AppendLine($"  cursor={cursor}");
            sb.AppendLine($"  internalExt={internalExt}");
            sb.AppendLine($"  hasWrdsV={(internalExt?.Contains("wrds_v") ?? false)}");
            if (content.Length > 0 && content.Length <= 4096)
            {
                sb.AppendLine($"  rawHex={BitConverter.ToString(content).Replace("-", " ")}");
            }
            else if (content.Length > 4096)
            {
                sb.AppendLine($"  rawHexFirst512={BitConverter.ToString(content.Take(512).ToArray()).Replace("-", " ")}");
            }
            sb.AppendLine();
            File.AppendAllText(Path.Combine(debugDir, "fetch_im_info.log"), sb.ToString());
        }
        catch
        {
        }
    }

    private string BuildSocketUrl(RoomConnectInfo info)
    {
        string signature = GetSignature(info.RoomId, info.UniqueId);
        Dictionary<string, string?> parameters = new()
        {
            ["aid"] = "6383",
            ["app_name"] = "douyin_web",
            ["browser_language"] = "zh-CN",
            ["browser_name"] = "Mozilla",
            ["browser_online"] = "true",
            ["browser_platform"] = "Win32",
            ["browser_version"] = GetUserAgentVersion(),
            ["compress"] = "gzip",
            ["cookie_enabled"] = "true",
            ["cursor"] = info.Cursor,
            ["device_platform"] = "web",
            ["did_rule"] = "3",
            ["endpoint"] = "live_pc",
            ["heartbeatDuration"] = "0",
            ["host"] = "https://live.douyin.com",
            ["identity"] = "audience",
            ["im_path"] = "/webcast/im/fetch/",
            ["insert_task_id"] = string.Empty,
            ["internal_ext"] = info.InternalExt,
            ["live_id"] = "1",
            ["live_reason"] = string.Empty,
            ["need_persist_msg_count"] = "15",
            ["room_id"] = info.RoomId,
            ["screen_height"] = "1080",
            ["screen_width"] = "1920",
            ["support_wrds"] = "1",
            ["tz_name"] = "Etc/GMT-8",
            ["update_version_code"] = Version,
            ["user_unique_id"] = info.UniqueId,
            ["version_code"] = "180800",
            ["webcast_sdk_version"] = Version,
            ["signature"] = signature,
        };

        string queryString = string.Join("&", parameters.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value ?? string.Empty)}"));
        return $"wss://webcast100-ws-web-hl.douyin.com/webcast/im/push/v2/?{queryString}";
    }

    private async Task RunSupervisorAsync(RoomConnectInfo info, CancellationToken cancellationToken)
    {
        bool hadSuccessfulConnection = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            RoomConnectInfo connectInfo = await PrepareConnectInfoAsync(info, hadSuccessfulConnection, cancellationToken);
            if (connectInfo.Status != 2)
            {
                ConnectionStateChanged?.Invoke(connectInfo.RoomUrl, DanmuConnectionState.WaitingForLive);
                return;
            }

            DanmuConnectionState state = hadSuccessfulConnection || _reconnectAttempt > 0
                ? DanmuConnectionState.Reconnecting
                : DanmuConnectionState.Connecting;
            ConnectionStateChanged?.Invoke(connectInfo.RoomUrl, state);

            try
            {
                SessionEndReason result = await RunConnectedSessionAsync(connectInfo, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (result == SessionEndReason.WaitingForLive)
                {
                    ConnectionStateChanged?.Invoke(connectInfo.RoomUrl, DanmuConnectionState.WaitingForLive);
                    return;
                }

                hadSuccessfulConnection = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Danmu] Session failed room={connectInfo.RoomUrl}: {ex}");
                if (!hadSuccessfulConnection && _reconnectAttempt == 0)
                {
                    ConnectionStateChanged?.Invoke(connectInfo.RoomUrl, DanmuConnectionState.Failed);
                    ErrorOccurred?.Invoke(connectInfo.RoomUrl, ex.Message);
                }

                hadSuccessfulConnection = true;
            }

            _reconnectAttempt++;
            ConnectionStateChanged?.Invoke(connectInfo.RoomUrl, DanmuConnectionState.Reconnecting);
            Debug.WriteLine($"[Danmu] Reconnecting room={connectInfo.RoomUrl} attempt={_reconnectAttempt}");

            try
            {
                await Task.Delay(GetReconnectDelay(_reconnectAttempt), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<RoomConnectInfo> PrepareConnectInfoAsync(RoomConnectInfo baselineInfo, bool refreshAllowed, CancellationToken cancellationToken)
    {
        RoomConnectInfo info = new()
        {
            RoomUrl = baselineInfo.RoomUrl,
            RoomNickname = baselineInfo.RoomNickname,
            RoomId = baselineInfo.RoomId,
            UniqueId = baselineInfo.UniqueId,
            Avatar = baselineInfo.Avatar,
            Status = baselineInfo.Status,
            Cursor = string.IsNullOrWhiteSpace(_currentCursor) ? baselineInfo.Cursor : _currentCursor,
            InternalExt = string.IsNullOrWhiteSpace(_currentInternalExt) ? baselineInfo.InternalExt : _currentInternalExt,
        };

        if (!refreshAllowed || _reconnectAttempt < 3)
        {
            return info;
        }

        RoomConnectInfo refreshed = await GetRoomConnectInfoAsync(info.RoomUrl, cancellationToken);
        _currentCursor = refreshed.Cursor;
        _currentInternalExt = refreshed.InternalExt;
        _currentConnectInfo = refreshed;
        return refreshed;
    }

    private async Task<SessionEndReason> RunConnectedSessionAsync(RoomConnectInfo roomInfo, CancellationToken cancellationToken)
    {
        ClientWebSocket webSocket = new();
        _webSocket = webSocket;
        _currentConnectInfo = roomInfo;

        webSocket.Options.SetRequestHeader("User-Agent", GetUserAgent());
        webSocket.Options.SetRequestHeader("Referer", $"https://live.douyin.com/{roomInfo.RoomId}");
        webSocket.Options.SetRequestHeader("Origin", "https://live.douyin.com");
        webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        // Build cookie string manually — must be sent as raw header because
        // CookieContainer domain-matches cookies to live.douyin.com, but
        // the WebSocket connects to webcast100-ws-web-hl.douyin.com.
        // This matches the Python reference's behavior: cookie=cookie_str
        List<string> wsCookies = [];
        string configuredCookies = Configurations.CookieChina.Get();
        if (!string.IsNullOrWhiteSpace(configuredCookies))
        {
            wsCookies.Add(configuredCookies.Trim().TrimEnd(';'));
        }

        CookieCollection containerCookies = _cookieContainer.GetCookies(new Uri("https://live.douyin.com/"));
        foreach (Cookie? cookie in containerCookies)
        {
            if (cookie != null && !string.IsNullOrWhiteSpace(cookie.Value))
            {
                wsCookies.Add($"{cookie.Name}={cookie.Value}");
            }
        }

        // Add the same gift-related cookie fields as the HTTP fetch path so the WebSocket
        // connection also satisfies Douyin's gift-delivery requirements.
        AppendRequiredGiftCookies(wsCookies);

        wsCookies.Add($"msToken={GenerateMsToken(182)}");
        string cookieHeader = string.Join("; ", wsCookies.Distinct());
        webSocket.Options.SetRequestHeader("Cookie", cookieHeader);
        Debug.WriteLine($"[Danmu] WSS Cookie header len={cookieHeader.Length}");

        string socketUrl = BuildSocketUrl(roomInfo);
        Debug.WriteLine($"[Danmu] Connecting room={roomInfo.RoomUrl}");
        await webSocket.ConnectAsync(new Uri(socketUrl), cancellationToken);
        Debug.WriteLine($"[Danmu] Connected room={roomInfo.RoomUrl}");

        _reconnectAttempt = 0;
        _lastLogId = 0;
        _lastReceiveAt = DateTimeOffset.UtcNow;
        _lastHeartbeatSentAt = DateTimeOffset.MinValue;
        _sessionStartedAt = DateTimeOffset.UtcNow;
        _heartbeatInterval = TimeSpan.FromSeconds(5);

        ConnectionStateChanged?.Invoke(roomInfo.RoomUrl, DanmuConnectionState.Connected);

        using CancellationTokenSource sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<SessionEndReason> receiveTask = ReceiveLoopAsync(roomInfo, webSocket, sessionCts.Token);
        Task<SessionEndReason> heartbeatTask = HeartbeatLoopAsync(roomInfo, webSocket, sessionCts.Token);
        Task<SessionEndReason> rotationTask = RotationLoopAsync(roomInfo, sessionCts.Token);

        Task<SessionEndReason> completedTask = await Task.WhenAny(receiveTask, heartbeatTask, rotationTask);
        SessionEndReason result = await completedTask;
        sessionCts.Cancel();

        try { await Task.WhenAll(receiveTask, heartbeatTask, rotationTask); } catch { }

        try
        {
            if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "rotate", CancellationToken.None);
            }
        }
        catch
        {
        }

        webSocket.Dispose();
        if (ReferenceEquals(_webSocket, webSocket))
        {
            _webSocket = null;
        }

        if (result == SessionEndReason.Completed)
        {
            return SessionEndReason.Reconnect;
        }

        return result;
    }

    private async Task<SessionEndReason> ReceiveLoopAsync(RoomConnectInfo roomInfo, ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        try
        {
            byte[] buffer = new byte[1024 * 128];
            while (!cancellationToken.IsCancellationRequested && webSocket.State == WebSocketState.Open)
            {
                using MemoryStream payload = new();
                WebSocketReceiveResult result;
                do
                {
                    result = await webSocket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Debug.WriteLine($"[Danmu] Socket close room={roomInfo.RoomUrl} status={result.CloseStatus} description={result.CloseStatusDescription}");
                        return cancellationToken.IsCancellationRequested ? SessionEndReason.Completed : SessionEndReason.Reconnect;
                    }

                    if (result.MessageType != WebSocketMessageType.Binary)
                    {
                        continue;
                    }

                    payload.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                _lastReceiveAt = DateTimeOffset.UtcNow;
                payload.Position = 0;
                DouyinPushFrame frame = Serializer.Deserialize<DouyinPushFrame>(payload);
                _lastLogId = frame.LogId;

                if (frame.Payload.Length == 0)
                {
                    continue;
                }

                byte[] responseBytes = frame.Payload;
                bool isGzip = string.Equals(frame.PayloadEncoding, "gzip", StringComparison.OrdinalIgnoreCase)
                    || (frame.HeadersList.TryGetValue("compress_type", out string compressType)
                        && string.Equals(compressType, "gzip", StringComparison.OrdinalIgnoreCase));

                if (isGzip)
                {
                    using MemoryStream compressed = new(frame.Payload);
                    using GZipStream gzip = new(compressed, CompressionMode.Decompress);
                    using MemoryStream unzipped = new();
                    await gzip.CopyToAsync(unzipped, cancellationToken);
                    responseBytes = unzipped.ToArray();
                }
                else if (frame.Payload.Length > 2 && frame.Payload[0] == 0x1F && frame.Payload[1] == 0x8B)
                {
                    // Fallback: detect gzip magic bytes when header doesn't indicate gzip
                    try
                    {
                        using MemoryStream compressed = new(frame.Payload);
                        using GZipStream gzip = new(compressed, CompressionMode.Decompress);
                        using MemoryStream unzipped = new();
                        await gzip.CopyToAsync(unzipped, cancellationToken);
                        responseBytes = unzipped.ToArray();
                    }
                    catch
                    {
                        // Not actually gzip, keep original bytes
                    }
                }

                DouyinResponse response;
                using (MemoryStream responseStream = new(responseBytes))
                {
                    response = Serializer.Deserialize<DouyinResponse>(responseStream);
                }

                // === FULL RESPONSE DIAGNOSTIC LOG ===
                // Dump the entire Response state and search raw bytes for gift/emoji strings
                try
                {
                    string debugDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TiktokLiveRec", "debug_proto");
                    Directory.CreateDirectory(debugDir);
                    StringBuilder sb = new();
                    sb.AppendLine($"=== [{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] room={roomInfo.RoomNickname} logId={frame.LogId} ===");
                    sb.AppendLine($"Frame: PayloadEncoding={frame.PayloadEncoding} PayloadType={frame.PayloadType} PayloadLen={frame.Payload.Length} HeadersList=[{string.Join(",", frame.HeadersList.Select(kv => kv.Key + "=" + kv.Value))}]");
                    sb.AppendLine($"ResponseBytes: len={responseBytes.Length}");
                    sb.AppendLine($"Response: cursor={response.Cursor} internalExt={response.InternalExt}");
                    sb.AppendLine($"Response: fetchInterval={response.FetchInterval} now={response.Now} fetchType={response.FetchType} heartbeatDuration={response.HeartbeatDuration} needAck={response.NeedAck} pushServer={response.PushServer} liveCursor={response.LiveCursor} historyNoMore={response.HistoryNoMore}");
                    sb.AppendLine($"Response.RouteParams: [{string.Join(",", response.RouteParams.Select(kv => kv.Key + "=" + kv.Value))}]");
                    sb.AppendLine($"Response.MessagesList.Count={response.MessagesList.Count}");
                    int idx = 0;
                    foreach (DouyinInnerMessage m in response.MessagesList)
                    {
                        sb.AppendLine($"  [{idx}] method={m.Method} msgId={m.MsgId} msgType={m.MsgType} offset={m.Offset} needWrdsStore={m.NeedWrdsStore} wrdsVersion={m.WrdsVersion} wrdsSubKey={m.WrdsSubKey} payloadLen={m.Payload.Length}");
                        idx++;
                    }
                    // Raw byte string search — does the raw response contain gift/emoji method names?
                    string rawAsUtf8 = Encoding.UTF8.GetString(responseBytes);
                    string[] needles = ["WebcastGiftMessage", "WebcastLightGiftMessage", "WebcastEmojiChatMessage", "GiftMessage", "LightGiftMessage", "EmojiChat"];
                    foreach (string needle in needles)
                    {
                        int pos = rawAsUtf8.IndexOf(needle, StringComparison.Ordinal);
                        if (pos >= 0)
                        {
                            sb.AppendLine($"  RAW HIT: '{needle}' found at byte offset {pos}");
                        }
                    }
                    sb.AppendLine();
                    File.AppendAllText(Path.Combine(debugDir, "response_dump.log"), sb.ToString());
                }
                catch
                {
                }

                UpdateResumeCursor(frame, response);
                UpdateHeartbeatInterval(roomInfo.RoomUrl, response);

                string ackExt = string.IsNullOrWhiteSpace(_currentInternalExt) ? response.InternalExt : _currentInternalExt;
                if (response.NeedAck)
                {
                    await SendAckAsync(webSocket, ackExt, cancellationToken);
                }

foreach (DouyinInnerMessage message in response.MessagesList)
                {
                    // Log every received message type to diagnose missing messages
                    try
                    {
                        string debugDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TiktokLiveRec", "debug_proto");
                        Directory.CreateDirectory(debugDir);
                        File.AppendAllText(Path.Combine(debugDir, "messages_received.log"),
                            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] room={roomInfo.RoomNickname} method={message.Method} len={message.Payload.Length}{Environment.NewLine}");
                    }
                    catch
                    {
                    }

                    bool isGift = message.Method is "WebcastLightGiftMessage" or "WebcastGiftMessage";
                    bool isEmoji = message.Method == "WebcastEmojiChatMessage";
                    bool shouldDecode = isGift || isEmoji || (message.Method == "WebcastChatMessage" && Interlocked.Increment(ref _chatDebugCount) <= 20);
                    if (shouldDecode)
                    {
                        try
                        {
                            string debugDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TiktokLiveRec", "debug_proto");
                            Directory.CreateDirectory(debugDir);
                            string decoded = DecodeProtobufFields(message.Payload);
                            string line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [{message.Method}] len={message.Payload.Length}{Environment.NewLine}{decoded}{Environment.NewLine}";
                            File.AppendAllText(Path.Combine(debugDir, "proto_decoded.log"), line);
                        }
                        catch
                        {
                        }
                    }

                    DanmuMessage mapped = MapMessage(roomInfo, message);
                    if (mapped != null)
                    {
                        MessageReceived?.Invoke(mapped);
                    }
                }
            }

            return SessionEndReason.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SessionEndReason.Completed;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Danmu] ReceiveLoop failed room={roomInfo.RoomUrl}: {ex}");
            throw new InvalidOperationException($"ReceiveLoop: {ex.Message}", ex);
        }
    }

    private async Task<SessionEndReason> HeartbeatLoopAsync(RoomConnectInfo roomInfo, ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_heartbeatInterval, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                await SendHeartbeatAsync(roomInfo.RoomUrl, webSocket, cancellationToken);
            }

            return SessionEndReason.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SessionEndReason.Completed;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Danmu] Heartbeat failed room={roomInfo.RoomUrl}: {ex}");
            throw new InvalidOperationException($"Heartbeat: {ex.Message}", ex);
        }
    }

    private async Task<SessionEndReason> RotationLoopAsync(RoomConnectInfo roomInfo, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SessionRotationInterval, cancellationToken);
            Debug.WriteLine($"[Danmu] Planned rotation room={roomInfo.RoomUrl} startedAt={_sessionStartedAt:HH:mm:ss} now={DateTimeOffset.UtcNow:HH:mm:ss}");
            return SessionEndReason.Reconnect;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SessionEndReason.Completed;
        }
    }

    private async Task SendAckAsync(ClientWebSocket webSocket, string internalExt, CancellationToken cancellationToken)
    {
        DouyinClientFrame frame = new()
        {
            LogId = _lastLogId,
            PayloadType = "ack",
            Payload = Encoding.UTF8.GetBytes(internalExt ?? string.Empty),
        };

        using MemoryStream stream = new();
        Serializer.Serialize(stream, frame);
        Debug.WriteLine($"[Danmu] Ack room={_currentRoomUrl} logId={_lastLogId}");
        if (webSocket.State == WebSocketState.Open)
        {
            await webSocket.SendAsync(stream.ToArray(), WebSocketMessageType.Binary, true, cancellationToken);
        }
    }

    private async Task SendHeartbeatAsync(string roomUrl, ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        DouyinClientFrame frame = new()
        {
            PayloadType = "hb",
            Payload = [],
        };

        using MemoryStream stream = new();
        Serializer.Serialize(stream, frame);
        _lastHeartbeatSentAt = DateTimeOffset.UtcNow;
        Debug.WriteLine($"[Danmu] Heartbeat room={roomUrl} at={_lastHeartbeatSentAt:HH:mm:ss} interval={_heartbeatInterval.TotalSeconds:0.##}s");
        if (webSocket.State == WebSocketState.Open)
        {
            await webSocket.SendAsync(stream.ToArray(), WebSocketMessageType.Binary, true, cancellationToken);
        }
    }

    private void UpdateResumeCursor(DouyinPushFrame frame, DouyinResponse response)
    {
        string cursor = string.Empty;
        string internalExt = string.Empty;

        if (frame.HeadersList.TryGetValue("im-cursor", out string headerCursor))
        {
            cursor = headerCursor;
        }

        if (frame.HeadersList.TryGetValue("im-internal_ext", out string headerInternalExt))
        {
            internalExt = headerInternalExt;
        }

        if (string.IsNullOrWhiteSpace(cursor))
        {
            cursor = response.Cursor;
        }

        if (string.IsNullOrWhiteSpace(internalExt))
        {
            internalExt = response.InternalExt;
        }

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            _currentCursor = cursor;
        }

        if (!string.IsNullOrWhiteSpace(internalExt))
        {
            _currentInternalExt = internalExt;
        }

        Debug.WriteLine($"[Danmu] Resume cursor room={_currentRoomUrl} cursor={_currentCursor} internalExt={_currentInternalExt}");
    }

    private void UpdateHeartbeatInterval(string roomUrl, DouyinResponse response)
    {
        if (response.HeartbeatDuration <= 0)
        {
            return;
        }

        TimeSpan updated = TimeSpan.FromMilliseconds(Math.Max(5000, (long)response.HeartbeatDuration));
        if (updated != _heartbeatInterval)
        {
            _heartbeatInterval = updated;
            Debug.WriteLine($"[Danmu] Heartbeat interval updated room={roomUrl} interval={_heartbeatInterval.TotalMilliseconds}ms");
        }
    }

    private void ResetRuntimeState()
    {
        _webSocket = null;
        _currentConnectInfo = null;
        _lastLogId = 0;
        _currentCursor = string.Empty;
        _currentInternalExt = string.Empty;
        _lastReceiveAt = DateTimeOffset.MinValue;
        _lastHeartbeatSentAt = DateTimeOffset.MinValue;
        _sessionStartedAt = DateTimeOffset.MinValue;
        _heartbeatInterval = TimeSpan.FromSeconds(5);
        _reconnectAttempt = 0;
    }

    private static TimeSpan GetReconnectDelay(int attempt) => attempt switch
    {
        <= 1 => TimeSpan.FromSeconds(1),
        2 => TimeSpan.FromSeconds(3),
        3 => TimeSpan.FromSeconds(5),
        _ => TimeSpan.FromSeconds(10),
    };

    [System.Diagnostics.Conditional("DEBUG")]
    private static void DebugLog(string message)
    {
        Debug.WriteLine(message);
    }

    private DanmuMessage? MapMessage(RoomConnectInfo roomInfo, DouyinInnerMessage message)
    {
        try
        {
            return message.Method switch
            {
                "WebcastChatMessage" => MapChat(roomInfo, message),
                "WebcastGiftMessage" => MapGift(roomInfo, message),
                "WebcastLightGiftMessage" => MapLightGift(roomInfo, message),
                "WebcastLikeMessage" => MapLike(roomInfo, message),
                "WebcastMemberMessage" => MapMember(roomInfo, message),
                "WebcastSocialMessage" => MapSocial(roomInfo, message),
                "WebcastEmojiChatMessage" => MapEmoji(roomInfo, message),
                "WebcastRoomUserSeqMessage" => MapRoomUserSeq(roomInfo, message),
                "WebcastRoomStatsMessage" => MapRoomStats(roomInfo, message),
                "WebcastRoomRankMessage" => MapRoomRank(roomInfo, message),
                "WebcastFansclubMessage" => MapFansClub(roomInfo, message),
                "WebcastControlMessage" => MapControl(roomInfo, message),
                _ => LogUnknownGift(roomInfo, message),
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Danmu] MapMessage failed method={message.Method}: {ex.Message}");
            try
            {
                string debugDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TiktokLiveRec", "debug_proto");
                Directory.CreateDirectory(debugDir);
                string hex = BitConverter.ToString(message.Payload.Take(Math.Min(message.Payload.Length, 512)).ToArray()).Replace("-", " ");
                File.AppendAllText(Path.Combine(debugDir, "map_failures.log"),
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] room={roomInfo.RoomNickname} method={message.Method} len={message.Payload.Length} ex={ex.GetType().Name}: {ex.Message}{Environment.NewLine}hex={hex}{Environment.NewLine}stack={ex.StackTrace}{Environment.NewLine}{Environment.NewLine}");
            }
            catch
            {
            }
            return null;
        }
    }

    private DanmuMessage MapChat(RoomConnectInfo roomInfo, DouyinInnerMessage envelope)
    {
        DouyinChatMessage data = Deserialize<DouyinChatMessage>(envelope.Payload);
        ulong? ts = data.EventTime > 0 ? data.EventTime : data.Common?.CreateTime > 0 ? data.Common.CreateTime : null;
        // Prefer RtfContent (rich text with image segments) over plain Content when available
        List<DanmuInlineSegment>? segments = BuildRichSegments(data.RtfContent);

        // Debug: log RtfContent parsing for first few chat messages with rtfContent
        if (data.RtfContent != null && data.RtfContent.PiecesList.Count > 0)
        {
            try
            {
                string debugDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TiktokLiveRec", "debug_proto");
                Directory.CreateDirectory(debugDir);
                StringBuilder sb = new();
                sb.AppendLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] Chat RtfContent: content={data.Content} pieces={data.RtfContent.PiecesList.Count}");
                for (int i = 0; i < data.RtfContent.PiecesList.Count; i++)
                {
                    DouyinTextPiece p = data.RtfContent.PiecesList[i];
                    string url = p.ImageValue?.Image?.UrlListList?.FirstOrDefault() ?? "";
                    string name = p.ImageValue?.Image?.Content?.Name ?? "";
                    sb.AppendLine($"  piece[{i}]: str=\"{p.StringValue}\" imgName=\"{name}\" imgUrl=\"{url}\"");
                }
                File.AppendAllText(Path.Combine(debugDir, "chat_rtf_debug.log"), sb.ToString());
            }
            catch { }
        }

        string content = segments != null
            ? string.Concat(segments.Select(s => s.IsImage ? "[" + (s.ImageName ?? "图") + "]" : s.Text))
            : (!string.IsNullOrWhiteSpace(data.Content) ? data.Content : BuildRichText(data.RtfContent));
        // If RtfContent didn't produce segments, try parsing [emoji_name] patterns from plain content
        segments ??= ParseChatEmojiSegments(content);

        DanmuMessage mapped = CreateBase(roomInfo, envelope, DanmuMessageMethod.Chat, data.User, ts, $"{data.User?.NickName}: {content}", content);
        mapped.Segments = segments;
        return mapped;
    }

    private DanmuMessage MapGift(RoomConnectInfo roomInfo, DouyinInnerMessage envelope)
    {
        DouyinGiftMessage data = Deserialize<DouyinGiftMessage>(envelope.Payload);
        string count = (data.RepeatCount > 0 ? data.RepeatCount : data.ComboCount).ToString();
        DanmuMessage mapped = CreateBase(roomInfo, envelope, DanmuMessageMethod.Gift, data.User, data.Common?.CreateTime,
            $"{data.User?.NickName} 送出 {data.Gift?.Name} x{count}",
            $"{data.Gift?.Name} x{count}");
        mapped.Gift = new DanmuGiftInfo
        {
            Name = data.Gift?.Name,
            IconUrl = data.Gift?.Image?.UrlListList.FirstOrDefault(),
            Count = count,
            Price = data.Gift?.DiamondCount.ToString(),
        };
        mapped.GiftName = mapped.Gift.Name;
        mapped.GiftIconUrl = mapped.Gift.IconUrl;
        mapped.GiftCount = mapped.Gift.Count;
        mapped.GiftPrice = mapped.Gift.Price;
        LogGift("MapGift", roomInfo.RoomUrl, envelope.Method, mapped);
        return mapped;
    }

    private DanmuMessage MapLightGift(RoomConnectInfo roomInfo, DouyinInnerMessage envelope)
    {
        DouyinLightGiftMessage data = Deserialize<DouyinLightGiftMessage>(envelope.Payload);

        // WebcastLightGiftMessage is a "lightweight" gift event: it carries only a numeric
        // user_id (field 5) and gift metadata (id/icon/count), without a nickname or a localized
        // gift name. Resolve both from the catalog / passive user cache with graceful fallbacks.

        // --- Gift name: catalog (static table + remote) -> gift struct -> empty (icon-only) ---
        ulong giftId = data.GiftInfo?.GiftId ?? data.Gift?.Id ?? 0;
        string giftName = string.Empty;
        // DouyinGiftInfo.DiamondCount is ulong; DouyinGiftStruct.DiamondCount is uint. Normalize to ulong.
        ulong diamondCount = data.GiftInfo?.DiamondCount ?? data.Gift?.DiamondCount ?? 0u;
        if (DouyinGiftCatalog.Instance.TryGetEntry(giftId, out DouyinGiftCatalog.GiftEntry? catalogEntry))
        {
            giftName = catalogEntry.Name;
            if (diamondCount == 0)
            {
                diamondCount = catalogEntry.DiamondCount;
            }
        }
        // Fallback to the (rarely populated) struct name from the payload itself.
        if (string.IsNullOrWhiteSpace(giftName))
        {
            giftName = data.Gift?.Name ?? string.Empty;
        }

        string count = data.Count > 0 ? data.Count.ToString()
            : (data.RepeatCount > 0 ? data.RepeatCount.ToString()
                : (data.ComboCount > 0 ? data.ComboCount.ToString() : "1"));

        // --- Icon: prefer payload GiftInfo icon (most accurate), then catalog, then gift struct ---
        string? iconUrl = data.GiftInfo?.GiftIcon?.UrlListList.FirstOrDefault()
            ?? catalogEntry?.IconUrl
            ?? data.Gift?.Image?.UrlListList.FirstOrDefault();

        // --- User: payload User (usually null) -> passive cache by user_id -> "用户####" fallback ---
        DouyinUser? user = data.Common?.User;
        string userName;
        DanmuUserInfo? resolvedUser = user != null
            ? new DanmuUserInfo { Name = user.NickName, AvatarUrl = user.AvatarThumb?.UrlListList.FirstOrDefault() }
            : ResolveUserById(data.UserId);
        if (resolvedUser != null && !string.IsNullOrWhiteSpace(resolvedUser.Name))
        {
            userName = resolvedUser.Name;
        }
        else
        {
            userName = data.UserId != 0 ? $"用户{data.UserId % 10000}" : "某用户";
        }

        // --- Display text & content ---
        string giftDisplay = string.IsNullOrWhiteSpace(giftName) ? "礼物" : giftName;
        string displayText = $"{userName} 送出 {giftDisplay} x{count}";
        string content = string.IsNullOrWhiteSpace(giftName) ? $"礼物 x{count}" : $"{giftName} x{count}";

        DanmuMessage mapped = CreateBase(roomInfo, envelope, DanmuMessageMethod.Gift, user, data.Common?.CreateTime, displayText, content);
        mapped.User = resolvedUser;
        mapped.UserName = resolvedUser?.Name ?? userName;
        mapped.UserAvatarUrl = resolvedUser?.AvatarUrl;
        mapped.Gift = new DanmuGiftInfo
        {
            Name = giftName,
            IconUrl = iconUrl,
            Count = count,
            Price = diamondCount > 0 ? diamondCount.ToString() : null,
        };
        mapped.GiftName = mapped.Gift.Name;
        mapped.GiftIconUrl = mapped.Gift.IconUrl;
        mapped.GiftCount = mapped.Gift.Count;
        mapped.GiftPrice = mapped.Gift.Price;
        LogGift("MapLightGift", roomInfo.RoomUrl, envelope.Method, mapped);
        return mapped;
    }

    /// <summary>按数字 user_id 从被动缓存查昵称/头像。命中返回用户信息，否则 null。</summary>
    private DanmuUserInfo? ResolveUserById(ulong userId)
    {
        if (userId == 0)
        {
            return null;
        }
        return _userCache.TryGetValue(userId, out DanmuUserInfo? info) ? info : null;
    }

    private DanmuMessage MapLike(RoomConnectInfo roomInfo, DouyinInnerMessage envelope)
    {
        DouyinLikeMessage data = Deserialize<DouyinLikeMessage>(envelope.Payload);
        return CreateBase(roomInfo, envelope, DanmuMessageMethod.Like, data.User, data.Common?.CreateTime,
            $"{data.User?.NickName} 点赞 {data.Count}", $"点赞 {data.Count}");
    }

    private DanmuMessage MapMember(RoomConnectInfo roomInfo, DouyinInnerMessage envelope)
    {
        DouyinMemberMessage data = Deserialize<DouyinMemberMessage>(envelope.Payload);
        return CreateBase(roomInfo, envelope, DanmuMessageMethod.Member, data.User, data.Common?.CreateTime,
            $"{data.User?.NickName} 进入直播间", "进入直播间");
    }

    private DanmuMessage MapSocial(RoomConnectInfo roomInfo, DouyinInnerMessage envelope)
    {
        DouyinSocialMessage data = Deserialize<DouyinSocialMessage>(envelope.Payload);
        return CreateBase(roomInfo, envelope, DanmuMessageMethod.Social, data.User, data.Common?.CreateTime,
            $"{data.User?.NickName} 关注了主播", "关注了主播");
    }

    private DanmuMessage MapEmoji(RoomConnectInfo roomInfo, DouyinInnerMessage envelope)
    {
        DouyinEmojiChatMessage data = Deserialize<DouyinEmojiChatMessage>(envelope.Payload);
        List<DanmuInlineSegment>? segments = BuildRichSegments(data.EmojiContent);
        // Find first image URL from segments (this is the emoji sticker URL)
        string? iconUrl = segments?.FirstOrDefault(s => s.IsImage && !string.IsNullOrWhiteSpace(s.ImageUrl))?.ImageUrl
            ?? data.EmojiContent?.PiecesList.FirstOrDefault(p => p.ImageValue?.Image?.UrlListList?.FirstOrDefault() != null)?.ImageValue?.Image?.UrlListList.FirstOrDefault();
        string emojiName = segments?.FirstOrDefault(s => s.IsImage && !string.IsNullOrWhiteSpace(s.ImageName))?.ImageName
            ?? data.EmojiContent?.PiecesList.FirstOrDefault(p => p.ImageValue?.Image?.Content?.Name != null)?.ImageValue?.Image?.Content?.Name
            ?? "表情";
        DanmuMessage mapped = CreateBase(roomInfo, envelope, DanmuMessageMethod.EmojiChat, data.User, data.Common?.CreateTime,
            $"{data.User?.NickName} 发送表情 [{emojiName}]", "[" + emojiName + "]");
        mapped.GiftIconUrl = iconUrl;
        mapped.Segments = segments;
        return mapped;
    }

    private DanmuMessage MapRoomUserSeq(RoomConnectInfo roomInfo, DouyinInnerMessage envelope)
    {
        DouyinRoomUserSeqMessage data = Deserialize<DouyinRoomUserSeqMessage>(envelope.Payload);
        string summary = string.Join(" / ", data.RanksList.Take(3).Select(item => $"{item.Rank}:{item.User?.NickName}"));
        return CreateBase(roomInfo, envelope, DanmuMessageMethod.RoomUserSeq, null, null,
            $"在线 {data.Total} 累计 {data.TotalUser} {summary}".Trim(), $"在线 {data.Total}");
    }

    private DanmuMessage MapRoomStats(RoomConnectInfo roomInfo, DouyinInnerMessage envelope)
    {
        DouyinRoomStatsMessage data = Deserialize<DouyinRoomStatsMessage>(envelope.Payload);
        return CreateBase(roomInfo, envelope, DanmuMessageMethod.RoomStats, null, null, $"房间统计 {data.DisplayMiddle}", data.DisplayMiddle);
    }

    private DanmuMessage MapRoomRank(RoomConnectInfo roomInfo, DouyinInnerMessage envelope)
    {
        DouyinRoomRankMessage data = Deserialize<DouyinRoomRankMessage>(envelope.Payload);
        string summary = string.Join(" / ", data.RanksList.Take(3).Select(item => $"{item.User?.NickName}:{item.ScoreStr}"));
        return CreateBase(roomInfo, envelope, DanmuMessageMethod.RoomRank, null, null, $"房间榜单 {summary}", summary);
    }

    private DanmuMessage MapFansClub(RoomConnectInfo roomInfo, DouyinInnerMessage envelope)
    {
        DouyinFansClubMessage data = Deserialize<DouyinFansClubMessage>(envelope.Payload);
        return CreateBase(roomInfo, envelope, DanmuMessageMethod.FansClub, data.User, null, data.Content, data.Content);
    }

    private DanmuMessage MapControl(RoomConnectInfo roomInfo, DouyinInnerMessage envelope)
    {
        DouyinControlMessage data = Deserialize<DouyinControlMessage>(envelope.Payload);
        return CreateBase(roomInfo, envelope, DanmuMessageMethod.Control, null, data.Common?.CreateTime,
            data.Common?.Describe ?? $"状态 {data.Status}", data.Common?.Describe);
    }

    private DanmuMessage CreateBase(RoomConnectInfo roomInfo, DouyinInnerMessage envelope, DanmuMessageMethod method, DouyinUser? user, ulong? createTime, string displayText, string? content)
    {
        DanmuUserInfo userInfo = new()
        {
            Name = user?.NickName,
            AvatarUrl = user?.AvatarThumb?.UrlListList.FirstOrDefault(),
        };

        // Backfill the passive user cache (id -> nickname/avatar) so that lightweight messages
        // carrying only a numeric user_id (e.g. WebcastLightGiftMessage) can resolve a nickname.
        if (user != null && user.Id != 0 && !string.IsNullOrWhiteSpace(user.NickName))
        {
            _userCache[user.Id] = userInfo;
        }

        return new DanmuMessage
        {
            RoomUrl = roomInfo.RoomUrl,
            RoomNickname = roomInfo.RoomNickname,
            MessageId = envelope.MsgId.ToString(),
            Method = method,
            RawTimestamp = createTime.HasValue && createTime.Value > 0
                ? (createTime.Value < 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeSeconds((long)createTime.Value)
                    : DateTimeOffset.FromUnixTimeMilliseconds((long)createTime.Value))
                : DateTimeOffset.Now,
            CreateTimeRaw = createTime,
            User = user != null ? userInfo : null,
            UserName = userInfo.Name,
            UserAvatarUrl = userInfo.AvatarUrl,
            Content = content,
            DisplayText = displayText,
        };
    }

    private static T Deserialize<T>(byte[] payload)
    {
        using MemoryStream stream = new(payload);
        return Serializer.Deserialize<T>(stream);
    }

    private string BuildRichText(DouyinText? text)
    {
        if (text?.PiecesList == null || text.PiecesList.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        foreach (DouyinTextPiece piece in text.PiecesList)
        {
            if (!string.IsNullOrWhiteSpace(piece.StringValue))
            {
                builder.Append(piece.StringValue);
            }
            else if (piece.UserValue?.User != null)
            {
                builder.Append('@').Append(piece.UserValue.User.NickName);
            }
            else if (piece.ImageValue?.Image?.Content?.Name != null)
            {
                builder.Append('[').Append(piece.ImageValue.Image.Content.Name).Append(']');
            }
        }

        return builder.ToString();
    }

    private List<DanmuInlineSegment>? BuildRichSegments(DouyinText? text)
    {
        if (text?.PiecesList == null || text.PiecesList.Count == 0)
        {
            return null;
        }

        List<DanmuInlineSegment> segments = new();
        foreach (DouyinTextPiece piece in text.PiecesList)
        {
            if (!string.IsNullOrWhiteSpace(piece.StringValue))
            {
                segments.Add(new DanmuInlineSegment { IsImage = false, Text = piece.StringValue });
            }
            else if (piece.UserValue?.User != null)
            {
                segments.Add(new DanmuInlineSegment { IsImage = false, Text = "@" + piece.UserValue.User.NickName });
            }
            else if (piece.ImageValue?.Image != null)
            {
                string? url = piece.ImageValue.Image.UrlListList?.FirstOrDefault();
                string? name = piece.ImageValue.Image.Content?.Name;
                if (!string.IsNullOrWhiteSpace(url))
                {
                    segments.Add(new DanmuInlineSegment { IsImage = true, ImageUrl = url, ImageName = name });
                }
                else if (!string.IsNullOrWhiteSpace(name))
                {
                    segments.Add(new DanmuInlineSegment { IsImage = false, Text = "[" + name + "]" });
                }
            }
        }

        return segments.Count > 0 ? segments : null;
    }

    /// <summary>
    /// Parses [emoji_name] patterns in chat text and creates inline segments.
    /// Currently outputs text-only segments since we don't have a mapping table from
    /// emoji name to image URL — this provides the structure for future enrichment.
    /// </summary>
    private static List<DanmuInlineSegment>? ParseChatEmojiSegments(string? content)
    {
        if (string.IsNullOrEmpty(content) || !content.Contains('['))
        {
            return null;
        }

        List<DanmuInlineSegment> segments = new();
        int i = 0;
        StringBuilder textBuf = new();
        while (i < content.Length)
        {
            if (content[i] == '[')
            {
                int closing = content.IndexOf(']', i + 1);
                if (closing > i && closing - i <= 10)
                {
                    string emojiName = content.Substring(i + 1, closing - i - 1);
                    if (!string.IsNullOrEmpty(emojiName) && !emojiName.Contains('[') && !emojiName.Contains(' '))
                    {
                        if (textBuf.Length > 0)
                        {
                            segments.Add(new DanmuInlineSegment { IsImage = false, Text = textBuf.ToString() });
                            textBuf.Clear();
                        }
                        segments.Add(new DanmuInlineSegment { IsImage = false, Text = "[" + emojiName + "]", ImageName = emojiName });
                        i = closing + 1;
                        continue;
                    }
                }
            }
            textBuf.Append(content[i]);
            i++;
        }
        if (textBuf.Length > 0)
        {
            segments.Add(new DanmuInlineSegment { IsImage = false, Text = textBuf.ToString() });
        }

        return segments.Count > 1 ? segments : null;
    }

    private string GetSignature(string roomId, string uniqueId)
    {
        string stub = GetStub($"live_id=1,aid=6383,version_code=180800,webcast_sdk_version={Version},room_id={roomId},sub_room_id=,sub_channel_id=,did_rule=3,user_unique_id={uniqueId},device_platform=web,device_type=,ac=,identity=audience");
        _signEngine.Script.inputValue = stub;
        object? result = _signEngine.Evaluate("window.byted_acrawler.frontierSign({'X-MS-STUB': inputValue})['X-Bogus']");
        return result?.ToString() ?? string.Empty;
    }

    private static string GetStub(string input)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(input);
        byte[] hash = System.Security.Cryptography.MD5.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GenerateMsToken(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        Random random = new();
        return new string(Enumerable.Range(0, length).Select(_ => chars[random.Next(chars.Length)]).ToArray());
    }

    private static string Extract(Regex regex, string html)
    {
        Match match = regex.Match(html);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static string DecodeUrl(string url) => url.Replace("\\u0026", "&", StringComparison.Ordinal);

    private string GetABogus(string queryString)
    {
        _aBogusEngine.Script.queryValue = queryString;
        _aBogusEngine.Script.userAgentValue = GetUserAgent();
        object? result = _aBogusEngine.Evaluate("get_ab(queryValue, userAgentValue)");
        return result?.ToString() ?? string.Empty;
    }

    private static string BuildQueryString(IEnumerable<KeyValuePair<string, string?>> parameters)
    {
        return string.Join("&", parameters.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value ?? string.Empty)}"));
    }

    private static string NormalizeHtmlPayload(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        return html
            .Replace("\\u0026", "&", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\/", "/", StringComparison.Ordinal);
    }

    private static bool TryExtractFetchInfo(byte[] content, out string cursor, out string internalExt)
    {
        string text = Encoding.Latin1.GetString(content);
        cursor = Extract(FetchCursorRegex, text);
        internalExt = Extract(FetchInternalExtRegex, text);
        return !string.IsNullOrWhiteSpace(cursor) && !string.IsNullOrWhiteSpace(internalExt);
    }

    private static string GetUserAgent()
    {
        string configured = Configurations.UserAgent.Get();
        return string.IsNullOrWhiteSpace(configured)
            ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36"
            : configured;
    }

    /// <summary>
    /// Extracts the version portion from the User-Agent string (everything after "Mozilla/").
    /// This matches the Python reference project's behavior for the browser_version WSS parameter.
    /// </summary>
    private static string GetUserAgentVersion()
    {
        string ua = GetUserAgent();
        int idx = ua.IndexOf("Mozilla/");
        return idx >= 0 ? ua.Substring(idx + 8) : ua;
    }

    public void Dispose()
    {
        DisconnectInternalAsync().GetAwaiter().GetResult();
        _signEngine.Dispose();
        _aBogusEngine.Dispose();
        _httpClient.Dispose();
        _switchLock.Dispose();
    }

    private sealed class RoomConnectInfo
    {
        public string RoomUrl { get; set; } = string.Empty;

        public string RoomNickname { get; set; } = string.Empty;

        public string RoomId { get; set; } = string.Empty;

        public string UniqueId { get; set; } = string.Empty;

        public string Cursor { get; set; } = string.Empty;

        public string InternalExt { get; set; } = string.Empty;

        public string Avatar { get; set; } = string.Empty;

        public int Status { get; set; }
    }

    private enum SessionEndReason
    {
        Completed,
        Reconnect,
        WaitingForLive,
    }

    private static string DecodeProtobufFields(byte[] data)
    {
        StringBuilder sb = new();
        int pos = 0;
        DecodeProtobufFieldsInternal(data, ref pos, sb, 0);
        return sb.ToString();
    }

    private static void DecodeProtobufFieldsInternal(byte[] data, ref int pos, StringBuilder sb, int depth)
    {
        string indent = new(' ', depth * 2);
        while (pos < data.Length)
        {
            byte tagByte = data[pos++];
            int fieldNumber = tagByte >> 3;
            int wireType = tagByte & 0x07;

            if (fieldNumber == 0 || wireType > 5)
            {
                sb.AppendLine($"{indent}[INVALID tag={tagByte:X2} at pos={pos - 1}]");
                return;
            }

            switch (wireType)
            {
                case 0: // varint
                    ulong varint = 0;
                    int shift = 0;
                    while (pos < data.Length)
                    {
                        byte b = data[pos++];
                        varint |= (ulong)(b & 0x7F) << shift;
                        if ((b & 0x80) == 0) break;
                        shift += 7;
                    }
                    sb.AppendLine($"{indent}field {fieldNumber}: varint = {varint}");
                    break;

                case 1: // 64-bit
                    if (pos + 8 > data.Length) { sb.AppendLine($"{indent}field {fieldNumber}: 64-bit [TRUNCATED]"); return; }
                    ulong val64 = BitConverter.ToUInt64(data, pos);
                    pos += 8;
                    sb.AppendLine($"{indent}field {fieldNumber}: 64-bit = {val64}");
                    break;

                case 2: // length-delimited
                    if (pos >= data.Length) { sb.AppendLine($"{indent}field {fieldNumber}: len-delimited [TRUNCATED LENGTH]"); return; }
                    ulong len = 0;
                    int lshift = 0;
                    while (pos < data.Length)
                    {
                        byte b = data[pos++];
                        len |= (ulong)(b & 0x7F) << lshift;
                        if ((b & 0x80) == 0) break;
                        lshift += 7;
                    }
                    if (pos + (int)len > data.Length) { sb.AppendLine($"{indent}field {fieldNumber}: len-delimited len={len} [TRUNCATED]"); return; }
                    byte[] subData = data.Skip(pos).Take((int)len).ToArray();
                    pos += (int)len;

                    bool isString = true;
                    if (subData.Length > 0)
                    {
                        foreach (byte b in subData)
                        {
                            if (b < 0x20 && b != 0x09 && b != 0x0A && b != 0x0D) { isString = false; break; }
                            if (b > 0x7E) { isString = false; break; }
                        }
                    }
                    else { isString = true; }

                    if (isString && subData.Length > 0)
                    {
                        try
                        {
                            string str = Encoding.UTF8.GetString(subData);
                            if (str.All(c => !char.IsControl(c) || c == '\n' || c == '\r' || c == '\t'))
                            {
                                sb.AppendLine($"{indent}field {fieldNumber}: string = \"{str}\"");
                                break;
                            }
                        }
                        catch { }
                    }

                    bool looksLikeSubmessage = subData.Length > 0 && (subData[0] & 0x07) is 0 or 2 && (subData[0] >> 3) > 0 && (subData[0] >> 3) < 100;
                    if (looksLikeSubmessage && depth < 4)
                    {
                        sb.AppendLine($"{indent}field {fieldNumber}: message len={len} {{");
                        int subPos = 0;
                        DecodeProtobufFieldsInternal(subData, ref subPos, sb, depth + 1);
                        sb.AppendLine($"{indent}}}");
                    }
                    else
                    {
                        string hex = BitConverter.ToString(subData.Take(64).ToArray()).Replace("-", " ");
                        sb.AppendLine($"{indent}field {fieldNumber}: bytes len={len} hex={hex}{(subData.Length > 64 ? "..." : "")}");
                    }
                    break;

                case 5: // 32-bit
                    if (pos + 4 > data.Length) { sb.AppendLine($"{indent}field {fieldNumber}: 32-bit [TRUNCATED]"); return; }
                    uint val32 = BitConverter.ToUInt32(data, pos);
                    pos += 4;
                    sb.AppendLine($"{indent}field {fieldNumber}: 32-bit = {val32}");
                    break;

                default:
                    sb.AppendLine($"{indent}field {fieldNumber}: unknown wire_type={wireType}");
                    return;
            }
        }
    }

    private static void LogGift(string stage, string roomUrl, string method, DanmuMessage msg)
    {
        try
        {
            string debugDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TiktokLiveRec", "debug_proto");
            Directory.CreateDirectory(debugDir);
            File.AppendAllText(Path.Combine(debugDir, "gift_trace.log"),
                $"[{DateTimeOffset.Now:HH:mm:ss.fff}] [{stage}] room={roomUrl} method={method} " +
                $"→ Method={msg.Method} User={msg.UserName} GiftName={msg.GiftName} " +
                $"GiftCount={msg.GiftCount} GiftIconUrl={msg.GiftIconUrl} " +
                $"DisplayText={msg.DisplayText}{Environment.NewLine}");
        }
        catch { }
    }

    private static DanmuMessage? LogUnknownGift(RoomConnectInfo roomInfo, DouyinInnerMessage message)
    {
        try
        {
            string debugDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TiktokLiveRec", "debug_proto");
            Directory.CreateDirectory(debugDir);

            // Only dump the full protobuf decode for gift-family methods (to diagnose gift
            // protocol changes). Other unmatched methods are noisy (thousands per session) and
            // would bloat the log, so they only get a one-line summary.
            bool isGiftFamily = message.Method != null && message.Method.Contains("Gift", StringComparison.Ordinal);
            string line = $"[{DateTimeOffset.Now:HH:mm:ss.fff}] [UNMATCHED] room={roomInfo.RoomUrl} method={message.Method} payloadLen={message.Payload.Length}{Environment.NewLine}";
            if (isGiftFamily)
            {
                string decoded = DecodeProtobufFields(message.Payload);
                File.AppendAllText(Path.Combine(debugDir, "gift_trace.log"),
                    line + decoded + Environment.NewLine + Environment.NewLine);
            }
            else
            {
                // Rate-limit non-gift summaries via a separate, capped file.
                File.AppendAllText(Path.Combine(debugDir, "unmatched_summary.log"), line);
            }
        }
        catch { }
        return null;
    }
}
