using System.Diagnostics;

namespace TruckersMP.Proxy;

internal sealed class ManagedService : IAsyncDisposable
{
    private static readonly ServiceProcessJob ServiceHosts = new();
    private readonly string _name;
    private readonly string _executable;
    private readonly string _workingDirectory;
    private readonly string[] _arguments;
    private readonly Action<string, string> _log;
    private Process? _host;
    private TaskCompletionSource<int>? _exitSource;
    private volatile bool _stopRequested;

    public ManagedService(
        string name,
        string executable,
        string workingDirectory,
        IEnumerable<string> arguments,
        Action<string, string> log)
    {
        _name = name;
        _executable = executable;
        _workingDirectory = workingDirectory;
        _arguments = arguments.ToArray();
        _log = log;
    }

    public event Action<int, bool>? Exited;

    public bool IsRunning => _host is { HasExited: false };

    public void Start()
    {
        if (IsRunning)
            return;

        _stopRequested = false;

        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot resolve GUI executable path");
        var startInfo = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add("--exe");
        startInfo.ArgumentList.Add(_executable);
        startInfo.ArgumentList.Add("--workdir");
        startInfo.ArgumentList.Add(_workingDirectory);
        startInfo.ArgumentList.Add("--");
        foreach (var argument in _arguments)
            startInfo.ArgumentList.Add(argument);

        var host = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        _exitSource = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OutputDataReceived += (_, eventArgs) => Forward(eventArgs.Data);
        host.ErrorDataReceived += (_, eventArgs) => Forward(eventArgs.Data);
        host.Exited += (_, _) =>
        {
            var code = host.ExitCode;
            _exitSource.TrySetResult(code);
            Exited?.Invoke(code, _stopRequested);
        };

        if (!host.Start())
            throw new InvalidOperationException($"Cannot start {_name}");

        try
        {
            ServiceHosts.Add(host);
        }
        catch
        {
            host.Kill(true);
            host.Dispose();
            throw;
        }

        _host = host;
        host.BeginOutputReadLine();
        host.BeginErrorReadLine();
    }

    public async Task StopAsync()
    {
        _stopRequested = true;
        var host = _host;
        if (host is null)
            return;

        if (!host.HasExited)
        {
            try
            {
                await host.StandardInput.WriteLineAsync("stop");
                await host.StandardInput.FlushAsync();
            }
            catch
            {
                // The host may have exited between the state check and the write.
            }

            var exitTask = _exitSource?.Task ?? host.WaitForExitAsync();
            try
            {
                await exitTask.WaitAsync(TimeSpan.FromSeconds(12));
            }
            catch (TimeoutException)
            {
                _log("GUI", $"{_name} graceful stop timed out; terminating host");
                if (!host.HasExited)
                    host.Kill(true);
                await host.WaitForExitAsync();
            }
        }

        host.Dispose();
        _host = null;
        _exitSource = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private void Forward(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        _log(_name, line);
    }
}
