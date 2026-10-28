using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TruckersMP.Proxy;

internal sealed class AppState
{
    public string ActiveProfileId { get; set; } = string.Empty;
    public string MqvpnUpdateSource { get; set; } = "r11234567/mqvpn";
    public string XrayUpdateSource { get; set; } = "XTLS/Xray-core";
    public string GeoDataUpdateSource { get; set; } = "Loyalsoldier/v2ray-rules-dat";
    public bool MqvpnIncludePreRelease { get; set; }
    public bool XrayIncludePreRelease { get; set; }
    public string LogLevel { get; set; } = LogLevels.Default;
    public bool StartMinimized { get; set; }
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public string LatencyProbeEndpoint { get; set; } = "1.1.1.1:80";
    public string ThemeMode { get; set; } = "system";
    public List<ProxyProfile> Profiles { get; set; } = [];
}

internal sealed class ProxyProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "MQVPN 配置";
    public string ConfigFile { get; set; } = string.Empty;
    public string RoutingPolicyId { get; set; } = "truckersmp";
    public bool AutoConnect { get; set; }
    public bool UseDefaultPath { get; set; }
    public int? LastLatencyMs { get; set; }
    public DateTime? LastTestUtc { get; set; }
    public long TotalUploadBytes { get; set; }
    public long TotalDownloadBytes { get; set; }
    public long TodayUploadBytes { get; set; }
    public long TodayDownloadBytes { get; set; }
    public string TrafficDate { get; set; } = string.Empty;
}

internal sealed class RoutingDocument
{
    public List<RoutingPolicy> Policies { get; set; } = [];
}

internal sealed class RoutingPolicy
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "自定义策略";
    public string FinalOutbound { get; set; } = "DIRECT";
    public List<RoutingRule> Rules { get; set; } = [];
}

internal sealed class RoutingRule
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "规则";
    public string Type { get; set; } = "PROCESS-NAME";
    public string Value { get; set; } = string.Empty;
    public string Network { get; set; } = "TCP/UDP";
    public string Outbound { get; set; } = "MQVPN-TUN";
}

