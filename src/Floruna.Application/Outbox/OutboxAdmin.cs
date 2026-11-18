namespace Floruna.Application.Outbox;

public sealed record OutboxMessageResponse(string Id, string TenantId, string Type, string State, int Attempts, string? LastError, string OccurredAt);

public sealed record OutboxMessageList(IReadOnlyList<OutboxMessageResponse> Messages);

/// <summary>
/// Operação da fila de mensagens mortas (DLQ), para o superadmin: ver o que falhou de vez
/// e, corrigida a causa, mandar processar de novo.
/// </summary>
public interface IOutboxAdmin
{
    Task<IReadOnlyList<OutboxMessageResponse>> ListAsync(string state, int limit, CancellationToken cancellationToken);

    /// <summary>Volta uma mensagem morta para a fila (zera tentativas). False se não estava morta.</summary>
    Task<bool> RetryAsync(Guid id, CancellationToken cancellationToken);
}
