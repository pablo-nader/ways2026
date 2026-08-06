using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Domain.Articulos;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;

namespace Ways.Application.Stock;

/// <summary>
/// Ajuste manual de stock (design decisión 10: dedicado, no una extensión de
/// <c>ServicioDeArticulos</c> — autorización admin-only y forma de escritura son propias de esta
/// operación, mismo criterio que <c>ServicioDeVentas</c>/<c>ServicioDeEscaneo</c>). El endpoint
/// (<c>Politicas.GestionDeCatalogo</c>) es quien bloquea al Vendedor — este servicio no repite
/// ese chequeo, mismo criterio que el resto de los ABM del proyecto (autorización vive en la capa
/// de API, nunca duplicada en Application).
/// </summary>
public class ServicioDeStock(IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto)
{
    public async Task<decimal> ObtenerCantidadAsync(int idPuntoVenta, int idArticulo, CancellationToken ct = default) =>
        await db.Stock
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == idPuntoVenta)
            .Select(s => s.Cantidad)
            .FirstOrDefaultAsync(ct);

    /// <summary>Design: API Surface — <c>POST /api/stock/ajustes</c>, <c>motivo = ajuste</c>, una
    /// única transacción (movimiento + upsert del caché, spec: Manual Ajuste Path Is
    /// Admin-Only).</summary>
    public async Task<decimal> AjustarAsync(SolicitudDeAjusteDeStock solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var cantidad = ExigirCantidadValida(solicitud.Cantidad);
        var observaciones = ExigirObservaciones(solicitud.Observaciones);

        // Pre-checks de existencia/tenant ANTES de la transacción (mismo criterio que
        // ServicioDeVentas: la referencia se valida sobre una lectura simple, nunca dejando que
        // el FK real de la base la rechace con un 500 crudo dentro del INSERT crudo de abajo).
        await ResolverArticuloAsync(solicitud.IdArticulo, ct);
        await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarAjusteAsync(
                idTenant, idEmpleado, solicitud.IdArticulo, solicitud.IdPuntoVenta, cantidad, observaciones, momento, ct));
    }

    private async Task<decimal> EjecutarAjusteAsync(
        int idTenant, int idEmpleado, int idArticulo, int idPuntoVenta, decimal cantidad, string observaciones,
        DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        await InsertarMovimientoStockAsync(
            conexion, transaccionCruda, idTenant, idArticulo, idPuntoVenta, cantidad, MotivoStock.Ajuste, idEmpleado,
            observaciones, momento, idComprobanteCompra: null, idPuntoVentaDestino: null, ct);

        var nuevaCantidad = await UpsertStockAsync(conexion, transaccionCruda, idTenant, idArticulo, idPuntoVenta, cantidad, ct);

        await transaccion.CommitAsync(ct);

        return nuevaCantidad;
    }

    // ---- transferencia entre puntos de venta (design: Transactions — TRANSFERENCIA; decisión 9) ----

    /// <summary>Design decisión 9: rechaza un artículo repetido en un mismo request, arma UN
    /// único orden ascendente sobre las <c>2N</c> claves <c>(id_articulo, id_punto_venta)</c> —
    /// nunca "todo el origen, después todo el destino" — y aplica cada una en ese orden. Ese
    /// orden total es lo que evita el deadlock contra una transferencia inversa simultánea
    /// (B→A) y contra un checkout en cualquiera de los dos puntos de venta (que ya ordena sus
    /// upserts de stock asc <c>id_articulo</c>).</summary>
    public async Task<ResultadoTransferencia> TransferirAsync(SolicitudDeTransferencia solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        // spec: "Same-PV transfer is rejected... before reaching the database" — chequeo puramente
        // en memoria, antes de cualquier consulta.
        if (solicitud.IdPuntoVentaOrigen == solicitud.IdPuntoVentaDestino)
        {
            throw new ErrorDominio(
                "transferencia_origen_igual_destino",
                "El origen y el destino de una transferencia tienen que ser puntos de venta distintos.",
                400);
        }

        var observaciones = ExigirObservaciones(solicitud.Observaciones);
        var lineas = ExigirLineasDeTransferenciaValidas(solicitud.Lineas);

        // Pre-checks de existencia/tenant ANTES de la transacción (mismo criterio que
        // AjustarAsync): ResolverPuntoVentaAsync da el mismo 404 para "no existe" y "es de otro
        // tenant" (ADR-8), tanto para origen como para destino.
        await ResolverPuntoVentaAsync(solicitud.IdPuntoVentaOrigen, ct);
        await ResolverPuntoVentaAsync(solicitud.IdPuntoVentaDestino, ct);

        foreach (var idArticulo in lineas.Select(l => l.IdArticulo).Distinct())
        {
            await ResolverArticuloAsync(idArticulo, ct);
        }

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarTransferenciaAsync(
                idTenant, idEmpleado, solicitud.IdPuntoVentaOrigen, solicitud.IdPuntoVentaDestino, lineas, observaciones,
                momento, ct));
    }

    private async Task<ResultadoTransferencia> EjecutarTransferenciaAsync(
        int idTenant, int idEmpleado, int idPuntoVentaOrigen, int idPuntoVentaDestino,
        IReadOnlyList<LineaDeTransferencia> lineas, string observaciones, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        var claves = ConstruirClavesOrdenadas(idPuntoVentaOrigen, idPuntoVentaDestino, lineas);
        var resultadosPorArticulo = new Dictionary<int, (decimal Origen, decimal Destino)>();

        foreach (var clave in claves)
        {
            await InsertarMovimientoStockAsync(
                conexion, transaccionCruda, idTenant, clave.IdArticulo, clave.IdPuntoVenta, clave.Delta,
                MotivoStock.Transferencia, idEmpleado, observaciones, momento,
                idComprobanteCompra: null, idPuntoVentaDestino: idPuntoVentaDestino, ct);

            var nueva = await UpsertStockAsync(conexion, transaccionCruda, idTenant, clave.IdArticulo, clave.IdPuntoVenta, clave.Delta, ct);

            // La RETURNING del upsert ES el chequeo de suficiencia (design decisión 5): sin
            // segunda consulta, sin TOCTOU. Back-office tightening (spec: Insufficient Origin
            // Stock Is Refused) — asimétrico a propósito respecto de una venta, que nunca bloquea.
            if (clave.Delta < 0m && nueva < 0m)
            {
                throw new ErrorDominio(
                    "stock_insuficiente_para_transferencia",
                    $"No hay stock suficiente del artículo {clave.IdArticulo} en el punto de venta de origen para transferir.",
                    409);
            }

            var previo = resultadosPorArticulo.TryGetValue(clave.IdArticulo, out var existente) ? existente : (Origen: 0m, Destino: 0m);
            resultadosPorArticulo[clave.IdArticulo] = clave.IdPuntoVenta == idPuntoVentaOrigen
                ? (nueva, previo.Destino)
                : (previo.Origen, nueva);
        }

        await transaccion.CommitAsync(ct);

        var lineasResultado = resultadosPorArticulo
            .OrderBy(kv => kv.Key)
            .Select(kv => new LineaTransferida(kv.Key, kv.Value.Origen, kv.Value.Destino))
            .ToList();

        return new ResultadoTransferencia(idPuntoVentaOrigen, idPuntoVentaDestino, lineasResultado);
    }

    private readonly record struct ClaveDeTransferencia(int IdArticulo, int IdPuntoVenta, decimal Delta);

    private static List<ClaveDeTransferencia> ConstruirClavesOrdenadas(
        int idPuntoVentaOrigen, int idPuntoVentaDestino, IReadOnlyList<LineaDeTransferencia> lineas) =>
        lineas
            .SelectMany(l => new[]
            {
                new ClaveDeTransferencia(l.IdArticulo, idPuntoVentaOrigen, -l.Cantidad),
                new ClaveDeTransferencia(l.IdArticulo, idPuntoVentaDestino, l.Cantidad)
            })
            .OrderBy(c => c.IdArticulo)
            .ThenBy(c => c.IdPuntoVenta)
            .ToList();

    // ---- conteo de inventario (design: Transactions — CONTEO DE INVENTARIO; decisión 10) ----------

    /// <summary>Design decisión 10: el cliente manda el TOTAL contado, nunca un delta. El delta
    /// se deriva del lado del servidor bajo el mismo lock de fila que <c>AjustarAsync</c> usa
    /// (el upsert no-op de <see cref="BloquearYCrearSiFaltaStockAsync"/>), así que un conteo
    /// nunca puede pisar una venta que corrió entre el conteo físico y el submit.</summary>
    public async Task<StockActual> ContarAsync(SolicitudDeConteo solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var contada = ExigirContadaValida(solicitud.Contada);
        var observaciones = ExigirObservaciones(solicitud.Observaciones);

        await ResolverArticuloAsync(solicitud.IdArticulo, ct);
        await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarConteoAsync(idTenant, idEmpleado, solicitud.IdPuntoVenta, solicitud.IdArticulo, contada, observaciones, momento, ct));
    }

    private async Task<StockActual> EjecutarConteoAsync(
        int idTenant, int idEmpleado, int idPuntoVenta, int idArticulo, decimal contada, string observaciones,
        DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        var actual = await BloquearYCrearSiFaltaStockAsync(conexion, transaccionCruda, idTenant, idArticulo, idPuntoVenta, ct);
        var delta = contada - actual;

        if (delta == 0m)
        {
            // spec: "Zero-Difference Conteo Writes No Ledger Row" — commit sin escribir nada,
            // que además evita ck_movimientos_stock_cantidad_no_cero (nunca lo alcanza).
            await transaccion.CommitAsync(ct);
            return new StockActual(idPuntoVenta, idArticulo, actual);
        }

        await InsertarMovimientoStockAsync(
            conexion, transaccionCruda, idTenant, idArticulo, idPuntoVenta, delta, MotivoStock.Inventario, idEmpleado,
            observaciones, momento, idComprobanteCompra: null, idPuntoVentaDestino: null, ct);

        var final = await UpsertStockAsync(conexion, transaccionCruda, idTenant, idArticulo, idPuntoVenta, delta, ct);

        if (final != contada)
        {
            // Defensa en profundidad (design decisión 5): bajo el lock tomado en el paso 1, esta
            // rama es inalcanzable en operación normal — nadie más pudo escribir esa fila entre
            // el lock y este upsert.
            throw new InvalidOperationException(
                $"El conteo de inventario produjo un resultado inconsistente: esperado {contada}, obtenido {final}.");
        }

        await transaccion.CommitAsync(ct);

        return new StockActual(idPuntoVenta, idArticulo, final);
    }

    /// <summary>Upsert no-op — <c>SET cantidad = stock.cantidad</c> — que crea la fila si falta
    /// (con <c>cantidad = 0</c>) Y toma el row lock en el mismo statement, sin escribir ningún
    /// delta todavía (design decisión 5: "the conteo uses the same primitive as a no-op upsert to
    /// create-if-missing and lock in one statement, then derives the delta").</summary>
    private static async Task<decimal> BloquearYCrearSiFaltaStockAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idArticulo, int idPuntoVenta, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO stock (id_articulo, id_punto_venta, id_tenant, cantidad) " +
            "VALUES ($1, $2, $3, 0) " +
            "ON CONFLICT (id_articulo, id_punto_venta) DO UPDATE " +
            "SET cantidad = stock.cantidad " +
            "RETURNING cantidad";

        AgregarParametro(comando, idArticulo);
        AgregarParametro(comando, idPuntoVenta);
        AgregarParametro(comando, idTenant);

        var resultado = await comando.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("El upsert no-op de stock no devolvió ninguna fila.");

        return Convert.ToDecimal(resultado);
    }

    // ---- statements crudos (misma convención que ServicioDeVentas) --------------------------------

    /// <summary><see cref="idComprobanteCompra"/>/<see cref="idPuntoVentaDestino"/> quedan en
    /// <c>null</c> para todo llamador de esta clase (Ajuste/Transferencia/Inventario) salvo
    /// <see cref="idPuntoVentaDestino"/> en <see cref="EjecutarTransferenciaAsync"/> —
    /// <c>id_comprobante_compra</c> nunca se escribe fuera de
    /// <c>ServicioDeCompras.ConfirmarAsync</c>/<c>AnularAsync</c> (Slice 2, doc-comment de
    /// <see cref="Ways.Domain.Stock.MovimientoStock.IdComprobanteCompra"/>); el parámetro se suma
    /// acá solo por simetría de firma con el statement gemelo de <c>ServicioDeCompras</c> (design:
    /// File Changes — "the two raw statements gain motivo/idComprobanteCompra/idPuntoVentaDestino
    /// parameters").</summary>
    private static async Task InsertarMovimientoStockAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idArticulo, int idPuntoVenta,
        decimal cantidad, MotivoStock motivo, int idEmpleado, string? observaciones, DateTimeOffset creadoEl,
        int? idComprobanteCompra, int? idPuntoVentaDestino, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO movimientos_stock " +
            "(id_tenant, id_articulo, id_punto_venta, cantidad, motivo, id_empleado, observaciones, " +
            "id_comprobante_compra, id_punto_venta_destino, creado_el) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)";

        AgregarParametro(comando, idTenant);
        AgregarParametro(comando, idArticulo);
        AgregarParametro(comando, idPuntoVenta);
        AgregarParametro(comando, cantidad);
        AgregarParametro(comando, motivo);
        AgregarParametro(comando, idEmpleado);
        AgregarParametroNulo(comando, observaciones);
        AgregarParametroNulo(comando, idComprobanteCompra);
        AgregarParametroNulo(comando, idPuntoVentaDestino);
        AgregarParametro(comando, creadoEl);

        await comando.ExecuteNonQueryAsync(ct);
    }

    private static async Task<decimal> UpsertStockAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idArticulo, int idPuntoVenta,
        decimal delta, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO stock (id_articulo, id_punto_venta, id_tenant, cantidad) " +
            "VALUES ($1, $2, $3, $4) " +
            "ON CONFLICT (id_articulo, id_punto_venta) DO UPDATE " +
            "SET cantidad = stock.cantidad + EXCLUDED.cantidad " +
            "RETURNING cantidad";

        AgregarParametro(comando, idArticulo);
        AgregarParametro(comando, idPuntoVenta);
        AgregarParametro(comando, idTenant);
        AgregarParametro(comando, delta);

        var resultado = await comando.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("El upsert de stock no devolvió ninguna fila.");

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

    // ---- validaciones -------------------------------------------------------------------------

    private async Task<Articulo> ResolverArticuloAsync(int idArticulo, CancellationToken ct) =>
        await db.Articulos.FirstOrDefaultAsync(a => a.Id == idArticulo, ct)
            // Mismo código que ServicioDeOfertas/ServicioDeVentas (referencia_invalida, 400): el
            // filtro de EF (+ RLS) ya deja invisible un artículo de otro tenant, así que "no
            // existe" y "es de otro tenant" caen en la misma rama.
            ?? throw new ErrorDominio("referencia_invalida", $"No existe el artículo {idArticulo}.", 400);

    private async Task<PuntoVenta> ResolverPuntoVentaAsync(int idPuntoVenta, CancellationToken ct) =>
        await db.PuntosVenta.FirstOrDefaultAsync(pv => pv.Id == idPuntoVenta, ct)
            // ADR-8: mismo 404 para "no existe" y "es de otro tenant" — mismo criterio que
            // ServicioDeVentas.ResolverPuntoVentaAsync.
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");

    private static decimal ExigirCantidadValida(decimal cantidad)
    {
        if (cantidad == 0)
        {
            throw new ErrorDominio("cantidad_de_ajuste_invalida", "La cantidad del ajuste no puede ser cero.", 400);
        }

        // Máximo 3 decimales (mismo código y criterio que ServicioDeVentas.ExigirLineasValidas —
        // doc 10: cantidad soporta fracción para UnidadVenta.Peso, pero sin precisión ilimitada).
        if (decimal.Round(cantidad, 3, MidpointRounding.AwayFromZero) != cantidad)
        {
            throw new ErrorDominio("cantidad_invalida", "La cantidad del ajuste admite hasta 3 decimales.", 400);
        }

        return cantidad;
    }

    private static string ExigirObservaciones(string? observaciones)
    {
        var limpio = observaciones?.Trim();

        if (string.IsNullOrEmpty(limpio))
        {
            throw new ErrorDominio(
                "observaciones_requeridas", "El ajuste manual de stock requiere una observación/motivo.", 400);
        }

        return limpio;
    }

    /// <summary>Design decisión 9: un artículo repetido en un mismo request se rechaza entero
    /// (spec: transferencias-de-stock — "articulo_repetido"), antes de resolver referencias o
    /// tocar la base.</summary>
    private static IReadOnlyList<LineaDeTransferencia> ExigirLineasDeTransferenciaValidas(
        IReadOnlyList<LineaDeTransferencia> lineas)
    {
        if (lineas is null || lineas.Count == 0)
        {
            throw new ErrorDominio(
                "transferencia_sin_lineas", "La transferencia no tiene líneas para procesar.", 400);
        }

        var repetida = lineas.GroupBy(l => l.IdArticulo).FirstOrDefault(g => g.Count() > 1);
        if (repetida is not null)
        {
            throw new ErrorDominio(
                "articulo_repetido", $"El artículo {repetida.Key} aparece más de una vez en la transferencia.", 400);
        }

        foreach (var linea in lineas)
        {
            if (linea.Cantidad <= 0)
            {
                throw new ErrorDominio(
                    "cantidad_de_transferencia_invalida", "La cantidad a transferir tiene que ser mayor a cero.", 400);
            }

            if (decimal.Round(linea.Cantidad, 3, MidpointRounding.AwayFromZero) != linea.Cantidad)
            {
                throw new ErrorDominio(
                    "cantidad_de_transferencia_invalida", "La cantidad a transferir admite hasta 3 decimales.", 400);
            }
        }

        return lineas;
    }

    /// <summary>Design: New Domain codes — <c>contada_invalida</c>. <see cref="SolicitudDeConteo.Contada"/>
    /// es el total físicamente contado: nunca negativo, hasta 3 decimales (mismo listón que
    /// <see cref="ExigirCantidadValida"/>, sin el chequeo de "distinto de cero" — un conteo que
    /// confirma el cero actual es un no-op legítimo, spec: Zero-Difference Conteo).</summary>
    private static decimal ExigirContadaValida(decimal contada)
    {
        if (contada < 0)
        {
            throw new ErrorDominio("contada_invalida", "La cantidad contada no puede ser negativa.", 400);
        }

        if (decimal.Round(contada, 3, MidpointRounding.AwayFromZero) != contada)
        {
            throw new ErrorDominio("contada_invalida", "La cantidad contada admite hasta 3 decimales.", 400);
        }

        return contada;
    }

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            // GestionDeCatalogo (capa de API) ya exige un actor de tenant admin — un actor de
            // plataforma nunca llega hasta acá. Defensa en profundidad, no un camino alcanzable
            // en operación normal.
            ?? throw new InvalidOperationException(
                "ServicioDeStock requiere un actor de tenant; GestionDeCatalogo no admite plataforma.");
}
