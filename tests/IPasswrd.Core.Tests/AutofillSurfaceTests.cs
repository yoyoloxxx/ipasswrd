using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

/// <summary>
/// CI pins for Autofill / Accessibility surfaces that historically lacked automated coverage
/// (audit M16). Platform services cannot run here; the pure rules they must call can.
/// </summary>
public class AutofillSurfaceTests
{
    private static readonly DateTimeOffset BgAt = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);

    // ---- C2 contract: fill paths must re-evaluate Autolock while the UI stays Stopped ----

    [Fact]
    public void Autolock_Due_After_Configured_Idle_While_Ui_Stays_Backgrounded()
    {
        // Models Autofill/a11y calling Autolock.IsDue hours after OnBackgrounded, with no OnResumed.
        Assert.True(Autolock.IsDue(1, BgAt, BgAt.AddHours(3)));
    }

    [Fact]
    public void Autolock_NotDue_When_Never_Or_Foreground()
    {
        Assert.False(Autolock.IsDue(0, BgAt, BgAt.AddDays(1)));                 // "never"
        Assert.False(Autolock.IsDue(5, Autolock.NotBackgrounded, BgAt.AddHours(1)));
    }

    // ---- H3 contract: default offers are matches-only ----

    [Theory]
    [InlineData(100, false, true)]
    [InlineData(90, false, true)]
    [InlineData(1, false, true)]
    [InlineData(0, false, false)]
    [InlineData(-1, false, false)]
    [InlineData(0, true, true)]
    [InlineData(-1, true, true)]
    public void OfferPolicy_IncludeInOffers(int score, bool showAll, bool expect)
        => Assert.Equal(expect, AutofillOfferPolicy.IncludeInOffers(score, showAll));

    [Fact]
    public void OfferPolicy_Filter_Default_Drops_Unmatched()
    {
        var ranked = new[] { ("bank", 100), ("other", 0), ("pkg", 90) };
        var shown = AutofillOfferPolicy.Filter(ranked, x => x.Item2, showAllConfirmed: false)
            .Select(x => x.Item1)
            .ToList();
        Assert.Equal(new[] { "bank", "pkg" }, shown);
    }

    [Fact]
    public void OfferPolicy_Filter_ShowAll_Keeps_Unmatched()
    {
        var ranked = new[] { ("bank", 100), ("other", 0) };
        var shown = AutofillOfferPolicy.Filter(ranked, x => x.Item2, showAllConfirmed: true)
            .Select(x => x.Item1)
            .ToList();
        Assert.Equal(new[] { "bank", "other" }, shown);
    }

    // ---- C1 unlock-pull: Decide never implies overwrite-before-open ----

    [Fact]
    public void UnlockPull_SameLineage_AlwaysRetains_Peer_For_Merge()
    {
        const string pw = "correct horse battery staple";
        var fast = KdfConfig.Fast;
        var seed = Vault.Create(pw, fast);
        seed.Add(new VaultItem
        {
            Type = "account",
            Title = "Seed",
            Fields = new() { ["username"] = "u", ["password"] = "k4!Vr#92mQzL&wPn", ["url"] = "example.com" },
        });
        byte[] blob = seed.Serialize();
        var localV = Vault.Unlock(blob, pw);
        var remoteV = Vault.Unlock(blob, pw);
        Thread.Sleep(1100);
        string localId = localV.Add(new VaultItem
        {
            Type = "account",
            Title = "offline-only",
            Fields = new() { ["username"] = "u", ["password"] = "k4!Vr#92mQzL&wPn", ["url"] = "example.com" },
        });
        Thread.Sleep(1100);
        remoteV.Add(new VaultItem
        {
            Type = "account",
            Title = "remote-only",
            Fields = new() { ["username"] = "u", ["password"] = "k4!Vr#92mQzL&wPn", ["url"] = "example.com" },
        });

        var d = VaultUnlockSource.Decide(localV.Serialize(), remoteV.Serialize());

        Assert.NotNull(d.OtherBlobToMergeAfterUnlock);
        var opened = Vault.Unlock(d.UnlockBlob, pw);
        opened.MergeFrom(d.OtherBlobToMergeAfterUnlock!);
        Assert.Equal("offline-only", opened.Get(localId).Title);
        Assert.Contains(opened.Items(), e => e.Item.Title == "remote-only");
    }
}
