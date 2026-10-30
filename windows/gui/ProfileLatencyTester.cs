using System.Diagnostics;
using System.Text.Json.Nodes;

namespace TruckersMP.Proxy;

internal static class ProfileLatencyTester
{
    public static async Task<int> TestAsync(
        string mqvpnExecutable,
        JsonObject sourceConfig,
        CancellationToken cancellationToken)
    {
        var config = sourceConfig.DeepClone().AsObject();
        config["tun_name"] = $"mqvpn-test-{Guid.NewGuid():N}"[..20];
        config["manage_routes"] = false;
        config["kill_switch"] = false;
        config["reconnect"] = false;
        config["dns"] = new JsonArray();
        config["log_level"] = "info";

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"mqvpn-test-{Guid.NewGuid():N}.json");
        ProfileStore.WriteMqvpnConfigFile(temporaryPath, config);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        long extendedConnectTimestamp = 0;
        long connectingTimestamp = 0;
        long networkEndTimestamp = 0;

        await using var service = new ManagedService(
            "MQVPN-TEST",
            mqvpnExecutable,
            Path.GetDirectoryName(mqvpnExecutable)!,
            ["--config", temporaryPath],
            (_, line) =>
            {
                var lower = line.ToLowerInvariant();
                // Extended CONNECT sent is when the HTTP/3 MASQUE tunnel request is dispatched.
                // The duration from Extended CONNECT to tunnel 200 OK is exactly 1-RTT to the server.
                if (lower.Contains("extended connect sent"))
                {
                    Interlocked.Exchange(ref extendedConnectTimestamp, Stopwatch.GetTimestamp());
                }
                else if (lower.Contains("connecting to "))
                {
                    Interlocked.CompareExchange(ref connectingTimestamp, Stopwatch.GetTimestamp(), 0);
                }

                if (lower.Contains("tunnel 200 ok"))
                {
                    Interlocked.CompareExchange(ref networkEndTimestamp, Stopwatch.GetTimestamp(), 0);
                    ready.TrySetResult();
                }
                if (lower.Contains("[err]") || lower.Contains("[error]") || lower.Contains("fatal"))
                    failure.TrySetResult(line);
            });

        try
        {
            var fallbackStopwatch = Stopwatch.StartNew();
            service.Start();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var completed = await Task.WhenAny(ready.Task, failure.Task).WaitAsync(timeout.Token);
            if (completed == failure.Task)
                throw new InvalidOperationException(await failure.Task);
            await ready.Task;
            fallbackStopwatch.Stop();

            var extStart = Interlocked.Read(ref extendedConnectTimestamp);
            var endTs = Interlocked.Read(ref networkEndTimestamp);
            if (extStart > 0 && endTs >= extStart)
            {
                var rttMs = (endTs - extStart) * 1000.0 / Stopwatch.Frequency;
                return Math.Max(1, (int)Math.Round(rttMs));
            }

            var connStart = Interlocked.Read(ref connectingTimestamp);
            if (connStart > 0 && endTs >= connStart)
            {
                var totalNetMs = (endTs - connStart) * 1000.0 / Stopwatch.Frequency;
                return Math.Max(1, (int)Math.Round(totalNetMs));
            }

            return Math.Max(1, (int)Math.Round(fallbackStopwatch.Elapsed.TotalMilliseconds));
        }
        finally
        {
            await service.StopAsync();
            File.Delete(temporaryPath);
        }
    }
}
