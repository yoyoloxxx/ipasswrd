using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Velopack;

namespace IPasswrd.App;

internal static class Program
{
    // single-instance guard: a second launch wakes the first copy instead of starting anew
    private const string MutexName = @"Local\IPasswrd.App.SingleInstance";
    private const string ShowEventName = @"Local\IPasswrd.App.Show";

    internal static void LogCrash(Exception? ex, string where)
    {
        try
        {
            string dir = Environment.GetEnvironmentVariable("IPASSWRD_VAULT") is { Length: > 0 } v
                ? System.IO.Path.GetDirectoryName(v)!
                : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IPasswrd");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "crash-log.txt"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* если и это не вышло, помочь уже нечем */ }
    }

    [STAThread]
    public static void Main(string[] args)
    {
        // Must be the very first thing: on install, update and uninstall Velopack re-runs this
        // exe with its own arguments, does its work and exits. Putting the single-instance
        // guard ahead of it would make those hooks silently no-op whenever a copy is running.
        VelopackApp.Build().Run();

        // Падение менеджера паролей без следов - худший вид падения: человек видит, что окно
        // исчезло, и не может сказать нам ничего полезного. Пишем причину рядом с сейфом.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception, "domain");
        TaskScheduler.UnobservedTaskException += (_, e) => { LogCrash(e.Exception, "task"); e.SetObserved(); };

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
