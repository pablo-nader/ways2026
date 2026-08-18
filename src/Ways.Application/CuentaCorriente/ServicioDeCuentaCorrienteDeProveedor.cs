using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;
using Ways.Domain.CuentaCorriente;

namespace Ways.Application.CuentaCorriente;

/// <summary>
/// El lado de lectura del ledger de proveedores (design.md: Interfaces / Contracts — Application —
/// read model, tasks 4.3-4.4). Header + página de movimientos en un único <c>GET</c>, sin lock
/// (lectura pura) — <c>saldo_resultante</c> es la ÚNICA fuente de la corrida, JAMÁS re-derivada
/// (design decisión 11). <c>historico</c> gana sobre <c>desde</c>/<c>hasta</c>; si ninguno de los
/// tres viene, aplica el default de último mes — mismo criterio pinneado por
/// <c>ServicioDeCuentaCorriente.ObtenerEstadoDeCuentaAsync</c> (cliente, stage 7).
/// PAGINADA (design decisión 10, <c>state.yaml</c> OD9): <c>CountAsync</c> + <c>Skip/Take</c>,
/// mismo patrón que <c>ServicioDeConsultaDeAuditoria.ConsultarAsync</c> (etapa 14).
/// El ajuste manual (<c>RegistrarAjusteAsync</c>) llega en Slice 5 — no declarado acá todavía.
/// </summary>
public sealed class ServicioDeCuentaCorrienteDeProveedor(IWaysDbContext db, IRelojDelSistema reloj)
{
    public async Task<PaginaDeEstadoDeCuentaDeProveedor> ObtenerEstadoDeCuentaAsync(
        int idProveedor, DateTimeOffset? desde, DateTimeOffset? hasta, bool historico,
        int pagina, int tamanio, CancellationToken ct = default)
    {
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

        var filas = await query
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .Select(m => new
            {
                m.Id, m.Fecha, m.Tipo, m.Importe, m.SaldoResultante, m.Detalle, m.IdComprobanteCompra, m.IdGasto
            })
            .ToListAsync(ct);

        var items = filas
            .Select(f => new MovimientoDeCuentaDeProveedor(
                f.Id, f.Fecha, f.Tipo, f.Importe, f.SaldoResultante, f.Detalle, f.IdComprobanteCompra, f.IdGasto,
                f.Tipo == TipoMovimientoCcProveedor.Ajuste
                    ? CalculadorDeEstadoDeCuentaDeProveedor.EtiquetarAjuste(f.IdComprobanteCompra)
                    : null))
            .ToList();

        var header = new EstadoDeCuentaDeProveedorHeader(idProveedor, saldo);
        return new PaginaDeEstadoDeCuentaDeProveedor(
            header, items, total, pagina, tamanio, historico, desdeEfectivo, hastaEfectivo);
    }

    /// <summary>
    /// Cláusulas bajo prueba (<c>mutation-proof-tests</c>, design.md:164-172), en orden de daño si
    /// se pierden:
    ///   <c>Where(m => m.IdProveedor == idProveedor)</c> → sin él, el ledger de un proveedor
    ///                                                      filtra otros (cross-tenant/cross-proveedor)
    ///   <c>ThenByDescending(Id)</c>                     → con <c>fecha</c> empatada (<c>RelojFijo</c>,
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

        return consulta.OrderByDescending(m => m.Fecha).ThenByDescending(m => m.Id);
    }

    /// <summary>ADR-8: mismo 404 para "no existe" y "es de otro tenant" — mismo criterio que
    /// <c>ServicioDeSaldoDeProveedor.ResolverProveedorAsync</c>, pero trae <c>Saldo</c> en la misma
    /// consulta (esta lectura lo necesita para el header; aquella solo necesita existencia).</summary>
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
}
