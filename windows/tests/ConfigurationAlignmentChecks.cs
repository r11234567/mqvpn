// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using TruckersMP.Proxy;
using TruckersMP.Proxy.Database;

internal static class ConfigurationAlignmentChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _checks;
    public static string EvidenceDirectory => Path.GetFullPath(Environment.GetEnvironmentVariable("MQVPN_CONFIG_EVIDENCE") ?? "build/config-fixtures");
    private static ProfileEditorForm Create(JsonObject config, bool defaultPath = false) => new("配置契约检查", "测试 节点", config,
        [new RoutingPolicy { Id = "global", Name = "全局", FinalOutbound = "MQVPN-TUN" }], "global", useDefaultPath: defaultPath);
    private static T Field<T>(object form, string name) => (T)form.GetType().GetField(name, Private)!.GetValue(form)!;
    private static Dictionary<string, IntegerInput> Numbers(ProfileEditorForm form) => Field<Dictionary<string, IntegerInput>>(form, "_numbers");
    private static void Require(bool result, string message)
    {
        if (!result) throw new InvalidOperationException(message);
        _checks++;
    }
    private static JsonObject Valid()
    {
        var config = ProfileStore.CreateDefaultMqvpnConfig();
        config["server_addr"] = "vpn.example:443";
        config["auth_key"] = "test-only-key";
        config["paths"] = new JsonArray("网卡 日本語 🌐");
        return config;
    }
    private static JsonObject Save(ProfileEditorForm form)
    {
        FluentUi.LastDialogMessage = null;
        typeof(ProfileEditorForm).GetMethod("SaveAndClose", Private)!.Invoke(form, null);
        Require(FluentUi.LastDialogMessage is null, FluentUi.LastDialogMessage ?? "save failed");
        return form.ResultConfig;
    }

    public static void Run()
    {
        FluentUi.EnsureApplication();
        FluentUi.SuppressDialogsForTesting = true;
        Directory.CreateDirectory(EvidenceDirectory);
        var original = Valid();
        original["custom_root"] = new JsonObject { ["value"] = ulong.MaxValue };
        original["tx_queue_len"] = 2048;
        original["tun_read_batch"] = 512;
        original["advanced"]!["udp_gso"] = false;
        original["hybrid"]!["tcp_connect_timeout_sec"] = 19;
        original["hybrid"]!["tcp_max_global_flows"] = 1234;
        original["hybrid"]!["egress_allow"] = new JsonArray("192.0.2.0/24");
        var before = original.DeepClone();
        using (var form = Create(original))
        {
            var saved = Save(form);
            Require(JsonNode.DeepEquals(saved, before), "unedited fields, Linux/server settings and unknown fields remain exact");
            Field<Wpf.Ui.Controls.TextBox>(form, "_name").Text = "另一个名字";
            Require(JsonNode.DeepEquals(Save(form), before), "profile rename cannot rewrite core config");
        }
        Require(JsonNode.DeepEquals(original, before), "save/cancel never mutate caller JSON");
        using (var form = Create(original))
        {
            Numbers(form)["init_max_path_id"].Text = "128";
            Field<Wpf.Ui.Controls.TextBox>(form, "_server").Text = "[2001:db8::1]:8443";
        }
        Require(JsonNode.DeepEquals(original, before), "cancel discards all edits");

        foreach (var spec in MqvpnProfileConfiguration.Fields.Where(f => f.IsInteger))
        {
            using var form = Create(Valid());
            var input = Numbers(form)[spec.Path];
            Require(input.Style?.TargetType == typeof(Wpf.Ui.Controls.TextBox), "integer input uses existing themed TextBox style");
            if (spec.Optional && spec.Path.StartsWith("reorder."))
                Field<CheckBox>(form, spec.Path.EndsWith("cap_packets") ? "_reorderCustomCap" : "_reorderCustomWait").IsChecked = true;
            if (spec.Path == "mtu") Field<CheckBox>(form, "_automaticMtu").IsChecked = false;
            foreach (var bad in new[] { "", "-1", "1.5", "1e3", (spec.Maximum + 1).ToString(CultureInfo.InvariantCulture) })
            {
                input.Text = bad;
                Require(!form.TryBuildConfiguration(out _, out var error) && error.Length > 0, $"{spec.Path}: reject '{bad}'");
            }
            input.Value = spec.Minimum;
            Require(input.Value == spec.Minimum, $"{spec.Path}: exact lower bound input");
            input.Value = spec.Maximum;
            Require(input.Value == spec.Maximum, $"{spec.Path}: exact upper bound input");
        }
        foreach (var (path, value) in new (string, JsonNode?)[]
        {
            ("init_max_path_id", JsonValue.Create(uint.MaxValue)), ("insecure", JsonValue.Create(1)),
            ("reconnect", JsonValue.Create("maybe")), ("hybrid", JsonValue.Create("wrong")),
            ("reorder.max_flows", JsonValue.Create("100")), ("cc", JsonValue.Create("future_cc")),
            ("server_addr", JsonValue.Create("host:0")), ("server_addr", JsonValue.Create("host:65536")),
            ("server_addr", JsonValue.Create("host:+443")), ("server_addr", JsonValue.Create("host:443/path")),
            ("server_addr", JsonValue.Create("2001:db8::1:443")), ("server_addr", JsonValue.Create("[no-ip]:443")),
            ("tun_name", JsonValue.Create(new string('中', 11))), ("auth_key", JsonValue.Create(new string('k', 256))),
            ("path_mtu", JsonValue.Create(1279)), ("reorder.cap_packets", JsonValue.Create(1000)),
            ("hybrid.tcp_max_flows", JsonValue.Create(4097)), ("advanced.recv_rate_limit", JsonValue.Create(10_000_000_001UL)),
            ("dns", new JsonArray("bad-dns")), ("paths", new JsonArray(Enumerable.Range(0, 9).Select(i => (JsonNode?)JsonValue.Create($"adapter{i}")).ToArray())),
            ("reorder_rules", new JsonArray(7)), ("insecure", null), ("tun_mtu", JsonValue.Create("invalid"))
        })
        {
            var cfg = Valid();
            MqvpnProfileConfiguration.Set(cfg, path, value);
            if (value is null) cfg[path] = null;
            using var form = Create(cfg);
            Require(!form.TryBuildConfiguration(out _, out _), $"invalid imported {path} must not become a silent default");
            Require(JsonNode.DeepEquals(form.ResultConfig, cfg), "failed save leaves config unchanged");
        }
        using (var form = Create(Valid()))
        {
            Field<CheckBox>(form, "_redundant").IsChecked = true;
            Require(Field<ComboBox>(form, "_congestion").SelectedItem?.ToString() == "bbr2", "redundancy must not override user's CC");
            Field<Wpf.Ui.Controls.TextBox>(form, "_redundantPorts").Text = "4200,9000";
            Field<CheckBox>(form, "_reorderEnabled").IsChecked = true;
            Require(form.TryBuildConfiguration(out _, out _), "zero rules accepted: core all-flow mode");
            form.ReorderRuleRows.Add(new ReorderRuleRow { Port = "443", Profile = "fiber_lte", MaxWaitMs = "50", CapPackets = "2048" });
            Require(form.TryBuildConfiguration(out _, out _), "valid rule accepted");
            form.ReorderRuleRows[0].MaxWaitMs = "0";
            Require(!form.TryBuildConfiguration(out _, out _), "per-rule zero wait rejected");
        }
        var legacy = Valid();
        legacy.Remove("mtu"); legacy["tun_mtu"] = 1400;
        legacy["reorder"]!["enabled"] = "on";
        using (var form = Create(legacy))
        {
            var migrated = Save(form);
            Require(migrated["mtu"]!.GetValue<ulong>() == 1400 && !migrated.ContainsKey("tun_mtu"), "legacy MTU migrated exactly");
            Require(migrated["reorder"]!["enabled"]!.GetValue<bool>(), "legacy boolean migrated to JSON bool");
        }
        var sparse = new JsonObject { ["server_addr"] = "127.0.0.1:443", ["auth_key"] = "test-only-key", ["paths"] = new JsonArray("Ethernet") };
        using (var form = Create(sparse)) Require(JsonNode.DeepEquals(Save(form), sparse), "missing defaults remain absent and use CLI defaults");
        Require(MqvpnProfileConfiguration.Boolean(sparse, "manage_routes"), "missing manage_routes uses actual CLI default true");
        try { RoutingSemantics.ValidateCoreRouteManagement(sparse, new RoutingPolicy()); Require(false, "split/core routing conflict must fail"); }
        catch (InvalidDataException) { _checks++; }

        var custom = original.DeepClone().AsObject();
        custom["paths"] = new JsonArray("网卡 日本語 🌐", "Ethernet");
        custom["dns"] = new JsonArray("1.1.1.1", "8.8.8.8", "2606:4700:4700::1111", "2001:4860:4860::8888");
        using (var form = Create(custom))
        {
            Field<Wpf.Ui.Controls.TextBox>(form, "_server").Text = "[2001:db8::123]:8443";
            Field<Wpf.Ui.Controls.TextBox>(form, "_tunName").Text = "隧道 空格";
            Field<CheckBox>(form, "_hybridEnabled").IsChecked = true;
            Field<CheckBox>(form, "_allowInsecure").IsChecked = true;
            Field<CheckBox>(form, "_manageRoutes").IsChecked = true;
            Field<CheckBox>(form, "_killSwitch").IsChecked = true;
            Field<CheckBox>(form, "_reconnect").IsChecked = false;
            Field<CheckBox>(form, "_redundant").IsChecked = true;
            Field<Wpf.Ui.Controls.TextBox>(form, "_redundantPorts").Text = "4200,9000";
            Field<ComboBox>(form, "_congestion").SelectedItem = "cubic";
            Field<ComboBox>(form, "_logLevel").SelectedItem = "debug";
            Field<ComboBox>(form, "_hybridTcp").SelectedItem = "stream";
            Field<ComboBox>(form, "_reinjection").SelectedItem = "deadline";
            Field<CheckBox>(form, "_requestServerScheduler").IsChecked = true;
            Field<CheckBox>(form, "_pmtud").IsChecked = true;
            Field<CheckBox>(form, "_reorderEnabled").IsChecked = true;
            Numbers(form)["reorder.max_bytes_per_flow"].Text = "9007199254740993";
            Numbers(form)["reorder.global_max_bytes"].Text = "18446744073709551615";
            Numbers(form)["advanced.recv_rate_limit"].Text = "10000000000";
            Numbers(form)["init_max_path_id"].Text = "128";
            Numbers(form)["path_mtu"].Text = "1492";
            Field<CheckBox>(form, "_reorderCustomWait").IsChecked = true;
            Numbers(form)["reorder.max_wait_ms"].Text = "150";
            form.ReorderRuleRows.Add(new ReorderRuleRow { Port = "443", Profile = "fiber_lte", CapPackets = "2048" });
            custom = Save(form).DeepClone().AsObject();
            Require(custom["reorder"]!["max_bytes_per_flow"]!.GetValue<ulong>() == 9007199254740993UL &&
                custom["reorder"]!["global_max_bytes"]!.GetValue<ulong>() == ulong.MaxValue, "uint64 remains exact across UI save");
        }
        using (var reopened = Create(JsonNode.Parse(custom.ToJsonString())!.AsObject()))
            Require(JsonNode.DeepEquals(Save(reopened), custom), "custom uint64 config survives reopen/resave");
        EmitRoundtrip("defaults", Valid());
        EmitRoundtrip("sparse", sparse);
        EmitRoundtrip("custom", custom);
        foreach (var mode in MqvpnProfileConfiguration.Field("scheduler").Choices!)
        {
            var cfg = Valid(); cfg["scheduler"] = mode;
            EmitRoundtrip($"scheduler-{mode}", cfg);
        }
        var bounds = Valid();
        bounds["paths"] = new JsonArray(Enumerable.Range(0, 8).Select(i => (JsonNode?)JsonValue.Create($"网卡 {i}")).ToArray());
        foreach (var spec in MqvpnProfileConfiguration.Fields.Where(f => f.IsInteger))
            MqvpnProfileConfiguration.Set(bounds, spec.Path, JsonValue.Create(decimal.ToUInt64(spec.Maximum)));
        bounds["reorder"]!["cap_packets"] = 2147483648U;
        bounds["reorder"]!["ingress_idle_sec"] = uint.MaxValue - 1;
        bounds["reinjection_deadline_lower_bound_ms"] = 60000;
        EmitRoundtrip("upper-bounds", bounds);
        var readme = Path.Combine(FindRoot(), "windows/examples/client.json");
        Require(File.Exists(readme), "README example exists");
        var importBytes = File.ReadAllBytes(readme);
        var importTime = File.GetLastWriteTimeUtc(readme);
        EmitRoundtrip("readme", ProfileStore.ReadConfigForImport(readme), defaultPath: true);
        Require(File.ReadAllBytes(readme).SequenceEqual(importBytes) && File.GetLastWriteTimeUtc(readme) == importTime,
            "production JSON import leaves source bytes/timestamp unchanged");

        var exe = Environment.GetEnvironmentVariable("MQVPN_CONFIG_CONTRACT_EXE");
        if (!string.IsNullOrEmpty(exe)) RunContract(exe);
        else if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            throw new InvalidOperationException("Native CI must run the actual C contract executable");
        else Console.WriteLine("C fixtures emitted; external Debug/sanitizer observer verification still required.");
        Console.WriteLine($"Configuration alignment: {_checks} checks passed; evidence: {EvidenceDirectory}");
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(Environment.CurrentDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CMakeLists.txt"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Run tests from the repository root");
    }

    private static void EmitRoundtrip(string name, JsonObject config, bool defaultPath = false)
    {
        using var form = Create(config, defaultPath);
        var saved = Save(form);
        var storeRoot = Path.Combine(Path.GetTempPath(), $"mqvpn contract 空格 {Guid.NewGuid():N}");
        try
        {
            var store = ProfileStore.Load(storeRoot);
            var profile = store.Add(name, saved, "global");
            profile.UseDefaultPath = defaultPath;
            store.Save();
            var loaded = store.ReadConfig(profile);
            Require(JsonNode.DeepEquals(saved, loaded), $"{name}: file/SQLite reload preserves JSON");
            Emit(name + "-saved", loaded);
            var runtime = store.GetRuntimeConfig(profile, () => "默认网卡 🌐");
            Emit(name + "-runtime", runtime);
            if (defaultPath) Require(loaded["paths"]!.AsArray().Count == 0 && runtime["paths"]!.AsArray().Count == 1, "default path resolved only at runtime");
            var backup = Path.Combine(storeRoot, "export.zip");
            Require(store.ExportBackup(backup), $"{name}: export succeeded");
            using var zip = ZipFile.OpenRead(backup);
            var entry = zip.Entries.Single(e => e.FullName.EndsWith(Path.GetFileName(store.GetConfigPath(profile)), StringComparison.OrdinalIgnoreCase));
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var exported = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
            Require(JsonNode.DeepEquals(loaded, exported), $"{name}: exported config exact");
            Emit(name + "-exported", exported);
        }
        finally
        {
            SQLiteHelper.Instance.Dispose();
            if (Directory.Exists(storeRoot)) Directory.Delete(storeRoot, true);
        }
    }

    private static void Emit(string name, JsonObject config) =>
        File.WriteAllText(Path.Combine(EvidenceDirectory, name + ".input.json"), config.ToJsonString(), new UTF8Encoding(false));

    private static void RunContract(string exe)
    {
        foreach (var input in Directory.GetFiles(EvidenceDirectory, "*.input.json"))
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            info.ArgumentList.Add(input);
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000)) { process.Kill(true); throw new TimeoutException("C config observer timed out"); }
            Task.WaitAll(stdout, stderr);
            Require(process.ExitCode == 0, $"C parser/bridge failed: {input}: {stderr.Result}");
            File.WriteAllText(input.Replace(".input.json", ".core.json"), stdout.Result, new UTF8Encoding(false));
        }
        VerifyContract(EvidenceDirectory);
    }

    public static void VerifyContract(string directory)
    {
        var count = 0;
        foreach (var input in Directory.GetFiles(directory, "*.input.json"))
        {
            count++;
            var config = JsonNode.Parse(File.ReadAllText(input))!.AsObject();
            var actual = JsonNode.Parse(File.ReadAllText(input.Replace(".input.json", ".core.json")))!.AsObject();
            foreach (var field in MqvpnProfileConfiguration.Fields)
            {
                var expected = MqvpnProfileConfiguration.Get(config, field.Path) ?? field.DefaultNode;
                foreach (var surface in new[] { "parser", "bridge" })
                {
                    if (surface == "bridge" && new[] { "mode", "server_addr", "tun_name", "manage_routes", "redundant_ports" }.Contains(field.Path)) continue;
                    var observed = MqvpnProfileConfiguration.Get(actual[surface]!.AsObject(), field.Path);
                    Require(JsonNode.DeepEquals(expected, observed), $"{Path.GetFileName(input)} {surface}.{field.Path}: expected {expected}, got {observed}");
                }
            }
            foreach (var key in new[] { "dns", "paths" })
                Require(JsonNode.DeepEquals(config[key] ?? new JsonArray(), actual["parser"]![key]), $"{input}: parser {key} exact UTF-8");
            Require(MqvpnProfileConfiguration.TryParseEndpoint(config["server_addr"]!.GetValue<string>(), out var host, out var port), "endpoint expected");
            Require(actual["bridge"]!["server_host"]!.GetValue<string>() == host && actual["bridge"]!["server_port"]!.GetValue<int>() == port, "CLI endpoint reaches bridge");
            Require(actual["bridge"]!["multipath"]!.GetValue<bool>() == (config["paths"]!.AsArray().Count > 1), "multipath uses runtime path count");
            var ports = MqvpnProfileConfiguration.Boolean(config, "redundant") ? (config["redundant_ports"]?.GetValue<string>() ?? "") : "";
            var expectedPorts = ports.Length == 0 ? new JsonArray() : new JsonArray(ports.Split(',').Select(p => (JsonNode?)JsonValue.Create(ushort.Parse(p.Trim(), CultureInfo.InvariantCulture))).ToArray());
            Require(JsonNode.DeepEquals(actual["bridge"]!["redundant_ports"], expectedPorts), "redundant UDP ports reach bridge");
            foreach (var surface in new[] { "parser", "bridge" })
            {
                foreach (var key in new[] { "wait", "cap" })
                {
                    var jsonKey = key == "wait" ? "max_wait_ms" : "cap_packets";
                    Require(actual[surface]!["reorder"]!["explicit_" + key]!.GetValue<bool>() == ((config["reorder"] as JsonObject)?.ContainsKey(jsonKey) ?? false), "reorder explicit override flag");
                }
                var rules = config["reorder_rules"] as JsonArray ?? new JsonArray();
                var observed = actual[surface]!["reorder_rules"]!.AsArray();
                Require(rules.Count == observed.Count, "reorder rule count reaches parser/bridge");
                for (var i = 0; i < rules.Count; i++)
                {
                    Require(observed[i]!["proto"]!.GetValue<int>() == 17 &&
                        JsonNode.DeepEquals(observed[i]!["port"], rules[i]!["port"]), "rule UDP/port exact");
                    var profile = rules[i]!["profile"]?.GetValue<string>() ?? "quic_bulk";
                    Require(observed[i]!["profile"]!.GetValue<int>() == Array.IndexOf(MqvpnProfileConfiguration.ReorderProfiles, profile), "rule profile exact");
                    foreach (var key in new[] { "max_wait_ms", "cap_packets" })
                        Require(JsonNode.DeepEquals(observed[i]![key], rules[i]![key] ?? JsonValue.Create(0)), "per-rule override exact");
                }
            }
        }
        Require(count >= 27, "C contract corpus must include all saved/runtime/exported variants");
        Console.WriteLine($"Actual C parser + shared bridge: {count} fixtures; {_checks} cumulative assertions passed.");
    }
}
