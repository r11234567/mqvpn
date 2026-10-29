using System.Runtime.InteropServices;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TruckersMP.Proxy;

internal sealed record GitHubAsset(string Name, string DownloadUrl, long Size);
internal sealed record GitHubRelease(string Tag, string Name, IReadOnlyList<GitHubAsset> Assets, bool IsPreRelease, string Body = "");

internal static class GitHubReleaseClient
{
    private const string TagMarker = "/releases/tag/";
    private static readonly HttpClient Client = CreateClient();

    private sealed record ReleaseFeedEntry(string Tag, string Title);

    public static async Task<GitHubRelease> GetLatestAsync(
        string repository,
        bool includePreRelease,
        CancellationToken cancellationToken)
    {
        var parts = repository.Trim().Trim('/').Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("更新源应使用 owner/repository 格式");

        // releases/latest never reports a pre-release, so the full list is required to see one.
        var endpoint = includePreRelease
            ? $"https://api.github.com/repos/{parts[0]}/{parts[1]}/releases?per_page=30"
            : $"https://api.github.com/repos/{parts[0]}/{parts[1]}/releases/latest";
        using var response = await Client.GetAsync(endpoint, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return await GetLatestFromPublicPagesAsync(parts[0], parts[1], includePreRelease, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = json.RootElement;
        if (!includePreRelease)
            return ReadRelease(root);

        var releases = root.EnumerateArray()
            .Where(element => !ReadFlag(element, "draft"))
            .Select(ReadRelease)
            .ToArray();
        if (releases.Length == 0)
            throw new InvalidOperationException("GitHub 上没有可用的发布记录");
        return releases.FirstOrDefault(release => release.Assets.Count > 0) ?? releases[0];
    }

    private static GitHubRelease ReadRelease(JsonElement element)
    {
        var assets = element.GetProperty("assets").EnumerateArray()
            .Select(asset => new GitHubAsset(
                asset.GetProperty("name").GetString() ?? string.Empty,
                asset.GetProperty("browser_download_url").GetString() ?? string.Empty,
                asset.GetProperty("size").GetInt64()))
            .ToArray();
        var body = element.TryGetProperty("body", out var b) ? b.GetString() ?? string.Empty : string.Empty;
        return new GitHubRelease(
            element.GetProperty("tag_name").GetString() ?? "unknown",
            element.GetProperty("name").GetString() ?? string.Empty,
            assets,
            ReadFlag(element, "prerelease"),
            body);
    }

    private static bool ReadFlag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static async Task<GitHubRelease> GetLatestFromPublicPagesAsync(
        string owner,
        string repository,
        bool includePreRelease,
        CancellationToken cancellationToken)
    {
        var entries = await ReadReleaseFeedAsync(owner, repository, cancellationToken);
        // The feed lists pre-releases without marking them, so the stable tag comes from the redirect.
        var stableTag = await TryResolveLatestStableTagAsync(owner, repository, cancellationToken);
        var selected = includePreRelease
            ? entries[0]
            : entries.FirstOrDefault(entry => string.Equals(entry.Tag, stableTag, StringComparison.Ordinal))
                ?? entries[0];
        var isPreRelease = stableTag is not null &&
            !string.Equals(selected.Tag, stableTag, StringComparison.Ordinal);

        var html = await Client.GetStringAsync(
            $"https://github.com/{owner}/{repository}/releases/expanded_assets/{Uri.EscapeDataString(selected.Tag)}",
            cancellationToken);
        var assets = Regex.Matches(html, "href=\"(?<url>/[^\"]+/releases/download/[^\"]+)\"")
            .Select(match => WebUtility.HtmlDecode(match.Groups["url"].Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(url => new GitHubAsset(
                Uri.UnescapeDataString(url[(url.LastIndexOf('/') + 1)..]),
                "https://github.com" + url,
                0))
            .ToArray();
        if (assets.Length == 0)
            throw new InvalidOperationException("GitHub 发布页没有可下载资源");
        return new GitHubRelease(selected.Tag, selected.Title, assets, isPreRelease);
    }

    private static async Task<IReadOnlyList<ReleaseFeedEntry>> ReadReleaseFeedAsync(
        string owner,
        string repository,
        CancellationToken cancellationToken)
    {
        var atom = await Client.GetStringAsync(
            $"https://github.com/{owner}/{repository}/releases.atom",
            cancellationToken);
        var document = XDocument.Parse(atom);
        XNamespace ns = "http://www.w3.org/2005/Atom";
        var entries = (document.Root?.Elements(ns + "entry") ?? Enumerable.Empty<XElement>())
            .Select(entry =>
            {
                var link = entry.Elements(ns + "link")
                    .Select(element => element.Attribute("href")?.Value)
                    .FirstOrDefault(value => value?.Contains(TagMarker) == true);
                if (link is null)
                    return null;
                var tag = Uri.UnescapeDataString(link[(link.LastIndexOf(TagMarker, StringComparison.Ordinal) + TagMarker.Length)..]);
                return new ReleaseFeedEntry(tag, entry.Element(ns + "title")?.Value ?? tag);
            })
            .OfType<ReleaseFeedEntry>()
            .ToArray();
        if (entries.Length == 0)
            throw new InvalidOperationException("GitHub Release feed 没有发布记录");
        return entries;
    }

    private static async Task<string?> TryResolveLatestStableTagAsync(
        string owner,
        string repository,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client.GetAsync(
                $"https://github.com/{owner}/{repository}/releases/latest",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var resolved = response.RequestMessage?.RequestUri?.ToString() ?? string.Empty;
            var marker = resolved.LastIndexOf(TagMarker, StringComparison.Ordinal);
            return marker < 0 ? null : Uri.UnescapeDataString(resolved[(marker + TagMarker.Length)..]);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public static Task DownloadAsync(string url, string destination, CancellationToken cancellationToken) =>
        DownloadAsync(url, destination, null, cancellationToken);

    public static async Task DownloadAsync(
        string url,
        string destination,
        IProgress<(long received, long total)>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        var totalRead = 0L;
        int bytesRead;

        while ((bytesRead = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalRead += bytesRead;
            progress?.Report((totalRead, totalBytes));
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MQVPN-GUI", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}

internal static class KernelInstaller
{
    public static GitHubAsset SelectAsset(string kernel, GitHubRelease release, Architecture? arch = null)
    {
        var targetArch = arch ?? RuntimeInformation.ProcessArchitecture;
        var candidates = release.Assets.Where(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
        GitHubAsset? selected = kernel switch
        {
            "gui" => targetArch == Architecture.Arm64
                ? candidates.FirstOrDefault(asset =>
                    asset.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase) &&
                    (asset.Name.Contains("mqvpn_windows_gui", StringComparison.OrdinalIgnoreCase) ||
                     asset.Name.Contains("windows_gui", StringComparison.OrdinalIgnoreCase) ||
                     asset.Name.Contains("mqvpn", StringComparison.OrdinalIgnoreCase)))
                  ?? candidates.FirstOrDefault(asset => asset.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase))
                : candidates.FirstOrDefault(asset =>
                    (asset.Name.Contains("x64", StringComparison.OrdinalIgnoreCase) || asset.Name.Contains("amd64", StringComparison.OrdinalIgnoreCase)) &&
                    (asset.Name.Contains("mqvpn_windows_gui", StringComparison.OrdinalIgnoreCase) ||
                     asset.Name.Contains("windows_gui", StringComparison.OrdinalIgnoreCase)))
                  ?? candidates.FirstOrDefault(asset =>
                    !asset.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase) &&
                    (asset.Name.Contains("mqvpn_windows_gui", StringComparison.OrdinalIgnoreCase) ||
                     asset.Name.Contains("windows_gui", StringComparison.OrdinalIgnoreCase)))
                  ?? candidates.FirstOrDefault(asset =>
                    !asset.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase) &&
                    asset.Name.Contains("mqvpn", StringComparison.OrdinalIgnoreCase) &&
                    asset.Name.Contains("gui", StringComparison.OrdinalIgnoreCase)),
            "mqvpn" => targetArch == Architecture.Arm64
                ? candidates.FirstOrDefault(asset =>
                    !asset.Name.Contains("gui", StringComparison.OrdinalIgnoreCase) &&
                    asset.Name.Contains("windows", StringComparison.OrdinalIgnoreCase) &&
                    asset.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase))
                : candidates.FirstOrDefault(asset =>
                    !asset.Name.Contains("gui", StringComparison.OrdinalIgnoreCase) &&
                    asset.Name.Contains("windows", StringComparison.OrdinalIgnoreCase) &&
                    (asset.Name.Contains("amd64", StringComparison.OrdinalIgnoreCase) ||
                     asset.Name.Contains("x64", StringComparison.OrdinalIgnoreCase) ||
                     asset.Name.Contains("x86_64", StringComparison.OrdinalIgnoreCase))),
            "xray" => targetArch == Architecture.Arm64
                ? candidates.FirstOrDefault(asset =>
                    asset.Name.Contains("xray", StringComparison.OrdinalIgnoreCase) &&
                    asset.Name.Contains("windows", StringComparison.OrdinalIgnoreCase) &&
                    asset.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase))
                  ?? candidates.FirstOrDefault(asset =>
                    asset.Name.Contains("xray", StringComparison.OrdinalIgnoreCase) &&
                    asset.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase))
                : candidates.FirstOrDefault(asset =>
                    asset.Name.Equals("Xray-windows-64.zip", StringComparison.OrdinalIgnoreCase))
                  ?? candidates.FirstOrDefault(asset =>
                    asset.Name.Contains("xray", StringComparison.OrdinalIgnoreCase) &&
                    asset.Name.Contains("windows", StringComparison.OrdinalIgnoreCase) &&
                    asset.Name.Contains("64", StringComparison.OrdinalIgnoreCase) &&
                    !asset.Name.Contains("arm", StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };
        return selected ?? throw new InvalidOperationException($"Release {release.Tag} 中没有兼容的 Windows {targetArch} ZIP");
    }

    public static async Task InstallGuiAsync(
        GitHubAsset asset,
        IProgress<(long received, long total)>? progress,
        CancellationToken cancellationToken)
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"mqvpn-gui-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        var archive = Path.Combine(temporaryRoot, asset.Name);

        try
        {
            await GitHubReleaseClient.DownloadAsync(asset.DownloadUrl, archive, progress, cancellationToken);

            var currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var exeName = !string.IsNullOrEmpty(currentExe) ? Path.GetFileName(currentExe) : "MQVPN.exe";
            var currentPid = Environment.ProcessId;

            // 寻找独立更新助手 MqvpnUpgrade.exe
            var updaterPath = Path.Combine(appDir, "MqvpnUpgrade.exe");
            if (!File.Exists(updaterPath))
            {
                var fallback = Path.Combine(appDir, "..", "updater", "bin", "Debug", "net9.0-windows", "MqvpnUpgrade.exe");
                if (File.Exists(fallback))
                    updaterPath = fallback;
            }

            if (File.Exists(updaterPath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = updaterPath,
                    Arguments = $"\"{archive}\" \"{appDir}\" \"{exeName}\" {currentPid}",
                    UseShellExecute = true,
                    WorkingDirectory = appDir
                };
                Process.Start(psi);
                Environment.Exit(0);
            }
            else
            {
                throw new FileNotFoundException("未找到更新助手 MqvpnUpgrade.exe，无法完成无锁覆盖替换。");
            }
        }
        catch
        {
            if (Directory.Exists(temporaryRoot))
            {
                try { Directory.Delete(temporaryRoot, true); } catch { }
            }
            throw;
        }
    }

    public static IReadOnlyList<GitHubAsset> SelectGeoDataAssets(GitHubRelease release)
    {
        var names = new[] { "geoip.dat", "geosite.dat" };
        var assets = names.Select(name => release.Assets.FirstOrDefault(asset =>
                asset.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Release {release.Tag} 中缺少 {name}"))
            .ToArray();
        return assets;
    }

    public static async Task InstallAsync(
        string kernel,
        GitHubAsset asset,
        string runtimeRoot,
        CancellationToken cancellationToken)
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"mqvpn-update-{Guid.NewGuid():N}");
        var volumeStage = Path.Combine(runtimeRoot, $".{kernel}-stage-{Guid.NewGuid():N}");
        var archive = Path.Combine(temporaryRoot, asset.Name);
        var extracted = Path.Combine(temporaryRoot, "extracted");
        var staged = Path.Combine(temporaryRoot, "staged");
        Directory.CreateDirectory(extracted);
        Directory.CreateDirectory(staged);

        try
        {
            await GitHubReleaseClient.DownloadAsync(asset.DownloadUrl, archive, cancellationToken);
            ZipFile.ExtractToDirectory(archive, extracted);
            var executableName = kernel switch
            {
                "mqvpn" => "mqvpn.exe",
                "xray" => "xray.exe",
                _ => throw new InvalidOperationException($"不支持的内核: {kernel}"),
            };
            var executable = Directory.EnumerateFiles(extracted, executableName, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (executable is null)
                throw new InvalidDataException("更新包中找不到内核可执行文件");

            CopyDirectory(Path.GetDirectoryName(executable)!, staged);
            if (kernel == "xray")
            {
                var wintunTarget = Path.Combine(staged, "wintun.dll");
                if (!File.Exists(wintunTarget))
                {
                    var currentWintun = Path.Combine(runtimeRoot, "xray", "wintun.dll");
                    var bundledWintun = Path.Combine(runtimeRoot, "mqvpn", "wintun.dll");
                    var wintunSource = File.Exists(currentWintun) ? currentWintun : bundledWintun;
                    if (!File.Exists(wintunSource))
                        throw new InvalidDataException("Xray TUN 需要 wintun.dll，但更新包和本地运行时均未提供");
                    File.Copy(wintunSource, wintunTarget, true);
                }
            }
            else
            {
                foreach (var required in new[] { "mqvpn.exe", "xquic.dll", "wintun.dll" })
                {
                    if (!File.Exists(Path.Combine(staged, required)))
                        throw new InvalidDataException($"mqvpn 更新包缺少 {required}");
                }
            }

            await ValidateKernelAsync(kernel, staged, cancellationToken);
            CopyDirectory(staged, volumeStage);
            await ValidateKernelAsync(kernel, volumeStage, cancellationToken);
            var target = Path.Combine(runtimeRoot, kernel);
            var backup = Path.Combine(runtimeRoot, $".{kernel}-backup");
            if (Directory.Exists(backup))
                Directory.Delete(backup, true);
            if (Directory.Exists(target))
                Directory.Move(target, backup);
            try
            {
                Directory.Move(volumeStage, target);
                await ValidateKernelAsync(kernel, target, cancellationToken);
                if (Directory.Exists(backup))
                    Directory.Delete(backup, true);
            }
            catch
            {
                if (Directory.Exists(target))
                    Directory.Delete(target, true);
                if (Directory.Exists(backup))
                    Directory.Move(backup, target);
                throw;
            }
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
                Directory.Delete(temporaryRoot, true);
            if (Directory.Exists(volumeStage))
                Directory.Delete(volumeStage, true);
        }
    }

    public static async Task<string> GetInstalledVersionAsync(string kernel, string runtimeRoot)
    {
        if (kernel == "gui")
        {
            var asm = typeof(MainForm).Assembly;
            var ver = asm.GetName().Version;
            var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return !string.IsNullOrEmpty(infoVer) ? $"v{infoVer}" : (ver is not null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "v1.2.0");
        }

        if (kernel == "geodata")
        {
            var xrayDirectory = Path.Combine(runtimeRoot, "xray");
            var files = new[] { "geoip.dat", "geosite.dat" }
                .Select(name => new FileInfo(Path.Combine(xrayDirectory, name)))
                .ToArray();
            if (files.Any(file => !file.Exists))
                return "未完整安装";
            return string.Join(" | ", files.Select(file =>
                $"{file.Name} {file.Length / 1024d / 1024d:0.0} MB"));
        }

        var executable = Path.Combine(runtimeRoot, kernel, kernel + ".exe");
        if (!File.Exists(executable))
            return "未安装";
        return await RunVersionAsync(executable, kernel == "mqvpn" ? "--version" : "version", CancellationToken.None);
    }

    public static async Task InstallGeoDataAsync(
        IReadOnlyList<GitHubAsset> assets,
        string runtimeRoot,
        CancellationToken cancellationToken)
    {
        var targetDirectory = Path.Combine(runtimeRoot, "xray");
        Directory.CreateDirectory(targetDirectory);
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"mqvpn-geodata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);

        try
        {
            foreach (var asset in assets)
            {
                var staged = Path.Combine(temporaryRoot, asset.Name);
                await GitHubReleaseClient.DownloadAsync(asset.DownloadUrl, staged, cancellationToken);
                if (new FileInfo(staged).Length < 1024)
                    throw new InvalidDataException($"下载的 {asset.Name} 文件异常");
            }

            var operations = assets.Select(asset =>
            {
                var target = Path.Combine(targetDirectory, asset.Name);
                return (
                    Target: target,
                    Backup: target + ".backup",
                    Staged: target + $".new-{Guid.NewGuid():N}");
            }).ToArray();
            var installedTargets = new List<string>();
            try
            {
                for (var index = 0; index < assets.Count; index++)
                {
                    var asset = assets[index];
                    var operation = operations[index];
                    File.Copy(Path.Combine(temporaryRoot, asset.Name), operation.Staged, true);
                    if (new FileInfo(operation.Staged).Length < 1024)
                        throw new InvalidDataException($"暂存的 {asset.Name} 文件异常");
                }

                foreach (var operation in operations)
                {
                    if (File.Exists(operation.Backup))
                        File.Delete(operation.Backup);
                    if (File.Exists(operation.Target))
                    {
                        File.Move(operation.Target, operation.Backup, true);
                    }
                    File.Move(operation.Staged, operation.Target, true);
                    installedTargets.Add(operation.Target);
                }

                foreach (var operation in operations)
                {
                    if (File.Exists(operation.Backup))
                    {
                        try
                        {
                            File.Delete(operation.Backup);
                        }
                        catch
                        {
                            // The new files are installed; a stale backup can be removed on the next update.
                        }
                    }
                }
            }
            catch
            {
                foreach (var target in installedTargets)
                {
                    if (File.Exists(target))
                        File.Delete(target);
                }
                foreach (var operation in operations)
                {
                    if (File.Exists(operation.Backup))
                        File.Move(operation.Backup, operation.Target, true);
                }
                throw;
            }
            finally
            {
                foreach (var operation in operations)
                {
                    if (File.Exists(operation.Staged))
                        File.Delete(operation.Staged);
                }
            }
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
                Directory.Delete(temporaryRoot, true);
        }
    }

    private static async Task ValidateKernelAsync(string kernel, string directory, CancellationToken cancellationToken)
    {
        var executable = Path.Combine(directory, kernel + ".exe");
        var output = await RunVersionAsync(executable, kernel == "mqvpn" ? "--version" : "version", cancellationToken);
        if (string.IsNullOrWhiteSpace(output))
            throw new InvalidDataException("内核版本检查没有输出");
    }

    private static async Task<string> RunVersionAsync(string executable, string argument, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动内核版本检查");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = (await outputTask).Trim();
        var error = (await errorTask).Trim();
        var reported = output.Length > 0 ? output : error;
        // Xray prints its build details across several lines; keep them all and let the UI wrap.
        var lines = reported.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);
        return string.Join(Environment.NewLine, lines);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
