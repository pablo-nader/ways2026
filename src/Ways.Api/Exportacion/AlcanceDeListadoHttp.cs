using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Parametros;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;

namespace Ways.Api.Exportacion;

/// <summary>
/// stage-11-exportacion-reportes (Slice 3): resuelve Empresa/zona horaria para los exports de
/// listado con <c>idPuntoVenta</c> opcional (ventas) — PV → empresa → default, misma precedencia
/// que <c>ServicioDeReportesDeVentas.ResolverZonaAsync</c>, pero arrancando desde el punto de
/// venta en vez de una empresa ya conocida: <c>GET /api/ventas</c> nunca pidió <c>idEmpresa</c>.
/// Los listados sin ningún concepto de punto de venta (compras, estado de cuenta) usan
/// <see cref="ZonaPorDefecto"/> directo, sin consulta — sus rutas fuente nunca tuvieron ese
/// parámetro y esta slice solo extrae <c>ConstruirQuery</c>, no agrega filtros nuevos (design
/// decisión 7).
/// </summary>
public static class AlcanceDeListadoHttp
{
    public static readonly string ZonaPorDefecto =
        JsonSerializer.Deserialize<string>(ParametroConocido.ZonaHoraria.ValorPorDefecto)!;

    public static async Task<(string Empresa, string ZonaId)> ResolverAsync(
        IWaysDbContext db, ServicioDeParametros parametros, int? idPuntoVenta, CancellationToken ct)
    {
        if (idPuntoVenta is not { } id)
        {
            return ("Todas", ZonaPorDefecto);
        }

        var idEmpresa = await db.PuntosVenta
            .Where(pv => pv.Id == id)
            .Select(pv => (int?)pv.IdEmpresa)
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {id}.");

        var resuelto = await parametros.ResolverAsync(ParametroConocido.ZonaHoraria.Clave, idEmpresa, id, ct);
        var zonaId = JsonSerializer.Deserialize<string>(resuelto.Valor)!;

        return (idEmpresa.ToString(), zonaId);
    }
}
