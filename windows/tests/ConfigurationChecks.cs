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
        if (args.FirstOrDefault() == "--verify-config-contract")
        {
            ConfigurationAlignmentChecks.VerifyContract(args[1]);
            return;
        }
        if (args.Contains("--configuration"))
        {
            ConfigurationAlignmentChecks.Run();
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

            ConfigurationAlignmentChecks.Run();
            var config = ProfileStore.CreateDefaultMqvpnConfig();
            config["server_addr"] = "127.0.0.1:443";
            config["auth_key"] = "test-only-key";
            config["paths"] = new JsonArray("test-adapter");

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
    private static void SetNumber(object form, string name, decimal value) => Field<IntegerInput>(form, name).Value = value;
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
