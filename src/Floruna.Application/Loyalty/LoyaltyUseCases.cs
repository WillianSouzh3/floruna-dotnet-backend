using System.Globalization;
using Floruna.Application.Abstractions;
using Floruna.Application.Catalog;
using Floruna.Application.Messaging;
using Floruna.Domain.Audit;
using Floruna.Domain.Loyalty;
using Floruna.SharedKernel;
using FluentValidation;

namespace Floruna.Application.Loyalty;

// ── Contratos (formato do Nest; pontos ficam fora: o Floruna .NET só tem cashback, ADR 0007) ──

public sealed record LoyaltyTierDto(string Key, string Name, int MinOrders, int MinFlowerKinds, long MinSpent, decimal DiscountPercent, bool FreeShipping)
{
    public static LoyaltyTierDto From(LoyaltyTier t) => new(t.Key, t.Name, t.MinOrders, t.MinFlowerKinds, t.MinSpent, t.DiscountPercent, t.FreeShipping);

    public LoyaltyTier ToDomain() => new(Key.Trim(), Name.Trim(), MinOrders, MinFlowerKinds, MinSpent, DiscountPercent, FreeShipping);
}

public sealed record LoyaltyProgramResponse(
    string? Id, bool Enabled, decimal CashbackPercent, long CashbackMinRedemption, int CashbackExpiryDays, string CouponStackingMode,
    int MaxTotalDiscountPercent, int MaxCashbackPercentOfOrder, bool AllowCashbackWithCoupon, IReadOnlyList<LoyaltyTierDto> Tiers)
{
    public static LoyaltyProgramResponse From(LoyaltyProgram p, bool persisted) => new(
        persisted ? p.Id.ToString() : null, p.Enabled, p.CashbackPercent, p.CashbackMinRedemption, p.CashbackExpiryDays,
        p.Stacking == StackingMode.BestOf ? "best_of" : "cumulative", p.MaxTotalDiscountPercent, p.MaxCashbackPercentOfOrder,
        p.AllowCashbackWithCoupon, [.. p.Tiers.Select(LoyaltyTierDto.From)]);
}

public sealed record LoyaltyProgramEnvelope(LoyaltyProgramResponse Program);

public sealed record LoyaltyAccountResponse(
    string Id, string Email, long CashbackBalance, long PointsBalance, long TotalSpent, int OrdersCount, IReadOnlyList<string> FlowerKinds, string TierKey, string CreatedAt);

public sealed record LoyaltyAccountEnvelope(LoyaltyAccountResponse Account);

public sealed record LoyaltyAccountPage(IReadOnlyList<LoyaltyAccountResponse> Accounts, int Total, int Page, int Limit, int Pages);

public sealed record LoyaltyEntryResponse(
    string Id, string AccountId, string Kind, long Amount, string Reason, string SourceType, string SourceId, long Remaining, string? ExpiresAt, string Description, string CreatedAt);

public sealed record LoyaltyStatementPage(IReadOnlyList<LoyaltyEntryResponse> Entries, int Total, int Page, int Limit, int Pages);

public sealed record LiabilityResponse(long CashbackCents, long Points, long PointsCents, long TotalCents);

public sealed record TierView(LoyaltyTierDto? Current, LoyaltyTierDto? Next, decimal Progress, TierMissing Missing);

public sealed record RedeemableView(long CashbackCents, long CashbackMinRedemption, int MaxCashbackPercentOfOrder);

public sealed record ExpiringLot(long Amount, string ExpiresAt);

public sealed record ExpiringView(ExpiringLot? Cashback, ExpiringLot? Points);

public sealed record WalletResponse(bool Enabled, bool Participating, LoyaltyAccountResponse Account, TierView Tier, RedeemableView Redeemable, ExpiringView Expiring);

/// <summary>Leituras da fidelidade (lado Q do CQRS).</summary>
public interface ILoyaltyQueries
{
    Task<(IReadOnlyList<LoyaltyAccountResponse> Items, int Total)> AccountsAsync(int page, int limit, CancellationToken cancellationToken);

    Task<long> OutstandingCashbackAsync(DateTimeOffset now, CancellationToken cancellationToken);

    Task<(IReadOnlyList<LoyaltyEntryResponse> Items, int Total)> StatementAsync(Guid accountId, int page, int limit, CancellationToken cancellationToken);

    Task<ExpiringLot?> NextExpiringAsync(Guid accountId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Contas com lote vencido e saldo, de TODAS as lojas (job): (loja, conta).</summary>
    Task<IReadOnlyList<(Guid TenantId, Guid AccountId)>> AccountsWithExpiredCreditsAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken);
}

