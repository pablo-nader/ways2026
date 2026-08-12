using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
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

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        var supervisor = await CrearYLoguearAsync(admin, nombre, "supervisor", RolConocido.Supervisor);
        var vendedor = await CrearYLoguearAsync(admin, nombre, "vendedor", RolConocido.Vendedor);

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

    private async Task<HttpClient> CrearYLoguearAsync(HttpClient admin, string nombre, string sufijo, RolConocido rol)
    {
        var corto = Guid.NewGuid().ToString("N")[..8];
        var mail = $"{nombre.ToLowerInvariant()}-{sufijo}@ways.test";
        var alta = await admin.PostAsJsonAsync("/api/usuarios", new CrearUsuario($"{sufijo}-{corto}", mail, (int)rol, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = fixture.CreateClient();
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

    // ---- task 2.4: equality tests (uno por export nuevo) ------------------------------------------

    [Fact]
    public async Task ElExportDePorPuntoVentaEsIgualAlEndpointJson()
    {
        var ctx = await PrepararAsync(nameof(ElExportDePorPuntoVentaEsIgualAlEndpointJson));
        await SembrarComprobanteAsync(ctx, 300m);

        var jsonRespuesta = await ctx.Admin.GetAsync($"/api/reportes/ventas/por-punto-venta?{Rango(ctx.IdEmpresa)}");
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var reporte = JsonSerializer.Deserialize<VentasPorPuntoVenta>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        var fila = Assert.Single(reporte.Filas);

        using var libro = await DescargarLibroAsync(
            ctx.Admin, $"/api/reportes/ventas/por-punto-venta/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");
        var hoja = libro.Worksheets.First();

        Assert.Equal(fila.IdPuntoVenta, hoja.Cell(7, 1).GetValue<int>());
        Assert.Equal(fila.Neto, hoja.Cell(7, 2).GetValue<decimal>());
        Assert.Equal(fila.CantidadTx, hoja.Cell(7, 3).GetValue<int>());
        Assert.Equal(fila.TicketPromedio, (decimal?)hoja.Cell(7, 4).GetValue<decimal>());
    }

    [Fact]
    public async Task ElExportDePorVendedorEsIgualAlEndpointJson()
    {
        var ctx = await PrepararAsync(nameof(ElExportDePorVendedorEsIgualAlEndpointJson));
        await SembrarComprobanteAsync(ctx, 500m);

        var jsonRespuesta = await ctx.Admin.GetAsync($"/api/reportes/ventas/por-vendedor?{Rango(ctx.IdEmpresa)}");
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var reporte = JsonSerializer.Deserialize<VentasPorVendedor>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        var fila = Assert.Single(reporte.Filas);

        using var libro = await DescargarLibroAsync(
            ctx.Admin, $"/api/reportes/ventas/por-vendedor/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");
        var hoja = libro.Worksheets.First();

        Assert.Equal(fila.IdEmpleado, hoja.Cell(7, 1).GetValue<int>());
        Assert.Equal(fila.Neto, hoja.Cell(7, 2).GetValue<decimal>());
        Assert.Equal(fila.CantidadTx, hoja.Cell(7, 3).GetValue<int>());
        Assert.Equal(fila.TicketPromedio, (decimal?)hoja.Cell(7, 4).GetValue<decimal>());
    }

    [Fact]
    public async Task ElExportDePorMedioPagoEsIgualAlEndpointJson()
    {
        var ctx = await PrepararAsync(nameof(ElExportDePorMedioPagoEsIgualAlEndpointJson));
        var idComprobante = await SembrarComprobanteAsync(ctx, 400m);
        await SembrarPagoAsync(ctx, idComprobante, 400m);

        var jsonRespuesta = await ctx.Admin.GetAsync($"/api/reportes/ventas/por-medio-pago?{Rango(ctx.IdEmpresa)}");
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var reporte = JsonSerializer.Deserialize<VentasPorMedioPago>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        var fila = Assert.Single(reporte.Filas);

        using var libro = await DescargarLibroAsync(
            ctx.Admin, $"/api/reportes/ventas/por-medio-pago/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");
        var hoja = libro.Worksheets.First();

        Assert.Equal(fila.IdMedioPago, hoja.Cell(7, 1).GetValue<int>());
        Assert.Equal(fila.Neto, hoja.Cell(7, 2).GetValue<decimal>());
        Assert.Equal(fila.CantidadPagos, hoja.Cell(7, 3).GetValue<int>());
    }

    [Fact]
    public async Task ElExportDeArticulosTopEsIgualAlEndpointJson()
    {
        var ctx = await PrepararAsync(nameof(ElExportDeArticulosTopEsIgualAlEndpointJson));
        var idArticulo = await SembrarArticuloAsync(ctx, "Articulo export top");
        await SembrarLineaAsync(ctx, idArticulo, "Articulo export top", 2m, 200m);

        var jsonRespuesta = await ctx.Admin.GetAsync($"/api/reportes/articulos/top?{Rango(ctx.IdEmpresa)}");
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var reporte = JsonSerializer.Deserialize<TopArticulos>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        var fila = Assert.Single(reporte.Articulos);

        using var libro = await DescargarLibroAsync(
            ctx.Admin, $"/api/reportes/articulos/top/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");
        var hoja = libro.Worksheets.First();

        Assert.Equal(fila.IdArticulo, hoja.Cell(7, 1).GetValue<int>());
        Assert.Equal(fila.Descripcion, hoja.Cell(7, 2).GetString());
        Assert.Equal(fila.Cantidad, hoja.Cell(7, 3).GetValue<decimal>());
        Assert.Equal(fila.Total, hoja.Cell(7, 4).GetValue<decimal>());
    }

    [Fact]
    public async Task ElExportDeComprasPorProveedorEsIgualAlEndpointJson()
    {
        var ctx = await PrepararAsync(nameof(ElExportDeComprasPorProveedorEsIgualAlEndpointJson));
        await SembrarCompraAsync(ctx, 1000m);

        var jsonRespuesta = await ctx.Admin.GetAsync($"/api/reportes/compras/por-proveedor?{Rango(ctx.IdEmpresa)}");
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var reporte = JsonSerializer.Deserialize<ComprasPorProveedor>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        var fila = Assert.Single(reporte.PorProveedor);

        using var libro = await DescargarLibroAsync(
            ctx.Admin, $"/api/reportes/compras/por-proveedor/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");
        var hoja = libro.Worksheets.First();

        Assert.Equal(fila.NombreProveedor, hoja.Cell(7, 1).GetString());
        Assert.Equal(fila.Total, hoja.Cell(7, 2).GetValue<decimal>());
        Assert.Equal(fila.CantidadCompras, hoja.Cell(7, 3).GetValue<int>());
        Assert.Equal(reporte.TotalGeneral, hoja.Cell(8, 2).GetValue<decimal>());
    }

    [Fact]
    public async Task ElExportDeGastosResumenEsIgualAlEndpointJson()
    {
        var ctx = await PrepararAsync(nameof(ElExportDeGastosResumenEsIgualAlEndpointJson));
        var idTurno = await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta);
        await SembrarGastoAsync(ctx, idTurno, 700m);

        var jsonRespuesta = await ctx.Admin.GetAsync($"/api/reportes/gastos/resumen?{Rango(ctx.IdEmpresa)}&granularidad=Dia");
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var reporte = JsonSerializer.Deserialize<ResumenDeGastos>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        Assert.Single(reporte.Serie);

        using var libro = await DescargarLibroAsync(
            ctx.Admin, $"/api/reportes/gastos/resumen/export?{Rango(ctx.IdEmpresa)}&granularidad=Dia&formato=xlsx");
        var hoja = libro.Worksheets.First();

        Assert.Equal(reporte.Serie[0].Etiqueta, hoja.Cell(7, 1).GetString());
        Assert.Equal(reporte.Serie[0].Importe, hoja.Cell(7, 2).GetValue<decimal>());
        Assert.Equal(reporte.ImporteTotal, hoja.Cell(8, 2).GetValue<decimal>());
    }

    [Fact]
    public async Task ElExportDeRentabilidadEsIgualAlEndpointJson()
    {
        var ctx = await PrepararAsync(nameof(ElExportDeRentabilidadEsIgualAlEndpointJson));
        var idArticulo = await SembrarArticuloAsync(ctx, "Articulo export rentabilidad");
        await SembrarLineaAsync(ctx, idArticulo, "Articulo export rentabilidad", 1m, 300m, costoUnitario: 100m);

        var jsonRespuesta = await ctx.Admin.GetAsync($"/api/reportes/rentabilidad?{Rango(ctx.IdEmpresa)}");
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var reporte = JsonSerializer.Deserialize<Rentabilidad>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        var fila = Assert.Single(reporte.PorArticulo);

        using var libro = await DescargarLibroAsync(
            ctx.Admin, $"/api/reportes/rentabilidad/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");
        var hoja = libro.Worksheets.First();

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
        var ctx = await PrepararAsync(nameof(ElExportDeComisionesEsIgualAlEndpointJson));
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
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoEnLosSeisExportsDeLecturaDeReportes) + ruta.Replace("/", "-"));

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
        var ctx = await PrepararAsync(nameof(UnSupervisorEsRechazadoEnElExportDeRentabilidad));

        var respuesta = await ctx.Supervisor.GetAsync($"/api/reportes/rentabilidad/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnSupervisorEsRechazadoEnElExportDeComisiones()
    {
        var ctx = await PrepararAsync(nameof(UnSupervisorEsRechazadoEnElExportDeComisiones));

        var respuesta = await ctx.Supervisor.GetAsync($"/api/reportes/comisiones/export?{Rango(ctx.IdEmpresa)}&formato=xlsx");

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

}
