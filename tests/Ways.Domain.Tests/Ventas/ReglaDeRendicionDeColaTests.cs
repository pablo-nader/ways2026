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
            bloqueVivo: true, reportadoAt: null, pendientes: null, entregadoHasta: null, desde: 10,
            techoVerificado: 9, comprobantesEnElRango: 0, momento: Momento);

        Assert.Equal(MotivoDeRendicionPendiente.SinReporte, motivo);
    }

    /// <summary>Mismo disyunto (a) por la otra puerta: una rendición a medias (lo que
    /// <c>ck_reservas_numeracion_reporte_consistente</c> impide en la base) nunca se completa con
    /// un default optimista.
    ///
    /// Las dos últimas filas son las que matan esos defaults, y son las que faltaban (judgment-day):
    /// con <c>reportadoAt</c> presente y EXACTAMENTE una de las otras dos en null, el flujo llega a
    /// los disyuntos siguientes, así que <c>pendientes ?? 0</c> y <c>entregadoHasta ?? desde - 1</c>
    /// devuelven <c>null</c> (no bloquea) en vez de <c>SinReporte</c>. Con <c>reportadoAt</c> en null
    /// —las tres primeras filas— ningún default de esos dos cambia el resultado, que es exactamente
    /// por qué los dos mutantes sobrevivían al archivo entero.
    ///
    /// Queda sin cubrir la sexta combinación parcial (<c>reportadoAt</c> null con las otras dos
    /// presentes): es la que mataría <c>reportadoAt ?? momento</c>, un mutante distinto que este
    /// archivo no cubre.</summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void UnReporteAMediasBloqueaIgual(bool conReportadoAt, bool conPendientes, bool conEntregadoHasta)
    {
        var motivo = ReglaDeRendicionDeCola.Evaluar(
            bloqueVivo: true, reportadoAt: conReportadoAt ? Momento : null,
            pendientes: conPendientes ? 0 : null,
            entregadoHasta: conEntregadoHasta ? 9 : null,
            desde: 10,
            techoVerificado: 9,
            comprobantesEnElRango: 0,
            momento: Momento);

        Assert.Equal(MotivoDeRendicionPendiente.SinReporte, motivo);
    }

    /// <summary>Disyunto (b): el reporte vencido. Un segundo más allá de la ventana bloquea.</summary>
    [Fact]
    public void UnReporteMasViejoQueLaVentanaBloquea()
    {
        var motivo = ReglaDeRendicionDeCola.Evaluar(
            bloqueVivo: true, reportadoAt: Momento - ReglaDeRendicionDeCola.VentanaDeFrescura - TimeSpan.FromSeconds(1),
            pendientes: 0, entregadoHasta: 9, desde: 10, techoVerificado: 9, comprobantesEnElRango: 0,
            momento: Momento);

        Assert.Equal(MotivoDeRendicionPendiente.ReporteVencido, motivo);
    }

    /// <summary>El otro lado del MISMO borde: EXACTAMENTE en la ventana todavía vale (la
    /// comparación es <c>&gt;</c>, no <c>&gt;=</c>). Junto con el test de arriba, mata los dos
    /// mutantes de esa comparación.</summary>
    [Fact]
    public void UnReporteJustoEnElBordeDeLaVentanaNoBloquea()
    {
        var motivo = ReglaDeRendicionDeCola.Evaluar(
            bloqueVivo: true, reportadoAt: Momento - ReglaDeRendicionDeCola.VentanaDeFrescura,
            pendientes: 0, entregadoHasta: 9, desde: 10, techoVerificado: 9, comprobantesEnElRango: 0,
            momento: Momento);

        Assert.Null(motivo);
    }

    /// <summary>Disyunto (c): el dispositivo declara ventas sin llegar. Una sola alcanza — el
    /// borde inferior de <c>pendientes &gt; 0</c>.</summary>
    [Fact]
    public void UnaSolaVentaPendienteBloquea()
    {
        var motivo = ReglaDeRendicionDeCola.Evaluar(
            bloqueVivo: true, reportadoAt: Momento, pendientes: 1, entregadoHasta: 9, desde: 10,
            techoVerificado: 9, comprobantesEnElRango: 0, momento: Momento);

        Assert.Equal(MotivoDeRendicionPendiente.VentasSinLlegar, motivo);
    }

    /// <summary>Disyunto (d): el hueco. El dispositivo dice haber entregado 10..12 (tres números)
    /// y solo llegaron dos comprobantes — el reporte se contradice con los hechos aunque declare
    /// cero pendientes, que es exactamente para lo que existe este chequeo.</summary>
    [Fact]
    public void UnHuecoDeComprobantesBloqueaAunqueNoHayaPendientes()
    {
        var motivo = ReglaDeRendicionDeCola.Evaluar(
            bloqueVivo: true, reportadoAt: Momento, pendientes: 0, entregadoHasta: 12, desde: 10,
            techoVerificado: 12, comprobantesEnElRango: 2, momento: Momento);

        Assert.Equal(MotivoDeRendicionPendiente.HuecoDeComprobantes, motivo);
    }

    /// <summary>El caso limpio y el otro lado del borde del hueco: los tres números entregados
    /// tienen sus tres comprobantes. Un mutante que cambie <c>&lt;</c> por <c>&lt;=</c> pone este
    /// test en rojo.</summary>
    [Fact]
    public void UnReporteFrescoSinPendientesYSinHuecoNoBloquea()
    {
        var motivo = ReglaDeRendicionDeCola.Evaluar(
            bloqueVivo: true, reportadoAt: Momento, pendientes: 0, entregadoHasta: 12, desde: 10,
            techoVerificado: 12, comprobantesEnElRango: 3, momento: Momento);

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
            bloqueVivo: true, reportadoAt: Momento, pendientes: 0, entregadoHasta: 9, desde: 10,
            techoVerificado: 9, comprobantesEnElRango: 0, momento: Momento);

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
            bloqueVivo: true, reportadoAt: Momento, pendientes: 0, entregadoHasta: 10, desde: 10,
            techoVerificado: 10, comprobantesEnElRango: comprobantes, momento: Momento);

        Assert.Equal(esperado, motivo);
    }

    /// <summary>El techo VERIFICADO manda sobre lo declarado (judgment-day, SEVERE): el dispositivo
    /// declara no haber repartido nada (<c>entregadoHasta == desde - 1</c>) y cero pendientes, pero el
    /// servidor ya vio llegar el 12 de ese bloque — o sea que repartió hasta 12 por lo menos. El
    /// esperado es 3 y llegó 1, así que bloquea. Es el kill de la aritmética contra
    /// <c>techoVerificado</c>: medirla contra <c>entregadoHasta</c> da esperado 0, <c>1 &lt; 0</c> es
    /// falso y el bloque pasa limpio con lo que tenga en la cola.</summary>
    [Fact]
    public void UnTechoVerificadoPorEncimaDeLoDeclaradoDescubreLaRetraccion()
    {
        var motivo = ReglaDeRendicionDeCola.Evaluar(
            bloqueVivo: true, reportadoAt: Momento, pendientes: 0, entregadoHasta: 9, desde: 10,
            techoVerificado: 12, comprobantesEnElRango: 1, momento: Momento);

        Assert.Equal(MotivoDeRendicionPendiente.HuecoDeComprobantes, motivo);
    }

    /// <summary>El otro lado del mismo borde: con el techo verificado en 12 y los TRES comprobantes
    /// de <c>[10, 12]</c> llegados, no hay hueco — así el test de arriba no puede pasar por el simple
    /// hecho de que el techo sea mayor que lo declarado.</summary>
    [Fact]
    public void UnTechoVerificadoConTodosSusComprobantesNoBloquea()
    {
        var motivo = ReglaDeRendicionDeCola.Evaluar(
            bloqueVivo: true, reportadoAt: Momento, pendientes: 0, entregadoHasta: 9, desde: 10,
            techoVerificado: 12, comprobantesEnElRango: 3, momento: Momento);

        Assert.Null(motivo);
    }

    /// <summary>El ORDEN de los disyuntos es parte del contrato (el motivo que viaja al operador):
    /// con reporte vencido Y pendientes Y hueco, el que gana es el vencido — un reporte viejo no
    /// habilita a creerle sus números, así que informar "tiene 3 pendientes" sería afirmar algo que
    /// ese reporte ya no prueba.</summary>
    [Fact]
    public void ElReporteVencidoGanaSobreLosDemasMotivos()
    {
        var motivo = ReglaDeRendicionDeCola.Evaluar(
            bloqueVivo: true, reportadoAt: Momento - ReglaDeRendicionDeCola.VentanaDeFrescura - TimeSpan.FromMinutes(1),
            pendientes: 3, entregadoHasta: 12, desde: 10, techoVerificado: 12, comprobantesEnElRango: 0,
            momento: Momento);

        Assert.Equal(MotivoDeRendicionPendiente.ReporteVencido, motivo);
    }

    /// <summary>Segundo tramo del mismo orden: con reporte fresco, los pendientes ganan sobre el
    /// hueco (los dos están presentes acá).</summary>
    [Fact]
    public void LosPendientesGananSobreElHueco()
    {
        var motivo = ReglaDeRendicionDeCola.Evaluar(
            bloqueVivo: true, reportadoAt: Momento, pendientes: 3, entregadoHasta: 12, desde: 10,
            techoVerificado: 12, comprobantesEnElRango: 0, momento: Momento);

        Assert.Equal(MotivoDeRendicionPendiente.VentasSinLlegar, motivo);
    }

    // ---- la frescura es del bloque VIVO, y solo de él (judgment-day ronda 3, SEVERE) ------------

    /// <summary>Kill del conjunto <c>bloqueVivo</c> del disyunto (b): el MISMO reporte vencido y
    /// limpio de <see cref="UnReporteMasViejoQueLaVentanaBloquea"/>, pero sobre un bloque ABANDONADO,
    /// NO bloquea. Un bloque abandonado no puede rendir nunca más —el <c>UPDATE</c> de
    /// <c>RegistrarRendicionAsync</c> solo toca el vivo— ni va a repartir un número más: su evidencia
    /// quedó congelada al rotar, así que su reporte no puede refrescarse y tampoco hace falta.
    /// Borrar el conjunto (volver a <c>momento - reporte &gt; VentanaDeFrescura</c> pelado) deja este
    /// test en rojo con <c>ReporteVencido</c>, que es EXACTAMENTE el defecto que rechazaba todo cierre
    /// del punto de venta cinco minutos después de cada reposición rutinaria.</summary>
    [Fact]
    public void UnReporteVencidoDeUnBloqueAbandonadoNoBloquea()
    {
        var motivo = ReglaDeRendicionDeCola.Evaluar(
            bloqueVivo: false,
            reportadoAt: Momento - ReglaDeRendicionDeCola.VentanaDeFrescura - TimeSpan.FromSeconds(1),
            pendientes: 0, entregadoHasta: 9, desde: 10, techoVerificado: 9, comprobantesEnElRango: 0,
            momento: Momento);

        Assert.Null(motivo);
    }

    /// <summary>El otro lado del MISMO conjunto, y el par que mata el mutante invertido
    /// (<c>!bloqueVivo &amp;&amp;</c>): el bloque VIVO con ese reporte vencido sigue bloqueando. Junto
    /// con el test de arriba, ningún mutante del conjunto sobrevive — ni borrarlo ni negarlo.</summary>
    [Fact]
    public void UnReporteVencidoDeUnBloqueVivoSigueBloqueando()
    {
        var motivo = ReglaDeRendicionDeCola.Evaluar(
            bloqueVivo: true,
            reportadoAt: Momento - ReglaDeRendicionDeCola.VentanaDeFrescura - TimeSpan.FromSeconds(1),
            pendientes: 0, entregadoHasta: 9, desde: 10, techoVerificado: 9, comprobantesEnElRango: 0,
            momento: Momento);

        Assert.Equal(MotivoDeRendicionPendiente.ReporteVencido, motivo);
    }

    /// <summary>Los otros TRES disyuntos no dependen de la vigencia: lo que un bloque abandonado dejó
    /// sin explicar sigue sin explicarse. Las tres filas son (a) nunca rindió, (c) declara ventas sin
    /// llegar y (d) faltan comprobantes del rango verificado, todas con <c>bloqueVivo: false</c>.
    /// Condicionar cualquiera de los tres a la vigencia —<c>bloqueVivo &amp;&amp;</c> delante de (c) o
    /// (d), <c>bloqueVivo ? SinReporte : null</c> en (a)— deja la fila correspondiente en rojo con
    /// <c>null</c>.</summary>
    [Theory]
    [InlineData(null, null, null, 9L, 0L, MotivoDeRendicionPendiente.SinReporte)]
    [InlineData(0, 5, 9L, 9L, 0L, MotivoDeRendicionPendiente.VentasSinLlegar)]
    [InlineData(0, 0, 12L, 12L, 2L, MotivoDeRendicionPendiente.HuecoDeComprobantes)]
    public void LosOtrosTresDisyuntosBloqueanIgualSobreUnBloqueAbandonado(
        int? minutosDelReporte, int? pendientes, long? entregadoHasta, long techoVerificado,
        long comprobantesEnElRango, MotivoDeRendicionPendiente esperado)
    {
        var motivo = ReglaDeRendicionDeCola.Evaluar(
            bloqueVivo: false,
            reportadoAt: minutosDelReporte is { } minutos ? Momento - TimeSpan.FromMinutes(minutos) : null,
            pendientes: pendientes,
            entregadoHasta: entregadoHasta,
            desde: 10,
            techoVerificado: techoVerificado,
            comprobantesEnElRango: comprobantesEnElRango,
            momento: Momento);

        Assert.Equal(esperado, motivo);
    }

    /// <summary>El ORDEN de los disyuntos sobre un bloque ABANDONADO: con el reporte vencido Y 3
    /// ventas sin llegar, el motivo que viaja al operador es <c>VentasSinLlegar</c> y no
    /// <c>ReporteVencido</c> — la frescura ya no compite. Es el espejo de
    /// <see cref="ElReporteVencidoGanaSobreLosDemasMotivos"/>, que sigue valiendo para el bloque vivo,
    /// y el discriminante es el MOTIVO, no el hecho de bloquear: los dos estados bloquean.</summary>
    [Fact]
    public void SobreUnBloqueAbandonadoElMotivoQueGanaEsElDeLosPendientes()
    {
        var motivo = ReglaDeRendicionDeCola.Evaluar(
            bloqueVivo: false,
            reportadoAt: Momento - ReglaDeRendicionDeCola.VentanaDeFrescura - TimeSpan.FromMinutes(1),
            pendientes: 3, entregadoHasta: 12, desde: 10, techoVerificado: 12, comprobantesEnElRango: 0,
            momento: Momento);

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
