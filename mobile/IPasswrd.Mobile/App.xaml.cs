using IPasswrd.Mobile.Services;
using IPasswrd.Mobile.Views;

namespace IPasswrd.Mobile;

public partial class App : Application
{
    private Window? _window;
    private bool _dying;   // окно умирает — корневую страницу больше не трогаем (гонка фрагментов Shell)

    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        _window = new Window(RootPage());
        _dying = false;

        // автоблокировка по времени в фоне (как «после простоя» на Windows)
        _window.Stopped += (_, _) => Svc.State.OnBackgrounded();
        _window.Resumed += (_, _) => Svc.State.OnResumed();
        // Закрытие приложения (смахнули из недавних) = замок сразу: Android может держать
        // процесс с открытым сейфом в памяти сколь угодно долго, полагаться на смерть процесса нельзя.
        _window.Destroying += (_, _) => { _dying = true; try { Svc.State.Lock(); } catch { } };

        Svc.State.LockedChanged += () =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_window is null || _dying) return;
                bool showingShell = _window.Page is AppShell;
                if (Svc.State.IsUnlocked == showingShell) return;   // страница уже правильная — не пересоздавать Shell
                try { _window.Page = RootPage(); } catch { /* окно уже умирает */ }
            });

        return _window;
    }

    private static Page RootPage() =>
        Svc.State.IsUnlocked ? new AppShell() : new NavigationPage(new UnlockPage());
}
