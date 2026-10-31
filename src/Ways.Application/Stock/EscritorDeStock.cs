using System.Data.Common;
using Ways.Application.Abstracciones;
using Ways.Domain.Stock;

namespace Ways.Application.Stock;

/// <summary>Un asiento del ledger de stock: el movimiento y la clave de stock que mueve. <see cref="IdSucursal"/> es la
/// clave del saldo; <see cref="IdPuntoVenta"/> es la caja que lo originó y solo queda en el movimiento.
/// <see cref="IdLote"/> presente mueve también el saldo del lote.</summary>
public sealed record AsientoDeStock(
    int IdTenant, int IdSucursal, int IdPuntoVenta, int IdArticulo, decimal Cantidad, MotivoStock Motivo, int IdEmpleado,
    DateTimeOffset CreadoEl)
{
    public int? IdLote { get; init; }
    public string? Observaciones { get; init; }
    public int? IdComprobanteVenta { get; init; }
    public int? IdComprobanteCompra { get; init; }
    public int? IdRemito { get; init; }
    public int? IdPuntoVentaDestino { get; init; }
    public int? IdSucursalDestino { get; init; }
}

/// <summary>Lo que dejó escrito un <see cref="AsientoDeStock"/>: el id del movimiento y los saldos posteriores del
/// agregado y, si el asiento tenía lote, del lote (los <c>RETURNING</c> de los upserts).</summary>
public readonly record struct AsientoAplicado(
    AsientoDeStock Asiento, int IdMovimiento, decimal CantidadAgregada, decimal? CantidadDelLote);

/// <summary>
/// El único escritor de <c>movimientos_stock</c> y de las cantidades de <c>stock</c> y <c>stock_lotes</c>. Los umbrales
/// de reposición (<c>minimo</c>, <c>reposicion</c>) los escribe <c>ServicioDeStock.EscribirMinimosAsync</c> con su propio
/// upsert, que puede crear la fila de <c>stock</c> en cero pero nunca toca una cantidad ni asienta un movimiento.
/// Statements crudos sobre la conexión y la transacción del llamador: no abre transacción propia ni decide la estrategia de reintento, que siguen
/// siendo de cada servicio.
///
/// Orden de locks, uno solo para todo el sistema: <c>(id_sucursal, id_articulo, id_lote NULLS FIRST)</c>. Cada upsert
/// toma el lock de su fila, y la fila agregada (lote nulo) va siempre antes que las de sus lotes. <see cref="AsentarAsync"/>
/// ordena sus asientos así; quien combine las primitivas sueltas tiene que respetar el mismo orden.
/// </summary>
public static class EscritorDeStock
{
    /// <summary>La sucursal del punto de venta, leída dentro de la transacción del llamador. Quien escribe un documento
    /// nuevo resuelve así la sucursal; una reversa usa la sucursal guardada en los movimientos originales.
    /// La lectura toma <c>FOR SHARE</c> sobre la fila del punto de venta hasta el commit: mover la caja a otra sucursal
    /// (<c>FOR UPDATE</c> sobre la misma fila) espera a los asientos en vuelo y los que llegan después leen la sucursal
    /// nueva, así que la sucursal que la baja o el movimiento miran vacía no recibe un asiento a mitad de camino. Varios
    /// escritores de la misma caja no se bloquean entre sí.</summary>
    public static async Task<int> ResolverSucursalAsync(
        DbConnection conexion, DbTransaction? transaccion, int idPuntoVenta, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText = "SELECT id_sucursal FROM puntos_venta WHERE id_punto_venta = $1 FOR SHARE";
        ParametrosDeComando.Agregar(comando, idPuntoVenta);

        var resultado = await comando.ExecuteScalarAsync(ct);
        return resultado is null or DBNull
            ? throw new InvalidOperationException($"El punto de venta {idPuntoVenta} no existe o no es visible.")
            : Convert.ToInt32(resultado);
    }

    /// <summary>Los elementos en el orden de locks del sistema.</summary>
    public static IEnumerable<T> EnOrdenDeLocks<T>(
        IEnumerable<T> elementos, Func<T, int> sucursal, Func<T, int> articulo, Func<T, int?> lote) =>
        elementos
            .OrderBy(sucursal)
            .ThenBy(articulo)
            .ThenBy(e => lote(e).HasValue)
            .ThenBy(e => lote(e) ?? 0);

