using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Ventas;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;

namespace Ways.Application.Pos;

/// <summary>
/// Rendición de la cola local del dispositivo (<c>POST /api/pos/rendicion-de-cola</c>): el
/// dispositivo declara hasta qué número repartió y cuántas ventas todavía no llegaron, y el cierre
/// de turno usa ese reporte —contrastándolo contra <c>comprobantes_venta</c>, que puede
/// contradecirlo pero no confirmarlo; ver
/// <see cref="Ways.Domain.Ventas.MotivoDeRendicionPendiente.HuecoDeComprobantes"/>— para no cerrar
/// sobre una cola sin drenar.
///
/// Device-only, su propio punto de venta únicamente: mismo criterio EXACTO que
/// <see cref="ServicioDeInstantaneaDePos"/> — el punto de venta se deriva de
/// <see cref="IContextoDeUsuario.IdDispositivo"/>, nunca del request, y
/// <c>PoliticaDeModoDePuntoVenta</c> se reusa TAL CUAL (nunca reimplementada).
///
/// Único consumidor de <see cref="AsignadorDeNumeroComprobante.RegistrarRendicionAsync"/>.
/// </summary>
public class ServicioDeRendicionDeCola(IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto)
{
    public async Task RegistrarAsync(SolicitudDeRendicionDeCola solicitud, CancellationToken ct = default)
    {
        // La policy del endpoint (RequiereDispositivo) ya exige la claim — defensa en profundidad,
        // mismo criterio que ServicioDeInstantaneaDePos/ServicioDeReservasDeNumeracion.
        var idDispositivo = contexto.IdDispositivo
            ?? throw new ErrorDominio("prohibido", "Esta operación requiere un dispositivo autenticado.", 403);
        var idTenant = ExigirTenantDeLaSesion();

        var idPuntoVentaDelDispositivo = await db.Dispositivos
            .Where(d => d.Id == idDispositivo)
            .Select(d => (int?)d.IdPuntoVenta)
            .FirstOrDefaultAsync(ct);

        if (idPuntoVentaDelDispositivo is null)
        {
            // Fila invisible al filtro global de EF: dispositivo revocado (baja lógica) o, en
            // teoría, un id que nunca existió. Mismo 403 que la falta de claim, nunca 404 —
            // distinguir "no existe" de "revocado" solo le daría información a un bearer robado
            // (mismo texto y mismo criterio que ServicioDeInstantaneaDePos.ObtenerAsync).
            throw new ErrorDominio("prohibido", "Este dispositivo ya no está vigente.", 403);
        }

        // Pre-validación EXIGIDA por db-error-backstops, no cortesía: pendientes viene del cliente
        // y sin esto un valor negativo llega a ck_reservas_numeracion_pendientes_no_negativo y
        // sale como 500 (la traducción del 23514 existe igual, como backstop de escritura cruda).
        if (solicitud.Pendientes < 0)
        {
            throw new ErrorDominio(
                "pendientes_invalido", "La cantidad de ventas pendientes no puede ser negativa.", 400);
        }

        var puntoVenta = await db.PuntosVenta.FirstOrDefaultAsync(pv => pv.Id == idPuntoVentaDelDispositivo, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVentaDelDispositivo}.");

        // Regla ÚNICA compartida con el checkout, reusada y nunca reimplementada acá (ver el
        // doc-comment de PoliticaDeModoDePuntoVenta): confina el dispositivo a SU PROPIO punto de
        // venta Escritorio.
        await PoliticaDeModoDePuntoVenta.ExigirCompatibleConElActorAsync(db, contexto, puntoVenta, ct);

        var tipo = await ResolverTipoAsync(solicitud.CodigoTipoComprobante, ct);

        var bloque = await LeerBloqueVivoAsync(idTenant, puntoVenta.Id, tipo.Codigo, idDispositivo, ct);

        if (bloque is null)
        {
            // Sin bloque vivo no hay nada que rendir: un dispositivo que no puede vender offline
            // (ExigirNumeroPreasignadoPropioAsync exige el bloque) no tiene cola que declarar.
            throw new ErrorDominio(
                "rendicion_sin_bloque_vivo",
                "Este dispositivo no tiene un bloque de numeración vigente para esa serie.",
                409);
        }

        // Pre-validación EXIGIDA por db-error-backstops, mismo motivo que pendientes: entregadoHasta
        // viene del cliente y ck_reservas_numeracion_entregado_en_rango lo rechazaría como 500.
        if (solicitud.EntregadoHasta < bloque.Desde - 1 || solicitud.EntregadoHasta > bloque.Hasta)
        {
            throw new ErrorDominio(
                "entregado_hasta_invalido",
                $"entregadoHasta tiene que estar entre {bloque.Desde - 1} y {bloque.Hasta} para el bloque vigente.",
                400);
        }

        // Piso monótono, sobre la fila que ya se leyó arriba: entregado_hasta es una marca de agua y
        // nunca puede bajar (un POST en vuelo que llega tarde, un puntero local reiniciado). Quien
        // de verdad lo IMPONE es el conjunto homónimo del UPDATE —ver
        // AsignadorDeNumeroComprobante.RegistrarRendicionAsync—, que además cubre la carrera que esta
        // lectura no puede ver; acá el chequeo existe para que el camino normal falle sin pagar el
        // statement, misma forma que el pre-chequeo de bloque vivo de arriba.
        var piso = bloque.EntregadoHasta ?? bloque.Desde - 1;

        if (solicitud.EntregadoHasta < piso)
        {
            throw new ErrorDominio("rendicion_regresiva", MensajeDeRegresion(piso), 409);
        }

        var momento = reloj.Ahora;

        // Estrategia REINTENTABLE (nunca FabricaDeEstrategiaSinReintento): el UPDATE escribe los
        // valores que el dispositivo declaró, no los incrementa, así que reintentarlo sobre un
        // commit ambiguo deja exactamente la misma fila — es idempotente, no hay nada que duplicar.
        // El piso monótono no rompe eso: compara con >=, así que el reintento del MISMO valor sobre
        // un intento que sí comiteó vuelve a afectar la fila en vez de parecer un rechazo.
        var estrategia = db.Database.CreateExecutionStrategy();
        var filas = await estrategia.ExecuteAsync(async () =>
            await AsignadorDeNumeroComprobante.RegistrarRendicionAsync(
                db, idTenant, puntoVenta.Id, tipo.Codigo, idDispositivo,
                solicitud.EntregadoHasta, solicitud.Pendientes, momento, ct));

        if (filas == 0)
        {
            // Los dos conjuntos que pueden haber rechazado el UPDATE describen situaciones
            // distintas, así que la relectura decide cuál informar: sin bloque vivo se abandonó
            // entre la lectura de arriba y el UPDATE; con bloque vivo, otra rendición ganó la
            // carrera y dejó la marca de agua más alta que lo que este reporte declara.
            var actual = await LeerBloqueVivoAsync(idTenant, puntoVenta.Id, tipo.Codigo, idDispositivo, ct);

            throw actual is null
                ? new ErrorDominio(
                    "rendicion_sin_bloque_vivo",
                    "Este dispositivo no tiene un bloque de numeración vigente para esa serie.",
                    409)
                : new ErrorDominio(
                    "rendicion_regresiva", MensajeDeRegresion(actual.EntregadoHasta ?? actual.Desde - 1), 409);
        }
    }

