namespace Floruna.SharedKernel;

/// <summary>
/// Resultado de uma operação que pode falhar por REGRA DE NEGÓCIO.
/// </summary>
/// <remarks>
/// Por que não exceção? Regra violada (cupom vencido, estoque insuficiente) é
/// um desfecho previsto do caso de uso, não um acidente. Como valor, ela
/// aparece na assinatura do método, obriga o chamador a tratar e não custa
/// o stack unwinding de uma exceção. Exceção fica para o inesperado
/// (banco fora do ar, bug).
/// </remarks>
public class Result
{
    protected Result(bool isSuccess, Error error)
    {
        if (isSuccess && error != Error.None)
        {
            throw new ArgumentException("Um sucesso não pode carregar erro.", nameof(error));
        }

        if (!isSuccess && error == Error.None)
        {
            throw new ArgumentException("Uma falha precisa de um erro.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error Error { get; }

    public static Result Success() => new(true, Error.None);

    public static Result Failure(Error error) => new(false, error);

    public static Result<TValue> Success<TValue>(TValue value) => new(value, true, Error.None);

    public static Result<TValue> Failure<TValue>(Error error) => new(default, false, error);

    public static implicit operator Result(Error error) => Failure(error);
}

/// <summary>Resultado com valor em caso de sucesso.</summary>
public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    internal Result(TValue? value, bool isSuccess, Error error)
        : base(isSuccess, error) => _value = value;

    /// <summary>Valor do sucesso. Ler o valor de uma falha é bug do chamador.</summary>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Resultado é falha ({Error.Code}); não há valor.");

    public static implicit operator Result<TValue>(TValue value) => Success(value);

    public static implicit operator Result<TValue>(Error error) => Failure<TValue>(error);

    /// <summary>Aplica uma transformação ao valor, propagando a falha sem tocá-la.</summary>
    public Result<TOut> Map<TOut>(Func<TValue, TOut> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return IsSuccess ? Success(map(Value)) : Failure<TOut>(Error);
    }
}
