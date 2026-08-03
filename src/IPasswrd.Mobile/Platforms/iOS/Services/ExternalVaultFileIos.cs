using Foundation;
using IPasswrd.Mobile.Services;
using UIKit;
using UniformTypeIdentifiers;

namespace IPasswrd.Mobile.Platforms.iOS.Services;

/// <summary>
/// Файл сейфа в iCloud Drive (тот же vault.ipvault из iCloudDrive\IPasswrd на Windows).
/// Пользователь один раз выбирает файл в «Файлах»; дальше доступ живёт через
/// security-scoped bookmark, чтение и запись — через NSFileCoordinator
/// (это же заставляет iCloud докачивать свежую версию).
/// </summary>
public sealed class ExternalVaultFileIos : IExternalVaultFile
{
    private const string BookmarkPref = "ext.bookmark";
    private const string NamePref = "ext.name";

    private NSObject? _aliveDelegate;   // держим делегат пикера, пока он на экране

    public bool IsConnected => Preferences.Get(BookmarkPref, "").Length > 0;

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
        Preferences.Remove(BookmarkPref);
        Preferences.Remove(NamePref);
    }

    // ================= выбор файла =================

    public async Task<byte[]?> PickAndConnectAsync()
    {
        var tcs = new TaskCompletionSource<NSUrl?>(TaskCreationOptions.RunContinuationsAsynchronously);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            var host = Platform.GetCurrentUIViewController();
            if (host is null) { tcs.TrySetResult(null); return; }

            var picker = new UIDocumentPickerViewController(new[] { UTTypes.Data, UTTypes.Item }, asCopy: false);
            var del = new PickDelegate(tcs, () => _aliveDelegate = null);
            _aliveDelegate = del;
            picker.Delegate = del;
            picker.AllowsMultipleSelection = false;
            host.PresentViewController(picker, true, null);
        });

        NSUrl? url = await tcs.Task;
        if (url is null) return null;

        return await Task.Run(() =>
        {
            bool access = url.StartAccessingSecurityScopedResource();
            try
            {
                NSData? bookmark = url.CreateBookmarkData(0, null, null, out NSError? bmErr);
                if (bookmark is null || bmErr is not null) return (byte[]?)null;

                Preferences.Set(BookmarkPref, Convert.ToBase64String(bookmark.ToArray()));
                Preferences.Set(NamePref, url.LastPathComponent ?? "vault.ipvault");

                byte[]? bytes = CoordinatedRead(url);
                return bytes ?? Array.Empty<byte>();
            }
            finally
            {
                if (access) url.StopAccessingSecurityScopedResource();
            }
        });
    }

    // ================= чтение / запись =================

    public Task<byte[]?> ReadAsync() => Task.Run(() =>
    {
        NSUrl? url = ResolveBookmark();
        if (url is null) return (byte[]?)null;
        bool access = url.StartAccessingSecurityScopedResource();
        try { return CoordinatedRead(url); }
        finally { if (access) url.StopAccessingSecurityScopedResource(); }
    });

    public Task<bool> WriteAsync(byte[] data) => Task.Run(() =>
    {
        NSUrl? url = ResolveBookmark();
        if (url is null) return false;
        bool access = url.StartAccessingSecurityScopedResource();
        try
        {
            using var coordinator = new NSFileCoordinator();
            bool ok = false;
            coordinator.CoordinateWrite(url, NSFileCoordinatorWritingOptions.ForReplacing, out NSError? err, newUrl =>
            {
                using NSData nsData = NSData.FromArray(data);
                ok = nsData.Save(newUrl, NSDataWritingOptions.Atomic, out NSError? saveErr) && saveErr is null;
            });
            return ok && err is null;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (access) url.StopAccessingSecurityScopedResource();
        }
    });

    private NSUrl? ResolveBookmark()
    {
        string b64 = Preferences.Get(BookmarkPref, "");
        if (b64.Length == 0) return null;
        try
        {
            using NSData data = NSData.FromArray(Convert.FromBase64String(b64));
            NSUrl? url = NSUrl.FromBookmarkData(data, 0, null, out bool isStale, out NSError? err);
            if (url is null || err is not null) return null;

            if (isStale)
            {
                bool access = url.StartAccessingSecurityScopedResource();
                try
                {
                    NSData? fresh = url.CreateBookmarkData(0, null, null, out NSError? bmErr);
                    if (fresh is not null && bmErr is null)
                        Preferences.Set(BookmarkPref, Convert.ToBase64String(fresh.ToArray()));
                }
                finally { if (access) url.StopAccessingSecurityScopedResource(); }
            }
            return url;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[]? CoordinatedRead(NSUrl url)
    {
        try
        {
            // подтолкнуть iCloud к докачке свежей версии
            NSFileManager.DefaultManager.StartDownloadingUbiquitous(url, out NSError? _);
        }
        catch (Exception) { }

        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                byte[]? bytes = null;
                using var coordinator = new NSFileCoordinator();
                coordinator.CoordinateRead(url, NSFileCoordinatorReadingOptions.WithoutChanges, out NSError? err, newUrl =>
                {
                    using NSData? data = NSData.FromUrl(newUrl);
                    bytes = data?.ToArray();
                });
                if (err is null && bytes is not null) return bytes;
            }
            catch (Exception) { }
            Thread.Sleep(500);
        }
        return null;
    }

    // ================= экспорт копии =================

    public async Task<bool> ExportCopyAsync(byte[] data, string suggestedName)
    {
        string tmp = Path.Combine(FileSystem.CacheDirectory, suggestedName);
        try { File.WriteAllBytes(tmp, data); }
        catch (Exception) { return false; }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            var host = Platform.GetCurrentUIViewController();
            if (host is null) { tcs.TrySetResult(false); return; }

            var picker = new UIDocumentPickerViewController(new[] { NSUrl.FromFilename(tmp) }, asCopy: true);
            var del = new ExportDelegate(tcs, () => _aliveDelegate = null);
            _aliveDelegate = del;
            picker.Delegate = del;
            host.PresentViewController(picker, true, null);
        });

        return await tcs.Task;
    }

    // ================= делегаты пикеров =================

    private sealed class PickDelegate : UIDocumentPickerDelegate
    {
        private readonly TaskCompletionSource<NSUrl?> _tcs;
        private readonly Action _release;
        public PickDelegate(TaskCompletionSource<NSUrl?> tcs, Action release) { _tcs = tcs; _release = release; }

        public override void DidPickDocument(UIDocumentPickerViewController controller, NSUrl url)
        {
            _tcs.TrySetResult(url);
            _release();
        }

        public override void DidPickDocument(UIDocumentPickerViewController controller, NSUrl[] urls)
        {
            _tcs.TrySetResult(urls.Length > 0 ? urls[0] : null);
            _release();
        }

        public override void WasCancelled(UIDocumentPickerViewController controller)
        {
            _tcs.TrySetResult(null);
            _release();
        }
    }

    private sealed class ExportDelegate : UIDocumentPickerDelegate
    {
        private readonly TaskCompletionSource<bool> _tcs;
        private readonly Action _release;
        public ExportDelegate(TaskCompletionSource<bool> tcs, Action release) { _tcs = tcs; _release = release; }

        public override void DidPickDocument(UIDocumentPickerViewController controller, NSUrl url)
        {
            _tcs.TrySetResult(true);
            _release();
        }

        public override void DidPickDocument(UIDocumentPickerViewController controller, NSUrl[] urls)
        {
            _tcs.TrySetResult(true);
            _release();
        }

        public override void WasCancelled(UIDocumentPickerViewController controller)
        {
            _tcs.TrySetResult(false);
            _release();
        }
    }
}
