using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;

namespace Ways.IntegrationTests;

/// <summary>
/// El stock vive en la sucursal del punto de venta. Las pruebas que siembran o leen <c>stock</c>, <c>stock_lotes</c> o
/// <c>movimientos_stock</c> a mano resuelven acá la sucursal de su punto de venta, nunca suponiendo que los dos ids
/// coinciden. Incluye los puntos de venta dados de baja: el aislamiento por tenant lo sigue dando RLS.
/// </summary>
public static class SucursalDePrueba
{
    /// <summary>Subconsulta SQL con la sucursal del punto de venta pasado en <paramref name="parametro"/>
    /// (por ejemplo <c>"$2"</c>), para usar en lugar de un <c>id_punto_venta</c> en un INSERT o un WHERE crudo.</summary>
    public static string Sql(string parametro) =>
        $"(SELECT id_sucursal FROM puntos_venta WHERE id_punto_venta = {parametro})";

    public static Task<int> DeAsync(IWaysDbContext db, int idPuntoVenta) =>
        db.PuntosVenta.IgnoreQueryFilters()
            .Where(pv => pv.Id == idPuntoVenta)
            .Select(pv => pv.IdSucursal)
            .SingleAsync();

    public static async Task<int> DeAsync(DbConnection conexion, int idPuntoVenta)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT id_sucursal FROM puntos_venta WHERE id_punto_venta = @id";
        var parametro = comando.CreateParameter();
        parametro.ParameterName = "id";
        parametro.Value = idPuntoVenta;
        comando.Parameters.Add(parametro);

        return Convert.ToInt32(await comando.ExecuteScalarAsync()
            ?? throw new InvalidOperationException($"No existe el punto de venta {idPuntoVenta}."));
    }
}
