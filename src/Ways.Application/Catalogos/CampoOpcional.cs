using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ways.Application.Catalogos;

/// <summary>
/// Un campo de un cuerpo JSON que distingue "no vino" de "vino en null": <c>Presente</c> es true cuando la clave está
/// en el cuerpo, con valor o con null. Un <c>int?</c> común no puede distinguirlos.
/// </summary>
[JsonConverter(typeof(CampoOpcionalJsonConverterFactory))]
public readonly record struct CampoOpcional<T>(bool Presente, T? Valor)
{
    public static CampoOpcional<T> De(T? valor) => new(true, valor);
}

public sealed class CampoOpcionalJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type tipo) =>
        tipo.IsGenericType && tipo.GetGenericTypeDefinition() == typeof(CampoOpcional<>);

    public override JsonConverter CreateConverter(Type tipo, JsonSerializerOptions opciones) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(CampoOpcionalJsonConverter<>).MakeGenericType(tipo.GetGenericArguments()[0]))!;

    private sealed class CampoOpcionalJsonConverter<T> : JsonConverter<CampoOpcional<T>>
    {
        // Sin esto, un null del cuerpo no llega al conversor y el campo quedaría como ausente.
        public override bool HandleNull => true;

        public override CampoOpcional<T> Read(ref Utf8JsonReader lector, Type tipo, JsonSerializerOptions opciones) =>
            lector.TokenType == JsonTokenType.Null
                ? CampoOpcional<T>.De(default)
                : CampoOpcional<T>.De(JsonSerializer.Deserialize<T>(ref lector, opciones));

        public override void Write(Utf8JsonWriter escritor, CampoOpcional<T> valor, JsonSerializerOptions opciones) =>
            JsonSerializer.Serialize(escritor, valor.Valor, opciones);
    }
}
