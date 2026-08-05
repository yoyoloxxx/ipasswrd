// Спайк: может ли обычное (неупакованное) приложение читать уведомления Windows
// через UserNotificationListener — это путь для перехвата СМС-кодов из «Связи с телефоном».
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

try
{
    var listener = UserNotificationListener.Current;
    var access = await listener.RequestAccessAsync();
    Console.WriteLine($"access: {access}");

    if (access == UserNotificationListenerAccessStatus.Allowed)
    {
        var notifs = await listener.GetNotificationsAsync(NotificationKinds.Toast);
        Console.WriteLine($"count: {notifs.Count}");
        foreach (var n in notifs.Take(10))
        {
            string app = "?";
            try { app = n.AppInfo?.DisplayInfo?.DisplayName ?? "?"; } catch { }
            string text = "";
            try
            {
                var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                if (binding is not null)
                    text = string.Join(" | ", binding.GetTextElements().Select(t => t.Text));
            }
            catch { }
            Console.WriteLine($"[{app}] {text}");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
}
