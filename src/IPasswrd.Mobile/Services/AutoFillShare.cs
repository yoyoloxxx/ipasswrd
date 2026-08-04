using System.Text;
using System.Text.RegularExpressions;
using IPasswrd.Core;

#if IOS
using AuthenticationServices;
using Foundation;
#endif

namespace IPasswrd.Mobile.Services;

/// <summary>Мост к системному автозаполнению iOS: копия сейфа в общем контейнере
/// (App Group) для расширения + подсказки логинов над клавиатурой
/// (ASCredentialIdentityStore). AltStore при подписи переименовывает группу,
/// поэтому её настоящее имя читаем из embedded.mobileprovision.</summary>
public static class AutoFillShare
{
    public const string FallbackGroup = "group.com.yoyoloxxx.ipasswrd";

#if IOS
    private static string? _group;

    private static string GroupId()
    {
        if (_group is not null) return _group;
        try
        {
            string? p = NSBundle.MainBundle.PathForResource("embedded", "mobileprovision");
            if (p is not null)
            {
                string s = Encoding.ASCII.GetString(File.ReadAllBytes(p));
                Match m = Regex.Match(s,
                    "<key>com\\.apple\\.security\\.application-groups</key>\\s*<array>\\s*<string>([^<]+)</string>",
                    RegexOptions.Singleline);
                if (m.Success) return _group = m.Groups[1].Value.Trim();
            }
        }
        catch { }
        return _group = FallbackGroup;
    }

    private static string? GroupVaultPath()
    {
        try
        {
            NSUrl? url = NSFileManager.DefaultManager.GetContainerUrl(GroupId());
            string? root = url?.Path;
            return root is null ? null : Path.Combine(root, "vault.ipvault");
        }
        catch { return null; }
    }

    /// <summary>Положить свежую (зашифрованную) копию сейфа в общий контейнер.</summary>
    public static void MirrorVault(byte[] data)
    {
        try
        {
            string? p = GroupVaultPath();
            if (p is null) return;
            File.WriteAllBytes(p, data);
        }
        catch { /* автозаполнение — необязательный путь */ }
    }

    /// <summary>Обновить подсказки над клавиатурой: домен + логин для каждого аккаунта.</summary>
    public static void UpdateIdentities(Vault v)
    {
        try
        {
            var ids = new List<ASPasswordCredentialIdentity>();
            foreach (VaultEntry e in v.Items())
            {
                if (e.Item.Type != "account") continue;
                string user = e.Item.Fields.GetValueOrDefault("username", "");
                string dom = Dedup.RegistrableDomain(e.Item.Fields.GetValueOrDefault("url", ""));
                if (user.Length == 0 || dom.Length == 0) continue;
                ids.Add(new ASPasswordCredentialIdentity(
                    new ASCredentialServiceIdentifier(dom, ASCredentialServiceIdentifierType.Domain),
                    user, e.Id));
            }

            ASCredentialIdentityStore.SharedStore.GetCredentialIdentityStoreState(state =>
            {
                if (state is null || !state.Enabled) return;
                ASCredentialIdentityStore.SharedStore.ReplaceCredentialIdentities(ids.ToArray(), (ok, err) => { });
            });
        }
        catch { /* стор может быть недоступен, это не критично */ }
    }
#else
    public static void MirrorVault(byte[] data) { }
    public static void UpdateIdentities(Vault v) { }
#endif
}
