// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

namespace TruckersMP.Proxy.Packaging;

internal static class PackageTransaction
{
    public static void Apply(string staged, string target, PackageManifest manifest)
    {
        Directory.CreateDirectory(target);
        var backup = Path.Combine(target, $".update-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(backup);
        var moved = new List<(string Target, string Backup, bool HadOriginal)>();
        var committed = false;
        try
        {
            // PackageContract validates the staged payload before GUI stops.
            foreach (var relative in manifest.Files.Select(f => f.Path).Append("package-manifest.json"))
            {
                var destination = PackageContract.ResolveFile(target, relative);
                var saved = PackageContract.ResolveFile(backup, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                var hadOriginal = File.Exists(destination);
                if (hadOriginal) File.Move(destination, saved);
                moved.Add((destination, saved, hadOriginal));
                File.Move(PackageContract.ResolveFile(staged, relative), destination);
            }
            committed = true;
        }
        catch (Exception installError)
        {
            var errors = new List<Exception> { installError };
            foreach (var item in moved.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(item.Target)) File.Delete(item.Target);
                    if (item.HadOriginal) File.Move(item.Backup, item.Target);
                }
                catch (Exception restoreError) { errors.Add(restoreError); }
            }
            throw new IOException($"更新未完成；已尝试回退原版本。恢复目录: {backup}", new AggregateException(errors));
        }
        finally
        {
            // The old updater may keep its renamed image mapped. Preserve that
            // recovery directory when Windows cannot remove it yet.
            if (committed)
                try { Directory.Delete(backup, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
