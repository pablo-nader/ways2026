using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Ventas;

/// <summary>
/// stage-17-presupuestos-y-remitos, Slice 1 (design decisión 11, patrón
/// <c>ReglaDeLotesTests</c>/<c>PoliticaDeRolesTests</c>): <see cref="ReglaDePresupuestos"/> es
/// pura y sin base de datos — cada hecho de acá corre sin fixture.
/// </summary>
public class ReglaDePresupuestosTests
{
    private static readonly DateOnly Hoy = new(2026, 8, 19);

    // ---- EstaVencido: falsa para todo estado != Enviado (incl. Borrador/Convertido/Anulado) ---

    [Theory]
    [InlineData(EstadoPresupuesto.Borrador)]
    [InlineData(EstadoPresupuesto.Convertido)]
    [InlineData(EstadoPresupuesto.Anulado)]
    public void EstaVencidoEsFalsoParaTodoEstadoQueNoSeaEnviado(EstadoPresupuesto estado)
    {
        // vencimiento bien pasado — si la guarda de estado no existiera, esto daría true.
        Assert.False(ReglaDePresupuestos.EstaVencido(estado, new DateOnly(2020, 1, 1), Hoy));
    }

    [Fact]
    public void EstaVencidoEsFalsoCuandoVencimientoEsNull()
    {
        Assert.False(ReglaDePresupuestos.EstaVencido(EstadoPresupuesto.Enviado, vencimiento: null, Hoy));
    }

    // ---- El borde: vencimiento == hoy ⇒ TODAVÍA convertible (< , no <=) --------------------

    [Theory]
    [InlineData(2026, 8, 18, true)]   // hoy - 1 ⇒ vencido
    [InlineData(2026, 8, 19, false)]  // == hoy ⇒ el borde, NO vencido
    [InlineData(2026, 8, 20, false)]  // hoy + 1 ⇒ no vencido
    public void EstaVencidoEnEnviadoRespetaElBordeDeIgualdad(int anio, int mes, int dia, bool esperado)
    {
        var vencimiento = new DateOnly(anio, mes, dia);

        Assert.Equal(esperado, ReglaDePresupuestos.EstaVencido(EstadoPresupuesto.Enviado, vencimiento, Hoy));
    }

    // ---- EsConvertible: Enviado y no vencido; todo lo demás, no ------------------------------

    [Theory]
    [InlineData(EstadoPresupuesto.Borrador, false)]
    [InlineData(EstadoPresupuesto.Enviado, true)]
    [InlineData(EstadoPresupuesto.Convertido, false)]
    [InlineData(EstadoPresupuesto.Anulado, false)]
    public void EsConvertibleSoloEsVerdaderoParaEnviadoNoVencido(EstadoPresupuesto estado, bool esperado)
    {
        // vencimiento en el futuro — nunca vencido; el único discriminante es el estado.
        Assert.Equal(esperado, ReglaDePresupuestos.EsConvertible(estado, new DateOnly(2099, 12, 31), Hoy));
    }

    [Fact]
    public void EsConvertibleEsVerdaderoElMismoDiaDelVencimiento()
    {
        // El borde de EstaVencido se propaga: convertible EN el día de su vencimiento.
        Assert.True(ReglaDePresupuestos.EsConvertible(EstadoPresupuesto.Enviado, Hoy, Hoy));
    }

    [Fact]
    public void EsConvertibleEsFalsoUnDiaDespuesDelVencimiento()
    {
        Assert.False(ReglaDePresupuestos.EsConvertible(EstadoPresupuesto.Enviado, Hoy.AddDays(-1), Hoy));
    }

    [Fact]
    public void EsConvertibleEsVerdaderoConVencimientoNullMientrasNoEstaVencido()
    {
        // EstaVencido exige `vencimiento is { } v` — null nunca está vencido, así que un enviado
        // sin vencimiento (estado transitorio irrepresentable en la práctica por
        // ck_presupuestos_envio_completo, pero la función en sí no lo asume) es convertible.
        Assert.True(ReglaDePresupuestos.EsConvertible(EstadoPresupuesto.Enviado, vencimiento: null, Hoy));
    }
}
