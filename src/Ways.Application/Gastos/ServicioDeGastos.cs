using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.CuentaCorriente;
using Ways.Domain.Common;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;

namespace Ways.Application.Gastos;

/// <summary>
/// Captura de gastos contra un turno abierto (design: Table Shapes — write path C; tasks.md
/// Slice 3). Reutiliza <see cref="ServicioDeTurnos.ResolverTurnoAbiertoAsync"/> (tasks.md,
/// Orchestrator Decision 3) en vez de escribir su propia consulta de turno abierto — mismo
/// criterio que <c>ServicioDeVentas.EmitirAsync</c> (Slice 5).
///
/// stage-8-compras-transferencias-inventario, Slice 4 (design decisión 7): cuando la solicitud
/// trae <c>idComprobanteCompra</c>, un <c>SELECT ... FOR SHARE</c> crudo sobre el header de la
/// compra — DESPUÉS del lock de turno existente — cierra el TOCTOU contra una anulación
/// concurrente de la misma compra (el mismo statement crudo/sibling raw SQL que
/// <c>ServicioDeCompras</c>, ver su doc-comment de clase).
/// </summary>
public class ServicioDeGastos(
    IWaysDbContext db, ServicioDeTurnos servicioDeTurnos, IRelojDelSistema reloj, IContextoDeUsuario contexto)
{
    /// <summary>Resuelve el punto de venta (404 ADR-8) antes que el turno abierto (spec: Gasto
    /// Requires An Open Turno) — mismo orden que <c>ServicioDeVentas.EmitirAsync</c> (design
    /// decisión 11): un punto de venta apócrifo tiene que dar 404, nunca el 409 de "sin turno
    /// abierto" de un punto de venta que ni siquiera existe.</summary>
    public async Task<GastoRegistrado> RegistrarAsync(SolicitudDeGasto solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        ExigirImporteValido(solicitud.Importe);
        // spec: gastos / A Comprobante Compra Link Requires Categoria Proveedor — "rejected
        // before reaching the database": chequeo de dominio puro, ANTES de cualquier consulta.
        ExigirCategoriaCoherenteConLaCompra(solicitud.Categoria, solicitud.IdComprobanteCompra);

        await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);
        var turno = await servicioDeTurnos.ResolverTurnoAbiertoAsync(solicitud.IdPuntoVenta, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        var gasto = await estrategia.ExecuteAsync(async () =>
            await InsertarGastoAsync(idTenant, turno.Id, solicitud, idEmpleado, momento, ct));

        return Proyectar(gasto);
    }

    /// <summary>Historial paginado (design: API Surface, <c>GET /api/gastos</c>) — mismo criterio
    /// de paginado que <c>ServicioDeTurnos.ListarAsync</c>.</summary>
    public async Task<PaginaDeGastos> ListarAsync(
        int? idPuntoVenta = null,
        DateTimeOffset? desde = null,
        DateTimeOffset? hasta = null,
        int pagina = 1,
        int tamanio = 25,
        CancellationToken ct = default)
    {
        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        var query = db.Gastos.AsQueryable();

        if (idPuntoVenta is { } pv)
        {
            query = query.Where(g => g.IdPuntoVenta == pv);
        }

        if (desde is { } d)
        {
            query = query.Where(g => g.Fecha >= d);
        }

        if (hasta is { } h)
        {
            query = query.Where(g => g.Fecha <= h);
        }

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(g => g.Fecha)
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .Select(g => new GastoListado(g.Id, g.IdPuntoVenta, g.Fecha, g.Categoria, g.IdMedioPago, g.Importe))
            .ToListAsync(ct);

        return new PaginaDeGastos(items, total, pagina, tamanio);
    }

    // ---- validación de dominio -------------------------------------------------------------

    /// <summary>Mismo código que la CHECK de esquema <c>ck_gastos_importe_positivo</c> (design:
    /// Backstop Map, Slice 1 task 1.7): esta validación de servicio es la UX rápida, la CHECK es
    /// el contrato real (db-error-backstops — nunca tratar el pre-check como la protección).
    /// (spec: Importe Must Be Positive).</summary>
    private static void ExigirImporteValido(decimal importe)
    {
        if (importe <= 0m)
        {
            throw new ErrorDominio("gasto_importe_invalido", "El importe del gasto tiene que ser positivo.", 400);
        }
    }

    /// <summary>spec: gastos / A Comprobante Compra Link Requires Categoria Proveedor — chequeo de
    /// dominio puro, sin DB: un gasto ligado a una compra SIEMPRE tiene que ser de categoría
    /// proveedor (la compra la paga un proveedor, nunca sueldos/viáticos/etc.).</summary>
    private static void ExigirCategoriaCoherenteConLaCompra(CategoriaGasto categoria, int? idComprobanteCompra)
    {
        if (idComprobanteCompra is not null && categoria != CategoriaGasto.Proveedor)
        {
            throw new ErrorDominio(
                "gasto_de_compra_debe_ser_de_proveedor",
                "Un gasto ligado a una compra tiene que ser de categoría proveedor.", 400);
        }
    }

    // ---- persistencia -------------------------------------------------------------------------

    /// <summary>task 4.17 (mismo guard que <c>ServicioDeTurnos.RegistrarMovimientoAsync</c>, reusado
    /// vía <see cref="ServicioDeTurnos.ExigirTurnoAbiertoBajoLockAsync"/> como PRIMER statement
    /// de esta transacción de escritura): el turno ya vino resuelto como abierto (<see
    /// cref="ServicioDeTurnos.ResolverTurnoAbiertoAsync"/>, arriba, ANTES de abrir esta
    /// transacción) — sin este re-chequeo bajo <c>FOR SHARE</c>, un gasto concurrente a un
    /// cierre podría comitear dentro de un turno cuyo arqueo YA se derivó (design decisión 1).
    ///
    /// Slice 4 (design decisión 7): el guard de la compra ligada corre DESPUÉS de ese lock de
    /// turno — mismo orden que el design pseudocódigo (Transactions — GASTO LIGADO A UNA
    /// COMPRA).</summary>
    private async Task<Gasto> InsertarGastoAsync(
        int idTenant, int idTurnoCaja, SolicitudDeGasto solicitud, int idEmpleado, DateTimeOffset momento,
        CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        await servicioDeTurnos.ExigirTurnoAbiertoBajoLockAsync(idTurnoCaja, ct);

        var idProveedor = solicitud.IdProveedor;
        if (solicitud.IdComprobanteCompra is { } idComprobanteCompra)
        {
            idProveedor = await ExigirCompraLigableAsync(idComprobanteCompra, idTenant, solicitud.IdProveedor, ct);
        }

        var gasto = new Gasto
        {
            IdTenant = idTenant,
            Fecha = momento,
            IdPuntoVenta = solicitud.IdPuntoVenta,
            IdTurnoCaja = idTurnoCaja,
            IdEmpleado = idEmpleado,
            Categoria = solicitud.Categoria,
            IdProveedor = idProveedor,
            IdArea = solicitud.IdArea,
            Concepto = solicitud.Concepto,
            Detalle = solicitud.Detalle,
            IdMedioPago = solicitud.IdMedioPago,
            NumeroFactura = solicitud.NumeroFactura,
            Importe = solicitud.Importe,
            IdComprobanteCompra = solicitud.IdComprobanteCompra,
            CreatedAt = momento,
            UpdatedAt = momento
        };

        db.Gastos.Add(gasto);
        await db.SaveChangesAsync(ct);

        // stage-15-cc-proveedores-ledger, Slice 3 (design decisión 7, tasks.md task 3.1): el
        // movimiento `pago` va DESPUÉS de SaveChangesAsync — id_gasto es identity, recién existe
        // acá — y es el ÚLTIMO lock de fila (for update) antes del commit. El predicado es
        // ServicioDeSaldoDeProveedor.cs:39-43 VERBATIM (la fórmula retirada que este ledger
        // reemplaza): categoría proveedor Y id_proveedor no nulo. `idProveedor` ya es el valor
        // RESUELTO (derivado por ExigirCompraLigableAsync cuando la solicitud no lo trae) — el
        // movimiento usa el mismo valor que la fila guarda, nunca el crudo de la solicitud.
        if (solicitud.Categoria == CategoriaGasto.Proveedor && idProveedor is { } idProveedorDelPago)
        {
            await EscribirPagoAProveedorAsync(idTenant, idProveedorDelPago, solicitud, gasto, idEmpleado, momento, ct);
        }

        await transaccion.CommitAsync(ct);

        return gasto;
    }

    /// <summary>stage-15-cc-proveedores-ledger, Slice 3: el ÚNICO call site de
    /// <see cref="EscriturasDeCuentaCorrienteProveedor"/> para pagos — <c>importe = −gasto.Importe</c>
    /// (el pago REDUCE el saldo), <c>id_gasto</c> = la fila recién flusheada,
    /// <c>id_comprobante_compra</c> = el vínculo del gasto (puede ser null: la imputación es
    /// opcional, spec: An Unlinked Proveedor Gasto Reduces The Saldo Without Imputación). Misma
    /// conexión/transacción cruda que <see cref="ExigirCompraLigableAsync"/> reutiliza — nunca una
    /// segunda transacción.</summary>
    private async Task EscribirPagoAProveedorAsync(
        int idTenant, int idProveedor, SolicitudDeGasto solicitud, Gasto gasto, int idEmpleado,
        DateTimeOffset momento, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        var nuevoSaldo = await EscriturasDeCuentaCorrienteProveedor.ActualizarSaldoProveedorAsync(
            conexion, transaccionCruda, idTenant, idProveedor, -solicitud.Importe, ct);

        await EscriturasDeCuentaCorrienteProveedor.InsertarMovimientoCcProveedorAsync(
            conexion, transaccionCruda, idTenant, idProveedor, momento, solicitud.IdPuntoVenta, idEmpleado,
            TipoMovimientoCcProveedor.Pago, gasto.IdComprobanteCompra, gasto.Id, -solicitud.Importe, nuevoSaldo,
            detalle: null, ct);
    }

    /// <summary>design decisión 7: <c>SELECT ... FOR SHARE</c> crudo sobre el header de la compra
    /// — cierra el TOCTOU contra una anulación concurrente de la MISMA compra. La anulación toma
    /// el lock EXCLUSIVO del header como su propio primer statement (<c>ServicioDeCompras.
    /// MarcarAnuladaAsync</c>): este gasto o bien bloquea y retoma viendo <c>anulada</c> ya
    /// comiteada (<c>409 compra_anulada</c>), o gana la carrera y queda simplemente visible para
    /// la anulación que llega después — ambos estados representables, ninguno corrupto. <c>estado
    /// ::text</c> en vez del enum nativo, mismo criterio cauteloso que
    /// <see cref="ServicioDeTurnos.ExigirTurnoAbiertoBajoLockAsync"/>. Devuelve el
    /// <c>id_proveedor</c> de la compra: se usa para derivarlo cuando el request no lo trae (spec:
    /// gastos / A Comprobante Compra Link — <c>id_proveedor</c> ausente se deriva, distinto se
    /// rechaza).</summary>
    private async Task<int> ExigirCompraLigableAsync(
        int idComprobanteCompra, int idTenant, int? idProveedorSolicitado, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccionCruda;
        comando.CommandText =
            "SELECT estado::text, id_proveedor FROM comprobantes_compra " +
            "WHERE id_comprobante_compra = $1 AND id_tenant = $2 FOR SHARE";
        ParametrosDeComando.Agregar(comando, idComprobanteCompra);
        ParametrosDeComando.Agregar(comando, idTenant);

        await using var lector = await comando.ExecuteReaderAsync(ct);
        if (!await lector.ReadAsync(ct))
        {
            // ADR-8: mismo 404 para "no existe" y "es de otro tenant".
            throw ErrorDominio.NoEncontrado($"No existe la compra {idComprobanteCompra}.");
        }

        var estado = lector.GetString(0);
        var idProveedorDeLaCompra = lector.GetInt32(1);

        if (estado == "anulada")
        {
            throw new ErrorDominio("compra_anulada", "La compra ligada está anulada.", 409);
        }

        if (estado != "confirmada")
        {
            throw new ErrorDominio(
                "compra_no_confirmada", "La compra ligada todavía no está confirmada.", 409);
        }

        if (idProveedorSolicitado is { } solicitado && solicitado != idProveedorDeLaCompra)
        {
            throw new ErrorDominio(
                "proveedor_no_coincide_con_la_compra",
                "El proveedor indicado no coincide con el proveedor de la compra.", 400);
        }

        return idProveedorDeLaCompra;
    }

    private async Task<DbConnection> ObtenerConexionAbiertaAsync(CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();

        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        return conexion;
    }

    // ---- resolución interna ---------------------------------------------------------------

    private async Task<PuntoVenta> ResolverPuntoVentaAsync(int idPuntoVenta, CancellationToken ct) =>
        await db.PuntosVenta.FirstOrDefaultAsync(pv => pv.Id == idPuntoVenta, ct)
            // ADR-8: mismo 404 para "no existe" y "es de otro tenant" — mismo criterio que
            // ServicioDeTurnos.ResolverPuntoVentaAsync/ServicioDeStock.ResolverPuntoVentaAsync/
            // ServicioDeVentas.ResolverPuntoVentaAsync.
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            // OperacionDePos (capa de API) ya exige un actor de tenant — un actor de plataforma
            // (root) nunca llega hasta acá. Defensa en profundidad, mismo criterio que
            // ServicioDeTurnos.ExigirTenantDeLaSesion.
            ?? throw new InvalidOperationException(
                "ServicioDeGastos requiere un actor de tenant; OperacionDePos no admite plataforma.");

    // ---- proyecciones -----------------------------------------------------------------------

    private static GastoRegistrado Proyectar(Gasto gasto) => new(
        gasto.Id,
        gasto.IdTurnoCaja,
        gasto.IdPuntoVenta,
        gasto.Fecha,
        gasto.Categoria,
        gasto.IdProveedor,
        gasto.IdArea,
        gasto.Concepto,
        gasto.Detalle,
        gasto.IdMedioPago,
        gasto.NumeroFactura,
        gasto.Importe,
        gasto.IdEmpleado,
        gasto.IdComprobanteCompra);
}
