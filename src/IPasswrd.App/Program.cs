using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace IPasswrd.App;

internal static class Program
{
    // single-instance guard: a second launch wakes the first copy instead of starting anew
    private const string MutexName = @"Local\IPasswrd.App.SingleInstance";
    private const string ShowEventName = @"Local\IPasswrd.App.Show";

    [STAThread]
    public static void Main(string[] args)
    {
        using var single = new Mutex(initiallyOwned: true, MutexName, out bool first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        if (!first)
        {
            try { showSignal.Set(); } catch { /* ignore */ }   // surface the running copy (it may be in the tray)
            return;
        }

        var wake = new Thread(() =>
        {
            while (true)
            {
                try { showSignal.WaitOne(); } catch { return; }
                Dispatcher.UIThread.Post(() =>
                {
                    if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d
                        && d.MainWindow is MainWindow mw)
                        mw.BringToFront();
                });
            }
        }) { IsBackground = true, Name = "single-instance-wake" };
        wake.Start();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // Blind-debug aid: if the app dies on startup, leave a trace we can read.
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "ipasswrd-app-crash.log"), ex.ToString()); }
            catch { /* ignore */ }
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
