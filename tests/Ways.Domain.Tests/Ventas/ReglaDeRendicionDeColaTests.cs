using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Ventas;

/// <summary>
/// La decisión de la guarda de cierre, sin base de datos (política de testing del proyecto: la
/// lógica de dominio se prueba sin DB, como <c>PoliticaDeRoles</c>). Cada test nombra el disyunto
/// de <see cref="ReglaDeRendicionDeCola.Evaluar"/> que existe para matar, y los bordes están
/// cubiertos de a pares (justo adentro / justo afuera) para que un mutante de comparación
/// (<c>&gt;</c> ↔ <c>&gt;=</c>, <c>&lt;</c> ↔ <c>&lt;=</c>) no sobreviva.
/// </summary>
public class ReglaDeRendicionDeColaTests
{
    private static readonly DateTimeOffset Momento = new(2026, 9, 26, 18, 0, 0, TimeSpan.Zero);

    /// <summary>Disyunto (a): <c>reportadoAt is null</c> — fail-closed. Todo lo demás está en
    /// orden (sin pendientes, sin hueco) y aun así bloquea.</summary>
    [Fact]
    public void UnBloqueQueNuncaRindioBloquea()
    {
        var motivo = ReglaDeRendicionDeCola.Evaluar(
            reportadoAt: null, pendientes: null, entregadoHasta: null, desde: 10,
            comprobantesEnElRango: 0, momento: Momento);

        Assert.Equal(MotivoDeRendicionPendiente.SinReporte, motivo);
    }

    /// <summary>Mismo disyunto (a) por la otra puerta: una rendición a medias (lo que
    /// <c>ck_reservas_numeracion_reporte_consistente</c> impide en la base) nunca se completa con
    /// un default optimista — las tres combinaciones parciales caen en SinReporte.</summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void UnReporteAMediasBloqueaIgual(bool conReportadoAt, bool conPendientes, bool conEntregadoHasta)
    {
        var motivo = ReglaDeRendicionDeCola.Evaluar(
            reportadoAt: conReportadoAt ? Momento : null,
            pendientes: conPendientes ? 0 : null,
            entregadoHasta: conEntregadoHasta ? 9 : null,
            desde: 10,
            comprobantesEnElRango: 0,
            momento: Momento);

        Assert.Equal(MotivoDeRendicionPendiente.SinReporte, motivo);
    }

    /// <summary>Disyunto (b): el reporte vencido. Un segundo más allá de la ventana bloquea.</summary>
    [Fact]
    public void UnReporteMasViejoQueLaVentanaBloquea()
    {
        var motivo = ReglaDeRendicionDeCola.Evaluar(
            reportadoAt: Momento - ReglaDeRendicionDeCola.VentanaDeFrescura - TimeSpan.FromSeconds(1),
            pendientes: 0, entregadoHasta: 9, desde: 10, comprobantesEnElRango: 0, momento: Momento);

        Assert.Equal(MotivoDeRendicionPendiente.ReporteVencido, motivo);
    }

    /// <summary>El otro lado del MISMO borde: EXACTAMENTE en la ventana todavía vale (la
    /// comparación es <c>&gt;</c>, no <c>&gt;=</c>). Junto con el test de arriba, mata los dos
    /// mutantes de esa comparación.</summary>
    [Fact]
    public void UnReporteJustoEnElBordeDeLaVentanaNoBloquea()
    {
        var motivo = ReglaDeRendicionDeCola.Evaluar(
            reportadoAt: Momento - ReglaDeRendicionDeCola.VentanaDeFrescura,
            pendientes: 0, entregadoHasta: 9, desde: 10, comprobantesEnElRango: 0, momento: Momento);

        Assert.Null(motivo);
    }

    /// <summary>Disyunto (c): el dispositivo declara ventas sin llegar. Una sola alcanza — el
    /// borde inferior de <c>pendientes &gt; 0</c>.</summary>
    [Fact]
    public void UnaSolaVentaPendienteBloquea()
    {
        var motivo = ReglaDeRendicionDeCola.Evaluar(
            reportadoAt: Momento, pendientes: 1, entregadoHasta: 9, desde: 10,
            comprobantesEnElRango: 0, momento: Momento);

        Assert.Equal(MotivoDeRendicionPendiente.VentasSinLlegar, motivo);
    }

