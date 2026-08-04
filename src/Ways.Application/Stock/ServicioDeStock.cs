using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
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

        var estrategia = CrearEstrategiaSinReintento(db);
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

    /// <summary>El ajuste manual es una operación rara, humana y sin ninguna clave de
    /// idempotencia natural (a diferencia de <c>ServicioDeVentas.EmitirAsync</c>, que reintenta
    /// con seguridad porque detecta un commit ambiguo previo antes de reinsertar). Si
    /// <c>EnableRetryOnFailure</c> (global, <c>DependencyInjection</c>) reintentara esta
    /// transacción tras un commit ambiguo — el servidor comitea pero el ACK no llega antes de que
    /// se corte la conexión —, el reintento volvería a INSERTAR el mismo movimiento y a duplicar
    /// el ajuste sobre <c>stock</c>: mismo criterio que <c>ServicioDeVentas.AnularAsync</c>.
    ///
    /// <para><see cref="Microsoft.EntityFrameworkCore.Storage.NonRetryingExecutionStrategy"/>
    /// NO sirve acá: no hereda de <see cref="ExecutionStrategy"/> y por eso no marca el ambient
    /// <c>ExecutionStrategy.Current</c> que <c>BeginTransactionAsync</c> + una consulta EF dentro
    /// de esa transacción necesitan para no disparar "does not support user-initiated
    /// transactions" (la consulta resuelve su PROPIA estrategia reintentable desde la
    /// configuración del <c>DbContext</c>, que sigue siendo <c>NpgsqlRetryingExecutionStrategy</c>
    /// sin importar con qué se envolvió la llamada externa). El mecanismo sancionado por EF Core
    /// para optar por-operación fuera del retry global sin romper ese ambient tracking es
    /// subclasear <see cref="ExecutionStrategy"/> con <c>maxRetryCount: 0</c> — mismo tipo base
    /// que la estrategia reintentable, así que <c>Current</c> se sigue marcando igual.</para>
    ///
    /// Con esto, una falla transitoria llega tal cual al operador — el reintento manual del
    /// humano es el correcto acá, no uno automático y silencioso.</summary>
    private static IExecutionStrategy CrearEstrategiaSinReintento(IWaysDbContext db)
    {
        var dependencias = ((IInfrastructure<IServiceProvider>)db.Database).Instance
            .GetRequiredService<ExecutionStrategyDependencies>();
        return new EstrategiaSinReintento(dependencias);
    }

    /// <summary>Ver el doc-comment de <see cref="CrearEstrategiaSinReintento"/> — <c>maxRetryCount:
    /// 0</c> más <see cref="ShouldRetryOn"/> siempre <c>false</c> es "nunca reintentar", pero
    /// heredando de <see cref="ExecutionStrategy"/> (no de <c>NonRetryingExecutionStrategy</c>)
    /// para preservar el ambient tracking que EF Core necesita dentro de una transacción
    /// manual.</summary>
    private sealed class EstrategiaSinReintento(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 0, maxRetryDelay: TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => false;
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

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            // GestionDeCatalogo (capa de API) ya exige un actor de tenant admin — un actor de
            // plataforma nunca llega hasta acá. Defensa en profundidad, no un camino alcanzable
            // en operación normal.
            ?? throw new InvalidOperationException(
                "ServicioDeStock requiere un actor de tenant; GestionDeCatalogo no admite plataforma.");
}
