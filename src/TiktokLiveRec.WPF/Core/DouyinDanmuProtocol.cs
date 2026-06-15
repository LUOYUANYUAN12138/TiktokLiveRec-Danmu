#nullable disable
using ProtoBuf;
namespace TiktokLiveRec.Core;

[ProtoContract]
internal sealed class DouyinResponse
{
    [ProtoMember(1)]
    public List<DouyinInnerMessage> MessagesList { get; set; } = [];

    [ProtoMember(2)]
    public string Cursor { get; set; } = string.Empty;

    [ProtoMember(3)]
    public ulong FetchInterval { get; set; }

    [ProtoMember(4)]
    public ulong Now { get; set; }

    [ProtoMember(5)]
    public string InternalExt { get; set; } = string.Empty;

    [ProtoMember(6)]
    public uint FetchType { get; set; }

    [ProtoMember(7)]
    public Dictionary<string, string> RouteParams { get; set; } = [];

    [ProtoMember(8)]
    public ulong HeartbeatDuration { get; set; }

    [ProtoMember(9)]
    public bool NeedAck { get; set; }

    [ProtoMember(10)]
    public string PushServer { get; set; } = string.Empty;

    [ProtoMember(11)]
    public string LiveCursor { get; set; } = string.Empty;

    [ProtoMember(12)]
    public bool HistoryNoMore { get; set; }
}

[ProtoContract]
internal sealed class DouyinInnerMessage
{
    [ProtoMember(1)]
    public string Method { get; set; } = string.Empty;

    [ProtoMember(2)]
    public byte[] Payload { get; set; } = [];

    [ProtoMember(3)]
    public long MsgId { get; set; }

    [ProtoMember(4)]
    public int MsgType { get; set; }

    [ProtoMember(5)]
    public long Offset { get; set; }

    [ProtoMember(6)]
    public bool NeedWrdsStore { get; set; }

    [ProtoMember(7)]
    public long WrdsVersion { get; set; }

    [ProtoMember(8)]
    public string WrdsSubKey { get; set; } = string.Empty;
}

[ProtoContract]
internal sealed class DouyinPushFrame
{
    [ProtoMember(2)]
    public ulong LogId { get; set; }

    [ProtoMember(5)]
    public Dictionary<string, string> HeadersList { get; set; } = [];

    [ProtoMember(6)]
    public string PayloadEncoding { get; set; } = string.Empty;

    [ProtoMember(7)]
    public string PayloadType { get; set; } = string.Empty;

    [ProtoMember(8)]
    public byte[] Payload { get; set; } = [];
}

[ProtoContract]
internal sealed class DouyinClientFrame
{
    [ProtoMember(2)]
    public ulong LogId { get; set; }

    [ProtoMember(7)]
    public string PayloadType { get; set; } = "ack";

    [ProtoMember(8)]
    public byte[] Payload { get; set; } = [];
}

[ProtoContract]
internal sealed class DouyinCommon
{
    [ProtoMember(1)]
    public string Method { get; set; } = string.Empty;

    [ProtoMember(2)]
    public ulong MsgId { get; set; }

    [ProtoMember(3)]
    public ulong RoomId { get; set; }

    [ProtoMember(4)]
    public ulong CreateTime { get; set; }

    [ProtoMember(5)]
    public uint Monitor { get; set; }

    [ProtoMember(6)]
    public bool IsShowMsg { get; set; }

    [ProtoMember(7)]
    public string Describe { get; set; } = string.Empty;

    [ProtoMember(9)]
    public ulong FoldType { get; set; }

    [ProtoMember(10)]
    public ulong AnchorFoldType { get; set; }

    [ProtoMember(11)]
    public ulong PriorityScore { get; set; }

    [ProtoMember(15)]
    public DouyinUser? User { get; set; }
}

[ProtoContract]
internal sealed class DouyinChatMessage
{
    [ProtoMember(1)]
    public DouyinCommon? Common { get; set; }

    [ProtoMember(2)]
    public DouyinUser? User { get; set; }

    [ProtoMember(3)]
    public string Content { get; set; } = string.Empty;

    [ProtoMember(15)]
    public ulong EventTime { get; set; }

    [ProtoMember(22)]
    public DouyinText? RtfContent { get; set; }
}

[ProtoContract]
internal sealed class DouyinGiftInfo
{
    [ProtoMember(1)]
    public ulong GiftId { get; set; }

    [ProtoMember(2)]
    public DouyinImage? GiftIcon { get; set; }

    [ProtoMember(3)]
    public ulong DiamondCount { get; set; }
}

[ProtoContract]
internal sealed class DouyinLightGiftMessage
{
    [ProtoMember(1)]
    public DouyinCommon? Common { get; set; }

    [ProtoMember(2)]
    public ulong GroupCount { get; set; }

    [ProtoMember(3)]
    public ulong RepeatCount { get; set; }

    [ProtoMember(4)]
    public ulong ComboCount { get; set; }

    [ProtoMember(5)]
    public ulong UserId { get; set; }

    [ProtoMember(7)]
    public DouyinGiftInfo? GiftInfo { get; set; }

    [ProtoMember(10)]
    public ulong Count { get; set; }

    [ProtoMember(13)]
    public DouyinGiftStruct? Gift { get; set; }
}

[ProtoContract]
internal sealed class DouyinGiftMessage
{
    [ProtoMember(1)]
    public DouyinCommon? Common { get; set; }

    [ProtoMember(5)]
    public ulong RepeatCount { get; set; }

