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

    /// <param name="minutes">User setting; 0 = never auto-lock.</param>
    /// <param name="backgroundedAt">Utc time when the UI stopped, or <see cref="NotBackgrounded"/>.</param>
    /// <param name="utcNow">Current utc time (injectable for tests).</param>
    public static bool IsDue(int minutes, DateTimeOffset backgroundedAt, DateTimeOffset utcNow)
    {
        if (minutes <= 0) return false;
        if (backgroundedAt == NotBackgrounded) return false;
        return utcNow - backgroundedAt >= TimeSpan.FromMinutes(minutes);
    }
}
