using System.Diagnostics;
using System.IO.Compression;
using TruckersMP.Proxy.Packaging;

namespace TruckersMP.Proxy.Updater;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-check") return 0;
        var verifyApply = args.FirstOrDefault() == "--verify-apply";
        if (verifyApply) args = args[1..];
        if (args.Length < 2) return 2;
        var target = Path.GetFullPath(args[1]);
        var stage = Path.Combine(target, $".update-stage-{Guid.NewGuid():N}");
        try
        {
            ZipFile.ExtractToDirectory(args[0], stage);
            var manifests = Directory.GetFiles(stage, "package-manifest.json", SearchOption.AllDirectories);
            if (manifests.Length != 1) throw new InvalidDataException("更新包必须包含唯一发行清单");
            var root = Path.GetDirectoryName(manifests[0])!;
            var manifest = PackageContract.Validate(root, PackageContract.CurrentArchitecture);
            var executableName = !verifyApply && args.Length > 2 ? args[2] : "MQVPN.exe";
            if (executableName != "MQVPN.exe") throw new InvalidDataException("只支持完整 MQVPN GUI 发行包");
            // Validate everything before waiting for the GUI, whose callback
            // has already stopped the cores through their normal lifecycle.
            if (!verifyApply && args.Length > 3 && int.TryParse(args[3], out var parentId) && parentId > 0)
            {
                Process? parent = null;
                try { parent = Process.GetProcessById(parentId); } catch (ArgumentException) { }
                using (parent)
                    if (parent is not null && !parent.WaitForExit(15000))
                        throw new IOException("旧客户端尚未退出，未替换文件");
            }
            PackageTransaction.Apply(root, target, manifest);
            if (verifyApply)
            {
                PackageContract.Validate(target, PackageContract.CurrentArchitecture, manifest.SourceSha);
                File.WriteAllText(args[2], System.Text.Json.JsonSerializer.Serialize(new
                {
                    sourceSha = manifest.SourceSha, architecture = manifest.Architecture, appliedAndVerified = true,
                }));
                return 0;
            }
            Process.Start(new ProcessStartInfo(Path.Combine(target, executableName))
            {
                WorkingDirectory = target, UseShellExecute = true,
            });
            File.Delete(args[0]);
            return 0;
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "update_error.log"), $"{DateTime.UtcNow:O}: {ex}");
            return 1;
        }
        finally
        {
            if (Directory.Exists(stage))
                try { Directory.Delete(stage, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
