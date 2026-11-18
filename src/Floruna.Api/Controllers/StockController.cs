using Floruna.Application.Inventory;
using Floruna.Application.Messaging;
using Floruna.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Floruna.Api.Controllers;

public sealed record RestockRequest(int? Quantity, DateTimeOffset? ExpiresAt, long? Cost, string? Note);

public sealed record AdjustRequest(int? Delta, string? Note);

public sealed record LossRequest(int? Quantity, string? Note);

/// <summary>Estoque (<c>/stock</c>), mesmo contrato do <c>StockController</c> do Nest. Só administradores.</summary>
[Route(Contracts.ApiVersion.RoutePrefix + "/stock")]
[Authorize(Roles = Roles.Admins)]
public sealed class StockController(ISender sender) : ApiController
{
    private string ActorName => User.FindFirst("name")?.Value ?? "sistema";

    [HttpGet("low")]
    public async Task<IActionResult> Low(CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new GetLowStockQuery(), cancellationToken), Ok);

    [HttpGet("low/count")]
    public async Task<IActionResult> LowCount(CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new GetLowStockCountQuery(), cancellationToken), Ok);

    /// <summary>Lotes com saldo que vencem nos próximos <c>days</c> dias (padrão 7).</summary>
    [HttpGet("expiring")]
    public async Task<IActionResult> Expiring([FromQuery] int days = 7, CancellationToken cancellationToken = default) =>
        FromResult(await sender.Send(new GetExpiringLotsQuery(days), cancellationToken), Ok);

    [HttpGet("reservations")]
    public async Task<IActionResult> Reservations(CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new GetHeldReservationsQuery(), cancellationToken), Ok);

    [HttpPost("reservations/{id:guid}/release")]
    public async Task<IActionResult> Release(Guid id, CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new ReleaseReservationCommand(id, ActorName), cancellationToken), Ok);

    [HttpGet("{productId:guid}/movements")]
    public async Task<IActionResult> Movements(Guid productId, CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new GetMovementsQuery(productId), cancellationToken), Ok);

    [HttpGet("{productId:guid}/lots")]
    public async Task<IActionResult> Lots(Guid productId, CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new GetLotsQuery(productId), cancellationToken), Ok);

    /// <summary>Entrada: cria um lote (com validade, se perecível). O custo informado vira o custo do produto.</summary>
    [HttpPost("{productId:guid}/restock")]
    [ProducesResponseType<StockOperationResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Restock(Guid productId, RestockRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var command = new RestockCommand(productId, body.Quantity ?? 0, body.ExpiresAt, body.Cost, body.Note, ActorName);
        return FromResult(await sender.Send(command, cancellationToken), Ok);
    }

    [HttpPost("{productId:guid}/adjust")]
    [ProducesResponseType<StockOperationResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Adjust(Guid productId, AdjustRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new AdjustStockCommand(productId, body.Delta ?? 0, body.Note, ActorName), cancellationToken), Ok);
    }

    /// <summary>Perda/descarte: consome primeiro os lotes vencidos (FEFO).</summary>
    [HttpPost("{productId:guid}/loss")]
    [ProducesResponseType<StockOperationResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Loss(Guid productId, LossRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new RegisterLossCommand(productId, body.Quantity ?? 0, body.Note, ActorName), cancellationToken), Ok);
    }
}
