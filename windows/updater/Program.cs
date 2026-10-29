using System.Diagnostics;
using System.IO.Compression;

namespace TruckersMP.Proxy.Updater;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("MQVPN 自更新助手 (MqvpnUpgrade)");
            Console.WriteLine("用法: MqvpnUpgrade <zip文件路径> [目标目录] [可执行文件名] [父进程PID]");
            return;
        }

        try
        {
            var zipPath = args[0].Trim('"', '\'');
            var targetDir = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])
                ? args[1].Trim('"', '\'')
                : AppDomain.CurrentDomain.BaseDirectory;
            var targetExe = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2])
                ? args[2].Trim('"', '\'')
                : "MQVPN.exe";
            var parentPid = args.Length > 3 && int.TryParse(args[3], out var pid)
                ? pid
                : 0;

            PerformUpgrade(zipPath, targetDir, targetExe, parentPid);
        }
        catch (Exception ex)
        {
            try
            {
                var errorLog = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update_error.log");
                File.WriteAllText(errorLog, $"{DateTime.Now}: {ex}");
            }
            catch
            {
                // Ignore log write failure
            }
        }
    }

    private static void PerformUpgrade(string zipPath, string targetDir, string targetExe, int parentPid)
    {
        if (!File.Exists(zipPath))
            return;

        // 1. 等待并确保父进程及旧实例退出
        if (parentPid > 0)
        {
            try
            {
                var parent = Process.GetProcessById(parentPid);
                if (!parent.WaitForExit(6000))
                {
                    parent.Kill();
                    parent.WaitForExit(2000);
                }
            }
            catch
            {
                // Process already terminated or inaccessible
            }
        }

        // 终结同目录下可能残余的 GUI 进程
        var exeBaseName = Path.GetFileNameWithoutExtension(targetExe);
        foreach (var p in Process.GetProcessesByName(exeBaseName))
        {
            try
            {
                var modulePath = p.MainModule?.FileName;
                if (!string.IsNullOrEmpty(modulePath) &&
                    modulePath.StartsWith(targetDir, StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill();
                    p.WaitForExit(2000);
                }
            }
            catch
            {
                // Ignore
            }
        }

        Thread.Sleep(800);

        // 2. 解压与原子文件替换
        var currentExe = Environment.ProcessPath ?? string.Empty;
        var backupExe = $"{currentExe}.old";
        if (File.Exists(backupExe))
        {
            try { File.Delete(backupExe); } catch { }
        }

        using (var archive = ZipFile.OpenRead(zipPath))
        {
            // 检测是否有多余顶层目录前缀（例如 mqvpn-windows-gui-x64/MQVPN.exe）
            var prefix = DetectCommonPrefix(archive);

            foreach (var entry in archive.Entries)
            {
                if (entry.Length == 0 && (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')))
                    continue;

                var relativePath = entry.FullName;
                if (!string.IsNullOrEmpty(prefix) && relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    relativePath = relativePath[prefix.Length..].TrimStart('/', '\\');
                }

                if (string.IsNullOrWhiteSpace(relativePath))
                    continue;

                var destinationPath = Path.GetFullPath(Path.Combine(targetDir, relativePath));

                // 路径安全检查，防止 ZipSlip 穿越
                var normalizedTarget = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!destinationPath.StartsWith(normalizedTarget, StringComparison.OrdinalIgnoreCase))
                    continue;

                var dir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                // 若解压目标是自身，先将自身重命名为 .old
                if (!string.IsNullOrEmpty(currentExe) &&
                    string.Equals(destinationPath, currentExe, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Move(currentExe, backupExe, true); } catch { }
                }

                TryExtractFile(entry, destinationPath);
            }
        }

        // 3. 启动更新后的程序
        Thread.Sleep(500);
        var newExePath = Path.Combine(targetDir, targetExe);
        if (File.Exists(newExePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = newExePath,
                    WorkingDirectory = targetDir,
                    UseShellExecute = true,
                });
            }
            catch
            {
                // Fallback attempt
                Process.Start(newExePath);
            }
        }

        // 4. 清理下载的 ZIP 包
        try
        {
            File.Delete(zipPath);
        }
        catch
        {
            // Ignore temporary file cleanup failure
        }
    }

    private static string DetectCommonPrefix(ZipArchive archive)
    {
        var firstEntry = archive.Entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.Name));
        if (firstEntry is null)
            return string.Empty;

        var parts = firstEntry.FullName.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1)
            return string.Empty;

        var potentialPrefix = parts[0] + "/";
        var allMatch = archive.Entries.All(e =>
            string.IsNullOrEmpty(e.FullName) ||
            e.FullName.StartsWith(potentialPrefix, StringComparison.OrdinalIgnoreCase) ||
            e.FullName.StartsWith(parts[0] + "\\", StringComparison.OrdinalIgnoreCase));

        return allMatch ? potentialPrefix : string.Empty;
    }

    private static bool TryExtractFile(ZipArchiveEntry entry, string destinationPath)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                entry.ExtractToFile(destinationPath, true);
                return true;
            }
            catch (IOException)
            {
                Thread.Sleep(1000);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(1000);
            }
        }
        return false;
    }
}
