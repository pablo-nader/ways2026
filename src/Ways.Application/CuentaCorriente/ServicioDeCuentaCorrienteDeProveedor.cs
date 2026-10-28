using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Compras;
using Ways.Application.Listados;
using Ways.Domain.Compras;
using Ways.Domain.Common;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Organizacion;

namespace Ways.Application.CuentaCorriente;

/// <summary>
/// El ledger de proveedores: lectura (design.md: Interfaces / Contracts — Application, tasks
/// 4.3-4.4) y el ajuste manual (Slice 5, tasks 5.4, design decisiones 12-15). Header + página de
/// movimientos en un único <c>GET</c>, sin lock (lectura pura) — <c>saldo_resultante</c> es la
/// ÚNICA fuente de la corrida, JAMÁS re-derivada (design decisión 11). <c>historico</c> gana sobre
/// <c>desde</c>/<c>hasta</c>; si ninguno de los tres viene, aplica el default de último mes —
/// mismo criterio pinneado por <c>ServicioDeCuentaCorriente.ObtenerEstadoDeCuentaAsync</c>
/// (cliente, stage 7). PAGINADA (design decisión 10, <c>state.yaml</c> OD9): <c>CountAsync</c> +
/// <c>Skip/Take</c>, mismo patrón que <c>ServicioDeConsultaDeAuditoria.ConsultarAsync</c> (etapa
/// 14). <see cref="RegistrarAjusteAsync"/> es la ÚNICA escritura de este servicio — el resto de la
/// clase sigue siendo lectura pura.
/// </summary>
public sealed class ServicioDeCuentaCorrienteDeProveedor(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto)
{
    public async Task<PaginaDeEstadoDeCuentaDeProveedor> ObtenerEstadoDeCuentaAsync(
        int idProveedor, DateTimeOffset? desde, DateTimeOffset? hasta, bool historico,
        int pagina, int tamanio, string? orden = null, string? direccion = null, CancellationToken ct = default)
    {
        var ordenPedido = OrdenDeListado.Interpretar(orden, direccion, ClavesDeOrden);

        var saldo = await ResolverSaldoDeProveedorAsync(idProveedor, ct);

        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        DateTimeOffset? desdeEfectivo = null;
        DateTimeOffset? hastaEfectivo = null;
        if (!historico)
        {
            // Un hasta explícito sin desde también recorta la ventana a un mes — mismo criterio
            // que ServicioDeCuentaCorriente.ObtenerEstadoDeCuentaAsync (cliente).
            desdeEfectivo = desde ?? (hasta is { } hastaSinDesde ? hastaSinDesde.AddMonths(-1) : reloj.Ahora.AddMonths(-1));
            hastaEfectivo = hasta;
        }

        var query = ConstruirQuery(idProveedor, desdeEfectivo, hastaEfectivo);

        var total = await query.CountAsync(ct);

        var filas = await Ordenar(query, ordenPedido)
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .Select(m => new
            {
                m.Id, m.Fecha, m.Tipo, m.Importe, m.SaldoResultante, m.Detalle, m.IdComprobanteCompra, m.IdGasto
            })
            .ToListAsync(ct);

        var saldosPendientes = await LeerSaldosPendientesDeComprasAsync(
            filas.Where(f => f.Tipo == TipoMovimientoCcProveedor.Compra && f.IdComprobanteCompra != null)
                .Select(f => f.IdComprobanteCompra!.Value)
                .Distinct()
                .ToList(),
            ct);

        var items = filas
            .Select(f => new MovimientoDeCuentaDeProveedor(
                f.Id, f.Fecha, f.Tipo, f.Importe, f.SaldoResultante, f.Detalle, f.IdComprobanteCompra, f.IdGasto,
                f.Tipo == TipoMovimientoCcProveedor.Ajuste
                    ? CalculadorDeEstadoDeCuentaDeProveedor.EtiquetarAjuste(f.IdComprobanteCompra)
                    : null,
                f.Tipo == TipoMovimientoCcProveedor.Compra && f.IdComprobanteCompra is { } idCompra
                    ? saldosPendientes.GetValueOrDefault(idCompra)
                    : null))
            .ToList();

        var header = new EstadoDeCuentaDeProveedorHeader(idProveedor, saldo);
        return new PaginaDeEstadoDeCuentaDeProveedor(
            header, items, total, pagina, tamanio, historico, desdeEfectivo, hastaEfectivo);
    }

