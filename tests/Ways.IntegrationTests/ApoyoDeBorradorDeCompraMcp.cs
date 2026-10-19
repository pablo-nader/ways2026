using Microsoft.EntityFrameworkCore;
using Ways.Domain.Compras;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>Siembra y lecturas de las pruebas de <c>crear_borrador_de_compra</c>; los artículos los siembra
/// <see cref="ApoyoDeHerramientasDeCompras"/>. Todo en modo plataforma, con el tenant explícito en cada fila: las
/// lecturas ven lo que quedó en la base sin pasar por la herramienta.</summary>
internal sealed class ApoyoDeBorradorDeCompraMcp(WaysApiFixture fixture)
{
    public async Task<int> IdAlicuotaAsync(decimal porcentaje)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        return await db.AlicuotasIva
            .Where(a => a.Porcentaje == porcentaje && a.Activo)
            .OrderBy(a => a.Id)
            .Select(a => a.Id)
            .FirstAsync();
    }

    public async Task<int> IdTipoAsync(string codigo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        return await db.TiposComprobante.Where(t => t.Codigo == codigo).Select(t => t.Id).SingleAsync();
    }

    /// <summary>Una compra cargada por fuera de la herramienta, con el número guardado tal cual se pasa (las
    /// filas viejas pueden tener otro formato que el canónico).</summary>
    public async Task<int> SembrarCompraAsync(
        int idTenant, int idProveedor, int idTipoComprobante, int idPuntoVenta, int idEmpleado, string numeroExterno,
        EstadoCompra estado, DateOnly fechaComprobante)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var compra = new ComprobanteCompra
        {
            IdTenant = idTenant,
            IdProveedor = idProveedor,
            IdTipoComprobante = idTipoComprobante,
            IdPuntoVenta = idPuntoVenta,
            IdEmpleado = idEmpleado,
            NumeroExterno = numeroExterno,
            FechaComprobante = fechaComprobante,
            FechaRecepcion = estado == EstadoCompra.Borrador ? null : ahora,
            Subtotal = 100m,
            DescuentoTotal = 0m,
            DiscriminaIva = true,
            IvaTotal = 21m,
            Total = 121m,
            Estado = estado,
            Origen = OrigenCompra.Manual,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesCompra.Add(compra);
        await db.SaveChangesAsync();
        return compra.Id;
    }

    public async Task<List<ComprobanteCompra>> ComprasDelProveedorAsync(int idProveedor)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        return await db.ComprobantesCompra.AsNoTracking().Where(c => c.IdProveedor == idProveedor).OrderBy(c => c.Id).ToListAsync();
    }

    public async Task<int> ContarComprasDelTenantAsync(int idTenant)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        return await db.ComprobantesCompra.CountAsync(c => c.IdTenant == idTenant);
    }

    public async Task<List<ItemComprobanteCompra>> ItemsAsync(int idCompra)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        return await db.ItemsComprobanteCompra.AsNoTracking().Where(i => i.IdComprobanteCompra == idCompra).OrderBy(i => i.Orden).ToListAsync();
    }

    public async Task<List<AlicuotaComprobanteCompra>> AlicuotasAsync(int idCompra)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        return await db.AlicuotasComprobanteCompra.AsNoTracking().Where(a => a.IdComprobanteCompra == idCompra).ToListAsync();
    }

    public async Task<List<PercepcionComprobanteCompra>> PercepcionesAsync(int idCompra)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        return await db.PercepcionesComprobanteCompra.AsNoTracking().Where(p => p.IdComprobanteCompra == idCompra).ToListAsync();
    }
}
