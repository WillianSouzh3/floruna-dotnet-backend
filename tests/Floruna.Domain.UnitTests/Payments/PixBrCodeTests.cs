using Floruna.Domain.Payments;

namespace Floruna.Domain.UnitTests.Payments;

/// <summary>
/// BR Code PIX (padrão EMV do Banco Central). Escrito ANTES da implementação (TDD),
/// com os mesmos casos do teste Jest do Nest + o vetor oficial do CRC16-CCITT.
/// </summary>
public sealed class PixBrCodeTests
{
    [Fact]
    public void Crc16_matches_the_ccitt_false_reference_vector()
    {
        PixBrCode.Crc16("123456789").ShouldBe("29B1");
    }

    [Fact]
    public void Builds_a_fixed_amount_single_use_code_with_key_amount_txid_and_valid_crc()
    {
        var code = PixBrCode.Build(new PixCharge(12_550, "order-123", "  pix@example.com  ", "Flôra & Lis Comércio", "Fortaleza/CE")).Value;

        code.ShouldStartWith("000201010212");                      // formato 01, uso único (valor fixo)
        code.ShouldContain("0014br.gov.bcb.pix0115pix@example.com"); // chave sem espaços
        code.ShouldContain("5406125.50");                            // valor em reais com ponto
        code.ShouldContain("5303986");                               // BRL
        code.ShouldContain("5919Flora  Lis Comercio");               // sem acento nem símbolo
        code.ShouldContain("6011FortalezaCE");
        code.ShouldContain("62120508order123");                      // txid só alfanumérico
        code[..^4].ShouldEndWith("6304");
        code[^4..].ShouldBe(PixBrCode.Crc16(code[..^4]));
    }

    [Fact]
    public void Truncates_name_city_and_txid_to_the_standard_limits()
    {
        var code = PixBrCode.Build(new PixCharge(1, "abcdefghijklmnopqrstuvwxyz-123", "123e4567-e89b-12d3-a456-426614174000",
            "ABCDEFGHIJKLMNOPQRSTUVWXYZ", "ABCDEFGHIJKLMNOP")).Value;

        code.ShouldContain("5925ABCDEFGHIJKLMNOPQRSTUVWXY"); // nome: 25
        code.ShouldContain("6015ABCDEFGHIJKLMNO");           // cidade: 15
        code.ShouldContain("62290525abcdefghijklmnopqrstuvwxy"); // txid: 25
    }

    [Fact]
    public void Empty_txid_after_cleanup_becomes_three_asterisks()
    {
        PixBrCode.Build(new PixCharge(100, "---", "pix@example.com", "Flora", "Fortaleza")).Value.ShouldContain("62070503***");
    }

    [Theory]
    [InlineData(0, "pix@x.com", "Loja", "Cidade")]
    [InlineData(100, " ", "Loja", "Cidade")]
    [InlineData(100, "pix@x.com", "&&&", "Cidade")]
    public void Invalid_charges_are_rejected(long amount, string key, string name, string city)
    {
        PixBrCode.Build(new PixCharge(amount, "tx", key, name, city)).IsFailure.ShouldBeTrue();
    }
}
