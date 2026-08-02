using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;

namespace Ways.Domain.Tests.Organizacion;

public class PlantillaDeAprovisionamientoTests
{
    [Fact]
    public void V1TieneExactamenteElAreaGeneral()
    {
        Assert.Equal("General", PlantillaDeAprovisionamiento.V1.Area);
    }

    [Fact]
    public void V1TieneExactamenteLosDosMediosDePagoAprobados()
    {
        var medios = PlantillaDeAprovisionamiento.V1.MediosDePago;

        Assert.Equal(2, medios.Count);

        var efectivo = Assert.Single(medios, m => m.Nombre == "Efectivo");
        Assert.Equal(ComportamientoMedioPago.Efectivo, efectivo.Comportamiento);
        Assert.True(efectivo.AdmiteVuelto);
        Assert.False(efectivo.RequiereReferencia);

        var transferencia = Assert.Single(medios, m => m.Nombre == "Transferencia");
        Assert.Equal(ComportamientoMedioPago.Electronico, transferencia.Comportamiento);
        Assert.False(transferencia.AdmiteVuelto);
        Assert.True(transferencia.RequiereReferencia);
    }

    [Fact]
    public void LosItemsDiferidosEstanDeclaradosNoDescartadosEnSilencio()
    {
        Assert.Equal(2, PlantillaV1.ItemsDiferidos.Count);
        Assert.Contains(PlantillaV1.ItemsDiferidos, i => i.Contains("lista_precio_general"));
        Assert.Contains(PlantillaV1.ItemsDiferidos, i => i.Contains("cliente_consumidor_final"));
    }
}
