using Ways.Domain.Precios;

namespace Ways.Domain.Tests.Precios;

/// <summary>
/// Qué filas de <c>precios</c> recibe, en una lista fija, un artículo recién creado que entra a una familia
/// (<see cref="ReglaDeCopiaDePrecios"/>): el estado de precios del miembro de referencia a un instante —el
/// precio vigente y, si lo hay, el pendiente—, sin su historia. Función pura: cada caso es un estado posible
/// de la referencia.
/// </summary>
public class ReglaDeCopiaDePreciosTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static FilaDePrecio Fila(decimal monto, DateTimeOffset desde, DateTimeOffset? hasta = null) =>
        new(monto, desde, hasta);

    [Fact]
    public void SinFilasLaReferenciaNoTransmiteNada()
    {
        Assert.Empty(ReglaDeCopiaDePrecios.FilasParaElNuevoMiembro([], Ahora));
    }

    /// <summary>El precio vigente arranca en "ahora" —el artículo recién nace—, no en la fecha en que el de la
    /// referencia empezó, y queda abierto.</summary>
    [Fact]
    public void ConSoloUnVigenteElNuevoArrancaEnAhoraYQuedaAbierto()
    {
        var resultado = ReglaDeCopiaDePrecios.FilasParaElNuevoMiembro([Fila(100m, Ahora.AddDays(-30))], Ahora);

        Assert.Equal([new FilaDePrecio(100m, Ahora, null)], resultado);
    }

    /// <summary>Con un pendiente, el vigente se cierra donde empieza el pendiente y el pendiente se hereda con
    /// su fecha y su monto, abierto. Primero la vigente y después la pendiente.</summary>
    [Fact]
    public void ConUnPendienteElVigenteSeCierraDondeEmpiezaElPendienteYElPendienteSeHereda()
    {
        var desdeDelPendiente = Ahora.AddDays(3);

        var resultado = ReglaDeCopiaDePrecios.FilasParaElNuevoMiembro(
            [
                Fila(100m, Ahora.AddDays(-30), desdeDelPendiente),
                Fila(130m, desdeDelPendiente)
            ],
            Ahora);

        Assert.Equal(
            [
                new FilaDePrecio(100m, Ahora, desdeDelPendiente),
                new FilaDePrecio(130m, desdeDelPendiente, null)
            ],
            resultado);
    }

    /// <summary>Un precio programado antes de que la referencia tuviera ninguno: solo hay pendiente, y el nuevo
    /// hereda solo eso, sin inventar un vigente.</summary>
    [Fact]
    public void ConSoloUnPendienteElNuevoHeredaSoloElPendiente()
    {
        var desdeDelPendiente = Ahora.AddDays(2);

        var resultado = ReglaDeCopiaDePrecios.FilasParaElNuevoMiembro([Fila(130m, desdeDelPendiente)], Ahora);

        Assert.Equal([new FilaDePrecio(130m, desdeDelPendiente, null)], resultado);
    }

    /// <summary>La historia de la referencia (filas ya cerradas) no se copia: el artículo nuevo no la tiene.</summary>
    [Fact]
    public void LaHistoriaCerradaDeLaReferenciaNoSeCopia()
    {
        var resultado = ReglaDeCopiaDePrecios.FilasParaElNuevoMiembro(
            [
                Fila(70m, Ahora.AddDays(-90), Ahora.AddDays(-60)),
                Fila(80m, Ahora.AddDays(-60), Ahora.AddDays(-30)),
                Fila(100m, Ahora.AddDays(-30))
            ],
            Ahora);

        Assert.Equal([new FilaDePrecio(100m, Ahora, null)], resultado);
    }

    [Fact]
    public void UnaReferenciaConSoloHistoriaCerradaNoTransmiteNada()
    {
        var resultado = ReglaDeCopiaDePrecios.FilasParaElNuevoMiembro(
            [Fila(70m, Ahora.AddDays(-90), Ahora.AddDays(-60))], Ahora);

        Assert.Empty(resultado);
    }

    /// <summary>El reemplazo de un pendiente con la misma fecha deja una fila muerta (<c>vigente_desde ==
    /// vigente_hasta</c>, a futuro): no es el vigente ni el pendiente, y no se copia.</summary>
    [Fact]
    public void UnaFilaMuertaDeUnReemplazoConLaMismaFechaNoSeCopia()
    {
        var desdeDelPendiente = Ahora.AddDays(3);

        var resultado = ReglaDeCopiaDePrecios.FilasParaElNuevoMiembro(
            [
                Fila(100m, Ahora.AddDays(-30), desdeDelPendiente),
                Fila(120m, desdeDelPendiente, desdeDelPendiente),
                Fila(130m, desdeDelPendiente)
            ],
            Ahora);

        Assert.Equal(
            [
                new FilaDePrecio(100m, Ahora, desdeDelPendiente),
                new FilaDePrecio(130m, desdeDelPendiente, null)
            ],
            resultado);
    }

    /// <summary>La frontera: una fila que empieza EXACTAMENTE en "ahora" ya es el vigente (<c>vigente_desde
    /// &lt;= ahora</c>) y no el pendiente.</summary>
    [Fact]
    public void UnaFilaQueEmpiezaExactamenteEnAhoraEsElVigente()
    {
        var resultado = ReglaDeCopiaDePrecios.FilasParaElNuevoMiembro([Fila(100m, Ahora)], Ahora);

        Assert.Equal([new FilaDePrecio(100m, Ahora, null)], resultado);
    }

    /// <summary>La otra frontera: una fila que se cierra EXACTAMENTE en "ahora" ya no es el vigente
    /// (<c>vigente_hasta &gt; ahora</c>), y la que la reemplaza, abierta desde ese instante, sí.</summary>
    [Fact]
    public void UnaFilaQueSeCierraExactamenteEnAhoraYaNoEsElVigente()
    {
        var resultado = ReglaDeCopiaDePrecios.FilasParaElNuevoMiembro(
            [
                Fila(90m, Ahora.AddDays(-10), Ahora),
                Fila(100m, Ahora)
            ],
            Ahora);

        Assert.Equal([new FilaDePrecio(100m, Ahora, null)], resultado);
    }

    /// <summary>El orden en que llegan las filas no cambia el resultado.</summary>
    [Fact]
    public void ElOrdenDeLlegadaDeLasFilasNoCambiaElResultado()
    {
        var desdeDelPendiente = Ahora.AddDays(3);
        var filas = new[]
        {
            Fila(70m, Ahora.AddDays(-90), Ahora.AddDays(-30)),
            Fila(100m, Ahora.AddDays(-30), desdeDelPendiente),
            Fila(130m, desdeDelPendiente)
        };

        var deIda = ReglaDeCopiaDePrecios.FilasParaElNuevoMiembro(filas, Ahora);
        var deVuelta = ReglaDeCopiaDePrecios.FilasParaElNuevoMiembro([.. filas.Reverse()], Ahora);

        Assert.Equal(deIda, deVuelta);
        Assert.Equal(2, deIda.Count);
    }
}
