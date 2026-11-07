using System.Security.Cryptography;
using System.Text.Json;

namespace IPasswrd.Mobile.Services;

/// <summary>
/// Обновление Android-сборки «по воздуху». Приложения нет в Google Play, APK раздаётся с
/// GitHub — значит, обновляться оно должно само, иначе человек навсегда останется на той
/// сборке, которую однажды поставил.
///
/// CI кладёт рядом с APK файл android-latest.json с номером сборки и sha256. Здесь мы его
/// читаем, сравниваем с текущим versionCode и, если новее, скачиваем APK, проверяем хеш
/// (и подпись пакета) и отдаём системному установщику. Ставится поверх: подпись та же
/// (постоянный ключ в секретах CI), данные целы.
///
/// На iOS обновления приходят через AltStore (источник altstore.json) — там этого нет.
/// </summary>
public static class AppUpdate
{
    public const string ManifestUrl =
        "https://github.com/yoyoloxxx/ipasswrd/releases/download/mobile-latest/android-latest.json";

    private const string LastCheckPref = "update.lastcheck";
    private const string SkipPref = "update.skip";           // сборка, от которой человек отказался
    private const string ApkName = "IPasswrd-update.apk";
    private const string AllowedRepoPath = "/yoyoloxxx/ipasswrd/";

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
    };

    public sealed record UpdateInfo(string VersionName, long VersionCode, string Url, long Size, string Notes, string Sha256);

    /// <summary>Обновление «по воздуху» только на Android (на iOS этим занимается AltStore).</summary>
    public static bool Supported =>
#if ANDROID
        true;
#else
        false;
