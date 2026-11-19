using System.Security.Cryptography;
using System.Text;
using Floruna.Domain.Catalog;
using Floruna.Domain.Ordering;
using Floruna.SharedKernel;

namespace Floruna.Domain.Substitutions;

/// <summary>
/// Política de substituição da loja (D-015). Escreve o limite do que é "equivalente": uma troca que
/// muda o valor do item além da tolerância deixa de ser equivalente e exige resposta do cliente,
/// mesmo de quem pré-aprovou.
/// </summary>
public sealed class SubstitutionPolicy : Entity<Guid>, ITenantOwned
{
    private SubstitutionPolicy()
        : base(Guid.Empty)
    {
    }

    private SubstitutionPolicy(TenantId tenantId)
        : base(Guid.CreateVersion7())
    {
        TenantId = tenantId;
        Version = 1;
        DefaultMode = SubstitutionMode.AskMe;
        PreapprovedTolerancePercent = 10;
        ConsentWindowHours = 24;
    }

    public TenantId TenantId { get; private set; }

    /// <summary>Cada alteração é uma versão nova; a proposta guarda a versão sob a qual foi decidida.</summary>
    public int Version { get; private set; }

    /// <summary>Modo sugerido na vitrine quando o comprador não escolhe.</summary>
    public SubstitutionMode DefaultMode { get; private set; }

    /// <summary>Variação máxima de valor (para mais OU para menos) aceita sem perguntar a quem pré-aprovou.</summary>
    public int PreapprovedTolerancePercent { get; private set; }

    /// <summary>Horas que a pergunta fica de pé esperando resposta.</summary>
    public int ConsentWindowHours { get; private set; }

    public static SubstitutionPolicy DefaultFor(TenantId tenantId) => new(tenantId);

    public Result Publish(SubstitutionMode? defaultMode, int? tolerancePercent, int? consentWindowHours)
    {
        if (tolerancePercent is < 0 or > 100 || consentWindowHours is < 1 or > 168)
        {
            return Error.Validation("VALIDATION_ERROR", "Tolerância de 0 a 100% e prazo de 1 a 168 horas.");
        }

        DefaultMode = defaultMode ?? DefaultMode;
        PreapprovedTolerancePercent = tolerancePercent ?? PreapprovedTolerancePercent;
        ConsentWindowHours = consentWindowHours ?? ConsentWindowHours;
        Version++;
        return Result.Success();
    }
}

public enum ProposalState
{
    Proposed,
    Approved,
    Rejected,
    Expired,
    Applied,
}

public sealed record ProposalInput(
    TenantId TenantId, OrderId OrderId, int ItemIndex,
    ProductId OriginalProductId, string OriginalName, long OriginalUnitPrice,
    ProductId SubstituteProductId, string SubstituteName, long SubstituteUnitPrice,
    int Quantity, SubstitutionMode OrderMode, string Reason, string IdempotencyKey, bool RestockOriginal = false);

/// <summary>
/// Proposta de troca de um item do pedido. A regra que este agregado existe para garantir:
/// <b>nada é aplicado sem autorização válida</b>. Trocar rosa por lírio sem avisar é a reclamação
/// que uma floricultura leva anos para desfazer.
/// </summary>
public sealed class SubstitutionProposal : AggregateRoot<Guid>, ITenantOwned
{
    private SubstitutionProposal()
        : base(Guid.Empty)
    {
        OriginalName = SubstituteName = Reason = DecidedBy = ConsentTokenHash = IdempotencyKey = string.Empty;
    }

