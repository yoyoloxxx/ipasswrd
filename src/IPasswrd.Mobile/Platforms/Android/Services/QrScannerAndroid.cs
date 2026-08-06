using Android.Content;
using IPasswrd.Mobile.Services;

namespace IPasswrd.Mobile.Platforms.Android.Services;

/// <summary>
/// Сканер QR — аналог QrScannerIos. Открывает отдельный полноэкранный экран камеры
/// (CameraX + декодер ZXing) и возвращает содержимое первого распознанного кода.
/// </summary>
public sealed class QrScannerAndroid : IQrScanner
{
    public async Task<string?> ScanAsync()
    {
        PermissionStatus status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.Camera>();

        if (status != PermissionStatus.Granted)
        {
            Page? page = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page is not null)
                await page.DisplayAlert("Нет доступа к камере",
                    "Разрешите доступ: Настройки → Приложения → IPasswrd → Разрешения → Камера.", "Ок");
            return null;
        }

        var intent = new Intent(Platform.AppContext, typeof(QrScanActivity));
        (global::Android.App.Result code, Intent? data) = await ActivityResults.StartAsync(intent);
        if (code != global::Android.App.Result.Ok) return null;

        string? text = data?.GetStringExtra(QrScanActivity.ExtraResult);
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