public static class LoyaltyMapping
{
    public static LoyaltyAccountResponse ToResponse(LoyaltyAccount a)
    {
        ArgumentNullException.ThrowIfNull(a);
        return new(a.Id.ToString(), a.Email, a.CashbackBalance, 0, a.TotalSpentCents, a.OrdersCount, a.FlowerKinds, a.TierKey, CatalogMapping.Iso(a.CreatedAt));
    }

    public static LoyaltyEntryResponse ToResponse(LoyaltyEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var (reason, source) = e.Kind switch
        {
            LoyaltyEntryKind.Earn => ("order_earned", "order"),
            LoyaltyEntryKind.Redeem => ("redeemed", "order"),
            LoyaltyEntryKind.RedeemReversal => ("reversed", "order"),
            LoyaltyEntryKind.Expire => ("expired", "system"),
            _ => ("admin_adjustment", "admin"),
        };
        return new(e.Id.ToString(), e.AccountId.ToString(), "cashback", e.Amount, reason, source, e.DedupeKey[(e.DedupeKey.LastIndexOf(':') + 1)..],
            e.Remaining, e.ExpiresAt is { } at ? CatalogMapping.Iso(at) : null, e.Description, CatalogMapping.Iso(e.CreatedAt));
    }

    public static int Pages(int total, int limit) => (int)Math.Ceiling(total / (double)limit);
}

// ── Painel da loja ──

public sealed record GetLoyaltyProgramQuery : IQuery<LoyaltyProgramEnvelope>;

public sealed record UpdateLoyaltyProgramCommand(
    bool Enabled, decimal CashbackPercent, long CashbackMinRedemption, int CashbackExpiryDays, string CouponStackingMode,
    int MaxTotalDiscountPercent, int MaxCashbackPercentOfOrder, bool AllowCashbackWithCoupon, IReadOnlyList<LoyaltyTierDto>? Tiers, AuditActor Actor)
    : ICommand<LoyaltyProgramEnvelope>;

internal sealed class UpdateLoyaltyProgramValidator : AbstractValidator<UpdateLoyaltyProgramCommand>
{
    public UpdateLoyaltyProgramValidator()
    {
        RuleFor(x => x.CouponStackingMode).Must(m => m is "best_of" or "cumulative").WithMessage("Modo de combinação inválido.");
        RuleFor(x => x.CashbackMinRedemption).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Tiers).Must(t => t is null || t.Count <= 10).WithMessage("No máximo 10 níveis.");
        RuleForEach(x => x.Tiers).ChildRules(t =>
        {
            t.RuleFor(x => x.Key).NotEmpty().Length(2, 40);
            t.RuleFor(x => x.Name).NotEmpty().Length(2, 60);
            t.RuleFor(x => x.MinOrders).GreaterThanOrEqualTo(0);
            t.RuleFor(x => x.MinFlowerKinds).GreaterThanOrEqualTo(0);
            t.RuleFor(x => x.MinSpent).GreaterThanOrEqualTo(0);
        });
    }
}

public sealed record ListLoyaltyAccountsQuery(int Page, int Limit) : IQuery<LoyaltyAccountPage>;

public sealed record GetLoyaltyLiabilityQuery : IQuery<LiabilityResponse>;

/// <summary>Ajuste manual: é uma LINHA nova do razão, nunca edição de saldo; exige motivo e é auditado.</summary>
public sealed record AdjustLoyaltyCommand(Guid AccountId, string? Kind, long Amount, string? Reason, string? IdempotencyKey, AuditActor Actor) : ICommand<LoyaltyAccountEnvelope>;

internal sealed class AdjustLoyaltyValidator : AbstractValidator<AdjustLoyaltyCommand>
{
    public AdjustLoyaltyValidator()
    {
        RuleFor(x => x.Kind).Equal("cashback").WithMessage("Só há cashback neste programa.");
        RuleFor(x => x.Amount).NotEqual(0).WithMessage("O ajuste precisa ser diferente de zero.");
        RuleFor(x => x.Reason).NotEmpty().Length(5, 200).WithMessage("Informe o motivo (5 a 200 caracteres).");
        RuleFor(x => x.IdempotencyKey).MaximumLength(120);
    }
}

