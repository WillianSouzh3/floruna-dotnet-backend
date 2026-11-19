using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Floruna.Application.Payments;
using Microsoft.Extensions.Options;

namespace Floruna.Infrastructure.Security;

public sealed class SecretsOptions
{
    public const string SectionName = "Secrets";

    /// <summary>
    /// Chave de 32 bytes em base64 (<c>openssl rand -base64 32</c>). Na AWS vem do Secrets
    /// Manager (ou vira envelope encryption com KMS, sem a chave nunca sair do HSM).
    /// </summary>
    [Required(ErrorMessage = "Configure Secrets:EncryptionKey (32 bytes em base64).")]
    public string EncryptionKey { get; set; } = string.Empty;
}

internal sealed class SecretsOptionsValidator : IValidateOptions<SecretsOptions>
{
    public ValidateOptionsResult Validate(string? name, SecretsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            return Convert.FromBase64String(options.EncryptionKey).Length == 32
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail("Secrets:EncryptionKey precisa ter exatamente 32 bytes (base64).");
        }
        catch (FormatException)
        {
            return ValidateOptionsResult.Fail("Secrets:EncryptionKey não é base64 válido.");
        }
    }
}

/// <summary>
/// AES-256-GCM: cifra E autentica. Formato: <c>v1:</c> + base64(nonce[12] | tag[16] | cifrado).
/// O contexto (ex.: <c>mercadopago-token:{loja}</c>) entra como "dados associados": se alguém
/// copiar o valor cifrado de uma loja para outra, a verificação do tag falha e nada é decifrado.
/// </summary>
internal sealed class AesGcmSecretProtector(IOptions<SecretsOptions> options) : ISecretProtector
{
    private const string Version = "v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public string Protect(string plaintext, string context)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[NonceSize + TagSize + data.Length];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce); // nonce NUNCA se repete com a mesma chave
        using var aes = new AesGcm(Key(), TagSize);
        aes.Encrypt(nonce, data, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize), Encoding.UTF8.GetBytes(context));
        return Version + Convert.ToBase64String(output);
    }

    public string? Unprotect(string ciphertext, string context)
    {
        if (ciphertext is null || !ciphertext.StartsWith(Version, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var input = Convert.FromBase64String(ciphertext[Version.Length..]);
            if (input.Length < NonceSize + TagSize)
            {
                return null;
            }

            var plaintext = new byte[input.Length - NonceSize - TagSize];
            using var aes = new AesGcm(Key(), TagSize);
            aes.Decrypt(input.AsSpan(0, NonceSize), input.AsSpan(NonceSize + TagSize), input.AsSpan(NonceSize, TagSize), plaintext, Encoding.UTF8.GetBytes(context));
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex) when (ex is FormatException or AuthenticationTagMismatchException)
        {
            return null;
        }
    }

    private byte[] Key() => Convert.FromBase64String(options.Value.EncryptionKey);
}
