using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Parametros;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Reportes;
using Ways.Domain.Ventas;

namespace Ways.Application.Reportes;

/// <summary>
/// El agregado de ventas de stage-10-agregacion-dashboard (design: Data Flow). Resuelve el
/// alcance (empresa → puntos de venta), la zona horaria (<c>ServicioDeParametros</c>, misma
/// precedencia punto de venta → empresa → default que cualquier otro parámetro), arma el
/// <see cref="RangoDeReporte"/> puro y left-joinea sus buckets contra las filas crudas de
/// <see cref="LectorDeSerieTemporal"/> — un bucket sin ventas queda en <c>0</c>, nunca
/// desaparece (design decisión 4).
/// </summary>
public class ServicioDeReportesDeVentas(IWaysDbContext db, LectorDeSerieTemporal lector, ServicioDeParametros parametros)
{
    public async Task<ResumenDeVentas> ObtenerResumenAsync(
        int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta, Granularidad granularidad,
        CancellationToken ct = default)
    {
        var idTenant = await ExigirEmpresaAsync(idEmpresa, ct);
        var idsPuntoVenta = await ResolverPuntosDeVentaAsync(idEmpresa, idPuntoVenta, ct);
        var (zonaId, zona) = await ResolverZonaAsync(idEmpresa, idPuntoVenta, ct);

        var rango = RangoDeReporte.Crear(desde, hasta, granularidad, zona);

        IReadOnlyList<FilaSerieDeVentas> filas = idsPuntoVenta.Count == 0
            ? Array.Empty<FilaSerieDeVentas>()
            : await lector.EjecutarVentasAsync(
                granularidad, zonaId, idTenant, idsPuntoVenta, rango.DesdeUtc, rango.HastaUtcExclusivo, ct);

        var filaPorBucket = filas.ToDictionary(f => f.Bucket);

        var serie = rango.Buckets()
            .Select(bucket =>
            {
                filaPorBucket.TryGetValue(bucket.Inicio, out var fila);
                var cantidadTx = fila?.CantidadTx ?? 0;
                var ticketPromedioDelBucket = cantidadTx > 0 ? fila!.NetoTx / cantidadTx : (decimal?)null;
                return new BucketDeVentas(bucket.Etiqueta, bucket.Inicio, fila?.Neto ?? 0m, cantidadTx, ticketPromedioDelBucket);
            })
            .ToList();

        var netoVendido = filas.Sum(f => f.Neto);
        var cantidadTxTotal = filas.Sum(f => f.CantidadTx);
        var netoTxTotal = filas.Sum(f => f.NetoTx);
        var ticketPromedio = cantidadTxTotal > 0 ? netoTxTotal / cantidadTxTotal : (decimal?)null;
        var cantidadNcx = filas.Sum(f => f.CantidadNcx);
        var netoNcx = filas.Sum(f => f.NetoNcx);

        return new ResumenDeVentas(
            desde, hasta, granularidad, zonaId, serie, netoVendido, cantidadTxTotal, ticketPromedio, cantidadNcx, netoNcx);
    }

    /// <summary>Sin <c>idPuntoVenta</c> — sería una contradicción filtrar por el mismo eje que se
    /// está agrupando (design: Interfaces / Contracts, dto-contract-honesty). Zona resuelta a
    /// nivel empresa (spec: Ventas Breakdown Endpoints).</summary>
    public async Task<VentasPorPuntoVenta> ObtenerPorPuntoVentaAsync(
        int idEmpresa, DateOnly desde, DateOnly hasta, CancellationToken ct = default)
    {
        await ExigirEmpresaAsync(idEmpresa, ct);
        var idsPuntoVenta = await ResolverPuntosDeVentaAsync(idEmpresa, null, ct);
        var (zonaId, zona) = await ResolverZonaAsync(idEmpresa, null, ct);
        var rango = RangoDeReporte.Crear(desde, hasta, Granularidad.Dia, zona);

        var filas = await ConsultarPorPuntoVentaAsync(idsPuntoVenta, rango, ct);

        return new VentasPorPuntoVenta(desde, hasta, zonaId, filas);
    }

    public async Task<VentasPorVendedor> ObtenerPorVendedorAsync(
        int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta, CancellationToken ct = default)
    {
        await ExigirEmpresaAsync(idEmpresa, ct);
        var idsPuntoVenta = await ResolverPuntosDeVentaAsync(idEmpresa, idPuntoVenta, ct);
        var (zonaId, zona) = await ResolverZonaAsync(idEmpresa, idPuntoVenta, ct);
        var rango = RangoDeReporte.Crear(desde, hasta, Granularidad.Dia, zona);

        var filas = await ConsultarPorVendedorAsync(idsPuntoVenta, rango, ct);

        return new VentasPorVendedor(desde, hasta, zonaId, filas);
    }

