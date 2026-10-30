using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace TruckersMP.Proxy;

internal sealed class SettingsForm : FluentWindow, IDisposable
{
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRegistryName = "MQVPN";

    private readonly ProfileStore? _store;
    private readonly AppState _state;
    private readonly Action<ProfileStore>? _storeCallback;
    private readonly Func<Task>? _beforeRestore;
    private readonly Action<AppState>? _stateCallback;
    private readonly RoutingDocument _routing;

    private readonly TabControl _tabs = new();

    // ==================== Tab 0: 参数设置 ====================
    // 常规与界面
    private readonly System.Windows.Controls.ComboBox _cboThemeMode = new() { Margin = new Thickness(0) };
    private readonly System.Windows.Controls.CheckBox _chkAutoStart;
    private readonly System.Windows.Controls.CheckBox _chkStartMinimized;
    private readonly System.Windows.Controls.CheckBox _chkMinimizeToTray;
    private readonly System.Windows.Controls.CheckBox _chkConfirmOnExit;
    private readonly System.Windows.Controls.CheckBox _chkDisplayRealTimeSpeed;
    private readonly System.Windows.Controls.ComboBox _cboDoubleClickAction = new() { Margin = new Thickness(0) };

    // 核心与网络
    private readonly System.Windows.Controls.ComboBox _cboLogLevel = new() { Margin = new Thickness(0) };
    private readonly System.Windows.Controls.CheckBox _chkAllowLan;
    private readonly Wpf.Ui.Controls.NumberBox _numSocksPort = new();
    private readonly Wpf.Ui.Controls.NumberBox _numHttpPort = new();
    private readonly System.Windows.Controls.CheckBox _chkEnableUdp;
    private readonly System.Windows.Controls.CheckBox _chkEnableSniffing;
    private readonly System.Windows.Controls.CheckBox _chkRouteOnly;

    // 测速与探测
    private readonly Wpf.Ui.Controls.TextBox _txtProbeEndpoint;
    private readonly Wpf.Ui.Controls.NumberBox _numSpeedTestTimeout = new();
    private readonly Wpf.Ui.Controls.TextBox _txtSpeedTestUrl;
    private readonly Wpf.Ui.Controls.NumberBox _numMixedConcurrency = new();

    // 重连与弹性
    private readonly System.Windows.Controls.CheckBox _chkAutoReconnect;
    private readonly Wpf.Ui.Controls.NumberBox _numReconnectInterval = new();

    // 组件更新源
    private readonly Wpf.Ui.Controls.TextBox _txtGuiSource;
    private readonly Wpf.Ui.Controls.TextBox _txtMqvpnSource;
    private readonly Wpf.Ui.Controls.TextBox _txtXraySource;
    private readonly Wpf.Ui.Controls.TextBox _txtGeodataSource;
    private readonly System.Windows.Controls.CheckBox _chkAutoCheckUpdate;
    private readonly System.Windows.Controls.CheckBox _chkPreRelease;

    // ==================== Tab 1: DNS 设置 ====================
    private readonly Wpf.Ui.Controls.TextBox _txtDirectDns;
    private readonly Wpf.Ui.Controls.TextBox _txtRemoteDns;
    private readonly Wpf.Ui.Controls.TextBox _txtBootstrapDns;
    private readonly System.Windows.Controls.ComboBox _cboDomainStrategy = new() { Margin = new Thickness(0) };
    private readonly System.Windows.Controls.CheckBox _chkFakeDns;
    private readonly Wpf.Ui.Controls.TextBox _txtFakeDnsRange;
    private readonly System.Windows.Controls.CheckBox _chkBlockAAAA;
    private readonly Wpf.Ui.Controls.TextBox _txtCustomHosts;

    // ==================== Tab 2: 路由设置 ====================
    private readonly System.Windows.Controls.ComboBox _policies = new() { Width = 260 };
    private readonly System.Windows.Controls.ComboBox _finalOutbound = new() { Width = 150 };
    private readonly Wpf.Ui.Controls.DataGrid _rules = new();
    private readonly ObservableCollection<RoutingRuleViewModel> _ruleItems = [];
    private bool _updatingPolicy;

    // ==================== Tab 3: 备份与还原 ====================
    private readonly System.Windows.Controls.TextBlock _txtBackupStatus = new() { TextWrapping = TextWrapping.Wrap };

    public TabControl TabsControl => _tabs;

    public bool ShowModal(Window? owner)
    {
        if (owner is { IsVisible: true })
            Owner = owner;
        return ShowDialog() == true;
    }

    public SettingsForm(AppState state, Action<AppState> saveCallback)
        : this(null, 0, null, state, saveCallback)
    {
    }

    public SettingsForm(ProfileStore store, int initialTabIndex = 0, Action<ProfileStore>? onSaved = null, Func<Task>? beforeRestore = null)
        : this(store, initialTabIndex, onSaved, store.State, null)
    {
        _beforeRestore = beforeRestore;
    }

