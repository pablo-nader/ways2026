namespace Ways.Domain.Ventas;

/// <summary>Por qué la cola local de un dispositivo bloquea el cierre del turno — uno por
/// disyunto de <see cref="ReglaDeRendicionDeCola.Evaluar"/>, para que el rechazo pueda decir
/// QUÉ falta y no solo que falta.</summary>
public enum MotivoDeRendicionPendiente
{
    /// <summary>El bloque vivo nunca rindió (<see cref="ReservaNumeracion.ReportadoAt"/> null, o
    /// una rendición a medias que <c>ck_reservas_numeracion_reporte_consistente</c> no debería
    /// dejar existir). Fail-closed: "no sé" nunca se interpreta como "no hay nada pendiente".</summary>
    SinReporte,

    /// <summary>El reporte existe pero es viejo: no dice nada sobre lo que el dispositivo vendió
    /// DESPUÉS de mandarlo (ver <see cref="ReglaDeRendicionDeCola.VentanaDeFrescura"/>).</summary>
    ReporteVencido,

    /// <summary>El dispositivo declara ventas que todavía no llegaron al servidor (outbox +
    /// rechazadas).</summary>
    VentasSinLlegar,

    /// <summary>El reporte se contradice con los comprobantes: el dispositivo dice haber repartido
    /// hasta un número y faltan filas en <c>comprobantes_venta</c> dentro de ese rango. Es lo que
    /// hace el reporte VERIFICABLE en vez de meramente creído.</summary>
    HuecoDeComprobantes
}

/// <summary>
/// La decisión de si la cola local de un dispositivo bloquea el cierre del turno — pura, sin base
/// de datos: el lector de <c>Ways.Application.Caja</c> solo hace la IO (leer el bloque vivo y
/// contar los comprobantes del rango) y esta regla decide.
///
/// El problema que existe para cerrar: un turno se podía cerrar mientras un dispositivo de
/// escritorio todavía tenía ventas sin drenar en su cola local. La guarda anterior era SOLO del
/// cliente (<c>Pos.tsx</c>/<c>CierreDeCaja.tsx</c>) y leía el IndexedDB LOCAL, así que un cierre
/// desde otra máquina o desde la web leía un almacén vacío y pasaba — esas ventas encoladas
/// después drenaban y caían en el turno siguiente, corrompiendo los dos arqueos.
///
/// Un dispositivo no puede vender offline sin un bloque de reserva vivo
/// (<c>ServicioDeVentas.ExigirNumeroPreasignadoPropioAsync</c> rechaza cualquier número
/// preasignado que no caiga en uno), así que el bloque vivo es el único lugar donde hace falta
/// mirar.
/// </summary>
public static class ReglaDeRendicionDeCola
{
    /// <summary>Cuánto vale un reporte antes de dejar de probar algo. El ciclo de sincronización
    /// del cliente es de 20 s (<c>INTERVALO_DE_SINCRONIZACION_MS</c>, <c>useSincronizacionOffline.ts</c>),
    /// así que 5 minutos son 15 ciclos perdidos seguidos: bastante para no molestar a un
    /// dispositivo sano con una red que hipa, y poco para que un dispositivo que se quedó sin
    /// señal no pase por limpio.</summary>
    public static readonly TimeSpan VentanaDeFrescura = TimeSpan.FromMinutes(5);

    /// <summary>
    /// <c>null</c> ⇒ este bloque no bloquea el cierre. Los cuatro disyuntos se evalúan en orden
    /// de menor a mayor información: sin reporte no se puede decir nada más, un reporte vencido no
    /// habilita a creerle sus números, y el hueco es el único que necesita haber contado
    /// comprobantes.
    /// </summary>
    /// <param name="comprobantesEnElRango">Cuántas filas de <c>comprobantes_venta</c> existen de
    /// verdad en <c>[desde, entregadoHasta]</c> para la misma serie y punto de venta.</param>
    public static MotivoDeRendicionPendiente? Evaluar(
        DateTimeOffset? reportadoAt,
        int? pendientes,
        long? entregadoHasta,
        long desde,
        long comprobantesEnElRango,
        DateTimeOffset momento)
    {
        // Las tres columnas del reporte son un solo hecho (ck_reservas_numeracion_reporte_consistente):
        // media rendición no existe en la base, y si alguna vez existiera se trata igual que la
        // ausencia total — nunca se completa con un default optimista.
        if (reportadoAt is not { } reporte
            || pendientes is not { } sinLlegar
            || entregadoHasta is not { } entregado)
        {
            return MotivoDeRendicionPendiente.SinReporte;
        }

        if (momento - reporte > VentanaDeFrescura)
        {
            return MotivoDeRendicionPendiente.ReporteVencido;
        }

        if (sinLlegar > 0)
        {
            return MotivoDeRendicionPendiente.VentasSinLlegar;
        }

        // entregado == desde - 1 ⇒ no repartió ningún número: el rango es vacío y el esperado es 0.
        if (comprobantesEnElRango < entregado - desde + 1)
        {
            return MotivoDeRendicionPendiente.HuecoDeComprobantes;
        }

        return null;
    }
}
