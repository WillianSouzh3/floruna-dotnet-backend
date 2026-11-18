namespace Floruna.Application.Identity;

/// <summary>Mesmo formato do <c>AuthResult</c> do Nest: <c>{ accessToken, user: { id, name, email, role } }</c>.</summary>
public sealed record AuthResult(string AccessToken, AuthUser User);

public sealed record AuthUser(string Id, string Name, string Email, string Role);
