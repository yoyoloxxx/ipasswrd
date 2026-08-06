using Android.Content;
using Android.Provider;
using IPasswrd.Mobile.Services;
using AndroidApp = Android.App.Application;
using AndroidUri = Android.Net.Uri;

namespace IPasswrd.Mobile.Platforms.Android.Services;

/// <summary>
/// Внешний файл сейфа через SAF (Storage Access Framework) — полный аналог
/// ExternalVaultFileIos: пользователь один раз выбирает vault.ipvault в системном
/// проводнике (Google Drive, Dropbox, локальная папка, любой провайдер документов),
/// и приложение получает постоянное право на чтение и запись именно этого файла.
///
/// iOS хранит security-scoped bookmark — здесь эквивалент это persistable URI permission:
/// право переживает перезапуск приложения и перезагрузку телефона.
/// </summary>
public sealed class ExternalVaultFileAndroid : IExternalVaultFile
{
    private const string UriPref = "ext.uri";
    private const string NamePref = "ext.name";

    private static ContentResolver? Resolver => AndroidApp.Context.ContentResolver;

    public bool IsConnected => Preferences.Get(UriPref, "").Length > 0;

    public string? DisplayName
    {
        get
        {
            string n = Preferences.Get(NamePref, "");
            return n.Length > 0 ? n : null;
        }
    }

    public void Disconnect()
    {
        try
        {
            AndroidUri? uri = SavedUri();
            if (uri is not null)
                Resolver?.ReleasePersistableUriPermission(uri,
                    ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        }
        catch (Exception) { }

        Preferences.Remove(UriPref);
        Preferences.Remove(NamePref);
    }

    // ================= выбор файла =================

    public async Task<byte[]?> PickAndConnectAsync()
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        // MIME для .ipvault система не знает — берём любой тип
        intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraMimeTypes, new[] { "*/*" });
        intent.AddFlags(ActivityFlags.GrantReadUriPermission
            | ActivityFlags.GrantWriteUriPermission
            | ActivityFlags.GrantPersistableUriPermission);

        (global::Android.App.Result code, Intent? data) = await ActivityResults.StartAsync(intent);
        if (code != global::Android.App.Result.Ok) return null;

        AndroidUri? uri = data?.Data;
        if (uri is null) return null;

        try
        {
            Resolver?.TakePersistableUriPermission(uri,
                ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        }
        catch (Exception)
        {
            // некоторые провайдеры не отдают постоянное право — тогда связь проживёт до перезапуска
        }

        Preferences.Set(UriPref, uri.ToString() ?? "");
        Preferences.Set(NamePref, QueryDisplayName(uri) ?? "vault.ipvault");

        byte[]? bytes = await ReadAsync();
        return bytes ?? Array.Empty<byte>();
    }

    // ================= чтение / запись =================

    public Task<byte[]?> ReadAsync() => Task.Run(() =>
    {
        AndroidUri? uri = SavedUri();
        if (uri is null) return (byte[]?)null;

        // Провайдер облака (Drive/Dropbox) может отдавать файл не с первой попытки,
        // пока докачивает свежую версию — как StartDownloadingUbiquitous на iOS.
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                using Stream? s = Resolver?.OpenInputStream(uri);
                if (s is not null)
                {
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
            catch (Exception) { }
            Thread.Sleep(500);
        }
        return null;
    });

    public Task<bool> WriteAsync(byte[] data) => Task.Run(() =>
    {
        AndroidUri? uri = SavedUri();
        if (uri is null) return false;
        try
        {
            // "wt" — truncate: без него остаётся хвост старого, более длинного файла
            using Stream? s = Resolver?.OpenOutputStream(uri, "wt");
            if (s is null) return false;
            s.Write(data, 0, data.Length);
            s.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    });

    // ================= экспорт копии =================

    public async Task<bool> ExportCopyAsync(byte[] data, string suggestedName)
    {
        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("application/octet-stream");
        intent.PutExtra(Intent.ExtraTitle, suggestedName);

        (global::Android.App.Result code, Intent? result) = await ActivityResults.StartAsync(intent);
        if (code != global::Android.App.Result.Ok) return false;

        AndroidUri? uri = result?.Data;
        if (uri is null) return false;

        try
        {
            using Stream? s = Resolver?.OpenOutputStream(uri, "wt");
            if (s is null) return false;
            s.Write(data, 0, data.Length);
            s.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ================= вспомогательное =================

    private static AndroidUri? SavedUri()
    {
        string s = Preferences.Get(UriPref, "");
        if (s.Length == 0) return null;
        try { return AndroidUri.Parse(s); }
        catch (Exception) { return null; }
    }

    private static string? QueryDisplayName(AndroidUri uri)
    {
        try
        {
            using global::Android.Database.ICursor? c =
                Resolver?.Query(uri, new[] { IOpenableColumns.DisplayName }, null, null, null);
            if (c is not null && c.MoveToFirst())
            {
                int idx = c.GetColumnIndex(IOpenableColumns.DisplayName);
                if (idx >= 0) return c.GetString(idx);
            }
        }
        catch (Exception) { }
        return uri.LastPathSegment;
    }
}
