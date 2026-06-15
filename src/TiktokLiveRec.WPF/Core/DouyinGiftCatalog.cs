#nullable disable
using System.Net;
using System.Net.Http;
using Newtonsoft.Json.Linq;
using System.Diagnostics;

namespace TiktokLiveRec.Core;

/// <summary>
/// 抖音礼物目录缓存：将 gift_id 映射为礼物中文名/图标/钻石数。
/// 数据来源：
///   1. 远程接口 https://live.douyin.com/webcast/gift/list/ （返回 ~1600+ 礼物，无需 Cookie）
///   2. 内置静态兜底表（接口失败时使用，覆盖最常见礼物）
/// 抽音轻量礼物消息 (WebcastLightGiftMessage) 仅携带 gift_id，礼物名必须经此补全。
/// </summary>
internal sealed class DouyinGiftCatalog
{
    private const string GiftListUrl = "https://live.douyin.com/webcast/gift/list/?aid=6383&device_platform=web";
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromHours(6);

    private static DouyinGiftCatalog? _instance;
    public static DouyinGiftCatalog Instance => _instance ??= new DouyinGiftCatalog();

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly HttpClient _httpClient;
    private readonly Dictionary<ulong, GiftEntry> _cache = [];
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private bool _loaded;
    private int _loadAttempt;

    private DouyinGiftCatalog()
    {
        HttpClientHandler handler = new()
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = Configurations.IsUseProxy.Get() && !string.IsNullOrWhiteSpace(Configurations.ProxyUrl.Get()),
            Proxy = Configurations.IsUseProxy.Get() && !string.IsNullOrWhiteSpace(Configurations.ProxyUrl.Get())
                ? new WebProxy($"http://{Configurations.ProxyUrl.Get()}")
                : null,
        };
        _httpClient = new(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://live.douyin.com/");

        // Seed static fallback table (most common gifts observed in real traffic)
        SeedStaticFallback();
    }

    internal sealed class GiftEntry
    {
        public string Name = string.Empty;
        public string? IconUrl;
        public uint DiamondCount;
    }

