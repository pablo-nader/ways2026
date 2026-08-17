using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Application.Exportacion;
using Ways.Application.Precios;
using Ways.Application.Stock;
using Ways.Domain.Articulos;
using Ways.Domain.Auditoria;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Compras;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Stock;

namespace Ways.Application.Compras;

/// <summary>
/// Ciclo de vida del comprobante de compra — el centerpiece de stage-8 (design: Technical
/// Approach, "the document header row is the serialization point of every state transition of
/// una compra"). Dedicado (no reusa ningún ABM), mismo criterio que
/// <see cref="Ways.Application.Ventas.ServicioDeVentas"/>: <see cref="ConfirmarAsync"/> y
/// <see cref="AnularAsync"/> son el único punto de escritura de <c>movimientos_stock</c>
/// (<c>motivo = compra/anulacion</c>) y de <c>articulos.costo_nominal</c> de esta etapa.
///
/// <see cref="ServicioDeStock"/>/<see cref="Ways.Application.Ventas.ServicioDeVentas"/> NO se
/// tocan (Slice 2 non-negotiable) — los statements crudos de stock de acá son propios de esta
/// clase (sibling raw SQL), duplicados a propósito del shape de
/// <c>ServicioDeStock.InsertarMovimientoStockAsync</c>/<c>UpsertStockAsync</c> en vez de
/// compartir un helper: <c>ServicioDeStock</c> gana esos parámetros recién en Slice 3.
///
/// stage-12-lotes-vencimientos, Slice 5 (design: Write site 2 — recepción): <see
/// cref="ServicioDeLotes"/> se consume por su API pública ESTÁTICA
/// (<c>ServicioDeLotes.ResolverOCrearAsync</c>, mismo criterio que el APPLY-RUN NOTE de la task
/// 3.1 — no requiere una instancia inyectada) bajo la misma <c>conexion</c>/<c>transaccionCruda</c>
/// que ya sostiene el lock del header (design decisión 3: lotes antes que stock). <c>ServicioDeLotes</c>
/// NO se modifica desde acá — API pública tal como quedó en Slice 3.
/// </summary>
public class ServicioDeCompras(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, ServicioDePrecios servicioDePrecios)
{
    // ---- lectura --------------------------------------------------------------------------------

    public async Task<CompraDetalle> ObtenerAsync(int id, CancellationToken ct = default)
    {
        var comprobante = await BuscarComprobanteAsync(id, ct);
        var items = await db.ItemsComprobanteCompra
            .Where(i => i.IdComprobanteCompra == id)
            .OrderBy(i => i.Orden)
            .ToListAsync(ct);

        return Proyectar(comprobante, items);
    }

    public async Task<PaginaDeCompras> ListarAsync(
        int? idProveedor = null,
        EstadoCompra? estado = null,
        DateTimeOffset? desde = null,
        DateTimeOffset? hasta = null,
        int pagina = 1,
        int tamanio = 25,
        CancellationToken ct = default)
    {
        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        var query = ConstruirQuery(idProveedor, estado, desde, hasta);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(c => c.Id)
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .Select(c => new CompraListada(c.Id, c.IdProveedor, c.IdTipoComprobante, c.NumeroExterno, c.Estado, c.FechaRecepcion, c.Total))
            .ToListAsync(ct);

        return new PaginaDeCompras(items, total, pagina, tamanio);
    }

    /// <summary>stage-11-exportacion-reportes (Slice 3, design decisión 7): mismo criterio que
    /// <c>ServicioDeVentas.ListarParaExportacionAsync</c> — <see cref="ConstruirQuery"/>
    /// compartido, <c>Contar → refuse → lectura única con <c>.Take(topeDeFilas + 1)</c></c>. El
    /// segundo <see cref="GuardaDeTope.Exigir"/> es el backstop de carrera contra un
    /// <c>COUNT(*)</c> desactualizado.</summary>
    public async Task<IReadOnlyList<CompraListada>> ListarParaExportacionAsync(
        int? idProveedor,
        EstadoCompra? estado,
        DateTimeOffset? desde,
        DateTimeOffset? hasta,
        int topeDeFilas,
        CancellationToken ct = default)
    {
        var query = ConstruirQuery(idProveedor, estado, desde, hasta);

        var cantidad = await query.CountAsync(ct);
        GuardaDeTope.Exigir(cantidad, topeDeFilas);

        var items = await query
            .OrderByDescending(c => c.Id)
            .Take(topeDeFilas + 1)
            .Select(c => new CompraListada(c.Id, c.IdProveedor, c.IdTipoComprobante, c.NumeroExterno, c.Estado, c.FechaRecepcion, c.Total))
            .ToListAsync(ct);

        GuardaDeTope.Exigir(items.Count, topeDeFilas);

        return items;
    }

    /// <summary>Filtro compartido de <see cref="ListarAsync"/> y
    /// <see cref="ListarParaExportacionAsync"/> (design decisión 7).</summary>
    private IQueryable<ComprobanteCompra> ConstruirQuery(
        int? idProveedor, EstadoCompra? estado, DateTimeOffset? desde, DateTimeOffset? hasta)
    {
        var query = db.ComprobantesCompra.AsQueryable();

        if (idProveedor is { } p)
        {
            query = query.Where(c => c.IdProveedor == p);
        }

        if (estado is { } e)
        {
            query = query.Where(c => c.Estado == e);
        }

        if (desde is { } d)
        {
            query = query.Where(c => c.FechaRecepcion >= d);
        }

        if (hasta is { } h)
        {
            query = query.Where(c => c.FechaRecepcion <= h);
        }

        return query;
    }

    // ---- borrador: crear + replace-set (design decisión 2) ---------------------------------------

