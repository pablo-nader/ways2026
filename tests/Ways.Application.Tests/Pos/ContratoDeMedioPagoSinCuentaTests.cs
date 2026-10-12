using System.Reflection;
using System.Text.Json;
using Ways.Application.Catalogos;
using Ways.Application.Pos;
using Ways.Domain.Catalogos;

namespace Ways.Application.Tests.Pos;

/// <summary>
/// Las columnas de cuenta de tesorería, comisión y retenciones de <c>medios_pago</c> todavía no
/// viajan: ni en la instantánea del POS ni en el listado y el alta del catálogo. Fija la lista exacta
/// de propiedades de los tres contratos, así que agregar una (o quitar una existente) falla acá.
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
    public void ElListadoDelCatalogoConservaSuFormaSinCamposDeCuenta()
    {
        Assert.Equal(
            ["Orden", "Comportamiento", "AdmiteVuelto", "RequiereReferencia", "RecargoPorcentaje", "PorcentajeFacturacion"],
            Propiedades(typeof(MedioPagoListado)));
    }

    [Fact]
    public void ElAltaDelCatalogoConservaSuFormaSinCamposDeCuenta()
    {
        Assert.Equal(
            ["Orden", "Comportamiento", "AdmiteVuelto", "RequiereReferencia", "RecargoPorcentaje", "PorcentajeFacturacion"],
            Propiedades(typeof(MedioPagoAlta)));
    }
}
