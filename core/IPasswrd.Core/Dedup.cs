namespace IPasswrd.Core;

/// <summary>
/// Duplicate detection for vault records, centralised so the SAME rule governs
/// file import today and password auto-save later.
///
/// Accounts and passkeys collapse across sub-domains: the same registrable domain
/// + same login + same password is one record. When collapsing, the lowest-level
/// host wins (google.com beats accounts.google.com), so the surviving entry keeps
/// the clean 2nd-level domain. Records with the same login but a DIFFERENT password
/// are kept separate — nothing is ever silently overwritten.
///
/// Other record types (note / card / document) only collapse when they are
/// effectively identical, so distinct items are never merged by accident.
///
/// The registrable-domain boundary is delegated to <see cref="PublicSuffix"/>, which
/// uses the full official Public Suffix List. That boundary is security-critical: it
/// is what keeps evil.duckdns.org from ever being treated as the same site as
/// alice.duckdns.org (and thus autofilled with the wrong tenant's password).
/// </summary>
public static class Dedup
{
    /// <summary>True if <paramref name="domain"/> is itself a public suffix (a bare TLD, or a
    /// multi-tenant registration point) — i.e. NOT a registrable site on its own.</summary>
    public static bool IsPublicSuffix(string? domain) => PublicSuffix.IsPublicSuffix(domain);

    /// <summary>Bare host of a URL, lower-cased, without scheme/path or a leading "www.".
    /// IDN hosts are folded to punycode so they match the (punycode) Public Suffix List and a
    /// Unicode homograph cannot dodge the suffix rule.</summary>
    private static string Host(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        string host;
        try
        {
            var u = url.Contains("://") ? new Uri(url) : new Uri("https://" + url.Trim());
            try { host = u.IdnHost; } catch { host = u.Host; }
        }
        catch { host = url.Trim(); }
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        return host;
    }

    /// <summary>Registrable domain (eTLD+1) of a URL: the longest matching public suffix plus one
    /// more label. accounts.google.com → google.com; victim.github.io → victim.github.io (github.io
    /// is a public suffix, so tenants do NOT collapse together).</summary>
    public static string RegistrableDomain(string? url) => PublicSuffix.RegistrableDomain(Host(url));

    /// <summary>Label count of the host (accounts.google.com = 3, google.com = 2). Fewer = closer to base domain.</summary>
    public static int HostDepth(string? url)
    {
        string host = Host(url);
        if (host.Length == 0) return int.MaxValue;   // no url -> least preferred as a survivor
        return host.Split('.', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>Two records that produce the same key are duplicates of each other.</summary>
    public static string Key(VaultItem it)
    {
        string type = it.Type ?? "";
        if (type is "account" or "passkey")
        {
            string url = it.Fields.TryGetValue("url", out var u) ? u : "";
            string login = (it.Fields.TryGetValue("username", out var us) ? us : "").Trim().ToLowerInvariant();
            string pass = it.Fields.TryGetValue("password", out var p) ? p : "";
            return $"acct{RegistrableDomain(url)}{login}{pass}";
        }
        if (type == "note")
            return $"note{(it.Title ?? "").Trim().ToLowerInvariant()}{(it.Notes ?? "").Trim()}";

        string num = it.Fields.TryGetValue("number", out var n) ? n : "";
        return $"{type}{(it.Title ?? "").Trim().ToLowerInvariant()}{num}";
    }

    /// <summary>True if <paramref name="a"/> should be kept over <paramref name="b"/> when they share a key.</summary>
    public static bool Prefer(VaultItem a, VaultItem b)
    {
        string ua = a.Fields.TryGetValue("url", out var x) ? x : "";
        string ub = b.Fields.TryGetValue("url", out var y) ? y : "";
        int da = HostDepth(ua), db = HostDepth(ub);
        if (da != db) return da < db;                                   // lowest-level host wins (google.com over accounts.google.com)

        bool ta = !string.IsNullOrWhiteSpace(a.Title), tb = !string.IsNullOrWhiteSpace(b.Title);
        if (ta != tb) return ta;                                        // prefer the one that actually has a title
        return (a.Title ?? "").Length <= (b.Title ?? "").Length;        // then the shorter title, else keep the first
    }

    /// <summary>
    /// Collapse a stream of items, keeping one survivor per <see cref="Key"/>
    /// (chosen by <see cref="Prefer"/>) and preserving first-seen order.
    /// </summary>
    public static List<VaultItem> Collapse(IEnumerable<VaultItem> items)
    {
        var best = new Dictionary<string, VaultItem>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var it in items)
        {
            string k = Key(it);
            if (!best.TryGetValue(k, out var cur)) { best[k] = it; order.Add(k); }
            else if (Prefer(it, cur)) best[k] = it;
        }
        return order.ConvertAll(k => best[k]);
    }
}