    private SettingsForm(
        ProfileStore? store,
        int initialTabIndex,
        Action<ProfileStore>? storeCallback,
        AppState state,
        Action<AppState>? stateCallback)
    {
        _store = store;
        _state = state;
        _storeCallback = storeCallback;
        _stateCallback = stateCallback;

        _routing = store is not null
            ? JsonSerializer.Deserialize<RoutingDocument>(JsonSerializer.Serialize(store.Routing)) ?? new RoutingDocument()
            : ProfileStore.CreateDefaultRouting();

        FluentUi.ConfigureWindow(this, "设置中心 (Settings)", 920, 720, 780, 580);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = FluentUi.CreateWindowRootGrid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleBar = FluentUi.CreateTitleBar("设置中心", showMaximize: true, showMinimize: false);
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
        bannerText.Children.Add(FluentUi.PrimaryText("参数设置 · DNS 解析 · 路由分流 · 备份与还原", 13.5, FontWeights.SemiBold));
        var subText = FluentUi.SecondaryText("借鉴 v2rayN 丰富系统设置架构，数据实时持久化至本地科学 SQLite 数据库 (mqvpn.db)。", 12);
        subText.Margin = new Thickness(0, 3, 0, 0);
        bannerText.Children.Add(subText);

        Grid.SetColumn(bannerIcon, 0);
        Grid.SetColumn(bannerText, 1);
        bannerGrid.Children.Add(bannerIcon);
        bannerGrid.Children.Add(bannerText);

        var bannerCard = FluentUi.CreateCardBorder(bannerGrid, new Thickness(16, 12, 16, 12), new Thickness(18, 8, 18, 6));
        Grid.SetRow(bannerCard, 1);
        root.Children.Add(bannerCard);

        // 1. 初始化【参数设置】控件
        _chkAutoStart = new System.Windows.Controls.CheckBox
        {
            Content = "开机时自动启动 MQVPN 客户端",
            IsChecked = CheckAutoStartRegistered(),
            Cursor = Cursors.Hand,
        };
        _chkStartMinimized = new System.Windows.Controls.CheckBox
        {
            Content = "客户端启动后自动最小化至系统托盘",
            IsChecked = _state.StartMinimized,
            Cursor = Cursors.Hand,
        };
        _chkMinimizeToTray = new System.Windows.Controls.CheckBox
        {
            Content = "点击关闭按钮时最小化到托盘（保持后台代理连接）",
            IsChecked = _state.MinimizeToTrayOnClose,
            Cursor = Cursors.Hand,
        };
        _chkConfirmOnExit = new System.Windows.Controls.CheckBox
        {
            Content = "退出客户端时弹出确认提示框",
            IsChecked = _state.ConfirmOnExit,
            Cursor = Cursors.Hand,
        };
        _chkDisplayRealTimeSpeed = new System.Windows.Controls.CheckBox
        {
            Content = "状态栏与主界面实时统计并刷新网络速率",
            IsChecked = _state.DisplayRealTimeSpeed,
            Cursor = Cursors.Hand,
        };

        _cboThemeMode.Items.Add("跟随系统 (默认)");
        _cboThemeMode.Items.Add("浅色模式");
        _cboThemeMode.Items.Add("深色模式");
        _cboThemeMode.SelectedItem = _state.ThemeMode?.ToLowerInvariant() switch
        {
            "light" => "浅色模式",
            "dark" => "深色模式",
            _ => "跟随系统 (默认)",
        };

        _cboDoubleClickAction.Items.Add("编辑配置 (默认)");
        _cboDoubleClickAction.Items.Add("设为活动并连接");
        _cboDoubleClickAction.SelectedIndex = _state.DoubleClickToActivate ? 1 : 0;

        foreach (var lvl in LogLevels.All)
            _cboLogLevel.Items.Add(lvl);
        _cboLogLevel.SelectedItem = LogLevels.Normalize(_state.LogLevel);

        _chkAllowLan = new System.Windows.Controls.CheckBox
        {
            Content = "允许局域网设备接入本代理客户端",
            IsChecked = _state.AllowLan,
            Cursor = Cursors.Hand,
        };
        ConfigureNumber(_numSocksPort, 0, 65535, _state.LocalSocksPort >= 0 ? _state.LocalSocksPort : 10808);
        ConfigureNumber(_numHttpPort, 0, 65535, _state.LocalHttpPort >= 0 ? _state.LocalHttpPort : 10809);
        _chkEnableUdp = new System.Windows.Controls.CheckBox
        {
            Content = "开启 UDP 流量转发支持",
            IsChecked = _state.EnableUdp,
            Cursor = Cursors.Hand,
        };
        _chkEnableSniffing = new System.Windows.Controls.CheckBox
        {
            Content = "开启流量嗅探 (HTTP / TLS / QUIC 协议识别)",
            IsChecked = _state.EnableSniffing,
            Cursor = Cursors.Hand,
        };
        _chkRouteOnly = new System.Windows.Controls.CheckBox
        {
            Content = "嗅探域名仅用于路由匹配，不篡改实际出站目的",
            IsChecked = _state.RouteOnly,
            Cursor = Cursors.Hand,
        };

        _txtProbeEndpoint = new Wpf.Ui.Controls.TextBox
        {
            Text = string.IsNullOrWhiteSpace(_state.LatencyProbeEndpoint) ? "1.1.1.1:80" : _state.LatencyProbeEndpoint,
            PlaceholderText = "1.1.1.1:80",
        };
        ConfigureNumber(_numSpeedTestTimeout, 500, 30000, _state.SpeedTestTimeoutMs > 0 ? _state.SpeedTestTimeoutMs : 3000);
        _txtSpeedTestUrl = new Wpf.Ui.Controls.TextBox
        {
            Text = string.IsNullOrWhiteSpace(_state.SpeedTestUrl) ? "http://cachefly.cachefly.net/10mb.test" : _state.SpeedTestUrl,
            PlaceholderText = "http://cachefly.cachefly.net/10mb.test",
        };
        ConfigureNumber(_numMixedConcurrency, 1, 30, _state.MixedConcurrencyCount > 0 ? _state.MixedConcurrencyCount : 5);

        _chkAutoReconnect = new System.Windows.Controls.CheckBox
        {
            Content = "隧道意外断开时自动重连",
            IsChecked = _state.AutoReconnect,
            Cursor = Cursors.Hand,
        };
        ConfigureNumber(_numReconnectInterval, 1, 60, _state.ReconnectIntervalSec > 0 ? _state.ReconnectIntervalSec : 5);

        _txtGuiSource = new Wpf.Ui.Controls.TextBox
        {
            Text = string.IsNullOrWhiteSpace(_state.GuiUpdateSource) ? "r11234567/mqvpn" : _state.GuiUpdateSource,
            PlaceholderText = "r11234567/mqvpn",
        };
        _txtMqvpnSource = new Wpf.Ui.Controls.TextBox
        {
            Text = string.IsNullOrWhiteSpace(_state.MqvpnUpdateSource) ? "r11234567/mqvpn" : _state.MqvpnUpdateSource,
            PlaceholderText = "r11234567/mqvpn",
        };
        _txtXraySource = new Wpf.Ui.Controls.TextBox
        {
            Text = string.IsNullOrWhiteSpace(_state.XrayUpdateSource) ? "XTLS/Xray-core" : _state.XrayUpdateSource,
            PlaceholderText = "XTLS/Xray-core",
        };
        _txtGeodataSource = new Wpf.Ui.Controls.TextBox
        {
            Text = string.IsNullOrWhiteSpace(_state.GeoDataUpdateSource) ? "Loyalsoldier/v2ray-rules-dat" : _state.GeoDataUpdateSource,
            PlaceholderText = "Loyalsoldier/v2ray-rules-dat",
        };
        _chkAutoCheckUpdate = new System.Windows.Controls.CheckBox
        {
            Content = "客户端启动时自动检查各组件是否有更新",
            IsChecked = _state.AutoCheckUpdatesOnStartup,
            Cursor = Cursors.Hand,
        };
        _chkPreRelease = new System.Windows.Controls.CheckBox
        {
            Content = "检查更新时包含 Pre-Release 预发布版本",
            IsChecked = _state.GuiIncludePreRelease || _state.MqvpnIncludePreRelease || _state.XrayIncludePreRelease,
            Cursor = Cursors.Hand,
        };

        // 2. 初始化【DNS 设置】控件
        _txtDirectDns = new Wpf.Ui.Controls.TextBox
        {
            Text = string.IsNullOrWhiteSpace(_state.DirectDns) ? "223.5.5.5, 119.29.29.29, 114.114.114.114" : _state.DirectDns,
            PlaceholderText = "223.5.5.5, 119.29.29.29, 114.114.114.114",
        };
        _txtRemoteDns = new Wpf.Ui.Controls.TextBox
        {
            Text = string.IsNullOrWhiteSpace(_state.RemoteDns) ? "https://1.1.1.1/dns-query, 8.8.8.8, 1.0.0.1" : _state.RemoteDns,
            PlaceholderText = "https://1.1.1.1/dns-query, 8.8.8.8, 1.0.0.1",
        };
        _txtBootstrapDns = new Wpf.Ui.Controls.TextBox
        {
            Text = string.IsNullOrWhiteSpace(_state.BootstrapDns) ? "223.5.5.5, 1.1.1.1" : _state.BootstrapDns,
            PlaceholderText = "223.5.5.5, 1.1.1.1",
        };

        _cboDomainStrategy.Items.Add("IPIfNonMatch (推荐)");
        _cboDomainStrategy.Items.Add("IPOnDemand (按需解析)");
        _cboDomainStrategy.Items.Add("AsIs (直接使用域名)");
        _cboDomainStrategy.SelectedItem = _state.DomainStrategy switch
        {
            "IPOnDemand" => "IPOnDemand (按需解析)",
            "AsIs" => "AsIs (直接使用域名)",
            _ => "IPIfNonMatch (推荐)",
        };

        _chkFakeDns = new System.Windows.Controls.CheckBox
        {
            Content = "开启 FakeDNS 虚假 IP 机制 (极大加速域名响应)",
            IsChecked = _state.FakeDnsEnabled,
            Cursor = Cursors.Hand,
        };
        _txtFakeDnsRange = new Wpf.Ui.Controls.TextBox
        {
            Text = string.IsNullOrWhiteSpace(_state.FakeDnsRange) ? "198.18.0.0/15" : _state.FakeDnsRange,
            PlaceholderText = "198.18.0.0/15",
            Width = 200,
        };
        _chkBlockAAAA = new System.Windows.Controls.CheckBox
        {
            Content = "阻止 IPv6 AAAA 解析查询 (避免部分网络 IPv6 绕路、高延迟或 DNS 污染)",
            IsChecked = _state.BlockAAAAQuery,
            Cursor = Cursors.Hand,
        };
        _txtCustomHosts = new Wpf.Ui.Controls.TextBox
        {
            Text = _state.CustomHosts ?? string.Empty,
            PlaceholderText = "自定义静态域名映射 (每行一条，例如: example.internal 192.168.1.1)",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 85,
        };

        // 3. 初始化【路由设置】控件
        InitRoutingControls();

        // 4. 装配 Tab 页面
        FluentUi.StyleTabControl(_tabs);
        _tabs.Margin = new Thickness(18, 4, 18, 8);
        _tabs.Items.Add(BuildOptionsTab());
        _tabs.Items.Add(BuildDnsTab());
        _tabs.Items.Add(BuildRoutingTab());
        _tabs.Items.Add(BuildBackupTab());

        if (initialTabIndex >= 0 && initialTabIndex < _tabs.Items.Count)
            _tabs.SelectedIndex = initialTabIndex;

        Grid.SetRow(_tabs, 2);
        root.Children.Add(_tabs);

        // 底部动作按钮
        var bottomGrid = new Grid { Margin = new Thickness(18, 8, 18, 14) };
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var infoTip = FluentUi.SecondaryText("点击“保存并应用设置”后将立即同步入库，若分流核心运行中会自动热加载生效。", 12);
        infoTip.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(infoTip, 0);
        bottomGrid.Children.Add(infoTip);

        var btnStack = new StackPanel { Orientation = Orientation.Horizontal };
        var btnCancel = FluentUi.ActionButton("取消", SymbolRegular.Dismiss20, ControlAppearance.Secondary, (_, _) => Close());
        btnCancel.Margin = new Thickness(0, 0, 8, 0);
        var btnSave = FluentUi.ActionButton("保存并应用设置", SymbolRegular.Checkmark20, ControlAppearance.Primary, (_, _) => SaveAndClose());

        btnStack.Children.Add(btnCancel);
        btnStack.Children.Add(btnSave);

        Grid.SetColumn(btnStack, 1);
        bottomGrid.Children.Add(btnStack);

        Grid.SetRow(bottomGrid, 3);
        root.Children.Add(bottomGrid);

        Content = root;
    }

