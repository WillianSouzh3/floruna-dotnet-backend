namespace Floruna.Domain.Identity;

/// <summary>
/// Bloqueio progressivo de login (o mesmo do <c>auth-attempt.service.ts</c> do Nest).
/// Função PURA: recebe o número de falhas, devolve quanto tempo bloquear.
/// </summary>
/// <remarks>
/// Duas contagens independentes, numa janela de 24 h:
/// <list type="bullet">
/// <item><b>conta + IP</b>: pega o ataque de UM lugar contra uma conta (limiar baixo, a partir de 5).</item>
/// <item><b>conta</b>, de qualquer IP: pega o ataque distribuído (limiar alto, a partir de 10), sem
/// deixar que um atacante tranque o dono legítimo com poucas tentativas.</item>
/// </list>
/// </remarks>
public static class LoginLockoutPolicy
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private static readonly (int Failures, TimeSpan Lock)[] AccountAndIp =
    [
        (8, TimeSpan.FromHours(1)),
        (7, TimeSpan.FromMinutes(15)),
        (6, TimeSpan.FromMinutes(5)),
        (5, TimeSpan.FromMinutes(1)),
    ];

    private static readonly (int Failures, TimeSpan Lock)[] Account =
    [
        (30, TimeSpan.FromHours(1)),
        (20, TimeSpan.FromMinutes(15)),
        (15, TimeSpan.FromMinutes(5)),
        (10, TimeSpan.FromMinutes(1)),
    ];

    public static TimeSpan LockForAccountAndIp(int failures) => LockFor(AccountAndIp, failures);

    public static TimeSpan LockForAccount(int failures) => LockFor(Account, failures);

    private static TimeSpan LockFor((int Failures, TimeSpan Lock)[] thresholds, int failures)
    {
        foreach (var (threshold, duration) in thresholds)
        {
            if (failures >= threshold)
            {
                return duration;
            }
        }

        return TimeSpan.Zero;
    }
}
