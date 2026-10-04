using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Ways.Application.Abstracciones;
using Ways.Application.Ofertas;
using Ways.Application.Organizacion;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;

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
/// Precios: <see cref="ServicioDeOfertas.ResolverConEscalonesAsync"/> corre en LOTE, una sola vez,
/// para TODO el catálogo activo × TODAS las listas que el punto de venta puede usar
/// (<see cref="ArmadorDeInstantanea.ListasAResolver"/>), a <c>cantidad = 1</c> y contra la empresa
/// del punto de venta: el dispositivo cotiza a cualquier cliente con su propia lista sin ir a la
/// red. Sus consultas no dependen de la cantidad de líneas, así que la cantidad de idas a la base
/// de esta instantánea es fija. NUNCA se reimplementa el motor de reglas en el dispositivo
/// (decisión del dueño): el precio queda CONGELADO al momento de la instantánea. Cada precio viaja
/// con su curva de escalones por cantidad, calculada por el motor real acá en el servidor.
///
/// Paginado: NO. <see cref="InstantaneaDePos.Momento"/> es el único invariante real de esta
/// respuesta — cada precio quedó resuelto contra el MISMO instante; paginar volvería la instantánea
/// inconsistente contra sí misma si un precio cambia entre páginas. Para que el refresco periódico
/// sea barato, el endpoint responde <c>304</c> cuando el contenido no cambió
/// (<see cref="EtiquetaDeInstantanea"/>); un cursor por fila no sería correcto (ver ese tipo).
/// </summary>
public class ServicioDeInstantaneaDePos(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, ServicioDeOfertas servicioDeOfertas, IMemoryCache cache)
{
    /// <summary>
    /// Cuánto se reutiliza una instantánea ya armada para el mismo tenant y punto de venta: el
    /// refresco periódico de varios dispositivos (y su <c>304</c>) no rearma todo en cada pedido. Es
    /// también la demora máxima con la que un cambio de precio, oferta o saldo llega a la
    /// instantánea, además del intervalo de sincronización del dispositivo.
    /// </summary>
    public static readonly TimeSpan VigenciaDeLaCache = TimeSpan.FromSeconds(60);

    private sealed record ClaveDeCache(int? IdTenant, int IdPuntoVenta, bool SoloListaDelConsumidorFinal);

    /// <param name="soloListaDelConsumidorFinal">Resuelve solo la lista del Consumidor Final: lo único que
    /// necesita el formato original (<see cref="ArmadorDeInstantanea.ProyectarLegada"/>).</param>
    public async Task<InstantaneaDePos> ObtenerAsync(bool soloListaDelConsumidorFinal = false, CancellationToken ct = default)
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

        // La caché se consulta recién acá, después de validar el dispositivo y el modo del punto
        // de venta en cada pedido, y su clave lleva tenant y punto de venta: nunca devuelve la
        // instantánea de otro.
        var clave = new ClaveDeCache(contexto.IdTenant, puntoVenta.Id, soloListaDelConsumidorFinal);
        if (cache.TryGetValue(clave, out InstantaneaDePos? cacheada) && cacheada is not null)
        {
            return cacheada;
        }

