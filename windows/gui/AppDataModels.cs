using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SQLite;
using TruckersMP.Proxy.Database;

namespace TruckersMP.Proxy;

internal sealed class AppState
{
    public string ActiveProfileId { get; set; } = string.Empty;
    public string GuiUpdateSource { get; set; } = "r11234567/mqvpn";
    public string MqvpnUpdateSource { get; set; } = "r11234567/mqvpn";
    public string XrayUpdateSource { get; set; } = "XTLS/Xray-core";
    public string GeoDataUpdateSource { get; set; } = "Loyalsoldier/v2ray-rules-dat";
    public bool GuiIncludePreRelease { get; set; }
    public bool MqvpnIncludePreRelease { get; set; }
    public bool XrayIncludePreRelease { get; set; }
    public bool AutoCheckUpdatesOnStartup { get; set; }
    public string LogLevel { get; set; } = LogLevels.Default;
    public bool StartMinimized { get; set; }
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public string LatencyProbeEndpoint { get; set; } = "1.1.1.1:80";
    public int SpeedTestTimeoutMs { get; set; } = 3000;
    public string ThemeMode { get; set; } = "system";
    public bool DoubleClickToActivate { get; set; }
    public bool AllowLan { get; set; }
    public bool AutoReconnect { get; set; } = true;
    public int ReconnectIntervalSec { get; set; } = 5;

    // 高级网络与代理端口
    public int LocalSocksPort { get; set; } = 10808;
    public int LocalHttpPort { get; set; } = 10809;
    public bool EnableUdp { get; set; } = true;
    public bool EnableSniffing { get; set; } = true;
    public bool RouteOnly { get; set; } = true;
    public bool DisplayRealTimeSpeed { get; set; } = true;
    public bool ConfirmOnExit { get; set; }
    public string SpeedTestUrl { get; set; } = "http://cachefly.cachefly.net/10mb.test";
    public int MixedConcurrencyCount { get; set; } = 5;

    // DNS 科学解析设置
    public string DirectDns { get; set; } = "223.5.5.5, 119.29.29.29, 114.114.114.114";
    public string RemoteDns { get; set; } = "https://1.1.1.1/dns-query, 8.8.8.8, 1.0.0.1";
    public string BootstrapDns { get; set; } = "223.5.5.5, 1.1.1.1";
    public string DomainStrategy { get; set; } = "IPIfNonMatch";
    public bool FakeDnsEnabled { get; set; }
    public string FakeDnsRange { get; set; } = "198.18.0.0/15";
    public bool BlockAAAAQuery { get; set; }
    public string CustomHosts { get; set; } = string.Empty;

    public List<ProxyProfile> Profiles { get; set; } = [];