    public async Task<VentasPorMedioPago> ObtenerPorMedioPagoAsync(
        int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta, CancellationToken ct = default)
    {
        await ExigirEmpresaAsync(idEmpresa, ct);
        var idsPuntoVenta = await ResolverPuntosDeVentaAsync(idEmpresa, idPuntoVenta, ct);
        var (zonaId, zona) = await ResolverZonaAsync(idEmpresa, idPuntoVenta, ct);
        var rango = RangoDeReporte.Crear(desde, hasta, Granularidad.Dia, zona);

        var filas = await ConsultarPorMedioPagoAsync(idsPuntoVenta, rango, ct);

        return new VentasPorMedioPago(desde, hasta, zonaId, filas);
    }

    /// <summary>Reporte PROVISIONAL de comisiones (Slice 10, droppable en su totalidad — spec
    /// rentabilidad-y-comisiones: Comisiones Is A Provisional, Non-Persisted Report). Reusa
    /// exactamente <see cref="ConsultarPorVendedorAsync"/>: el neto por vendedor YA es el filtro
    /// de venta neta que pide el spec (<c>neto_vendido_por_empleado</c>), así que la única pieza
    /// propia de este reporte es resolver <c>comision_porcentaje</c> y multiplicar. Sin
    /// escritura — ninguna fila se persiste en ninguna tabla, el cálculo es enteramente on the
    /// fly.</summary>
    public async Task<Comisiones> ObtenerComisionesAsync(
        int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta, CancellationToken ct = default)
    {
        await ExigirEmpresaAsync(idEmpresa, ct);
        var idsPuntoVenta = await ResolverPuntosDeVentaAsync(idEmpresa, idPuntoVenta, ct);
        var (zonaId, zona) = await ResolverZonaAsync(idEmpresa, idPuntoVenta, ct);
        var rango = RangoDeReporte.Crear(desde, hasta, Granularidad.Dia, zona);

        var comisionPorcentaje = await ResolverComisionPorcentajeAsync(idEmpresa, idPuntoVenta, ct);
        var filas = await ConsultarPorVendedorAsync(idsPuntoVenta, rango, ct);

        // mutation-proof-tests: la multiplicación por `comisionPorcentaje / 100m` es la ÚNICA
        // cláusula que gobierna tanto la tasa configurada como el default en 0 — no hay una rama
        // condicional separada para "tasa cero", el mismo producto la resuelve. Mutación aplicada
        // (reemplazado `f.Neto * comisionPorcentaje / 100m` por `f.Neto`, i.e. ignorar la tasa):
        // 4 de 7 tests de ReportesComisionesTests fallaron —
        // LaComisionCoincideConElCalculoAMano (500 esperado, 10000 obtenido),
        // SinParametroConfiguradoLaTasaDefaultEsCeroYTodaComisionEsCero (0 esperado, 10000
        // obtenido) y las dos pruebas del patrón de 4 que aciertan un `Comision` no-cero
        // (soft-delete/anulado) — revertida, vuelven a pasar las 7.
        var comisiones = filas
            .Select(f => new ComisionPorEmpleado(f.IdEmpleado, f.Neto, f.Neto * comisionPorcentaje / 100m))
            .ToList();

        return new Comisiones(desde, hasta, zonaId, comisionPorcentaje, comisiones, Provisional: true);
    }

    private async Task<int> ExigirEmpresaAsync(int idEmpresa, CancellationToken ct)
    {
        var idTenant = await db.Empresas
            .Where(e => e.Id == idEmpresa)
            .Select(e => (int?)e.IdTenant)
            .FirstOrDefaultAsync(ct);

        // ADR-8: mismo 404 para "no existe" y "es de otro tenant" — el filtro de EF/RLS ya deja
        // invisible una empresa ajena, mismo criterio que el resto de los servicios de Application.
        return idTenant ?? throw ErrorDominio.NoEncontrado($"No existe la empresa {idEmpresa}.");
    }

