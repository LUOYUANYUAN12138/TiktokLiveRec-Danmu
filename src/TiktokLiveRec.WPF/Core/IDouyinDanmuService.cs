#nullable disable
using TiktokLiveRec.Models;

namespace TiktokLiveRec.Core;

public interface IDouyinDanmuService : IDisposable
{
    event Action<DanmuMessage>? MessageReceived;

    event Action<string, DanmuConnectionState>? ConnectionStateChanged;

    event Action<string, string>? ErrorOccurred;

    Task SwitchRoomAsync(string? roomUrl, string? roomNickname, CancellationToken cancellationToken = default);

    Task DisconnectAsync();
}