    // ==================== Tab 0: 参数设置 UI ====================
    private TabItem BuildOptionsTab()
    {
        var page = CreateTabPage("参数设置", out var stack);

        stack.Children.Add(FluentUi.SectionHeader("常规与界面偏好", SymbolRegular.DesktopPulse20));
        stack.Children.Add(FluentUi.SettingCard("界面主题模式", _cboThemeMode, "切换深色、浅色模式，或随 Windows 系统外观实时自动平滑切换。"));
        stack.Children.Add(FluentUi.SettingCard("双击节点行为", _cboDoubleClickAction, "双击主界面配置列表节点项时执行的操作。"));
        stack.Children.Add(FluentUi.SettingCard("开机自启动", _chkAutoStart, "向当前用户的 Windows 注册表 Run 键注册开机自启项。"));
        stack.Children.Add(FluentUi.SettingCard("静默常驻启动", _chkStartMinimized, "启动时不弹出主窗口，仅常驻系统托盘图标。"));
        stack.Children.Add(FluentUi.SettingCard("托盘常驻守护", _chkMinimizeToTray, "关闭窗口时保持内核代理后台运行，避免误关断流；从托盘菜单可彻底退出。"));
        stack.Children.Add(FluentUi.SettingCard("退出确认提示", _chkConfirmOnExit, "关闭或退出客户端时弹出确认弹窗。"));
        stack.Children.Add(FluentUi.SettingCard("实时网速统计", _chkDisplayRealTimeSpeed, "以 1 秒为周期实时统计网卡上传与下载速率。"));

        stack.Children.Add(FluentUi.SectionHeader("核心网络与代理端口", SymbolRegular.Router20));
        stack.Children.Add(FluentUi.SettingCard("控制台日志级别", _cboLogLevel, "设置 mqvpn 与 xray 内核的控制台日志详细级别。"));
        stack.Children.Add(FluentUi.SettingCard("允许局域网接入", _chkAllowLan, "是否允许局域网中其他设备通过本机访问代理流量。"));
        stack.Children.Add(FluentUi.SettingCard("本地 Socks5 端口", _numSocksPort, "本地 Socks5 代理监听端口 (填 0 为关闭，默认 10808)。"));
        stack.Children.Add(FluentUi.SettingCard("本地 HTTP 代理端口", _numHttpPort, "本地 HTTP 代理监听端口 (填 0 为关闭，默认 10809)。"));
        stack.Children.Add(FluentUi.SettingCard("UDP 流量转发", _chkEnableUdp, "是否对 UDP 数据包进行透明转发。"));
        stack.Children.Add(FluentUi.SettingCard("流量协议嗅探", _chkEnableSniffing, "通过嗅探数据包分析 HTTP/TLS/QUIC 目标域名，支持分流规则精准匹配。"));
        stack.Children.Add(FluentUi.SettingCard("嗅探仅用于路由", _chkRouteOnly, "开启后嗅探结果仅用于分流出站决策，不重写客户端请求的原始目的地址。"));

        stack.Children.Add(FluentUi.SectionHeader("测速与延迟探测", SymbolRegular.Flash20));
        stack.Children.Add(FluentUi.SettingCard("测速探针端点", _txtProbeEndpoint, "在线状态下真连接测试的探测端点（默认 1.1.1.1:80，采用轻量 HTTP HEAD 探针规避伪延迟）。"));
        stack.Children.Add(FluentUi.SettingCard("测速超时阈值 (ms)", _numSpeedTestTimeout, "真连接测试最大允许等待的超时时间（毫秒），超时将判定节点不可达。"));
        stack.Children.Add(FluentUi.SettingCard("下载测速 URL", _txtSpeedTestUrl, "真实速度测试文件下载地址。"));
        stack.Children.Add(FluentUi.SettingCard("测速并发数量", _numMixedConcurrency, "批量测试节点延迟时的最大并发线程数。"));

        stack.Children.Add(FluentUi.SectionHeader("断线重连与健壮性", SymbolRegular.ArrowSync20));
        stack.Children.Add(FluentUi.SettingCard("断线自动重连", _chkAutoReconnect, "网络抖动或切换物理网卡导致隧道断开时，自动发起指数退避重连。"));
        stack.Children.Add(FluentUi.SettingCard("重连基础间隔 (秒)", _numReconnectInterval, "触发重试的基础间隔秒数，由内核按退避策略动态调整。"));

        stack.Children.Add(FluentUi.SectionHeader("组件更新源配置", SymbolRegular.ArrowDownload20));
        stack.Children.Add(FluentUi.SettingCard("客户端 GUI 仓库", _txtGuiSource, "客户端发行版仓库。更新检查器固定匹配名为 mqvpn_windows_gui 的发布包。"));
        stack.Children.Add(FluentUi.SettingCard("MQVPN 核心仓库", _txtMqvpnSource, "多路径 QUIC 核心 (mqvpn.exe / xquic.dll) 来源仓库。"));
        stack.Children.Add(FluentUi.SettingCard("Xray 核心仓库", _txtXraySource, "透明路由与分流引擎 (xray.exe) 来源仓库。"));
        stack.Children.Add(FluentUi.SettingCard("GeoData 规则库", _txtGeodataSource, "GeoIP / GeoSite 路由规则数据仓库 (Loyalsoldier/v2ray-rules-dat)。"));
        stack.Children.Add(FluentUi.SettingCard("启动自动检查更新", _chkAutoCheckUpdate, "客户端启动并就绪后，在后台静默查询是否有可用组件新版本。"));
        stack.Children.Add(FluentUi.SettingCard("允许预发布版本", _chkPreRelease, "检查组件更新时包含最新的 Pre-Release 试验版本。"));

        return page;
    }

