using Ways.Domain.Precios;

namespace Ways.Domain.Tests.Precios;

/// <summary>
/// El estado de precios de un par (artículo, lista fija) a un instante (<see cref="EstadoDePrecios"/>): el
/// precio vigente y, si lo hay, el pendiente, sin historia. Función pura: cada caso es un conjunto de filas de
/// <c>precios</c> posible. Las filas de las pruebas de <see cref="ReglaDeCopiaDePrecios"/> pasan por esta misma
/// regla, así que sus casos de frontera también la cubren por ese lado.
/// </summary>
public class EstadoDePreciosTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static FilaDePrecio Fila(decimal monto, DateTimeOffset desde, DateTimeOffset? hasta = null) =>
        new(monto, desde, hasta);

    [Fact]
    public void SinFilasElEstadoEsVacio()
    {
        Assert.Equal(EstadoDePrecios.Vacio, EstadoDePrecios.De([], Ahora));
        Assert.Null(EstadoDePrecios.Vacio.Vigente);
        Assert.Null(EstadoDePrecios.Vacio.Pendiente);
    }

    [Fact]
    public void ConUnaFilaAbiertaDesdeElPasadoElVigenteEsEseMonto()
    {
        var estado = EstadoDePrecios.De([Fila(100m, Ahora.AddDays(-30))], Ahora);

        Assert.Equal(new EstadoDePrecios(100m, null), estado);
    }

    /// <summary>El vigente se cierra donde empieza el pendiente: las dos filas del estado que deja programar un
    /// precio. El estado trae el monto del vigente y el monto y la fecha del pendiente.</summary>
    [Fact]
    public void ConUnVigenteCerradoEnElInicioDelPendienteElEstadoTraeLosDos()
    {
        var desdeDelPendiente = Ahora.AddDays(3);

        var estado = EstadoDePrecios.De(
            [Fila(100m, Ahora.AddDays(-30), desdeDelPendiente), Fila(130m, desdeDelPendiente)], Ahora);

        Assert.Equal(new EstadoDePrecios(100m, new PrecioPendiente(130m, desdeDelPendiente)), estado);
    }

    [Fact]
    public void ConSoloUnPendienteNoHayVigente()
    {
        var desdeDelPendiente = Ahora.AddDays(2);

        var estado = EstadoDePrecios.De([Fila(130m, desdeDelPendiente)], Ahora);

        Assert.Equal(new EstadoDePrecios(null, new PrecioPendiente(130m, desdeDelPendiente)), estado);
    }

    [Fact]
    public void LaHistoriaCerradaNoEsNiVigenteNiPendiente()
    {
        var estado = EstadoDePrecios.De(
            [Fila(70m, Ahora.AddDays(-90), Ahora.AddDays(-60)), Fila(80m, Ahora.AddDays(-60), Ahora.AddDays(-30))],
            Ahora);

        Assert.Equal(EstadoDePrecios.Vacio, estado);
    }

    /// <summary>El reemplazo de un pendiente con la misma fecha deja una fila muerta (<c>vigente_desde ==
    /// vigente_hasta</c>, a futuro): no es el vigente ni el pendiente.</summary>
    [Fact]
    public void UnaFilaMuertaDeUnReemplazoConLaMismaFechaNoCuenta()
    {
        var desdeDelPendiente = Ahora.AddDays(3);

        var estado = EstadoDePrecios.De(
            [
                Fila(100m, Ahora.AddDays(-30), desdeDelPendiente),
                Fila(120m, desdeDelPendiente, desdeDelPendiente),
                Fila(130m, desdeDelPendiente)
            ],
            Ahora);

        Assert.Equal(new EstadoDePrecios(100m, new PrecioPendiente(130m, desdeDelPendiente)), estado);
    }

    /// <summary>La frontera: una fila que empieza EXACTAMENTE en "ahora" ya es el vigente
    /// (<c>vigente_desde &lt;= ahora</c>), no el pendiente.</summary>
    [Fact]
    public void UnaFilaQueEmpiezaExactamenteEnAhoraEsElVigente()
    {
        var estado = EstadoDePrecios.De([Fila(100m, Ahora)], Ahora);

        Assert.Equal(new EstadoDePrecios(100m, null), estado);
    }

    /// <summary>La otra frontera: una fila que se cierra EXACTAMENTE en "ahora" ya no es el vigente
    /// (<c>vigente_hasta &gt; ahora</c>), y si ninguna otra la reemplaza el par queda sin vigente. Sola, para que
    /// ninguna otra fila decida el resultado: con una sucesora abierta desde ese instante el orden por fecha de
    /// inicio elegiría a la sucesora igual.</summary>
    [Fact]
    public void UnaFilaQueSeCierraExactamenteEnAhoraYaNoEsElVigenteAunqueNingunaLaReemplace()
    {
        var estado = EstadoDePrecios.De([Fila(90m, Ahora.AddDays(-10), Ahora)], Ahora);

        Assert.Equal(EstadoDePrecios.Vacio, estado);
    }

    [Fact]
    public void LaSucesoraAbiertaDesdeElInstanteEnQueSeCierraLaAnteriorEsElVigente()
    {
        var estado = EstadoDePrecios.De([Fila(90m, Ahora.AddDays(-10), Ahora), Fila(100m, Ahora)], Ahora);

        Assert.Equal(new EstadoDePrecios(100m, null), estado);
    }

    /// <summary>Con dos candidatos a vigente, gana el de <c>vigente_desde</c> más reciente; con dos a pendiente,
    /// el más próximo. El orden en que llegan las filas no cambia el resultado.</summary>
    [Fact]
    public void ConVariosCandidatosGananElVigenteMasRecienteYElPendienteMasProximo()
    {
        var filas = new[]
        {
            Fila(50m, Ahora.AddDays(-20)),
            Fila(60m, Ahora.AddDays(-5)),
            Fila(300m, Ahora.AddDays(9)),
            Fila(200m, Ahora.AddDays(4))
        };

        var esperado = new EstadoDePrecios(60m, new PrecioPendiente(200m, Ahora.AddDays(4)));

        Assert.Equal(esperado, EstadoDePrecios.De(filas, Ahora));
        Assert.Equal(esperado, EstadoDePrecios.De([.. filas.Reverse()], Ahora));
    }

    /// <summary>Dos estados son iguales por valor: el mismo vigente, y el mismo monto y la misma fecha de
    /// pendiente. Lo que la alineación de una familia compara es exactamente esto.</summary>
    [Fact]
    public void DosEstadosSonIgualesPorValorYDifierenEnCualquieraDeSusPartes()
    {
        var desde = Ahora.AddDays(3);
        var estado = new EstadoDePrecios(100m, new PrecioPendiente(130m, desde));

        Assert.Equal(estado, new EstadoDePrecios(100m, new PrecioPendiente(130m, desde)));
        Assert.NotEqual(estado, new EstadoDePrecios(101m, new PrecioPendiente(130m, desde)));
        Assert.NotEqual(estado, new EstadoDePrecios(100m, new PrecioPendiente(131m, desde)));
        Assert.NotEqual(estado, new EstadoDePrecios(100m, new PrecioPendiente(130m, desde.AddDays(1))));
        Assert.NotEqual(estado, new EstadoDePrecios(100m, null));
        Assert.NotEqual(estado, new EstadoDePrecios(null, new PrecioPendiente(130m, desde)));
    }

    // =================================================================================================
    // Las filas de un estado: lo que se inserta es lo que después se lee
    // =================================================================================================

    private static EstadoDePrecios EstadoDeLaForma(string forma)
    {
        var desde = Ahora.AddDays(3);

        return forma switch
        {
            "vacio" => EstadoDePrecios.Vacio,
            "solo vigente" => new EstadoDePrecios(100m, null),
            "vigente y pendiente" => new EstadoDePrecios(100m, new PrecioPendiente(130m, desde)),
            "solo pendiente" => new EstadoDePrecios(null, new PrecioPendiente(130m, desde)),
            _ => throw new ArgumentOutOfRangeException(nameof(forma), forma, "Forma de estado desconocida.")
        };
    }

    /// <summary>Insertar las filas de un estado en un par sin filas abiertas lo deja exactamente en ese estado:
    /// las dos reglas —leer el estado y escribir sus filas— son inversas. Una fila por cada una de las cuatro
    /// formas posibles del estado.</summary>
    [Theory]
    [InlineData("vacio")]
    [InlineData("solo vigente")]
    [InlineData("vigente y pendiente")]
    [InlineData("solo pendiente")]
    public void LasFilasDeUnEstadoLoReproducenCuandoSeLeen(string forma)
    {
        var estado = EstadoDeLaForma(forma);

        var filas = ReglaDeCopiaDePrecios.FilasDelEstado(estado, Ahora);

        Assert.Equal(estado, EstadoDePrecios.De(filas, Ahora));
    }

    [Fact]
    public void LasFilasDeUnEstadoSonLaVigenteCerradaEnElPendienteYDespuesLaPendiente()
    {
        var desde = Ahora.AddDays(3);

        Assert.Empty(ReglaDeCopiaDePrecios.FilasDelEstado(EstadoDePrecios.Vacio, Ahora));
        Assert.Equal(
            [new FilaDePrecio(100m, Ahora, null)],
            ReglaDeCopiaDePrecios.FilasDelEstado(new EstadoDePrecios(100m, null), Ahora));
        Assert.Equal(
            [new FilaDePrecio(100m, Ahora, desde), new FilaDePrecio(130m, desde, null)],
            ReglaDeCopiaDePrecios.FilasDelEstado(new EstadoDePrecios(100m, new PrecioPendiente(130m, desde)), Ahora));
        Assert.Equal(
            [new FilaDePrecio(130m, desde, null)],
            ReglaDeCopiaDePrecios.FilasDelEstado(new EstadoDePrecios(null, new PrecioPendiente(130m, desde)), Ahora));
    }
}
