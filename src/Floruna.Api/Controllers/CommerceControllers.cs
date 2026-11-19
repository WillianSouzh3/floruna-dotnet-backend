using System.Security.Claims;
using Floruna.Api.Security;
using Floruna.Application.Catalog;
using Floruna.Application.Delivery;
using Floruna.Application.Messaging;
using Floruna.Application.Ordering;
using Floruna.Application.Outbox;
using Floruna.Application.Payments;
using Floruna.Application.Promotions;
using Floruna.Domain.Identity;
using Floruna.Domain.Ordering;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Floruna.Api.Controllers;

public sealed record OrderItemRequest(string? ProductId, int Quantity, string? VariantLabel);

public sealed record CheckoutRequest(
    string? IdempotencyKey, string? CustomerName, string? CustomerPhone, string? CustomerEmail, IReadOnlyList<OrderItemRequest>? Items,
    string? DeliveryMethod, DeliveryAddress? Address, string? DeliveryDate, string? DeliveryTimeWindow, GiftCard? GiftCard,
    string? CouponCode, string? SubstitutionMode, long? UseCashback = null)
{
    public CheckoutInput ToInput() => new(IdempotencyKey, CustomerName, CustomerPhone, CustomerEmail,
        Items?.Select(i => new OrderItemInput(i.ProductId, i.Quantity, i.VariantLabel)).ToList(),
        DeliveryMethod, Address is null ? null : Address with
        {
            Complement = Address.Complement ?? string.Empty,
            DeliveryNotes = Address.DeliveryNotes ?? string.Empty,
        },
        DeliveryDate, DeliveryTimeWindow,
        GiftCard is null ? null : new GiftCard(GiftCard.Message, GiftCard.From ?? string.Empty, GiftCard.To ?? string.Empty),
        CouponCode, SubstitutionMode, UseCashback);
}

public sealed record UpdateOrderStatusRequest(string? Status, string? ExpectedState, string? IdempotencyKey, string? Reason);

public sealed record OrderQueryParameters(string? Status, string? Search, long? MinTotal, long? MaxTotal, int? Page, int? Limit);

