using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

public class AutolockTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsDue_False_When_Minutes_Zero_Never()
    {
        // "Никогда" — even hours in background must not lock the UI path.
        Assert.False(Autolock.IsDue(0, T0, T0.AddHours(5)));
    }

    [Fact]
    public void IsDue_False_When_Not_Backgrounded()
    {
        Assert.False(Autolock.IsDue(1, Autolock.NotBackgrounded, T0));
    }

    [Fact]
    public void IsDue_False_Before_Deadline()
    {
        Assert.False(Autolock.IsDue(1, T0, T0.AddSeconds(59)));
    }

    [Fact]
    public void IsDue_True_At_And_After_Deadline()
    {
        Assert.True(Autolock.IsDue(1, T0, T0.AddMinutes(1)));
        Assert.True(Autolock.IsDue(1, T0, T0.AddMinutes(30)));
    }

    [Theory]
    [InlineData(5, 4, false)]
    [InlineData(5, 5, true)]
    [InlineData(5, 6, true)]
    public void IsDue_Respects_Configured_Minutes(int minutes, int elapsedMinutes, bool due)
    {
        Assert.Equal(due, Autolock.IsDue(minutes, T0, T0.AddMinutes(elapsedMinutes)));
    }

    [Fact]
    public void IsDueForCredentialFill_Caps_Never_At_AutofillBackgroundCap()
    {
        // Audit L9: UI «Никогда» still locks Autofill after the cap.
        Assert.False(Autolock.IsDueForCredentialFill(0, T0, T0.AddMinutes(Autolock.AutofillBackgroundCapMinutes - 1)));
        Assert.True(Autolock.IsDueForCredentialFill(0, T0, T0.AddMinutes(Autolock.AutofillBackgroundCapMinutes)));
        Assert.True(Autolock.IsDueForCredentialFill(0, T0, T0.AddHours(5)));
    }

    [Fact]
    public void IsDueForCredentialFill_Honours_Positive_User_Setting()
    {
        Assert.False(Autolock.IsDueForCredentialFill(5, T0, T0.AddMinutes(4)));
        Assert.True(Autolock.IsDueForCredentialFill(5, T0, T0.AddMinutes(5)));
    }

    [Fact]
    public void IsDueForCredentialFill_False_When_Not_Backgrounded()
    {
        Assert.False(Autolock.IsDueForCredentialFill(0, Autolock.NotBackgrounded, T0.AddHours(2)));
    }
}
