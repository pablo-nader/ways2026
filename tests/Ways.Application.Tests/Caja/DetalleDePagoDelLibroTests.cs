using Ways.Application.Caja;
using Ways.Domain.Pagos;

namespace Ways.Application.Tests.Caja;

public class DetalleDePagoDelLibroTests
{
    [Fact]
    public void UnPagoSinProveedorNiPersonaMuestraCategoriaYConcepto()
    {
        Assert.Equal(
            "Servicios — Luz de octubre",
            DetalleDePagoDelLibro.Armar(CategoriaPago.Servicios, "Luz de octubre", null, null, null, null));
    }

    [Fact]
    public void UnPagoAProveedorMuestraSuRazonSocialEnLugarDelConcepto()
    {
        Assert.Equal(
            "Proveedores — Distribuidora SA",
            DetalleDePagoDelLibro.Armar(CategoriaPago.Proveedor, "Factura 123", 4, "Distribuidora SA", null, null));
    }

    [Fact]
    public void UnPagoDeSueldoMuestraElNombreDeLaPersonaEnLugarDelConcepto()
    {
        Assert.Equal(
            "Sueldos — Ana Gómez",
            DetalleDePagoDelLibro.Armar(CategoriaPago.Sueldos, "Quincena", null, null, 9, "Ana Gómez"));
    }

    [Fact]
    public void UnProveedorOUnaPersonaDadosDeBajaSeMuestranComoNoDisponiblesSinPerderLaFila()
    {
        Assert.Equal(
            "Proveedores — (proveedor no disponible)",
            DetalleDePagoDelLibro.Armar(CategoriaPago.Proveedor, "Factura 1", 4, null, null, null));
        Assert.Equal(
            "Sueldos — (persona no disponible)",
            DetalleDePagoDelLibro.Armar(CategoriaPago.Sueldos, "Quincena", null, null, 9, null));
    }
}
