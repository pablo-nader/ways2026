using Ways.Domain.Precios;

namespace Ways.Application.Precios;

/// <summary>Una lista de precios fija del tenant: su id y su nombre, que los mensajes de la alineación nombran.</summary>
internal sealed record ListaFija(int Id, string Nombre);

/// <summary>
/// La alineación de UN par (artículo destino, lista fija) con el artículo de referencia, ya decidida
/// (<see cref="ReglaDeAlineacionDePrecios"/>): el estado de precios <see cref="Actual"/> del destino, el
/// <see cref="Referencia"/> al que tiene que llegar y qué hace falta para eso. Los rechazos
/// (<see cref="EsInalineable"/>) conservan los dos estados: el mensaje al cliente los nombra.
/// </summary>
internal sealed record AlineacionDeUnPar(
    int IdArticulo,
    int IdListaPrecio,
    EstadoDePrecios Actual,
    EstadoDePrecios Referencia,
    ResolucionDeAlineacionDePrecios Resolucion)
{
    public bool EsInalineable => Resolucion is not (ResolucionDeAlineacionDePrecios.SinCambios or ResolucionDeAlineacionDePrecios.Alinear);
}

/// <summary>
/// El resultado de la FASE 1 de la alineación de precios (<c>ServicioDePrecios.PlanificarAlineacionAsync</c>): una
/// <see cref="AlineacionDeUnPar"/> por cada par (artículo destino, lista fija), en el orden en que se recibieron
/// los ids (ascendentes por id de artículo y después por id de lista), más —para los pares que se alinean— las filas
/// del destino que alinear cerraría: su fila abierta y, si era pendiente, su predecesor. Solo lecturas: planificar
/// no escribe nada, así que sirve igual para previsualizar.
/// </summary>
internal sealed class PlanDeAlineacionDePrecios(
    IReadOnlyList<AlineacionDeUnPar> pares,
    IReadOnlyDictionary<(int IdArticulo, int IdListaPrecio), ServicioDePrecios.PlanDeUnArticulo> cierres)
{
    public IReadOnlyList<AlineacionDeUnPar> Pares { get; } = pares;

    /// <summary>Las filas que cerraría cada par que se alinea. Los demás pares no tienen entrada.</summary>
    public IReadOnlyDictionary<(int IdArticulo, int IdListaPrecio), ServicioDePrecios.PlanDeUnArticulo> Cierres { get; } = cierres;

    /// <summary>El primer par que no se puede alinear, o <c>null</c>.</summary>
    public AlineacionDeUnPar? PrimerRechazo => Pares.FirstOrDefault(par => par.EsInalineable);
}
