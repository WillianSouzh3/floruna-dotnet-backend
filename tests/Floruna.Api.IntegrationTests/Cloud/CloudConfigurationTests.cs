using System.Text;
using Floruna.Infrastructure.Persistence;
using Floruna.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Floruna.Api.IntegrationTests.Cloud;

/// <summary>Configuração no formato que a AWS entrega (campos do segredo do RDS, segredo gerado pelo Secrets Manager).</summary>
public sealed class CloudConfigurationTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => KeyValuePair.Create(v.Key, (string?)v.Value))).Build();

    [Fact]
    public void Ready_connection_string_wins()
    {
        DatabaseConnection.Compose(Config(("ConnectionStrings:Floruna", "Host=local"), ("Database:Host", "rds"))).ShouldBe("Host=local");
    }

    [Fact]
    public void Connection_string_is_built_from_the_rds_secret_fields_with_tls()
    {
        var composed = new NpgsqlConnectionStringBuilder(DatabaseConnection.Compose(Config(
            ("Database:Host", "floruna.abc.sa-east-1.rds.amazonaws.com"), ("Database:Port", "5432"), ("Database:Name", "floruna"),
            ("Database:Username", "floruna"), ("Database:Password", "p@ss;word="))));

        composed.Host.ShouldBe("floruna.abc.sa-east-1.rds.amazonaws.com");
        composed.Password.ShouldBe("p@ss;word="); // caracteres especiais escapados pelo builder, não por concatenação
        composed.SslMode.ShouldBe(SslMode.Require);
    }

    [Fact]
    public void Nothing_configured_yields_empty_so_startup_validation_fails()
    {
        DatabaseConnection.Compose(Config()).ShouldBeEmpty();
    }

    [Fact]
    public void Base64_key_is_used_as_is_and_long_random_passphrase_is_derived()
    {
        var raw = new byte[32];
        Random.Shared.NextBytes(raw);
        EncryptionKeyMaterial.Derive(Convert.ToBase64String(raw)).ShouldBe(raw);

        var passphrase = new string('x', 20) + Guid.NewGuid().ToString("N"); // 52 caracteres
        var derived = EncryptionKeyMaterial.Derive(passphrase);
        derived.ShouldNotBeNull().Length.ShouldBe(32);
        EncryptionKeyMaterial.Derive(passphrase).ShouldBe(derived); // determinístico: a mesma chave em toda task
    }

    [Theory]
    [InlineData("")]
    [InlineData("senha-curta-digitada-por-humano")]
    public void Short_or_empty_secret_is_rejected(string configured)
    {
        EncryptionKeyMaterial.Derive(configured).ShouldBeNull();
        Encoding.UTF8.GetByteCount(configured).ShouldBeLessThan(EncryptionKeyMaterial.MinPassphraseLength);
    }
}