/// <summary>Pedidos (<c>/orders</c>), contrato do Nest.</summary>
[Route(Contracts.ApiVersion.RoutePrefix + "/orders")]
public sealed class OrdersController(ISender sender) : ApiController
{
    /// <summary>Checkout. Login OPCIONAL: com token de cliente, o pedido fica vinculado à conta.</summary>
    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingSetup.CheckoutPolicy)]
    [ProducesResponseType<OrderEnvelope>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CheckoutRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var isCustomer = User.FindFirstValue("role") == Roles.Customer;
        var customerId = isCustomer && Guid.TryParse(User.FindFirstValue("sub"), out var id) ? id : (Guid?)null;
        var sessionEmail = customerId is null ? null : User.FindFirstValue("email");
        var result = await sender.Send(new PlaceOrderCommand(body.ToInput(), customerId, sessionEmail), cancellationToken);
        return FromResult(result, order => StatusCode(StatusCodes.Status201Created, order));
    }

    [HttpGet]
    [Authorize(Roles = Roles.Admins)]
    [ProducesResponseType<OrderPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] OrderQueryParameters query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var filter = new OrderListFilter(query.Status, query.Search, query.MinTotal, query.MaxTotal,
            Math.Max(1, query.Page ?? 1), Math.Clamp(query.Limit ?? 20, 1, 100));
        return FromResult(await sender.Send(new ListOrdersQuery(filter), cancellationToken), Ok);
    }

    [HttpGet("me")]
    [Authorize(Roles = Roles.Customer)]
    public async Task<IActionResult> Mine([FromQuery] int? page, [FromQuery] int? limit, CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new ListMyOrdersQuery(CurrentUserId, Math.Max(1, page ?? 1), Math.Clamp(limit ?? 20, 1, 100)), cancellationToken), Ok);

    /// <summary>Por <c>?token=</c> (página de sucesso), pelo cliente dono ou pela equipe.</summary>
    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    [ProducesResponseType<OrderEnvelope>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, [FromQuery] string? token, CancellationToken cancellationToken)
    {
        var userId = Guid.TryParse(User.FindFirstValue("sub"), out var sub) ? sub : (Guid?)null;
        return FromResult(await sender.Send(new GetOrderQuery(id, token, userId, User.FindFirstValue("role")), cancellationToken), Ok);
    }

    /// <summary>Muda o status. <c>expectedState</c> + <c>idempotencyKey</c> = compare-and-swap seguro contra duplo clique.</summary>
    [HttpPatch("{id:guid}/status")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> UpdateStatus(Guid id, UpdateOrderStatusRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var actor = CurrentActor();
        var command = new ChangeOrderStatusCommand(id, body.Status ?? string.Empty, body.ExpectedState, body.IdempotencyKey, body.Reason, actor.UserId, actor.Role);
        return FromResult(await sender.Send(command, cancellationToken), Ok);
    }

    /// <summary>
    /// Troca de item. Pré-aprovado dentro da tolerância: aplica na hora (200). Senão: proposta com link para o
    /// cliente responder (202, com <c>consentToken</c> uma única vez). Pedido "exato": 409.
    /// </summary>
    [HttpPost("{id:guid}/resolutions/substitution")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Substitute(Guid id, SubstitutionRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var command = new Application.Substitutions.ProposeSubstitutionCommand(id, body.OriginalProductId, body.SubstituteProductId, body.Quantity ?? 0,
            body.Reason, body.RestockOriginal ?? false, body.IdempotencyKey, CurrentActor());
        return FromResult(await sender.Send(command, cancellationToken),
            outcome => outcome.Applied ? Ok(outcome) : StatusCode(StatusCodes.Status202Accepted, outcome));
    }

    [HttpGet("{id:guid}/substitutions")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Substitutions(Guid id, CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new Application.Substitutions.ListOrderSubstitutionsQuery(id), cancellationToken), Ok);

    /// <summary>Venda no balcão (PDV não fiscal): pedido já pago e entregue, com baixa de estoque.</summary>
    [HttpPost("pos")]
    [Authorize(Roles = Roles.PointOfSale)]
    [RequiresPlanFeature(Domain.Platform.PlanFeature.Pdv)]
    public async Task<IActionResult> PosSale(PosSaleRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var command = new Application.Pos.PlacePosSaleCommand(
            body.Items?.Select(i => new OrderItemInput(i.ProductId, i.Quantity, i.VariantLabel)).ToList(),
            body.Payments?.Select(p => new Application.Pos.PosPaymentInput(p.Method, p.Amount)).ToList(),
            body.Discount, body.CustomerName, body.CustomerPhone, body.IdempotencyKey, CurrentActor());
        return FromResult(await sender.Send(command, cancellationToken), o => StatusCode(StatusCodes.Status201Created, o));
    }

    /// <summary>
    /// Reembolso parcial pós-venda (devolução ou cortesia). 200 = confirmado; 202 = o gateway não
    /// respondeu de forma definitiva e o reembolso segue pendente (o job repete com a mesma chave).
    /// </summary>
    [HttpPost("{id:guid}/resolutions/refund")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Refund(Guid id, PartialRefundRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var command = new PartialRefundCommand(id, body.Amount ?? 0, body.Reason,
            body.Items?.Select(i => new RefundItemInput(i.ProductId, i.Quantity)).ToList(), body.Restock ?? false, body.IdempotencyKey, CurrentActor());
        return FromResult(await sender.Send(command, cancellationToken),
            result => result.Refund.Status == "pending" ? StatusCode(StatusCodes.Status202Accepted, result) : Ok(result));
    }
}

public sealed record SubstitutionRequest(string? OriginalProductId, string? SubstituteProductId, int? Quantity, string? Reason, bool? RestockOriginal, string? IdempotencyKey);

public sealed record PosPaymentRequest(string? Method, long Amount);

public sealed record PosSaleRequest(
    IReadOnlyList<OrderItemRequest>? Items, IReadOnlyList<PosPaymentRequest>? Payments, long? Discount, string? CustomerName, string? CustomerPhone,
    string? IdempotencyKey);

public sealed record PartialRefundItemRequest(string? ProductId, int Quantity);

public sealed record PartialRefundRequest(long? Amount, string? Reason, IReadOnlyList<PartialRefundItemRequest>? Items, bool? Restock, string? IdempotencyKey);

public sealed record CouponRequest(
    string? Code, string? Type, long? Value, long? MinOrder, Optional<int?> MaxUses, Optional<int?> PerCustomerLimit,
    Optional<DateTimeOffset?> ExpiresAt, string? TargetEmail, bool? IsActive)
{
    public CouponInput ToInput() => new(Code, Type, Value, MinOrder, MaxUses, PerCustomerLimit, ExpiresAt, TargetEmail, IsActive);
}

[Route(Contracts.ApiVersion.RoutePrefix + "/coupons")]
public sealed class CouponsController(ISender sender) : ApiController
{
    [HttpGet]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new ListCouponsQuery(), cancellationToken), Ok);

    [HttpPost]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Create(CouponRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new CreateCouponCommand(body.ToInput()), cancellationToken), c => StatusCode(StatusCodes.Status201Created, c));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Update(Guid id, CouponRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new UpdateCouponCommand(id, body.ToInput()), cancellationToken), Ok);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new DeleteCouponCommand(id), cancellationToken);
        return result.IsSuccess ? Ok(new { success = true }) : Failure(result.Error);
    }

    /// <summary>Pré-visualização no carrinho. Não consome o cupom.</summary>
    [HttpGet("validate")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingSetup.AuthPolicy)]
    public async Task<IActionResult> Validate([FromQuery] string? code, [FromQuery] long? subtotal, [FromQuery] string? email, CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new ValidateCouponQuery(code ?? string.Empty, subtotal ?? -1, email), cancellationToken), Ok);
}