internal sealed class LoyaltyAdminHandlers(
    ILoyaltyRepository loyalty, ILoyaltyQueries queries, ITenantContext tenantContext, IAuditRepository audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : IQueryHandler<GetLoyaltyProgramQuery, LoyaltyProgramEnvelope>,
      ICommandHandler<UpdateLoyaltyProgramCommand, LoyaltyProgramEnvelope>,
      IQueryHandler<ListLoyaltyAccountsQuery, LoyaltyAccountPage>,
      IQueryHandler<GetLoyaltyLiabilityQuery, LiabilityResponse>,
      ICommandHandler<AdjustLoyaltyCommand, LoyaltyAccountEnvelope>
{
    public async Task<Result<LoyaltyProgramEnvelope>> Handle(GetLoyaltyProgramQuery request, CancellationToken cancellationToken)
    {
        var program = await loyalty.GetProgramAsync(cancellationToken);
        return new LoyaltyProgramEnvelope(program is null
            ? LoyaltyProgramResponse.From(LoyaltyProgram.DefaultFor(tenantContext.RequiredTenantId), persisted: false)
            : LoyaltyProgramResponse.From(program, persisted: true));
    }

    public async Task<Result<LoyaltyProgramEnvelope>> Handle(UpdateLoyaltyProgramCommand request, CancellationToken cancellationToken)
    {
        var program = await loyalty.GetProgramAsync(cancellationToken);
        if (program is null)
        {
            program = LoyaltyProgram.DefaultFor(tenantContext.RequiredTenantId);
            loyalty.Add(program);
        }

        var spec = new LoyaltyProgramSpec(request.Enabled, request.CashbackPercent, request.CashbackMinRedemption, request.CashbackExpiryDays,
            request.CouponStackingMode == "cumulative" ? StackingMode.Cumulative : StackingMode.BestOf, request.MaxTotalDiscountPercent,
            request.MaxCashbackPercentOfOrder, request.AllowCashbackWithCoupon, request.Tiers?.Select(t => t.ToDomain()).ToList() ?? program.Tiers);
        var updated = program.Update(spec);
        if (updated.IsFailure)
        {
            return updated.Error;
        }

        audit.Add(AuditEntry.Record(program.TenantId, LoyaltyAuditActions.ProgramUpdated, request.Actor, "loyaltyProgram", program.Id.ToString(),
            $"Programa de fidelidade atualizado (ativo: {request.Enabled}, cashback: {request.CashbackPercent.ToString(CultureInfo.InvariantCulture)}%)", clock.GetUtcNow()));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new LoyaltyProgramEnvelope(LoyaltyProgramResponse.From(program, persisted: true));
    }

    public async Task<Result<LoyaltyAccountPage>> Handle(ListLoyaltyAccountsQuery request, CancellationToken cancellationToken)
    {
        var (items, total) = await queries.AccountsAsync(request.Page, request.Limit, cancellationToken);
        return new LoyaltyAccountPage(items, total, request.Page, request.Limit, LoyaltyMapping.Pages(total, request.Limit));
    }

    /// <summary>Passivo em aberto: cashback distribuído é dívida da loja em mercadoria.</summary>
    public async Task<Result<LiabilityResponse>> Handle(GetLoyaltyLiabilityQuery request, CancellationToken cancellationToken)
    {
        var cashback = await queries.OutstandingCashbackAsync(clock.GetUtcNow(), cancellationToken);
        return new LiabilityResponse(cashback, 0, 0, cashback);
    }

    public async Task<Result<LoyaltyAccountEnvelope>> Handle(AdjustLoyaltyCommand request, CancellationToken cancellationToken)
    {
        // Sem chave, o mesmo ajuste enviado duas vezes credita duas vezes (como no Nest).
        var dedupeKey = $"admin:{request.IdempotencyKey ?? Guid.NewGuid().ToString()}";
        var result = await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var account = await loyalty.GetByIdForUpdateAsync(request.AccountId, ct);
            if (account is null)
            {
                return Result.Failure<LoyaltyAccount>(LoyaltyErrors.AccountNotFound);
            }

            if (await loyalty.EntryExistsAsync(dedupeKey, ct))
            {
                return Result.Success(account); // replay: o ajuste já foi feito
            }

            var now = clock.GetUtcNow();
            var program = await loyalty.GetProgramAsync(ct);
            var adjusted = account.Adjust(request.Amount, program?.CashbackExpiryDays ?? 365, request.Reason!.Trim(), dedupeKey, now);
            if (adjusted.IsFailure)
            {
                return Result.Failure<LoyaltyAccount>(adjusted.Error);
            }

            loyalty.Track(account);
            audit.Add(AuditEntry.Record(account.TenantId, LoyaltyAuditActions.Adjusted, request.Actor, "loyaltyAccount", account.Id.ToString(),
                $"Ajuste de {request.Amount} (cashback) em {account.Email}: {request.Reason!.Trim()}", now));
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Success(account);
        }, cancellationToken);

        return result.IsFailure ? result.Error : new LoyaltyAccountEnvelope(LoyaltyMapping.ToResponse(result.Value));
    }
}