    /// <summary>Saldo pendiente de las compras de una página, con UNA consulta de total/estado y una
    /// de pagado (<see cref="LectorDePagadoPorCompra"/>) para todas. Una compra que ya no es visible
    /// no figura en el resultado.</summary>
    private async Task<Dictionary<int, decimal>> LeerSaldosPendientesDeComprasAsync(
        IReadOnlyCollection<int> idsCompras, CancellationToken ct)
    {
        if (idsCompras.Count == 0)
        {
            return [];
        }

        var compras = await db.ComprobantesCompra
            .Where(c => idsCompras.Contains(c.Id))
            .Select(c => new { c.Id, c.Estado, c.Total })
            .ToListAsync(ct);

        var pagadoPorCompra = await LectorDePagadoPorCompra.LeerAsync(
            db, compras.Where(c => c.Estado == EstadoCompra.Confirmada).Select(c => c.Id).ToList(), ct);

        return compras.ToDictionary(
            c => c.Id,
            c => ReglaDePagoDeCompra.SaldoPendiente(c.Estado, c.Total, pagadoPorCompra.GetValueOrDefault(c.Id, 0m)));
    }

    /// <summary>
    /// Cláusulas bajo prueba (<c>mutation-proof-tests</c>, design.md:164-172), en orden de daño si
    /// se pierden:
    ///   <c>Where(m => m.IdProveedor == idProveedor)</c> → sin él, el ledger de un proveedor
    ///                                                      filtra otros (cross-tenant/cross-proveedor)
    ///   <c>ThenByDescending(Id)</c> (en <see cref="Ordenar"/>) → con <c>fecha</c> empatada (<c>RelojFijo</c>,
    ///                                                      o confirmar + contramovimiento) la
    ///                                                      paginación duplica y saltea (mutation
    ///                                                      target #25, task 4.16)
    ///   cada <c>if (desde/hasta is { } x)</c>            → un filtro ignorado devuelve de más, en
    ///                                                      silencio (mutation target #26, task 4.17)
    /// El branch <c>historico</c> vs. default de último mes vive en el llamador (no toma
    /// <paramref name="desde"/>/<paramref name="hasta"/> crudos, ya resueltos ahí).
    /// </summary>
    private IQueryable<MovimientoCuentaCorrienteProveedor> ConstruirQuery(
        int idProveedor, DateTimeOffset? desde, DateTimeOffset? hasta)
    {
        var consulta = db.MovimientosCuentaCorrienteProveedor.Where(m => m.IdProveedor == idProveedor);

        if (desde is { } desdeAplicado)
        {
            consulta = consulta.Where(m => m.Fecha >= desdeAplicado);
        }

        if (hasta is { } hastaAplicado)
        {
            consulta = consulta.Where(m => m.Fecha <= hastaAplicado);
        }

        return consulta;
    }

    /// <summary>Sin orden pedido: fecha e <c>Id</c> descendentes (el orden de siempre del libro). Con orden: la
    /// columna pedida y SIEMPRE <c>Id</c> ascendente como desempate; ordenar nunca recalcula nada, cada fila
    /// conserva el saldo resultante guardado. "Tipo" ordena por la etiqueta de la celda y "Comprobante/Gasto" por
    /// lo que la celda muestra: primero las compras, después los gastos, cada grupo por su número, y los
    /// movimientos sin referencia (la celda dice un guion) al final en las dos direcciones. Un detalle ausente
    /// también va al final.</summary>
    private static IOrderedQueryable<MovimientoCuentaCorrienteProveedor> Ordenar(
        IQueryable<MovimientoCuentaCorrienteProveedor> query, OrdenDeListado<ColumnaDeOrdenDeCuentaDeProveedor>? orden)
    {
        if (orden is not { } pedido)
        {
            return query.OrderByDescending(m => m.Fecha).ThenByDescending(m => m.Id);
        }

        var desc = pedido.Descendente;
        var ordenado = pedido.Columna switch
        {
            ColumnaDeOrdenDeCuentaDeProveedor.Fecha => OrdenarPor.Clave(query, m => m.Fecha, desc),
            ColumnaDeOrdenDeCuentaDeProveedor.Tipo => OrdenarPor.Clave(
                query, EtiquetasDeMovimientoDeCuenta.SelectorDeTipoDeProveedor(), desc),
            ColumnaDeOrdenDeCuentaDeProveedor.Referencia => OrdenarPorReferencia(query, desc),
            ColumnaDeOrdenDeCuentaDeProveedor.Detalle => OrdenarPor.ClaveConVacios(query, m => m.Detalle, desc),
            ColumnaDeOrdenDeCuentaDeProveedor.Importe => OrdenarPor.Clave(query, m => m.Importe, desc),
            ColumnaDeOrdenDeCuentaDeProveedor.SaldoResultante => OrdenarPor.Clave(query, m => m.SaldoResultante, desc),
            _ => query.OrderByDescending(m => m.Fecha),
        };

        return ordenado.ThenBy(m => m.Id);
    }

