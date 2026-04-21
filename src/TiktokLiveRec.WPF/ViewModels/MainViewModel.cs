#nullable disable
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using ComputedConverters;
using Fischless.Configuration;
using Flucli;
using Microsoft.Toolkit.Uwp.Notifications;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TiktokLiveRec.Core;
using TiktokLiveRec.Extensions;
using TiktokLiveRec.Models;
using TiktokLiveRec.Threading;
using TiktokLiveRec.Views;
using Vanara.PInvoke;
using Windows.Storage;
using Windows.System;
using Wpf.Ui.Violeta.Controls;
using Wpf.Ui.Violeta.Threading;
using CheckBox = System.Windows.Controls.CheckBox;

namespace TiktokLiveRec.ViewModels;

[ObservableObject]
public partial class MainViewModel : ReactiveObject, IDisposable
{
    private readonly IDouyinDanmuService _douyinDanmuService = new DouyinDanmuService();
    private readonly SemaphoreSlim _danmuRefreshLock = new(1, 1);
    private StreamStatus _lastSelectedDanmuRoomStreamStatus = StreamStatus.Initialized;
    private int _selectedDanmuRoomNotStreamingStreak;

    protected internal ForeverDispatcherTimer DispatcherTimer { get; }

    [ObservableProperty]
    private ReactiveCollection<RoomStatusReactive> roomStatuses = [];

    [ObservableProperty]
    private RoomStatusReactive selectedItem = new();

    [ObservableProperty]
    private ReactiveCollection<RoomStatusReactive> danmuRoomOptions = [];

    [ObservableProperty]
    private RoomStatusReactive selectedDanmuRoom = new();

    [ObservableProperty]
    private ReactiveCollection<DanmuMessage> displayedDanmuMessages = [];

    [ObservableProperty]
    private bool isRecording = false;

    [ObservableProperty]
    private bool isEnableDanmu = Configurations.IsEnableDanmu.Get();

    partial void OnIsEnableDanmuChanged(bool value)
    {
        Configurations.IsEnableDanmu.Set(value);
        ConfigurationManager.Save();
        _ = RefreshDanmuConnectionAsync();
    }

    [ObservableProperty]
    private string danmuPanelTitle = "未选择弹幕房间";

    [ObservableProperty]
    private string danmuStatusText = "已关闭";

    partial void OnSelectedItemChanged(RoomStatusReactive value)
    {
        if (value != null)
        {
            value.RefreshStatus();
        }
    }

    partial void OnSelectedDanmuRoomChanged(RoomStatusReactive value)
    {
        Configurations.SelectedDanmuRoomUrl.Set(value?.RoomUrl ?? string.Empty);
        ConfigurationManager.Save();

        DanmuPanelTitle = string.IsNullOrWhiteSpace(value?.NickName) ? "未选择弹幕房间" : value.NickName;
        DanmuStatusText = value?.DanmuConnectionStateText ?? (IsEnableDanmu ? "待连接" : "已关闭");
        _lastSelectedDanmuRoomStreamStatus = value?.StreamStatus ?? StreamStatus.Initialized;
        _selectedDanmuRoomNotStreamingStreak = 0;
        RebuildDisplayedDanmuMessages();
        _ = RefreshDanmuConnectionAsync();
    }

    [ObservableProperty]
    private bool showDanmuChat = Configurations.ShowDanmuChat.Get();

    partial void OnShowDanmuChatChanged(bool value) => SaveDanmuFilter(() => Configurations.ShowDanmuChat.Set(value));

    [ObservableProperty]
    private bool showDanmuGift = Configurations.ShowDanmuGift.Get();

    partial void OnShowDanmuGiftChanged(bool value) => SaveDanmuFilter(() => Configurations.ShowDanmuGift.Set(value));

    [ObservableProperty]
    private bool showDanmuLike = Configurations.ShowDanmuLike.Get();

    partial void OnShowDanmuLikeChanged(bool value) => SaveDanmuFilter(() => Configurations.ShowDanmuLike.Set(value));

    [ObservableProperty]
    private bool showDanmuMember = Configurations.ShowDanmuMember.Get();

    partial void OnShowDanmuMemberChanged(bool value) => SaveDanmuFilter(() => Configurations.ShowDanmuMember.Set(value));

    [ObservableProperty]
    private bool showDanmuFollow = Configurations.ShowDanmuFollow.Get();

    partial void OnShowDanmuFollowChanged(bool value) => SaveDanmuFilter(() => Configurations.ShowDanmuFollow.Set(value));

