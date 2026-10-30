using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace TruckersMP.Proxy;

internal enum LogSeverity
{
    Debug,
    Info,
    Warning,
    Error,
}

internal enum LogChannel
{
    Client,
    Mqvpn,
    Xray,
}

internal sealed record LogEntry(
    DateTime Timestamp,
    string Source,
    string Message,
    bool Important,
    LogSeverity Severity,
    LogChannel Channel);

internal static class LogLevels
{
    public const string Default = "info";

    public static readonly string[] All = ["debug", "info", "warn", "error"];

    public static string Normalize(string? value)
    {
        var level = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return level switch
        {
            "debug" => "debug",
            "info" => "info",
            "warn" or "warning" => "warn",
            "error" => "error",
            _ => Default,
        };
    }

    // mqvpn accepts warn; xray spells the same level warning.
    public static string ToXray(string? value) => Normalize(value) switch
    {
        "warn" => "warning",
        var level => level,
    };

    public static LogSeverity ToSeverity(string? value) => Normalize(value) switch
    {
        "debug" => LogSeverity.Debug,
        "warn" => LogSeverity.Warning,
        "error" => LogSeverity.Error,
        _ => LogSeverity.Info,
    };
}

internal static class LogClassifier
{
    private static readonly string[] ImportantInfoPatterns =
    {
        "state:",
        "handshake finished",
        "tunnel 200 ok",
        "address_assign",
        "tun mqvpn0 created",
        "tun mqvpn0 configured",
        "tun mqvpn0 destroyed",
        "established",
        "connection closed",
        "dns restored",
        "received ctrl",
        "graceful stop",
        "xray-tmp",
        "xray started",
        "xray stopped",
        "mqvpn started",
        "split tunnel",
        "proxy is ready",
        "proxy stopped",
        "log buffer overflow",
    };

    // Classifying and filtering share one lowercase pass, and lines the selected level discards
    // never reach the pattern scan. Both matter because this runs per line at debug level.
    public static bool TryCreate(
        string source,
        string message,
        LogSeverity minimumSeverity,
        [NotNullWhen(true)] out LogEntry? entry)
    {
        entry = null;
        var channel = ResolveChannel(source);
        var lower = message.ToLowerInvariant();
        var severity = ResolveSeverity(lower);

        // The selected level governs kernel output only; the GUI's own status lines always show.
        if (channel != LogChannel.Client)
        {
            if (severity < minimumSeverity)
                return false;
            // Successful xquic reports are pure noise unless the user asked for debug output.
            if (minimumSeverity > LogSeverity.Debug &&
                lower.Contains("[xquic]") &&
                lower.Contains("[report]") &&
                (lower.Contains("|err:0|") || lower.Contains("|err:0x0|")))
                return false;
        }

        var important = severity >= LogSeverity.Warning ||
            ImportantInfoPatterns.Any(lower.Contains) ||
            (lower.Contains("tun ") &&
             (lower.Contains(" created") || lower.Contains(" configured") || lower.Contains(" destroyed")));
        entry = new LogEntry(DateTime.Now, source, message, important, severity, channel);
        return true;
    }

    private static LogSeverity ResolveSeverity(string lower)
    {
        var expectedShutdown = lower.Contains("graceful stop requested");
        var isError =
            (lower.Contains("level=error") ||
             lower.Contains("[err]") ||
             lower.Contains("[error]") ||
             lower.Contains(" error:") ||
             lower.Contains(" failed") ||
             lower.Contains("fatal"));
        var isWarning = !expectedShutdown &&
            lower.Contains("level=warning") ||
            !expectedShutdown && lower.Contains("[wrn]") ||
            !expectedShutdown && lower.Contains("[warning]") ||
            !expectedShutdown && lower.Contains(" warning:");
        var isDebug =
            lower.Contains("level=debug") ||
            lower.Contains("[dbg]") ||
            lower.Contains("[debug]");
        return isError
            ? LogSeverity.Error
            : isWarning
                ? LogSeverity.Warning
                : isDebug
                    ? LogSeverity.Debug
                    : LogSeverity.Info;
    }

    private static LogChannel ResolveChannel(string source) => source switch
    {
        "MQVPN" => LogChannel.Mqvpn,
        "XRAY" => LogChannel.Xray,
        _ => LogChannel.Client,
    };
}

internal sealed class LogBuffer
{
    private const int Capacity = 12000;

    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private int _count;
    private int _dropped;

    public void Add(LogEntry entry)
    {
        _entries.Enqueue(entry);
        var count = Interlocked.Increment(ref _count);
        while (count > Capacity && _entries.TryDequeue(out _))
        {
            Interlocked.Increment(ref _dropped);
            count = Interlocked.Decrement(ref _count);
        }
    }

    // Overflow deletes the oldest lines, which reads as an unexplained break in the log unless the
    // display says so, so the count is reported once and reset rather than silently swallowed.
    public int TakeDroppedCount() => Interlocked.Exchange(ref _dropped, 0);

    public IEnumerable<LogEntry> Drain(int max)
    {
        for (var i = 0; i < max && _entries.TryDequeue(out var entry); i++)
        {
            Interlocked.Decrement(ref _count);
            yield return entry;
        }
    }
}
