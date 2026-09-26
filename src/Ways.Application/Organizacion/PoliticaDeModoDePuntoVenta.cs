using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;

namespace Ways.Application.Organizacion;

/// <summary>
/// Compatibilidad de modo entre el actor (web o dispositivo) y el punto de venta contra el que
/// intenta emitir (stage-desktop-pos, DB CHANGE GATE aprobado): un actor con claim de dispositivo
/// (<see cref="IContextoDeUsuario.IdDispositivo"/>) solo puede emitir contra el punto de venta
/// Escritorio que ESE dispositivo tiene vinculado — nunca contra otro, aunque sea del mismo
/// tenant—; un actor sin esa claim (sesión web normal) solo puede emitir contra un punto de venta
/// Web. Cualquier otra combinación es <c>409 punto_venta_modo_incompatible</c>, nunca <c>404</c>:
/// el punto de venta existe y es del tenant correcto, lo que falla es la compatibilidad de modo.
///
/// judgment-day ronda 1 (hallazgo BLOCKER 2): ÚNICA implementación — antes vivía duplicada (y a
/// punto de desalinearse) en <c>ServicioDeVentas.ExigirModoCompatibleConElActorAsync</c>. Todo
/// emisor de <c>comprobantes_venta</c> cuya numeración salga de <c>numeraciones_comprobante</c>
/// —el espacio de numeración del POS que un bloque reservado offline va a repartir— tiene que
/// compartir la MISMA regla. Un emisor que numera desde OTRO espacio (p. ej.
/// <c>ServicioDeFacturacionFiscal</c>, <c>numeraciones_fiscales</c>) queda exento a propósito y lo
/// documenta en su propio sitio de llamada, nunca acá.
///
/// <see cref="ExigirPuntoVentaPropioDelDispositivoAsync"/> (revisión adversarial post-stage-17):
/// SEGUNDA regla, mitad dispositivo únicamente, para remitos/presupuestos/órdenes de compra — ver
/// su propio doc-comment para el porqué. Sigue siendo ÚNICA implementación por regla: ambos
/// métodos comparten el mismo lookup dispositivo→punto de venta (<see
/// cref="ResolverIdPuntoVentaDelDispositivoAsync"/>), nunca duplicado.
/// </summary>
public static class PoliticaDeModoDePuntoVenta
{
    public static async Task ExigirCompatibleConElActorAsync(
        IWaysDbContext db, IContextoDeUsuario contexto, PuntoVenta puntoVenta, CancellationToken ct)
    {
        if (contexto.IdDispositivo is { } idDispositivo)
        {
            var idPuntoVentaDelDispositivo = await ResolverIdPuntoVentaDelDispositivoAsync(db, idDispositivo, ct);

            if (puntoVenta.Modo != ModoPuntoVenta.Escritorio || idPuntoVentaDelDispositivo != puntoVenta.Id)
            {
                throw new ErrorDominio(
                    "punto_venta_modo_incompatible",
                    "Este dispositivo no puede vender contra ese punto de venta.",
                    409);
            }

            return;
        }

        if (puntoVenta.Modo != ModoPuntoVenta.Web)
        {
            throw new ErrorDominio(
                "punto_venta_modo_incompatible",
                "Este punto de venta requiere un dispositivo de escritorio vinculado.",
                409);
        }
    }

