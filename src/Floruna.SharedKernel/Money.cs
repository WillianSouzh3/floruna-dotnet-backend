namespace Floruna.SharedKernel;

/// <summary>
/// Dinheiro em CENTAVOS inteiros + moeda. Value object imutável.
/// </summary>
/// <remarks>
/// <para>Por que <c>long</c> em centavos e não <c>decimal</c>/<c>double</c>? É o mesmo
/// contrato do backend atual e do front ("preços em centavos ponta a ponta"),
/// e elimina arredondamento acumulado: 0,1 + 0,2 em double ≠ 0,3.</para>
/// <para>Invariante: nunca negativo. Desconto, frete e total são sempre ≥ 0;
/// um valor "a devolver" é modelado como reembolso, não como dinheiro negativo.</para>
/// </remarks>
public readonly record struct Money : IComparable<Money>
{
    public const string DefaultCurrency = "BRL";

    private Money(long cents, string currency)
    {
        Cents = cents;
        Currency = currency;
    }

    public long Cents { get; }

    public string Currency { get; }

    public static Money Zero(string currency = DefaultCurrency) => new(0, currency);

    /// <summary>Cria a partir de centavos. Lança se negativo: é bug de quem chama, não regra do usuário.</summary>
    public static Money FromCents(long cents, string currency = DefaultCurrency)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cents);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        if (currency.Length != 3)
        {
            throw new ArgumentException("Moeda deve ser um código ISO 4217 de 3 letras.", nameof(currency));
        }

        return new Money(cents, currency.ToUpperInvariant());
    }

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(checked(Cents + other.Cents), Currency);
    }

    /// <summary>Subtrai. Resultado negativo é violação de invariante.</summary>
    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        if (other.Cents > Cents)
        {
            throw new InvalidOperationException($"Subtração resultaria em valor negativo ({Cents} - {other.Cents}).");
        }

        return new Money(Cents - other.Cents, Currency);
    }

    /// <summary>Subtrai sem passar de zero (ex.: desconto maior que o valor dos itens).</summary>
    public Money SubtractFloorZero(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Math.Max(0, Cents - other.Cents), Currency);
    }

    public Money Multiply(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quantity);
        return new Money(checked(Cents * quantity), Currency);
    }

    /// <summary>
    /// Porcentagem do valor, arredondada para o centavo mais próximo
    /// (meio centavo arredonda para cima). Equivale ao <c>Math.round</c> do
    /// backend atual para valores positivos.
    /// </summary>
    public Money Percent(decimal percent)
    {
        if (percent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percent), percent, "Porcentagem deve estar entre 0 e 100.");
        }

        var cents = Math.Round(Cents * percent / 100m, MidpointRounding.AwayFromZero);
        return new Money((long)cents, Currency);
    }

    public static Money Min(Money a, Money b)
    {
        a.EnsureSameCurrency(b);
        return a.Cents <= b.Cents ? a : b;
    }

    public int CompareTo(Money other)
    {
        EnsureSameCurrency(other);
        return Cents.CompareTo(other.Cents);
    }

    public static Money operator +(Money left, Money right) => left.Add(right);

    public static Money operator -(Money left, Money right) => left.Subtract(right);

    public static Money operator *(Money left, int quantity) => left.Multiply(quantity);

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Currency} {Cents / 100m:0.00}";

    private void EnsureSameCurrency(Money other)
    {
        if (!string.Equals(Currency, other.Currency, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Moedas diferentes: {Currency} e {other.Currency}.");
        }
    }
}
