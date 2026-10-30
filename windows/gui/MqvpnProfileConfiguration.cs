// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TruckersMP.Proxy;

internal sealed record ProfileConfigField(string Path, object DefaultValue,
    decimal Minimum = 0, decimal Maximum = 0, string[]? Choices = null,
    int MaxUtf8Bytes = 0, bool Optional = false)
{
    public bool IsInteger => DefaultValue is not (string or bool);
    public JsonNode DefaultNode => JsonSerializer.SerializeToNode(DefaultValue)!;
}

// Client fields actually consumed by the CLI -> shared bridge -> Windows.
// Linux-only and server-only keys are deliberately not edited or injected.
internal static class MqvpnProfileConfiguration
{
    public static readonly ProfileConfigField[] Fields =
    [
        new("mode", "client", Choices: ["client"]),
        new("server_addr", "", MaxUtf8Bytes: 279),
        new("tls_server_name", "", MaxUtf8Bytes: 255),
        new("auth_key", "", MaxUtf8Bytes: 255),
        new("insecure", false), new("tun_name", "mqvpn0", MaxUtf8Bytes: 31),
        new("log_level", "info", Choices: LogLevels.All),
        new("manage_routes", true), new("kill_switch", false), new("reconnect", true),
        new("reconnect_interval", 5, 1, int.MaxValue),
        new("scheduler", "wlb", Choices: ["wlb", "minrtt", "wlb_udp_pin", "backup_fec"]),
        new("request_server_scheduler", false),
        new("cc", "bbr2", Choices: ["bbr2", "bbr", "cubic", "none"]),
        new("init_max_path_id", 0, 0, 128),
        new("mtu", 0, 0, 9000, Optional: true),
        new("pmtud", false), new("path_mtu", 0, 0, 9000),
        new("reinjection", "off", Choices: ["off", "deadline", "idle"]),
        new("reinjection_srtt_factor_pct", 110, 100, 1000),
        new("reinjection_hard_deadline_ms", 500, 1, 60000),
        new("reinjection_deadline_lower_bound_ms", 20, 1, 60000),
        new("redundant", false), new("redundant_ports", "", MaxUtf8Bytes: 255),
        new("hybrid.enabled", false), new("hybrid.tcp", "auto", Choices: ["auto", "stream", "raw"]),
        new("hybrid.tcp_max_flows", 256, 1, 4096),
        new("hybrid.tcp_idle_timeout_sec", 300, 0, uint.MaxValue),
        new("reorder.enabled", false),
        new("reorder.max_wait_ms", 30, 0, uint.MaxValue, Optional: true),
        new("reorder.cap_packets", 1024, 1, uint.MaxValue, Optional: true),
        new("reorder.max_bytes_per_flow", 1572864, 0, ulong.MaxValue),
        new("reorder.classify_window", 64, 0, ushort.MaxValue),
        new("reorder.ack_demote_max_large", 3, 0, ushort.MaxValue),
        new("reorder.small_packet_threshold", 200, 0, uint.MaxValue),
        new("reorder.reset_mark_packets", 8, 0, uint.MaxValue),
        new("reorder.reset_idle_grace_ms", 10000, 0, uint.MaxValue),
        new("reorder.max_flows", 65536, 0, uint.MaxValue),
        new("reorder.global_max_bytes", 67108864, 0, ulong.MaxValue),
        new("reorder.ingress_idle_sec", 30, 0, uint.MaxValue),
        new("reorder.egress_idle_sec", 300, 0, uint.MaxValue),
        new("advanced.recv_rate_limit", 0, 0, 10_000_000_000),
    ];

    public static readonly string[] ReorderProfiles = ["quic_bulk", "low_latency", "default_udp", "cellular_bond", "fiber_lte"];

    public static ProfileConfigField Field(string path) => Fields.Single(f => f.Path == path);
    public static JsonNode? Get(JsonObject config, string path)
    {
        JsonNode? node = config;
        foreach (var key in path.Split('.'))
            node = (node as JsonObject)?[key];
        return node;
    }

    public static void Set(JsonObject config, string path, JsonNode? value)
    {
        var keys = path.Split('.');
        var target = config;
        foreach (var key in keys[..^1])
        {
            if (target[key] is not JsonObject obj)
                target[key] = obj = new JsonObject();
            target = obj;
        }
        if (value is null) target.Remove(keys[^1]);
        else target[keys[^1]] = value.DeepClone();
    }

