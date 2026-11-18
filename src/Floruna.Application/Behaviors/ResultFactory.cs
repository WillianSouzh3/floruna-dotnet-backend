using System.Collections.Concurrent;
using System.Reflection;
using Floruna.SharedKernel;

namespace Floruna.Application.Behaviors;

/// <summary>
/// Cria uma falha do tipo exato que o pipeline espera (<c>Result</c> ou
/// <c>Result&lt;T&gt;</c>). Necessário porque um behavior genérico só conhece
/// <c>TResponse</c>, e não o <c>T</c> de dentro.
/// </summary>
internal static class ResultFactory
{
    private static readonly MethodInfo GenericFailure =
        typeof(Result).GetMethod(nameof(Result.Failure), genericParameterCount: 1, [typeof(Error)])!;

    private static readonly ConcurrentDictionary<Type, Func<Error, Result>> Cache = new();

    public static TResponse Failure<TResponse>(Error error)
        where TResponse : Result
    {
        if (typeof(TResponse) == typeof(Result))
        {
            return (TResponse)Result.Failure(error);
        }

        var factory = Cache.GetOrAdd(typeof(TResponse), static responseType =>
        {
            var valueType = responseType.GetGenericArguments()[0];
            var method = GenericFailure.MakeGenericMethod(valueType);
            return e => (Result)method.Invoke(null, [e])!;
        });

        return (TResponse)factory(error);
    }
}