    // ==================== Tab 1: DNS 设置 UI ====================
    private TabItem BuildDnsTab()
    {
        var page = CreateTabPage("DNS 设置", out var stack);

        stack.Children.Add(FluentUi.SectionHeader("核心 DNS 服务器", SymbolRegular.GlobeSearch20));
        stack.Children.Add(FluentUi.SettingCard("国内直连 DNS", _txtDirectDns, "国内域名与直连规则的解析服务器，低延迟防污染（逗号分隔，如 223.5.5.5, 119.29.29.29）。"));
        stack.Children.Add(FluentUi.SettingCard("远程代理解析 DNS", _txtRemoteDns, "用于被墙域名及海外规则解析，支持 DoH 加密（如 https://1.1.1.1/dns-query, 8.8.8.8）。"));
        stack.Children.Add(FluentUi.SettingCard("Bootstrap 引导 DNS", _txtBootstrapDns, "用于初次解析 DoH 域名自身 IP 的引导 DNS 服务器。"));

        stack.Children.Add(FluentUi.SectionHeader("解析策略与高级特性", SymbolRegular.ShieldKeyhole20));
        stack.Children.Add(FluentUi.SettingCard("域名解析查询策略", _cboDomainStrategy, "控制路由分流时的域名解析行为。IPIfNonMatch 为推荐模式：未命中域名规则时解析为 IP 再次匹配。"));
        stack.Children.Add(FluentUi.SettingCard("FakeDNS 解析加速", _chkFakeDns, "分配保留虚假 IP 给域名，减少解析等待耗时。"));
        stack.Children.Add(FluentUi.SettingCard("FakeDNS 地址池", _txtFakeDnsRange, "FakeDNS 所使用的 CIDR 地址范围，默认 198.18.0.0/15。"));
        stack.Children.Add(FluentUi.SettingCard("过滤 IPv6 AAAA", _chkBlockAAAA, "阻止 IPv6 地址解析，有效避免部分运营商环境下因 IPv6 绕路导致的额外延迟与污染。"));

        stack.Children.Add(FluentUi.SectionHeader("自定义静态 Hosts", SymbolRegular.DocumentBulletList20));
        stack.Children.Add(FluentUi.SettingCard("静态域名映射表", _txtCustomHosts, "自定义域名与 IP 映射（每行一条，格式如: my.server 1.2.3.4）。"));

        var presetBtn = FluentUi.ActionButton("恢复推荐科学 DNS 预设", SymbolRegular.ArrowReset20, ControlAppearance.Secondary, (_, _) =>
        {
            _txtDirectDns.Text = "223.5.5.5, 119.29.29.29, 114.114.114.114";
            _txtRemoteDns.Text = "https://1.1.1.1/dns-query, 8.8.8.8, 1.0.0.1";
            _txtBootstrapDns.Text = "223.5.5.5, 1.1.1.1";
            _cboDomainStrategy.SelectedIndex = 0;
            _chkFakeDns.IsChecked = false;
            _txtFakeDnsRange.Text = "198.18.0.0/15";
            _chkBlockAAAA.IsChecked = false;
            FluentUi.ShowAlert(this, "已重置为推荐科学 DNS 配置！", "预设已恢复");
        });
        presetBtn.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(presetBtn);

        return page;
    }

