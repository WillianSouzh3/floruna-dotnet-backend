using System.Security.Cryptography;
using System.Text;
using Floruna.Domain.Ordering;
using Floruna.SharedKernel;

namespace Floruna.Domain.CustomOrders;

/// <summary>Opções do configurador (contrato do Nest: valores em texto, validados contra estas listas).</summary>
public static class CustomOrderOptions
{
    public static readonly IReadOnlySet<string> Occasions = Set("aniversario", "romance", "luto", "agradecimento", "nascimento", "casamento", "outro");
    public static readonly IReadOnlySet<string> Containers = Set("cesta_vime", "caixa", "vaso", "buque_papel", "outro");
    public static readonly IReadOnlySet<string> Sizes = Set("pequeno", "medio", "grande");
    public static readonly IReadOnlySet<string> ColorPalettes = Set("brancas", "rosa_pastel", "vermelhas_vibrantes", "tons_outono", "mix_colorido");
    public static readonly IReadOnlySet<string> FlowerPreferences = Set("rosas", "girassois", "lirios", "orquideas", "hortensias", "sem_preferencia");
    public static readonly IReadOnlySet<string> BudgetRanges = Set("ate_100", "100_200", "200_350", "acima_350", "sem_preferencia");
    public static readonly IReadOnlySet<string> Statuses = Set("pending", "quoted", "approved", "rejected");

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
}

public sealed record CustomOrderSpec(
    string CustomerName, string CustomerPhone, string? CustomerEmail, string? Occasion, string ContainerType, string Size,
    IReadOnlyList<string> ColorPalette, IReadOnlyList<string> FlowerPreferences, string? Ribbon, string? CardMessage,
    string? BudgetRange, DateTimeOffset? DesiredDate, string? Notes);

/// <summary>
/// Solicitação de pedido personalizado (cesta/arranjo montado no configurador da vitrine).
/// O <see cref="Status"/> é a visão simples do painel; o detalhe da negociação mora nos orçamentos versionados.
/// </summary>
public sealed class CustomOrderRequest : AggregateRoot<Guid>, ITenantOwned
{
    private CustomOrderRequest()
        : base(Guid.Empty)
    {
        CustomerName = CustomerPhone = CustomerEmail = ContainerType = Size = Ribbon = CardMessage = Notes = Status = AdminNotes = string.Empty;
        ColorPalette = [];
        FlowerPreferences = [];
    }

    private CustomOrderRequest(TenantId tenantId, CustomOrderSpec s, DateTimeOffset now)
        : base(Guid.CreateVersion7())
    {
        TenantId = tenantId;
        CustomerName = s.CustomerName.Trim();
        CustomerPhone = s.CustomerPhone.Trim();
        CustomerEmail = s.CustomerEmail?.Trim().ToLowerInvariant() ?? string.Empty;
        Occasion = s.Occasion;
        ContainerType = s.ContainerType;
        Size = s.Size;
        ColorPalette = [.. s.ColorPalette.Distinct(StringComparer.Ordinal)];
        FlowerPreferences = [.. s.FlowerPreferences.Distinct(StringComparer.Ordinal)];
        Ribbon = s.Ribbon?.Trim() ?? string.Empty;
        CardMessage = s.CardMessage?.Trim() ?? string.Empty;
        BudgetRange = s.BudgetRange;
        DesiredDate = s.DesiredDate;
        Notes = s.Notes?.Trim() ?? string.Empty;
        Status = "pending";
        AdminNotes = string.Empty;
        CreatedAt = UpdatedAt = now;
    }

    public TenantId TenantId { get; private set; }

    public string CustomerName { get; private set; }

    public string CustomerPhone { get; private set; }

    public string CustomerEmail { get; private set; }

    public string? Occasion { get; private set; }

    public string ContainerType { get; private set; }

    public string Size { get; private set; }

    public List<string> ColorPalette { get; private set; }

    public List<string> FlowerPreferences { get; private set; }

    public string Ribbon { get; private set; }

    public string CardMessage { get; private set; }

    public string? BudgetRange { get; private set; }

    public DateTimeOffset? DesiredDate { get; private set; }

    public string Notes { get; private set; }

    /// <summary>Contrato: <c>pending, quoted, approved, rejected</c>.</summary>
    public string Status { get; private set; }

    public long? QuotedPrice { get; private set; }

