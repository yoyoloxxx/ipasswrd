using System.Text.Json;
using IPasswrd.Core;

namespace IPasswrd.Mobile.Services;

/// <summary>
/// Сессия сейфа на телефоне. Повторяет поведение Windows-приложения:
/// тот же формат файла (vault.ipvault), тот же локаут (Core.Lockout),
/// быстрая разблокировка сессионным ключом (здесь — Keychain + Face ID
/// вместо DPAPI) и синхронизация через файл в iCloud Drive с
/// поэлементным last-write-wins (Vault.MergeFrom).
/// </summary>
public sealed class AppState
{
    public const string VaultFileName = "vault.ipvault";
    private const string QuickUnlockKey = "quickunlock";
    /// <summary>Мастер-пароль переспрашивается не реже раза в 30 дней (телефонная норма; на ПК — интервал автоблокировки).</summary>
    private const int QuickUnlockDays = 30;

    private Vault? _vault;
    private DateTimeOffset _backgroundedAt = Autolock.NotBackgrounded;

    public event Action? LockedChanged;      // разблокировали или заблокировали
    public event Action? VaultChanged;       // содержимое изменилось (после Save/Sync)
    public event Action<string>? SyncProblem;

    public Vault? Vault => _vault;
    public bool IsUnlocked => _vault is not null;

    public string LocalVaultPath => Path.Combine(FileSystem.AppDataDirectory, VaultFileName);
    public bool HasLocalVault => File.Exists(LocalVaultPath);

    // ================= настройки =================

    public int AutolockMinutes
    {
        get => Preferences.Get("autolockMinutes", 1);
        set => Preferences.Set("autolockMinutes", value);
    }

    public bool BiometricUnlockEnabled
    {
        get => Preferences.Get("biometricUnlock", true);
        set
        {
            Preferences.Set("biometricUnlock", value);
            if (!value) WipeQuickUnlock();
            else if (_vault is not null) SaveQuickUnlock();
        }
    }

    /// <summary>Секунд до авто-очистки буфера (0 = выкл; по умолчанию 30, как на ПК).</summary>
    public int ClipboardClearSeconds
    {
        get => Preferences.Get("clipboardClearSeconds", 30);
        set { Preferences.Set("clipboardClearSeconds", value); SecureClipboard.ClearSeconds = value; }
    }

    public string? LastSyncStatus { get; private set; }

    // ================= локаут (как на Windows: счётчик на устройстве) =================

    // iOS: App Group UserDefaults — тот же счётчик, что у AutoFill (audit H2).
    // Android: те же ключи через Preferences (WipeDek/App Group — no-op).
    private int Fails
    {
        get => AutoFillShare.GetLockoutFails();
        set => AutoFillShare.SetLockoutFails(value);
    }

    private DateTimeOffset LockedUntil
    {
        get => DateTimeOffset.FromUnixTimeSeconds(AutoFillShare.GetLockoutUntilUnix());
        set => AutoFillShare.SetLockoutUntilUnix(value.ToUnixTimeSeconds());
    }

