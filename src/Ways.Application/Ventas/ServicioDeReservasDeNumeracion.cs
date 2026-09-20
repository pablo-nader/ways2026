using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;

namespace Ways.Application.Ventas;

/// <summary>
/// Reserva de bloques de numeración para el POS de escritorio offline
/// (stage-pos-reserva-de-numeracion, DB CHANGE GATE aprobado): un dispositivo sin conexión no
/// puede tocar <c>numeraciones_comprobante</c> por request, así que pide bloques por adelantado y
/// los reparte localmente. Único consumidor de <see cref="AsignadorDeNumeroComprobante.ReservarBloqueAsync"/>.
/// </summary>
public class ServicioDeReservasDeNumeracion(
    IWaysDbContext db, IContextoDeUsuario contexto, IRelojDelSistema reloj)
{
    /// <summary>Tope de <c>cantidad</c> por pedido — un bloque más grande no protege nada nuevo
    /// (los números no consumidos se abandonan igual ante un re-pedido, spec punto 2) y solo
    /// infla el hueco de un dispositivo que nunca vuelve a sincronizar. 500 alcanza y sobra para
    /// una jornada de venta minorista.</summary>
    public const int CantidadMaxima = 500;

    public async Task<BloqueDeNumeracionReservado> ReservarAsync(
        SolicitudDeReservaDeNumeracion solicitud, CancellationToken ct = default)
    {
        // La policy del endpoint (RequiereDispositivo) ya exige la claim — este chequeo es
        // defensa en profundidad, mismo criterio que ExigirTenantDeLaSesion de abajo, para que el
        // servicio nunca dependa en silencio de que la capa de API lo haya validado.
        var idDispositivo = contexto.IdDispositivo
            ?? throw new ErrorDominio(
                "prohibido", "Esta operación requiere un dispositivo autenticado.", 403);
        var idTenant = ExigirTenantDeLaSesion();

        if (solicitud.Cantidad < 1 || solicitud.Cantidad > CantidadMaxima)
        {
            throw new ErrorDominio(
                "cantidad_invalida",
                $"La cantidad tiene que ser mayor a 0 y no puede superar {CantidadMaxima}.",
                400);
        }

        var puntoVenta = await db.PuntosVenta.FirstOrDefaultAsync(p => p.Id == solicitud.IdPuntoVenta, ct)
            // El filtro de EF (+ RLS) ya deja invisible un punto de venta de otro tenant — mismo
            // criterio (ADR-8) que ServicioDeVentas.ResolverPuntoVentaAsync.
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {solicitud.IdPuntoVenta}.");

        // Regla ÚNICA compartida con el checkout — nunca reimplementada acá (ver el doc-comment
        // de PoliticaDeModoDePuntoVenta): un dispositivo solo puede reservar contra SU PROPIO
        // punto de venta Escritorio.
        await PoliticaDeModoDePuntoVenta.ExigirCompatibleConElActorAsync(db, contexto, puntoVenta, ct);

        var tipo = await ResolverTipoAsync(solicitud.CodigoTipoComprobante, ct);
        var momento = reloj.Ahora;

        // Misma "same execution strategy" que AsignadorDeNumeroComprobante.AsignarComprometidoAsync:
        // ReservarBloqueAsync abandona cualquier bloque vivo de este dispositivo ANTES de insertar
        // el nuevo, en su propia transacción — un reintento sobre un commit ambiguo abandona el
        // bloque recién comiteado y reparte otro, nunca deja dos vivos ni devuelve un rango stale
        // (ver el doc-comment de ese método).
        var estrategia = db.Database.CreateExecutionStrategy();
        var (desde, hasta) = await estrategia.ExecuteAsync(async () =>
            await AsignadorDeNumeroComprobante.ReservarBloqueAsync(
                db, idTenant, puntoVenta.Id, tipo.Codigo, idDispositivo, solicitud.Cantidad, momento, ct));

        return new BloqueDeNumeracionReservado(desde, hasta, puntoVenta.Id, tipo.Codigo);
    }

    /// <summary>Mismo predicado EXACTO que <c>ServicioDeVentas.ResolverTipoComprobanteAsync</c> —
    /// duplicado a propósito (no extraído) para no acoplar esta reserva al checkout; tiene que
    /// mantenerse en sync a mano si ese predicado cambia. Reservar un bloque para un tipo que la
    /// venta rechazaría dejaría el bloque entero muerto en la práctica.</summary>
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
                "ServicioDeReservasDeNumeracion requiere un actor de tenant; RequiereDispositivo no admite plataforma.");
}
