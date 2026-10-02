using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ways.Application.Pos;

/// <summary>
/// ETag fuerte de <see cref="InstantaneaDePos"/>: SHA-256 del contenido serializado con
/// <see cref="InstantaneaDePos.Momento"/> neutralizado. <c>Momento</c> cambia en cada pedido aunque
/// nada más cambie; incluirlo haría que la etiqueta no coincidiera nunca.
///
/// No hay cursor por fila a propósito: <c>clientes.saldo</c> se actualiza por SQL directo sin
/// tocar <c>updated_at</c> y un precio u oferta con vigencia cambia con el paso del tiempo sin que
/// cambie ninguna fila, así que solo comparar el contenido ya resuelto es correcto. El servidor
/// sigue armando la instantánea completa en cada pedido; lo que se ahorra es la transferencia.
/// </summary>
public static class EtiquetaDeInstantanea
{
    private static readonly JsonSerializerOptions Opciones = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Calcular(InstantaneaDePos instantanea)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(instantanea with { Momento = default }, Opciones);
        return $"\"{Convert.ToHexStringLower(SHA256.HashData(bytes))}\"";
    }
}
