// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

using System.Text;
using System.Text.Json.Nodes;
using TruckersMP.Proxy;
using TruckersMP.Proxy.Database;

internal static class PersistenceChecks
{
    public static void EncodingAndSave()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mqvpn persistence 空格 {Guid.NewGuid():N}");
        try
        {
            var store = ProfileStore.Load(root);
            var config = ProfileStore.CreateDefaultMqvpnConfig();
            config["server_addr"] = "vpn.example:443";
            const string adapter = "网卡 日本語 café 🌐";
            config["paths"] = new JsonArray(adapter);
            config["unknown_future_field"] = new JsonObject { ["text"] = adapter, ["large"] = ulong.MaxValue };
            var profile = store.Add("Unicode 节点", config, "global");
            var path = store.GetConfigPath(profile);
            var bytes = File.ReadAllBytes(path);
            Require(!bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }), "new files omit UTF-8 BOM");
            var decoded = new UTF8Encoding(false, true).GetString(bytes);
            Require(decoded.Contains("网卡") && JsonNode.Parse(decoded)!["paths"]![0]!.GetValue<string>() == adapter,
                "UTF-8 preserves all adapter characters");
            File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var timestamp = File.GetLastWriteTimeUtc(path);
            SQLiteHelper.Instance.Dispose();
            store = ProfileStore.Load(root);
            profile = store.State.Profiles.Single(p => p.Id == profile.Id);
            Require(File.GetLastWriteTimeUtc(path) == timestamp && File.ReadAllBytes(path).SequenceEqual(bytes),
                "restart leaves valid UTF-8 file and timestamp untouched");
            profile.Name = "重命名";
            store.Save();
            var clone = store.Clone(profile);
            Require(JsonNode.DeepEquals(store.ReadConfig(clone), config), "clone/rename preserve unknown fields and uint64");

            var bomPath = store.GetConfigPath(clone);
            File.WriteAllText(bomPath, config.ToJsonString(), new UTF8Encoding(true));
            SQLiteHelper.Instance.Dispose();
            store = ProfileStore.Load(root);
            Require(store.ReadConfig(store.State.Profiles.Single(p => p.Id == clone.Id))["paths"]![0]!.GetValue<string>() == adapter,
                "UTF-8 BOM input is read correctly");

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var legacy = (Encoding)typeof(ProfileStore).GetField("LegacyConfigEncoding",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
            if (legacy.CodePage != 65001)
            {
                // Pick a real non-ASCII character representable in this host's
                // legacy code page, rather than assuming a Chinese ACP.
                var sample = new[] { "网卡", "café", "日本語" }.First(s => CanEncode(legacy, s));
                var legacyConfig = new JsonObject { ["server_addr"] = "legacy.example:443", ["paths"] = new JsonArray(sample) };
                var legacyBytes = legacy.GetBytes(legacyConfig.ToJsonString(new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                File.WriteAllBytes(bomPath, legacyBytes);
                SQLiteHelper.Instance.Dispose();
                store = ProfileStore.Load(root);
                Require(store.ReadConfig(store.State.Profiles.Single(p => p.Id == clone.Id))["paths"]![0]!.GetValue<string>() == sample,
                    "legacy code page migrates without character loss");
                var backups = Directory.GetFiles(Path.GetDirectoryName(bomPath)!, Path.GetFileName(bomPath) + ".legacy-*.bak");
                Require(backups.Length == 1 && File.ReadAllBytes(backups[0]).SequenceEqual(legacyBytes), "legacy migration preserves exact original bytes");
                var migrated = File.ReadAllBytes(bomPath);
                SQLiteHelper.Instance.Dispose();
                store = ProfileStore.Load(root);
                Require(File.ReadAllBytes(bomPath).SequenceEqual(migrated) &&
                    Directory.GetFiles(Path.GetDirectoryName(bomPath)!, Path.GetFileName(bomPath) + ".legacy-*.bak").Length == 1,
                    "legacy migration is idempotent");
            }
            else Console.WriteLine("SKIP: legacy non-UTF8 migration (system ACP is UTF-8)");

            profile = store.State.Profiles.Single(p => p.Id == profile.Id);
            var before = File.ReadAllBytes(path);
            var previousSnapshot = profile.ConfigContent;
            SQLiteHelper.Instance.Dispose();
            config["server_addr"] = "changed.example:8443";
            try { store.SaveConfig(profile, config); throw new Exception("expected closed-database error"); }
            catch (InvalidOperationException) { }
            Require(File.ReadAllBytes(path).SequenceEqual(before) && profile.ConfigContent == previousSnapshot,
                "failed database commit restores original file and in-memory snapshot");
            store = ProfileStore.Load(root);
            Require(store.ReadConfig(store.State.Profiles.Single(p => p.Id == profile.Id))["server_addr"]!.GetValue<string>() == "vpn.example:443",
                "failed save remains rolled back after reopening SQLite");

            File.WriteAllText(bomPath, "{\"server_addr\":");
            var damagedBytes = File.ReadAllBytes(bomPath);
            SQLiteHelper.Instance.Dispose();
            store = ProfileStore.Load(root);
            Require(store.LoadWarnings.Count == 1 && store.LoadWarnings[0].Contains(clone.ConfigFile), "invalid node is reported by path");
            Require(File.ReadAllBytes(bomPath).SequenceEqual(damagedBytes) && store.ReadConfig(profile) is not null,
                "invalid node is left intact and other nodes remain readable");
            Console.WriteLine("PASS: production configuration persistence checks");
        }
        finally
        {
            SQLiteHelper.Instance.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static bool CanEncode(Encoding encoding, string value)
    {
        try { return encoding.GetBytes(value).Any(b => b >= 0x80); }
        catch (EncoderFallbackException) { return false; }
    }

    public static void ResetAndReopen()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mqvpn-reset-{Guid.NewGuid():N}");
        try
        {
            var store = ProfileStore.Load(root);
            var config = ProfileStore.CreateDefaultMqvpnConfig();
            config["server_addr"] = "old.example:443";
            store.SaveConfig(store.ActiveProfile, config);
            store.Add("old node 1", config, "global");
            var active = store.Add("old node 2", config, "global");
            store.State.ActiveProfileId = active.Id;
            store.State.LocalSocksPort = 20808;
            store.State.GuiIncludePreRelease = true;
            var oldIds = store.State.Profiles.Select(p => p.Id).ToHashSet();
            var external = Path.Combine(root, "external-user-file.txt");
            File.WriteAllText(external, "keep me");
            var backupDir = Path.Combine(root, "backups");
            Directory.CreateDirectory(backupDir);
            File.WriteAllText(Path.Combine(backupDir, "older-backup.txt"), "keep me too");
            store.Save();
            store.ResetToFactoryDefaults();
            Require(store.State.Profiles.Count == 1 && !oldIds.Contains(store.ActiveProfile.Id), "reset replaces complete node collection immediately");
            Require(store.State.LocalSocksPort == 10808 && !store.State.GuiIncludePreRelease, "reset replaces settings and valid active ID together");
            Require(JsonNode.DeepEquals(store.ReadConfig(store.ActiveProfile), ProfileStore.CreateDefaultMqvpnConfig()),
                "reset overwrites old default configuration contents");
            SQLiteHelper.Instance.Dispose();
            store = ProfileStore.Load(root);
            Require(store.State.Profiles.Count == 1 && !oldIds.Contains(store.ActiveProfile.Id), "old SQLite rows do not return after reopening");
            Require(SQLiteHelper.Instance.GetAllProfiles().Count == 1, "database contains only the new node");
            Require(store.ActiveProfile.RoutingPolicyId == "truckersmp" && store.GetPolicy("global") is not null,
                "reset restores routing and profile assignment");
            Require(File.ReadAllText(external) == "keep me" && File.Exists(Path.Combine(backupDir, "older-backup.txt")),
                "reset preserves external files and previous backups");
            var backup = Directory.GetFiles(backupDir, "backup_before_reset_*.zip").Single();
            using (var zip = System.IO.Compression.ZipFile.OpenRead(backup))
            {
                Require(zip.GetEntry("mqvpn.db") is not null && zip.GetEntry("client-windows.json") is not null,
                    "reset safety backup contains database and original default file");
            }
            // Restore the actual backup through the production import path.
            Require(store.ImportBackup(backup), "reset safety backup can be restored");
            SQLiteHelper.Instance.Dispose();
            store = ProfileStore.Load(root);
            Require(store.State.Profiles.Count == 3 && store.State.Profiles.All(p => oldIds.Contains(p.Id)) &&
                    store.ActiveProfile.Id == active.Id && store.State.LocalSocksPort == 20808,
                "restored backup recovers nodes, active ID and settings after restart");
        }
        finally
        {
            SQLiteHelper.Instance.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    internal static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine($"PASS: {description}");
    }
}
