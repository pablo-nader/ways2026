using Ways.Domain.Ofertas;

namespace Ways.Domain.Tests.Ofertas;

/// <summary>
/// <see cref="EscalonesDeCantidad"/> — función pura, sin base de datos, mismo criterio que
/// <see cref="ResolvedorDeOfertasTests"/>. Cada test nombra la cláusula que existe para matar
/// (mutation-proof-tests regla 1); las de descubrimiento se prueban contra
/// <see cref="EscalonesDeCantidad.DescubrirUmbrales"/> y no contra
/// <see cref="EscalonesDeCantidad.Construir"/> a propósito: el colapso absorbe cualquier umbral
/// espurio, así que de punta a punta esas cláusulas serían inmatables (regla 3, rutear por debajo
/// del confound).
/// </summary>
public class EscalonesDeCantidadTests
{
    private static readonly DateOnly Fecha = new(2026, 8, 3);
    private static readonly TimeOnly Hora = new(12, 0);
    private const int DiaSemana = 1; // lunes

    private static LineaAResolver CrearLinea(decimal cantidad = 1m, decimal precioOriginal = 1000m) =>
        new(1, null, [], 1, cantidad, precioOriginal, Fecha, Hora, DiaSemana);

    private static OfertaCandidata CrearCandidata(
        int id = 1, string nombre = "oferta", int prioridad = 0, bool acumulable = false,
        BeneficioDeOferta? beneficio = null, decimal? cantidadMinima = null,
        DateOnly? fechaDesde = null, DateOnly? fechaHasta = null,
        IReadOnlySet<int>? diasSemana = null) =>
        new(
            id, nombre, prioridad, acumulable,
            AlcanceDeOferta.DeArticulo(1), beneficio ?? BeneficioDeOferta.DePorcentaje(10m),
            cantidadMinima, fechaDesde, fechaHasta, null, null,
            diasSemana ?? new HashSet<int>(), new HashSet<int>());

    // ---- descubrimiento de umbrales ----------------------------------------------------------

    /// <summary>Cláusula: <c>umbral &lt;= linea.Cantidad</c> de
    /// <see cref="EscalonesDeCantidad.DescubrirUmbrales"/>. Una oferta con
    /// <c>cantidad_minima = 1</c> ya aplica a la cantidad pedida (es parte del resultado plano),
    /// así que no es un escalón; una con umbral fraccionario por debajo, tampoco.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(0.5)]
    public void UnUmbralQueNoSupereLaCantidadPedidaNoAportaUmbral(decimal cantidadMinima)
    {
        var umbrales = EscalonesDeCantidad.DescubrirUmbrales(
            CrearLinea(cantidad: 1m), [CrearCandidata(cantidadMinima: cantidadMinima)]);

        Assert.Empty(umbrales);
    }

    /// <summary>Cláusula: el <see cref="ResolvedorDeOfertas.Coincide"/> evaluado EN el umbral de
    /// <see cref="EscalonesDeCantidad.DescubrirUmbrales"/> — una oferta por volumen cuya ventana
    /// de fecha todavía no abrió no aporta umbral, aunque su <c>cantidad_minima</c> sí supere la
    /// cantidad pedida.</summary>
    [Fact]
    public void UnaOfertaPorVolumenFueraDeSuVentanaDeFechaNoAportaUmbral()
    {
        var candidata = CrearCandidata(cantidadMinima: 3m, fechaDesde: Fecha.AddDays(1));

        var umbrales = EscalonesDeCantidad.DescubrirUmbrales(CrearLinea(), [candidata]);

        Assert.Empty(umbrales);
    }

    /// <summary>Misma cláusula que
    /// <see cref="UnaOfertaPorVolumenFueraDeSuVentanaDeFechaNoAportaUmbral"/> sobre el otro eje
    /// que el <see cref="ResolvedorDeOfertas.Coincide"/> del umbral cubre: la línea cae lunes y la
    /// oferta por volumen solo vale martes.</summary>
    [Fact]
    public void UnaOfertaPorVolumenFueraDeSuDiaDeSemanaNoAportaUmbral()
    {
        var candidata = CrearCandidata(cantidadMinima: 3m, diasSemana: new HashSet<int> { 2 });

        var umbrales = EscalonesDeCantidad.DescubrirUmbrales(CrearLinea(), [candidata]);

        Assert.Empty(umbrales);
    }

