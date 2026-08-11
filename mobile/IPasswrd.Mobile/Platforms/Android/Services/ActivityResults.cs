using System.Collections.Concurrent;
using Android.App;
using Android.Content;

namespace IPasswrd.Mobile.Platforms.Android.Services;

/// <summary>
/// Свой маленький реестр startActivityForResult: MAUI-овский держит собственные коды,
/// а нам нужны SAF-пикер и сканер QR с ожиданием результата в await.
/// Результат доставляет MainActivity.OnActivityResult.
/// </summary>
internal static class ActivityResults
{
    private const int FirstCode = 0x5A00;   // подальше от кодов MAUI (они начинаются с малых чисел)
    private static int _next = FirstCode;

    private static readonly ConcurrentDictionary<int, TaskCompletionSource<(Result Code, Intent? Data)>> Pending = new();

    /// <summary>Запустить activity и дождаться её результата.</summary>
    public static Task<(Result Code, Intent? Data)> StartAsync(Intent intent)
    {
        var tcs = new TaskCompletionSource<(Result, Intent?)>(TaskCreationOptions.RunContinuationsAsynchronously);

        Activity? host = Platform.CurrentActivity;
        if (host is null)
        {
            tcs.TrySetResult((Result.Canceled, null));
            return tcs.Task;
        }

        int code = NextCode();
        Pending[code] = tcs;
        try
        {
            host.StartActivityForResult(intent, code);
        }
        catch (Exception)
        {
            Pending.TryRemove(code, out _);
            tcs.TrySetResult((Result.Canceled, null));
        }
        return tcs.Task;
    }

    /// <summary>Вызывается из MainActivity.OnActivityResult. true — код был наш.</summary>
    public static bool Deliver(int requestCode, Result resultCode, Intent? data)
    {
        if (!Pending.TryRemove(requestCode, out var tcs)) return false;
        tcs.TrySetResult((resultCode, data));
        return true;
    }

    private static int NextCode()
    {
        int c = Interlocked.Increment(ref _next);
        if (c > 0xFFFF)                      // requestCode должен помещаться в 16 бит
        {
            Interlocked.Exchange(ref _next, FirstCode);
            c = Interlocked.Increment(ref _next);
        }
        return c;
    }
}
