// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using TruckersMP.Proxy;
using TruckersMP.Proxy.Database;

internal static class StartupChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run()
    {
        var application = FluentUi.EnsureApplication();
        var previousShutdown = application.ShutdownMode;
        var previousWindow = application.MainWindow;
        application.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        var root = Path.Combine(Path.GetTempPath(), $"mqvpn first launch 空格 {Guid.NewGuid():N}");
        try
        {
            // Presence-only runtime fixtures: initialization must not launch a kernel.
            foreach (var kernel in new[] { "mqvpn", "xray" })
            {
                var directory = Path.Combine(root, "runtime", kernel);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, kernel + ".exe"), "startup presence fixture");
            }
            var configPath = Path.Combine(root, "config", "client-windows.json");
            PersistenceChecks.Require(!Directory.Exists(Path.GetDirectoryName(configPath)), "first launch starts without a config directory");
            var store = Initialize(root);
            PersistenceChecks.Require(File.Exists(configPath) && store.State.Profiles.Count == 1 && store.LoadWarnings.Count == 0,
                "actual GUI initialization creates the first configuration and node");
            var config = store.ReadConfig(store.ActiveProfile);
            config["server_addr"] = "saved.example:443";
            config["unknown_future_field"] = "preserve me";
            store.SaveConfig(store.ActiveProfile, config);
            store.Save();
            var before = File.ReadAllBytes(configPath);
            var id = store.ActiveProfile.Id;
            SQLiteHelper.Instance.Dispose();
            store = Initialize(root);
            PersistenceChecks.Require(store.ActiveProfile.Id == id && File.ReadAllBytes(configPath).SequenceEqual(before),
                "GUI restart preserves the existing node and configuration bytes");
        }
        finally
        {
            SQLiteHelper.Instance.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
            application.MainWindow = previousWindow;
            application.ShutdownMode = previousShutdown;
        }
    }

    private static ProfileStore Initialize(string root)
    {
        var window = new MainForm(root);
        try
        {
            typeof(MainForm).GetMethod("InitializeApplication", Private)!.Invoke(window, null);
            var store = (ProfileStore?)typeof(MainForm).GetField("_profiles", Private)!.GetValue(window);
            PersistenceChecks.Require(store is not null && Field<ToggleSwitch>(window, "_proxyToggle").Enabled,
                "actual GUI startup reaches an enabled node editor instead of Missing file");
            return store!;
        }
        finally
        {
            Field<DispatcherTimer>(window, "_logTimer").Stop();
            Field<DispatcherTimer>(window, "_trafficTimer").Stop();
            Field<System.Windows.Forms.NotifyIcon>(window, "_trayIcon").Dispose();
            typeof(MainForm).GetField("_allowClose", Private)!.SetValue(window, true);
            window.Close();
        }
    }

    private static T Field<T>(object instance, string name) =>
        (T)typeof(MainForm).GetField(name, Private)!.GetValue(instance)!;
}