    /// <summary>Cláusula: el <c>HashSet</c> de
    /// <see cref="EscalonesDeCantidad.DescubrirUmbrales"/> — dos ofertas distintas con el MISMO
    /// umbral son un solo escalón (el umbral es el eje, no la oferta).</summary>
    [Fact]
    public void DosOfertasConElMismoUmbralAportanUnSoloUmbral()
    {
        var primera = CrearCandidata(id: 1, cantidadMinima: 3m);
        var segunda = CrearCandidata(id: 2, cantidadMinima: 3m, acumulable: true);

        var umbrales = EscalonesDeCantidad.DescubrirUmbrales(CrearLinea(), [primera, segunda]);

        Assert.Equal([3m], umbrales);
    }

    /// <summary>Cláusula: el <c>OrderBy</c> de
    /// <see cref="EscalonesDeCantidad.DescubrirUmbrales"/> — las candidatas llegan en el orden
    /// arbitrario de la consulta, y <see cref="EscalonesDeCantidad.Colapsar"/> compara contra el
    /// anterior CONSERVADO, así que sin orden ascendente el colapso compararía contra el escalón
    /// equivocado.</summary>
    [Fact]
    public void LosUmbralesSalenAscendentesAunqueLasCandidatasVenganAlReves()
    {
        var umbrales = EscalonesDeCantidad.DescubrirUmbrales(
            CrearLinea(),
            [
                CrearCandidata(id: 1, cantidadMinima: 10m),
                CrearCandidata(id: 2, cantidadMinima: 5m),
                CrearCandidata(id: 3, cantidadMinima: 3m)
            ]);

        Assert.Equal([3m, 5m, 10m], umbrales);
    }

    // ---- curva completa ----------------------------------------------------------------------

    /// <summary>Camino feliz: la oferta por volumen gana la base a partir de su umbral, y el
    /// escalón viaja con el precio YA RESUELTO por el motor (no con la oferta cruda).
    /// <c>PrecioOriginal</c> no cambia entre cantidades — por eso el contrato HTTP no lo
    /// repite.</summary>
    [Fact]
    public void UnaOfertaPorVolumenGeneraSuEscalonConElPrecioYaResuelto()
    {
        var directa = CrearCandidata(id: 1, prioridad: 0, beneficio: BeneficioDeOferta.DePorcentaje(10m));
        var porVolumen = CrearCandidata(
            id: 2, prioridad: 10, beneficio: BeneficioDeOferta.DePorcentaje(25m), cantidadMinima: 3m);

        var escalones = EscalonesDeCantidad.Construir(CrearLinea(), [directa, porVolumen]).Escalones;

        var escalon = Assert.Single(escalones);
        Assert.Equal(3m, escalon.CantidadDesde);
        Assert.Equal(1000m, escalon.Resolucion.PrecioOriginal);
        Assert.Equal(250m, escalon.Resolucion.DescuentoUnitario);
        Assert.Equal(750m, escalon.Resolucion.PrecioFinal);
        var aplicada = Assert.Single(escalon.Resolucion.Aplicadas);
        Assert.Equal(2, aplicada.IdOferta);
    }

