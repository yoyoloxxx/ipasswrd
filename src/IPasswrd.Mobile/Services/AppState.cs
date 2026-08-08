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
    private DateTimeOffset _backgroundedAt = DateTimeOffset.MaxValue;

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
        get => Preferences.Get("autolockMinutes", 5);
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

    private int Fails
    {
        get => Preferences.Get("lockout.fails", 0);
        set => Preferences.Set("lockout.fails", value);
    }

    private DateTimeOffset LockedUntil
    {
        get => DateTimeOffset.FromUnixTimeSeconds(Preferences.Get("lockout.until", 0L));
        set => Preferences.Set("lockout.until", value.ToUnixTimeSeconds());
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

        byte[] blob;
        try { blob = await PullLatestAsync() ?? throw new FileNotFoundException(); }
        catch { return "Файл сейфа не найден."; }

        try
        {
            _vault = await Task.Run(() => IPasswrd.Core.Vault.Unlock(blob, masterPassword));
        }
        catch (WrongMasterPasswordException)
        {
            Fails++;
            TimeSpan pen = Lockout.PenaltyFor(Fails);
            if (pen > TimeSpan.Zero)
            {
                LockedUntil = DateTimeOffset.UtcNow + pen;
                return $"Неверный мастер-пароль. Вход закрыт на {Fmt.Duration(pen)}.";
            }
            int left = Lockout.AttemptsLeft(Fails);
            return left > 0
                ? $"Неверный мастер-пароль. Осталось попыток без блокировки: {left}."
                : "Неверный мастер-пароль.";
        }
        catch (Exception)
        {
            return "Файл сейфа повреждён или имеет неизвестный формат.";
        }

        Fails = 0;
        LockedUntil = DateTimeOffset.MinValue;
        AfterUnlock();
        return null;
    }

    private void AfterUnlock()
    {
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
    /// Перед разблокировкой мастер-паролем подтягиваем свежий файл из iCloud (pull-first):
    /// так конверт ключа (соль/обёртка) всегда последний — смена мастер-пароля на Windows
    /// не откатится записью со старым конвертом. Быстрой разблокировке это не нужно:
    /// DEK не меняется при смене пароля.
    /// </summary>
    private async Task<byte[]?> PullLatestAsync()
    {
        // Google Drive имеет приоритет, если вход выполнен (тот же файл, что на ПК).
        if (GoogleDrive.IsConnected)
        {
            try
            {
                byte[]? g = await GoogleDrive.PullAsync();
                if (g is { Length: > 0 })
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LocalVaultPath)!);
                    // Файл сейчас заменится привезённым с Диска — сначала копия того, что было на устройстве:
                    // именно при таких подменах теряют данные, а не при обычных правках.
                    VaultBackups.Snapshot(LocalVaultPath);
                    File.WriteAllBytes(LocalVaultPath, g);
                    return g;
                }
            }
            catch { /* оффлайн/сбой → откат к локальному */ }
        }
        if (Svc.External.IsConnected)
        {
            byte[]? ext = await Svc.External.ReadAsync();
            if (ext is { Length: > 0 })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LocalVaultPath)!);
                VaultBackups.Snapshot(LocalVaultPath);   // та же подмена, только из iCloud/файла
                File.WriteAllBytes(LocalVaultPath, ext);
                return ext;
            }
        }
        return HasLocalVault ? File.ReadAllBytes(LocalVaultPath) : null;
    }

    // ================= быстрая разблокировка (Keychain + Face ID) =================

    private sealed class QuickUnlockData
    {
        public string Dek { get; set; } = "";
        public long ExpiresAt { get; set; }
    }

    public bool QuickUnlockAvailable =>
        BiometricUnlockEnabled && Svc.Biometric.IsAvailable && Svc.KeyStore.Load(QuickUnlockKey) is not null && HasLocalVault;

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
            Svc.KeyStore.Save(QuickUnlockKey, JsonSerializer.SerializeToUtf8Bytes(data));
        }
        catch { /* необязательный путь: мастер-пароль всегда работает */ }
    }

    private void WipeQuickUnlock() => Svc.KeyStore.Delete(QuickUnlockKey);

    /// <summary>null — успех; "" — тихий отказ (нет ключа/отмена Face ID); иначе текст ошибки.</summary>
    public async Task<string?> TryQuickUnlockAsync()
    {
        if (LockoutRemaining > TimeSpan.Zero)
            return $"Слишком много попыток. Подождите {Fmt.Duration(LockoutRemaining)}.";

        byte[]? raw = Svc.KeyStore.Load(QuickUnlockKey);
        if (raw is null || !HasLocalVault) return "";

        QuickUnlockData? d;
        try { d = JsonSerializer.Deserialize<QuickUnlockData>(raw); }
        catch { WipeQuickUnlock(); return ""; }
        if (d is null || string.IsNullOrEmpty(d.Dek)) { WipeQuickUnlock(); return ""; }
        if (d.ExpiresAt != 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() > d.ExpiresAt)
        {
            WipeQuickUnlock();
            return "Пора ввести мастер-пароль (прошло 30 дней).";
        }

        if (!await Svc.Biometric.AuthenticateAsync("Открыть сейф IPasswrd")) return "";

        try
        {
            byte[] blob = File.ReadAllBytes(LocalVaultPath);
            _vault = await Task.Run(() => IPasswrd.Core.Vault.UnlockWithSessionKey(blob, Convert.FromBase64String(d.Dek)));
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

    /// <summary>Убрать сейф из памяти (Face ID продолжает работать — ключ остаётся в Keychain).</summary>
    public void Lock()
    {
        SecureClipboard.Wipe();   // стереть скопированный секрет при блокировке
        _vault = null;
        LockedChanged?.Invoke();
    }

    public void OnBackgrounded() => _backgroundedAt = DateTimeOffset.UtcNow;

    public void OnResumed()
    {
        if (_vault is null) return;
        int m = AutolockMinutes;
        if (m > 0 && DateTimeOffset.UtcNow - _backgroundedAt >= TimeSpan.FromMinutes(m))
            Lock();
        _backgroundedAt = DateTimeOffset.MaxValue;
    }

    // ================= сохранение и синхронизация =================

    public async Task SaveAsync()
    {
        if (_vault is null) return;
        byte[] data = _vault.Serialize();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LocalVaultPath)!);
            VaultBackups.Snapshot(LocalVaultPath);   // копия того, что сейчас будет перезаписано — как на ПК
            File.WriteAllBytes(LocalVaultPath, data);
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

            if (ext is { Length: > 0 })
            {
                int changed;
                try { changed = v.MergeFrom(ext); }
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
                if (changed > 0 && ReferenceEquals(_vault, v))
                {
                    File.WriteAllBytes(LocalVaultPath, v.Serialize());
                    ShareForAutoFill();
                    MainThread.BeginInvokeOnMainThread(() => VaultChanged?.Invoke());
                }
            }

            byte[] mine = v.Serialize();
            if (ext is null || !mine.AsSpan().SequenceEqual(ext))
            {
                bool ok = await RemoteWriteAsync(mine);
                LastSyncStatus = ok ? $"Синхронизировано {DateTime.Now:HH:mm}" : $"Не удалось записать в {where}.";
                if (!ok) SyncProblem?.Invoke(LastSyncStatus);
            }
            else
            {
                LastSyncStatus = $"Синхронизировано {DateTime.Now:HH:mm}";
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
                v.MergeFrom(remote);                          // может бросить VaultIntegrityException
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

        byte[] blob;
        try { blob = await PullLatestAsync() ?? throw new FileNotFoundException(); }
        catch { return "Файл сейфа не найден."; }

        Vault restored;
        try
        {
            restored = await Task.Run(() => IPasswrd.Core.Vault.UnlockWithRecoveryCode(blob, recoveryCode));
        }
        catch (RecoveryNotEnabledException)
        {
            return "Для этого сейфа код восстановления не создавался.";
        }
        catch (WrongRecoveryCodeException)
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
        catch (Exception)
        {
            return "Файл сейфа повреждён или имеет неизвестный формат.";
        }

        // Сейф открыт, но всё ещё завёрнут забытым паролем — ОБЯЗАТЕЛЬНО задаём новый.
        try { await Task.Run(() => restored.ResetMasterPassword(newMasterPassword)); }
        catch (Exception) { return "Не удалось задать новый мастер-пароль."; }

        _vault = restored;
        Fails = 0;
        LockedUntil = DateTimeOffset.MinValue;
        await SaveAsync();      // запишет уже с новым паролем + синк
        AfterUnlock();
        return null;
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
