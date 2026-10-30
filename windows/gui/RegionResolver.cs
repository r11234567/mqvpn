using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace TruckersMP.Proxy;

internal static class RegionResolver
{
    private static readonly HttpClient Client = CreateClient();
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new();
    private static readonly object CacheFileSync = new();
    private static readonly string CachePath = Path.Combine(AppContext.BaseDirectory, "config", "region-cache.json");
    private static bool _cacheLoaded;

    public static async Task<string> ResolveAsync(string serverAddress, CancellationToken cancellationToken)
    {
        var host = ParseHost(serverAddress);
        if (host.Length == 0)
            throw new InvalidOperationException("服务器地址为空");

        var address = await ResolveAddressAsync(host, cancellationToken);
        var key = address.ToString();
        LoadCache();
        Cache.TryGetValue(key, out var cached);
        if (cached is not null && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Region;

        string? region = null;
        try
        {
            region = await QueryWithTimeoutAsync(QueryIpWhoAsync, address, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
        }

        if (region is null)
        {
            try
            {
                region = await QueryWithTimeoutAsync(QueryIpInfoAsync, address, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }

        if (region is null)
        {
            if (cached is not null)
                return cached.Region;
            throw new InvalidOperationException("在线地区服务暂时不可用");
        }

        Cache[key] = new CacheEntry(region, DateTimeOffset.UtcNow.AddHours(24));
        SaveCache();
        return region;
    }

    private static async Task<string> QueryWithTimeoutAsync(
        Func<IPAddress, CancellationToken, Task<string>> query,
        IPAddress address,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        return await query(address, timeout.Token);
    }

    private static async Task<string> QueryIpWhoAsync(IPAddress address, CancellationToken cancellationToken)
    {
        var uri = $"https://ipwho.is/{Uri.EscapeDataString(address.ToString())}" +
                  "?fields=success,country,region,city,message";
        using var document = await GetJsonAsync(uri, cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("success", out var success) || !success.GetBoolean())
            throw new InvalidOperationException("ipwho.is 未返回有效结果");
        return FormatRegion(root, "country", "region", "city");
    }

    private static async Task<string> QueryIpInfoAsync(IPAddress address, CancellationToken cancellationToken)
    {
        var uri = $"https://ipinfo.io/{Uri.EscapeDataString(address.ToString())}/json";
        using var document = await GetJsonAsync(uri, cancellationToken);
        return FormatRegion(document.RootElement, "country", "region", "city");
    }

    private static async Task<JsonDocument> GetJsonAsync(string uri, CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
    }

    private static string FormatRegion(JsonElement root, string countryName, string regionName, string cityName)
    {
        var country = ReadString(root, countryName);
        var region = ReadString(root, regionName);
        var city = ReadString(root, cityName);
        var detail = city.Length > 0 ? city : region;
        var display = country.Length > 0 && detail.Length > 0 &&
                      !country.Equals(detail, StringComparison.OrdinalIgnoreCase)
            ? $"{country} · {detail}"
            : country.Length > 0 ? country : detail;
        return display.Length > 0
            ? display
            : throw new InvalidOperationException("地区服务未返回地区名称");
    }

    private static void LoadCache()
    {
        lock (CacheFileSync)
        {
            if (_cacheLoaded)
                return;
            _cacheLoaded = true;
            try
            {
                if (!File.Exists(CachePath))
                    return;
                var entries = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(CachePath));
                if (entries is null)
                    return;
                foreach (var entry in entries)
                    Cache[entry.Key] = entry.Value;
            }
            catch
            {
            }
        }
    }

    private static void SaveCache()
    {
        lock (CacheFileSync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                var snapshot = Cache.ToDictionary(entry => entry.Key, entry => entry.Value);
                File.WriteAllText(CachePath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
            }
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MQVPN-Windows-GUI/1.0");
        return client;
    }

    private static async Task<IPAddress> ResolveAddressAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
            return literal;

        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        return addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)
               ?? addresses.FirstOrDefault()
               ?? throw new InvalidOperationException("无法解析服务器地址");
    }

    private static string ParseHost(string serverAddress)
    {
        var value = serverAddress.Trim();
        if (Uri.TryCreate("udp://" + value, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
            return uri.Host;
        return value;
    }

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private sealed record CacheEntry(string Region, DateTimeOffset ExpiresAt);
}