    /// <summary>
    /// 查询礼物名。命中返回 true；未命中（已加载但 id 不存在）返回 false。
    /// 未加载时触发后台异步加载（不阻塞当前调用）。
    /// </summary>
    public bool TryGetName(ulong giftId, out string name)
    {
        name = string.Empty;
        if (giftId == 0)
        {
            return false;
        }

        // Trigger async load on first use / after expiry (fire-and-forget, non-blocking).
        EnsureLoaded();

        if (_cache.TryGetValue(giftId, out GiftEntry? entry) && !string.IsNullOrWhiteSpace(entry.Name))
        {
            name = entry.Name;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 查询完整礼物信息。命中返回 true。
    /// </summary>
    public bool TryGetEntry(ulong giftId, out GiftEntry? entry)
    {
        entry = null;
        if (giftId == 0)
        {
            return false;
        }
        EnsureLoaded();
        return _cache.TryGetValue(giftId, out entry);
    }

    /// <summary>主动触发加载（如连接房间时调用，可 await）。</summary>
    public Task LoadAsync() => LoadAsync(force: false);

    private void EnsureLoaded()
    {
        if (_loaded && DateTimeOffset.UtcNow - _loadedAt < CacheExpiry)
        {
            return;
        }
        // Fire-and-forget; the lock guards concurrent triggers.
        _ = LoadAsync(force: false);
    }

    private async Task LoadAsync(bool force)
    {
        if (!force && _loaded && DateTimeOffset.UtcNow - _loadedAt < CacheExpiry)
        {
            return;
        }

        await _loadLock.WaitAsync();
        try
        {
            if (!force && _loaded && DateTimeOffset.UtcNow - _loadedAt < CacheExpiry)
            {
                return;
            }

            // Bound retry attempts to avoid hammering when the endpoint is down.
            if (!force && _loadAttempt >= 5 && !_loaded)
            {
                _ = LoadAfterDelayAsync();
                return;
            }

            try
            {
                using HttpResponseMessage response = await _httpClient.GetAsync(GiftListUrl);
                response.EnsureSuccessStatusCode();
                string json = await response.Content.ReadAsStringAsync();
                int count = ParseGiftList(json);
                if (count > 0)
                {
                    _loadedAt = DateTimeOffset.UtcNow;
                    _loaded = true;
                    _loadAttempt = 0;
                    Debug.WriteLine($"[GiftCatalog] Loaded {count} gifts from remote.");
                }
            }
            catch (Exception ex)
            {
                _loadAttempt++;
                Debug.WriteLine($"[GiftCatalog] Load failed (attempt {_loadAttempt}): {ex.Message}");
                // Retry after a delay so the static fallback isn't the final state forever.
                _ = LoadAfterDelayAsync();
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task LoadAfterDelayAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2));
            await LoadAsync(force: false);
        }
        catch
        {
        }
    }

    /// <summary>解析 data.gifts[] 数组，合并进缓存（不覆盖已有静态兜底项以避免空名覆盖）。</summary>
    private int ParseGiftList(string json)
    {
        int added = 0;
        JToken? root = JToken.Parse(json);
        JArray? gifts = root?["data"]?["gifts"] as JArray;
        if (gifts == null)
        {
            return 0;
        }

        foreach (JToken g in gifts)
        {
            ulong id = (ulong?)g["id"] ?? 0;
            if (id == 0)
            {
                continue;
            }
            string name = (string?)g["name"] ?? string.Empty;
            // Don't let empty names overwrite a static fallback that has a name.
            if (string.IsNullOrWhiteSpace(name) && _cache.ContainsKey(id))
            {
                continue;
            }
            GiftEntry entry = new()
            {
                Name = name,
                IconUrl = ExtractIconUrl(g),
                DiamondCount = (uint?)g["diamond_count"] ?? 0,
            };
            _cache[id] = entry;
            added++;
        }
        return added;
    }

    private static string? ExtractIconUrl(JToken gift)
    {
        // Prefer image.url_list[0], then icon.url_list[0], then webp_image.
        string? url = (gift["image"]?["url_list"] as JArray)?.FirstOrDefault()?.ToString();
        if (!string.IsNullOrWhiteSpace(url))
        {
            return url;
        }
        url = (gift["icon"]?["url_list"] as JArray)?.FirstOrDefault()?.ToString();
        if (!string.IsNullOrWhiteSpace(url))
        {
            return url;
        }
        url = (string?)gift["webp_image"]?["url_list"];
        return string.IsNullOrWhiteSpace(url) ? null : url;
    }

    /// <summary>内置静态兜底表。这些 gift_id 在真实流量中频繁出现，确保接口失败时仍能显示名称。</summary>
    private void SeedStaticFallback()
    {
        // (giftId, name, diamondCount) — sourced from observed real payloads.
        ReadOnlySpan<(ulong id, string name, uint dia)> seeds =
        [
            (463u, "小心心", 1u),
            (685u, "粉丝团灯牌", 1u),
            (3242u, "入团卡", 1u),
            (3246u, "玫瑰", 1u),
            (1u, "点赞", 1u),
            (2u, "抖一抖", 1u),
            (4u, "鲜花", 1u),
            (5u, "棒棒糖", 1u),
            (8u, "亲吻", 1u),
            (10u, "玫瑰", 1u),
            (11u, "握手", 1u),
            (13u, "star", 1u),
            (15u, "亲吻", 1u),
            (16u, "玫瑰", 1u),
            (266u, "浪漫花", 5u),
            (269u, "亲吻", 5u),
            (317u, "烟花", 30u),
            (99u, "跑车", 120u),
            (100u, "嘉年华", 30000u),
            (459u, "抖音", 1u),
            (460u, "鲜花", 1u),
        ];
        foreach ((ulong id, string name, uint dia) in seeds)
        {
            if (!_cache.ContainsKey(id))
            {
                _cache[id] = new GiftEntry { Name = name, DiamondCount = dia };
            }
        }
    }
}
