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
    private readonly IntegerInput _reconnectInterval = new();
    private readonly IntegerInput _initMaxPathId = new();
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
    private readonly IntegerInput _mtu = new();
    private readonly CheckBox _pmtud = new();
    private readonly IntegerInput _pathMtu = new();
    private readonly ComboBox _reinjection = new();
    private readonly IntegerInput _reinjectionFactor = new();
    private readonly IntegerInput _reinjectionHardDeadline = new();
    private readonly IntegerInput _reinjectionLowerBound = new();
    private readonly CheckBox _hybridEnabled = new();
    private readonly ComboBox _hybridTcp = new();
    private readonly IntegerInput _tcpMaxFlows = new();
    private readonly IntegerInput _tcpIdleTimeout = new();
    private readonly CheckBox _reorderEnabled = new();
    private readonly Wpf.Ui.Controls.DataGrid _reorderRules = new();
    private readonly CheckBox _reorderCustomWait = new();
    private readonly CheckBox _reorderCustomCap = new();
    private readonly IntegerInput _reorderMaxWait = new();
    private readonly IntegerInput _reorderCapPackets = new();
    private readonly IntegerInput _reorderMaxBytesPerFlow = new();
    private readonly IntegerInput _reorderClassifyWindow = new();
    private readonly IntegerInput _reorderAckDemoteMaxLarge = new();
    private readonly IntegerInput _reorderSmallPacketThreshold = new();
    private readonly IntegerInput _reorderResetMarkPackets = new();
    private readonly IntegerInput _reorderResetIdleGrace = new();
    private readonly IntegerInput _reorderMaxFlows = new();
    private readonly IntegerInput _reorderGlobalMaxBytes = new();
    private readonly IntegerInput _reorderIngressIdle = new();
    private readonly IntegerInput _reorderEgressIdle = new();
    private readonly IntegerInput _recvRateLimit = new();
    private readonly CheckBox _autoConnect = new();
    private readonly TabControl _tabs = new();
    private JsonObject _config;
    private readonly Dictionary<string, (Func<JsonNode?> Read, JsonNode? Initial)> _bindings = [];
    private readonly Dictionary<string, IntegerInput> _numbers = [];

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

        ConfigureCombo(_scheduler, "scheduler");
        _requestServerScheduler.Content = "请求服务端对齐调度器 (RequestServerScheduler)";
        _requestServerScheduler.IsChecked = GetBoolean("request_server_scheduler", false);
        ConfigureCombo(_congestion, "cc");
        ConfigureNumber(_initMaxPathId, "init_max_path_id");

        _routing.DisplayMemberPath = nameof(RoutingPolicy.Name);
        _routing.SelectedValuePath = nameof(RoutingPolicy.Id);
        _routing.ItemsSource = policies.ToList();
        _routing.SelectedValue = routingPolicyId;
        if (_routing.SelectedIndex < 0 && _routing.Items.Count > 0)
            _routing.SelectedIndex = 0;

        _reconnect.IsChecked = GetBoolean("reconnect", true);
        _reconnect.Content = "断线自动重连";
        ConfigureNumber(_reconnectInterval, "reconnect_interval");
        _reconnect.Checked += (_, _) => _reconnectInterval.IsEnabled = true;
        _reconnect.Unchecked += (_, _) => _reconnectInterval.IsEnabled = false;
        _reconnectInterval.IsEnabled = _reconnect.IsChecked == true;

        _tunName.Text = GetString("tun_name", "mqvpn0");
        ConfigureCombo(_logLevel, "log_level");
        _manageRoutes.Content = "由 MQVPN 管理系统路由";
        _manageRoutes.IsChecked = GetBoolean("manage_routes", true);
        _killSwitch.Content = "Kill Switch（断线时阻止流量绕过 VPN）";
        _killSwitch.IsChecked = GetBoolean("kill_switch", false);

        var configuredMtu = GetInteger("mtu", GetInteger("tun_mtu", 0));
        _automaticMtu.Content = "自动协商 MTU（推荐）";
        _automaticMtu.IsChecked = configuredMtu == 0;
        ConfigureNumber(_mtu, "mtu");
        _automaticMtu.Checked += (_, _) => _mtu.IsEnabled = false;
        _automaticMtu.Unchecked += (_, _) => _mtu.IsEnabled = true;
        _mtu.IsEnabled = _automaticMtu.IsChecked != true;

        _pmtud.Content = "启用路径 MTU 动态发现 (PMTUD)";
        _pmtud.IsChecked = GetBoolean("pmtud", false);
        ConfigureNumber(_pathMtu, "path_mtu");

        ConfigureCombo(_reinjection, "reinjection");
        ConfigureNumber(_reinjectionFactor, "reinjection_srtt_factor_pct");
        ConfigureNumber(_reinjectionHardDeadline, "reinjection_hard_deadline_ms");
        ConfigureNumber(_reinjectionLowerBound, "reinjection_deadline_lower_bound_ms");
        _reinjection.SelectionChanged += (_, _) => UpdateDependentControls();

        _redundant.Content = "启用 Redundant 冗余模式（实时多路径发送复制包）";
        _redundant.IsChecked = GetBoolean("redundant", false);
        _redundantPorts.Text = GetString("redundant_ports");
        _redundant.Checked += (_, _) => UpdateDependentControls();
        _redundant.Unchecked += (_, _) => UpdateDependentControls();

        var hybrid = GetObject("hybrid");
        _hybridEnabled.Content = "启用 Hybrid TCP lane";
        _hybridEnabled.IsChecked = GetBoolean(hybrid, "enabled", false);
        ConfigureCombo(_hybridTcp, "hybrid.tcp");
        ConfigureNumber(_tcpMaxFlows, "hybrid.tcp_max_flows");
        ConfigureNumber(_tcpIdleTimeout, "hybrid.tcp_idle_timeout_sec");
        _hybridEnabled.Checked += (_, _) => UpdateDependentControls();
        _hybridEnabled.Unchecked += (_, _) => UpdateDependentControls();

        var reorder = GetObject("reorder");
        InitializeReorderRules(reorder);
        ConfigureNumber(_reorderMaxWait, "reorder.max_wait_ms");
        ConfigureNumber(_reorderCapPackets, "reorder.cap_packets");
        ConfigureNumber(_reorderMaxBytesPerFlow, "reorder.max_bytes_per_flow");
        ConfigureNumber(_reorderClassifyWindow, "reorder.classify_window");
        ConfigureNumber(_reorderAckDemoteMaxLarge, "reorder.ack_demote_max_large");
        ConfigureNumber(_reorderSmallPacketThreshold, "reorder.small_packet_threshold");
        ConfigureNumber(_reorderResetMarkPackets, "reorder.reset_mark_packets");
        ConfigureNumber(_reorderResetIdleGrace, "reorder.reset_idle_grace_ms");
        ConfigureNumber(_reorderMaxFlows, "reorder.max_flows");
        ConfigureNumber(_reorderGlobalMaxBytes, "reorder.global_max_bytes");
        ConfigureNumber(_reorderIngressIdle, "reorder.ingress_idle_sec");
        ConfigureNumber(_reorderEgressIdle, "reorder.egress_idle_sec");

        ConfigureNumber(_recvRateLimit, "advanced.recv_rate_limit");

        _autoConnect.Content = "GUI 启动后自动连接此配置";
        _autoConnect.IsChecked = autoConnect;
        UpdateDependentControls();
        InitializeBindings();
    }

    private TabItem BuildBasicPage()
    {
        var page = CreatePage("基础设置", out var stack);
        stack.Children.Add(FluentUi.SectionHeader("连接与鉴权", SymbolRegular.Server20));
        stack.Children.Add(FluentUi.SettingCard("配置名称", _name));
        stack.Children.Add(FluentUi.SettingCard("服务器地址", _server, "格式为 HOST:PORT；IPv6 使用 [地址]:端口。"));
        stack.Children.Add(FluentUi.SettingCard("TLS 服务器名称 (SNI)", _sni, "可留空；留空时使用服务器地址中的主机名验证证书。"));
        stack.Children.Add(FluentUi.SettingCard("认证密钥 (PSK)", _auth));
        stack.Children.Add(FluentUi.SettingCard("TLS 证书校验", _allowInsecure, "默认验证证书。通常使用 Windows 信任库；SSL_CERT_FILE/SSL_CERT_DIR 环境变量可改用 CA bundle/目录；自签名证书应先配置信任，不必关闭验证。"));

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
        stack.Children.Add(FluentUi.SettingCard("多路径物理网卡 (Paths)", pathsBorder, "最多 8 张，每个名称最多 31 个 UTF-8 字节。不勾选表示默认网卡模式：GUI 在每次连接时解析当前默认物理网卡。"));
        stack.Children.Add(FluentUi.SettingCard("多路径调度器", _scheduler, "影响 DATAGRAM；Hybrid STREAM 固定使用 MinRTT。随包核心启用 WLB、BBR2、FEC/XOR；自换核心需匹配构建能力。"));
        stack.Children.Add(FluentUi.SettingCard("请求服务端对齐调度器", _requestServerScheduler, "向服务端协商使用本客户端的调度器算法作为下行流量调度策略。"));
        stack.Children.Add(FluentUi.SettingCard("拥塞控制算法 (CC)", _congestion, "CC 与多路径调度器相互独立。Redundant 模式下推荐使用 bbr。"));
        stack.Children.Add(FluentUi.SettingCard("初始最大 Path ID", _initMaxPathId, "InitMaxPathId：0 使用 xquic 默认值 8；非零范围 1–128。旧超限值必须明确修正。"));
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
        stack.Children.Add(FluentUi.SettingCard("冗余模式开关", _redundant, "在多个活动路径上复制 UDP 数据包；适合需要冗余的场景，效果仍受链路可用性影响。"));
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

        stack.Children.Add(FluentUi.SectionHeader("平台限制", SymbolRegular.Info20));
        stack.Children.Add(new System.Windows.Controls.TextBlock { Text = "Linux 的 tx_queue_len、tun_read_batch、UDP/TUN offload，以及 Hybrid 服务端连接超时、全局流数和 ACL 在 Windows 客户端不生效。导入的这些字段会原样保留。", TextWrapping = TextWrapping.Wrap });
        return page;
    }

    private TabItem BuildAdvancedPage()
    {
        var page = CreatePage("高级设置", out var stack);
        stack.Children.Add(FluentUi.SectionHeader("虚拟网卡与安全", SymbolRegular.Shield20));
        stack.Children.Add(FluentUi.SettingCard("TUN 网卡名称", _tunName, "非空，最多 31 个 UTF-8 字节（中文通常每字 3 字节），可含空格。"));
        stack.Children.Add(FluentUi.SettingCard("默认日志等级", _logLevel, "写入配置文件的默认等级；启动时主界面的“日志级别”会覆盖它。"));
        stack.Children.Add(FluentUi.SettingCard("系统路由管理", _manageRoutes, "GUI 管理全局/分流路由，建议关闭。核心 manage_routes 会接管系统路由，不能与 GUI 分流策略同时使用；不会自动改变 Kill Switch。"));
        stack.Children.Add(FluentUi.SettingCard("断线保护 (Kill Switch)", _killSwitch, "核心 WFP 规则阻止绕过隧道的流量，可能影响分流中的直连流量；它不决定 GUI 的全局/分流模式。"));
        stack.Children.Add(FluentUi.SettingCard("MTU 协商模式", _automaticMtu));
        stack.Children.Add(FluentUi.SettingCard("自定义 MTU 上限", _mtu, "范围 1280-9000。客户端值仅作为协商 MTU 的上限。"));
        stack.Children.Add(FluentUi.SettingCard("路径 MTU 动态发现 (PMTUD)", _pmtud, "0.19.0 新增。动态探测端到端网络路径支持的最大报文大小，避免分片。默认关闭，按需在各端启用并验证网络支持。"));
        stack.Children.Add(FluentUi.SettingCard("固定路径 MTU (PathMTU)", _pathMtu, "0.19.0 新增。固定路径 MTU 大小（1280-9000，0 使用核心默认报文大小）。开启 PMTUD 时作为探测上限。"));

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
        if (!TryBuildConfiguration(out var result, out var error))
        {
            ShowValidation(error);
            return;
        }
        _config = result;
        InitializeBindings();
        ProfileName = _name.Text.Trim();
        RoutingPolicyId = _routing.SelectedValue?.ToString() ?? "truckersmp";
        AutoConnect = _autoConnect.IsChecked == true;
        UseDefaultPath = _pathBoxes.All(cb => cb.IsChecked != true);
        if (IsLoaded)
        {
            DialogResult = true;
            Close();
        }
    }

    // This is the same path used by the Save button and the contract tests.
    internal bool TryBuildConfiguration(out JsonObject result, out string error)
    {
        result = _config.DeepClone().AsObject();
        error = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(_name.Text))
                throw new InvalidDataException("请填写配置名称。");
            foreach (var (path, control) in _numbers)
            {
                if ((path == "mtu" && _automaticMtu.IsChecked == true && !_config.ContainsKey("tun_mtu")) ||
                    (path == "reorder.max_wait_ms" && _reorderCustomWait.IsChecked != true) ||
                    (path == "reorder.cap_packets" && _reorderCustomCap.IsChecked != true)) continue;
                if (!control.TryRead(out _))
                    throw new InvalidDataException($"{path} / {(path == "init_max_path_id" ? "InitMaxPathId" : path == "path_mtu" ? "PathMTU" : path)} 必须是 {control.Minimum}–{control.Maximum} 的整数，不能包含小数或指数。");
            }
            foreach (var (path, binding) in _bindings)
            {
                var current = binding.Read();
                var original = MqvpnProfileConfiguration.Get(_config, path);
                // Preserve missing defaults and unedited JSON, but migrate recognized
                // legacy boolean spellings to actual JSON booleans.
                var legacyBool = current is JsonValue boolean && boolean.TryGetValue<bool>(out _) &&
                    original is JsonValue legacy && legacy.TryGetValue<string>(out var text) &&
                    new[] { "on", "off", "true", "false" }.Contains(text, StringComparer.OrdinalIgnoreCase);
                if (legacyBool || !JsonNode.DeepEquals(current, binding.Initial))
                    MqvpnProfileConfiguration.Set(result, path, current);
            }
            // Canonicalize only the supported legacy MTU alias, preserving its value.
            if (_config.ContainsKey("tun_mtu"))
            {
                if (!result.ContainsKey("mtu") && _automaticMtu.IsChecked != true)
                    result["mtu"] = decimal.ToUInt64(_mtu.Value!.Value);
                result.Remove("tun_mtu");
            }
            if (!TryReadReorderRules(out var rules, out error)) return false;
            if (!JsonNode.DeepEquals(rules, _initialRules)) result["reorder_rules"] = rules;
            MqvpnProfileConfiguration.Validate(result);
            if (_routing.SelectedItem is RoutingPolicy policy)
                RoutingSemantics.ValidateCoreRouteManagement(result, policy);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or OverflowException or InvalidOperationException)
        {
            error = ex.Message;
            return false;
        }
    }

    private JsonArray _initialRules = new();

    private void Bind(string path, Func<JsonNode?> read) =>
        _bindings[path] = (read, read()?.DeepClone());

    private void InitializeBindings()
    {
        foreach (var (path, control) in _numbers)
            Bind(path, () => control.Value is decimal n ? JsonValue.Create(decimal.ToUInt64(n)) : JsonValue.Create(control.Text));
        Bind("mtu", () => _automaticMtu.IsChecked == true ? null : _mtu.Value is decimal n ? JsonValue.Create(decimal.ToUInt64(n)) : JsonValue.Create(_mtu.Text));
        Bind("server_addr", () => JsonValue.Create(_server.Text.Trim()));
        Bind("tls_server_name", () => JsonValue.Create(_sni.Text.Trim()));
        Bind("auth_key", () => JsonValue.Create(_auth.Password));
        Bind("tun_name", () => JsonValue.Create(_tunName.Text.Trim()));
        Bind("redundant_ports", () => JsonValue.Create(_redundantPorts.Text.Trim()));
        foreach (var (path, box) in new (string, CheckBox)[]
        {
            ("insecure", _allowInsecure), ("reconnect", _reconnect), ("manage_routes", _manageRoutes),
            ("kill_switch", _killSwitch), ("request_server_scheduler", _requestServerScheduler),
            ("redundant", _redundant), ("pmtud", _pmtud), ("hybrid.enabled", _hybridEnabled), ("reorder.enabled", _reorderEnabled)
        }) Bind(path, () => JsonValue.Create(box.IsChecked == true));
        foreach (var (path, combo) in new (string, ComboBox)[]
        {
            ("scheduler", _scheduler), ("cc", _congestion), ("reinjection", _reinjection),
            ("log_level", _logLevel), ("hybrid.tcp", _hybridTcp)
        }) Bind(path, () => JsonValue.Create(combo.SelectedItem?.ToString() ?? ""));
        Bind("reorder.max_wait_ms", () => _reorderCustomWait.IsChecked == true ? JsonValue.Create(decimal.ToUInt32(_reorderMaxWait.Value ?? 0)) : null);
        Bind("reorder.cap_packets", () => _reorderCustomCap.IsChecked == true ? JsonValue.Create(decimal.ToUInt32(_reorderCapPackets.Value ?? 0)) : null);
        Bind("dns", () => new JsonArray(SplitValues(_dns.Text).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()));
        Bind("paths", () => new JsonArray(_pathBoxes.Where(cb => cb.IsChecked == true).Select(cb => (JsonNode?)JsonValue.Create(cb.Tag?.ToString())).ToArray()));
        if (TryReadReorderRules(out var rules, out _)) _initialRules = rules;
    }

    private void ShowValidation(string message) =>
        FluentUi.ShowAlert(this, message, "配置不完整", isError: true);

    private static List<string> SplitValues(string value) =>
        value.Split([',', ';', '\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool IsPowerOfTwo(decimal value) => MqvpnProfileConfiguration.IsPowerOfTwo(value);

    private void ConfigureCombo(ComboBox combo, string path)
    {
        var field = MqvpnProfileConfiguration.Field(path);
        var values = field.Choices!;
        var selected = (MqvpnProfileConfiguration.Get(_config, path) as JsonValue)?.ToString() ?? (string)field.DefaultValue;
        combo.Items.Clear();
        foreach (var item in values)
            combo.Items.Add(item);
        combo.SelectedItem = selected;
        if (combo.SelectedIndex < 0)
        {
            combo.Items.Add(selected);
            combo.SelectedItem = selected;
        }
    }

    private void ConfigureNumber(IntegerInput control, string path)
    {
        var field = MqvpnProfileConfiguration.Field(path);
        control.Minimum = field.Minimum;
        control.Maximum = field.Maximum;
        var node = MqvpnProfileConfiguration.Get(_config, path);
        if (path == "mtu") node ??= _config["tun_mtu"];
        control.Text = path == "mtu" && _config.ContainsKey("tun_mtu") && _config["tun_mtu"] is null
            ? "null" : (node ?? field.DefaultNode).ToJsonString();
        control.ToolTip = $"{path}: {field.Minimum}–{field.Maximum}；默认 {field.DefaultValue}";
        _numbers[path] = control;
    }

    private string GetString(string name, string fallback = "") =>
        GetString(_config, name, MqvpnProfileConfiguration.Fields.FirstOrDefault(f => f.Path == name)?.DefaultValue as string ?? fallback);

    private bool GetBoolean(string name, bool fallback) =>
        GetBoolean(_config, name, MqvpnProfileConfiguration.Fields.FirstOrDefault(f => f.Path == name)?.DefaultValue is bool value ? value : fallback);

    private int GetInteger(string name, int fallback) =>
        GetInteger(_config, name, fallback);

    private IReadOnlyList<string> GetStringArray(string name) =>
        (_config[name] as JsonArray)?.Select(node => node is JsonValue v && v.TryGetValue<string>(out var text) ? text : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray() ?? [];

    private JsonObject GetObject(string name) => _config[name] as JsonObject ?? new JsonObject();

    private static string GetString(JsonObject? source, string name, string fallback = "") =>
        source?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : fallback;

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

}
