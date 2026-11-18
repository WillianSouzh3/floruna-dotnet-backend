using Floruna.Application.Messaging;
using Floruna.SharedKernel;
using FluentValidation;

namespace Floruna.Application.Behaviors;

/// <summary>
/// Valida a FORMA da mensagem (campos obrigatórios, tamanhos, formatos) antes
/// do handler. Regra de NEGÓCIO (cupom vencido, estoque) não fica aqui: fica
/// no domínio, porque depende de estado.
/// </summary>
/// <remarks>
/// Em vez de lançar exceção, devolve <c>Result.Failure</c> com
/// <c>VALIDATION_ERROR</c>: o mesmo código e o mesmo formato
/// (<c>issues: string[]</c>) que o backend NestJS devolve hoje ao front.
/// </remarks>
internal sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : Result
{
    public const string ErrorCode = "VALIDATION_ERROR";

    public async Task<TResponse> Handle(TRequest request, Func<Task<TResponse>> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        var validatorList = validators.ToList();
        if (validatorList.Count == 0)
        {
            return await next().ConfigureAwait(false);
        }

        var context = new ValidationContext<TRequest>(request);
        var results = new List<FluentValidation.Results.ValidationResult>(validatorList.Count);
        foreach (var validator in validatorList)
        {
            results.Add(await validator.ValidateAsync(context, cancellationToken).ConfigureAwait(false));
        }

        var issues = results
            .SelectMany(r => r.Errors)
            .Select(f => f.ErrorMessage)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (issues.Count == 0)
        {
            return await next().ConfigureAwait(false);
        }

        return ResultFactory.Failure<TResponse>(Error.Validation(ErrorCode, "Dados inválidos.", issues));
    }
}
