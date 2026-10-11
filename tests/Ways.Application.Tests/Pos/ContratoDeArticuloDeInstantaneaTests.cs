using System.Text.Json;
using Ways.Application.Ofertas;
using Ways.Application.Pos;

namespace Ways.Application.Tests.Pos;

/// <summary>
/// Forma serializada de los precios de la instantánea (<see cref="PrecioDeListaDeInstantanea"/> y
/// el artículo del formato original, <see cref="ArticuloDeInstantaneaLegada"/>). La AUSENCIA de la
/// clave <c>escalones</c> es contrato: un POS ya instalado lee el formato original tal cual. Se
/// serializa con <see cref="JsonSerializerDefaults.Web"/>, la convención de nombres de la API.
/// </summary>
public class ContratoDeArticuloDeInstantaneaTests
{
    private static readonly JsonSerializerOptions Opciones = new(JsonSerializerDefaults.Web);

    private static readonly EscalonDeCantidad Escalon =
        new(6m, 80m, 20m, [new OfertaAplicadaDto(4, "Volumen 6", 20m)]);

    private static ArticuloDeInstantaneaLegada CrearArticuloLegado(IReadOnlyList<EscalonDeCantidad>? escalones) =>
        new(7, "art-7", "Artículo", ["7791234560001"], 100m, 100m, 0m, [], 3, 21m, escalones);

    private static PrecioDeListaDeInstantanea CrearPrecio(IReadOnlyList<EscalonDeCantidad>? escalones) =>
        new(2, 100m, 100m, 0m, [], escalones);

    /// <summary>Cláusula: el <c>JsonIgnore(WhenWritingNull)</c> de
    /// <see cref="ArticuloDeInstantaneaLegada.Escalones"/> — sin oferta por volumen la clave queda
    /// ausente, byte por byte el formato que leen los POS ya instalados.</summary>
    [Fact]
    public void SinEscalonesLaClaveNoViajaEnElArticuloLegado()
    {
        var json = JsonSerializer.Serialize(CrearArticuloLegado(null), Opciones);

        Assert.DoesNotContain("escalones", json);
        Assert.Contains("\"precioFinal\":100", json);
    }

    [Fact]
    public void ConEscalonesLaCurvaViajaEnElArticuloLegado()
    {
        var json = JsonSerializer.Serialize(CrearArticuloLegado([Escalon]), Opciones);

        Assert.Contains("\"escalones\":[{\"cantidadDesde\":6,\"precioFinal\":80,\"descuentoUnitario\":20", json);
        Assert.Contains("\"idOferta\":4", json);
    }

    /// <summary>Cláusula: el <c>JsonIgnore(WhenWritingNull)</c> de
    /// <see cref="PrecioDeListaDeInstantanea.Escalones"/> — se repite por artículo y por lista, así
    /// que la clave ausente es lo que mantiene acotado el payload.</summary>
    [Fact]
    public void SinEscalonesLaClaveNoViajaEnElPrecioDeLista()
    {
        var json = JsonSerializer.Serialize(CrearPrecio(null), Opciones);

        Assert.DoesNotContain("escalones", json);
        Assert.Contains("\"idListaPrecio\":2", json);
    }

    /// <summary>Cláusulas: los <c>JsonIgnore(WhenWritingNull)</c> de
    /// <see cref="PrecioDeListaDeInstantanea.EscalonesDeFamilia"/> y
    /// <see cref="ArticuloDeInstantanea.IdFamilia"/> — sin oferta de familia ni familia el JSON es
    /// el de siempre, byte por byte.</summary>
    [Fact]
    public void SinFamiliaNiGrillaLasClavesNuevasNoViajan()
    {
        var articulo = new ArticuloDeInstantanea(
            7, "art-7", "Artículo", [], 3, 21m, true, Ways.Domain.Articulos.UnidadVenta.Unidad, [CrearPrecio(null)]);

        var json = JsonSerializer.Serialize(articulo, Opciones);

        Assert.DoesNotContain("idFamilia", json);
        Assert.DoesNotContain("escalonesDeFamilia", json);
    }

    [Fact]
    public void ConFamiliaYGrillaViajanConNombreEnCamelCase()
    {
        var celda = new EscalonDeFamilia(2m, 1m, 1750m, 250m, [new OfertaAplicadaDto(4, "2 x 3500", 250m)]);
        var precio = new PrecioDeListaDeInstantanea(2, 2000m, 2000m, 0m, [], null, [celda]);
        var articulo = new ArticuloDeInstantanea(
            7, "art-7", "Artículo", [], 3, 21m, true, Ways.Domain.Articulos.UnidadVenta.Unidad, [precio], 9);

        var json = JsonSerializer.Serialize(articulo, Opciones);

        Assert.Contains("\"idFamilia\":9", json);
        Assert.Contains(
            "\"escalonesDeFamilia\":[{\"cantidadDeFamiliaDesde\":2,\"cantidadDesde\":1,\"precioFinal\":1750,\"descuentoUnitario\":250", json);
    }

    [Fact]
    public void ConEscalonesLaCurvaViajaEnElPrecioDeLista()
    {
        var json = JsonSerializer.Serialize(CrearPrecio([Escalon]), Opciones);

        Assert.Contains("\"escalones\":[{\"cantidadDesde\":6,\"precioFinal\":80,\"descuentoUnitario\":20", json);
    }
}