    /// <summary>Sin <paramref name="idPuntoVenta"/>: todos los puntos de venta de la empresa
    /// (empresa-wide, design decisión 5). Con él: la misma regla de pertenencia que
    /// <c>ServicioDeParametros.ValidarPuntoVentaDeLaEmpresaAsync</c> — un punto de venta real pero
    /// de otra empresa del mismo tenant no tiene FK que lo impida (nada en el esquema lo evita),
    /// así que esta consulta, scopeada por tenant vía el filtro de EF, es quien lo valida.</summary>
    private async Task<IReadOnlyList<int>> ResolverPuntosDeVentaAsync(int idEmpresa, int? idPuntoVenta, CancellationToken ct)
    {
        var puntosDeLaEmpresa = db.PuntosVenta.Where(pv => pv.IdEmpresa == idEmpresa);

        if (idPuntoVenta is { } id)
        {
            var pertenece = await puntosDeLaEmpresa.AnyAsync(pv => pv.Id == id, ct);
            if (!pertenece)
            {
                throw new ErrorDominio(
                    "punto_venta_no_pertenece_a_la_empresa",
                    "El punto de venta indicado no pertenece a la empresa declarada.",
                    400);
            }

            return [id];
        }

        return await puntosDeLaEmpresa.Select(pv => pv.Id).ToListAsync(ct);
    }

    /// <summary>design decisión 5: la zona se resuelve UNA vez, al alcance que pidió el caller —
    /// con <paramref name="idPuntoVenta"/>, PV → empresa → default; sin él, empresa → default,
    /// ignorando cualquier override de punto de venta (mismo comportamiento que
    /// <c>ServicioDeParametros.ResolverAsync</c> con <c>idPuntoVenta = null</c>, que solo mira las
    /// filas de nivel empresa).</summary>
    private async Task<(string ZonaId, TimeZoneInfo Zona)> ResolverZonaAsync(
        int idEmpresa, int? idPuntoVenta, CancellationToken ct)
    {
        var resuelto = await parametros.ResolverAsync(ParametroConocido.ZonaHoraria.Clave, idEmpresa, idPuntoVenta, ct);
        var zonaId = JsonSerializer.Deserialize<string>(resuelto.Valor)!;
        return (zonaId, TimeZoneInfo.FindSystemTimeZoneById(zonaId));
    }

    /// <summary>Misma precedencia PV → empresa → default que <see cref="ResolverZonaAsync"/>
    /// (design decisión 5, aplicada acá a la tasa en vez de la zona) — default <c>0</c> declarado
    /// en <c>ParametroConocido.ComisionPorcentaje</c> (spec: "Default rate yields zero
    /// commission").</summary>
    private async Task<decimal> ResolverComisionPorcentajeAsync(int idEmpresa, int? idPuntoVenta, CancellationToken ct)
    {
        var resuelto = await parametros.ResolverAsync(ParametroConocido.ComisionPorcentaje.Clave, idEmpresa, idPuntoVenta, ct);
        return JsonSerializer.Deserialize<decimal>(resuelto.Valor);
    }

    /// <summary>LINQ plano (design: Raw-SQL Invariant Checklist filas 3-4) — <c>Tenant</c> y
    /// <c>BajaLogica</c> los aplica EF automáticamente vía sus query filters globales; acá se
    /// espeletrean explícitamente <c>Estado != Anulado</c>, igual que el SQL crudo de
    /// <see cref="LectorDeSerieTemporal"/> los espeletrea a mano. Sin el <c>Join</c> a
    /// <c>tipos_comprobante</c> todavía — cada consumidor lo arma con una proyección anónima
    /// propia: EF no logra traducir un <c>GroupBy</c> sobre la propiedad de un record con nombre
    /// construido en el mismo <c>Join</c> (probado — <c>InvalidOperationException</c> "could not
    /// be translated"), pero sí sobre la de un tipo anónimo, mismo patrón que
    /// <c>LectorDeContenidoDeResumen.LeerAsync</c> paso 4.</summary>
    private IQueryable<ComprobanteVenta> ComprobantesVentaDelPeriodo(
        IReadOnlyCollection<int> idsPuntoVenta, RangoDeReporte rango) =>
        db.ComprobantesVenta
            .Where(cv => idsPuntoVenta.Contains(cv.IdPuntoVenta))
            .Where(cv => cv.Estado != EstadoComprobante.Anulado)
            .Where(cv => cv.Fecha >= rango.DesdeUtc && cv.Fecha < rango.HastaUtcExclusivo);

