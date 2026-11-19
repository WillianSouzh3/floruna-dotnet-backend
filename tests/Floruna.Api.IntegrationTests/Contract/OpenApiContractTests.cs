using System.Text.Json;
using System.Text.Json.Nodes;
using Floruna.Api.IntegrationTests.Support;

namespace Floruna.Api.IntegrationTests.Contract;

/// <summary>
/// RNF-10: o contrato HTTP é versionado no repositório (<c>docs/openapi/v1.json</c>). Qualquer mudança no
/// <c>openapi.json</c> gerado quebra este teste com um resumo legível; remover operação é sinalizado como
/// QUEBRA DE CONTRATO (o front, o BFF e integrações dependem dela). Aceitar a mudança é um ato deliberado:
/// rode com <c>UPDATE_OPENAPI_SNAPSHOT=1</c> e o diff do arquivo vai para a revisão do PR.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OpenApiContractTests(FlorunaApiFactory factory)
{
    private static readonly string SnapshotPath = Path.Combine(RepositoryRoot(), "docs", "openapi", "v1.json");

    [Fact]
    public async Task Generated_contract_matches_the_versioned_snapshot()
    {
        var current = Normalize(JsonNode.Parse(await factory.CreateClient().GetStringAsync("/api/openapi/v1.json"))!);
        var currentText = current.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";

        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI_SNAPSHOT") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)!);
            await File.WriteAllTextAsync(SnapshotPath, currentText);
            return;
        }

        File.Exists(SnapshotPath).ShouldBeTrue($"Snapshot inexistente: gere com UPDATE_OPENAPI_SNAPSHOT=1 ({SnapshotPath}).");
        var snapshotText = await File.ReadAllTextAsync(SnapshotPath);
        if (snapshotText == currentText)
        {
            return;
        }

        var before = Operations(JsonNode.Parse(snapshotText)!);
        var after = Operations(current);
        var removed = before.Except(after).ToList();
        var added = after.Except(before).ToList();
        var summary = string.Join('\n', new[]
        {
            removed.Count > 0 ? $"QUEBRA DE CONTRATO, operações removidas: {string.Join(", ", removed)}" : null,
            added.Count > 0 ? $"Operações novas: {string.Join(", ", added)}" : null,
            removed.Count == 0 && added.Count == 0 ? "Mesmas operações; mudaram parâmetros ou schemas." : null,
            "Se a mudança é intencional: UPDATE_OPENAPI_SNAPSHOT=1 dotnet test e revise o diff de docs/openapi/v1.json.",
        }.Where(line => line is not null));
        Assert.Fail(summary);
    }

    [Fact]
    public async Task Every_operation_is_documented_with_its_responses()
    {
        var document = JsonNode.Parse(await factory.CreateClient().GetStringAsync("/api/openapi/v1.json"))!;

        var undocumented = document["paths"]!.AsObject()
            .SelectMany(path => path.Value!.AsObject().Select(op => (Op: $"{op.Key.ToUpperInvariant()} {path.Key}", Responses: op.Value?["responses"])))
            .Where(x => x.Responses is null || x.Responses.AsObject().Count == 0)
            .Select(x => x.Op)
            .ToList();

        undocumented.ShouldBeEmpty();
    }

    internal static SortedSet<string> Operations(JsonNode document) =>
        new(document["paths"]!.AsObject().SelectMany(path => path.Value!.AsObject()
            .Where(op => op.Key is "get" or "post" or "put" or "patch" or "delete")
            .Select(op => $"{op.Key.ToUpperInvariant()} {path.Key}")), StringComparer.Ordinal);

    /// <summary>Ordena as chaves: o snapshot só muda quando o CONTRATO muda, não quando a ordem de geração muda.</summary>
    private static JsonNode Normalize(JsonNode node) => node switch
    {
        JsonObject obj => new JsonObject(obj.Where(p => p.Key != "servers").OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, p.Value is null ? null : Normalize(p.Value)))),
        JsonArray array => new JsonArray([.. array.Select(item => item is null ? null : Normalize(item))]),
        _ => node.DeepClone(),
    };

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Floruna.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada.");
    }
}