    /// <summary>Disyunto (d): el hueco. El dispositivo dice haber entregado 10..12 (tres números)
    /// y solo llegaron dos comprobantes — el reporte se contradice con los hechos aunque declare
    /// cero pendientes, que es exactamente para lo que existe este chequeo.</summary>
    [Fact]
    public void UnHuecoDeComprobantesBloqueaAunqueNoHayaPendientes()
    {
        var motivo = ReglaDeRendicionDeCola.Evaluar(
            reportadoAt: Momento, pendientes: 0, entregadoHasta: 12, desde: 10,
            comprobantesEnElRango: 2, momento: Momento);

        Assert.Equal(MotivoDeRendicionPendiente.HuecoDeComprobantes, motivo);
    }

    /// <summary>El caso limpio y el otro lado del borde del hueco: los tres números entregados
    /// tienen sus tres comprobantes. Un mutante que cambie <c>&lt;</c> por <c>&lt;=</c> pone este
    /// test en rojo.</summary>
    [Fact]
    public void UnReporteFrescoSinPendientesYSinHuecoNoBloquea()
    {
        var motivo = ReglaDeRendicionDeCola.Evaluar(
            reportadoAt: Momento, pendientes: 0, entregadoHasta: 12, desde: 10,
            comprobantesEnElRango: 3, momento: Momento);

        Assert.Null(motivo);
    }

    /// <summary>Borde de "todavía no repartió nada": <c>entregadoHasta == desde - 1</c> ⇒ el rango
    /// es vacío y el esperado es CERO, así que cero comprobantes no es un hueco. Un mutante en la
    /// aritmética del esperado (<c>- desde</c> en vez de <c>- desde + 1</c>, o <c>+ 1</c> de más)
    /// lo pone en rojo.</summary>
    [Fact]
    public void UnBloqueSinNumerosRepartidosNoTieneHueco()
    {
        var motivo = ReglaDeRendicionDeCola.Evaluar(
            reportadoAt: Momento, pendientes: 0, entregadoHasta: 9, desde: 10,
            comprobantesEnElRango: 0, momento: Momento);

        Assert.Null(motivo);
    }

    /// <summary>Un solo número repartido (<c>entregadoHasta == desde</c>), el borde inmediatamente
    /// siguiente al de arriba: esperado 1. Los dos casos juntos fijan la aritmética en los dos
    /// valores donde un off-by-one es invisible por separado.</summary>
    [Theory]
    [InlineData(0, MotivoDeRendicionPendiente.HuecoDeComprobantes)]
    [InlineData(1, null)]
    public void ConUnSoloNumeroRepartidoElEsperadoEsUno(int comprobantes, MotivoDeRendicionPendiente? esperado)
    {
        var motivo = ReglaDeRendicionDeCola.Evaluar(
            reportadoAt: Momento, pendientes: 0, entregadoHasta: 10, desde: 10,
            comprobantesEnElRango: comprobantes, momento: Momento);

        Assert.Equal(esperado, motivo);
    }

    /// <summary>El ORDEN de los disyuntos es parte del contrato (el motivo que viaja al operador):
    /// con reporte vencido Y pendientes Y hueco, el que gana es el vencido — un reporte viejo no
    /// habilita a creerle sus números, así que informar "tiene 3 pendientes" sería afirmar algo que
    /// ese reporte ya no prueba.</summary>
    [Fact]
    public void ElReporteVencidoGanaSobreLosDemasMotivos()
    {
        var motivo = ReglaDeRendicionDeCola.Evaluar(
            reportadoAt: Momento - ReglaDeRendicionDeCola.VentanaDeFrescura - TimeSpan.FromMinutes(1),
            pendientes: 3, entregadoHasta: 12, desde: 10, comprobantesEnElRango: 0, momento: Momento);

        Assert.Equal(MotivoDeRendicionPendiente.ReporteVencido, motivo);
    }

    /// <summary>Segundo tramo del mismo orden: con reporte fresco, los pendientes ganan sobre el
    /// hueco (los dos están presentes acá).</summary>
    [Fact]
    public void LosPendientesGananSobreElHueco()
    {
        var motivo = ReglaDeRendicionDeCola.Evaluar(
            reportadoAt: Momento, pendientes: 3, entregadoHasta: 12, desde: 10,
            comprobantesEnElRango: 0, momento: Momento);

        Assert.Equal(MotivoDeRendicionPendiente.VentasSinLlegar, motivo);
    }

    /// <summary>La ventana de frescura es el dato que el operador ve traducido a "tu reporte está
    /// vencido": cambiarla es una decisión de producto, no un refactor. 5 minutos = 15 ciclos de
    /// los 20 s de <c>INTERVALO_DE_SINCRONIZACION_MS</c>.</summary>
    [Fact]
    public void LaVentanaDeFrescuraEsDeCincoMinutos()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), ReglaDeRendicionDeCola.VentanaDeFrescura);
    }
}
