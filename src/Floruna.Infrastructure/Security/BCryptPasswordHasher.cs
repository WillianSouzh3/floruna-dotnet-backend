using Floruna.Application.Abstractions;

namespace Floruna.Infrastructure.Security;

/// <summary>
/// BCrypt com custo 10: o MESMO formato do <c>bcryptjs</c> do Nest
/// (<c>$2a$10$...</c>/<c>$2b$10$...</c>). Por isso as senhas migradas do
/// MongoDB continuam funcionando sem pedir troca de senha.
/// </summary>
internal sealed class BCryptPasswordHasher : IPasswordHasher
{
    private const int WorkFactor = 10;

    // Calculado UMA vez por processo (custa ~50 ms): usado quando a conta
    // não existe, para o login levar o mesmo tempo nos dois casos.
    private static readonly Lazy<string> DummyHash = new(() => BCrypt.Net.BCrypt.HashPassword("timing-equalizer", WorkFactor));

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        return BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);
    }

    public bool Verify(string password, string? hash)
    {
        try
        {
            var matches = BCrypt.Net.BCrypt.Verify(password ?? string.Empty, hash ?? DummyHash.Value);
            return matches && hash is not null;
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false; // hash corrompido no banco: nega, não derruba a requisição
        }
    }
}
