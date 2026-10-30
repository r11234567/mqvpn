using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace TruckersMP.Proxy;

internal sealed class ProfileRowViewModel : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _server = string.Empty;
    private string _policyName = string.Empty;
    private string _latencyText = "--";
    private int? _latencyMs;
    private string _trafficText = string.Empty;
    private bool _isActive;

    public required ProxyProfile Profile { get; init; }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(StatusBadgeBackground));
            OnPropertyChanged(nameof(StatusBadgeBorder));
            OnPropertyChanged(nameof(StatusBadgeForeground));
        }
    }

    public string StatusLabel => IsActive ? "活动中" : "待机";

    public Brush StatusBadgeBackground =>
        Application.Current?.TryFindResource(IsActive ? "StatusActiveBadgeBgBrush" : "StatusStandbyBadgeBgBrush") as Brush
        ?? Brushes.Transparent;

    public Brush StatusBadgeBorder =>
        Application.Current?.TryFindResource(IsActive ? "StatusActiveBadgeBorderBrush" : "StatusStandbyBadgeBorderBrush") as Brush
        ?? Brushes.Transparent;

    public Brush StatusBadgeForeground =>
        Application.Current?.TryFindResource(IsActive ? "StatusActiveBadgeFgBrush" : "StatusStandbyBadgeFgBrush") as Brush
        ?? Brushes.White;

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public string Server
    {
        get => _server;
        set { _server = value; OnPropertyChanged(); }
    }

    public string PolicyName
    {
        get => _policyName;
        set { _policyName = value; OnPropertyChanged(); }
    }

    public int? LatencyMs
    {
        get => _latencyMs;
        set
        {
            _latencyMs = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LatencyBrush));
        }
    }

    public string LatencyText
    {
        get => _latencyText;
        set { _latencyText = value; OnPropertyChanged(); }
    }

    private static readonly SolidColorBrush _lightGreenBrush = new(Color.FromRgb(0x05, 0x96, 0x69));
    private static readonly SolidColorBrush _lightAmberBrush = new(Color.FromRgb(0xB4, 0x53, 0x09));
    private static readonly SolidColorBrush _lightRedBrush = new(Color.FromRgb(0xDC, 0x26, 0x26));

    static ProfileRowViewModel()
    {
        _lightGreenBrush.Freeze();
        _lightAmberBrush.Freeze();
        _lightRedBrush.Freeze();
    }

    public Brush LatencyBrush
    {
        get
        {
            var isDark = FluentUi.CurrentTheme == ApplicationTheme.Dark;
            if (LatencyMs is null or <= 0)
                return FluentUi.MutedBrush;
            if (LatencyMs < 250)
                return isDark ? FluentUi.GreenBrush : _lightGreenBrush;
            if (LatencyMs < 500)
                return isDark ? FluentUi.AmberBrush : _lightAmberBrush;
            return isDark ? FluentUi.RedBrush : _lightRedBrush;
        }
    }

    public string TrafficText
    {
        get => _trafficText;
        set { _trafficText = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshThemeVisuals()
    {
        OnPropertyChanged(nameof(StatusBadgeBackground));
        OnPropertyChanged(nameof(StatusBadgeBorder));
        OnPropertyChanged(nameof(StatusBadgeForeground));
        OnPropertyChanged(nameof(LatencyBrush));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed class MainForm : FluentWindow
{
    private static readonly Color Green = FluentUi.EmeraldGreen;
    private static readonly Color Amber = FluentUi.AmberGold;
    private static readonly Color Red = FluentUi.CrimsonRed;
    private static Color Muted => FluentUi.GetMutedColor();

    private static readonly Regex AnsiEscape = new(
        "\\x1B\\[[0-?]*[ -/]*[@-~]",
        RegexOptions.Compiled);
    private static readonly Regex AnsiOperatingSystemCommand = new(
        "\\x1B\\][^\\a\\x1B]*(?:\\a|\\x1B\\\\)",
        RegexOptions.Compiled);
    private static readonly Regex TerminalControlCharacters = new(
        "[\\x00-\\x08\\x0B-\\x1F\\x7F-\\x9F]",
        RegexOptions.Compiled);
    private static readonly char[] LineBreaks = ['\r', '\n'];

    private readonly LogBuffer _logBuffer = new();
    private readonly SemaphoreSlim _tunOperation = new(1, 1);
    private readonly SemaphoreSlim _latencyProbeOperation = new(1, 1);
    private readonly DispatcherTimer _logTimer;
    private readonly DispatcherTimer _trafficTimer;
    private readonly ToggleSwitch _proxyToggle = new();
    private readonly ToggleSwitch _tunToggle = new();
    private readonly System.Windows.Controls.TextBlock _proxyStatus = new();
    private readonly System.Windows.Controls.TextBlock _tunStatus = new();
    private readonly Ellipse _proxyDot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Ellipse _tunDot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Ellipse _footerDot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0) };
    private readonly System.Windows.Controls.TextBlock _footerStatus = new();
    private readonly System.Windows.Controls.TextBlock _proxySpeedText = new();
    private readonly System.Windows.Controls.TextBlock _directSpeedText = new();
    private readonly System.Windows.Controls.TextBlock _todayTrafficText = new();
    private readonly System.Windows.Controls.TextBlock _latencyStatus = new();
    private readonly System.Windows.Controls.TextBlock _headerLatencyBadge = new();
    private readonly System.Windows.Controls.TextBlock _importantCount = new();
    private readonly System.Windows.Controls.TextBlock _subtitle = new();
    private readonly System.Windows.Controls.TextBlock _activeProfileTitle = new();
    private readonly Wpf.Ui.Controls.DataGrid _profilesGrid = new();
    private readonly ObservableCollection<ProfileRowViewModel> _profileRows = [];
    private readonly ComboBox _routingSelector = new() { Width = 220 };
    private readonly LogView _importantLog;
    private readonly LogView _mqvpnLog;
    private readonly LogView _xrayLog;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private readonly ContextMenu _trayContextMenu = new();
    private readonly ContextMenu _profileContextMenu = new();
    private System.Windows.Controls.MenuItem? _menuItemTest;
    private System.Windows.Controls.MenuItem? _menuItemActivate;
    private System.Windows.Controls.MenuItem? _menuItemClone;
    private System.Windows.Controls.MenuItem? _menuItemEdit;
    private System.Windows.Controls.MenuItem? _menuItemMoveUp;
    private System.Windows.Controls.MenuItem? _menuItemMoveDown;
    private System.Windows.Controls.MenuItem? _menuItemDelete;
    private bool _exitRequested;

    private ManagedService? _proxyService;
    private readonly AsyncOperation _connectionOperation = new();
    private JsonObject? _runtimeConfig;
    private ManagedService? _tunService;
    private bool _closing;
    private bool _allowClose;
    private int _importantLines;
    private CancellationTokenSource? _latencyCancellation;
    private CancellationTokenSource? _regionCancellation;
    private bool? _lastLatencyHealthy;
    private string _currentRegion = "地区查询中";
    private string _currentLatency = "真连接 --";
    private Color _currentLatencyColor = FluentUi.SlateMuted;
    private bool _updatingRoutingSelector;
    private bool _flushingLogs;
    private Task _trafficSample = Task.CompletedTask;
    private string? _activeTunName;
    private LogSeverity _minimumLogSeverity = LogLevels.ToSeverity(LogLevels.Default);
    private long? _lastAdapterUploadBytes;
    private long? _lastAdapterDownloadBytes;
    private long? _lastXrayUploadBytes;
    private long? _lastXrayDownloadBytes;
    private DateTime? _lastTrafficSampleUtc;
    private double _proxyUploadRate;
    private double _proxyDownloadRate;
    private double _directUploadRate;
    private double _directDownloadRate;
    private DateTime _lastTrafficSaveUtc;
    private ProfileStore? _profiles;

    public bool IsDisposed { get; private set; }

    private string BasePath => AppContext.BaseDirectory;
    private string ConfigDirectory => System.IO.Path.Combine(BasePath, "config");
    private string RuntimeDirectory => System.IO.Path.Combine(BasePath, "runtime");
    private string MqvpnExecutable => System.IO.Path.Combine(BasePath, "runtime", "mqvpn", "mqvpn.exe");
    private string MqvpnConfig => System.IO.Path.Combine(ConfigDirectory, "active-mqvpn.json");
    private string ActiveMqvpnTunName => _activeTunName ??=
        _profiles?.ReadConfig(_profiles.ActiveProfile)["tun_name"]?.GetValue<string>() ?? "mqvpn0";
    private string XrayExecutable => System.IO.Path.Combine(BasePath, "runtime", "xray", "xray.exe");
    private string XrayConfig => System.IO.Path.Combine(ConfigDirectory, "active-xray.json");
    private string CurrentLogLevel => LogLevels.Normalize(_profiles?.State.LogLevel);

    public MainForm()
    {
        FluentUi.ConfigureWindow(this, "MQVPN", 1220, 820, 1000, 680);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        System.Drawing.Icon? trayDrawingIcon = null;
        if (Environment.ProcessPath is { } executablePath)
        {
            try
            {
                trayDrawingIcon = System.Drawing.Icon.ExtractAssociatedIcon(executablePath);
            }
            catch
            {
            }
        }

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = trayDrawingIcon ?? System.Drawing.SystemIcons.Application,
            Text = "MQVPN",
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
        _trayIcon.MouseUp += OnTrayIconMouseUp;

        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _trafficTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        var root = FluentUi.CreateWindowRootGrid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(255) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleBar = BuildTitleBar();
        var dashboard = BuildDashboard();
        var profilesSection = BuildProfilesSection();
        var logsSection = BuildLogs(out _importantLog, out _mqvpnLog, out _xrayLog);
        var footer = BuildFooter();

        Grid.SetRow(titleBar, 0);
        Grid.SetRow(dashboard, 1);
        Grid.SetRow(profilesSection, 2);
        Grid.SetRow(logsSection, 3);
        Grid.SetRow(footer, 4);

        root.Children.Add(titleBar);
        root.Children.Add(dashboard);
        root.Children.Add(profilesSection);
        root.Children.Add(logsSection);
        root.Children.Add(footer);
        Content = root;

        _proxyToggle.ToggleRequested += desired => _ = HandleProxyToggleAsync(desired);
        _tunToggle.ToggleRequested += desired => _ = HandleTunToggleAsync(desired);
        _tunToggle.Enabled = false;
        SetProxyState(false, "已关闭", Muted);
        SetTunState(false, "已关闭", Muted);

        _logTimer.Tick += (_, _) => FlushLogs();
        _logTimer.Start();
        _trafficTimer.Tick += (_, _) => ScheduleTrafficSample();

        Closing += HandleWindowClosing;
        Closed += (_, _) => IsDisposed = true;
        ContentRendered += (_, _) => InitializeApplication();
    }

    private static HwndSource? _dummyTrayHwnd;

    private static IntPtr GetDummyTrayHandle()
    {
        if (_dummyTrayHwnd is null)
        {
            var parameters = new HwndSourceParameters("MQVPN_TrayHost")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0x800000,
            };
            _dummyTrayHwnd = new HwndSource(parameters);
        }
        return _dummyTrayHwnd.Handle;
    }

    private void OnTrayIconMouseUp(object? sender, System.Windows.Forms.MouseEventArgs e)
    {
        if (e.Button == System.Windows.Forms.MouseButtons.Right)
        {
            Dispatcher.BeginInvoke(() =>
            {
                BuildTrayContextMenu();
                _trayContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                User32.SetForegroundWindow(GetDummyTrayHandle());
                _trayContextMenu.IsOpen = true;
            });
        }
    }

    private void BuildTrayContextMenu()
    {
        _trayContextMenu.Items.Clear();
        _trayContextMenu.SetResourceReference(Control.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        _trayContextMenu.SetResourceReference(Control.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        _trayContextMenu.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        _trayContextMenu.BorderThickness = new Thickness(1);
        _trayContextMenu.Padding = new Thickness(4);

        var proxyItem = FluentUi.CreateMenuItem(
            _proxyToggle.IsOn ? "断开代理隧道" : "连接代理隧道",
            SymbolRegular.ShieldGlobe20,
            async (_, _) => await HandleProxyToggleAsync(!_proxyToggle.IsOn));
        _trayContextMenu.Items.Add(proxyItem);

        var tunItem = FluentUi.CreateMenuItem(
            _tunToggle.IsOn ? "关闭系统 TUN" : "启用系统 TUN",
            SymbolRegular.Router20,
            async (_, _) => await HandleTunToggleAsync(!_tunToggle.IsOn));
        tunItem.IsEnabled = _proxyToggle.IsOn && !_proxyToggle.Busy;
        _trayContextMenu.Items.Add(tunItem);

        _trayContextMenu.Items.Add(new Separator());

        if (_profiles is not null && _profiles.State.Profiles.Count > 0)
        {
            var activeProfile = _profiles.ActiveProfile;
            var profilesSubMenu = FluentUi.CreateMenuItem(
                $"切换节点配置 ({activeProfile.Name})",
                SymbolRegular.Server20);

            foreach (var profile in _profiles.State.Profiles)
            {
                var isCurrent = profile.Id == _profiles.State.ActiveProfileId;
                var item = FluentUi.CreateMenuItem(
                    profile.Name,
                    isCurrent ? SymbolRegular.Checkmark20 : null,
                    async (_, _) =>
                    {
                        if (profile.Id == _profiles.State.ActiveProfileId)
                            return;
                        SelectProfileRow(profile.Id);
                        await SetSelectedProfileActiveAsync();
                    },
                    isCheckable: true,
                    isChecked: isCurrent);
                profilesSubMenu.Items.Add(item);
            }

            _trayContextMenu.Items.Add(profilesSubMenu);
            _trayContextMenu.Items.Add(new Separator());
        }

        _trayContextMenu.Items.Add(FluentUi.CreateMenuItem("打开主界面", SymbolRegular.Window20, (_, _) => ShowFromTray()));
        _trayContextMenu.Items.Add(FluentUi.CreateMenuItem("常规设置", SymbolRegular.Settings20, (_, _) => OpenSettings()));
        _trayContextMenu.Items.Add(new Separator());
        _trayContextMenu.Items.Add(FluentUi.CreateMenuItem("退出 MQVPN", SymbolRegular.ArrowExit20, async (_, _) => await ExitApplicationAsync()));
    }

    private static class User32
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);
    }

    internal void ShowFromSecondInstance() => ShowFromTray();

    private void ShowFromTray()
    {
        if (IsDisposed)
            return;
        Show();
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private UIElement BuildTitleBar()
    {
        var trailing = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };

        var settingsBtn = new Wpf.Ui.Controls.Button
        {
            Appearance = ControlAppearance.Transparent,
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(6),
            Cursor = Cursors.Hand,
            ToolTip = "常规设置",
            Icon = FluentUi.Icon(SymbolRegular.Settings20, 15, foreground: FluentUi.BrandSoftBrush),
            Content = "设置",
            Margin = new Thickness(0, 0, 6, 0),
        };
        settingsBtn.Click += (_, _) => OpenSettings();
        trailing.Children.Add(settingsBtn);

        var themeBtn = new Wpf.Ui.Controls.Button
        {
            Appearance = ControlAppearance.Transparent,
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(6),
            Cursor = Cursors.Hand,
            ToolTip = "点击切换色彩外观（跟随系统 → 浅色模式 → 深色模式）",
            Margin = new Thickness(0, 0, 4, 0),
        };

        void UpdateThemeUi()
        {
            var mode = FluentUi.ThemeMode.ToLowerInvariant();
            var icon = mode switch
            {
                "light" => SymbolRegular.WeatherSunny20,
                "dark" => SymbolRegular.WeatherMoon20,
                _ => SymbolRegular.Desktop20,
            };
            var label = mode switch
            {
                "light" => "浅色模式",
                "dark" => "深色模式",
                _ => "跟随系统",
            };
            themeBtn.Icon = FluentUi.Icon(icon, 15, foreground: FluentUi.BrandSoftBrush);
            themeBtn.Content = label;
        }

        void CycleThemePreference()
        {
            var currentMode = FluentUi.ThemeMode.ToLowerInvariant();
            var nextMode = currentMode switch
            {
                "system" => "light",
                "light" => "dark",
                "dark" => "system",
                _ => "light",
            };

            FluentUi.SetThemeMode(nextMode);
            if (_profiles is not null)
            {
                _profiles.State.ThemeMode = nextMode;
                _profiles.Save();
            }
            UpdateThemeUi();
        }

        UpdateThemeUi();
        FluentUi.ThemeChanged += _ => UpdateThemeUi();
        FluentUi.ThemeChanged += OnThemeChanged;

        themeBtn.Click += (_, _) => CycleThemePreference();
        trailing.Children.Add(themeBtn);

        return FluentUi.CreateTitleBar("MQVPN — Multipath QUIC VPN Client", trailing);
    }

    private void OnThemeChanged(ApplicationTheme theme)
    {
        var isDark = theme == ApplicationTheme.Dark;
        if (_proxyToggle.IsOn)
            SetProxyState(true, _proxyStatus.Text, Green);
        else
            SetProxyState(false, _proxyStatus.Text, Muted);

        if (_tunToggle.IsOn)
            SetTunState(true, _tunStatus.Text, Green);
        else
            SetTunState(false, _tunStatus.Text, Muted);

        if (!_proxyToggle.IsOn)
            _footerDot.Fill = FluentUi.MutedBrush;

        _importantLog?.RefreshThemeColors(isDark);
        _mqvpnLog?.RefreshThemeColors(isDark);
        _xrayLog?.RefreshThemeColors(isDark);

        RenderConnectionStatus();
        RefreshProfilesGrid();
    }

    private UIElement BuildDashboard()
    {
        var grid = new Grid { Margin = new Thickness(18, 6, 18, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });

        // Card 1: Brand & Active Profile
        var brandGrid = new Grid();
        brandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        brandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = FluentUi.CreateMqvpnBrandBadge(48);
        badge.Margin = new Thickness(0, 0, 14, 0);
        badge.VerticalAlignment = VerticalAlignment.Center;

        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _activeProfileTitle.Text = "MQVPN";
        _activeProfileTitle.FontSize = 16;
        _activeProfileTitle.FontWeight = FontWeights.Bold;
        _activeProfileTitle.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");

        var protoPill = new Border
        {
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Color.FromArgb(0x28, 0x8B, 0x5C, 0xF6)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x8B, 0x5C, 0xF6)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new System.Windows.Controls.TextBlock
            {
                Text = "MP-QUIC",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = FluentUi.BrandSoftBrush,
            },
        };
        titleRow.Children.Add(_activeProfileTitle);
        titleRow.Children.Add(protoPill);

        _subtitle.Text = "Multipath QUIC VPN";
        _subtitle.FontSize = 12;
        _subtitle.Margin = new Thickness(0, 4, 0, 6);
        _subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _subtitle.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var probeRow = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = Cursors.Hand,
            ToolTip = "点击重新测试真连接延迟",
        };
        probeRow.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        probeRow.MouseLeftButtonUp += async (_, _) => await RetestLatencyAsync();

        var probeInner = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        probeInner.Children.Add(FluentUi.Icon(SymbolRegular.Globe20, 13, foreground: FluentUi.BrandSoftBrush));
        _headerLatencyBadge.Text = "地区查询中 · 真连接 --";
        _headerLatencyBadge.FontSize = 11.5;
        _headerLatencyBadge.Margin = new Thickness(6, 0, 6, 0);
        _headerLatencyBadge.Foreground = FluentUi.MutedBrush;
        probeInner.Children.Add(_headerLatencyBadge);
        probeInner.Children.Add(FluentUi.Icon(SymbolRegular.ArrowSync20, 12, foreground: FluentUi.MutedBrush));
        probeRow.Child = probeInner;

        infoStack.Children.Add(titleRow);
        infoStack.Children.Add(_subtitle);
        infoStack.Children.Add(probeRow);

        Grid.SetColumn(badge, 0);
        Grid.SetColumn(infoStack, 1);
        brandGrid.Children.Add(badge);
        brandGrid.Children.Add(infoStack);

        var brandCard = FluentUi.CreateCardBorder(brandGrid, new Thickness(16, 12, 16, 12), new Thickness(0, 0, 10, 0));

        // Card 2: Real-Time Speed & Traffic
        var speedStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var speedHeader = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        speedHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        speedHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var speedTitlePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        speedTitlePanel.Children.Add(FluentUi.Icon(SymbolRegular.TopSpeed20, 15, foreground: FluentUi.BrandSoftBrush));
        var speedTitle = FluentUi.PrimaryText("实时吞吐监控", 12.5, FontWeights.SemiBold);
        speedTitle.Margin = new Thickness(6, 0, 0, 0);
        speedTitlePanel.Children.Add(speedTitle);

        _todayTrafficText.Text = "今日 ↑ 0 B  ↓ 0 B";
        _todayTrafficText.FontSize = 11.5;
        _todayTrafficText.FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
        _todayTrafficText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        Grid.SetColumn(speedTitlePanel, 0);
        Grid.SetColumn(_todayTrafficText, 1);
        speedHeader.Children.Add(speedTitlePanel);
        speedHeader.Children.Add(_todayTrafficText);

        var proxySpeedRow = BuildSpeedRow("Proxy 隧道", _proxySpeedText, FluentUi.EmeraldGreen);
        proxySpeedRow.Margin = new Thickness(0, 2, 0, 4);
        var directSpeedRow = BuildSpeedRow("Direct 直连", _directSpeedText, FluentUi.BrandVioletSoft);

        speedStack.Children.Add(speedHeader);
        speedStack.Children.Add(proxySpeedRow);
        speedStack.Children.Add(directSpeedRow);

        var speedCard = FluentUi.CreateCardBorder(speedStack, new Thickness(16, 12, 16, 12), new Thickness(0, 0, 10, 0));

        // Card 3: Dual Service Toggles
        var servicesGrid = new Grid();
        servicesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        servicesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var proxyServiceCard = BuildServiceCard(
            "代理隧道",
            "MQVPN 核心",
            SymbolRegular.ShieldGlobe24,
            _proxyDot,
            _proxyStatus,
            _proxyToggle,
            new Thickness(0, 0, 5, 0));
        var tunServiceCard = BuildServiceCard(
            "系统 TUN",
            "Xray 分流",
            SymbolRegular.Router24,
            _tunDot,
            _tunStatus,
            _tunToggle,
            new Thickness(5, 0, 0, 0));

        Grid.SetColumn(proxyServiceCard, 0);
        Grid.SetColumn(tunServiceCard, 1);
        servicesGrid.Children.Add(proxyServiceCard);
        servicesGrid.Children.Add(tunServiceCard);

        Grid.SetColumn(brandCard, 0);
        Grid.SetColumn(speedCard, 1);
        Grid.SetColumn(servicesGrid, 2);
        grid.Children.Add(brandCard);
        grid.Children.Add(speedCard);
        grid.Children.Add(servicesGrid);

        return grid;
    }

    private static Border BuildSpeedRow(string label, System.Windows.Controls.TextBlock valueBlock, Color indicatorColor)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = new SolidColorBrush(indicatorColor),
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        left.Children.Add(FluentUi.SecondaryText(label, 12));

        valueBlock.Text = "↑ 0 B/s   ↓ 0 B/s";
        valueBlock.FontSize = 12;
        valueBlock.FontWeight = FontWeights.SemiBold;
        valueBlock.FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
        valueBlock.HorizontalAlignment = HorizontalAlignment.Right;
        valueBlock.VerticalAlignment = VerticalAlignment.Center;
        valueBlock.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");

        Grid.SetColumn(left, 0);
        Grid.SetColumn(valueBlock, 1);
        row.Children.Add(left);
        row.Children.Add(valueBlock);

        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 5, 10, 5),
            Child = row,
        };
        border.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        return border;
    }

    private static Border BuildServiceCard(
        string title,
        string subtitle,
        SymbolRegular icon,
        Ellipse statusDot,
        System.Windows.Controls.TextBlock statusLabel,
        ToggleSwitch toggle,
        Thickness margin)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var topRow = new Grid();
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconBox = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x24, 0x8B, 0x5C, 0xF6)),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = FluentUi.Icon(icon, 18, foreground: FluentUi.BrandSoftBrush),
        };

        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(FluentUi.PrimaryText(title, 13.5, FontWeights.Bold));
        titleStack.Children.Add(FluentUi.SecondaryText(subtitle, 11));

        Grid.SetColumn(iconBox, 0);
        Grid.SetColumn(titleStack, 1);
        Grid.SetColumn(toggle, 2);
        topRow.Children.Add(iconBox);
        topRow.Children.Add(titleStack);
        topRow.Children.Add(toggle);

        var statusPill = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 10, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        statusPill.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");

        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        statusDot.VerticalAlignment = VerticalAlignment.Center;
        statusLabel.FontSize = 12;
        statusLabel.FontWeight = FontWeights.SemiBold;
        statusLabel.VerticalAlignment = VerticalAlignment.Center;
        statusRow.Children.Add(statusDot);
        statusRow.Children.Add(statusLabel);
        statusPill.Child = statusRow;

        Grid.SetRow(topRow, 0);
        Grid.SetRow(statusPill, 1);
        grid.Children.Add(topRow);
        grid.Children.Add(statusPill);

        var card = FluentUi.CreateCardBorder(grid, new Thickness(14, 12, 14, 12), margin);
        card.Cursor = Cursors.Hand;
        card.MouseLeftButtonUp += (sender, e) =>
        {
            if (!Equals(e.OriginalSource, toggle))
                toggle.RequestToggle();
        };
        return card;
    }

    private UIElement BuildProfilesSection()
    {
        var cardGrid = new Grid();
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var toolbar = new Grid { Margin = new Thickness(4, 0, 4, 10) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftBar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        leftBar.Children.Add(FluentUi.Icon(SymbolRegular.ServerMultiple20, 17, foreground: FluentUi.BrandSoftBrush));
        var sectionTitle = FluentUi.PrimaryText("节点配置列表", 13.5, FontWeights.SemiBold);
        sectionTitle.Margin = new Thickness(7, 0, 14, 0);
        leftBar.Children.Add(sectionTitle);

        var btnAdd = FluentUi.ActionButton("添加配置", SymbolRegular.Add20, ControlAppearance.Primary, (_, _) => AddProfile());
        btnAdd.Margin = new Thickness(0, 0, 8, 0);
        var btnTest = FluentUi.ActionButton("测试真连接", SymbolRegular.Flash20, ControlAppearance.Secondary, async (_, _) => await TestSelectedProfileAsync());
        btnTest.Margin = new Thickness(0, 0, 8, 0);
        var btnRouting = FluentUi.ActionButton("路由设置", SymbolRegular.Router20, ControlAppearance.Secondary, (_, _) => EditRoutingSettings());
        btnRouting.Margin = new Thickness(0, 0, 8, 0);
        var btnUpdate = FluentUi.ActionButton("检查更新", SymbolRegular.CloudArrowDown20, ControlAppearance.Secondary, (_, _) => OpenKernelUpdate());
        btnUpdate.Margin = new Thickness(0, 0, 8, 0);

        leftBar.Children.Add(btnAdd);
        leftBar.Children.Add(btnTest);
        leftBar.Children.Add(btnRouting);
        leftBar.Children.Add(btnUpdate);

        var rightBar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var btnExit = FluentUi.ActionButton("退出", SymbolRegular.ArrowExit20, ControlAppearance.Secondary, async (_, _) => await ExitApplicationAsync());
        rightBar.Children.Add(btnExit);

        Grid.SetColumn(leftBar, 0);
        Grid.SetColumn(rightBar, 1);
        toolbar.Children.Add(leftBar);
        toolbar.Children.Add(rightBar);

        ConfigureProfilesGrid();

        Grid.SetRow(toolbar, 0);
        Grid.SetRow(_profilesGrid, 1);
        cardGrid.Children.Add(toolbar);
        cardGrid.Children.Add(_profilesGrid);

        return FluentUi.CreateCardBorder(cardGrid, new Thickness(14, 12, 14, 12), new Thickness(18, 0, 18, 10));
    }

    private void ConfigureProfilesGrid()
    {
        FluentUi.StyleDataGrid(_profilesGrid);
        _profilesGrid.AutoGenerateColumns = false;
        _profilesGrid.CanUserAddRows = false;
        _profilesGrid.CanUserDeleteRows = false;
        _profilesGrid.CanUserResizeRows = false;
        _profilesGrid.IsReadOnly = true;
        _profilesGrid.SelectionMode = DataGridSelectionMode.Single;
        _profilesGrid.SelectionUnit = DataGridSelectionUnit.FullRow;
        _profilesGrid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _profilesGrid.RowHeight = 44;
        _profilesGrid.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _profilesGrid.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _profilesGrid.GridLinesVisibility = DataGridGridLinesVisibility.None;
        _profilesGrid.HorizontalGridLinesBrush = Brushes.Transparent;
        _profilesGrid.VerticalGridLinesBrush = Brushes.Transparent;
        _profilesGrid.ItemsSource = _profileRows;

        _profilesGrid.Columns.Clear();
        _profilesGrid.Columns.Add(CreateStatusColumn());
        _profilesGrid.Columns.Add(CreateReadOnlyColumn("配置名称", nameof(ProfileRowViewModel.Name), new DataGridLength(1.4, DataGridLengthUnitType.Star), fontWeight: FontWeights.SemiBold));
        _profilesGrid.Columns.Add(CreateReadOnlyColumn("服务器地址", nameof(ProfileRowViewModel.Server), new DataGridLength(1.3, DataGridLengthUnitType.Star), fontFamily: new FontFamily("Cascadia Mono, Consolas, Segoe UI"), foregroundResource: "TextFillColorSecondaryBrush"));
        _profilesGrid.Columns.Add(CreateReadOnlyColumn("路由策略", nameof(ProfileRowViewModel.PolicyName), new DataGridLength(1.2, DataGridLengthUnitType.Star), foregroundResource: "TextFillColorSecondaryBrush"));
        _profilesGrid.Columns.Add(CreateLatencyColumn());
        _profilesGrid.Columns.Add(CreateReadOnlyColumn("流量统计（今日 / 累计）", nameof(ProfileRowViewModel.TrafficText), new DataGridLength(1.6, DataGridLengthUnitType.Star), fontFamily: new FontFamily("Cascadia Mono, Consolas, Segoe UI"), foregroundResource: "TextFillColorSecondaryBrush"));

        _profilesGrid.MouseDoubleClick += (_, _) =>
        {
            if (SelectedProfile is not null)
            {
                if (_profiles?.State.DoubleClickToActivate == true)
                    _ = SetSelectedProfileActiveAsync();
                else
                    EditSelectedProfile();
            }
        };

        _profilesGrid.PreviewMouseRightButtonDown += OnProfilesGridPreviewMouseRightButtonDown;

        _profileContextMenu.SetResourceReference(Control.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        _profileContextMenu.SetResourceReference(Control.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        _profileContextMenu.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");

        _menuItemTest = FluentUi.CreateMenuItem("测试真连接", SymbolRegular.Flash20, async (_, _) => await TestSelectedProfileAsync());
        _menuItemActivate = FluentUi.CreateMenuItem("设为活动", SymbolRegular.CheckmarkCircle20, async (_, _) => await SetSelectedProfileActiveAsync());
        _menuItemClone = FluentUi.CreateMenuItem("克隆", SymbolRegular.Copy20, (_, _) => CloneSelectedProfile());
        _menuItemEdit = FluentUi.CreateMenuItem("编辑", SymbolRegular.Edit20, (_, _) => EditSelectedProfile());
        _menuItemMoveUp = FluentUi.CreateMenuItem("上移", SymbolRegular.ArrowUp20, (_, _) => MoveSelectedProfileUp());
        _menuItemMoveDown = FluentUi.CreateMenuItem("下移", SymbolRegular.ArrowDown20, (_, _) => MoveSelectedProfileDown());
        _menuItemDelete = FluentUi.CreateMenuItem("删除", SymbolRegular.Delete20, async (_, _) => await DeleteSelectedProfileAsync());

        _profileContextMenu.Items.Add(_menuItemTest);
        _profileContextMenu.Items.Add(_menuItemActivate);
        _profileContextMenu.Items.Add(_menuItemClone);
        _profileContextMenu.Items.Add(FluentUi.CreateMenuItem("导入 JSON 为新节点", SymbolRegular.DocumentAdd20, (_, _) => ImportProfile()));
        _profileContextMenu.Items.Add(_menuItemEdit);
        _profileContextMenu.Items.Add(_menuItemMoveUp);
        _profileContextMenu.Items.Add(_menuItemMoveDown);
        _profileContextMenu.Items.Add(new Separator());
        _profileContextMenu.Items.Add(_menuItemDelete);

        _profilesGrid.ContextMenu = _profileContextMenu;
        _profilesGrid.ContextMenuOpening += (sender, e) =>
        {
            e.Handled = true;
        };
    }

    private static DataGridTemplateColumn CreateStatusColumn()
    {
        var col = new DataGridTemplateColumn
        {
            Header = "状态",
            Width = new DataGridLength(92),
            CanUserResize = false,
            IsReadOnly = true,
        };

        var template = new DataTemplate();
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        borderFactory.SetValue(Border.PaddingProperty, new Thickness(8, 3, 9, 3));
        borderFactory.SetValue(Border.MarginProperty, new Thickness(10, 0, 8, 0));
        borderFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        borderFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        borderFactory.SetBinding(Border.BackgroundProperty, new Binding(nameof(ProfileRowViewModel.StatusBadgeBackground)));
        borderFactory.SetBinding(Border.BorderBrushProperty, new Binding(nameof(ProfileRowViewModel.StatusBadgeBorder)));

        var stackFactory = new FrameworkElementFactory(typeof(StackPanel));
        stackFactory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        stackFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var dotFactory = new FrameworkElementFactory(typeof(Ellipse));
        dotFactory.SetValue(FrameworkElement.WidthProperty, 6.0);
        dotFactory.SetValue(FrameworkElement.HeightProperty, 6.0);
        dotFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 5, 0));
        dotFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        dotFactory.SetBinding(Shape.FillProperty, new Binding(nameof(ProfileRowViewModel.StatusBadgeForeground)));

        var textFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
        textFactory.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 11.5);
        textFactory.SetValue(System.Windows.Controls.TextBlock.FontWeightProperty, FontWeights.SemiBold);
        textFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        textFactory.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new Binding(nameof(ProfileRowViewModel.StatusLabel)));
        textFactory.SetBinding(System.Windows.Controls.TextBlock.ForegroundProperty, new Binding(nameof(ProfileRowViewModel.StatusBadgeForeground)));

        stackFactory.AppendChild(dotFactory);
        stackFactory.AppendChild(textFactory);
        borderFactory.AppendChild(stackFactory);
        template.VisualTree = borderFactory;
        col.CellTemplate = template;
        return col;
    }

    private static DataGridTemplateColumn CreateLatencyColumn()
    {
        var col = new DataGridTemplateColumn
        {
            Header = "真连接",
            Width = new DataGridLength(92),
            CanUserResize = false,
            IsReadOnly = true,
        };

        var template = new DataTemplate();
        var textFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
        textFactory.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 12.0);
        textFactory.SetValue(System.Windows.Controls.TextBlock.FontWeightProperty, FontWeights.SemiBold);
        textFactory.SetValue(System.Windows.Controls.TextBlock.FontFamilyProperty, new FontFamily("Cascadia Mono, Consolas, Segoe UI"));
        textFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 10, 0));
        textFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        textFactory.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new Binding(nameof(ProfileRowViewModel.LatencyText)));
        textFactory.SetBinding(System.Windows.Controls.TextBlock.ForegroundProperty, new Binding(nameof(ProfileRowViewModel.LatencyBrush)));

        template.VisualTree = textFactory;
        col.CellTemplate = template;
        return col;
    }

    private static DataGridTemplateColumn CreateReadOnlyColumn(
        string header,
        string bindingPath,
        DataGridLength width,
        FontWeight? fontWeight = null,
        FontFamily? fontFamily = null,
        string foregroundResource = "TextFillColorPrimaryBrush")
    {
        var col = new DataGridTemplateColumn
        {
            Header = header,
            Width = width,
            IsReadOnly = true,
        };

        var template = new DataTemplate();
        var factory = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
        factory.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new Binding(bindingPath));
        factory.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 10, 0));
        factory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        factory.SetValue(System.Windows.Controls.TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        if (fontWeight is not null)
            factory.SetValue(System.Windows.Controls.TextBlock.FontWeightProperty, fontWeight.Value);
        if (fontFamily is not null)
            factory.SetValue(System.Windows.Controls.TextBlock.FontFamilyProperty, fontFamily);
        factory.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, foregroundResource);
        template.VisualTree = factory;
        col.CellTemplate = template;
        return col;
    }

    private void OnProfilesGridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not DataGridRow)
        {
            dep = VisualTreeHelper.GetParent(dep);
        }

        if (dep is DataGridRow row && row.Item is ProfileRowViewModel vm)
        {
            _profilesGrid.SelectedItem = vm;
            row.Focus();

            UpdateProfileContextMenuState(vm.Profile);
            _profileContextMenu.PlacementTarget = row;
            _profileContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            _profileContextMenu.IsOpen = true;
            e.Handled = true;
        }
        else
        {
            e.Handled = true;
        }
    }

    private void UpdateProfileContextMenuState(ProxyProfile profile)
    {
        var index = _profiles?.State.Profiles.IndexOf(profile) ?? -1;
        var count = _profiles?.State.Profiles.Count ?? 0;
        var isActive = profile.Id == _profiles?.State.ActiveProfileId;

        if (_menuItemTest is not null) _menuItemTest.IsEnabled = true;
        if (_menuItemActivate is not null) _menuItemActivate.IsEnabled = !isActive;
        if (_menuItemClone is not null) _menuItemClone.IsEnabled = true;
        if (_menuItemEdit is not null) _menuItemEdit.IsEnabled = true;
        if (_menuItemMoveUp is not null) _menuItemMoveUp.IsEnabled = index > 0;
        if (_menuItemMoveDown is not null) _menuItemMoveDown.IsEnabled = index >= 0 && index < count - 1;
        if (_menuItemDelete is not null) _menuItemDelete.IsEnabled = count > 1;
    }

    private void MoveSelectedProfileUp()
    {
        if (_profiles is null || SelectedProfile is not { } profile)
            return;
        if (_profiles.MoveUp(profile))
        {
            RefreshProfilesGrid();
            SelectProfileRow(profile.Id);
        }
    }

    private void MoveSelectedProfileDown()
    {
        if (_profiles is null || SelectedProfile is not { } profile)
            return;
        if (_profiles.MoveDown(profile))
        {
            RefreshProfilesGrid();
            SelectProfileRow(profile.Id);
        }
    }

    private UIElement BuildLogs(out LogView importantLog, out LogView mqvpnLog, out LogView xrayLog)
    {
        var grid = new Grid { Margin = new Thickness(18, 0, 18, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.12, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var importantPanel = BuildLogCard("重要日志（混合显示）", SymbolRegular.Info20, true, 2000, new Thickness(0, 0, 6, 0), out importantLog);
        var mqvpnPanel = BuildLogCard("MQVPN 日志", SymbolRegular.ShieldGlobe20, false, 6000, new Thickness(4, 0, 4, 0), out mqvpnLog);
        var xrayPanel = BuildLogCard("Xray 日志", SymbolRegular.Router20, false, 6000, new Thickness(6, 0, 0, 0), out xrayLog);

        Grid.SetColumn(importantPanel, 0);
        Grid.SetColumn(mqvpnPanel, 1);
        Grid.SetColumn(xrayPanel, 2);
        grid.Children.Add(importantPanel);
        grid.Children.Add(mqvpnPanel);
        grid.Children.Add(xrayPanel);

        return grid;
    }

    private Border BuildLogCard(string title, SymbolRegular icon, bool important, int maximumLines, Thickness margin, out LogView log)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid { Margin = new Thickness(2, 0, 2, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var caption = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        caption.Children.Add(FluentUi.Icon(icon, 16, foreground: FluentUi.BrandSoftBrush));
        var titleBlock = FluentUi.PrimaryText(title, 13, FontWeights.SemiBold);
        titleBlock.Margin = new Thickness(7, 0, 0, 0);
        caption.Children.Add(titleBlock);

        if (important)
        {
            _importantCount.Text = "0";
            _importantCount.FontSize = 11;
            _importantCount.FontWeight = FontWeights.SemiBold;
            _importantCount.Foreground = FluentUi.BrandSoftBrush;

            var countBadge = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(0x28, 0x8B, 0x5C, 0xF6)),
                Padding = new Thickness(7, 1, 7, 1),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = _importantCount,
            };
            caption.Children.Add(countBadge);
        }

        var box = new System.Windows.Controls.RichTextBox
        {
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0),
        };
        box.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");

        var view = new LogView(box, maximumLines);
        var clearBtn = FluentUi.ActionButton(
            "清空",
            SymbolRegular.Broom20,
            ControlAppearance.Secondary,
            (_, _) =>
            {
                view.Clear();
                if (important)
                {
                    _importantLines = 0;
                    UpdateImportantCount();
                }
            },
            new Thickness(9, 3, 9, 3));
        clearBtn.FontSize = 11.5;

        Grid.SetColumn(caption, 0);
        Grid.SetColumn(clearBtn, 1);
        header.Children.Add(caption);
        header.Children.Add(clearBtn);

        var boxBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = box,
        };
        boxBorder.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        boxBorder.SetResourceReference(Border.BorderBrushProperty, "ControlElevationBorderBrush");

        Grid.SetRow(header, 0);
        Grid.SetRow(boxBorder, 1);
        grid.Children.Add(header);
        grid.Children.Add(boxBorder);

        log = view;
        return FluentUi.CreateCardBorder(grid, new Thickness(12, 10, 12, 12), margin);
    }

    private UIElement BuildFooter()
    {
        var footer = new Border
        {
            Padding = new Thickness(18, 10, 18, 10),
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        footer.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorSecondaryBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var statusPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _footerDot.Fill = FluentUi.MutedBrush;
        _footerDot.VerticalAlignment = VerticalAlignment.Center;
        _footerStatus.Text = "MQVPN 就绪";
        _footerStatus.FontSize = 12.5;
        _footerStatus.FontWeight = FontWeights.Medium;
        _footerStatus.VerticalAlignment = VerticalAlignment.Center;
        _footerStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        _footerStatus.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        statusPanel.Children.Add(_footerDot);
        statusPanel.Children.Add(_footerStatus);

        var routePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        routePanel.Children.Add(FluentUi.Icon(SymbolRegular.Router20, 15, foreground: FluentUi.BrandSoftBrush));
        var routeLabel = FluentUi.SecondaryText("当前路由分流策略", 12);
        routeLabel.Margin = new Thickness(6, 0, 10, 0);
        routePanel.Children.Add(routeLabel);

        _routingSelector.SelectionChanged += async (_, _) => await ChangeActiveRoutingAsync();
        routePanel.Children.Add(_routingSelector);

        var latencyBox = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 10, 4),
            Cursor = Cursors.Hand,
            ToolTip = "点击重新测试真连接延迟",
        };
        latencyBox.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        latencyBox.MouseLeftButtonUp += async (_, _) => await RetestLatencyAsync();

        _latencyStatus.Text = "地区查询中 · 真连接 --";
        _latencyStatus.FontSize = 12;
        _latencyStatus.FontWeight = FontWeights.Medium;
        _latencyStatus.Foreground = FluentUi.MutedBrush;
        _latencyStatus.VerticalAlignment = VerticalAlignment.Center;
        latencyBox.Child = _latencyStatus;

        Grid.SetColumn(statusPanel, 0);
        Grid.SetColumn(routePanel, 1);
        Grid.SetColumn(latencyBox, 2);
        grid.Children.Add(statusPanel);
        grid.Children.Add(routePanel);
        grid.Children.Add(latencyBox);

        footer.Child = grid;
        return footer;
    }

    private void InitializeApplication()
    {
        var required = new[]
        {
            MqvpnExecutable,
            System.IO.Path.Combine(ConfigDirectory, "client-windows.json"),
            XrayExecutable,
        };
        var missing = required.Where(path => !System.IO.File.Exists(path)).ToArray();
        if (missing.Length > 0)
        {
            foreach (var path in missing)
                QueueLog("GUI", $"[error] Missing file: {path}");
            _proxyToggle.Enabled = false;
            _footerStatus.Text = "运行时文件不完整";
            _footerDot.Fill = FluentUi.RedBrush;
            return;
        }

        try
        {
            _profiles = ProfileStore.Load(ConfigDirectory);
            foreach (var warning in _profiles.LoadWarnings)
                QueueLog("GUI", $"[error] {warning}");
            FluentUi.SetThemeMode(_profiles.State.ThemeMode);
            foreach (var profile in _profiles.State.Profiles)
                ResetDailyTrafficIfNeeded(profile);
            _minimumLogSeverity = LogLevels.ToSeverity(_profiles.State.LogLevel);
            _profiles.Save();
            RefreshProfilesGrid();
            RefreshRoutingSelector();
            UpdateActiveProfileCaption();
            try { GenerateActiveRoutingConfig(); }
            catch (Exception ex) { QueueLog("GUI", $"[error] 当前节点无法生成路由: {ex.Message}"); }
            RenderConnectionStatus();
            QueueLog("GUI", "Profile metadata loaded; configuration warnings are listed above");
            if (_profiles.State.StartMinimized)
            {
                WindowState = WindowState.Minimized;
                Hide();
                ShowInTaskbar = false;
            }
            if (_profiles.ActiveProfile.AutoConnect)
                _ = Dispatcher.BeginInvoke(async () => await HandleProxyToggleAsync(true));
        }
        catch (Exception ex)
        {
            QueueLog("GUI", $"[error] 配置初始化失败: {ex.Message}");
            _proxyToggle.Enabled = false;
        }
    }

    private ProxyProfile? SelectedProfile =>
        (_profilesGrid.SelectedItem as ProfileRowViewModel)?.Profile;

    private void RefreshProfilesGrid()
    {
        if (_profiles is null)
            return;
        var selectedId = SelectedProfile?.Id ?? _profiles.State.ActiveProfileId;
        _profileRows.Clear();
        ProfileRowViewModel? toSelect = null;

        foreach (var profile in _profiles.State.Profiles)
        {
            var server = string.IsNullOrEmpty(profile.ServerAddress) ? string.Empty : $"{profile.ServerAddress}:{profile.ServerPort}";
            var policy = _profiles.GetPolicy(profile.RoutingPolicyId);
            var latency = profile.LastLatencyMs is { } value ? $"{value} ms" : "--";
            var isActive = profile.Id == _profiles.State.ActiveProfileId;
            var rowVm = new ProfileRowViewModel
            {
                Profile = profile,
                IsActive = isActive,
                Name = profile.AutoConnect ? $"{profile.Name} [自启]" : profile.Name,
                Server = server,
                PolicyName = policy.Name,
                LatencyMs = profile.LastLatencyMs,
                LatencyText = latency,
                TrafficText = FormatProfileTraffic(profile),
            };
            _profileRows.Add(rowVm);
            if (profile.Id == selectedId)
                toSelect = rowVm;
        }

        _profilesGrid.SelectedItem = toSelect ?? _profileRows.FirstOrDefault();
    }

    private void RefreshRoutingSelector()
    {
        if (_profiles is null)
            return;
        _updatingRoutingSelector = true;
        _routingSelector.ItemsSource = null;
        _routingSelector.DisplayMemberPath = nameof(RoutingPolicy.Name);
        _routingSelector.SelectedValuePath = nameof(RoutingPolicy.Id);
        _routingSelector.ItemsSource = _profiles.Routing.Policies.ToList();
        _routingSelector.SelectedValue = _profiles.ActiveProfile.RoutingPolicyId;
        _updatingRoutingSelector = false;
    }

    private void UpdateActiveProfileCaption()
    {
        if (_profiles is null)
            return;
        _activeProfileTitle.Text = _profiles.ActiveProfile.Name;
        JsonObject config;
        try { config = _profiles.ReadConfig(_profiles.ActiveProfile); }
        catch (Exception ex)
        {
            _subtitle.Text = $"当前节点配置读取失败：{ex.Message}";
            return;
        }
        var server = config["server_addr"]?.GetValue<string>() ?? string.Empty;
        var adapter = (config["paths"] as JsonArray)?.FirstOrDefault()?.GetValue<string>() ?? "默认路由";
        _subtitle.Text = $"服务器: {server}   ·   绑定网卡: {adapter}";
        StartRegionLookup(server);
    }

    private void GenerateActiveRoutingConfig()
    {
        if (_profiles is null)
            throw new InvalidOperationException("配置列表尚未初始化");
        _profiles.GenerateXrayConfig(_profiles.ActiveProfile, XrayConfig,
            _proxyService?.IsRunning == true ? _runtimeConfig : null);
    }

    private void ImportProfile()
    {
        if (_profiles is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "导入 MQVPN 客户端 JSON", Filter = "MQVPN JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var config = ProfileStore.ReadConfigForImport(dialog.FileName);
            var defaultPath = config["paths"] is not JsonArray { Count: > 0 };
            using var editor = new ProfileEditorForm("导入配置", System.IO.Path.GetFileNameWithoutExtension(dialog.FileName), config,
                _profiles.Routing.Policies, _profiles.ActiveProfile.RoutingPolicyId, useDefaultPath: defaultPath);
            if (!editor.ShowModal(this)) return;
            var profile = _profiles.Add(editor.ProfileName, editor.ResultConfig, editor.RoutingPolicyId);
            profile.AutoConnect = editor.AutoConnect;
            profile.UseDefaultPath = editor.UseDefaultPath;
            _profiles.Save();
            RefreshProfilesGrid();
            SelectProfileRow(profile.Id);
        }
        catch (Exception ex) { FluentUi.ShowAlert(this, ex.Message, "导入配置失败", isError: true); }
    }

    private void AddProfile()
    {
        if (_profiles is null)
            return;
        using var editor = new ProfileEditorForm(
            "添加配置",
            "MQVPN 配置",
            ProfileStore.CreateDefaultMqvpnConfig(),
            _profiles.Routing.Policies,
            _profiles.ActiveProfile.RoutingPolicyId,
            false,
            true);
        if (!editor.ShowModal(this))
            return;
        var profile = _profiles.Add(editor.ProfileName, editor.ResultConfig, editor.RoutingPolicyId);
        profile.AutoConnect = editor.AutoConnect;
        profile.UseDefaultPath = editor.UseDefaultPath;
        _profiles.Save();
        RefreshProfilesGrid();
        SelectProfileRow(profile.Id);
    }

    private async void EditSelectedProfile()
    {
        if (_profiles is null || SelectedProfile is not { } profile)
            return;
        using var editor = new ProfileEditorForm(
            "编辑配置",
            profile.Name,
            _profiles.ReadConfig(profile),
            _profiles.Routing.Policies,
            profile.RoutingPolicyId,
            profile.AutoConnect,
            profile.UseDefaultPath);
        if (!editor.ShowModal(this))
            return;

        if (profile.Id == _profiles.State.ActiveProfileId)
            await StopProxyAsync();
        profile.Name = editor.ProfileName;
        profile.RoutingPolicyId = editor.RoutingPolicyId;
        profile.AutoConnect = editor.AutoConnect;
        profile.UseDefaultPath = editor.UseDefaultPath;
        _profiles.SaveConfig(profile, editor.ResultConfig);
        _profiles.Save();
        _activeTunName = null;
        if (profile.Id == _profiles.State.ActiveProfileId)
        {
            GenerateActiveRoutingConfig();
            RefreshRoutingSelector();
            UpdateActiveProfileCaption();
        }
        RefreshProfilesGrid();
        SelectProfileRow(profile.Id);
    }

    private void CloneSelectedProfile()
    {
        if (_profiles is null || SelectedProfile is not { } profile)
            return;
        var clone = _profiles.Clone(profile);
        RefreshProfilesGrid();
        SelectProfileRow(clone.Id);
    }

    private async Task SetSelectedProfileActiveAsync()
    {
        if (_profiles is null || SelectedProfile is not { } profile ||
            profile.Id == _profiles.State.ActiveProfileId)
            return;
        await StopProxyAsync();
        _profiles.State.ActiveProfileId = profile.Id;
        _profiles.Save();
        _activeTunName = null;
        GenerateActiveRoutingConfig();
        RefreshProfilesGrid();
        RefreshRoutingSelector();
        UpdateActiveProfileCaption();
        RenderConnectionStatus();
        QueueLog("GUI", $"Active profile changed to {profile.Name}");
    }

    private async Task DeleteSelectedProfileAsync()
    {
        if (_profiles is null || SelectedProfile is not { } profile)
            return;
        if (!FluentUi.Confirm(this, $"确认删除配置“{profile.Name}”？", "确认删除"))
            return;
        if (profile.Id == _profiles.State.ActiveProfileId)
            await StopProxyAsync();
        try
        {
            _profiles.Delete(profile);
            _activeTunName = null;
            GenerateActiveRoutingConfig();
            RefreshProfilesGrid();
            RefreshRoutingSelector();
            UpdateActiveProfileCaption();
            RenderConnectionStatus();
        }
        catch (Exception ex)
        {
            FluentUi.ShowAlert(this, ex.Message, "无法删除", isError: true);
        }
    }

    private async Task TestSelectedProfileAsync()
    {
        if (_profiles is null || SelectedProfile is not { } profile)
            return;
        var rowVm = _profileRows.FirstOrDefault(r => r.Profile.Id == profile.Id);
        try
        {
            if (rowVm is not null)
                rowVm.LatencyText = "测试中...";
            _footerStatus.Text = $"正在测试 {profile.Name}";
            int latency;
            if (profile.Id == _profiles.State.ActiveProfileId &&
                _proxyService?.IsRunning == true && _proxyToggle.IsOn)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                latency = Math.Max(1, (int)Math.Round(
                    (await MeasureLatencySerializedAsync(timeout.Token)).TotalMilliseconds));
            }
            else
            {
                latency = await ProfileLatencyTester.TestAsync(
                    MqvpnExecutable,
                    _profiles.GetRuntimeConfig(profile),
                    CancellationToken.None);
            }
            profile.LastLatencyMs = latency;
            profile.LastTestUtc = DateTime.UtcNow;
            _profiles.Save();
            QueueLog("TEST", $"{profile.Name} real connection: {latency} ms");
        }
        catch (Exception ex)
        {
            QueueLog("TEST", $"[error] {profile.Name} 测试失败: {ex.Message}");
            FluentUi.ShowAlert(this, ex.Message, "真连接测试失败", isError: true);
        }
        finally
        {
            RefreshProfilesGrid();
            SelectProfileRow(profile.Id);
            _footerStatus.Text = _proxyToggle.IsOn ? "代理已连接" : "MQVPN 就绪";
        }
    }

    private void EditRoutingSettings()
    {
        OpenSettings(2);
    }

    private void OpenKernelUpdate()
    {
        if (_profiles is null)
            return;
        var store = _profiles;
        using var update = new KernelUpdateForm(
            RuntimeDirectory,
            new KernelUpdatePreferences(
                store.State.GuiUpdateSource,
                store.State.MqvpnUpdateSource,
                store.State.XrayUpdateSource,
                store.State.GeoDataUpdateSource,
                store.State.GuiIncludePreRelease,
                store.State.MqvpnIncludePreRelease,
                store.State.XrayIncludePreRelease),
            StopProxyAsync,
            preferences =>
            {
                store.State.GuiUpdateSource = preferences.GuiSource;
                store.State.MqvpnUpdateSource = preferences.MqvpnSource;
                store.State.XrayUpdateSource = preferences.XraySource;
                store.State.GeoDataUpdateSource = preferences.GeoDataSource;
                store.State.GuiIncludePreRelease = preferences.GuiIncludePreRelease;
                store.State.MqvpnIncludePreRelease = preferences.MqvpnIncludePreRelease;
                store.State.XrayIncludePreRelease = preferences.XrayIncludePreRelease;
                store.Save();
            });
        update.ShowModal(this);
    }

    private void OpenSettings(int initialTabIndex = 0)
    {
        if (_profiles is null)
            return;

        using var settings = new SettingsForm(_profiles, initialTabIndex, updatedStore =>
        {
            _activeTunName = null;
            _profiles.Save();
            GenerateActiveRoutingConfig();
            RefreshProfilesGrid();
            RefreshRoutingSelector();
            _ = Task.Run(async () => await Dispatcher.InvokeAsync(async () => await ApplyLogLevelAsync(_profiles.State.LogLevel)));
            QueueLog("GUI", "设置中心已更新并保存");
        }, StopProxyAsync);
        settings.ShowModal(this);
    }

    private Task ApplyLogLevelAsync(string newLevel) => _connectionOperation.RunAsync(() => ApplyLogLevelCoreAsync(newLevel));

    private async Task ApplyLogLevelCoreAsync(string newLevel)
    {
        if (_profiles is null)
            return;
        var level = LogLevels.Normalize(newLevel);
        _profiles.State.LogLevel = level;
        _minimumLogSeverity = LogLevels.ToSeverity(level);

        try
        {
            var restartTun = _tunService?.IsRunning == true;
            if (restartTun)
                await StopTunAsync();
            GenerateActiveRoutingConfig();
            if (restartTun)
                await StartTunAsync();
        }
        catch (Exception ex)
        {
            QueueLog("GUI", $"[error] 应用日志级别失败: {ex.Message}");
            SetTunState(false, "启动失败", Red);
            return;
        }

        QueueLog("GUI", _proxyService?.IsRunning == true
            ? $"Log level set to {level}; mqvpn 将在下次连接时使用该级别"
            : $"Log level set to {level}");
    }

    private bool IsGlobalMqvpnActive()
    {
        if (_profiles is null)
            return false;
        var policy = _profiles.GetPolicy(_profiles.ActiveProfile.RoutingPolicyId);
        return RoutingSemantics.IsGlobalMqvpn(policy);
    }

    private Task ChangeActiveRoutingAsync() => _connectionOperation.RunAsync(async () =>
    {
        try { await ChangeActiveRoutingCoreAsync(); }
        catch (Exception ex)
        {
            QueueLog("GUI", $"[error] 策略切换失败: {ex.Message}");
            await StopProxyCoreAsync();
            RefreshRoutingSelector();
        }
    });

    private async Task ChangeActiveRoutingCoreAsync()
    {
        if (_closing || _updatingRoutingSelector || _profiles is null || _routingSelector.SelectedValue is not string policyId ||
            policyId == _profiles.ActiveProfile.RoutingPolicyId)
            return;
        var restartTun = _tunService?.IsRunning == true;
        if (restartTun)
            await StopTunAsync();
        _profiles.ActiveProfile.RoutingPolicyId = policyId;
        _profiles.Save();
        GenerateActiveRoutingConfig();
        RefreshProfilesGrid();

        if (_proxyService?.IsRunning == true)
        {
            var isGlobal = IsGlobalMqvpnActive();
            await EnsureMqvpnRouteAsync();
            if (isGlobal)
            {
                if (restartTun)
                    await StopTunAsync();
                QueueLog("GUI", "已切换至全局 MQVPN 模式，直通虚拟网卡，无需开启 Xray TUN");
                SetTunState(false, "全局直通 (免TUN)", FluentUi.MutedBrush.Color);
                _tunToggle.Enabled = false;
            }
            else
            {
                _tunToggle.Enabled = true;
                await StartTunAsync();
            }
        }
    }

    private void SelectProfileRow(string id)
    {
        var match = _profileRows.FirstOrDefault(r => r.Profile.Id == id);
        if (match is not null)
            _profilesGrid.SelectedItem = match;
    }

    private async Task HandleProxyToggleAsync(bool desired)
    {
        if (_closing || _proxyToggle.Busy) return;
        _proxyToggle.Busy = true;
        try
        {
            if (desired)
                await StartProxyAsync();
            else
                await StopProxyAsync();
        }
        catch (Exception ex)
        {
            QueueLog("GUI", $"[error] {ex.Message}");
            SetProxyState(false, "启动失败", Red);
        }
        finally
        {
            _proxyToggle.Busy = false;
        }
    }

    private Task HandleTunToggleAsync(bool desired) => _connectionOperation.RunAsync(() => HandleTunToggleCoreAsync(desired));

    private async Task HandleTunToggleCoreAsync(bool desired)
    {
        if (_closing) return;
        _tunToggle.Busy = true;
        try
        {
            if (desired && IsGlobalMqvpnActive())
            {
                FluentUi.ShowAlert(this, "当前策略为全局 MQVPN，所有流量直接由 MQVPN 虚拟网卡转发，无需开启 Xray 分流 TUN。", "全局直通提示");
                SetTunState(false, "全局直通 (免TUN)", FluentUi.MutedBrush.Color);
                _tunToggle.Enabled = false;
                return;
            }

            if (desired)
                await StartTunAsync();
            else
                await StopTunAsync();
        }
        catch (Exception ex)
        {
            QueueLog("GUI", $"[error] {ex.Message}");
            SetTunState(false, "启动失败", Red);
        }
        finally
        {
            _tunToggle.Busy = false;
        }
    }

    private Task StartProxyAsync() => _connectionOperation.RunAsync(() =>
        _closing ? Task.CompletedTask : StartProxyCoreAsync());

    private async Task StartProxyCoreAsync()
    {
        if (_proxyService?.IsRunning == true)
            return;

        _activeTunName = null;
        _runtimeConfig = _profiles?.GetRuntimeConfig(_profiles.ActiveProfile)
            ?? throw new InvalidOperationException("配置列表尚未初始化");
        ValidateConfiguredPaths(_runtimeConfig);
        ProfileStore.WriteMqvpnConfigFile(MqvpnConfig, _runtimeConfig);
        _profiles.GenerateXrayConfig(_profiles.ActiveProfile, XrayConfig, _runtimeConfig);

        SetProxyState(false, "连接中...", Amber);
        var service = new ManagedService(
            "MQVPN",
            MqvpnExecutable,
            System.IO.Path.GetDirectoryName(MqvpnExecutable)!,
            new[] { "--config", MqvpnConfig, "--log-level", CurrentLogLevel },
            QueueLog);
        service.Exited += (code, expected) => OnServiceExited("proxy", service, code, expected);
        _proxyService = service;
        try
        {
            service.Start();
            QueueLog("GUI", $"mqvpn started (log level {CurrentLogLevel})");

            if (!await WaitForAdapterAsync(ActiveMqvpnTunName, service, TimeSpan.FromSeconds(18)))
            {
                await service.StopAsync();
                _proxyService = null;
                throw new InvalidOperationException($"{ActiveMqvpnTunName} 未在 18 秒内就绪");
            }

            _tunToggle.Enabled = false;
            _tunToggle.Busy = true;
            _footerStatus.Text = "代理已连接";
            _footerDot.Fill = FluentUi.GreenBrush;
            await EnsureMqvpnRouteAsync();
            if (!service.IsRunning) throw new InvalidOperationException("MQVPN 在路由设置期间退出");
            SetProxyState(true, "已连接", Green);
            StartTrafficMonitor();
            StartLatencyMonitor();
            QueueLog("GUI", "Proxy is ready");

            if (IsGlobalMqvpnActive())
            {
                QueueLog("GUI", "当前路由策略为【全局 MQVPN】，所有流量直接通过 MQVPN 虚拟网卡传输，无需开启 Xray 分流 TUN。");
                SetTunState(false, "全局直通 (免TUN)", FluentUi.MutedBrush.Color);
                _tunToggle.Busy = false;
                _tunToggle.Enabled = false;
                _footerStatus.Text = "全局 MQVPN 代理已连接";
            }
            else
            {
                try
                {
                    await StartTunAsync();
                }
                catch (Exception ex)
                {
                    QueueLog("GUI", $"[warning] 系统 TUN 自动启动失败: {ex.Message}");
                    SetTunState(false, "启动失败", Red);
                }
                finally
                {
                    _tunToggle.Busy = false;
                    _tunToggle.Enabled = true;
                }
            }
        }
        catch
        {
            await StopProxyCoreAsync();
            throw;
        }
    }

    private static void ValidateConfiguredPaths(JsonObject config)
    {
        var paths = (config["paths"] as JsonArray)?
            .Select(node => node?.GetValue<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray() ?? [];
        if (paths.Length == 0)
            return;

        var available = NetworkInterface.GetAllNetworkInterfaces()
            .Select(adapter => adapter.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = paths.Where(path => !available.Contains(path)).ToArray();
        if (missing.Length > 0)
        {
            var listed = string.Join(", ", missing);
            throw new InvalidOperationException($"MQVPN 网络路径不存在: {listed}\r\n请在配置编辑中重新选择当前网卡。");
        }
    }

    private Task StopProxyAsync() => _connectionOperation.RunAsync(StopProxyCoreAsync);

    private async Task StopProxyCoreAsync()
    {
        _tunToggle.Busy = false;
        StopLatencyMonitor();
        if (_tunService?.IsRunning == true || _tunToggle.IsOn)
            await StopTunAsync();
        await StopTrafficMonitorAsync();

        var service = _proxyService;
        if (service is not null)
        {
            SetProxyState(true, "关闭中...", Amber);
            await service.StopAsync();
            _proxyService = null;
        }

        if (_activeTunName is not null)
            await RemoveMqvpnRouteAsync();
        _activeTunName = null;
        _runtimeConfig = null;
        SetProxyState(false, "已关闭", Muted);
        _tunToggle.Enabled = false;
        _footerStatus.Text = "MQVPN 就绪";
        _footerDot.Fill = FluentUi.MutedBrush;
        if (service is not null)
            QueueLog("GUI", "Proxy stopped");
    }

    private async Task StartTunAsync()
    {
        await _tunOperation.WaitAsync();
        try
        {
            await StartTunCoreAsync();
        }
        finally
        {
            _tunOperation.Release();
        }
    }

    private async Task StartTunCoreAsync()
    {
        if (_proxyService?.IsRunning != true || !_proxyToggle.IsOn)
            throw new InvalidOperationException("请先开启代理");
        if (_tunService?.IsRunning == true)
            return;
        if (IsGlobalMqvpnActive())
        {
            QueueLog("GUI", "当前策略为全局 MQVPN，无需启动 Xray 分流 TUN");
            SetTunState(false, "全局直通 (免TUN)", FluentUi.MutedBrush.Color);
            _tunToggle.Enabled = false;
            return;
        }

        SetTunState(false, "启动中...", Amber);
        await EnsureMqvpnRouteAsync();

        var service = new ManagedService(
            "XRAY",
            XrayExecutable,
            System.IO.Path.GetDirectoryName(XrayExecutable)!,
            new[] { "run", "-c", XrayConfig },
            QueueLog);
        service.Exited += (code, expected) => OnServiceExited("tun", service, code, expected);
        _tunService = service;
        service.Start();
        QueueLog("GUI", "xray started");

        if (!await WaitForAdapterAsync("xray-tmp", service, TimeSpan.FromSeconds(12)))
        {
            await service.StopAsync();
            _tunService = null;
            throw new InvalidOperationException("xray-tmp 未在 12 秒内就绪");
        }

        SetTunState(true, "已开启", Green);
        _footerStatus.Text = $"{_profiles?.GetPolicy(_profiles.ActiveProfile.RoutingPolicyId).Name ?? "系统"} TUN 已开启";
        _footerDot.Fill = FluentUi.GreenBrush;
        QueueLog("GUI", "TruckersMP split tunnel is ready");
    }

    private async Task StopTunAsync()
    {
        await _tunOperation.WaitAsync();
        try
        {
            await StopTunCoreAsync();
        }
        finally
        {
            _tunOperation.Release();
        }
    }

    private async Task StopTunCoreAsync()
    {
        var service = _tunService;
        if (service is not null)
        {
            SetTunState(true, "关闭中...", Amber);
            await service.StopAsync();
            _tunService = null;
            QueueLog("GUI", "xray stopped");
        }

        SetTunState(false, "已关闭", Muted);
        if (_proxyToggle.IsOn)
        {
            _footerStatus.Text = "代理已连接";
            _footerDot.Fill = FluentUi.GreenBrush;
        }
    }

    private async Task<bool> WaitForAdapterAsync(
        string adapterName,
        ManagedService service,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!service.IsRunning)
                return false;

            var ready = await Task.Run(() => NetworkInterface.GetAllNetworkInterfaces().Any(adapter =>
                adapter.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase) &&
                adapter.OperationalStatus == OperationalStatus.Up));
            if (ready)
                return true;

            await Task.Delay(250);
        }

        return false;
    }

    private async Task EnsureMqvpnRouteAsync()
    {
        var adapter = ActiveMqvpnTunName.Replace("'", "''", StringComparison.Ordinal);
        var isGlobal = IsGlobalMqvpnActive();
        var metric = isGlobal ? 10 : 9000;

        var serverHost = string.Empty;
        if (isGlobal && _profiles is not null)
        {
            var cfg = _profiles.ReadConfig(_profiles.ActiveProfile);
            serverHost = ProfileStore.ParseHost(cfg["server_addr"]?.GetValue<string>() ?? string.Empty);
        }

        var script =
            $"Get-NetRoute -InterfaceAlias '{adapter}' -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Where-Object {{$_.RouteMetric -ne {metric}}} | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue; " +
            $"Get-NetRoute -InterfaceAlias '{adapter}' -DestinationPrefix '::/0' -ErrorAction SilentlyContinue | Where-Object {{$_.RouteMetric -ne {metric}}} | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue; " +
            $"$v4=Get-NetRoute -InterfaceAlias '{adapter}' -DestinationPrefix '0.0.0.0/0' " +
            $"-ErrorAction SilentlyContinue | Where-Object {{$_.RouteMetric -eq {metric}}}; " +
            "if(-not $v4){New-NetRoute -DestinationPrefix '0.0.0.0/0' " +
            $"-InterfaceAlias '{adapter}' -NextHop '0.0.0.0' -RouteMetric {metric} " +
            "-PolicyStore ActiveStore | Out-Null}; " +
            $"$v6=Get-NetRoute -InterfaceAlias '{adapter}' -DestinationPrefix '::/0' " +
            $"-ErrorAction SilentlyContinue | Where-Object {{$_.RouteMetric -eq {metric}}}; " +
            "if(-not $v6){New-NetRoute -DestinationPrefix '::/0' " +
            $"-InterfaceAlias '{adapter}' -NextHop '::' -RouteMetric {metric} " +
            "-PolicyStore ActiveStore -ErrorAction SilentlyContinue | Out-Null}";

        if (isGlobal && !string.IsNullOrEmpty(serverHost) && IPAddress.TryParse(serverHost, out var srvIp))
        {
            var prefix = srvIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? $"{serverHost}/32" : $"{serverHost}/128";
            script += $"; $srv=Get-NetRoute -DestinationPrefix '{prefix}' -ErrorAction SilentlyContinue | Where-Object {{$_.InterfaceAlias -ne '{adapter}'}}; " +
                      $"if(-not $srv){{ $def=Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Where-Object {{$_.InterfaceAlias -ne '{adapter}'}} | Sort-Object RouteMetric | Select-Object -First 1; " +
                      $"if($def){{ New-NetRoute -DestinationPrefix '{prefix}' -InterfaceIndex $def.InterfaceIndex -NextHop $def.NextHop -RouteMetric 1 -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Out-Null }} }}";
        }

        await RunPowerShellAsync(script);
    }

    private async Task RemoveMqvpnRouteAsync()
    {
        var adapter = ActiveMqvpnTunName.Replace("'", "''", StringComparison.Ordinal);
        var script =
            $"Get-NetRoute -InterfaceAlias '{adapter}' " +
            "-ErrorAction SilentlyContinue | Where-Object {$_.RouteMetric -eq 10 -or $_.RouteMetric -eq 9000} | " +
            "Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue";
        await RunPowerShellAsync(script, throwOnError: false);
    }

    private async Task RunPowerShellAsync(string script, bool throwOnError = true)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 PowerShell");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();
        if (stdout.Length > 0)
            QueueLog("ROUTE", stdout);
        if (stderr.Length > 0)
            QueueLog("ROUTE", $"[error] {stderr}");
        if (throwOnError && process.ExitCode != 0)
            throw new InvalidOperationException($"路由操作失败，退出码 {process.ExitCode}");
    }

    private void OnServiceExited(string serviceName, ManagedService source, int exitCode, bool expected)
    {
        if (IsDisposed)
            return;

        _ = Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await _connectionOperation.RunAsync(async () =>
                {
                    if (_closing)
                        return;

                    if (serviceName == "proxy" && !expected && ReferenceEquals(_proxyService, source))
                    {
                        QueueLog("GUI", $"[error] mqvpn 意外退出，退出码 {exitCode}");
                        await StopProxyCoreAsync();
                        SetProxyState(false, "已中断", Red);
                        _tunToggle.Enabled = false;
                        _footerDot.Fill = FluentUi.RedBrush;
                    }
                    else if (serviceName == "tun" && !expected && ReferenceEquals(_tunService, source))
                    {
                        QueueLog("GUI", $"[error] Xray 意外退出，退出码 {exitCode}");
                        await StopTunAsync();
                        SetTunState(false, "已中断", Red);
                        _footerDot.Fill = FluentUi.AmberBrush;
                    }
                });
            }
            catch (Exception ex)
            {
                QueueLog("GUI", $"[error] {serviceName} 退出后的清理失败: {ex.Message}");
                if (serviceName == "proxy") SetProxyState(false, "清理失败", Red);
                else SetTunState(false, "清理失败", Red);
                _footerStatus.Text = "进程已退出，清理失败，请检查日志";
                _footerDot.Fill = FluentUi.RedBrush;
            }
        });
    }

    private void SetProxyState(bool enabled, string text, Color color)
    {
        var brush = new SolidColorBrush(color);
        _proxyToggle.IsOn = enabled;
        _proxyStatus.Text = text;
        _proxyStatus.Foreground = brush;
        _proxyDot.Fill = brush;
    }

    private void SetTunState(bool enabled, string text, Color color)
    {
        var brush = new SolidColorBrush(color);
        _tunToggle.IsOn = enabled;
        _tunStatus.Text = text;
        _tunStatus.Foreground = brush;
        _tunDot.Fill = brush;
    }

    private void QueueLog(string source, string message)
    {
        var minimumSeverity = _minimumLogSeverity;
        if (message.IndexOfAny(LineBreaks) < 0)
        {
            QueueLogLine(source, message, minimumSeverity);
            return;
        }

        foreach (var rawLine in message.Replace("\r", string.Empty).Split('\n'))
            QueueLogLine(source, rawLine, minimumSeverity);
    }

    private void QueueLogLine(string source, string rawLine, LogSeverity minimumSeverity)
    {
        var line = SanitizeLogLine(rawLine);
        if (line.Length == 0)
            return;
        if (LogClassifier.TryCreate(source, line, minimumSeverity, out var entry))
            _logBuffer.Add(entry);
    }

    private static string SanitizeLogLine(string rawLine)
    {
        if (!HasTerminalControlCharacter(rawLine))
            return rawLine.TrimEnd();
        var withoutTerminalSequences = AnsiOperatingSystemCommand.Replace(rawLine, string.Empty);
        withoutTerminalSequences = AnsiEscape.Replace(withoutTerminalSequences, string.Empty);
        return TerminalControlCharacters.Replace(withoutTerminalSequences, string.Empty).TrimEnd();
    }

    private static bool HasTerminalControlCharacter(string value)
    {
        foreach (var character in value)
        {
            if (character <= '\x08' ||
                (character >= '\x0B' && character <= '\x1F') ||
                (character >= '\x7F' && character <= '\x9F'))
                return true;
        }
        return false;
    }

    private void StartTrafficMonitor()
    {
        _lastAdapterUploadBytes = null;
        _lastAdapterDownloadBytes = null;
        _lastXrayUploadBytes = null;
        _lastXrayDownloadBytes = null;
        _lastTrafficSampleUtc = null;
        _lastTrafficSaveUtc = DateTime.UtcNow;
        if (_profiles is not null)
            ResetDailyTrafficIfNeeded(_profiles.ActiveProfile);
        ScheduleTrafficSample();
        _trafficTimer.Start();
    }

    private async Task StopTrafficMonitorAsync()
    {
        _trafficTimer.Stop();
        await _trafficSample;
        await SampleTrafficAsync();
        _lastAdapterUploadBytes = null;
        _lastAdapterDownloadBytes = null;
        _lastXrayUploadBytes = null;
        _lastXrayDownloadBytes = null;
        _lastTrafficSampleUtc = null;
        _proxyUploadRate = 0;
        _proxyDownloadRate = 0;
        _directUploadRate = 0;
        _directDownloadRate = 0;
        UpdateSpeedDisplay();
        _profiles?.Save();
        RenderConnectionStatus();
    }

    private readonly record struct AdapterSample(
        bool TunReady,
        long TunUpload,
        long TunDownload,
        bool XrayReady,
        long XrayUpload,
        long XrayDownload);

    private static AdapterSample ReadAdapterSample(string tunName)
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces();
        var tun = adapters.FirstOrDefault(candidate =>
            candidate.Name.Equals(tunName, StringComparison.OrdinalIgnoreCase) &&
            candidate.OperationalStatus == OperationalStatus.Up);
        var xray = adapters.FirstOrDefault(candidate =>
            candidate.Name.Equals("xray-tmp", StringComparison.OrdinalIgnoreCase) &&
            candidate.OperationalStatus == OperationalStatus.Up);
        var tunStatistics = tun?.GetIPStatistics();
        var xrayStatistics = xray?.GetIPStatistics();
        return new AdapterSample(
            tunStatistics is not null,
            tunStatistics?.BytesSent ?? 0,
            tunStatistics?.BytesReceived ?? 0,
            xrayStatistics is not null,
            xrayStatistics?.BytesSent ?? 0,
            xrayStatistics?.BytesReceived ?? 0);
    }

    private void ScheduleTrafficSample()
    {
        if (_trafficSample.IsCompleted)
            _trafficSample = SampleTrafficAsync();
    }

    private async Task SampleTrafficAsync()
    {
        if (_profiles is null || _proxyService?.IsRunning != true)
            return;

        try
        {
            var tunName = ActiveMqvpnTunName;
            var sample = await Task.Run(() => ReadAdapterSample(tunName));
            if (IsDisposed || _profiles is null || _proxyService?.IsRunning != true)
                return;
            ApplyTrafficSample(sample);
        }
        catch
        {
        }
    }

    private void ApplyTrafficSample(AdapterSample sample)
    {
        if (_profiles is null || !sample.TunReady)
            return;

        var upload = sample.TunUpload;
        var download = sample.TunDownload;
        var now = DateTime.UtcNow;
        var elapsedSeconds = _lastTrafficSampleUtc is { } lastSample
            ? Math.Max(0.1, (now - lastSample).TotalSeconds)
            : 0;
        var profile = _profiles.ActiveProfile;
        ResetDailyTrafficIfNeeded(profile);

        long proxyUploadDelta = 0;
        long proxyDownloadDelta = 0;

        if (_lastAdapterUploadBytes is { } previousUpload)
        {
            // Handle adapter reset/recreation: if counter wrapped or restarted from zero, treat upload as fresh delta
            proxyUploadDelta = upload >= previousUpload ? (upload - previousUpload) : upload;
            profile.TodayUploadBytes += proxyUploadDelta;
            profile.TotalUploadBytes += proxyUploadDelta;
        }
        if (_lastAdapterDownloadBytes is { } previousDownload)
        {
            proxyDownloadDelta = download >= previousDownload ? (download - previousDownload) : download;
            profile.TodayDownloadBytes += proxyDownloadDelta;
            profile.TotalDownloadBytes += proxyDownloadDelta;
        }

        _lastAdapterUploadBytes = upload;
        _lastAdapterDownloadBytes = download;

        long directUploadDelta = 0;
        long directDownloadDelta = 0;

        if (sample.XrayReady)
        {
            var xrayUpload = sample.XrayUpload;
            var xrayDownload = sample.XrayDownload;
            if (_lastXrayUploadBytes is { } previousXrayUpload &&
                _lastXrayDownloadBytes is { } previousXrayDownload)
            {
                var totalUploadDelta = xrayUpload >= previousXrayUpload
                    ? (xrayUpload - previousXrayUpload)
                    : xrayUpload;
                var totalDownloadDelta = xrayDownload >= previousXrayDownload
                    ? (xrayDownload - previousXrayDownload)
                    : xrayDownload;

                // xray-tmp (MTU 9000 app layer) and mqvpn0 (MTU ~1400 QUIC tunnel layer) count bytes with different
                // framing overhead and packet fragmentation. When running pure proxy traffic, proxyDelta can slightly
                // exceed totalDelta or vice versa. Filter out this small framing discrepancy (<15%) to avoid direct speed fluttering.
                if (totalUploadDelta > proxyUploadDelta)
                {
                    var diff = totalUploadDelta - proxyUploadDelta;
                    if (proxyUploadDelta == 0 || diff > (long)(proxyUploadDelta * 0.15))
                        directUploadDelta = diff;
                }

                if (totalDownloadDelta > proxyDownloadDelta)
                {
                    var diff = totalDownloadDelta - proxyDownloadDelta;
                    if (proxyDownloadDelta == 0 || diff > (long)(proxyDownloadDelta * 0.15))
                        directDownloadDelta = diff;
                }
            }
            _lastXrayUploadBytes = xrayUpload;
            _lastXrayDownloadBytes = xrayDownload;
        }
        else
        {
            _lastXrayUploadBytes = null;
            _lastXrayDownloadBytes = null;
        }

        if (elapsedSeconds > 0)
        {
            var instantProxyUp = proxyUploadDelta / elapsedSeconds;
            var instantProxyDown = proxyDownloadDelta / elapsedSeconds;
            var instantDirectUp = directUploadDelta / elapsedSeconds;
            var instantDirectDown = directDownloadDelta / elapsedSeconds;

            // Apply Exponential Moving Average (EMA) smoothing to prevent rate jumping and timer jitter
            _proxyUploadRate = SmoothRate(_proxyUploadRate, instantProxyUp);
            _proxyDownloadRate = SmoothRate(_proxyDownloadRate, instantProxyDown);
            _directUploadRate = SmoothRate(_directUploadRate, instantDirectUp);
            _directDownloadRate = SmoothRate(_directDownloadRate, instantDirectDown);
        }

        _lastTrafficSampleUtc = now;
        UpdateSpeedDisplay();
        UpdateTrafficDisplay(profile);
        RenderConnectionStatus();

        if (DateTime.UtcNow - _lastTrafficSaveUtc >= TimeSpan.FromSeconds(15))
        {
            _profiles.Save();
            _lastTrafficSaveUtc = DateTime.UtcNow;
        }
    }

    private static double SmoothRate(double previousRate, double instantRate)
    {
        if (previousRate <= 0.001)
            return instantRate;
        if (instantRate <= 0.001)
            return previousRate < 10240 ? 0 : previousRate * 0.2;
        return (previousRate * 0.35) + (instantRate * 0.65);
    }

    private static void ResetDailyTrafficIfNeeded(ProxyProfile profile)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        if (profile.TrafficDate == today)
            return;
        profile.TrafficDate = today;
        profile.TodayUploadBytes = 0;
        profile.TodayDownloadBytes = 0;
    }

    private void UpdateTrafficDisplay(ProxyProfile profile)
    {
        var rowVm = _profileRows.FirstOrDefault(r => r.Profile.Id == profile.Id);
        if (rowVm is not null)
            rowVm.TrafficText = FormatProfileTraffic(profile);
    }

    private static string FormatProfileTraffic(ProxyProfile profile) =>
        $"今日 ↑{FormatCompactBytes(profile.TodayUploadBytes)} ↓{FormatCompactBytes(profile.TodayDownloadBytes)}   |   " +
        $"累计 ↑{FormatCompactBytes(profile.TotalUploadBytes)} ↓{FormatCompactBytes(profile.TotalDownloadBytes)}";

    private static string FormatCompactBytes(long bytes) => FormatBytes(bytes).Replace(" ", string.Empty);

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        var display = (double)value;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value} B" : $"{display:0.##} {units[unit]}";
    }

    private static string FormatRate(double bytesPerSecond) =>
        FormatBytes((long)Math.Max(0, Math.Round(bytesPerSecond))) + "/s";

    private void UpdateSpeedDisplay()
    {
        _proxySpeedText.Text = $"↑ {FormatRate(_proxyUploadRate)}   ↓ {FormatRate(_proxyDownloadRate)}";
        _directSpeedText.Text = $"↑ {FormatRate(_directUploadRate)}   ↓ {FormatRate(_directDownloadRate)}";
    }

    private void StartLatencyMonitor()
    {
        StopLatencyMonitor();
        _latencyCancellation = new CancellationTokenSource();
        _lastLatencyHealthy = null;
        _ = MonitorLatencyAsync(_latencyCancellation.Token);
    }

    private void StopLatencyMonitor()
    {
        _latencyCancellation?.Cancel();
        _latencyCancellation?.Dispose();
        _latencyCancellation = null;
        _lastLatencyHealthy = null;
        SetConnectionStatus("真连接 --", Muted);
    }

    private async Task MonitorLatencyAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probeTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                var elapsed = await MeasureLatencySerializedAsync(probeTimeout.Token);
                if (IsDisposed || cancellationToken.IsCancellationRequested)
                    return;

                _ = Dispatcher.BeginInvoke(() =>
                    SetConnectionStatus($"真连接 {Math.Max(1, elapsed.TotalMilliseconds):0} ms", Green));
                if (_lastLatencyHealthy == false)
                    QueueLog("LATENCY", "Real connection latency test recovered");
                _lastLatencyHealthy = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (IsDisposed || cancellationToken.IsCancellationRequested)
                    return;

                _ = Dispatcher.BeginInvoke(() => SetConnectionStatus("真连接超时", Red));
                if (_lastLatencyHealthy != false)
                    QueueLog("LATENCY", $"[warning] Real connection latency test failed: {ex.Message}");
                _lastLatencyHealthy = false;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RetestLatencyAsync()
    {
        if (_proxyService?.IsRunning != true || !_proxyToggle.IsOn)
        {
            SetConnectionStatus("真连接未连接", Muted);
            return;
        }

        SetConnectionStatus("真连接检测中...", Amber);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var elapsed = await MeasureLatencySerializedAsync(timeout.Token);
            if (!IsDisposed)
                SetConnectionStatus($"真连接 {Math.Max(1, elapsed.TotalMilliseconds):0} ms", Green);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or InvalidOperationException)
        {
            if (!IsDisposed)
                SetConnectionStatus("真连接超时", Red);
        }
    }

    private async Task<TimeSpan> MeasureLatencySerializedAsync(CancellationToken cancellationToken)
    {
        await _latencyProbeOperation.WaitAsync(cancellationToken);
        try
        {
            var probeEndpoint = _profiles?.State.LatencyProbeEndpoint;
            return await LatencyProbe.MeasureAsync(ActiveMqvpnTunName, cancellationToken, probeEndpoint);
        }
        finally
        {
            _latencyProbeOperation.Release();
        }
    }

    private void StartRegionLookup(string serverAddress)
    {
        _regionCancellation?.Cancel();
        _regionCancellation?.Dispose();
        _regionCancellation = new CancellationTokenSource();
        _currentRegion = "地区查询中";
        RenderConnectionStatus();
        _ = ResolveRegionAsync(serverAddress, _regionCancellation.Token);
    }

    private async Task ResolveRegionAsync(string serverAddress, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var region = await RegionResolver.ResolveAsync(serverAddress, timeout.Token);
            if (cancellationToken.IsCancellationRequested || IsDisposed)
                return;
            _currentRegion = region;
            RenderConnectionStatus();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            if (cancellationToken.IsCancellationRequested || IsDisposed)
                return;
            _currentRegion = "地区未知";
            RenderConnectionStatus();
        }
    }

    private void SetConnectionStatus(string latency, Color color)
    {
        _currentLatency = latency;
        _currentLatencyColor = color;
        RenderConnectionStatus();
    }

    private void RenderConnectionStatus()
    {
        var traffic = _profiles is null
            ? "今日 ↑ 0 B  ↓ 0 B"
            : $"今日 ↑ {FormatBytes(_profiles.ActiveProfile.TodayUploadBytes)}  ↓ {FormatBytes(_profiles.ActiveProfile.TodayDownloadBytes)}";
        _todayTrafficText.Text = traffic;
        var summary = $"{_currentRegion} · {_currentLatency}";
        var effectiveColor = _currentLatencyColor == FluentUi.SlateMuted ? FluentUi.GetMutedColor() : _currentLatencyColor;
        var brush = new SolidColorBrush(effectiveColor);
        _headerLatencyBadge.Text = summary;
        _headerLatencyBadge.Foreground = brush;
        _latencyStatus.Text = $"{summary}   |   {traffic}";
        _latencyStatus.Foreground = brush;
    }

    private void FlushLogs()
    {
        if (_flushingLogs)
            return;
        _flushingLogs = true;
        var important = 0;
        try
        {
            if (_logBuffer.TakeDroppedCount() is var dropped and > 0)
            {
                var notice = $"[warning] log buffer overflow: {dropped} 行日志被丢弃";
                if (Dispatch(new LogEntry(
                        DateTime.Now, "GUI", notice, true, LogSeverity.Warning, LogChannel.Client)))
                    important++;
            }

            var processed = 0;
            var deadline = Environment.TickCount64 + 40;
            foreach (var entry in _logBuffer.Drain(8000))
            {
                if (Dispatch(entry))
                    important++;
                if ((++processed & 255) == 0 && Environment.TickCount64 >= deadline)
                    break;
            }
        }
        finally
        {
            _importantLog.EndBatch();
            _mqvpnLog.EndBatch();
            _xrayLog.EndBatch();
            _flushingLogs = false;
        }

        if (important > 0)
        {
            _importantLines += important;
            UpdateImportantCount();
        }
    }

    private bool Dispatch(LogEntry entry)
    {
        if (entry.Channel != LogChannel.Xray)
            _mqvpnLog.Append(entry, entry.Severity);
        if (entry.Channel != LogChannel.Mqvpn)
            _xrayLog.Append(entry, entry.Severity);
        if (!entry.Important)
            return false;
        _importantLog.Append(entry, entry.Severity);
        return true;
    }

    private static Color SeverityColor(LogSeverity severity)
    {
        var isDark = FluentUi.CurrentTheme == ApplicationTheme.Dark;
        return severity switch
        {
            LogSeverity.Error => isDark ? Color.FromRgb(0xF8, 0x71, 0x71) : Color.FromRgb(0xDC, 0x26, 0x26),
            LogSeverity.Warning => isDark ? Color.FromRgb(0xFB, 0xBF, 0x24) : Color.FromRgb(0xB4, 0x53, 0x09),
            LogSeverity.Debug => isDark ? Color.FromRgb(0x94, 0xA3, 0xB8) : Color.FromRgb(0x47, 0x55, 0x69),
            _ => isDark ? Color.FromRgb(0xE2, 0xE8, 0xF0) : Color.FromRgb(0x0F, 0x17, 0x2A),
        };
    }

    private void UpdateImportantCount()
    {
        _importantCount.Text = _importantLines.ToString();
    }

    private async void HandleWindowClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (_allowClose)
            return;

        eventArgs.Cancel = true;
        if (!_exitRequested && _profiles?.State.MinimizeToTrayOnClose != false)
        {
            Hide();
            ShowInTaskbar = false;
            return;
        }

        if (_closing)
            return;
        await ShutdownAndCloseAsync();
    }

    private Task ExitApplicationAsync()
    {
        if (_allowClose || _closing)
            return Task.CompletedTask;
        _exitRequested = true;
        Close();
        return Task.CompletedTask;
    }

    private async Task ShutdownAndCloseAsync()
    {
        if (_closing)
            return;
        _closing = true;
        _regionCancellation?.Cancel();
        _proxyToggle.Enabled = false;
        _tunToggle.Enabled = false;
        _footerStatus.Text = "正在安全关闭内核...";

        try
        {
            await StopProxyAsync();
        }
        catch (Exception ex)
        {
            QueueLog("GUI", $"[error] 关闭失败: {ex.Message}");
        }
        finally
        {
            _logTimer.Stop();
            _trafficTimer.Stop();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _allowClose = true;
            Close();
        }
    }
}
