using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
/// </summary>
public class ServicioDeLotes(IWaysDbContext db, IRelojDelSistema reloj)
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
        var sugerido = ReglaDeLotes.ElegirFefo(saldos);

        var hoy = DateOnly.FromDateTime(reloj.Ahora.UtcDateTime);
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
