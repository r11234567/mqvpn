using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
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
    private static void Main()
    {
        FluentUi.EnsureApplication();
        var config = ProfileStore.CreateDefaultMqvpnConfig();
        config["server_addr"] = "127.0.0.1:443";
        config["auth_key"] = "test-only-key";
        config["paths"] = new JsonArray("test-adapter");
        config["tun_mtu"] = 1400;
        config["custom_root"] = "preserve";
        config["reorder"]!["enabled"] = "on";
        config["reorder_rules"] = JsonNode.Parse("""
            [
              {"proto":"udp","port":443,"profile":"fiber_lte","max_wait_ms":50,"cap_packets":2048,"custom_rule":true},
              {"proto":"udp","port":53,"profile":"default_udp"}
            ]
            """);
        using (var form = Create(config))
        {
            Check(Field<NumberBox>(form, "_mtu").Value == 1400, "legacy MTU import");
            Check(Field<CheckBox>(form, "_reorderCustomWait").IsChecked != true, "absent global wait remains inherited");
            SetNumber(form, "_tunQueueLength", 2048);
            SetNumber(form, "_tunReadBatch", 512);
            SetNumber(form, "_initMaxPathId", uint.MaxValue);
            Save(form);
            var saved = form.ResultConfig;
            Check(saved["mtu"]!.GetValue<int>() == 1400 && !saved.ContainsKey("tun_mtu"), "MTU CLI key migration");
            Check(saved["tx_queue_len"]!.GetValue<int>() == 2048 && saved["tun_read_batch"]!.GetValue<int>() == 512, "TUN tuning saved");
            Check(saved["init_max_path_id"]!.GetValue<uint>() == uint.MaxValue, "32-bit unsigned Path ID");
            Check(!saved["reorder"]!.AsObject().ContainsKey("max_wait_ms") && !saved["reorder"]!.AsObject().ContainsKey("cap_packets"), "preset precedence preserved");
            Check(saved["reorder_rules"]![0]!["profile"]!.GetValue<string>() == "fiber_lte" && saved["reorder_rules"]![1]!["profile"]!.GetValue<string>() == "default_udp", "independent rule presets");
            Check(saved["reorder_rules"]![0]!["cap_packets"]!.GetValue<uint>() == 2048, "rule override retained");
            Check(saved["custom_root"]!.GetValue<string>() == "preserve" && saved["reorder_rules"]![0]!["custom_rule"]!.GetValue<bool>(), "unknown fields retained");
            using var reopened = Create(JsonNode.Parse(saved.ToJsonString())!.AsObject());
            Check(Field<NumberBox>(reopened, "_initMaxPathId").Value == uint.MaxValue, "saved config reopens");
        }
        using (var form = Create(config))
        {
            Field<CheckBox>(form, "_automaticMtu").IsChecked = true;
            Field<CheckBox>(form, "_reorderEnabled").IsChecked = false;
            Field<CheckBox>(form, "_reorderCustomWait").IsChecked = true;
            SetNumber(form, "_reorderMaxWait", 150);
            Save(form);
            Check(!form.ResultConfig.ContainsKey("mtu") && !form.ResultConfig.ContainsKey("tun_mtu"), "automatic MTU removes both keys");
            Check(form.ResultConfig["reorder_rules"]!.AsArray().Count == 2, "disabled reorder retains rules");
            Check(form.ResultConfig["reorder"]!["max_wait_ms"]!.GetValue<uint>() == 150, "explicit global override");
        }
        using (var form = Create(config))
        {
            var rows = form.ReorderRuleRows;
            rows[0].CapPackets = "1000";
            Check(!ReadRules(form), "non-power-of-two rule rejected");
            rows[0].CapPackets = "2048";
            rows[0].MaxWaitMs = "0";
            Check(!ReadRules(form), "zero rule wait rejected");
            rows[0].MaxWaitMs = "";
            rows[1].Port = "443";
            Check(!ReadRules(form), "duplicate port rejected");
            rows[1].Port = "53";
            Check(ReadRules(form), "valid mixed rules accepted");
            for (var i = 0; i < 15; i++)
            {
                rows.Add(new ReorderRuleRow
                {
                    Port = (10000 + i).ToString(),
                    Profile = "quic_bulk",
                });
            }
            Check(!ReadRules(form), "core rule count limit enforced");
        }

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

        var sampleStore = ProfileStore.Load(Path.Combine(Path.GetTempPath(), $"mqvpn-test-store-{Guid.NewGuid():N}"));
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

        Console.WriteLine($"PASS: {_checks} configuration checks; WPF-UI screenshots rendered to {shotsDir}.");
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
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }
}
