using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;

namespace Ways.Application.Stock;

/// <summary>
/// Slice 3 de stage-12-lotes-vencimientos: identidad de lote (get-or-create), saldos acotados
/// (design decisión 6) y el ABM admin de <c>POST /api/stock/lotes</c>. Los tres escritores reales
/// del stage (<c>ServicioDeVentas</c>/<c>ServicioDeCompras</c>/<c>ServicioDeStock</c>, slices
/// 5-10) consumen <see cref="ResolverOCrearAsync"/>/<see cref="ResolverSinIdentificarAsync"/>/
/// <see cref="LeerSaldosAsync"/> por su propia conexión/transacción — esta clase no abre
/// transacción propia para esos tres métodos, mismo criterio que
/// <c>ServicioDeStock.InsertarMovimientoStockAsync</c>/<c>UpsertStockAsync</c> (statements
/// crudos, sin dueño de la transacción).
///
/// Slice 4 agrega <see cref="ReconciliarAsync"/> (design decisión 13/14): acá SÍ es dueña de su
/// propia transacción, UNA POR PAR <c>(articulo, punto de venta)</c> — nunca una transacción para
/// todo el lote de pares (decisión 13: un flip tenant-wide no puede retener locks de stock de
/// todas las PV de un artículo mientras alguien está cobrando).
/// </summary>
public class ServicioDeLotes(IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto)
{
    // ---- identidad de lote (design decisión 4/5) -----------------------------------------------

