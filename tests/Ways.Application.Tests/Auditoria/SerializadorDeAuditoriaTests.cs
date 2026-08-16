using System.Text.Json;
using Ways.Application.Auditoria;
using Ways.Domain.Ventas;

namespace Ways.Application.Tests.Auditoria;

/// <summary>
/// stage-14-auditoria-trazabilidad, Slice 1 (task 1.15, design decisión 6): las cuatro reglas del
/// único <see cref="SerializadorDeAuditoria.Opciones"/> — sin DB, sin fixture.
/// </summary>
public class SerializadorDeAuditoriaTests
{
    /// <summary>Mutation target (slice 1, row 3): <c>DictionaryKeyPolicy</c> → <c>PropertyNamingPolicy</c>
    /// (design decisión 6, un no-op sobre un diccionario). Las claves de producción de
    /// <c>PayloadDeAuditoria</c> ya son snake_case a mano, así que una entrada snake_case NO
    /// discrimina la política (pasa igual con las dos) — <c>mutation-proof-tests</c> regla 3: la
    /// clave de entrada es PascalCase, algo que <see cref="RegistroDeAuditoria"/> jamás dejaría
    /// pasar en producción (su propio invariante de snake_case), pero que SÍ deja ver si
    /// <see cref="SerializadorDeAuditoria.Opciones"/> la transformó — la prueba de que la
    /// política correcta está activa, ruteada por debajo de esa validación aguas arriba.</summary>
    [Fact]
    public void LasClavesSerializanEnSnakeCase()
    {
        var valor = new Dictionary<string, object?> { ["IdListaPrecio"] = 1, ["vigente_desde"] = "x" };

        var json = SerializadorDeAuditoria.Serializar(valor);

        Assert.Contains("\"id_lista_precio\"", json);
        Assert.DoesNotContain("\"IdListaPrecio\"", json);
        Assert.Contains("\"vigente_desde\"", json);
    }

    [Fact]
    public void UnEnumSerializaComoSuEtiquetaDeBase()
    {
        var valor = new Dictionary<string, object?> { ["estado"] = EstadoComprobante.Emitido };

        var json = SerializadorDeAuditoria.Serializar(valor);

        Assert.Contains("\"estado\":\"emitido\"", json);
        Assert.DoesNotContain("Emitido", json);
    }

    [Fact]
    public void UnDateTimeOffsetSerializaIso8601()
    {
        var momento = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var valor = new Dictionary<string, object?> { ["creado_el"] = momento };

        var json = SerializadorDeAuditoria.Serializar(valor);

        Assert.Contains("2026-08-14T12:00:00", json);
    }

    [Fact]
    public void UnNuloExplicitoSeDistingueDeUnaClaveAusente()
    {
        var conNuloExplicito = new Dictionary<string, object?> { ["deleted_at"] = null };
        var sinLaClave = new Dictionary<string, object?>();

        var jsonConNulo = SerializadorDeAuditoria.Serializar(conNuloExplicito);
        var jsonSinClave = SerializadorDeAuditoria.Serializar(sinLaClave);

        using var documento = JsonDocument.Parse(jsonConNulo);
        Assert.True(documento.RootElement.TryGetProperty("deleted_at", out var elemento));
        Assert.Equal(JsonValueKind.Null, elemento.ValueKind);

        Assert.Equal("{}", jsonSinClave);
    }
}