    public async Task<CompraDetalle> CrearBorradorAsync(SolicitudDeCompra solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        // Etapa 12, slice 5 (spec comprobantes-compra: "Expired Reception Is Refused") — chequeo
        // puro, sin base de datos, ANTES de cualquier lectura: fecha_vencimiento en el pasado se
        // rechaza al guardar, no solo al confirmar.
        ValidarVencimientosDeRecepcion(solicitud.Items, DateOnly.FromDateTime(momento.UtcDateTime));

        var (tipo, _, _, _, porcentajePorAlicuota, margenes) = await ResolverContextoAsync(solicitud, ct);
        var (lineas, calculada) = Calcular(solicitud.Items, tipo.DiscriminaIva, porcentajePorAlicuota, margenes);

        var comprobante = new ComprobanteCompra
        {
            IdTenant = idTenant,
            IdProveedor = solicitud.IdProveedor,
            IdTipoComprobante = solicitud.IdTipoComprobante,
            NumeroExterno = NormalizarOpcional(solicitud.NumeroExterno),
            FechaComprobante = solicitud.FechaComprobante,
            FechaRecepcion = null,
            IdPuntoVenta = solicitud.IdPuntoVenta,
            IdEmpleado = idEmpleado,
            Subtotal = calculada.Subtotal,
            DescuentoTotal = calculada.DescuentoTotal,
            IvaTotal = calculada.IvaTotal,
            Total = calculada.Total,
            Observaciones = NormalizarOpcional(solicitud.Observaciones),
            Estado = EstadoCompra.Borrador,
            CreatedAt = momento,
            UpdatedAt = momento
        };
        db.ComprobantesCompra.Add(comprobante);
        await db.SaveChangesAsync(ct);

        var itemsEntidad = MaterializarItems(comprobante.Id, idTenant, lineas, calculada, solicitud.Items, momento);
        db.ItemsComprobanteCompra.AddRange(itemsEntidad);
        await db.SaveChangesAsync(ct);

        return Proyectar(comprobante, itemsEntidad);
    }

