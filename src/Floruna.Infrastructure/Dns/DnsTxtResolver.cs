using DnsClient;
using Floruna.Application.Abstractions;

namespace Floruna.Infrastructure.Dns;

/// <summary>Consulta TXT com o DnsClient (o .NET não tem consulta TXT nativa). Singleton: o cliente é thread-safe.</summary>
internal sealed class DnsTxtResolver : IDnsTxtResolver
{
    private readonly LookupClient _client = new(new LookupClientOptions { UseCache = false, Timeout = TimeSpan.FromSeconds(5) });

    public async Task<TxtLookup> ResolveAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.QueryAsync(name, QueryType.TXT, cancellationToken: cancellationToken);
            // Um registro TXT pode vir em vários pedaços: junta cada registro.
            var values = response.Answers.TxtRecords().Select(r => string.Concat(r.Text)).ToList();
            return values.Count == 0
                ? new TxtLookup(TxtLookupStatus.NotFound, [])
                : new TxtLookup(TxtLookupStatus.Found, values);
        }
        catch (DnsResponseException)
        {
            return new TxtLookup(TxtLookupStatus.Failed, []);
        }
    }
}
