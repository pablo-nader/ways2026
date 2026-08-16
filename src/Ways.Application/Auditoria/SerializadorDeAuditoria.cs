using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ways.Application.Auditoria;

/// <summary>
/// Único punto de serialización de <c>valor_anterior</c>/<c>valor_nuevo</c> (design decisión 6):
/// compartido por los dos modos de <see cref="ServicioDeAuditoria"/> (EF y ADO) — un solo lugar
/// produce el documento, así que los dos caminos no pueden divergir. NO se registra
/// globalmente (no en <c>AddControllers().AddJsonOptions</c> ni en ningún <c>DbContext</c>): es
/// una instancia propia, dedicada a este único payload.
/// </summary>
public static class SerializadorDeAuditoria
{
    /// <summary><c>DictionaryKeyPolicy</c>, NO <c>PropertyNamingPolicy</c> — sobre un
    /// <c>IReadOnlyDictionary&lt;string, object?&gt;</c> (el shape de <see cref="Domain.Auditoria.RegistroDeAuditoria"/>),
    /// <c>PropertyNamingPolicy</c> es un no-op que parece una decisión: System.Text.Json aplica
    /// <c>DictionaryKeyPolicy</c> a las claves de un diccionario. El <c>JsonStringEnumConverter</c>
    /// con la misma política es lo que hace que un enum serialice como su etiqueta de base
    /// (<c>EstadoComprobante.Emitido</c> → <c>"emitido"</c>) — la "etiqueta de la base de datos",
    /// que es lo que "las claves y los valores reflejan el esquema" realmente significa.</summary>
    public static readonly JsonSerializerOptions Opciones = new()
    {
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public static string Serializar(IReadOnlyDictionary<string, object?> valor) =>
        JsonSerializer.Serialize(valor, Opciones);
}
