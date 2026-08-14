using Ways.Domain.Common;
using Ways.Domain.Stock;

namespace Ways.Domain.Tests.Stock;

/// <summary>
/// stage-13-stock-inteligente, Slice 1 (design decisión 1, patrón <c>PoliticaDeRoles</c>/<c>
/// ReglaDeLotes</c>): <see cref="ReglaDeReposicion"/> es pura y sin base de datos — cada hecho de
/// acá corre sin fixture. También cubre <see cref="ReglaDeReposicion.ExigirVentanaValida"/>
/// (task 1.8 — nombrada "Application unit" en tasks.md, pero la función vive en Domain junto al
/// resto de la regla; sin fixture ni DB, mismo criterio que todo lo demás de este archivo).
/// </summary>
public class ReglaDeReposicionTests
{
    // ---- Clasificar (spec reposicion-de-stock: "The Low-Stock Boundary Is Inclusive") ----------

    [Theory]
    [InlineData(9, 10, EstadoDeReposicion.Bajo)]   // cantidad = minimo - 1
    [InlineData(10, 10, EstadoDeReposicion.Bajo)]  // cantidad = minimo (borde inclusivo)
    [InlineData(11, 10, EstadoDeReposicion.Ok)]    // cantidad = minimo + 1
    public void ClasificarEnLosTresBordesDelPuntoDePedido(decimal cantidad, decimal minimo, EstadoDeReposicion esperado)
    {
        Assert.Equal(esperado, ReglaDeReposicion.Clasificar(cantidad, minimo));
    }

    [Fact]
    public void ClasificarConMinimoCeroAlertaSoloAlAgotarse()
    {
        Assert.Equal(EstadoDeReposicion.Bajo, ReglaDeReposicion.Clasificar(cantidad: 0m, minimo: 0m));
        // Un saldo negativo es legal (paridad legacy) y también queda bajo el punto de pedido.
        Assert.Equal(EstadoDeReposicion.Bajo, ReglaDeReposicion.Clasificar(cantidad: -1m, minimo: 0m));
    }

    [Fact]
    public void ClasificarConMinimoNuloEsSinMinimoIndependienteDeLaCantidad()
    {
        Assert.Equal(EstadoDeReposicion.SinMinimo, ReglaDeReposicion.Clasificar(cantidad: 0m, minimo: null));
        Assert.Equal(EstadoDeReposicion.SinMinimo, ReglaDeReposicion.Clasificar(cantidad: -50m, minimo: null));
        Assert.Equal(EstadoDeReposicion.SinMinimo, ReglaDeReposicion.Clasificar(cantidad: 1000m, minimo: null));
    }

    // ---- Sugerido (spec: "sugerido is null, never zero, when reposicion is unset") --------------

    [Fact]
    public void SugeridoEsNuloCuandoReposicionNoEstaSeteada()
    {
        Assert.Null(ReglaDeReposicion.Sugerido(cantidad: 3m, reposicion: null));
    }

    [Fact]
    public void SugeridoComputaLaBrechaAlObjetivo()
    {
        Assert.Equal(30m, ReglaDeReposicion.Sugerido(cantidad: 20m, reposicion: 50m));
    }

    [Fact]
    public void SugeridoNuncaEsNegativoAunqueLaCantidadSuperonElObjetivo()
    {
        Assert.Equal(0m, ReglaDeReposicion.Sugerido(cantidad: 80m, reposicion: 50m));
    }

    [Fact]
    public void SugeridoConCantidadNegativaSumaCorrectamenteALaBrecha()
    {
        Assert.Equal(60m, ReglaDeReposicion.Sugerido(cantidad: -10m, reposicion: 50m));
    }

    // ---- ConsumoDiario (spec: "A zero-history articulo shows no suggestion...") ------------------

    [Fact]
    public void ConsumoDiarioEsNuloCuandoNoHayHistoriaCalificada()
    {
        Assert.Null(ReglaDeReposicion.ConsumoDiario(netoConsumido: null, diasVentana: 30));
    }

    [Fact]
    public void ConsumoDiarioEsCeroConNetoCeroPeroNoNulo()
    {
        Assert.Equal(0m, ReglaDeReposicion.ConsumoDiario(netoConsumido: 0m, diasVentana: 30));
    }

    [Fact]
    public void ConsumoDiarioPositivoSeDivideEntreLosDiasDeLaVentana()
    {
        Assert.Equal(3m, ReglaDeReposicion.ConsumoDiario(netoConsumido: 90m, diasVentana: 30));
    }

