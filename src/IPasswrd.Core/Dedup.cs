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
/// </summary>
public static class Dedup
{
    // Multi-part public suffixes where the registrable domain is the last THREE labels.
    private static readonly HashSet<string> TwoLevelSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "co.uk","org.uk","gov.uk","ac.uk","me.uk","com.br","com.au","net.au","org.au",
        "co.jp","or.jp","co.kr","com.tr","co.in","com.ua","net.ua","org.ua","net.ru","org.ru",
        "co.il","com.mx","co.nz","com.tw","com.sg","com.cn","com.hk","com.pl","com.pt","com.es",
        "com.ar","co.za","com.my","co.th","com.vn","com.ph","com.co",
    };

    /// <summary>Bare host of a URL, lower-cased, without scheme/path or a leading "www.".</summary>
    private static string Host(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        string host;
        try { var u = url.Contains("://") ? new Uri(url) : new Uri("https://" + url.Trim()); host = u.Host; }
        catch { host = url.Trim(); }
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        return host;
    }

    /// <summary>Registrable domain (eTLD+1) of a URL, e.g. accounts.google.com to google.com.</summary>
    public static string RegistrableDomain(string? url)
    {
        string host = Host(url);
        if (host.Length == 0) return "";
        if (System.Net.IPAddress.TryParse(host, out _)) return host;   // IP address (10.90.90.2): never trim
        var l = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (l.Length <= 2) return host;
        string lastTwo = l[^2] + "." + l[^1];
        return TwoLevelSuffixes.Contains(lastTwo) ? string.Join('.', l[^3..]) : lastTwo;
    }

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
            return $"acct{RegistrableDomain(url)}{login}{pass}";
        }
        if (type == "note")
            return $"note{(it.Title ?? "").Trim().ToLowerInvariant()}{(it.Notes ?? "").Trim()}";

        string num = it.Fields.TryGetValue("number", out var n) ? n : "";
        return $"{type}{(it.Title ?? "").Trim().ToLowerInvariant()}{num}";
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
