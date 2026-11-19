using System.ComponentModel.DataAnnotations;
using Floruna.Api.Controllers;
using Floruna.Api.Security;
using Floruna.Domain.Platform;
using Floruna.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Floruna.Api.IntegrationTests.Support;

/// <summary>
/// Controller que só existe nos testes: exercita cada caminho do contrato
/// HTTP (sucesso, erro de negócio, validação, exceção) sem depender de
/// nenhum módulo de negócio.
/// </summary>
[AllowAnonymous]
[Route("api/v1/_probe")]
[ApiExplorerSettings(IgnoreApi = true)] // rota de teste: fora do contrato publicado (docs/openapi/v1.json)
public sealed class ContractProbeController : ApiController
{
    public sealed record Flower(string Name, long PriceCents);

    public sealed class CreateFlowerBody
    {
        [Required(ErrorMessage = "Nome é obrigatório.")]
        public string? Name { get; init; }

        [Range(1, long.MaxValue, ErrorMessage = "Preço deve ser positivo.")]
        public long PriceCents { get; init; }
    }

    [HttpGet("ok")]
    public IActionResult Success() => Ok(new Flower("Rosa", 1_990));

    [HttpGet("conflict")]
    public IActionResult Conflict_() =>
        FromResult(Result.Failure<Flower>(Error.Conflict("COUPON_EXHAUSTED", "O cupom se esgotou.")), Ok);

    [HttpGet("invalid")]
    public IActionResult Invalid() =>
        FromResult(Result.Failure(Error.Validation("VALIDATION_ERROR", "Dados inválidos.", ["Nome é obrigatório."])));

    [HttpGet("boom")]
    public IActionResult Boom() => throw new InvalidOperationException("detalhe interno que não pode vazar");

    [HttpPost("flowers")]
    public IActionResult Create(CreateFlowerBody body) =>
        StatusCode(StatusCodes.Status201Created, new Flower(body.Name!, body.PriceCents));
}

/// <summary>Endpoints protegidos por plano, para testar o <see cref="RequiresPlanFeatureAttribute"/>.</summary>
[Route("api/v1/_probe/plan")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class PlanProbeController : ApiController
{
    [HttpGet("pdv")]
    [RequiresPlanFeature(PlanFeature.Pdv)]
    public IActionResult Pdv() => Ok(new { feature = "pdv" });

    [HttpGet("fiscal")]
    [RequiresPlanFeature(PlanFeature.Fiscal)]
    public IActionResult Fiscal() => Ok(new { feature = "fiscal" });
}
