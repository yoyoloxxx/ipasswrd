namespace IPasswrd.Core;

/// <summary>
/// Escalating lockout after repeated wrong master-password attempts. The first
/// FOUR wrong attempts are free (typos happen); the 5th and every following one
/// lock the vault for an escalating period: 5m, 15m, 1h, 5h, 24h, 7d, 30d, then
/// 30d for every further wrong attempt — i.e. one attempt per cooldown. This is
/// a UI-level deterrent; the real brute-force protection is the slow Argon2id
/// key derivation.
/// </summary>
public static class Lockout
{
    public const int FreeAttempts = 4;

    private static readonly TimeSpan[] Penalties =
    {
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(5),
        TimeSpan.FromHours(24),
        TimeSpan.FromDays(7),
        TimeSpan.FromDays(30),
    };

    /// <summary>Lockout to apply after <paramref name="consecutiveFails"/> wrong attempts (Zero = none).</summary>
    public static TimeSpan PenaltyFor(int consecutiveFails)
    {
        if (consecutiveFails <= FreeAttempts) return TimeSpan.Zero;
        int idx = Math.Min(consecutiveFails - FreeAttempts - 1, Penalties.Length - 1);
        return Penalties[idx];
    }

    /// <summary>Free attempts left before locking begins (for a friendly hint).</summary>
    public static int AttemptsLeft(int consecutiveFails) => Math.Max(0, FreeAttempts - consecutiveFails);
}
