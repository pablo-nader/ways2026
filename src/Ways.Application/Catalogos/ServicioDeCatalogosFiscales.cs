using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;

namespace Ways.Application.Catalogos;

/// <summary>
/// Los 3 catálogos globales (ADR-11, gate #4) son de solo lectura para la API en esta etapa
/// — sin ABM, la plataforma los siembra (<c>InicializadorDeBaseDeDatos</c>). RLS ya deja leer
/// en cualquier modo de acceso (<c>HabilitarRlsDeCatalogoGlobal</c>); esto solo agrega la
/// proyección a DTO.
/// </summary>
public class ServicioDeCatalogosFiscales(IWaysDbContext db)
{
    public async Task<IReadOnlyList<CondicionFiscalListado>> ListarCondicionesFiscalesAsync(
        CancellationToken ct = default) =>
        await db.CondicionesFiscales
            .Where(c => c.Activo)
            .OrderBy(c => c.Codigo)
            .Select(c => new CondicionFiscalListado(c.Id, c.Codigo, c.Nombre, c.CodigoAfip, c.Activo))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<AlicuotaIvaListado>> ListarAlicuotasIvaAsync(CancellationToken ct = default) =>
        await db.AlicuotasIva
            .Where(a => a.Activo)
            .OrderByDescending(a => a.Porcentaje)
            .Select(a => new AlicuotaIvaListado(a.Id, a.Nombre, a.Porcentaje, a.CodigoAfip, a.Activo))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<TipoComprobanteListado>> ListarTiposComprobanteAsync(
        CancellationToken ct = default) =>
        await db.TiposComprobante
            .Where(t => t.Activo)
            .OrderBy(t => t.Codigo)
            .Select(t => new TipoComprobanteListado(
                t.Id, t.Clase, t.Codigo, t.Nombre, t.Letra, t.Signo,
                t.DiscriminaIva, t.EsFiscal, t.AfectaStock, t.CodigoAfip, t.Activo))
            .ToListAsync(ct);
}
