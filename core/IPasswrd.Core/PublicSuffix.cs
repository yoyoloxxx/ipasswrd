using System.Reflection;

namespace IPasswrd.Core;

/// <summary>
/// The official Public Suffix List (publicsuffix.org — ICANN + PRIVATE sections),
/// embedded as a resource. A "public suffix" is a level under which independent
/// parties register names; the registrable domain (eTLD+1) is the boundary that
/// stops one tenant's saved password from ever autofilling into a sibling tenant's
/// site (e.g. evil.duckdns.org must NOT resolve to the same site as alice.duckdns.org).
///
/// This replaces the former hand-curated subset, which omitted many multi-tenant
/// registries (duckdns.org, *.ngrok.io, trycloudflare.com, no-ip.org, hopto.org, …)
/// and therefore collapsed sibling tenants into one "site" — a cross-tenant credential
/// leak. Kept byte-for-byte in sync with the extension's psl.js (same data, same rule).
///
/// Algorithm (PSL spec): the prevailing rule is the longest that matches; "*.x" is a
/// wildcard (any one label + x is a suffix); "!x" is an exception (x is registrable,
/// not a suffix). The registrable domain is the public suffix plus one more label.
/// </summary>
public static class PublicSuffix
{
    private static readonly HashSet<string> Exact = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Wildcard = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Exception = new(StringComparer.Ordinal);

    static PublicSuffix() => Load();

    private static void Load()
    {
        try
        {
            Assembly asm = typeof(PublicSuffix).Assembly;
            string? name = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith("public_suffix_list.dat", StringComparison.OrdinalIgnoreCase));
            if (name is null) return;
            using Stream? s = asm.GetManifestResourceStream(name);
            if (s is null) return;
            using var r = new StreamReader(s, System.Text.Encoding.UTF8);
            string? line;
            while ((line = r.ReadLine()) is not null)
            {
                string l = line.Trim();
                if (l.Length == 0 || l.StartsWith("//", StringComparison.Ordinal)) continue;
                int sp = l.IndexOf(' ');
                if (sp > 0) l = l[..sp];                       // the rule is the first whitespace-delimited token
                l = l.ToLowerInvariant();
                if (l.StartsWith('!')) Exception.Add(l[1..]);
                else if (l.StartsWith("*.", StringComparison.Ordinal)) Wildcard.Add(l[2..]);
                else Exact.Add(l);
            }
        }
        catch { /* resource missing/unreadable — falls back to "last label is the TLD", never throws on lookup */ }
    }

    /// <summary>Label count of the public suffix of a host given as its label array.</summary>
    private static int PsLabels(string[] labels)
    {
        int n = labels.Length;
        for (int i = 0; i < n; i++)                            // exceptions win outright
            if (Exception.Contains(string.Join('.', labels[i..]))) return n - (i + 1);
        int best = 0;
        for (int i = 0; i < n; i++)
            if (Exact.Contains(string.Join('.', labels[i..]))) { int len = n - i; if (len > best) best = len; }
        for (int i = 1; i < n; i++)                            // "*.x": one label before x, so i starts at 1
            if (Wildcard.Contains(string.Join('.', labels[i..]))) { int len = (n - i) + 1; if (len > best) best = len; }
        return best == 0 ? 1 : best;                           // default rule "*": the rightmost label is the TLD
    }

    /// <summary>Registrable domain (eTLD+1) of a bare host. Empty/IP returned as-is; a host that
    /// IS itself a public suffix is returned unchanged (never collapsed with its siblings).</summary>
    public static string RegistrableDomain(string? host)
    {
        string h = (host ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        if (h.Length == 0) return "";
        if (System.Net.IPAddress.TryParse(h, out _)) return h;
        var labels = h.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length <= 1) return h;
        int ps = PsLabels(labels);
        if (labels.Length <= ps) return h;                     // host is a public suffix on its own
        return string.Join('.', labels[^(ps + 1)..]);
    }

    /// <summary>True if <paramref name="domain"/> is itself a public suffix (a bare TLD or a listed
    /// multi-tenant registration point) — i.e. NOT a registrable site on its own.</summary>
    public static bool IsPublicSuffix(string? domain)
    {
        string d = (domain ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        if (d.Length == 0) return true;
        if (d.IndexOf('.') < 0) return true;                   // bare TLD ("com", "io", "ru")
        var labels = d.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return labels.Length <= PsLabels(labels);
    }
}