    private static IOrderedQueryable<MovimientoCuentaCorrienteProveedor> OrdenarPorReferencia(
        IQueryable<MovimientoCuentaCorrienteProveedor> query, bool descendente)
    {
        var sinReferenciaAlFinal = query.OrderBy(m => m.IdComprobanteCompra == null && m.IdGasto == null);

        return descendente
            ? sinReferenciaAlFinal
                .ThenByDescending(m => m.IdComprobanteCompra == null)
                .ThenByDescending(m => m.IdComprobanteCompra ?? m.IdGasto)
            : sinReferenciaAlFinal
                .ThenBy(m => m.IdComprobanteCompra == null)
                .ThenBy(m => m.IdComprobanteCompra ?? m.IdGasto);
    }

    public static readonly IReadOnlyDictionary<string, ColumnaDeOrdenDeCuentaDeProveedor> ClavesDeOrden =
        new Dictionary<string, ColumnaDeOrdenDeCuentaDeProveedor>(StringComparer.Ordinal)
        {
            ["fecha"] = ColumnaDeOrdenDeCuentaDeProveedor.Fecha,
            ["tipo"] = ColumnaDeOrdenDeCuentaDeProveedor.Tipo,
            ["referencia"] = ColumnaDeOrdenDeCuentaDeProveedor.Referencia,
            ["detalle"] = ColumnaDeOrdenDeCuentaDeProveedor.Detalle,
            ["importe"] = ColumnaDeOrdenDeCuentaDeProveedor.Importe,
            ["saldoResultante"] = ColumnaDeOrdenDeCuentaDeProveedor.SaldoResultante,
        };

    /// <summary>ADR-8: mismo 404 para "no existe" y "es de otro tenant" — mismo criterio que
    /// <c>ServicioDeSaldoDeProveedor.ResolverProveedorAsync</c>, pero trae <c>Saldo</c> en la misma
    /// consulta (esta lectura lo necesita para el header; aquella solo necesita existencia).
    /// <see cref="RegistrarAjusteAsync"/> REUSA este mismo método para su propio 404 de proveedor
    /// (misma clase, mismo guard — nunca un segundo <c>ResolverProveedorAsync</c> paralelo).</summary>
    private async Task<decimal> ResolverSaldoDeProveedorAsync(int idProveedor, CancellationToken ct)
    {
        var proveedor = await db.Proveedores
            .Where(p => p.Id == idProveedor)
            .Select(p => new { p.Saldo })
            .FirstOrDefaultAsync(ct);

        if (proveedor is null)
        {
            throw ErrorDominio.NoEncontrado($"No existe el proveedor {idProveedor}.");
        }

        return proveedor.Saldo;
    }

    // ==== Ajuste manual (Slice 5, design decisiones 12-15, Transactions — AJUSTE MANUAL) =========