    /// <summary>La entrada de cantidad 1 NUNCA es un escalón: una oferta con
    /// <c>cantidad_minima = 1</c> ya está en el resultado plano de la línea.
    ///
    /// <para>Honestidad del alcance (claims-match-code): acá el resultado está SOBREDETERMINADO —
    /// lo entregan juntos el filtro <c>umbral &lt;= linea.Cantidad</c> y el colapso (a cantidad 1,
    /// resolver en el umbral 1 da exactamente el resultado plano, así que el colapso lo
    /// descartaría igual). La cláusula del filtro la mata
    /// <see cref="UnUmbralQueNoSupereLaCantidadPedidaNoAportaUmbral"/> (por debajo del colapso) y
    /// su consecuencia de negocio
    /// <see cref="UnUmbralPorDebajoDeLaCantidadPedidaNuncaEntraEnLaCurva"/>; este test fija la
    /// conducta observable del contrato, no una cláusula.</para></summary>
    [Fact]
    public void UnaOfertaConUmbralIgualAUnoNoGeneraEscalon()
    {
        var escalones = EscalonesDeCantidad.Construir(
            CrearLinea(cantidad: 1m), [CrearCandidata(cantidadMinima: 1m)]).Escalones;

        Assert.Empty(escalones);
    }

    /// <summary>Cláusula: <c>umbral &lt;= linea.Cantidad</c>, pero por su CONSECUENCIA de negocio
    /// — el dispositivo elige el ÚLTIMO escalón cuyo umbral entra en la cantidad, así que un
    /// escalón por DEBAJO de la cantidad ya resuelta le haría cobrar el precio de menos unidades.
    /// Pedida cantidad 5 con una oferta de umbral 5 (la que aplica) y otra de umbral 2 (peor), la
    /// curva por encima de 5 está vacía.
    ///
    /// <para>Sin el filtro, el umbral 2 entraría y su resolución (sin la oferta de umbral 5) SÍ
    /// difiere del resultado plano, así que el colapso no lo salva: aparecerían dos escalones
    /// (2 y 5) y el carrito de 5 unidades cobraría el descuento de 2.</para></summary>
    [Fact]
    public void UnUmbralPorDebajoDeLaCantidadPedidaNuncaEntraEnLaCurva()
    {
        var porVolumen = CrearCandidata(
            id: 1, prioridad: 10, beneficio: BeneficioDeOferta.DePorcentaje(30m), cantidadMinima: 5m);
        var deArranque = CrearCandidata(
            id: 2, prioridad: 5, beneficio: BeneficioDeOferta.DePorcentaje(10m), cantidadMinima: 2m);

        var linea = CrearLinea(cantidad: 5m);

        Assert.Equal(300m, ResolvedorDeOfertas.Resolver(linea, [porVolumen, deArranque]).DescuentoUnitario);
        Assert.Empty(EscalonesDeCantidad.Construir(linea, [porVolumen, deArranque]).Escalones);
    }

    /// <summary>Los escalones salen ascendentes y cada uno resuelto A SU cantidad — tres umbrales
    /// que se van ganando la base uno al otro, con las candidatas entrando al revés.</summary>
    [Fact]
    public void LosEscalonesSalenAscendentesConCadaUnoResueltoASuCantidad()
    {
        var diez = CrearCandidata(
            id: 1, prioridad: 3, beneficio: BeneficioDeOferta.DePorcentaje(30m), cantidadMinima: 10m);
        var cinco = CrearCandidata(
            id: 2, prioridad: 2, beneficio: BeneficioDeOferta.DePorcentaje(20m), cantidadMinima: 5m);
        var tres = CrearCandidata(
            id: 3, prioridad: 1, beneficio: BeneficioDeOferta.DePorcentaje(10m), cantidadMinima: 3m);

        var escalones = EscalonesDeCantidad.Construir(CrearLinea(), [diez, cinco, tres]).Escalones;

        Assert.Equal([3m, 5m, 10m], escalones.Select(e => e.CantidadDesde));
        Assert.Equal([100m, 200m, 300m], escalones.Select(e => e.Resolucion.DescuentoUnitario));
        Assert.Equal([900m, 800m, 700m], escalones.Select(e => e.Resolucion.PrecioFinal));
        Assert.Equal([3, 2, 1], escalones.Select(e => Assert.Single(e.Resolucion.Aplicadas).IdOferta));
    }

    // ---- colapso -----------------------------------------------------------------------------