public sealed record ZoneRequest(string? Name, string? ZipStart, string? ZipEnd, long? Fee, bool? IsActive)
{
    public ZoneInput ToInput() => new(Name, ZipStart, ZipEnd, Fee, IsActive);
}

[Route(Contracts.ApiVersion.RoutePrefix + "/delivery-zones")]
public sealed class DeliveryZonesController(ISender sender) : ApiController
{
    [HttpGet("quote")]
    [AllowAnonymous]
    public async Task<IActionResult> Quote([FromQuery] string? cep, CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new QuoteFeeQuery(cep ?? string.Empty), cancellationToken), Ok);

    [HttpGet]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new ListZonesQuery(), cancellationToken), Ok);

    [HttpPost]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Create(ZoneRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new CreateZoneCommand(body.ToInput()), cancellationToken), z => StatusCode(StatusCodes.Status201Created, z));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Update(Guid id, ZoneRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new UpdateZoneCommand(id, body.ToInput()), cancellationToken), Ok);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new DeleteZoneCommand(id), cancellationToken);
        return result.IsSuccess ? Ok(new { success = true }) : Failure(result.Error);
    }
}

public sealed record RuleRequest(string? Kind, int? Weekday, string? Date, string? Window, Guid? ZoneId, int? Capacity, int? CutoffMinutes, bool? IsActive, string? Note)
{
    public RuleInput ToInput() => new(Kind, Weekday, Date, Window, ZoneId, Capacity, CutoffMinutes, IsActive, Note);
}

public sealed record PromiseQuoteRequest(string? Method, string? ZipCode, string? Date, string? Window);

[Route(Contracts.ApiVersion.RoutePrefix + "/delivery-capacity")]
[Authorize(Roles = Roles.Admins)]
public sealed class DeliveryCapacityController(ISender sender) : ApiController
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new ListRulesQuery(), cancellationToken), Ok);

    [HttpPost]
    public async Task<IActionResult> Create(RuleRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new CreateRuleCommand(body.ToInput()), cancellationToken), r => StatusCode(StatusCodes.Status201Created, r));
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, RuleRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new UpdateRuleCommand(id, body.ToInput()), cancellationToken), Ok);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new DeleteRuleCommand(id), cancellationToken);
        return result.IsSuccess ? Ok(new { success = true }) : Failure(result.Error);
    }
}

[Route(Contracts.ApiVersion.RoutePrefix + "/delivery-promises")]
public sealed class DeliveryPromisesController(ISender sender) : ApiController
{
    /// <summary>Cotação para a vitrine: a mesma conta que o checkout refaz na hora de gravar.</summary>
    [HttpPost("quote")]
    [AllowAnonymous]
    [ProducesResponseType<PromiseEnvelope>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Quote(PromiseQuoteRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new QuotePromiseQuery(body.Method ?? "entrega", body.ZipCode, body.Date, body.Window), cancellationToken), Ok);
    }
}

/// <summary>Operação da plataforma (superadmin): fila de mensagens mortas da outbox.</summary>
[Route(Contracts.ApiVersion.RoutePrefix + "/platform/outbox")]
[Authorize(Roles = Roles.SuperAdmin)]
public sealed class PlatformOutboxController(IOutboxAdmin outbox) : ApiController
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? state, [FromQuery] int? limit, CancellationToken cancellationToken) =>
        Ok(new OutboxMessageList(await outbox.ListAsync(state ?? "dead", limit ?? 50, cancellationToken)));

    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken cancellationToken) =>
        await outbox.RetryAsync(id, cancellationToken)
            ? Ok(new { ok = true })
            : Failure(SharedKernel.Error.NotFound("NOT_FOUND", "Mensagem não encontrada na fila de mortas."));
}
