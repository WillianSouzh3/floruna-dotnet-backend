using System.Security.Claims;
using Floruna.Api.Security;
using Floruna.Application.Loyalty;
using Floruna.Application.Messaging;
using Floruna.Domain.Identity;
using Floruna.Domain.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Floruna.Api.Controllers;

public sealed record UpdateLoyaltyProgramRequest(
    bool? Enabled, decimal? CashbackPercent, long? CashbackMinRedemption, int? CashbackExpiryDays, string? CouponStackingMode,
    int? MaxTotalDiscountPercent, int? MaxCashbackPercentOfOrder, bool? AllowCashbackWithCoupon, IReadOnlyList<LoyaltyTierDto>? Tiers);

public sealed record AdjustLoyaltyRequest(string? Kind, long? Amount, string? Reason, string? IdempotencyKey);

public sealed record PageParameters(int? Page, int? Limit)
{
    public int SafePage => Math.Max(1, Page ?? 1);

    public int SafeLimit => Math.Clamp(Limit ?? 20, 1, 100);
}

[Route(Contracts.ApiVersion.RoutePrefix + "/loyalty")]
public sealed class LoyaltyController(ISender sender) : ApiController
{
    // ── Painel da loja ──

    [HttpGet("program")]
    [Authorize(Roles = Roles.Admins)]
    [RequiresPlanFeature(PlanFeature.Loyalty)]
    public async Task<IActionResult> GetProgram(CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new GetLoyaltyProgramQuery(), cancellationToken), Ok);

    /// <summary>PUT parcial (como o Nest): campo ausente mantém o valor atual.</summary>
    [HttpPut("program")]
    [Authorize(Roles = Roles.Admins)]
    [RequiresPlanFeature(PlanFeature.Loyalty)]
    public async Task<IActionResult> UpdateProgram(UpdateLoyaltyProgramRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var current = await sender.Send(new GetLoyaltyProgramQuery(), cancellationToken);
        if (current.IsFailure)
        {
            return FromResult(current, Ok);
        }

        var p = current.Value.Program;
        var command = new UpdateLoyaltyProgramCommand(
            body.Enabled ?? p.Enabled, body.CashbackPercent ?? p.CashbackPercent, body.CashbackMinRedemption ?? p.CashbackMinRedemption,
            body.CashbackExpiryDays ?? p.CashbackExpiryDays, body.CouponStackingMode ?? p.CouponStackingMode,
            body.MaxTotalDiscountPercent ?? p.MaxTotalDiscountPercent, body.MaxCashbackPercentOfOrder ?? p.MaxCashbackPercentOfOrder,
            body.AllowCashbackWithCoupon ?? p.AllowCashbackWithCoupon, body.Tiers, CurrentActor());
        return FromResult(await sender.Send(command, cancellationToken), Ok);
    }

    [HttpGet("accounts")]
    [Authorize(Roles = Roles.Admins)]
    [RequiresPlanFeature(PlanFeature.Loyalty)]
    public async Task<IActionResult> Accounts([FromQuery] PageParameters query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return FromResult(await sender.Send(new ListLoyaltyAccountsQuery(query.SafePage, query.SafeLimit), cancellationToken), Ok);
    }

    /// <summary>Passivo em aberto: o lojista precisa ver a dívida antes de aumentar o percentual.</summary>
    [HttpGet("liability")]
    [Authorize(Roles = Roles.Admins)]
    [RequiresPlanFeature(PlanFeature.Loyalty)]
    public async Task<IActionResult> Liability(CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new GetLoyaltyLiabilityQuery(), cancellationToken), Ok);

    [HttpPost("accounts/{id:guid}/adjust")]
    [Authorize(Roles = Roles.Admins)]
    [RequiresPlanFeature(PlanFeature.Loyalty)]
    public async Task<IActionResult> Adjust(Guid id, AdjustLoyaltyRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new AdjustLoyaltyCommand(id, body.Kind, body.Amount ?? 0, body.Reason, body.IdempotencyKey, CurrentActor()), cancellationToken), Ok);
    }

    // ── Área do cliente: exige login (saldo por e-mail seria enumeração e gasto do saldo alheio) ──

    [HttpGet("me")]
    [Authorize(Roles = Roles.Customer)]
    public async Task<IActionResult> Me(CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new GetMyWalletQuery(User.FindFirstValue("email") ?? string.Empty), cancellationToken), Ok);

    [HttpGet("me/statement")]
    [Authorize(Roles = Roles.Customer)]
    public async Task<IActionResult> Statement([FromQuery] PageParameters query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return FromResult(await sender.Send(new GetMyStatementQuery(User.FindFirstValue("email") ?? string.Empty, query.SafePage, query.SafeLimit), cancellationToken), Ok);
    }
}