// ── Área do cliente (exige sessão: saldo por e-mail seria enumeração) ──

public sealed record GetMyWalletQuery(string Email) : IQuery<WalletResponse>;

public sealed record GetMyStatementQuery(string Email, int Page, int Limit) : IQuery<LoyaltyStatementPage>;

internal sealed class LoyaltyCustomerHandlers(ILoyaltyRepository loyalty, ILoyaltyQueries queries, ITenantContext tenantContext, IUnitOfWork unitOfWork, TimeProvider clock)
    : IQueryHandler<GetMyWalletQuery, WalletResponse>,
      IQueryHandler<GetMyStatementQuery, LoyaltyStatementPage>
{
    public async Task<Result<WalletResponse>> Handle(GetMyWalletQuery request, CancellationToken cancellationToken)
    {
        var program = await loyalty.GetProgramAsync(cancellationToken) ?? LoyaltyProgram.DefaultFor(tenantContext.RequiredTenantId);
        var account = await EnsureAsync(request.Email, cancellationToken);
        var now = clock.GetUtcNow();
        var progress = LoyaltyTiers.Progress(program.Tiers, account.OrdersCount, account.FlowerKinds.Count, account.TotalSpentCents);
        var next = await queries.NextExpiringAsync(account.Id, now, cancellationToken);

        return new WalletResponse(
            program.Enabled,
            program.Enabled,
            LoyaltyMapping.ToResponse(account) with { CashbackBalance = account.AvailableCashback(now) },
            new TierView(progress.Current is { } c ? LoyaltyTierDto.From(c) : null, progress.Next is { } n ? LoyaltyTierDto.From(n) : null, progress.Progress, progress.Missing),
            new RedeemableView(account.AvailableCashback(now), program.CashbackMinRedemption, program.MaxCashbackPercentOfOrder),
            new ExpiringView(next, null));
    }

    public async Task<Result<LoyaltyStatementPage>> Handle(GetMyStatementQuery request, CancellationToken cancellationToken)
    {
        var account = await EnsureAsync(request.Email, cancellationToken);
        var (items, total) = await queries.StatementAsync(account.Id, request.Page, request.Limit, cancellationToken);
        return new LoyaltyStatementPage(items, total, request.Page, request.Limit, LoyaltyMapping.Pages(total, request.Limit));
    }

    /// <summary>A carteira nasce na primeira consulta (como o <c>ensure</c> do Nest).</summary>
    private async Task<LoyaltyAccount> EnsureAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = email.Trim().ToLowerInvariant();
        if (await loyalty.FindAsync(normalized, cancellationToken) is { } existing)
        {
            return existing;
        }

        var account = LoyaltyAccount.Open(tenantContext.RequiredTenantId, normalized, clock.GetUtcNow());
        loyalty.Add(account);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return account;
        }
        catch (UniqueConstraintViolationException)
        {
            // Duas abas abriram a carteira ao mesmo tempo: a outra venceu, usamos a dela.
            return await loyalty.FindAsync(normalized, cancellationToken) ?? throw new InvalidOperationException("Carteira sumiu após conflito.");
        }
    }
}

// ── Expiração (job) ──

public sealed record ExpireLoyaltyCreditsCommand(Guid AccountId) : ICommand<long>;

internal sealed class ExpireLoyaltyCreditsHandler(ILoyaltyRepository loyalty, IUnitOfWork unitOfWork, TimeProvider clock) : ICommandHandler<ExpireLoyaltyCreditsCommand, long>
{
    public async Task<Result<long>> Handle(ExpireLoyaltyCreditsCommand request, CancellationToken cancellationToken) =>
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var account = await loyalty.GetByIdForUpdateAsync(request.AccountId, ct);
            if (account is null)
            {
                return Result.Success(0L);
            }

            var expired = account.ExpireCredits(clock.GetUtcNow());
            loyalty.Track(account);
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Success(expired);
        }, cancellationToken);
}

public static class LoyaltyAuditActions
{
    public const string ProgramUpdated = "loyalty.program_updated";
    public const string Adjusted = "loyalty.adjusted";
}