internal sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _configDirectory;
    private readonly string _statePath;
    private readonly string _routingPath;
    private static readonly Encoding MqvpnConfigEncoding = CreateMqvpnConfigEncoding();

    private ProfileStore(string configDirectory, AppState state, RoutingDocument routing)
    {
        _configDirectory = configDirectory;
        _statePath = Path.Combine(configDirectory, "profiles.json");
        _routingPath = Path.Combine(configDirectory, "routing.json");
        State = state;
        Routing = routing;
    }

    public AppState State { get; }
    public RoutingDocument Routing { get; }
    public ProxyProfile ActiveProfile =>
        State.Profiles.First(profile => profile.Id == State.ActiveProfileId);

    public static ProfileStore Load(string configDirectory)
    {
        Directory.CreateDirectory(configDirectory);
        Directory.CreateDirectory(Path.Combine(configDirectory, "profiles"));
        var statePath = Path.Combine(configDirectory, "profiles.json");
        var routingPath = Path.Combine(configDirectory, "routing.json");

        var state = File.Exists(statePath)
            ? JsonSerializer.Deserialize<AppState>(File.ReadAllText(statePath), JsonOptions) ?? new AppState()
            : CreateInitialState(configDirectory);
        var routing = File.Exists(routingPath)
            ? JsonSerializer.Deserialize<RoutingDocument>(File.ReadAllText(routingPath), JsonOptions) ?? CreateDefaultRouting()
            : CreateDefaultRouting();

        if (state.Profiles.Count == 0)
            state = CreateInitialState(configDirectory);
        if (!state.Profiles.Any(profile => profile.Id == state.ActiveProfileId))
            state.ActiveProfileId = state.Profiles[0].Id;
        if (routing.Policies.Count == 0)
            routing = CreateDefaultRouting();
        EnsureBuiltInPolicies(routing);

        var store = new ProfileStore(configDirectory, state, routing);
        store.NormalizeProfilePolicies();
        store.NormalizeConfigEncoding();
        store.Save();
        return store;
    }

    public string GetConfigPath(ProxyProfile profile)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_configDirectory, profile.ConfigFile));
        var root = Path.GetFullPath(_configDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("配置文件路径超出 config 目录");
        return fullPath;
    }

    public JsonObject ReadConfig(ProxyProfile profile)
    {
        return JsonNode.Parse(ReadMqvpnConfigFile(GetConfigPath(profile)))?.AsObject()
            ?? throw new InvalidDataException($"配置 {profile.Name} 不是有效 JSON");
    }

    public void SaveConfig(ProxyProfile profile, JsonObject config)
    {
        WriteMqvpnConfigFile(GetConfigPath(profile), config);
    }

    public static void WriteMqvpnConfigFile(string path, JsonObject config) =>
        File.WriteAllText(path, config.ToJsonString(JsonOptions), MqvpnConfigEncoding);

    private static string ReadMqvpnConfigFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return MqvpnConfigEncoding.GetString(bytes);
        }
    }

    private static Encoding CreateMqvpnConfigEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding((int)NativeMethods.GetACP());
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        internal static extern uint GetACP();
    }

    private void NormalizeConfigEncoding()
    {
        foreach (var profile in State.Profiles)
        {
            var config = ReadConfig(profile);
            // Re-serialize every profile so legacy \uXXXX output becomes UTF-8 text.
            SaveConfig(profile, config);
        }
    }

    public ProxyProfile Add(string name, JsonObject config, string routingPolicyId)
    {
        var profile = new ProxyProfile
        {
            Name = name,
            RoutingPolicyId = routingPolicyId,
        };
        profile.ConfigFile = Path.Combine("profiles", $"{profile.Id}.json");
        State.Profiles.Add(profile);
        SaveConfig(profile, config);
        Save();
        return profile;
    }

    public ProxyProfile Clone(ProxyProfile source)
    {
        var clone = Add($"{source.Name} - 副本", ReadConfig(source), source.RoutingPolicyId);
        clone.AutoConnect = source.AutoConnect;
        clone.UseDefaultPath = source.UseDefaultPath;
        clone.LastLatencyMs = source.LastLatencyMs;
        clone.LastTestUtc = source.LastTestUtc;
        Save();
        return clone;
    }

    public void Delete(ProxyProfile profile)
    {
        if (State.Profiles.Count == 1)
            throw new InvalidOperationException("至少保留一个配置");

        State.Profiles.Remove(profile);
        if (State.ActiveProfileId == profile.Id)
            State.ActiveProfileId = State.Profiles[0].Id;
        var path = GetConfigPath(profile);
        if (!Path.GetFileName(path).Equals("client-windows.json", StringComparison.OrdinalIgnoreCase))
            File.Delete(path);
        Save();
    }

    public bool MoveUp(ProxyProfile profile)
    {
        var index = State.Profiles.IndexOf(profile);
        if (index <= 0)
            return false;
        (State.Profiles[index - 1], State.Profiles[index]) = (State.Profiles[index], State.Profiles[index - 1]);
        Save();
        return true;
    }

    public bool MoveDown(ProxyProfile profile)
    {
        var index = State.Profiles.IndexOf(profile);
        if (index < 0 || index >= State.Profiles.Count - 1)
            return false;
        (State.Profiles[index + 1], State.Profiles[index]) = (State.Profiles[index], State.Profiles[index + 1]);
        Save();
        return true;
    }

    public void Save()
    {
        File.WriteAllText(_statePath, JsonSerializer.Serialize(State, JsonOptions));
        File.WriteAllText(_routingPath, JsonSerializer.Serialize(Routing, JsonOptions));
    }

    public void ReplaceRouting(RoutingDocument routing)
    {
        Routing.Policies.Clear();
        Routing.Policies.AddRange(routing.Policies);
        NormalizeProfilePolicies();
        Save();
    }

    public RoutingPolicy GetPolicy(string id) =>
        Routing.Policies.FirstOrDefault(policy => policy.Id == id) ?? Routing.Policies[0];

    public string GenerateXrayConfig(ProxyProfile profile, string outputPath)
    {
        var config = ReadConfig(profile);
        var serverAddress = config["server_addr"]?.GetValue<string>() ?? string.Empty;
        var serverHost = ParseHost(serverAddress);
        var physicalInterface = (config["paths"] as JsonArray)?.FirstOrDefault()?.GetValue<string>()
            ?? GetDefaultPhysicalInterface();
        var mqvpnInterface = config["tun_name"]?.GetValue<string>() ?? "mqvpn0";
        var policy = GetPolicy(profile.RoutingPolicyId);

        var rules = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "field",
                ["process"] = new JsonArray("self/", "mqvpn"),
                ["outboundTag"] = "PHYSICAL-DIRECT",
                ["ruleTag"] = "内核防回环",
            },
        };

        if (IPAddress.TryParse(serverHost, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["ip"] = new JsonArray($"{serverHost}/32"),
                ["outboundTag"] = "PHYSICAL-DIRECT",
                ["ruleTag"] = "MQVPN 服务端绕行",
            });
        }
        else if (IPAddress.TryParse(serverHost, out address) &&
                 address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["ip"] = new JsonArray($"{serverHost}/128"),
                ["outboundTag"] = "PHYSICAL-DIRECT",
                ["ruleTag"] = "MQVPN 服务端绕行",
            });
        }
        else if (serverHost.Length > 0)
        {
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["domain"] = new JsonArray($"full:{ValidateRuleValue(serverHost)}"),
                ["outboundTag"] = "PHYSICAL-DIRECT",
                ["ruleTag"] = "MQVPN 服务端绕行",
            });
        }

        rules.Add(new JsonObject
        {
            ["type"] = "field",
            ["ip"] = new JsonArray(
                "127.0.0.0/8",
                "10.0.0.0/8",
                "172.16.0.0/12",
                "192.168.0.0/16",
                "169.254.0.0/16",
                "::1/128",
                "fc00::/7",
                "fe80::/10"),
            ["outboundTag"] = "PHYSICAL-DIRECT",
            ["ruleTag"] = "私网直连",
        });

        var enabledRules = policy.Rules.Where(rule => rule.Enabled).ToList();
        for (var ruleIndex = 0; ruleIndex < enabledRules.Count; ruleIndex++)
        {
            var rule = enabledRules[ruleIndex];
            var type = ValidateToken(rule.Type,
                ["PROCESS-NAME", "DOMAIN", "DOMAIN-SUFFIX", "IP-CIDR", "DST-PORT", "GEOIP", "GEOSITE", "PROTOCOL"]);
            var value = ValidateRuleValue(rule.Value);
            var outbound = ValidateToken(rule.Outbound, ["MQVPN-TUN", "DIRECT", "REJECT"]);
            var generatedOutbound = outbound == "DIRECT" ? "PHYSICAL-DIRECT" : outbound;
            var generatedRule = new JsonObject
            {
                ["type"] = "field",
                ["outboundTag"] = generatedOutbound,
                ["ruleTag"] = string.IsNullOrWhiteSpace(rule.Name) ? "自定义规则" : rule.Name.Trim(),
            };

            switch (type)
            {
                case "PROCESS-NAME":
                    var processes = ToJsonArray(ParseRuleValues(value).Select(NormalizeXrayProcessName));
                    while (ruleIndex + 1 < enabledRules.Count)
                    {
                        var next = enabledRules[ruleIndex + 1];
                        if (!next.Type.Equals("PROCESS-NAME", StringComparison.OrdinalIgnoreCase) ||
                            !next.Outbound.Equals(rule.Outbound, StringComparison.OrdinalIgnoreCase) ||
                            !next.Network.Equals(rule.Network, StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }

                        foreach (var process in ParseRuleValues(ValidateRuleValue(next.Value)))
                            processes.Add(NormalizeXrayProcessName(process));
                        ruleIndex++;
                    }
                    generatedRule["process"] = processes;
                    break;
                case "DOMAIN":
                    generatedRule["domain"] = ToJsonArray(ParseRuleValues(value).Select(item => $"full:{item}"));
                    break;
                case "DOMAIN-SUFFIX":
                    generatedRule["domain"] = ToJsonArray(ParseRuleValues(value).Select(item =>
                        item.StartsWith("domain:", StringComparison.OrdinalIgnoreCase)
                            ? item
                            : $"domain:{item.TrimStart('.')}"));
                    break;
                case "IP-CIDR":
                    generatedRule["ip"] = ToJsonArray(ParseRuleValues(value));
                    break;
                case "DST-PORT":
                    generatedRule["port"] = value;
                    break;
                case "GEOIP":
                    generatedRule["ip"] = ToJsonArray(ParseRuleValues(value).Select(item =>
                        item.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase) ? item : $"geoip:{item}"));
                    break;
                case "GEOSITE":
                    generatedRule["domain"] = ToJsonArray(ParseRuleValues(value).Select(item =>
                        item.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase) ? item : $"geosite:{item}"));
                    break;
                case "PROTOCOL":
                    generatedRule["protocol"] = ToJsonArray(ParseRuleValues(value).Select(item => item.ToLowerInvariant()));
                    break;
            }

            if (rule.Network.Equals("TCP", StringComparison.OrdinalIgnoreCase) ||
                rule.Network.Equals("UDP", StringComparison.OrdinalIgnoreCase))
                generatedRule["network"] = rule.Network.ToLowerInvariant();

            rules.Add(generatedRule);
        }

        var finalOutbound = ValidateToken(policy.FinalOutbound, ["MQVPN-TUN", "DIRECT", "REJECT"]);
        var generatedFinalOutbound = finalOutbound == "DIRECT" ? "PHYSICAL-DIRECT" : finalOutbound;
        rules.Add(new JsonObject
        {
            ["type"] = "field",
            ["network"] = "tcp,udp",
            ["outboundTag"] = generatedFinalOutbound,
            ["ruleTag"] = "最终规则",
        });

        var root = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["loglevel"] = LogLevels.ToXray(State.LogLevel),
            },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["tag"] = "tun",
                    ["protocol"] = "tun",
                    ["settings"] = new JsonObject
                    {
                        ["name"] = "xray-tmp",
                        ["desc"] = "MQVPN Routing",
                        ["mtu"] = 9000,
                        ["gateway"] = new JsonArray("172.28.247.1/30", "fd28:247::1/126"),
                        ["dns"] = new JsonArray("1.1.1.1", "2606:4700:4700::1111"),
                        ["autoSystemRoutingTable"] = new JsonArray("0.0.0.0/0", "::/0"),
                        ["autoOutboundsInterface"] = "auto",
                    },
                    ["sniffing"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["destOverride"] = new JsonArray("http", "tls", "quic"),
                        ["routeOnly"] = true,
                    },
                },
            },
            ["outbounds"] = new JsonArray
            {
                CreateFreedomOutbound("PHYSICAL-DIRECT", physicalInterface),
                CreateFreedomOutbound("MQVPN-TUN", mqvpnInterface),
                new JsonObject
                {
                    ["tag"] = "REJECT",
                    ["protocol"] = "blackhole",
                },
            },
            ["routing"] = new JsonObject
            {
                ["domainStrategy"] = "IPIfNonMatch",
                ["rules"] = rules,
            },
        };

        File.WriteAllText(outputPath, root.ToJsonString(JsonOptions), new UTF8Encoding(false));
        return outputPath;
    }

    private static string GetDefaultPhysicalInterface()
    {
        return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                              adapter.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Ethernet or System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 &&
                              adapter.GetIPProperties().UnicastAddresses.Count > 0 &&
                              !adapter.Name.StartsWith("mqvpn", StringComparison.OrdinalIgnoreCase) &&
                              !adapter.Name.StartsWith("xray", StringComparison.OrdinalIgnoreCase) &&
                              !adapter.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(adapter => adapter.GetIPProperties().GatewayAddresses.Count > 0)
            .Select(adapter => adapter.Name)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("未找到可用的默认物理网卡");
    }

    private static JsonObject CreateFreedomOutbound(string tag, string interfaceName) => new()
    {
        ["tag"] = tag,
        ["protocol"] = "freedom",
        ["settings"] = new JsonObject(),
        ["streamSettings"] = new JsonObject
        {
            ["sockopt"] = new JsonObject
            {
                ["interface"] = interfaceName,
            },
        },
    };

    private static string NormalizeXrayProcessName(string value)
    {
        var process = Path.GetFileName(value.Replace('\\', '/'));
        return process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? process[..^4] : process;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var result = new JsonArray();
        foreach (var value in values)
            result.Add(value);
        return result;
    }

    private static IReadOnlyList<string> ParseRuleValues(string value)
    {
        var values = value.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length == 0 || values.Any(item => item.Length == 0))
            throw new InvalidDataException($"无效的路由规则值: {value}");
        return values;
    }

    private static AppState CreateInitialState(string configDirectory)
    {
        var configPath = Path.Combine(configDirectory, "client-windows.json");
        if (!File.Exists(configPath))
            WriteMqvpnConfigFile(configPath, CreateDefaultMqvpnConfig());
        var profile = new ProxyProfile
        {
            Name = "当前 MQVPN 配置",
            ConfigFile = "client-windows.json",
            RoutingPolicyId = "truckersmp",
        };
        return new AppState
        {
            ActiveProfileId = profile.Id,
            Profiles = [profile],
        };
    }

    public static JsonObject CreateDefaultMqvpnConfig() => new()
    {
        ["mode"] = "client",
        ["server_addr"] = string.Empty,
        ["tls_server_name"] = string.Empty,
        ["auth_key"] = string.Empty,
        ["insecure"] = false,
        ["tun_name"] = "mqvpn0",
        ["dns"] = new JsonArray("1.1.1.1", "8.8.8.8"),
        ["log_level"] = "info",
        ["kill_switch"] = false,
        ["manage_routes"] = false,
        ["reconnect"] = true,
        ["reconnect_interval"] = 5,
        ["scheduler"] = "wlb",
        ["cc"] = "bbr2",
        ["init_max_path_id"] = 0,
        ["paths"] = new JsonArray(),
        ["tx_queue_len"] = 0,
        ["tun_read_batch"] = 0,
        ["reinjection"] = "off",
        ["reinjection_srtt_factor_pct"] = 110,
        ["reinjection_hard_deadline_ms"] = 500,
        ["reinjection_deadline_lower_bound_ms"] = 20,
        ["hybrid"] = new JsonObject
        {
            ["enabled"] = false,
            ["tcp"] = "auto",
            ["tcp_max_flows"] = 256,
            ["tcp_idle_timeout_sec"] = 300,
            ["tcp_connect_timeout_sec"] = 10,
        },
        ["reorder"] = new JsonObject
        {
            ["enabled"] = "off",
            ["max_bytes_per_flow"] = 1572864,
            ["classify_window"] = 64,
            ["ack_demote_max_large"] = 3,
            ["small_packet_threshold"] = 200,
            ["reset_mark_packets"] = 8,
            ["reset_idle_grace_ms"] = 10000,
            ["max_flows"] = 65536,
            ["global_max_bytes"] = 67108864,
            ["ingress_idle_sec"] = 30,
            ["egress_idle_sec"] = 300,
        },
        ["advanced"] = new JsonObject
        {
            ["recv_rate_limit"] = 0,
            ["udp_gso"] = true,
            ["udp_gro"] = true,
        },
    };

    private static RoutingDocument CreateDefaultRouting()
    {
        var routing = new RoutingDocument();
        routing.Policies.AddRange(
        [
            new RoutingPolicy
            {
                Id = "truckersmp",
                Name = "仅 TruckersMP",
                Rules = CreateTruckersMpRules(),
            },
            new RoutingPolicy { Id = "global", Name = "全局 MQVPN", FinalOutbound = "MQVPN-TUN" },
            new RoutingPolicy { Id = "direct", Name = "全部直连", FinalOutbound = "DIRECT" },
        ]);
        EnsureBuiltInPolicies(routing);
        return routing;
    }

    private static void EnsureBuiltInPolicies(RoutingDocument routing)
    {
        foreach (var policy in CreateV2RayNV4Policies())
        {
            if (!routing.Policies.Any(candidate => candidate.Id == policy.Id))
                routing.Policies.Add(policy);
        }
    }

    private static List<RoutingRule> CreateTruckersMpRules() =>
    [
        Rule("TruckersMP Launcher", "PROCESS-NAME", "TruckersMP-Launcher.exe", "MQVPN-TUN"),
        Rule("Euro Truck Simulator 2", "PROCESS-NAME", "eurotrucks2.exe", "MQVPN-TUN"),
    ];

    private static IReadOnlyList<RoutingPolicy> CreateV2RayNV4Policies()
    {
        const string chinaDnsIp =
            "223.5.5.5,223.6.6.6,2400:3200::1,2400:3200:baba::1,119.29.29.29,1.12.12.12," +
            "120.53.53.53,2402:4e00::,2402:4e00:1::,180.76.76.76,2400:da00::6666," +
            "114.114.114.114,114.114.115.115,114.114.114.119,114.114.115.119," +
            "114.114.114.110,114.114.115.110,180.184.1.1,180.184.2.2,101.226.4.6," +
            "218.30.118.6,123.125.81.6,140.207.198.6,1.2.4.8,210.2.4.8,52.80.66.66," +
            "117.50.22.22,2400:7fc0:849e:200::4,2404:c2c0:85d8:901::4,117.50.10.10," +
            "52.80.52.52,2400:7fc0:849e:200::8,2404:c2c0:85d8:901::8,117.50.60.30,52.80.60.30";
        const string overseasDnsIp =
            "1.1.1.1,1.0.0.1,2606:4700:4700::1111,2606:4700:4700::1001," +
            "1.1.1.2,1.0.0.2,2606:4700:4700::1112,2606:4700:4700::1002," +
            "1.1.1.3,1.0.0.3,2606:4700:4700::1113,2606:4700:4700::1003," +
            "8.8.8.8,8.8.4.4,2001:4860:4860::8888,2001:4860:4860::8844," +
            "94.140.14.14,94.140.15.15,2a10:50c0::ad1:ff,2a10:50c0::ad2:ff," +
            "94.140.14.15,94.140.15.16,2a10:50c0::bad1:ff,2a10:50c0::bad2:ff," +
            "94.140.14.140,94.140.14.141,2a10:50c0::1:ff,2a10:50c0::2:ff," +
            "208.67.222.222,208.67.220.220,2620:119:35::35,2620:119:53::53," +
            "208.67.222.123,208.67.220.123,2620:119:35::123,2620:119:53::123," +
            "9.9.9.9,149.112.112.112,2620:fe::9,2620:fe::fe,9.9.9.11,149.112.112.11," +
            "2620:fe::11,2620:fe::fe:11,9.9.9.10,149.112.112.10,2620:fe::10,2620:fe::fe:10," +
            "77.88.8.8,77.88.8.1,2a02:6b8::feed:0ff,2a02:6b8:0:1::feed:0ff," +
            "77.88.8.88,77.88.8.2,2a02:6b8::feed:bad,2a02:6b8:0:1::feed:bad," +
            "77.88.8.7,77.88.8.3,2a02:6b8::feed:a11,2a02:6b8:0:1::feed:a11";

        var whitelist = new RoutingPolicy
        {
            Id = "v2rayn-v4-whitelist",
            Name = "V4-绕过大陆 (Whitelist)",
            FinalOutbound = "MQVPN-TUN",
            Rules = CreateTruckersMpRules(),
        };
        whitelist.Rules.AddRange(
        [
            Rule("代理 Google", "GEOSITE", "google", "MQVPN-TUN"),
            Rule("绕过局域网 IP", "GEOIP", "private", "DIRECT"),
            Rule("绕过局域网域名", "GEOSITE", "private", "DIRECT"),
            Rule("绕过中国公共 DNS IP", "IP-CIDR", chinaDnsIp, "DIRECT"),
            Rule("绕过中国公共 DNS 域名", "DOMAIN-SUFFIX", "alidns.com,doh.pub,dot.pub,360.cn,onedns.net", "DIRECT"),
            Rule("绕过中国 IP", "GEOIP", "cn", "DIRECT"),
            Rule("绕过中国域名", "GEOSITE", "cn", "DIRECT"),
        ]);

        var blacklist = new RoutingPolicy
        {
            Id = "v2rayn-v4-blacklist",
            Name = "V4-黑名单 (Blacklist)",
            FinalOutbound = "DIRECT",
            Rules = CreateTruckersMpRules(),
        };
        blacklist.Rules.AddRange(
        [
            Rule("绕过 BitTorrent", "PROTOCOL", "bittorrent", "DIRECT"),
            Rule("代理 api.ip.sb", "DOMAIN", "api.ip.sb", "MQVPN-TUN"),
            Rule("代理 Google", "GEOSITE", "google", "MQVPN-TUN"),
            Rule("绕过局域网 IP", "GEOIP", "private", "DIRECT"),
            Rule("绕过局域网域名", "GEOSITE", "private", "DIRECT"),
            Rule("代理海外公共 DNS IP", "IP-CIDR", overseasDnsIp, "MQVPN-TUN"),
            Rule("代理海外公共 DNS 域名", "DOMAIN-SUFFIX",
                "cloudflare-dns.com,one.one.one.one,dns.google,adguard-dns.com,opendns.com,umbrella.com,quad9.net,yandex.net",
                "MQVPN-TUN"),
            Rule("代理海外 IP", "GEOIP", "facebook,fastly,google,netflix,telegram,twitter", "MQVPN-TUN"),
            Rule("代理 GFW", "GEOSITE", "gfw,greatfire", "MQVPN-TUN"),
        ]);

        var global = new RoutingPolicy
        {
            Id = "v2rayn-v4-global",
            Name = "V4-全局 (Global)",
            FinalOutbound = "MQVPN-TUN",
            Rules = CreateTruckersMpRules(),
        };
        global.Rules.AddRange(
        [
            Rule("绕过局域网 IP", "GEOIP", "private", "DIRECT"),
            Rule("绕过局域网域名", "GEOSITE", "private", "DIRECT"),
        ]);

        return [whitelist, blacklist, global];
    }

    private static RoutingRule Rule(string name, string type, string value, string outbound, string network = "TCP/UDP") => new()
    {
        Name = name,
        Type = type,
        Value = value,
        Network = network,
        Outbound = outbound,
    };

    private void NormalizeProfilePolicies()
    {
        foreach (var profile in State.Profiles)
        {
            if (!Routing.Policies.Any(policy => policy.Id == profile.RoutingPolicyId))
                profile.RoutingPolicyId = Routing.Policies[0].Id;
        }
    }

    private static string ParseHost(string serverAddress)
    {
        if (serverAddress.StartsWith('['))
        {
            var end = serverAddress.IndexOf(']');
            return end > 1 ? serverAddress[1..end] : serverAddress;
        }
        var separator = serverAddress.LastIndexOf(':');
        return separator > 0 ? serverAddress[..separator] : serverAddress;
    }

    private static string ValidateRuleValue(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Any(char.IsControl))
            throw new InvalidDataException($"无效的路由规则值: {value}");
        return trimmed;
    }

    private static string ValidateToken(string value, string[] allowed)
    {
        if (!allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"不支持的路由值: {value}");
        return allowed.First(item => item.Equals(value, StringComparison.OrdinalIgnoreCase));
    }
}
