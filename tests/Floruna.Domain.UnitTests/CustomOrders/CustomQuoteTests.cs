using Floruna.Domain.CustomOrders;
using Floruna.Domain.Ordering;
using Floruna.SharedKernel;

namespace Floruna.Domain.UnitTests.CustomOrders;

public sealed class CustomQuoteTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<QuoteLine> Lines =
    [
        new("Cesta de vime grande", 1, 12_000),
        new("Rosas vermelhas", 12, 800),
    ];

    private static CustomQuote Draft(int version = 1) => CustomQuote.Draft(TenantId.New(), Guid.NewGuid(), version, Lines, "Entrega inclusa", Now).Value;

    private static (CustomQuote Quote, string Token) Published()
    {
        var quote = Draft();
        return (quote, quote.Publish(7, Now).Value);
    }

    [Fact]
    public void Draft_total_is_computed_from_the_lines()
    {
        Draft().Total.ShouldBe(12_000 + 12 * 800);
    }

    [Fact]
    public void Draft_needs_at_least_one_line()
    {
        CustomQuote.Draft(TenantId.New(), Guid.NewGuid(), 1, [], "", Now).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Published_quote_cannot_be_edited_only_superseded_by_a_new_version()
    {
        var (quote, _) = Published();

        quote.UpdateDraft([new QuoteLine("Outra coisa", 1, 1)], "", Now).Error.ShouldBe(QuoteErrors.NotEditable);
        quote.State.ShouldBe(QuoteState.Published);
        quote.ValidUntil.ShouldBe(Now.AddDays(7));
    }

    [Fact]
    public void Customer_accepts_with_the_token_from_the_link()
    {
        var (quote, token) = Published();

        quote.Decide("token-errado-0123456789", accept: true, "", Now).Error.ShouldBe(QuoteErrors.InvalidLink);
        quote.Decide(token, accept: true, "", Now.AddDays(1)).IsSuccess.ShouldBeTrue();

        quote.State.ShouldBe(QuoteState.Accepted);
        quote.Decide(token, accept: false, "", Now.AddDays(1)).Error.ShouldBe(QuoteErrors.AlreadyAnswered);
    }

    [Fact]
    public void Answer_after_the_validity_expires_the_quote_instead_of_counting()
    {
        var (quote, token) = Published();

        quote.Decide(token, accept: true, "", Now.AddDays(8)).Error.ShouldBe(QuoteErrors.Expired);

        quote.State.ShouldBe(QuoteState.Expired); // e fica expirado: o preço antigo não volta
    }

    [Fact]
    public void Only_an_accepted_quote_converts_and_only_once()
    {
        var (quote, token) = Published();
        quote.Convert(new OrderId(Guid.NewGuid()), Now).Error.ShouldBe(QuoteErrors.NotAccepted);

        quote.Decide(token, accept: true, "", Now);
        var order = new OrderId(Guid.NewGuid());
        quote.Convert(order, Now).IsSuccess.ShouldBeTrue();

        quote.State.ShouldBe(QuoteState.Converted);
        quote.ConvertedOrderId.ShouldBe(order.Value);
        quote.Convert(new OrderId(Guid.NewGuid()), Now).Error.ShouldBe(QuoteErrors.AlreadyConverted);
    }

    [Fact]
    public void Superseded_published_quote_can_no_longer_be_accepted()
    {
        var (quote, token) = Published();

        quote.Supersede(newVersion: 2, Now);

        quote.State.ShouldBe(QuoteState.Expired);
        quote.DecisionReason.ShouldContain("versão 2");
        quote.Decide(token, accept: true, "", Now).Error.ShouldBe(QuoteErrors.AlreadyAnswered);
    }

    [Fact]
    public void Token_check_is_by_hash_and_never_stores_the_token()
    {
        var (quote, token) = Published();

        quote.ResponseTokenHash.ShouldNotBe(token);
        quote.ResponseTokenHash.Length.ShouldBe(64);
        quote.CanBeReadWith(token).ShouldBeTrue();
        quote.CanBeReadWith(token + "x").ShouldBeFalse();
    }
}