    public string AdminNotes { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static CustomOrderRequest Submit(TenantId tenantId, CustomOrderSpec spec, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var request = new CustomOrderRequest(tenantId, spec, now);
        request.Raise(new CustomOrderRequested(request.Id, tenantId, now));
        return request;
    }

    /// <summary>Ajuste manual do painel (como o PATCH do Nest).</summary>
    public void Annotate(string? status, long? quotedPrice, string? adminNotes, DateTimeOffset now)
    {
        Status = status ?? Status;
        QuotedPrice = quotedPrice ?? QuotedPrice;
        AdminNotes = adminNotes?.Trim() ?? AdminNotes;
        UpdatedAt = now;
    }

    /// <summary>A negociação (orçamentos) mantém a visão simples do painel em dia.</summary>
    public void QuoteChanged(QuoteState state, long total, DateTimeOffset now)
    {
        (Status, QuotedPrice) = state switch
        {
            QuoteState.Published => ("quoted", total),
            QuoteState.Accepted or QuoteState.Converted => ("approved", total),
            QuoteState.Rejected => ("rejected", QuotedPrice),
            _ => (Status, QuotedPrice),
        };
        UpdatedAt = now;
    }
}

public sealed record CustomOrderRequested(Guid RequestId, TenantId TenantId, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public enum QuoteState
{
    Draft,
    Published,
    Accepted,
    Rejected,
    Expired,
    Converted,
}

public sealed record QuoteLine(string Description, int Quantity, long UnitPrice)
{
    public long LineTotal => Quantity * UnitPrice;
}

/// <summary>
/// Orçamento versionado (RN-74, D-018). Máquina de estados:
/// <c>draft → published → accepted | rejected | expired</c>, e <c>accepted → converted</c> (uma vez).
/// Depois de publicado, nada muda: revisar = nova versão (a proposta que o cliente viu continua legível).
/// </summary>
public sealed class CustomQuote : AggregateRoot<Guid>, ITenantOwned
{
    public const int DefaultValidityDays = 7;

    private readonly List<QuoteLine> _lines = [];

    private CustomQuote()
        : base(Guid.Empty)
    {
        Notes = ResponseTokenHash = DecisionReason = string.Empty;
    }

    private CustomQuote(TenantId tenantId, Guid requestId, int version, DateTimeOffset now)
        : base(Guid.CreateVersion7())
    {
        TenantId = tenantId;
        RequestId = requestId;
        Version = version;
        State = QuoteState.Draft;
        Notes = ResponseTokenHash = DecisionReason = string.Empty;
        CreatedAt = UpdatedAt = now;
    }

    public TenantId TenantId { get; private set; }

    public Guid RequestId { get; private set; }

    public int Version { get; private set; }

    public IReadOnlyList<QuoteLine> Lines => _lines.AsReadOnly();

    public long Total { get; private set; }

    public string Notes { get; private set; }

    public QuoteState State { get; private set; }

    public DateTimeOffset? ValidUntil { get; private set; }

    /// <summary>SHA-256 do token do link. O token em si só existe na resposta do "publicar".</summary>
    public string ResponseTokenHash { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public Guid? ConvertedOrderId { get; private set; }

    public string DecisionReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<CustomQuote> Draft(TenantId tenantId, Guid requestId, int version, IReadOnlyList<QuoteLine> lines, string? notes, DateTimeOffset now)
    {
        var quote = new CustomQuote(tenantId, requestId, version, now);
        var set = quote.SetContent(lines, notes);
        return set.IsFailure ? set.Error : quote;
    }

    public Result UpdateDraft(IReadOnlyList<QuoteLine> lines, string? notes, DateTimeOffset now)
    {
        if (State != QuoteState.Draft)
        {
            return QuoteErrors.NotEditable;
        }

        UpdatedAt = now;
        return SetContent(lines, notes);
    }

    /// <summary>Envia ao cliente. Devolve o token do link UMA vez; o banco guarda só o hash.</summary>
    public Result<string> Publish(int validityDays, DateTimeOffset now)
    {
        if (State != QuoteState.Draft)
        {
            return QuoteErrors.CannotPublish(State);
        }

        var token = Base64Url(RandomNumberGenerator.GetBytes(24));
        ResponseTokenHash = Hash(token);
        State = QuoteState.Published;
        PublishedAt = UpdatedAt = now;
        ValidUntil = now.AddDays(validityDays);
        Raise(new CustomQuoteChanged(Id, RequestId, TenantId, State, now));
        return token;
    }

    /// <summary>Resposta do cliente pelo link. Fora do prazo, o orçamento EXPIRA (e o erro é devolvido).</summary>
    public Result Decide(string token, bool accept, string? reason, DateTimeOffset now)
    {
        if (State != QuoteState.Published)
        {
            return QuoteErrors.AlreadyAnswered;
        }

        if (!CanBeReadWith(token))
        {
            return QuoteErrors.InvalidLink;
        }

        if (ValidUntil <= now)
        {
            Close(QuoteState.Expired, "prazo de validade encerrado", now);
            return QuoteErrors.Expired;
        }

        Close(accept ? QuoteState.Accepted : QuoteState.Rejected, reason ?? string.Empty, now);
        return Result.Success();
    }

    /// <summary>Uma versão nova foi publicada: esta deixa de valer (o cliente não aceita o preço antigo).</summary>
    public void Supersede(int newVersion, DateTimeOffset now)
    {
        if (State == QuoteState.Published)
        {
            Close(QuoteState.Expired, $"substituído pela versão {newVersion}", now);
        }
    }

    /// <summary>Job de expiração.</summary>
    public bool ExpireIfDue(DateTimeOffset now)
    {
        if (State != QuoteState.Published || ValidUntil > now)
        {
            return false;
        }

        Close(QuoteState.Expired, "prazo de validade encerrado", now);
        return true;
    }

    /// <summary>Liga o orçamento aceito ao pedido. Uma vez só: dois cliques não viram duas vendas.</summary>
    public Result Convert(OrderId orderId, DateTimeOffset now)
    {
        if (ConvertedOrderId is not null)
        {
            return QuoteErrors.AlreadyConverted;
        }

        if (State != QuoteState.Accepted)
        {
            return QuoteErrors.NotAccepted;
        }

        State = QuoteState.Converted;
        ConvertedOrderId = orderId.Value;
        UpdatedAt = now;
        Raise(new CustomQuoteChanged(Id, RequestId, TenantId, State, now));
        return Result.Success();
    }

    public bool CanBeReadWith(string? token) =>
        !string.IsNullOrEmpty(token) && ResponseTokenHash.Length > 0 &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(token)), Encoding.ASCII.GetBytes(ResponseTokenHash));

    private void Close(QuoteState state, string reason, DateTimeOffset now)
    {
        State = state;
        DecisionReason = reason.Trim().Length > 300 ? reason.Trim()[..300] : reason.Trim();
        DecidedAt = UpdatedAt = now;
        Raise(new CustomQuoteChanged(Id, RequestId, TenantId, state, now));
    }

    private Result SetContent(IReadOnlyList<QuoteLine> lines, string? notes)
    {
        if (lines.Count == 0)
        {
            return Error.Validation("VALIDATION_ERROR", "Informe ao menos um item do orçamento.");
        }

        if (lines.Any(l => l.Quantity < 1 || l.UnitPrice < 0 || string.IsNullOrWhiteSpace(l.Description)))
        {
            return Error.Validation("VALIDATION_ERROR", "Itens do orçamento inválidos.");
        }

        _lines.Clear();
        _lines.AddRange(lines.Select(l => l with { Description = l.Description.Trim() }));
        Total = _lines.Sum(l => l.LineTotal);
        var trimmed = (notes ?? string.Empty).Trim();
        Notes = trimmed.Length > 1000 ? trimmed[..1000] : trimmed;
        return Result.Success();
    }

    private static string Hash(string token) => System.Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) => System.Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record CustomQuoteChanged(Guid QuoteId, Guid RequestId, TenantId TenantId, QuoteState State, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public static class QuoteErrors
{
    public static readonly Error NotFound = Error.NotFound("NOT_FOUND", "Orçamento não encontrado.");
    public static readonly Error RequestNotFound = Error.NotFound("NOT_FOUND", "Solicitação não encontrada.");
    public static readonly Error NotEditable = Error.Conflict("CONFLICT", "Orçamento já enviado não pode ser alterado: publique uma nova versão.");
    public static readonly Error AlreadyAnswered = Error.Conflict("CONFLICT", "Este orçamento já foi respondido.");
    public static readonly Error InvalidLink = Error.Validation("VALIDATION_ERROR", "Link de resposta inválido.");
    public static readonly Error Expired = Error.Conflict("CONFLICT", "Este orçamento venceu. Peça uma nova proposta à loja.");
    public static readonly Error NotAccepted = Error.Conflict("CONFLICT", "Só um orçamento aceito pode virar pedido.");
    public static readonly Error AlreadyConverted = Error.Conflict("CONFLICT", "Este orçamento já virou pedido.");
    public static readonly Error AlreadyAcceptedVersion = Error.Conflict("CONFLICT", "Outra versão deste orçamento já foi aceita.");
    public static readonly Error OrderAlreadyLinked = Error.Conflict("CONFLICT", "Este pedido já está ligado a outro orçamento.");

    public static Error CannotPublish(QuoteState state) =>
        Error.Conflict("CONFLICT", $"Orçamento em \"{state.ToString().ToLowerInvariant()}\" não pode ser enviado de novo.");
}

public interface ICustomOrderRepository
{
    void Add(CustomOrderRequest request);

    void Add(CustomQuote quote);

    Task<CustomOrderRequest?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Trava a SOLICITAÇÃO: toda operação de orçamento passa por aqui, então numeração de versão,
    /// publicação (que substitui a anterior) e decisões ficam em fila por solicitação.
    /// </summary>
    Task<CustomOrderRequest?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<CustomQuote?> GetQuoteAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Trava a solicitação do orçamento e devolve os dois LIDOS DO BANCO depois da trava
    /// (descarta cópias rastreadas, lição da ADR 0007). Null se o orçamento não existe.
    /// </summary>
    Task<(CustomOrderRequest Request, CustomQuote Quote)?> LockQuoteAsync(Guid quoteId, CancellationToken cancellationToken);

    Task<IReadOnlyList<CustomQuote>> QuotesOfAsync(Guid requestId, CancellationToken cancellationToken);
}
