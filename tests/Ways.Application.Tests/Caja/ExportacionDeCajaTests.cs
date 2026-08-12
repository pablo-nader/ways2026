using Ways.Application.Caja;
using Ways.Application.Exportacion;
using Ways.Application.Gastos;
using Ways.Application.Ventas;
using Ways.Domain.Gastos;
using Ways.Domain.Ventas;

namespace Ways.Application.Tests.Caja;

/// <summary>
/// stage-11-exportacion-reportes, Slice 5b: reglas de mapeo de <see cref="ExportacionDeCaja"/> —
/// DB-free, mismo criterio (<c>PoliticaDeRoles</c>) que el resto de <c>Application.Tests</c>.
/// Cubre la conversión de <see cref="Celda.FechaHora"/> (design decisión 3) y la forma seccionada
/// de la hoja del Z-report (design: Interfaces/Contracts — <see cref="TablaExportable"/> solo
/// admite una hoja).
/// </summary>
public class ExportacionDeCajaTests
{
    private static readonly TimeZoneInfo ZonaBuenosAires = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");

    private static readonly ContextoDeExportacion Contexto = new(
        Empresa: "1",
        PuntoVenta: "PV 3",
        Desde: new DateOnly(2026, 8, 1),
        Hasta: new DateOnly(2026, 8, 1),
        ZonaHoraria: "America/Argentina/Buenos_Aires",
        Usuario: "admin@ways.test",
        GeneradoEl: new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero),
        Cobertura: null);

    // ---- G2 histórico --------------------------------------------------------------------------

    [Fact]
    public void HistoricoConvierteAperturaYCierreALaZonaLocalYDescartaElOffset()
    {
        var apertura = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var cierre = new DateTimeOffset(2026, 8, 1, 18, 0, 0, TimeSpan.Zero);
        var fila = new FilaDeHistoricoDeCajas(412, 3, apertura, cierre, 1000m, 970m, 30m, new EgresosDeTurno([], [], 0m));

        var tabla = ExportacionDeCaja.De([fila], Contexto, ZonaBuenosAires);

        Assert.Equal(new DateTime(2026, 8, 1, 9, 0, 0), tabla.Filas[0][2].Valor);
        Assert.Equal(new DateTime(2026, 8, 1, 15, 0, 0), tabla.Filas[0][3].Valor);
    }

    [Fact]
    public void HistoricoReponeElRetiroDeLosEgresosEnSuPropiaColumna()
    {
        var ahora = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var fila = new FilaDeHistoricoDeCajas(412, 3, ahora, ahora, 1000m, 1000m, 0m, new EgresosDeTurno([], [], 250m));

        var tabla = ExportacionDeCaja.De([fila], Contexto, ZonaBuenosAires);

        Assert.Equal(250m, tabla.Filas[0][7].Valor);
    }

    [Fact]
    public void HistoricoSinFilasProduceUnaTablaVacia()
    {
        var tabla = ExportacionDeCaja.De((IReadOnlyList<FilaDeHistoricoDeCajas>)[], Contexto, ZonaBuenosAires);

        Assert.Empty(tabla.Filas);
    }

    // ---- Z-report (detalle de turno) ------------------------------------------------------------

    private static ResumenDeTurno ResumenVacio(
        IReadOnlyList<LineaDeResumen>? medios = null, EgresosDeTurno? egresos = null) =>
        new(
            IdTurnoCaja: 412,
            IdMedioAncla: 1,
            Medios: medios ?? [],
            CantidadTickets: 0,
            PrimerTicket: null,
            UltimoTicket: null,
            IngresosPorArea: [],
            Egresos: egresos ?? new EgresosDeTurno([], [], 0m));

    [Fact]
    public void DetalleEscribeUnaFilaPorMedioEnLaSeccionMediosDePago()
    {
        var resumen = ResumenVacio(medios: [new LineaDeResumen(IdMedioPago: 3, ImporteEsperado: 500m)]);
        var detalle = new DetalleDeTurno(resumen, [], []);

        var tabla = ExportacionDeCaja.De(detalle, Contexto, ZonaBuenosAires);

        // Fila 0 = Medios de pago; la sección Retiros SIEMPRE se escribe, incluso en 0 (el
        // mapper nunca omite una sección por valor nulo/cero — mismo criterio que el resto de
        // filas de totales de la etapa).
        Assert.Equal(2, tabla.Filas.Count);
        var fila = tabla.Filas[0];
        Assert.Equal("Medios de pago", fila[0].Valor);
        Assert.Equal("Medio 3", fila[1].Valor);
        Assert.Null(fila[2].Valor);
        Assert.Equal(500m, fila[3].Valor);
        Assert.Equal("Retiros", tabla.Filas[1][0].Valor);
    }

    [Fact]
    public void DetalleEscribeLosEgresosPorCategoriaYPorAreaMasElTotalDeRetiros()
    {
        var egresos = new EgresosDeTurno(
            PorCategoria: [new EgresoPorCategoria(CategoriaGasto.Otros, 40m)],
            PorArea: [new EgresoPorArea(IdArea: null, NombreArea: "Sin área", Total: 40m)],
            Retiros: 100m);
        var detalle = new DetalleDeTurno(ResumenVacio(egresos: egresos), [], []);

        var tabla = ExportacionDeCaja.De(detalle, Contexto, ZonaBuenosAires);

        Assert.Equal(3, tabla.Filas.Count);
        Assert.Equal("Egresos por categoría", tabla.Filas[0][0].Valor);
        Assert.Equal("Otros", tabla.Filas[0][1].Valor);
        Assert.Equal("Egresos por área", tabla.Filas[1][0].Valor);
        Assert.Equal("Sin área", tabla.Filas[1][1].Valor);
        Assert.Equal("Retiros", tabla.Filas[2][0].Valor);
        Assert.Equal(100m, tabla.Filas[2][3].Valor);
    }

    [Fact]
    public void DetalleEscribeTicketsYGastosConSuFechaConvertidaALaZonaLocal()
    {
        var fecha = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var ticket = new ComprobanteListado(
            1, 1L, "0003-00000001", EstadoComprobante.Emitido, fecha, 3, 1, 150m);
        var gasto = new GastoListado(1, 3, fecha, CategoriaGasto.Otros, 1, 40m);
        var detalle = new DetalleDeTurno(ResumenVacio(), [ticket], [gasto]);

        var tabla = ExportacionDeCaja.De(detalle, Contexto, ZonaBuenosAires);

        // Fila 0 = Retiros (siempre presente, ver DetalleEscribeUnaFilaPorMedioEnLaSeccionMediosDePago),
        // fila 1 = Tickets, fila 2 = Gastos.
        Assert.Equal(3, tabla.Filas.Count);
        Assert.Equal("Tickets", tabla.Filas[1][0].Valor);
        Assert.Equal("0003-00000001", tabla.Filas[1][1].Valor);
        Assert.Equal(new DateTime(2026, 8, 1, 9, 0, 0), tabla.Filas[1][2].Valor);
        Assert.Equal(150m, tabla.Filas[1][3].Valor);

        Assert.Equal("Gastos", tabla.Filas[2][0].Valor);
        Assert.Equal("Otros", tabla.Filas[2][1].Valor);
        Assert.Equal(new DateTime(2026, 8, 1, 9, 0, 0), tabla.Filas[2][2].Valor);
        Assert.Equal(40m, tabla.Filas[2][3].Valor);
    }
}
