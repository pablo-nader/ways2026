using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Ofertas;
using Ways.Application.Organizacion;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Common;

namespace Ways.Application.Pos;

/// <summary>
/// Instantánea de venta offline (stage-pos-venta-offline-backend, Parte A) — <c>GET
/// /api/pos/instantanea</c>. Deriva su contenido EXACTAMENTE de lo que el checkout online lee hoy
/// (<c>Pos.tsx</c>, <c>ServicioDeEscaneo</c>, <c>ServicioDeOfertas.ResolverAsync</c>,
/// <c>ServicioDeVentas.EmitirAsync</c>, <c>ServicioDePrecios</c>) — nunca un campo "por si acaso".
///
/// Device-only, su propio punto de venta únicamente: reusa <c>PoliticaDeModoDePuntoVenta</c> TAL
/// CUAL (nunca reimplementada) — mismo criterio exacto que
/// <c>ServicioDeReservasDeNumeracion</c>/<c>ServicioDeVentas</c>. Sin parámetro <c>idPuntoVenta</c>
/// en la firma a propósito: el llamador nunca elige contra qué punto de venta pedir la
/// instantánea, siempre es el suyo — derivado de <see cref="IContextoDeUsuario.IdDispositivo"/>,
/// nunca del request.
///
/// Precios: <see cref="ServicioDeOfertas.ResolverAsync"/> corre en LOTE, una sola vez para TODO el
/// catálogo activo, a <c>cantidad = 1</c>, contra la lista de precio del Consumidor Final del
/// tenant (<see cref="ReglaDeClientes.NumeroConsumidorFinal"/>) y la empresa del punto de venta —
/// el mismo par (lista, empresa) que un walk-in sin cliente seleccionado resolvería online. NUNCA
/// se reimplementa el motor de reglas en el dispositivo (decisión del dueño, rechazada
/// explícitamente): el precio queda CONGELADO al momento de la instantánea, el dispositivo nunca
/// vuelve a evaluarlo. Limitación aceptada: una oferta con <c>cantidadMinima > 1</c> no se refleja
/// (la instantánea resuelve a cantidad unitaria) hasta que el dispositivo recupere señal.
///
/// Paginado: NO. <see cref="InstantaneaDePos.Momento"/> es el único invariante real de esta
/// respuesta — cada artículo quedó resuelto contra el MISMO instante. Paginar (aun con el mismo
/// tope de <c>ServicioDeArticulos.TamanioMaximoDePagina</c> de la grilla interactiva) exigiría N
/// requests secuenciales, y un precio que cambia entre la página 3 y la página 4 volvería la
/// instantánea INCONSISTENTE contra sí misma — exactamente lo que este endpoint existe para
/// evitar. Con ~6000 artículos (la referencia del legacy) y los 10 campos mínimos de
/// <see cref="ArticuloDeInstantanea"/>, el JSON completo pesa un puñado de cientos de KB (varios
/// menos comprimido) — un solo response, aceptable para un pull en background, ocasional, sobre
/// Wi-Fi/LAN de local. Tampoco hay refresco incremental (If-Modified-Since/ETag): no existe
/// change-tracking entre <c>articulos</c>/<c>precios</c>/<c>ofertas</c>/<c>codigos_barra</c> como
/// para construir un delta correcto sin inventar infraestructura nueva, y el propio diseño de esta
/// etapa encuadra la vejez como "acotada por el intervalo de refresco" (el dispositivo vuelve a
/// pedir la instantánea COMPLETA cada vez que tiene señal), no por el tamaño de un delta — agregar
/// eso ahora sería optimización especulativa sin un problema de tamaño medido que la justifique.
/// </summary>
public class ServicioDeInstantaneaDePos(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, ServicioDeOfertas servicioDeOfertas)
{
    public async Task<InstantaneaDePos> ObtenerAsync(CancellationToken ct = default)
    {
        // La policy del endpoint (RequiereDispositivo) ya exige la claim — defensa en
        // profundidad, mismo criterio que ServicioDeReservasDeNumeracion.ReservarAsync.
        var idDispositivo = contexto.IdDispositivo
            ?? throw new ErrorDominio("prohibido", "Esta operación requiere un dispositivo autenticado.", 403);

        var idPuntoVentaDelDispositivo = await db.Dispositivos
            .Where(d => d.Id == idDispositivo)
            .Select(d => (int?)d.IdPuntoVenta)
            .FirstOrDefaultAsync(ct);

        if (idPuntoVentaDelDispositivo is null)
        {
            // Fila invisible al filtro global de EF: dispositivo revocado (baja lógica) o, en
            // teoría, un id que nunca existió. Mismo 403 que la falta de claim, nunca 404 —
            // distinguir "no existe" de "revocado" solo le daría información a un bearer robado.
            throw new ErrorDominio("prohibido", "Este dispositivo ya no está vigente.", 403);
        }

        var puntoVenta = await db.PuntosVenta.FirstOrDefaultAsync(pv => pv.Id == idPuntoVentaDelDispositivo, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVentaDelDispositivo}.");

        // Regla ÚNICA compartida con el checkout — nunca reimplementada acá (ver el doc-comment
        // de PoliticaDeModoDePuntoVenta): confina el dispositivo a SU PROPIO punto de venta
        // Escritorio, incluso si el modo cambió administrativamente después de vincularlo.
        await PoliticaDeModoDePuntoVenta.ExigirCompatibleConElActorAsync(db, contexto, puntoVenta, ct);

        var momento = reloj.Ahora;

        // Spec: "Omitted idCliente defaults to Consumidor Final" (ServicioDeVentas.
        // ResolverClienteAsync) — la instantánea resuelve precio contra el MISMO walk-in que un
        // checkout online sin cliente seleccionado.
        var cliente = await db.Clientes.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Numero == ReglaDeClientes.NumeroConsumidorFinal, ct)
            ?? throw new InvalidOperationException("El tenant actual no tiene un Consumidor Final sembrado.");

