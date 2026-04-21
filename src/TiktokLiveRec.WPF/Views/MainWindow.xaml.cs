using Microsoft.Toolkit.Uwp.Notifications;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TiktokLiveRec.Core;
using TiktokLiveRec.ViewModels;
using Vanara.PInvoke;
using Wpf.Ui.Controls;

namespace TiktokLiveRec.Views;

public partial class MainWindow : FluentWindow
{
    public MainViewModel ViewModel { get; }
    private INotifyCollectionChanged? _currentDanmuCollection;
    private ScrollViewer? _danmuScrollViewer;
    private bool _isDanmuPinnedToBottom = true;

    public MainWindow()
    {
        DataContext = ViewModel = new();
        InitializeComponent();

        Loaded += (_, _) =>
        {
            AttachDanmuScrollViewer();
            ScrollDanmuToLatest(force: true);
        };

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        AttachDanmuCollection();
        AttachDanmuScrollViewer();
        ScrollDanmuToLatest(force: true);

        if (Configurations.IsUseKeepAwake.Get())
        {
            // Start keep awake
            _ = Kernel32.SetThreadExecutionState(Kernel32.EXECUTION_STATE.ES_CONTINUOUS | Kernel32.EXECUTION_STATE.ES_SYSTEM_REQUIRED | Kernel32.EXECUTION_STATE.ES_AWAYMODE_REQUIRED);
        }

        if (Environment.GetCommandLineArgs().Any(cli => cli == "/autorun"))
        {
            Visibility = System.Windows.Visibility.Hidden;
            WindowState = System.Windows.WindowState.Minimized;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.DisplayedDanmuMessages) or nameof(MainViewModel.SelectedDanmuRoom))
        {
            AttachDanmuCollection();
            AttachDanmuScrollViewer();
            ScrollDanmuToLatest(force: true);
        }
    }

    private void AttachDanmuCollection()
    {
        if (_currentDanmuCollection != null)
        {
            _currentDanmuCollection.CollectionChanged -= OnDanmuCollectionChanged;
        }

        _currentDanmuCollection = ViewModel.DisplayedDanmuMessages;

        if (_currentDanmuCollection != null)
        {
            _currentDanmuCollection.CollectionChanged += OnDanmuCollectionChanged;
        }
    }

    private void AttachDanmuScrollViewer()
    {
        _danmuScrollViewer ??= FindVisualChild<ScrollViewer>(DanmuListBox);
        if (_danmuScrollViewer == null)
        {
            return;
        }

        _danmuScrollViewer.ScrollChanged -= OnDanmuScrollChanged;
        _danmuScrollViewer.ScrollChanged += OnDanmuScrollChanged;
    }

    private void OnDanmuCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
        {
            ScrollDanmuToLatest();
        }
    }

    private void OnDanmuScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        _isDanmuPinnedToBottom = scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 4;
    }

    private void ScrollDanmuToLatest(bool force = false)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!force && !_isDanmuPinnedToBottom)
            {
                return;
            }

            if (DanmuListBox.Items.Count <= 0)
            {
                return;
            }

            object item = DanmuListBox.Items[DanmuListBox.Items.Count - 1];
            DanmuListBox.ScrollIntoView(item);
            _danmuScrollViewer?.ScrollToEnd();
            _isDanmuPinnedToBottom = true;
        }, DispatcherPriority.Background);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        if (!TrayIconManager.GetInstance().IsShutdownTriggered)
        {
            e.Cancel = true;
            Hide();

            if (!Configurations.IsOffRemindCloseToTray.Get())
            {
                Notifier.AddNoticeWithButton("Title".Tr(), "CloseToTrayHint".Tr(), [
                    new ToastContentButtonOption()
                    {
                        Content = "ButtonOfOffRemind".Tr(),
                        Arguments = [("OffRemindTheCloseToTrayHint", bool.TrueString)],
                        ActivationType = ToastActivationType.Background,
                    },
                    new ToastContentButtonOption()
                    {
                        Content = "ButtonOfClose".Tr(),
                        ActivationType = ToastActivationType.Foreground,
                    },
                ]);
            }
        }
        else
        {
            if (_currentDanmuCollection != null)
            {
                _currentDanmuCollection.CollectionChanged -= OnDanmuCollectionChanged;
            }

            if (_danmuScrollViewer != null)
            {
                _danmuScrollViewer.ScrollChanged -= OnDanmuScrollChanged;
            }

            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

            if (Configurations.IsUseKeepAwake.Get())
            {
                // Stop keep awake
                _ = Kernel32.SetThreadExecutionState(Kernel32.EXECUTION_STATE.ES_CONTINUOUS);
            }

            ViewModel.Dispose();
        }
    }

    private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent == null)
        {
            return null;
        }

        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            T? nested = FindVisualChild<T>(child);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }
}
