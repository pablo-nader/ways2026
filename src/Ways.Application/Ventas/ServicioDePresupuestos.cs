using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Ofertas;
using Ways.Application.Parametros;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Ventas;

namespace Ways.Application.Ventas;

/// <summary>
/// stage-17-presupuestos-y-remitos, Slice 2 (design: Technical Approach; API Surface). Borrador
/// CRUD (replace-set completo bajo <c>SELECT … FOR UPDATE … WHERE estado='borrador'</c>, mismo
/// criterio que <see cref="Compras.ServicioDeOrdenesDeCompra"/>) + <see cref="EnviarAsync"/>
/// (numeración propia, serie <c>'PRES'</c>, número comprometido ANTES de la transacción vía
/// <see cref="AsignadorDeNumeroComprobante.AsignarComprometidoAsync"/> — el patrón de
/// <c>ServicioDeOrdenesDeCompra.EnviarAsync</c>, pineando <c>id_punto_venta = $pv</c> en el
/// <c>UPDATE</c> final) + <see cref="AnularAsync"/> (guardado por estado, sin coupling con la
/// conversión — decisión 4/9 del proposal, OD8/T1: <c>convertido</c> es terminal, la anulación de
/// la venta resultante NO revive el presupuesto, así que <c>AnularAsync</c> no necesita saberlo).
///
/// La conversión (<c>estado = 'convertido'</c>, escrita por <c>EscriturasDePresupuesto</c> desde
/// <c>ServicioDeVentas</c>, Slice 3) — esta clase nunca escribe <c>convertido</c> ni conoce
/// <c>ServicioDeVentas</c> (design: "la contención ES el producto"). <see cref="ObtenerParaVentaAsync"/>
/// (Slice 3) SÍ vive acá: es lectura pura, nunca escribe y nunca es la autoridad de precio.
///
/// OD9 (orquestador, apply de esta slice, precedente stage-16 slice 2): los resolvers 404 de
/// punto de venta/cliente son PRIVADOS y PROPIOS de esta clase — copian la forma exacta de
/// <c>ServicioDeVentas.ResolverPuntoVentaAsync</c>/<c>ResolverClienteAsync</c> (privados ahí
/// también) en vez de promoverlos a un helper compartido.
///
/// <c>vencido</c>/<c>convertible</c> son SIEMPRE derivados en la lectura (<see
/// cref="ReglaDePresupuestos"/>, design decisión 11) — nunca una columna. La zona horaria del
/// punto de venta se resuelve vía <see cref="ServicioDeParametros"/> (design decisión 16):
/// una vez por punto de venta involucrado, nunca por fila.
/// </summary>
public class ServicioDePresupuestos(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, ServicioDeOfertas servicioDeOfertas,
    ServicioDeParametros servicioDeParametros)
{
    // ---- lectura: listado paginado + detalle (design decisión 15/16, task 2.8/2.9) ----------------

    /// <summary>design decisión 16: el filtro <c>vencido</c> EXIGE <c>idPuntoVenta</c> (400
    /// <c>punto_venta_requerido</c>) — sin él, "hoy" no tiene una zona única con la que
    /// traducir el predicado a SQL. Con el punto de venta fijo, el filtro se empuja a la
    /// consulta usando la MISMA fórmula que <see cref="ReglaDePresupuestos.EstaVencido"/>
    /// (`estado = enviado AND vencimiento &lt; hoy`) — nunca una segunda derivación paralela.
    /// El campo <c>Vencido</c>/<c>Convertible</c> de CADA fila devuelta, en cambio, siempre se
    /// deriva en memoria contra la zona de SU PROPIO punto de venta (task 2.9: "Vencido resuelto
    /// por PV DISTINTO de la página") — la página puede mezclar puntos de venta aunque el filtro
    /// no esté presente.</summary>
    public async Task<PaginaDePresupuestos> ListarAsync(
        int? idPuntoVenta = null,
        int? idCliente = null,
        EstadoPresupuesto? estado = null,
        bool? vencido = null,
        DateTimeOffset? desde = null,
        DateTimeOffset? hasta = null,
        int pagina = 1,
        int tamanio = 25,
        CancellationToken ct = default)
    {
        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        if (vencido is not null && idPuntoVenta is null)
        {
            throw new ErrorDominio(
                "punto_venta_requerido", "El filtro 'vencido' requiere especificar un punto de venta.", 400);
        }

        var momento = reloj.Ahora;
        var query = ConstruirQuery(idPuntoVenta, idCliente, estado, desde, hasta);

        if (vencido is { } v)
        {
            var (_, zonaDelFiltro) = await ResolverZonaAsync(idPuntoVenta!.Value, ct);
            var hoyDelFiltro = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(momento, zonaDelFiltro).DateTime);

            query = v
                ? query.Where(p => p.Estado == EstadoPresupuesto.Enviado && p.Vencimiento < hoyDelFiltro)
                : query.Where(p => !(p.Estado == EstadoPresupuesto.Enviado && p.Vencimiento < hoyDelFiltro));
        }

        var total = await query.CountAsync(ct);

        var pagados = await query
            .OrderByDescending(p => p.FechaEmision)
            .ThenByDescending(p => p.Id)
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .ToListAsync(ct);

        var zonasPorPuntoVenta = await ResolverZonasPorPuntoVentaAsync(
            pagados.Select(p => p.IdPuntoVenta).Distinct().ToList(), ct);

        var items = pagados.Select(p => ProyectarListado(p, zonasPorPuntoVenta, momento)).ToList();

        return new PaginaDePresupuestos(items, total, pagina, tamanio);
    }

    /// <summary>Cláusulas bajo prueba (<c>mutation-proof-tests</c>, mutation target #59, mitad
    /// presupuesto), en orden de daño si se pierden:
    ///   <c>Where(p => p.IdPuntoVenta == pv)</c> / <c>Where(p => p.IdCliente == c)</c> → un
    ///                                                                    presupuesto filtra los de otro
    ///   <c>ThenByDescending(p => p.Id)</c> → con <c>fecha_emision</c> empatada (<c>RelojFijo</c>)
    ///                                        la paginación duplica y saltea
    ///   cada <c>if (idPuntoVenta/idCliente/estado/desde/hasta is { } x)</c> → filtro ignorado
    /// </summary>
    private IQueryable<Presupuesto> ConstruirQuery(
        int? idPuntoVenta, int? idCliente, EstadoPresupuesto? estado, DateTimeOffset? desde, DateTimeOffset? hasta)
    {
        var query = db.Presupuestos.AsQueryable();

        if (idPuntoVenta is { } pv)
        {
            query = query.Where(p => p.IdPuntoVenta == pv);
        }

        if (idCliente is { } c)
        {
            query = query.Where(p => p.IdCliente == c);
        }

        if (estado is { } e)
        {
            query = query.Where(p => p.Estado == e);
        }

        if (desde is { } d)
        {
            query = query.Where(p => p.FechaEmision >= d);
        }

        if (hasta is { } h)
        {
            query = query.Where(p => p.FechaEmision <= h);
        }

        return query;
    }

    // ---- /para-venta: lectura para mostrar, jamás la autoridad de precio (Slice 3, design
    // decisión 2, dto-contract-honesty regla 1) -----------------------------------------------------

    /// <summary>stage-17-presupuestos-y-remitos, Slice 3 (design: API Surface, decisión 2;
    /// registrado en tasks.md — el endpoint se cablea recién acá, `ContratosDePresupuesto.cs`
    /// declaró el shape desde la Slice 2). Refusa el mismo par de causas que la conversión
    /// (`ServicioDeVentas.ResolverConversionDesdePresupuestoAsync`), ANTES de la autoridad real —
    /// esta lectura NUNCA escribe nada y NUNCA es la autoridad de precio: la conversión
    /// (`POST /api/ventas` con `idPresupuestoOrigen`) es la única fuente de verdad.</summary>
    public async Task<PresupuestoParaVenta> ObtenerParaVentaAsync(int id, CancellationToken ct = default)
    {
        var presupuesto = await db.Presupuestos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el presupuesto {id}.");

        var (_, zona) = await ResolverZonaAsync(presupuesto.IdPuntoVenta, ct);
        var hoy = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(reloj.Ahora, zona).DateTime);

        if (presupuesto.Estado == EstadoPresupuesto.Convertido)
        {
            throw new ErrorDominio(
                "presupuesto_ya_convertido", "El presupuesto ya fue convertido en una venta.", 409);
        }

        if (presupuesto.Estado != EstadoPresupuesto.Enviado)
        {
            throw new ErrorDominio(
                "presupuesto_no_convertible", "El presupuesto no está en un estado convertible.", 409);
        }

        if (ReglaDePresupuestos.EstaVencido(presupuesto.Estado, presupuesto.Vencimiento, hoy))
        {
            throw new ErrorDominio("presupuesto_vencido", "El presupuesto está vencido.", 409);
        }

        var convertible = ReglaDePresupuestos.EsConvertible(presupuesto.Estado, presupuesto.Vencimiento, hoy);

        var items = await db.ItemsPresupuesto.AsNoTracking()
            .Where(i => i.IdPresupuesto == id)
            .OrderBy(i => i.Orden)
            .ToListAsync(ct);

        return new PresupuestoParaVenta(
            presupuesto.Id, presupuesto.Numero, presupuesto.IdPuntoVenta, presupuesto.IdCliente,
            presupuesto.Vencimiento, Vencido: false, convertible, presupuesto.Subtotal, presupuesto.DescuentoTotal,
            presupuesto.Total,
            items
                .Select(i => new ItemDePresupuesto(
                    i.Orden, i.IdArticulo, i.Descripcion, i.Cantidad, i.PrecioUnitario, i.Descuento, i.Total,
                    i.IdListaPrecio, i.IdOferta, i.IdAlicuotaIva, i.PorcentajeIva))
                .ToList());
    }

    public async Task<PresupuestoDetalle> ObtenerDetalleAsync(int id, CancellationToken ct = default)
    {
        var presupuesto = await db.Presupuestos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el presupuesto {id}.");

        var items = await db.ItemsPresupuesto.AsNoTracking()
            .Where(i => i.IdPresupuesto == id)
            .OrderBy(i => i.Orden)
            .ToListAsync(ct);

        return await ProyectarDetalleAsync(presupuesto, items, ct);
    }

    // ---- borrador: crear + replace-set (mismo criterio que ServicioDeOrdenesDeCompra) -------------

    /// <summary>design: Technical Approach (fact 1), task 2.2: los precios se resuelven al
    /// GUARDAR el borrador — la misma <see cref="ServicioDeOfertas"/> que usa el checkout, nunca
    /// una segunda autoridad. Con cero líneas persiste igual (spec: "A borrador presupuesto is
    /// created with no items yet").</summary>
    public async Task<PresupuestoDetalle> CrearBorradorAsync(
        SolicitudDePresupuesto solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var puntoVenta = await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);
        var cliente = await ResolverClienteAsync(solicitud.IdCliente, ct);
        ExigirCantidadesValidas(solicitud.Lineas);

        var (lineasMaterializadas, totales) = await ResolverYMaterializarAsync(
            solicitud.Lineas, puntoVenta.IdEmpresa, cliente.IdListaPrecio, momento, ct);

        var presupuesto = new Presupuesto
        {
            IdTenant = idTenant,
            IdPuntoVenta = solicitud.IdPuntoVenta,
            IdCliente = cliente.Id,
            IdEmpleado = idEmpleado,
            Numero = null,
            FechaEmision = momento,
            FechaEnvio = null,
            Vencimiento = null,
            Observaciones = NormalizarOpcional(solicitud.Observaciones),
            Subtotal = totales.Subtotal,
            DescuentoTotal = totales.DescuentoTotal,
            Total = totales.Total,
            Estado = EstadoPresupuesto.Borrador,
            CreatedAt = momento,
            UpdatedAt = momento
        };
        db.Presupuestos.Add(presupuesto);
        await db.SaveChangesAsync(ct);

        var items = ConstruirItems(presupuesto.Id, idTenant, momento, lineasMaterializadas);
        db.ItemsPresupuesto.AddRange(items);
        await db.SaveChangesAsync(ct);

        return await ProyectarDetalleAsync(presupuesto, items, ct);
    }

    /// <summary>Mismo criterio que <c>ServicioDeOrdenesDeCompra.ActualizarBorradorAsync</c>:
    /// replace-set completo bajo <c>SELECT … FOR UPDATE … WHERE estado='borrador'</c> (mutation
    /// target #12) — el predicado de estado en el mismo statement hace que editar un presupuesto
    /// ya enviado sea estructuralmente imposible. El <c>RemoveRange</c> está scopeado por
    /// <c>IdPresupuesto</c> (mutation target #13) — un presupuesto hermano del mismo tenant, con
    /// sus propios items, queda intacto (rule 12c, task 2.17).</summary>
    public async Task<PresupuestoDetalle> EditarAsync(
        int id, SolicitudDePresupuesto solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var momento = reloj.Ahora;

        var puntoVenta = await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);
        var cliente = await ResolverClienteAsync(solicitud.IdCliente, ct);
        ExigirCantidadesValidas(solicitud.Lineas);

        var (lineasMaterializadas, totales) = await ResolverYMaterializarAsync(
            solicitud.Lineas, puntoVenta.IdEmpresa, cliente.IdListaPrecio, momento, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarEdicionAsync(id, idTenant, solicitud, cliente.Id, lineasMaterializadas, totales, momento, ct));
    }

    private async Task<PresupuestoDetalle> EjecutarEdicionAsync(
        int id, int idTenant, SolicitudDePresupuesto solicitud, int idCliente,
        IReadOnlyList<LineaMaterializada> lineasMaterializadas, TotalesCalculados totales, DateTimeOffset momento,
        CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        var bloqueado = await BloquearBorradorAsync(conexion, transaccionCruda, id, idTenant, ct);
        if (!bloqueado)
        {
            var existe = await db.Presupuestos.AsNoTracking().AnyAsync(p => p.Id == id, ct);
            if (!existe)
            {
                throw ErrorDominio.NoEncontrado($"No existe el presupuesto {id}.");
            }

            throw new ErrorDominio(
                "presupuesto_no_editable", "Solo un presupuesto en borrador puede editarse.", 409);
        }

        // El lock de fila crudo de arriba ya serializa cualquier escritor concurrente sobre este
        // header — misma lógica que EjecutarActualizacionAsync de ServicioDeOrdenesDeCompra.
        var presupuesto = await db.Presupuestos.FirstAsync(p => p.Id == id, ct);

        var itemsExistentes = await db.ItemsPresupuesto.Where(i => i.IdPresupuesto == id).ToListAsync(ct);
        db.ItemsPresupuesto.RemoveRange(itemsExistentes);

        presupuesto.IdPuntoVenta = solicitud.IdPuntoVenta;
        presupuesto.IdCliente = idCliente;
        presupuesto.Observaciones = NormalizarOpcional(solicitud.Observaciones);
        presupuesto.Subtotal = totales.Subtotal;
        presupuesto.DescuentoTotal = totales.DescuentoTotal;
        presupuesto.Total = totales.Total;
        presupuesto.UpdatedAt = momento;

        var itemsNuevos = ConstruirItems(id, idTenant, momento, lineasMaterializadas);
        db.ItemsPresupuesto.AddRange(itemsNuevos);

        await db.SaveChangesAsync(ct);
        await transaccion.CommitAsync(ct);

        return await ProyectarDetalleAsync(presupuesto, itemsNuevos, ct);
    }

    // ---- enviar: numeración propia consumida ANTES de la transacción (mismo criterio que OC) -----

    /// <summary>Mismo shape que <c>ServicioDeOrdenesDeCompra.EnviarAsync</c>: el número (serie
    /// <c>'PRES'</c>) se asigna y comitea en su PROPIA transacción chica ANTES de abrir la que
    /// escribe el presupuesto (design: Transactions — "ENVIAR PRESUPUESTO"). El <c>UPDATE</c>
    /// final pinea <c>id_punto_venta = $pv</c> (mutation target #17): sin el segundo conjunto, un
    /// <c>PUT</c> concurrente que mueve el presupuesto a otro punto de venta haría aterrizar el
    /// número en la serie equivocada. 0 filas puede deberse a un doble-enviar o a ese relink
    /// concurrente — en ambos casos el número YA se comiteó y queda quemado, residuo aceptado
    /// (design decisión 15/proposal decisión 6).</summary>
    public async Task<PresupuestoDetalle> EnviarAsync(
        int id, SolicitudDeEnvio solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var momento = reloj.Ahora;

        var preLectura = await db.Presupuestos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el presupuesto {id}.");

        if (preLectura.Estado != EstadoPresupuesto.Borrador)
        {
            throw new ErrorDominio(
                "presupuesto_ya_enviado", "El presupuesto ya no está en borrador.", 409);
        }

        // Conflicto de esta slice: un presupuesto sin items nunca debería consumir un número
        // (mutation target #21) — se rechaza ACÁ, antes de gastar uno.
        var tieneItems = await db.ItemsPresupuesto.AsNoTracking().AnyAsync(i => i.IdPresupuesto == id, ct);
        if (!tieneItems)
        {
            throw new ErrorDominio(
                "presupuesto_sin_items", "El presupuesto no tiene items para enviar.", 400);
        }

        var idPuntoVenta = preLectura.IdPuntoVenta;

        // design decisión 10/11: "hoy" SIEMPRE resuelto en la zona del punto de venta (mutation
        // target #19) — jamás DateTime.UtcNow/reloj.Ahora.UtcDateTime.
        var (_, zona) = await ResolverZonaAsync(idPuntoVenta, ct);
        var hoy = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(momento, zona).DateTime);

        if (solicitud.Vencimiento < hoy)
        {
            throw new ErrorDominio(
                "vencimiento_invalido", "El vencimiento tiene que ser hoy o una fecha futura.", 400);
        }

        var estrategiaNumeracion = db.Database.CreateExecutionStrategy();
        var numero = await estrategiaNumeracion.ExecuteAsync(async () =>
            await AsignadorDeNumeroComprobante.AsignarComprometidoAsync(db, idTenant, idPuntoVenta, "PRES", ct));

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarEnvioAsync(id, idTenant, idPuntoVenta, numero, solicitud.Vencimiento, momento, ct));
    }

    private async Task<PresupuestoDetalle> EjecutarEnvioAsync(
        int id, int idTenant, int idPuntoVenta, long numero, DateOnly vencimiento, DateTimeOffset momento,
        CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        var numeroAsignado = await EnviarHeaderAsync(
            conexion, transaccionCruda, id, idTenant, idPuntoVenta, numero, vencimiento, momento, ct);
        if (numeroAsignado is null)
        {
            var existe = await db.Presupuestos.AsNoTracking().AnyAsync(p => p.Id == id, ct);
            if (!existe)
            {
                throw ErrorDominio.NoEncontrado($"No existe el presupuesto {id}.");
            }

            throw new ErrorDominio(
                "presupuesto_ya_enviado", "El presupuesto ya no está en borrador en ese punto de venta.", 409);
        }

        await transaccion.CommitAsync(ct);

        return await ObtenerDetalleAsync(id, ct);
    }

    // ---- anular: guardado por estado, sin coupling con la conversión (proposal decisión 9) --------

    /// <summary>OD8/T1 (tasks.md decisión 4, proposal decisión 9): <c>convertido</c> es terminal
    /// — esta clase JAMÁS lo revierte, y por eso el <c>WHERE</c> de abajo solo admite
    /// <c>borrador</c>/<c>enviado</c> (spec: "Anulación Is Rejected For A Convertido
    /// Presupuesto"). Un único <c>UPDATE … RETURNING</c> (mismo criterio que
    /// <c>ServicioDeOrdenesDeCompra.MarcarOrdenAnuladaAsync</c>, sin lock previo — no hay ningún
    /// segundo invariante que verificar bajo lock, a diferencia de la OC gobernada por el
    /// libro).</summary>
    public async Task<PresupuestoDetalle> AnularAsync(int id, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var momento = reloj.Ahora;

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarAnulacionAsync(id, idTenant, momento, ct));
    }

    private async Task<PresupuestoDetalle> EjecutarAnulacionAsync(
        int id, int idTenant, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        var anulado = await MarcarAnuladoAsync(conexion, transaccionCruda, id, idTenant, momento, ct);
        if (!anulado)
        {
            var existe = await db.Presupuestos.AsNoTracking().AnyAsync(p => p.Id == id, ct);
            if (!existe)
            {
                throw ErrorDominio.NoEncontrado($"No existe el presupuesto {id}.");
            }

            throw new ErrorDominio("presupuesto_no_anulable", "El presupuesto no puede anularse.", 409);
        }

        await transaccion.CommitAsync(ct);

        return await ObtenerDetalleAsync(id, ct);
    }

    // ---- statements crudos (sibling raw SQL, mismo criterio que ServicioDeOrdenesDeCompra) --------

    private static async Task<bool> BloquearBorradorAsync(
        DbConnection conexion, DbTransaction? transaccion, int id, int idTenant, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT 1 FROM presupuestos " +
            "WHERE id_presupuesto = $1 AND id_tenant = $2 AND estado = 'borrador'::estado_presupuesto " +
            "FOR UPDATE";

        ParametrosDeComando.Agregar(comando, id);
        ParametrosDeComando.Agregar(comando, idTenant);

        var resultado = await comando.ExecuteScalarAsync(ct);
        return resultado is not null;
    }

    /// <summary>design: Transactions — ENVIAR PRESUPUESTO, único statement de la transacción de
    /// escritura. El predicado pinea <c>estado='borrador'</c> Y <c>id_punto_venta=$pv</c>
    /// (mutation target #17). <c>fecha_envio</c>/<c>vencimiento</c> viajan SIEMPRE por
    /// <see cref="ParametrosDeComando.Agregar"/> (mutation target #22) — nunca un parámetro
    /// armado a mano sin <c>ToUniversalTime()</c>.</summary>
    private static async Task<long?> EnviarHeaderAsync(
        DbConnection conexion, DbTransaction? transaccion, int id, int idTenant, int idPuntoVenta, long numero,
        DateOnly vencimiento, DateTimeOffset momento, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "UPDATE presupuestos SET numero = $1, fecha_envio = $2, vencimiento = $3, " +
            "estado = 'enviado'::estado_presupuesto, updated_at = $2 " +
            "WHERE id_presupuesto = $4 AND id_tenant = $5 AND estado = 'borrador'::estado_presupuesto " +
            "AND id_punto_venta = $6 " +
            "RETURNING numero";

        ParametrosDeComando.Agregar(comando, numero);
        ParametrosDeComando.Agregar(comando, momento);
        ParametrosDeComando.Agregar(comando, vencimiento);
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

    /// <summary>design: Transactions — ANULAR PRESUPUESTO, único statement — la ÚNICA autoridad
    /// de transición a <c>anulado</c>. <c>convertido</c> queda deliberadamente afuera del
    /// <c>IN</c> (OD8/T1): un presupuesto ya convertido nunca matchea, así que 0 filas colapsa
    /// "convertido"/"ya anulado"/"no existe para este tenant" en el mismo código de dominio de
    /// rechazo, distinguido de 404 solo por la existencia de la fila.</summary>
    private static async Task<bool> MarcarAnuladoAsync(
        DbConnection conexion, DbTransaction? transaccion, int id, int idTenant, DateTimeOffset momento,
        CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "UPDATE presupuestos SET estado = 'anulado'::estado_presupuesto, updated_at = $1 " +
            "WHERE id_presupuesto = $2 AND id_tenant = $3 " +
            "AND estado IN ('borrador'::estado_presupuesto, 'enviado'::estado_presupuesto) " +
            "RETURNING estado";

        ParametrosDeComando.Agregar(comando, momento);
        ParametrosDeComando.Agregar(comando, id);
        ParametrosDeComando.Agregar(comando, idTenant);

        var resultado = await comando.ExecuteScalarAsync(ct);
        return resultado is not null;
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

    // ---- resolución de precio (mismo criterio que ServicioDeVentas, sin signo — nunca NCX) -------

    /// <summary>design: Technical Approach (fact 1). Con cero líneas devuelve totales en cero sin
    /// consultar nada (<c>CalculadorDeTotales.Calcular([])</c> ya es válido — la invariante de
    /// consistencia interna pasa trivialmente).</summary>
    private async Task<(IReadOnlyList<LineaMaterializada> Lineas, TotalesCalculados Totales)> ResolverYMaterializarAsync(
        IReadOnlyList<LineaDePresupuesto> lineas, int idEmpresa, int idListaPrecio, DateTimeOffset momento,
        CancellationToken ct)
    {
        if (lineas.Count == 0)
        {
            return (Array.Empty<LineaMaterializada>(), CalculadorDeTotales.Calcular([]));
        }

        // La autoridad de precio ÚNICA (design decisión 2/checkout, fact 1) — nunca lo que
        // mandó el cliente.
        var lineasDeResolucion = lineas
            .Select(l => new LineaDeResolucion(l.IdArticulo, idEmpresa, idListaPrecio, l.Cantidad))
            .ToList();
        var resolucion = await servicioDeOfertas.ResolverAsync(lineasDeResolucion, momento, ct);

        var idsArticulo = lineas.Select(l => l.IdArticulo).Distinct().ToList();
        var articuloPorId = await db.Articulos
            .Where(a => idsArticulo.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);

        var idsAlicuota = articuloPorId.Values.Select(a => a.IdAlicuotaIva).Distinct().ToList();
        var porcentajePorAlicuota = await db.AlicuotasIva
            .Where(a => idsAlicuota.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Porcentaje, ct);

        return MaterializarLineas(lineas, resolucion, articuloPorId, porcentajePorAlicuota, idListaPrecio);
    }

    private static (IReadOnlyList<LineaMaterializada> Lineas, TotalesCalculados Totales) MaterializarLineas(
        IReadOnlyList<LineaDePresupuesto> lineas,
        IReadOnlyList<ResultadoDeResolucion> resolucion,
        IReadOnlyDictionary<int, Articulo> articuloPorId,
        IReadOnlyDictionary<int, decimal> porcentajePorAlicuota,
        int idListaPrecio)
    {
        var lineasParaCalcular = new List<LineaParaCalcular>(lineas.Count);

        for (var i = 0; i < lineas.Count; i++)
        {
            var resultado = resolucion[i];

            if (resultado.PrecioOriginal is null)
            {
                throw new ErrorDominio(
                    "articulo_sin_precio_vigente",
                    $"El artículo {lineas[i].IdArticulo} no tiene un precio vigente en la lista del cliente.",
                    400);
            }

            lineasParaCalcular.Add(new LineaParaCalcular(
                lineas[i].Cantidad, resultado.PrecioOriginal.Value, resultado.DescuentoUnitario));
        }

        var totales = CalculadorDeTotales.Calcular(lineasParaCalcular);

        var resultadoFinal = new List<LineaMaterializada>(lineas.Count);
        for (var i = 0; i < lineas.Count; i++)
        {
            var linea = lineas[i];
            var resultado = resolucion[i];
            var calculado = totales.Items[i];
            var articulo = articuloPorId[linea.IdArticulo];

            // Solo UNA columna id_oferta por item (esquema): cuando se acumulan varias ofertas en
            // la misma línea, se snapshotea la de mayor prioridad — mismo criterio que
            // ServicioDeVentas.MaterializarItems.
            var idOferta = resultado.Aplicadas.Count > 0 ? resultado.Aplicadas[0].IdOferta : (int?)null;

            resultadoFinal.Add(new LineaMaterializada(
                articulo.Id, articulo.Nombre, calculado.Cantidad, calculado.PrecioUnitario, calculado.Descuento,
                calculado.Total, idListaPrecio, idOferta, articulo.IdAlicuotaIva,
                porcentajePorAlicuota[articulo.IdAlicuotaIva]));
        }

        return (resultadoFinal, totales);
    }

    private static List<ItemPresupuesto> ConstruirItems(
        int idPresupuesto, int idTenant, DateTimeOffset momento, IReadOnlyList<LineaMaterializada> lineas)
    {
        var resultado = new List<ItemPresupuesto>(lineas.Count);
        var orden = 1;

        foreach (var linea in lineas)
        {
            resultado.Add(new ItemPresupuesto
            {
                IdTenant = idTenant,
                IdPresupuesto = idPresupuesto,
                Orden = orden++,
                IdArticulo = linea.IdArticulo,
                Descripcion = linea.Descripcion,
                Cantidad = linea.Cantidad,
                PrecioUnitario = linea.PrecioUnitario,
                Descuento = linea.Descuento,
                Total = linea.Total,
                IdListaPrecio = linea.IdListaPrecio,
                IdOferta = linea.IdOferta,
                IdAlicuotaIva = linea.IdAlicuotaIva,
                PorcentajeIva = linea.PorcentajeIva,
                CreatedAt = momento,
                UpdatedAt = momento
            });
        }

        return resultado;
    }

    /// <summary>Backstop de servicio para <c>ck_items_presupuesto_cantidad_positiva</c> (400
    /// <c>cantidad_de_linea_invalida</c>, ANTES de tocar la base — <c>db-error-backstops</c>: el
    /// CHECK de esquema queda como defensa en profundidad, solo alcanzable fuera de banda).</summary>
    private static void ExigirCantidadesValidas(IReadOnlyList<LineaDePresupuesto> lineas)
    {
        foreach (var linea in lineas)
        {
            if (linea.Cantidad <= 0)
            {
                throw new ErrorDominio(
                    "cantidad_de_linea_invalida", "La cantidad de una línea de presupuesto tiene que ser positiva.", 400);
            }
        }
    }

    // ---- zona horaria del punto de venta (design decisión 16) --------------------------------------

    private async Task<(string ZonaId, TimeZoneInfo Zona)> ResolverZonaAsync(int idPuntoVenta, CancellationToken ct)
    {
        var puntoVenta = await db.PuntosVenta.AsNoTracking()
            .Where(pv => pv.Id == idPuntoVenta)
            .Select(pv => new { pv.IdEmpresa })
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");

        var resuelto = await servicioDeParametros.ResolverAsync(
            ParametroConocido.ZonaHoraria.Clave, puntoVenta.IdEmpresa, idPuntoVenta, ct);
        var zonaId = JsonSerializer.Deserialize<string>(resuelto.Valor)!;

        return (zonaId, TimeZoneInfo.FindSystemTimeZoneById(zonaId));
    }

    /// <summary>design decisión 16: una zona por punto de venta DISTINTO de la página — el costo
    /// está acotado por el tamaño de página, nunca por fila.</summary>
    private async Task<IReadOnlyDictionary<int, TimeZoneInfo>> ResolverZonasPorPuntoVentaAsync(
        IReadOnlyList<int> idsPuntoVenta, CancellationToken ct)
    {
        var resultado = new Dictionary<int, TimeZoneInfo>();

        foreach (var idPuntoVenta in idsPuntoVenta)
        {
            var (_, zona) = await ResolverZonaAsync(idPuntoVenta, ct);
            resultado[idPuntoVenta] = zona;
        }

        return resultado;
    }

    // ---- resolución de contexto (fuera de transacción, resolvers PRIVADOS PROPIOS — OD9) ---------

    private async Task<PuntoVenta> ResolverPuntoVentaAsync(int idPuntoVenta, CancellationToken ct) =>
        await db.PuntosVenta.FirstOrDefaultAsync(pv => pv.Id == idPuntoVenta, ct)
            // ADR-8: mismo 404 para "no existe" y "es de otro tenant".
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");

    private async Task<Cliente> ResolverClienteAsync(int? idCliente, CancellationToken ct)
    {
        if (idCliente is { } id)
        {
            return await db.Clientes.FirstOrDefaultAsync(c => c.Id == id, ct)
                ?? throw ErrorDominio.NoEncontrado($"No existe el cliente {id}.");
        }

        // Spec: "Omitted idCliente defaults to Consumidor Final" — mismo criterio que
        // ServicioDeVentas.ResolverClienteAsync.
        return await db.Clientes.FirstOrDefaultAsync(c => c.Numero == ReglaDeClientes.NumeroConsumidorFinal, ct)
            ?? throw new InvalidOperationException("El tenant actual no tiene un Consumidor Final sembrado.");
    }

    // ---- proyección ----------------------------------------------------------------------------

    private async Task<PresupuestoDetalle> ProyectarDetalleAsync(
        Presupuesto presupuesto, IReadOnlyList<ItemPresupuesto> items, CancellationToken ct)
    {
        var (zonaId, zona) = await ResolverZonaAsync(presupuesto.IdPuntoVenta, ct);
        var hoy = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(reloj.Ahora, zona).DateTime);

        var vencido = ReglaDePresupuestos.EstaVencido(presupuesto.Estado, presupuesto.Vencimiento, hoy);
        var convertible = ReglaDePresupuestos.EsConvertible(presupuesto.Estado, presupuesto.Vencimiento, hoy);

        // dto-contract-honesty regla 1: la columna existe desde la Slice 1, pero ningún escritor
        // la puebla todavía (Slice 3) — SIEMPRE null en esta slice, honesto por construcción (ver
        // el doc-comment de PresupuestoDetalle.IdComprobanteVenta).
        var idComprobanteVenta = await db.ComprobantesVenta.AsNoTracking()
            .Where(c => c.IdPresupuestoOrigen == presupuesto.Id)
            .Select(c => (int?)c.Id)
            .FirstOrDefaultAsync(ct);

        return new PresupuestoDetalle(
            presupuesto.Id,
            presupuesto.IdPuntoVenta,
            presupuesto.IdCliente,
            presupuesto.IdEmpleado,
            presupuesto.Numero,
            presupuesto.Numero is { } n ? NumeroDeComprobante.Formatear(presupuesto.IdPuntoVenta, n) : null,
            presupuesto.FechaEmision,
            presupuesto.FechaEnvio,
            presupuesto.Vencimiento,
            vencido,
            convertible,
            zonaId,
            presupuesto.Observaciones,
            presupuesto.Subtotal,
            presupuesto.DescuentoTotal,
            presupuesto.Total,
            presupuesto.Estado,
            idComprobanteVenta,
            items
                .OrderBy(i => i.Orden)
                .Select(i => new ItemDePresupuesto(
                    i.Orden, i.IdArticulo, i.Descripcion, i.Cantidad, i.PrecioUnitario, i.Descuento, i.Total,
                    i.IdListaPrecio, i.IdOferta, i.IdAlicuotaIva, i.PorcentajeIva))
                .ToList());
    }

    private static PresupuestoListado ProyectarListado(
        Presupuesto presupuesto, IReadOnlyDictionary<int, TimeZoneInfo> zonasPorPuntoVenta, DateTimeOffset momento)
    {
        var zona = zonasPorPuntoVenta[presupuesto.IdPuntoVenta];
        var hoy = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(momento, zona).DateTime);

        var vencido = ReglaDePresupuestos.EstaVencido(presupuesto.Estado, presupuesto.Vencimiento, hoy);
        var convertible = ReglaDePresupuestos.EsConvertible(presupuesto.Estado, presupuesto.Vencimiento, hoy);

        return new PresupuestoListado(
            presupuesto.Id,
            presupuesto.IdPuntoVenta,
            presupuesto.IdCliente,
            presupuesto.Numero,
            presupuesto.Numero is { } n ? NumeroDeComprobante.Formatear(presupuesto.IdPuntoVenta, n) : null,
            presupuesto.FechaEmision,
            presupuesto.Vencimiento,
            vencido,
            convertible,
            presupuesto.Total,
            presupuesto.Estado);
    }

    private static string? NormalizarOpcional(string? valor)
    {
        var limpio = valor?.Trim();
        return string.IsNullOrEmpty(limpio) ? null : limpio;
    }

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            ?? throw new InvalidOperationException(
                "ServicioDePresupuestos requiere un actor de tenant; OperacionDePos no admite plataforma.");

    /// <summary>Forma intermedia entre la resolución de precio y la entidad persistida — todavía
    /// no conoce <c>IdPresupuesto</c> (no existe hasta el primer <c>SaveChangesAsync</c> del
    /// header, mismo problema de orden que <c>ServicioDeVentas.MaterializarItems</c> resuelve con
    /// <c>LineaDelPlan</c>) ni <c>IdTenant</c>/timestamps (los agrega <see cref="ConstruirItems"/>
    /// al construir las entidades reales).</summary>
    private readonly record struct LineaMaterializada(
        int IdArticulo,
        string Descripcion,
        decimal Cantidad,
        decimal PrecioUnitario,
        decimal Descuento,
        decimal Total,
        int IdListaPrecio,
        int? IdOferta,
        int IdAlicuotaIva,
        decimal PorcentajeIva);
}