    private SubstitutionProposal(ProposalInput input, SubstitutionPolicy policy, DateTimeOffset now)
        : base(Guid.CreateVersion7())
    {
        TenantId = input.TenantId;
        OrderId = input.OrderId.Value;
        ItemIndex = input.ItemIndex;
        OriginalProductId = input.OriginalProductId.Value;
        OriginalName = input.OriginalName;
        SubstituteProductId = input.SubstituteProductId.Value;
        SubstituteName = input.SubstituteName;
        Quantity = input.Quantity;
        ValueDelta = (input.SubstituteUnitPrice - input.OriginalUnitPrice) * input.Quantity;
        ValueDeltaPercent = input.OriginalUnitPrice > 0
            ? Math.Round((input.SubstituteUnitPrice - input.OriginalUnitPrice) * 100m / input.OriginalUnitPrice, 2, MidpointRounding.AwayFromZero)
            : 0;
        OrderMode = input.OrderMode;
        PolicyVersion = policy.Version;
        Reason = input.Reason.Trim();
        IdempotencyKey = input.IdempotencyKey;
        RestockOriginal = input.RestockOriginal;
        DecidedBy = ConsentTokenHash = string.Empty;
        CreatedAt = now;
    }

    public TenantId TenantId { get; private set; }

    public Guid OrderId { get; private set; }

    public int ItemIndex { get; private set; }

    public Guid OriginalProductId { get; private set; }

    public string OriginalName { get; private set; }

    public Guid SubstituteProductId { get; private set; }

    public string SubstituteName { get; private set; }

    public int Quantity { get; private set; }

    /// <summary>Diferença de valor de catálogo (centavos): positivo = substituto mais caro.</summary>
    public long ValueDelta { get; private set; }

    public decimal ValueDeltaPercent { get; private set; }

    /// <summary>O que o comprador escolheu no checkout, CONGELADO na proposta.</summary>
    public SubstitutionMode OrderMode { get; private set; }

    public int PolicyVersion { get; private set; }

    public ProposalState State { get; private set; }

    public string ConsentTokenHash { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary><c>customer</c>, <c>system</c> (pré-aprovado) ou o papel de quem registrou.</summary>
    public string DecidedBy { get; private set; }

    public string Reason { get; private set; }

    public string IdempotencyKey { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? AppliedAt { get; private set; }

    /// <summary>O original volta ao estoque ao aplicar? (Normalmente não: falta dele é o motivo da troca.)</summary>
    public bool RestockOriginal { get; private set; }

    /// <summary>
    /// <c>exact</c> recusa na origem; <c>preapproved</c> dentro da tolerância já nasce aprovada (sem link);
    /// o resto vira pergunta com prazo. O token do link volta UMA vez (o banco guarda o hash).
    /// </summary>
    public static Result<(SubstitutionProposal Proposal, string? ConsentToken)> Propose(ProposalInput input, SubstitutionPolicy policy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(policy);
        if (input.OrderMode == SubstitutionMode.Exact)
        {
            return SubstitutionErrors.ExactOrder;
        }

        if (input.Quantity < 1 || input.OriginalProductId == input.SubstituteProductId)
        {
            return Error.Validation("VALIDATION_ERROR", "Substituição inválida: quantidade positiva e um produto diferente do original.");
        }

        var proposal = new SubstitutionProposal(input, policy, now);
        string? token = null;
        if (input.OrderMode == SubstitutionMode.Preapproved && Math.Abs(proposal.ValueDeltaPercent) <= policy.PreapprovedTolerancePercent)
        {
            proposal.Decide(ProposalState.Approved, "system", now);
        }
        else
        {
            token = Base64Url(RandomNumberGenerator.GetBytes(24));
            proposal.ConsentTokenHash = Hash(token);
            proposal.State = ProposalState.Proposed;
            proposal.ExpiresAt = now.AddHours(policy.ConsentWindowHours);
            proposal.Raise(new SubstitutionChanged(proposal.Id, input.OrderId, input.TenantId, ProposalState.Proposed, now));
        }

        return (proposal, token);
    }

    public Result DecideByCustomer(string token, bool approve, DateTimeOffset now)
    {
        if (State != ProposalState.Proposed)
        {
            return SubstitutionErrors.AlreadyAnswered;
        }

        if (!CanBeReadWith(token))
        {
            return SubstitutionErrors.InvalidLink;
        }

        if (ExpiresAt <= now)
        {
            Decide(ProposalState.Expired, "system", now);
            return SubstitutionErrors.Expired;
        }

        Decide(approve ? ProposalState.Approved : ProposalState.Rejected, "customer", now);
        return Result.Success();
    }

    /// <summary>A loja registra a resposta (ex.: o cliente respondeu por telefone). Em "perguntar antes", só recusa.</summary>
    public Result DecideByOperator(bool approve, string role, string? reason, DateTimeOffset now)
    {
        if (State != ProposalState.Proposed)
        {
            return SubstitutionErrors.AlreadyAnswered;
        }

        if (approve && OrderMode == SubstitutionMode.AskMe)
        {
            return SubstitutionErrors.CustomerMustApprove;
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            Reason = reason.Trim().Length > 300 ? reason.Trim()[..300] : reason.Trim();
        }

        Decide(approve ? ProposalState.Approved : ProposalState.Rejected, role, now);
        return Result.Success();
    }

    public bool ExpireIfDue(DateTimeOffset now)
    {
        if (State != ProposalState.Proposed || ExpiresAt > now)
        {
            return false;
        }

        Decide(ProposalState.Expired, "system", now);
        return true;
    }

    /// <summary>A ÚNICA porta para aplicar: só a partir de aprovada.</summary>
    public Result MarkApplied(DateTimeOffset now)
    {
        if (State != ProposalState.Approved)
        {
            return SubstitutionErrors.NotApproved;
        }

        State = ProposalState.Applied;
        AppliedAt = now;
        Raise(new SubstitutionChanged(Id, new OrderId(OrderId), TenantId, State, now));
        return Result.Success();
    }

    public bool CanBeReadWith(string? token) =>
        !string.IsNullOrEmpty(token) && ConsentTokenHash.Length > 0 &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(token)), Encoding.ASCII.GetBytes(ConsentTokenHash));