    private async Task<IReadOnlyList<FilaVentasPorPuntoVenta>> ConsultarPorPuntoVentaAsync(
        IReadOnlyCollection<int> idsPuntoVenta, RangoDeReporte rango, CancellationToken ct)
    {
        if (idsPuntoVenta.Count == 0)
        {
            return [];
        }

        var agregados = await ComprobantesVentaDelPeriodo(idsPuntoVenta, rango)
            .Join(
                db.TiposComprobante.Where(t => t.Clase == ClaseComprobante.Venta),
                cv => cv.IdTipoComprobante, t => t.Id, (cv, t) => new { cv.IdPuntoVenta, cv.Total, t.Signo })
            .GroupBy(x => x.IdPuntoVenta)
            .Select(g => new
            {
                Clave = g.Key,
                Neto = g.Sum(x => x.Total),
                CantidadTx = g.Count(x => x.Signo > 0),
                NetoTx = g.Sum(x => x.Signo > 0 ? x.Total : 0m)
            })
            .ToListAsync(ct);

        return agregados
            .Select(a => new FilaVentasPorPuntoVenta(
                a.Clave, a.Neto, a.CantidadTx, a.CantidadTx > 0 ? a.NetoTx / a.CantidadTx : (decimal?)null))
            .OrderBy(f => f.IdPuntoVenta)
            .ToList();
    }

    private async Task<IReadOnlyList<FilaVentasPorVendedor>> ConsultarPorVendedorAsync(
        IReadOnlyCollection<int> idsPuntoVenta, RangoDeReporte rango, CancellationToken ct)
    {
        if (idsPuntoVenta.Count == 0)
        {
            return [];
        }

        var agregados = await ComprobantesVentaDelPeriodo(idsPuntoVenta, rango)
            .Join(
                db.TiposComprobante.Where(t => t.Clase == ClaseComprobante.Venta),
                cv => cv.IdTipoComprobante, t => t.Id, (cv, t) => new { cv.IdEmpleado, cv.Total, t.Signo })
            .GroupBy(x => x.IdEmpleado)
            .Select(g => new
            {
                Clave = g.Key,
                Neto = g.Sum(x => x.Total),
                CantidadTx = g.Count(x => x.Signo > 0),
                NetoTx = g.Sum(x => x.Signo > 0 ? x.Total : 0m)
            })
            .ToListAsync(ct);

        return agregados
            .Select(a => new FilaVentasPorVendedor(
                a.Clave, a.Neto, a.CantidadTx, a.CantidadTx > 0 ? a.NetoTx / a.CantidadTx : (decimal?)null))
            .OrderBy(f => f.IdEmpleado)
            .ToList();
    }

    /// <summary><c>pagos_comprobante.importe</c> nunca es negativo (CHECK
    /// <c>ck_pagos_comprobante_importe_no_negativo</c>, <c>ValidadorDePagos</c> regla 0) — el
    /// signo lo aporta el <c>Signo</c> del tipo de comprobante del encabezado, mismo discriminador
    /// que las otras dos rutas, así que una NCX resta sin ninguna rama condicional (design
    /// decisión 9).</summary>
    private async Task<IReadOnlyList<FilaVentasPorMedioPago>> ConsultarPorMedioPagoAsync(
        IReadOnlyCollection<int> idsPuntoVenta, RangoDeReporte rango, CancellationToken ct)
    {
        if (idsPuntoVenta.Count == 0)
        {
            return [];
        }

        var agregados = await ComprobantesVentaDelPeriodo(idsPuntoVenta, rango)
            .Join(
                db.TiposComprobante.Where(t => t.Clase == ClaseComprobante.Venta),
                cv => cv.IdTipoComprobante, t => t.Id, (cv, t) => new { cv.Id, t.Signo })
            .Join(db.PagosComprobante, cv => cv.Id, p => p.IdComprobanteVenta, (cv, p) => new { cv.Signo, p.IdMedioPago, p.Importe })
            .GroupBy(x => x.IdMedioPago)
            // mutation-proof-tests: la cláusula "x.Signo" es lo único que hace que una NCX reste
            // acá (probado por PorMedioPagoUnaNcxReduceElSubtotalDelMedioSinRamaEspecial).
            .Select(g => new { Clave = g.Key, Neto = g.Sum(x => x.Importe * x.Signo), CantidadPagos = g.Count() })
            .ToListAsync(ct);

        return agregados
            .Select(a => new FilaVentasPorMedioPago(a.Clave, a.Neto, a.CantidadPagos))
            .OrderBy(f => f.IdMedioPago)
            .ToList();
    }
}
