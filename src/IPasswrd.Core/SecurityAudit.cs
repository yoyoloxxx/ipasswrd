namespace IPasswrd.Core;

public enum Strength { Weak, Fair, Strong }

/// <summary>One issue found for one account.</summary>
public sealed record AuditFinding(string Id, string Title, string Reason);   // reason: "weak" | "reused"

/// <summary>Result of a local (no-network) password audit.</summary>
public sealed record AuditReport(
    IReadOnlyList<AuditFinding> Weak,
    IReadOnlyList<AuditFinding> Reused,
    int AccountsChecked,
    int Ok);

/// <summary>
/// Local password health checks: strength classification and reuse detection.
/// Everything runs offline; an online breach lookup (HIBP) is a later, opt-in add-on.
/// </summary>
public static class Auditor
{
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "123456", "123456789", "12345678", "1234567890", "qwerty", "qwerty123",
        "password", "password1", "111111", "000000", "iloveyou", "admin",
        "welcome", "abc123", "letmein", "monkey", "dragon", "1234567",
        "sunshine", "princess", "football", "qwertyuiop", "1q2w3e4r",
    };

    public static Strength Rate(string password)
    {
        if (string.IsNullOrEmpty(password)) return Strength.Weak;
        if (Common.Contains(password)) return Strength.Weak;

        int classes = 0;
        if (password.Any(char.IsLower)) classes++;
        if (password.Any(char.IsUpper)) classes++;
        if (password.Any(char.IsDigit)) classes++;
        if (password.Any(c => !char.IsLetterOrDigit(c))) classes++;

        int len = password.Length;
        if (len < 8 || classes <= 1) return Strength.Weak;
        if (len < 12 || classes == 2) return Strength.Fair;
        return Strength.Strong;
    }

    public static AuditReport Audit(IEnumerable<VaultEntry> entries)
    {
        var accounts = entries
            .Where(e => e.Item.Type == "account" && e.Item.Fields.ContainsKey("password")
                        && !string.IsNullOrEmpty(e.Item.Fields["password"]))
            .ToList();

        var weak = accounts
            .Where(e => Rate(e.Item.Fields["password"]) == Strength.Weak)
            .Select(e => new AuditFinding(e.Id, e.Item.Title, "weak"))
            .ToList();

        var reusedIds = new HashSet<string>();
        var reused = new List<AuditFinding>();
        foreach (var group in accounts.GroupBy(e => e.Item.Fields["password"]).Where(g => g.Count() >= 2))
            foreach (var e in group)
            {
                reused.Add(new AuditFinding(e.Id, e.Item.Title, "reused"));
                reusedIds.Add(e.Id);
            }

        int ok = accounts.Count(e =>
            Rate(e.Item.Fields["password"]) == Strength.Strong && !reusedIds.Contains(e.Id));

        return new AuditReport(weak, reused, accounts.Count, ok);
    }
}