    [ProtoMember(6)]
    public ulong ComboCount { get; set; }

    [ProtoMember(7)]
    public DouyinUser? User { get; set; }

    [ProtoMember(9)]
    public uint RepeatEnd { get; set; }

    [ProtoMember(15)]
    public DouyinGiftStruct? Gift { get; set; }
}

[ProtoContract]
internal sealed class DouyinLikeMessage
{
    [ProtoMember(1)]
    public DouyinCommon? Common { get; set; }

    [ProtoMember(2)]
    public ulong Count { get; set; }

    [ProtoMember(3)]
    public ulong Total { get; set; }

    [ProtoMember(5)]
    public DouyinUser? User { get; set; }
}

[ProtoContract]
internal sealed class DouyinMemberMessage
{
    [ProtoMember(1)]
    public DouyinCommon? Common { get; set; }

    [ProtoMember(2)]
    public DouyinUser? User { get; set; }

    [ProtoMember(3)]
    public ulong MemberCount { get; set; }
}

[ProtoContract]
internal sealed class DouyinSocialMessage
{
    [ProtoMember(1)]
    public DouyinCommon? Common { get; set; }

    [ProtoMember(2)]
    public DouyinUser? User { get; set; }

    [ProtoMember(6)]
    public ulong FollowCount { get; set; }
}

[ProtoContract]
internal sealed class DouyinEmojiChatMessage
{
    [ProtoMember(1)]
    public DouyinCommon? Common { get; set; }

    [ProtoMember(2)]
    public DouyinUser? User { get; set; }

    [ProtoMember(4)]
    public DouyinText? EmojiContent { get; set; }
}

[ProtoContract]
internal sealed class DouyinRoomUserSeqMessage
{
    [ProtoMember(2)]
    public List<DouyinRoomUserSeqContributor> RanksList { get; set; } = [];

    [ProtoMember(3)]
    public long Total { get; set; }

    [ProtoMember(7)]
    public long TotalUser { get; set; }
}

[ProtoContract]
internal sealed class DouyinRoomUserSeqContributor
{
    [ProtoMember(2)]
    public DouyinUser? User { get; set; }

    [ProtoMember(3)]
    public ulong Rank { get; set; }
}

[ProtoContract]
internal sealed class DouyinRoomStatsMessage
{
    [ProtoMember(3)]
    public string DisplayMiddle { get; set; } = string.Empty;
}

[ProtoContract]
internal sealed class DouyinRoomRankMessage
{
    [ProtoMember(2)]
    public List<DouyinRoomRank> RanksList { get; set; } = [];
}

[ProtoContract]
internal sealed class DouyinRoomRank
{
    [ProtoMember(1)]
    public DouyinUser? User { get; set; }

    [ProtoMember(2)]
    public string ScoreStr { get; set; } = string.Empty;
}

[ProtoContract]
internal sealed class DouyinFansClubMessage
{
    [ProtoMember(3)]
    public string Content { get; set; } = string.Empty;

    [ProtoMember(4)]
    public DouyinUser? User { get; set; }
}

[ProtoContract]
internal sealed class DouyinControlMessage
{
    [ProtoMember(1)]
    public DouyinCommon? Common { get; set; }

    [ProtoMember(2)]
    public int Status { get; set; }
}

[ProtoContract]
internal sealed class DouyinUser
{
    [ProtoMember(1)]
    public ulong Id { get; set; }

    [ProtoMember(3)]
    public string NickName { get; set; } = string.Empty;

    [ProtoMember(4)]
    public uint Gender { get; set; }

    [ProtoMember(9)]
    public DouyinImage? AvatarThumb { get; set; }

    [ProtoMember(46)]
    public string SecUid { get; set; } = string.Empty;

    [ProtoMember(1028)]
    public string IdStr { get; set; } = string.Empty;
}

[ProtoContract]
internal sealed class DouyinGiftStruct
{
    [ProtoMember(1)]
    public DouyinImage? Image { get; set; }

    [ProtoMember(2)]
    public string Describe { get; set; } = string.Empty;

    [ProtoMember(5)]
    public ulong Id { get; set; }

    [ProtoMember(11)]
    public uint Type { get; set; }

    [ProtoMember(12)]
    public uint DiamondCount { get; set; }

    [ProtoMember(16)]
    public string Name { get; set; } = string.Empty;
}

[ProtoContract]
internal sealed class DouyinImage
{
    [ProtoMember(1)]
    public List<string> UrlListList { get; set; } = [];

    [ProtoMember(8)]
    public DouyinImageContent? Content { get; set; }
}

[ProtoContract]
internal sealed class DouyinImageContent
{
    [ProtoMember(1)]
    public string Name { get; set; } = string.Empty;
}

[ProtoContract]
internal sealed class DouyinText
{
    [ProtoMember(4)]
    public List<DouyinTextPiece> PiecesList { get; set; } = [];
}

[ProtoContract]
internal sealed class DouyinTextPiece
{
    [ProtoMember(3)]
    public string StringValue { get; set; } = string.Empty;

    [ProtoMember(4)]
    public DouyinTextPieceUser? UserValue { get; set; }

    [ProtoMember(8)]
    public DouyinTextPieceImage? ImageValue { get; set; }
}

[ProtoContract]
internal sealed class DouyinTextPieceUser
{
    [ProtoMember(1)]
    public DouyinUser? User { get; set; }
}

[ProtoContract]
internal sealed class DouyinTextPieceImage
{
    [ProtoMember(1)]
    public DouyinImage? Image { get; set; }
}
