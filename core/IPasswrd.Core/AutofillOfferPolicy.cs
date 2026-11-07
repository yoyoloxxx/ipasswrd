namespace IPasswrd.Core;

/// <summary>
/// Shared Autofill / Accessibility offer rules. Platform UIs (Android Autofill dropdown,
/// a11y overlay, iOS credential list) must filter through this so "show all accounts"
/// never becomes the silent default — unmatched rows are phishing surface (audit H3).
/// </summary>
public static class AutofillOfferPolicy
{
    /// <summary>
    /// Whether a ranked candidate with the given match <paramref name="score"/> belongs in the
    /// default fill list. Score &gt; 0 means a confident domain/package match; unmatched rows
    /// appear only after the user confirms an explicit "show all" warning.
    /// </summary>
    public static bool IncludeInOffers(int score, bool showAllConfirmed)
        => showAllConfirmed || score > 0;

    /// <summary>Filter a ranked sequence with <see cref="IncludeInOffers"/>.</summary>
    public static IEnumerable<T> Filter<T>(IEnumerable<T> ranked, Func<T, int> scoreOf, bool showAllConfirmed)
    {
        foreach (T item in ranked)
        {
            if (IncludeInOffers(scoreOf(item), showAllConfirmed))
                yield return item;
        }
    }
}
