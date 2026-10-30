// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TruckersMP.Proxy.Packaging;

internal sealed record PackageFile(string Path, string Sha256);
internal sealed record PackageManifest(int Schema, string Architecture, string SourceSha,
    string XquicSha, string BoringSslSha, PackageFile[] Files);

internal static class PackageContract
{
    public static readonly string[] RequiredFiles =
    [
        "MQVPN.exe", "MqvpnUpgrade.exe", "runtime/mqvpn/mqvpn.exe", "runtime/mqvpn/wintun.dll",
        "runtime/xray/xray.exe", "runtime/xray/wintun.dll", "runtime/xray/geoip.dat", "runtime/xray/geosite.dat",
        "LICENSE", "NOTICE", "third-party/xquic.txt", "third-party/boringssl.txt", "third-party/lwip.txt",
        "third-party/yyjson.txt", "third-party/libevent.txt", "third-party/wintun.txt", "third-party/xray.txt",
        "third-party/geodata.txt", "third-party/dotnet-LICENSE.txt", "third-party/dotnet-ThirdPartyNotices.txt",
        "third-party/nuget-licenses.txt", "third-party/desktop-LICENSE.txt", "core-build.json", "dependency-lock.json"
    ];

    public static string CurrentArchitecture => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64", Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException("仅支持 Windows x64/ARM64"),
    };

    public static string ResolveFile(string root, string relative)
    {
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        var prefix = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        if (System.IO.Path.IsPathRooted(relative) || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("包清单中的路径超出包目录");
        return path;
    }

    public static PackageManifest Validate(string root, string arch, string? sourceSha = null)
    {
        var manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(System.IO.Path.Combine(root, "package-manifest.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("缺少包清单");
        if (manifest.Schema != 1 || manifest.Architecture != arch ||
            !IsSha(manifest.SourceSha) || !IsSha(manifest.XquicSha) || !IsSha(manifest.BoringSslSha) ||
            (sourceSha is not null && !manifest.SourceSha.Equals(sourceSha, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("包来源或架构与要求不一致");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            if (entry.Path.Replace('\\', '/').StartsWith("config/", StringComparison.OrdinalIgnoreCase) || !files.Add(entry.Path))
                throw new InvalidDataException("发行包不能包含用户配置或重复文件");
            var path = ResolveFile(root, entry.Path);
            using var stream = File.OpenRead(path);
            if (stream.Length == 0 || !Convert.ToHexString(SHA256.HashData(stream)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"包文件校验失败: {entry.Path}");
        }
        foreach (var required in RequiredFiles)
            if (!files.Contains(required)) throw new InvalidDataException($"发行包缺少 {required}");
        foreach (var actual in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(root, actual).Replace('\\', '/');
            if (relative != "package-manifest.json" && !files.Contains(relative))
                throw new InvalidDataException($"文件未包含在包清单中: {relative}");
        }
        foreach (var file in files.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            ValidatePortableExecutable(ResolveFile(root, file), arch);
        return manifest;
    }

    private static bool IsSha(string value) => value is { Length: 40 } && value.All(Uri.IsHexDigit);

    public static void ValidatePortableExecutable(string path, string arch)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var machine = pe.PEHeaders.CoffHeader.Machine;
        if ((arch == "x64" && machine != Machine.Amd64) || (arch == "arm64" && machine != Machine.Arm64) ||
            arch is not ("x64" or "arm64"))
            throw new InvalidDataException($"PE 架构不匹配: {System.IO.Path.GetFileName(path)} ({machine})");
        var header = pe.PEHeaders.PEHeader ?? throw new InvalidDataException("缺少 PE 头");
        foreach (var descriptor in new[] { (header.ImportTableDirectory, 20, 12), (header.DelayImportTableDirectory, 32, 4) })
        {
            var (directory, stride, nameOffset) = descriptor;
            if (directory.RelativeVirtualAddress == 0) continue;
            var table = pe.GetSectionData(directory.RelativeVirtualAddress).GetContent();
            var length = Math.Min(table.Length, directory.Size);
            for (var offset = 0; offset + stride <= length; offset += stride)
            {
                var nameRva = BitConverter.ToUInt32(table.AsSpan(offset + nameOffset, 4));
                if (nameRva == 0) break;
                if (stride == 32 && (BitConverter.ToUInt32(table.AsSpan(offset, 4)) & 1) == 0)
                    nameRva = checked((uint)(nameRva - header.ImageBase));
                var bytes = pe.GetSectionData(checked((int)nameRva)).GetContent();
                var end = bytes.IndexOf((byte)0);
                if (end < 0) throw new InvalidDataException("无效 PE 导入表");
                var name = Encoding.ASCII.GetString(bytes.AsSpan(0, end));
                if (IsWindowsLibrary(name)) continue;
                if (!File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, name)))
                    throw new InvalidDataException($"缺少 {System.IO.Path.GetFileName(path)} 的运行依赖 {name}");
            }
        }
    }

    private static bool IsWindowsLibrary(string name) =>
        name.StartsWith("api-ms-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ext-ms-", StringComparison.OrdinalIgnoreCase) ||
        WindowsLibraries.Contains(name);

    private static readonly HashSet<string> WindowsLibraries = new(StringComparer.OrdinalIgnoreCase)
    {
        "kernel32.dll", "kernelbase.dll", "ntdll.dll", "user32.dll", "gdi32.dll", "advapi32.dll", "ws2_32.dll",
        "bcrypt.dll", "bcryptprimitives.dll", "ncrypt.dll", "crypt32.dll", "iphlpapi.dll", "dnsapi.dll",
        "secur32.dll", "sspicli.dll", "shell32.dll", "shlwapi.dll", "ole32.dll", "oleaut32.dll", "combase.dll",
        "comctl32.dll", "comdlg32.dll", "version.dll", "winmm.dll", "winhttp.dll", "wininet.dll", "msvcrt.dll",
        "ucrtbase.dll", "setupapi.dll", "cfgmgr32.dll", "rpcrt4.dll", "fwpuclnt.dll", "wtsapi32.dll", "mswsock.dll",
        "wintrust.dll", "normaliz.dll", "userenv.dll", "netapi32.dll", "powrprof.dll", "psapi.dll", "dwmapi.dll", "nci.dll"
    };
}