    private void Decide(ProposalState state, string by, DateTimeOffset now)
    {
        State = state;
        DecidedBy = by;
        DecidedAt = now;
        Raise(new SubstitutionChanged(Id, new OrderId(OrderId), TenantId, state, now));
    }

    private static string Hash(string token) => System.Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) => System.Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record SubstitutionChanged(Guid ProposalId, OrderId OrderId, TenantId TenantId, ProposalState State, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public static class SubstitutionErrors
{
    public static readonly Error NotFound = Error.NotFound("NOT_FOUND", "Proposta não encontrada.");
    public static readonly Error ExactOrder = Error.Conflict("CONFLICT", "Este pedido foi fechado como exato: nenhuma substituição é permitida.");
    public static readonly Error AlreadyAnswered = Error.Conflict("CONFLICT", "Esta proposta já foi respondida.");
    public static readonly Error InvalidLink = Error.Validation("VALIDATION_ERROR", "Link de resposta inválido.");
    public static readonly Error Expired = Error.Conflict("CONFLICT", "O prazo para responder esta proposta terminou. Fale com a loja.");
    public static readonly Error CustomerMustApprove = Error.Conflict("CONFLICT", "Pedido marcado como \"perguntar antes\": a aprovação precisa vir do comprador.");
    public static readonly Error NotApproved = Error.Conflict("CONFLICT", "Só uma proposta aprovada pode ser aplicada (e uma vez só).");
    public static readonly Error WrongOrderState = Error.Conflict("CONFLICT", "Substituição só em pedido confirmado ou em preparo.");
    public static readonly Error ItemNotInOrder = Error.Validation("VALIDATION_ERROR", "O item original não está neste pedido (ou a quantidade passa da vendida).");
}

public interface ISubstitutionRepository
{
    Task<SubstitutionPolicy?> GetPolicyAsync(CancellationToken cancellationToken);

    void Add(SubstitutionPolicy policy);

    void Add(SubstitutionProposal proposal);

    Task<SubstitutionProposal?> FindByKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    Task<SubstitutionProposal?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Proposta travada e lida do banco: decisão do cliente, da loja, job e aplicação entram em fila.</summary>
    Task<SubstitutionProposal?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<SubstitutionProposal>> ByOrderAsync(Guid orderId, CancellationToken cancellationToken);
}
