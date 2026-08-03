using System.Security.Cryptography;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using IPasswrd.Core;

namespace IPasswrd.App;

// Google Drive sync wiring on top of GoogleDriveSync (the OAuth + Drive REST client).
// The canonical vault stays local; here we push it up after each Save and pull + merge on a timer.
public partial class MainWindow
{
    private GoogleDriveSync? _gdrive;
    private bool _gPushing, _gPushPending, _gPulling, _gSuppressPush;
    private DateTimeOffset _gLastPull = DateTimeOffset.MinValue;

    private GoogleDriveSync EnsureGdrive() => _gdrive ??= new GoogleDriveSync(LocalDataDir());

    // ---- push (after Save) ----

    /// <summary>Kick an async push of the current vault to Drive. Coalesces bursts; best-effort (offline = retry next Save/tick).</summary>
    private void GooglePushKick()
    {
        if (_gSuppressPush) return;
        if (_syncProvider != "google" || _vault is null) return;
        var g = _gdrive;
        if (g is null || !g.IsSignedIn) return;
        if (_gPushing) { _gPushPending = true; return; }
        _gPushing = true;
        _ = GooglePushLoop();
    }

    private async Task GooglePushLoop()
    {
        try
        {
            do
            {
                _gPushPending = false;
                if (_vault is null) break;
                byte[] data = _vault.Serialize();      // on UI thread
                await _gdrive!.PushAsync(data);
            }
            while (_gPushPending);
        }
        catch { /* offline / transient — the next Save or tick retries */ }
        finally { _gPushing = false; }
    }

    // ---- pull (on the detail timer) ----

    private void GooglePullMaybe()
    {
        if (_syncProvider != "google" || _vault is null || _gPulling) return;
        var g = _gdrive;
        if (g is null || !g.IsSignedIn) return;
        if ((DateTimeOffset.UtcNow - _gLastPull).TotalSeconds < 15) return;
        _gLastPull = DateTimeOffset.UtcNow;
        _ = GooglePullTick();
    }

    private async Task GooglePullTick()
    {
        _gPulling = true;
        try
        {
            string? mt = await _gdrive!.RemoteModifiedTimeAsync();
            if (mt is null) { await _gdrive.PushAsync(_vault!.Serialize()); return; }   // remote gone → re-materialize
            if (mt == _gdrive.LastModifiedTime) return;                                  // unchanged since we last saw it

            byte[]? remote = await _gdrive.PullAsync();
            if (remote is null || _vault is null) return;
            string h = Convert.ToHexString(SHA256.HashData(remote));
            if (h == _vaultHash) return;                                                 // identical content

            int changed = _vault.MergeFrom(remote);                                      // may throw on a foreign vault
            _gSuppressPush = true;
            try { Save(); } finally { _gSuppressPush = false; }
            await _gdrive.PushAsync(_vault.Serialize());                                  // push the union back up

            if (changed > 0 && VaultScreen.IsVisible)
            {
                string? keep = _currentId;
                LoadEntries(selectFirst: false);
                RenderSidebar();
                var row = keep is null ? null
                    : (EntryList.ItemsSource as IEnumerable<EntryRow>)?.FirstOrDefault(r => r.Id == keep);
                if (row is not null) EntryList.SelectedItem = row;
            }
        }
        catch (VaultIntegrityException) { /* a different vault sits on Drive — leave local alone */ }
        catch { /* offline / transient */ }
        finally { _gPulling = false; }
    }

    /// <summary>After an unlock, pull + merge + push once so the session starts from the freshest copy.</summary>
    private async void GoogleResumeAfterUnlock()
    {
        if (_syncProvider != "google" || _vault is null) return;
        var g = EnsureGdrive();
        if (!g.IsSignedIn) return;
        _gLastPull = DateTimeOffset.UtcNow;
        try
        {
            byte[]? remote = await g.PullAsync();
            if (remote is not null && _vault is not null)
            {
                string h = Convert.ToHexString(SHA256.HashData(remote));
                if (h != _vaultHash)
                {
                    _vault.MergeFrom(remote);
                    _gSuppressPush = true;
                    try { Save(); } finally { _gSuppressPush = false; }
                    if (VaultScreen.IsVisible) { LoadEntries(selectFirst: false); RenderSidebar(); }
                }
            }
            await g.PushAsync(_vault!.Serialize());
        }
        catch (VaultIntegrityException) { /* foreign remote */ }
        catch { /* offline */ }
    }