    /// <summary>Escribe cada asiento en el orden de locks: su movimiento, el upsert del agregado y, con lote, el del
    /// lote. Devuelve los resultados en ese mismo orden.</summary>
    public static async Task<IReadOnlyList<AsientoAplicado>> AsentarAsync(
        DbConnection conexion, DbTransaction? transaccion, IEnumerable<AsientoDeStock> asientos, CancellationToken ct)
    {
        var aplicados = new List<AsientoAplicado>();

        foreach (var asiento in EnOrdenDeLocks(asientos, a => a.IdSucursal, a => a.IdArticulo, a => a.IdLote))
        {
            var idMovimiento = await InsertarMovimientoAsync(conexion, transaccion, asiento, ct);
            var agregada = await SumarAStockAsync(
                conexion, transaccion, asiento.IdTenant, asiento.IdSucursal, asiento.IdArticulo, asiento.Cantidad, ct);

            decimal? delLote = asiento.IdLote is { } idLote
                ? await SumarAStockDeLoteAsync(
                    conexion, transaccion, asiento.IdTenant, asiento.IdSucursal, asiento.IdArticulo, idLote,
                    asiento.Cantidad, ct)
                : null;

            aplicados.Add(new AsientoAplicado(asiento, idMovimiento, agregada, delLote));
        }

        return aplicados;
    }

    /// <summary>Solo la fila de <c>movimientos_stock</c>, sin tocar saldos. Devuelve su id.</summary>
    public static async Task<int> InsertarMovimientoAsync(
        DbConnection conexion, DbTransaction? transaccion, AsientoDeStock asiento, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO movimientos_stock " +
            "(id_tenant, id_articulo, id_sucursal, id_punto_venta, cantidad, motivo, id_empleado, observaciones, " +
            "id_comprobante_venta, id_comprobante_compra, id_remito, id_punto_venta_destino, id_sucursal_destino, " +
            "id_lote, creado_el) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15) " +
            "RETURNING id_movimiento";

        ParametrosDeComando.Agregar(comando, asiento.IdTenant);
        ParametrosDeComando.Agregar(comando, asiento.IdArticulo);
        ParametrosDeComando.Agregar(comando, asiento.IdSucursal);
        ParametrosDeComando.Agregar(comando, asiento.IdPuntoVenta);
        ParametrosDeComando.Agregar(comando, asiento.Cantidad);
        ParametrosDeComando.Agregar(comando, asiento.Motivo);
        ParametrosDeComando.Agregar(comando, asiento.IdEmpleado);
        ParametrosDeComando.AgregarNulo(comando, asiento.Observaciones);
        ParametrosDeComando.AgregarNulo(comando, asiento.IdComprobanteVenta);
        ParametrosDeComando.AgregarNulo(comando, asiento.IdComprobanteCompra);
        ParametrosDeComando.AgregarNulo(comando, asiento.IdRemito);
        ParametrosDeComando.AgregarNulo(comando, asiento.IdPuntoVentaDestino);
        ParametrosDeComando.AgregarNulo(comando, asiento.IdSucursalDestino);
        ParametrosDeComando.AgregarNulo(comando, asiento.IdLote);
        ParametrosDeComando.Agregar(comando, asiento.CreadoEl);

        var resultado = await comando.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("El INSERT de movimientos_stock no devolvió ninguna fila.");

        return Convert.ToInt32(resultado);
    }

    /// <summary>Suma <paramref name="delta"/> al agregado y devuelve el saldo posterior. El <c>RETURNING</c> es el
    /// chequeo de suficiencia de quien lo necesite: sin segunda lectura, sin TOCTOU.</summary>
    public static async Task<decimal> SumarAStockAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idSucursal, int idArticulo, decimal delta,
        CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO stock (id_articulo, id_sucursal, id_tenant, cantidad) " +
            "VALUES ($1, $2, $3, $4) " +
            "ON CONFLICT (id_articulo, id_sucursal) DO UPDATE " +
            "SET cantidad = stock.cantidad + EXCLUDED.cantidad " +
            "RETURNING cantidad";

