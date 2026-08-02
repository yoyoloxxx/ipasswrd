using System.Text;
using IPasswrd.Core;
using IPasswrd.Core.Import;

// Privacy-preserving import + duplication analysis. Prints ONLY counts and base-domain
// names. Never prints a login, password, url path or note body.
Console.OutputEncoding = Encoding.UTF8;

string path = args.Length > 0 ? args[0] : "";
if (!File.Exists(path)) { Console.WriteLine("FILE NOT FOUND: " + path); return; }

string content = File.ReadAllText(path);
var items = Importer.Parse(content);
var accounts = items.Where(i => i.Type == "account").ToList();

Console.WriteLine("== PARSE ==");
Console.WriteLine("total items: " + items.Count + " | accounts: " + accounts.Count +
                  " | notes: " + items.Count(i => i.Type == "note"));

// The ACTUAL app rule: base-domain + login + password (IPasswrd.Core.Dedup).
var collapsed = Dedup.Collapse(items);
Console.WriteLine();
Console.WriteLine("== Dedup.Collapse (the real app rule) ==");
Console.WriteLine("items AFTER collapse: " + collapsed.Count +
                  " | accounts: " + collapsed.Count(i => i.Type == "account") +
                  " | notes: " + collapsed.Count(i => i.Type == "note"));
Console.WriteLine("would remove: " + (items.Count - collapsed.Count));

Console.WriteLine();
Console.WriteLine("== top base-domains by entry count (names only) ==");
foreach (var g in accounts.GroupBy(a => Dedup.RegistrableDomain(a.Fields.TryGetValue("url", out var u) ? u : ""))
                          .Select(g => new { g.Key, N = g.Count() })
                          .OrderByDescending(x => x.N).Take(10))
    Console.WriteLine("  " + (string.IsNullOrEmpty(g.Key) ? "(no url)" : g.Key) + ": " + g.N);