    public Dictionary<string, string> ToSettingsMap() => new()
    {
        [nameof(ActiveProfileId)] = ActiveProfileId ?? string.Empty,
        [nameof(GuiUpdateSource)] = GuiUpdateSource ?? "r11234567/mqvpn",
        [nameof(MqvpnUpdateSource)] = MqvpnUpdateSource ?? "r11234567/mqvpn",
        [nameof(XrayUpdateSource)] = XrayUpdateSource ?? "XTLS/Xray-core",
        [nameof(GeoDataUpdateSource)] = GeoDataUpdateSource ?? "Loyalsoldier/v2ray-rules-dat",
        [nameof(GuiIncludePreRelease)] = GuiIncludePreRelease.ToString(),
        [nameof(MqvpnIncludePreRelease)] = MqvpnIncludePreRelease.ToString(),
        [nameof(XrayIncludePreRelease)] = XrayIncludePreRelease.ToString(),
        [nameof(AutoCheckUpdatesOnStartup)] = AutoCheckUpdatesOnStartup.ToString(),
        [nameof(LogLevel)] = LogLevel ?? LogLevels.Default,
        [nameof(StartMinimized)] = StartMinimized.ToString(),
        [nameof(MinimizeToTrayOnClose)] = MinimizeToTrayOnClose.ToString(),
        [nameof(LatencyProbeEndpoint)] = LatencyProbeEndpoint ?? "1.1.1.1:80",
        [nameof(SpeedTestTimeoutMs)] = SpeedTestTimeoutMs.ToString(),
        [nameof(ThemeMode)] = ThemeMode ?? "system",
        [nameof(DoubleClickToActivate)] = DoubleClickToActivate.ToString(),
        [nameof(AllowLan)] = AllowLan.ToString(),
        [nameof(AutoReconnect)] = AutoReconnect.ToString(),
        [nameof(ReconnectIntervalSec)] = ReconnectIntervalSec.ToString(),

        [nameof(LocalSocksPort)] = LocalSocksPort.ToString(),
        [nameof(LocalHttpPort)] = LocalHttpPort.ToString(),
        [nameof(EnableUdp)] = EnableUdp.ToString(),
        [nameof(EnableSniffing)] = EnableSniffing.ToString(),
        [nameof(RouteOnly)] = RouteOnly.ToString(),
        [nameof(DisplayRealTimeSpeed)] = DisplayRealTimeSpeed.ToString(),
        [nameof(ConfirmOnExit)] = ConfirmOnExit.ToString(),
        [nameof(SpeedTestUrl)] = SpeedTestUrl ?? "http://cachefly.cachefly.net/10mb.test",
        [nameof(MixedConcurrencyCount)] = MixedConcurrencyCount.ToString(),

        [nameof(DirectDns)] = DirectDns ?? string.Empty,
        [nameof(RemoteDns)] = RemoteDns ?? string.Empty,
        [nameof(BootstrapDns)] = BootstrapDns ?? string.Empty,
        [nameof(DomainStrategy)] = DomainStrategy ?? "IPIfNonMatch",
        [nameof(FakeDnsEnabled)] = FakeDnsEnabled.ToString(),
        [nameof(FakeDnsRange)] = FakeDnsRange ?? "198.18.0.0/15",
        [nameof(BlockAAAAQuery)] = BlockAAAAQuery.ToString(),
        [nameof(CustomHosts)] = CustomHosts ?? string.Empty,
    };

