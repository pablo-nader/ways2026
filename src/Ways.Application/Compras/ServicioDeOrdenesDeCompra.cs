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
/// (numeración propia, serie <c>'OC'</c>, consumida al enviar). La lectura paginada + el detalle
/// con cobertura llegan en slice 5; la ligadura con <c>comprobantes_compra</c> en slice 3 (ver
/// <see cref="EscriturasDeOrdenDeCompra"/>, llamada SOLO desde <see cref="ServicioDeCompras"/>).
///
/// Slice 4 (design: Transactions — CERRAR OC/ANULAR OC, decisiones 5/9): <see cref="CerrarAsync"/>
/// (cierre manual, actor-stamped, jamás revertido por la proyección) y <see cref="AnularAsync"/>
/// (gobernada por el libro, guard lock-free — decisión 9, mutation target #33). Ninguno de los dos
/// pasa por <see cref="EscriturasDeOrdenDeCompra"/>: son caminos de escritura propios de esta
/// clase, no proyecciones del libro de recepción.
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
    // ---- lectura: listado paginado (design decisión 15, task 5.2) ---------------------------------

    /// <summary>design: Interfaces/Contracts — <c>ConstruirQuery</c> (mismo patrón que
    /// <c>ServicioDeCuentaCorrienteDeProveedor.ObtenerEstadoDeCuentaAsync</c>/<c>ServicioDeCompras.
    /// ListarAsync</c>): <c>CountAsync</c> sobre el mismo <see cref="IQueryable{T}"/> que después
    /// pagina. Orden <c>fecha_emision DESC, id_orden_compra DESC</c> (design decisión 15) — el
    /// desempate por <c>Id</c> NO es cosmético: <c>fecha_emision</c> es un solo <c>reloj.Ahora</c>
    /// por operación, así que un fixture entero bajo <c>RelojFijo</c> empata por construcción y la
    /// paginación duplica/saltea filas sin él (mutation target #34b, parte 1).</summary>
    public async Task<PaginaDeOrdenesDeCompra> ListarAsync(
        int? idProveedor = null,
        int? idPuntoVenta = null,
        EstadoOrdenCompra? estado = null,
        DateTimeOffset? desde = null,
        DateTimeOffset? hasta = null,
        int pagina = 1,
        int tamanio = 25,
        CancellationToken ct = default)
    {
        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        var query = ConstruirQuery(idProveedor, idPuntoVenta, estado, desde, hasta);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(o => o.FechaEmision)
            .ThenByDescending(o => o.Id)
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .Select(o => new OrdenDeCompraListada(
                o.Id, o.IdProveedor, o.IdPuntoVenta, o.Numero, o.FechaEmision, o.FechaEsperada, o.Estado))
            .ToListAsync(ct);

        return new PaginaDeOrdenesDeCompra(items, total, pagina, tamanio);
    }

    /// <summary>Cláusulas bajo prueba (<c>mutation-proof-tests</c>, design.md:194-204, mutation
    /// target #34b), en orden de daño si se pierden:
    ///   <c>Where(o => o.IdProveedor == p)</c> / <c>Where(o => o.Id == id)</c> → una OC filtra las
    ///                                                                          de otra entidad
    ///   <c>ThenByDescending(o => o.Id)</c> → con <c>fecha_emision</c> empatada (<c>RelojFijo</c>)
    ///                                        la paginación duplica y saltea
    ///   cada <c>if (idProveedor/idPuntoVenta/estado/desde/hasta is { } x)</c> → un filtro ignorado
    ///                                                                          devuelve de más, en silencio
    /// </summary>
    private IQueryable<OrdenCompra> ConstruirQuery(
        int? idProveedor, int? idPuntoVenta, EstadoOrdenCompra? estado, DateTimeOffset? desde, DateTimeOffset? hasta)
    {
        var query = db.OrdenesCompra.AsQueryable();

        if (idProveedor is { } p)
        {
            query = query.Where(o => o.IdProveedor == p);
        }

        if (idPuntoVenta is { } pv)
        {
            query = query.Where(o => o.IdPuntoVenta == pv);
        }

        if (estado is { } e)
        {
            query = query.Where(o => o.Estado == e);
        }

        if (desde is { } d)
        {
            query = query.Where(o => o.FechaEmision >= d);
        }

        if (hasta is { } h)
        {
            query = query.Where(o => o.FechaEmision <= h);
        }

        return query;
    }

    // ---- lectura: detalle con cobertura por artículo + desvío informativo (design decisiones 12-14, task 5.3) ----

    /// <summary>design decisión 12: <see cref="OrdenCompra.Estado"/> se LEE de la columna — esta
    /// clase NUNCA re-deriva el estado (lo escribe únicamente <see
    /// cref="EscriturasDeOrdenDeCompra"/>, slices 3/4). <c>mutation-proof-tests</c> regla 12(a) se
    /// prueba literal: un <c>UPDATE</c> crudo que desincroniza <c>estado</c> a un sentinela hace que
    /// este endpoint devuelva el sentinela (task 5.8/5.9).
    ///
    /// La cobertura (design decisión 13) y el desvío (decisión 14) SÍ tienen su propia derivación
    /// LINQ, deliberadamente separada de la derivación raw-ADO de <see
    /// cref="EscriturasDeOrdenDeCompra.ProyectarEstadoAsync"/>: esa clase solo necesita dos
    /// booleanos agregados (<c>completa</c>/<c>algoRecibido</c>) para decidir una transición; esta
    /// lectura necesita las filas per-artículo completas, incluyendo recibido-no-pedido (<c>Pedida
    /// = 0</c>) y los costos, que la derivación de escritura ni calcula. La consistencia entre
    /// ambas la prueba la fixture de "projection fidelity" (design: Testing Strategy, task 5.9):
    /// recomputar <c>ProyectorDeEstadoDeOrden.Proyectar</c> desde los números de ESTA lectura debe
    /// coincidir con la columna — nunca compartir SQL entre escritura y lectura.</summary>
    public async Task<OrdenDeCompraDetalle> ObtenerDetalleAsync(int id, CancellationToken ct = default)
    {
        var orden = await db.OrdenesCompra.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la orden de compra {id}.");

        var items = await db.ItemsOrdenCompra.AsNoTracking()
            .Where(i => i.IdOrdenCompra == id)
            .OrderBy(i => i.Orden)
            .Select(i => new ItemDeOrden(i.Orden, i.IdArticulo, i.Descripcion, i.CantidadPedida, i.CostoUnitarioEstimado))
            .ToListAsync(ct);

        var cobertura = await ObtenerCoberturaAsync(id, ct);

        // TotalEstimado/TotalReal: agregan Cobertura ponderando por Pedida/Recibida
        // respectivamente, sumando SOLO los artículos con ese lado comparable (dto-contract-
        // honesty: un total que mezclara ceros fabricados con datos reales sería deshonesto).
        // null cuando NINGÚN artículo aporta ese lado.
        var conCostoEstimado = cobertura.Where(c => c.CostoEstimado is not null).ToList();
        decimal? totalEstimado = conCostoEstimado.Count > 0
            ? conCostoEstimado.Sum(c => c.CostoEstimado!.Value * c.Pedida)
            : null;

        var conCostoReal = cobertura.Where(c => c.CostoReal is not null).ToList();
        decimal? totalReal = conCostoReal.Count > 0
            ? conCostoReal.Sum(c => c.CostoReal!.Value * c.Recibida)
            : null;

        decimal? desvioTotal = totalEstimado is { } te && te != 0m && totalReal is { } tr
            ? Math.Round((tr - te) / te * 100m, 2, MidpointRounding.AwayFromZero)
            : null;

        var comprobantesLigados = await db.ComprobantesCompra.AsNoTracking()
            .Where(c => c.IdOrdenCompra == id)
            .OrderBy(c => c.Id)
            .Select(c => c.Id)
            .ToListAsync(ct);

        return new OrdenDeCompraDetalle(
            orden.Id, orden.IdProveedor, orden.IdPuntoVenta, orden.Numero, orden.FechaEmision, orden.FechaEnvio,
            orden.FechaEsperada, orden.FechaCierre, orden.IdEmpleadoCierre is not null, orden.Observaciones,
            orden.Estado, items, cobertura, totalEstimado, totalReal, desvioTotal, comprobantesLigados);
    }

    /// <summary>Deriva la cobertura per-artículo (design decisión 13): agrupa <c>items_orden_compra</c>
    /// (pedido) e <c>items_comprobante_compra</c> de comprobantes CONFIRMADOS ligados a esta orden
    /// (recibido) — mismo criterio de "confirmada" que <see
    /// cref="EscriturasDeOrdenDeCompra"/>. Ambos lados se traen materializados (<c>ToListAsync</c>)
    /// porque <see cref="CalculadorDeCompra.CalcularCostoEfectivoDesdeItem"/> es Domain puro en C#,
    /// no traducible a SQL — el promedio ponderado por artículo se calcula en memoria, LINQ-to-
    /// Objects (design decisión 14). La unión de artículos pedidos ∪ recibidos es lo que hace
    /// visible un recibido-no-pedido con <c>Pedida = 0</c> (decisión 13).</summary>
    private async Task<IReadOnlyList<CoberturaDeArticulo>> ObtenerCoberturaAsync(int idOrdenCompra, CancellationToken ct)
    {
        // deleted_at IS NULL explícito en ambos lados — mismo defense-in-depth que la derivación
        // raw-ADO de EscriturasDeOrdenDeCompra.DerivarAsync (design decisión 3). Ninguna entidad de
        // este repo tiene HasQueryFilter global (RLS cubre tenant, nada cubre soft-delete acá), así
        // que sin este filtro explícito una recepción soft-deleted contaría en esta lectura pero NO
        // en la derivación de escritura — exactamente la divergencia que la fixture de "soft-deleted
        // reception" (design: Testing Strategy) y la prueba de projection fidelity (task 5.9) están
        // para atrapar.
        var itemsPedido = await db.ItemsOrdenCompra.AsNoTracking()
            .Where(i => i.IdOrdenCompra == idOrdenCompra && i.DeletedAt == null)
            .Select(i => new { i.IdArticulo, i.CantidadPedida, i.CostoUnitarioEstimado })
            .ToListAsync(ct);

        var itemsRecibido = await (
            from ic in db.ItemsComprobanteCompra.AsNoTracking()
            join c in db.ComprobantesCompra.AsNoTracking() on ic.IdComprobanteCompra equals c.Id
            join t in db.TiposComprobante.AsNoTracking() on c.IdTipoComprobante equals t.Id
            where c.IdOrdenCompra == idOrdenCompra && c.Estado == EstadoCompra.Confirmada
                  && c.DeletedAt == null && ic.DeletedAt == null
            select new { ic.IdArticulo, ic.Cantidad, ic.Total, ic.PorcentajeIva, t.DiscriminaIva })
            .ToListAsync(ct);

        var pedidaPorArticulo = itemsPedido
            .GroupBy(i => i.IdArticulo)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.CantidadPedida));

        // Promedio ponderado por cantidad SOLO sobre las líneas cotizadas (CostoUnitarioEstimado
        // != null) — una línea sin cotizar no aporta ceros al promedio del artículo.
        var costoEstimadoPorArticulo = itemsPedido
            .Where(i => i.CostoUnitarioEstimado is not null)
            .GroupBy(i => i.IdArticulo)
            .ToDictionary(g => g.Key, g =>
            {
                var cantidad = g.Sum(x => x.CantidadPedida);
                return cantidad > 0m
                    ? g.Sum(x => x.CostoUnitarioEstimado!.Value * x.CantidadPedida) / cantidad
                    : (decimal?)null;
            });

        var recibidoPorArticulo = itemsRecibido
            .GroupBy(i => i.IdArticulo)
            .ToDictionary(g => g.Key, g =>
            {
                var cantidad = g.Sum(x => x.Cantidad);
                var totalEfectivo = g.Sum(x =>
                    CalculadorDeCompra.CalcularCostoEfectivoDesdeItem(x.Total, x.Cantidad, x.PorcentajeIva, x.DiscriminaIva) * x.Cantidad);
                return (Cantidad: cantidad, CostoReal: cantidad > 0m ? (decimal?)(totalEfectivo / cantidad) : null);
            });

        var idsArticulo = pedidaPorArticulo.Keys.Union(recibidoPorArticulo.Keys).OrderBy(x => x);

        var resultado = new List<CoberturaDeArticulo>();
        foreach (var idArticulo in idsArticulo)
        {
            var pedida = pedidaPorArticulo.GetValueOrDefault(idArticulo, 0m);
            var costoEstimado = costoEstimadoPorArticulo.GetValueOrDefault(idArticulo);

            var tieneRecibido = recibidoPorArticulo.TryGetValue(idArticulo, out var recibidoDeArticulo);
            var recibida = tieneRecibido ? recibidoDeArticulo.Cantidad : 0m;
            var costoReal = tieneRecibido ? recibidoDeArticulo.CostoReal : null;

            var pendiente = Math.Max(pedida - recibida, 0m);

            // Desvio null cuando cualquiera de los dos lados no es comparable — JAMÁS 0
            // (mutation target #34b, parte 3; ordenes-de-compra/spec.md: "no comparable, never 0").
            decimal? desvio = costoEstimado is { } ce && ce != 0m && costoReal is { } cr
                ? Math.Round((cr - ce) / ce * 100m, 2, MidpointRounding.AwayFromZero)
                : null;

            resultado.Add(new CoberturaDeArticulo(idArticulo, pedida, recibida, pendiente, costoEstimado, costoReal, desvio));
        }

        return resultado;
    }

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

    // ---- cerrar: manual, actor-stamped, jamás revertido (design decisión 5, Transactions — CERRAR OC) ----

    /// <summary>design: Transactions — CERRAR OC (manual). Una única <c>UPDATE … RETURNING</c>
    /// escribe la tríada <c>estado</c>/<c>fecha_cierre</c>/<c>id_empleado_cierre</c> — el CHECK 2
    /// (<c>ck_ordenes_compra_cierre</c>, proposal §B) exige que las tres cambien juntas. Válido
    /// solo desde <c>enviada</c>/<c>recibida_parcial</c> (ordenes-de-compra/spec.md: "Manual Close
    /// Is A Human Decision"); <c>borrador</c> NO es cerrable (nunca se envió), ni <c>cerrada</c>
    /// (mutation target #31) ni <c>anulada</c>. Una vez escrito, <c>id_empleado_cierre IS NOT
    /// NULL</c> hace que <see cref="EscriturasDeOrdenDeCompra.ProyectarEstadoAsync"/> jamás vuelva
    /// a tocar esta orden (design decisión 2, cortocircuito bajo el mismo lock, mutation target
    /// #26).</summary>
    public async Task<OrdenDeCompraBorrador> CerrarAsync(int id, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarCierreAsync(id, idTenant, idEmpleado, momento, ct));
    }

    private async Task<OrdenDeCompraBorrador> EjecutarCierreAsync(
        int id, int idTenant, int idEmpleado, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        var cerrada = await CerrarHeaderAsync(conexion, transaccionCruda, id, idTenant, idEmpleado, momento, ct);
        if (!cerrada)
        {
            var existe = await db.OrdenesCompra.AsNoTracking().AnyAsync(o => o.Id == id, ct);
            if (!existe)
            {
                throw ErrorDominio.NoEncontrado($"No existe la orden de compra {id}.");
            }

            // Código único deliberadamente general (mismo criterio que orden_compra_no_enviable,
            // decisión 19 de tasks.md): borrador (nunca se envió), ya cerrada (mutation target
            // #31) y anulada colapsan en la misma causa observable — "no está en un estado
            // cerrable ahora mismo".
            throw new ErrorDominio(
                "orden_compra_no_cerrable", "La orden de compra no está en un estado cerrable.", 409);
        }

        await transaccion.CommitAsync(ct);

        return await ObtenerParaRespuestaAsync(id, ct);
    }

    // ---- anular: gobernada por el libro, guard lock-free (design decisión 9, Transactions — ANULAR OC) ----

    /// <summary>design decisión 9 (proposal decisión 9, ratificada): anulación GOBERNADA POR EL
    /// LIBRO, nunca por el estado solo. Statement 1 (<c>SELECT … FOR UPDATE</c>) es el ÚNICO lock
    /// de fila de todo el método — ni el statement 2 (recepción confirmada) ni el 3 (borrador
    /// ligado) toman lock alguno (decisión 9: "adding FOR SHARE there closes a lock cycle against
    /// the confirm path" — un <c>FOR SHARE</c> acá armaría el ciclo confirm-quiere-OC /
    /// anular-quiere-comprobante, mutation target #33, tasks 4.10/OD-decisión-20.2 de este
    /// slice). Los TRES guards fallidos colapsan al MISMO código de dominio,
    /// <c>orden_compra_con_recepciones</c> — el propio contrato del spec ("otherwise 409
    /// orden_compra_con_recepciones") lo pinea como código único, mismo criterio de generalidad
    /// deliberada que decisión 19 (<c>orden_compra_no_enviable</c>).</summary>
    public async Task<OrdenDeCompraBorrador> AnularAsync(int id, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var momento = reloj.Ahora;

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarAnulacionDeOrdenAsync(id, idTenant, momento, ct));
    }

    private async Task<OrdenDeCompraBorrador> EjecutarAnulacionDeOrdenAsync(
        int id, int idTenant, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        // Statement 1 — PRIMER y ÚNICO lock (FOR UPDATE). 0 filas ⇒ la orden no existe para este
        // tenant (ADR-8: mismo 404 para "no existe" y "es de otro tenant").
        var estado = await BloquearYLeerEstadoOrdenAsync(conexion, transaccionCruda, id, idTenant, ct);
        if (estado is null)
        {
            throw ErrorDominio.NoEncontrado($"No existe la orden de compra {id}.");
        }

        if (estado != "borrador" && estado != "enviada")
        {
            throw new ErrorDominio(
                "orden_compra_con_recepciones", "La orden de compra no puede anularse.", 409);
        }

        // Statement 2 — recepción confirmada (recibida > 0 en cualquier artículo). Lock-free
        // (decisión 9): un SELECT simple nunca bloquea bajo READ COMMITTED, ve solo el último
        // commit — el UPDATE en curso de un confirm concurrente (todavía sin comitear) es
        // invisible acá, no hace falta ningún lock para no verlo.
        if (await TieneRecepcionConfirmadaAsync(conexion, transaccionCruda, id, idTenant, ct))
        {
            throw new ErrorDominio(
                "orden_compra_con_recepciones", "La orden de compra tiene una recepción confirmada.", 409);
        }

        // Statement 3 — comprobante ligado todavía en borrador (mutation target #33, task 4.10):
        // SIN lock de fila — agregar FOR SHARE acá arma el ciclo de deadlock contra
        // EjecutarConfirmarAsync (que toma comprobantes_compra primero y quiere ordenes_compra
        // después, mientras esta transacción ya tiene ordenes_compra y querría comprobantes_compra).
        if (await TieneComprobanteLigadoEnBorradorAsync(conexion, transaccionCruda, id, idTenant, ct))
        {
            throw new ErrorDominio(
                "orden_compra_con_recepciones",
                "La orden de compra tiene un comprobante ligado todavía en borrador.", 409);
        }

        // Statement 4 — la ÚNICA autoridad de transición a `anulada`. El lock de statement 1 ya
        // serializa cualquier escritor concurrente sobre esta fila — 0 filas acá sería un
        // invariante roto (nadie más pudo mover el estado entretanto), nunca un ErrorDominio.
        var anulada = await MarcarOrdenAnuladaAsync(conexion, transaccionCruda, id, idTenant, momento, ct)
            ?? throw new InvalidOperationException(
                $"La anulación de la orden de compra {id} no afectó ninguna fila bajo el lock ya tomado.");

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

    /// <summary>design: Transactions — CERRAR OC, único statement. Escribe la tríada
    /// <c>estado</c>/<c>fecha_cierre</c>/<c>id_empleado_cierre</c> en UN <c>UPDATE … RETURNING</c>
    /// (design decisión 5) — el CHECK 2 exige que las tres cambien juntas, así que partirlo en dos
    /// statements dejaría una ventana con la fila en un estado que el propio CHECK rechazaría.
    /// <c>momento</c>/<c>idEmpleado</c> viajan SIEMPRE por <see cref="ParametrosDeComando.Agregar"/>
    /// (mutation target #32 para el segundo).</summary>
    private static async Task<bool> CerrarHeaderAsync(
        DbConnection conexion, DbTransaction? transaccion, int id, int idTenant, int idEmpleado,
        DateTimeOffset momento, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "UPDATE ordenes_compra SET estado = 'cerrada'::estado_orden_compra, " +
            "fecha_cierre = $1, id_empleado_cierre = $2, updated_at = $1 " +
            "WHERE id_orden_compra = $3 AND id_tenant = $4 " +
            "AND estado IN ('enviada'::estado_orden_compra, 'recibida_parcial'::estado_orden_compra) " +
            "RETURNING estado";

        ParametrosDeComando.Agregar(comando, momento);
        ParametrosDeComando.Agregar(comando, idEmpleado);
        ParametrosDeComando.Agregar(comando, id);
        ParametrosDeComando.Agregar(comando, idTenant);

        var resultado = await comando.ExecuteScalarAsync(ct);
        return resultado is not null;
    }

    /// <summary>design: Transactions — ANULAR OC, statement 1. El ÚNICO <c>FOR UPDATE</c> de todo
    /// el método <see cref="EjecutarAnulacionDeOrdenAsync"/> — statements 2/3 son lecturas
    /// lock-free a propósito (decisión 9). <c>null</c> ⇒ la fila no existe para este tenant
    /// (invariante de FK garantiza que, si existe, esta lectura la ve).</summary>
    private static async Task<string?> BloquearYLeerEstadoOrdenAsync(
        DbConnection conexion, DbTransaction? transaccion, int id, int idTenant, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT estado::text FROM ordenes_compra WHERE id_orden_compra = $1 AND id_tenant = $2 FOR UPDATE";

        ParametrosDeComando.Agregar(comando, id);
        ParametrosDeComando.Agregar(comando, idTenant);

        var resultado = await comando.ExecuteScalarAsync(ct);
        return resultado as string;
    }

    /// <summary>design: Transactions — ANULAR OC, statement 2 ("recibida &gt; 0 en cualquier
    /// artículo"). <c>cantidad</c> de un item de comprobante confirmado es SIEMPRE positiva
    /// (<c>ck_items_comprobante_compra_cantidad_positiva</c>) — por eso alcanza con un EXISTS de
    /// cualquier item confirmado y ligado, sin necesidad de sumar por artículo como hace
    /// <see cref="EscriturasDeOrdenDeCompra"/>: cualquier fila que pase el filtro ya prueba
    /// "recibida &gt; 0" para SU artículo. SIN lock de fila — lectura simple, nunca bloquea bajo
    /// READ COMMITTED (decisión 9).</summary>
    private static async Task<bool> TieneRecepcionConfirmadaAsync(
        DbConnection conexion, DbTransaction? transaccion, int idOrdenCompra, int idTenant, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT EXISTS (" +
            "    SELECT 1 " +
            "    FROM items_comprobante_compra ic " +
            "    JOIN comprobantes_compra c " +
            "      ON c.id_comprobante_compra = ic.id_comprobante_compra AND c.id_tenant = ic.id_tenant " +
            "    WHERE c.id_orden_compra = $1 AND c.id_tenant = $2 " +
            "      AND c.estado = 'confirmada'::estado_compra " +
            "      AND c.deleted_at IS NULL AND ic.deleted_at IS NULL" +
            ")";

        ParametrosDeComando.Agregar(comando, idOrdenCompra);
        ParametrosDeComando.Agregar(comando, idTenant);

        return (bool)(await comando.ExecuteScalarAsync(ct))!;
    }

    /// <summary>design: Transactions — ANULAR OC, statement 3 (mutation target #33, task 4.10):
    /// EXISTS de un comprobante ligado todavía en <c>borrador</c> — SIN lock de fila, a propósito
    /// (decisión 9: agregar <c>FOR SHARE</c> acá cierra el ciclo de deadlock contra
    /// <c>EjecutarConfirmarAsync</c>, que toma <c>comprobantes_compra</c> primero y
    /// <c>ordenes_compra</c> después — el orden inverso exacto de esta transacción, que ya tiene
    /// <c>ordenes_compra</c> desde el statement 1 y pediría <c>comprobantes_compra</c> acá).</summary>
    private static async Task<bool> TieneComprobanteLigadoEnBorradorAsync(
        DbConnection conexion, DbTransaction? transaccion, int idOrdenCompra, int idTenant, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT EXISTS (" +
            "    SELECT 1 FROM comprobantes_compra " +
            "    WHERE id_orden_compra = $1 AND id_tenant = $2 " +
            "      AND estado = 'borrador'::estado_compra AND deleted_at IS NULL" +
            ")";

        ParametrosDeComando.Agregar(comando, idOrdenCompra);
        ParametrosDeComando.Agregar(comando, idTenant);

        return (bool)(await comando.ExecuteScalarAsync(ct))!;
    }

    /// <summary>design: Transactions — ANULAR OC, statement 4 — la ÚNICA autoridad de transición a
    /// <c>anulada</c>. <c>fecha_cierre</c> no se toca: <c>borrador</c>/<c>enviada</c> nunca la
    /// tuvieron seteada (CHECK 2 ya lo garantiza), así que no hace falta ningún <c>CASE</c> de
    /// limpieza como el de <see cref="EscriturasDeOrdenDeCompra"/>.</summary>
    private static async Task<string?> MarcarOrdenAnuladaAsync(
        DbConnection conexion, DbTransaction? transaccion, int id, int idTenant, DateTimeOffset momento,
        CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "UPDATE ordenes_compra SET estado = 'anulada'::estado_orden_compra, updated_at = $1 " +
            "WHERE id_orden_compra = $2 AND id_tenant = $3 " +
            "AND estado IN ('borrador'::estado_orden_compra, 'enviada'::estado_orden_compra) " +
            "RETURNING estado::text";

        ParametrosDeComando.Agregar(comando, momento);
        ParametrosDeComando.Agregar(comando, id);
        ParametrosDeComando.Agregar(comando, idTenant);

        var resultado = await comando.ExecuteScalarAsync(ct);
        return resultado as string;
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
        orden.FechaEsperada, orden.FechaCierre, orden.IdEmpleadoCierre, orden.Observaciones, orden.Estado,
        items
            .OrderBy(i => i.Orden)
            .Select(i => new ItemDeOrden(i.Orden, i.IdArticulo, i.Descripcion, i.CantidadPedida, i.CostoUnitarioEstimado))
            .ToList());
}