    /// <summary>Design decisión 2: replace-set completo bajo <c>SELECT … FOR UPDATE … WHERE
    /// estado='borrador'</c> — el lock de fila hace que "el último committer gana" sea una
    /// garantía real (no una carrera) y el predicado de estado en el mismo statement hace que
    /// editar una confirmada sea estructuralmente imposible, no solo chequeado.</summary>
    public async Task<CompraDetalle> ActualizarBorradorAsync(int id, SolicitudDeCompra solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var momento = reloj.Ahora;

        // Etapa 12, slice 5: mismo chequeo que CrearBorradorAsync — un PUT también es un "save".
        ValidarVencimientosDeRecepcion(solicitud.Items, DateOnly.FromDateTime(momento.UtcDateTime));

        var (tipo, _, _, _, porcentajePorAlicuota, margenes) = await ResolverContextoAsync(solicitud, ct);
        var (lineas, calculada) = Calcular(solicitud.Items, tipo.DiscriminaIva, porcentajePorAlicuota, margenes);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarActualizacionAsync(id, idTenant, solicitud, lineas, calculada, momento, ct));
    }

    private async Task<CompraDetalle> EjecutarActualizacionAsync(
        int id, int idTenant, SolicitudDeCompra solicitud, IReadOnlyList<LineaDeCompra> lineas, CompraCalculada calculada,
        DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        var bloqueado = await BloquearBorradorAsync(conexion, transaccionCruda, id, idTenant, ct);
        if (!bloqueado)
        {
            var existe = await db.ComprobantesCompra.AsNoTracking().AnyAsync(c => c.Id == id, ct);
            if (!existe)
            {
                throw ErrorDominio.NoEncontrado($"No existe la compra {id}.");
            }

            throw new ErrorDominio("compra_no_editable", "Solo una compra en borrador puede editarse.", 409);
        }

        // El lock de fila crudo de arriba ya serializa cualquier escritor concurrente sobre este
        // header — esta lectura vía EF (sin FOR UPDATE propio) es segura, ve el estado ya
        // comiteado bajo el mismo lock (mismo criterio que ServicioDePrecios.BuscarFilaAbiertaAsync
        // tras TomarLockDelParAsync).
        var comprobante = await db.ComprobantesCompra.FirstAsync(c => c.Id == id, ct);

        var itemsExistentes = await db.ItemsComprobanteCompra.Where(i => i.IdComprobanteCompra == id).ToListAsync(ct);
        db.ItemsComprobanteCompra.RemoveRange(itemsExistentes);

        comprobante.IdProveedor = solicitud.IdProveedor;
        comprobante.IdTipoComprobante = solicitud.IdTipoComprobante;
        comprobante.NumeroExterno = NormalizarOpcional(solicitud.NumeroExterno);
        comprobante.FechaComprobante = solicitud.FechaComprobante;
        comprobante.IdPuntoVenta = solicitud.IdPuntoVenta;
        comprobante.Observaciones = NormalizarOpcional(solicitud.Observaciones);
        comprobante.Subtotal = calculada.Subtotal;
        comprobante.DescuentoTotal = calculada.DescuentoTotal;
        comprobante.IvaTotal = calculada.IvaTotal;
        comprobante.Total = calculada.Total;
        comprobante.UpdatedAt = momento;

        var itemsNuevos = MaterializarItems(id, idTenant, lineas, calculada, solicitud.Items, momento);
        db.ItemsComprobanteCompra.AddRange(itemsNuevos);

        await db.SaveChangesAsync(ct);
        await transaccion.CommitAsync(ct);

        return Proyectar(comprobante, itemsNuevos);
    }

    // ---- confirmar (design: Transactions — CONFIRMAR COMPRA) --------------------------------------

    public async Task<CompraDetalle> ConfirmarAsync(int id, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        // Camino secuencial (spec: canónico) — una compra visiblemente ya procesada se rechaza
        // ACÁ, antes de entrar a la transacción, con el código que el spec pinea
        // (compra_ya_procesada). El UPDATE...RETURNING atómico de abajo sigue siendo la única
        // autoridad race-safe: si otro confirmar gana la carrera entre esta lectura y ese UPDATE,
        // la rama de 0 filas lo atrapa con el código genérico del backstop
        // (compra_no_es_borrador, design: Transactions — "double confirm... the loser").
        var preLectura = await db.ComprobantesCompra.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (preLectura is null)
        {
            throw ErrorDominio.NoEncontrado($"No existe la compra {id}.");
        }

        if (preLectura.Estado != EstadoCompra.Borrador)
        {
            throw new ErrorDominio("compra_ya_procesada", "La compra ya fue procesada.", 409);
        }

        // spec: "Confirming without a numero_externo is rejected... before any write" — chequeo
        // de servicio explícito, distinto del backstop de esquema
        // (ck_comprobantes_compra_confirmada_completa → compra_incompleta_para_confirmar), que
        // queda como defensa de una escritura fuera de banda. fecha_comprobante comparte la
        // misma CHECK de esquema (sin código propio pineado por el spec), así que reusa el
        // código del backstop.
        if (preLectura.NumeroExterno is null)
        {
            throw new ErrorDominio(
                "compra_numero_externo_requerido",
                "La compra necesita un número de comprobante del proveedor para confirmarse.",
                400);
        }

        if (preLectura.FechaComprobante is null)
        {
            throw new ErrorDominio(
                "compra_incompleta_para_confirmar",
                "La compra necesita una fecha de comprobante para confirmarse.",
                400);
        }

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarConfirmarAsync(id, idTenant, idEmpleado, momento, ct));
    }

    private async Task<CompraDetalle> EjecutarConfirmarAsync(
        int id, int idTenant, int idEmpleado, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        // 1. UPDATE ... RETURNING — autoridad única de la transición (design decisión 1). El
        // lock de fila serializa dos confirmar concurrentes: el que pierde re-evalúa el WHERE
        // contra el estado YA COMITEADO por el ganador, 0 filas, nunca un 500 ni una doble
        // escritura de stock. La RETURNING trae id_tipo_comprobante — el valor que ESTE lock
        // vio, nunca el leído antes de entrar a la transacción (design: Transactions — CONFIRMAR
        // COMPRA): un PUT concurrente puede cambiarlo entre el pre-chequeo de ConfirmarAsync y
        // este lock, y discrimina_iva se resuelve recién acá adentro con ese valor. El
        // WHERE de este UPDATE exige además numero_externo/fecha_comprobante NOT NULL (ver el
        // doc-comment de ConfirmarHeaderAsync), así que 0 filas puede deberse a dos motivos
        // distintos que hay que reclasificar bajo lock: la compra ya no está en borrador, o
        // sigue en borrador pero un PUT concurrente la dejó incompleta.
        if (await ConfirmarHeaderAsync(conexion, transaccionCruda, id, idTenant, momento, ct) is not { } encabezado)
        {
            var actual = await db.ComprobantesCompra.AsNoTracking()
                .Where(c => c.Id == id)
                .Select(c => (EstadoCompra?)c.Estado)
                .FirstOrDefaultAsync(ct);

            if (actual is null)
            {
                throw ErrorDominio.NoEncontrado($"No existe la compra {id}.");
            }

            if (actual == EstadoCompra.Borrador)
            {
                throw new ErrorDominio(
                    "compra_incompleta_para_confirmar",
                    "La compra necesita número de comprobante y fecha para confirmarse.",
                    400);
            }

            throw new ErrorDominio("compra_no_es_borrador", "La compra ya no está en borrador.", 409);
        }

        // 2. El read set de items queda congelado bajo el lock del header (design decisión 1).
        var items = await db.ItemsComprobanteCompra
            .Where(i => i.IdComprobanteCompra == id)
            .OrderBy(i => i.IdArticulo)
            .ToListAsync(ct);

        if (items.Count == 0)
        {
            throw new ErrorDominio("compra_sin_items", "La compra no tiene items para confirmar.", 400);
        }

        // discriminaIva del tipo QUE VIO este lock (encabezado.IdTipoComprobante), nunca el
        // resuelto antes de entrar a la transacción — un PUT concurrente que cambia el tipo
        // (p.ej. C-FB → C-FA) entre el pre-chequeo y este lock no puede corromper costo_nominal
        // con un discriminaIva stale (design: Transactions — CONFIRMAR COMPRA, paso 1).
        var tipo = await db.TiposComprobante.FirstAsync(t => t.Id == encabezado.IdTipoComprobante, ct);
        var discriminaIva = tipo.DiscriminaIva;

        // 2.b Resolución de lotes (etapa 12, slice 5; design decisión 3: lotes ANTES que stock) —
        // bajo el MISMO lock del header que el paso 1 ya tomó, antes del primer lock de stock del
        // paso 3. Orden ascendente (id_articulo, codigo_lote) para que dos confirmaciones
        // concurrentes que comparten códigos de lote tomen esas filas en el mismo orden.
        var idsArticulo = items.Select(i => i.IdArticulo).Distinct().ToList();

        // EsLoteEfectivo necesita controla_lote por artículo y lotes_habilitado de la empresa del
        // encabezado — ambos FUERA del presupuesto de comandos del checkout (design: Write site
        // 2), esta clase no comparte ese presupuesto.
        var idEmpresa = await db.PuntosVenta
            .Where(pv => pv.Id == encabezado.IdPuntoVenta)
            .Select(pv => pv.IdEmpresa)
            .FirstAsync(ct);
        var controlaLotePorArticulo = await db.Articulos
            .Where(a => idsArticulo.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.ControlaLote, ct);
        var lotesHabilitado = await ResolverLotesHabilitadoAsync(idEmpresa, encabezado.IdPuntoVenta, ct);

        // Honestidad documental: "hoy" acá es UTC naive (interino por diseño, mismo criterio que
        // ServicioDeLotes.ListarAsync/CrearAsync — slice 3) y no la zona_horaria del PV. El
        // reporte de vencimientos (slice 13) SÍ resuelve "hoy" en la zona_horaria del PV; este
        // rechequeo de confirmación no necesita esa precisión.
        var hoy = DateOnly.FromDateTime(momento.UtcDateTime);

        var itemsLoteEfectivos = items
            .Where(i => ReglaDeLotes.ControlEfectivo(controlaLotePorArticulo.GetValueOrDefault(i.IdArticulo), lotesHabilitado))
            .OrderBy(i => i.IdArticulo)
            .ThenBy(i => i.CodigoLote);

        foreach (var item in itemsLoteEfectivos)
        {
            if (item.FechaVencimiento is null)
            {
                throw new ErrorDominio(
                    "lote_requerido",
                    $"El artículo {item.IdArticulo} controla lote; la línea necesita codigo_lote/fecha_vencimiento para confirmarse.",
                    400);
            }

            // Rechequeo al confirmar (spec: "This check MUST fire when the line is saved or
            // edited, not only at confirm") — el guardado del borrador ya lo probó una vez, pero
            // el reloj pudo avanzar entre el último save y este confirm.
            if (ReglaDeLotes.EstaVencido(item.FechaVencimiento, hoy))
            {
                throw new ErrorDominio(
                    "lote_vencido_en_recepcion",
                    $"La fecha de vencimiento del artículo {item.IdArticulo} ya pasó; una recepción no puede " +
                    "ingresar mercadería vencida.",
                    409);
            }

            item.IdLote = await ServicioDeLotes.ResolverOCrearAsync(
                conexion, transaccionCruda, idTenant, item.IdArticulo, item.CodigoLote, item.FechaVencimiento.Value,
                momento, ct);
        }

        // Congela item.IdLote (entidades trackeadas por el read set del paso 2) antes del loop de
        // stock, que lo lee en memoria para el orden y el upsert — sin esto los UPDATEs de
        // id_lote nunca se emitirían.
        await db.SaveChangesAsync(ct);

        // 3. Un movimiento + upsert de stock por item, orden ascendente (id_articulo, id_lote)
        // (design decisión 8/9; Transactions — lock order discipline). El upsert agregado corre
        // SIEMPRE, lot-effective o no (byte-idéntico al camino previo a esta etapa cuando no lo
        // es); el upsert de stock_lotes solo cuando el item resolvió un lote.
        foreach (var item in items.OrderBy(i => i.IdArticulo).ThenBy(i => i.IdLote))
        {
            await InsertarMovimientoStockAsync(
                conexion, transaccionCruda, idTenant, item.IdArticulo, encabezado.IdPuntoVenta, item.Cantidad,
                MotivoStock.Compra, id, idEmpleado, momento, item.IdLote, ct);

            await UpsertStockAsync(conexion, transaccionCruda, idTenant, item.IdArticulo, encabezado.IdPuntoVenta, item.Cantidad, ct);

            if (item.IdLote is { } idLote)
            {
                await UpsertStockLoteAsync(
                    conexion, transaccionCruda, idTenant, item.IdArticulo, encabezado.IdPuntoVenta, idLote, item.Cantidad, ct);
            }
        }

        // 4. costo_nominal — solo actualiza_costo AND costo_unitario > 0, deduplicado con el
        // mayor orden ganando (design decisión 4; CalculadorDeCompra.ResolverActualizacionesDeCosto).
        var itemsParaCosto = items
            .Select(i => (
                i.Orden, i.IdArticulo, i.ActualizaCosto, i.CostoUnitario,
                CostoEfectivo: CalculadorDeCompra.CalcularCostoEfectivoDesdeItem(i.Total, i.Cantidad, i.PorcentajeIva, discriminaIva)))
            .ToList();

        var costosAActualizar = CalculadorDeCompra.ResolverActualizacionesDeCosto(itemsParaCosto);

        foreach (var (idArticulo, costo) in costosAActualizar.OrderBy(kv => kv.Key))
        {
            await ActualizarCostoNominalAsync(conexion, transaccionCruda, idTenant, idArticulo, costo, momento, ct);
        }

        await transaccion.CommitAsync(ct);

        return await ObtenerAsync(id, ct);
    }

    // ---- anular (design: Transactions — ANULAR COMPRA; decisión 6, la regla invertida) -----------

    public async Task<ResultadoAnulacion> AnularAsync(int id, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var preLectura = await db.ComprobantesCompra.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (preLectura is null)
        {
            throw ErrorDominio.NoEncontrado($"No existe la compra {id}.");
        }

        // spec: "Anulando a borrador is rejected... 409 compra_no_procesada — a borrador has no
        // ledger effect to reverse" — el código canónico del scenario, distinto del backstop
        // atómico genérico de abajo.
        if (preLectura.Estado == EstadoCompra.Borrador)
        {
            throw new ErrorDominio(
                "compra_no_procesada", "Una compra en borrador no tiene movimientos que revertir.", 409);
        }

        if (preLectura.Estado == EstadoCompra.Anulada)
        {
            throw new ErrorDominio("compra_no_confirmada", "La compra ya está anulada.", 409);
        }

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () => await EjecutarAnulacionAsync(id, idTenant, idEmpleado, momento, ct));
    }

    private async Task<ResultadoAnulacion> EjecutarAnulacionAsync(
        int id, int idTenant, int idEmpleado, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        // 1. UPDATE ... RETURNING — misma autoridad única que confirmar.
        var idPuntoVenta = await MarcarAnuladaAsync(conexion, transaccionCruda, id, idTenant, momento, ct);
        if (idPuntoVenta is null)
        {
            var existe = await db.ComprobantesCompra.AsNoTracking().AnyAsync(c => c.Id == id, ct);
            if (!existe)
            {
                throw ErrorDominio.NoEncontrado($"No existe la compra {id}.");
            }

            throw new ErrorDominio("compra_no_confirmada", "La compra no está confirmada.", 409);
        }

        // 1.5. Auditoría (stage-14-auditoria-trazabilidad, Slice 3; spec auditoria-de-operaciones;
        // design call site 8) — MISMA transacción cruda; id_punto_venta sale del RETURNING que
        // MarcarAnuladaAsync YA devuelve (sin cambios en ese método), nunca de una lectura extra.
        // ServicioDeAuditoria se instancia local con los mismos db/reloj/contexto de este
        // servicio, mismo criterio que ServicioDeVentas.EjecutarAnulacionAsync.
        var servicioDeAuditoriaAnulacionCompra = new ServicioDeAuditoria(db, reloj, contexto);
        var (valorAnteriorCompraAnulacion, valorNuevoCompraAnulacion) =
            PayloadDeAuditoria.AnulacionDeCompra(EstadoCompra.Confirmada, EstadoCompra.Anulada);
        await servicioDeAuditoriaAnulacionCompra.RegistrarAsync(
            conexion, transaccionCruda,
            new RegistroDeAuditoria(
                idTenant, idPuntoVenta, AccionAuditada.CompraAnulacion, id,
                valorAnteriorCompraAnulacion, valorNuevoCompraAnulacion),
            ct);

        // 2. El ledger ORIGINAL, nunca recalculado desde items (design: doc-comment de
        // ServicioDeVentas.AnularAsync, mismo criterio acá). Orden ascendente (id_articulo,
        // id_lote) — etapa 12, slice 6, mismo criterio de lock que el confirmar (decisión 8/9).
        var movimientosOriginales = await db.MovimientosStock
            .Where(m => m.IdComprobanteCompra == id && m.Motivo == MotivoStock.Compra)
            .OrderBy(m => m.IdArticulo)
            .ThenBy(m => m.IdLote)
            .ToListAsync(ct);

        foreach (var original in movimientosOriginales)
        {
            var inversa = -original.Cantidad;

            // Reversa EXACTA por lote (etapa 12, slice 6, reemplaza el guard interino de slice 5
            // FIX 4): el movimiento original ya trae su propio id_lote — la reversa lo copia
            // estructuralmente, sin re-derivar nada (design: "Exactness is structural, not
            // derived").
            await InsertarMovimientoStockAsync(
                conexion, transaccionCruda, idTenant, original.IdArticulo, original.IdPuntoVenta, inversa,
                MotivoStock.Anulacion, id, idEmpleado, momento, original.IdLote, ct);

            var nueva = await UpsertStockAsync(
                conexion, transaccionCruda, idTenant, original.IdArticulo, original.IdPuntoVenta, inversa, ct);

            if (nueva < 0m)
            {
                throw new ErrorDominio(
                    "compra_anulacion_stock_negativo",
                    $"El artículo {original.IdArticulo} quedaría con stock negativo al anular esta compra.",
                    409);
            }

            // Chequeo por lote (spec comprobantes-compra: "the negative-balance refusal MUST also
            // apply at the lot level... even if the articulo's aggregate stock.cantidad would stay
            // non-negative") — un agregado suficiente puede esconder un lote específico ya vendido
            // por debajo de la cantidad que esta anulación intenta devolverle.
            if (original.IdLote is { } idLoteReversa)
            {
                var nuevaDelLote = await UpsertStockLoteAsync(
                    conexion, transaccionCruda, idTenant, original.IdArticulo, original.IdPuntoVenta, idLoteReversa,
                    inversa, ct);

                if (nuevaDelLote < 0m)
                {
                    throw new ErrorDominio(
                        "compra_anulacion_stock_negativo",
                        $"El lote {idLoteReversa} del artículo {original.IdArticulo} quedaría con stock " +
                        "negativo al anular esta compra.",
                        409);
                }
            }
        }

        // 4. Informativo — la regla invertida (design decisión 6): NUNCA bloquea.
        var gastosLigados = await db.Gastos.CountAsync(g => g.IdComprobanteCompra == id, ct);

        await transaccion.CommitAsync(ct);

        var detalle = await ObtenerAsync(id, ct);
        return new ResultadoAnulacion(detalle, gastosLigados);
    }

    // ---- aplicar precio sugerido (design decisión 8) -----------------------------------------------

    /// <summary>Loop de <c>AbrirNuevoPrecioAsync</c>, cada llamada su PROPIA transacción — un
    /// rechazo de una línea (p.ej. <c>precio_pendiente_existe</c>) no aborta las demás (design
    /// decisión 8: partial success es el contrato honesto).</summary>
    public async Task<IReadOnlyList<ResultadoAplicarPrecio>> AplicarPrecioSugeridoAsync(
        int id, SolicitudDeAplicarPrecios solicitud, CancellationToken ct = default)
    {
        var comprobante = await BuscarComprobanteAsync(id, ct);
        if (comprobante.Estado != EstadoCompra.Confirmada)
        {
            throw new ErrorDominio(
                "compra_no_confirmada", "Solo una compra confirmada tiene precio_sugerido para aplicar.", 409);
        }

        var items = await db.ItemsComprobanteCompra
            .Where(i => i.IdComprobanteCompra == id && i.PrecioSugerido != null)
            .OrderBy(i => i.Orden)
            .ToListAsync(ct);

        var resultados = new List<ResultadoAplicarPrecio>(items.Count);

        foreach (var item in items)
        {
            try
            {
                var precio = await servicioDePrecios.EstablecerPrecioAsync(
                    item.IdArticulo,
                    new AltaPrecio(solicitud.IdListaPrecio, item.PrecioSugerido!.Value, solicitud.ConfirmarReemplazo),
                    ct);

                resultados.Add(new ResultadoAplicarPrecio(item.IdArticulo, true, precio.Precio, null));
            }
            catch (ErrorDominio error)
            {
                resultados.Add(new ResultadoAplicarPrecio(item.IdArticulo, false, null, error.Message));
            }
        }

        return resultados;
    }

    // ---- statements crudos: confirmar/anular (sibling raw SQL, ver el doc-comment de la clase) ----

    /// <summary>Fila devuelta por el UPDATE...RETURNING de <see cref="ConfirmarHeaderAsync"/> —
    /// design: Transactions — CONFIRMAR COMPRA, paso 1. Los valores son los que ESTE lock vio,
    /// nunca los leídos antes de entrar a la transacción. Solo se devuelven las columnas que la
    /// transacción consume: la completitud de numero_externo/fecha_comprobante ya la valida el
    /// propio predicado del UPDATE, así que devolverlas sería código muerto.</summary>
    private readonly record struct EncabezadoConfirmado(int IdPuntoVenta, int IdTipoComprobante);

    /// <summary>El predicado incluye <c>numero_externo</c>/<c>fecha_comprobante IS NOT NULL</c>
    /// además de <c>estado='borrador'</c> — validación bajo el mismo lock, resuelta por el propio
    /// predicado del UPDATE en vez de un chequeo posterior en C#: sin esto, un PUT concurrente que
    /// vacía esas columnas entre el pre-chequeo amistoso de <see cref="ConfirmarAsync"/> y este
    /// lock haría que este UPDATE viole <c>ck_comprobantes_compra_confirmada_completa</c> — un
    /// <c>PostgresException</c> crudo (nunca envuelto en <c>DbUpdateException</c>, a diferencia del
    /// camino de EF <c>SaveChanges</c>) que <c>ManejadorDeErrores</c> no puede clasificar y deja
    /// pasar como 500. <c>Ways.Application</c> no referencia Npgsql (Npgsql solo vive en
    /// Infrastructure), así que la única forma de evitar ese 500 sin acoplar la capa es que el
    /// propio predicado impida la violación en vez de atraparla después.</summary>
    private static async Task<EncabezadoConfirmado?> ConfirmarHeaderAsync(
        DbConnection conexion, DbTransaction? transaccion, int id, int idTenant, DateTimeOffset momento, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "UPDATE comprobantes_compra SET estado = 'confirmada'::estado_compra, fecha_recepcion = $1, updated_at = $1 " +
            "WHERE id_comprobante_compra = $2 AND id_tenant = $3 AND estado = 'borrador'::estado_compra " +
            "AND numero_externo IS NOT NULL AND fecha_comprobante IS NOT NULL " +
            "RETURNING id_punto_venta, id_tipo_comprobante";

        ParametrosDeComando.Agregar(comando, momento);
        ParametrosDeComando.Agregar(comando, id);
        ParametrosDeComando.Agregar(comando, idTenant);

        await using var lector = await comando.ExecuteReaderAsync(ct);
        if (!await lector.ReadAsync(ct))
        {
            return null;
        }

        return new EncabezadoConfirmado(lector.GetInt32(0), lector.GetInt32(1));
    }

    private static async Task<int?> MarcarAnuladaAsync(
        DbConnection conexion, DbTransaction? transaccion, int id, int idTenant, DateTimeOffset momento, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "UPDATE comprobantes_compra SET estado = 'anulada'::estado_compra, updated_at = $1 " +
            "WHERE id_comprobante_compra = $2 AND id_tenant = $3 AND estado = 'confirmada'::estado_compra " +
            "RETURNING id_punto_venta";

        ParametrosDeComando.Agregar(comando, momento);
        ParametrosDeComando.Agregar(comando, id);
        ParametrosDeComando.Agregar(comando, idTenant);

        var resultado = await comando.ExecuteScalarAsync(ct);
        return resultado is null ? null : Convert.ToInt32(resultado);
    }

    private static async Task<bool> BloquearBorradorAsync(
        DbConnection conexion, DbTransaction? transaccion, int id, int idTenant, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT 1 FROM comprobantes_compra " +
            "WHERE id_comprobante_compra = $1 AND id_tenant = $2 AND estado = 'borrador'::estado_compra " +
            "FOR UPDATE";

        ParametrosDeComando.Agregar(comando, id);
        ParametrosDeComando.Agregar(comando, idTenant);

        var resultado = await comando.ExecuteScalarAsync(ct);
        return resultado is not null;
    }

    private static async Task InsertarMovimientoStockAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idArticulo, int idPuntoVenta,
        decimal cantidad, MotivoStock motivo, int idComprobanteCompra, int idEmpleado, DateTimeOffset creadoEl,
        int? idLote, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO movimientos_stock " +
            "(id_tenant, id_articulo, id_punto_venta, cantidad, motivo, id_comprobante_compra, id_empleado, creado_el, id_lote) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)";

        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, idArticulo);
        ParametrosDeComando.Agregar(comando, idPuntoVenta);
        ParametrosDeComando.Agregar(comando, cantidad);
        ParametrosDeComando.Agregar(comando, motivo);
        ParametrosDeComando.Agregar(comando, idComprobanteCompra);
        ParametrosDeComando.Agregar(comando, idEmpleado);
        ParametrosDeComando.Agregar(comando, creadoEl);
        ParametrosDeComando.AgregarNulo(comando, idLote);

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

        ParametrosDeComando.Agregar(comando, idArticulo);
        ParametrosDeComando.Agregar(comando, idPuntoVenta);
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, delta);

        var resultado = await comando.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("El upsert de stock no devolvió ninguna fila.");

        return Convert.ToDecimal(resultado);
    }

    /// <summary>Etapa 12, slice 5 (design: Write site 2 — "UpsertStockLoteAsync: la MISMA forma
    /// que UpsertStockAsync, una clave más") — sibling raw SQL propio de esta clase, mismo shape
    /// que <c>ServicioDeVentas.UpsertStockLoteAsync</c> (Slice 8) y
    /// <c>ServicioDeLotes.ResolverOCrearAsync</c> (Slice 3): <c>INSERT ... ON CONFLICT DO UPDATE
    /// ... RETURNING</c>, nunca <c>DO NOTHING</c>.</summary>
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

        ParametrosDeComando.Agregar(comando, idArticulo);
        ParametrosDeComando.Agregar(comando, idPuntoVenta);
        ParametrosDeComando.Agregar(comando, idLote);
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, delta);

        var resultado = await comando.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("El upsert de stock_lotes no devolvió ninguna fila.");

        return Convert.ToDecimal(resultado);
    }

    private static async Task ActualizarCostoNominalAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idArticulo, decimal costoNominal,
        DateTimeOffset momento, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "UPDATE articulos SET costo_nominal = $1, updated_at = $2 WHERE id_articulo = $3 AND id_tenant = $4";

        ParametrosDeComando.Agregar(comando, costoNominal);
        ParametrosDeComando.Agregar(comando, momento);
        ParametrosDeComando.Agregar(comando, idArticulo);
        ParametrosDeComando.Agregar(comando, idTenant);

        await comando.ExecuteNonQueryAsync(ct);
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

    // ---- resolución de contexto (fuera de transacción) ---------------------------------------------

    private async Task<(
        TipoComprobante Tipo, Proveedor Proveedor, PuntoVenta PuntoVenta,
        IReadOnlyDictionary<int, Articulo> ArticuloPorId, IReadOnlyDictionary<int, decimal> PorcentajePorAlicuota,
        IReadOnlyDictionary<int, (decimal? MargenGrupo, decimal? MargenProveedor)> Margenes)>
        ResolverContextoAsync(SolicitudDeCompra solicitud, CancellationToken ct)
    {
        var tipo = await ResolverTipoDeCompraAsync(solicitud.IdTipoComprobante, ct);
        var proveedor = await ResolverProveedorAsync(solicitud.IdProveedor, ct);
        var puntoVenta = await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);

        var idsArticulo = solicitud.Items.Select(i => i.IdArticulo).Distinct().ToList();
        var articuloPorId = idsArticulo.Count == 0
            ? new Dictionary<int, Articulo>()
            : await db.Articulos.Where(a => idsArticulo.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);

        var idsArticuloFaltantes = idsArticulo.Except(articuloPorId.Keys).ToList();
        if (idsArticuloFaltantes.Count > 0)
        {
            throw new ErrorDominio("referencia_invalida", $"No existe el artículo {idsArticuloFaltantes[0]}.", 400);
        }

        var idsAlicuota = solicitud.Items.Select(i => i.IdAlicuotaIva).Distinct().ToList();
        var porcentajePorAlicuota = idsAlicuota.Count == 0
            ? new Dictionary<int, decimal>()
            : await db.AlicuotasIva.Where(a => idsAlicuota.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Porcentaje, ct);

        var idsAlicuotaFaltantes = idsAlicuota.Except(porcentajePorAlicuota.Keys).ToList();
        if (idsAlicuotaFaltantes.Count > 0)
        {
            throw new ErrorDominio("referencia_invalida", $"No existe la alícuota de IVA {idsAlicuotaFaltantes[0]}.", 400);
        }

        var margenes = await ResolverMargenesAsync(articuloPorId, ct);

        return (tipo, proveedor, puntoVenta, articuloPorId, porcentajePorAlicuota, margenes);
    }

    private async Task<IReadOnlyDictionary<int, (decimal? MargenGrupo, decimal? MargenProveedor)>> ResolverMargenesAsync(
        IReadOnlyDictionary<int, Articulo> articuloPorId, CancellationToken ct)
    {
        var idsGrupo = articuloPorId.Values.Where(a => a.IdGrupo is not null).Select(a => a.IdGrupo!.Value).Distinct().ToList();
        var margenPorGrupo = idsGrupo.Count == 0
            ? new Dictionary<int, decimal?>()
            : await db.Grupos.Where(g => idsGrupo.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Margen, ct);

        var idsProveedor = articuloPorId.Values
            .Where(a => a.IdProveedorHabitual is not null)
            .Select(a => a.IdProveedorHabitual!.Value)
            .Distinct()
            .ToList();
        var margenPorProveedor = idsProveedor.Count == 0
            ? new Dictionary<int, decimal?>()
            : await db.Proveedores.Where(p => idsProveedor.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Margen, ct);

        return articuloPorId.ToDictionary(
            kv => kv.Key,
            kv => (
                kv.Value.IdGrupo is { } g && margenPorGrupo.TryGetValue(g, out var mg) ? mg : (decimal?)null,
                kv.Value.IdProveedorHabitual is { } p && margenPorProveedor.TryGetValue(p, out var mp) ? mp : (decimal?)null));
    }

    private static (IReadOnlyList<LineaDeCompra> Lineas, CompraCalculada Calculada) Calcular(
        IReadOnlyList<LineaDeCompraSolicitada> items, bool discriminaIva,
        IReadOnlyDictionary<int, decimal> porcentajePorAlicuota,
        IReadOnlyDictionary<int, (decimal? MargenGrupo, decimal? MargenProveedor)> margenes)
    {
        var orden = 1;
        var lineas = items
            .Select(i => new LineaDeCompra(
                orden++, i.IdArticulo, i.Descripcion, i.Unidades, i.Bultos, i.UnidadesPorBulto,
                i.CostoUnitario, i.Descuento, i.IdAlicuotaIva, porcentajePorAlicuota[i.IdAlicuotaIva], i.ActualizaCosto))
            .ToList();

        var calculada = CalculadorDeCompra.Calcular(lineas, discriminaIva, margenes);
        return (lineas, calculada);
    }

    /// <summary>Etapa 12, slice 5 (design: Write site 2 — "codigo_lote y fecha_vencimiento pasan
    /// derecho por MaterializarItems, sin resolución"): <paramref name="solicitudItems"/> viaja en
    /// paralelo a <paramref name="lineas"/>/<paramref name="calculada"/> (mismo orden, mismo
    /// índice — <c>Calcular</c> los produjo a partir de la misma lista) solo para llevar el input
    /// crudo de lote hasta la entidad; <c>CalculadorDeCompra</c> (Domain, aritmética pura) no
    /// necesita saber que existen.</summary>
    private static List<ItemComprobanteCompra> MaterializarItems(
        int idComprobante, int idTenant, IReadOnlyList<LineaDeCompra> lineas, CompraCalculada calculada,
        IReadOnlyList<LineaDeCompraSolicitada> solicitudItems, DateTimeOffset momento)
    {
        var items = new List<ItemComprobanteCompra>(lineas.Count);

        for (var i = 0; i < lineas.Count; i++)
        {
            var linea = lineas[i];
            var item = calculada.Items[i];
            var solicitudItem = solicitudItems[i];

            items.Add(new ItemComprobanteCompra
            {
                IdTenant = idTenant,
                IdComprobanteCompra = idComprobante,
                Orden = linea.Orden,
                IdArticulo = linea.IdArticulo,
                Descripcion = linea.Descripcion,
                Cantidad = item.Cantidad,
                Bultos = linea.Bultos,
                UnidadesPorBulto = linea.UnidadesPorBulto,
                CostoUnitario = linea.CostoUnitario,
                Descuento = linea.Descuento,
                IdAlicuotaIva = linea.IdAlicuotaIva,
                PorcentajeIva = linea.PorcentajeIva,
                Total = item.Total,
                ActualizaCosto = linea.ActualizaCosto,
                PrecioSugerido = item.PrecioSugerido,
                CodigoLote = NormalizarOpcional(solicitudItem.CodigoLote),
                FechaVencimiento = solicitudItem.FechaVencimiento,
                CreatedAt = momento,
                UpdatedAt = momento
            });
        }

        return items;
    }

    /// <summary>Chequeo puro (spec comprobantes-compra: "Expired Reception Is Refused") — corre
    /// ANTES de cualquier lectura de base de datos, en cada guardado de borrador (creación o
    /// edición), no solo al confirmar. Deliberadamente incondicional a <c>controla_lote</c>: el
    /// esquema (<c>ck_items_comprobante_compra_lote_input</c>) ya permite que cualquier línea
    /// cargue <c>fecha_vencimiento</c>, y el spec no condiciona este rechazo a que el artículo sea
    /// lot-effective en ese momento.
    ///
    /// Guard primario (judgment-day, slice 5, FIX 1a): un <c>codigo_lote</c> no vacío sin
    /// <c>fecha_vencimiento</c> jamás puede resolver a un lote válido (<c>ResolverOCrearAsync</c>
    /// exige fecha) — se rechaza acá, ANTES de tocar la base, con el mismo código
    /// (<c>lote_input_incompleto</c>) que el backstop de esquema
    /// <c>ck_items_comprobante_compra_lote_input</c> traduce en <c>ManejadorDeErrores</c> por si
    /// algún camino futuro esquiva este guard.</summary>
    private static void ValidarVencimientosDeRecepcion(IReadOnlyList<LineaDeCompraSolicitada> items, DateOnly hoy)
    {
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.CodigoLote) && item.FechaVencimiento is null)
            {
                throw new ErrorDominio(
                    "lote_input_incompleto",
                    $"El artículo {item.IdArticulo} trae codigo_lote sin fecha_vencimiento; ambos son " +
                    "requeridos juntos.",
                    400);
            }

            if (item.FechaVencimiento is { } fecha && ReglaDeLotes.EstaVencido(fecha, hoy))
            {
                throw new ErrorDominio(
                    "lote_vencido_en_recepcion",
                    $"La fecha de vencimiento del artículo {item.IdArticulo} ya pasó; una recepción no puede " +
                    "ingresar mercadería vencida.",
                    409);
            }
        }
    }

    private async Task<TipoComprobante> ResolverTipoDeCompraAsync(int idTipoComprobante, CancellationToken ct)
    {
        var tipo = await db.TiposComprobante.FirstOrDefaultAsync(t => t.Id == idTipoComprobante, ct);

        if (tipo is null || !tipo.Activo || tipo.Clase != ClaseComprobante.Compra)
        {
            throw new ErrorDominio(
                "tipo_de_compra_invalido", $"El tipo de comprobante {idTipoComprobante} no es un tipo de compra válido.", 400);
        }

        return tipo;
    }

    private async Task<Proveedor> ResolverProveedorAsync(int idProveedor, CancellationToken ct) =>
        await db.Proveedores.FirstOrDefaultAsync(p => p.Id == idProveedor, ct)
            // ADR-8: mismo 404 para "no existe" y "es de otro tenant".
            ?? throw ErrorDominio.NoEncontrado($"No existe el proveedor {idProveedor}.");

    private async Task<PuntoVenta> ResolverPuntoVentaAsync(int idPuntoVenta, CancellationToken ct) =>
        await db.PuntosVenta.FirstOrDefaultAsync(pv => pv.Id == idPuntoVenta, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");

    private async Task<ComprobanteCompra> BuscarComprobanteAsync(int id, CancellationToken ct) =>
        await db.ComprobantesCompra.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la compra {id}.");

    /// <summary>Etapa 12, slice 5 — mismo patrón que
    /// <c>ServicioDeLotes.ResolverDiasAlertaAsync</c> (Slice 3): una query filtrada por clave y
    /// empresa, delegando la precedencia PV &gt; empresa a <c>ResolucionDeParametros.Resolver</c>.
    /// Fuera del presupuesto de comandos del checkout — esta clase no lo comparte.</summary>
    private async Task<bool> ResolverLotesHabilitadoAsync(int idEmpresa, int idPuntoVenta, CancellationToken ct)
    {
        var candidatos = await db.Parametros
            .Where(p => p.Clave == ParametroConocido.LotesHabilitado.Clave && p.IdEmpresa == idEmpresa
                && (p.IdPuntoVenta == null || p.IdPuntoVenta == idPuntoVenta))
            .ToListAsync(ct);

        var valorJson = ResolucionDeParametros.Resolver(ParametroConocido.LotesHabilitado.Clave, candidatos, idPuntoVenta);
        return JsonSerializer.Deserialize<bool>(valorJson);
    }

    private static string? NormalizarOpcional(string? valor)
    {
        var limpio = valor?.Trim();
        return string.IsNullOrEmpty(limpio) ? null : limpio;
    }

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            ?? throw new InvalidOperationException(
                "ServicioDeCompras requiere un actor de tenant; GestionDeCatalogo no admite plataforma.");

    private static CompraDetalle Proyectar(ComprobanteCompra comprobante, IReadOnlyList<ItemComprobanteCompra> items) => new(
        comprobante.Id, comprobante.IdProveedor, comprobante.IdTipoComprobante, comprobante.IdPuntoVenta,
        comprobante.NumeroExterno, comprobante.FechaComprobante, comprobante.FechaRecepcion,
        comprobante.Subtotal, comprobante.DescuentoTotal, comprobante.IvaTotal, comprobante.Total,
        comprobante.Observaciones, comprobante.Estado,
        items
            .OrderBy(i => i.Orden)
            .Select(i => new ItemDeCompra(
                i.Orden, i.IdArticulo, i.Descripcion, i.Cantidad, i.Bultos, i.UnidadesPorBulto,
                i.CostoUnitario, i.Descuento, i.IdAlicuotaIva, i.PorcentajeIva, i.Total, i.ActualizaCosto,
                i.PrecioSugerido, i.CodigoLote, i.FechaVencimiento, i.IdLote))
            .ToList());
}
