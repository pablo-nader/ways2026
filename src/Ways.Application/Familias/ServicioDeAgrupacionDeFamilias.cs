using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Precios;
using Ways.Domain.Articulos;
using Ways.Domain.Common;
using Ways.Domain.Precios;

namespace Ways.Application.Familias;

/// <summary>
/// Agrupar artículos en una familia (doc 10 §3, "Familias de artículos"): hoy, la previsualización de lo que
/// cambiaría. Autorización: <c>Politicas.GestionDeCatalogo</c> aplicada en la capa de API, la misma puerta que el alta
/// y la edición de artículos.
///
/// <para>Alinear un artículo con el de referencia es dejarlo idéntico a él en los trece campos compartidos
/// (<see cref="ValoresCompartidosDeFamilia"/>) y en el estado de precios de cada lista fija
/// (<see cref="EstadoDePrecios"/>); los campos propios no se tocan. La previsualización es solo lectura, sin locks y
/// sin transacción: una foto.</para>
/// </summary>
public class ServicioDeAgrupacionDeFamilias(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, ServicioDePrecios servicioDePrecios)
{
    /// <summary>
    /// Qué cambiaría si se agruparan los artículos pedidos con la referencia (<see cref="SolicitudDePrevisualizacion"/>):
    /// por cada artículo que se puede alinear, las columnas compartidas que cambian (actual → nuevo) y el cambio de precios
    /// de cada lista fija; y todos los problemas que impedirían agrupar (<see cref="ProblemaDeAgrupacion"/>), con el
    /// código y el mensaje del error que el pedido real daría. Si la referencia no existe no hay nada contra qué
    /// comparar y la respuesta trae solo los artículos inexistentes.
    ///
    /// <para>Solo lee: no abre transacción, no toma ningún lock y no rastrea ninguna entidad. "Ahora" se lee una sola
    /// vez, para clasificar los precios de todos los artículos igual.</para>
    /// </summary>
    public async Task<PrevisualizacionDeAgrupacion> PrevisualizarAsync(
        SolicitudDePrevisualizacion datos, CancellationToken ct = default)
    {
        ReglaDeAgrupacion.ExigirReferencia(datos.IdArticuloReferencia);
        var destinosPedidos = ReglaDeAgrupacion.Destinos(datos.IdsArticulos, datos.IdArticuloReferencia);
        var idTenant = ExigirTenantDeLaSesion();
        var ahora = reloj.Ahora;

        var idsPedidos = new List<int>(destinosPedidos.Count + 1) { datos.IdArticuloReferencia };
        idsPedidos.AddRange(destinosPedidos);
        var idsAConsultar = idsPedidos.ToArray();

        var articulos = (await db.Articulos.AsNoTracking().Where(a => idsAConsultar.Contains(a.Id)).ToListAsync(ct))
            .ToDictionary(a => a.Id);

        var inexistentes = idsPedidos
            .Order()
            .Where(id => !articulos.ContainsKey(id))
            .Select(id => new ProblemaDeAgrupacion("referencia_invalida", MensajeDeArticuloInexistente(id), id, null))
            .ToList();

        if (!articulos.TryGetValue(datos.IdArticuloReferencia, out var referencia))
        {
            return new PrevisualizacionDeAgrupacion(datos.IdArticuloReferencia, IdFamilia: null, [], inexistentes);
        }

        var problemas = new List<ProblemaDeAgrupacion>();

        if (referencia.IdFamilia is { } idFamilia)
        {
            var familia = await db.Familias
                .AsNoTracking()
                .Where(f => f.Id == idFamilia)
                .Select(f => new { f.Nombre, f.Activo })
                .FirstOrDefaultAsync(ct);

            if (familia is null)
            {
                problemas.Add(new ProblemaDeAgrupacion("no_encontrado", MensajeDeFamiliaInexistente(idFamilia), null, null));
            }
            else if (ReglaDeFamilias.ResolverAgregado(familia.Activo, tieneMiembrosVivos: true) == ResolucionDeIngresoAFamilia.FamiliaInactiva)
            {
                problemas.Add(new ProblemaDeAgrupacion(
                    "familia_inactiva", MensajeDeFamiliaInactiva(familia.Nombre), null, null));
            }
        }

        problemas.AddRange(inexistentes);

        var enOtraFamilia = destinosPedidos
            .Where(id => articulos.TryGetValue(id, out var a) && a.IdFamilia is not null && a.IdFamilia != referencia.IdFamilia)
            .ToList();
        var nombresDeFamilias = await NombresDeFamiliasAsync(
            [.. enOtraFamilia.Select(id => articulos[id].IdFamilia!.Value)], ct);

        problemas.AddRange(enOtraFamilia.Select(id => new ProblemaDeAgrupacion(
            "articulo_en_otra_familia",
            MensajeDeArticuloEnOtraFamilia(articulos[id], nombresDeFamilias),
            id,
            null)));

        var alineables = destinosPedidos
            .Where(id => articulos.TryGetValue(id, out var a) && (a.IdFamilia is null || a.IdFamilia == referencia.IdFamilia))
            .ToList();

        var listas = await servicioDePrecios.ListasFijasAsync(ct);
        var plan = await servicioDePrecios.PlanificarAlineacionAsync(
            referencia.Id, alineables, [.. listas.Select(l => l.Id)], idTenant, ahora, ct);

        var valoresDeLaReferencia = ValoresCompartidosDeFamilia.De(referencia);

        problemas.AddRange(plan.Pares.Where(par => par.EsInalineable).Select(par => new ProblemaDeAgrupacion(
            "familia_precio_inalineable",
            MensajeDePrecioInalineable(articulos[par.IdArticulo], listas.Single(l => l.Id == par.IdListaPrecio), par),
            par.IdArticulo,
            par.IdListaPrecio)));

        var cambios = alineables
            .Select(id => CambiosDe(articulos[id], valoresDeLaReferencia, plan))
            .ToList();

        return new PrevisualizacionDeAgrupacion(referencia.Id, referencia.IdFamilia, cambios, problemas);
    }

