using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ways.Application.Abstracciones;
using Ways.Application.Parametros;
using Ways.Domain.Catalogos;
using Ways.Domain.Fiscal;

namespace Ways.Application.Fiscal;

/// <summary>
/// Decide, una vez registrada una venta del POS, si el ticket entero debe facturarse (facturación por medio de pago).
/// No emite la factura: deja la decisión (<c>facturaciones_de_venta</c> en <c>pendiente</c>) y mueve los acumulados
/// mensuales por medio de pago (<c>acumulados_de_facturacion</c>).
///
/// Corre en su PROPIA transacción corta, después del commit de la venta, para que el lock del acumulado nunca entre en
/// el conjunto de locks del checkout. Es seguro llamarlo cualquier cantidad de veces para la misma venta: la marca
/// <c>comprobantes_venta.facturacion_decidida_en</c> (que existe porque una venta que no se factura no tiene fila de
/// decisión) y la fila de decisión hacen que la segunda llamada devuelva lo ya decidido sin tocar saldos.
///
/// La marca en <c>NULL</c> significa una sola cosa: la decisión está debida. Toda venta que la decisión examina queda
/// marcada, se facture o no (no es un ticket, está anulada, la empresa no tiene un certificado activo del ambiente configurado, no cobró
/// nada); solo los espejos fiscales, que nunca se deciden, quedan fuera. Los comprobantes que nunca son un ticket y se
/// crean por otros escritores (consolidación de remitos, recibos de cuenta corriente, emisión fiscal directa) nacen ya
/// marcados. Las ventas anteriores a la función nacieron marcadas por la migración. Una decisión que falla deja la marca
/// en <c>NULL</c>, suma un intento fallido con espera creciente (ver <see cref="RegistrarFalloAsync"/>) y la completa
/// <see cref="SanearDecisionesDebidasAsync"/> desde la siguiente venta del mismo punto de venta, cuando vence la espera.
///
/// Orden de locks de la transacción: 1) la fila del comprobante (FOR UPDATE), que serializa todo lo que decide o anula
/// esa venta; 2) las filas del acumulado de la venta, por <c>id_medio_pago</c> ascendente. El acumulado solo se escribe
/// con SQL crudo (<c>SaveChanges</c> rechaza la tabla), igual que <see cref="AsignadorDeNumeroFiscal"/>.
///
/// Monto de cada pago: <c>importe − vuelto</c>, lo que ese medio realmente cobró. Es la misma convención del arqueo
/// (<c>LectorDeMovimientosDelTurno</c> resta el vuelto del efectivo) y hace que los montos del ticket sumen su total;
/// con el importe entregado, un ticket de 700 pagado con un billete de 1000 sumaría 1000 al medio.
///
/// Período: el primer día del mes de la fecha del comprobante (el reloj del servidor al ingerir la venta) en la zona
/// horaria del punto de venta (<c>zona_horaria</c> resuelta por <see cref="ServicioDeParametros"/>), la misma que usan
/// los reportes de ventas.
/// </summary>
public class ServicioDeDecisionDeFacturacion(
    IWaysDbContext db,
    IRelojDelSistema reloj,
    IOptions<OpcionesFiscales> opciones,
    ServicioDeParametros parametros,
    ILogger<ServicioDeDecisionDeFacturacion> log)
{
    /// <summary>Cuántas ventas debidas como máximo decide un barrido, para no frenar la caja.</summary>
    public const int LimiteDeBarrido = 5;

    /// <summary>Tiempo total que puede ocupar un barrido; vencido, las ventas que faltan esperan al siguiente.</summary>
    public static readonly TimeSpan PresupuestoDeBarrido = TimeSpan.FromSeconds(3);

    /// <summary>Tiempo total que puede ocupar la decisión de la venta recién emitida, con todos los reintentos de la
    /// estrategia de ejecución adentro; vencido, la venta responde <c>sin_decidir</c> y queda debida.</summary>
    public static readonly TimeSpan PresupuestoDeDecision = TimeSpan.FromSeconds(3);

    /// <summary>Tiempo máximo para anotar un intento fallido. Si otra sesión tiene tomada la fila de la venta, la
    /// anotación espera ese tiempo y se omite: la venta sigue debida, solo que sin espera creciente.</summary>
    public static readonly TimeSpan PresupuestoDeRegistroDeFallo = TimeSpan.FromSeconds(1);

    /// <summary>Espera tras el primer fallo; se duplica con cada fallo siguiente (1 min, 2, 4, 8…).</summary>
    public static readonly TimeSpan EsperaInicialTrasFallo = TimeSpan.FromMinutes(1);

    /// <summary>Tope de la espera creciente: una venta que no se puede decidir se reintenta cada 4 horas como máximo.</summary>
    public static readonly TimeSpan EsperaMaximaTrasFallo = TimeSpan.FromHours(4);

    /// <summary>Desde este intento fallido la venta se registra con nivel de error para que una venta trabada sea visible.</summary>
    public const int IntentosParaRegistrarComoError = 5;

    private static readonly JsonSerializerOptions OpcionesDeAportes = new(JsonSerializerDefaults.Web);

    /// <summary>Cada cuánto, como máximo, se avisa en el log que una empresa tiene certificado activo solo de otro
    /// ambiente que el configurado.</summary>
    public static readonly TimeSpan PeriodoDeAvisoDeAmbiente = TimeSpan.FromHours(1);

    // El servicio es scoped; el aviso se limita por proceso, así que el estado es estático. Se limita por empresa.
    private static readonly Lock CandadoDeAvisos = new();
    private static readonly Dictionary<int, DateTimeOffset> UltimoAvisoDeAmbientePorEmpresa = [];

    /// <summary>Lo que llama el endpoint de ventas: decide la venta recién emitida y, salvo que esa decisión haya
    /// terminado en <see cref="EstadosFiscalesDeVenta.SinDecidir"/> (sea cual sea la causa), sanea las ventas debidas
    /// del mismo punto de venta. Nunca lanza.</summary>
    public async Task<EstadoFiscalDeVenta> DecidirYSanearAsync(int idComprobanteVenta, int idPuntoVenta)
    {
        var estado = await DecidirTrasLaVentaAsync(idComprobanteVenta);
        if (estado.Estado != EstadosFiscalesDeVenta.SinDecidir)
        {
            await SanearDecisionesDebidasAsync(idPuntoVenta, idComprobanteVenta);
        }

        return estado;
    }

    /// <summary>Decide la venta recién comiteada. Nunca lanza: la venta ya está registrada y la caja no puede frenarse por
    /// la decisión fiscal. Corre con su propio presupuesto de <see cref="PresupuestoDeDecision"/>, que se pasa como token a la
    /// estrategia de ejecución y a cada comando. Si falla o el presupuesto vence, la transacción se deshace, el intento
    /// se anota (<see cref="RegistrarFalloAsync"/>), responde <see cref="EstadosFiscalesDeVenta.SinDecidir"/> y la marca
    /// <c>facturacion_decidida_en</c> sigue en <c>NULL</c>: <see cref="SanearDecisionesDebidasAsync"/> la completa en una
    /// venta posterior. No recibe el token de la solicitud a propósito: la venta ya está comiteada y que el cliente corte
    /// la conexión no debe dejar la decisión a medias.</summary>
    public async Task<EstadoFiscalDeVenta> DecidirTrasLaVentaAsync(int idComprobanteVenta)
    {
        try
        {
            using var presupuesto = new CancellationTokenSource(PresupuestoDeDecision);
            return await DecidirAsync(idComprobanteVenta, presupuesto.Token);
        }
        catch (Exception ex)
        {
            await RegistrarFalloAsync(idComprobanteVenta, ex);
            return EstadoFiscalDeVenta.SinDecidir;
        }
    }

    /// <summary>Decide hasta <see cref="LimiteDeBarrido"/> ventas debidas del punto de venta cuya espera ya venció, las más
    /// viejas primero, cada una en su propia transacción. Es la forma en que una decisión que falló se completa sola. Nunca
    /// lanza, no recibe el token de la solicitud y se corta a los <see cref="PresupuestoDeBarrido"/>. Una venta que falla
    /// se anota con su espera creciente y el barrido sigue con la siguiente, salvo que la falla sea el corte del propio
    /// presupuesto del barrido (su token cancelado): eso no es un fallo de la venta, no se anota y el barrido termina;
    /// cada candidata se intenta una sola vez por
    /// llamada. <paramref name="idExcluida"/> es la venta que el llamador acaba de decidir, para no reintentarla en la
    /// misma llamada.</summary>
    public async Task SanearDecisionesDebidasAsync(int idPuntoVenta, int idExcluida)
    {
        using var presupuesto = new CancellationTokenSource(PresupuestoDeBarrido);
        await SanearDecisionesDebidasAsync(idPuntoVenta, idExcluida, presupuesto.Token);
    }

    /// <summary>El mismo barrido con un presupuesto que fija el llamador (<see cref="EmisionEnLineaAlCobrar"/> lo recorta a lo
    /// que queda de su tope total). Solo lo llama código de la aplicación: el endpoint no tiene forma de pasarle el token de la
    /// solicitud. El corte de ese token se trata igual que el del presupuesto propio.</summary>
    internal async Task SanearDecisionesDebidasAsync(int idPuntoVenta, int idExcluida, CancellationToken ct)
    {
        try
        {
            var ahora = reloj.Ahora;

            // Ventas del punto de venta que nadie decidió: sin marca y que no son espejos (que nunca se deciden), salvo las
            // que fallaron hace poco y todavía esperan (facturacion_proximo_intento_en en el futuro). Las más viejas
            // primero por id; el índice parcial ix_comprobantes_venta_decision_debida entrega ese orden y la espera se
            // filtra sobre sus filas.
            var debidas = await db.Database.SqlQuery<int>(
                $"""
                SELECT id_comprobante_venta AS "Value" FROM comprobantes_venta
                WHERE id_punto_venta = {idPuntoVenta} AND facturacion_decidida_en IS NULL AND id_venta_origen IS NULL
                AND id_comprobante_venta <> {idExcluida}
                AND (facturacion_proximo_intento_en IS NULL OR facturacion_proximo_intento_en <= {ahora})
                ORDER BY id_comprobante_venta
                LIMIT {LimiteDeBarrido}
                """).ToListAsync(ct);

            foreach (var idDebida in debidas)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await DecidirAsync(idDebida, ct);
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    // El presupuesto del barrido cortó la decisión de una venta sana: no es un fallo de la venta, así que no
                    // se anota. Queda debida, sin espera, y la decide una llamada posterior.
                    log.LogDebug(
                        "El presupuesto del barrido cortó la decisión de la venta {IdComprobanteVenta}; no se anota como fallo.",
                        idDebida);
                    break;
                }
                catch (Exception ex)
                {
                    await RegistrarFalloAsync(idDebida, ex);
                }
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "El barrido de decisiones de facturación debidas del punto de venta {IdPuntoVenta} falló.", idPuntoVenta);
        }
    }

    /// <summary>Anota un intento fallido de decidir la venta, FUERA de la transacción que falló: suma uno al contador y
    /// fija la espera <c>EsperaInicialTrasFallo × 2^(intentos−1)</c> con tope <see cref="EsperaMaximaTrasFallo"/>, contada
    /// desde el reloj del servicio. Nunca lanza; tiene su propio presupuesto
    /// (<see cref="PresupuestoDeRegistroDeFallo"/>) para no quedar atado a un token vencido ni a una fila bloqueada.
    /// Registra el fallo con nivel de advertencia, y de error cuando la venta ya acumula
    /// <see cref="IntentosParaRegistrarComoError"/> intentos.</summary>
    private async Task RegistrarFalloAsync(int idComprobanteVenta, Exception causa)
    {
        int? intentos = null;
        try
        {
            using var presupuesto = new CancellationTokenSource(PresupuestoDeRegistroDeFallo);
            var ct = presupuesto.Token;

            await db.Database.OpenConnectionAsync(ct);
            try
            {
                await using var comando = db.Database.GetDbConnection().CreateCommand();
                comando.CommandText =
                    "UPDATE comprobantes_venta SET facturacion_intentos = facturacion_intentos + 1, " +
                    "facturacion_proximo_intento_en = $2::timestamptz + " +
                    "make_interval(secs => LEAST($3::double precision * power(2, LEAST(facturacion_intentos, 30)), " +
                    "$4::double precision)) " +
                    "WHERE id_comprobante_venta = $1 " +
                    "RETURNING facturacion_intentos";
                ParametrosDeComando.Agregar(comando, idComprobanteVenta);
                ParametrosDeComando.Agregar(comando, reloj.Ahora);
                ParametrosDeComando.Agregar(comando, EsperaInicialTrasFallo.TotalSeconds);
                ParametrosDeComando.Agregar(comando, EsperaMaximaTrasFallo.TotalSeconds);
                intentos = await comando.ExecuteScalarAsync(ct) as int?;
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(
                ex, "No se pudo anotar el intento fallido de la decisión de la venta {IdComprobanteVenta}.", idComprobanteVenta);
        }

        if (intentos >= IntentosParaRegistrarComoError)
        {
            log.LogError(
                causa,
                "La decisión de facturación de la venta {IdComprobanteVenta} lleva {Intentos} intentos fallidos; sigue debida.",
                idComprobanteVenta, intentos);
        }
        else
        {
            log.LogWarning(
                causa,
                "No se pudo decidir la facturación de la venta {IdComprobanteVenta} (intento {Intentos}); queda debida.",
                idComprobanteVenta, intentos);
        }
    }

    /// <summary>Deja un aviso en el log, a lo sumo uno por empresa y por <see cref="PeriodoDeAvisoDeAmbiente"/> en el proceso
    /// (medido con el reloj del servicio), de que la empresa tiene certificado activo pero no del ambiente configurado.</summary>
    private void AvisarCertificadoSoloDeOtroAmbiente(int idEmpresa, AmbienteFiscal ambiente)
    {
        var ahora = reloj.Ahora;
        lock (CandadoDeAvisos)
        {
            if (UltimoAvisoDeAmbientePorEmpresa.TryGetValue(idEmpresa, out var ultimo) && ahora - ultimo < PeriodoDeAvisoDeAmbiente)
            {
                return;
            }

            UltimoAvisoDeAmbientePorEmpresa[idEmpresa] = ahora;
        }

        log.LogWarning(
            "La empresa {IdEmpresa} tiene un certificado fiscal activo, pero ninguno del ambiente configurado {Ambiente}: " +
            "sus ventas se marcan como no aplicables y no se facturan.",
            idEmpresa, ambiente);
    }

    public async Task<EstadoFiscalDeVenta> DecidirAsync(int idComprobanteVenta, CancellationToken ct = default)
    {
        var estrategia = db.Database.CreateExecutionStrategy();

        return await estrategia.ExecuteAsync(async _ =>
        {
            db.ChangeTracker.Clear();

            await using var transaccion = await db.Database.BeginTransactionAsync(ct);
            var conexion = db.Database.GetDbConnection();
            var transaccionCruda = transaccion.GetDbTransaction();
            var ahora = reloj.Ahora;

            // Un espejo fiscal nunca se decide: la lectura lo deja afuera (id_venta_origen IS NULL).
            var venta = await BloquearVentaAsync(conexion, transaccionCruda, idComprobanteVenta, ct);
            if (venta is null)
            {
                return EstadoFiscalDeVenta.NoCorresponde;
            }

            // Cierra sin mover saldos: la venta queda marcada como decidida (si no lo estaba) y se devuelve el resultado.
            async Task<EstadoFiscalDeVenta> CerrarSinMovimientoAsync(EstadoFiscalDeVenta resultado)
            {
                if (venta.DecididaEn is null)
                {
                    await MarcarDecididaAsync(conexion, transaccionCruda, idComprobanteVenta, ahora, ct);
                    await transaccion.CommitAsync(ct);
                }

                return resultado;
            }

            if (!venta.EsTicketDeVenta)
            {
                return await CerrarSinMovimientoAsync(EstadoFiscalDeVenta.NoCorresponde);
            }

            if (await LeerDecisionAsync(conexion, transaccionCruda, idComprobanteVenta, ct) is { } yaDecidida)
            {
                return await CerrarSinMovimientoAsync(yaDecidida);
            }

            if (venta.DecididaEn is not null)
            {
                return EstadoFiscalDeVenta.NoCorresponde;
            }

            if (venta.Estado != "emitido")
            {
                return await CerrarSinMovimientoAsync(EstadoFiscalDeVenta.NoCorresponde);
            }

            // Una empresa sin certificado activo del ambiente configurado no puede facturar en este despliegue: la venta se
            // decide como no aplicable y queda marcada, así que un certificado cargado después no la factura
            // retroactivamente. Si además tiene uno activo de OTRO ambiente se deja un aviso (limitado) en el log, por si
            // el ambiente está mal configurado; la venta se marca igual.
            var ambiente = opciones.Value.ResolverAmbiente();
            var ambientesActivos = await db.CertificadosFiscales.AsNoTracking()
                .Where(c => c.IdEmpresa == venta.IdEmpresa && c.Activo)
                .Select(c => c.Ambiente)
                .ToListAsync(ct);
            if (!ambientesActivos.Contains(ambiente))
            {
                var sinCertificado = await CerrarSinMovimientoAsync(EstadoFiscalDeVenta.NoCorresponde);
                if (ambientesActivos.Count > 0)
                {
                    AvisarCertificadoSoloDeOtroAmbiente(venta.IdEmpresa, ambiente);
                }

                return sinCertificado;
            }

            var pagos = await LeerPagosAsync(conexion, transaccionCruda, idComprobanteVenta, ct);
            if (pagos.Count == 0)
            {
                // Nada se cobró (ticket sin pagos o de importe neto cero): no hay medio al que imputar ni qué facturar.
                return await CerrarSinMovimientoAsync(EstadoFiscalDeVenta.NoCorresponde);
            }

            var periodo = await ResolverPeriodoAsync(venta, ct);
            var idsMedio = pagos.Select(p => p.IdMedioPago).Distinct().Order().ToArray();

            foreach (var idMedio in idsMedio)
            {
                await AsegurarAcumuladoAsync(conexion, transaccionCruda, venta, idMedio, periodo, ct);
            }

            var saldos = await BloquearAcumuladosAsync(conexion, transaccionCruda, venta.IdEmpresa, idsMedio, periodo, ct);

            var decision = PoliticaDeFacturacionPorMedio.Decidir(
                [.. pagos.Select(p => new PagoDeTicket(p.IdMedioPago, p.Monto, p.Porcentaje))], saldos, venta.FacturaForzada);

            var aportes = decision.Aportes.ToDictionary(a => a.IdMedioPago, a => a.Aporte);
            foreach (var idMedio in idsMedio)
            {
                var vendido = pagos.Where(p => p.IdMedioPago == idMedio).Sum(p => p.Monto);
                await AplicarAportesAsync(
                    conexion, transaccionCruda, venta.IdEmpresa, idMedio, periodo,
                    aportes[idMedio], vendido, decision.Facturar ? vendido : 0m, ct);
            }

            EstadoFiscalDeVenta resultado = EstadoFiscalDeVenta.NoCorresponde;
            if (decision.Facturar)
            {
                // El lock de la fila de la venta serializa las decisiones de esta venta, así que el UNIQUE de
                // id_comprobante_venta no puede chocar acá: si lo hiciera sería un defecto y sale como excepción.
                var idFacturacion = await InsertarDecisionAsync(
                    conexion, transaccionCruda, idComprobanteVenta, venta, decision, periodo, ahora, ct);

                resultado = new EstadoFiscalDeVenta(
                    EstadosFiscalesDeVenta.Pendiente, EtiquetaDeMotivo(decision.Motivo), idFacturacion);
            }

            await MarcarDecididaAsync(conexion, transaccionCruda, idComprobanteVenta, ahora, ct);
            await transaccion.CommitAsync(ct);
            return resultado;
        }, ct);
    }

    // ---- Lecturas -------------------------------------------------------------------------------

    private sealed record VentaBloqueada(
        string Estado, bool FacturaForzada, DateTimeOffset Fecha, int IdPuntoVenta, int IdTenant,
        DateTimeOffset? DecididaEn, int IdEmpresa, short Signo, bool EsFiscal, bool AfectaStock, string Clase)
    {
        /// <summary>Un ticket de venta: clase venta, no fiscal, con signo positivo y que mueve stock (lo que el POS
        /// emite como TX). Las devoluciones (signo negativo) y los remitos consolidados quedan afuera.</summary>
        public bool EsTicketDeVenta => Clase == "venta" && !EsFiscal && Signo > 0 && AfectaStock;
    }

    private sealed record PagoNeto(int IdMedioPago, decimal Monto, decimal Porcentaje);

    private static async Task<VentaBloqueada?> BloquearVentaAsync(
        DbConnection conexion, DbTransaction transaccion, int idComprobanteVenta, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT c.estado::text, c.factura_forzada, c.fecha, c.id_punto_venta, c.id_tenant, c.facturacion_decidida_en, " +
            "pv.id_empresa, t.signo, t.es_fiscal, t.afecta_stock, t.clase::text " +
            "FROM comprobantes_venta c " +
            "JOIN tipos_comprobante t ON t.id_tipo_comprobante = c.id_tipo_comprobante " +
            "JOIN puntos_venta pv ON pv.id_punto_venta = c.id_punto_venta " +
            "WHERE c.id_comprobante_venta = $1 AND c.id_venta_origen IS NULL " +
            "FOR UPDATE OF c";
        ParametrosDeComando.Agregar(comando, idComprobanteVenta);

        await using var lector = await comando.ExecuteReaderAsync(ct);
        if (!await lector.ReadAsync(ct))
        {
            return null;
        }

        return new VentaBloqueada(
            lector.GetString(0), lector.GetBoolean(1), lector.GetFieldValue<DateTimeOffset>(2), lector.GetInt32(3),
            lector.GetInt32(4), lector.IsDBNull(5) ? null : lector.GetFieldValue<DateTimeOffset>(5), lector.GetInt32(6),
            lector.GetInt16(7), lector.GetBoolean(8), lector.GetBoolean(9), lector.GetString(10));
    }

    private static async Task<EstadoFiscalDeVenta?> LeerDecisionAsync(
        DbConnection conexion, DbTransaction? transaccion, int idComprobanteVenta, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT id_facturacion_venta, motivo::text, estado::text FROM facturaciones_de_venta " +
            "WHERE id_comprobante_venta = $1 AND id_facturacion_origen IS NULL";
        ParametrosDeComando.Agregar(comando, idComprobanteVenta);

        await using var lector = await comando.ExecuteReaderAsync(ct);
        if (!await lector.ReadAsync(ct))
        {
            return null;
        }

        var estado = lector.GetString(2) switch
        {
            "pendiente" => EstadosFiscalesDeVenta.Pendiente,
            "emitida" => EstadosFiscalesDeVenta.Aprobado,
            "rechazada" => EstadosFiscalesDeVenta.Rechazado,
            "cancelada" => EstadosFiscalesDeVenta.Cancelado,
            var otro => throw new InvalidOperationException($"Estado de facturación desconocido: {otro}.")
        };

        return new EstadoFiscalDeVenta(estado, lector.GetString(1), lector.GetInt32(0));
    }

    private static async Task<IReadOnlyList<PagoNeto>> LeerPagosAsync(
        DbConnection conexion, DbTransaction transaccion, int idComprobanteVenta, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT p.id_medio_pago, p.importe - p.vuelto, m.porcentaje_facturacion " +
            "FROM pagos_comprobante p " +
            "JOIN medios_pago m ON m.id_medio_pago = p.id_medio_pago " +
            "WHERE p.id_comprobante_venta = $1 AND p.deleted_at IS NULL";
        ParametrosDeComando.Agregar(comando, idComprobanteVenta);

        var pagos = new List<PagoNeto>();
        await using var lector = await comando.ExecuteReaderAsync(ct);
        while (await lector.ReadAsync(ct))
        {
            var monto = lector.GetDecimal(1);
            if (monto > 0m)
            {
                pagos.Add(new PagoNeto(lector.GetInt32(0), monto, lector.GetDecimal(2)));
            }
        }

        return pagos;
    }

    private async Task<DateOnly> ResolverPeriodoAsync(VentaBloqueada venta, CancellationToken ct)
    {
        var resuelto = await parametros.ResolverAsync(
            ParametroConocido.ZonaHoraria.Clave, venta.IdEmpresa, venta.IdPuntoVenta, ct);
        var zona = TimeZoneInfo.FindSystemTimeZoneById(JsonSerializer.Deserialize<string>(resuelto.Valor)!);
        var dia = FechaFiscal.DeInstante(venta.Fecha, zona);
        return new DateOnly(dia.Year, dia.Month, 1);
    }

    // ---- Acumulado (SQL crudo: SaveChanges rechaza la tabla) -----------------------------------

    private static async Task AsegurarAcumuladoAsync(
        DbConnection conexion, DbTransaction transaccion, VentaBloqueada venta, int idMedioPago, DateOnly periodo,
        CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO acumulados_de_facturacion (id_empresa, id_medio_pago, periodo, id_tenant) " +
            "VALUES ($1, $2, $3, $4) " +
            "ON CONFLICT (id_empresa, id_medio_pago, periodo) DO NOTHING";
        ParametrosDeComando.Agregar(comando, venta.IdEmpresa);
        ParametrosDeComando.Agregar(comando, idMedioPago);
        ParametrosDeComando.Agregar(comando, periodo);
        ParametrosDeComando.Agregar(comando, venta.IdTenant);
        await comando.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Toma los locks de las filas del acumulado en orden ascendente de medio y devuelve los saldos leídos
    /// UNA sola vez, ya bajo el lock: son los que la política usa para decidir.</summary>
    private static async Task<IReadOnlyDictionary<int, decimal>> BloquearAcumuladosAsync(
        DbConnection conexion, DbTransaction transaccion, int idEmpresa, int[] idsMedioPago, DateOnly periodo,
        CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT id_medio_pago, saldo_a_facturar FROM acumulados_de_facturacion " +
            "WHERE id_empresa = $1 AND periodo = $2 AND id_medio_pago = ANY($3) " +
            "ORDER BY id_medio_pago FOR UPDATE";
        ParametrosDeComando.Agregar(comando, idEmpresa);
        ParametrosDeComando.Agregar(comando, periodo);
        ParametrosDeComando.Agregar(comando, idsMedioPago);

        var saldos = new Dictionary<int, decimal>();
        await using var lector = await comando.ExecuteReaderAsync(ct);
        while (await lector.ReadAsync(ct))
        {
            saldos[lector.GetInt32(0)] = lector.GetDecimal(1);
        }

        return saldos;
    }

    private static async Task AplicarAportesAsync(
        DbConnection conexion, DbTransaction transaccion, int idEmpresa, int idMedioPago, DateOnly periodo,
        decimal aporte, decimal vendido, decimal facturado, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "UPDATE acumulados_de_facturacion SET saldo_a_facturar = saldo_a_facturar + $4, " +
            "total_vendido = total_vendido + $5, total_facturado = total_facturado + $6 " +
            "WHERE id_empresa = $1 AND id_medio_pago = $2 AND periodo = $3";
        ParametrosDeComando.Agregar(comando, idEmpresa);
        ParametrosDeComando.Agregar(comando, idMedioPago);
        ParametrosDeComando.Agregar(comando, periodo);
        ParametrosDeComando.Agregar(comando, aporte);
        ParametrosDeComando.Agregar(comando, vendido);
        ParametrosDeComando.Agregar(comando, facturado);
        await comando.ExecuteNonQueryAsync(ct);
    }

    // ---- Escrituras de la decisión -------------------------------------------------------------

    /// <summary>Inserta la fila de decisión. Es un <c>INSERT</c> simple: el lock de la venta ya serializó a los escritores,
    /// de modo que un 23505 del UNIQUE de la venta es un defecto y no una carrera que perder.</summary>
    private static async Task<int> InsertarDecisionAsync(
        DbConnection conexion, DbTransaction transaccion, int idComprobanteVenta, VentaBloqueada venta,
        DecisionDeFacturacion decision, DateOnly periodo, DateTimeOffset ahora, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO facturaciones_de_venta (id_tenant, id_empresa, id_comprobante_venta, motivo, estado, aportes, " +
            "periodo, intentos, created_at, updated_at) " +
            "VALUES ($1, $2, $3, $4::motivo_facturacion, 'pendiente'::estado_facturacion_venta, $5::jsonb, $6, 0, $7, $7) " +
            "RETURNING id_facturacion_venta";
        ParametrosDeComando.Agregar(comando, venta.IdTenant);
        ParametrosDeComando.Agregar(comando, venta.IdEmpresa);
        ParametrosDeComando.Agregar(comando, idComprobanteVenta);
        ParametrosDeComando.Agregar(comando, EtiquetaDeMotivo(decision.Motivo));
        ParametrosDeComando.Agregar(comando, JsonSerializer.Serialize(decision.Aportes, OpcionesDeAportes));
        ParametrosDeComando.Agregar(comando, periodo);
        ParametrosDeComando.Agregar(comando, ahora);

        return (int)(await comando.ExecuteScalarAsync(ct))!;
    }

    private static async Task MarcarDecididaAsync(
        DbConnection conexion, DbTransaction transaccion, int idComprobanteVenta, DateTimeOffset ahora, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "UPDATE comprobantes_venta SET facturacion_decidida_en = $2 " +
            "WHERE id_comprobante_venta = $1 AND id_venta_origen IS NULL";
        ParametrosDeComando.Agregar(comando, idComprobanteVenta);
        ParametrosDeComando.Agregar(comando, ahora);
        await comando.ExecuteNonQueryAsync(ct);
    }

    private static string EtiquetaDeMotivo(MotivoDeFacturacion motivo) => motivo switch
    {
        MotivoDeFacturacion.Forzada => "forzada",
        MotivoDeFacturacion.MedioExigeFactura => "medio_exige_factura",
        MotivoDeFacturacion.CupoAlcanzado => "cupo_alcanzado",
        _ => throw new ArgumentOutOfRangeException(nameof(motivo), motivo, "Una venta que no se factura no tiene decisión.")
    };
}