    // ==================== Tab 2: 路由设置 UI ====================
    private TabItem BuildRoutingTab()
    {
        var grid = new Grid { Margin = new Thickness(4, 6, 4, 6) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 顶栏：策略与最终出口
        var topGrid = new Grid();
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var policyBar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        policyBar.Children.Add(FluentUi.Icon(SymbolRegular.Router20, 18, foreground: FluentUi.BrandSoftBrush));
        var lbl = FluentUi.PrimaryText("当前策略", 13, FontWeights.SemiBold);
        lbl.Margin = new Thickness(8, 0, 10, 0);
        policyBar.Children.Add(lbl);

        _policies.Margin = new Thickness(0, 0, 10, 0);
        _policies.SelectionChanged += (_, _) =>
        {
            if (!_updatingPolicy)
                RefreshRules();
        };
        policyBar.Children.Add(_policies);

        var btnNew = FluentUi.ActionButton("新建", SymbolRegular.Add20, ControlAppearance.Secondary, (_, _) => AddPolicy());
        btnNew.Margin = new Thickness(0, 0, 6, 0);
        var btnClone = FluentUi.ActionButton("克隆", SymbolRegular.Copy20, ControlAppearance.Secondary, (_, _) => ClonePolicy());
        btnClone.Margin = new Thickness(0, 0, 6, 0);
        var btnDelete = FluentUi.ActionButton("删除", SymbolRegular.Delete20, ControlAppearance.Secondary, (_, _) => DeletePolicy());
        policyBar.Children.Add(btnNew);
        policyBar.Children.Add(btnClone);
        policyBar.Children.Add(btnDelete);

        var finalBar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var finalLabel = FluentUi.SecondaryText("未匹配流量出站", 12.5);
        finalLabel.Margin = new Thickness(0, 0, 8, 0);
        finalBar.Children.Add(finalLabel);
        foreach (var item in new[] { "DIRECT", "MQVPN-TUN", "REJECT" })
            _finalOutbound.Items.Add(item);
        _finalOutbound.SelectionChanged += (_, _) =>
        {
            if (!_updatingPolicy && CurrentPolicy is { } policy && _finalOutbound.SelectedItem is string val)
                policy.FinalOutbound = val;
        };
        finalBar.Children.Add(_finalOutbound);

        Grid.SetColumn(policyBar, 0);
        Grid.SetColumn(finalBar, 1);
        topGrid.Children.Add(policyBar);
        topGrid.Children.Add(finalBar);

        var topCard = FluentUi.CreateCardBorder(topGrid, new Thickness(14, 10, 14, 10), new Thickness(0, 0, 0, 8));
        Grid.SetRow(topCard, 0);
        grid.Children.Add(topCard);

        // 主体：规则列表与侧边动作
        var centerGrid = new Grid();
        centerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        centerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(144) });

        var tableCard = FluentUi.CreateCardBorder(_rules, new Thickness(4), new Thickness(0, 0, 10, 0));
        Grid.SetColumn(tableCard, 0);
        centerGrid.Children.Add(tableCard);

        var sideStack = new StackPanel();
        sideStack.Children.Add(MakeSideButton("添加规则", SymbolRegular.Add20, ControlAppearance.Primary, (_, _) => AddRule()));
        sideStack.Children.Add(MakeSideButton("编辑规则", SymbolRegular.Edit20, ControlAppearance.Secondary, (_, _) => EditRule()));
        sideStack.Children.Add(MakeSideButton("删除规则", SymbolRegular.Delete20, ControlAppearance.Secondary, (_, _) => DeleteRule()));
        sideStack.Children.Add(new Separator { Margin = new Thickness(4, 6, 4, 10) });
        sideStack.Children.Add(MakeSideButton("上移", SymbolRegular.ArrowUp20, ControlAppearance.Secondary, (_, _) => MoveRule(-1)));
        sideStack.Children.Add(MakeSideButton("下移", SymbolRegular.ArrowDown20, ControlAppearance.Secondary, (_, _) => MoveRule(1)));
        sideStack.Children.Add(new Separator { Margin = new Thickness(4, 6, 4, 10) });
        sideStack.Children.Add(MakeSideButton("重置预设", SymbolRegular.ArrowReset20, ControlAppearance.Secondary, (_, _) => ResetPresetPolicies()));

        var sideCard = FluentUi.CreateCardBorder(sideStack, new Thickness(10));
        Grid.SetColumn(sideCard, 1);
        centerGrid.Children.Add(sideCard);

        Grid.SetRow(centerGrid, 1);
        grid.Children.Add(centerGrid);

        RefreshPolicies();

