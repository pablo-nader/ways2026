using System.Text.Json;
using Ways.Application.Ofertas;
using Ways.Application.Pos;

namespace Ways.Application.Tests.Pos;

/// <summary>
/// Forma serializada de <see cref="ArticuloDeInstantanea"/> — la parte del contrato de
/// <c>GET /api/pos/instantanea</c> que el dispositivo guarda tal cual en IndexedDB (sin versión de
/// esquema ni validación), así que la AUSENCIA de la clave es contrato, no detalle. Se serializa
/// con <see cref="JsonSerializerDefaults.Web"/>, que es la convención de nombres que usa el
/// pipeline de la API (<c>Program.cs</c> solo le agrega el converter de enums, que este contrato
/// no toca).
/// </summary>
public class ContratoDeArticuloDeInstantaneaTests
{
    private static readonly JsonSerializerOptions Opciones = new(JsonSerializerDefaults.Web);

    private static ArticuloDeInstantanea CrearArticulo(IReadOnlyList<EscalonDeCantidad>? escalones) =>
        new(7, "art-7", "Artículo", ["7791234560001"], 100m, 100m, 0m, [], 3, 21m, escalones);

    /// <summary>Cláusula: el <c>JsonIgnore(WhenWritingNull)</c> de
    /// <see cref="ArticuloDeInstantanea.Escalones"/> — para un artículo sin oferta por volumen
    /// (la mayoría del catálogo) la clave queda AUSENTE, no <c>"escalones":null</c> repetido miles de
    /// veces, y el payload vuelve a ser byte por byte el de antes de esta etapa (lo que mantiene
    /// válido el snapshot de un dispositivo que quedó offline cruzando el deploy).</summary>
    [Fact]
    public void SinEscalonesLaClaveNoViajaEnElJson()
    {
        var json = JsonSerializer.Serialize(CrearArticulo(null), Opciones);

        Assert.DoesNotContain("escalones", json);
        Assert.Contains("\"precioFinal\":100", json);
    }

    /// <summary>La contracara: con curva, la clave viaja completa (umbral + precio final +
    /// descuento + ofertas aplicadas).</summary>
    [Fact]
    public void ConEscalonesLaCurvaViajaEnElJson()
    {
        var escalon = new EscalonDeCantidad(6m, 80m, 20m, [new OfertaAplicadaDto(4, "Volumen 6", 20m)]);

        var json = JsonSerializer.Serialize(CrearArticulo([escalon]), Opciones);

        Assert.Contains("\"escalones\":[{\"cantidadDesde\":6,\"precioFinal\":80,\"descuentoUnitario\":20", json);
        Assert.Contains("\"idOferta\":4", json);
    }
}
