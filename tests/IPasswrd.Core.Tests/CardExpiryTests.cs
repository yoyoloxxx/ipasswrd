using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

// Истёкшая карта в сейфе выглядит точно так же, как рабочая, и обнаруживается на кассе.
// Разбор срока — единственное, что отличает одно от другого, поэтому он обязан переваривать
// всё, что человек мог написать руками.
public class CardExpiryTests
{
    private static readonly DateOnly Today = new(2026, 8, 7);

    [Theory] // 1
    [InlineData("09/29")]
    [InlineData("9/29")]
    [InlineData("09/2029")]
    [InlineData("09.29")]
    [InlineData("09-2029")]
    [InlineData(" 09 / 29 ")]
    [InlineData("0929")]
    public void EveryWayPeopleWriteItReadsTheSame(string written)
    {
        Assert.Equal(new DateOnly(2029, 9, 30), CardExpiry.LastDay(written));
    }

    [Fact] // 2
    public void CardWorksThroughTheEndOfItsMonth()
    {
        // На карте написано 08/26 — она действует весь август, а не до его начала.
        Assert.Equal(CardExpiry.Status.Soon, CardExpiry.Check("08/26", Today));
        Assert.Equal(CardExpiry.Status.Expired, CardExpiry.Check("08/26", new DateOnly(2026, 9, 1)));
        Assert.Equal(CardExpiry.Status.Soon, CardExpiry.Check("08/26", new DateOnly(2026, 8, 31)));
    }

    [Fact] // 3
    public void ExpiredIsExpired()
    {
        Assert.Equal(CardExpiry.Status.Expired, CardExpiry.Check("01/24", Today));
    }

    [Fact] // 4
    public void WarningComesEarlyEnoughToOrderANewOne()
    {
        // 60 дней: перевыпуск занимает недели, предупреждение накануне бесполезно.
        Assert.Equal(CardExpiry.Status.Soon, CardExpiry.Check("09/26", Today));
        Assert.Equal(CardExpiry.Status.Ok, CardExpiry.Check("12/26", Today));
    }

    [Theory] // 5
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("бессрочно")]
    [InlineData("13/29")]     // такого месяца нет
    [InlineData("00/29")]
    [InlineData("09/1899")]
    public void UnparsableStaysSilentInsteadOfCryingWolf(string written)
    {
        // Непонятный срок — не повод объявлять карту просроченной: человек её просто не заполнил.
        Assert.Null(CardExpiry.LastDay(written));
        Assert.Equal(CardExpiry.Status.Unknown, CardExpiry.Check(written, Today));
    }

    [Fact] // 6
    public void FormatBringsEverythingToOneShape()
    {
        Assert.Equal("09/2029", CardExpiry.Format("9/29"));
        Assert.Equal("09/2029", CardExpiry.Format("09.2029"));
        // Неразобранное показываем как есть — врать про срок хуже, чем показать сырое.
        Assert.Equal("бессрочно", CardExpiry.Format("бессрочно"));
    }
}
