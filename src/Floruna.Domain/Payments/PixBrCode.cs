using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Floruna.SharedKernel;

namespace Floruna.Domain.Payments;

/// <param name="AmountCents">Valor fixo em centavos (o QR já vem com o valor).</param>
/// <param name="TxId">Identificador da cobrança (aparece no extrato).</param>
public sealed record PixCharge(long AmountCents, string TxId, string Key, string ReceiverName, string City);

/// <summary>
/// "Copia e cola" do PIX (BR Code, padrão EMV QRCPS do Banco Central), gerado SEM
/// chamar ninguém: função pura de domínio, testável em microssegundos.
/// </summary>
/// <remarks>
/// O payload é uma sequência de campos TLV (ID de 2 dígitos + tamanho de 2 dígitos + valor),
/// terminada pelo CRC16-CCITT (polinômio 0x1021, início 0xFFFF) calculado sobre tudo
/// antes dele, inclusive o próprio cabeçalho "6304".
/// </remarks>
public static partial class PixBrCode
{
    public static Result<string> Build(PixCharge charge)
    {
        ArgumentNullException.ThrowIfNull(charge);
        if (charge.AmountCents <= 0)
        {
            return Invalid("O valor da cobrança Pix deve ser positivo.");
        }

        var key = charge.Key?.Trim() ?? string.Empty;
        if (key.Length is 0 or > 77)
        {
            return Invalid("A chave Pix deve ter entre 1 e 77 caracteres.");
        }

        var name = Sanitize(charge.ReceiverName, 25);
        var city = Sanitize(charge.City, 15);
        if (name.Length == 0 || city.Length == 0)
        {
            return Invalid("Nome e cidade do recebedor Pix são obrigatórios.");
        }

        var value = (charge.AmountCents / 100m).ToString("0.00", CultureInfo.InvariantCulture);
        if (value.Length > 13)
        {
            return Invalid("O valor da cobrança excede o limite do BR Code Pix.");
        }

        var txid = NonAlphanumeric().Replace(charge.TxId ?? string.Empty, string.Empty);
        txid = txid.Length == 0 ? "***" : txid[..Math.Min(25, txid.Length)];

        var payload = new StringBuilder()
            .Append(Tlv("00", "01"))                                            // formato do payload
            .Append(Tlv("01", "12"))                                            // 12 = uso único (valor fixo)
            .Append(Tlv("26", Tlv("00", "br.gov.bcb.pix") + Tlv("01", key)))    // conta do recebedor
            .Append(Tlv("52", "0000"))                                          // categoria do comerciante
            .Append(Tlv("53", "986"))                                           // moeda: BRL
            .Append(Tlv("54", value))
            .Append(Tlv("58", "BR"))
            .Append(Tlv("59", name))
            .Append(Tlv("60", city))
            .Append(Tlv("62", Tlv("05", txid)))                                 // dados adicionais: txid
            .Append("6304")
            .ToString();

        return payload + Crc16(payload);
    }

    /// <summary>CRC16-CCITT-FALSE em hexadecimal maiúsculo, 4 dígitos.</summary>
    public static string Crc16(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var crc = 0xFFFF;
        foreach (var c in payload)
        {
            crc ^= c << 8;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1;
                crc &= 0xFFFF;
            }
        }

        return crc.ToString("X4", CultureInfo.InvariantCulture);
    }

    private static string Tlv(string id, string value) =>
        id + value.Length.ToString("D2", CultureInfo.InvariantCulture) + value;

    /// <summary>Só A–Z, 0–9 e espaço (o padrão não aceita acento): "Flôra &amp; Lis" → "Flora  Lis".</summary>
    private static string Sanitize(string? value, int max)
    {
        var decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && (char.IsAsciiLetterOrDigit(c) || c == ' '))
            {
                builder.Append(c);
            }
        }

        var clean = builder.ToString().Trim();
        return clean.Length > max ? clean[..max] : clean;
    }

    private static Error Invalid(string message) => Error.Validation("VALIDATION_ERROR", message);

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumeric();
}
