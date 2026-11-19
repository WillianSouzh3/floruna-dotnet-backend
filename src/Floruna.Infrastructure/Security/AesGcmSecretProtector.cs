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
    /// Chave de 32 bytes em base64 (<c>openssl rand -base64 32</c>) OU uma frase aleatória de 48+
    /// caracteres (o formato que o Secrets Manager gera), da qual a chave é derivada com HKDF.
    /// </summary>
    [Required(ErrorMessage = "Configure Secrets:EncryptionKey (32 bytes em base64 ou 48+ caracteres aleatórios).")]
    public string EncryptionKey { get; set; } = string.Empty;
}

internal sealed class SecretsOptionsValidator : IValidateOptions<SecretsOptions>
{
    public ValidateOptionsResult Validate(string? name, SecretsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return EncryptionKeyMaterial.Derive(options.EncryptionKey) is not null
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Secrets:EncryptionKey precisa ser 32 bytes em base64 ou ter ao menos 48 caracteres aleatórios.");
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

    private byte[] Key() => EncryptionKeyMaterial.Derive(options.Value.EncryptionKey)!;
}

/// <summary>
/// Transforma o segredo configurado na chave AES-256. Dois formatos:
/// <list type="bullet">
/// <item>32 bytes em base64: usados como estão (compatível com o que já foi cifrado).</item>
/// <item>Frase aleatória de 48+ caracteres (gerada pelo Secrets Manager, que não sabe gerar
/// "32 bytes em base64"): a chave é DERIVADA com HKDF-SHA256. Uma senha digitada por humano não
/// serve (HKDF não é lento como PBKDF2/Argon2): o segredo precisa ser aleatório de verdade.</item>
/// </list>
/// </summary>
public static class EncryptionKeyMaterial
{
    public const int MinPassphraseLength = 48;
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("floruna/secret-protector/v1");

    public static byte[]? Derive(string? configured)
    {
        if (string.IsNullOrEmpty(configured))
        {
            return null;
        }

        var raw = new byte[configured.Length];
        if (Convert.TryFromBase64String(configured, raw, out var written) && written == 32)
        {
            return raw[..32];
        }

        return configured.Length >= MinPassphraseLength
            ? HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(configured), 32, salt: null, info: Info)
            : null;
    }
}
