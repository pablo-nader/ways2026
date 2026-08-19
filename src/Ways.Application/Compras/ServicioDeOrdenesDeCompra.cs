using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Ventas;
using Ways.Domain.Common;
using Ways.Domain.Compras;

namespace Ways.Application.Compras;

/// <summary>
/// stage-16-ordenes-de-compra, Slice 2 (design: Technical Approach, decisiones 6-7). Borrador CRUD
/// (replace-set, mismo criterio que <see cref="ServicioDeCompras"/>) + <see cref="EnviarAsync"/>
/// (numeración propia, serie <c>'OC'</c>, consumida al enviar). <c>cerrar</c>/<c>anular</c> llegan
/// en slice 4; la lectura paginada + el detalle con cobertura en slice 5; la ligadura con
/// <c>comprobantes_compra</c> en slice 3 — ninguno de esos caminos existe todavía.
///
/// OD9 (orquestador, apply de esta slice): los resolvers 404 de proveedor/punto de venta son
/// PRIVADOS y PROPIOS de esta clase — copian la FORMA exacta de
/// <see cref="ServicioDeCompras.ResolverProveedorAsync"/>/<c>ResolverPuntoVentaAsync</c> (ambos
/// <c>private</c> ahí, así que no hay nada que reusar por composición) en vez de promoverlos a un
/// helper compartido: mismo criterio que <c>ServicioDeGastos</c> ya aplica frente a
/// <c>ServicioDeCompras</c> para el mismo par de resoluciones.
/// </summary>
public class ServicioDeOrdenesDeCompra(IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto)
{
    // ---- borrador: crear + replace-set (mismo criterio que ServicioDeCompras) --------------------

    public async Task<OrdenDeCompraBorrador> CrearBorradorAsync(
        SolicitudDeOrdenDeCompra solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        await ResolverProveedorAsync(solicitud.IdProveedor, ct);
        await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);
        await ExigirArticulosExistentesAsync(solicitud.Items, ct);

        var orden = new OrdenCompra
        {
            IdTenant = idTenant,
            IdPuntoVenta = solicitud.IdPuntoVenta,
            IdProveedor = solicitud.IdProveedor,
            IdEmpleado = idEmpleado,
            Numero = null,
            FechaEmision = momento,
            FechaEsperada = solicitud.FechaEsperada,
            Observaciones = NormalizarOpcional(solicitud.Observaciones),
            Estado = EstadoOrdenCompra.Borrador,
            CreatedAt = momento,
            UpdatedAt = momento
        };
        db.OrdenesCompra.Add(orden);
        await db.SaveChangesAsync(ct);

        var items = MaterializarItems(orden.Id, idTenant, solicitud.Items, momento);
        db.ItemsOrdenCompra.AddRange(items);
        await db.SaveChangesAsync(ct);

