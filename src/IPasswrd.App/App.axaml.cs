using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace IPasswrd.App;

public partial class App : Application
{
    private MainWindow? _win;   // rooted here so a tray-only (windowless) start is never collected

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // closing the window hides to tray; the app exits only via the tray menu
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _win = new MainWindow();   // ctor wires the tray icon + browser-bridge pipe server

            bool tray = (desktop.Args ?? Array.Empty<string>())
                .Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
            if (!tray)
                desktop.MainWindow = _win;
            // --tray (autostart / started by the browser bridge): MainWindow is NOT assigned, so
            // the lifetime never shows it — no window, not even for a single frame. The tray icon
            // and the pipe server are alive; the window appears only when explicitly opened.
        }

        base.OnFrameworkInitializationCompleted();
    }
}
