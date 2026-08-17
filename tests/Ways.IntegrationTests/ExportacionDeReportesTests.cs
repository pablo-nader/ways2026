using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Exportacion;
using Ways.Application.Organizacion;
using Ways.Application.Parametros;
using Ways.Application.Reportes;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.Gastos;
using Ways.Domain.Proveedores;
using Ways.Domain.Reportes;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-11-exportacion-reportes, Slice 2 (tasks 2.4-2.8): los ocho export siblings restantes de
/// stage-10 (por-punto-venta, por-vendedor, por-medio-pago, articulos/top, compras/por-proveedor,
/// gastos/resumen, rentabilidad, comisiones) — mismo patrón que
/// <see cref="ReportesVentasResumenExportTests"/> (equality + 403), más el bloque de cobertura de
/// rentabilidad y la etiqueta PROVISIONAL de comisiones (spec rentabilidad-y-comisiones:
/// Rentabilidad And Comisiones Exports Stack LecturaDeRentabilidad And Carry Coverage). Sembrado
/// con fechas fijas + mediodía UTC (nunca <c>DateTime.UtcNow</c>-derivado) — lección de la ventana
/// 00-03 UTC de PR #89.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ExportacionDeReportesTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";
    private const string ContentTypeXlsx =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static long _numeroSecuencial = 1;
    private static long _numeroExternoSecuencial = 1;

    private static readonly DateOnly Dia = new(2026, 8, 1);
    private static readonly DateTimeOffset MediodiaUtc = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, HttpClient Supervisor, HttpClient Vendedor,
        int IdCliente, int IdEmpleadoAdmin, int IdTipoComprobanteTx, int IdArea, int IdAlicuotaIva, int IdListaPrecio,
        int IdMedioPagoEfectivo, int IdProveedor);

    /// <summary>Parametrizado por <paramref name="factory"/> (mismo idioma que
    /// <c>ReportesVentasResumenExportTests.PrepararAsync</c>): las pruebas de tope de esta clase
    /// usan un <c>WithWebHostBuilder</c> propio para bajar <c>OpcionesDeExportacion.TopeDeFilas</c>
    /// sin afectar al resto de la clase, que sigue pasando la <c>fixture</c> compartida.</summary>
    private async Task<Contexto> PrepararAsync(string nombre, WebApplicationFactory<Program> factory)
    {
        var root = factory.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = factory.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        var supervisor = await CrearYLoguearAsync(admin, factory, nombre, "supervisor", RolConocido.Supervisor);
        var vendedor = await CrearYLoguearAsync(admin, factory, nombre, "vendedor", RolConocido.Vendedor);

        await using var dbTenant = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idCliente = await dbTenant.Clientes.Select(c => c.Id).FirstAsync();
        var idAlicuotaIva = await dbTenant.AlicuotasIva.Select(a => a.Id).FirstAsync();
        var idListaPrecio = await dbTenant.ListasPrecio.Select(l => l.Id).FirstAsync();
        var idMedioEfectivo = await dbTenant.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Area export", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        dbTenant.Areas.Add(area);
        await dbTenant.SaveChangesAsync();

        await using var dbPlataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idTipoComprobanteTx = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).SingleAsync();

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        dbPlataforma.CondicionesFiscales.Add(condicionFiscal);
        await dbPlataforma.SaveChangesAsync();

        var proveedor = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = $"{nombre}-Prov", IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        dbPlataforma.Proveedores.Add(proveedor);
        await dbPlataforma.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, supervisor, vendedor,
            idCliente, resultado.IdUsuarioAdmin, idTipoComprobanteTx, area.Id, idAlicuotaIva, idListaPrecio,
            idMedioEfectivo, proveedor.Id);
    }

    private static async Task<HttpClient> CrearYLoguearAsync(
        HttpClient admin, WebApplicationFactory<Program> factory, string nombre, string sufijo, RolConocido rol)
    {
        var corto = Guid.NewGuid().ToString("N")[..8];
        var mail = $"{nombre.ToLowerInvariant()}-{sufijo}@ways.test";
        var alta = await admin.PostAsJsonAsync("/api/usuarios", new CrearUsuario($"{sufijo}-{corto}", mail, (int)rol, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = factory.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    /// <summary>Siembra directo, sin pasar por <c>ServicioDeVentas</c> — mismo criterio que
    /// <c>ReportesVentasResumenTests.SembrarComprobanteAsync</c>. Devuelve el id del comprobante
    /// para encadenar pago/item.</summary>
    private async Task<int> SembrarComprobanteAsync(
        Contexto ctx, decimal total, int? idEmpleado = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var comprobante = new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant,
            IdTipoComprobante = ctx.IdTipoComprobanteTx,
            Numero = Interlocked.Increment(ref _numeroSecuencial),
            Fecha = MediodiaUtc,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdEmpleado = idEmpleado ?? ctx.IdEmpleadoAdmin,
            IdCliente = ctx.IdCliente,
            Subtotal = total,
            DescuentoTotal = 0m,
            Total = total,
            Estado = EstadoComprobante.Emitido,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();
        return comprobante.Id;
    }

    private async Task SembrarPagoAsync(Contexto ctx, int idComprobanteVenta, decimal importe)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        db.PagosComprobante.Add(new PagoComprobante
        {
            IdTenant = ctx.IdTenant,
            IdComprobanteVenta = idComprobanteVenta,
            IdMedioPago = ctx.IdMedioPagoEfectivo,
            Importe = importe,
            Vuelto = 0m,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private async Task<int> SembrarArticuloAsync(Contexto ctx, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre,
            IdArea = ctx.IdArea, IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();
        return articulo.Id;
    }

    /// <summary>Siembra un comprobante con un único ítem — costo opcional para alimentar tanto
    /// <c>articulos/top</c> (sin costo) como <c>rentabilidad</c> (con costo real/estimado).</summary>
    private async Task<int> SembrarLineaAsync(
        Contexto ctx, int idArticulo, string descripcion, decimal cantidad, decimal total,
        decimal? costoUnitario = null, bool costoEsEstimado = false)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var comprobante = new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant,
            IdTipoComprobante = ctx.IdTipoComprobanteTx,
            Numero = Interlocked.Increment(ref _numeroSecuencial),
            Fecha = MediodiaUtc,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdEmpleado = ctx.IdEmpleadoAdmin,
            IdCliente = ctx.IdCliente,
            Subtotal = total,
            DescuentoTotal = 0m,
            Total = total,
            Estado = EstadoComprobante.Emitido,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        db.ItemsComprobanteVenta.Add(new ItemComprobanteVenta
        {
            IdTenant = ctx.IdTenant, IdComprobanteVenta = comprobante.Id, Orden = 1, IdArticulo = idArticulo,
            Descripcion = descripcion, IdArea = ctx.IdArea, IdListaPrecio = ctx.IdListaPrecio,
            IdAlicuotaIva = ctx.IdAlicuotaIva, PorcentajeIva = 0m, Cantidad = cantidad,
            PrecioUnitario = total / cantidad, Descuento = 0m, Total = total,
            CostoUnitario = costoUnitario, CostoEsEstimado = costoEsEstimado,
            CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
        return comprobante.Id;
    }

    private async Task SembrarCompraAsync(Contexto ctx, decimal total)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var numeroExterno = $"export-slice2-{Interlocked.Increment(ref _numeroExternoSecuencial)}";

        await using var dbPlataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idTipoComprobanteCompra = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();

        db.ComprobantesCompra.Add(new ComprobanteCompra
        {
            IdTenant = ctx.IdTenant,
            IdProveedor = ctx.IdProveedor,
            IdTipoComprobante = idTipoComprobanteCompra,
            NumeroExterno = numeroExterno,
            FechaComprobante = Dia,
            FechaRecepcion = MediodiaUtc,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdEmpleado = 1,
            Subtotal = total,
            DescuentoTotal = 0m,
            Total = total,
            Estado = EstadoCompra.Confirmada,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private static async Task<int> AbrirTurnoAsync(HttpClient cliente, int idPuntoVenta)
    {
        var respuesta = await cliente.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(idPuntoVenta, 0m, "Apertura de soporte"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<TurnoResumen>(cuerpo, OpcionesJson)!.Id;
    }

    private async Task SembrarGastoAsync(Contexto ctx, int idTurno, decimal importe)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        db.Gastos.Add(new Gasto
        {
            IdTenant = ctx.IdTenant,
            Fecha = MediodiaUtc,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdTurnoCaja = idTurno,
            IdEmpleado = 1,
            Categoria = CategoriaGasto.Otros,
            Concepto = "Gasto export slice 2",
            IdMedioPago = ctx.IdMedioPagoEfectivo,
            Importe = importe,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private static async Task ConfigurarComisionAsync(Contexto ctx, string valorJson)
    {
        var respuesta = await ctx.Admin.PutAsJsonAsync(
            $"/api/parametros?idEmpresa={ctx.IdEmpresa}", new ParametroAlta("comision_porcentaje", valorJson, null));
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    private static string Rango(int idEmpresa) => $"idEmpresa={idEmpresa}&desde={Dia:yyyy-MM-dd}&hasta={Dia:yyyy-MM-dd}";

    private static async Task<XLWorkbook> DescargarLibroAsync(HttpClient cliente, string ruta)
    {
        var respuesta = await cliente.GetAsync(ruta);
        var cuerpo = respuesta.IsSuccessStatusCode ? null : await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        Assert.Equal(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);
        return new XLWorkbook(new MemoryStream(await respuesta.Content.ReadAsByteArrayAsync()));
    }

    /// <summary>Sibling de <see cref="DescargarLibroAsync"/> que NO valida la respuesta — las
    /// pruebas de rechazo (formato no soportado, tope superado) necesitan el <see
    /// cref="HttpResponseMessage"/> crudo para inspeccionar el 400 y el ProblemDetails, algo que
    /// <see cref="DescargarLibroAsync"/> no puede devolver porque exige 200 con el assert.</summary>
    private static Task<HttpResponseMessage> LlamarExportSinValidarAsync(HttpClient cliente, string ruta) =>
        cliente.GetAsync(ruta);

    // ---- task 2.4: equality tests (uno por export nuevo) ------------------------------------------

    [Fact]
    public async Task ElExportDePorPuntoVentaEsIgualAlEndpointJson()
    {
        var ctx = await PrepararAsync(nameof(ElExportDePorPuntoVentaEsIgualAlEndpointJson), fixture);
        await SembrarComprobanteAsync(ctx, 300m);

        var jsonRespuesta = await ctx.Admin.GetAsync($"/api/reportes/ventas/por-punto-venta?{Rango(ctx.IdEmpresa)}");
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var reporte = JsonSerializer.Deserialize<VentasPorPuntoVenta>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        var fila = Assert.Single(reporte.Filas);

        using var libro = await DescargarLibroAsync(
            ctx.Admin, $"/api/reportes/ventas/por-punto-venta/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");
        var hoja = libro.Worksheets.First();

        // Fila 6 = título de tabla (mutation-proof-tests regla 8): el header es lo que ata cada
        // celda de datos a su columna, sin este assert un swap de labels pasa inadvertido.
        Assert.Equal(
            ["Punto de venta", "Neto", "TX", "Ticket promedio"],
            Enumerable.Range(1, 4).Select(c => hoja.Cell(6, c).GetString()));

        Assert.Equal(fila.IdPuntoVenta, hoja.Cell(7, 1).GetValue<int>());
        Assert.Equal(fila.Neto, hoja.Cell(7, 2).GetValue<decimal>());
        Assert.Equal(fila.CantidadTx, hoja.Cell(7, 3).GetValue<int>());
        Assert.Equal(fila.TicketPromedio, (decimal?)hoja.Cell(7, 4).GetValue<decimal>());
    }

    [Fact]
    public async Task ElExportDePorVendedorEsIgualAlEndpointJson()
    {
        var ctx = await PrepararAsync(nameof(ElExportDePorVendedorEsIgualAlEndpointJson), fixture);
        await SembrarComprobanteAsync(ctx, 500m);

        var jsonRespuesta = await ctx.Admin.GetAsync($"/api/reportes/ventas/por-vendedor?{Rango(ctx.IdEmpresa)}");
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var reporte = JsonSerializer.Deserialize<VentasPorVendedor>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        var fila = Assert.Single(reporte.Filas);

        using var libro = await DescargarLibroAsync(
            ctx.Admin, $"/api/reportes/ventas/por-vendedor/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");
        var hoja = libro.Worksheets.First();

        // Fila 6 = título de tabla (mutation-proof-tests regla 8): el header es lo que ata cada
        // celda de datos a su columna, sin este assert un swap de labels pasa inadvertido.
        Assert.Equal(
            ["Vendedor", "Neto", "TX", "Ticket promedio"],
            Enumerable.Range(1, 4).Select(c => hoja.Cell(6, c).GetString()));

        Assert.Equal(fila.IdEmpleado, hoja.Cell(7, 1).GetValue<int>());
        Assert.Equal(fila.Neto, hoja.Cell(7, 2).GetValue<decimal>());
        Assert.Equal(fila.CantidadTx, hoja.Cell(7, 3).GetValue<int>());
        Assert.Equal(fila.TicketPromedio, (decimal?)hoja.Cell(7, 4).GetValue<decimal>());
    }

    [Fact]
    public async Task ElExportDePorMedioPagoEsIgualAlEndpointJson()
    {
        var ctx = await PrepararAsync(nameof(ElExportDePorMedioPagoEsIgualAlEndpointJson), fixture);
        var idComprobante = await SembrarComprobanteAsync(ctx, 400m);
        await SembrarPagoAsync(ctx, idComprobante, 400m);

        var jsonRespuesta = await ctx.Admin.GetAsync($"/api/reportes/ventas/por-medio-pago?{Rango(ctx.IdEmpresa)}");
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var reporte = JsonSerializer.Deserialize<VentasPorMedioPago>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        var fila = Assert.Single(reporte.Filas);

        using var libro = await DescargarLibroAsync(
            ctx.Admin, $"/api/reportes/ventas/por-medio-pago/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");
        var hoja = libro.Worksheets.First();

        // Fila 6 = título de tabla (mutation-proof-tests regla 8): el header es lo que ata cada
        // celda de datos a su columna, sin este assert un swap de labels pasa inadvertido.
        Assert.Equal(
            ["Medio de pago", "Neto", "Cantidad de pagos"],
            Enumerable.Range(1, 3).Select(c => hoja.Cell(6, c).GetString()));

        Assert.Equal(fila.IdMedioPago, hoja.Cell(7, 1).GetValue<int>());
        Assert.Equal(fila.Neto, hoja.Cell(7, 2).GetValue<decimal>());
        Assert.Equal(fila.CantidadPagos, hoja.Cell(7, 3).GetValue<int>());
    }

    [Fact]
    public async Task ElExportDeArticulosTopEsIgualAlEndpointJson()
    {
        var ctx = await PrepararAsync(nameof(ElExportDeArticulosTopEsIgualAlEndpointJson), fixture);
        var idArticulo = await SembrarArticuloAsync(ctx, "Articulo export top");
        await SembrarLineaAsync(ctx, idArticulo, "Articulo export top", 2m, 200m);

        var jsonRespuesta = await ctx.Admin.GetAsync($"/api/reportes/articulos/top?{Rango(ctx.IdEmpresa)}");
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var reporte = JsonSerializer.Deserialize<TopArticulos>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        var fila = Assert.Single(reporte.Articulos);

        using var libro = await DescargarLibroAsync(
            ctx.Admin, $"/api/reportes/articulos/top/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");
        var hoja = libro.Worksheets.First();

        // Fila 6 = título de tabla (mutation-proof-tests regla 8): el header es lo que ata cada
        // celda de datos a su columna, sin este assert un swap de labels pasa inadvertido.
        Assert.Equal(
            ["Artículo", "Descripción", "Cantidad", "Total"],
            Enumerable.Range(1, 4).Select(c => hoja.Cell(6, c).GetString()));

        Assert.Equal(fila.IdArticulo, hoja.Cell(7, 1).GetValue<int>());
        Assert.Equal(fila.Descripcion, hoja.Cell(7, 2).GetString());
        Assert.Equal(fila.Cantidad, hoja.Cell(7, 3).GetValue<decimal>());
        Assert.Equal(fila.Total, hoja.Cell(7, 4).GetValue<decimal>());
    }

    [Fact]
    public async Task ElExportDeComprasPorProveedorEsIgualAlEndpointJson()
    {
        var ctx = await PrepararAsync(nameof(ElExportDeComprasPorProveedorEsIgualAlEndpointJson), fixture);
        await SembrarCompraAsync(ctx, 1000m);

        var jsonRespuesta = await ctx.Admin.GetAsync($"/api/reportes/compras/por-proveedor?{Rango(ctx.IdEmpresa)}");
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var reporte = JsonSerializer.Deserialize<ComprasPorProveedor>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        var fila = Assert.Single(reporte.PorProveedor);

        using var libro = await DescargarLibroAsync(
            ctx.Admin, $"/api/reportes/compras/por-proveedor/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");
        var hoja = libro.Worksheets.First();

        // Fila 6 = título de tabla (mutation-proof-tests regla 8): el header es lo que ata cada
        // celda de datos a su columna, sin este assert un swap de labels pasa inadvertido.
        Assert.Equal(
            ["Proveedor", "Total", "Cantidad de compras"],
            Enumerable.Range(1, 3).Select(c => hoja.Cell(6, c).GetString()));

        Assert.Equal(fila.NombreProveedor, hoja.Cell(7, 1).GetString());
        Assert.Equal(fila.Total, hoja.Cell(7, 2).GetValue<decimal>());
        Assert.Equal(fila.CantidadCompras, hoja.Cell(7, 3).GetValue<int>());
        Assert.Equal(reporte.TotalGeneral, hoja.Cell(8, 2).GetValue<decimal>());
    }

    [Fact]
    public async Task ElExportDeGastosResumenEsIgualAlEndpointJson()
    {
        var ctx = await PrepararAsync(nameof(ElExportDeGastosResumenEsIgualAlEndpointJson), fixture);
        var idTurno = await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta);
        await SembrarGastoAsync(ctx, idTurno, 700m);

        var jsonRespuesta = await ctx.Admin.GetAsync($"/api/reportes/gastos/resumen?{Rango(ctx.IdEmpresa)}&granularidad=Dia");
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var reporte = JsonSerializer.Deserialize<ResumenDeGastos>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        Assert.Single(reporte.Serie);

        using var libro = await DescargarLibroAsync(
            ctx.Admin, $"/api/reportes/gastos/resumen/export?{Rango(ctx.IdEmpresa)}&granularidad=Dia&formato=xlsx");
        var hoja = libro.Worksheets.First();

        // Fila 6 = título de tabla (mutation-proof-tests regla 8): el header es lo que ata cada
        // celda de datos a su columna, sin este assert un swap de labels pasa inadvertido.
        Assert.Equal(
            ["Período", "Importe"],
            Enumerable.Range(1, 2).Select(c => hoja.Cell(6, c).GetString()));

        Assert.Equal(reporte.Serie[0].Etiqueta, hoja.Cell(7, 1).GetString());
        Assert.Equal(reporte.Serie[0].Importe, hoja.Cell(7, 2).GetValue<decimal>());
        Assert.Equal(reporte.ImporteTotal, hoja.Cell(8, 2).GetValue<decimal>());
    }

    [Fact]
    public async Task ElExportDeRentabilidadEsIgualAlEndpointJson()
    {
        var ctx = await PrepararAsync(nameof(ElExportDeRentabilidadEsIgualAlEndpointJson), fixture);
        var idArticulo = await SembrarArticuloAsync(ctx, "Articulo export rentabilidad");
        await SembrarLineaAsync(ctx, idArticulo, "Articulo export rentabilidad", 1m, 300m, costoUnitario: 100m);

        var jsonRespuesta = await ctx.Admin.GetAsync($"/api/reportes/rentabilidad?{Rango(ctx.IdEmpresa)}");
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var reporte = JsonSerializer.Deserialize<Rentabilidad>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        var fila = Assert.Single(reporte.PorArticulo);

        using var libro = await DescargarLibroAsync(
            ctx.Admin, $"/api/reportes/rentabilidad/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");
        var hoja = libro.Worksheets.First();

        // Fila 6 = título de tabla (mutation-proof-tests regla 8): el header es lo que ata cada
        // celda de datos a su columna, sin este assert un swap de labels pasa inadvertido.
        Assert.Equal(
            ["Artículo", "Descripción", "Venta considerada", "Costo considerado", "Margen", "Margen %"],
            Enumerable.Range(1, 6).Select(c => hoja.Cell(6, c).GetString()));

        Assert.Equal(fila.IdArticulo, hoja.Cell(7, 1).GetValue<int>());
        Assert.Equal(fila.Descripcion, hoja.Cell(7, 2).GetString());
        Assert.Equal(fila.VentaConsiderada, hoja.Cell(7, 3).GetValue<decimal>());
        Assert.Equal(fila.CostoConsiderado, hoja.Cell(7, 4).GetValue<decimal>());
        Assert.Equal(fila.Margen, hoja.Cell(7, 5).GetValue<decimal>());
        Assert.Equal(reporte.Margen, hoja.Cell(8, 5).GetValue<decimal>());
    }

    [Fact]
    public async Task ElExportDeComisionesEsIgualAlEndpointJson()
    {
        var ctx = await PrepararAsync(nameof(ElExportDeComisionesEsIgualAlEndpointJson), fixture);
        await ConfigurarComisionAsync(ctx, "10");
        await SembrarComprobanteAsync(ctx, 1000m);

        var jsonRespuesta = await ctx.Admin.GetAsync($"/api/reportes/comisiones?{Rango(ctx.IdEmpresa)}");
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var reporte = JsonSerializer.Deserialize<Comisiones>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        var fila = Assert.Single(reporte.Filas);
        Assert.Equal(100m, fila.Comision);

        using var libro = await DescargarLibroAsync(
            ctx.Admin, $"/api/reportes/comisiones/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");
        var hoja = libro.Worksheets.First();

        // Fila 6 = título de tabla (mutation-proof-tests regla 8): el header es lo que ata cada
        // celda de datos a su columna, sin este assert un swap de labels pasa inadvertido.
        Assert.Equal(
            ["Vendedor", "Neto vendido", "Comisión"],
            Enumerable.Range(1, 3).Select(c => hoja.Cell(6, c).GetString()));

        Assert.Equal(fila.IdEmpleado, hoja.Cell(7, 1).GetValue<int>());
        Assert.Equal(fila.NetoVendido, hoja.Cell(7, 2).GetValue<decimal>());
        Assert.Equal(fila.Comision, hoja.Cell(7, 3).GetValue<decimal>());
    }

    // ---- task 2.5: 403 para el rol un escalón debajo del gate --------------------------------------

    public static readonly TheoryData<string> RutasSoloLecturaDeReportes = new()
    {
        "ventas/por-punto-venta/export", "ventas/por-vendedor/export", "ventas/por-medio-pago/export",
        "articulos/top/export", "compras/por-proveedor/export", "gastos/resumen/export"
    };

    [Theory]
    [MemberData(nameof(RutasSoloLecturaDeReportes))]
    public async Task UnVendedorEsRechazadoEnLosSeisExportsDeLecturaDeReportes(string ruta)
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoEnLosSeisExportsDeLecturaDeReportes) + ruta.Replace("/", "-"), fixture);

        var respuesta = await ctx.Vendedor.GetAsync(
            $"/api/reportes/{ruta}?{Rango(ctx.IdEmpresa)}&granularidad=Dia&formato=xlsx");

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    // ---- task 2.5/2.6: el objetivo de mutación de política apilada (mutation-proof-tests) ----------

    /// <summary>Prueba distintiva de la política apilada (spec: A Supervisor Is Rejected On The
    /// Rentabilidad Export): a diferencia de los seis exports de <c>LecturaDeReportes</c> sola
    /// (que un Supervisor sí puede leer), acá <c>LecturaDeReportes</c> no alcanza — hace falta
    /// <c>LecturaDeRentabilidad</c> apilada encima (design decisión 7 de stage-10, heredada acá).
    /// mutation-proof-tests: mutación aplicada (comentar
    /// <c>.RequireAuthorization(Politicas.LecturaDeRentabilidad)</c> en
    /// <c>/rentabilidad/export</c>) — este test pasó de <c>403</c> esperado a <c>200</c> obtenido
    /// (la política del grupo, <c>LecturaDeReportes</c>, admite Supervisor por sí sola); revertida,
    /// vuelve a pasar. Evidencia registrada en el cuerpo del commit.</summary>
    [Fact]
    public async Task UnSupervisorEsRechazadoEnElExportDeRentabilidad()
    {
        var ctx = await PrepararAsync(nameof(UnSupervisorEsRechazadoEnElExportDeRentabilidad), fixture);

        var respuesta = await ctx.Supervisor.GetAsync($"/api/reportes/rentabilidad/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnSupervisorEsRechazadoEnElExportDeComisiones()
    {
        var ctx = await PrepararAsync(nameof(UnSupervisorEsRechazadoEnElExportDeComisiones), fixture);

        var respuesta = await ctx.Supervisor.GetAsync($"/api/reportes/comisiones/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    // ---- task 2.7: el bloque de cobertura en el export de rentabilidad -----------------------------

    /// <summary>7 líneas con costo real, 2 con costo estimado (incluido vía <c>incluirEstimados</c>
    /// para que también aparezcan en <c>PorArticulo</c>, aunque la cobertura las cuenta siempre —
    /// spec: Coverage Reflects A Mixed Period), 1 con costo desconocido — el encabezado del
    /// workbook (fila 4) tiene que repetir los mismos tres conteos y sus subtotales de venta que
    /// <see cref="CoberturaDeCosto"/> trae en la respuesta JSON (spec: An Admin's Rentabilidad
    /// Export Carries The Coverage Block).</summary>
    [Fact]
    public async Task ElExportDeRentabilidadCargaElBloqueDeCobertura()
    {
        var ctx = await PrepararAsync(nameof(ElExportDeRentabilidadCargaElBloqueDeCobertura), fixture);
        var idArticulo = await SembrarArticuloAsync(ctx, "Articulo cobertura");

        for (var i = 0; i < 7; i++)
        {
            await SembrarLineaAsync(ctx, idArticulo, $"real-{i}", 1m, 100m, costoUnitario: 40m);
        }

        for (var i = 0; i < 2; i++)
        {
            await SembrarLineaAsync(ctx, idArticulo, $"estimado-{i}", 1m, 50m, costoUnitario: 20m, costoEsEstimado: true);
        }

        await SembrarLineaAsync(ctx, idArticulo, "desconocido", 1m, 30m, costoUnitario: null);

        var jsonRespuesta = await ctx.Admin.GetAsync(
            $"/api/reportes/rentabilidad?{Rango(ctx.IdEmpresa)}&incluirEstimados=true");
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var reporte = JsonSerializer.Deserialize<Rentabilidad>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;

        Assert.Equal(7, reporte.Cobertura.LineasConCostoReal);
        Assert.Equal(2, reporte.Cobertura.LineasConCostoEstimado);
        Assert.Equal(1, reporte.Cobertura.LineasSinCosto);

        using var libro = await DescargarLibroAsync(
            ctx.Admin, $"/api/reportes/rentabilidad/export?{Rango(ctx.IdEmpresa)}&incluirEstimados=true&formato=xlsx");
        var hoja = libro.Worksheets.First();
        var textoEncabezado = hoja.Cell(4, 1).GetString();

        Assert.Contains($"{reporte.Cobertura.LineasConCostoReal} líneas con costo real", textoEncabezado);
        Assert.Contains($"{reporte.Cobertura.LineasConCostoEstimado} con costo estimado", textoEncabezado);
        Assert.Contains($"{reporte.Cobertura.LineasSinCosto} con costo desconocido", textoEncabezado);
        Assert.Contains(reporte.Cobertura.VentaConCostoReal.ToString("0.00"), textoEncabezado);
        Assert.Contains(reporte.Cobertura.VentaConCostoEstimado.ToString("0.00"), textoEncabezado);
        Assert.Contains(reporte.Cobertura.VentaSinCosto.ToString("0.00"), textoEncabezado);
    }

    // ---- task 2.8: la etiqueta PROVISIONAL en el export de comisiones ------------------------------

    [Fact]
    public async Task ElExportDeComisionesLlevaLaEtiquetaProvisional()
    {
        var ctx = await PrepararAsync(nameof(ElExportDeComisionesLlevaLaEtiquetaProvisional), fixture);
        await SembrarComprobanteAsync(ctx, 100m);

        var jsonRespuesta = await ctx.Admin.GetAsync($"/api/reportes/comisiones?{Rango(ctx.IdEmpresa)}");
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var reporte = JsonSerializer.Deserialize<Comisiones>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        Assert.True(reporte.Provisional);

        using var libro = await DescargarLibroAsync(
            ctx.Admin, $"/api/reportes/comisiones/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");
        var hoja = libro.Worksheets.First();

        Assert.Contains("PROVISIONAL", hoja.Cell(4, 1).GetString());
    }

    // ---- sweep GAP 2: FormatoDeExportacion.Parsear por cada una de las ocho rutas -----------------

    public static readonly TheoryData<string> RutasDeLosOchoExports = new()
    {
        "ventas/por-punto-venta/export", "ventas/por-vendedor/export", "ventas/por-medio-pago/export",
        "articulos/top/export", "compras/por-proveedor/export", "gastos/resumen/export",
        "rentabilidad/export", "comisiones/export"
    };

    /// <summary>Cierra el gap de <see cref="FormatoDeExportacion.Parsear"/> para los ocho export
    /// siblings de esta clase — ninguno lo tenía cubierto (esta clase no tenía ni un test de
    /// formato): borrar el <c>Parsear(formato)</c> de CUALQUIERA de las ocho rutas de
    /// <c>ReportesEndpoints.MapearReportes</c> deja pasar un <c>formato=pdf</c> con 200 XLSX en vez
    /// de 400. Un solo <c>[Theory]</c> con un caso por ruta (en vez de ocho <c>[Fact]</c>s) porque
    /// las ocho comparten los mismos parámetros obligatorios (idEmpresa,
    /// desde, hasta, formato); <c>granularidad=Dia</c> viaja siempre en la query aunque solo
    /// <c>gastos/resumen/export</c> la exija — minimal API ignora los query params no declarados
    /// por una ruta, así que no rompe a las otras siete. Sin datos sembrados: <c>Parsear</c> corta
    /// ANTES de tocar el servicio de reportes, mismo criterio que el resto del barrido.</summary>
    [Theory]
    [MemberData(nameof(RutasDeLosOchoExports))]
    public async Task UnFormatoNoSoportadoRechazaConProblemDetailsEnCadaUnoDeLosOchoExports(string ruta)
    {
        var ctx = await PrepararAsync(
            nameof(UnFormatoNoSoportadoRechazaConProblemDetailsEnCadaUnoDeLosOchoExports) + ruta.Replace("/", "-"),
            fixture);

        var respuesta = await LlamarExportSinValidarAsync(
            ctx.Admin, $"/api/reportes/{ruta}?{Rango(ctx.IdEmpresa)}&granularidad=Dia&formato=pdf");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("formato_no_soportado", problema.GetProperty("codigo").GetString());
    }

    // ---- sweep GAP 3: el único GuardaDeTope.Exigir AGREGADO de cada ruta, pineado de ambos lados --
    //
    // Las ocho rutas de este archivo son AGREGADAS (un solo Exigir sobre tabla.Filas.Count, sin
    // .Take ni COUNT(*) propio) — GAP 1 del barrido no aplica. Cada par de abajo reusa EXACTAMENTE
    // la siembra de su test de igualdad (task 2.4), sin inventar siembra nueva: el tope se baja
    // hasta calzar justo con la cantidad de filas que esa siembra ya produce.
    //   - por-punto-venta / por-vendedor / por-medio-pago / articulos-top / comisiones: sin fila de
    //     totales (ExportacionDeReportes.De no le agrega una) — 1 comprobante/línea sembrada ⇒
    //     tabla.Filas.Count == 1 ⇒ tope éxito = 1, tope rechazo = 0.
    //   - compras-por-proveedor / gastos-resumen / rentabilidad: SÍ agregan una fila de totales
    //     (ExportacionDeReportes.De) — 1 fila de negocio sembrada ⇒ tabla.Filas.Count == 2 (dato +
    //     totales) ⇒ tope éxito = 2, tope rechazo = 1.

    /// <summary>Discrimina el ÚNICO <c>GuardaDeTope.Exigir</c> de <c>ventas/por-punto-venta/export</c>
    /// del lado del ÉXITO: <c>ElExportDePorPuntoVentaEsIgualAlEndpointJson</c> siembra un
    /// comprobante y <c>VentasPorPuntoVenta.Filas</c> trae exactamente 1 fila, sin fila de totales
    /// (<c>ExportacionDeReportes.De(VentasPorPuntoVenta, …)</c> no le agrega una) ⇒
    /// <c>tabla.Filas.Count == 1</c>. Con <c>TopeDeFilas = 1</c>, mutar el segundo argumento a
    /// <c>tope - 1</c> sobrevive sin este test — la prueba de rechazo de abajo solo cubre el lado de
    /// ARRIBA del tope.</summary>
    [Fact]
    public async Task UnaExportacionDePorPuntoVentaExactamenteEnElTopeSeAceptaCompleta()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 1)));

        var ctx = await PrepararAsync(nameof(UnaExportacionDePorPuntoVentaExactamenteEnElTopeSeAceptaCompleta), factoryBajo);
        await SembrarComprobanteAsync(ctx, 300m);

        using var libro = await DescargarLibroAsync(
            ctx.Admin, $"/api/reportes/ventas/por-punto-venta/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");
        var hoja = libro.Worksheets.First();

        Assert.False(hoja.Row(7).IsEmpty());
        Assert.True(hoja.Row(8).IsEmpty());
    }

    /// <summary>Discrimina el mismo <c>Exigir</c> del lado del RECHAZO: con <c>TopeDeFilas = 0</c> la
    /// única fila sembrada (1 &gt; 0) rechaza con la cantidad REAL en el título — sin este test,
    /// borrar el <c>Exigir</c> completo de la ruta sobrevivía (esta clase no tenía ningún test de
    /// tope hasta esta pasada del barrido).</summary>
    [Fact]
    public async Task UnaExportacionDePorPuntoVentaQueSuperaElTopeSeRechazaConLaCantidadReal()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 0)));

        var ctx = await PrepararAsync(nameof(UnaExportacionDePorPuntoVentaQueSuperaElTopeSeRechazaConLaCantidadReal), factoryBajo);
        await SembrarComprobanteAsync(ctx, 300m);

        var respuesta = await LlamarExportSinValidarAsync(
            ctx.Admin, $"/api/reportes/ventas/por-punto-venta/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exportacion_demasiado_grande", problema.GetProperty("codigo").GetString());
        Assert.Contains("tiene 1 filas", problema.GetProperty("title").GetString());
    }

    /// <summary>Mismo par que <c>por-punto-venta</c>, ruta <c>ventas/por-vendedor/export</c>: 1
    /// comprobante sembrado (mismo criterio de <c>ElExportDePorVendedorEsIgualAlEndpointJson</c>) ⇒
    /// <c>VentasPorVendedor.Filas.Count == 1</c>, sin totales ⇒ tope éxito 1 / rechazo 0.</summary>
    [Fact]
    public async Task UnaExportacionDePorVendedorExactamenteEnElTopeSeAceptaCompleta()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 1)));

        var ctx = await PrepararAsync(nameof(UnaExportacionDePorVendedorExactamenteEnElTopeSeAceptaCompleta), factoryBajo);
        await SembrarComprobanteAsync(ctx, 500m);

        using var libro = await DescargarLibroAsync(
            ctx.Admin, $"/api/reportes/ventas/por-vendedor/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");
        var hoja = libro.Worksheets.First();

        Assert.False(hoja.Row(7).IsEmpty());
        Assert.True(hoja.Row(8).IsEmpty());
    }

    /// <summary>Contraparte de rechazo: <c>TopeDeFilas = 0</c> contra la misma fila única — sin
    /// ningún test de tope previo en esta ruta, borrar el <c>Exigir</c> de
    /// <c>ventas/por-vendedor/export</c> sobrevivía.</summary>
    [Fact]
    public async Task UnaExportacionDePorVendedorQueSuperaElTopeSeRechazaConLaCantidadReal()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 0)));

        var ctx = await PrepararAsync(nameof(UnaExportacionDePorVendedorQueSuperaElTopeSeRechazaConLaCantidadReal), factoryBajo);
        await SembrarComprobanteAsync(ctx, 500m);

        var respuesta = await LlamarExportSinValidarAsync(
            ctx.Admin, $"/api/reportes/ventas/por-vendedor/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exportacion_demasiado_grande", problema.GetProperty("codigo").GetString());
        Assert.Contains("tiene 1 filas", problema.GetProperty("title").GetString());
    }

    /// <summary>Mismo par, ruta <c>ventas/por-medio-pago/export</c>: 1 comprobante + 1 pago (mismo
    /// criterio de <c>ElExportDePorMedioPagoEsIgualAlEndpointJson</c>) ⇒
    /// <c>VentasPorMedioPago.Filas.Count == 1</c>, sin totales ⇒ tope éxito 1 / rechazo 0.</summary>
    [Fact]
    public async Task UnaExportacionDePorMedioPagoExactamenteEnElTopeSeAceptaCompleta()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 1)));

        var ctx = await PrepararAsync(nameof(UnaExportacionDePorMedioPagoExactamenteEnElTopeSeAceptaCompleta), factoryBajo);
        var idComprobante = await SembrarComprobanteAsync(ctx, 400m);
        await SembrarPagoAsync(ctx, idComprobante, 400m);

        using var libro = await DescargarLibroAsync(
            ctx.Admin, $"/api/reportes/ventas/por-medio-pago/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");
        var hoja = libro.Worksheets.First();

        Assert.False(hoja.Row(7).IsEmpty());
        Assert.True(hoja.Row(8).IsEmpty());
    }

    /// <summary>Contraparte de rechazo de <c>ventas/por-medio-pago/export</c>: <c>TopeDeFilas = 0</c>
    /// contra la misma fila única — sin este par, la ruta no tenía ningún test de tope.</summary>
    [Fact]
    public async Task UnaExportacionDePorMedioPagoQueSuperaElTopeSeRechazaConLaCantidadReal()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 0)));

        var ctx = await PrepararAsync(nameof(UnaExportacionDePorMedioPagoQueSuperaElTopeSeRechazaConLaCantidadReal), factoryBajo);
        var idComprobante = await SembrarComprobanteAsync(ctx, 400m);
        await SembrarPagoAsync(ctx, idComprobante, 400m);

        var respuesta = await LlamarExportSinValidarAsync(
            ctx.Admin, $"/api/reportes/ventas/por-medio-pago/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exportacion_demasiado_grande", problema.GetProperty("codigo").GetString());
        Assert.Contains("tiene 1 filas", problema.GetProperty("title").GetString());
    }

    /// <summary>Mismo par, ruta <c>articulos/top/export</c>: 1 artículo + 1 línea (mismo criterio de
    /// <c>ElExportDeArticulosTopEsIgualAlEndpointJson</c>) ⇒ <c>TopArticulos.Articulos.Count == 1</c>,
    /// sin totales ⇒ tope éxito 1 / rechazo 0.</summary>
    [Fact]
    public async Task UnaExportacionDeArticulosTopExactamenteEnElTopeSeAceptaCompleta()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 1)));

        var ctx = await PrepararAsync(nameof(UnaExportacionDeArticulosTopExactamenteEnElTopeSeAceptaCompleta), factoryBajo);
        var idArticulo = await SembrarArticuloAsync(ctx, "Articulo tope top");
        await SembrarLineaAsync(ctx, idArticulo, "Articulo tope top", 2m, 200m);

        using var libro = await DescargarLibroAsync(
            ctx.Admin, $"/api/reportes/articulos/top/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");
        var hoja = libro.Worksheets.First();

        Assert.False(hoja.Row(7).IsEmpty());
        Assert.True(hoja.Row(8).IsEmpty());
    }

    /// <summary>Contraparte de rechazo de <c>articulos/top/export</c>: <c>TopeDeFilas = 0</c> contra
    /// la misma fila única — sin este par, la ruta no tenía ningún test de tope.</summary>
    [Fact]
    public async Task UnaExportacionDeArticulosTopQueSuperaElTopeSeRechazaConLaCantidadReal()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 0)));

        var ctx = await PrepararAsync(nameof(UnaExportacionDeArticulosTopQueSuperaElTopeSeRechazaConLaCantidadReal), factoryBajo);
        var idArticulo = await SembrarArticuloAsync(ctx, "Articulo tope top rechazo");
        await SembrarLineaAsync(ctx, idArticulo, "Articulo tope top rechazo", 2m, 200m);

        var respuesta = await LlamarExportSinValidarAsync(
            ctx.Admin, $"/api/reportes/articulos/top/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exportacion_demasiado_grande", problema.GetProperty("codigo").GetString());
        Assert.Contains("tiene 1 filas", problema.GetProperty("title").GetString());
    }

    /// <summary>Ruta <c>compras/por-proveedor/export</c>: 1 compra (mismo criterio de
    /// <c>ElExportDeComprasPorProveedorEsIgualAlEndpointJson</c>) ⇒ <c>PorProveedor.Count == 1</c>
    /// MÁS la fila de totales que <c>ExportacionDeReportes.De(ComprasPorProveedor, …)</c> siempre
    /// agrega (línea 166-171: <c>filas.Add([… "Total" …])</c>) ⇒ <c>tabla.Filas.Count == 2</c> ⇒
    /// tope éxito 2 / rechazo 1. Fila 7 = dato, fila 8 = totales, fila 9 tiene que quedar vacía.</summary>
    [Fact]
    public async Task UnaExportacionDeComprasPorProveedorExactamenteEnElTopeSeAceptaCompleta()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 2)));

        var ctx = await PrepararAsync(nameof(UnaExportacionDeComprasPorProveedorExactamenteEnElTopeSeAceptaCompleta), factoryBajo);
        await SembrarCompraAsync(ctx, 1000m);

        using var libro = await DescargarLibroAsync(
            ctx.Admin, $"/api/reportes/compras/por-proveedor/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");
        var hoja = libro.Worksheets.First();

        Assert.False(hoja.Row(7).IsEmpty());
        Assert.False(hoja.Row(8).IsEmpty());
        Assert.True(hoja.Row(9).IsEmpty());
    }

    /// <summary>Contraparte de rechazo de <c>compras/por-proveedor/export</c>: con
    /// <c>TopeDeFilas = 1</c> las 2 filas reales (dato + totales) superan el tope — sin este par, la
    /// ruta no tenía ningún test de tope.</summary>
    [Fact]
    public async Task UnaExportacionDeComprasPorProveedorQueSuperaElTopeSeRechazaConLaCantidadReal()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 1)));

        var ctx = await PrepararAsync(nameof(UnaExportacionDeComprasPorProveedorQueSuperaElTopeSeRechazaConLaCantidadReal), factoryBajo);
        await SembrarCompraAsync(ctx, 1000m);

        var respuesta = await LlamarExportSinValidarAsync(
            ctx.Admin, $"/api/reportes/compras/por-proveedor/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exportacion_demasiado_grande", problema.GetProperty("codigo").GetString());
        Assert.Contains("tiene 2 filas", problema.GetProperty("title").GetString());
    }

    /// <summary>Ruta <c>gastos/resumen/export</c>: 1 gasto (mismo criterio de
    /// <c>ElExportDeGastosResumenEsIgualAlEndpointJson</c>) ⇒ <c>Serie.Count == 1</c> MÁS la fila de
    /// totales que <c>ExportacionDeReportes.De(ResumenDeGastos, …)</c> siempre agrega (línea 197:
    /// <c>filas.Add([… ImporteTotal])</c>) ⇒ <c>tabla.Filas.Count == 2</c> ⇒ tope éxito 2 / rechazo
    /// 1.</summary>
    [Fact]
    public async Task UnaExportacionDeGastosResumenExactamenteEnElTopeSeAceptaCompleta()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 2)));

        var ctx = await PrepararAsync(nameof(UnaExportacionDeGastosResumenExactamenteEnElTopeSeAceptaCompleta), factoryBajo);
        var idTurno = await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta);
        await SembrarGastoAsync(ctx, idTurno, 700m);

        using var libro = await DescargarLibroAsync(
            ctx.Admin, $"/api/reportes/gastos/resumen/export?{Rango(ctx.IdEmpresa)}&granularidad=Dia&formato=xlsx");
        var hoja = libro.Worksheets.First();

        Assert.False(hoja.Row(7).IsEmpty());
        Assert.False(hoja.Row(8).IsEmpty());
        Assert.True(hoja.Row(9).IsEmpty());
    }

    /// <summary>Contraparte de rechazo de <c>gastos/resumen/export</c>: con <c>TopeDeFilas = 1</c>
    /// las 2 filas reales (bucket + totales) superan el tope — sin este par, la ruta no tenía ningún
    /// test de tope.</summary>
    [Fact]
    public async Task UnaExportacionDeGastosResumenQueSuperaElTopeSeRechazaConLaCantidadReal()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 1)));

        var ctx = await PrepararAsync(nameof(UnaExportacionDeGastosResumenQueSuperaElTopeSeRechazaConLaCantidadReal), factoryBajo);
        var idTurno = await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta);
        await SembrarGastoAsync(ctx, idTurno, 700m);

        var respuesta = await LlamarExportSinValidarAsync(
            ctx.Admin, $"/api/reportes/gastos/resumen/export?{Rango(ctx.IdEmpresa)}&granularidad=Dia&formato=xlsx");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exportacion_demasiado_grande", problema.GetProperty("codigo").GetString());
        Assert.Contains("tiene 2 filas", problema.GetProperty("title").GetString());
    }

    /// <summary>Ruta <c>rentabilidad/export</c>: 1 línea con costo real (mismo criterio de
    /// <c>ElExportDeRentabilidadEsIgualAlEndpointJson</c>) ⇒ <c>PorArticulo.Count == 1</c> MÁS la
    /// fila de totales que <c>ExportacionDeReportes.De(Rentabilidad, …)</c> siempre agrega (línea
    /// 233-241: <c>filas.Add([… "Total" …])</c>) ⇒ <c>tabla.Filas.Count == 2</c> ⇒ tope éxito 2 /
    /// rechazo 1.</summary>
    [Fact]
    public async Task UnaExportacionDeRentabilidadExactamenteEnElTopeSeAceptaCompleta()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 2)));

        var ctx = await PrepararAsync(nameof(UnaExportacionDeRentabilidadExactamenteEnElTopeSeAceptaCompleta), factoryBajo);
        var idArticulo = await SembrarArticuloAsync(ctx, "Articulo tope rentabilidad");
        await SembrarLineaAsync(ctx, idArticulo, "Articulo tope rentabilidad", 1m, 300m, costoUnitario: 100m);

        using var libro = await DescargarLibroAsync(
            ctx.Admin, $"/api/reportes/rentabilidad/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");
        var hoja = libro.Worksheets.First();

        Assert.False(hoja.Row(7).IsEmpty());
        Assert.False(hoja.Row(8).IsEmpty());
        Assert.True(hoja.Row(9).IsEmpty());
    }

    /// <summary>Contraparte de rechazo de <c>rentabilidad/export</c>: con <c>TopeDeFilas = 1</c> las
    /// 2 filas reales (artículo + totales) superan el tope — sin este par, la ruta no tenía ningún
    /// test de tope.</summary>
    [Fact]
    public async Task UnaExportacionDeRentabilidadQueSuperaElTopeSeRechazaConLaCantidadReal()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 1)));

        var ctx = await PrepararAsync(nameof(UnaExportacionDeRentabilidadQueSuperaElTopeSeRechazaConLaCantidadReal), factoryBajo);
        var idArticulo = await SembrarArticuloAsync(ctx, "Articulo tope rentabilidad rechazo");
        await SembrarLineaAsync(ctx, idArticulo, "Articulo tope rentabilidad rechazo", 1m, 300m, costoUnitario: 100m);

        var respuesta = await LlamarExportSinValidarAsync(
            ctx.Admin, $"/api/reportes/rentabilidad/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exportacion_demasiado_grande", problema.GetProperty("codigo").GetString());
        Assert.Contains("tiene 2 filas", problema.GetProperty("title").GetString());
    }

    /// <summary>Ruta <c>comisiones/export</c>: 1 comprobante con comisión configurada (mismo
    /// criterio de <c>ElExportDeComisionesEsIgualAlEndpointJson</c>) ⇒ <c>Comisiones.Filas.Count ==
    /// 1</c>, sin fila de totales (<c>ExportacionDeReportes.De(Comisiones, …)</c> no le agrega una —
    /// la etiqueta PROVISIONAL viaja en el encabezado, no en una fila) ⇒ tope éxito 1 / rechazo
    /// 0.</summary>
    [Fact]
    public async Task UnaExportacionDeComisionesExactamenteEnElTopeSeAceptaCompleta()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 1)));

        var ctx = await PrepararAsync(nameof(UnaExportacionDeComisionesExactamenteEnElTopeSeAceptaCompleta), factoryBajo);
        await ConfigurarComisionAsync(ctx, "10");
        await SembrarComprobanteAsync(ctx, 1000m);

        using var libro = await DescargarLibroAsync(
            ctx.Admin, $"/api/reportes/comisiones/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");
        var hoja = libro.Worksheets.First();

        Assert.False(hoja.Row(7).IsEmpty());
        Assert.True(hoja.Row(8).IsEmpty());
    }

    /// <summary>Contraparte de rechazo de <c>comisiones/export</c>: <c>TopeDeFilas = 0</c> contra la
    /// misma fila única — sin este par, la ruta no tenía ningún test de tope.</summary>
    [Fact]
    public async Task UnaExportacionDeComisionesQueSuperaElTopeSeRechazaConLaCantidadReal()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 0)));

        var ctx = await PrepararAsync(nameof(UnaExportacionDeComisionesQueSuperaElTopeSeRechazaConLaCantidadReal), factoryBajo);
        await ConfigurarComisionAsync(ctx, "10");
        await SembrarComprobanteAsync(ctx, 1000m);

        var respuesta = await LlamarExportSinValidarAsync(
            ctx.Admin, $"/api/reportes/comisiones/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exportacion_demasiado_grande", problema.GetProperty("codigo").GetString());
        Assert.Contains("tiene 1 filas", problema.GetProperty("title").GetString());
    }
}