    [Fact]
    public void ConsumoDiarioConNetoNegativoSeRecortaACeroNuncaANulo()
    {
        var resultado = ReglaDeReposicion.ConsumoDiario(netoConsumido: -15m, diasVentana: 30);

        Assert.NotNull(resultado);
        Assert.Equal(0m, resultado);
    }

    // ---- MinimoSugerido (spec parametros-operativos: "dias_cobertura_objetivo feeds minimoSugerido") --

    [Fact]
    public void MinimoSugeridoMultiplicaConsumoDiarioPorDiasDeCobertura()
    {
        Assert.Equal(21m, ReglaDeReposicion.MinimoSugerido(consumoDiario: 3m, diasCoberturaObjetivo: 7));
    }

    [Fact]
    public void MinimoSugeridoRedondeaATresDecimales()
    {
        // 1/3 * 7 = 2.3333... redondeado a 3 decimales (numeric(12,3)).
        var consumoDiario = 1m / 3m;

        Assert.Equal(2.333m, ReglaDeReposicion.MinimoSugerido(consumoDiario, diasCoberturaObjetivo: 7));
    }

    [Fact]
    public void MinimoSugeridoEsNuloCuandoElConsumoDiarioEsNulo()
    {
        Assert.Null(ReglaDeReposicion.MinimoSugerido(consumoDiario: null, diasCoberturaObjetivo: 7));
    }

    // ---- DiasDeCobertura (design decisión 1: ni infinito ni cero son respuestas honestas) --------

    [Fact]
    public void DiasDeCoberturaEsNuloCuandoElConsumoDiarioEsNulo()
    {
        Assert.Null(ReglaDeReposicion.DiasDeCobertura(cantidad: 10m, consumoDiario: null));
    }

    [Fact]
    public void DiasDeCoberturaEsNuloCuandoElConsumoDiarioEsCero()
    {
        Assert.Null(ReglaDeReposicion.DiasDeCobertura(cantidad: 10m, consumoDiario: 0m));
    }

    [Fact]
    public void DiasDeCoberturaDivideLaCantidadPorElConsumoDiario()
    {
        Assert.Equal(5m, ReglaDeReposicion.DiasDeCobertura(cantidad: 10m, consumoDiario: 2m));
    }

    // ---- VentanaDeRotacion (design decisión 7: bordes de día local, medianoche inválida/ambigua) --

    private static readonly DateOnly Hoy = new(2026, 8, 14);

