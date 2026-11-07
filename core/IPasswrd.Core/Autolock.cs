namespace IPasswrd.Core;

/// <summary>
/// Pure timing check for the mobile autolock setting. The host stores when the UI
/// went to the background; Autofill / Accessibility must re-evaluate this before
/// serving credentials, because they run while the MAUI window stays stopped and
/// never hit the resume path that historically performed the lock.
/// </summary>
public static class Autolock
{
    /// <summary>
    /// Sentinel meaning "UI is foreground / not tracking idle" — same value
    /// <c>AppState</c> uses for <c>_backgroundedAt</c> after resume.
    /// </summary>
    public static readonly DateTimeOffset NotBackgrounded = DateTimeOffset.MaxValue;

    /// <summary>
    /// Hard ceiling for Autofill / Accessibility when the UI setting is «Никогда» (0).
    /// The main app may stay unlocked for convenience; credential-serving paths must not
    /// keep the in-memory vault forever while the process sits in the background (audit L9).
    /// Matches the longest explicit UI option (1 hour).
    /// </summary>
    public const int AutofillBackgroundCapMinutes = 60;

    /// <param name="minutes">User setting; 0 = never auto-lock (UI / resume path).</param>
    /// <param name="backgroundedAt">Utc time when the UI stopped, or <see cref="NotBackgrounded"/>.</param>
    /// <param name="utcNow">Current utc time (injectable for tests).</param>
    public static bool IsDue(int minutes, DateTimeOffset backgroundedAt, DateTimeOffset utcNow)
    {
        if (minutes <= 0) return false;
        if (backgroundedAt == NotBackgrounded) return false;
        return utcNow - backgroundedAt >= TimeSpan.FromMinutes(minutes);
    }

    /// <summary>
    /// Autofill / Accessibility idle check. Honours the user setting when it is a positive
    /// number of minutes; when the UI says «never», still locks after
    /// <see cref="AutofillBackgroundCapMinutes"/> (audit L9).
    /// </summary>
    public static bool IsDueForCredentialFill(int minutes, DateTimeOffset backgroundedAt, DateTimeOffset utcNow)
    {
        int effective = minutes <= 0 ? AutofillBackgroundCapMinutes : minutes;
        return IsDue(effective, backgroundedAt, utcNow);
    }
}
