namespace Ways.Domain.Ventas;

/// <summary>Por qué la cola local de un dispositivo bloquea el cierre del turno — uno por
/// disyunto de <see cref="ReglaDeRendicionDeCola.Evaluar"/>, para que el rechazo pueda decir
/// QUÉ falta y no solo que falta.</summary>
public enum MotivoDeRendicionPendiente
{
    /// <summary>El bloque nunca rindió (<see cref="ReservaNumeracion.ReportadoAt"/> null, o una
    /// rendición a medias que <c>ck_reservas_numeracion_reporte_consistente</c> no debería dejar
    /// existir). Fail-closed: "no sé" nunca se interpreta como "no hay nada pendiente".</summary>
    SinReporte,

    /// <summary>El reporte existe pero es viejo: no dice nada sobre lo que el dispositivo vendió
    /// DESPUÉS de mandarlo (ver <see cref="ReglaDeRendicionDeCola.VentanaDeFrescura"/>). Solo aplica
    /// a un bloque VIVO — ver el parámetro <c>bloqueVivo</c> de
    /// <see cref="ReglaDeRendicionDeCola.Evaluar"/>.</summary>
    ReporteVencido,

    /// <summary>El dispositivo declara ventas que todavía no llegaron al servidor (outbox +
    /// rechazadas).</summary>
    VentasSinLlegar,

    /// <summary>El reporte se contradice con los comprobantes: faltan filas en
    /// <c>comprobantes_venta</c> dentro del rango verificado (<c>[desde, techoVerificado]</c>).
    /// Lo que el techo VERIFICADO agrega, dicho sin adornos: que el dispositivo se RETRACTE (declare
    /// un techo por debajo del número más alto que el servidor ya vio llegar de ese bloque) ya no
    /// puede TAPAR un hueco existente, porque el rango esperado no se encoge con lo declarado.
    /// Retractarse por sí solo NO bloquea: con llegadas contiguas el techo verificado es el máximo
    /// llegado y los comprobantes del rango son exactamente los esperados. NO cubre —ni puede,
    /// porque es inforjable desde el servidor— un número que el dispositivo imprimió, nunca mandó y
    /// nunca declara: de ese caso el servidor no tiene ninguna traza.</summary>
    HuecoDeComprobantes
}

/// <summary>
/// La decisión de si la cola local de un dispositivo bloquea el cierre del turno — pura, sin base
/// de datos: el lector de <c>Ways.Application.Caja</c> solo hace la IO (leer el bloque, contar los
/// comprobantes que llegaron y derivar el techo verificado) y esta regla decide.
///
/// El problema que existe para cerrar: un turno se podía cerrar mientras un dispositivo de
/// escritorio todavía tenía ventas sin drenar en su cola local. La guarda anterior era SOLO del
/// cliente (<c>Pos.tsx</c>/<c>CierreDeCaja.tsx</c>) y leía el IndexedDB LOCAL, así que un cierre
/// desde otra máquina o desde la web leía un almacén vacío y pasaba — esas ventas encoladas
/// después drenaban y caían en el turno siguiente, corrompiendo los dos arqueos.
///
/// Un dispositivo no puede vender offline sin un bloque de reserva
/// (<c>ServicioDeVentas.ExigirNumeroPreasignadoPropioAsync</c> rechaza cualquier número
/// preasignado que no caiga en uno), así que los bloques son el único lugar donde hace falta mirar
/// — TODOS los que no estén saldados, no solo el vigente: el bloque vivo rota en cada reposición y
/// lo que quedó sin rendir en el anterior no desaparece por eso (ver
/// <see cref="ReservaNumeracion.RendicionSaldadaAt"/>).
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
    /// <param name="bloqueVivo">Si el bloque todavía está vigente (<c>abandonada_at IS NULL</c>).
    /// Parte la regla en dos, y no es una sutileza (judgment-day, SEVERE): un bloque VIVO todavía
    /// puede rendir y todavía puede repartir números nuevos, así que la FRESCURA de su reporte es
    /// información — un reporte viejo no dice nada de lo que se vendió después de mandarlo. Un
    /// bloque ABANDONADO no puede rendir nunca más (el <c>UPDATE</c> de
    /// <c>AsignadorDeNumeroComprobante.RegistrarRendicionAsync</c> solo toca el bloque vivo) ni va a
    /// repartir un número más: su evidencia quedó CONGELADA en el momento de la rotación, así que la
    /// frescura no significa nada para él y exigírsela rechazaría TODO cierre del punto de venta
    /// cinco minutos después de cada reposición rutinaria, sin ningún hueco que mostrar. Los otros
    /// tres disyuntos valen igual para los dos. Residual conocido de (c) sobre un bloque abandonado:
    /// su <c>pendientes</c> quedó congelado y nadie puede corregirlo, así que sigue bloqueando aun
    /// cuando esas ventas después hayan llegado — la salida es el forzado supervisado (que lo salda
    /// definitivamente, porque ya está abandonado) o revocar el dispositivo.</param>
    /// <param name="techoVerificado">El MAYOR entre <paramref name="entregadoHasta"/> y el número
    /// más alto del bloque que ya llegó a <c>comprobantes_venta</c> (<c>desde - 1</c> cuando no
    /// llegó ninguno): el techo que el servidor puede sostener por sí mismo, así que nunca queda
    /// por debajo de lo declarado. Lo calcula el lector en la misma consulta que cuenta, para que
    /// un dispositivo no pueda encoger el rango verificado declarando menos.</param>
    /// <param name="comprobantesEnElRango">Cuántas filas de <c>comprobantes_venta</c> existen de
    /// verdad en <c>[desde, techoVerificado]</c> para la misma serie y punto de venta.</param>
    public static MotivoDeRendicionPendiente? Evaluar(
        bool bloqueVivo,
        DateTimeOffset? reportadoAt,
        int? pendientes,
        long? entregadoHasta,
        long desde,
        long techoVerificado,
        long comprobantesEnElRango,
        DateTimeOffset momento)
    {
        // Las tres columnas del reporte son un solo hecho (ck_reservas_numeracion_reporte_consistente):
        // media rendición no existe en la base, y si alguna vez existiera se trata igual que la
        // ausencia total — nunca se completa con un default optimista.
        if (reportadoAt is not { } reporte
            || pendientes is not { } sinLlegar
            || entregadoHasta is null)
        {
            return MotivoDeRendicionPendiente.SinReporte;
        }

        // La frescura es el único disyunto que depende de que el bloque siga vivo: el reporte de un
        // bloque abandonado no puede refrescarse nunca más y tampoco hace falta, porque ese bloque ya
        // no reparte números (ver el parámetro bloqueVivo).
        if (bloqueVivo && momento - reporte > VentanaDeFrescura)
        {
            return MotivoDeRendicionPendiente.ReporteVencido;
        }

        if (sinLlegar > 0)
        {
            return MotivoDeRendicionPendiente.VentasSinLlegar;
        }

        // El esperado se mide contra el techo VERIFICADO, no contra lo declarado: así declarar menos
        // no encoge el rango que el servidor exige completo. techoVerificado == desde - 1 ⇒ ni se
        // declararon números repartidos ni llegó ninguno: el rango es vacío y el esperado es 0.
        if (comprobantesEnElRango < techoVerificado - desde + 1)
        {
            return MotivoDeRendicionPendiente.HuecoDeComprobantes;
        }

        return null;
    }
}
