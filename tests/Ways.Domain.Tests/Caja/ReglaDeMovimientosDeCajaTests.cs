using Ways.Domain.Caja;
using Ways.Domain.Common;

namespace Ways.Domain.Tests.Caja;

/// <summary>
/// stage-6-turnos-caja, Slice 2 (task 2.2, task 2.7, design decisión 8) — pura, sin base de
/// datos. Exhaustivo sobre importe/motivo por tipo, incluido el borde de 5 caracteres.
/// </summary>
public class ReglaDeMovimientosDeCajaTests
{
    // ---- ExigirImporteValido -----------------------------------------------------------------

    [Fact]
    public void RetiroConImportePositivoEsValido() =>
        ReglaDeMovimientosDeCaja.ExigirImporteValido(TipoMovimientoCaja.Retiro, 200m);

    [Fact]
    public void RefuerzoConImportePositivoEsValido() =>
        ReglaDeMovimientosDeCaja.ExigirImporteValido(TipoMovimientoCaja.Refuerzo, 1m);

    [Fact]
    public void RetiroConImporteCeroSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeMovimientosDeCaja.ExigirImporteValido(TipoMovimientoCaja.Retiro, 0m));
        Assert.Equal("movimiento_de_caja_importe_invalido", excepcion.Codigo);
        Assert.Equal(400, excepcion.EstadoHttp);
    }

    [Fact]
    public void RefuerzoConImporteNegativoSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeMovimientosDeCaja.ExigirImporteValido(TipoMovimientoCaja.Refuerzo, -1m));
        Assert.Equal("movimiento_de_caja_importe_invalido", excepcion.Codigo);
    }

    [Fact]
    public void AperturaDeCajonConImporteCeroEsValida() =>
        ReglaDeMovimientosDeCaja.ExigirImporteValido(TipoMovimientoCaja.AperturaCajon, 0m);

    [Fact]
    public void AperturaDeCajonConImportePositivoSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeMovimientosDeCaja.ExigirImporteValido(TipoMovimientoCaja.AperturaCajon, 50m));
        Assert.Equal("movimiento_de_caja_importe_invalido", excepcion.Codigo);
    }

    [Fact]
    public void AperturaDeCajonConImporteNegativoSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeMovimientosDeCaja.ExigirImporteValido(TipoMovimientoCaja.AperturaCajon, -1m));
        Assert.Equal("movimiento_de_caja_importe_invalido", excepcion.Codigo);
    }

    // ---- ExigirMotivoValido -------------------------------------------------------------------

    [Fact]
    public void RetiroConMotivoVacioSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeMovimientosDeCaja.ExigirMotivoValido(TipoMovimientoCaja.Retiro, ""));
        Assert.Equal("movimiento_de_caja_sin_motivo", excepcion.Codigo);
        Assert.Equal(400, excepcion.EstadoHttp);
    }

    [Fact]
    public void RetiroConMotivoNuloSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeMovimientosDeCaja.ExigirMotivoValido(TipoMovimientoCaja.Retiro, null));
        Assert.Equal("movimiento_de_caja_sin_motivo", excepcion.Codigo);
    }

    [Fact]
    public void RefuerzoConMotivoMasCortoQueElMinimoSeRechaza()
    {
        // design decisión 8: la longitud mínima (5) aplica uniforme a los 3 tipos, no solo a
        // apertura_cajon — "abc" (3 caracteres) también falla acá.
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeMovimientosDeCaja.ExigirMotivoValido(TipoMovimientoCaja.Refuerzo, "abc"));
        Assert.Equal("movimiento_de_caja_sin_motivo", excepcion.Codigo);
    }

    [Fact]
    public void RetiroConMotivoValidoEsAceptado() =>
        ReglaDeMovimientosDeCaja.ExigirMotivoValido(TipoMovimientoCaja.Retiro, "pago a proveedor en efectivo");

    [Fact]
    public void AperturaDeCajonConMotivoCortoSeRechazaConCodigoPropio()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeMovimientosDeCaja.ExigirMotivoValido(TipoMovimientoCaja.AperturaCajon, "abc"));
        Assert.Equal("motivo_de_apertura_cajon_invalido", excepcion.Codigo);
        Assert.Equal(400, excepcion.EstadoHttp);
    }

    [Fact]
    public void AperturaDeCajonEnElBordeDeCincoCaracteresEsValida() =>
        ReglaDeMovimientosDeCaja.ExigirMotivoValido(TipoMovimientoCaja.AperturaCajon, "abcde");

    [Fact]
    public void AperturaDeCajonConCuatroCaracteresSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeMovimientosDeCaja.ExigirMotivoValido(TipoMovimientoCaja.AperturaCajon, "abcd"));
        Assert.Equal("motivo_de_apertura_cajon_invalido", excepcion.Codigo);
    }

    [Fact]
    public void UnMotivoConEspaciosAlrededorSeRecortaAntesDeMedir()
    {
        // "  ab  ".Trim() = "ab" (2 caracteres) — no alcanza el mínimo aunque el string crudo
        // tenga 6 caracteres de largo.
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            ReglaDeMovimientosDeCaja.ExigirMotivoValido(TipoMovimientoCaja.Retiro, "  ab  "));
        Assert.Equal("movimiento_de_caja_sin_motivo", excepcion.Codigo);
    }

    [Fact]
    public void AperturaDeCajonConMotivoValidoEsAceptada() =>
        ReglaDeMovimientosDeCaja.ExigirMotivoValido(TipoMovimientoCaja.AperturaCajon, "conteo inicial de turno");
}
