using Android.App;
using Android.Content;
using Android.Content.PM;
using Microsoft.Maui.Authentication;

namespace IPasswrd.Mobile.Platforms.Android;

/// <summary>
/// Возврат из окна согласия Google. Схема — «перевёрнутый» client id, ровно тот же
/// redirect_uri, что использует iOS-версия (GoogleDrive.RedirectUri строит его из ClientId),
/// поэтому вход на Android работает с тем же OAuth-клиентом и без правок в Google Cloud.
/// </summary>
[Activity(
    NoHistory = true,
    LaunchMode = LaunchMode.SingleTop,
    Exported = true)]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = "com.googleusercontent.apps.520945928883-chesjggkssgjurt88p5pv0pbvqta0rf1")]
public class WebAuthCallbackActivity : WebAuthenticatorCallbackActivity
{
}
