using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Compras;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// El costo de las compras y el dinero de las órdenes de compra son del back-office: el vendedor
/// lee ambos recursos bajo <c>OperacionDePos</c> pero recibe <c>null</c> en el costo de cada línea
/// y en todo el dinero de la orden. Los totales del encabezado de la compra se conservan porque el
/// vendedor paga compras desde el turno. Supervisor y admin ven todo.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ComprasCostoOcultoAlVendedorTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOperador = "una-contraseña-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Escenario(
        HttpClient Admin, HttpClient Supervisor, HttpClient Vendedor, int IdCompra, int IdOrden, int IdArticulo1, int IdArticulo2);

    private async Task<HttpClient> CrearYLoguearAsync(HttpClient admin, string nombre, string sufijo, RolConocido rol)
    {
        var mail = $"{nombre.ToLowerInvariant()}-{sufijo}@ways.test";
        var alta = await admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario($"{sufijo}-{Guid.NewGuid():N}"[..16], mail, (int)rol, PasswordOperador));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordOperador));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private static async Task<T> LeerAsync<T>(HttpResponseMessage respuesta, HttpStatusCode esperado)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == esperado, cuerpo);
        return JsonSerializer.Deserialize<T>(cuerpo, OpcionesJson)!;
    }

    /// <summary>Una compra confirmada de 10 u a 100 con IVA discriminado y una orden de compra con
    /// dos líneas cotizadas, recibida en parte: todos los valores de dinero son distintos entre sí.</summary>
    private async Task<Escenario> PrepararAsync(string nombre)
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

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        int idProveedor;
        int idArticulo1;
        int idArticulo2;
        int idAlicuotaIva21;
        int idTipoCFA;
        int idTipoCFB;
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Costo-oculto-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
            db.Areas.Add(area);
            await db.SaveChangesAsync();

            idAlicuotaIva21 = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();
            idTipoCFA = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();
            idTipoCFB = await db.TiposComprobante.Where(t => t.Codigo == "C-FB").Select(t => t.Id).SingleAsync();

            var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
            db.CondicionesFiscales.Add(condicionFiscal);
            await db.SaveChangesAsync();

            var proveedor = new Proveedor
            {
                IdTenant = resultado.IdTenant, RazonSocial = nombre, IdCondicionFiscal = condicionFiscal.Id,
                Margen = 50m, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Proveedores.Add(proveedor);
            await db.SaveChangesAsync();

            var articulo1 = new Articulo
            {
                IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-1-{Guid.NewGuid():N}", Nombre = "Articulo 1",
                IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
                IdProveedorHabitual = proveedor.Id,
                CreatedAt = ahora, UpdatedAt = ahora
            };
            var articulo2 = new Articulo
            {
                IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-2-{Guid.NewGuid():N}", Nombre = "Articulo 2",
                IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
                CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Articulos.AddRange(articulo1, articulo2);
            await db.SaveChangesAsync();

            idProveedor = proveedor.Id;
            idArticulo1 = articulo1.Id;
            idArticulo2 = articulo2.Id;
        }

        var supervisor = await CrearYLoguearAsync(admin, nombre, "supervisor", RolConocido.Supervisor);
        var vendedor = await CrearYLoguearAsync(admin, nombre, "vendedor", RolConocido.Vendedor);

        var compraCreada = await LeerAsync<CompraDetalle>(
            await admin.PostAsJsonAsync(
                "/api/compras",
                new SolicitudDeCompra(
                    idProveedor, idTipoCFA, resultado.IdPuntoVenta, DatosDePrueba.NumeroExternoUnico(), FechaDelNegocio.Hoy(), null,
                    [new LineaDeCompraSolicitada(idArticulo1, "Linea de compra", 10m, null, null, 100m, 0m, idAlicuotaIva21)])),
            HttpStatusCode.Created);
        var compra = await LeerAsync<CompraDetalle>(
            await admin.PostAsync($"/api/compras/{compraCreada.Id}/confirmar", null), HttpStatusCode.OK);

        var borradorDeOrden = await LeerAsync<OrdenDeCompraBorrador>(
            await admin.PostAsJsonAsync(
                "/api/ordenes-compra",
                new SolicitudDeOrdenDeCompra(
                    idProveedor, resultado.IdPuntoVenta, null, null,
                    [
                        new LineaDeOrdenSolicitada(idArticulo1, "Pedido 1", 10m, 100m),
                        new LineaDeOrdenSolicitada(idArticulo2, "Pedido 2", 2m, 40m)
                    ])),
            HttpStatusCode.Created);
        await LeerAsync<OrdenDeCompraBorrador>(
            await admin.PostAsync($"/api/ordenes-compra/{borradorDeOrden.Id}/enviar", null), HttpStatusCode.OK);

        var recepcion = await LeerAsync<CompraDetalle>(
            await admin.PostAsJsonAsync(
                "/api/compras",
                new SolicitudDeCompra(
                    idProveedor, idTipoCFB, resultado.IdPuntoVenta, DatosDePrueba.NumeroExternoUnico(), FechaDelNegocio.Hoy(), null,
                    [new LineaDeCompraSolicitada(idArticulo1, "Recepción", 4m, null, null, 110m, 0m, idAlicuotaIva21, false)],
                    borradorDeOrden.Id)),
            HttpStatusCode.Created);
        await LeerAsync<CompraDetalle>(
            await admin.PostAsync($"/api/compras/{recepcion.Id}/confirmar", null), HttpStatusCode.OK);

        return new Escenario(admin, supervisor, vendedor, compra.Id, borradorDeOrden.Id, idArticulo1, idArticulo2);
    }

    private static async Task<CompraDetalle> LeerCompraAsync(HttpClient cliente, int id) =>
        await LeerAsync<CompraDetalle>(await cliente.GetAsync($"/api/compras/{id}"), HttpStatusCode.OK);

    private static async Task<OrdenDeCompraDetalle> LeerOrdenAsync(HttpClient cliente, int id) =>
        await LeerAsync<OrdenDeCompraDetalle>(await cliente.GetAsync($"/api/ordenes-compra/{id}"), HttpStatusCode.OK);

    private static async Task AssertVeTodoElCostoAsync(Escenario escenario, HttpClient cliente)
    {
        var compra = await LeerCompraAsync(cliente, escenario.IdCompra);
        var item = Assert.Single(compra.Items);
        Assert.Equal(100m, item.CostoUnitario);
        Assert.Equal(0m, item.Descuento);
        Assert.Equal(1000m, item.Total);
        Assert.Equal(181.5m, item.PrecioSugerido);
        Assert.Equal(1210m, compra.Total);

        var alicuota = Assert.Single(compra.Alicuotas);
        Assert.Equal(1000m, alicuota.Neto);
        Assert.Equal(210m, alicuota.Iva);

        var orden = await LeerOrdenAsync(cliente, escenario.IdOrden);
        Assert.Equal(1080m, orden.TotalEstimado);
        Assert.Equal(440m, orden.TotalReal);
        Assert.Equal(-59.26m, orden.DesvioTotal);
        Assert.Equal(100m, orden.Items.Single(i => i.IdArticulo == escenario.IdArticulo1).CostoUnitarioEstimado);
        Assert.Equal(40m, orden.Items.Single(i => i.IdArticulo == escenario.IdArticulo2).CostoUnitarioEstimado);

        var cobertura1 = orden.Cobertura.Single(c => c.IdArticulo == escenario.IdArticulo1);
        Assert.Equal(100m, cobertura1.CostoEstimado);
        Assert.Equal(110m, cobertura1.CostoReal);
        Assert.Equal(10.00m, cobertura1.Desvio);
    }

    [Fact]
    public async Task ElAdminVeElCostoDeLasLineasYElDineroDeLaOrden()
    {
        var escenario = await PrepararAsync(nameof(ElAdminVeElCostoDeLasLineasYElDineroDeLaOrden));

        await AssertVeTodoElCostoAsync(escenario, escenario.Admin);
    }

    [Fact]
    public async Task ElSupervisorVeElCostoDeLasLineasYElDineroDeLaOrden()
    {
        var escenario = await PrepararAsync(nameof(ElSupervisorVeElCostoDeLasLineasYElDineroDeLaOrden));

        await AssertVeTodoElCostoAsync(escenario, escenario.Supervisor);
    }

    [Fact]
    public async Task ElVendedorNoVeElCostoDeLasLineasPeroConservaLosTotalesDelEncabezado()
    {
        var escenario = await PrepararAsync(nameof(ElVendedorNoVeElCostoDeLasLineasPeroConservaLosTotalesDelEncabezado));

        var vistoPorAdmin = await LeerCompraAsync(escenario.Admin, escenario.IdCompra);
        var compra = await LeerCompraAsync(escenario.Vendedor, escenario.IdCompra);

        var item = Assert.Single(compra.Items);
        Assert.Null(item.CostoUnitario);
        Assert.Null(item.Descuento);
        Assert.Null(item.Total);
        Assert.Null(item.PrecioSugerido);
        Assert.Equal(10m, item.Cantidad);
        Assert.True(item.ActualizaCosto);

        // El desglose por alícuota reconstruye importes de línea: la fila existe, sus montos no.
        var alicuota = Assert.Single(compra.Alicuotas);
        Assert.Equal(21m, alicuota.Porcentaje);
        Assert.Null(alicuota.Neto);
        Assert.Null(alicuota.Iva);

        Assert.Equal(1210m, compra.Total);
        Assert.Equal(vistoPorAdmin.Subtotal, compra.Subtotal);
        Assert.Equal(vistoPorAdmin.DescuentoTotal, compra.DescuentoTotal);
        Assert.Equal(vistoPorAdmin.IvaTotal, compra.IvaTotal);
        Assert.NotEqual(0m, compra.Subtotal);

        var listado = await LeerAsync<PaginaDeCompras>(await escenario.Vendedor.GetAsync("/api/compras"), HttpStatusCode.OK);
        Assert.Equal(1210m, listado.Items.Single(c => c.Id == escenario.IdCompra).Total);
    }

    [Fact]
    public async Task ElVendedorNoVeNingunMontoDeLaOrdenDeCompraPeroSiLasCantidades()
    {
        var escenario = await PrepararAsync(nameof(ElVendedorNoVeNingunMontoDeLaOrdenDeCompraPeroSiLasCantidades));

        var orden = await LeerOrdenAsync(escenario.Vendedor, escenario.IdOrden);

        Assert.Null(orden.TotalEstimado);
        Assert.Null(orden.TotalReal);
        Assert.Null(orden.DesvioTotal);
        Assert.All(orden.Items, i => Assert.Null(i.CostoUnitarioEstimado));
        Assert.All(orden.Cobertura, c =>
        {
            Assert.Null(c.CostoEstimado);
            Assert.Null(c.CostoReal);
            Assert.Null(c.Desvio);
        });

        Assert.Equal(2, orden.Items.Count);
        var cobertura1 = orden.Cobertura.Single(c => c.IdArticulo == escenario.IdArticulo1);
        Assert.Equal(10m, cobertura1.Pedida);
        Assert.Equal(4m, cobertura1.Recibida);
        Assert.Equal(6m, cobertura1.Pendiente);

        var listado = await LeerAsync<PaginaDeOrdenesDeCompra>(
            await escenario.Vendedor.GetAsync("/api/ordenes-compra"), HttpStatusCode.OK);
        Assert.Contains(listado.Items, o => o.Id == escenario.IdOrden);
    }
}
