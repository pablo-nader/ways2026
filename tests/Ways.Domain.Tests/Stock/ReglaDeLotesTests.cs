using Ways.Domain.Stock;

namespace Ways.Domain.Tests.Stock;

/// <summary>
/// stage-12 slice 2 (design decisión 1, patrón <c>PoliticaDeRolesTests</c>): <see
/// cref="ReglaDeLotes"/> es pura y sin base de datos — cada hecho de acá corre sin fixture.
/// </summary>
public class ReglaDeLotesTests
{
    private static SaldoDeLote Saldo(
        int idLote, bool esSinIdentificar = false, DateOnly? fechaVencimiento = null, decimal cantidad = 10m) =>
        new(IdArticulo: 40, IdLote: idLote, Codigo: $"L-{idLote}", EsSinIdentificar: esSinIdentificar,
            FechaVencimiento: fechaVencimiento, Cantidad: cantidad);

    // ---- ControlEfectivo (spec lotes-y-vencimientos: "Effective Lot Control Is controla_lote
    // AND lotes_habilitado") -----------------------------------------------------------------

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void ControlEfectivoEsElAndDeAmbosFlags(bool controlaLote, bool lotesHabilitado, bool esperado)
    {
        Assert.Equal(esperado, ReglaDeLotes.ControlEfectivo(controlaLote, lotesHabilitado));
    }

    // ---- OrdenarFefo (spec: "FEFO Is The Server-Computed Default", "The sin-identificar lot is
    // offered before every dated lot") --------------------------------------------------------

    [Fact]
    public void OrdenarFefoPoneElSinIdentificarPrimeroYDespuesVencimientoAscendenteConIdComoDesempate()
    {
        var sinIdentificar = Saldo(idLote: 1, esSinIdentificar: true);
        var conVencimientoLejano = Saldo(idLote: 2, fechaVencimiento: new DateOnly(2026, 9, 1));
        var conVencimientoCercanoIdMayor = Saldo(idLote: 5, fechaVencimiento: new DateOnly(2026, 8, 1));
        var conVencimientoCercanoIdMenor = Saldo(idLote: 3, fechaVencimiento: new DateOnly(2026, 8, 1));

        var saldos = new[] { conVencimientoLejano, sinIdentificar, conVencimientoCercanoIdMayor, conVencimientoCercanoIdMenor };

        var ordenado = ReglaDeLotes.OrdenarFefo(saldos);

        // sin-identificar primero; después los dos que empatan en vencimiento (2026-08-01),
        // desempatados por id_lote ascendente (3 antes que 5); el de vencimiento más lejano
        // (2026-09-01) al final — la secuencia de ids es la aserción, no solo el conjunto.
        Assert.Equal([1, 3, 5, 2], ordenado.Select(s => s.IdLote));
    }

    // ---- ElegirFefo (spec: default de línea sin idLote) ---------------------------------------

    private static readonly DateOnly Hoy = new(2026, 8, 12);

    [Fact]
    public void ElegirFefoDevuelveNullCuandoNingunSaldoEsPositivo()
    {
        SaldoDeLote[] saldos = [Saldo(idLote: 1, cantidad: 0m), Saldo(idLote: 2, cantidad: -3m)];

        Assert.Null(ReglaDeLotes.ElegirFefo(saldos, Hoy));
    }

    [Fact]
    public void ElegirFefoDevuelveElPrimeroDelOrdenFefoEntreLosDeSaldoPositivo()
    {
        // Ambos vencimientos NO vencidos respecto de Hoy — misma partición, gana el más cercano
        // (comportamiento FEFO base, sin interferencia de la partición de decisión 15).
        var sinSaldo = Saldo(idLote: 1, fechaVencimiento: new DateOnly(2026, 7, 1), cantidad: 0m);
        var conSaldoMasCercano = Saldo(idLote: 2, fechaVencimiento: new DateOnly(2027, 8, 1), cantidad: 5m);
        var conSaldoMasLejano = Saldo(idLote: 3, fechaVencimiento: new DateOnly(2027, 9, 1), cantidad: 5m);

        var elegido = ReglaDeLotes.ElegirFefo([sinSaldo, conSaldoMasLejano, conSaldoMasCercano], Hoy);

        Assert.Equal(2, elegido!.Value.IdLote);
    }

    // ---- ElegirFefo — decisión 15 (judgment-day slice 7): partición no-vencido antes que vencido ---

    [Fact]
    public void ElegirFefoPrefiereElVigenteAunqueElVencidoVenzaAntesEnElOrdenFefoBase()
    {
        // Orden FEFO puro (solo fecha ASC) elegiría el vencido, por vencer antes — decisión 15
        // exige que la partición no-vencido gane primero.
        var vencido = Saldo(idLote: 1, fechaVencimiento: new DateOnly(2020, 1, 15), cantidad: 5m);
        var vigente = Saldo(idLote: 2, fechaVencimiento: new DateOnly(2099, 12, 31), cantidad: 5m);

        var elegido = ReglaDeLotes.ElegirFefo([vencido, vigente], Hoy);

        Assert.Equal(2, elegido!.Value.IdLote);
    }

