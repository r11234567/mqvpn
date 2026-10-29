using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace TruckersMP.Proxy;

internal sealed partial class ProfileEditorForm : FluentWindow, IDisposable
{
    private readonly Wpf.Ui.Controls.TextBox _name = new() { PlaceholderText = "例如：欧洲节点 01" };
    private readonly Wpf.Ui.Controls.TextBox _server = new() { PlaceholderText = "host:port 或 [ipv6]:port" };
    private readonly Wpf.Ui.Controls.TextBox _sni = new() { PlaceholderText = "留空则使用服务器主机名" };
    private readonly Wpf.Ui.Controls.PasswordBox _auth = new() { PlaceholderText = "输入服务端预共享密钥 (PSK)" };
    private readonly Wpf.Ui.Controls.TextBox _dns = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72 };
    private readonly StackPanel _pathsPanel = new();
    private readonly List<CheckBox> _pathBoxes = [];
    private readonly ComboBox _scheduler = new();
    private readonly ComboBox _congestion = new();
    private readonly ComboBox _routing = new();
    private readonly NumberBox _reconnectInterval = new();
    private readonly NumberBox _initMaxPathId = new();
    private readonly CheckBox _allowInsecure = new();
    private readonly CheckBox _reconnect = new();
    private readonly CheckBox _requestServerScheduler = new();
    private readonly CheckBox _redundant = new();
    private readonly Wpf.Ui.Controls.TextBox _redundantPorts = new() { PlaceholderText = "例如：4200,9000 (留空表示复制全部 UDP)" };

    private readonly Wpf.Ui.Controls.TextBox _tunName = new();
    private readonly ComboBox _logLevel = new();
    private readonly CheckBox _manageRoutes = new();
    private readonly CheckBox _killSwitch = new();
    private readonly CheckBox _automaticMtu = new();
    private readonly NumberBox _mtu = new();
    private readonly CheckBox _pmtud = new();
    private readonly NumberBox _pathMtu = new();
    private readonly NumberBox _tunQueueLength = new();
    private readonly NumberBox _tunReadBatch = new();
    private readonly ComboBox _reinjection = new();
    private readonly NumberBox _reinjectionFactor = new();
    private readonly NumberBox _reinjectionHardDeadline = new();
    private readonly NumberBox _reinjectionLowerBound = new();
    private readonly CheckBox _hybridEnabled = new();
    private readonly ComboBox _hybridTcp = new();
    private readonly NumberBox _tcpMaxFlows = new();
    private readonly NumberBox _tcpIdleTimeout = new();
    private readonly NumberBox _tcpConnectTimeout = new();
    private readonly CheckBox _reorderEnabled = new();
    private readonly Wpf.Ui.Controls.DataGrid _reorderRules = new();
    private readonly CheckBox _reorderCustomWait = new();
    private readonly CheckBox _reorderCustomCap = new();
    private readonly NumberBox _reorderMaxWait = new();
    private readonly NumberBox _reorderCapPackets = new();
    private readonly NumberBox _reorderMaxBytesPerFlow = new();
    private readonly NumberBox _reorderClassifyWindow = new();
    private readonly NumberBox _reorderAckDemoteMaxLarge = new();
    private readonly NumberBox _reorderSmallPacketThreshold = new();
    private readonly NumberBox _reorderResetMarkPackets = new();
    private readonly NumberBox _reorderResetIdleGrace = new();
    private readonly NumberBox _reorderMaxFlows = new();
    private readonly NumberBox _reorderGlobalMaxBytes = new();
    private readonly NumberBox _reorderIngressIdle = new();
    private readonly NumberBox _reorderEgressIdle = new();
    private readonly NumberBox _recvRateLimit = new();
    private readonly CheckBox _udpGso = new();
    private readonly CheckBox _udpGro = new();
    private readonly CheckBox _tunOffload = new();
    private readonly CheckBox _tunGro = new();
    private readonly CheckBox _autoConnect = new();
    private readonly TabControl _tabs = new();
    private readonly JsonObject _config;

    public ProfileEditorForm(
        string title,
        string profileName,
        JsonObject config,
        IReadOnlyList<RoutingPolicy> policies,
        string routingPolicyId,
        bool autoConnect = false,
        bool useDefaultPath = false)
    {
        FluentUi.ConfigureWindow(this, title, 840, 780, 740, 620);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _config = config.DeepClone().AsObject();

        InitializeValues(profileName, policies, routingPolicyId, autoConnect, useDefaultPath);

        var root = FluentUi.CreateWindowRootGrid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleBar = FluentUi.CreateTitleBar(title, showMaximize: true, showMinimize: false);
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        FluentUi.StyleTabControl(_tabs);
        _tabs.Margin = new Thickness(18, 8, 18, 8);
        _tabs.Items.Add(BuildBasicPage());
        _tabs.Items.Add(BuildTransportPage());
        _tabs.Items.Add(BuildReorderPage());
        _tabs.Items.Add(BuildAdvancedPage());
        Grid.SetRow(_tabs, 1);
        root.Children.Add(_tabs);

        var footer = new Border
        {
            Padding = new Thickness(20, 12, 20, 14),
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        footer.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorSecondaryBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancel = FluentUi.ActionButton("取消", SymbolRegular.Dismiss20, ControlAppearance.Secondary, (_, _) =>
        {
            DialogResult = false;
            Close();
        });
        cancel.MinWidth = 96;
        cancel.Margin = new Thickness(0, 0, 10, 0);

        var save = FluentUi.ActionButton("保存配置", SymbolRegular.Checkmark20, ControlAppearance.Primary, (_, _) => SaveAndClose());
        save.MinWidth = 112;

        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        footer.Child = buttons;
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
    }

    public string ProfileName { get; private set; } = string.Empty;
    public string RoutingPolicyId { get; private set; } = string.Empty;
    public bool AutoConnect { get; private set; }
    public bool UseDefaultPath { get; private set; }
    public JsonObject ResultConfig => _config;
    public TabControl TabsControl => _tabs;

    public bool ShowModal(Window? owner)
    {
        if (owner is { IsVisible: true })
            Owner = owner;
        return ShowDialog() == true;
    }

    public void Dispose()
    {
        if (IsVisible)
            Close();
    }

    private void InitializeValues(
        string profileName,
        IReadOnlyList<RoutingPolicy> policies,
        string routingPolicyId,
        bool autoConnect,
        bool useDefaultPath)
    {
        _name.Text = profileName;
        _server.Text = GetString("server_addr");
        _sni.Text = GetString("tls_server_name");
        _auth.Password = GetString("auth_key");

        _allowInsecure.IsChecked = GetBoolean("insecure", false);
        _allowInsecure.Content = "允许不安全连接（跳过 TLS 证书验证）";

        _dns.Text = string.Join(Environment.NewLine, GetStringArray("dns"));

        var selectedPaths = useDefaultPath
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : GetStringArray("paths").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                              adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
                              adapter.GetIPProperties().UnicastAddresses.Count > 0 &&
                              !adapter.Name.StartsWith("mqvpn", StringComparison.OrdinalIgnoreCase) &&
                              !adapter.Name.StartsWith("xray", StringComparison.OrdinalIgnoreCase) &&
                              !adapter.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase))
            .Select(adapter => adapter.Name)
            .Concat(selectedPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        _pathsPanel.Children.Clear();
        _pathBoxes.Clear();
        foreach (var adapter in adapters)
        {
            var cb = new CheckBox
            {
                Content = adapter,
                Tag = adapter,
                IsChecked = selectedPaths.Contains(adapter),
                Margin = new Thickness(0, 3, 12, 3),
            };
            FluentUi.StyleCheckBox(cb);
            _pathBoxes.Add(cb);
            _pathsPanel.Children.Add(cb);
        }

        ConfigureCombo(_scheduler, ["wlb", "minrtt", "wlb_udp_pin", "backup_fec"], GetString("scheduler", "wlb"));
        _requestServerScheduler.Content = "请求服务端对齐调度器 (RequestServerScheduler)";
        _requestServerScheduler.IsChecked = GetBoolean("request_server_scheduler", false);
        ConfigureCombo(_congestion, ["bbr2", "bbr", "cubic", "none"], GetString("cc", "bbr2"));
        ConfigureNumber(_initMaxPathId, 0, uint.MaxValue, GetLong("init_max_path_id", 0));

        _routing.DisplayMemberPath = nameof(RoutingPolicy.Name);
        _routing.SelectedValuePath = nameof(RoutingPolicy.Id);
        _routing.ItemsSource = policies.ToList();
        _routing.SelectedValue = routingPolicyId;
        if (_routing.SelectedIndex < 0 && _routing.Items.Count > 0)
            _routing.SelectedIndex = 0;

        _reconnect.IsChecked = GetBoolean("reconnect", true);
        _reconnect.Content = "断线自动重连";
        ConfigureNumber(_reconnectInterval, 1, int.MaxValue, GetInteger("reconnect_interval", 5));
        _reconnect.Checked += (_, _) => _reconnectInterval.IsEnabled = true;
        _reconnect.Unchecked += (_, _) => _reconnectInterval.IsEnabled = false;
        _reconnectInterval.IsEnabled = _reconnect.IsChecked == true;

        _tunName.Text = GetString("tun_name", "mqvpn0");
        ConfigureCombo(_logLevel, LogLevels.All, GetString("log_level", LogLevels.Default));
        _manageRoutes.Content = "由 MQVPN 管理系统路由";
        _manageRoutes.IsChecked = GetBoolean("manage_routes", false);
        _killSwitch.Content = "Kill Switch（断线时阻止流量绕过 VPN）";
        _killSwitch.IsChecked = GetBoolean("kill_switch", false);

        var configuredMtu = GetInteger("mtu", GetInteger("tun_mtu", 0));
        _automaticMtu.Content = "自动协商 MTU（推荐）";
        _automaticMtu.IsChecked = configuredMtu == 0;
        ConfigureNumber(_mtu, 1280, 9000, configuredMtu == 0 ? 1382 : configuredMtu);
        _automaticMtu.Checked += (_, _) => _mtu.IsEnabled = false;
        _automaticMtu.Unchecked += (_, _) => _mtu.IsEnabled = true;
        _mtu.IsEnabled = _automaticMtu.IsChecked != true;

        _pmtud.Content = "启用路径 MTU 动态发现 (PMTUD)";
        _pmtud.IsChecked = GetBoolean("pmtud", false);
        ConfigureNumber(_pathMtu, 0, 65535, GetInteger("path_mtu", 0));

        ConfigureNumber(_tunQueueLength, 0, 65536, GetInteger("tx_queue_len", 0));
        ConfigureNumber(_tunReadBatch, 0, 4096, GetInteger("tun_read_batch", 0));

        ConfigureCombo(_reinjection, ["off", "deadline", "idle"], GetString("reinjection", "off"));
        ConfigureNumber(_reinjectionFactor, 100, 1000, GetInteger("reinjection_srtt_factor_pct", 110));
        ConfigureNumber(_reinjectionHardDeadline, 1, 60000, GetInteger("reinjection_hard_deadline_ms", 500));
        ConfigureNumber(_reinjectionLowerBound, 1, 60000, GetInteger("reinjection_deadline_lower_bound_ms", 20));
        _reinjection.SelectionChanged += (_, _) => UpdateDependentControls();

        _redundant.Content = "启用 Redundant 冗余模式（实时多路径发送复制包）";
        _redundant.IsChecked = GetBoolean("redundant", false);
        _redundantPorts.Text = GetString("redundant_ports");
        _redundant.Checked += (_, _) =>
        {
            UpdateDependentControls();
            if (string.Equals(_congestion.SelectedItem?.ToString(), "bbr2", StringComparison.OrdinalIgnoreCase))
                _congestion.SelectedItem = "bbr";
        };
        _redundant.Unchecked += (_, _) => UpdateDependentControls();

        var hybrid = GetObject("hybrid");
        _hybridEnabled.Content = "启用 Hybrid TCP lane";
        _hybridEnabled.IsChecked = GetBoolean(hybrid, "enabled", false);
        ConfigureCombo(_hybridTcp, ["auto", "stream", "raw"], GetString(hybrid, "tcp", "auto"));
        ConfigureNumber(_tcpMaxFlows, 1, 4096, GetInteger(hybrid, "tcp_max_flows", 256));
        ConfigureNumber(_tcpIdleTimeout, 0, uint.MaxValue, GetLong(hybrid, "tcp_idle_timeout_sec", 300));
        ConfigureNumber(_tcpConnectTimeout, 1, uint.MaxValue, GetLong(hybrid, "tcp_connect_timeout_sec", 10));
        _hybridEnabled.Checked += (_, _) => UpdateDependentControls();
        _hybridEnabled.Unchecked += (_, _) => UpdateDependentControls();

        var reorder = GetObject("reorder");
        InitializeReorderRules(reorder);
        ConfigureNumber(_reorderMaxWait, 0, uint.MaxValue, GetLong(reorder, "max_wait_ms", 30));
        ConfigureNumber(_reorderCapPackets, 1, uint.MaxValue, GetLong(reorder, "cap_packets", 1024));
        ConfigureNumber(_reorderMaxBytesPerFlow, 0, ulong.MaxValue, GetUnsignedLong(reorder, "max_bytes_per_flow", 1572864));
        ConfigureNumber(_reorderClassifyWindow, 0, ushort.MaxValue, GetLong(reorder, "classify_window", 64));
        ConfigureNumber(_reorderAckDemoteMaxLarge, 0, ushort.MaxValue, GetLong(reorder, "ack_demote_max_large", 3));
        ConfigureNumber(_reorderSmallPacketThreshold, 0, uint.MaxValue, GetLong(reorder, "small_packet_threshold", 200));
        ConfigureNumber(_reorderResetMarkPackets, 0, uint.MaxValue, GetLong(reorder, "reset_mark_packets", 8));
        ConfigureNumber(_reorderResetIdleGrace, 0, uint.MaxValue, GetLong(reorder, "reset_idle_grace_ms", 10000));
        ConfigureNumber(_reorderMaxFlows, 0, uint.MaxValue, GetLong(reorder, "max_flows", 65536));
        ConfigureNumber(_reorderGlobalMaxBytes, 0, ulong.MaxValue, GetUnsignedLong(reorder, "global_max_bytes", 67108864));
        ConfigureNumber(_reorderIngressIdle, 0, uint.MaxValue, GetLong(reorder, "ingress_idle_sec", 30));
        ConfigureNumber(_reorderEgressIdle, 0, uint.MaxValue, GetLong(reorder, "egress_idle_sec", 300));

        var advanced = GetObject("advanced");
        ConfigureNumber(_recvRateLimit, 0, 10_000_000_000d, GetLong(advanced, "recv_rate_limit", 0));
        _recvRateLimit.SmallChange = 1_000_000;

        _udpGso.Content = "UDP GSO 批量发送";
        _udpGso.IsChecked = GetBoolean(advanced, "udp_gso", true);
        _udpGro.Content = "UDP GRO 合并接收";
        _udpGro.IsChecked = GetBoolean(advanced, "udp_gro", true);
        _tunOffload.Content = "TUN 批量读取卸载 (TunOffload)";
        _tunOffload.IsChecked = GetBoolean(advanced, "tun_offload", true);
        _tunGro.Content = "TUN 接收合并 (TunGro)";
        _tunGro.IsChecked = GetBoolean(advanced, "tun_gro", true);

        _autoConnect.Content = "GUI 启动后自动连接此配置";
        _autoConnect.IsChecked = autoConnect;
        UpdateDependentControls();
    }

    private TabItem BuildBasicPage()
    {
        var page = CreatePage("基础设置", out var stack);
        stack.Children.Add(FluentUi.SectionHeader("连接与鉴权", SymbolRegular.Server20));
        stack.Children.Add(FluentUi.SettingCard("配置名称", _name));
        stack.Children.Add(FluentUi.SettingCard("服务器地址", _server, "格式为 HOST:PORT；IPv6 使用 [地址]:端口。"));
        stack.Children.Add(FluentUi.SettingCard("TLS 服务器名称 (SNI)", _sni, "可留空；留空时使用服务器地址中的主机名验证证书。"));
        stack.Children.Add(FluentUi.SettingCard("认证密钥 (PSK)", _auth));
        stack.Children.Add(FluentUi.SettingCard("TLS 证书校验", _allowInsecure, "默认关闭不安全模式。仅自签名证书需要开启。"));

        stack.Children.Add(FluentUi.SectionHeader("多路径与选路策略", SymbolRegular.Router20));
        var pathsBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            MaxHeight = 120,
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _pathsPanel,
            },
        };
        pathsBorder.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        pathsBorder.SetResourceReference(Border.BorderBrushProperty, "ControlElevationBorderBrush");
        stack.Children.Add(FluentUi.SettingCard("多路径物理网卡 (Paths)", pathsBorder, "可同时勾选多张网卡聚合加速；不勾选时由内核自动绑定默认物理路由。"));
        stack.Children.Add(FluentUi.SettingCard("多路径调度器", _scheduler));
        stack.Children.Add(FluentUi.SettingCard("请求服务端对齐调度器", _requestServerScheduler, "向服务端协商使用本客户端的调度器算法作为下行流量调度策略。"));
        stack.Children.Add(FluentUi.SettingCard("拥塞控制算法 (CC)", _congestion, "CC 与多路径调度器相互独立。Redundant 模式下推荐使用 bbr。"));
        stack.Children.Add(FluentUi.SettingCard("初始最大 Path ID", _initMaxPathId, "InitMaxPathId：0 使用 xquic 默认值 8；非零范围 1-4294967295。"));
        stack.Children.Add(FluentUi.SettingCard("路由分流策略", _routing));
        stack.Children.Add(FluentUi.SettingCard("DNS 服务器", _dns, "每行或用逗号分隔，最多 4 个 IPv4/IPv6 DNS 地址。"));
        stack.Children.Add(FluentUi.SettingCard("自动重连", _reconnect, "启用后按基础间隔重试，并由内核指数退避到最多 60 秒。"));
        stack.Children.Add(FluentUi.SettingCard("重连基础间隔（秒）", _reconnectInterval));
        return page;
    }

    private TabItem BuildTransportPage()
    {
        var page = CreatePage("传输增强", out var stack);
        stack.Children.Add(FluentUi.SectionHeader("Redundant 冗余模式 (0.19.0 新增)", SymbolRegular.Stream20));
        stack.Children.Add(FluentUi.SettingCard("冗余模式开关", _redundant, "在多个活动路径上同时复制发送 UDP 数据包，保障 SRT 等直播流在链路瞬断时不中断。"));
        stack.Children.Add(FluentUi.SettingCard("复制 UDP 目的端口", _redundantPorts, "逗号分隔的端口列表（最多 16 个），留空表示复制全部 UDP。启用时推荐拥塞控制使用 bbr。"));

        stack.Children.Add(FluentUi.SectionHeader("Reinjection 重注入", SymbolRegular.Flash20));
        stack.Children.Add(FluentUi.SettingCard("重注入模式", _reinjection, "off：关闭；deadline：按时限复制未确认包；idle：路径空闲时重注入。旧版 dgram 已由 Redundant 冗余模式替代。"));
        stack.Children.Add(FluentUi.SettingCard("SRTT 系数（%）", _reinjectionFactor, "仅 deadline 模式生效，范围 100-1000，默认 110。"));
        stack.Children.Add(FluentUi.SettingCard("硬截止时间（ms）", _reinjectionHardDeadline, "仅 deadline 模式生效，默认 500 ms。"));
        stack.Children.Add(FluentUi.SettingCard("截止下限（ms）", _reinjectionLowerBound, "仅 deadline 模式生效，默认 20 ms。"));

        stack.Children.Add(FluentUi.SectionHeader("Hybrid TCP Lane", SymbolRegular.PlugConnected20));
        stack.Children.Add(FluentUi.SettingCard("Hybrid 增强", _hybridEnabled, "服务端也必须启用 Hybrid。auto 仅在连接建立时已有至少两条活动路径才使用 TCP lane。"));
        stack.Children.Add(FluentUi.SettingCard("TCP 传输模式", _hybridTcp, "auto：多路径时使用 stream；stream：始终使用 TCP lane；raw：始终走原始 CONNECT-IP 数据报。"));
        stack.Children.Add(FluentUi.SettingCard("最大 TCP 流", _tcpMaxFlows, "Windows/桌面构建上限 4096；默认 256。服务端也应配置足够的会话流上限。"));
        stack.Children.Add(FluentUi.SettingCard("TCP 空闲超时（秒）", _tcpIdleTimeout, "0 表示不按空闲时间回收。"));
        stack.Children.Add(FluentUi.SettingCard("TCP 连接超时（秒）", _tcpConnectTimeout, "服务端 Hybrid 出站 connect() 超时；超时后客户端收到 HTTP 504。默认 10 秒。"));

        stack.Children.Add(FluentUi.SectionHeader("Linux 内核网络与虚拟网卡卸载", SymbolRegular.TopSpeed20));
        stack.Children.Add(FluentUi.SettingCard("UDP GSO", _udpGso, "0.16 新增，默认开启。仅 Linux 生效；不支持分段的路径会自动回退到 sendmmsg。"));
        stack.Children.Add(FluentUi.SettingCard("UDP GRO", _udpGro, "0.16 新增，默认开启。仅 Linux 生效；Windows 客户端保留此配置但不会使用。"));
        stack.Children.Add(FluentUi.SettingCard("TUN Offload", _tunOffload, "0.19 新增，默认开启。仅 Linux 生效；批量读取 TUN 报文提升高吞吐效率。"));
        stack.Children.Add(FluentUi.SettingCard("TUN GRO", _tunGro, "0.19 新增，默认开启。仅 Linux 生效；Linux 6.2+ 内核合并写入 TUN。"));
        return page;
    }

    private TabItem BuildAdvancedPage()
    {
        var page = CreatePage("高级设置", out var stack);
        stack.Children.Add(FluentUi.SectionHeader("虚拟网卡与安全", SymbolRegular.Shield20));
        stack.Children.Add(FluentUi.SettingCard("TUN 网卡名称", _tunName));
        stack.Children.Add(FluentUi.SettingCard("默认日志等级", _logLevel, "写入配置文件的默认等级；启动时主界面的“日志级别”会覆盖它。"));
        stack.Children.Add(FluentUi.SettingCard("系统路由管理", _manageRoutes, "分流模式通常保持关闭；它与 Kill Switch 是两个独立开关。"));
        stack.Children.Add(FluentUi.SettingCard("断线保护 (Kill Switch)", _killSwitch));
        stack.Children.Add(FluentUi.SettingCard("MTU 协商模式", _automaticMtu));
        stack.Children.Add(FluentUi.SettingCard("自定义 MTU 上限", _mtu, "范围 1280-9000。客户端值仅作为协商 MTU 的上限。"));
        stack.Children.Add(FluentUi.SettingCard("路径 MTU 动态发现 (PMTUD)", _pmtud, "0.19.0 新增。动态探测端到端网络路径支持的最大报文大小，避免分片。需两端同时开启。"));
        stack.Children.Add(FluentUi.SettingCard("固定路径 MTU (PathMTU)", _pathMtu, "0.19.0 新增。固定路径 MTU 大小（1280-9000，0 表示默认 1400 字节）。开启 PMTUD 时作为探测上限。"));
        stack.Children.Add(FluentUi.SettingCard("TUN 队列深度", _tunQueueLength, "TxQueueLen：0 使用内核默认值 500；非零范围 100-65536。仅 Linux 生效。"));
        stack.Children.Add(FluentUi.SettingCard("TUN 读取批量", _tunReadBatch, "TunReadBatch：0 使用内置默认值 64；非零范围 1-4096。仅 Linux 生效。"));

        stack.Children.Add(FluentUi.SectionHeader("客户端行为与限流", SymbolRegular.Settings20));
        stack.Children.Add(FluentUi.SettingCard("接收窗口上限", _recvRateLimit, "RecvRateLimit，单位 bytes/s；0 表示关闭。它用于约束接收窗口和内存，不是精确限速器。"));
        stack.Children.Add(FluentUi.SettingCard("启动自动连接", _autoConnect));
        return page;
    }

    private static TabItem CreatePage(string title, out StackPanel stack)
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

    private void UpdateDependentControls()
    {
        var deadline = string.Equals(_reinjection.SelectedItem?.ToString(), "deadline", StringComparison.Ordinal);
        _reinjectionFactor.IsEnabled = deadline;
        _reinjectionHardDeadline.IsEnabled = deadline;
        _reinjectionLowerBound.IsEnabled = deadline;

        _redundantPorts.IsEnabled = _redundant.IsChecked == true;

        var hybridOn = _hybridEnabled.IsChecked == true;
        _hybridTcp.IsEnabled = hybridOn;
        _tcpMaxFlows.IsEnabled = hybridOn;
        _tcpIdleTimeout.IsEnabled = hybridOn;
        _tcpConnectTimeout.IsEnabled = hybridOn;

        var reorderEnabled = _reorderEnabled.IsChecked == true;
        _reorderRules.IsEnabled = reorderEnabled;
        _reorderCustomWait.IsEnabled = reorderEnabled;
        _reorderCustomCap.IsEnabled = reorderEnabled;
        _reorderMaxWait.IsEnabled = reorderEnabled && _reorderCustomWait.IsChecked == true;
        _reorderCapPackets.IsEnabled = reorderEnabled && _reorderCustomCap.IsChecked == true;
        _reorderMaxBytesPerFlow.IsEnabled = reorderEnabled;
        _reorderClassifyWindow.IsEnabled = reorderEnabled;
        _reorderAckDemoteMaxLarge.IsEnabled = reorderEnabled;
        _reorderSmallPacketThreshold.IsEnabled = reorderEnabled;
        _reorderResetMarkPackets.IsEnabled = reorderEnabled;
        _reorderResetIdleGrace.IsEnabled = reorderEnabled;
        _reorderMaxFlows.IsEnabled = reorderEnabled;
        _reorderGlobalMaxBytes.IsEnabled = reorderEnabled;
        _reorderIngressIdle.IsEnabled = reorderEnabled;
        _reorderEgressIdle.IsEnabled = reorderEnabled;
    }

    private void SaveAndClose()
    {
        if (string.IsNullOrWhiteSpace(_name.Text) ||
            string.IsNullOrWhiteSpace(_server.Text) ||
            !_server.Text.Contains(':') ||
            string.IsNullOrWhiteSpace(_auth.Password))
        {
            ShowValidation("请完整填写配置名称、服务器地址和认证密钥。TLS 服务器名称和网络路径可以留空。");
            return;
        }

        var dns = SplitValues(_dns.Text);
        if (dns.Count > 4 || dns.Any(value => !IPAddress.TryParse(value, out _)))
        {
            ShowValidation("DNS 最多填写 4 个有效的 IPv4/IPv6 地址。");
            return;
        }

        var tunName = _tunName.Text.Trim();
        if (tunName.Length is < 1 or > 64 || tunName.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            ShowValidation("TUN 名称只能包含字母、数字、点、短横线和下划线，长度为 1-64。");
            return;
        }

        if (_redundant.IsChecked == true && !string.IsNullOrWhiteSpace(_redundantPorts.Text))
        {
            var ports = _redundantPorts.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (ports.Length > 16)
            {
                ShowValidation("RedundantPorts 最多只能指定 16 个目的端口。");
                return;
            }
            foreach (var p in ports)
            {
                if (!ushort.TryParse(p, out var portVal) || portVal == 0)
                {
                    ShowValidation($"RedundantPorts 包含无效端口: {p}（必须是 1-65535 的整数）。");
                    return;
                }
            }
        }

        var pathMtuVal = decimal.ToInt32(GetDecimal(_pathMtu));
        if (pathMtuVal != 0 && (pathMtuVal < 1280 || pathMtuVal > 9000))
        {
            ShowValidation("PathMTU 必须为 0，或 1280-9000 之间的整数。");
            return;
        }

        var tunQueueLen = GetDecimal(_tunQueueLength);
        if (tunQueueLen is > 0 and < 100)
        {
            ShowValidation("TUN 发送队列深度只能为 0，或 100-65536。");
            return;
        }
        if (GetDecimal(_reinjectionLowerBound) > GetDecimal(_reinjectionHardDeadline))
        {
            ShowValidation("Reinjection 截止下限不能大于硬截止时间。");
            return;
        }
        if (_reorderCustomCap.IsChecked == true && !IsPowerOfTwo(GetDecimal(_reorderCapPackets)))
        {
            ShowValidation("UDP Reorder 的缓存包数必须是非零的 2 的幂。");
            return;
        }
        if (GetDecimal(_reorderIngressIdle) >= GetDecimal(_reorderEgressIdle))
        {
            ShowValidation("UDP Reorder 的接收流空闲回收时间必须小于发送流空闲回收时间。");
            return;
        }
        if (!TryReadReorderRules(out var rules, out var ruleError))
        {
            ShowValidation(ruleError);
            return;
        }

        ProfileName = _name.Text.Trim();
        RoutingPolicyId = _routing.SelectedValue?.ToString() ?? "truckersmp";
        AutoConnect = _autoConnect.IsChecked == true;
        var checkedPaths = _pathBoxes.Where(cb => cb.IsChecked == true).Select(cb => cb.Tag?.ToString() ?? string.Empty).Where(s => s.Length > 0).ToArray();
        UseDefaultPath = checkedPaths.Length == 0;

        _config["mode"] = "client";
        _config["server_addr"] = _server.Text.Trim();
        SetOptionalString("tls_server_name", _sni.Text);
        _config["auth_key"] = _auth.Password;
        _config["insecure"] = _allowInsecure.IsChecked == true;
        _config["dns"] = new JsonArray(dns.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        _config["tun_name"] = tunName;
        _config["log_level"] = LogLevels.Normalize(_logLevel.SelectedItem?.ToString());
        _config["manage_routes"] = _manageRoutes.IsChecked == true;
        _config["kill_switch"] = _killSwitch.IsChecked == true;
        _config["reconnect"] = _reconnect.IsChecked == true;
        _config["reconnect_interval"] = decimal.ToInt32(GetDecimal(_reconnectInterval));
        _config["scheduler"] = _scheduler.SelectedItem?.ToString() ?? "wlb";
        _config["request_server_scheduler"] = _requestServerScheduler.IsChecked == true;
        _config["cc"] = _congestion.SelectedItem?.ToString() ?? "bbr2";
        _config["init_max_path_id"] = decimal.ToUInt32(GetDecimal(_initMaxPathId));

        var selectedPaths = checkedPaths;
        if (selectedPaths.Length == 0)
            selectedPaths = [ResolveDefaultPhysicalInterface()];
        _config["paths"] = new JsonArray(selectedPaths
            .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

        if (_automaticMtu.IsChecked == true)
            _config.Remove("mtu");
        else
            _config["mtu"] = decimal.ToInt32(GetDecimal(_mtu));
        _config.Remove("tun_mtu");
        _config["tx_queue_len"] = decimal.ToInt32(tunQueueLen);
        _config["tun_read_batch"] = decimal.ToInt32(GetDecimal(_tunReadBatch));

        _config["reinjection"] = _reinjection.SelectedItem?.ToString() ?? "off";
        _config["reinjection_srtt_factor_pct"] = decimal.ToInt32(GetDecimal(_reinjectionFactor));
        _config["reinjection_hard_deadline_ms"] = decimal.ToInt32(GetDecimal(_reinjectionHardDeadline));
        _config["reinjection_deadline_lower_bound_ms"] = decimal.ToInt32(GetDecimal(_reinjectionLowerBound));
        _config["redundant"] = _redundant.IsChecked == true;
        _config["redundant_ports"] = _redundantPorts.Text.Trim();
        _config["pmtud"] = _pmtud.IsChecked == true;
        _config["path_mtu"] = pathMtuVal;

        var hybrid = GetOrCreateObject("hybrid");
        hybrid["enabled"] = _hybridEnabled.IsChecked == true;
        hybrid["tcp"] = _hybridTcp.SelectedItem?.ToString() ?? "auto";
        hybrid["tcp_max_flows"] = decimal.ToInt32(GetDecimal(_tcpMaxFlows));
        hybrid["tcp_idle_timeout_sec"] = decimal.ToUInt32(GetDecimal(_tcpIdleTimeout));
        hybrid["tcp_connect_timeout_sec"] = decimal.ToUInt32(GetDecimal(_tcpConnectTimeout));

        var reorder = GetOrCreateObject("reorder");
        reorder["enabled"] = _reorderEnabled.IsChecked == true;
        SaveReorderOverride(reorder, "max_wait_ms", _reorderCustomWait.IsChecked == true, GetDecimal(_reorderMaxWait));
        SaveReorderOverride(reorder, "cap_packets", _reorderCustomCap.IsChecked == true, GetDecimal(_reorderCapPackets));
        reorder["max_bytes_per_flow"] = decimal.ToUInt64(GetDecimal(_reorderMaxBytesPerFlow));
        reorder["classify_window"] = decimal.ToUInt16(GetDecimal(_reorderClassifyWindow));
        reorder["ack_demote_max_large"] = decimal.ToUInt16(GetDecimal(_reorderAckDemoteMaxLarge));
        reorder["small_packet_threshold"] = decimal.ToUInt32(GetDecimal(_reorderSmallPacketThreshold));
        reorder["reset_mark_packets"] = decimal.ToUInt32(GetDecimal(_reorderResetMarkPackets));
        reorder["reset_idle_grace_ms"] = decimal.ToUInt32(GetDecimal(_reorderResetIdleGrace));
        reorder["max_flows"] = decimal.ToUInt32(GetDecimal(_reorderMaxFlows));
        reorder["global_max_bytes"] = decimal.ToUInt64(GetDecimal(_reorderGlobalMaxBytes));
        reorder["ingress_idle_sec"] = decimal.ToUInt32(GetDecimal(_reorderIngressIdle));
        reorder["egress_idle_sec"] = decimal.ToUInt32(GetDecimal(_reorderEgressIdle));
        _config["reorder_rules"] = rules;

        var advanced = GetOrCreateObject("advanced");
        advanced["recv_rate_limit"] = decimal.ToInt64(GetDecimal(_recvRateLimit));
        advanced["udp_gso"] = _udpGso.IsChecked == true;
        advanced["udp_gro"] = _udpGro.IsChecked == true;
        advanced["tun_offload"] = _tunOffload.IsChecked == true;
        advanced["tun_gro"] = _tunGro.IsChecked == true;

        if (IsLoaded)
        {
            DialogResult = true;
            Close();
        }
    }

    private void ShowValidation(string message) =>
        FluentUi.ShowAlert(this, message, "配置不完整", isError: true);

    private void SetOptionalString(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            _config.Remove(name);
        else
            _config[name] = value.Trim();
    }

    private static List<string> SplitValues(string value) =>
        value.Split([',', ';', '\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsPowerOfTwo(decimal value)
    {
        if (value <= 0 || value > uint.MaxValue)
            return false;
        var number = decimal.ToUInt32(value);
        return (number & (number - 1)) == 0;
    }

    private static void SaveReorderOverride(JsonObject target, string name, bool enabled, decimal value)
    {
        if (!enabled)
            target.Remove(name);
        else
            target[name] = decimal.ToUInt32(value);
    }

    private static void ConfigureCombo(ComboBox combo, string[] values, string selected)
    {
        combo.Items.Clear();
        foreach (var item in values)
            combo.Items.Add(item);
        combo.SelectedItem = selected;
        if (combo.SelectedIndex < 0 && combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private static void ConfigureNumber(NumberBox control, double minimum, double maximum, double value)
    {
        control.Minimum = minimum;
        control.Maximum = maximum;
        control.MaxDecimalPlaces = 0;
        control.SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline;
        control.Value = Math.Clamp(value, minimum, maximum);
    }

    private static decimal GetDecimal(NumberBox control)
    {
        var val = control.Value ?? control.Minimum;
        if (double.IsNaN(val) || double.IsInfinity(val))
            val = control.Minimum;
        return (decimal)Math.Round(Math.Clamp(val, control.Minimum, control.Maximum));
    }

    private static string ResolveDefaultPhysicalInterface()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                              adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                              !adapter.Name.StartsWith("mqvpn", StringComparison.OrdinalIgnoreCase) &&
                              !adapter.Name.StartsWith("xray", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(adapter => adapter.GetIPProperties().GatewayAddresses.Count > 0)
            .Select(adapter => adapter.Name)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("未找到可用的默认物理网卡");
    }

    private string GetString(string name, string fallback = "") =>
        _config[name]?.GetValue<string>() ?? fallback;

    private bool GetBoolean(string name, bool fallback) =>
        GetBoolean(_config, name, fallback);

    private int GetInteger(string name, int fallback) =>
        GetInteger(_config, name, fallback);

    private IReadOnlyList<string> GetStringArray(string name) =>
        (_config[name] as JsonArray)?.Select(node => node?.GetValue<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray() ?? [];

    private JsonObject GetObject(string name) => _config[name] as JsonObject ?? new JsonObject();

    private JsonObject GetOrCreateObject(string name)
    {
        if (_config[name] is JsonObject result)
            return result;
        result = new JsonObject();
        _config[name] = result;
        return result;
    }

    private static string GetString(JsonObject? source, string name, string fallback = "") =>
        source?[name]?.GetValue<string>() ?? fallback;

    private static bool GetBoolean(JsonObject? source, string name, bool fallback)
    {
        if (source?[name] is not JsonValue value)
            return fallback;
        if (value.TryGetValue<bool>(out var result))
            return result;
        if (value.TryGetValue<string>(out var str))
        {
            if (string.Equals(str, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(str, "on", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(str, "false", StringComparison.OrdinalIgnoreCase) || string.Equals(str, "off", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return fallback;
    }

    private static int GetInteger(JsonObject? source, string name, int fallback) =>
        source?[name] is JsonValue value && value.TryGetValue<int>(out var result) ? result : fallback;

    private static long GetLong(JsonObject? source, string name, long fallback) =>
        source?[name] is JsonValue value && value.TryGetValue<long>(out var result) ? result : fallback;

    private long GetLong(string name, long fallback) => GetLong(_config, name, fallback);

    private static ulong GetUnsignedLong(JsonObject? source, string name, ulong fallback)
    {
        if (source?[name] is not JsonValue value)
            return fallback;
        if (value.TryGetValue<ulong>(out var unsignedResult))
            return unsignedResult;
        return value.TryGetValue<long>(out var signedResult) && signedResult >= 0
            ? (ulong)signedResult
            : fallback;
    }
}