    public TimeSpan LockoutRemaining
    {
        get
        {
            TimeSpan left = LockedUntil - DateTimeOffset.UtcNow;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    public int FreeAttemptsLeft => Lockout.AttemptsLeft(Fails);

    // ================= anti-downgrade sentinel (parity with the desktop app-private flag) =================
    // The vault's document MAC (Core) is verified whenever present, which already blocks a forged
    // updatedAt, a fabricated tombstone, or a swapped key envelope. To also stop an attacker STRIPPING
    // the MAC so a forgery "looks legacy", once this install has produced a MAC-authenticated vault of a
    // given lineage we refuse an un-sealed copy of that lineage. The marker lives in app-private
    // Preferences, which the sync cloud (iCloud/Drive) never sees.
    private const string AuthVaultIdPref = "vault.authenticatedId";

    private static void MarkVaultAuthenticated(Vault? v)
    {
        try
        {
            if (v is null) return;
            Preferences.Set(AuthVaultIdPref, v.VaultId);
            // App Group copy for iOS AutoFill (audit M12) — extension must refuse MAC-stripped peers too.
            AutoFillShare.SetAuthenticatedVaultId(v.VaultId);
        }
        catch { /* best-effort */ }
    }

    /// <summary>True if the blob belongs to a vault this install knows to be MAC-protected, so a
    /// missing seal on it can only be a downgrade attempt.</summary>
    private static bool RequireAuth(byte[] blob)
    {
        try
        {
            string id = IPasswrd.Core.Vault.VaultIdOf(blob);
            string marked = Preferences.Get(AuthVaultIdPref, "");
            if (marked.Length == 0) marked = AutoFillShare.GetAuthenticatedVaultId();
            return id.Length > 0 && marked == id;
        }
        catch { return false; }
    }

    /// <summary>Merge-side check: the open vault's lineage id is already known.</summary>
    private bool RequireAuthLocal()
    {
        try
        {
            if (_vault is null) return false;
            string marked = Preferences.Get(AuthVaultIdPref, "");
            if (marked.Length == 0) marked = AutoFillShare.GetAuthenticatedVaultId();
            return marked == _vault.VaultId;
        }
        catch { return false; }
    }

    // ================= создание / разблокировка =================

    public async Task CreateAsync(string masterPassword)
    {
        await CreateVaultCoreAsync(masterPassword);
        AfterUnlock();
    }

    /// <summary>Создать сейф и сохранить, но НЕ активировать (корень экрана не переключается).
    /// Нужно, чтобы на экране создания успеть показать код восстановления до перехода в сейф.</summary>
    public async Task CreateVaultCoreAsync(string masterPassword)
    {
        Vault v = await Task.Run(() => IPasswrd.Core.Vault.Create(masterPassword));
        _vault = v;
        await SaveAsync();
    }

    /// <summary>Завершить создание — перейти в открытый сейф (после показа кода восстановления).</summary>
    public void ActivateAfterCreate() => AfterUnlock();

    /// <summary>null — успех; иначе текст ошибки для пользователя.</summary>
    public async Task<string?> UnlockAsync(string masterPassword)
    {
        if (LockoutRemaining > TimeSpan.Zero)
            return $"Слишком много попыток. Подождите {Fmt.Duration(LockoutRemaining)}.";

        VaultUnlockSource.Decision decision;
        try { decision = await PrepareUnlockDecisionAsync(); }
        catch { return "Файл сейфа не найден."; }

        Vault unlocked;
        byte[]? mergePeer = decision.OtherBlobToMergeAfterUnlock;
        try
        {
            unlocked = await Task.Run(() =>
                IPasswrd.Core.Vault.Unlock(decision.UnlockBlob, masterPassword, RequireAuth(decision.UnlockBlob)));
        }
        catch (WrongMasterPasswordException)
        {
            // Stale wrap on the preferred side (e.g. password changed elsewhere, or the
            // newer-envelope remote was preferred but the user still typed the old password):
            // try the peer once before counting a fail — same DEK lineage, different envelope.
            if (mergePeer is { Length: > 0 })
            {
                try
                {
                    unlocked = await Task.Run(() =>
                        IPasswrd.Core.Vault.Unlock(mergePeer, masterPassword, RequireAuth(mergePeer)));
                    // Unlocked the peer: merge the side we tried first.
                    mergePeer = decision.UnlockBlob;
                }
                catch (WrongMasterPasswordException)
                {
                    return FailUnlockPassword(decision.UnlockBlob, mergePeer);
                }
                catch (Exception)
                {
                    return "Файл сейфа повреждён или имеет неизвестный формат.";
                }
            }
            else return FailUnlockPassword(decision.UnlockBlob, null);
        }
        catch (Exception)
        {
            return "Файл сейфа повреждён или имеет неизвестный формат.";
        }

        _vault = unlocked;
        PersistMergedUnlock(mergePeer, decision.RemoteIsForeign);
        Fails = 0;
        LockedUntil = DateTimeOffset.UnixEpoch;
        AfterUnlock();
        return null;
    }

    private string FailUnlockPassword(byte[] primary, byte[]? secondary)
    {
        Fails++;
        // Prefer the hint from the newer envelope when both sides are present.
        string hintBlob = secondary is { Length: > 0 }
            && string.CompareOrdinal(
                IPasswrd.Core.Vault.MasterPasswordChangedAtOf(secondary),
                IPasswrd.Core.Vault.MasterPasswordChangedAtOf(primary)) > 0
            ? secondary : primary;
        string hint = MasterChangeHint(hintBlob);
        TimeSpan pen = Lockout.PenaltyFor(Fails);
        if (pen > TimeSpan.Zero)
        {
            LockedUntil = DateTimeOffset.UtcNow + pen;
            return $"Неверный мастер-пароль. Вход закрыт на {Fmt.Duration(pen)}.{hint}";
        }
        int left = Lockout.AttemptsLeft(Fails);
        return (left > 0
            ? $"Неверный мастер-пароль. Осталось попыток без блокировки: {left}."
            : "Неверный мастер-пароль.") + hint;
    }

    /// <summary>
    /// Подсказка к неверному паролю: когда конверт ключа менялся. После смены пароля на ПК
    /// дата со штампа в файле объясняет «неверно», не выдавая ничего сверх метаданных файла.
    /// </summary>
    private static string MasterChangeHint(byte[] blob)
    {
        try
        {
            string at = IPasswrd.Core.Vault.MasterPasswordChangedAtOf(blob);
            if (at.Length == 0) return "";
            if (!DateTimeOffset.TryParse(at, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset utc)) return "";
            return $" Мастер-пароль менялся {utc.ToLocalTime():dd.MM 'в' HH:mm} — если это было на другом устройстве, вводите новый пароль.";
        }
        catch { return ""; }
    }

    private void AfterUnlock()
    {
        if (_vault?.IsAuthenticated == true) MarkVaultAuthenticated(_vault);   // opened a sealed vault: keep the anti-downgrade marker current
        SecureClipboard.ClearSeconds = ClipboardClearSeconds;   // применить настройку авто-очистки буфера
        if (BiometricUnlockEnabled) SaveQuickUnlock();
        ShareForAutoFill();
        LockedChanged?.Invoke();
        _ = SyncAsync();   // фоновая сверка с файлом в iCloud
    }

    /// <summary>Передать зашифрованную копию сейфа расширению автозаполнения
    /// и обновить подсказки логинов над клавиатурой.</summary>
    private void ShareForAutoFill()
    {
        try
        {
            Vault? v = _vault;
            if (v is null) return;
            AutoFillShare.MirrorVault(v.Serialize());
            AutoFillShare.UpdateIdentities(v);
        }
        catch { /* необязательный путь */ }
    }

    /// <summary>
    /// Read local vault + optional cloud peer for unlock. Never overwrites local with remote:
    /// offline edits must survive until <see cref="Vault.MergeFrom"/> (Windows GoogleSyncUi model).
    /// A remote-only device materialises the cloud file onto disk once (nothing local to lose).
    /// </summary>
    private async Task<VaultUnlockSource.Decision> PrepareUnlockDecisionAsync()
    {
        byte[]? local = HasLocalVault ? File.ReadAllBytes(LocalVaultPath) : null;
        byte[]? remote = await TryPullRemoteAsync();

        if ((local is null || local.Length == 0) && remote is { Length: > 0 })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LocalVaultPath)!);
            File.WriteAllBytes(LocalVaultPath, remote);
            local = remote;
        }

