using Ways.Application.Compras;
using Ways.Application.CuentaCorriente;
using Ways.Application.Exportacion;
using Ways.Application.Ventas;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Ventas;

namespace Ways.Application.Tests.Exportacion;

/// <summary>
/// stage-11-exportacion-reportes, Slice 3: reglas de mapeo de <see cref="ExportacionDeListados"/> —
/// DB-free, mismo criterio (<c>PoliticaDeRoles</c>) que el resto de <c>Application.Tests</c>. Cubre
/// las reglas de <c>null</c> que cada mapper documenta (Detalle vacío, sentinela <c>"-"</c> de
/// <c>NumeroExterno</c>) y la conversión de <see cref="Celda.FechaHora"/> (design decisión 3:
/// <see cref="DateTimeOffset"/> → hora local sin offset).
/// </summary>
public class ExportacionDeListadosTests
{
    private static readonly TimeZoneInfo ZonaBuenosAires = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");

    private static readonly ContextoDeExportacion Contexto = new(
        Empresa: "1",
        PuntoVenta: null,
        Desde: new DateOnly(2026, 8, 1),
        Hasta: new DateOnly(2026, 8, 1),
        ZonaHoraria: "America/Argentina/Buenos_Aires",
        Usuario: "admin@ways.test",
        GeneradoEl: new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero),
        Cobertura: null);

    // ---- Ventas ------------------------------------------------------------------------------

    [Fact]
    public void VentasConvierteLaFechaALaZonaLocalYDescartaElOffset()
    {
        var instante = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var fila = new ComprobanteListado(1, 1L, "0001-00000001", EstadoComprobante.Emitido, instante, 1, 1, 100m);

        var tabla = ExportacionDeListados.De([fila], Contexto, ZonaBuenosAires);

        Assert.Equal(new DateTime(2026, 8, 1, 9, 0, 0), tabla.Filas[0][1].Valor);
    }

    // ---- Compras -------------------------------------------------------------------------------

    [Fact]
    public void ComprasConNumeroExternoNuloQuedaComoElSentinela()
    {
        var fila = new CompraListada(1, 1, 1, null, EstadoCompra.Borrador, null, 500m);

        var tabla = ExportacionDeListados.De([fila], Contexto, ZonaBuenosAires);

        Assert.Equal("-", tabla.Filas[0][0].Valor);
    }

    [Fact]
    public void ComprasConNumeroExternoPasaElTextoTalCual()
    {
        var fila = new CompraListada(1, 1, 1, "0001-00000042", EstadoCompra.Confirmada, null, 500m);

        var tabla = ExportacionDeListados.De([fila], Contexto, ZonaBuenosAires);

        Assert.Equal("0001-00000042", tabla.Filas[0][0].Valor);
    }

    [Fact]
    public void ComprasConvierteLaFechaDeRecepcionALaZonaLocalYDescartaElOffset()
    {
        var instante = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var fila = new CompraListada(1, 1, 1, "0001-00000001", EstadoCompra.Confirmada, instante, 500m);

        var tabla = ExportacionDeListados.De([fila], Contexto, ZonaBuenosAires);

        Assert.Equal(new DateTime(2026, 8, 1, 9, 0, 0), tabla.Filas[0][2].Valor);
    }

    [Fact]
    public void ComprasConFechaDeRecepcionNulaQuedaComoCeldaVacia()
    {
        var fila = new CompraListada(1, 1, 1, "0001-00000001", EstadoCompra.Borrador, null, 500m);

        var tabla = ExportacionDeListados.De([fila], Contexto, ZonaBuenosAires);

        Assert.Null(tabla.Filas[0][2].Valor);
    }

    // ---- Estado de cuenta -----------------------------------------------------------------------

    [Fact]
    public void EstadoDeCuentaConDetalleNuloQuedaComoCeldaVacia()
    {
        var fila = new MovimientoDeCuentaCorriente(
            1, new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero), TipoMovimientoCc.Consumo, 100m, 100m,
            null, null, null);

        var tabla = ExportacionDeListados.De([fila], Contexto, ZonaBuenosAires);

        Assert.Null(tabla.Filas[0][4].Valor);
    }

    [Fact]
    public void EstadoDeCuentaConDetallePasaElTextoTalCual()
    {
        var fila = new MovimientoDeCuentaCorriente(
            1, new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero), TipoMovimientoCc.Ajuste, 100m, 100m,
            "Ajuste de prueba", null, EtiquetaDeAjuste.Manual);

        var tabla = ExportacionDeListados.De([fila], Contexto, ZonaBuenosAires);

        Assert.Equal("Ajuste de prueba", tabla.Filas[0][4].Valor);
    }

    [Fact]
    public void EstadoDeCuentaConvierteLaFechaALaZonaLocalYDescartaElOffset()
    {
        var instante = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var fila = new MovimientoDeCuentaCorriente(
            1, instante, TipoMovimientoCc.Consumo, 100m, 100m, null, null, null);

        var tabla = ExportacionDeListados.De([fila], Contexto, ZonaBuenosAires);

        Assert.Equal(new DateTime(2026, 8, 1, 9, 0, 0), tabla.Filas[0][0].Valor);
    }
}
