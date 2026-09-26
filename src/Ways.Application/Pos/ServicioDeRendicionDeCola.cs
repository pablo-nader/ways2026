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
/// de turno usa ese reporte —verificándolo contra <c>comprobantes_venta</c>, ver
/// <see cref="Ways.Domain.Ventas.ReglaDeRendicionDeCola"/>— para no cerrar sobre una cola sin
/// drenar.
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

        // Regla ÚNICA compartida con el checkout — nunca reimplementada acá (ver el doc-comment de
        // PoliticaDeModoDePuntoVenta): confina el dispositivo a SU PROPIO punto de venta
        // Escritorio, incluso si el modo cambió administrativamente después de vincularlo.
        await PoliticaDeModoDePuntoVenta.ExigirCompatibleConElActorAsync(db, contexto, puntoVenta, ct);

        var tipo = await ResolverTipoAsync(solicitud.CodigoTipoComprobante, ct);

        var bloque = await db.ReservasNumeracion
            .Where(r => r.IdTenant == idTenant
                && r.IdPuntoVenta == puntoVenta.Id
                && r.TipoComprobante == tipo.Codigo
                && r.IdDispositivo == idDispositivo
                && r.AbandonadaAt == null)
            .Select(r => new { r.Desde, r.Hasta })
            .FirstOrDefaultAsync(ct);

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

        var momento = reloj.Ahora;

        // Estrategia REINTENTABLE (nunca FabricaDeEstrategiaSinReintento): el UPDATE escribe los
        // valores que el dispositivo declaró, no los incrementa, así que reintentarlo sobre un
        // commit ambiguo deja exactamente la misma fila — es idempotente, no hay nada que duplicar.
        var estrategia = db.Database.CreateExecutionStrategy();
        var filas = await estrategia.ExecuteAsync(async () =>
            await AsignadorDeNumeroComprobante.RegistrarRendicionAsync(
                db, idTenant, puntoVenta.Id, tipo.Codigo, idDispositivo,
                solicitud.EntregadoHasta, solicitud.Pendientes, momento, ct));

        if (filas == 0)
        {
            // El bloque se abandonó entre la lectura de arriba y este UPDATE (el dispositivo pidió
            // uno nuevo en paralelo): mismo 409 que la ausencia de bloque, porque es lo mismo.
            throw new ErrorDominio(
                "rendicion_sin_bloque_vivo",
                "Este dispositivo no tiene un bloque de numeración vigente para esa serie.",
                409);
        }
    }

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
