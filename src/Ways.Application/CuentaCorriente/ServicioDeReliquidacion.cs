using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Precios;
using Ways.Domain.Clientes;
using Ways.Domain.Common;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Organizacion;

namespace Ways.Application.CuentaCorriente;

/// <summary>
/// Reliquidación a precio del día — el centro de la etapa (design: Technical Approach, "one
/// derivation, no second copy"). <see cref="PreviewAsync"/> y <see cref="EjecutarAsync"/> llaman a
/// la MISMA <see cref="ReliquidadorDeConsumos"/> con los MISMOS inputs; la única diferencia entre
/// los dos es que <see cref="EjecutarAsync"/> corre bajo el lock del cliente y escribe, mientras
/// <see cref="PreviewAsync"/> nunca lockea ni escribe (design: API Surface — "never authoritative").
///
/// Sin turno (design decisión 4, pinned): no mueve plata física, no aporta ningún término a
/// <c>CalculadorDeArqueo</c>. El PRIMER statement de la transacción de <see cref="EjecutarAsync"/>
/// es el lock del cliente — el escaneo de elegibles corre DESPUÉS, bajo ese lock, así que una
/// venta concurrente del mismo cliente se serializa contra esta corrida (design: Concurrency
/// guarantees).
/// </summary>
public class ServicioDeReliquidacion(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, LectorDeConsumosReliquidables lector,
    ServicioDePrecios servicioDePrecios)
{
    /// <summary>Preview — <c>GET</c>, sin lock, nunca autoritativo (design: API Surface). Un
    /// consumo que se marca ENTRE este preview y el commit siguiente simplemente deja de aparecer
    /// en el commit — no hay ninguna reserva ni "congelamiento" del resultado del preview.</summary>
    public async Task<ResultadoDeReliquidacion> PreviewAsync(int idCliente, CancellationToken ct = default)
    {
        var cliente = await ResolverClienteAsync(idCliente, ct);
        var momento = reloj.Ahora;

        var consumos = await lector.LeerElegiblesAsync(idCliente, ct);
        if (consumos.Count == 0)
        {
            return new ResultadoDeReliquidacion(0m, [], [], false);
        }

        var precioPorArticulo = await ResolverPreciosAsync(consumos, cliente.IdListaPrecio, momento, ct);
        return ReliquidadorDeConsumos.Calcular(consumos, precioPorArticulo);
    }

    /// <summary>Commit — design: Transactions, RELIQUIDACIÓN (8 pasos, orden pineado). La mitad
    /// que decide (cliente, punto de venta) corre AFUERA de la transacción de escritura — mismo
    /// criterio que el resto de la cuenta corriente.</summary>
    public async Task<ResultadoDeReliquidacion> EjecutarAsync(
        int idCliente, SolicitudDeReliquidacion solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        await ResolverClienteAsync(idCliente, ct);
        var puntoVenta = await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);

        // Sin reintento automático (mismo criterio que ServicioDeVentas.AnularAsync/
        // ServicioDeCuentaCorriente.RegistrarPagoAsync): una reliquidación es manual, sin clave de
        // idempotencia propia.
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarTransaccionAsync(idTenant, idEmpleado, momento, idCliente, puntoVenta.Id, ct));
    }

    private async Task<ResultadoDeReliquidacion> EjecutarTransaccionAsync(
        int idTenant, int idEmpleado, DateTimeOffset momento, int idCliente, int idPuntoVenta, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        // 1. Lock del cliente — PRIMER statement, SIN turno (design decisión 4, pinned).
        var (_, idListaPrecio) = await BloquearClienteAsync(idTenant, idCliente, ct);

        // 2/3. Escaneo de elegibles + items — bajo el lock recién tomado (design: "scan runs
        // after it, inside the same transaction").
        var consumos = await lector.LeerElegiblesAsync(idCliente, ct);
        if (consumos.Count == 0)
        {
            // Cero elegibles ⇒ no-op limpio, sin escribir nada (spec: A Run With No Eligible
            // Consumos Is A No-Op).
            await transaccion.CommitAsync(ct);
            return new ResultadoDeReliquidacion(0m, [], [], false);
        }

        // 4. Precios vigentes en lote — NUNCA ServicioDeOfertas.ResolverAsync (design decisión 3:
        // reaplicar las ofertas de HOY sería la inversión exacta de "el descuento se anula").
        var precioPorArticulo = await ResolverPreciosAsync(consumos, idListaPrecio, momento, ct);

        // 5. Cálculo puro — la MISMA fórmula que PreviewAsync (design: "never two formulas").
        var resultado = ReliquidadorDeConsumos.Calcular(consumos, precioPorArticulo);

        if (resultado.Delta == 0m)
        {
            // Zero delta ⇒ no-op: COMMIT sin escribir nada (design: The Re-Pricing Derivation —
            // "the same consumos are re-evaluated against the prices of the day the client
            // actually pays"). La lista de cubiertos del calculador refleja lo PROCESADO, no lo
            // MARCADO — acá no se escribe ningún marcador, así que la respuesta tiene que reflejar
            // la DB real: sin cubiertos.
            await transaccion.CommitAsync(ct);
            return resultado with { IdsMovimientosCubiertos = [] };
        }

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        // 6. Saldo — el mismo UPDATE ... RETURNING que el resto de la cuenta corriente.
        var nuevoSaldo = await EscriturasDeCuentaCorriente.ActualizarSaldoClienteAsync(
            conexion, transaccionCruda, idTenant, idCliente, resultado.Delta, ct);

        // 7. Movimiento — un único ActualizacionPrecios; id_comprobante_venta/id_pago_comprobante
        // NULL (no lo origina un comprobante puntual, sino la corrida completa).
        var detalle = JsonSerializer.Serialize(resultado.Detalle);
        var idMovimiento = await EscriturasDeCuentaCorriente.InsertarMovimientoCcAsync(
            conexion, transaccionCruda, idTenant, idCliente, momento, idPuntoVenta, idEmpleado,
            TipoMovimientoCc.ActualizacionPrecios, idComprobanteVenta: null, idPagoComprobante: null, resultado.Delta,
            nuevoSaldo, detalle, ct);

        // 8. Marcador — self-FK sobre cada consumo cubierto (design decisión 2).
        var filasMarcadas = await MarcarConsumosCubiertosAsync(
            conexion, transaccionCruda, idTenant, resultado.IdsMovimientosCubiertos, idMovimiento, ct);

        if (filasMarcadas != resultado.IdsMovimientosCubiertos.Count)
        {
            // Defensa en profundidad (design: paso 8 — "rowcount ≠ |ids| ⇒ throw, imposible bajo
            // el lock"): bajo el lock del cliente ningún otro escritor puede haber tocado el
            // marcador de estos consumos entre el escaneo (paso 2) y este UPDATE.
            throw new InvalidOperationException(
                $"El marcador de reliquidación afectó {filasMarcadas} filas, se esperaban " +
                $"{resultado.IdsMovimientosCubiertos.Count} — invariante de escritura violado.");
        }

        await transaccion.CommitAsync(ct);
        return resultado;
    }

    // ---- Resolución de datos --------------------------------------------------------------------

    private async Task<IReadOnlyDictionary<int, decimal?>> ResolverPreciosAsync(
        IReadOnlyList<ConsumoAReliquidar> consumos, int idListaPrecio, DateTimeOffset momento, CancellationToken ct)
    {
        var idsArticulo = consumos
            .SelectMany(c => c.Lineas)
            .Where(l => l.IdArticulo is not null)
            .Select(l => l.IdArticulo!.Value)
            .Distinct()
            .ToList();

        if (idsArticulo.Count == 0)
        {
            return new Dictionary<int, decimal?>();
        }

        var precios = await servicioDePrecios.PreciosVigentesEnLoteAsync(idsArticulo, [idListaPrecio], momento, ct);
        return precios.ToDictionary(kv => kv.Key.IdArticulo, kv => kv.Value);
    }

    private async Task<(decimal Saldo, int IdListaPrecio)> BloquearClienteAsync(
        int idTenant, int idCliente, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccionCruda;
        comando.CommandText = "SELECT saldo, id_lista_precio FROM clientes WHERE id_cliente = $1 AND id_tenant = $2 FOR UPDATE";

        AgregarParametro(comando, idCliente);
        AgregarParametro(comando, idTenant);

        await using var lector = await comando.ExecuteReaderAsync(ct);
        if (!await lector.ReadAsync(ct))
        {
            throw new InvalidOperationException(
                $"El cliente {idCliente} no existe bajo el lock — invariante violado (ya se validó su existencia fuera de la transacción).");
        }

        var saldo = lector.GetFieldValue<decimal>(0);
        var idListaPrecio = lector.GetInt32(1);
        return (saldo, idListaPrecio);
    }

    /// <summary>Paso 8 — self-FK sobre cada consumo cubierto (design decisión 2). El
    /// <c>WHERE id_movimiento_actualizacion IS NULL</c> es defensa en profundidad: bajo el lock
    /// del cliente ningún otro escritor puede haber marcado estos consumos entre el escaneo y
    /// este UPDATE, así que en operación normal nunca reduce el rowcount por sí solo.</summary>
    private static async Task<int> MarcarConsumosCubiertosAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, IReadOnlyList<int> idsMovimiento,
        int idMovimientoActualizacion, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "UPDATE movimientos_cuenta_corriente SET id_movimiento_actualizacion = $1 " +
            "WHERE id_movimiento = ANY($2) AND id_tenant = $3 AND id_movimiento_actualizacion IS NULL";

        AgregarParametro(comando, idMovimientoActualizacion);
        AgregarParametro(comando, idsMovimiento.ToArray());
        AgregarParametro(comando, idTenant);

        return await comando.ExecuteNonQueryAsync(ct);
    }

    private async Task<Cliente> ResolverClienteAsync(int idCliente, CancellationToken ct)
    {
        var cliente = await db.Clientes.FirstOrDefaultAsync(c => c.Id == idCliente, ct)
            // ADR-8: mismo 404 para "no existe" y "es de otro tenant" — mismo criterio que
            // ServicioDeCuentaCorriente.ResolverClienteAsync.
            ?? throw ErrorDominio.NoEncontrado($"No existe el cliente {idCliente}.");

        if (cliente.EsConsumidorFinal)
        {
            throw new ErrorDominio(
                "cliente_sin_cuenta_corriente", "El Consumidor Final no tiene cuenta corriente.", 400);
        }

        return cliente;
    }

    private async Task<PuntoVenta> ResolverPuntoVentaAsync(int idPuntoVenta, CancellationToken ct) =>
        await db.PuntosVenta.FirstOrDefaultAsync(pv => pv.Id == idPuntoVenta, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");

    private async Task<DbConnection> ObtenerConexionAbiertaAsync(CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();

        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        return conexion;
    }

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            ?? throw new InvalidOperationException(
                "ServicioDeReliquidacion requiere un actor de tenant; SupervisionDeCuentaCorriente no admite plataforma.");

    private static void AgregarParametro(DbCommand comando, object valor)
    {
        var parametro = comando.CreateParameter();
        parametro.Value = valor;
        comando.Parameters.Add(parametro);
    }
}
