using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

public class LockoutTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 0)]                    // first four attempts are free (typos happen)
    [InlineData(5, 5 * 60)]               // 5 minutes — then one attempt per cooldown
    [InlineData(6, 15 * 60)]              // 15 minutes
    [InlineData(7, 60 * 60)]              // 1 hour
    [InlineData(8, 5 * 60 * 60)]          // 5 hours
    [InlineData(9, 24 * 60 * 60)]         // 24 hours
    [InlineData(10, 7 * 24 * 60 * 60)]    // 7 days
    [InlineData(11, 30 * 24 * 60 * 60)]   // 30 days
    [InlineData(12, 30 * 24 * 60 * 60)]   // and 30 days for every further attempt
    [InlineData(50, 30 * 24 * 60 * 60)]
    public void PenaltyFor_Escalates(int fails, int expectedSeconds)
    {
        Assert.Equal(expectedSeconds, (int)Lockout.PenaltyFor(fails).TotalSeconds);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(1, 3)]                    // after the first wrong attempt: 3 more free
    [InlineData(2, 2)]
    [InlineData(3, 1)]
    [InlineData(4, 0)]
    [InlineData(5, 0)]
    public void AttemptsLeft_Counts_Down(int fails, int expected)
    {
        Assert.Equal(expected, Lockout.AttemptsLeft(fails));
    }
}
