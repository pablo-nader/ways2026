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
    [Fact]
    public void LasClavesSerializanEnSnakeCase()
    {
        var valor = new Dictionary<string, object?> { ["id_lista_precio"] = 1, ["vigente_desde"] = "x" };

        var json = SerializadorDeAuditoria.Serializar(valor);

        Assert.Contains("\"id_lista_precio\"", json);
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