    [Fact]
    public void ElegirFefoEligeElVencidoCuandoEsLaUnicaParticionConSaldo()
    {
        // "Nunca bloquea" (decisión 12): si la única partición con saldo positivo es la de
        // vencidos, se elige el vencido igual — el llamador adjunta el warning LoteVencido.
        var vencidoA = Saldo(idLote: 1, fechaVencimiento: new DateOnly(2020, 1, 15), cantidad: 5m);
        var vencidoB = Saldo(idLote: 2, fechaVencimiento: new DateOnly(2021, 6, 1), cantidad: 5m);

        var elegido = ReglaDeLotes.ElegirFefo([vencidoA, vencidoB], Hoy);

        Assert.Equal(1, elegido!.Value.IdLote);
    }

    [Fact]
    public void ElegirFefoPrefiereElSinIdentificarPorSobreUnVigenteDentroDeLaParticionNoVencida()
    {
        // El sin-identificar (sin fecha) nunca está vencido (EstaVencido), así que cae en la
        // misma partición no-vencido que el vigente — y OrdenarFefo lo sigue poniendo primero
        // (es_sin_identificar DESC) dentro de esa partición, decisión 4 intacta.
        var sinIdentificar = Saldo(idLote: 1, esSinIdentificar: true, cantidad: 5m);
        var vigente = Saldo(idLote: 2, fechaVencimiento: new DateOnly(2099, 12, 31), cantidad: 5m);
        var vencido = Saldo(idLote: 3, fechaVencimiento: new DateOnly(2020, 1, 15), cantidad: 5m);

        var elegido = ReglaDeLotes.ElegirFefo([vencido, vigente, sinIdentificar], Hoy);

        Assert.Equal(1, elegido!.Value.IdLote);
    }

    [Fact]
    public void ElegirFefoDesempataDentroDeLaParticionNoVencidaPorVencimientoAscendenteYLuegoPorId()
    {
        var vencido = Saldo(idLote: 1, fechaVencimiento: new DateOnly(2020, 1, 15), cantidad: 5m);
        var noVencidoLejano = Saldo(idLote: 4, fechaVencimiento: new DateOnly(2099, 12, 31), cantidad: 5m);
        var noVencidoCercanoIdMayor = Saldo(idLote: 3, fechaVencimiento: new DateOnly(2027, 1, 1), cantidad: 5m);
        var noVencidoCercanoIdMenor = Saldo(idLote: 2, fechaVencimiento: new DateOnly(2027, 1, 1), cantidad: 5m);

        var elegido = ReglaDeLotes.ElegirFefo(
            [vencido, noVencidoLejano, noVencidoCercanoIdMayor, noVencidoCercanoIdMenor], Hoy);

        Assert.Equal(2, elegido!.Value.IdLote);
    }

    // ---- DerivarCodigo (spec: "A lot is created with a server-derived codigo") ----------------

    [Fact]
    public void DerivarCodigoFormateaLaFechaComoIso()
    {
        Assert.Equal("2026-12-31", ReglaDeLotes.DerivarCodigo(new DateOnly(2026, 12, 31)));
    }

    // ---- EstaVencido / Clasificar (spec: "Vencimientos Report Resolves Hoy…") ------------------

    [Fact]
    public void EstaVencidoEsFalsoParaUnLoteSinFecha()
    {
        Assert.False(ReglaDeLotes.EstaVencido(fecha: null, hoy: new DateOnly(2026, 8, 12)));
    }

    [Theory]
    [InlineData(2026, 8, 11, EstadoDeVencimiento.Vencido)]       // hoy - 1
    [InlineData(2026, 8, 12, EstadoDeVencimiento.PorVencer)]     // hoy
    [InlineData(2026, 9, 11, EstadoDeVencimiento.PorVencer)]     // hoy + dias (30)
    [InlineData(2026, 9, 12, EstadoDeVencimiento.Vigente)]       // hoy + dias + 1
    public void ClasificarEnLosCuatroBordesDelHorizonteDeAlerta(int anio, int mes, int dia, EstadoDeVencimiento esperado)
    {
        var hoy = new DateOnly(2026, 8, 12);
        var fecha = new DateOnly(anio, mes, dia);

        Assert.Equal(esperado, ReglaDeLotes.Clasificar(fecha, hoy, diasDeAlerta: 30));
    }

    [Fact]
    public void ClasificarDevuelveSinFechaParaElLoteSinIdentificar()
    {
        Assert.Equal(
            EstadoDeVencimiento.SinFecha,
            ReglaDeLotes.Clasificar(fecha: null, hoy: new DateOnly(2026, 8, 12), diasDeAlerta: 30));
    }
}
