// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

using System.Text.Json;
using TruckersMP.Proxy.Database;
using TruckersMP.Proxy.Packaging;

namespace TruckersMP.Proxy;

internal static class PackageSelfCheck
{
    public static int Run(string[] args)
    {
        var runtimeOnly = args.Length == 2 && args[0] == "--runtime-check";
        if (!runtimeOnly && args.Length != 5) return 2;
        var report = runtimeOnly ? args[1] : args[4];
        var temp = Path.Combine(Path.GetTempPath(), $"mqvpn-package-check-{Guid.NewGuid():N}");
        try
        {
            if (!runtimeOnly && args[2] != PackageContract.CurrentArchitecture)
                throw new InvalidOperationException("验收必须在目标架构原生进程中执行");
            var manifest = runtimeOnly ? null : PackageContract.Validate(args[1], args[2], args[3]);
            var store = ProfileStore.Load(temp);
            var config = ProfileStore.CreateDefaultMqvpnConfig();
            config["server_addr"] = "offline.example:443";
            config["paths"] = new System.Text.Json.Nodes.JsonArray("离线网卡 日本語");
            store.SaveConfig(store.ActiveProfile, config);
            var id = store.ActiveProfile.Id;
            SQLiteHelper.Instance.Dispose();
            store = ProfileStore.Load(temp);
            if (store.ActiveProfile.Id != id || store.ReadConfig(store.ActiveProfile)["paths"]![0]!.GetValue<string>() != "离线网卡 日本語")
                throw new IOException("发行包 SQLite/UTF-8 往返失败");
            if (!runtimeOnly)
            {
                foreach (var kernel in new[] { "mqvpn", "xray" })
                {
                    var shipped = Path.Combine(args[1], "runtime", kernel);
                    var runtimeRoot = Path.Combine(temp, "runtime");
                    var installed = Path.Combine(runtimeRoot, kernel);
                    Directory.CreateDirectory(installed);
                    foreach (var file in Directory.GetFiles(shipped)) File.Copy(file, Path.Combine(installed, Path.GetFileName(file)));
                    var updateSource = Path.Combine(temp, kernel + "-update");
                    Directory.CreateDirectory(updateSource);
                    foreach (var binary in Directory.GetFiles(shipped).Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                        File.Copy(binary, Path.Combine(updateSource, Path.GetFileName(binary)));
                    var archive = Path.Combine(temp, kernel + ".zip");
                    System.IO.Compression.ZipFile.CreateFromDirectory(updateSource, archive, System.IO.Compression.CompressionLevel.Fastest, false);
                    KernelInstaller.InstallAsync(kernel, new GitHubAsset(kernel + ".zip", "", new FileInfo(archive).Length),
                        runtimeRoot, CancellationToken.None, archive).GetAwaiter().GetResult();
                    if (kernel == "xray")
                        foreach (var data in new[] { "geoip.dat", "geosite.dat" })
                            if (!System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(shipped, data))).SequenceEqual(
                                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(installed, data)))))
                                throw new IOException("Xray 更新未保留现有 GeoData");
                }
            }
            var bundle = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
            var modules = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>().ToArray();
            var coreClr = modules.FirstOrDefault(m => m.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase))?.FileName;
            var sqlite = modules.Single(m => m.ModuleName.Equals("e_sqlite3.dll", StringComparison.OrdinalIgnoreCase)).FileName;
            // Single-file runtimes can map CoreCLR directly from the bundle;
            // it need not appear as a separate OS-loaded coreclr.dll module.
            #pragma warning disable IL3000 // Intentional check that CoreLib is embedded in the single-file package.
            if (!string.IsNullOrEmpty(bundle) && (new[] { coreClr, sqlite }.Where(p => p is not null).Any(path => !path!.StartsWith(Path.GetFullPath(bundle) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) ||
                !string.IsNullOrEmpty(typeof(object).Assembly.Location)))
                throw new IOException("运行时或 SQLite 未从自包含包释放目录加载");
            #pragma warning restore IL3000
            AtomicFile.WriteText(report, JsonSerializer.Serialize(new
            {
                sourceSha = manifest?.SourceSha, architecture = PackageContract.CurrentArchitecture,
                sqliteRoundtrip = true, runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                kernelUpdateRoundtrip = !runtimeOnly,
                coreClr = coreClr ?? "single-file image", sqlite,
                networkAccess = false,
            }));
            return 0;
        }
        catch (Exception ex)
        {
            AtomicFile.WriteText(report, JsonSerializer.Serialize(new { error = ex.ToString(),
                modules = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>()
                    .Select(m => new { m.ModuleName, m.FileName }).ToArray() }));
            return 1;
        }
        finally
        {
            SQLiteHelper.Instance.Dispose();
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }
    }
}