    /// <summary>Cláusula: el <c>continue</c> de <see cref="EscalonesDeCantidad.Colapsar"/> — un
    /// umbral cuya oferta PIERDE contra la base que ya aplicaba no cambia nada, así que no viaja.
    /// La oferta por volumen descuenta 10% con prioridad 10; la directa, 30% con prioridad 20: a
    /// partir de 3 unidades sigue ganando la directa.</summary>
    [Fact]
    public void UnUmbralQuePierdeContraLaBaseVigenteNoGeneraEscalon()
    {
        var directa = CrearCandidata(id: 1, prioridad: 20, beneficio: BeneficioDeOferta.DePorcentaje(30m));
        var porVolumen = CrearCandidata(
            id: 2, prioridad: 10, beneficio: BeneficioDeOferta.DePorcentaje(10m), cantidadMinima: 3m);

        Assert.Empty(EscalonesDeCantidad.Construir(CrearLinea(), [directa, porVolumen]).Escalones);
    }

    /// <summary>Cláusula: el <c>SequenceEqual</c> de ids aplicados de
    /// <see cref="EscalonesDeCantidad.Colapsar"/> — el escalón llega al MISMO descuento total que
    /// el resultado plano ($100 sobre $1000) pero con otras ofertas (una base de $50 con mayor
    /// prioridad más una acumulable de $50), y cuáles aplicaron es dato del ticket, así que el
    /// escalón NO se colapsa.</summary>
    [Fact]
    public void UnEscalonConElMismoDescuentoPeroOtrasOfertasNoSeColapsa()
    {
        var directa = CrearCandidata(id: 1, prioridad: 10, beneficio: BeneficioDeOferta.DePorcentaje(10m));
        var baseDeVolumen = CrearCandidata(
            id: 2, prioridad: 20, beneficio: BeneficioDeOferta.DeImporteFijo(50m), cantidadMinima: 3m);
        var acumulableDeVolumen = CrearCandidata(
            id: 3, prioridad: 5, acumulable: true,
            beneficio: BeneficioDeOferta.DeImporteFijo(50m), cantidadMinima: 3m);

        var linea = CrearLinea();
        var plano = ResolvedorDeOfertas.Resolver(linea, [directa, baseDeVolumen, acumulableDeVolumen]);

        var escalones = EscalonesDeCantidad.Construir(linea, [directa, baseDeVolumen, acumulableDeVolumen]).Escalones;

        Assert.Equal(100m, plano.DescuentoUnitario);
        var escalon = Assert.Single(escalones);
        Assert.Equal(3m, escalon.CantidadDesde);
        Assert.Equal(100m, escalon.Resolucion.DescuentoUnitario);
        Assert.Equal([2, 3], escalon.Resolucion.Aplicadas.Select(a => a.IdOferta));
    }

    /// <summary>Cláusula: el <c>DescuentoUnitario ==</c> de
    /// <see cref="EscalonesDeCantidad.Colapsar"/>.
    ///
    /// <para>Honestidad del alcance (claims-match-code): esta entrada se arma a mano y HOY el
    /// pipeline no la produce — con el mismo conjunto de ids aplicados y el mismo precio original,
    /// la aritmética de <see cref="ResolvedorDeOfertas"/> da siempre el mismo descuento, porque la
    /// cantidad no entra en el cálculo. El test fija el contrato de
    /// <see cref="EscalonesDeCantidad.Colapsar"/> (compara lo que el dispositivo COBRA, no solo
    /// qué ofertas se reportaron), que es lo que lo mantiene correcto si algún beneficio futuro
    /// pasa a depender de la cantidad.</para></summary>
    [Fact]
    public void ColapsarNoDescartaUnEscalonQueCambiaElDescuentoConLasMismasOfertas()
    {
        var plano = new PrecioConOfertas(1000m, 900m, 100m, [new OfertaAplicada(1, "oferta", 100m)]);
        var candidato = new EscalonResuelto(
            3m, new PrecioConOfertas(1000m, 800m, 200m, [new OfertaAplicada(1, "oferta", 200m)]));

        var escalones = EscalonesDeCantidad.Colapsar(plano, [candidato]);

        Assert.Equal([3m], escalones.Select(e => e.CantidadDesde));
    }
}