    /// <summary>Get-or-create sobre <c>ux_lotes_articulo_codigo</c> (design decisión 4):
    /// <c>INSERT ... ON CONFLICT (id_tenant, id_articulo, codigo) WHERE deleted_at IS NULL DO
    /// UPDATE ... RETURNING</c> — nunca <c>DO NOTHING</c>. La <c>RETURNING</c> toma el lock en el
    /// mismo statement, así que el chequeo de inmutabilidad de <c>fecha_vencimiento</c> corre BAJO
    /// ese lock, sin una segunda lectura (sin retry-read loop, la ventana TOCTOU que
    /// <c>DO NOTHING</c> + <c>SELECT</c> hubiera dejado abierta).</summary>
    public static async Task<int> ResolverOCrearAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idArticulo, string? codigo,
        DateOnly fechaVencimiento, DateTimeOffset momento, CancellationToken ct)
    {
        var codigoResuelto = string.IsNullOrWhiteSpace(codigo) ? ReglaDeLotes.DerivarCodigo(fechaVencimiento) : codigo.Trim();

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO lotes (id_tenant, id_articulo, codigo, fecha_vencimiento, es_sin_identificar, " +
            "created_at, updated_at) VALUES ($1, $2, $3, $4, false, $5, $6) " +
            "ON CONFLICT (id_tenant, id_articulo, codigo) WHERE deleted_at IS NULL DO UPDATE " +
            "SET updated_at = lotes.updated_at " +
            "RETURNING id_lote, fecha_vencimiento";

        AgregarParametro(comando, idTenant);
        AgregarParametro(comando, idArticulo);
        AgregarParametro(comando, codigoResuelto);
        AgregarParametro(comando, fechaVencimiento);
        AgregarParametro(comando, momento);
        AgregarParametro(comando, momento);

        await using var lector = await comando.ExecuteReaderAsync(ct);
        if (!await lector.ReadAsync(ct))
        {
            throw new InvalidOperationException("El get-or-create de lote no devolvió ninguna fila.");
        }

        var idLote = lector.GetInt32(0);
        var fechaExistente = lector.IsDBNull(1) ? (DateOnly?)null : lector.GetFieldValue<DateOnly>(1);

        // Chequeo de inmutabilidad BAJO el lock que la RETURNING de arriba ya tomó (design
        // decisión 4; spec: "A Lot's Expiry Is Immutable Once Created") — nunca un retry-read.
        if (fechaExistente != fechaVencimiento)
        {
            throw new ErrorDominio(
                "lote_vencimiento_incompatible",
                $"El lote '{codigoResuelto}' del artículo {idArticulo} ya existe con otra fecha de vencimiento.",
                409);
        }

        return idLote;
    }

    /// <summary>Get-or-create del lote "sin identificar" (design decisión 5): mismo shape que
    /// <see cref="ResolverOCrearAsync"/>, sobre el MISMO índice/conflict target
    /// (<c>ux_lotes_articulo_codigo</c>) gracias al código reservado
    /// <see cref="ReglaDeLotes.CodigoSinIdentificar"/> — es lo que serializa la creación
    /// perezosa de este lote entre dos checkouts concurrentes del mismo artículo nunca recibido,
    /// en vez de dejar que la carrera caiga en <c>ux_lotes_sin_identificar</c> (exención
    /// documentada, sin camino de cliente que la dispare, task 1.21).</summary>
    public static async Task<int> ResolverSinIdentificarAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idArticulo, DateTimeOffset momento,
        CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO lotes (id_tenant, id_articulo, codigo, fecha_vencimiento, es_sin_identificar, " +
            "created_at, updated_at) VALUES ($1, $2, $3, NULL, true, $4, $5) " +
            "ON CONFLICT (id_tenant, id_articulo, codigo) WHERE deleted_at IS NULL DO UPDATE " +
            "SET updated_at = lotes.updated_at " +
            "RETURNING id_lote";

        AgregarParametro(comando, idTenant);
        AgregarParametro(comando, idArticulo);
        AgregarParametro(comando, ReglaDeLotes.CodigoSinIdentificar);
        AgregarParametro(comando, momento);
        AgregarParametro(comando, momento);

        var resultado = await comando.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("El get-or-create del lote sin identificar no devolvió ninguna fila.");

        return Convert.ToInt32(resultado);
    }

    // ---- saldos (design decisión 6) -------------------------------------------------------------

    /// <summary>UNA query, acotada por decisión 6 a "lotes físicamente presentes, más los que el
    /// cliente nombró": <c>lotes ⟕ stock_lotes</c> del punto de venta pedido, filtrado a
    /// <c>cantidad &lt;&gt; 0 OR es_sin_identificar OR id_lote IN (@lotesPedidos)</c>. Nunca
    /// devuelve TODOS los lotes históricos del artículo (un yogurt recibido semanalmente durante
    /// tres años son 150 filas por línea de carrito en el camino más caliente del sistema).</summary>
    public async Task<IReadOnlyList<SaldoDeLote>> LeerSaldosAsync(
        int idPuntoVenta, IReadOnlyList<int> idsArticulo, IReadOnlyList<int> idsLotePedidos, CancellationToken ct)
    {
        if (idsArticulo.Count == 0)
        {
            return [];
        }

        var saldos =
            from lote in db.Lotes
            where idsArticulo.Contains(lote.IdArticulo)
            join stockLote in db.StockLotes.Where(sl => sl.IdPuntoVenta == idPuntoVenta)
                on lote.Id equals stockLote.IdLote into stockLotesDelLote
            from stockLote in stockLotesDelLote.DefaultIfEmpty()
            where (stockLote != null && stockLote.Cantidad != 0m)
                  || lote.EsSinIdentificar
                  || idsLotePedidos.Contains(lote.Id)
            select new SaldoDeLote(
                lote.IdArticulo, lote.Id, lote.Codigo, lote.EsSinIdentificar, lote.FechaVencimiento,
                stockLote != null ? stockLote.Cantidad : 0m);

        return await saldos.ToListAsync(ct);
    }

    // ---- reconciliación (design decisión 13/14; spec lotes-y-vencimientos: Reclasificación
    // Reconciles Pre-Existing Stock Without Moving The Aggregate) ---------------------------------

    /// <summary>Reconcilia el stock preexistente hacia el lote sin identificar cuando el control
    /// efectivo de lote se activa para un par <c>(articulo, punto de venta)</c>. Alcance: si
    /// <paramref name="idArticulo"/>/<paramref name="idPuntoVenta"/> son <c>null</c>, se resuelve
    /// sobre TODOS los pares con fila de <c>stock</c> cuyo artículo tiene <c>controla_lote = true</c>
    /// Y cuyo punto de venta pertenece a una empresa con <c>lotes_habilitado</c> efectivo
    /// <c>true</c> — el mismo AND que <see cref="Domain.Stock.ReglaDeLotes.ControlEfectivo"/>, acá
    /// resuelto por SQL en vez de en memoria porque el conjunto puede cubrir varias empresas.
    /// Idempotente por par (design decisión 13): un re-run amplio nunca reescribe un par ya
    /// reconciliado, así que los dos disparadores automáticos (<c>ServicioDeArticulos</c>,
    /// <c>ServicioDeParametros</c>) pueden pasar un alcance más ancho que el estrictamente
    /// necesario sin ningún costo de corrección, solo de trabajo redundante (ambos no-ops).</summary>
    public async Task<ResultadoDeReconciliacion> ReconciliarAsync(int? idArticulo, int? idPuntoVenta, CancellationToken ct)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        if (idArticulo is { } idArticuloPedido)
        {
            await ResolverArticuloAsync(idArticuloPedido, ct);
        }

        if (idPuntoVenta is { } idPuntoVentaPedido)
        {
            await ResolverPuntoVentaAsync(idPuntoVentaPedido, ct);
        }

        // Una fila de stock ya identifica un único (articulo, punto_venta) por PK — el join con
        // puntos_venta agrega el id_empresa sin duplicar filas, sin necesitar Distinct().
        var pares = await (
            from stock in db.Stock
            where (idArticulo == null || stock.IdArticulo == idArticulo)
               && (idPuntoVenta == null || stock.IdPuntoVenta == idPuntoVenta)
            join articulo in db.Articulos on stock.IdArticulo equals articulo.Id
            where articulo.ControlaLote
            join puntoVenta in db.PuntosVenta on stock.IdPuntoVenta equals puntoVenta.Id
            orderby stock.IdArticulo, stock.IdPuntoVenta
            select new { stock.IdArticulo, stock.IdPuntoVenta, puntoVenta.IdEmpresa })
            .ToListAsync(ct);

        if (pares.Count == 0)
        {
            return new ResultadoDeReconciliacion(ParesReconciliados: 0, ParesSinResiduo: 0);
        }

        // "lotes_habilitado" resuelto por empresa/PV (ADR-13), traído en un solo query para las
        // empresas involucradas — nunca N+1 por par.
        var idsEmpresa = pares.Select(p => p.IdEmpresa).Distinct().ToList();
        var candidatosLotesHabilitado = await db.Parametros
            .Where(p => p.Clave == ParametroConocido.LotesHabilitado.Clave && idsEmpresa.Contains(p.IdEmpresa))
            .ToListAsync(ct);

        var estrategia = db.Database.CreateExecutionStrategy();
        var reconciliados = 0;
        var sinResiduo = 0;

        foreach (var par in pares)
        {
            var candidatosDeEmpresa = candidatosLotesHabilitado
                .Where(p => p.IdEmpresa == par.IdEmpresa && (p.IdPuntoVenta == null || p.IdPuntoVenta == par.IdPuntoVenta))
                .ToList();
            var lotesHabilitadoJson = ResolucionDeParametros.Resolver(
                ParametroConocido.LotesHabilitado.Clave, candidatosDeEmpresa, par.IdPuntoVenta);

            if (!JsonSerializer.Deserialize<bool>(lotesHabilitadoJson))
            {
                continue;
            }

            var escribioAlgo = await estrategia.ExecuteAsync(()
                => ReconciliarParAsync(idTenant, idEmpleado, par.IdArticulo, par.IdPuntoVenta, momento, ct));

            if (escribioAlgo)
            {
                reconciliados++;
            }
            else
            {
                sinResiduo++;
            }
        }

        return new ResultadoDeReconciliacion(reconciliados, sinResiduo);
    }

    /// <summary>Design decisión 13, los seis pasos, UNA transacción por par. Design decisión 14:
    /// <c>stock</c> jamás se toca — el par de <c>movimientos_stock</c> suma cero por construcción,
    /// así que <c>stock.cantidad = SUM(movimientos)</c> se sostiene sin ningún upsert de la caché
    /// agregada. Devuelve <c>true</c> si escribió el par (residuo ≠ 0), <c>false</c> si el par ya
    /// estaba reconciliado (residuo 0, commit sin escribir nada — spec: "A second reconciliation
    /// run is a no-op").</summary>
    private async Task<bool> ReconciliarParAsync(
        int idTenant, int idEmpleado, int idArticulo, int idPuntoVenta, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        // 1. lotes ANTES de stock (design decisión 3): el sin-identificar se resuelve primero,
        // nunca después de tomar el lock de la fila agregada.
        var idSinIdentificar = await ResolverSinIdentificarAsync(conexion, transaccionCruda, idTenant, idArticulo, momento, ct);

        // 2. fila agregada, PRIMERO dentro del segundo tier (design decisión 3/8) — reusa el
        // upsert no-op de ContarAsync, mismo lock, mismo criterio de create-if-missing.
        var agregado = await ServicioDeStock.BloquearYCrearSiFaltaStockAsync(
            conexion, transaccionCruda, idTenant, idArticulo, idPuntoVenta, ct);

        // 3. SUM(stock_lotes) bajo lock, ascendente por id_lote — mismo orden global que el resto
        // de la etapa (subquery porque Postgres rechaza FOR UPDATE combinado con una agregación).
        var sumaLotes = await SumarStockLotesBajoLockAsync(conexion, transaccionCruda, idArticulo, idPuntoVenta, ct);

        // 4. residuo.
        var residuo = agregado - sumaLotes;

        if (residuo == 0m)
        {
            // 5. spec: "A second reconciliation run is a no-op" / "A zero-cantidad reclasificación
            // row never violates the non-zero CHECK" — commit sin escribir NADA. Design decisión
            // 13: la idempotencia acá no es una propiedad accesoria, es el mecanismo de
            // recuperación (un par que un crash dejó sin reconciliar, o que una venta concurrente
            // dejó negativo, se autocura en la próxima corrida).
            await transaccion.CommitAsync(ct);
            return false;
        }

        // 6. par neto cero, motivo = reclasificacion SIEMPRE (spec: "Reclasificación never uses
        // motivo ajuste"), stock_lotes del sin-identificar recibe el residuo — stock NO se toca
        // (decisión 14).
        await InsertarMovimientoReclasificacionAsync(
            conexion, transaccionCruda, idTenant, idArticulo, idPuntoVenta, -residuo, idEmpleado, idLote: null, momento, ct);
        await InsertarMovimientoReclasificacionAsync(
            conexion, transaccionCruda, idTenant, idArticulo, idPuntoVenta, residuo, idEmpleado, idSinIdentificar, momento, ct);

        await UpsertStockLoteAsync(conexion, transaccionCruda, idTenant, idArticulo, idPuntoVenta, idSinIdentificar, residuo, ct);

        await transaccion.CommitAsync(ct);
        return true;
    }

    /// <summary>Postgres no permite <c>FOR UPDATE</c> junto con una función de agregación en el
    /// mismo <c>SELECT</c> ("FOR UPDATE is not allowed with aggregate functions") — la subquery
    /// toma el lock fila por fila, ascendente por <c>id_lote</c> (mismo orden global que el resto
    /// de la etapa), y la agregación corre en la query exterior, ya sobre filas bloqueadas.</summary>
    private static async Task<decimal> SumarStockLotesBajoLockAsync(
        DbConnection conexion, DbTransaction? transaccion, int idArticulo, int idPuntoVenta, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT COALESCE(SUM(cantidad), 0) FROM (" +
            "  SELECT cantidad FROM stock_lotes WHERE id_articulo = $1 AND id_punto_venta = $2 " +
            "  ORDER BY id_lote FOR UPDATE" +
            ") bloqueadas";

        AgregarParametro(comando, idArticulo);
        AgregarParametro(comando, idPuntoVenta);

        var resultado = await comando.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("La suma bajo lock de stock_lotes no devolvió ninguna fila.");

        return Convert.ToDecimal(resultado);
    }

    /// <summary>Fila de <c>movimientos_stock</c> con <c>motivo = reclasificacion</c> fijo — el
    /// único motivo que este método escribe, nunca <c>ajuste</c> (spec: "Reclasificación never
    /// uses motivo ajuste"). Copia deliberada del statement de <c>ServicioDeStock</c> (no
    /// compartida): los frentes en paralelo de esta etapa viven en archivos distintos a propósito
    /// (design: Slicing — "Conflict surface between fronts").</summary>
    private static async Task InsertarMovimientoReclasificacionAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idArticulo, int idPuntoVenta,
        decimal cantidad, int idEmpleado, int? idLote, DateTimeOffset creadoEl, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO movimientos_stock " +
            "(id_tenant, id_articulo, id_punto_venta, cantidad, motivo, id_empleado, id_lote, creado_el) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8)";

        AgregarParametro(comando, idTenant);
        AgregarParametro(comando, idArticulo);
        AgregarParametro(comando, idPuntoVenta);
        AgregarParametro(comando, cantidad);
        AgregarParametro(comando, MotivoStock.Reclasificacion);
        AgregarParametro(comando, idEmpleado);
        AgregarParametroNulo(comando, idLote);
        AgregarParametro(comando, creadoEl);

        await comando.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Misma forma atómica que <c>ServicioDeStock.UpsertStockAsync</c>, una clave más
    /// (design: Write site 1 — "UpsertStockLoteAsync: la MISMA forma que UpsertStockAsync, una
    /// clave más"). Acá el único llamador es <see cref="ReconciliarParAsync"/>, siempre sobre el
    /// lote sin identificar.</summary>
    private static async Task<decimal> UpsertStockLoteAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idArticulo, int idPuntoVenta,
        int idLote, decimal delta, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO stock_lotes (id_articulo, id_punto_venta, id_lote, id_tenant, cantidad) " +
            "VALUES ($1, $2, $3, $4, $5) " +
            "ON CONFLICT (id_articulo, id_punto_venta, id_lote) DO UPDATE " +
            "SET cantidad = stock_lotes.cantidad + EXCLUDED.cantidad " +
            "RETURNING cantidad";

        AgregarParametro(comando, idArticulo);
        AgregarParametro(comando, idPuntoVenta);
        AgregarParametro(comando, idLote);
        AgregarParametro(comando, idTenant);
        AgregarParametro(comando, delta);

        var resultado = await comando.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("El upsert de stock_lotes no devolvió ninguna fila.");

        return Convert.ToDecimal(resultado);
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

    // ---- ABM admin (design: API Surface) ----------------------------------------------------------

    /// <summary><c>GET /api/stock/lotes</c> — feed del picker (design decisión 19): reusa la
    /// misma query acotada de <see cref="LeerSaldosAsync"/> (sin lotes pedidos explícitos, es un
    /// listado, no una validación) y agrega <c>estado</c>/<c>sugerido</c> server-side — el FEFO
    /// nunca se recomputa en TypeScript.</summary>
    public async Task<IReadOnlyList<LoteListado>> ListarAsync(int idPuntoVenta, int idArticulo, CancellationToken ct)
    {
        var puntoVenta = await ResolverPuntoVentaAsync(idPuntoVenta, ct);
        await ResolverArticuloAsync(idArticulo, ct);

        var saldos = await LeerSaldosAsync(idPuntoVenta, [idArticulo], [], ct);
        var ordenados = ReglaDeLotes.OrdenarFefo(saldos);

        // Honestidad documental: "hoy" acá es UTC naive (interino por diseño en este slice 3),
        // no la zona_horaria del PV. El reporte de vencimientos del slice 13 SÍ resuelve "hoy"
        // en la zona_horaria del PV (requisito vinculante del spec lotes-y-vencimientos) — este
        // picker admin no necesita esa precisión, mismo criterio de honestidad que
        // diasAlertaPorDefecto en CrearAsync.
        var hoy = DateOnly.FromDateTime(reloj.Ahora.UtcDateTime);

        // Decisión 15 (judgment-day del slice 7): el Sugerido del picker tiene que ser
        // consistente con la selección real del server (ServicioDeVentas) — mismo "hoy", mismo
        // ElegirFefo particionado por no-vencido primero.
        var sugerido = ReglaDeLotes.ElegirFefo(saldos, hoy);
        var diasAlerta = await ResolverDiasAlertaAsync(puntoVenta.IdEmpresa, idPuntoVenta, ct);

        return ordenados
            .Select(s => new LoteListado(
                s.IdLote, s.IdArticulo, s.Codigo, s.FechaVencimiento, s.EsSinIdentificar, s.Cantidad,
                ReglaDeLotes.Clasificar(s.FechaVencimiento, hoy, diasAlerta),
                Sugerido: sugerido is not null && sugerido.Value.IdLote == s.IdLote))
            .ToList();
    }

    /// <summary>Alta manual de un lote (design: API Surface, admin-only). A diferencia de
    /// <see cref="ResolverOCrearAsync"/> (get-or-create silencioso de los escritores de
    /// negocio), esta vía crea vía EF <c>SaveChangesAsync</c> — una colisión con
    /// <c>ux_lotes_articulo_codigo</c> es un genuino <c>409 lote_duplicado</c>
    /// (<c>ManejadorDeErrores</c>, task 1.16), nunca una reutilización silenciosa: un admin que
    /// da de alta un lote espera que un duplicado le avise, no que la operación se pierda contra
    /// una fila existente.</summary>
    public async Task<LoteListado> CrearAsync(SolicitudDeLote solicitud, CancellationToken ct)
    {
        await ResolverArticuloAsync(solicitud.IdArticulo, ct);

        var codigoNormalizado = string.IsNullOrWhiteSpace(solicitud.Codigo) ? null : solicitud.Codigo.Trim();

        // Task 3.4 (design decisión 5): el código reservado del lote sin identificar no se puede
        // dar de alta por esta vía — ese lote SOLO lo crea la reconciliación (slice 4), de forma
        // perezosa.
        if (codigoNormalizado is not null
            && string.Equals(codigoNormalizado, ReglaDeLotes.CodigoSinIdentificar, StringComparison.Ordinal))
        {
            throw new ErrorDominio(
                "codigo_de_lote_reservado",
                $"'{ReglaDeLotes.CodigoSinIdentificar}' es el código reservado del lote sin identificar; no se puede dar de alta a mano.",
                400);
        }

        var codigoResuelto = codigoNormalizado ?? ReglaDeLotes.DerivarCodigo(solicitud.FechaVencimiento);
        var ahora = reloj.Ahora;

        var lote = new Lote
        {
            IdArticulo = solicitud.IdArticulo,
            Codigo = codigoResuelto,
            FechaVencimiento = solicitud.FechaVencimiento,
            EsSinIdentificar = false
        };
        db.Lotes.Add(lote);
        await db.SaveChangesAsync(ct);

        // Honestidad documental: "hoy" acá es UTC naive (interino por diseño en este slice 3),
        // no la zona_horaria del PV — mismo criterio que ListarAsync. El reporte de
        // vencimientos del slice 13 SÍ resuelve "hoy" en la zona_horaria del PV (requisito
        // vinculante del spec lotes-y-vencimientos).
        var hoy = DateOnly.FromDateTime(ahora.UtcDateTime);

        // Sin punto de venta en el request (alta de catálogo, no de un movimiento): el horizonte
        // de alerta usa el default declarado, no una resolución por empresa — el reporte real
        // (slice 13) sí resuelve el override de la empresa/PV.
        var diasAlertaPorDefecto = JsonSerializer.Deserialize<int>(ParametroConocido.DiasAlertaVencimiento.ValorPorDefecto);

        return new LoteListado(
            lote.Id, lote.IdArticulo, lote.Codigo, lote.FechaVencimiento, lote.EsSinIdentificar, Cantidad: 0m,
            ReglaDeLotes.Clasificar(lote.FechaVencimiento, hoy, diasAlertaPorDefecto), Sugerido: false);
    }

    // ---- statements crudos (misma convención que ServicioDeStock/ServicioDeCompras) --------------

    private static void AgregarParametro(DbCommand comando, object valor)
    {
        var parametro = comando.CreateParameter();
        parametro.Value = valor;
        comando.Parameters.Add(parametro);
    }

    private static void AgregarParametroNulo(DbCommand comando, object? valor)
    {
        var parametro = comando.CreateParameter();
        parametro.Value = valor ?? DBNull.Value;
        comando.Parameters.Add(parametro);
    }

    // ---- validaciones ---------------------------------------------------------------------------

    private async Task<Articulo> ResolverArticuloAsync(int idArticulo, CancellationToken ct) =>
        await db.Articulos.FirstOrDefaultAsync(a => a.Id == idArticulo, ct)
            // Mismo criterio que ServicioDeStock.ResolverArticuloAsync: el filtro de EF (+ RLS)
            // ya deja invisible un artículo de otro tenant, "no existe" y "es de otro tenant"
            // caen en la misma rama.
            ?? throw new ErrorDominio("referencia_invalida", $"No existe el artículo {idArticulo}.", 400);

    private async Task<PuntoVenta> ResolverPuntoVentaAsync(int idPuntoVenta, CancellationToken ct) =>
        await db.PuntosVenta.FirstOrDefaultAsync(pv => pv.Id == idPuntoVenta, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            // Mismo criterio que ServicioDeStock.ExigirTenantDeLaSesion: GestionDeCatalogo (capa
            // de API) ya exige un actor de tenant admin para el endpoint de reconciliación; los
            // dos disparadores automáticos (ServicioDeArticulos/ServicioDeParametros) corren
            // dentro del mismo request autenticado que ya validó ese actor.
            ?? throw new InvalidOperationException(
                "ServicioDeLotes.ReconciliarAsync requiere un actor de tenant; GestionDeCatalogo no admite plataforma.");

    private async Task<int> ResolverDiasAlertaAsync(int idEmpresa, int idPuntoVenta, CancellationToken ct)
    {
        var candidatos = await db.Parametros
            .Where(p => p.Clave == ParametroConocido.DiasAlertaVencimiento.Clave && p.IdEmpresa == idEmpresa
                && (p.IdPuntoVenta == null || p.IdPuntoVenta == idPuntoVenta))
            .ToListAsync(ct);

        var valorJson = ResolucionDeParametros.Resolver(ParametroConocido.DiasAlertaVencimiento.Clave, candidatos, idPuntoVenta);
        return JsonSerializer.Deserialize<int>(valorJson);
    }
}
