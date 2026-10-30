// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

using System.Text;
using System.Text.Json.Nodes;
using TruckersMP.Proxy;
using TruckersMP.Proxy.Database;

internal static class LifecycleChecks
{
    public static void RunChild(bool exit)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        using var stop = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
        Console.WriteLine("child ready 网卡");
        if (!exit && !stop.Wait(TimeSpan.FromSeconds(20))) Environment.Exit(2);
    }

    public static async Task RunAsync()
    {
        var check = PersistenceChecks.Require;
        var policy = new RoutingPolicy { Id = "global", Name = "重命名后", FinalOutbound = "MQVPN-TUN" };
        check(RoutingSemantics.IsGlobalMqvpn(policy), "global semantics survive display rename");
        policy.Rules.Add(new RoutingRule { Enabled = true, Outbound = "DIRECT" });
        check(!RoutingSemantics.IsGlobalMqvpn(policy), "enabled direct rule needs split routing even on global ID");
        policy.Id = "custom";
        policy.Name = "全局 MQVPN";
        check(!RoutingSemantics.IsGlobalMqvpn(policy), "display name cannot override enabled routing rules");
        policy.Rules[0].Enabled = false;
        check(RoutingSemantics.IsGlobalMqvpn(policy), "disabled rules do not require Xray");
        policy.FinalOutbound = "DIRECT";
        check(!RoutingSemantics.IsGlobalMqvpn(policy), "DIRECT final outbound is never global MQVPN");

        var operation = new AsyncOperation();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var first = operation.RunAsync(async () => { events.Add("start"); entered.SetResult(); await release.Task; events.Add("ready"); });
        await entered.Task;
        var second = operation.RunAsync(() => { events.Add("stop"); return Task.CompletedTask; });
        check(!second.IsCompleted, "connection stop waits for in-progress start");
        release.SetResult();
        await Task.WhenAll(first, second);
        check(events.SequenceEqual(new[] { "start", "ready", "stop" }), "connection mutations execute in order");
        try { await operation.RunAsync(() => throw new IOException("ordinary operation failure")); }
        catch (IOException) { }
        await operation.RunAsync(() => { events.Add("recovered"); return Task.CompletedTask; });
        check(events.Last() == "recovered", "failed operation releases connection gate");

        var root = Path.Combine(Path.GetTempPath(), $"mqvpn-runtime-{Guid.NewGuid():N}");
        try
        {
            var store = ProfileStore.Load(root);
            var config = ProfileStore.CreateDefaultMqvpnConfig();
            config["paths"] = new JsonArray("previous adapter");
            store.SaveConfig(store.ActiveProfile, config);
            store.ActiveProfile.UseDefaultPath = true;
            var runtime = store.GetRuntimeConfig(store.ActiveProfile, () => "current 网卡");
            check(runtime["paths"]![0]!.GetValue<string>() == "current 网卡" &&
                store.ReadConfig(store.ActiveProfile)["paths"]![0]!.GetValue<string>() == "previous adapter",
                "default path is resolved at connection time without changing stored configuration");
            store.ActiveProfile.UseDefaultPath = false;
            check(store.GetRuntimeConfig(store.ActiveProfile, () => throw new Exception("unexpected resolver"))["paths"]![0]!.GetValue<string>() == "previous adapter",
                "explicit paths are kept at runtime");
        }
        finally
        {
            SQLiteHelper.Instance.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        // Run the actual ManagedService -> NativeProcessHost -> child chain.
        // No VPN, routes, WFP or network access are involved.
        var executable = Path.Combine(AppContext.BaseDirectory, "GUI.Tests.exe");
        var output = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<(int, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var service = new ManagedService("test", executable, AppContext.BaseDirectory,
                         new[] { "--service-child" }, (_, line) => { if (line.Contains("child ready 网卡")) output.TrySetResult(); }))
        {
            service.Exited += (code, expected) => exited.TrySetResult((code, expected));
            service.Start();
            await output.Task.WaitAsync(TimeSpan.FromSeconds(15));
            check(service.IsRunning, "hidden process host forwards real UTF-8 output");
            await service.StopAsync();
            var result = await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(result == (0, true) && !service.IsRunning, "CTRL_BREAK stops child gracefully and releases process host");
            await service.StopAsync();
        }
        exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var service = new ManagedService("test", executable, AppContext.BaseDirectory,
                         new[] { "--service-child", "--exit" }, (_, _) => { }))
        {
            service.Exited += (code, expected) => exited.TrySetResult((code, expected));
            service.Start();
            check(await exited.Task.WaitAsync(TimeSpan.FromSeconds(15)) == (0, false), "ordinary child exit retains exit reason");
        }
    }
}
