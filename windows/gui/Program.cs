namespace TruckersMP.Proxy;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--host")
            return NativeProcessHost.Run(args[1..]);

        HiddenConsole.Initialize();
        using var instanceMutex = new Mutex(
            initiallyOwned: true,
            name: "Local\\MQVPN.GUI",
            createdNew: out var firstInstance);
        if (!firstInstance)
        {
            try
            {
                using var showEvent = EventWaitHandle.OpenExisting("Local\\MQVPN.GUI.Show");
                showEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The existing process may be between startup stages.
            }
            HiddenConsole.Shutdown();
            return 0;
        }

        using var showSignal = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            "Local\\MQVPN.GUI.Show");

        var app = FluentUi.EnsureApplication();
        var form = new MainForm();
        _ = Task.Run(() =>
        {
            while (!form.IsDisposed)
            {
                if (!showSignal.WaitOne(500))
                    continue;
                if (form.IsDisposed)
                    continue;
                try
                {
                    form.Dispatcher.BeginInvoke(form.ShowFromSecondInstance);
                }
                catch (InvalidOperationException)
                {
                    break;
                }
            }
        });

        app.Run(form);
        HiddenConsole.Shutdown();
        return 0;
    }
}