    /// <summary>Lo que cambia en <paramref name="articulo"/>: las columnas compartidas que difieren de las de la
    /// referencia y los pares de la lista que se alinean.</summary>
    private static CambiosDeUnArticulo CambiosDe(
        Articulo articulo, ValoresCompartidosDeFamilia valoresDeLaReferencia, PlanDeAlineacionDePrecios plan)
    {
        var actuales = ValoresCompartidosDeFamilia.De(articulo);

        return new CambiosDeUnArticulo(
            articulo.Id,
            valoresDeLaReferencia.CamposDistintos(actuales),
            actuales,
            valoresDeLaReferencia,
            [
                .. plan.Pares
                    .Where(par => par.IdArticulo == articulo.Id && par.Resolucion == ResolucionDeAlineacionDePrecios.Alinear)
                    .Select(par => new CambioDePreciosDeLista(par.IdListaPrecio, par.Actual, par.Referencia))
            ]);
    }

    /// <summary>Los nombres de las familias dadas, también las dadas de baja: el mensaje de un artículo que ya
    /// pertenece a una familia la nombra aunque la fila de la familia ya no esté viva. Solo el filtro de baja lógica se
    /// ignora; el de tenant sigue puesto.</summary>
    private async Task<Dictionary<int, string>> NombresDeFamiliasAsync(IReadOnlyList<int> idsDeFamilia, CancellationToken ct)
    {
        if (idsDeFamilia.Count == 0)
        {
            return [];
        }

        var ids = idsDeFamilia.Distinct().ToArray();

        return await db.Familias
            .IgnoreQueryFilters(["BajaLogica"])
            .AsNoTracking()
            .Where(f => ids.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, f => f.Nombre, ct);
    }

    // =================================================================================================
    // Los mensajes de los rechazos: los mismos en la previsualización y en el pedido real
    // =================================================================================================

    private static string MensajeDeArticuloInexistente(int idArticulo) => $"No existe el artículo {idArticulo}.";

    private static string MensajeDeFamiliaInexistente(int idFamilia) => $"No existe la familia {idFamilia}.";

    private static string MensajeDeFamiliaInactiva(string nombre) =>
        $"La familia \"{nombre}\" está inactiva: no se le pueden agregar artículos.";

    private static string MensajeDeArticuloEnOtraFamilia(Articulo articulo, IReadOnlyDictionary<int, string> nombresDeFamilias) =>
        $"El artículo {articulo.CodigoInterno} ya pertenece a la familia " +
        $"\"{(nombresDeFamilias.TryGetValue(articulo.IdFamilia!.Value, out var nombre) ? nombre : $"{articulo.IdFamilia}")}\": " +
        "hay que sacarlo de ella antes de agruparlo.";

    private static string MensajeDePrecioInalineable(Articulo articulo, ListaFija lista, AlineacionDeUnPar par) =>
        $"No se puede alinear el artículo {articulo.CodigoInterno} en la lista \"{lista.Nombre}\": " + par.Resolucion switch
        {
            ResolucionDeAlineacionDePrecios.InalineablePorReferenciaSinPrecios =>
                "el artículo de referencia no tiene ningún precio en esa lista y este sí, y un precio no se puede quitar.",
            ResolucionDeAlineacionDePrecios.InalineablePorReferenciaSoloProgramada =>
                "el artículo de referencia solo tiene un precio programado y este ya tiene un precio vigente, que no se puede quitar.",
            ResolucionDeAlineacionDePrecios.InalineablePorPrecioPredecesorPosterior =>
                "su precio programado reemplaza a uno que empieza después de este momento, y ese no se puede cerrar.",
            _ => throw new InvalidOperationException($"La resolución {par.Resolucion} no es un rechazo.")
        };

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            // GestionDeCatalogo (capa de API) ya exige admin de tenant: un actor de plataforma nunca llega hasta
            // acá. Defensa en profundidad, no un camino alcanzable en operación normal.
            ?? throw new InvalidOperationException(
                "ServicioDeAgrupacionDeFamilias requiere un actor de tenant; GestionDeCatalogo es admin-only.");
}