    // ---- enable / disable ----

    /// <summary>Sign in, reconcile with any copy already on Drive, and turn Google sync on.
    /// Returns an error message (localized) or null on success.</summary>
    private async Task<string?> EnableGoogleAsync()
    {
        if (_vault is null) return Tr("Сначала откройте сейф.");
        var g = EnsureGdrive();
        if (!g.IsConfigured) return Tr("Сначала вставьте Client ID и Client secret от Google.");
        try
        {
            await g.SignInAsync();
        }
        catch (Exception ex)
        {
            return Tr("Не удалось войти в Google: ") + GTranslateError(ex.Message);
        }
        try
        {
            byte[]? remote = await g.PullAsync();
            if (remote is not null)
            {
                try { _vault.MergeFrom(remote); }
                catch (VaultIntegrityException)
                {
                    g.SignOut();
                    return Tr("В Google уже лежит другой сейф. Сначала решите, какой оставить.");
                }
                Save();
            }
            await g.PushAsync(_vault.Serialize());       // materialize the merged (or first) copy on Drive
            _syncProvider = "google";
            SaveSettings();
            LoadEntries(selectFirst: false);
            RenderSidebar();
            return null;
        }
        catch (Exception ex)
        {
            return Tr("Ошибка Google Drive: ") + GTranslateError(ex.Message);
        }
    }

    private void DisableGoogleSync()
    {
        try { _gdrive?.SignOut(); } catch { /* ignore */ }
        _syncProvider = "";
        SaveSettings();
    }

    private static string GTranslateError(string code) => code switch
    {
        "no_code" or "consent_access_denied" => "вход отменён",
        "no_refresh_token" => "Google не выдал refresh-токен (повторите вход)",
        "token_exchange_failed" or "refresh_failed" => "проблема с токеном — проверьте Client ID/secret",
        "google_client_not_configured" => "не заданы Client ID/secret",
        _ => code,
    };

    // ---- connect UI (inline in the Sync row) ----

    /// <summary>The expandable Google block: a Client ID / secret form (first run) then a Connect button.</summary>
    private Control BuildGoogleConnectPanel()
    {
        var g = EnsureGdrive();
        var panel = new StackPanel { Spacing = 9, Margin = new Thickness(17, 4, 17, 14) };

        var status = new TextBlock { IsVisible = false, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        void SetStatus(string m, bool error) { status.Text = m; status.Foreground = error ? Bad : Ok; status.IsVisible = true; }

        var (cfgId, cfgSecret) = GoogleDriveSync.LoadConfig(LocalDataDir());
        var idBox = new TextBox { Watermark = "Client ID (…apps.googleusercontent.com)", Text = cfgId, FontSize = 12.5 };
        var secretBox = new TextBox { Watermark = "Client secret", Text = cfgSecret, FontSize = 12.5 };

        panel.Children.Add(new TextBlock
        {
            Text = Tr("Вставьте Client ID и Client secret из вашего проекта Google Cloud (тип «Desktop app»). Пароль от Google приложение не увидит — вход идёт в браузере."),
            Foreground = Text2, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(Labeled("Client ID", idBox));
        panel.Children.Add(Labeled("Client secret", secretBox));

        var guide = new Button { Content = Tr("Как получить ключи?"), Padding = new Thickness(13, 6) };
        guide.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "https://console.cloud.google.com/apis/credentials") { UseShellExecute = true });
            }
            catch { /* ignore */ }
        };

        var connect = new Button { Content = Tr("Подключить Google"), Padding = new Thickness(16, 8) };
        connect.Classes.Add("primary");
        connect.Click += async (_, _) =>
        {
            _lastActivity = DateTimeOffset.UtcNow;
            string id = (idBox.Text ?? "").Trim(), secret = (secretBox.Text ?? "").Trim();
            if (id.Length == 0 || secret.Length == 0) { SetStatus(Tr("Заполните Client ID и Client secret."), true); return; }
            g.SaveConfig(id, secret);
            SetStatus(Tr("Открываю браузер для входа в Google…"), false);
            connect.IsEnabled = false;
            string? err = await EnableGoogleAsync();
            connect.IsEnabled = true;
            if (err is null) { UpdateSyncChip(); ShowTool("settings"); }
            else SetStatus(err, true);
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(connect);
        row.Children.Add(guide);
        panel.Children.Add(row);
        panel.Children.Add(status);
        return panel;
    }
}
