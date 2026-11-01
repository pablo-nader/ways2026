using System.Reflection;
using System.Text.Json;
using Ways.Application.Catalogos;
using Ways.Application.Pos;
using Ways.Domain.Catalogos;

namespace Ways.Application.Tests.Pos;

/// <summary>
/// La cuenta de tesorería, la comisión y las retenciones de <c>medios_pago</c> viajan en el catálogo del back office
/// pero NO en la instantánea del POS. Fija la lista exacta de propiedades de la instantánea y de su JSON, así que
/// agregar una (o quitar una existente) falla acá; los contratos del catálogo se fijan aparte, con los campos de cuenta.
/// </summary>
public class ContratoDeMedioPagoSinCuentaTests
{
    private static readonly JsonSerializerOptions Opciones = new(JsonSerializerDefaults.Web);

    private static string[] Propiedades(Type tipo) =>
        tipo.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(p => p.Name)
            .ToArray();

    [Fact]
    public void ElMedioDeLaInstantaneaConservaSuFormaSinCamposDeCuenta()
    {
        Assert.Equal(
            ["IdMedioPago", "Nombre", "Comportamiento", "AdmiteVuelto", "RequiereReferencia", "PorcentajeFacturacion"],
            Propiedades(typeof(MedioPagoDeInstantanea)));
    }

    [Fact]
    public void ElJsonDelMedioDeLaInstantaneaNoTraeClavesDeCuenta()
    {
        var json = JsonSerializer.Serialize(
            new MedioPagoDeInstantanea(1, "QR", ComportamientoMedioPago.Electronico, false, true, 0m), Opciones);

        using var documento = JsonDocument.Parse(json);
        var claves = documento.RootElement.EnumerateObject().Select(p => p.Name).ToArray();

        Assert.Equal(
            ["idMedioPago", "nombre", "comportamiento", "admiteVuelto", "requiereReferencia", "porcentajeFacturacion"], claves);
    }

    [Fact]
    public void ElListadoDelCatalogoTraeLosCamposDeCuentaPeroNoLosDeLaInstantanea()
    {
        var listado = Propiedades(typeof(MedioPagoListado));

        Assert.Equal(
            [
                "Orden", "Comportamiento", "AdmiteVuelto", "RequiereReferencia", "RecargoPorcentaje", "PorcentajeFacturacion",
                "IdCuentaTesoreria", "ComisionPorcentaje", "RetencionIibbPorcentaje", "RetencionIvaPorcentaje",
                "RetencionGananciasPorcentaje", "NombreCuentaTesoreria", "CuentaTesoreriaInactiva", "Proveedor"
            ],
            listado);
        Assert.All(
            Propiedades(typeof(MedioPagoDeInstantanea)),
            propiedad => Assert.DoesNotContain("Cuenta", propiedad));
    }

    [Fact]
    public void ElAltaDelCatalogoTraeLosCamposDeCuenta()
    {
        Assert.Equal(
            [
                "Orden", "Comportamiento", "AdmiteVuelto", "RequiereReferencia", "RecargoPorcentaje", "PorcentajeFacturacion",
                "ComisionPorcentaje", "RetencionIibbPorcentaje", "RetencionIvaPorcentaje",
                "RetencionGananciasPorcentaje", "IdCuentaTesoreria", "Proveedor"
            ],
            Propiedades(typeof(MedioPagoAlta)));
    }
}