        return new TabItem
        {
            Header = "路由设置",
            Content = grid,
        };
    }

    // ==================== Tab 3: 备份与还原 UI ====================
    private TabItem BuildBackupTab()
    {
        var page = CreateTabPage("备份与还原", out var stack);

        stack.Children.Add(FluentUi.SectionHeader("配置导出备份 (Local Backup)", SymbolRegular.FolderZip20));
        var btnExport = FluentUi.ActionButton("导出完整配置备份 (ZIP)...", SymbolRegular.ArrowDownload20, ControlAppearance.Primary, (_, _) => ExportBackup());
        stack.Children.Add(FluentUi.SettingCard("导出 ZIP 备份", btnExport, "将节点配置、SQLite 数据库 (mqvpn.db)、分流路由规则打包压缩为标准 .zip 文件。"));

        stack.Children.Add(FluentUi.SectionHeader("配置本地还原 (Local Restore)", SymbolRegular.ArrowUpload20));
        var btnImport = FluentUi.ActionButton("从备份文件还原 (ZIP)...", SymbolRegular.ArrowUpload20, ControlAppearance.Secondary, async (_, _) => await ImportBackupAsync());
        stack.Children.Add(FluentUi.SettingCard("导入 ZIP 还原", btnImport, "从已有的 ZIP 备份恢复所有节点与配置。覆盖前系统将自动生成紧急安全快照。"));

        stack.Children.Add(FluentUi.SectionHeader("危险操作与重置", SymbolRegular.Warning20));
        var btnReset = FluentUi.ActionButton("恢复出厂默认设置", SymbolRegular.ArrowReset20, ControlAppearance.Danger, async (_, _) => await ResetFactoryDefaultsAsync());
        stack.Children.Add(FluentUi.SettingCard("恢复出厂初始", btnReset, "清空所有自定义节点与规则，将客户端与数据库重置为出厂预设状态（重置前自动创建快照）。"));

        _txtBackupStatus.Margin = new Thickness(4, 16, 4, 0);
        _txtBackupStatus.Foreground = FluentUi.BrandSoftBrush;
        stack.Children.Add(_txtBackupStatus);

        return page;
    }

    private void ExportBackup()
    {
        if (_store is null)
        {
            FluentUi.ShowAlert(this, "独立测试模式下未关联配置目录，无法执行备份。", "提示", isError: true);
            return;
        }

        var sfd = new SaveFileDialog
        {
            Title = "导出 MQVPN 配置备份",
            Filter = "ZIP 压缩文件 (*.zip)|*.zip",
            FileName = $"mqvpn_backup_{DateTime.Now:yyyyMMdd_HHmmss}.zip",
        };
        if (sfd.ShowDialog() != true)
            return;

        if (_store.ExportBackup(sfd.FileName))
        {
            var len = new FileInfo(sfd.FileName).Length;
            _txtBackupStatus.Text = $"✅ 成功导出备份至: {sfd.FileName} ({len / 1024d:0.0} KB)";
            FluentUi.ShowAlert(this, $"配置已成功导出为 ZIP 备份！\n路径: {sfd.FileName}\n大小: {len / 1024d:0.0} KB", "备份成功");
        }
        else
        {
            FluentUi.ShowAlert(this, "导出备份失败，请检查文件写入权限。", "备份失败", isError: true);
        }
    }

    private async Task ImportBackupAsync()
    {
        if (_store is null)
        {
            FluentUi.ShowAlert(this, "独立测试模式下未关联配置目录，无法执行还原。", "提示", isError: true);
            return;
        }

        var ofd = new OpenFileDialog
        {
            Title = "选择 MQVPN 配置备份 ZIP 文件",
            Filter = "ZIP 压缩文件 (*.zip)|*.zip",
        };
        if (ofd.ShowDialog() != true)
            return;

        if (!FluentUi.Confirm(this, $"确定要从以下备份文件还原配置吗？\n{ofd.FileName}\n\n还原操作将覆盖当前节点与数据库设置（还原前系统将自动生成紧急安全备份）。", "确认配置还原"))
            return;

        try
        {
            if (_beforeRestore is not null) await _beforeRestore();
            if (_store.ImportBackup(ofd.FileName))
            {
                _txtBackupStatus.Text = $"✅ 成功从备份还原: {Path.GetFileName(ofd.FileName)}";
                FluentUi.ShowAlert(this, "配置已成功还原！页面参数与路由设置已重新载入。", "还原成功");
                ReloadFromStore();
                _storeCallback?.Invoke(_store);
                Close();
            }
            else
            {
                FluentUi.ShowAlert(this, "还原备份失败，备份文件可能已损坏或格式不兼容。", "还原失败", isError: true);
            }
        }
        catch (Exception ex)
        {
            FluentUi.ShowAlert(this, ex.Message, "还原失败", isError: true);
        }
    }

    private async Task ResetFactoryDefaultsAsync()
    {
        if (_store is null)
        {
            FluentUi.ShowAlert(this, "独立测试模式下不支持出厂重置。", "提示", isError: true);
            return;
        }

        if (!FluentUi.Confirm(this, "⚠️ 确定要将所有节点配置、分流路由规则与全局设置恢复为出厂默认状态吗？\n\n此操作不可逆，但系统在执行前会自动在 config/backups 下生成安全快照。", "确认恢复出厂设置"))
            return;

        try
        {
            if (_beforeRestore is not null) await _beforeRestore();
            _store.ResetToFactoryDefaults();
        }
        catch (Exception ex)
        {
            FluentUi.ShowAlert(this, ex.Message, "重置失败", isError: true);
            return;
        }
        _txtBackupStatus.Text = "⚠️ 已成功恢复出厂默认设置。";
        FluentUi.ShowAlert(this, "已成功恢复出厂预设！", "重置完成");
        ReloadFromStore();
        _storeCallback?.Invoke(_store);
        Close();
    }

    private void ReloadFromStore()
    {
        if (_store is null) return;
        _routing.Policies.Clear();
        _routing.Policies.AddRange(_store.Routing.Policies);
        RefreshPolicies();

        _txtDirectDns.Text = _store.State.DirectDns;
        _txtRemoteDns.Text = _store.State.RemoteDns;
        _txtBootstrapDns.Text = _store.State.BootstrapDns;
        _chkFakeDns.IsChecked = _store.State.FakeDnsEnabled;
        _txtFakeDnsRange.Text = _store.State.FakeDnsRange;
        _chkBlockAAAA.IsChecked = _store.State.BlockAAAAQuery;
        _txtCustomHosts.Text = _store.State.CustomHosts;

        _numSocksPort.Value = _store.State.LocalSocksPort;
        _numHttpPort.Value = _store.State.LocalHttpPort;
        _chkEnableUdp.IsChecked = _store.State.EnableUdp;
        _chkEnableSniffing.IsChecked = _store.State.EnableSniffing;
        _chkRouteOnly.IsChecked = _store.State.RouteOnly;
    }

    // ==================== 路由控制逻辑 ====================
    private RoutingPolicy? CurrentPolicy =>
        _policies.SelectedItem is RoutingPolicy policy ? policy : null;

    private void InitRoutingControls()
    {
        FluentUi.StyleDataGrid(_rules);
        _rules.AutoGenerateColumns = false;
        _rules.CanUserAddRows = false;
        _rules.CanUserDeleteRows = false;
        _rules.IsReadOnly = false;
        _rules.SelectionMode = DataGridSelectionMode.Single;
        _rules.HeadersVisibility = DataGridHeadersVisibility.Column;
        _rules.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "启用",
            Binding = new Binding(nameof(RoutingRuleViewModel.Enabled)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = 56,
        });
        _rules.Columns.Add(new DataGridTextColumn
        {
            Header = "规则名称",
            Binding = new Binding(nameof(RoutingRuleViewModel.Name)),
            IsReadOnly = true,
            Width = 160,
        });
        _rules.Columns.Add(new DataGridTextColumn
        {
            Header = "类型",
            Binding = new Binding(nameof(RoutingRuleViewModel.TypeDisplay)),
            IsReadOnly = true,
            Width = 110,
        });
        _rules.Columns.Add(new DataGridTextColumn
        {
            Header = "匹配目标",
            Binding = new Binding(nameof(RoutingRuleViewModel.Value)),
            IsReadOnly = true,
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
        });
        _rules.Columns.Add(new DataGridTextColumn
        {
            Header = "网络",
            Binding = new Binding(nameof(RoutingRuleViewModel.Network)),
            IsReadOnly = true,
            Width = 75,
        });
        _rules.Columns.Add(new DataGridTextColumn
        {
            Header = "出站动作",
            Binding = new Binding(nameof(RoutingRuleViewModel.Outbound)),
            IsReadOnly = true,
            Width = 100,
        });
        _rules.MouseDoubleClick += (_, _) =>
        {
            if (_rules.SelectedItem is RoutingRuleViewModel)
                EditRule();
        };
        _rules.ItemsSource = _ruleItems;
    }

    private void RefreshPolicies()
    {
        _updatingPolicy = true;
        var selectedId = CurrentPolicy?.Id;
        _policies.ItemsSource = null;
        _policies.DisplayMemberPath = nameof(RoutingPolicy.Name);
        _policies.ItemsSource = _routing.Policies.ToList();
        var selected = _routing.Policies.FirstOrDefault(p => p.Id == selectedId)
            ?? _routing.Policies.FirstOrDefault();
        _policies.SelectedItem = selected;
        _updatingPolicy = false;
        RefreshRules();
    }

    private void RefreshRules()
    {
        _ruleItems.Clear();
        var policy = CurrentPolicy;
        if (policy is null)
        {
            _finalOutbound.SelectedItem = null;
            return;
        }

        _updatingPolicy = true;
        _finalOutbound.SelectedItem = policy.FinalOutbound;
        _updatingPolicy = false;

        foreach (var rule in policy.Rules)
            _ruleItems.Add(new RoutingRuleViewModel(rule));
    }

    private void AddPolicy()
    {
        using var prompt = new TextPromptForm("新建策略", "请输入新分流策略名称：");
        if (!prompt.ShowModal(this))
            return;
        var name = prompt.Value;
        if (string.IsNullOrWhiteSpace(name))
            return;
        var policy = new RoutingPolicy { Name = name.Trim(), FinalOutbound = "DIRECT" };
        _routing.Policies.Add(policy);
        RefreshPolicies();
        _policies.SelectedItem = policy;
    }

    private void ClonePolicy()
    {
        if (CurrentPolicy is not { } policy) return;
        var cloned = JsonSerializer.Deserialize<RoutingPolicy>(JsonSerializer.Serialize(policy));
        if (cloned is null) return;
        cloned.Id = Guid.NewGuid().ToString("N");
        cloned.Name = $"{policy.Name} (副本)";
        _routing.Policies.Add(cloned);
        RefreshPolicies();
        _policies.SelectedItem = cloned;
    }

    private void DeletePolicy()
    {
        if (CurrentPolicy is not { } policy) return;
        if (_routing.Policies.Count <= 1)
        {
            FluentUi.ShowAlert(this, "必须保留至少一个分流策略。", "不可删除", isError: true);
            return;
        }
        if (!FluentUi.Confirm(this, $"确认删除策略【{policy.Name}】？", "删除策略"))
            return;
        _routing.Policies.Remove(policy);
        RefreshPolicies();
    }

    private void AddRule()
    {
        if (CurrentPolicy is not { } policy) return;
        using var editor = new RoutingRuleForm(new RoutingRule());
        if (!editor.ShowModal(this) || editor.Result is not { } rule)
            return;
        policy.Rules.Add(rule);
        RefreshRules();
        _rules.SelectedIndex = _ruleItems.Count - 1;
    }

    private void EditRule()
    {
        if (CurrentPolicy is not { } policy || _rules.SelectedItem is not RoutingRuleViewModel selected)
            return;
        var index = policy.Rules.IndexOf(selected.Model);
        if (index < 0) return;
        using var editor = new RoutingRuleForm(selected.Model);
        if (!editor.ShowModal(this) || editor.Result is not { } updated)
            return;
        policy.Rules[index] = updated;
        RefreshRules();
        _rules.SelectedIndex = index;
    }

    private void DeleteRule()
    {
        if (CurrentPolicy is not { } policy || _rules.SelectedItem is not RoutingRuleViewModel selected)
            return;
        var index = policy.Rules.IndexOf(selected.Model);
        if (index < 0) return;
        policy.Rules.RemoveAt(index);
        RefreshRules();
        if (_ruleItems.Count > 0)
            _rules.SelectedIndex = Math.Clamp(index, 0, _ruleItems.Count - 1);
    }

    private void MoveRule(int delta)
    {
        if (CurrentPolicy is not { } policy || _rules.SelectedItem is not RoutingRuleViewModel selected)
            return;
        var index = policy.Rules.IndexOf(selected.Model);
        if (index < 0) return;
        var target = index + delta;
        if (target < 0 || target >= policy.Rules.Count) return;
        policy.Rules.RemoveAt(index);
        policy.Rules.Insert(target, selected.Model);
        RefreshRules();
        _rules.SelectedIndex = target;
    }

    private void ResetPresetPolicies()
    {
        if (!FluentUi.Confirm(this, "确认重新载入并重置 v2rayN 官方 V4 经典分流规则吗？", "重置预设"))
            return;
        var defaultDoc = ProfileStore.CreateDefaultRouting();
        _routing.Policies.Clear();
        _routing.Policies.AddRange(defaultDoc.Policies);
        RefreshPolicies();
        FluentUi.ShowAlert(this, "预设分流策略已重置！", "完成");
    }

    // ==================== 保存并持久化 ====================
    private void SaveAndClose()
    {
        // 1. 保存常规参数
        SetAutoStartRegistration(_chkAutoStart.IsChecked == true);
        _state.StartMinimized = _chkStartMinimized.IsChecked == true;
        _state.MinimizeToTrayOnClose = _chkMinimizeToTray.IsChecked == true;
        _state.ConfirmOnExit = _chkConfirmOnExit.IsChecked == true;
        _state.DisplayRealTimeSpeed = _chkDisplayRealTimeSpeed.IsChecked == true;

        _state.ThemeMode = _cboThemeMode.SelectedIndex switch
        {
            1 => "light",
            2 => "dark",
            _ => "system",
        };
        _state.DoubleClickToActivate = _cboDoubleClickAction.SelectedIndex == 1;

        _state.LogLevel = _cboLogLevel.SelectedItem?.ToString() ?? LogLevels.Default;
        _state.AllowLan = _chkAllowLan.IsChecked == true;
        _state.LocalSocksPort = (int)(_numSocksPort.Value ?? 10808);
        _state.LocalHttpPort = (int)(_numHttpPort.Value ?? 10809);
        _state.EnableUdp = _chkEnableUdp.IsChecked == true;
        _state.EnableSniffing = _chkEnableSniffing.IsChecked == true;
        _state.RouteOnly = _chkRouteOnly.IsChecked == true;

        _state.LatencyProbeEndpoint = string.IsNullOrWhiteSpace(_txtProbeEndpoint.Text) ? "1.1.1.1:80" : _txtProbeEndpoint.Text.Trim();
        _state.SpeedTestTimeoutMs = (int)(_numSpeedTestTimeout.Value ?? 3000);
        _state.SpeedTestUrl = string.IsNullOrWhiteSpace(_txtSpeedTestUrl.Text) ? "http://cachefly.cachefly.net/10mb.test" : _txtSpeedTestUrl.Text.Trim();
        _state.MixedConcurrencyCount = (int)(_numMixedConcurrency.Value ?? 5);

        _state.AutoReconnect = _chkAutoReconnect.IsChecked == true;
        _state.ReconnectIntervalSec = (int)(_numReconnectInterval.Value ?? 5);

        _state.GuiUpdateSource = string.IsNullOrWhiteSpace(_txtGuiSource.Text) ? "r11234567/mqvpn" : _txtGuiSource.Text.Trim();
        _state.MqvpnUpdateSource = string.IsNullOrWhiteSpace(_txtMqvpnSource.Text) ? "r11234567/mqvpn" : _txtMqvpnSource.Text.Trim();
        _state.XrayUpdateSource = string.IsNullOrWhiteSpace(_txtXraySource.Text) ? "XTLS/Xray-core" : _txtXraySource.Text.Trim();
        _state.GeoDataUpdateSource = string.IsNullOrWhiteSpace(_txtGeodataSource.Text) ? "Loyalsoldier/v2ray-rules-dat" : _txtGeodataSource.Text.Trim();
        _state.AutoCheckUpdatesOnStartup = _chkAutoCheckUpdate.IsChecked == true;

        var pre = _chkPreRelease.IsChecked == true;
        _state.GuiIncludePreRelease = pre;
        _state.MqvpnIncludePreRelease = pre;
        _state.XrayIncludePreRelease = pre;

        // 2. 保存 DNS 设置
        _state.DirectDns = _txtDirectDns.Text.Trim();
        _state.RemoteDns = _txtRemoteDns.Text.Trim();
        _state.BootstrapDns = _txtBootstrapDns.Text.Trim();
        _state.DomainStrategy = _cboDomainStrategy.SelectedIndex switch
        {
            1 => "IPOnDemand",
            2 => "AsIs",
            _ => "IPIfNonMatch",
        };
        _state.FakeDnsEnabled = _chkFakeDns.IsChecked == true;
        _state.FakeDnsRange = string.IsNullOrWhiteSpace(_txtFakeDnsRange.Text) ? "198.18.0.0/15" : _txtFakeDnsRange.Text.Trim();
        _state.BlockAAAAQuery = _chkBlockAAAA.IsChecked == true;
        _state.CustomHosts = _txtCustomHosts.Text.Trim();

        // 3. 保存 路由设置
        if (_store is not null)
        {
            _store.ReplaceRouting(_routing);
            _storeCallback?.Invoke(_store);
        }
        else
        {
            _stateCallback?.Invoke(_state);
        }

        DialogResult = true;
        Close();
    }

    private static TabItem CreateTabPage(string title, out StackPanel stack)
    {
        stack = new StackPanel
        {
            Margin = new Thickness(4, 6, 12, 16),
        };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = stack,
        };
        return new TabItem
        {
            Header = title,
            Content = scroll,
        };
    }

    private static Wpf.Ui.Controls.Button MakeSideButton(string text, SymbolRegular icon, ControlAppearance appearance, RoutedEventHandler onClick)
    {
        var btn = FluentUi.ActionButton(text, icon, appearance, onClick);
        btn.Width = 120;
        btn.HorizontalAlignment = HorizontalAlignment.Center;
        btn.Margin = new Thickness(0, 0, 0, 8);
        return btn;
    }

    private static void ConfigureNumber(Wpf.Ui.Controls.NumberBox box, double min, double max, double value)
    {
        box.Minimum = min;
        box.Maximum = max;
        box.Value = Math.Clamp(value, min, max);
        box.SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline;
        box.Width = 140;
    }

    private static bool CheckAutoStartRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
            return key?.GetValue(AppRegistryName) is string;
        }
        catch
        {
            return false;
        }
    }

    private static void SetAutoStartRegistration(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
            if (key is null) return;
            if (enable)
            {
                var exePath = Environment.ProcessPath ?? string.Empty;
                if (!string.IsNullOrEmpty(exePath))
                    key.SetValue(AppRegistryName, $"\"{exePath}\"");
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

    public void Dispose()
    {
    }
}
