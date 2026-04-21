#nullable disable
using System.Globalization;

namespace TiktokLiveRec.Models;

public enum DanmuConnectionState
{
    Disabled,
    Idle,
    WaitingForLive,
    Unsupported,
    Connecting,
    Connected,
    Reconnecting,
    Failed,
}

public enum DanmuMessageMethod
{
    Unknown,
    Chat,
    Gift,
    Like,
    Member,
    Social,
    EmojiChat,
    RoomUserSeq,
    RoomStats,
    RoomRank,
    FansClub,
    Control,
}

public sealed class DanmuUserInfo
{
    public string? Name { get; set; }

    public string? AvatarUrl { get; set; }
}

public sealed class DanmuGiftInfo
{
    public string? Name { get; set; }

    public string? IconUrl { get; set; }

    public string? Count { get; set; }

    public string? Price { get; set; }
}

public sealed class DanmuMessage
{
    public string RoomUrl { get; set; } = string.Empty;

    public string RoomNickname { get; set; } = string.Empty;

    public string MessageId { get; set; } = string.Empty;

    public DanmuMessageMethod Method { get; set; } = DanmuMessageMethod.Unknown;

    public string? UserName { get; set; }

    public string? UserAvatarUrl { get; set; }

    public string? Content { get; set; }

    public string? GiftName { get; set; }

    public string? GiftIconUrl { get; set; }

    public string? GiftCount { get; set; }

    public string? GiftPrice { get; set; }

    public DateTimeOffset RawTimestamp { get; set; } = DateTimeOffset.Now;

    public string DisplayText { get; set; } = string.Empty;

    public DanmuUserInfo? User { get; set; }

    public DanmuGiftInfo? Gift { get; set; }

    public bool IsGift => Method == DanmuMessageMethod.Gift;

    public bool IsSecondary => Method is DanmuMessageMethod.RoomRank or DanmuMessageMethod.RoomStats or DanmuMessageMethod.RoomUserSeq;

    public string TimestampText => RawTimestamp.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string FormatForLog()
    {
        string header = $"[{RawTimestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss}] [{Method}] [房间:{RoomNickname}]";
        string user = string.IsNullOrWhiteSpace(UserName) ? string.Empty : $" [用户:{UserName}]";
        string avatar = string.IsNullOrWhiteSpace(UserAvatarUrl) ? string.Empty : $" [头像:{UserAvatarUrl}]";
        string gift = string.IsNullOrWhiteSpace(GiftName)
            ? string.Empty
            : $" 礼物={GiftName} 数量={GiftCount ?? "1"} 价格={GiftPrice ?? string.Empty} 图标={GiftIconUrl ?? string.Empty}";
        string content = string.IsNullOrWhiteSpace(Content) ? string.Empty : $" 内容={Content}";
        return $"{header}{user}{avatar}{gift}{content}".TrimEnd();
    }
}
