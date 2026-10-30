// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

using System.Runtime.InteropServices;
using TruckersMP.Proxy;
using TruckersMP.Proxy.Packaging;

internal static class PackageChecks
{
    public static void Run()
    {
        var check = PersistenceChecks.Require;
        var root = Path.Combine(Path.GetTempPath(), $"mqvpn-update-test-{Guid.NewGuid():N}");
        var target = Path.Combine(root, "installed");
        var staged = Path.Combine(root, "staged");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(staged);
        try
        {
            // Explicitly synthetic isolated files exercise the production file
            // transaction. Real package acceptance is tested on the final ZIP.
            var files = new[] { "first.txt", "second.txt" };
            var manifest = new PackageManifest(1, "x64", "", "", "", files.Select(f => new PackageFile(f, "")).ToArray());
            foreach (var file in files)
            {
                File.WriteAllText(Path.Combine(target, file), "old " + file);
                File.WriteAllText(Path.Combine(staged, file), "new " + file);
            }
            File.WriteAllText(Path.Combine(staged, "package-manifest.json"), "synthetic test manifest");
            using (var locked = new FileStream(Path.Combine(target, files[1]), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try { PackageTransaction.Apply(staged, target, manifest); throw new Exception("Expected file sharing failure"); }
                catch (IOException) { }
                check(File.ReadAllText(Path.Combine(target, files[0])) == "old first.txt", "file replacement failure restores earlier file");
            }
            check(File.ReadAllText(Path.Combine(target, files[1])) == "old second.txt", "file sharing failure preserves blocked original");
            foreach (var file in files) File.WriteAllText(Path.Combine(staged, file), "new " + file);
            Directory.CreateDirectory(Path.Combine(target, "config"));
            File.WriteAllText(Path.Combine(target, "config/profiles.json"), "user configuration");
            PackageTransaction.Apply(staged, target, manifest);
            check(files.All(f => File.ReadAllText(Path.Combine(target, f)) == "new " + f), "normal update installs complete file set");
            check(File.ReadAllText(Path.Combine(target, "config/profiles.json")) == "user configuration", "update preserves user configuration");
            PackageContract.ValidatePortableExecutable(Path.Combine(AppContext.BaseDirectory, "GUI.Tests.exe"), PackageContract.CurrentArchitecture);
            check(true, "real test apphost passes native PE architecture/dependency check");

            var missing = new GitHubRelease("test", "Synthetic asset list", new[]
            {
                new GitHubAsset("mqvpn-windows-arm64.zip", "https://example.invalid/core", 1),
                new GitHubAsset("unrelated-arm64.zip", "https://example.invalid/other", 1),
            }, false);
            try { KernelInstaller.SelectAsset("gui", missing, Architecture.Arm64); throw new Exception("Expected missing GUI asset"); }
            catch (InvalidOperationException) { }
            try { KernelInstaller.SelectAsset("mqvpn", missing, Architecture.X64); throw new Exception("Expected missing x64 asset"); }
            catch (InvalidOperationException) { }
            check(true, "missing exact architecture/component is rejected without fallback");
            KernelInstaller.EnsureMqvpnVersionCompatible("mqvpn 0.19.0");
            KernelInstaller.EnsureMqvpnVersionCompatible("mqvpn snapshot+20261030T120000Z.g123456789abc\ncore 0.19.0; callback ABI 3\n");
            try { KernelInstaller.EnsureMqvpnVersionCompatible("mqvpn 0.18.0"); throw new Exception("Expected incompatible configuration interface"); }
            catch (InvalidDataException) { }
            check(true, "kernel updates require the supported production configuration interface");
            foreach (var invalid in new[]
            {
                "mqvpn snapshot+20261030T120000Z.g123456789abc",
                "mqvpn snapshot+20261030T120000Z.g123456789abc\ncore 0.18.0; callback ABI 3\n",
                "mqvpn snapshot+20261030T120000Z.g123456789abc\ncore 0.19.0; callback ABI 2\n",
                "mqvpn snapshot+invalid\ncore 0.19.0; callback ABI 3\n",
            })
            {
                try { KernelInstaller.EnsureMqvpnVersionCompatible(invalid); throw new Exception("Expected incompatible snapshot"); }
                catch (InvalidDataException) { }
            }
            check(true, "snapshot kernel identity requires explicit compatible core and callback ABI metadata");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
