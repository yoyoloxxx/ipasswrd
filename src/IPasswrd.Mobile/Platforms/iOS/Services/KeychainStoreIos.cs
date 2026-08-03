using Foundation;
using IPasswrd.Mobile.Services;
using Security;

namespace IPasswrd.Mobile.Platforms.iOS.Services;

/// <summary>Keychain: сессионный ключ быстрой разблокировки хранится только на этом устройстве
/// (kSecAttrAccessibleWhenUnlockedThisDeviceOnly — не попадает в резервные копии и iCloud).</summary>
public sealed class KeychainStoreIos : ISecureKeyStore
{
    private const string Service = "com.yoyoloxxx.ipasswrd";

    private static SecRecord Query(string name) => new(SecKind.GenericPassword)
    {
        Service = Service,
        Account = name,
    };

    public bool Save(string name, byte[] data)
    {
        try
        {
            SecKeyChain.Remove(Query(name));
            var rec = Query(name);
            rec.Accessible = SecAccessible.WhenUnlockedThisDeviceOnly;
            rec.ValueData = NSData.FromArray(data);
            return SecKeyChain.Add(rec) == SecStatusCode.Success;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public byte[]? Load(string name)
    {
        try
        {
            NSData? data = SecKeyChain.QueryAsData(Query(name), false, out SecStatusCode code);
            if (code != SecStatusCode.Success || data is null) return null;
            return data.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Delete(string name)
    {
        try { SecKeyChain.Remove(Query(name)); } catch (Exception) { }
    }
}