        return Proyectar(orden, items);
    }

    /// <summary>Mismo criterio que <see cref="ServicioDeCompras.ActualizarBorradorAsync"/>:
    /// replace-set completo bajo <c>SELECT … FOR UPDATE … WHERE estado='borrador'</c> (mutation
    /// target #10) — el predicado de estado en el mismo statement hace que editar una orden ya
    /// enviada sea estructuralmente imposible. Reemplaza también <c>IdPuntoVenta</c> — esto es lo
    /// que habilita la carrera de mutation target #11 (task 2.16): un PUT concurrente puede mover
    /// la orden a otro punto de venta entre la pre-lectura y el lock de <see cref="EnviarAsync"/>.</summary>
    public async Task<OrdenDeCompraBorrador> ActualizarBorradorAsync(
        int id, SolicitudDeOrdenDeCompra solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var momento = reloj.Ahora;

        await ResolverProveedorAsync(solicitud.IdProveedor, ct);
        await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);
        await ExigirArticulosExistentesAsync(solicitud.Items, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarActualizacionAsync(id, idTenant, solicitud, momento, ct));
    }

    private async Task<OrdenDeCompraBorrador> EjecutarActualizacionAsync(
        int id, int idTenant, SolicitudDeOrdenDeCompra solicitud, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        var bloqueado = await BloquearBorradorAsync(conexion, transaccionCruda, id, idTenant, ct);
        if (!bloqueado)
        {
            var existe = await db.OrdenesCompra.AsNoTracking().AnyAsync(o => o.Id == id, ct);
            if (!existe)
            {
                throw ErrorDominio.NoEncontrado($"No existe la orden de compra {id}.");
            }

            throw new ErrorDominio(
                "orden_compra_no_editable", "Solo una orden de compra en borrador puede editarse.", 409);
        }

        // El lock de fila crudo de arriba ya serializa cualquier escritor concurrente sobre este
        // header — misma lógica que EjecutarActualizacionAsync de ServicioDeCompras.
        var orden = await db.OrdenesCompra.FirstAsync(o => o.Id == id, ct);

        var itemsExistentes = await db.ItemsOrdenCompra.Where(i => i.IdOrdenCompra == id).ToListAsync(ct);
        db.ItemsOrdenCompra.RemoveRange(itemsExistentes);

        orden.IdProveedor = solicitud.IdProveedor;
        orden.IdPuntoVenta = solicitud.IdPuntoVenta;
        orden.FechaEsperada = solicitud.FechaEsperada;
        orden.Observaciones = NormalizarOpcional(solicitud.Observaciones);
        orden.UpdatedAt = momento;

        var itemsNuevos = MaterializarItems(id, idTenant, solicitud.Items, momento);
        db.ItemsOrdenCompra.AddRange(itemsNuevos);

        await db.SaveChangesAsync(ct);
        await transaccion.CommitAsync(ct);

        return Proyectar(orden, itemsNuevos);
    }

    // ---- enviar: numeración propia consumida ANTES de la transacción (design decisión 6) --------

    /// <summary>design: Transactions — ENVIAR OC. El número (serie <c>'OC'</c>) se asigna y
    /// COMITEA en su propia transacción chica ANTES de abrir la que escribe la orden (mismo shape
    /// que <c>ServicioDeVentas.cs:278-280</c>) — <c>AsignadorDeNumeroComprobante.
    /// AsignarComprometidoAsync</c> no se toca. El <c>UPDATE</c> final pinea
    /// <c>id_punto_venta = $pv</c> (el capturado en la pre-lectura, ANTES del draw): 0 filas puede
    /// deberse a un doble-enviar (mutation target #12/#13, tasks 2.14-2.15) o a un <c>PUT</c>
    /// concurrente que movió la orden a otro punto de venta (mutation target #11, task 2.16) — en
    /// ambos casos el número YA se comiteó y queda quemado, residuo aceptado (design decisión
    /// 6).</summary>
    public async Task<OrdenDeCompraBorrador> EnviarAsync(int id, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var momento = reloj.Ahora;

        var preLectura = await db.OrdenesCompra.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);
        if (preLectura is null)
        {
            throw ErrorDominio.NoEncontrado($"No existe la orden de compra {id}.");
        }

        if (preLectura.Estado != EstadoOrdenCompra.Borrador)
        {
            throw new ErrorDominio(
                "orden_compra_no_enviable", "La orden de compra ya no está en borrador.", 409);
        }

        // Conflicto #3 (tasks.md, design decisión 7, Open Question T6): una orden sin items
        // proyectaría `cerrada` en la primera confirmación (la derivación de la slice 3 es
        // vacuamente `completa`) — se rechaza ACÁ, antes de gastar un número.
        var tieneItems = await db.ItemsOrdenCompra.AsNoTracking().AnyAsync(i => i.IdOrdenCompra == id, ct);
        if (!tieneItems)
        {
            throw new ErrorDominio(
                "orden_compra_sin_items", "La orden de compra no tiene items para enviar.", 400);
        }

        var idPuntoVenta = preLectura.IdPuntoVenta;

        var estrategiaNumeracion = db.Database.CreateExecutionStrategy();
        var numero = await estrategiaNumeracion.ExecuteAsync(async () =>
            await AsignadorDeNumeroComprobante.AsignarComprometidoAsync(db, idTenant, idPuntoVenta, "OC", ct));

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarEnvioAsync(id, idTenant, idPuntoVenta, numero, momento, ct));
    }

    private async Task<OrdenDeCompraBorrador> EjecutarEnvioAsync(
        int id, int idTenant, int idPuntoVenta, long numero, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        var numeroAsignado = await EnviarHeaderAsync(
            conexion, transaccionCruda, id, idTenant, idPuntoVenta, numero, momento, ct);
        if (numeroAsignado is null)
        {
            var existe = await db.OrdenesCompra.AsNoTracking().AnyAsync(o => o.Id == id, ct);
            if (!existe)
            {
                throw ErrorDominio.NoEncontrado($"No existe la orden de compra {id}.");
            }

            throw new ErrorDominio(
                "orden_compra_no_enviable", "La orden de compra ya no está en borrador en ese punto de venta.", 409);
        }

        await transaccion.CommitAsync(ct);

        return await ObtenerParaRespuestaAsync(id, ct);
    }

    // ---- statements crudos (sibling raw SQL, mismo criterio que ServicioDeCompras) ----------------

    private static async Task<bool> BloquearBorradorAsync(
        DbConnection conexion, DbTransaction? transaccion, int id, int idTenant, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT 1 FROM ordenes_compra " +
            "WHERE id_orden_compra = $1 AND id_tenant = $2 AND estado = 'borrador'::estado_orden_compra " +
            "FOR UPDATE";

        ParametrosDeComando.Agregar(comando, id);
        ParametrosDeComando.Agregar(comando, idTenant);

        var resultado = await comando.ExecuteScalarAsync(ct);
        return resultado is not null;
    }

    /// <summary>design: Transactions — ENVIAR OC, único statement de la transacción de escritura.
    /// El predicado pinea <c>estado='borrador'</c> Y <c>id_punto_venta=$pv</c> (mutation target
    /// #11) — sin el segundo conjunto, un número dibujado para la serie vieja de un punto de venta
    /// podría escribirse igual tras un relink concurrente, aterrizando en la serie equivocada.
    /// <c>fecha_envio</c> viaja SIEMPRE por <see cref="ParametrosDeComando.Agregar"/> (mutation
    /// target #16) — nunca un parámetro armado a mano sin <c>ToUniversalTime()</c>.</summary>
    private static async Task<long?> EnviarHeaderAsync(
        DbConnection conexion, DbTransaction? transaccion, int id, int idTenant, int idPuntoVenta, long numero,
        DateTimeOffset momento, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "UPDATE ordenes_compra SET numero = $1, fecha_envio = $2, estado = 'enviada'::estado_orden_compra, " +
            "updated_at = $2 " +
            "WHERE id_orden_compra = $3 AND id_tenant = $4 AND estado = 'borrador'::estado_orden_compra " +
            "AND id_punto_venta = $5 " +
            "RETURNING numero";

        ParametrosDeComando.Agregar(comando, numero);
        ParametrosDeComando.Agregar(comando, momento);
        ParametrosDeComando.Agregar(comando, id);
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, idPuntoVenta);

        await using var lector = await comando.ExecuteReaderAsync(ct);
        if (!await lector.ReadAsync(ct))
        {
            return null;
        }

        return lector.IsDBNull(0) ? null : lector.GetInt64(0);
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

    // ---- resolución de contexto (fuera de transacción, resolvers PRIVADOS PROPIOS — OD9) ---------

    private async Task ExigirArticulosExistentesAsync(IReadOnlyList<LineaDeOrdenSolicitada> items, CancellationToken ct)
    {
        var idsArticulo = items.Select(i => i.IdArticulo).Distinct().ToList();
        if (idsArticulo.Count == 0)
        {
            return;
        }

        var existentes = await db.Articulos.Where(a => idsArticulo.Contains(a.Id)).Select(a => a.Id).ToListAsync(ct);
        var faltantes = idsArticulo.Except(existentes).ToList();
        if (faltantes.Count > 0)
        {
            throw new ErrorDominio("referencia_invalida", $"No existe el artículo {faltantes[0]}.", 400);
        }
    }

    private async Task ResolverProveedorAsync(int idProveedor, CancellationToken ct)
    {
        var existe = await db.Proveedores.AsNoTracking().AnyAsync(p => p.Id == idProveedor, ct);
        if (!existe)
        {
            // ADR-8: mismo 404 para "no existe" y "es de otro tenant".
            throw ErrorDominio.NoEncontrado($"No existe el proveedor {idProveedor}.");
        }
    }

    private async Task ResolverPuntoVentaAsync(int idPuntoVenta, CancellationToken ct)
    {
        var existe = await db.PuntosVenta.AsNoTracking().AnyAsync(pv => pv.Id == idPuntoVenta, ct);
        if (!existe)
        {
            throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");
        }
    }

    private async Task<OrdenDeCompraBorrador> ObtenerParaRespuestaAsync(int id, CancellationToken ct)
    {
        var orden = await db.OrdenesCompra.AsNoTracking().FirstAsync(o => o.Id == id, ct);
        var items = await db.ItemsOrdenCompra.AsNoTracking()
            .Where(i => i.IdOrdenCompra == id)
            .OrderBy(i => i.Orden)
            .ToListAsync(ct);

        return Proyectar(orden, items);
    }

    private static List<ItemOrdenCompra> MaterializarItems(
        int idOrdenCompra, int idTenant, IReadOnlyList<LineaDeOrdenSolicitada> items, DateTimeOffset momento)
    {
        var resultado = new List<ItemOrdenCompra>(items.Count);
        var orden = 1;

        foreach (var linea in items)
        {
            resultado.Add(new ItemOrdenCompra
            {
                IdTenant = idTenant,
                IdOrdenCompra = idOrdenCompra,
                Orden = orden++,
                IdArticulo = linea.IdArticulo,
                Descripcion = linea.Descripcion,
                CantidadPedida = linea.CantidadPedida,
                CostoUnitarioEstimado = linea.CostoUnitarioEstimado,
                CreatedAt = momento,
                UpdatedAt = momento
            });
        }

        return resultado;
    }

    private static string? NormalizarOpcional(string? valor)
    {
        var limpio = valor?.Trim();
        return string.IsNullOrEmpty(limpio) ? null : limpio;
    }

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            ?? throw new InvalidOperationException(
                "ServicioDeOrdenesDeCompra requiere un actor de tenant; GestionDeCatalogo no admite plataforma.");

    private static OrdenDeCompraBorrador Proyectar(OrdenCompra orden, IReadOnlyList<ItemOrdenCompra> items) => new(
        orden.Id, orden.IdProveedor, orden.IdPuntoVenta, orden.Numero, orden.FechaEmision, orden.FechaEnvio,
        orden.FechaEsperada, orden.Observaciones, orden.Estado,
        items
            .OrderBy(i => i.Orden)
            .Select(i => new ItemDeOrden(i.Orden, i.IdArticulo, i.Descripcion, i.CantidadPedida, i.CostoUnitarioEstimado))
            .ToList());
}
