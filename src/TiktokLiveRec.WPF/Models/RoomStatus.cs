using TiktokLiveRec.Core;

namespace TiktokLiveRec.Models;

public sealed class RoomStatus
{
    public string NickName { get; set; } = string.Empty;

    public string AvatarThumbUrl { get; set; } = string.Empty;

    public string RoomUrl { get; set; } = string.Empty;

    public string FlvUrl { get; set; } = string.Empty;

    public string HlsUrl { get; set; } = string.Empty;

    public StreamStatus StreamStatus { get; set; } = default;

    public RecordStatus RecordStatus
    {
        get => Recorder.RecordStatus;
        internal set => Recorder.RecordStatus = value;
    }

    public DanmuConnectionState DanmuConnectionState { get; set; } = DanmuConnectionState.Disabled;

    public DateTime DanmuLastMessageTime { get; set; } = DateTime.MinValue;

    public List<DanmuMessage> DanmuMessages { get; } = [];

    public string LastRecordError
    {
        get => Recorder.LastError;
        set => Recorder.LastError = value;
    }

    public DateTime LastRecordAttemptTime
    {
        get => Recorder.LastAttemptTime;
        set => Recorder.LastAttemptTime = value;
    }

    public string LastRecordStartCommand
    {
        get => Recorder.LastStartCommand;
        set => Recorder.LastStartCommand = value;
    }

    public Recorder Recorder { get; } = new();

    public Player Player { get; } = new();
}

public enum StreamStatus
{
    Initialized,
    Disabled,
    NotStreaming,
    Streaming,
}
