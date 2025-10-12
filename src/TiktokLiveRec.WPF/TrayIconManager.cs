using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using TiktokLiveRec.Core;
using TiktokLiveRec.Extensions;
using TiktokLiveRec.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Violeta.Appearance;
using Wpf.Ui.Violeta.Resources;
using Wpf.Ui.Violeta.Win32;

namespace TiktokLiveRec;

internal class TrayIconManager
{
    private static TrayIconManager _instance = null!;

    private readonly TrayIconHost _icon = null!;

    private readonly TrayMenuItem? _itemAutoRun = null;

    public bool IsShutdownTriggered { get; private set; } = false;

    private TrayIconManager()
    {
        _icon = new TrayIconHost()
        {
            ToolTipText = "TiktokLiveRec",
            Menu =
            [
                new TrayMenuItem()
                {
                    Header = $"v{Assembly.GetExecutingAssembly().GetName().Version!.ToString(3)}",
                    IsEnabled = false,
                },
                new TraySeparator(),
                new TrayMenuItem()
                {
                   Header = "TrayMenuShowMainWindow".Tr(),
                   Tag = "TrayMenuShowMainWindow",
                   Command = new RelayCommand(() =>
                   {
                        Application.Current.MainWindow.Show();
                        Application.Current.MainWindow.Activate();
                        Interop.RestoreWindow(new WindowInteropHelper(Application.Current.MainWindow).Handle);
                    }),
                },
                new TrayMenuItem()
                {
                    Header = "TrayMenuOpenSettings".Tr(),
                    Tag = "TrayMenuOpenSettings",
                    Command = new RelayCommand(() =>
                    {
                        foreach (Window win in Application.Current.Windows.OfType<SettingsWindow>())
                        {
                        win.Close();
                        }

                        _ = new SettingsWindow()
                        {
                        WindowStartupLocation = WindowStartupLocation.CenterScreen,
                        }.ShowDialog();
                    }),
                },
                _itemAutoRun = new TrayMenuItem()
                {
                    Header = "TrayMenuAutoRun".Tr(),
                    Tag = "TrayMenuAutoRun",
                    Command = new RelayCommand(() =>
                    {
                        if (AutoStartupHelper.IsAutorun())
                        {
                            AutoStartupHelper.RemoveAutorunShortcut();
                        }
                        else
                        {
                            AutoStartupHelper.CreateAutorunShortcut();
                        }
                    }),
                },
                new TrayMenuItem()
                {
                    Header = "TrayMenuRestart".Tr(),
                    Tag = "TrayMenuRestart",
                    Command = new RelayCommand(() =>
                    {
                        if (GlobalMonitor.RoomStatus.Values.ToArray().Any(roomStatus => roomStatus.RecordStatus == RecordStatus.Recording))
                        {
                            if (MessageBox.Question("SureOnRecording".Tr()) == MessageBoxResult.Yes)
                            {
                                RuntimeHelper.Restart(forced: true);
                            }
                        }
                        else
                        {
                            RuntimeHelper.Restart(forced: true);
                        }
                    }),
                },
                new TrayMenuItem()
                {
                    Header = "TrayMenuExit".Tr(),
                    Tag = "TrayMenuExit",
                    Command = new RelayCommand(() =>
                    {
                        if (GlobalMonitor.RoomStatus.Values.ToArray().Any(roomStatus => roomStatus.RecordStatus == RecordStatus.Recording))
                        {
                            if (MessageBox.Question("SureOnRecording".Tr()) == MessageBoxResult.Yes)
                            {
                                IsShutdownTriggered = true;
                                Application.Current.Shutdown();
                            }
                        }
                        else
                        {
                            IsShutdownTriggered = true;
                            Application.Current.Shutdown();
                        }
                    }),
                },
            ],
        };
        UpdateTrayIcon();

        _icon.RightDown += (_, _) =>
        {
            _itemAutoRun.IsChecked = AutoStartupHelper.IsAutorun();
        };

        _icon.LeftDoubleClick += (_, _) =>
        {
            if (Application.Current.MainWindow.IsVisible)
            {
                Application.Current.MainWindow.Hide();
            }
            else
            {
                Application.Current.MainWindow.Show();
                Application.Current.MainWindow.Activate();
                Interop.RestoreWindow(new WindowInteropHelper(Application.Current.MainWindow).Handle);
            }
        };

        Locale.CultureChanged += (_, _) =>
        {
            foreach (ITrayMenuItemBase item in _icon.Menu.Items)
            {
                if (item.Tag is string trKey)
                {
                    item.Header = trKey.Tr();
                }
            }
        };

        SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(Configurations.Theme.Get()))
            {
                ThemeManager.Apply(ApplicationTheme.Unknown);
            }
            UpdateTrayIcon();
        };
    }

    public static TrayIconManager GetInstance()
    {
        return _instance ??= new TrayIconManager();
    }

    public static void Start()
    {
        _ = GetInstance();
    }

    public void UpdateTrayIcon()
    {
        _icon.Icon = GetTrayIcon();

        static nint GetTrayIcon()
        {
            try
            {
                if (Configurations.IsUseStatusTray.Get())
                {
                    string status = GlobalMonitor.RoomStatus.Values.ToArray().Any(roomStatus => roomStatus.RecordStatus == RecordStatus.Recording) ? "Recording" : "Unrecording";
                    string theme = GetTraySystemTheme() switch
                    {
                        SystemTheme.Dark or SystemTheme.HCBlack or SystemTheme.Glow or SystemTheme.CapturedMotion => "Dark",
                        _ => "Light",
                    };

                    return new Icon(ResourcesProvider.GetStream($"pack://application:,,,/TiktokLiveRec;component/Assets/{status}{theme}.ico")).Handle;
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
            }
            return Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule?.FileName!)!.Handle;
        }
    }

    /// <summary>
    /// <seealso cref="SystemThemeManager.GetCachedSystemTheme"/>
    /// </summary>
    private static SystemTheme GetTraySystemTheme()
    {
        var currentTheme =
            Registry.GetValue(
                "HKEY_CURRENT_USER\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Themes",
                "CurrentTheme",
                "aero.theme"
            ) as string
            ?? string.Empty;

        if (!string.IsNullOrEmpty(currentTheme))
        {
            currentTheme = currentTheme.ToLower().Trim();

            // This may be changed in the next versions, check the Insider previews
            if (currentTheme.Contains("basic.theme"))
            {
                return SystemTheme.Light;
            }

            if (currentTheme.Contains("aero.theme"))
            {
                return SystemTheme.Light;
            }

            if (currentTheme.Contains("dark.theme"))
            {
                return SystemTheme.Dark;
            }

            if (currentTheme.Contains("hcblack.theme"))
            {
                return SystemTheme.HCBlack;
            }

            if (currentTheme.Contains("hcwhite.theme"))
            {
                return SystemTheme.HCWhite;
            }

            if (currentTheme.Contains("hc1.theme"))
            {
                return SystemTheme.HC1;
            }

            if (currentTheme.Contains("hc2.theme"))
            {
                return SystemTheme.HC2;
            }

            if (currentTheme.Contains("themea.theme"))
            {
                return SystemTheme.Glow;
            }

            if (currentTheme.Contains("themeb.theme"))
            {
                return SystemTheme.CapturedMotion;
            }

            if (currentTheme.Contains("themec.theme"))
            {
                return SystemTheme.Sunrise;
            }

            if (currentTheme.Contains("themed.theme"))
            {
                return SystemTheme.Flow;
            }
        }

        /*if (currentTheme.Contains("custom.theme"))
            return ; custom can be light or dark*/
        var rawSystemUsesLightTheme =
            Registry.GetValue(
                "HKEY_CURRENT_USER\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize",
                "SystemUsesLightTheme",
                1
            ) ?? 1;

        return rawSystemUsesLightTheme is 0 ? SystemTheme.Dark : SystemTheme.Light;
    }
}
