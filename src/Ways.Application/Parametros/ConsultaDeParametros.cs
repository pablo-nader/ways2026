using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;

namespace Ways.Application.Parametros;

/// <summary>
/// Lectura de UNA clave de <c>parametros</c> para quienes consultan <c>db.Parametros</c> directo (sin inyectar
/// <see cref="ServicioDeParametros"/>): trae las filas candidatas del nivel que la clave admite y delega la precedencia
/// en <see cref="ResolucionDeParametros"/>. Una clave de sucursal trae la fila de la sucursal del punto de venta y la
/// de la empresa; una de caja, la del punto de venta y la de la empresa.
/// </summary>
public static class ConsultaDeParametros
{
    /// <summary>Resuelve <paramref name="clave"/> desde un punto de venta: lee antes su sucursal (una consulta más).</summary>
    public static async Task<string> ResolverDePuntoVentaAsync(
        IWaysDbContext db, string clave, int idEmpresa, int idPuntoVenta, CancellationToken ct)
    {
        var idSucursal = await db.PuntosVenta
            .Where(pv => pv.Id == idPuntoVenta)
            .Select(pv => (int?)pv.IdSucursal)
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");

        return await ResolverAsync(db, clave, idEmpresa, idPuntoVenta, idSucursal, ct);
    }

    /// <summary>Resuelve <paramref name="clave"/> con la sucursal del punto de venta ya conocida.</summary>
    public static async Task<string> ResolverAsync(
        IWaysDbContext db, string clave, int idEmpresa, int? idPuntoVenta, int? idSucursal, CancellationToken ct)
    {
        var candidatos = await db.Parametros
            .Where(p => p.Clave == clave && p.IdEmpresa == idEmpresa
                && (p.IdPuntoVenta == null || p.IdPuntoVenta == idPuntoVenta)
                && (p.IdSucursal == null || p.IdSucursal == idSucursal))
            .ToListAsync(ct);

        return ResolucionDeParametros.Resolver(clave, candidatos, idPuntoVenta, idSucursal);
    }
}
