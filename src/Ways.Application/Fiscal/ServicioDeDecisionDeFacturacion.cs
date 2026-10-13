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
/// Orden de locks de la transacción: 1) la fila del comprobante (FOR UPDATE), que serializa todo lo que decide o anula
/// esa venta; 2) las filas del acumulado de la venta, por <c>id_medio_pago</c> ascendente. El acumulado solo se escribe
/// con SQL crudo (<c>SaveChanges</c> rechaza la tabla), igual que <see cref="AsignadorDeNumeroFiscal"/>.
///
/// Monto de cada pago: <c>importe − vuelto</c>, lo que ese medio realmente cobró. Es la misma convención del arqueo
/// (<c>LectorDeMovimientosDelTurno</c> resta el vuelto del efectivo) y hace que los montos del ticket sumen su total;
/// con el importe entregado, un ticket de 700 pagado con un billete de 1000 sumaría 1000 al medio.
///
/// Período: el primer día del mes de la fecha del comprobante en la zona horaria del punto de venta
/// (<c>zona_horaria</c> resuelta por <see cref="ServicioDeParametros"/>), la misma que usan los reportes de ventas.
/// </summary>
public class ServicioDeDecisionDeFacturacion(
    IWaysDbContext db,
    IRelojDelSistema reloj,
    IOptions<OpcionesFiscales> opciones,
    ServicioDeParametros parametros,
    ILogger<ServicioDeDecisionDeFacturacion> log)
{
    private static readonly JsonSerializerOptions OpcionesDeAportes = new(JsonSerializerDefaults.Web);

    /// <summary>Lo que llama el endpoint de ventas después del commit de la venta. Nunca lanza: la venta ya está
    /// registrada y la caja no puede frenarse por la decisión fiscal. Si falla, responde
    /// <see cref="EstadosFiscalesDeVenta.SinDecidir"/>: la marca <c>facturacion_decidida_en</c> sigue en <c>NULL</c> y
    /// la decisión se completa en la próxima llamada (el reenvío de la venta con su número pre-asignado, o el
    /// procesador de pendientes).</summary>
    public async Task<EstadoFiscalDeVenta> DecidirTrasLaVentaAsync(int idComprobanteVenta, CancellationToken ct = default)
    {
        try
        {
            return await DecidirAsync(idComprobanteVenta, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(
                ex, "No se pudo decidir la facturación de la venta {IdComprobanteVenta}; queda debida.", idComprobanteVenta);
            return EstadoFiscalDeVenta.SinDecidir;
        }
    }

    public async Task<EstadoFiscalDeVenta> DecidirAsync(int idComprobanteVenta, CancellationToken ct = default)
    {
        var estrategia = db.Database.CreateExecutionStrategy();

        return await estrategia.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();

            await using var transaccion = await db.Database.BeginTransactionAsync(ct);
            var conexion = db.Database.GetDbConnection();
            var transaccionCruda = transaccion.GetDbTransaction();

            // Un espejo fiscal nunca se decide: la lectura lo deja afuera (id_venta_origen IS NULL).
            var venta = await BloquearVentaAsync(conexion, transaccionCruda, idComprobanteVenta, ct);
            if (venta is null || !venta.EsTicketDeVenta)
            {
                return EstadoFiscalDeVenta.NoCorresponde;
            }

            if (await LeerDecisionAsync(conexion, transaccionCruda, idComprobanteVenta, ct) is { } yaDecidida)
            {
                return yaDecidida;
            }

            if (venta.DecididaEn is not null || venta.Estado != "emitido")
            {
                return EstadoFiscalDeVenta.NoCorresponde;
            }

            var ambiente = opciones.Value.ResolverAmbiente();
            var hayCertificadoActivo = await db.CertificadosFiscales.AsNoTracking()
                .AnyAsync(c => c.IdEmpresa == venta.IdEmpresa && c.Ambiente == ambiente && c.Activo, ct);
            if (!hayCertificadoActivo)
            {
                return EstadoFiscalDeVenta.NoCorresponde;
            }

            var pagos = await LeerPagosAsync(conexion, transaccionCruda, idComprobanteVenta, ct);
            var ahora = reloj.Ahora;

            if (pagos.Count == 0)
            {
                // Nada se cobró (ticket sin pagos o de importe cero): no hay medio al que imputar ni qué facturar.
                // Se marca decidida para no reprocesarla.
                await MarcarDecididaAsync(conexion, transaccionCruda, idComprobanteVenta, ahora, ct);
                await transaccion.CommitAsync(ct);
                return EstadoFiscalDeVenta.NoCorresponde;
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
                var idFacturacion = await InsertarDecisionAsync(
                    conexion, transaccionCruda, idComprobanteVenta, venta, decision, periodo, ahora, ct);

                if (idFacturacion is null)
                {
                    // Otro escritor ya decidió esta venta (UNIQUE de id_comprobante_venta): se deshace el acumulado de
                    // esta transacción y se devuelve la decisión existente.
                    await transaccion.RollbackAsync(ct);
                    return await LeerDecisionAsync(conexion, null, idComprobanteVenta, ct)
                        ?? throw new InvalidOperationException(
                            $"La venta {idComprobanteVenta} chocó con una decisión que ya no se puede leer.");
                }

                resultado = new EstadoFiscalDeVenta(
                    EstadosFiscalesDeVenta.Pendiente, EtiquetaDeMotivo(decision.Motivo), idFacturacion);
            }

            await MarcarDecididaAsync(conexion, transaccionCruda, idComprobanteVenta, ahora, ct);
            await transaccion.CommitAsync(ct);
            return resultado;
        });
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
            "WHERE id_comprobante_venta = $1";
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

    /// <summary><c>ON CONFLICT DO NOTHING</c> sobre el UNIQUE de la venta: perder la carrera devuelve <c>null</c> en vez
    /// de abortar la transacción con un 23505.</summary>
    private static async Task<int?> InsertarDecisionAsync(
        DbConnection conexion, DbTransaction transaccion, int idComprobanteVenta, VentaBloqueada venta,
        DecisionDeFacturacion decision, DateOnly periodo, DateTimeOffset ahora, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO facturaciones_de_venta (id_tenant, id_empresa, id_comprobante_venta, motivo, estado, aportes, " +
            "periodo, intentos, created_at, updated_at) " +
            "VALUES ($1, $2, $3, $4::motivo_facturacion, 'pendiente'::estado_facturacion_venta, $5::jsonb, $6, 0, $7, $7) " +
            "ON CONFLICT (id_comprobante_venta, id_tenant) DO NOTHING " +
            "RETURNING id_facturacion_venta";
        ParametrosDeComando.Agregar(comando, venta.IdTenant);
        ParametrosDeComando.Agregar(comando, venta.IdEmpresa);
        ParametrosDeComando.Agregar(comando, idComprobanteVenta);
        ParametrosDeComando.Agregar(comando, EtiquetaDeMotivo(decision.Motivo));
        ParametrosDeComando.Agregar(comando, JsonSerializer.Serialize(decision.Aportes, OpcionesDeAportes));
        ParametrosDeComando.Agregar(comando, periodo);
        ParametrosDeComando.Agregar(comando, ahora);

        return await comando.ExecuteScalarAsync(ct) is int id ? id : null;
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
