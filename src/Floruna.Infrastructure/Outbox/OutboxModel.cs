using System.Text.Json;
using Floruna.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Floruna.Infrastructure.Outbox;

public enum OutboxState
{
    Pending,
    Processing,
    Processed,
    Dead,
}

/// <summary>
/// Evento de domínio persistido para entrega posterior (Transactional Outbox).
/// Nasce na MESMA transação do agregado que o gerou: se a transação abortar, o evento
/// não existe; se o processo cair depois do commit, o evento continua aqui.
/// </summary>
internal sealed class OutboxMessage
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Type { get; set; } = string.Empty;

    public string Payload { get; set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; set; }

    public OutboxState State { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>Lease: até quando a réplica que pegou a mensagem tem exclusividade.</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }
}

/// <summary>
/// "Este handler já processou esta mensagem". Se um evento tem 3 efeitos e o 2º falha,
/// o retry não refaz o 1º (não manda o e-mail duas vezes). Evolução do <c>markStep</c> do Nest,
/// mas genérico: vale para qualquer handler novo sem editar o processador.
/// </summary>
internal sealed class OutboxReceipt
{
    public Guid MessageId { get; set; }

    public string Handler { get; set; } = string.Empty;

    public DateTimeOffset ProcessedAt { get; set; }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Type).HasMaxLength(200).IsRequired();
        builder.Property(m => m.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(m => m.LastError).HasMaxLength(2000);
        // O processador busca por estado + vencimento: índice parcial só do que falta processar.
        builder.HasIndex(m => new { m.State, m.NextAttemptAt }).HasFilter("state IN ('Pending', 'Processing')");
        builder.HasIndex(m => new { m.TenantId, m.OccurredAt });
    }
}

internal sealed class OutboxReceiptConfiguration : IEntityTypeConfiguration<OutboxReceipt>
{
    public void Configure(EntityTypeBuilder<OutboxReceipt> builder)
    {
        builder.ToTable("outbox_receipts");
        builder.HasKey(r => new { r.MessageId, r.Handler });
        builder.Property(r => r.Handler).HasMaxLength(200);
    }
}

/// <summary>
/// Recolhe os eventos registrados pelos agregados e os grava como <see cref="OutboxMessage"/>
/// no MESMO SaveChanges (logo, na mesma transação). O agregado não publica nada: ele só
/// registra o fato; quem garante a entrega é a outbox.
/// </summary>
internal sealed class DomainEventsToOutboxInterceptor(TimeProvider clock) : SaveChangesInterceptor
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    private void Collect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var aggregates = context.ChangeTracker.Entries<IAggregateRoot>()
            .Where(e => e.Entity.DomainEvents.Count > 0)
            .Select(e => e.Entity)
            .ToList();

        foreach (var aggregate in aggregates)
        {
            var tenantId = aggregate is ITenantOwned owned ? owned.TenantId.Value : Guid.Empty;
            foreach (var domainEvent in aggregate.DomainEvents)
            {
                context.Set<OutboxMessage>().Add(new OutboxMessage
                {
                    Id = domainEvent.EventId,
                    TenantId = tenantId,
                    Type = domainEvent.GetType().FullName!,
                    Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), Json),
                    OccurredAt = domainEvent.OccurredAt,
                    State = OutboxState.Pending,
                    NextAttemptAt = clock.GetUtcNow(),
                });
            }

            aggregate.ClearDomainEvents();
        }
    }
}