    public void LoadFromSettingsMap(Dictionary<string, string> map)
    {
        if (map.TryGetValue(nameof(ActiveProfileId), out var activeId)) ActiveProfileId = activeId;
        if (map.TryGetValue(nameof(GuiUpdateSource), out var guiSrc)) GuiUpdateSource = guiSrc;
        if (map.TryGetValue(nameof(MqvpnUpdateSource), out var mqvpnSrc)) MqvpnUpdateSource = mqvpnSrc;
        if (map.TryGetValue(nameof(XrayUpdateSource), out var xraySrc)) XrayUpdateSource = xraySrc;
        if (map.TryGetValue(nameof(GeoDataUpdateSource), out var geoSrc)) GeoDataUpdateSource = geoSrc;
        if (map.TryGetValue(nameof(GuiIncludePreRelease), out var gPre) && bool.TryParse(gPre, out var bgPre)) GuiIncludePreRelease = bgPre;
        if (map.TryGetValue(nameof(MqvpnIncludePreRelease), out var mPre) && bool.TryParse(mPre, out var bmPre)) MqvpnIncludePreRelease = bmPre;
        if (map.TryGetValue(nameof(XrayIncludePreRelease), out var xPre) && bool.TryParse(xPre, out var bxPre)) XrayIncludePreRelease = bxPre;
        if (map.TryGetValue(nameof(AutoCheckUpdatesOnStartup), out var autoUp) && bool.TryParse(autoUp, out var bAutoUp)) AutoCheckUpdatesOnStartup = bAutoUp;
        if (map.TryGetValue(nameof(LogLevel), out var logLvl)) LogLevel = logLvl;
        if (map.TryGetValue(nameof(StartMinimized), out var startMin) && bool.TryParse(startMin, out var bStartMin)) StartMinimized = bStartMin;
        if (map.TryGetValue(nameof(MinimizeToTrayOnClose), out var minTray) && bool.TryParse(minTray, out var bMinTray)) MinimizeToTrayOnClose = bMinTray;
        if (map.TryGetValue(nameof(LatencyProbeEndpoint), out var probe)) LatencyProbeEndpoint = probe;
        if (map.TryGetValue(nameof(SpeedTestTimeoutMs), out var spdTo) && int.TryParse(spdTo, out var ispdTo)) SpeedTestTimeoutMs = ispdTo;
        if (map.TryGetValue(nameof(ThemeMode), out var theme)) ThemeMode = theme;
        if (map.TryGetValue(nameof(DoubleClickToActivate), out var dblAct) && bool.TryParse(dblAct, out var bdblAct)) DoubleClickToActivate = bdblAct;
        if (map.TryGetValue(nameof(AllowLan), out var lan) && bool.TryParse(lan, out var blan)) AllowLan = blan;
        if (map.TryGetValue(nameof(AutoReconnect), out var reconn) && bool.TryParse(reconn, out var breconn)) AutoReconnect = breconn;
        if (map.TryGetValue(nameof(ReconnectIntervalSec), out var recInt) && int.TryParse(recInt, out var irecInt)) ReconnectIntervalSec = irecInt;

        if (map.TryGetValue(nameof(LocalSocksPort), out var lsp) && int.TryParse(lsp, out var ilsp)) LocalSocksPort = ilsp;
        if (map.TryGetValue(nameof(LocalHttpPort), out var lhp) && int.TryParse(lhp, out var ilhp)) LocalHttpPort = ilhp;
        if (map.TryGetValue(nameof(EnableUdp), out var udp) && bool.TryParse(udp, out var budp)) EnableUdp = budp;
        if (map.TryGetValue(nameof(EnableSniffing), out var snf) && bool.TryParse(snf, out var bsnf)) EnableSniffing = bsnf;
        if (map.TryGetValue(nameof(RouteOnly), out var ro) && bool.TryParse(ro, out var bro)) RouteOnly = bro;
        if (map.TryGetValue(nameof(DisplayRealTimeSpeed), out var drts) && bool.TryParse(drts, out var bdrts)) DisplayRealTimeSpeed = bdrts;
        if (map.TryGetValue(nameof(ConfirmOnExit), out var coe) && bool.TryParse(coe, out var bcoe)) ConfirmOnExit = bcoe;
        if (map.TryGetValue(nameof(SpeedTestUrl), out var stu)) SpeedTestUrl = stu;
        if (map.TryGetValue(nameof(MixedConcurrencyCount), out var mcc) && int.TryParse(mcc, out var imcc)) MixedConcurrencyCount = imcc;

        if (map.TryGetValue(nameof(DirectDns), out var ddns)) DirectDns = ddns;
        if (map.TryGetValue(nameof(RemoteDns), out var rdns)) RemoteDns = rdns;
        if (map.TryGetValue(nameof(BootstrapDns), out var bdns)) BootstrapDns = bdns;
        if (map.TryGetValue(nameof(DomainStrategy), out var ds)) DomainStrategy = ds;
        if (map.TryGetValue(nameof(FakeDnsEnabled), out var fde) && bool.TryParse(fde, out var bfde)) FakeDnsEnabled = bfde;
        if (map.TryGetValue(nameof(FakeDnsRange), out var fdr)) FakeDnsRange = fdr;
        if (map.TryGetValue(nameof(BlockAAAAQuery), out var baq) && bool.TryParse(baq, out var bbaq)) BlockAAAAQuery = bbaq;
        if (map.TryGetValue(nameof(CustomHosts), out var ch)) CustomHosts = ch;
    }
}

[Table("Profiles")]
internal sealed class ProxyProfile
{
    [PrimaryKey]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [Indexed]
    public int SortOrder { get; set; }

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