        var armada = await ArmarAsync(puntoVenta, soloListaDelConsumidorFinal, ct);
        cache.Set(clave, armada, VigenciaDeLaCache);
        return armada;
    }

    private async Task<InstantaneaDePos> ArmarAsync(PuntoVenta puntoVenta, bool soloListaDelConsumidorFinal, CancellationToken ct)
    {
        var momento = reloj.Ahora;
        var idEmpresa = puntoVenta.IdEmpresa;

        // Todas las listas visibles (no dadas de baja) del tenant, en una consulta: de acá salen
        // tanto las listas a resolver como la detección de una FK de cliente a una lista dada de
        // baja.
        var listasVisibles = await db.ListasPrecio.AsNoTracking()
            .OrderBy(l => l.Id)
            .Select(l => new ArmadorDeInstantanea.ListaVisible(l.Id, l.IdEmpresa, l.Activo, l.Modo, l.IdListaBase, l.Porcentaje))
            .ToListAsync(ct);
        var idsListaVisibles = listasVisibles.Select(l => l.Id).ToHashSet();

        // Los clientes que el punto de venta puede elegir: activos, compartidos o de su empresa, y
        // el Consumidor Final siempre (spec: "Omitted idCliente defaults to Consumidor Final").
        var clientesCrudos = await db.Clientes.AsNoTracking()
            .Where(c => c.Numero == ReglaDeClientes.NumeroConsumidorFinal
                || (c.Activo && (c.IdEmpresa == null || c.IdEmpresa == idEmpresa)))
            .OrderBy(c => c.Numero)
            .Select(c => new
            {
                c.Id, c.Numero, c.Nombre, c.Apellido, c.RazonSocial, c.TipoDocumento, c.NumeroDocumento,
                c.IdCondicionFiscal, c.IdEmpresa, c.IdListaPrecio, c.Saldo, c.LimiteCredito, c.CreditoIlimitado
            })
            .ToListAsync(ct);

        if (!clientesCrudos.Any(c => c.Numero == ReglaDeClientes.NumeroConsumidorFinal))
        {
            throw new InvalidOperationException("El tenant actual no tiene un Consumidor Final sembrado.");
        }

        var clientes = clientesCrudos
            .Select(c => new ClienteDeInstantanea(
                c.Id, c.Numero, c.Nombre, c.Apellido, c.RazonSocial, c.TipoDocumento, c.NumeroDocumento,
                c.IdCondicionFiscal, c.IdEmpresa, ArmadorDeInstantanea.ListaEfectiva(c.IdListaPrecio, idsListaVisibles),
                ReglaDeClientes.EsConsumidorFinal(c.Numero), c.Saldo, c.LimiteCredito, c.CreditoIlimitado))
            .ToList();

        var idsLista = ArmadorDeInstantanea.ListasAResolver(listasVisibles, idEmpresa, clientes.Select(c => c.IdListaPrecio));
        if (soloListaDelConsumidorFinal)
        {
            idsLista = ArmadorDeInstantanea.SoloListaDelConsumidorFinal(idsLista, clientes);
        }

        // Mismo scope que ServicioDeEscaneo (solo Activo, sin filtro de disponibilidad por
        // empresa): un artículo que el dispositivo puede escanear y vender ONLINE tiene que poder
        // vender OFFLINE también — la disponibilidad por empresa es un filtro de vidriera/grilla,
        // nunca una restricción de venta. Ordenado por id: la etiqueta de contenido depende del
        // orden.
        var articulos = await db.Articulos.AsNoTracking()
            .Where(a => a.Activo)
            .OrderBy(a => a.Id)
            .Select(a => new ArmadorDeInstantanea.ArticuloAResolver(a.Id, a.CodigoInterno, a.Nombre, a.IdAlicuotaIva, a.AcumulaEnVenta, a.UnidadVenta))
            .ToListAsync(ct);

        var idsArticulo = articulos.Select(a => a.Id).ToList();

        var codigosPorArticulo = (await db.CodigosBarra.AsNoTracking()
                .Where(c => c.Activo && idsArticulo.Contains(c.IdArticulo))
                .OrderBy(c => c.IdArticulo).ThenBy(c => c.Codigo)
                .Select(c => new { c.IdArticulo, c.Codigo })
                .ToListAsync(ct))
            .GroupBy(c => c.IdArticulo)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(c => c.Codigo).ToList());

        var idsAlicuota = articulos.Select(a => a.IdAlicuotaIva).Distinct().ToList();
        var porcentajePorAlicuota = await db.AlicuotasIva.AsNoTracking()
            .Where(a => idsAlicuota.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Porcentaje, ct);

        // La ÚNICA autoridad de precio, en lote, para TODO el catálogo activo y TODAS las listas a
        // la vez — mismo llamado que el checkout, nunca una reimplementación. Una sola llamada:
        // sus consultas no crecen con artículos × listas.
        var resolucion = await servicioDeOfertas.ResolverConEscalonesAsync(
            ArmadorDeInstantanea.LineasDeResolucion(articulos, idsLista, idEmpresa), momento, ct);

        var articulosDeInstantanea = ArmadorDeInstantanea.ArmarArticulos(
            articulos, idsLista, resolucion, codigosPorArticulo, porcentajePorAlicuota);

        // Mismo scope que ServicioDeVentas.EmitirAsync (db.MediosPago.Where(idsMedioPago.Contains)):
        // el checkout no filtra medios de pago por empresa al validar un pago, así que la
        // instantánea tampoco angosta las opciones — cualquiera que el checkout aceptaría online
        // se ofrece offline.
        var mediosDePago = await db.MediosPago.AsNoTracking()
            .Where(m => m.Activo)
            .OrderBy(m => m.Orden).ThenBy(m => m.Id)
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

        return new InstantaneaDePos(momento, puntoVenta.Id, articulosDeInstantanea, clientes, mediosDePago, toleranciaPago);
    }
}
