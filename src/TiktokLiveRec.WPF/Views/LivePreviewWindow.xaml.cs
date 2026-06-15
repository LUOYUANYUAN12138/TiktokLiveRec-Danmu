using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using TiktokLiveRec.Core;

namespace TiktokLiveRec.Views;

public partial class LivePreviewWindow : IDisposable
{
    private const int GwlStyle = -16;
    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;
    private const int WsMinimize = 0x20000000;
    private const int WsMaximizeBox = 0x00010000;
    private const int WsSysMenu = 0x00080000;
    private const int WsChild = 0x40000000;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private readonly Panel _previewPanel;
    private Process? _playerProcess;
    private string _currentRoomUrl = string.Empty;
    private string _currentStreamUrl = string.Empty;
    private PreviewStreamKind? _currentStreamKind;
    private nint _embeddedWindowHandle;
    private bool _isDisposed;
    private bool _isLoading;

    public LivePreviewWindow()
    {
        InitializeComponent();

        _previewPanel = new Panel
        {
            BackColor = System.Drawing.Color.Black,
            Dock = DockStyle.Fill,
        };
        PreviewHost.Child = _previewPanel;

        Closed += OnClosed;
        SizeChanged += OnWindowSizeChanged;
    }

    internal void LoadStream(string roomName, string roomUrl, string streamUrl, PreviewStreamKind streamKind)
    {
        Title = $"{roomName} - 直播预览";

        if (string.Equals(_currentRoomUrl, roomUrl, StringComparison.OrdinalIgnoreCase)
         && string.Equals(_currentStreamUrl, streamUrl, StringComparison.OrdinalIgnoreCase)
         && _currentStreamKind == streamKind
         && _playerProcess is { HasExited: false })
        {
            Activate();
            return;
        }

        _currentRoomUrl = roomUrl;
        _currentStreamUrl = streamUrl;
        _currentStreamKind = streamKind;

        ShowStatus("正在连接直播流...", true, isError: false);
        _ = LoadStreamCoreAsync(streamUrl);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Closed -= OnClosed;
        SizeChanged -= OnWindowSizeChanged;
        StopPlayer();
        _previewPanel.Dispose();
    }

    private async Task LoadStreamCoreAsync(string streamUrl)
    {
        if (_isLoading)
        {
            return;
        }

        _isLoading = true;
        try
        {
            StopPlayer();

            string? ffplayPath = Player.ResolveFfplayPath();
            if (string.IsNullOrWhiteSpace(ffplayPath))
            {
                ShowStatus("未找到 ffplay，无法预览直播流", true, isError: true);
                return;
            }

            Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffplayPath,
                    Arguments = $"-fflags nobuffer -flags low_delay -hide_banner -loglevel error -window_title \"{Title}\" -i \"{streamUrl}\"",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
                EnableRaisingEvents = true,
            };
            process.Exited += OnPlayerExited;

            process.Start();
            _playerProcess = process;
            Debug.WriteLine($"[Preview] ffplay started pid={process.Id} url={streamUrl}");

            nint embeddedHandle = await WaitForPlayerWindowAsync(process);
            if (embeddedHandle == nint.Zero)
            {
                ShowStatus("直播预览窗口初始化失败", true, isError: true);
                StopPlayer();
                return;
            }

            AttachPlayerWindow(embeddedHandle);
            ResizeEmbeddedWindow();
            await Task.Delay(200);
            ResizeEmbeddedWindow();
            ShowStatus(string.Empty, false, isError: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Preview] ffplay load failed: {ex}");
            ShowStatus($"直播预览加载失败: {ex.Message}", true, isError: true);
            StopPlayer();
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async Task<nint> WaitForPlayerWindowAsync(Process process)
    {
        for (int i = 0; i < 50; i++)
        {
            if (process.HasExited)
            {
                return nint.Zero;
            }

            nint[] handles = Interop.GetWindowHandleByProcessId(process.Id);
            nint handle = handles.FirstOrDefault(h => h != nint.Zero);
            if (handle != nint.Zero)
            {
                return handle;
            }

            await Task.Delay(100);
        }

        return nint.Zero;
    }

    private void AttachPlayerWindow(nint playerWindowHandle)
    {
        _embeddedWindowHandle = playerWindowHandle;

        nint style = GetWindowLongPtr(playerWindowHandle, GwlStyle);
        nint newStyle = new(style.ToInt64() & ~(WsCaption | WsThickFrame | WsMinimize | WsMaximizeBox | WsSysMenu));
        newStyle = new(newStyle.ToInt64() | WsChild);
        _ = SetWindowLongPtr(playerWindowHandle, GwlStyle, newStyle);

        _ = SetParent(playerWindowHandle, _previewPanel.Handle);
        _ = SetWindowPos(playerWindowHandle, nint.Zero, 0, 0, _previewPanel.Width, _previewPanel.Height, SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        _ = ShowWindow(playerWindowHandle, 5);
    }

    private void ResizeEmbeddedWindow()
    {
        if (_embeddedWindowHandle == nint.Zero || _previewPanel.IsDisposed)
        {
            return;
        }

        _ = SetWindowPos(
            _embeddedWindowHandle,
            nint.Zero,
            0,
            0,
            Math.Max(1, _previewPanel.ClientSize.Width),
            Math.Max(1, _previewPanel.ClientSize.Height),
            SwpNoZOrder | SwpNoActivate);
    }

    private void StopPlayer()
    {
        if (_playerProcess != null)
        {
            try
            {
                _playerProcess.Exited -= OnPlayerExited;
                if (!_playerProcess.HasExited)
                {
                    _playerProcess.Kill(entireProcessTree: true);
                    _playerProcess.WaitForExit(2000);
                }
            }
            catch
            {
            }
            finally
            {
                _playerProcess.Dispose();
                _playerProcess = null;
            }
        }

        _embeddedWindowHandle = nint.Zero;
    }

    private void OnPlayerExited(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_isDisposed || _playerProcess == null)
            {
                return;
            }

            if (_embeddedWindowHandle == nint.Zero)
            {
                ShowStatus("直播流播放失败", true, isError: true);
            }
        }, DispatcherPriority.Background);
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ResizeEmbeddedWindow();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Dispose();
    }

    private void ShowStatus(string message, bool visible, bool isError)
    {
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = isError ? System.Windows.Media.Brushes.OrangeRed : System.Windows.Media.Brushes.White;
        StatusOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        LoadingProgressBar.Visibility = visible && !isError ? Visibility.Visible : Visibility.Collapsed;
    }

    [DllImport("user32.dll", EntryPoint = "SetParent")]
    private static extern nint SetParent(nint hWndChild, nint hWndNewParent);

    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
}