    public string ServerAddress { get; set; } = string.Empty;
    public int ServerPort { get; set; }
    public string Protocol { get; set; } = "mqvpn";
    public string Remarks { get; set; } = string.Empty;
    public string? ConfigContent { get; set; }
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
    private static readonly Encoding LegacyConfigEncoding = CreateLegacyConfigEncoding();
    private readonly List<string> _loadWarnings = [];

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
    public IReadOnlyList<string> LoadWarnings => _loadWarnings;
    public ProxyProfile ActiveProfile =>
        State.Profiles.First(profile => profile.Id == State.ActiveProfileId);

    public static ProfileStore Load(string configDirectory)
    {
        Directory.CreateDirectory(configDirectory);
        Directory.CreateDirectory(Path.Combine(configDirectory, "profiles"));
        var statePath = Path.Combine(configDirectory, "profiles.json");
        var routingPath = Path.Combine(configDirectory, "routing.json");
        var dbPath = Path.Combine(configDirectory, "mqvpn.db");

        SQLiteHelper.Instance.Initialize(dbPath);

        // SQLite owns node metadata/settings. JSON is a compatibility export and
        // first-run import; each node's file owns its configuration contents.
        var dbProfiles = SQLiteHelper.Instance.GetAllProfiles();
        var state = dbProfiles.Count > 0 ? new AppState { Profiles = dbProfiles } : File.Exists(statePath)
            ? JsonSerializer.Deserialize<AppState>(File.ReadAllText(statePath), JsonOptions) ?? new AppState()
            : CreateInitialState(configDirectory);
        var routing = File.Exists(routingPath)
            ? JsonSerializer.Deserialize<RoutingDocument>(File.ReadAllText(routingPath), JsonOptions) ?? CreateDefaultRouting()
            : CreateDefaultRouting();

        if (state.Profiles.Count == 0)
            state = CreateInitialState(configDirectory);

        // 优先从 SQLite 数据库同步 App 全局设置
        var dbSettings = SQLiteHelper.Instance.GetAllSettings();
        if (dbSettings.Count > 0)
        {
            state.LoadFromSettingsMap(dbSettings);
        }
        else
        {
            // 首次将 JSON 中的设置迁移至 SQLite
            SQLiteHelper.Instance.SaveAllSettings(state.ToSettingsMap());
        }

        if (!state.Profiles.Any(profile => profile.Id == state.ActiveProfileId))
            state.ActiveProfileId = state.Profiles[0].Id;
        if (routing.Policies.Count == 0)
            routing = CreateDefaultRouting();
        EnsureBuiltInPolicies(routing);

        var store = new ProfileStore(configDirectory, state, routing);
        store.NormalizeProfilePolicies();
        store.LoadConfigurations();
        store.Save();
        return store;
    }

