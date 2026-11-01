using Ways.Application.Caja;

namespace Ways.Application.Tests.Caja;

public class DetalleDePagoDelLibroTests
{
    [Fact]
    public void UnPagoSinProveedorNiPersonaMuestraSoloElConcepto()
    {
        Assert.Equal(
            "Luz de octubre",
            DetalleDePagoDelLibro.Armar("Luz de octubre", null, null, null, null));
    }

    [Fact]
    public void UnPagoAProveedorMuestraSuRazonSocialEnLugarDelConcepto()
    {
        Assert.Equal(
            "Distribuidora SA",
            DetalleDePagoDelLibro.Armar("Factura 123", 4, "Distribuidora SA", null, null));
    }

    [Fact]
    public void UnPagoDeSueldoMuestraElNombreDeLaPersonaEnLugarDelConcepto()
    {
        Assert.Equal(
            "Ana Gómez",
            DetalleDePagoDelLibro.Armar("Quincena", null, null, 9, "Ana Gómez"));
    }

    [Fact]
    public void UnProveedorOUnaPersonaDadosDeBajaSeMuestranComoNoDisponiblesSinPerderLaFila()
    {
        Assert.Equal(
            "(proveedor no disponible)",
            DetalleDePagoDelLibro.Armar("Factura 1", 4, null, null, null));
        Assert.Equal(
            "(persona no disponible)",
            DetalleDePagoDelLibro.Armar("Quincena", null, null, 9, null));
    }
}