    [Fact]
    public void VentanaDeRotacionEnUtcCubreLosDiasPedidosConBordeExclusivo()
    {
        var (desde, hasta) = ReglaDeReposicion.VentanaDeRotacion(Hoy, dias: 30, TimeZoneInfo.Utc);

        Assert.Equal(new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero), desde);
        Assert.Equal(new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero), hasta);
        Assert.Equal(30, (hasta - desde).TotalDays);
    }

    [Fact]
    public void VentanaDeRotacionEnMenosTresCubreLosDiasPedidosConBordeExclusivo()
    {
        var zona = TimeZoneInfo.CreateCustomTimeZone("Fijo/-03:00", TimeSpan.FromHours(-3), "Fijo -03:00", "Fijo -03:00");

        var (desde, hasta) = ReglaDeReposicion.VentanaDeRotacion(Hoy, dias: 30, zona);

        Assert.Equal(new DateTimeOffset(2026, 7, 16, 3, 0, 0, TimeSpan.Zero), desde);
        Assert.Equal(new DateTimeOffset(2026, 8, 15, 3, 0, 0, TimeSpan.Zero), hasta);
    }

    [Fact]
    public void VentanaDeRotacionConDiasUnoEsSoloElDiaDeHoy()
    {
        var (desde, hasta) = ReglaDeReposicion.VentanaDeRotacion(Hoy, dias: 1, TimeZoneInfo.Utc);

        Assert.Equal(new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero), desde);
        Assert.Equal(new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero), hasta);
    }

    /// <summary>Zona sintética con una transición EXACTAMENTE a medianoche local (design decisión
    /// 7: "zonas que saltan a las 24:00") — <c>2026-08-14T00:00</c> local no existe (el reloj
    /// salta de 2026-08-13T23:59:59 -03:00 directo a 2026-08-14T01:00:00 -02:00). Un <c>
    /// ConvertTimeToUtc</c> naive tira <c>ArgumentException</c> acá; <see
    /// cref="ReglaDeReposicion.VentanaDeRotacion"/> avanza al instante del salto (design: "el
    /// offset ANTERIOR a la transición").</summary>
    private static TimeZoneInfo CrearZonaConMedianocheInvalida()
    {
        var inicioTransicion = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 8, 14);
        var finTransicion = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 8, 20);
        var regla = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1), inicioTransicion, finTransicion);

        return TimeZoneInfo.CreateCustomTimeZone(
            "Prueba/MedianocheInvalida", TimeSpan.FromHours(-3), "Prueba", "Estándar", "Verano", [regla]);
    }

    [Fact]
    public void VentanaDeRotacionSobreUnaMedianocheLocalInvalidaAvanzaAlInstanteDelSalto()
    {
        var zona = CrearZonaConMedianocheInvalida();
        Assert.True(zona.IsInvalidTime(new DateTime(2026, 8, 14, 0, 0, 0)));

        var (_, hastaExclusivo) = ReglaDeReposicion.VentanaDeRotacion(new DateOnly(2026, 8, 13), dias: 1, zona);

        // La medianoche local del 14 no existe: el offset ANTERIOR a la transición (-03:00)
        // aplicado a 2026-08-14T00:00 cae exactamente en el instante del salto — el mismo UTC que
        // 2026-08-14T01:00 -02:00 (el primer instante local válido tras el salto).
        Assert.Equal(new DateTimeOffset(2026, 8, 14, 3, 0, 0, TimeSpan.Zero), hastaExclusivo);
    }

    /// <summary>Zona sintética con una medianoche local AMBIGUA (design decisión 7): <c>
    /// 2026-08-20T00:00</c>-<c>00:59:59</c> ocurre dos veces (el reloj retrocede de -02:00 a
    /// -03:00). <see cref="ReglaDeReposicion.VentanaDeRotacion"/> toma el offset ESTÁNDAR por
    /// diseño de la BCL, sin código especial.</summary>
    [Fact]
    public void VentanaDeRotacionSobreUnaMedianocheLocalAmbiguaTomaElOffsetEstandar()
    {
        var inicioTransicion = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 8, 14);
        var finTransicion = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 8, 20);
        var regla = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1), inicioTransicion, finTransicion);
        var zona = TimeZoneInfo.CreateCustomTimeZone(
            "Prueba/MedianocheAmbigua", TimeSpan.FromHours(-3), "Prueba", "Estándar", "Verano", [regla]);
        Assert.True(zona.IsAmbiguousTime(new DateTime(2026, 8, 20, 0, 0, 0)));

        var (desde, _) = ReglaDeReposicion.VentanaDeRotacion(new DateOnly(2026, 8, 20), dias: 1, zona);

        // Offset estándar (-03:00): 2026-08-20T00:00 -03:00 = 2026-08-20T03:00 UTC.
        Assert.Equal(new DateTimeOffset(2026, 8, 20, 3, 0, 0, TimeSpan.Zero), desde);
    }

    // ---- ExigirVentanaValida (task 1.8) ------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ExigirVentanaValidaRechazaDiasNoPositivos(int dias)
    {
        var error = Assert.Throws<ErrorDominio>(() => ReglaDeReposicion.ExigirVentanaValida(dias, "dias_rotacion_invalido"));

        Assert.Equal("dias_rotacion_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void ExigirVentanaValidaPropagaElCodigoDelLlamador()
    {
        var error = Assert.Throws<ErrorDominio>(
            () => ReglaDeReposicion.ExigirVentanaValida(-1, "dias_cobertura_invalido"));

        Assert.Equal("dias_cobertura_invalido", error.Codigo);
    }

    [Fact]
    public void ExigirVentanaValidaAceptaUnDiaPositivo()
    {
        Assert.Equal(1, ReglaDeReposicion.ExigirVentanaValida(1, "dias_rotacion_invalido"));
    }

    // ---- mutation targets 1.10/1.11 (mutation-proof-tests) ----------------------------------------
    // Evidencia registrada en el resumen de apply (mutar → correr → falla → revertir → verde):
    // 1.10 — Clasificar's "<=" mutado a "<": ClasificarEnLosTresBordesDelPuntoDePedido
    //        (cantidad = minimo, esperado Bajo) FALLÓ (obtuvo Ok); revertido, vuelve a pasar.
    // 1.11 — Sugerido's "reposicion is null ⇒ null" mutado a "⇒ 0m":
    //        SugeridoEsNuloCuandoReposicionNoEstaSeteada FALLÓ (Assert.Null recibió 0m);
    //        revertido, vuelve a pasar.
}