    public static (string address, int port) ParseServerEndpoint(JsonObject? config)
    {
        if (config is null) return (string.Empty, 0);
        if (config.TryGetPropertyValue("server_addr", out var node) && node is not null)
        {
            var raw = node.ToString().Trim();
            var colonIndex = raw.LastIndexOf(':');
            if (colonIndex > 0 && int.TryParse(raw[(colonIndex + 1)..], out var port))
            {
                var host = raw[..colonIndex].Trim('[', ']');
                return (host, port);
            }
            return (raw, 0);
        }
        return (string.Empty, 0);
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

    // Same decoding as existing profiles; never writes the selected source.
    public static JsonObject ReadConfigForImport(string path) =>
        JsonNode.Parse(ReadMqvpnConfigFile(path)) as JsonObject
        ?? throw new InvalidDataException("导入配置必须是 JSON 对象。");

    public void SaveConfig(ProxyProfile profile, JsonObject config)
    {
        var path = GetConfigPath(profile);
        RecoverConfigSave(profile);
        var content = config.ToJsonString(JsonOptions);
        var oldContent = profile.ConfigContent;
        var oldAddress = profile.ServerAddress;
        var oldPort = profile.ServerPort;
        // A durable recovery point bridges the file replacement and SQLite
        // commit. They are deliberately not treated as one atomic transaction.
        var recovery = new ConfigSaveRecovery(content,
            File.Exists(path) ? Convert.ToBase64String(File.ReadAllBytes(path)) : null);
        var recoveryPath = path + ".pending-save";
        AtomicFile.WriteText(recoveryPath, JsonSerializer.Serialize(recovery));
        var (addr, port) = ParseServerEndpoint(config);
        try
        {
            AtomicFile.WriteText(path, content);
            profile.ServerAddress = addr;
            profile.ServerPort = port;
            profile.ConfigContent = content;
            SQLiteHelper.Instance.SaveProfile(profile);
        }
        catch
        {
            profile.ServerAddress = oldAddress;
            profile.ServerPort = oldPort;
            profile.ConfigContent = oldContent;
            RestoreConfig(path, recovery);
            File.Delete(recoveryPath);
            throw;
        }
        // Once committed, an interrupted cleanup is completed during Load.
        File.Delete(recoveryPath);
    }

    public static void WriteMqvpnConfigFile(string path, JsonObject config) =>
        AtomicFile.WriteText(path, config.ToJsonString(JsonOptions));

    private static string ReadMqvpnConfigFile(string path) => ReadMqvpnConfigFile(path, out _);

    private static string ReadMqvpnConfigFile(string path, out bool legacy)
    {
        var bytes = File.ReadAllBytes(path);
        legacy = false;
        try
        {
            var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
            return AtomicFile.Utf8.GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            legacy = true;
            return LegacyConfigEncoding.GetString(bytes);
        }
    }

    private static Encoding CreateLegacyConfigEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding((int)NativeMethods.GetACP(),
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        internal static extern uint GetACP();
    }

    private sealed record ConfigSaveRecovery(string NewContent, string? PreviousBytes);

    private static void RestoreConfig(string path, ConfigSaveRecovery recovery)
    {
        if (recovery.PreviousBytes is null) File.Delete(path);
        else AtomicFile.WriteBytes(path, Convert.FromBase64String(recovery.PreviousBytes));
    }

    private void RecoverConfigSave(ProxyProfile profile)
    {
        var path = GetConfigPath(profile);
        var recoveryPath = path + ".pending-save";
        if (!File.Exists(recoveryPath)) return;
        var recovery = JsonSerializer.Deserialize<ConfigSaveRecovery>(File.ReadAllText(recoveryPath))
            ?? throw new InvalidDataException($"配置 {profile.Name} 的保存恢复记录无效");
        if (profile.ConfigContent != recovery.NewContent)
            RestoreConfig(path, recovery);
        File.Delete(recoveryPath);
    }

    private void LoadConfigurations()
    {
        foreach (var profile in State.Profiles)
        {
            try
            {
                RecoverConfigSave(profile);
                var path = GetConfigPath(profile);
                var text = ReadMqvpnConfigFile(path, out var legacy);
                var config = JsonNode.Parse(text) as JsonObject
                    ?? throw new InvalidDataException("根节点必须是 JSON 对象");
                if (legacy)
                {
                    // Keep the exact original bytes, including characters the
                    // old writer may already have lost; never overwrite backup.
                    File.Copy(path, path + $".legacy-{Guid.NewGuid():N}.bak");
                    SaveConfig(profile, config);
                }
                else
                {
                    (profile.ServerAddress, profile.ServerPort) = ParseServerEndpoint(config);
                    profile.ConfigContent = config.ToJsonString(JsonOptions);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       JsonException or DecoderFallbackException or InvalidOperationException or FormatException)
            {
                _loadWarnings.Add($"节点“{profile.Name}” ({profile.ConfigFile}): {ex.Message}；原文件保留，请修正后重试。");
            }
        }
    }

    public ProxyProfile Add(string name, JsonObject config, string routingPolicyId)
    {
        var (addr, port) = ParseServerEndpoint(config);
        var profile = new ProxyProfile
        {
            Name = name,
            RoutingPolicyId = routingPolicyId,
            SortOrder = State.Profiles.Count,
            ServerAddress = addr,
            ServerPort = port,
            ConfigContent = config.ToJsonString(JsonOptions),
        };
        profile.ConfigFile = Path.Combine("profiles", $"{profile.Id}.json");
        SaveConfig(profile, config);
        State.Profiles.Add(profile);
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
        SQLiteHelper.Instance.SaveProfile(clone);
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
        SQLiteHelper.Instance.DeleteProfile(profile.Id);
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
        for (var i = 0; i < State.Profiles.Count; i++)
        {
            State.Profiles[i].SortOrder = i;
        }
        SQLiteHelper.Instance.SaveState(State.Profiles, State.ToSettingsMap());
        AtomicFile.WriteText(_statePath, JsonSerializer.Serialize(State, JsonOptions));
        AtomicFile.WriteText(_routingPath, JsonSerializer.Serialize(Routing, JsonOptions));
    }

    public void ReplaceRouting(RoutingDocument routing)
    {
        Routing.Policies.Clear();
        Routing.Policies.AddRange(routing.Policies);
        NormalizeProfilePolicies();
        Save();
    }

    public string ConfigDirectory => _configDirectory;

    public bool ExportBackup(string destinationZipPath)
    {
        try
        {
            Save();
            var dir = Path.GetDirectoryName(destinationZipPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(destinationZipPath))
                File.Delete(destinationZipPath);

            var tempDir = Path.Combine(Path.GetTempPath(), $"mqvpn-backup-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var filesToCopy = Directory.GetFiles(_configDirectory, "*.*", SearchOption.AllDirectories);
                foreach (var file in filesToCopy)
                {
                    if (file.Contains(Path.DirectorySeparatorChar + "backups" + Path.DirectorySeparatorChar))
                        continue;

                    var rel = Path.GetRelativePath(_configDirectory, file);
                    var dest = Path.Combine(tempDir, rel);
                    var d = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
                    File.Copy(file, dest, true);
                }
                ZipFile.CreateFromDirectory(tempDir, destinationZipPath, CompressionLevel.Optimal, false);
                return true;
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    try { Directory.Delete(tempDir, true); } catch { }
            }
        }
        catch
        {
            return false;
        }
    }

    public bool ImportBackup(string sourceZipPath)
    {
        try
        {
            if (!File.Exists(sourceZipPath))
                return false;

            using (var archive = ZipFile.OpenRead(sourceZipPath))
            {
                var hasValidEntries = archive.Entries.Any(e =>
                    e.FullName.EndsWith("mqvpn.db", StringComparison.OrdinalIgnoreCase) ||
                    e.FullName.EndsWith("profiles.json", StringComparison.OrdinalIgnoreCase) ||
                    e.FullName.EndsWith("routing.json", StringComparison.OrdinalIgnoreCase));
                if (!hasValidEntries)
                    return false;
            }

            var backupDir = Path.Combine(_configDirectory, "backups");
            Directory.CreateDirectory(backupDir);
            var safetyZip = Path.Combine(backupDir, $"backup_before_restore_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
            if (!ExportBackup(safetyZip)) return false;

            SQLiteHelper.Instance.Dispose();

            using (var archive = ZipFile.OpenRead(sourceZipPath))
            {
                var canonicalBase = Path.GetFullPath(_configDirectory) + Path.DirectorySeparatorChar;
                foreach (var entry in archive.Entries)
                {
                    if (entry.Length == 0 && (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')))
                        continue;
                    var destPath = Path.GetFullPath(Path.Combine(_configDirectory, entry.FullName));
                    if (!destPath.StartsWith(canonicalBase, StringComparison.OrdinalIgnoreCase))
                        continue; // Zip Slip path traversal protection
                    var d = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
                    entry.ExtractToFile(destPath, true);
                }
            }

            var dbPath = Path.Combine(_configDirectory, "mqvpn.db");
            SQLiteHelper.Instance.Initialize(dbPath);

            var reloaded = Load(_configDirectory);
            State.Profiles = reloaded.State.Profiles;
            State.ActiveProfileId = reloaded.State.ActiveProfileId;
            State.LoadFromSettingsMap(reloaded.State.ToSettingsMap());
            Routing.Policies.Clear();
            Routing.Policies.AddRange(reloaded.Routing.Policies);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void ResetToFactoryDefaults()
    {
        var backupDir = Path.Combine(_configDirectory, "backups");
        Directory.CreateDirectory(backupDir);
        var safetyZip = Path.Combine(backupDir, $"backup_before_reset_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.zip");
        if (!ExportBackup(safetyZip))
            throw new IOException("重置前备份失败，未执行重置。");

        var defaultPath = Path.Combine(_configDirectory, "client-windows.json");
        var previousDefault = File.Exists(defaultPath) ? File.ReadAllBytes(defaultPath) : null;
        var freshState = CreateInitialState(_configDirectory);
        var freshRouting = CreateDefaultRouting();
        var freshConfig = CreateDefaultMqvpnConfig();
        freshState.Profiles[0].ConfigContent = freshConfig.ToJsonString(JsonOptions);
        try
        {
            WriteMqvpnConfigFile(defaultPath, freshConfig);
            SQLiteHelper.Instance.SaveState(freshState.Profiles, freshState.ToSettingsMap(), replace: true);
        }
        catch (Exception ex)
        {
            if (previousDefault is not null) AtomicFile.WriteBytes(defaultPath, previousDefault);
            else File.Delete(defaultPath);
            throw new IOException($"重置失败，原配置已保留。恢复备份: {safetyZip}", ex);
        }

        State.Profiles = freshState.Profiles;
        State.LoadFromSettingsMap(freshState.ToSettingsMap());

        Routing.Policies.Clear();
        Routing.Policies.AddRange(freshRouting.Policies);

        Save();
    }

    public RoutingPolicy GetPolicy(string id) =>
        Routing.Policies.FirstOrDefault(policy => policy.Id == id) ?? Routing.Policies[0];

    public JsonObject GetRuntimeConfig(ProxyProfile profile, Func<string>? resolveDefault = null)
    {
        var config = ReadConfig(profile);
        if (profile.UseDefaultPath)
            config["paths"] = new JsonArray((resolveDefault ?? GetDefaultPhysicalInterface)());
        if (config["paths"] is not JsonArray { Count: > 0 })
            throw new InvalidOperationException("Windows 连接需要至少一张物理网卡，请在节点设置中选择网卡或启用默认网卡模式。");
        MqvpnProfileConfiguration.Validate(config, runtime: true);
        RoutingSemantics.ValidateCoreRouteManagement(config, GetPolicy(profile.RoutingPolicyId));
        return config;
    }

    public string GenerateXrayConfig(ProxyProfile profile, string outputPath, JsonObject? runtimeConfig = null)
    {
        var config = runtimeConfig ?? ReadConfig(profile);
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

        var inbounds = new JsonArray
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
                    ["dns"] = BuildTunDnsServers(State),
                    ["autoSystemRoutingTable"] = new JsonArray("0.0.0.0/0", "::/0"),
                    ["autoOutboundsInterface"] = "auto",
                },
                ["sniffing"] = new JsonObject
                {
                    ["enabled"] = State.EnableSniffing,
                    ["destOverride"] = new JsonArray("http", "tls", "quic"),
                    ["routeOnly"] = State.RouteOnly,
                },
            },
        };

        if (State.LocalSocksPort > 0)
        {
            inbounds.Add(new JsonObject
            {
                ["tag"] = "socks-in",
                ["port"] = State.LocalSocksPort,
                ["listen"] = State.AllowLan ? "0.0.0.0" : "127.0.0.1",
                ["protocol"] = "socks",
                ["settings"] = new JsonObject
                {
                    ["auth"] = "noauth",
                    ["udp"] = State.EnableUdp,
                },
                ["sniffing"] = new JsonObject
                {
                    ["enabled"] = State.EnableSniffing,
                    ["destOverride"] = new JsonArray("http", "tls", "quic"),
                    ["routeOnly"] = State.RouteOnly,
                },
            });
        }

        if (State.LocalHttpPort > 0)
        {
            inbounds.Add(new JsonObject
            {
                ["tag"] = "http-in",
                ["port"] = State.LocalHttpPort,
                ["listen"] = State.AllowLan ? "0.0.0.0" : "127.0.0.1",
                ["protocol"] = "http",
                ["sniffing"] = new JsonObject
                {
                    ["enabled"] = State.EnableSniffing,
                    ["destOverride"] = new JsonArray("http", "tls", "quic"),
                    ["routeOnly"] = State.RouteOnly,
                },
            });
        }

        var dnsObject = new JsonObject();
        var dnsServers = new JsonArray();
        if (!string.IsNullOrWhiteSpace(State.RemoteDns))
        {
            foreach (var s in State.RemoteDns.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                dnsServers.Add(s);
            }
        }
        else
        {
            dnsServers.Add("https://1.1.1.1/dns-query");
            dnsServers.Add("8.8.8.8");
        }

        if (!string.IsNullOrWhiteSpace(State.DirectDns))
        {
            var directList = State.DirectDns.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (directList.Length > 0)
            {
                dnsServers.Add(new JsonObject
                {
                    ["address"] = directList[0],
                    ["domains"] = new JsonArray("geosite:cn"),
                    ["expectIPs"] = new JsonArray("geoip:cn"),
                });
            }
        }

        dnsObject["servers"] = dnsServers;
        if (State.BlockAAAAQuery)
        {
            dnsObject["queryStrategy"] = "UseIPv4";
        }

        if (!string.IsNullOrWhiteSpace(State.CustomHosts))
        {
            var hostsObj = new JsonObject();
            var lines = State.CustomHosts.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var line in lines)
            {
                var parts = line.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2)
                {
                    if (IPAddress.TryParse(parts[0], out _))
                        hostsObj[parts[1]] = parts[0];
                    else
                        hostsObj[parts[0]] = parts[1];
                }
            }
            if (hostsObj.Count > 0)
                dnsObject["hosts"] = hostsObj;
        }

        var root = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["loglevel"] = LogLevels.ToXray(State.LogLevel),
            },
            ["dns"] = dnsObject,
            ["inbounds"] = inbounds,
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
                ["domainStrategy"] = string.IsNullOrWhiteSpace(State.DomainStrategy) ? "IPIfNonMatch" : State.DomainStrategy,
                ["rules"] = rules,
            },
        };

        File.WriteAllText(outputPath, root.ToJsonString(JsonOptions), new UTF8Encoding(false));
        return outputPath;
    }

    private static JsonArray BuildTunDnsServers(AppState state)
    {
        var array = new JsonArray();
        if (!string.IsNullOrWhiteSpace(state.RemoteDns))
        {
            var items = state.RemoteDns.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var item in items)
            {
                if (IPAddress.TryParse(item, out var ip))
                {
                    array.Add(ip.ToString());
                    if (array.Count >= 2) break;
                }
            }
        }
        if (array.Count == 0)
        {
            array.Add("1.1.1.1");
            array.Add("2606:4700:4700::1111");
        }
        return array;
    }

    internal static string GetDefaultPhysicalInterface()
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

    public static JsonObject CreateDefaultMqvpnConfig() => MqvpnProfileConfiguration.CreateDefault();

    public static RoutingDocument CreateDefaultRouting()
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

    public static string ParseHost(string serverAddress)
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