    [ObservableProperty]
    private bool showDanmuEmoji = Configurations.ShowDanmuEmoji.Get();

    partial void OnShowDanmuEmojiChanged(bool value) => SaveDanmuFilter(() => Configurations.ShowDanmuEmoji.Set(value));

    [ObservableProperty]
    private bool showDanmuRoomStats = Configurations.ShowDanmuRoomStats.Get();

    partial void OnShowDanmuRoomStatsChanged(bool value) => SaveDanmuFilter(() => Configurations.ShowDanmuRoomStats.Set(value));

    [ObservableProperty]
    private bool showDanmuRoomRank = Configurations.ShowDanmuRoomRank.Get();

    partial void OnShowDanmuRoomRankChanged(bool value) => SaveDanmuFilter(() => Configurations.ShowDanmuRoomRank.Set(value));

    [ObservableProperty]
    private bool showDanmuFansClub = Configurations.ShowDanmuFansClub.Get();

    partial void OnShowDanmuFansClubChanged(bool value) => SaveDanmuFilter(() => Configurations.ShowDanmuFansClub.Set(value));

    partial void OnIsRecordingChanged(bool value)
    {
        TrayIconManager.GetInstance().UpdateTrayIcon();
    }

    [ObservableProperty]
    private bool statusOfIsToNotify = Configurations.IsToNotify.Get();

    [ObservableProperty]
    private bool statusOfIsToRecord = Configurations.IsToRecord.Get();

    [ObservableProperty]
    private bool statusOfIsUseProxy = Configurations.IsUseProxy.Get();

    [ObservableProperty]
    private bool statusOfIsUseKeepAwake = Configurations.IsUseKeepAwake.Get();

    [ObservableProperty]
    private bool statusOfIsUseAutoShutdown = Configurations.IsUseAutoShutdown.Get();

    [ObservableProperty]
    private string statusOfAutoShutdownTime = Configurations.AutoShutdownTime.Get();

