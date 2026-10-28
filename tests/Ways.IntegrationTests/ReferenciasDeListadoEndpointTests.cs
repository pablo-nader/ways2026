using System.Net;
using System.Text.Json;
using Ways.Domain.Compras;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>El cliente o proveedor de cada fila de Compras, Presupuestos, Remitos y Órdenes de compra sale del servidor:
/// la página trae el nombre (y el número de cliente) de cada fila aunque el front no tenga esa referencia en su lista, y
/// un cliente o proveedor dado de baja llega vacío sin sacar la fila de la página.</summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ReferenciasDeListadoEndpointTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private static async Task<JsonElement[]> FilasAsync(HttpClient cliente, string ruta)
    {
        var respuesta = await cliente.GetAsync($"{ruta}?tamanio=50");
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        using var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync());
        return [.. json.RootElement.GetProperty("items").EnumerateArray().Select(i => i.Clone())];
    }

    private static string? Texto(JsonElement fila, string propiedad) =>
        fila.TryGetProperty(propiedad, out var valor) && valor.ValueKind == JsonValueKind.String ? valor.GetString() : null;

    private static int? Entero(JsonElement fila, string propiedad) =>
        fila.TryGetProperty(propiedad, out var valor) && valor.ValueKind == JsonValueKind.Number ? valor.GetInt32() : null;

    [Fact]
    public async Task ComprasYOrdenesDeCompraTraenLaRazonSocialDelProveedorYVacioSiEstaDadoDeBaja()
    {
        var e = await SembradoDeDocumentos.AprovisionarAsync(fixture, "rfp");
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var viva = SembradoDeDocumentos.Proveedor(e, "Distribuidora Norte");
        var baja = SembradoDeDocumentos.Proveedor(e, "ZZ-baja", baja: true);
        db.Proveedores.AddRange(viva, baja);
        await db.SaveChangesAsync();

        var idsCompra = new List<int>();
        var idsOrden = new List<int>();
        foreach (var proveedor in new[] { viva.Id, baja.Id })
        {
            var compra = new ComprobanteCompra
            {
                IdTenant = e.IdTenant, IdPuntoVenta = e.PvA, IdProveedor = proveedor, IdTipoComprobante = e.TiposCompra[0],
                IdEmpleado = e.IdEmpleado, Estado = EstadoCompra.Borrador, Total = 10m, Subtotal = 10m,
                CreatedAt = SembradoDeDocumentos.Ahora, UpdatedAt = SembradoDeDocumentos.Ahora
            };
            var orden = new OrdenCompra
            {
                IdTenant = e.IdTenant, IdPuntoVenta = e.PvA, IdProveedor = proveedor, IdEmpleado = e.IdEmpleado,
                Estado = EstadoOrdenCompra.Borrador, FechaEmision = SembradoDeDocumentos.Ahora,
                CreatedAt = SembradoDeDocumentos.Ahora, UpdatedAt = SembradoDeDocumentos.Ahora
            };
            db.ComprobantesCompra.Add(compra);
            db.OrdenesCompra.Add(orden);
            await db.SaveChangesAsync();
            idsCompra.Add(compra.Id);
            idsOrden.Add(orden.Id);
        }

        foreach (var (ruta, ids) in new[] { ("/api/compras", idsCompra), ("/api/ordenes-compra", idsOrden) })
        {
            var filas = await FilasAsync(e.Admin, ruta);

            Assert.Equal(2, filas.Length);
            Assert.Equal("Distribuidora Norte", Texto(filas.Single(f => f.GetProperty("id").GetInt32() == ids[0]), "nombreProveedor"));
            Assert.Null(Texto(filas.Single(f => f.GetProperty("id").GetInt32() == ids[1]), "nombreProveedor"));
        }
    }

    [Fact]
    public async Task PresupuestosYRemitosTraenNumeroYNombreDelClienteYVacioSiEstaDadoDeBaja()
    {
        var e = await SembradoDeDocumentos.AprovisionarAsync(fixture, "rfc");
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var conRazon = SembradoDeDocumentos.Cliente(e, 10);
        conRazon.RazonSocial = "Ferretería Sur";
        var sinRazon = SembradoDeDocumentos.Cliente(e, 11);
        sinRazon.Apellido = "Gómez";
        var baja = SembradoDeDocumentos.Cliente(e, 12, baja: true);
        db.Clientes.AddRange(conRazon, sinRazon, baja);
        await db.SaveChangesAsync();

        var idsPresupuesto = new List<int>();
        var idsRemito = new List<int>();
        foreach (var cliente in new[] { conRazon.Id, sinRazon.Id, baja.Id })
        {
            var presupuesto = new Presupuesto
            {
                IdTenant = e.IdTenant, IdPuntoVenta = e.PvA, IdCliente = cliente, IdEmpleado = e.IdEmpleado,
                Estado = EstadoPresupuesto.Borrador, FechaEmision = SembradoDeDocumentos.Ahora, Total = 10m, Subtotal = 10m,
                CreatedAt = SembradoDeDocumentos.Ahora, UpdatedAt = SembradoDeDocumentos.Ahora
            };
            var remito = new Remito
            {
                IdTenant = e.IdTenant, IdPuntoVenta = e.PvA, IdCliente = cliente, IdEmpleado = e.IdEmpleado,
                Estado = EstadoRemito.Borrador, FechaEmision = SembradoDeDocumentos.Ahora, Total = 10m, Subtotal = 10m,
                CreatedAt = SembradoDeDocumentos.Ahora, UpdatedAt = SembradoDeDocumentos.Ahora
            };
            db.Presupuestos.Add(presupuesto);
            db.Remitos.Add(remito);
            await db.SaveChangesAsync();
            idsPresupuesto.Add(presupuesto.Id);
            idsRemito.Add(remito.Id);
        }

        foreach (var (ruta, ids) in new[] { ("/api/presupuestos", idsPresupuesto), ("/api/remitos", idsRemito) })
        {
            var filas = await FilasAsync(e.Admin, ruta);
            JsonElement Fila(int i) => filas.Single(f => f.GetProperty("id").GetInt32() == ids[i]);

            Assert.Equal(3, filas.Length);
            Assert.Equal(10, Entero(Fila(0), "numeroCliente"));
            Assert.Equal("Ferretería Sur", Texto(Fila(0), "nombreCliente"));
            Assert.Equal(11, Entero(Fila(1), "numeroCliente"));
            Assert.Equal("Cli11 Gómez", Texto(Fila(1), "nombreCliente"));
            Assert.Null(Entero(Fila(2), "numeroCliente"));
            Assert.Null(Texto(Fila(2), "nombreCliente"));
        }
    }
}
