using Ways.Domain.Precios;

namespace Ways.Domain.Tests.Precios;

/// <summary>
/// La tabla de decisión de <see cref="ReglaDeAlineacionDePrecios"/> completa: cada estado posible de la referencia
/// contra cada estado posible del destino, una fila por celda. Cada celda tiene su caso porque cada una tiene su
/// mutante: cambiar una sola resolución de la regla deja verdes las demás, y un caso representativo no lo vería.
/// Los diez estados del destino son los que separan los comportamientos: sin precios, un vigente (el mismo monto de la
/// referencia y otro), un vigente con pendiente (igual, con otro monto de pendiente, con otra fecha y con otro
/// vigente) y un pendiente solo (igual, con otro monto y con otra fecha).
/// </summary>
public class ReglaDeAlineacionDePreciosTests
{
    private static readonly DateTimeOffset V = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static EstadoDePrecios Estado(string forma) => forma switch
    {
        "ninguno" => EstadoDePrecios.Vacio,
        "100" => new EstadoDePrecios(100m, null),
        "90" => new EstadoDePrecios(90m, null),
        "100 y 130 en V" => new EstadoDePrecios(100m, new PrecioPendiente(130m, V)),
        "100 y 131 en V" => new EstadoDePrecios(100m, new PrecioPendiente(131m, V)),
        "100 y 130 en otra fecha" => new EstadoDePrecios(100m, new PrecioPendiente(130m, V.AddDays(2))),
        "90 y 130 en V" => new EstadoDePrecios(90m, new PrecioPendiente(130m, V)),
        "solo 130 en V" => new EstadoDePrecios(null, new PrecioPendiente(130m, V)),
        "solo 131 en V" => new EstadoDePrecios(null, new PrecioPendiente(131m, V)),
        "solo 130 en otra fecha" => new EstadoDePrecios(null, new PrecioPendiente(130m, V.AddDays(2))),
        _ => throw new ArgumentOutOfRangeException(nameof(forma), forma, "Forma de estado desconocida.")
    };

    private static readonly string[] Destinos =
    [
        "ninguno", "100", "90", "100 y 130 en V", "100 y 131 en V", "100 y 130 en otra fecha", "90 y 130 en V",
        "solo 130 en V", "solo 131 en V", "solo 130 en otra fecha"
    ];

    public static TheoryData<string, string, ResolucionDeAlineacionDePrecios> TablaDeDecision()
    {
        var tabla = new TheoryData<string, string, ResolucionDeAlineacionDePrecios>();

        // (ninguno, ninguno): solo el destino sin precios ya está alineado; cualquier otro tiene un precio que no se puede quitar.
        foreach (var destino in Destinos)
        {
            tabla.Add(
                "ninguno", destino,
                destino == "ninguno"
                    ? ResolucionDeAlineacionDePrecios.SinCambios
                    : ResolucionDeAlineacionDePrecios.InalineablePorReferenciaSinPrecios);
        }

        // (c, ninguno) y (c, (p, V)): el destino idéntico no escribe nada; cualquier otro pasa a tener el estado de la referencia.
        foreach (var referencia in new[] { "100", "100 y 130 en V" })
        {
            foreach (var destino in Destinos)
            {
                tabla.Add(
                    referencia, destino,
                    destino == referencia
                        ? ResolucionDeAlineacionDePrecios.SinCambios
                        : ResolucionDeAlineacionDePrecios.Alinear);
            }
        }

        // (ninguno, (p, V)): el destino sin vigente se alinea (con el pendiente propio reemplazado); el que tiene vigente no se puede.
        foreach (var destino in Destinos)
        {
            var resolucion = destino switch
            {
                "solo 130 en V" => ResolucionDeAlineacionDePrecios.SinCambios,
                "ninguno" or "solo 131 en V" or "solo 130 en otra fecha" => ResolucionDeAlineacionDePrecios.Alinear,
                _ => ResolucionDeAlineacionDePrecios.InalineablePorReferenciaSoloProgramada
            };

            tabla.Add("solo 130 en V", destino, resolucion);
        }

        return tabla;
    }

    [Theory]
    [MemberData(nameof(TablaDeDecision))]
    public void CadaCeldaDeLaTablaDaSuResolucion(string referencia, string destino, ResolucionDeAlineacionDePrecios esperada)
    {
        Assert.Equal(esperada, ReglaDeAlineacionDePrecios.Resolver(Estado(referencia), Estado(destino)));
    }

    /// <summary>La tabla cubre cada estado de la referencia contra los diez del destino: cuarenta celdas, sin
    /// repetidas.</summary>
    [Fact]
    public void LaTablaCubreCadaEstadoDeLaReferenciaContraCadaEstadoDelDestino()
    {
        var celdas = TablaDeDecision().Select(fila => ((string)fila[0], (string)fila[1])).ToList();

        Assert.Equal(40, celdas.Count);
        Assert.Equal(40, celdas.Distinct().Count());
        Assert.Equal(
            ["100", "100 y 130 en V", "ninguno", "solo 130 en V"],
            celdas.Select(celda => celda.Item1).Distinct().Order(StringComparer.Ordinal));
    }

    /// <summary>Un estado es siempre igual a sí mismo: cualquiera de los diez, como referencia y como destino, no
    /// escribe nada.</summary>
    [Fact]
    public void UnEstadoContraSiMismoNuncaEscribeNada()
    {
        foreach (var forma in Destinos)
        {
            Assert.Equal(
                ResolucionDeAlineacionDePrecios.SinCambios,
                ReglaDeAlineacionDePrecios.Resolver(Estado(forma), Estado(forma)));
        }
    }

    /// <summary>Lo que se alinea queda alineado: las filas del estado de la referencia, insertadas en un par sin filas
    /// abiertas, lo dejan en el estado de la referencia, así que el destino ya alineado es un <c>SinCambios</c>. Para
    /// cada estado de la referencia que no sea un rechazo contra ningún destino.</summary>
    [Fact]
    public void ElDestinoQueQuedaConLasFilasDeLaReferenciaEstaAlineado()
    {
        var ahora = V.AddDays(-5);

        foreach (var forma in new[] { "100", "100 y 130 en V", "solo 130 en V" })
        {
            var referencia = Estado(forma);
            var filas = ReglaDeCopiaDePrecios.FilasDelEstado(referencia, ahora);
            var destinoAlineado = EstadoDePrecios.De(filas, ahora);

            Assert.Equal(ResolucionDeAlineacionDePrecios.SinCambios, ReglaDeAlineacionDePrecios.Resolver(referencia, destinoAlineado));
        }
    }
}
