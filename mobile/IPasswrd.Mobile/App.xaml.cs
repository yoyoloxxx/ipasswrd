using IPasswrd.Mobile.Services;
using IPasswrd.Mobile.Views;

namespace IPasswrd.Mobile;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        _window = new Window(RootPage());

        // автоблокировка по времени в фоне (как «после простоя» на Windows)
        _window.Stopped += (_, _) => Svc.State.OnBackgrounded();
        _window.Resumed += (_, _) => Svc.State.OnResumed();

        Svc.State.LockedChanged += () =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_window is not null) _window.Page = RootPage();
            });

        return _window;
    }

    private static Page RootPage() =>
        Svc.State.IsUnlocked ? new AppShell() : new NavigationPage(new UnlockPage());
}