    private static string MensajeDeRegresion(long piso) =>
        $"Este bloque ya tiene registrado hasta el número {piso}: una rendición no puede declarar menos.";

    /// <summary>El bloque VIVO del dispositivo para esa serie, o <c>null</c> si no hay ninguno.
    /// Mismo predicado exacto que el <c>WHERE</c> de
    /// <see cref="AsignadorDeNumeroComprobante.RegistrarRendicionAsync"/> (menos el piso monótono),
    /// y por eso vive en un solo lugar: lo usan el pre-chequeo y la relectura que separa las dos
    /// causas de un UPDATE sin filas.</summary>
    private async Task<BloqueVivo?> LeerBloqueVivoAsync(
        int idTenant, int idPuntoVenta, string tipoComprobante, int idDispositivo, CancellationToken ct) =>
        await db.ReservasNumeracion
            .Where(r => r.IdTenant == idTenant
                && r.IdPuntoVenta == idPuntoVenta
                && r.TipoComprobante == tipoComprobante
                && r.IdDispositivo == idDispositivo
                && r.AbandonadaAt == null)
            .Select(r => new BloqueVivo(r.Desde, r.Hasta, r.EntregadoHasta))
            .FirstOrDefaultAsync(ct);

    private sealed record BloqueVivo(long Desde, long Hasta, long? EntregadoHasta);

    /// <summary>Mismo predicado EXACTO que <c>ServicioDeReservasDeNumeracion.ResolverTipoAsync</c>
    /// (que a su vez lo duplica de <c>ServicioDeVentas.ResolverTipoComprobanteAsync</c>) —
    /// duplicado a propósito, misma convención del repo: rendir sobre un tipo que la reserva no
    /// habría entregado no tiene sentido, así que los dos tienen que aceptar exactamente el mismo
    /// conjunto y mantenerse en sync a mano si ese predicado cambia.</summary>
    private async Task<TipoComprobante> ResolverTipoAsync(string codigo, CancellationToken ct)
    {
        var tipo = await db.TiposComprobante.FirstOrDefaultAsync(t => t.Codigo == codigo, ct);

        if (tipo is null || !tipo.Activo || tipo.Clase != ClaseComprobante.Venta || tipo.EsFiscal || !tipo.AfectaStock)
        {
            throw new ErrorDominio(
                "tipo_comprobante_invalido", $"'{codigo}' no es un tipo de comprobante válido para el POS.", 400);
        }

        return tipo;
    }

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            ?? throw new InvalidOperationException(
                "ServicioDeRendicionDeCola requiere un actor de tenant; RequiereDispositivo no admite plataforma.");
}
