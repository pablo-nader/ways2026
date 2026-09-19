using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Caja;

namespace Ways.Application.Caja;

/// <summary>
/// Arma <see cref="ResumenDeCierrePorRetiro"/> combinando el cálculo puro de <see
/// cref="CalculadorDeCierrePorRetiro"/> (que a su vez reusa <see cref="LectorDeMovimientosDelTurno"/>
/// + <see cref="CalculadorDeArqueo"/>, la MISMA derivación que el cierre clásico — spec: Resumen
/// Parcial Uses The Same Derivation As Cierre, extendida acá) con las lecturas de nombres (medios,
/// punto de venta, empleados) y el listado de retiros del turno.
///
/// Compartida TAL CUAL por <c>ServicioDeTurnos.CerrarPorRetiroAsync</c> (justo después del commit)
/// y por <c>ServicioDeTurnos.ObtenerResumenDeCierreAsync</c> (<c>GET …/resumen-de-cierre</c>, sobre
/// un turno YA cerrado — reimpresión / recuperación tras una falla de red ambigua): las dos rutas
/// llaman a este mismo método sobre el mismo <paramref name="turno"/> ya persistido, así que la
/// respuesta del POST y la del GET son bit-a-bit la misma construcción — nunca dos fórmulas.
///
/// El turno tiene que estar cerrado (<see cref="TurnoCaja.FechaCierre"/>/<see
/// cref="TurnoCaja.IdEmpleadoCierre"/> no nulos) — el llamador lo garantiza: el POST recién llama
/// acá después de que <c>MarcarCerradoAsync</c> comiteó, y el GET lo exige antes de llamar
/// (<c>409 turno_no_cerrado</c> si no).
/// </summary>
public class LectorDeResumenDeCierrePorRetiro(IWaysDbContext db, LectorDeMovimientosDelTurno lector)
{
    public async Task<ResumenDeCierrePorRetiro> LeerAsync(TurnoCaja turno, CancellationToken ct = default)
    {
        var idEmpleadoCierre = turno.IdEmpleadoCierre
            ?? throw new InvalidOperationException(
                $"El turno {turno.Id} todavía no tiene empleado de cierre — LeerAsync exige un turno cerrado.");
        var fechaCierre = turno.FechaCierre
            ?? throw new InvalidOperationException(
                $"El turno {turno.Id} todavía no tiene fecha de cierre — LeerAsync exige un turno cerrado.");

        var insumos = await lector.LeerAsync(turno.Id, ct);
        var idAncla = ResolvedorDeMedioDeCajaFisica.Resolver(insumos.Actividad);
        var arqueables = CalculadorDeArqueo.Calcular(insumos, idAncla);
        var resultado = CalculadorDeCierrePorRetiro.Calcular(insumos, idAncla, arqueables);

        // Retiros del turno — TODOS, incluido el de cierre (ya persistido por
        // CerrarPorRetiroAsync antes de derivar), ordenados por fecha para un listado estable.
        var retirosCrudos = await db.MovimientosCaja
            .Where(m => m.IdTurnoCaja == turno.Id && m.Tipo == TipoMovimientoCaja.Retiro)
            .OrderBy(m => m.CreadoEl).ThenBy(m => m.Id)
            .Select(m => new { m.CreadoEl, m.Importe, m.Motivo, m.IdEmpleado })
            .ToListAsync(ct);

        // Nombres de empleados: apertura, cierre y cada empleado que registró un retiro — una
        // sola consulta agrupada, nunca una por fila.
        var idsDeEmpleados = new HashSet<int> { turno.IdEmpleadoApertura, idEmpleadoCierre };
        foreach (var retiro in retirosCrudos)
        {
            idsDeEmpleados.Add(retiro.IdEmpleado);
        }

        var nombresDeEmpleados = await db.Usuarios
            .Where(u => idsDeEmpleados.Contains(u.Id))
            .Select(u => new { u.Id, u.NombreUsuario })
            .ToDictionaryAsync(u => u.Id, u => u.NombreUsuario, ct);

        string NombreDeEmpleado(int id) => nombresDeEmpleados.GetValueOrDefault(id, $"Usuario #{id}");

        var retiros = retirosCrudos
            .Select(r => new RetiroDeCierre(r.CreadoEl, r.Importe, r.Motivo, NombreDeEmpleado(r.IdEmpleado)))
            .ToList();

        // Nombres de medios — solo los que aparecen en VentasPorMedio, una sola consulta.
        var idsDeMedios = resultado.VentasPorMedio.Select(v => v.IdMedioPago).ToList();
        var nombresDeMedios = await db.MediosPago
            .Where(m => idsDeMedios.Contains(m.Id))
            .Select(m => new { m.Id, m.Nombre })
            .ToDictionaryAsync(m => m.Id, m => m.Nombre, ct);

        var ventasPorMedio = resultado.VentasPorMedio
            .Select(v => new VentaPorMedio(
                v.IdMedioPago, nombresDeMedios.GetValueOrDefault(v.IdMedioPago, $"Medio #{v.IdMedioPago}"), v.Importe))
            .ToList();

        var puntoVenta = await db.PuntosVenta
            .Where(p => p.Id == turno.IdPuntoVenta)
            .Select(p => new { p.Id, p.Nombre })
            .FirstAsync(ct);

        return new ResumenDeCierrePorRetiro(
            turno.Id,
            new PuntoVentaDeCierre(puntoVenta.Id, puntoVenta.Id, puntoVenta.Nombre),
            turno.FechaApertura,
            fechaCierre,
            NombreDeEmpleado(turno.IdEmpleadoApertura),
            NombreDeEmpleado(idEmpleadoCierre),
            turno.FondoInicial,
            ventasPorMedio,
            resultado.TotalVentas,
            retiros,
            resultado.TotalRetiros,
            resultado.VentasEnEfectivoNetas,
            resultado.GastosEnEfectivo,
            resultado.Refuerzos,
            resultado.Diferencia);
    }
}
