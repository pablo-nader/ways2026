using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;
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

        var estrategia = db.Database.CreateExecutionStrategy();
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
            conexion, transaccionCruda, idTenant, idArticulo, idPuntoVenta, cantidad, idEmpleado, observaciones, momento, ct);

        var nuevaCantidad = await UpsertStockAsync(conexion, transaccionCruda, idTenant, idArticulo, idPuntoVenta, cantidad, ct);

        await transaccion.CommitAsync(ct);

        return nuevaCantidad;
    }

    // ---- statements crudos (misma convención que ServicioDeVentas) --------------------------------

    private static async Task InsertarMovimientoStockAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idArticulo, int idPuntoVenta,
        decimal cantidad, int idEmpleado, string observaciones, DateTimeOffset creadoEl, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO movimientos_stock " +
            "(id_tenant, id_articulo, id_punto_venta, cantidad, motivo, id_empleado, observaciones, creado_el) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8)";

        AgregarParametro(comando, idTenant);
        AgregarParametro(comando, idArticulo);
        AgregarParametro(comando, idPuntoVenta);
        AgregarParametro(comando, cantidad);
        AgregarParametro(comando, MotivoStock.Ajuste);
        AgregarParametro(comando, idEmpleado);
        AgregarParametro(comando, observaciones);
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

    // ---- validaciones -------------------------------------------------------------------------

    private static decimal ExigirCantidadValida(decimal cantidad)
    {
        if (cantidad == 0)
        {
            throw new ErrorDominio("cantidad_de_ajuste_invalida", "La cantidad del ajuste no puede ser cero.", 400);
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

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            // GestionDeCatalogo (capa de API) ya exige un actor de tenant admin — un actor de
            // plataforma nunca llega hasta acá. Defensa en profundidad, no un camino alcanzable
            // en operación normal.
            ?? throw new InvalidOperationException(
                "ServicioDeStock requiere un actor de tenant; GestionDeCatalogo no admite plataforma.");
}