    [ObservableProperty]
    private string statusOfRecordFormat = Configurations.RecordFormat.Get();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusOfRoutineIntervalWithUnit))]
    private int statusOfRoutineInterval = Configurations.RoutineInterval.Get();

    public string StatusOfRoutineIntervalWithUnit
    {
        get
        {
            if (StatusOfRoutineInterval > 60000d)
            {
                return $"{Math.Round(StatusOfRoutineInterval / 60000d, 1)}min";
            }
            else if (StatusOfRoutineInterval > 1000d)
            {
                return $"{StatusOfRoutineInterval / 1000d}s";
            }
            else
            {
                return $"{StatusOfRoutineInterval}ms";
            }
        }
    }

    [ObservableProperty]
    private bool isReadyToShutdown = false;

    public CancellationTokenSource? ShutdownCancellationTokenSource { get; private set; } = null;

    public MainViewModel()
    {
        DispatcherTimer = new(TimeSpan.FromSeconds(3), ReloadRoomStatus);

        RoomStatuses.Reset(Configurations.Rooms.Get().Select(room => new RoomStatusReactive()
        {
            NickName = room.NickName,
            RoomUrl = room.RoomUrl,
            IsToNotify = room.IsToNotify,
            IsToRecord = room.IsToRecord,
        }));
        SyncDanmuRoomOptions();

        Locale.CultureChanged += (_, _) =>
        {
            foreach (RoomStatusReactive roomStatusReactive in RoomStatuses)
            {
                roomStatusReactive.RefreshStatus();
            }
        };

        WeakReferenceMessenger.Default.Register<ToastNotificationActivatedMessage>(this, (_, msg) =>
        {
            string arguments = msg.EventArgs.Argument;

            if (!string.IsNullOrEmpty(arguments))
            {
                NameValueCollection parsedArgs = HttpUtility.ParseQueryString(arguments);

                if (parsedArgs["AutoShutdownCancel"] != null)
                {
                    ShutdownCancellationTokenSource?.Cancel();
                }
            }
        });

        _douyinDanmuService.MessageReceived += OnDanmuMessageReceived;
        _douyinDanmuService.ConnectionStateChanged += OnDanmuConnectionStateChanged;
        _douyinDanmuService.ErrorOccurred += OnDanmuErrorOccurred;

        GlobalMonitor.Start();
        ChildProcessTracerPeriodicTimer.Default.WhiteList = ["ffmpeg", "ffplay"];
        ChildProcessTracerPeriodicTimer.Default.Start();
        DispatcherTimer.Start();

        if (RoomStatuses.Count > 0)
        {
            SelectedItem = RoomStatuses[0];
        }

        RoomStatusReactive initialDanmuRoom = DanmuRoomOptions.FirstOrDefault(room => room.RoomUrl == Configurations.SelectedDanmuRoomUrl.Get())
            ?? DanmuRoomOptions.FirstOrDefault()
            ?? new RoomStatusReactive();
        SelectedDanmuRoom = initialDanmuRoom;
    }

    private void ReloadRoomStatus()
    {
        foreach (RoomStatus roomStatus in GlobalMonitor.RoomStatus.Values.ToArray())
        {
            RoomStatusReactive? roomStatusReactive = RoomStatuses.Where(room => room.RoomUrl == roomStatus.RoomUrl).FirstOrDefault();

            if (roomStatusReactive != null)
            {
                if (!string.IsNullOrWhiteSpace(roomStatus.NickName))
                {
                    roomStatusReactive.NickName = roomStatus.NickName;
                }

                if (!string.IsNullOrWhiteSpace(roomStatus.AvatarThumbUrl))
                {
                    roomStatusReactive.AvatarThumbUrl = roomStatus.AvatarThumbUrl;
                }

                roomStatusReactive.StreamStatus = roomStatus.StreamStatus;
                roomStatusReactive.RecordStatus = roomStatus.RecordStatus;
                roomStatusReactive.FlvUrl = roomStatus.FlvUrl;
                roomStatusReactive.HlsUrl = roomStatus.HlsUrl;
                roomStatusReactive.StartTime = roomStatus.Recorder.StartTime;
                roomStatusReactive.EndTime = roomStatus.Recorder.EndTime;
                roomStatusReactive.DanmuConnectionState = roomStatus.DanmuConnectionState;
                roomStatusReactive.DanmuLastMessageTime = roomStatus.DanmuLastMessageTime;
                roomStatusReactive.LastRecordError = roomStatus.LastRecordError;
                roomStatusReactive.LastRecordAttemptTime = roomStatus.LastRecordAttemptTime;
                roomStatusReactive.LastRecordStartCommand = roomStatus.LastRecordStartCommand;
                roomStatusReactive.RefreshDuration();
            }
        }

        IsRecording = RoomStatuses.Any(roomStatusReactive => roomStatusReactive.RecordStatus == RecordStatus.Recording);

        StatusOfIsToNotify = Configurations.IsToNotify.Get();
        StatusOfIsToRecord = Configurations.IsToRecord.Get();
        StatusOfIsUseProxy = Configurations.IsUseProxy.Get();
        StatusOfIsUseKeepAwake = Configurations.IsUseKeepAwake.Get();
        StatusOfIsUseAutoShutdown = Configurations.IsUseAutoShutdown.Get();
        StatusOfAutoShutdownTime = Configurations.AutoShutdownTime.Get();
        StatusOfRecordFormat = Configurations.RecordFormat.Get();
        StatusOfRoutineInterval = Configurations.RoutineInterval.Get();
        DanmuStatusText = SelectedDanmuRoom?.DanmuConnectionStateText ?? (IsEnableDanmu ? "待连接" : "已关闭");
        SyncSelectedDanmuRoomAutoConnection();

        if (StatusOfIsUseAutoShutdown && TimeSpan.TryParse(StatusOfAutoShutdownTime, out TimeSpan targetTime))
        {
            int timeOffset = (int)(DateTime.Now.TimeOfDay - targetTime).TotalSeconds;

            if (timeOffset >= 0 && timeOffset <= 60)
            {
                IsReadyToShutdown = true;
            }

            if (IsReadyToShutdown && !IsRecording)
            {
                if (ShutdownCancellationTokenSource == null)
                {
                    ShutdownCancellationTokenSource = new();

                    Notifier.AddNoticeWithButton("Title".Tr(), "AutoShutdownInTime".Tr(), [
                        new ToastContentButtonOption()
                            {
                                Content = "ButtonOfCancel".Tr(),
                                Arguments = [("AutoShutdownCancel", string.Empty)],
                                ActivationType = ToastActivationType.Foreground,
                            }
                    ]);

                    ApplicationDispatcher.BeginInvoke(async () =>
                    {
                        await Task.Delay(60000);

                        if (!ShutdownCancellationTokenSource.IsCancellationRequested && !IsRecording)
                        {
                            if (Debugger.IsAttached)
                            {
                                _ = MessageBox.Information("AutoShutdown".Tr());
                            }
                            else
                            {
                                _ = Interop.ExitWindowsEx(User32.ExitWindowsFlags.EWX_SHUTDOWN | User32.ExitWindowsFlags.EWX_FORCE);
                            }
                        }

                        ShutdownCancellationTokenSource = null;
                        IsReadyToShutdown = false;
                    });
                }
            }
        }
    }

    private void SyncDanmuRoomOptions()
    {
        DanmuRoomOptions.Reset(RoomStatuses.Where(room => IsDouyinRoom(room.RoomUrl)));

        if (SelectedDanmuRoom == null || string.IsNullOrWhiteSpace(SelectedDanmuRoom.RoomUrl))
        {
            return;
        }

        RoomStatusReactive matched = DanmuRoomOptions.FirstOrDefault(room => room.RoomUrl == SelectedDanmuRoom.RoomUrl);
        if (matched != null && !ReferenceEquals(matched, SelectedDanmuRoom))
        {
            SelectedDanmuRoom = matched;
        }
    }

    private static bool IsDouyinRoom(string roomUrl) =>
        !string.IsNullOrWhiteSpace(roomUrl) && roomUrl.Contains("douyin", StringComparison.OrdinalIgnoreCase);

    private void SaveDanmuFilter(Action saveAction)
    {
        saveAction();
        ConfigurationManager.Save();
        RebuildDisplayedDanmuMessages();
    }

    private bool IsDanmuMessageVisible(DanmuMessage message) => message.Method switch
    {
        DanmuMessageMethod.Chat => ShowDanmuChat,
        DanmuMessageMethod.Gift => ShowDanmuGift,
        DanmuMessageMethod.Like => ShowDanmuLike,
        DanmuMessageMethod.Member => ShowDanmuMember,
        DanmuMessageMethod.Social => ShowDanmuFollow,
        DanmuMessageMethod.EmojiChat => ShowDanmuEmoji,
        DanmuMessageMethod.RoomUserSeq or DanmuMessageMethod.RoomStats => ShowDanmuRoomStats,
        DanmuMessageMethod.RoomRank => ShowDanmuRoomRank,
        DanmuMessageMethod.FansClub => ShowDanmuFansClub,
        _ => true,
    };

    private void RebuildDisplayedDanmuMessages()
    {
        if (SelectedDanmuRoom == null || string.IsNullOrWhiteSpace(SelectedDanmuRoom.RoomUrl))
        {
            DisplayedDanmuMessages.Clear();
            return;
        }

        if (!GlobalMonitor.RoomStatus.TryGetValue(SelectedDanmuRoom.RoomUrl, out RoomStatus roomStatus))
        {
            DisplayedDanmuMessages.Clear();
            return;
        }

        DisplayedDanmuMessages.Reset(roomStatus.DanmuMessages.Where(IsDanmuMessageVisible));
    }

    [RelayCommand]
    private async Task AddRoomAsync()
    {
        AddRoomContentDialog dialog = new();
        ContentDialogResult result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            if (!string.IsNullOrWhiteSpace(dialog.NickName))
            {
                List<Room> rooms = [.. Configurations.Rooms.Get()];

                rooms.RemoveAll(room => room.RoomUrl == dialog.Url);
                rooms.Add(new Room()
                {
                    NickName = dialog.NickName,
                    RoomUrl = dialog.RoomUrl!,
                });
                Configurations.Rooms.Set([.. rooms]);
                ConfigurationManager.Save();

                RoomStatuses.Add(new RoomStatusReactive()
                {
                    NickName = dialog.NickName,
                    RoomUrl = dialog.RoomUrl!,
                });
                SyncDanmuRoomOptions();

                if (RoomStatuses.Count == 1)
                {
                    SelectedItem = RoomStatuses[0];
                }

                if (SelectedDanmuRoom == null || string.IsNullOrWhiteSpace(SelectedDanmuRoom.RoomUrl))
                {
                    SelectedDanmuRoom = DanmuRoomOptions.FirstOrDefault() ?? new RoomStatusReactive();
                }
            }
        }
    }

    [RelayCommand]
    private void OpenSettingsDialog()
    {
        foreach (Window win in Application.Current.Windows.OfType<SettingsWindow>())
        {
            win.Close();
        }

        _ = new SettingsWindow()
        {
            Owner = Application.Current.MainWindow,
        }.ShowDialog();
    }

    [RelayCommand]
    private async Task OpenSaveFolderAsync()
    {
        // TODO: Implement for other platforms
        await Launcher.LaunchFolderAsync(
            await StorageFolder.GetFolderFromPathAsync(
                SaveFolderHelper.GetSaveFolder(Configurations.SaveFolder.Get())
            )
        );
    }

    [RelayCommand]
    private async Task OpenDanmuLogFolderAsync()
    {
        string nickname = SelectedDanmuRoom?.NickName;
        string folder = DanmuLogWriter.Instance.GetLogFolder(nickname);
        await Launcher.LaunchFolderAsync(await StorageFolder.GetFolderFromPathAsync(folder));
    }

    [RelayCommand]
    private void ClearDanmuMessages()
    {
        if (SelectedDanmuRoom == null || string.IsNullOrWhiteSpace(SelectedDanmuRoom.RoomUrl))
        {
            return;
        }

        SelectedDanmuRoom.DanmuMessages.Clear();
        DisplayedDanmuMessages.Clear();

        if (GlobalMonitor.RoomStatus.TryGetValue(SelectedDanmuRoom.RoomUrl, out RoomStatus? roomStatus))
        {
            roomStatus.DanmuMessages.Clear();
        }
    }

    [RelayCommand]
    private async Task OpenSettingsFileFolderAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await "explorer"
                .WithArguments($"/select,\"{ConfigurationManager.FilePath}\"")
                .ExecuteAsync();
        }
        else
        {
            // TODO: Implement for other platforms
            await Launcher.LaunchUriAsync(new Uri(ConfigurationManager.FilePath));
        }
    }

    [RelayCommand]
    private async Task OpenAboutAsync()
    {
        AboutContentDialog dialog = new();
        _ = await dialog.ShowAsync();
    }

    [RelayCommand]
    private async Task PlayRecordAsync()
    {
        if (SelectedItem == null || string.IsNullOrWhiteSpace(SelectedItem.RoomUrl))
        {
            return;
        }

        if (GlobalMonitor.RoomStatus.TryGetValue(SelectedItem.RoomUrl, out RoomStatus? roomStatus)
         && File.Exists(roomStatus.Recorder.FileName))
        {
            await Player.PlayAsync(roomStatus.Recorder.FileName, isSeekable: roomStatus.RecordStatus == RecordStatus.Recording);
        }
        else
        {
            Toast.Warning("PlayerErrorOfNoFile".Tr());
        }
    }

    [RelayCommand]
    private void RowUpRoomUrl()
    {
        if (SelectedItem == null || string.IsNullOrWhiteSpace(SelectedItem.RoomUrl))
        {
            return;
        }

        // SelectedItem's properties is mapped from CollectionView, so we need to find the original item
        RoomStatuses.MoveUp(RoomStatuses.Where(roomStatus => roomStatus.RoomUrl == SelectedItem.RoomUrl).FirstOrDefault()!);
    }

    [RelayCommand]
    private void RowDownRoomUrl()
    {
        if (SelectedItem == null || string.IsNullOrWhiteSpace(SelectedItem.RoomUrl))
        {
            return;
        }

        // SelectedItem's properties is mapped from CollectionView, so we need to find the original item
        RoomStatuses.MoveDown(RoomStatuses.Where(roomStatus => roomStatus.RoomUrl == SelectedItem.RoomUrl).FirstOrDefault()!);
    }

    [RelayCommand]
    private async Task RemoveRoomUrlAsync()
    {
        if (SelectedItem == null || string.IsNullOrWhiteSpace(SelectedItem.RoomUrl))
        {
            return;
        }

        MessageBoxResult result = await MessageBox.QuestionAsync("SureRemoveRoom".Tr(SelectedItem.NickName));

        if (result == MessageBoxResult.Yes)
        {
            string removedRoomUrl = SelectedItem.RoomUrl;

            // Stop and remove from Global status
            if (GlobalMonitor.RoomStatus.TryGetValue(removedRoomUrl, out RoomStatus? roomStatus))
            {
                roomStatus.Recorder.Stop();
                roomStatus.DanmuMessages.Clear();
                _ = GlobalMonitor.RoomStatus.TryRemove(removedRoomUrl, out _);
            }

            // Remove from Reactive UI
            RoomStatusReactive? roomStatusReactive = RoomStatuses.Where(room => room.RoomUrl == roomStatus?.RoomUrl).FirstOrDefault();
            if (roomStatusReactive != null)
            {
                RoomStatuses.Remove(roomStatusReactive);
            }
            SyncDanmuRoomOptions();

            if (RoomStatuses.Count > 0)
            {
                SelectedItem = RoomStatuses[0];
            }
            else
            {
                SelectedItem = new RoomStatusReactive();
            }

            if (SelectedDanmuRoom?.RoomUrl == removedRoomUrl)
            {
                SelectedDanmuRoom = DanmuRoomOptions.FirstOrDefault() ?? new RoomStatusReactive();
            }

            // Remove from Configuration
            List<Room> rooms = [.. Configurations.Rooms.Get()];

            rooms.Remove(rooms.Where(room => room.RoomUrl == removedRoomUrl).FirstOrDefault()!);
            Configurations.Rooms.Set([.. rooms]);
            ConfigurationManager.Save();

            Toast.Success("SuccOp".Tr());
        }
    }

    private async Task RefreshDanmuConnectionAsync()
    {
        await _danmuRefreshLock.WaitAsync();
        try
        {
            if (!IsEnableDanmu)
            {
                DanmuStatusText = "已关闭";

                if (SelectedDanmuRoom != null)
                {
                    SelectedDanmuRoom.DanmuConnectionState = DanmuConnectionState.Disabled;
                }

                await _douyinDanmuService.DisconnectAsync();
                return;
            }

            if (SelectedDanmuRoom == null || string.IsNullOrWhiteSpace(SelectedDanmuRoom.RoomUrl))
            {
                DanmuStatusText = "未选择弹幕房间";
                await _douyinDanmuService.DisconnectAsync();
                return;
            }

            if (SelectedDanmuRoom.StreamStatus != StreamStatus.Streaming)
            {
                SelectedDanmuRoom.DanmuConnectionState = DanmuConnectionState.WaitingForLive;
                DanmuStatusText = SelectedDanmuRoom.DanmuConnectionStateText;
                await _douyinDanmuService.DisconnectAsync();
                return;
            }

            await _douyinDanmuService.SwitchRoomAsync(SelectedDanmuRoom.RoomUrl, SelectedDanmuRoom.NickName);
        }
        finally
        {
            _danmuRefreshLock.Release();
        }
    }

    private void SyncSelectedDanmuRoomAutoConnection()
    {
        if (!IsEnableDanmu || SelectedDanmuRoom == null || string.IsNullOrWhiteSpace(SelectedDanmuRoom.RoomUrl))
        {
            return;
        }

        StreamStatus currentStatus = SelectedDanmuRoom.StreamStatus;
        bool isStreaming = currentStatus == StreamStatus.Streaming;
        bool wasStreaming = _lastSelectedDanmuRoomStreamStatus == StreamStatus.Streaming;

        if (isStreaming)
        {
            _selectedDanmuRoomNotStreamingStreak = 0;

            if (!wasStreaming || SelectedDanmuRoom.DanmuConnectionState is DanmuConnectionState.WaitingForLive or DanmuConnectionState.Idle)
            {
                _ = RefreshDanmuConnectionAsync();
            }
        }
        else
        {
            _selectedDanmuRoomNotStreamingStreak++;

            bool shouldWaitForLive = SelectedDanmuRoom.DanmuConnectionState == DanmuConnectionState.WaitingForLive
                || SelectedDanmuRoom.DanmuConnectionState == DanmuConnectionState.Disabled
                || _selectedDanmuRoomNotStreamingStreak >= 2;

            if (shouldWaitForLive && SelectedDanmuRoom.DanmuConnectionState != DanmuConnectionState.WaitingForLive)
            {
                SelectedDanmuRoom.DanmuConnectionState = DanmuConnectionState.WaitingForLive;
                DanmuStatusText = SelectedDanmuRoom.DanmuConnectionStateText;
                _ = _douyinDanmuService.DisconnectAsync();
            }
        }

        _lastSelectedDanmuRoomStreamStatus = currentStatus;
    }

    private void OnDanmuMessageReceived(DanmuMessage message)
    {
        bool isVisible = IsDanmuMessageVisible(message);
        if (isVisible)
        {
            DanmuLogWriter.Instance.Enqueue(message);
        }

        ApplicationDispatcher.BeginInvoke(() =>
        {
            if (!GlobalMonitor.RoomStatus.TryGetValue(message.RoomUrl, out RoomStatus? roomStatus))
            {
                roomStatus = new RoomStatus()
                {
                    RoomUrl = message.RoomUrl,
                    NickName = message.RoomNickname,
                };
                GlobalMonitor.RoomStatus.TryAdd(message.RoomUrl, roomStatus);
            }

            roomStatus.DanmuLastMessageTime = message.RawTimestamp.LocalDateTime;
            roomStatus.DanmuMessages.Add(message);

            int maxItems = Math.Max(50, Configurations.DanmuMaxItems.Get());
            while (roomStatus.DanmuMessages.Count > maxItems)
            {
                roomStatus.DanmuMessages.RemoveAt(0);
            }

            RoomStatusReactive? reactive = RoomStatuses.FirstOrDefault(room => room.RoomUrl == message.RoomUrl);
            if (reactive == null)
            {
                return;
            }

            reactive.DanmuLastMessageTime = roomStatus.DanmuLastMessageTime;
            reactive.DanmuMessages.Reset(roomStatus.DanmuMessages);

            if (SelectedDanmuRoom?.RoomUrl == message.RoomUrl)
            {
                if (isVisible)
                {
                    DisplayedDanmuMessages.Add(message);
                    int displayedMaxItems = Math.Max(50, Configurations.DanmuMaxItems.Get());
                    while (DisplayedDanmuMessages.Count > displayedMaxItems)
                    {
                        DisplayedDanmuMessages.RemoveAt(0);
                    }
                }

                DanmuStatusText = reactive.DanmuConnectionStateText;
            }
        });
    }

    private void OnDanmuConnectionStateChanged(string roomUrl, DanmuConnectionState state)
    {
        ApplicationDispatcher.BeginInvoke(() =>
        {
            if (!GlobalMonitor.RoomStatus.TryGetValue(roomUrl, out RoomStatus? roomStatus))
            {
                roomStatus = new RoomStatus()
                {
                    RoomUrl = roomUrl,
                    NickName = RoomStatuses.FirstOrDefault(room => room.RoomUrl == roomUrl)?.NickName ?? string.Empty,
                };
                GlobalMonitor.RoomStatus.TryAdd(roomUrl, roomStatus);
            }

            if (roomStatus != null)
            {
                roomStatus.DanmuConnectionState = state;
            }

            RoomStatusReactive? reactive = RoomStatuses.FirstOrDefault(room => room.RoomUrl == roomUrl);
            if (reactive != null)
            {
                reactive.DanmuConnectionState = state;

                if (SelectedDanmuRoom?.RoomUrl == roomUrl)
                {
                    DanmuStatusText = reactive.DanmuConnectionStateText;
                }
            }
        });
    }

    private void OnDanmuErrorOccurred(string roomUrl, string reason)
    {
        ApplicationDispatcher.BeginInvoke(() =>
        {
            if (SelectedDanmuRoom?.RoomUrl == roomUrl)
            {
                DanmuStatusText = $"连接失败: {reason}";
            }
        });
    }

    [RelayCommand]
    private async Task GotoRoomUrlAsync()
    {
        if (SelectedItem == null || string.IsNullOrWhiteSpace(SelectedItem.RoomUrl))
        {
            return;
        }

        // TODO: Implement for other platforms
        await Launcher.LaunchUriAsync(new Uri(SelectedItem.RoomUrl));
    }

    [RelayCommand]
    private async Task StopRecordAsync()
    {
        if (SelectedItem == null || string.IsNullOrWhiteSpace(SelectedItem.RoomUrl))
        {
            return;
        }

        if (GlobalMonitor.RoomStatus.TryGetValue(SelectedItem.RoomUrl, out RoomStatus? roomStatus))
        {
            if (roomStatus.RecordStatus == RecordStatus.Recording)
            {
                // https://github.com/emako/TiktokLiveRec/issues/13
                // https://github.com/emako/TiktokLiveRec/issues/19

                StackPanel content = new();
                CheckBox checkBox = new()
                {
                    Content = "EnableRecord".Tr(),
                    DataContext = SelectedItem,
                };

                // Do not use `CheckBox::Checked`, because it will be triggered when the CheckBox is loaded
                checkBox.Click += (_, _) =>
                {
                    IsToRecord();
                    Toast.Success("SuccOp".Tr());
                };

                // We not need to binding with two way, because we update the config through method `IsToRecord()`.
                checkBox.SetBinding(CheckBox.IsCheckedProperty, nameof(RoomStatusReactive.IsToRecord));

                content.Children.Add(new TextBlock()
                {
                    Text = "SureStopRecord".Tr(roomStatus.NickName)
                });
                content.Children.Add(checkBox);

                ContentDialog dialog = new()
                {
                    Title = "StopRecord".Tr(),
                    Content = content,
                    CloseButtonText = "ButtonOfCancel".Tr(),
                    PrimaryButtonText = "StopRecord".Tr(),
                    DefaultButton = ContentDialogButton.Primary,
                };

                ContentDialogResult result = await dialog.ShowAsync();

                if (result == ContentDialogResult.Primary)
                {
                    roomStatus.Recorder.Stop();
                    Toast.Success("SuccOp".Tr());
                }
            }
            else
            {
                Toast.Warning("NoRecordTask".Tr());
            }
        }
        else
        {
            Toast.Warning("NoRecordTask".Tr());
        }
    }

    [RelayCommand]
    private void ShowRecordLog()
    {
        if (SelectedItem == null || string.IsNullOrWhiteSpace(SelectedItem.RoomUrl))
        {
            return;
        }

        // TODO
        Toast.Warning("ComingSoon".Tr() + " ...");
    }

    [RelayCommand]
    private void IsToNotify()
    {
        if (SelectedItem == null || string.IsNullOrWhiteSpace(SelectedItem.RoomUrl))
        {
            return;
        }

        RoomStatusReactive? roomStatusReactive = RoomStatuses.Where(room => room.RoomUrl == SelectedItem.RoomUrl).FirstOrDefault();

        if (roomStatusReactive != null)
        {
            roomStatusReactive.IsToNotify = SelectedItem.IsToNotify;
        }

        Room[] rooms = Configurations.Rooms.Get();
        Room? room = rooms.Where(room => room.RoomUrl == SelectedItem.RoomUrl).FirstOrDefault();

        if (room != null)
        {
            room.IsToNotify = SelectedItem.IsToNotify;
        }
        Configurations.Rooms.Set(rooms);
        ConfigurationManager.Save();
    }

    [RelayCommand]
    private void IsToRecord()
    {
        if (SelectedItem == null || string.IsNullOrWhiteSpace(SelectedItem.RoomUrl))
        {
            return;
        }

        RoomStatusReactive? roomStatusReactive = RoomStatuses.Where(room => room.RoomUrl == SelectedItem.RoomUrl).FirstOrDefault();

        if (roomStatusReactive != null)
        {
            roomStatusReactive.IsToRecord = SelectedItem.IsToRecord;
        }

        Room[] rooms = Configurations.Rooms.Get();
        Room? room = rooms.Where(room => room.RoomUrl == SelectedItem.RoomUrl).FirstOrDefault();

        if (room != null)
        {
            room.IsToRecord = SelectedItem.IsToRecord;
        }
        Configurations.Rooms.Set(rooms);
        ConfigurationManager.Save();
    }

    [RelayCommand]
    private void OnContextMenuLoaded(RelayEventParameter param)
    {
        ContextMenu sender = (ContextMenu)param.Deconstruct().Sender;

        sender.Opened -= ContextMenuOpened;
        sender.Opened += ContextMenuOpened;

        // Closure method
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ContextMenuOpened(object sender, RoutedEventArgs e)
        {
            if (sender is ContextMenu { } contextMenu
             && contextMenu.Parent is Popup { } popup
             && popup.PlacementTarget is DataGrid { } dataGrid)
            {
                if (dataGrid.InputHitTest(Mouse.GetPosition(dataGrid)) is FrameworkElement { } element)
                {
                    if (GetDataGridRow(element) is DataGridRow { } row)
                    {
                        if (row.DataContext is RoomStatusReactive { } data)
                        {
                            _ = data.MapTo(SelectedItem);

                            foreach (UIElement d in ((ContextMenu)sender).Items.OfType<UIElement>())
                            {
                                d.Visibility = Visibility.Visible;
                            }
                        }
                    }
                    else
                    {
                        ((ContextMenu)sender).IsOpen = false;
                        _ = SelectedItem.MapFrom(new RoomStatusReactive());

                        foreach (UIElement d in ((ContextMenu)sender).Items.OfType<UIElement>())
                        {
                            d.Visibility = Visibility.Collapsed;
                        }
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static DataGridRow? GetDataGridRow(FrameworkElement? element)
            {
                while (element != null && element is not DataGridRow)
                {
                    element = VisualTreeHelper.GetParent(element) as FrameworkElement;
                }
                return element as DataGridRow;
            }
        }
    }

    public void Dispose()
    {
        _douyinDanmuService.Dispose();
        DanmuLogWriter.Instance.Dispose();
    }
}

