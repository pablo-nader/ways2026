using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Articulos;
using Ways.Application.Ofertas;
using Ways.Domain.Common;

namespace Ways.Application.Etiquetas;

/// <summary>
/// stage-18-etiquetas-y-consulta, Slice 2 (task 2.13; design.md:14-16, "Technical Approach").
/// COMPONE la selección (ids explícitos o el filtro de <see cref="Articulos.ServicioDeArticulos"/>)
/// con el precio ya resuelto por <see cref="ServicioDeOfertas.ResolverAsync"/> — nunca reimplementa
/// ninguna parte del matching de ofertas (design decisión 6/proposal decisión 12). Read-only:
/// ningún <c>SaveChangesAsync</c> en esta clase, mismo contrato que
/// <c>POST /api/ofertas/resolver</c> (spec: "Applied Ofertas Are Reported, Never Persisted").
/// </summary>
public class ServicioDeEtiquetas(
    IWaysDbContext db, IRelojDelSistema reloj, ServicioDeArticulos servicioDeArticulos, ServicioDeOfertas servicioDeOfertas)
{
    public async Task<DatosDeEtiquetas> ComponerAsync(SolicitudDeEtiquetas solicitud, CancellationToken ct = default)
    {
        // Reconciliación T1 (tasks.md) + design decisión 12: la selección es ids XOR filtro,
        // nunca un tercer camino ni una combinación de los dos — mutation targets 20/21.
        var hayIds = solicitud.IdsArticulo is not null;
        var hayFiltro = solicitud.Filtro is not null;

        if (hayIds && hayFiltro)
        {
            throw new ErrorDominio(
                "seleccion_ambigua", "Enviá idsArticulo o filtro, nunca los dos a la vez.", 400);
        }

        if (!hayIds && !hayFiltro)
        {
            throw new ErrorDominio(
                "seleccion_requerida", "Enviá idsArticulo o filtro para componer la hoja.", 400);
        }

        if (hayIds && solicitud.IdsArticulo!.Count > ServicioDeArticulos.TamanioMaximoDePagina)
        {
            // Nunca un truncado silencioso (design decisión 12): 300 ids explícitos con un tope
            // de 200 es el caller ignorando el cap que su propia pantalla ya le mostró — muy
            // distinto de un filtro que matchea de más (ver Truncado más abajo).
            throw new ErrorDominio(
                "seleccion_excedida",
                $"La selección explícita no puede superar los {ServicioDeArticulos.TamanioMaximoDePagina} artículos.",
                400);
        }

        // 1 consulta: idPuntoVenta → idEmpresa. IdEmpresa es LOAD-BEARING para
        // ReglaDeOfertas.CoincideEmpresa (design decisión 5, mutation target 9) — se lee UNA vez
        // acá y viaja explícito en cada LineaDeResolucion más abajo, nunca null.
        var puntoVenta = await db.PuntosVenta
            .Where(pv => pv.Id == solicitud.IdPuntoVenta)
            .Select(pv => new { pv.IdEmpresa })
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {solicitud.IdPuntoVenta}.");

        var idEmpresa = puntoVenta.IdEmpresa;

        // 1 consulta: idListaPrecio → NombreDeLista, leído por el SERVIDOR (decisión 11) — nunca
        // la etiqueta que el selector del cliente ya tenía, para que el nombre impreso y la lista
        // realmente tasada sean siempre la misma lectura (mutation target 23).
        var nombreDeLista = await db.ListasPrecio
            .Where(l => l.Id == solicitud.IdListaPrecio)
            .Select(l => l.Nombre)
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la lista de precios {solicitud.IdListaPrecio}.");

        var (idsArticulo, truncado) = hayIds
            ? (solicitud.IdsArticulo!, false)
            : await ResolverPorFiltroAsync(solicitud.Filtro!, idEmpresa, ct);

        if (idsArticulo.Count == 0)
        {
            return new DatosDeEtiquetas(solicitud.IdListaPrecio, nombreDeLista, reloj.Ahora, [], [], truncado);
        }

        // 1 consulta: identidad (código interno, nombre, unidad de venta) del conjunto elegido —
        // ResolverAsync solo proyecta Id/IdCategoria/IdGrupo (Ofertas.Contratos), nunca los campos
        // que la etiqueta necesita imprimir. judgment-day Slice 2, ronda 2 (juez A, CRITICAL):
        // arbitraje del orquestador — la disponibilidad por empresa viaja en LA MISMA consulta
        // (mismo EXISTS que ArticuloConsultas.DisponibleEnEmpresa) en vez de un roundtrip aparte,
        // así el camino de ids sigue con el mismo presupuesto de comandos.
        var identidad = await db.Articulos
            .Where(a => idsArticulo.Contains(a.Id))
            .Select(a => new
            {
                a.Id, a.CodigoInterno, a.Nombre, a.UnidadVenta,
                Disponible = a.DisponibleParaTodas ||
                    db.ArticulosEmpresas.Any(ae => ae.IdArticulo == a.Id && ae.IdEmpresa == idEmpresa)
            })
            .ToDictionaryAsync(a => a.Id, ct);

        // Arbitraje del orquestador, contrato (a): un id explícito que NO resuelve identidad en el
        // tenant (inexistente o cross-tenant, ya invisible por el filtro global de EF) es un 400 —
        // mismo código de dominio que el guard de ServicioDeOfertas.ResolverAsync:356-360, nunca un
        // drop silencioso. El camino por filtro nunca entra acá: sus ids salen de una consulta al
        // mismo db.Articulos, así que siempre resuelven identidad.
        if (hayIds)
        {
            var idsArticuloFaltantes = idsArticulo.Except(identidad.Keys).ToList();
            if (idsArticuloFaltantes.Count > 0)
            {
                throw new ErrorDominio(
                    "referencia_invalida", $"No existe el artículo {idsArticuloFaltantes[0]}.", 400);
            }
        }

        // 1 consulta: codigos_barra del conjunto — hasta uno por artículo (el primero por Id,
        // mismo criterio de orden que ServicioDeArticulos.ListarCodigosBarraAsync).
        var codigoBarraPorArticulo = (await db.CodigosBarra
                .Where(c => idsArticulo.Contains(c.IdArticulo))
                .Select(c => new { c.IdArticulo, c.Id, c.Codigo })
                .ToListAsync(ct))
            .GroupBy(c => c.IdArticulo)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Id).First().Codigo);

        // Decisión 10 (design.md:61): UN solo momento para toda la hoja, resuelto acá y ECHADO
        // en la respuesta — nunca uno por línea (mutation target 25). El request no lleva
        // Momento (Reconciliación/decisión 10): un precio de estante para un instante
        // hipotético es un precio falso en la góndola.
        var momento = reloj.Ahora;

        // Arbitraje del orquestador, contrato (b): un id que RESUELVE pero cuyo artículo no está
        // disponible en la empresa del PV va a Excluidos con su identidad y motivo propio (mismo
        // patrón que la decisión 6 de abajo — la identidad se conoce, la exclusión es honesta) —
        // nunca a Filas, nunca resuelve precio para él. El camino por filtro no cae nunca acá:
        // ResolverPorFiltroAsync ya scopea por DisponibleEnEmpresa antes de elegir los ids.
        var excluidos = new List<ArticuloExcluido>();
        var idsDisponibles = new List<int>(idsArticulo.Count);

        foreach (var id in idsArticulo)
        {
            var datosDeIdentidad = identidad[id];
            if (!datosDeIdentidad.Disponible)
            {
                excluidos.Add(new ArticuloExcluido(
                    datosDeIdentidad.Id, datosDeIdentidad.CodigoInterno, datosDeIdentidad.Nombre,
                    "No disponible en la empresa del punto de venta."));
                continue;
            }

            idsDisponibles.Add(id);
        }

        // Decisión 5 (design.md:56, mutation targets 9/10): cantidad=1 SIEMPRE, IdEmpresa del PV
        // en TODAS las líneas — nunca null, nunca la cantidad de copias.
        var lineas = idsDisponibles
            .Select(id => new LineaDeResolucion(id, idEmpresa, solicitud.IdListaPrecio, Cantidad: 1m))
            .ToList();

        var resultados = await servicioDeOfertas.ResolverAsync(lineas, momento, ct);

        var filas = new List<FilaDeEtiqueta>();

        foreach (var resultado in resultados)
        {
            var datos = identidad[resultado.IdArticulo];

            // Decisión 6 (design.md decisión 6 restada, mutation targets 11/12): sin precio
            // vigente ⇒ Excluidos, CON identidad, nunca una fila con $0 (Reconciliación T3).
            if (resultado.PrecioOriginal is not { } precioOriginal || resultado.PrecioFinal is not { } precioFinal)
            {
                excluidos.Add(new ArticuloExcluido(
                    datos.Id, datos.CodigoInterno, datos.Nombre, "Sin precio vigente en la lista seleccionada."));
                continue;
            }

            filas.Add(new FilaDeEtiqueta(
                datos.Id, datos.CodigoInterno,
                codigoBarraPorArticulo.TryGetValue(datos.Id, out var codigoBarra) ? codigoBarra : null,
                datos.Nombre, datos.UnidadVenta.ToString(), precioOriginal, precioFinal, resultado.Aplicadas));
        }

        // Decisión 6 (design.md:57, mutation targets 13/14): soloConOfertaVigente delega POR
        // COMPLETO en Aplicadas.Count > 0 — nunca un segundo matching (p.ej. comparar precios).
        // Post-filtro sobre las FILAS ya resueltas, nunca sobre el candidato grueso.
        if (solicitud.Filtro is { SoloConOfertaVigente: true })
        {
            filas = filas.Where(f => f.Ofertas.Count > 0).ToList();
        }

        return new DatosDeEtiquetas(solicitud.IdListaPrecio, nombreDeLista, momento, filas, excluidos, truncado);
    }

    /// <summary>Decisión 7 (design.md:58, mutation target 19): reusa
    /// <see cref="Articulos.ServicioDeArticulos.ListarAsync"/> — un único query builder, una
    /// única expansión de descendientes, un único clamp (decisión 9). <c>Truncado</c> sale de la
    /// <c>PaginaDe&lt;T&gt;.Total</c> que <c>ListarAsync</c> YA calcula, nunca un segundo
    /// <c>COUNT</c>/<c>Take(cap+1)</c>.</summary>
    private async Task<(IReadOnlyList<int> Ids, bool Truncado)> ResolverPorFiltroAsync(
        FiltroDeEtiquetas filtro, int idEmpresa, CancellationToken ct)
    {
        var pagina = await servicioDeArticulos.ListarAsync(
            busqueda: filtro.Busqueda,
            idEmpresa: idEmpresa,
            incluirEliminados: false,
            pagina: 1,
            tamanio: ServicioDeArticulos.TamanioMaximoDePagina,
            idArea: filtro.IdArea,
            idCategoria: filtro.IdCategoria,
            idMarca: filtro.IdMarca,
            ct: ct);

        var truncado = pagina.Total > ServicioDeArticulos.TamanioMaximoDePagina;

        return (pagina.Items.Select(a => a.Id).ToList(), truncado);
    }
}
