using System.Text.RegularExpressions;
using Floruna.SharedKernel;

namespace Floruna.Domain.Platform;

/// <summary>Domínio próprio de uma loja (<c>www.minhaflora.com.br</c>), normalizado e validado.</summary>
public sealed partial record CustomDomainName
{
    public const string VerifyPrefix = "_flora-verify";

    private CustomDomainName(string value) => Value = value;

    public string Value { get; }

    /// <param name="raw">O que o lojista digitou (aceita "https://", barra final, porta).</param>
    /// <param name="platformDomain">Domínio da plataforma, que não pode ser reivindicado.</param>
    public static Result<CustomDomainName> Create(string? raw, string platformDomain)
    {
        var domain = Normalize(raw);
        if (!Pattern().IsMatch(domain))
        {
            return CustomDomainErrors.Invalid;
        }

        var platform = platformDomain?.ToLowerInvariant() ?? string.Empty;
        if (platform.Length > 0 && (domain == platform || domain.EndsWith("." + platform, StringComparison.Ordinal)))
        {
            return CustomDomainErrors.BelongsToPlatform;
        }

        if (ReservedTld().IsMatch(domain))
        {
            return CustomDomainErrors.Reserved;
        }

        return new CustomDomainName(domain);
    }

    /// <summary>
    /// Onde publicar o TXT: no domínio-base. Quem informa <c>www.loja.com.br</c>
    /// cria o registro em <c>_flora-verify.loja.com.br</c>.
    /// </summary>
    public static string VerificationRecordFor(string domain) =>
        $"{VerifyPrefix}.{(domain.StartsWith("www.", StringComparison.Ordinal) ? domain[4..] : domain)}";

    private static string Normalize(string? raw)
    {
        var domain = (raw ?? string.Empty).Trim().ToLowerInvariant();
        domain = SchemePrefix().Replace(domain, string.Empty);
        domain = PathSuffix().Replace(domain, string.Empty);
        domain = PortSuffix().Replace(domain, string.Empty);
        return domain.TrimEnd('.');
    }

    [GeneratedRegex(@"^(?=.{4,253}$)([a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,}$")]
    private static partial Regex Pattern();

    [GeneratedRegex(@"\.(local|localhost|internal|test|invalid|example)$")]
    private static partial Regex ReservedTld();

    [GeneratedRegex("^https?://")]
    private static partial Regex SchemePrefix();

    [GeneratedRegex("/.*$")]
    private static partial Regex PathSuffix();

    [GeneratedRegex(@":\d+$")]
    private static partial Regex PortSuffix();
}

public static class CustomDomainErrors
{
    public static readonly Error Invalid = Error.Validation(
        "VALIDATION_ERROR", "Domínio inválido. Use o formato \"loja.com.br\" ou \"www.loja.com.br\".",
        ["Domínio inválido. Use o formato \"loja.com.br\" ou \"www.loja.com.br\"."]);

    public static readonly Error BelongsToPlatform = Error.Validation(
        "VALIDATION_ERROR", "Este domínio pertence à plataforma. Use o endereço da sua loja ou um domínio próprio.");

    public static readonly Error Reserved = Error.Validation("VALIDATION_ERROR", "Domínio reservado.");

    public static readonly Error TakenByAnotherStore = Error.Conflict(
        "CONFLICT", "Este domínio já está em uso por outra loja da plataforma.");

    public static readonly Error NothingPending = Error.Validation(
        "VALIDATION_ERROR", "Nenhum domínio pendente de verificação.");

    public static readonly Error DnsUnavailable = Error.Validation(
        "VALIDATION_ERROR", "Não foi possível consultar o DNS agora. Tente novamente em instantes.");

    public static Error TxtNotFound(string record) => Error.Validation(
        "VALIDATION_ERROR", $"Nenhum registro TXT encontrado em {record}. A propagação do DNS pode levar alguns minutos.");

    public static Error TxtMismatch(string record) => Error.Validation(
        "VALIDATION_ERROR",
        $"O registro TXT em {record} não confere. Verifique se o valor copiado está exatamente igual ao exibido no painel.");
}
