using Floruna.Domain.Catalog;
using Floruna.Domain.Ordering;
using Floruna.Domain.Substitutions;
using Floruna.SharedKernel;

namespace Floruna.Domain.UnitTests.Substitutions;

public sealed class SubstitutionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly SubstitutionPolicy Policy = SubstitutionPolicy.DefaultFor(TenantId.New()); // 10%, 24 h

    private static Result<(SubstitutionProposal Proposal, string? Token)> Propose(SubstitutionMode mode, long originalUnit = 10_000, long substituteUnit = 10_500) =>
        SubstitutionProposal.Propose(new ProposalInput(TenantId.New(), new OrderId(Guid.NewGuid()), 0,
            new ProductId(Guid.NewGuid()), "Rosas vermelhas", originalUnit, new ProductId(Guid.NewGuid()), "Rosas cor-de-rosa", substituteUnit,
            Quantity: 2, mode, "Faltou rosa vermelha", "chave-1"), Policy, Now);

    [Fact]
    public void Exact_order_refuses_any_substitution()
    {
        Propose(SubstitutionMode.Exact).Error.ShouldBe(SubstitutionErrors.ExactOrder);
    }

    [Fact]
    public void Preapproved_within_tolerance_is_born_approved_without_a_link()
    {
        var (proposal, token) = Propose(SubstitutionMode.Preapproved, 10_000, 10_500).Value; // +5%

        proposal.State.ShouldBe(ProposalState.Approved);
        proposal.DecidedBy.ShouldBe("system");
        token.ShouldBeNull();
    }

    [Theory]
    [InlineData(10_000, 11_500)] // 15% mais caro: deixa de ser "equivalente"
    [InlineData(10_000, 8_000)]  // 20% mais barato: rebaixa o arranjo, o cliente decide
    public void Preapproval_stops_at_the_policy_tolerance_in_both_directions(long original, long substitute)
    {
        var (proposal, token) = Propose(SubstitutionMode.Preapproved, original, substitute).Value;

        proposal.State.ShouldBe(ProposalState.Proposed);
        token.ShouldNotBeNull();
        proposal.ExpiresAt.ShouldBe(Now.AddHours(24));
    }

    [Fact]
    public void Ask_me_always_asks_and_only_the_customer_can_approve()
    {
        var (proposal, token) = Propose(SubstitutionMode.AskMe, 10_000, 10_000).Value;

        proposal.State.ShouldBe(ProposalState.Proposed);
        proposal.DecideByOperator(approve: true, "gerente", "cliente ligou", Now).Error.ShouldBe(SubstitutionErrors.CustomerMustApprove);
        proposal.DecideByCustomer(token!, approve: true, Now.AddHours(1)).IsSuccess.ShouldBeTrue();
        proposal.State.ShouldBe(ProposalState.Approved);
        proposal.DecidedBy.ShouldBe("customer");
    }

    [Fact]
    public void Late_or_forged_answers_do_not_count()
    {
        var (proposal, token) = Propose(SubstitutionMode.AskMe).Value;

        proposal.DecideByCustomer("token-forjado-0123456789", approve: true, Now).Error.ShouldBe(SubstitutionErrors.InvalidLink);
        proposal.DecideByCustomer(token!, approve: true, Now.AddHours(25)).Error.ShouldBe(SubstitutionErrors.Expired);
        proposal.State.ShouldBe(ProposalState.Expired); // sem resposta no prazo, o pedido segue como estava
    }

    [Fact]
    public void Nothing_is_applied_without_approval_and_applying_happens_once()
    {
        var (proposal, token) = Propose(SubstitutionMode.AskMe).Value;
        proposal.MarkApplied(Now).Error.ShouldBe(SubstitutionErrors.NotApproved);

        proposal.DecideByCustomer(token!, approve: true, Now);
        proposal.MarkApplied(Now).IsSuccess.ShouldBeTrue();

        proposal.State.ShouldBe(ProposalState.Applied);
        proposal.MarkApplied(Now).Error.ShouldBe(SubstitutionErrors.NotApproved);
    }

    [Fact]
    public void Operator_can_always_register_a_refusal()
    {
        var (proposal, _) = Propose(SubstitutionMode.AskMe).Value;

        proposal.DecideByOperator(approve: false, "gerente", "cliente recusou por telefone", Now).IsSuccess.ShouldBeTrue();
        proposal.State.ShouldBe(ProposalState.Rejected);
    }
}
