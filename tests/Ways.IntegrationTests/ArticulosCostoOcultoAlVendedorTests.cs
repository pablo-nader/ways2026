using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Articulos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Los costos del artículo (<c>CostoNominal</c>, <c>CostoLista</c>, <c>DescuentoProveedor</c>) los ven
/// admin y supervisor: el listado, el detalle y la grilla heredan <c>OperacionDePos</c> (el vendedor
/// lee los mismos endpoints desde el POS), así que al vendedor le llega <c>null</c> en vez del costo.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ArticulosCostoOcultoAlVendedorTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordVendedor = "una-contraseña-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private async Task<(int IdArticulo, HttpClient Admin, HttpClient Vendedor)> PrepararAsync(string nombre, RolConocido rolNoAdmin)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var respuesta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var hasheador = new HasheadorPbkdf2();
        var mailVendedor = $"{nombre.ToLowerInvariant()}-vendedor@ways.test";
        int idArticulo;
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            var area = new Area { IdTenant = resultado.IdTenant, Nombre = $"{nombre}-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
            db.Areas.Add(area);
            await db.SaveChangesAsync();

            var articulo = new Articulo
            {
                IdTenant = resultado.IdTenant,
                CodigoInterno = "COSTO-1",
                Nombre = "Artículo con costo",
                IdArea = area.Id,
                IdAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync(),
                UnidadVenta = UnidadVenta.Unidad,
                EsProducto = true,
                CostoLista = 70m,
                DescuentoProveedor = 5m,
                CostoNominal = 61.5m,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };
            db.Articulos.Add(articulo);
            db.Usuarios.Add(new Usuario
            {
                IdTenant = resultado.IdTenant,
                NombreUsuario = "vendedor",
                Mail = mailVendedor,
                RolId = (int)rolNoAdmin,
                PasswordHash = hasheador.Hashear(PasswordVendedor),
                PasswordAlgoritmo = hasheador.Algoritmo,
                PasswordActualizadoEl = ahora,
                CreatedAt = ahora,
                UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
            idArticulo = articulo.Id;
        }

        var admin = fixture.CreateClient();
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal))).StatusCode);
        var vendedor = fixture.CreateClient();
        Assert.Equal(
            HttpStatusCode.OK,
            (await vendedor.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailVendedor, PasswordVendedor))).StatusCode);

        return (idArticulo, admin, vendedor);
    }

    [Theory]
    [InlineData(RolConocido.Admin)]
    [InlineData(RolConocido.Supervisor)]
    public async Task UnRolDeBackOfficeVeLosCostosEnListadoDetalleYGrilla(RolConocido rol)
    {
        var (idArticulo, admin, supervisor) = await PrepararAsync($"CostoVisible{rol}", RolConocido.Supervisor);
        var cliente = rol == RolConocido.Admin ? admin : supervisor;

        var listado = await cliente.GetFromJsonAsync<PaginaDe<ArticuloListado>>("/api/articulos?busqueda=con+costo", OpcionesJson);
        var fila = Assert.Single(listado!.Items);
        Assert.Equal((70m, 5m, 61.5m), (fila.CostoLista, fila.DescuentoProveedor, fila.CostoNominal));

        var detalle = await cliente.GetFromJsonAsync<ArticuloListado>($"/api/articulos/{idArticulo}", OpcionesJson);
        Assert.Equal((70m, 5m, 61.5m), (detalle!.CostoLista, detalle.DescuentoProveedor, detalle.CostoNominal));

        var grilla = await cliente.GetFromJsonAsync<PaginaDeArticulosGrilla>("/api/articulos/grilla?nombre=con+costo", OpcionesJson);
        Assert.Equal(61.5m, Assert.Single(grilla!.Items).CostoNominal);
    }

    [Fact]
    public async Task ElVendedorRecibeLosCostosEnNullEnListadoDetalleYGrilla()
    {
        var (idArticulo, _, vendedor) = await PrepararAsync(nameof(ElVendedorRecibeLosCostosEnNullEnListadoDetalleYGrilla), RolConocido.Vendedor);

        var listado = await vendedor.GetFromJsonAsync<PaginaDe<ArticuloListado>>("/api/articulos?busqueda=con+costo", OpcionesJson);
        var fila = Assert.Single(listado!.Items);
        Assert.Equal((null, null, null), (fila.CostoLista, fila.DescuentoProveedor, fila.CostoNominal));

        var detalle = await vendedor.GetFromJsonAsync<ArticuloListado>($"/api/articulos/{idArticulo}", OpcionesJson);
        Assert.Equal((null, null, null), (detalle!.CostoLista, detalle.DescuentoProveedor, detalle.CostoNominal));

        var grilla = await vendedor.GetFromJsonAsync<PaginaDeArticulosGrilla>("/api/articulos/grilla?nombre=con+costo", OpcionesJson);
        Assert.Null(Assert.Single(grilla!.Items).CostoNominal);
    }
}
