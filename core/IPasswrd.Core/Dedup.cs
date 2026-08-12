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
    // ---- Public Suffix List (curated) ----
    //
    // The registrable domain is "longest matching public suffix + one more label".
    // A "public suffix" is a level under which independent parties register names, so
    // two names below it are DIFFERENT sites. This matters for security: if we treated
    // a multi-tenant suffix like "github.io" as an ordinary 2nd-level domain, every
    // "*.github.io" tenant would collapse to one "site" and IPasswrd could hand one
    // tenant's saved password to another. So the set below MUST include the private,
    // multi-tenant registries (github.io, *.web.app, herokuapp.com, …), not just the
    // classic ICANN ccTLD second-levels. Longest suffix wins, so listing both
    // "amazonaws.com" and "s3.amazonaws.com" keeps each S3 bucket a separate site.
    //
    // This is a curated subset. For exhaustive coverage, replace it with the official
    // Public Suffix List (https://publicsuffix.org) loaded as an embedded resource;
    // IsPublicSuffix/RegistrableDomain already implement the correct longest-match rule.
    private static readonly HashSet<string> PublicSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        // ---- ICANN: multi-label ccTLD registration points ----
        "co.uk","org.uk","gov.uk","ac.uk","me.uk","ltd.uk","plc.uk","net.uk","sch.uk","nhs.uk","police.uk",
        "com.au","net.au","org.au","edu.au","gov.au","id.au","asn.au",
        "co.jp","or.jp","ne.jp","ac.jp","go.jp","gr.jp","ed.jp","lg.jp","ad.jp",
        "co.kr","or.kr","ne.kr","re.kr","pe.kr","go.kr","ac.kr","hs.kr","ms.kr",
        "com.br","net.br","org.br","gov.br","edu.br","art.br","blog.br",
        "com.cn","net.cn","org.cn","gov.cn","edu.cn","ac.cn",
        "com.tw","net.tw","org.tw","idv.tw","gov.tw","edu.tw",
        "com.hk","net.hk","org.hk","edu.hk","gov.hk","idv.hk",
        "com.sg","net.sg","org.sg","edu.sg","gov.sg","per.sg",
        "com.my","net.my","org.my","gov.my","edu.my",
        "co.in","net.in","org.in","gen.in","firm.in","ind.in","gov.in","ac.in","edu.in",
        "com.tr","net.tr","org.tr","gov.tr","edu.tr","bel.tr",
        "com.ua","net.ua","org.ua","in.ua","kiev.ua",
        "com.ru","net.ru","org.ru","msk.ru","spb.ru",           // legacy sub-zones; .ru itself is registrable
        "com.mx","com.ar","com.co","net.co","nom.co","com.pe","com.ve","com.ec","com.uy","com.py","com.bo","com.cl",
        "co.il","org.il","net.il","ac.il","gov.il","muni.il",
        "co.za","org.za","net.za","web.za","gov.za","ac.za",
        "co.nz","net.nz","org.nz","govt.nz","ac.nz","geek.nz","school.nz",
        "co.th","in.th","ac.th","go.th","or.th","net.th",
        "com.vn","net.vn","org.vn","gov.vn","edu.vn",
        "com.ph","net.ph","org.ph","gov.ph",
        "com.pl","net.pl","org.pl","gov.pl","waw.pl","edu.pl",
        "com.pt","com.es","org.es","com.eg","com.sa","com.ng","com.gh","com.kw","com.qa","com.bh","com.pk","com.bd",
        "co.id","or.id","web.id","ac.id","go.id","my.id","biz.id",
        "co.ke","co.tz","co.ug","co.zw","co.mz",
        // ---- Private multi-tenant registries (the ones that cause cross-tenant leaks) ----
        "github.io","githubusercontent.com","gitlab.io","pages.dev","workers.dev","r2.dev",
        "web.app","firebaseapp.com","appspot.com","cloudfunctions.net","run.app",
        "vercel.app","now.sh","netlify.app","netlify.com","onrender.com","render.com",
        "herokuapp.com","herokudns.com","fly.dev","railway.app","up.railway.app",
        "azurewebsites.net","azurestaticapps.net","cloudapp.net","cloudapp.azure.com","trafficmanager.net",
        "blob.core.windows.net","web.core.windows.net","azureedge.net",
        "amazonaws.com","s3.amazonaws.com","s3-website.amazonaws.com","elasticbeanstalk.com",
        "cloudfront.net","amplifyapp.com","awsapprunner.com","execute-api.amazonaws.com",
        "blogspot.com","wordpress.com","tumblr.com","weebly.com","wixsite.com","editorx.io",
        "myshopify.com","squarespace.com","webflow.io","framer.app","framer.website","framer.media",
        "glitch.me","repl.co","replit.dev","replit.app","surge.sh","bubbleapps.io","softr.app",
        "translate.goog","googleusercontent.com",
        "readthedocs.io","gitbook.io","notion.site","super.site","carrd.co","substack.com",
        "sharepoint.com","atlassian.net","zendesk.com","freshdesk.com","myjetbrains.com","statuspage.io",
        "pythonanywhere.com","codeberg.page","stackblitz.io","vercel.sh","deno.dev",
    };

    /// <summary>True if <paramref name="domain"/> is itself a public suffix (a bare TLD, or a
    /// listed multi-tenant registration point) — i.e. NOT a registrable site on its own.</summary>
    public static bool IsPublicSuffix(string? domain)
    {
        string d = (domain ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        if (d.Length == 0) return true;
        if (d.IndexOf('.') < 0) return true;                    // bare TLD ("com", "io", "ru")
        return PublicSuffixes.Contains(d);
    }

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

    /// <summary>Registrable domain (eTLD+1) of a URL: the longest matching public suffix plus one
    /// more label. accounts.google.com → google.com; victim.github.io → victim.github.io (github.io
    /// is a public suffix, so tenants do NOT collapse together).</summary>
    public static string RegistrableDomain(string? url)
    {
        string host = Host(url);
        if (host.Length == 0) return "";
        if (System.Net.IPAddress.TryParse(host, out _)) return host;   // IP address (10.90.90.2): never trim
        var l = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (l.Length <= 1) return host;

        // Longest public suffix, measured in labels. Default: the last label is the TLD.
        int suffixLabels = 1;
        for (int i = l.Length - 1; i >= 1; i--)
        {
            string cand = string.Join('.', l[i..]);
            if (PublicSuffixes.Contains(cand)) suffixLabels = l.Length - i;
        }
        int take = suffixLabels + 1;
        if (take > l.Length) return host;   // host IS a public suffix (e.g. "github.io") → keep as-is, never collapse
        return string.Join('.', l[^take..]);
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