        ParametrosDeComando.Agregar(comando, idArticulo);
        ParametrosDeComando.Agregar(comando, idSucursal);
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, delta);

        return await EscalarDecimalAsync(comando, "El upsert de stock no devolvió ninguna fila.", ct);
    }

    /// <summary>Suma <paramref name="delta"/> al saldo del lote y devuelve el saldo posterior.</summary>
    public static async Task<decimal> SumarAStockDeLoteAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idSucursal, int idArticulo, int idLote,
        decimal delta, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO stock_lotes (id_articulo, id_sucursal, id_lote, id_tenant, cantidad) " +
            "VALUES ($1, $2, $3, $4, $5) " +
            "ON CONFLICT (id_articulo, id_sucursal, id_lote) DO UPDATE " +
            "SET cantidad = stock_lotes.cantidad + EXCLUDED.cantidad " +
            "RETURNING cantidad";

        ParametrosDeComando.Agregar(comando, idArticulo);
        ParametrosDeComando.Agregar(comando, idSucursal);
        ParametrosDeComando.Agregar(comando, idLote);
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, delta);

        return await EscalarDecimalAsync(comando, "El upsert de stock_lotes no devolvió ninguna fila.", ct);
    }

    /// <summary>Upsert no-op (<c>SET cantidad = stock.cantidad</c>): crea la fila en cero si falta y toma su lock en el
    /// mismo statement, sin escribir ningún delta. Devuelve el saldo actual bajo lock.</summary>
    public static async Task<decimal> BloquearYCrearSiFaltaAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idSucursal, int idArticulo, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO stock (id_articulo, id_sucursal, id_tenant, cantidad) " +
            "VALUES ($1, $2, $3, 0) " +
            "ON CONFLICT (id_articulo, id_sucursal) DO UPDATE " +
            "SET cantidad = stock.cantidad " +
            "RETURNING cantidad";

        ParametrosDeComando.Agregar(comando, idArticulo);
        ParametrosDeComando.Agregar(comando, idSucursal);
        ParametrosDeComando.Agregar(comando, idTenant);

        return await EscalarDecimalAsync(comando, "El upsert no-op de stock no devolvió ninguna fila.", ct);
    }

    /// <summary>El mismo upsert no-op que <see cref="BloquearYCrearSiFaltaAsync"/>, sobre el saldo de un lote.</summary>
    public static async Task<decimal> BloquearYCrearSiFaltaDeLoteAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idSucursal, int idArticulo, int idLote,
        CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO stock_lotes (id_articulo, id_sucursal, id_lote, id_tenant, cantidad) " +
            "VALUES ($1, $2, $3, $4, 0) " +
            "ON CONFLICT (id_articulo, id_sucursal, id_lote) DO UPDATE " +
            "SET cantidad = stock_lotes.cantidad " +
            "RETURNING cantidad";

        ParametrosDeComando.Agregar(comando, idArticulo);
        ParametrosDeComando.Agregar(comando, idSucursal);
        ParametrosDeComando.Agregar(comando, idLote);
        ParametrosDeComando.Agregar(comando, idTenant);

        return await EscalarDecimalAsync(comando, "El upsert no-op de stock_lotes no devolvió ninguna fila.", ct);
    }

    /// <summary>La suma de los saldos de lote de un artículo en una sucursal, con cada fila bloqueada en orden
    /// ascendente de lote. Postgres no admite <c>FOR UPDATE</c> junto a una agregación: la subconsulta bloquea y la
    /// exterior suma.</summary>
    public static async Task<decimal> SumarLotesBajoLockAsync(
        DbConnection conexion, DbTransaction? transaccion, int idSucursal, int idArticulo, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT COALESCE(SUM(cantidad), 0) FROM (" +
            "  SELECT cantidad FROM stock_lotes WHERE id_articulo = $1 AND id_sucursal = $2 " +
            "  ORDER BY id_lote FOR UPDATE" +
            ") bloqueadas";

        ParametrosDeComando.Agregar(comando, idArticulo);
        ParametrosDeComando.Agregar(comando, idSucursal);

        return await EscalarDecimalAsync(comando, "La suma bajo lock de stock_lotes no devolvió ninguna fila.", ct);
    }

    private static async Task<decimal> EscalarDecimalAsync(DbCommand comando, string siNoHayFila, CancellationToken ct)
    {
        var resultado = await comando.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException(siNoHayFila);
        return Convert.ToDecimal(resultado);
    }
}
