using Ways.Domain.Common;
using Ways.Domain.CuentaCorriente;

namespace Ways.Domain.Tests.CuentaCorriente;

/// <summary>
/// stage-7-cuenta-corriente, Slice 4 (task 4.5, design decisión 8; spec:
/// ajustes-de-cuenta-corriente / Ajuste Requires A Detalle) — pura, sin base de datos.
/// </summary>
public class ReglaDeAjusteDeCuentaTests
{
    [Fact]
    public void UnDetalleVacioSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() => ReglaDeAjusteDeCuenta.Validar(50m, string.Empty));
        Assert.Equal("ajuste_detalle_requerido", excepcion.Codigo);
        Assert.Equal(400, excepcion.EstadoHttp);
    }

    [Fact]
    public void UnDetalleNuloSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() => ReglaDeAjusteDeCuenta.Validar(50m, null));
        Assert.Equal("ajuste_detalle_requerido", excepcion.Codigo);
    }

    [Fact]
    public void UnDetalleSoloDeEspaciosSeConsideraFaltante()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() => ReglaDeAjusteDeCuenta.Validar(50m, "     "));
        Assert.Equal("ajuste_detalle_requerido", excepcion.Codigo);
    }

    [Fact]
    public void UnDetalleDeMenosDeCincoCaracteresSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() => ReglaDeAjusteDeCuenta.Validar(50m, "abcd"));
        Assert.Equal("ajuste_detalle_requerido", excepcion.Codigo);
    }

    [Fact]
    public void UnDetalleDeExactamenteCincoCaracteresEsAceptado()
    {
        var excepcion = Record.Exception(() => ReglaDeAjusteDeCuenta.Validar(50m, "abcde"));
        Assert.Null(excepcion);
    }

    [Fact]
    public void UnDetalleConEspaciosAlBordeSeRecortaAntesDeMedirLaLongitud()
    {
        // "  ab  " recortado queda en "ab" (2 chars) — tiene que rechazarse igual que "ab" sin
        // padding, no colarse por el largo crudo del string sin recortar.
        var excepcion = Assert.Throws<ErrorDominio>(() => ReglaDeAjusteDeCuenta.Validar(50m, "  ab  "));
        Assert.Equal("ajuste_detalle_requerido", excepcion.Codigo);
    }

    [Fact]
    public void UnImporteCeroSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() => ReglaDeAjusteDeCuenta.Validar(0m, "Detalle válido"));
        Assert.Equal("ajuste_importe_invalido", excepcion.Codigo);
    }

    [Fact]
    public void UnImportePositivoConDetalleValidoEsAceptado()
    {
        var excepcion = Record.Exception(() => ReglaDeAjusteDeCuenta.Validar(100m, "Descuento por reclamo"));
        Assert.Null(excepcion);
    }

    [Fact]
    public void UnImporteNegativoConDetalleValidoEsAceptado()
    {
        var excepcion = Record.Exception(() => ReglaDeAjusteDeCuenta.Validar(-50m, "Descuento por reclamo"));
        Assert.Null(excepcion);
    }
}
