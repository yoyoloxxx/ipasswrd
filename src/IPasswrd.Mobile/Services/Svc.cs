namespace IPasswrd.Mobile.Services;

/// <summary>Сканер QR-кодов (нативная камера).</summary>
public interface IQrScanner
{
    /// <summary>Открывает камеру; возвращает содержимое первого QR или null (отмена/нет доступа).</summary>
    Task<string?> ScanAsync();
}

/// <summary>Биометрия (Face ID / Touch ID) или код устройства.</summary>
public interface IBiometricAuth
{
    bool IsAvailable { get; }
    /// <summary>«Face ID» | «Touch ID» | «Код устройства» — для подписей в UI.</summary>
    string Kind { get; }
    Task<bool> AuthenticateAsync(string reason);
}

/// <summary>Защищённое хранилище маленьких секретов (Keychain на iOS).</summary>
public interface ISecureKeyStore
{
    bool Save(string name, byte[] data);
    byte[]? Load(string name);
    void Delete(string name);
}

/// <summary>
/// Внешний файл сейфа в iCloud Drive (тот же vault.ipvault, который пишет Windows-приложение
/// в iCloudDrive\IPasswrd). Подключается через системный выбор файла; доступ сохраняется
/// security-scoped bookmark'ом, чтение/запись — через NSFileCoordinator.
/// </summary>
public interface IExternalVaultFile
{
    bool IsConnected { get; }
    string? DisplayName { get; }
    /// <summary>Диалог выбора файла; сохраняет закладку. null — отмена, иначе байты выбранного файла (может быть пустой массив).</summary>
    Task<byte[]?> PickAndConnectAsync();
    Task<byte[]?> ReadAsync();
    Task<bool> WriteAsync(byte[] data);
    void Disconnect();
    /// <summary>Разово выгрузить копию сейфа (например, в iCloud Drive/IPasswrd при первом переносе).</summary>
    Task<bool> ExportCopyAsync(byte[] data, string suggestedName);
}

/// <summary>Локатор платформенных сервисов (инициализируется в MauiProgram).</summary>
public static class Svc
{
    public static IQrScanner Qr { get; private set; } = new NullQrScanner();
    public static IBiometricAuth Biometric { get; private set; } = new NullBiometric();
    public static ISecureKeyStore KeyStore { get; private set; } = new PrefsKeyStore();
    public static IExternalVaultFile External { get; private set; } = new NullExternalVaultFile();
    public static AppState State { get; } = new();

    public static void Init(IQrScanner qr, IBiometricAuth biometric, ISecureKeyStore keyStore, IExternalVaultFile external)
    {
        Qr = qr; Biometric = biometric; KeyStore = keyStore; External = external;
    }
}

// ---- заглушки для платформ без реализации (Android появится позже) ----

public sealed class NullQrScanner : IQrScanner
{
    public Task<string?> ScanAsync() => Task.FromResult<string?>(null);
}

public sealed class NullBiometric : IBiometricAuth
{
    public bool IsAvailable => false;
    public string Kind => "Биометрия";
    public Task<bool> AuthenticateAsync(string reason) => Task.FromResult(false);
}

/// <summary>Небезопасный запасной вариант (только для отладки вне iOS).</summary>
public sealed class PrefsKeyStore : ISecureKeyStore
{
    public bool Save(string name, byte[] data) { Preferences.Set("ks." + name, Convert.ToBase64String(data)); return true; }
    public byte[]? Load(string name)
    {
        string s = Preferences.Get("ks." + name, "");
        return string.IsNullOrEmpty(s) ? null : Convert.FromBase64String(s);
    }
    public void Delete(string name) => Preferences.Remove("ks." + name);
}

public sealed class NullExternalVaultFile : IExternalVaultFile
{
    public bool IsConnected => false;
    public string? DisplayName => null;
    public Task<byte[]?> PickAndConnectAsync() => Task.FromResult<byte[]?>(null);
    public Task<byte[]?> ReadAsync() => Task.FromResult<byte[]?>(null);
    public Task<bool> WriteAsync(byte[] data) => Task.FromResult(false);
    public void Disconnect() { }
    public Task<bool> ExportCopyAsync(byte[] data, string suggestedName) => Task.FromResult(false);
}
