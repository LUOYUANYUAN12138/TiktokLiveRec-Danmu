using System.Windows;
using TiktokLiveRec.Models;
using TiktokLiveRec.Views;
using Wpf.Ui.Violeta.Controls;

namespace TiktokLiveRec.Core;

internal static class LivePreviewManager
{
    private static LivePreviewWindow? _window;

    public static void ShowOrActivate(RoomStatus roomStatus)
    {
        if (roomStatus.StreamStatus != StreamStatus.Streaming)
        {
            Toast.Warning("PlayerErrorOfNoFile".Tr());
            return;
        }

        (string? streamUrl, PreviewStreamKind? streamKind) = ResolveStream(roomStatus);
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            Toast.Warning("PlayerErrorOfNoFile".Tr());
            return;
        }

        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_window == null || !_window.IsLoaded)
            {
                _window = new LivePreviewWindow();
                if (Application.Current.MainWindow != _window)
                {
                    _window.Owner = Application.Current.MainWindow;
                }

                _window.Closed += (_, _) => _window = null;
            }

            _window.Show();
            _window.Activate();
            _window.LoadStream(roomStatus.NickName, roomStatus.RoomUrl, streamUrl, streamKind!.Value);
        });
    }

    private static (string? streamUrl, PreviewStreamKind? streamKind) ResolveStream(RoomStatus roomStatus)
    {
        if (!string.IsNullOrWhiteSpace(roomStatus.HlsUrl))
        {
            return (roomStatus.HlsUrl, PreviewStreamKind.Hls);
        }

        if (!string.IsNullOrWhiteSpace(roomStatus.FlvUrl))
        {
            return (roomStatus.FlvUrl, PreviewStreamKind.Flv);
        }

        return (null, null);
    }
}
