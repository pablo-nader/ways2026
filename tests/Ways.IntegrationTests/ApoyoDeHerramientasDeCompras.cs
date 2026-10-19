using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using static Ways.IntegrationTests.ApoyoDeConectorMcp;

namespace Ways.IntegrationTests;

/// <summary>Siembra y llamadas compartidas por las pruebas de las herramientas de compras del
/// conector MCP. Siembra en modo plataforma con el tenant explícito en cada fila.</summary>
internal sealed class ApoyoDeHerramientasDeCompras(WaysApiFixture fixture)
{
    public sealed record RespuestaDeHerramienta(string Texto, bool EsError);

    /// <summary>El rol se cambia antes de pedir el token: después, el validador de sesión revocaría
    /// el token por rol cambiado.</summary>
    public async Task CambiarRolAsync(int idUsuario, RolConocido rol)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var usuario = await db.Usuarios.SingleAsync(u => u.Id == idUsuario);
        usuario.RolId = (int)rol;
        await db.SaveChangesAsync();
    }

    public async Task<int> SembrarProveedorAsync(
        int idTenant,
        string razonSocial,
        string? cuit = null,
        string? nombreFantasia = null,
        bool activo = true,
        bool percibeIibb = false,
        bool percibeIva = false,
        bool preciosIncluyenIva = false,
        int? idEmpresa = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();

        var proveedor = new Proveedor
        {
            IdTenant = idTenant,
            IdEmpresa = idEmpresa,
            RazonSocial = razonSocial,
            NombreFantasia = nombreFantasia,
            Cuit = cuit,
            IdCondicionFiscal = idCondicionFiscal,
            Activo = activo,
            PercibeIibb = percibeIibb,
            PercibeIva = percibeIva,
            PreciosIncluyenIva = preciosIncluyenIva,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();
        return proveedor.Id;
    }

    public async Task<(int IdEmpresa, int IdPuntoVenta)> SembrarEmpresaConPuntoDeVentaAsync(
        int idTenant, string razonSocial, string nombrePuntoVenta, decimal? percepcionIibb = null, decimal? percepcionIva = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var empresa = new Empresa
        {
            IdTenant = idTenant,
            RazonSocial = razonSocial,
            AlicuotaPercepcionIibb = percepcionIibb,
            AlicuotaPercepcionIva = percepcionIva,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        var puntoVenta = new PuntoVenta
        {
            IdTenant = idTenant,
            IdEmpresa = empresa.Id,
            Nombre = nombrePuntoVenta,
            Modo = ModoPuntoVenta.Web,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();

        return (empresa.Id, puntoVenta.Id);
    }

    public sealed record ArticuloSembrado(int Id, string CodigoInterno);

    /// <summary>Artículo con costos cargados, para probar que la salida no los expone. Cada llamada
    /// crea su propia área en el tenant.</summary>
    public async Task<ArticuloSembrado> SembrarArticuloAsync(
        int idTenant,
        string nombre,
        UnidadVenta unidad = UnidadVenta.Unidad,
        bool activo = true,
        string? codigoBarras = null,
        (int IdProveedor, string Codigo)? codigoProveedor = null,
        int? idAlicuotaIva = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area { IdTenant = idTenant, Nombre = $"Área {Guid.NewGuid():N}", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var articulo = new Articulo
        {
            IdTenant = idTenant,
            CodigoInterno = $"CI-{Guid.NewGuid().ToString("N")[..10]}",
            Nombre = nombre,
            IdArea = area.Id,
            IdAlicuotaIva = idAlicuotaIva ?? await IdAlicuotaDel21Async(db),
            UnidadVenta = unidad,
            EsProducto = true,
            Activo = activo,
            CostoLista = CostoListaSembrado,
            CostoNominal = CostoNominalSembrado,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        if (codigoBarras is not null)
        {
            db.CodigosBarra.Add(new CodigoBarra
            {
                IdTenant = idTenant, IdArticulo = articulo.Id, Codigo = codigoBarras, CreatedAt = ahora, UpdatedAt = ahora
            });
        }

        if (codigoProveedor is { } cp)
        {
            db.CodigosProveedor.Add(new CodigoProveedor
            {
                IdTenant = idTenant, IdArticulo = articulo.Id, IdProveedor = cp.IdProveedor, Codigo = cp.Codigo,
                CreatedAt = ahora, UpdatedAt = ahora
            });
        }

        await db.SaveChangesAsync();
        return new ArticuloSembrado(articulo.Id, articulo.CodigoInterno);
    }

    public const decimal CostoListaSembrado = 1234.56m;

    public const decimal CostoNominalSembrado = 987.65m;

    /// <summary>Alícuota propia de la prueba, dada de baja: el catálogo es global y las sembradas
    /// no se tocan.</summary>
    public async Task<int> SembrarAlicuotaDadaDeBajaAsync()
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var alicuota = new AlicuotaIva
        {
            Nombre = $"Baja {Guid.NewGuid():N}"[..20], Porcentaje = 21m, CreatedAt = ahora, UpdatedAt = ahora, DeletedAt = ahora
        };
        db.AlicuotasIva.Add(alicuota);
        await db.SaveChangesAsync();
        return alicuota.Id;
    }

    public async Task<int> IdAlicuotaDel21Async()
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        return await IdAlicuotaDel21Async(db);
    }

    private static Task<int> IdAlicuotaDel21Async(WaysDbContext db) =>
        db.AlicuotasIva.Where(a => a.Porcentaje == 21m).Select(a => a.Id).FirstAsync();

    /// <summary>Texto del primer bloque del resultado y su <c>isError</c> (ausente = <c>false</c>).</summary>
    public static async Task<RespuestaDeHerramienta> LlamarHerramientaAsync(
        HttpClient cliente, string accessToken, string nombre, object argumentos)
    {
        var (estado, mensaje) = await LlamarMcpAsync(
            cliente, accessToken, "tools/call", new { name = nombre, arguments = argumentos });
        Assert.Equal(HttpStatusCode.OK, estado);

        var resultado = mensaje!.Value.GetProperty("result");
        var esError = resultado.TryGetProperty("isError", out var marca) && marca.ValueKind == JsonValueKind.True;
        return new RespuestaDeHerramienta(resultado.GetProperty("content")[0].GetProperty("text").GetString()!, esError);
    }
}
