using System.Text.Json.Serialization;

namespace Floruna.Api.Contracts;

// Contrato HTTP idêntico ao backend NestJS atual (api/src/common/api-contract.ts),
// para que o front Next.js funcione sem alteração.
//
//   sucesso: { "data": ..., "meta": { "requestId", "timestamp", "version": "1" } }
//   erro:    { "error": { "code", "message", "issues"? }, "meta": { ... } }

public sealed record ApiMeta(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequestId,
    string Timestamp,
    string Version);

public sealed record ApiSuccessEnvelope(object? Data, ApiMeta Meta);

public sealed record ApiErrorBody(
    string Code,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Issues);

public sealed record ApiErrorEnvelope(ApiErrorBody Error, ApiMeta Meta);

public static class ApiVersion
{
    public const string Current = "1";
    public const string RoutePrefix = "api/v1";
}
