using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace TruckersMP.Proxy;

internal sealed class SettingsForm : FluentWindow, IDisposable
{
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRegistryName = "MQVPN";

    private readonly AppState _state;
    private readonly Action<AppState> _saveCallback;

    private readonly System.Windows.Controls.CheckBox _chkAutoStart;
    private readonly System.Windows.Controls.CheckBox _chkStartMinimized;
    private readonly System.Windows.Controls.CheckBox _chkMinimizeToTray;
    private readonly System.Windows.Controls.ComboBox _cboThemeMode;
    private readonly Wpf.Ui.Controls.TextBox _txtProbeEndpoint;
    private readonly System.Windows.Controls.ComboBox _cboLogLevel;
    private readonly Wpf.Ui.Controls.TextBox _txtMqvpnSource;
    private readonly Wpf.Ui.Controls.TextBox _txtXraySource;
    private readonly Wpf.Ui.Controls.TextBox _txtGeodataSource;
    private readonly System.Windows.Controls.CheckBox _chkPreRelease;

    public SettingsForm(AppState state, Action<AppState> saveCallback)
    {
        _state = state;
        _saveCallback = saveCallback;

        FluentUi.ConfigureWindow(this, "常规设置", 780, 680, 680, 540);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = FluentUi.CreateWindowRootGrid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleBar = FluentUi.CreateTitleBar("常规设置", showMaximize: false, showMinimize: false);
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        // Banner Card
        var bannerGrid = new Grid();
        bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var bannerIcon = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x28, 0x8B, 0x5C, 0xF6)),
            Margin = new Thickness(0, 0, 12, 0),
            Child = FluentUi.Icon(SymbolRegular.Settings24, 20, foreground: FluentUi.BrandSoftBrush),
        };
        var bannerText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        bannerText.Children.Add(FluentUi.PrimaryText("客户端全局行为偏好设置", 13.5, FontWeights.SemiBold));
        var subText = FluentUi.SecondaryText("配置开机自启、托盘控制、真连接测试探针端点及内核更新来源等全局选项。", 12);
        subText.Margin = new Thickness(0, 3, 0, 0);
        bannerText.Children.Add(subText);

        Grid.SetColumn(bannerIcon, 0);
        Grid.SetColumn(bannerText, 1);
        bannerGrid.Children.Add(bannerIcon);
        bannerGrid.Children.Add(bannerText);

        var bannerCard = FluentUi.CreateCardBorder(bannerGrid, new Thickness(16, 12, 16, 12), new Thickness(18, 8, 18, 10));
        Grid.SetRow(bannerCard, 1);
        root.Children.Add(bannerCard);

        // Scrollable content
        var contentStack = new StackPanel { Margin = new Thickness(18, 0, 18, 10) };

        // 1. 界面外观与主题
        contentStack.Children.Add(FluentUi.SectionHeader("界面外观与主题", SymbolRegular.Color20));

        _cboThemeMode = new System.Windows.Controls.ComboBox { Margin = new Thickness(0) };
        _cboThemeMode.Items.Add("跟随系统 (默认)");
        _cboThemeMode.Items.Add("浅色模式");
        _cboThemeMode.Items.Add("深色模式");
        var currentThemeSetting = _state.ThemeMode?.ToLowerInvariant() switch
        {
            "light" => "浅色模式",
            "dark" => "深色模式",
            _ => "跟随系统 (默认)",
        };
        _cboThemeMode.SelectedItem = currentThemeSetting;
        contentStack.Children.Add(FluentUi.SettingCard("界面主题", _cboThemeMode, "切换深色、浅色模式，或随 Windows 系统深浅色外观自动平滑切换。"));

        // 2. 系统与托盘
        contentStack.Children.Add(FluentUi.SectionHeader("系统与托盘控制", SymbolRegular.DesktopPulse20));

        _chkAutoStart = new System.Windows.Controls.CheckBox
        {
            Content = "开机时自动启动 MQVPN 客户端",
            IsChecked = CheckAutoStartRegistered(),
            Cursor = Cursors.Hand,
        };
        contentStack.Children.Add(FluentUi.SettingCard("开机自启动", _chkAutoStart, "向当前用户的 Windows 注册表 Run 键注册开机自启项。"));

        _chkStartMinimized = new System.Windows.Controls.CheckBox
        {
            Content = "客户端启动后自动最小化至托盘",
            IsChecked = _state.StartMinimized,
            Cursor = Cursors.Hand,
        };
        contentStack.Children.Add(FluentUi.SettingCard("静默启动", _chkStartMinimized, "启动后不弹出主窗口，仅常驻系统托盘图标。"));

        _chkMinimizeToTray = new System.Windows.Controls.CheckBox
        {
            Content = "点击关闭按钮时最小化到系统托盘",
            IsChecked = _state.MinimizeToTrayOnClose,
            Cursor = Cursors.Hand,
        };
        contentStack.Children.Add(FluentUi.SettingCard("托盘常驻", _chkMinimizeToTray, "关闭窗口时保持内核代理后台运行，避免误关断流；从托盘菜单可彻底退出。"));

        // 2. 网络与测速
        contentStack.Children.Add(FluentUi.SectionHeader("真连接测速偏好", SymbolRegular.Flash20));

        _txtProbeEndpoint = new Wpf.Ui.Controls.TextBox
        {
            Text = string.IsNullOrWhiteSpace(_state.LatencyProbeEndpoint) ? "1.1.1.1:80" : _state.LatencyProbeEndpoint,
            PlaceholderText = "1.1.1.1:80",
        };
        contentStack.Children.Add(FluentUi.SettingCard("测速探针端点", _txtProbeEndpoint, "在线状态下真连接测试的探测端点（默认 1.1.1.1:80，采用轻量 HTTP HEAD 探针规避 Hybrid TCP 假延迟）。"));

        _cboLogLevel = new System.Windows.Controls.ComboBox { Margin = new Thickness(0) };
        foreach (var lvl in LogLevels.All)
            _cboLogLevel.Items.Add(lvl);
        _cboLogLevel.SelectedItem = LogLevels.Normalize(_state.LogLevel);
        contentStack.Children.Add(FluentUi.SettingCard("全局日志级别", _cboLogLevel, "设置 mqvpn 与 xray 内核的控制台日志详细级别。"));

        // 3. 内核更新偏好
        contentStack.Children.Add(FluentUi.SectionHeader("内核更新仓库来源", SymbolRegular.ArrowDownload20));

        _txtMqvpnSource = new Wpf.Ui.Controls.TextBox
        {
            Text = string.IsNullOrWhiteSpace(_state.MqvpnUpdateSource) ? "r11234567/mqvpn" : _state.MqvpnUpdateSource,
            PlaceholderText = "r11234567/mqvpn",
        };
        contentStack.Children.Add(FluentUi.SettingCard("MQVPN 核心仓库", _txtMqvpnSource, "GitHub 仓库所有者与名称（owner/repo）。"));

        _txtXraySource = new Wpf.Ui.Controls.TextBox
        {
            Text = string.IsNullOrWhiteSpace(_state.XrayUpdateSource) ? "XTLS/Xray-core" : _state.XrayUpdateSource,
            PlaceholderText = "XTLS/Xray-core",
        };
        contentStack.Children.Add(FluentUi.SettingCard("Xray 核心仓库", _txtXraySource, "分流核心 GitHub 仓库来源。"));

        _txtGeodataSource = new Wpf.Ui.Controls.TextBox
        {
            Text = string.IsNullOrWhiteSpace(_state.GeoDataUpdateSource) ? "Loyalsoldier/v2ray-rules-dat" : _state.GeoDataUpdateSource,
            PlaceholderText = "Loyalsoldier/v2ray-rules-dat",
        };
        contentStack.Children.Add(FluentUi.SettingCard("GeoData 规则仓库", _txtGeodataSource, "GeoIP / GeoSite 路由规则数据文件来源。"));

        _chkPreRelease = new System.Windows.Controls.CheckBox
        {
            Content = "包含 Pre-Release 测试版核心与规则更新",
            IsChecked = _state.MqvpnIncludePreRelease || _state.XrayIncludePreRelease,
            Cursor = Cursors.Hand,
        };
        contentStack.Children.Add(FluentUi.SettingCard("预发布更新", _chkPreRelease, "检查组件更新时包含最新的 Pre-Release 试验版本。"));

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = contentStack,
        };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        // Bottom Actions
        var bottomGrid = new Grid { Margin = new Thickness(18, 8, 18, 14) };
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var btnStack = new StackPanel { Orientation = Orientation.Horizontal };
        var btnCancel = FluentUi.ActionButton("取消", SymbolRegular.Dismiss20, ControlAppearance.Secondary, (_, _) => Close());
        btnCancel.Margin = new Thickness(0, 0, 8, 0);
        var btnSave = FluentUi.ActionButton("保存并应用", SymbolRegular.Checkmark20, ControlAppearance.Primary, (_, _) => SaveAndClose());

        btnStack.Children.Add(btnCancel);
        btnStack.Children.Add(btnSave);

        Grid.SetColumn(btnStack, 1);
        bottomGrid.Children.Add(btnStack);

        Grid.SetRow(bottomGrid, 3);
        root.Children.Add(bottomGrid);

        Content = root;
    }

    private void SaveAndClose()
    {
        try
        {
            // Apply AutoStart registry
            var autoStartDesired = _chkAutoStart.IsChecked == true;
            ApplyAutoStartRegistry(autoStartDesired);

            _state.StartMinimized = _chkStartMinimized.IsChecked == true;
            _state.MinimizeToTrayOnClose = _chkMinimizeToTray.IsChecked == true;

            var selectedTheme = _cboThemeMode.SelectedItem?.ToString() switch
            {
                "浅色模式" => "light",
                "深色模式" => "dark",
                _ => "system",
            };
            _state.ThemeMode = selectedTheme;
            FluentUi.SetThemeMode(selectedTheme);

            _state.LatencyProbeEndpoint = string.IsNullOrWhiteSpace(_txtProbeEndpoint.Text) ? "1.1.1.1:80" : _txtProbeEndpoint.Text.Trim();
            _state.LogLevel = _cboLogLevel.SelectedItem as string ?? LogLevels.Default;

            _state.MqvpnUpdateSource = string.IsNullOrWhiteSpace(_txtMqvpnSource.Text) ? "r11234567/mqvpn" : _txtMqvpnSource.Text.Trim();
            _state.XrayUpdateSource = string.IsNullOrWhiteSpace(_txtXraySource.Text) ? "XTLS/Xray-core" : _txtXraySource.Text.Trim();
            _state.GeoDataUpdateSource = string.IsNullOrWhiteSpace(_txtGeodataSource.Text) ? "Loyalsoldier/v2ray-rules-dat" : _txtGeodataSource.Text.Trim();
            _state.MqvpnIncludePreRelease = _chkPreRelease.IsChecked == true;
            _state.XrayIncludePreRelease = _chkPreRelease.IsChecked == true;

            _saveCallback(_state);
            Close();
        }
        catch (Exception ex)
        {
            FluentUi.ShowAlert(this, $"保存设置失败: {ex.Message}", "设置错误", isError: true);
        }
    }

    private static bool CheckAutoStartRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
            return key?.GetValue(AppRegistryName) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyAutoStartRegistry(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
            if (key is null) return;
            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                    key.SetValue(AppRegistryName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(AppRegistryName, false);
            }
        }
        catch
        {
        }
    }

    public void ShowModal(Window? owner)
    {
        Owner = owner;
        ShowDialog();
    }

    public void Dispose()
    {
    }
}
