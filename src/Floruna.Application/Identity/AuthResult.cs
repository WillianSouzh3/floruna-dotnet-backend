namespace Floruna.Application.Identity;

/// <summary>Desfecho do login da equipe: sessão aberta OU desafio de 2FA.</summary>
#pragma warning disable CA1040 // marcador intencional: as duas respostas têm formatos diferentes no contrato
public interface IStaffLoginOutcome;
#pragma warning restore CA1040

/// <summary>Mesmo formato do <c>AuthResult</c> do Nest: <c>{ accessToken, user: { id, name, email, role } }</c>.</summary>
public sealed record AuthResult(string AccessToken, AuthUser User) : IStaffLoginOutcome;

public sealed record AuthUser(string Id, string Name, string Email, string Role);

/// <summary>2FA exigido: <c>{ requiresTwoFactor: true, pendingToken }</c>. O código foi enviado por e-mail.</summary>
public sealed record PendingTwoFactorResult(string PendingToken) : IStaffLoginOutcome
{
    public bool RequiresTwoFactor => true;
}

internal static class AuthResults
{
    public static AuthResult For(Floruna.Domain.Identity.StaffUser user, string token) =>
        new(token, new AuthUser(user.Id.ToString(), user.Name, user.Email.Value, user.Role));
}
