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
/// </summary>
public static class PoliticaDeModoDePuntoVenta
{
    public static async Task ExigirCompatibleConElActorAsync(
        IWaysDbContext db, IContextoDeUsuario contexto, PuntoVenta puntoVenta, CancellationToken ct)
    {
        if (contexto.IdDispositivo is { } idDispositivo)
        {
            var idPuntoVentaDelDispositivo = await db.Dispositivos
                .Where(d => d.Id == idDispositivo)
                .Select(d => (int?)d.IdPuntoVenta)
                .FirstOrDefaultAsync(ct);

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
}
