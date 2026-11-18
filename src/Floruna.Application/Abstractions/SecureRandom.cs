using System.Security.Cryptography;

namespace Floruna.Application.Abstractions;

/// <summary>
/// Segredos aleatórios com gerador CRIPTOGRÁFICO. Nunca <c>new Random()</c>:
/// ele é previsível a partir de saídas observadas, e num código de 2FA isso
/// permitiria adivinhar o código de outra pessoa.
/// </summary>
public static class SecureRandom
{
    /// <summary>Token opaco em hexadecimal (32 bytes = 64 caracteres).</summary>
    public static string HexToken(int bytes = 32) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));

    /// <summary>Código de 6 dígitos, com zeros à esquerda.</summary>
    public static string SixDigitCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
}
