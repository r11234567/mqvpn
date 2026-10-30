using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TruckersMP.Proxy;
using Wpf.Ui.Controls;

internal static class ConfigurationChecks
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _checks;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.FirstOrDefault() == "--pe")
        {
            try { TruckersMP.Proxy.Packaging.PackageContract.ValidatePortableExecutable(args[1], args[2]); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
            return;
        }
        if (args.FirstOrDefault() == "--host")
        {
            Environment.Exit(NativeProcessHost.Run(args[1..]));
            return;
        }
        if (args.FirstOrDefault() == "--service-child")
        {
            LifecycleChecks.RunChild(args.Contains("--exit"));
            return;
        }
        if (args.Contains("--lifecycle"))
        {
            LifecycleChecks.RunAsync().GetAwaiter().GetResult();
            return;
        }
        if (args.Contains("--persistence"))
        {
            PersistenceChecks.EncodingAndSave();
            return;
        }
        if (args.Contains("--reset"))
        {
            PersistenceChecks.ResetAndReopen();
            return;
        }
        if (args.Contains("--package"))
        {
            PackageChecks.Run();
            return;
        }
        Console.WriteLine("================================================================================");
        Console.WriteLine("MQVPN GUI production behavior checks");
        Console.WriteLine("================================================================================");

        try
        {
            PersistenceChecks.EncodingAndSave();
            PersistenceChecks.ResetAndReopen();
            LifecycleChecks.RunAsync().GetAwaiter().GetResult();
            PackageChecks.Run();
            FluentUi.EnsureApplication();
            FluentUi.SuppressDialogsForTesting = true;

            // -------------------------------------------------------------------------
            // 痛点 1: 0.19.0-beta-1 严格布尔配置与参数迁移测试
            // -------------------------------------------------------------------------
            Console.WriteLine("\n--- [Stage 1] Upstream 0.19.0-beta-1 Schema & Strict Booleans ---");
            var config = ProfileStore.CreateDefaultMqvpnConfig();
            config["server_addr"] = "127.0.0.1:443";
            config["auth_key"] = "test-only-key";
            config["paths"] = new JsonArray("test-adapter");
            config["tun_mtu"] = 1400;
            config["custom_root"] = "preserve";
            // 模拟旧版本传入的 "on" 字符串
            config["reorder"]!["enabled"] = "on";
            config["reorder_rules"] = JsonNode.Parse("""
                [
                  {"proto":"udp","port":443,"profile":"fiber_lte","max_wait_ms":50,"cap_packets":2048,"custom_rule":true},
                  {"proto":"udp","port":53,"profile":"default_udp"}
                ]
                """);

            using (var form = Create(config))
            {
                Check(Field<NumberBox>(form, "_mtu").Value == 1400, "legacy MTU import", "Expected MTU 1400");
                Check(Field<CheckBox>(form, "_reorderCustomWait").IsChecked != true, "absent global wait remains inherited", "Expected custom wait unchecked");

                // 验证 Reinjection 下拉框中绝不包含已被上游废弃的 dgram 模式
                var reinjCombo = Field<ComboBox>(form, "_reinjection");
                var reinjItems = reinjCombo.Items.Cast<object>().Select(x => x.ToString()).ToList();
                Check(!reinjItems.Contains("dgram"), "0.19.0-beta-1 reinjection does not include obsolete 'dgram'", "Found obsolete 'dgram' in options");
                Check(reinjItems.Contains("off") && reinjItems.Contains("deadline") && reinjItems.Contains("idle"),
                    "0.19.0-beta-1 reinjection contains standard modes: off, deadline, idle", "Missing standard reinjection modes");

                SetNumber(form, "_tunQueueLength", 2048);
                SetNumber(form, "_tunReadBatch", 512);
                SetNumber(form, "_initMaxPathId", 128);

                // 0.19.0-beta-1 Multipath, PMTUD and Redundant mode checks
                Field<CheckBox>(form, "_requestServerScheduler").IsChecked = true;
                Field<CheckBox>(form, "_redundant").IsChecked = true;
                Check(Field<ComboBox>(form, "_congestion").SelectedItem?.ToString() == "bbr",
                    "redundant mode auto-recommends CC = bbr", "CC was not switched to bbr");
                Field<Wpf.Ui.Controls.TextBox>(form, "_redundantPorts").Text = "4200,9000";
                Field<CheckBox>(form, "_pmtud").IsChecked = true;
                SetNumber(form, "_pathMtu", 1492);
                Field<CheckBox>(form, "_tunOffload").IsChecked = true;
                Field<CheckBox>(form, "_tunGro").IsChecked = true;

                Save(form);

                var saved = form.ResultConfig;
                Check(saved["mtu"]!.GetValue<int>() == 1400 && !saved.ContainsKey("tun_mtu"), "MTU CLI key migration", "Expected mtu migrated and tun_mtu removed");
                Check(saved["tx_queue_len"]!.GetValue<int>() == 2048 && saved["tun_read_batch"]!.GetValue<int>() == 512, "TUN tuning saved", "TUN queue/batch mismatch");
                Check(saved["init_max_path_id"]!.GetValue<uint>() == 128, "core-supported Path ID maximum", "Path ID mismatch");
                Check(saved["request_server_scheduler"]!.GetValue<bool>() == true, "0.19.0-beta-1 request_server_scheduler saved", "request_server_scheduler mismatch");
                Check(saved["redundant"]!.GetValue<bool>() == true, "0.19.0-beta-1 redundant saved", "redundant mismatch");
                Check(saved["redundant_ports"]!.GetValue<string>() == "4200,9000", "0.19.0-beta-1 redundant_ports saved", "redundant_ports mismatch");
                Check(saved["pmtud"]!.GetValue<bool>() == true, "0.19.0-beta-1 pmtud saved", "pmtud mismatch");
                Check(saved["path_mtu"]!.GetValue<int>() == 1492, "0.19.0-beta-1 path_mtu saved", "path_mtu mismatch");
                Check(saved["advanced"]!["tun_offload"]!.GetValue<bool>() == true && saved["advanced"]!["tun_gro"]!.GetValue<bool>() == true,
                    "0.19.0-beta-1 tun_offload and tun_gro saved", "advanced offload mismatch");

                // 重点痛点：0.19.0-beta-1 严格要求保存出的 JSON 属性必须是原生 JSON 布尔值 (true / false)，字符串 "on"/"off" 会导致内核崩溃
                var reorderNode = saved["reorder"]!["enabled"];
                Check(reorderNode is JsonValue jv && jv.TryGetValue<bool>(out var isReorderBool) && isReorderBool,
                    "0.19.0-beta-1 strict boolean serialization for reorder.enabled (true)", $"Expected boolean true, got {reorderNode?.ToJsonString()}");
                var hybridNode = saved["hybrid"]!["enabled"];
                Check(hybridNode is JsonValue jvH && jvH.TryGetValue<bool>(out var isHybridBool) && !isHybridBool,
                    "0.19.0-beta-1 strict boolean serialization for hybrid.enabled (false)", $"Expected boolean false, got {hybridNode?.ToJsonString()}");

                Check(!saved["reorder"]!.AsObject().ContainsKey("max_wait_ms") && !saved["reorder"]!.AsObject().ContainsKey("cap_packets"),
                    "preset precedence preserved", "Global overrides unexpectedly present");
                Check(saved["reorder_rules"]![0]!["profile"]!.GetValue<string>() == "fiber_lte" && saved["reorder_rules"]![1]!["profile"]!.GetValue<string>() == "default_udp",
                    "independent rule presets", "Rule profiles mismatch");
                Check(saved["reorder_rules"]![0]!["cap_packets"]!.GetValue<uint>() == 2048, "rule override retained", "Rule cap_packets mismatch");
                Check(saved["custom_root"]!.GetValue<string>() == "preserve" && saved["reorder_rules"]![0]!["custom_rule"]!.GetValue<bool>(),
                    "unknown fields retained", "Custom fields not retained");

                using var reopened = Create(JsonNode.Parse(saved.ToJsonString())!.AsObject());
                Check(Field<NumberBox>(reopened, "_initMaxPathId").Value == 128, "saved config reopens cleanly", "InitMaxPathId mismatch upon reopening");
                Check(Field<CheckBox>(reopened, "_requestServerScheduler").IsChecked == true, "reopened request_server_scheduler true", "request_server_scheduler false");
                Check(Field<CheckBox>(reopened, "_redundant").IsChecked == true, "reopened redundant true", "redundant false");
                Check(Field<Wpf.Ui.Controls.TextBox>(reopened, "_redundantPorts").Text == "4200,9000", "reopened redundant_ports 4200,9000", "redundant_ports mismatch");
                Check(Field<CheckBox>(reopened, "_pmtud").IsChecked == true, "reopened pmtud true", "pmtud false");
                Check(Field<NumberBox>(reopened, "_pathMtu").Value == 1492, "reopened path_mtu 1492", "path_mtu mismatch");
                Check(Field<CheckBox>(reopened, "_tunOffload").IsChecked == true && Field<CheckBox>(reopened, "_tunGro").IsChecked == true,
                    "reopened tun_offload/tun_gro true", "tun_offload/tun_gro mismatch");
            }

            using (var form = Create(config))
            {
                Field<CheckBox>(form, "_automaticMtu").IsChecked = true;
                Field<CheckBox>(form, "_reorderEnabled").IsChecked = false;
                Field<CheckBox>(form, "_reorderCustomWait").IsChecked = true;
                SetNumber(form, "_reorderMaxWait", 150);
                Save(form);

                Check(!form.ResultConfig.ContainsKey("mtu") && !form.ResultConfig.ContainsKey("tun_mtu"), "automatic MTU removes both keys", "MTU keys unexpectedly present");
                Check(form.ResultConfig["reorder_rules"]!.AsArray().Count == 2, "disabled reorder retains rules", "Rules dropped");
                Check(form.ResultConfig["reorder"]!["max_wait_ms"]!.GetValue<uint>() == 150, "explicit global override", "Max wait override mismatch");

                // 重点痛点：关闭状态必须也是原生 JSON 布尔值 false
                var reorderDisabledNode = form.ResultConfig["reorder"]!["enabled"];
                Check(reorderDisabledNode is JsonValue jvOff && jvOff.TryGetValue<bool>(out var offBool) && !offBool,
                    "0.19.0-beta-1 strict boolean serialization for reorder.enabled (false)", $"Expected boolean false, got {reorderDisabledNode?.ToJsonString()}");
            }

            // -------------------------------------------------------------------------
            // 痛点 2: 0.19.0-beta-1 新增参数校验（RedundantPorts、PathMTU）与 Reorder 参数上下限
            // -------------------------------------------------------------------------
            Console.WriteLine("\n--- [Stage 2] 0.19.0-beta-1 Bounds & Invariant Validation ---");
            var legacyPathConfig = config.DeepClone().AsObject();
            legacyPathConfig["init_max_path_id"] = uint.MaxValue;
            using (var old = Create(legacyPathConfig))
            {
                Check(Field<NumberBox>(old, "_initMaxPathId").Value == uint.MaxValue, "old excessive Path ID is shown without silently clamping it");
                FluentUi.LastDialogMessage = null;
                Save(old);
                Check(FluentUi.LastDialogMessage?.Contains("InitMaxPathId") == true && old.ResultConfig["init_max_path_id"]!.GetValue<uint>() == uint.MaxValue,
                    "old excessive Path ID requires explicit correction before saving");
            }
            using (var form = Create(config))
            {
                // 校验 RedundantPorts 端口合法性与上限 16 端口
                Field<CheckBox>(form, "_redundant").IsChecked = true;
                FluentUi.LastDialogMessage = null;
                Field<Wpf.Ui.Controls.TextBox>(form, "_redundantPorts").Text = "1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17";
                Save(form);
                Check(FluentUi.LastDialogMessage != null && FluentUi.LastDialogMessage.Contains("16"),
                    "RedundantPorts exceeding 16 ports rejected", "Should reject >16 ports");

                FluentUi.LastDialogMessage = null;
                Field<Wpf.Ui.Controls.TextBox>(form, "_redundantPorts").Text = "4200,70000";
                Save(form);
                Check(FluentUi.LastDialogMessage != null && FluentUi.LastDialogMessage.Contains("70000"),
                    "RedundantPorts out of range (>65535) rejected", "Should reject port 70000");

                FluentUi.LastDialogMessage = null;
                Field<Wpf.Ui.Controls.TextBox>(form, "_redundantPorts").Text = "4200,0";
                Save(form);
                Check(FluentUi.LastDialogMessage != null && FluentUi.LastDialogMessage.Contains("0"),
                    "RedundantPorts port 0 rejected", "Should reject port 0");

                FluentUi.LastDialogMessage = null;
                Field<Wpf.Ui.Controls.TextBox>(form, "_redundantPorts").Text = "4200, 9000";
                SetNumber(form, "_pathMtu", 1000); // PathMTU must be 0 or 1280..9000
                Save(form);
                Check(FluentUi.LastDialogMessage != null && FluentUi.LastDialogMessage.Contains("PathMTU"),
                    "PathMTU < 1280 (non-zero) rejected", "Should reject path mtu 1000");

                FluentUi.LastDialogMessage = null;
                SetNumber(form, "_pathMtu", 9500);
                Save(form);
                Check(FluentUi.LastDialogMessage != null && FluentUi.LastDialogMessage.Contains("PathMTU"),
                    "PathMTU > 9000 rejected", "Should reject path mtu 9500");

                FluentUi.LastDialogMessage = null;
                SetNumber(form, "_pathMtu", 1500);
                Save(form);
                Check(FluentUi.LastDialogMessage == null && form.ResultConfig["path_mtu"]!.GetValue<int>() == 1500,
                    "Valid 0.19.0-beta-1 parameters accepted", "Valid parameters unexpectedly rejected");
            }
            using (var form = Create(config))
            {
                var rows = form.ReorderRuleRows;
                rows[0].CapPackets = "1000";
                Check(!ReadRules(form), "non-power-of-two rule rejected", "Power of two validation failed");
                rows[0].CapPackets = "2048";
                rows[0].MaxWaitMs = "0";
                Check(!ReadRules(form), "zero rule wait rejected", "Zero rule wait should be rejected");
                rows[0].MaxWaitMs = "";
                rows[1].Port = "443";
                Check(!ReadRules(form), "duplicate port rejected", "Duplicate port collision should be rejected");
                rows[1].Port = "53";
                Check(ReadRules(form), "valid mixed rules accepted", "Valid rules rejected unexpectedly");

                for (var i = 0; i < 15; i++)
                {
                    rows.Add(new ReorderRuleRow
                    {
                        Port = (10000 + i).ToString(),
                        Profile = "quic_bulk",
                    });
                }
                Check(!ReadRules(form), "core rule count limit enforced (max 16 rules)", "Core rule count limit not enforced");
            }

            // -------------------------------------------------------------------------
            // 痛点 3: SQLite 数据库迁移、持久化与设置中心 4-in-1
            // -------------------------------------------------------------------------
            Console.WriteLine("\n--- [Stage 3] SQLite Database Persistence & 4-in-1 Settings ---");
            var tempDir = Path.Combine(Path.GetTempPath(), $"mqvpn-test-store-{Guid.NewGuid():N}");
            var sampleStore = ProfileStore.Load(tempDir);
            var dbFile = Path.Combine(tempDir, "mqvpn.db");
            Check(File.Exists(dbFile), "SQLite mqvpn.db file created", "Database file not found");

            var addedCfg = ProfileStore.CreateDefaultMqvpnConfig();
            addedCfg["server_addr"] = "198.51.100.1:8443";
            var newProfile = sampleStore.Add("测试节点 2", addedCfg, "truckersmp");
            Check(newProfile.SortOrder == 1, "profile SortOrder assigned", "Sort order was not 1");
            Check(newProfile.ServerAddress == "198.51.100.1" && newProfile.ServerPort == 8443,
                "server endpoint parsed into SQLite entity", "Endpoint parsing mismatch");

            var clonedProfile = sampleStore.Clone(newProfile);
            Check(sampleStore.State.Profiles.Count == 3, "profile cloned in SQLite", "Expected 3 profiles");

            sampleStore.MoveDown(sampleStore.State.Profiles[0]);
            Check(sampleStore.State.Profiles[1].Name == "当前 MQVPN 配置", "profile reordered in memory & SQLite", "Order mismatch");

            var reopenedStore = ProfileStore.Load(tempDir);
            Check(reopenedStore.State.Profiles.Count == 3, "reloaded from SQLite db", "Reloaded count mismatch");
            Check(reopenedStore.State.Profiles[1].Name == "当前 MQVPN 配置", "reloaded order preserved", "Order not preserved");

            reopenedStore.Delete(reopenedStore.State.Profiles[2]);
            Check(reopenedStore.State.Profiles.Count == 2, "profile deleted from SQLite db", "Profile delete mismatch");

            reopenedStore.State.SpeedTestTimeoutMs = 4500;
            reopenedStore.State.AllowLan = true;
            reopenedStore.Save();

            var settingsReopened = ProfileStore.Load(tempDir);
            Check(settingsReopened.State.SpeedTestTimeoutMs == 4500, "settings preserved in SQLite AppSettings", "SpeedTestTimeoutMs mismatch");
            Check(settingsReopened.State.AllowLan, "AllowLan setting preserved in SQLite AppSettings", "AllowLan mismatch");

            // DNS 与高级参数 SQLite 持久化验证
            settingsReopened.State.DirectDns = "1.2.4.8, 210.2.4.8";
            settingsReopened.State.RemoteDns = "https://dns.google/dns-query";
            settingsReopened.State.DomainStrategy = "IPOnDemand";
            settingsReopened.State.FakeDnsEnabled = true;
            settingsReopened.State.LocalSocksPort = 20808;
            settingsReopened.State.LocalHttpPort = 20809;
            settingsReopened.Save();

            var dnsReopened = ProfileStore.Load(tempDir);
            Check(dnsReopened.State.DirectDns == "1.2.4.8, 210.2.4.8", "DirectDns preserved in SQLite", "DirectDns mismatch");
            Check(dnsReopened.State.RemoteDns == "https://dns.google/dns-query", "RemoteDns preserved in SQLite", "RemoteDns mismatch");
            Check(dnsReopened.State.DomainStrategy == "IPOnDemand", "DomainStrategy preserved in SQLite", "DomainStrategy mismatch");
            Check(dnsReopened.State.FakeDnsEnabled, "FakeDnsEnabled preserved in SQLite", "FakeDnsEnabled mismatch");
            Check(dnsReopened.State.LocalSocksPort == 20808 && dnsReopened.State.LocalHttpPort == 20809,
                "Local proxy ports preserved in SQLite", "Proxy ports mismatch");

            // -------------------------------------------------------------------------
            // 痛点 4: 全局直通 (免 TUN) 路由判定与模式分流
            // -------------------------------------------------------------------------
            Console.WriteLine("\n--- [Stage 4] Global MQVPN Bypass & Smart Route Logic ---");
            var globalPolicy = dnsReopened.GetPolicy("global");
            var bypassPolicy = new RoutingPolicy { Id = "bypass_mainland", Name = "绕过大陆" };
            var customTunPolicy = new RoutingPolicy
            {
                Id = "custom_tun",
                Name = "完全直通 TUN",
                FinalOutbound = "MQVPN-TUN",
                Rules = new List<RoutingRule>()
            };

            Check(RoutingSemantics.IsGlobalMqvpn(globalPolicy), "Routing policy 'global' identified as Global MQVPN", "global policy not detected");
            Check(!RoutingSemantics.IsGlobalMqvpn(new RoutingPolicy { Id = "test", Name = "全局 MQVPN 模式", FinalOutbound = "DIRECT" }),
                "display name cannot override routing semantics", "name changed mode");
            Check(RoutingSemantics.IsGlobalMqvpn(customTunPolicy),
                "Routing policy with FinalOutbound=MQVPN-TUN and empty rules identified as Global MQVPN", "TUN direct not detected");
            Check(!RoutingSemantics.IsGlobalMqvpn(bypassPolicy),
                "Routing policy 'bypass_mainland' correctly recognized as requiring TUN/Routing", "Bypass mainland incorrectly flagged global");

            // -------------------------------------------------------------------------
            // 痛点 5: 备份与还原 (Backup & Restore ZIP) 完整往返
            // -------------------------------------------------------------------------
            Console.WriteLine("\n--- [Stage 5] Backup & Restore ZIP Roundtrip & Snapshot Safety ---");
            var backupZip = Path.Combine(tempDir, "test_backup.zip");
            Check(dnsReopened.ExportBackup(backupZip), "ExportBackup succeeded", "ExportBackup returned false");
            Check(File.Exists(backupZip), "Backup ZIP created on disk", "ZIP file missing");

            // 验证 ZIP 内部文件结构完整性
            using (var zip = ZipFile.OpenRead(backupZip))
            {
                Check(zip.GetEntry("mqvpn.db") is not null, "Backup ZIP contains mqvpn.db", "mqvpn.db entry missing in zip");
                Check(zip.GetEntry("routing.json") is not null, "Backup ZIP contains routing.json", "routing.json entry missing in zip");
            }

            // 还原到全新目录并检验恢复数据
            var restoreDir = Path.Combine(Path.GetTempPath(), $"mqvpn-test-restore-{Guid.NewGuid():N}");
            var freshStore = ProfileStore.Load(restoreDir);
            Check(freshStore.ImportBackup(backupZip), "ImportBackup succeeded", "ImportBackup returned false");
            var restoredStore = ProfileStore.Load(restoreDir);
            Check(restoredStore.State.DirectDns == "1.2.4.8, 210.2.4.8", "DirectDns restored from ZIP", "DirectDns not restored");
            Check(restoredStore.State.LocalSocksPort == 20808, "LocalSocksPort restored from ZIP", "LocalSocksPort not restored");

            // 验证出厂重置
            restoredStore.ResetToFactoryDefaults();
            var resetStore = ProfileStore.Load(restoreDir);
            Check(resetStore.State.LocalSocksPort == 10808, "ResetToFactoryDefaults reset LocalSocksPort to 10808", "Reset failed");

            // -------------------------------------------------------------------------
            // 痛点 6: 多架构 (x64 / arm64) 资源包自动匹配与内核发现
            // -------------------------------------------------------------------------
            Console.WriteLine("\n--- [Stage 6] Multi-Arch Kernel & Release Asset Matching (x64 & arm64) ---");
            var mockRelease = new GitHubRelease("v0.19.0-beta1", "Release v0.19.0-beta1", new List<GitHubAsset>
            {
                new("mqvpn_windows_gui_x64.zip", "https://example.com/gui_x64.zip", 1024),
                new("mqvpn_windows_gui_arm64.zip", "https://example.com/gui_arm64.zip", 1024),
                new("mqvpn-windows-amd64.zip", "https://example.com/mqvpn_x64.zip", 1024),
                new("mqvpn-windows-arm64.zip", "https://example.com/mqvpn_arm64.zip", 1024),
                new("Xray-windows-64.zip", "https://example.com/xray_x64.zip", 1024),
                new("Xray-windows-arm64-v8a.zip", "https://example.com/xray_arm64.zip", 1024),
                new("geoip.dat", "https://example.com/geoip.dat", 1024),
                new("geosite.dat", "https://example.com/geosite.dat", 1024),
            }, false);

            var guiX64 = KernelInstaller.SelectAsset("gui", mockRelease, Architecture.X64);
            Check(guiX64.Name == "mqvpn_windows_gui_x64.zip", "KernelInstaller matched x64 GUI ZIP", $"Got {guiX64.Name}");

            var guiArm64 = KernelInstaller.SelectAsset("gui", mockRelease, Architecture.Arm64);
            Check(guiArm64.Name == "mqvpn_windows_gui_arm64.zip", "KernelInstaller matched ARM64 GUI ZIP", $"Got {guiArm64.Name}");

            var mqvpnX64 = KernelInstaller.SelectAsset("mqvpn", mockRelease, Architecture.X64);
            Check(mqvpnX64.Name == "mqvpn-windows-amd64.zip", "KernelInstaller matched x64 Core mqvpn ZIP", $"Got {mqvpnX64.Name}");

            var mqvpnArm64 = KernelInstaller.SelectAsset("mqvpn", mockRelease, Architecture.Arm64);
            Check(mqvpnArm64.Name == "mqvpn-windows-arm64.zip", "KernelInstaller matched ARM64 Core mqvpn ZIP", $"Got {mqvpnArm64.Name}");

            var xrayX64 = KernelInstaller.SelectAsset("xray", mockRelease, Architecture.X64);
            Check(xrayX64.Name == "Xray-windows-64.zip", "KernelInstaller matched x64 Xray ZIP", $"Got {xrayX64.Name}");

            var xrayArm64 = KernelInstaller.SelectAsset("xray", mockRelease, Architecture.Arm64);
            Check(xrayArm64.Name == "Xray-windows-arm64-v8a.zip", "KernelInstaller matched ARM64 Xray ZIP", $"Got {xrayArm64.Name}");

            var geoAssets = KernelInstaller.SelectGeoDataAssets(mockRelease);
            Check(geoAssets.Count == 2 && geoAssets.Any(a => a.Name == "geoip.dat") && geoAssets.Any(a => a.Name == "geosite.dat"),
                "KernelInstaller matched geoip.dat and geosite.dat rules", "GeoData matching mismatch");

            // -------------------------------------------------------------------------
            // 痛点 7: UI 渲染及截图采集（无视觉差异判定）
            // -------------------------------------------------------------------------
            Console.WriteLine("\n--- [Stage 7] UI Rendering & Screenshot Capture ---");
            var shotsDir = Path.Combine(AppContext.BaseDirectory, "screenshots");
            Directory.CreateDirectory(shotsDir);

            using (var form = Create(config))
            {
                form.Show();
                PumpEvents();
                var tabs = form.TabsControl;
                for (var i = 0; i < tabs.Items.Count; i++)
                {
                    tabs.SelectedIndex = i;
                    PumpEvents();
                    SaveWindowPng(form, Path.Combine(shotsDir, $"tab-{i}.png"));
                }
                form.Close();
            }

            var main = new MainForm();
            main.Show();
            PumpEvents();
            var mainRows = (ObservableCollection<ProfileRowViewModel>)typeof(MainForm).GetField("_profileRows", Private)!.GetValue(main)!;
            mainRows.Add(new ProfileRowViewModel
            {
                Profile = new ProxyProfile { Name = "欧洲多路径节点 01" },
                IsActive = true,
                Name = "欧洲多路径节点 01 [自启]",
                Server = "de-fra.mqvpn.example:443",
                PolicyName = "仅 TruckersMP",
                LatencyText = "32 ms",
                TrafficText = "今日 ↑128.4MB ↓1.42GB   |   累计 ↑3.8GB ↓42.6GB",
            });
            mainRows.Add(new ProfileRowViewModel
            {
                Profile = new ProxyProfile { Name = "香港 BGP 高速节点" },
                IsActive = false,
                Name = "香港 BGP 高速节点",
                Server = "hk-bgp.mqvpn.example:8443",
                PolicyName = "V4-绕过大陆 (Whitelist)",
                LatencyText = "18 ms",
                TrafficText = "今日 ↑0B ↓0B   |   累计 ↑512MB ↓6.2GB",
            });
            PumpEvents();
            SaveWindowPng(main, Path.Combine(shotsDir, "main-dark.png"));
            FluentUi.ToggleTheme();
            PumpEvents();
            SaveWindowPng(main, Path.Combine(shotsDir, "main-light.png"));
            FluentUi.ToggleTheme();
            PumpEvents();
            typeof(MainForm).GetField("_allowClose", Private)!.SetValue(main, true);
            main.Close();

            using (var settingsForm = new SettingsForm(sampleStore, 0))
            {
                settingsForm.Show();
                PumpEvents();
                var tabs = settingsForm.TabsControl;
                for (var t = 0; t < tabs.Items.Count; t++)
                {
                    tabs.SelectedIndex = t;
                    PumpEvents();
                    SaveWindowPng(settingsForm, Path.Combine(shotsDir, $"settings-tab-{t}.png"));
                }
                SaveWindowPng(settingsForm, Path.Combine(shotsDir, "settings.png"));
                settingsForm.Close();
            }

            using (var routingForm = new RoutingSettingsForm(sampleStore.Routing))
            {
                routingForm.Show();
                PumpEvents();
                SaveWindowPng(routingForm, Path.Combine(shotsDir, "routing.png"));
                routingForm.Close();
            }

            using (var kernelForm = new KernelUpdateForm(
                Path.GetTempPath(),
                new KernelUpdatePreferences("r11234567/mqvpn", "XTLS/Xray-core", "Loyalsoldier/v2ray-rules-dat", false, false),
                () => Task.CompletedTask,
                _ => { }))
            {
                kernelForm.Show();
                PumpEvents();
                SaveWindowPng(kernelForm, Path.Combine(shotsDir, "kernel-update.png"));
                kernelForm.Close();
            }

            Console.WriteLine("\n================================================================================");
            Console.WriteLine($"PASS: configuration checks completed ({_checks} assertions); screenshots are capture artifacts only.");
            Console.WriteLine($"UI screenshots saved to: {shotsDir}");
            Console.WriteLine("================================================================================");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\n!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
            Console.WriteLine(">>> CRITICAL TEST FAILURE DETECTED <<<");
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine($"Stack Trace:\n{ex.StackTrace}");
            Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
            Console.ResetColor();
            Environment.Exit(1);
        }
    }

    private static void PumpEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new DispatcherOperationCallback(_ =>
            {
                frame.Continue = false;
                return null;
            }),
            null);
        Dispatcher.PushFrame(frame);
    }

    private static void SaveWindowPng(Window window, string path)
    {
        window.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static ProfileEditorForm Create(JsonObject config) => new("编辑配置", "欧洲多路径节点 01", config,
        new[] { new RoutingPolicy { Id = "truckersmp", Name = "仅 TruckersMP" } }, "truckersmp");
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
    private static void SetNumber(object form, string name, double value) => Field<NumberBox>(form, name).Value = value;
    private static void Save(ProfileEditorForm form) => typeof(ProfileEditorForm).GetMethod("SaveAndClose", Private)!.Invoke(form, null);
    private static bool ReadRules(ProfileEditorForm form) => (bool)typeof(ProfileEditorForm).GetMethod("TryReadReorderRules", Private)!.Invoke(form, new object?[] { null, null })!;
    
    private static void Check(bool condition, string testName, string detail = "")
    {
        if (!condition)
        {
            var msg = string.IsNullOrEmpty(detail) ? testName : $"{testName} (Detail: {detail})";
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  [FAIL] {msg}");
            Console.ResetColor();
            throw new InvalidOperationException($"Check failed: {msg}");
        }
        _checks++;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  [PASS] ");
        Console.ResetColor();
        Console.WriteLine(testName);
    }
}