    /// <summary>
    /// Mitad "dispositivo" de la regla de arriba, aislada para remitos/presupuestos/órdenes de
    /// compra: un actor con claim de dispositivo solo puede crear, editar o emitir/enviar esos tres
    /// documentos contra el punto de venta que ESE dispositivo tiene vinculado — nunca contra otro
    /// punto de venta del mismo tenant. Un actor SIN esa claim (sesión web) es <b>completamente
    /// intocado por este método</b>: retorna de inmediato, sin query ni error, y sigue eligiendo
    /// cualquier punto de venta de su tenant, en cualquier <c>modo</c> — ver el porqué más abajo.
    ///
    /// <para><b>Por qué remitos/PRES/OC necesitan esta mitad y ventas necesita las dos.</b> El
    /// checkout escribe <c>stock</c>/<c>movimientos_stock</c> por <c>id_punto_venta</c> Y consume
    /// <c>numeraciones_comprobante</c>; ambos lados de <see cref="ExigirCompatibleConElActorAsync"/>
    /// (dispositivo Y web) protegen esa doble escritura. Los tres documentos de acá también numeran
    /// desde <c>numeraciones_comprobante</c> (<see cref="Ways.Application.Ventas.AsignadorDeNumeroComprobante"/>,
    /// series <c>REM</c>/<c>PRES</c>/<c>OC</c>), pero cada uno en su propia fila — el daño concreto
    /// acá no es el número (ver el párrafo siguiente), es que
    /// <c>ServicioDeRemitos.EmitirAsync</c> decrementa <c>stock</c>/<c>stock_lotes</c> del punto de
    /// venta del documento, y <c>id_punto_venta</c> de una orden de compra es el destino donde
    /// <c>ServicioDeCompras</c> va a sumar la mercadería recibida. Un actor de dispositivo (la caja
    /// física de UN local) no tiene ningún motivo legítimo para mover stock ajeno o dirigir una
    /// recepción a otro local; un actor web (back office) sí gestiona varios locales a la vez
    /// (<c>docs/09-multi-tenancy.md:210-212</c>: "la selección de punto de venta del legacy (A2) se
    /// conserva"; <c>openspec/specs/operacion-de-pos/spec.md:53-58</c>: el mismo actor opera dos
    /// puntos de venta en secuencia, ambos éxito) — por eso la mitad web NO se agrega acá, a
    /// propósito, aunque complete la simetría con la regla de ventas.</para>
    ///
    /// <para><b>No es una cuestión de numeración.</b> REM/PRES/OC son filas separadas en
    /// <c>numeraciones_comprobante</c> (PK <c>(id_punto_venta, tipo_comprobante)</c>,
    /// <c>docs/09-multi-tenancy.md:159-165</c>) — un bloque <c>TX</c> reservado offline nunca puede
    /// colisionar con ellas. Esta regla es autorización + integridad operativa (destino del stock),
    /// no un backstop de numeración.</para>
    ///
    /// <para><b>Por qué NO hay un chequeo de <c>Modo == Escritorio</c> acá</b> (repo rule "guardas
    /// inmatables no se shippean", PR #257): sería una cláusula estructuralmente inalcanzable. El
    /// flip administrativo a Web se rechaza con <c>409 punto_venta_con_dispositivo_activo</c>
    /// mientras el punto de venta tenga un dispositivo activo vinculado
    /// (<c>docs/10-modelo-de-datos.md:1387-1390</c>) — así que, para llegar acá con
    /// <c>contexto.IdDispositivo</c> set, ese dispositivo o bien sigue activo (su punto de venta
    /// sigue siendo Escritorio) o fue revocado, en cuyo caso el lookup de abajo devuelve
    /// <c>null</c> y la comparación de identidad ya lo rechaza sin necesitar el modo. Ningún test
    /// puede matar un <c>if (puntoVenta.Modo != Escritorio)</c> agregado acá — se documenta la
    /// razón en vez de shippear el <c>if</c> muerto.</para>
    /// </summary>
    public static async Task ExigirPuntoVentaPropioDelDispositivoAsync(
        IWaysDbContext db, IContextoDeUsuario contexto, int idPuntoVenta, CancellationToken ct)
    {
        if (contexto.IdDispositivo is not { } idDispositivo)
        {
            return;
        }

        var idPuntoVentaDelDispositivo = await ResolverIdPuntoVentaDelDispositivoAsync(db, idDispositivo, ct);

        if (idPuntoVentaDelDispositivo != idPuntoVenta)
        {
            throw new ErrorDominio(
                "punto_venta_ajeno_al_dispositivo",
                "Este dispositivo no puede operar contra ese punto de venta.",
                409);
        }
    }

    /// <summary>Único lookup dispositivo→punto de venta, compartido por las dos reglas de esta
    /// clase — nunca duplicado (ver el doc-comment de la clase).</summary>
    private static async Task<int?> ResolverIdPuntoVentaDelDispositivoAsync(
        IWaysDbContext db, int idDispositivo, CancellationToken ct) =>
        await db.Dispositivos
            .Where(d => d.Id == idDispositivo)
            .Select(d => (int?)d.IdPuntoVenta)
            .FirstOrDefaultAsync(ct);
}
