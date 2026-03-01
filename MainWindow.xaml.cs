using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Networking.Connectivity;
using Windows.UI;

namespace AutoLogin;

public sealed partial class MainWindow : Window
{
    private const string CaptivePortalUrl = "https://connectivitycheck.gstatic.com/generate_204";
    private const string DefaultHomeUrl = "https://www.google.com";
    private const string LogFileName = "autologin.log";

    private const int TrayIconId = 1;
    private const uint WM_TRAYICON = 0x0400 + 100;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int WM_NULL = 0x0000;

    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_NONOTIFY = 0x0080;

    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint MF_POPUP = 0x0010;
    private const uint MF_GRAYED = 0x0001;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const uint LR_DEFAULTSIZE = 0x0040;

    private const uint IDI_APPLICATION = 0x7F00;

    private const uint MenuResume = 1001;
    private const uint MenuDisable = 1002;
    private const uint MenuPause5 = 1105;
    private const uint MenuPause10 = 1110;
    private const uint MenuPause15 = 1115;
    private const uint MenuPause30 = 1130;
    private const uint MenuPause60 = 1160;
    private const uint MenuExit = 1200;

    private static readonly string UserDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AutoLogin",
        "WebView2Profile");

    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AutoLogin",
        "logs");

    private static readonly string LogFilePath = Path.Combine(LogDirectory, LogFileName);
    private static readonly object LogLock = new();

    private bool _initialized;
    private bool _titleBarConfigured;
    private bool _isCaptivePortalActive;
    private bool _isWindowVisible;
    private bool _isMonitoringDisabledIndefinitely;
    private bool _isExiting;
    private DateTimeOffset? _monitoringPausedUntilUtc;
    private DateTimeOffset _lastNetworkEvaluationUtc = DateTimeOffset.MinValue;
    private string? _returnUrlBeforeCaptive;

    private readonly WebView2 _browser;
    private readonly Border _titleBar;
    private readonly Border _titleBarDragRegion;
    private readonly TextBlock _titleText;
    private readonly DispatcherQueueTimer _pauseTimer;

    private string? _trayWindowClassName;
    private WndProcDelegate? _trayWndProcDelegate;
    private IntPtr _trayWindowHandle;
    private IntPtr _trayIconHandle;
    private bool _trayIconAdded;

    public MainWindow()
    {
        SystemBackdrop = new MicaBackdrop();

        var root = new Grid
        {
            Margin = new Thickness(0)
        };

        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _titleText = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(44, 0, 140, 0),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = "AutoLogin",
            IsHitTestVisible = false
        };

        _titleBarDragRegion = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0))
        };

        var refreshButton = new Button
        {
            Width = 28,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(0),
            Content = new FontIcon
            {
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                Glyph = "\uE72C",
                FontSize = 14
            }
        };
        refreshButton.Click += RefreshButton_Click;

        var titleBarGrid = new Grid();
        titleBarGrid.Children.Add(_titleBarDragRegion);
        titleBarGrid.Children.Add(_titleText);
        titleBarGrid.Children.Add(refreshButton);

        _titleBar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            Child = titleBarGrid,
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(8, 8, 8, 2)
        };
        Grid.SetRow(_titleBar, 0);
        root.Children.Add(_titleBar);

        _browser = new WebView2
        {
            Margin = new Thickness(8, 2, 8, 8)
        };
        Grid.SetRow(_browser, 1);
        root.Children.Add(_browser);

        Content = root;

        _pauseTimer = DispatcherQueue.CreateTimer();
        _pauseTimer.IsRepeating = false;
        _pauseTimer.Tick += (_, _) =>
        {
            Log("TRAY", "Pause timeout elapsed, monitoring resumed.");
            ResumeMonitoringNow();
        };

        Activated += MainWindow_Activated;
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        ConfigureMicaTitleBar();

        _initialized = true;
        Directory.CreateDirectory(UserDataFolder);
        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, UserDataFolder, null);
        await _browser.EnsureCoreWebView2Async(environment);
        _browser.CoreWebView2.SourceChanged += CoreWebView2_SourceChanged;

        InitializeTrayIcon();

        NetworkInformation.NetworkStatusChanged += NetworkInformation_NetworkStatusChanged;
        SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
        AppWindow.Closing += AppWindow_Closing;
        Closed += MainWindow_Closed;

        _browser.Source = new Uri(DefaultHomeUrl);
        UpdateWindowTitle(_browser.Source?.ToString());

        HideToBackground();
        EvaluateNetworkState();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        NetworkInformation.NetworkStatusChanged -= NetworkInformation_NetworkStatusChanged;
        SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
        AppWindow.Closing -= AppWindow_Closing;
        Closed -= MainWindow_Closed;

        _pauseTimer.Stop();
        DestroyTrayIcon();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isExiting)
        {
            return;
        }

        args.Cancel = true;
        HideToBackground();
    }

    private void NetworkInformation_NetworkStatusChanged(object sender)
    {
        DispatcherQueue.TryEnqueue(EvaluateNetworkState);
    }

    private void SystemEvents_PowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Log("NETWORK", "System resume detected.");
            DispatcherQueue.TryEnqueue(EvaluateNetworkState);
        }
    }

    private void EvaluateNetworkState()
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastNetworkEvaluationUtc) < TimeSpan.FromMilliseconds(250))
        {
            return;
        }

        _lastNetworkEvaluationUtc = now;

        if (!IsMonitoringActive())
        {
            Log("NETWORK", "Monitoring paused/disabled. State evaluation skipped.");
            return;
        }

        var profile = NetworkInformation.GetInternetConnectionProfile();
        var connectivityLevel = profile?.GetNetworkConnectivityLevel() ?? NetworkConnectivityLevel.None;
        Log("NETWORK", $"Connectivity level: {connectivityLevel}");

        if (connectivityLevel == NetworkConnectivityLevel.ConstrainedInternetAccess)
        {
            HandleCaptivePortalDetected();
            return;
        }

        if (connectivityLevel == NetworkConnectivityLevel.InternetAccess)
        {
            HandleInternetRestored();
            return;
        }

        if (_isCaptivePortalActive)
        {
            Log("WINDOW", "Connectivity changed away from captive portal, hiding window.");
            _isCaptivePortalActive = false;
            HideToBackground();
        }
    }

    private void HandleCaptivePortalDetected()
    {
        if (!IsMonitoringActive())
        {
            return;
        }

        if (_browser.CoreWebView2 is null)
        {
            return;
        }

        var currentUrl = GetCurrentUrl();
        if (!_isCaptivePortalActive)
        {
            if (!IsCaptivePortalUrl(currentUrl))
            {
                _returnUrlBeforeCaptive = currentUrl;
            }

            _isCaptivePortalActive = true;
        }

        ShowForCaptivePortal();

        if (!IsCaptivePortalUrl(currentUrl))
        {
            _browser.Source = new Uri(CaptivePortalUrl);
        }
    }

    private void HandleInternetRestored()
    {
        if (!_isCaptivePortalActive)
        {
            return;
        }

        _isCaptivePortalActive = false;

        if (!string.IsNullOrWhiteSpace(_returnUrlBeforeCaptive))
        {
            var target = _returnUrlBeforeCaptive;
            _returnUrlBeforeCaptive = null;

            if (!IsSameUrl(GetCurrentUrl(), target) && Uri.TryCreate(target, UriKind.Absolute, out var returnUri))
            {
                _browser.Source = returnUri;
            }
        }

        HideToBackground();
    }

    private void ShowForCaptivePortal()
    {
        if (_isWindowVisible)
        {
            Activate();
            return;
        }

        Log("WINDOW", "Opening window for captive portal authentication.");
        AppWindow.Show();
        _isWindowVisible = true;
        Activate();
    }

    private void HideToBackground()
    {
        if (_isWindowVisible)
        {
            Log("WINDOW", "Closing/hiding window to background.");
        }

        AppWindow.Hide();
        _isWindowVisible = false;
    }

    private string? GetCurrentUrl()
    {
        if (_browser.CoreWebView2 is not null)
        {
            return _browser.CoreWebView2.Source;
        }

        return _browser.Source?.ToString();
    }

    private static bool IsCaptivePortalUrl(string? url)
    {
        return IsSameUrl(url, CaptivePortalUrl);
    }

    private static bool IsSameUrl(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(left.TrimEnd('/'), right.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private void ConfigureMicaTitleBar()
    {
        if (_titleBarConfigured)
        {
            return;
        }

        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(_titleBarDragRegion);

        var titleBar = AppWindow.TitleBar;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        titleBar.ButtonBackgroundColor = Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonInactiveBackgroundColor = Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonHoverBackgroundColor = Color.FromArgb(32, 255, 255, 255);
        titleBar.ButtonPressedBackgroundColor = Color.FromArgb(48, 255, 255, 255);

        _titleBarConfigured = true;
    }

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
    }

    private void CoreWebView2_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        UpdateWindowTitle(_browser.CoreWebView2?.Source);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        Log("TRAY", "Reload button clicked.");

        if (_browser.CoreWebView2 is not null)
        {
            _browser.CoreWebView2.Reload();
            return;
        }

        if (_browser.Source is not null)
        {
            _browser.Source = _browser.Source;
        }
    }

    private void UpdateWindowTitle(string? url)
    {
        var title = string.IsNullOrWhiteSpace(url) ? "AutoLogin" : $"{url} - AutoLogin";
        AppWindow.Title = title;
        _titleText.Text = title;
    }

    private bool IsMonitoringActive()
    {
        if (_isMonitoringDisabledIndefinitely)
        {
            return false;
        }

        if (_monitoringPausedUntilUtc.HasValue)
        {
            return DateTimeOffset.UtcNow >= _monitoringPausedUntilUtc.Value;
        }

        return true;
    }

    private void DisableMonitoringIndefinitely()
    {
        _isMonitoringDisabledIndefinitely = true;
        _monitoringPausedUntilUtc = null;
        _pauseTimer.Stop();
        _isCaptivePortalActive = false;
        HideToBackground();

        Log("TRAY", "Monitoring disabled indefinitely.");
    }

    private void PauseMonitoring(int minutes)
    {
        _isMonitoringDisabledIndefinitely = false;
        _monitoringPausedUntilUtc = DateTimeOffset.UtcNow.AddMinutes(minutes);

        _pauseTimer.Stop();
        _pauseTimer.Interval = TimeSpan.FromMinutes(minutes);
        _pauseTimer.Start();

        _isCaptivePortalActive = false;
        HideToBackground();

        Log("TRAY", $"Monitoring paused for {minutes} minutes.");
    }

    private void ResumeMonitoringNow()
    {
        _isMonitoringDisabledIndefinitely = false;
        _monitoringPausedUntilUtc = null;
        _pauseTimer.Stop();

        Log("TRAY", "Monitoring resumed.");
        EvaluateNetworkState();
    }

    private void ExitApplication()
    {
        _isExiting = true;
        _pauseTimer.Stop();
        DestroyTrayIcon();

        try
        {
            AppWindow.Closing -= AppWindow_Closing;
        }
        catch
        {
        }

        Application.Current.Exit();
    }

    private void InitializeTrayIcon()
    {
        _trayWndProcDelegate = TrayWindowProc;
        _trayWindowClassName = $"AutoLogin.TrayWindow.{Guid.NewGuid():N}";

        var wndClass = new WNDCLASS
        {
            lpfnWndProc = _trayWndProcDelegate,
            lpszClassName = _trayWindowClassName
        };

        var classAtom = RegisterClass(ref wndClass);
        if (classAtom == 0)
        {
            return;
        }

        _trayWindowHandle = CreateWindowEx(
            0,
            _trayWindowClassName,
            string.Empty,
            0,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_trayWindowHandle == IntPtr.Zero)
        {
            return;
        }

        _trayIconHandle = LoadTrayIconHandle();

        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _trayWindowHandle,
            uID = TrayIconId,
            uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _trayIconHandle,
            szTip = "AutoLogin"
        };

        _trayIconAdded = Shell_NotifyIcon(NIM_ADD, ref data);
        if (_trayIconAdded)
        {
            Log("TRAY", "Tray icon initialized.");
        }
    }

    private void DestroyTrayIcon()
    {
        if (_trayIconAdded && _trayWindowHandle != IntPtr.Zero)
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _trayWindowHandle,
                uID = TrayIconId
            };

            Shell_NotifyIcon(NIM_DELETE, ref data);
            _trayIconAdded = false;
        }

        if (_trayIconHandle != IntPtr.Zero)
        {
            DestroyIcon(_trayIconHandle);
            _trayIconHandle = IntPtr.Zero;
        }

        if (_trayWindowHandle != IntPtr.Zero)
        {
            DestroyWindow(_trayWindowHandle);
            _trayWindowHandle = IntPtr.Zero;
        }

        if (!string.IsNullOrWhiteSpace(_trayWindowClassName))
        {
            UnregisterClass(_trayWindowClassName, IntPtr.Zero);
            _trayWindowClassName = null;
        }
    }

    private IntPtr TrayWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAYICON)
        {
            var mouseMessage = lParam.ToInt32();
            if (mouseMessage == WM_RBUTTONUP || mouseMessage == WM_CONTEXTMENU)
            {
                ShowTrayContextMenu();
                return IntPtr.Zero;
            }
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowTrayContextMenu()
    {
        Log("TRAY", "Context menu opened.");

        var isPaused = _monitoringPausedUntilUtc.HasValue && DateTimeOffset.UtcNow < _monitoringPausedUntilUtc.Value;
        var canResume = _isMonitoringDisabledIndefinitely || isPaused;

        var menu = CreatePopupMenu();
        var pauseSubmenu = CreatePopupMenu();
        if (menu == IntPtr.Zero || pauseSubmenu == IntPtr.Zero)
        {
            return;
        }

        if (canResume)
        {
            var resumeText = "Resume monitoring";
            if (isPaused && _monitoringPausedUntilUtc.HasValue)
            {
                var remaining = _monitoringPausedUntilUtc.Value - DateTimeOffset.UtcNow;
                resumeText = $"Resume monitoring ({Math.Max(0, (int)Math.Ceiling(remaining.TotalMinutes))}m left)";
            }
            AppendMenu(menu, MF_STRING, (UIntPtr)MenuResume, resumeText);
        }

        var disableFlags = _isMonitoringDisabledIndefinitely ? MF_STRING | MF_GRAYED : MF_STRING;
        AppendMenu(menu, disableFlags, (UIntPtr)MenuDisable, "Disable indefinitely");

        AppendMenu(pauseSubmenu, MF_STRING, (UIntPtr)MenuPause5, "5 minutes");
        AppendMenu(pauseSubmenu, MF_STRING, (UIntPtr)MenuPause10, "10 minutes");
        AppendMenu(pauseSubmenu, MF_STRING, (UIntPtr)MenuPause15, "15 minutes");
        AppendMenu(pauseSubmenu, MF_STRING, (UIntPtr)MenuPause30, "30 minutes");
        AppendMenu(pauseSubmenu, MF_STRING, (UIntPtr)MenuPause60, "60 minutes");

        var pauseMenuFlags = _isMonitoringDisabledIndefinitely ? MF_POPUP | MF_GRAYED : MF_POPUP;
        AppendMenu(menu, pauseMenuFlags, (UIntPtr)pauseSubmenu.ToInt64(), "Pause monitoring for");

        AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, string.Empty);
        AppendMenu(menu, MF_STRING, (UIntPtr)MenuExit, "Exit");

        GetCursorPos(out var point);
        SetForegroundWindow(_trayWindowHandle);

        var selected = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_NONOTIFY, point.X, point.Y, _trayWindowHandle, IntPtr.Zero);
        if (selected != 0)
        {
            HandleTrayCommand(selected);
        }

        DestroyMenu(pauseSubmenu);
        DestroyMenu(menu);
        PostMessage(_trayWindowHandle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
    }

    private void HandleTrayCommand(uint commandId)
    {
        switch (commandId)
        {
            case MenuResume:
                Log("TRAY", "Resume monitoring clicked.");
                ResumeMonitoringNow();
                break;
            case MenuDisable:
                Log("TRAY", "Disable indefinitely clicked.");
                DisableMonitoringIndefinitely();
                break;
            case MenuPause5:
                PauseMonitoring(5);
                break;
            case MenuPause10:
                PauseMonitoring(10);
                break;
            case MenuPause15:
                PauseMonitoring(15);
                break;
            case MenuPause30:
                PauseMonitoring(30);
                break;
            case MenuPause60:
                PauseMonitoring(60);
                break;
            case MenuExit:
                Log("TRAY", "Exit clicked.");
                ExitApplication();
                break;
        }
    }

    private static IntPtr LoadTrayIconHandle()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "icon.ico");
            if (File.Exists(iconPath))
            {
                var icon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                if (icon != IntPtr.Zero)
                {
                    return icon;
                }
            }
        }
        catch
        {
        }

        return LoadIcon(IntPtr.Zero, (IntPtr)IDI_APPLICATION);
    }

    private static void Log(string category, string message)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(LogDirectory);
                var line = $"{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)} [{category}] {message}{Environment.NewLine}";
                File.AppendAllText(LogFilePath, line);
            }
        }
        catch
        {
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszMenuName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass([In] ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
