using System.Text.Json;
using System.Text.Json.Serialization;
using Floruna.Application.Catalog;

namespace Floruna.Api.Infrastructure;

/// <summary>
/// Faz o JSON distinguir "campo ausente" de "campo null" (PATCH de verdade).
/// Se a propriedade não vem no corpo, o conversor nem é chamado: fica
/// <c>Optional.Absent</c>. Se vem (mesmo como null), vira <c>Optional.Of(valor)</c>.
/// </summary>
internal sealed class OptionalJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(OptionalConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;

    private sealed class OptionalConverter<T> : JsonConverter<Optional<T>>
    {
        public override bool HandleNull => true;

        public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Optional<T>.Of(JsonSerializer.Deserialize<T>(ref reader, options)!);

        public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.Value, options);
    }
}