    /// <summary>
    /// Design: Transactions — AJUSTE MANUAL (orden pineado: "fuera: ReglaDeAjusteDeCuenta …
    /// proveedor (404 ADR-8) … PV (404)"). Sin turno (design decisión 14: no mueve plata física ni
    /// aporta término al arqueo — mismo criterio que <c>ServicioDeCuentaCorriente.RegistrarAjusteAsync</c>,
    /// cliente, stage 7). El DTO no tiene <c>tipo</c> ni <c>idComprobanteCompra</c> — el movimiento
    /// escrito SIEMPRE lleva <c>id_comprobante_compra IS NULL</c> (spec: "Manual Ajuste Requires A
    /// Detalle Under A Dedicated Policy" — la marca estructural que lo distingue del
    /// contramovimiento de anulación, que sí lo lleva).</summary>
    public async Task<MovimientoDeCuentaDeProveedor> RegistrarAjusteAsync(
        int idProveedor, SolicitudDeAjusteDeProveedor solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        // 1. ReglaDeAjusteDeCuenta — pura, corre ANTES de cualquier consulta a la base (spec:
        // "A manual ajuste with no detalle is rejected" — "rejected before any write").
        ReglaDeAjusteDeCuenta.Validar(solicitud.Importe, solicitud.Detalle);
        var detalleNormalizado = solicitud.Detalle!.Trim();

        // 2. Proveedor — 404 ADR-8, reusando el mismo guard que el estado de cuenta.
        await ResolverSaldoDeProveedorAsync(idProveedor, ct);

        // 3. Punto de venta — provenance, no autoridad (design decisión 14); 404 ANTES de la
        // transacción sin turno (mismo orden que ServicioDeGastos.cs:28-31 / ServicioDeCuentaCorriente,
        // cliente: un PV apócrifo tiene que dar 404, nunca abrir una transacción para nada).
        var puntoVenta = await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarAjusteAsync(
                idTenant, idEmpleado, momento, idProveedor, puntoVenta.Id, solicitud.Importe, detalleNormalizado, ct));
    }

    private async Task<MovimientoDeCuentaDeProveedor> EjecutarAjusteAsync(
        int idTenant, int idEmpleado, DateTimeOffset momento, int idProveedor, int idPuntoVenta, decimal importe,
        string detalle, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        // 1. Saldo — el único lock de la transacción (design: Transactions, AJUSTE MANUAL — "sin
        // turno_caja/comprobantes_compra/lotes/stock que lockear antes, proveedores es el único").
        var nuevoSaldo = await EscriturasDeCuentaCorrienteProveedor.ActualizarSaldoProveedorAsync(
            conexion, transaccionCruda, idTenant, idProveedor, importe, ct);

        // 2. Movimiento — id_comprobante_compra e id_gasto NULL (marca estructural: es un ajuste
        // manual, nunca un contramovimiento de anulación ni un pago).
        var idMovimiento = await EscriturasDeCuentaCorrienteProveedor.InsertarMovimientoCcProveedorAsync(
            conexion, transaccionCruda, idTenant, idProveedor, momento, idPuntoVenta, idEmpleado,
            TipoMovimientoCcProveedor.Ajuste, idComprobanteCompra: null, idGasto: null, importe, nuevoSaldo,
            detalle, ct);

        await transaccion.CommitAsync(ct);

        return new MovimientoDeCuentaDeProveedor(
            idMovimiento, momento, TipoMovimientoCcProveedor.Ajuste, importe, nuevoSaldo, detalle,
            IdComprobanteCompra: null, IdGasto: null, Etiqueta: EtiquetaDeAjuste.Manual);
    }

    /// <summary>ADR-8: mismo 404 para "no existe" y "es de otro tenant" — mismo criterio que
    /// <c>ServicioDeGastos.ResolverPuntoVentaAsync</c>/<c>ServicioDeCuentaCorriente.ResolverPuntoVentaAsync</c>
    /// (cliente).</summary>
    private async Task<PuntoVenta> ResolverPuntoVentaAsync(int idPuntoVenta, CancellationToken ct) =>
        await db.PuntosVenta.FirstOrDefaultAsync(pv => pv.Id == idPuntoVenta, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");

    private async Task<DbConnection> ObtenerConexionAbiertaAsync(CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();

        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        return conexion;
    }

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            // OperacionDePos/SupervisionDeCuentaDeProveedor (capa de API) ya exigen un actor de
            // tenant — un actor de plataforma (root) nunca llega hasta acá. Defensa en
            // profundidad, mismo criterio que ServicioDeGastos.ExigirTenantDeLaSesion.
            ?? throw new InvalidOperationException(
                "ServicioDeCuentaCorrienteDeProveedor requiere un actor de tenant; las policies de este servicio no admiten plataforma.");
}