    public static JsonObject CreateDefault()
    {
        var config = new JsonObject();
        foreach (var field in Fields.Where(f => !f.Optional))
            Set(config, field.Path, field.DefaultNode);
        config["dns"] = new JsonArray("1.1.1.1", "8.8.8.8");
        config["paths"] = new JsonArray();
        // GUI owns route policy by default; an imported missing key still
        // means the CLI default true, and must not be silently reinterpreted.
        config["manage_routes"] = false;
        return config;
    }

    public static decimal Number(JsonObject config, string path) =>
        decimal.Parse((Get(config, path) ?? Field(path).DefaultNode).ToJsonString(), CultureInfo.InvariantCulture);

    public static bool Boolean(JsonObject config, string path) =>
        (Get(config, path) ?? Field(path).DefaultNode).GetValue<bool>();

    public static void Validate(JsonObject config, bool runtime = false)
    {
        foreach (var group in new[] { "hybrid", "reorder", "advanced" })
            if (config.ContainsKey(group) && config[group] is not JsonObject)
                throw new InvalidDataException($"{group} 必须是 JSON 对象。");
        foreach (var field in Fields)
        {
            var node = Get(config, field.Path);
            if (node is null)
            {
                // A present null is not a missing/default value.
                var keys = field.Path.Split('.');
                var parent = keys.Length == 1 ? config : config[keys[0]] as JsonObject;
                if (parent?.ContainsKey(keys[^1]) == true)
                    throw new InvalidDataException($"{field.Path} 不能为 null。");
                continue;
            }
            if (field.IsInteger)
            {
                var text = node.ToJsonString();
                if (text.Length == 0 || text.Any(c => c is < '0' or > '9') ||
                    !decimal.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
                    number < field.Minimum || number > field.Maximum)
                    throw new InvalidDataException($"{field.Path} 必须是 {field.Minimum}–{field.Maximum} 的 JSON 整数。");
            }
            else if (field.DefaultValue is bool)
            {
                if (node is not JsonValue value || !value.TryGetValue<bool>(out _))
                    throw new InvalidDataException($"{field.Path} 必须是 JSON boolean (true/false)。");
            }
            else
            {
                if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
                    throw new InvalidDataException($"{field.Path} 必须是 JSON 字符串。");
                if (text.Contains('\0') || (field.MaxUtf8Bytes > 0 && Encoding.UTF8.GetByteCount(text) > field.MaxUtf8Bytes))
                    throw new InvalidDataException($"{field.Path} 最多 {field.MaxUtf8Bytes} 个 UTF-8 字节，且不能含 NUL。");
                if (field.Choices is not null && !field.Choices.Contains(text, StringComparer.Ordinal))
                    throw new InvalidDataException($"{field.Path} 不支持 '{text}'；请选择 {string.Join(" / ", field.Choices)}。");
            }
        }
        var endpoint = (config["server_addr"] as JsonValue)?.GetValue<string>() ?? "";
        if (!TryParseEndpoint(endpoint, out _, out _))
            throw new InvalidDataException("服务器地址必须为 host:port 或 [IPv6]:port，端口 1–65535，主机最多 255 个 UTF-8 字节。");
        if (string.IsNullOrWhiteSpace(config["auth_key"]?.GetValue<string>()))
            throw new InvalidDataException("请填写认证密钥 auth_key。");
        if (string.IsNullOrWhiteSpace((config["tun_name"] ?? Field("tun_name").DefaultNode).GetValue<string>()))
            throw new InvalidDataException("TUN 名称不能为空（最多 31 个 UTF-8 字节）。");
        if (((config["tun_name"] ?? Field("tun_name").DefaultNode).GetValue<string>()).Any(char.IsControl))
            throw new InvalidDataException("TUN 名称不能包含控制字符。");
        foreach (var path in new[] { "mtu", "path_mtu" })
            if (Number(config, path) is > 0 and < 1280)
                throw new InvalidDataException($"{path} / PathMTU 必须为 0 或 1280–9000。");
        ValidateStrings(config, "dns", 4, false, s => IPAddress.TryParse(s, out _));
        ValidateStrings(config, "paths", 8, runtime, s => !string.IsNullOrWhiteSpace(s) && Encoding.UTF8.GetByteCount(s) <= 31 && !s.Any(char.IsControl));
        if (Number(config, "reinjection_deadline_lower_bound_ms") > Number(config, "reinjection_hard_deadline_ms"))
            throw new InvalidDataException("Reinjection 截止下限不能大于硬截止时间。");
        if (!IsPowerOfTwo(Number(config, "reorder.cap_packets")))
            throw new InvalidDataException("reorder.cap_packets 必须是非零的 2 的幂。");
        if (Number(config, "reorder.ingress_idle_sec") >= Number(config, "reorder.egress_idle_sec"))
            throw new InvalidDataException("reorder.ingress_idle_sec 必须小于 egress_idle_sec。");
        ValidateReorderRules(config);
        if (Boolean(config, "redundant"))
        {
            var text = config["redundant_ports"]?.GetValue<string>() ?? "";
            var ports = text.Length == 0 ? [] : text.Split(',');
            if (ports.Length > 16)
                throw new InvalidDataException("RedundantPorts 最多支持 16 个端口。");
            foreach (var port in ports)
                if (!ushort.TryParse(port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n == 0)
                    throw new InvalidDataException($"RedundantPorts 包含无效端口 {port}（1–65535）。");
        }
    }

    private static void ValidateReorderRules(JsonObject config)
    {
        if (!config.ContainsKey("reorder_rules")) return;
        if (config["reorder_rules"] is not JsonArray rules || rules.Count > 16)
            throw new InvalidDataException("reorder_rules 必须是最多 16 项的 JSON 数组。");
        var ports = new HashSet<uint>();
        foreach (var node in rules)
        {
            if (node is not JsonObject rule ||
                (rule.ContainsKey("proto") && rule["proto"]?.ToJsonString() != "\"udp\"") ||
                (rule.ContainsKey("profile") && (rule["profile"] is not JsonValue profile || !profile.TryGetValue<string>(out var name) || !ReorderProfiles.Contains(name))))
                throw new InvalidDataException("reorder_rules 的 proto 必须是 udp，profile 必须是已知预设。");
            if (!ReadRuleNumber(rule, "port", out var port) || port is < 1 or > 65535 || !ports.Add(port))
                throw new InvalidDataException("GUI 重排规则端口必须为 1–65535，且不能重复。");
            foreach (var key in new[] { "max_wait_ms", "cap_packets" })
                if (rule.ContainsKey(key) && (!ReadRuleNumber(rule, key, out var n) || n == 0 || (key == "cap_packets" && !IsPowerOfTwo(n))))
                    throw new InvalidDataException($"reorder_rules.{key} 必须是非零整数；cap_packets 必须是 2 的幂。");
        }
    }

    private static bool ReadRuleNumber(JsonObject rule, string key, out uint number) =>
        uint.TryParse(rule[key]?.ToJsonString(), NumberStyles.None, CultureInfo.InvariantCulture, out number);

    public static bool IsPowerOfTwo(decimal number) => number is > 0 and <= uint.MaxValue &&
        ((uint)number & ((uint)number - 1)) == 0;

    private static void ValidateStrings(JsonObject config, string key, int maximum, bool required, Func<string, bool> valid)
    {
        if (!config.ContainsKey(key) && !required) return;
        if (config[key] is not JsonArray values || values.Count > maximum || (required && values.Count == 0) ||
            values.Any(n => n is not JsonValue v || !v.TryGetValue<string>(out var text) || !valid(text)))
            throw new InvalidDataException($"{key} 必须是有效的 JSON 字符串数组，最多 {maximum} 项；Windows 运行时至少需要一条 paths。");
    }

    public static bool TryParseEndpoint(string endpoint, out string host, out int port)
    {
        host = ""; port = 0;
        var colon = endpoint.LastIndexOf(':');
        if (colon < 1) return false;
        if (endpoint.StartsWith('['))
        {
            if (colon < 3 || endpoint[colon - 1] != ']') return false;
            host = endpoint[1..(colon - 1)];
            if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return false;
        }
        else
        {
            host = endpoint[..colon];
            if (host.Contains(':') || host.Contains('[') || host.Contains(']')) return false;
        }
        return !host.Any(char.IsWhiteSpace) && !host.Contains('\0') && Encoding.UTF8.GetByteCount(host) is > 0 and <= 255 &&
            int.TryParse(endpoint[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
    }
}