#endif

    public static string CurrentVersion => AppInfo.Current.VersionString;

    public static long CurrentCode =>
        long.TryParse(AppInfo.Current.BuildString, out long c) ? c : 0;

    /// <summary>Спросить GitHub о свежей сборке. null — обновления нет (или сеть недоступна).</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        if (!Supported) return null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("IPasswrd-mobile");
            string json = await http.GetStringAsync(ManifestUrl, ct);

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement r = doc.RootElement;
            string name = r.TryGetProperty("versionName", out var vn) ? (vn.GetString() ?? "") : "";
            long code = r.TryGetProperty("versionCode", out var vc) && vc.TryGetInt64(out long c) ? c : 0;
            string url = r.TryGetProperty("url", out var u) ? (u.GetString() ?? "") : "";
            long size = r.TryGetProperty("size", out var s) && s.TryGetInt64(out long sz) ? sz : 0;
            string notes = r.TryGetProperty("notes", out var n) ? (n.GetString() ?? "") : "";
            string sha256 = r.TryGetProperty("sha256", out var h) ? (h.GetString() ?? "") : "";

            Preferences.Default.Set(LastCheckPref, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (code <= CurrentCode || url.Length == 0) return null;
            // Audit M2: refuse unexpected hosts and require a content hash before offering install.
            if (!IsAllowedDownloadUrl(url) || sha256.Length != 64) return null;
            return new UpdateInfo(name, code, url, size, notes, sha256.ToLowerInvariant());
        }
        catch (Exception)
        {
            return null;   // офлайн или релиз недоступен — молча, это фоновая проверка
        }
    }

    /// <summary>Фоновая проверка: не чаще раза в сутки и без повтора отклонённой сборки.</summary>
    public static async Task<UpdateInfo?> CheckQuietlyAsync()
    {
        if (!Supported) return null;
        long last = Preferences.Default.Get(LastCheckPref, 0L);
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - last < 24 * 3600) return null;

        UpdateInfo? info = await CheckAsync();
        if (info is null) return null;
        if (Preferences.Default.Get(SkipPref, 0L) == info.VersionCode) return null;
        return info;
    }

    /// <summary>«Не сейчас» — больше не напоминать про эту сборку (кнопка в настройках остаётся).</summary>
    public static void Skip(UpdateInfo info) => Preferences.Default.Set(SkipPref, info.VersionCode);

    /// <summary>Скачать APK во временную папку. Возвращает путь к файлу или null.</summary>
    public static async Task<string?> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null,
                                                    CancellationToken ct = default)
    {
        if (!IsAllowedDownloadUrl(info.Url) || info.Sha256.Length != 64) return null;

        string path = Path.Combine(FileSystem.CacheDirectory, ApkName);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("IPasswrd-mobile");

            using HttpResponseMessage resp =
                await http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            // Final URI after redirects must still be an allowed host.
            if (resp.RequestMessage?.RequestUri is Uri final && !IsAllowedDownloadUrl(final.ToString()))
            {
                TryDelete(path);
                return null;
            }

            long total = resp.Content.Headers.ContentLength ?? info.Size;
            using Stream src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

            var buf = new byte[128 * 1024];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
            await dst.FlushAsync(ct);

            // Оборванная закачка = битый APK: установщик покажет невнятную ошибку, лучше сразу честно.
            if (total > 0 && done < total) { TryDelete(path); return null; }

            // Audit M2: verify SHA-256 before handing the file to the installer.
            string actual = await Sha256FileAsync(path, ct);
            if (!string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(path);
                return null;
            }

            // Pin signing cert to the currently installed app (same release key).
            if (!SigningCertMatchesInstalled(path))
            {
                TryDelete(path);
                return null;
            }

            return path;
        }
        catch (Exception)
        {
            TryDelete(path);
            return null;
        }
    }

    /// <summary>Отдать APK системному установщику. false — система попросила разрешение (экран уже открыт).</summary>
    public static bool Install(string apkPath)
    {
#if ANDROID
        try
        {
            var ctx = global::Android.App.Application.Context;

            // Android 8+: установка из «неизвестного источника» разрешается отдельно и один раз.
            if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O
                && ctx.PackageManager?.CanRequestPackageInstalls() != true)
            {
                var ask = new global::Android.Content.Intent(
                    global::Android.Provider.Settings.ActionManageUnknownAppSources,
                    global::Android.Net.Uri.Parse("package:" + ctx.PackageName));
                ask.AddFlags(global::Android.Content.ActivityFlags.NewTask);
                ctx.StartActivity(ask);
                return false;
            }

            var file = new Java.IO.File(apkPath);
            global::Android.Net.Uri uri = global::AndroidX.Core.Content.FileProvider.GetUriForFile(
                ctx, ctx.PackageName + ".updateprovider", file);

            var i = new global::Android.Content.Intent(global::Android.Content.Intent.ActionView);
            i.SetDataAndType(uri, "application/vnd.android.package-archive");
            i.AddFlags(global::Android.Content.ActivityFlags.NewTask
                     | global::Android.Content.ActivityFlags.GrantReadUriPermission);
            ctx.StartActivity(i);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
#else
        return false;
#endif
    }

    /// <summary>HTTPS + allow-listed GitHub hosts; github.com paths must be this repo.</summary>
    internal static bool IsAllowedDownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? u)) return false;
        if (!string.Equals(u.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!AllowedHosts.Contains(u.Host)) return false;
        if (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && u.AbsolutePath.IndexOf(AllowedRepoPath, StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        return true;
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>True when the APK's signing cert matches the installed package (or when we cannot check).</summary>
    private static bool SigningCertMatchesInstalled(string apkPath)
    {
#if ANDROID
        try
        {
            var ctx = global::Android.App.Application.Context;
            var pm = ctx.PackageManager;
            if (pm is null) return false;

            IList<string> installed = SigningCertDigests(pm, ctx.PackageName!, archivePath: null);
            if (installed.Count == 0) return false;

            IList<string> archive = SigningCertDigests(pm, packageName: null, archivePath: apkPath);
            if (archive.Count == 0) return false;

            foreach (string a in archive)
                if (installed.Contains(a)) return true;
            return false;
        }
        catch
        {
            return false;
        }
#else
        return true;
#endif
    }

#if ANDROID
    private static IList<string> SigningCertDigests(global::Android.Content.PM.PackageManager pm,
                                                    string? packageName, string? archivePath)
    {
        var digests = new List<string>();
        try
        {
            global::Android.Content.PM.PackageInfo? info;
            if (archivePath is not null)
            {
                info = OperatingSystem.IsAndroidVersionAtLeast(28)
                    ? pm.GetPackageArchiveInfo(archivePath, global::Android.Content.PM.PackageInfoFlags.SigningCertificates)
                    : pm.GetPackageArchiveInfo(archivePath, global::Android.Content.PM.PackageInfoFlags.Signatures);
                // Without SourceDir, some API levels return empty signing info for archives.
                if (info?.ApplicationInfo is not null)
                {
                    info.ApplicationInfo.SourceDir = archivePath;
                    info.ApplicationInfo.PublicSourceDir = archivePath;
                }
            }
            else
            {
                info = OperatingSystem.IsAndroidVersionAtLeast(28)
                    ? pm.GetPackageInfo(packageName!, global::Android.Content.PM.PackageInfoFlags.SigningCertificates)
                    : pm.GetPackageInfo(packageName!, global::Android.Content.PM.PackageInfoFlags.Signatures);
            }
            if (info is null) return digests;

            if (OperatingSystem.IsAndroidVersionAtLeast(28) && info.SigningInfo is { } si)
            {
                var certs = si.HasMultipleSigners
                    ? si.GetApkContentsSigners()
                    : si.GetSigningCertificateHistory();
                if (certs is not null)
                {
                    foreach (var c in certs)
                    {
                        byte[]? raw = c?.ToByteArray();
                        if (raw is { Length: > 0 })
                            digests.Add(Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant());
                    }
                }
            }
            else if (info.Signatures is not null)
            {
                foreach (var s in info.Signatures)
                {
                    byte[]? raw = s?.ToByteArray();
                    if (raw is { Length: > 0 })
                        digests.Add(Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant());
                }
            }
        }
        catch { }
        return digests;
    }
#endif

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }
}