        // Mismo scope que ServicioDeEscaneo (solo Activo, sin filtro de disponibilidad por
        // empresa): un artículo que el dispositivo puede escanear y vender ONLINE tiene que poder
        // vender OFFLINE también — la disponibilidad por empresa es un filtro de vidriera/grilla,
        // nunca una restricción de venta.
        var articulos = await db.Articulos.AsNoTracking()
            .Where(a => a.Activo)
            .Select(a => new { a.Id, a.CodigoInterno, a.Nombre, a.IdAlicuotaIva })
            .ToListAsync(ct);

        var idsArticulo = articulos.Select(a => a.Id).ToList();

        var codigosPorArticulo = (await db.CodigosBarra.AsNoTracking()
                .Where(c => c.Activo && idsArticulo.Contains(c.IdArticulo))
                .Select(c => new { c.IdArticulo, c.Codigo })
                .ToListAsync(ct))
            .GroupBy(c => c.IdArticulo)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(c => c.Codigo).ToList());

        var idsAlicuota = articulos.Select(a => a.IdAlicuotaIva).Distinct().ToList();
        var porcentajePorAlicuota = await db.AlicuotasIva.AsNoTracking()
            .Where(a => idsAlicuota.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Porcentaje, ct);

        // La ÚNICA autoridad de precio, en lote, para TODO el catálogo activo a la vez — mismo
        // llamado que el checkout, nunca una reimplementación (ver el doc-comment de la clase).
        var lineasDeResolucion = articulos
            .Select(a => new LineaDeResolucion(a.Id, puntoVenta.IdEmpresa, cliente.IdListaPrecio, 1m))
            .ToList();
        var resolucion = await servicioDeOfertas.ResolverAsync(lineasDeResolucion, momento, ct);

        var articulosDeInstantanea = new List<ArticuloDeInstantanea>(articulos.Count);
        for (var i = 0; i < articulos.Count; i++)
        {
            var resultado = resolucion[i];

            // Un artículo sin precio vigente HOY ya rechazaría 400 articulo_sin_precio_vigente en
            // el camino online (ServicioDeVentas.MaterializarItems) — ofrecerlo offline sin poder
            // cobrarlo no tiene sentido: se omite de la instantánea en vez de viajar con un precio
            // inventado.
            if (resultado.PrecioOriginal is null)
            {
                continue;
            }

            var articulo = articulos[i];
            articulosDeInstantanea.Add(new ArticuloDeInstantanea(
                articulo.Id,
                articulo.CodigoInterno,
                articulo.Nombre,
                codigosPorArticulo.GetValueOrDefault(articulo.Id, (IReadOnlyList<string>)[]),
                resultado.PrecioOriginal.Value,
                resultado.PrecioFinal ?? resultado.PrecioOriginal.Value,
                resultado.DescuentoUnitario,
                resultado.Aplicadas,
                articulo.IdAlicuotaIva,
                porcentajePorAlicuota[articulo.IdAlicuotaIva]));
        }

        // Mismo scope que ServicioDeVentas.EmitirAsync (db.MediosPago.Where(idsMedioPago.Contains)):
        // el checkout no filtra medios de pago por empresa al validar un pago, así que la
        // instantánea tampoco angosta las opciones — cualquiera que el checkout aceptaría online
        // se ofrece offline.
        var mediosDePago = await db.MediosPago.AsNoTracking()
            .Where(m => m.Activo)
            .OrderBy(m => m.Orden)
            .Select(m => new MedioPagoDeInstantanea(m.Id, m.Nombre, m.Comportamiento, m.AdmiteVuelto, m.RequiereReferencia))
            .ToListAsync(ct);

        // Mismo criterio de resolución (ResolucionDeParametros.Resolver: punto de venta > empresa
        // > default) que ServicioDeVentas.ResolverParametrosDeVentaAsync — una sola clave, sin el
        // riesgo de mezcla multi-clave que ese método documenta (acá no hace falta el Where extra
        // por clave: el candidate set ya es de una sola).
        var candidatosTolerancia = await db.Parametros.AsNoTracking()
            .Where(p => p.Clave == ParametroConocido.ToleranciaPago.Clave && p.IdEmpresa == puntoVenta.IdEmpresa
                && (p.IdPuntoVenta == null || p.IdPuntoVenta == puntoVenta.Id))
            .ToListAsync(ct);
        var toleranciaPago = JsonSerializer.Deserialize<decimal>(
            ResolucionDeParametros.Resolver(ParametroConocido.ToleranciaPago.Clave, candidatosTolerancia, puntoVenta.Id));

        return new InstantaneaDePos(momento, puntoVenta.Id, articulosDeInstantanea, mediosDePago, toleranciaPago);
    }
}
