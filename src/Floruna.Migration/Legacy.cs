using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;

namespace Floruna.Migration;

/// <summary>
/// IDs DETERMINÍSTICOS a partir do ObjectId do Mongo (UUID v5: SHA-1 de namespace + "tipo:id").
/// O mesmo documento vira sempre o mesmo Guid: ensaios repetidos geram as mesmas chaves e as
/// referências (pedido → produto, produto → categoria) se resolvem sem tabela de-para.
/// </summary>
public static class LegacyIds
{
    private static readonly byte[] Namespace = Guid.Parse("6f6c6f72-756e-612d-6e65-73742d6d6967").ToByteArray(true);

    public static Guid For(string kind, string legacyId)
    {
        var name = Encoding.UTF8.GetBytes($"{kind}:{legacyId}");
        var hash = SHA1.HashData([.. Namespace, .. name]);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // versão 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variante RFC 4122
        return new Guid(bytes, bigEndian: true);
    }

    public static Guid For(string kind, ObjectId legacyId) => For(kind, legacyId.ToString());
}

/// <summary>
/// Restaura campos HISTÓRICOS (id, datas, estado) em agregados criados pelas fábricas do domínio.
/// As fábricas validam o dado legado contra as regras de hoje; este restaurador só copia o que é
/// fato passado (quando foi criado, em que estado está). Fica restrito à ferramenta de migração:
/// o domínio não ganha "construtores de importação".
/// </summary>
internal static class Restore
{
    public static T Set<T>(T target, string member, object? value)
        where T : notnull
    {
        for (var type = target.GetType(); type is not null; type = type.BaseType)
        {
            var property = type.GetProperty(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property?.GetSetMethod(nonPublic: true) is { } setter)
            {
                setter.Invoke(target, [value]);
                return target;
            }

            var field = type.GetField($"<{member}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                        ?? type.GetField($"_{char.ToLowerInvariant(member[0])}{member[1..]}", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field is not null)
            {
                field.SetValue(target, value);
                return target;
            }
        }

        throw new InvalidOperationException($"{target.GetType().Name}.{member} não encontrado para restauração.");
    }

    public static TValue Get<TValue>(object target, string field)
    {
        for (var type = target.GetType(); type is not null; type = type.BaseType)
        {
            if (type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } info)
            {
                return (TValue)info.GetValue(target)!;
            }
        }

        throw new InvalidOperationException($"{target.GetType().Name}.{field} não encontrado.");
    }
}

/// <summary>Leitura tolerante de documentos legados: campo ausente, nulo ou com tipo trocado vira o padrão.</summary>
internal static class Bson
{
    public static string Str(this BsonDocument d, string field, string fallback = "") =>
        d.TryGetValue(field, out var v) && v.IsString ? v.AsString : fallback;

    public static string? NStr(this BsonDocument d, string field) =>
        d.TryGetValue(field, out var v) && v.IsString && v.AsString.Length > 0 ? v.AsString : null;

    public static long Long(this BsonDocument d, string field, long fallback = 0) =>
        d.TryGetValue(field, out var v) ? v.BsonType switch
        {
            BsonType.Int32 => v.AsInt32,
            BsonType.Int64 => v.AsInt64,
            BsonType.Double => (long)Math.Round(v.AsDouble, MidpointRounding.AwayFromZero),
            BsonType.Decimal128 => (long)Math.Round((decimal)v.AsDecimal128, MidpointRounding.AwayFromZero),
            _ => fallback,
        } : fallback;

    public static long? NLong(this BsonDocument d, string field) =>
        d.TryGetValue(field, out var v) && v.IsNumeric ? d.Long(field) : null;

    public static int Int(this BsonDocument d, string field, int fallback = 0) => (int)d.Long(field, fallback);

    public static bool Bool(this BsonDocument d, string field, bool fallback = false) =>
        d.TryGetValue(field, out var v) && v.IsBoolean ? v.AsBoolean : fallback;

    public static DateTimeOffset? Date(this BsonDocument d, string field) =>
        d.TryGetValue(field, out var v) && v.IsValidDateTime ? new DateTimeOffset(v.ToUniversalTime(), TimeSpan.Zero) : null;

    public static ObjectId? Oid(this BsonDocument d, string field) =>
        d.TryGetValue(field, out var v) ? v.BsonType switch
        {
            BsonType.ObjectId => v.AsObjectId,
            BsonType.String when ObjectId.TryParse(v.AsString, out var parsed) => parsed,
            _ => null,
        } : null;

    public static ObjectId Id(this BsonDocument d) => d["_id"].AsObjectId;

    public static IEnumerable<BsonDocument> Docs(this BsonDocument d, string field) =>
        d.TryGetValue(field, out var v) && v.IsBsonArray ? v.AsBsonArray.Where(x => x.IsBsonDocument).Select(x => x.AsBsonDocument) : [];

    public static IEnumerable<string> Strings(this BsonDocument d, string field) =>
        d.TryGetValue(field, out var v) && v.IsBsonArray ? v.AsBsonArray.Where(x => x.IsString).Select(x => x.AsString) : [];

    public static BsonDocument? Doc(this BsonDocument d, string field) =>
        d.TryGetValue(field, out var v) && v.IsBsonDocument ? v.AsBsonDocument : null;
}

/// <summary>
/// Envelope de segredo do Nest: <c>enc:v1:&lt;key-id&gt;:&lt;iv&gt;:&lt;tag&gt;:&lt;cifrado&gt;</c> (base64url,
/// AES-256-GCM, AAD = contexto). Usado só para LER o token do gateway e recifrá-lo com a chave da API .NET.
/// </summary>
internal static class NestSecrets
{
    public static string? Decrypt(string stored, string context, byte[]? key)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        if (!stored.StartsWith("enc:", StringComparison.Ordinal))
        {
            return stored; // legado em claro (antes da cifra existir no Nest)
        }

        var parts = stored.Split(':');
        if (key is null || parts.Length != 6 || parts[1] != "v1")
        {
            return null;
        }

        var iv = Base64Url(parts[3]);
        var tag = Base64Url(parts[4]);
        var cipher = Base64Url(parts[5]);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(iv, cipher, tag, plain, Encoding.UTF8.GetBytes(context));
            return Encoding.UTF8.GetString(plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            return null; // chave errada ou contexto errado: o relatório avisa
        }
    }

    private static byte[] Base64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }
}
