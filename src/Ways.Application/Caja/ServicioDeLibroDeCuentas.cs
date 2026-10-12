using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;

namespace Ways.Application.Caja;

/// <summary>
/// Lectura de las cuentas de tesorería para la pantalla de Tesorería (doc 10 §7): saldos por cuenta con los avisos de
/// medios sin cuenta, y el libro de una cuenta con su saldo de apertura y los totales del rango. Solo lee. La
/// autorización (<c>LecturaDeReportes</c>: Supervisor y Admin) se aplica en la capa de API.
///
/// El libro se ordena siempre por <c>Id</c> y nunca por <c>Fecha</c>, igual que el de efectivo: el significado de la cadena
/// es el orden de inserción. El rango usa los mismos límites inclusivos de <see cref="ServicioDeTesoreria"/>
/// (<c>fecha &gt;= desde</c> y <c>fecha &lt;= hasta</c>, el día completo lo arma el cliente).
/// </summary>
public class ServicioDeLibroDeCuentas(IWaysDbContext db)
{
    public async Task<CuentasDeLaEmpresa> ListarCuentasAsync(int idEmpresa, CancellationToken ct = default)
    {
        if (!await db.Empresas.AnyAsync(e => e.Id == idEmpresa, ct))
        {
            throw ErrorDominio.NoEncontrado($"No existe la empresa {idEmpresa}.");
        }

        var cuentas = await (
            from c in db.CuentasTesoreria
            where c.IdEmpresa == idEmpresa
            let ultimo = db.MovimientosCuenta
                .Where(m => m.IdCuentaTesoreria == c.Id)
                .OrderByDescending(m => m.Id)
                .Select(m => new { m.Final, m.Fecha })
                .FirstOrDefault()
            orderby c.Nombre, c.Id
            select new CuentaConSaldo(
                c.Id,
                c.Nombre,
                c.Activo,
                c.SaldoInicial,
                ultimo == null ? c.SaldoInicial : ultimo.Final,
                ultimo == null ? null : ultimo.Fecha))
            .ToListAsync(ct);

        // Mismo criterio que el escritor del cierre: una cuenta dada de baja (que el filtro de EF oculta) o de otra empresa
        // no acredita; una inactiva sí. Un medio compartido por todo el tenant también aplica a esta empresa.
        var mediosSinCuenta = await (
            from mp in db.MediosPago
            where mp.Comportamiento == ComportamientoMedioPago.Electronico
                  && mp.Activo
                  && (mp.IdEmpresa == null || mp.IdEmpresa == idEmpresa)
                  && !db.CuentasTesoreria.Any(c => c.Id == mp.IdCuentaTesoreria && c.IdEmpresa == idEmpresa)
            orderby mp.Nombre, mp.Id
            select new MedioElectronicoSinCuenta(mp.Id, mp.Nombre))
            .ToListAsync(ct);

        return new CuentasDeLaEmpresa(idEmpresa, cuentas, mediosSinCuenta);
    }

    /// <summary>404 si la cuenta no existe, está dada de baja o es de otro tenant, antes de mirar el rango o la página.</summary>
    public async Task<LibroDeCuenta> ObtenerLibroAsync(
        int idCuenta,
        DateTimeOffset? desde = null,
        DateTimeOffset? hasta = null,
        int pagina = 1,
        int tamanio = 25,
        CancellationToken ct = default)
    {
        var cuenta = await (
            from c in db.CuentasTesoreria
            where c.Id == idCuenta
            let ultimoFinal = db.MovimientosCuenta
                .Where(m => m.IdCuentaTesoreria == c.Id)
                .OrderByDescending(m => m.Id)
                .Select(m => (decimal?)m.Final)
                .FirstOrDefault()
            select new { c.Nombre, c.Activo, c.SaldoInicial, SaldoActual = ultimoFinal ?? c.SaldoInicial })
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la cuenta de tesorería {idCuenta}.");

        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        var enElRango = db.MovimientosCuenta.Where(m => m.IdCuentaTesoreria == idCuenta);

        if (desde is { } d)
        {
            enElRango = enElRango.Where(m => m.Fecha >= d);
        }

        if (hasta is { } h)
        {
            enElRango = enElRango.Where(m => m.Fecha <= h);
        }

        var apertura = cuenta.SaldoInicial;
        if (desde is { } limite)
        {
            apertura = await db.MovimientosCuenta
                .Where(m => m.IdCuentaTesoreria == idCuenta && m.Fecha < limite)
                .OrderByDescending(m => m.Id)
                .Select(m => (decimal?)m.Final)
                .FirstOrDefaultAsync(ct) ?? cuenta.SaldoInicial;
        }

        var netoPorTipo = await enElRango
            .GroupBy(m => m.Tipo)
            .Select(g => new { Tipo = g.Key, Neto = g.Sum(m => m.Ingreso - m.Egreso) })
            .ToDictionaryAsync(x => x.Tipo, x => x.Neto, ct);

        var total = await enElRango.CountAsync(ct);

        var items = await (
            from m in enElRango.OrderBy(m => m.Id).Skip((pagina - 1) * tamanio).Take(tamanio)
            select new MovimientoDeCuentaListado(
                m.Id,
                m.Fecha,
                m.Tipo,
                m.Concepto,
                m.Ingreso,
                m.Egreso,
                m.Final,
                m.IdTurnoCaja,
                m.IdMedioPago,
                m.IdMedioPago == null
                    ? null
                    : db.MediosPago.Where(mp => mp.Id == m.IdMedioPago).Select(mp => mp.Nombre).FirstOrDefault(),
                m.IdEmpleado,
                db.Usuarios.Where(u => u.Id == m.IdEmpleado).Select(u => u.NombreUsuario).FirstOrDefault(),
                m.BaseBruta,
                m.PorcentajeAplicado))
            .ToListAsync(ct);

        var totales = ArmarTotales(netoPorTipo);

        return new LibroDeCuenta(
            idCuenta, cuenta.Nombre, cuenta.Activo, cuenta.SaldoActual, apertura, apertura + totales.Neto, totales, items,
            total, pagina, tamanio);
    }

    private static TotalesDeCuentaEnElRango ArmarTotales(Dictionary<TipoMovimientoCuenta, decimal> netoPorTipo)
    {
        decimal De(TipoMovimientoCuenta tipo) => netoPorTipo.GetValueOrDefault(tipo);

        var otros = De(TipoMovimientoCuenta.Transferencia) + De(TipoMovimientoCuenta.Deposito) + De(TipoMovimientoCuenta.Retiro);

        return new TotalesDeCuentaEnElRango(
            Cobrado: De(TipoMovimientoCuenta.Cobro),
            Comision: -De(TipoMovimientoCuenta.Comision),
            RetencionIibb: -De(TipoMovimientoCuenta.RetencionIibb),
            RetencionIva: -De(TipoMovimientoCuenta.RetencionIva),
            RetencionGanancias: -De(TipoMovimientoCuenta.RetencionGanancias),
            Ajustes: De(TipoMovimientoCuenta.Ajuste),
            Otros: otros,
            Neto: netoPorTipo.Values.Sum());
    }
}