        return VaultUnlockSource.Decide(local, remote);
    }

    /// <summary>Best-effort cloud read. Google Drive wins over iCloud/external when connected.</summary>
    private async Task<byte[]?> TryPullRemoteAsync()
    {
        if (GoogleDrive.IsConnected)
        {
            try
            {
                byte[]? g = await GoogleDrive.PullAsync();
                if (g is { Length: > 0 }) return g;
            }
            catch { /* offline → local only */ }
        }
        if (Svc.External.IsConnected)
        {
            try
            {
                byte[]? ext = await Svc.External.ReadAsync();
                if (ext is { Length: > 0 }) return ext;
            }
            catch { /* offline → local only */ }
        }
        return null;
    }

    /// <summary>
    /// After a successful unlock: merge the peer (if any), force-snapshot the previous local
    /// file, and write the union. Skips merge when the remote is a foreign vaultId.
    /// </summary>
    private void PersistMergedUnlock(byte[]? mergePeer, bool remoteIsForeign)
    {
        if (_vault is null) return;
        bool touched = false;
        if (mergePeer is { Length: > 0 } && !remoteIsForeign)
        {
            try
            {
                string envBefore = _vault.MasterPasswordChangedAt;
                int changed = _vault.MergeFrom(mergePeer, RequireAuthLocal());
                touched = changed > 0
                    || !string.Equals(_vault.MasterPasswordChangedAt, envBefore, StringComparison.Ordinal);
            }
            catch (VaultIntegrityException)
            {
                // Foreign / tampered peer — keep the unlocked side as-is.
            }
        }

        // Always persist when we unlocked a blob that may not yet be what is on disk
        // (newer remote envelope preferred), or when merge changed anything.
        byte[] data = _vault.Serialize();
        bool diskDiffers = !HasLocalVault
            || !data.AsSpan().SequenceEqual(File.ReadAllBytes(LocalVaultPath));
        if (!touched && !diskDiffers) return;

        Directory.CreateDirectory(Path.GetDirectoryName(LocalVaultPath)!);
        // Destructive replace of the on-disk file — force past MinInterval so a save five
        // minutes ago cannot suppress the only rollback copy of unsynced local bytes.
        VaultBackups.Snapshot(LocalVaultPath, force: true);
        File.WriteAllBytes(LocalVaultPath, data);
        if (_vault.IsAuthenticated) MarkVaultAuthenticated(_vault);
    }

    // ================= быстрая разблокировка (Keychain + Face ID) =================

    private sealed class QuickUnlockData
    {
        public string Dek { get; set; } = "";
        public long ExpiresAt { get; set; }
    }

    /// <summary>A hardware biometric CRYPTO gate is used when the device has a strong biometric or a
    /// device credential; otherwise the (audited-adequate) un-gated quick unlock is kept, so no device
    /// loses the feature. Only the vault session key is gated — the sync refresh token stays silent.</summary>
    private static bool UseBioCrypto => Svc.BiometricSecret.IsAvailable;

    /// <summary>Plain marker that a quick-unlock secret was stored (in whichever backing store), so the
    /// unlock screen can offer biometrics without probing the biometric store.</summary>
    private const string QuickUnlockPresentPref = "quickunlock.present";

    /// <summary>Каким путём сохранён быстрый вход: "bio" (крипто-гейт) или "soft" (старый путь).</summary>
    private const string QuickModePref = "quickunlock.mode";

    public bool QuickUnlockAvailable =>
        BiometricUnlockEnabled && HasLocalVault && Preferences.Get(QuickUnlockPresentPref, false)
        && (Preferences.Get(QuickModePref, UseBioCrypto ? "bio" : "soft") == "bio"
                ? Svc.BiometricSecret.IsAvailable : Svc.Biometric.IsAvailable);

    private void SaveQuickUnlock()
    {
        try
        {
            if (_vault is null) return;
            var data = new QuickUnlockData
            {
                Dek = Convert.ToBase64String(_vault.ExportSessionKey()),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(QuickUnlockDays).ToUnixTimeSeconds(),
            };
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(data);
            if (UseBioCrypto)
            {
                // Audit M1: never leave a soft (un-gated) DEK copy while bio Protect is pending —
                // process compromise would read it without a prompt. Fail-closed: if Protect is
                // slow/killed/fails, quick-unlock stays off and the master password remains the door.
                try { Svc.KeyStore.Delete(QuickUnlockKey); } catch { }
                Preferences.Set(QuickModePref, "bio");
                Preferences.Remove(QuickUnlockPresentPref);
                _ = Task.Run(async () =>
                {
                    bool ok = false;
                    try
                    {
                        Task<bool> protect = Svc.BiometricSecret.ProtectAsync(QuickUnlockKey, json);
                        ok = await protect.WaitAsync(TimeSpan.FromSeconds(8));
                    }
                    catch { /* timeout / protect failure → master password */ }
                    if (ok)
                    {
                        Preferences.Set(QuickModePref, "bio");
                        Preferences.Set(QuickUnlockPresentPref, true);
                        try { Svc.KeyStore.Delete(QuickUnlockKey); } catch { }
                    }
                    else
                    {
                        try { Svc.BiometricSecret.Delete(QuickUnlockKey); } catch { }
                        try { Preferences.Remove(QuickUnlockPresentPref); } catch { }
                    }
                });
            }
            else
            {
                Svc.KeyStore.Save(QuickUnlockKey, json);
                Preferences.Set(QuickModePref, "soft");
                Preferences.Set(QuickUnlockPresentPref, true);
            }
        }
        catch { /* необязательный путь: мастер-пароль всегда работает */ }
    }

    private void WipeQuickUnlock()
    {
        try { Svc.KeyStore.Delete(QuickUnlockKey); } catch { }
        try { Svc.BiometricSecret.Delete(QuickUnlockKey); } catch { }
        try { Preferences.Remove(QuickUnlockPresentPref); } catch { }
        try { Preferences.Remove(QuickModePref); } catch { }
    }

    /// <summary>null — успех; "" — тихий отказ (нет ключа/отмена Face ID); иначе текст ошибки.</summary>
    public async Task<string?> TryQuickUnlockAsync()
    {
        if (LockoutRemaining > TimeSpan.Zero)
            return $"Слишком много попыток. Подождите {Fmt.Duration(LockoutRemaining)}.";

        if (!HasLocalVault || !Preferences.Get(QuickUnlockPresentPref, false)) return "";

        string quickMode = Preferences.Get(QuickModePref, UseBioCrypto ? "bio" : "soft");
        // Audit M1: soft must not win while a hardware bio gate is available (leftover mid-upgrade).
        if (UseBioCrypto && quickMode != "bio")
        {
            WipeQuickUnlock();
            return "";
        }
        bool cryptoGated = quickMode == "bio" && UseBioCrypto;
        byte[]? raw;
        if (cryptoGated)
        {
            // The crypto-gated store prompts biometrics as it releases the key.
            raw = await Svc.BiometricSecret.RevealAsync(QuickUnlockKey, "Открыть сейф IPasswrd");
            if (raw is null) { Console.WriteLine("[IPW-BIO] quick unlock: gate null -> password"); return ""; }
        }
        else
        {
            // Audit M1: UI-auth BEFORE loading the DEK into process memory.
            if (!await Svc.Biometric.AuthenticateAsync("Открыть сейф IPasswrd")) return "";
            raw = Svc.KeyStore.Load(QuickUnlockKey);
            if (raw is null) { WipeQuickUnlock(); return ""; }
        }

        QuickUnlockData? d;
        try { d = JsonSerializer.Deserialize<QuickUnlockData>(raw); }
        catch { WipeQuickUnlock(); return ""; }
        if (d is null || string.IsNullOrEmpty(d.Dek)) { WipeQuickUnlock(); return ""; }
        if (d.ExpiresAt != 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() > d.ExpiresAt)
        {
            WipeQuickUnlock();
            return "Пора ввести мастер-пароль (прошло 30 дней).";
        }

        try
        {
            byte[] blob = File.ReadAllBytes(LocalVaultPath);
            _vault = await Task.Run(() => IPasswrd.Core.Vault.UnlockWithSessionKey(blob, Convert.FromBase64String(d.Dek), RequireAuth(blob)));
        }
        catch
        {
            WipeQuickUnlock();
            return "Быстрая разблокировка не сработала — введите мастер-пароль.";
        }

        AfterUnlock();
        return null;
    }

    // ================= блокировка =================

    /// <summary>Убрать сейф из памяти (Face ID продолжает работать — ключ остаётся в Keychain).
    /// Кэш DEK расширения AutoFill стирается: «замок» отзывает silent/quick AutoFill-сессию.</summary>
    public void Lock()
    {
        SecureClipboard.Wipe();   // стереть скопированный секрет при блокировке
        AutoFillShare.WipeDek();  // shared keychain-access-group (audit H1)
        _vault = null;
        LockedChanged?.Invoke();
    }

    public void OnBackgrounded() => _backgroundedAt = DateTimeOffset.UtcNow;

    /// <summary>
    /// Wipe the in-memory vault if background idle time has reached <see cref="AutolockMinutes"/>.
    /// Must be called from Autofill / Accessibility before reading <see cref="IsUnlocked"/> /
    /// <see cref="Vault"/>: those services run while the MAUI window stays stopped, so
    /// <see cref="OnResumed"/> never fires and credentials would otherwise keep serving.
    /// </summary>
    public void EnforceAutolock()
    {
        if (_vault is null) return;
        if (Autolock.IsDue(AutolockMinutes, _backgroundedAt, DateTimeOffset.UtcNow))
            Lock();
    }

    /// <summary>Vault still open after applying autolock — prefer on fill paths over raw <see cref="Vault"/>.</summary>
    public Vault? ActiveVault
    {
        get
        {
            EnforceAutolock();
            return _vault;
        }
    }

    public void OnResumed()
    {
        EnforceAutolock();
        _backgroundedAt = Autolock.NotBackgrounded;
    }

    // ================= сохранение и синхронизация =================

    /// <summary>Merge an externally-supplied vault copy (a user-picked sync file, or a pulled cloud
    /// copy) into the open vault, enforcing the anti-downgrade rule: a stripped MAC on a vault this
    /// device already knows to be protected is rejected as tampering.</summary>
    public int MergeExternal(byte[] otherBlob)
    {
        if (_vault is null) throw new InvalidOperationException("vault is locked");
        return _vault.MergeFrom(otherBlob, RequireAuthLocal());
    }

    public async Task SaveAsync()
    {
        if (_vault is null) return;
        byte[] data = _vault.Serialize();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LocalVaultPath)!);
            VaultBackups.Snapshot(LocalVaultPath);   // копия того, что сейчас будет перезаписано — как на ПК
            File.WriteAllBytes(LocalVaultPath, data);
            MarkVaultAuthenticated(_vault);   // this write carries a MAC; refuse un-sealed copies of this lineage hereafter
        }
        catch (Exception ex)
        {
            // Нет места / отозван доступ к хранилищу — сообщаем, но НЕ роняем экран
            // (все вызовы идут из async void-обработчиков).
            Console.WriteLine("[IPW] SaveAsync local write failed: " + ex);
            LastSyncStatus = "Не удалось сохранить сейф на устройстве.";
            SyncProblem?.Invoke(LastSyncStatus);
            return;
        }
        VaultChanged?.Invoke();
        if (BiometricUnlockEnabled) SaveQuickUnlock();
        ShareForAutoFill();
        try { await SyncAsync(); }
        catch (Exception ex) { Console.WriteLine("[IPW] SaveAsync sync failed: " + ex); }
    }

    private int _syncBusy;

    /// <summary>Активный бэкенд синхронизации: Google Drive (приоритет) или файл в iCloud.</summary>
    private bool GoogleActive => GoogleDrive.IsConnected;
    public bool AnySyncConnected => GoogleDrive.IsConnected || Svc.External.IsConnected;

    private async Task<byte[]?> RemoteReadAsync() =>
        GoogleActive ? await GoogleDrive.PullAsync() : await Svc.External.ReadAsync();

    private async Task<bool> RemoteWriteAsync(byte[] data)
    {
        if (GoogleActive) { try { await GoogleDrive.PushAsync(data); return true; } catch { return false; } }
        return await Svc.External.WriteAsync(data);
    }

    /// <summary>Слить изменения с внешним сейфом (Google Drive или iCloud) поэлементно и дописать обратно.</summary>
    public async Task SyncAsync()
    {
        Vault? v = _vault;
        if (v is null || !AnySyncConnected) return;
        if (Interlocked.Exchange(ref _syncBusy, 1) == 1) return;
        bool google = GoogleActive;
        string where = google ? "Google" : "iCloud";
        try
        {
            byte[]? ext;
            try { ext = await RemoteReadAsync(); }
            catch (Exception)
            {
                LastSyncStatus = $"Нет связи с {where}.";
                SyncProblem?.Invoke(LastSyncStatus);
                return;
            }

            bool adoptedPassword = false;
            if (ext is { Length: > 0 })
            {
                int changed;
                string envBefore = v.MasterPasswordChangedAt;
                try { changed = v.MergeFrom(ext, RequireAuthLocal()); }
                catch (VaultIntegrityException)
                {
                    LastSyncStatus = $"В {where} лежит другой сейф. Сначала решите, какой оставить.";
                    SyncProblem?.Invoke(LastSyncStatus);
                    return;
                }
                catch (Exception)
                {
                    LastSyncStatus = $"Файл в {where} не читается.";
                    SyncProblem?.Invoke(LastSyncStatus);
                    return;
                }
                // Смена мастер-пароля с другого устройства: конверт принят внутри MergeFrom.
                // Записать локальный файл нужно даже без изменённых записей — иначе телефон
                // до следующей правки хранит старый конверт и путает пользователя при входе.
                adoptedPassword = !string.Equals(v.MasterPasswordChangedAt, envBefore, StringComparison.Ordinal);
                if ((changed > 0 || adoptedPassword) && ReferenceEquals(_vault, v))
                {
                    File.WriteAllBytes(LocalVaultPath, v.Serialize());
                    ShareForAutoFill();
                    MainThread.BeginInvokeOnMainThread(() => VaultChanged?.Invoke());
                }
            }

            // Отпечаток/Face ID хранит DEK, а не пароль, поэтому быстрая разблокировка
            // переживает смену конверта; человеку важно лишь знать, что при следующем
            // вводе пароля телефон ждёт НОВЫЙ (тот, что задан на другом устройстве).
            string note = adoptedPassword ? " · применена смена мастер-пароля с другого устройства" : "";

            byte[] mine = v.Serialize();
            if (ext is null || !mine.AsSpan().SequenceEqual(ext))
            {
                bool ok = await RemoteWriteAsync(mine);
                LastSyncStatus = ok ? $"Синхронизировано {DateTime.Now:HH:mm}{note}" : $"Не удалось записать в {where}.";
                if (!ok) SyncProblem?.Invoke(LastSyncStatus);
            }
            else
            {
                LastSyncStatus = $"Синхронизировано {DateTime.Now:HH:mm}{note}";
            }
        }
        finally { Interlocked.Exchange(ref _syncBusy, 0); }
    }

    /// <summary>Войти в Google и связать сейф с Drive. Возвращает email; бросает при ошибке.
    /// VaultIntegrityException — на Drive лежит другой сейф.</summary>
    public async Task<string?> ConnectGoogleAsync()
    {
        string? email = await GoogleDrive.SignInAsync();     // системное окно согласия
        Svc.External.Disconnect();                            // Google становится единственным каналом

        Vault? v = _vault;
        if (v is not null)
        {
            byte[]? remote = await GoogleDrive.PullAsync();
            if (remote is { Length: > 0 })
            {
                v.MergeFrom(remote, RequireAuthLocal());                          // может бросить VaultIntegrityException
                File.WriteAllBytes(LocalVaultPath, v.Serialize());
                ShareForAutoFill();
                MainThread.BeginInvokeOnMainThread(() => VaultChanged?.Invoke());
            }
            await GoogleDrive.PushAsync(v.Serialize());
            LastSyncStatus = $"Синхронизировано {DateTime.Now:HH:mm}";
        }
        return email;
    }

    public void DisconnectGoogle() => GoogleDrive.SignOut();

    // ================= смена мастер-пароля =================

    public async Task<string?> ChangeMasterPasswordAsync(string oldPw, string newPw)
    {
        if (_vault is null) return "Сейф закрыт.";
        try
        {
            await Task.Run(() => _vault.ChangeMasterPassword(oldPw, newPw));
        }
        catch (WrongMasterPasswordException)
        {
            return "Текущий мастер-пароль указан неверно.";
        }
        await SaveAsync();
        return null;
    }

    // ================= код восстановления =================

    /// <summary>Есть ли у открытого сейфа код восстановления.</summary>
    public bool HasRecoveryCode => _vault?.HasRecoveryCode == true;

    /// <summary>Когда выдан текущий код (ISO-8601 UTC) или null.</summary>
    public string? RecoveryIssuedAt => _vault?.RecoveryCodeIssuedAt;

    /// <summary>Есть ли конверт восстановления в локальном файле — можно узнать без мастер-пароля
    /// (для ссылки «Забыли мастер-пароль?» на экране входа).</summary>
    public bool RecoveryAvailableForLocalVault
    {
        get
        {
            try { return HasLocalVault && IPasswrd.Core.Vault.IsRecoveryAvailable(File.ReadAllBytes(LocalVaultPath)); }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Создать/пересоздать код восстановления. Возвращает код для показа (единственная копия);
    /// старый код перестаёт работать. Сейф должен быть открыт.</summary>
    public async Task<string?> EnableRecoveryAsync()
    {
        if (_vault is null) return null;
        string code = _vault.EnableRecovery();
        await SaveAsync();
        return code;
    }

    /// <summary>Удалить код восстановления (записанная бумажка перестаёт открывать сейф).</summary>
    public async Task DisableRecoveryAsync()
    {
        if (_vault is null) return;
        _vault.DisableRecovery();
        await SaveAsync();
    }

    /// <summary>Открыть сейф кодом восстановления и сразу задать новый мастер-пароль.
    /// null — успех; иначе текст ошибки. Учитывает тот же локаут, что и обычный вход.</summary>
    public async Task<string?> RecoverWithCodeAsync(string recoveryCode, string newMasterPassword)
    {
        if (LockoutRemaining > TimeSpan.Zero)
            return $"Слишком много попыток. Подождите {Fmt.Duration(LockoutRemaining)}.";

        VaultUnlockSource.Decision decision;
        try { decision = await PrepareUnlockDecisionAsync(); }
        catch { return "Файл сейфа не найден."; }

        Vault restored;
        byte[]? mergePeer = decision.OtherBlobToMergeAfterUnlock;
        try
        {
            restored = await Task.Run(() =>
                IPasswrd.Core.Vault.UnlockWithRecoveryCode(
                    decision.UnlockBlob, recoveryCode, RequireAuth(decision.UnlockBlob)));
        }
        catch (RecoveryNotEnabledException) when (mergePeer is { Length: > 0 })
        {
            // Recovery may exist only on the peer copy — try once before giving up.
            try
            {
                restored = await Task.Run(() =>
                    IPasswrd.Core.Vault.UnlockWithRecoveryCode(
                        mergePeer, recoveryCode, RequireAuth(mergePeer)));
                mergePeer = decision.UnlockBlob;
            }
            catch (RecoveryNotEnabledException)
            {
                return "Для этого сейфа код восстановления не создавался.";
            }
            catch (WrongRecoveryCodeException)
            {
                return FailUnlockRecovery();
            }
            catch (Exception)
            {
                return "Файл сейфа повреждён или имеет неизвестный формат.";
            }
        }
        catch (RecoveryNotEnabledException)
        {
            return "Для этого сейфа код восстановления не создавался.";
        }
        catch (WrongRecoveryCodeException) when (mergePeer is { Length: > 0 })
        {
            try
            {
                restored = await Task.Run(() =>
                    IPasswrd.Core.Vault.UnlockWithRecoveryCode(
                        mergePeer, recoveryCode, RequireAuth(mergePeer)));
                mergePeer = decision.UnlockBlob;
            }
            catch (WrongRecoveryCodeException)
            {
                return FailUnlockRecovery();
            }
            catch (RecoveryNotEnabledException)
            {
                return FailUnlockRecovery();
            }
            catch (Exception)
            {
                return "Файл сейфа повреждён или имеет неизвестный формат.";
            }
        }
        catch (WrongRecoveryCodeException)
        {
            return FailUnlockRecovery();
        }
        catch (Exception)
        {
            return "Файл сейфа повреждён или имеет неизвестный формат.";
        }

        // Сейф открыт, но всё ещё завёрнут забытым паролем — ОБЯЗАТЕЛЬНО задаём новый.
        try { await Task.Run(() => restored.ResetMasterPassword(newMasterPassword)); }
        catch (Exception) { return "Не удалось задать новый мастер-пароль."; }

        _vault = restored;
        PersistMergedUnlock(mergePeer, decision.RemoteIsForeign);
        Fails = 0;
        LockedUntil = DateTimeOffset.UnixEpoch;
        await SaveAsync();      // запишет уже с новым паролем + синк
        AfterUnlock();
        return null;
    }

    private string FailUnlockRecovery()
    {
        Fails++;
        TimeSpan pen = Lockout.PenaltyFor(Fails);
        if (pen > TimeSpan.Zero)
        {
            LockedUntil = DateTimeOffset.UtcNow + pen;
            return $"Неверный код восстановления. Вход закрыт на {Fmt.Duration(pen)}.";
        }
        int left = Lockout.AttemptsLeft(Fails);
        return left > 0
            ? $"Неверный код восстановления. Осталось попыток без блокировки: {left}."
            : "Неверный код восстановления.";
    }

    // ================= импорт из файла =================

    /// <summary>Разобрать экспорт (CSV/Kaspersky), схлопнуть дубли и добавить только новые записи.
    /// Возвращает (добавлено, всего в файле). Дубли определяются как на ПК (Core.Dedup).</summary>
    public async Task<(int added, int parsed)> ImportAsync(string content)
    {
        if (_vault is null) return (0, 0);

        List<VaultItem> parsed = await Task.Run(() => IPasswrd.Core.Import.Importer.Parse(content));
        if (parsed.Count == 0) return (0, 0);

        // ключи уже существующих записей — чтобы не заводить дубли того, что уже есть
        var existing = new HashSet<string>(StringComparer.Ordinal);
        foreach (VaultEntry e in _vault.Items())
            existing.Add(Dedup.Key(e.Item));

        int added = 0;
        foreach (VaultItem it in Dedup.Collapse(parsed))
        {
            if (existing.Add(Dedup.Key(it)))   // Add == true → раньше не встречалось
            {
                _vault.Add(it);
                added++;
            }
        }

        if (added > 0) await SaveAsync();
        return (added, parsed.Count);
    }

    // ================= настройки, синхронизируемые через сейф (meta-запись) =================

    /// <summary>Пользовательские имена групп сайтов из meta-записи (пишутся Windows-приложением).</summary>
    public Dictionary<string, string> SiteNames()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            VaultItem? prefs = _vault?.Items().FirstOrDefault(x => x.Id == SiteGroups.PrefsRecordId)?.Item;
            if (prefs is not null && prefs.Fields.TryGetValue("siteNames", out var sn) && !string.IsNullOrWhiteSpace(sn))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(sn);
                if (d is not null) return d;
            }
        }
        catch { /* meta-запись необязательна */ }
        return result;
    }
}
