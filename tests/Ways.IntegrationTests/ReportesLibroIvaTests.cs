using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Compras;
using Ways.Application.Exportacion;
using Ways.Application.Organizacion;
using Ways.Application.Reportes;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Compras;
using Ways.Domain.Fiscal;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// Libro IVA compras y ventas punta a punta. Las compras se cargan por la API real (así las
/// alícuotas y percepciones son las que guarda <c>ServicioDeCompras</c>); las ventas fiscales se
/// siembran directo porque emitirlas exige WSFE. Todas las fechas son fijas (mediodía UTC, mayo de
/// 2026), nunca "hoy".
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ReportesLibroIvaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";
    private const string ContentTypeXlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static readonly DateOnly Desde = new(2026, 5, 1);
    private static readonly DateOnly Hasta = new(2026, 5, 31);

    private static long _numeroSecuencial = 1;

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdEmpresa2, int IdPuntoVenta2, HttpClient Admin,
        HttpClient Supervisor, HttpClient Vendedor, int IdEmpleadoAdmin, int IdProveedor, string RazonSocialProveedor,
        int IdCliente, int IdArea, int IdListaPrecio, int IdAlicuota21, int IdAlicuota105, int IdAlicuotaExento,
        int IdAlicuotaNoGravado, int IdTipoCFA, int IdTipoCFB, int IdTipoCRM, int IdTipoFA, int IdTipoNCA, int IdTipoTX);

    private async Task<Contexto> PrepararAsync(string nombre, WebApplicationFactory<Program>? fixtureDeLaPrueba = null)
    {
        var f = fixtureDeLaPrueba ?? fixture;
        var root = f.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var respuesta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = f.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        var supervisor = await CrearYLoguearAsync(f, admin, nombre, "supervisor", RolConocido.Supervisor);
        var vendedor = await CrearYLoguearAsync(f, admin, nombre, "vendedor", RolConocido.Vendedor);

        await using var dbTenant = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Area libro", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        dbTenant.Areas.Add(area);
        await dbTenant.SaveChangesAsync();

        var clienteBase = await dbTenant.Clientes.AsNoTracking().OrderBy(c => c.Id).FirstAsync();
        var cliente = new Cliente
        {
            IdTenant = resultado.IdTenant, Numero = 100, Nombre = "Cliente", RazonSocial = $"{nombre} Cliente SRL",
            TipoDocumento = TipoDocumento.Cuit, NumeroDocumento = "30711111118",
            IdCondicionFiscal = clienteBase.IdCondicionFiscal, IdListaPrecio = clienteBase.IdListaPrecio,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        dbTenant.Clientes.Add(cliente);

        var empresa2 = new Empresa { IdTenant = resultado.IdTenant, RazonSocial = $"{nombre} Empresa 2", CreatedAt = ahora, UpdatedAt = ahora };
        dbTenant.Empresas.Add(empresa2);
        await dbTenant.SaveChangesAsync();

        var puntoVenta2 = new PuntoVenta
        {
            IdTenant = resultado.IdTenant, IdEmpresa = empresa2.Id, Nombre = "Local 2", CreatedAt = ahora, UpdatedAt = ahora
        };
        dbTenant.PuntosVenta.Add(puntoVenta2);
        await dbTenant.SaveChangesAsync();

        await using var dbPlataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idCondicionFiscal = await dbPlataforma.CondicionesFiscales.Select(c => c.Id).FirstAsync();
        var proveedor = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = $"{nombre} Proveedor SA", Cuit = "30-70000000-1",
            IdCondicionFiscal = idCondicionFiscal, CreatedAt = ahora, UpdatedAt = ahora
        };
        dbPlataforma.Proveedores.Add(proveedor);
        await dbPlataforma.SaveChangesAsync();

        async Task<int> AlicuotaAsync(string nombreAlicuota) =>
            await dbPlataforma.AlicuotasIva.Where(a => a.Nombre == nombreAlicuota).Select(a => a.Id).FirstAsync();

        async Task<int> TipoAsync(string codigo) =>
            await dbPlataforma.TiposComprobante.Where(t => t.Codigo == codigo).Select(t => t.Id).SingleAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, empresa2.Id, puntoVenta2.Id, admin,
            supervisor, vendedor, resultado.IdUsuarioAdmin, proveedor.Id, proveedor.RazonSocial, cliente.Id, area.Id,
            clienteBase.IdListaPrecio, await AlicuotaAsync("21%"), await AlicuotaAsync("10.5%"),
            await AlicuotaAsync("Exento"), await AlicuotaAsync("No gravado"), await TipoAsync("C-FA"),
            await TipoAsync("C-FB"), await TipoAsync("C-RM"), await TipoAsync("FA"), await TipoAsync("NCA"),
            await TipoAsync("TX"));
    }

    private static async Task<HttpClient> CrearYLoguearAsync(
        WebApplicationFactory<Program> f, HttpClient admin, string nombre, string sufijo, RolConocido rol)
    {
        var corto = Guid.NewGuid().ToString("N")[..8];
        var mail = $"{nombre.ToLowerInvariant()}-{sufijo}@ways.test";
        var alta = await admin.PostAsJsonAsync("/api/usuarios", new CrearUsuario($"{sufijo}-{corto}", mail, (int)rol, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = f.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    // ---- compras: carga por la API real --------------------------------------------------------------

    private static LineaDeCompraSolicitada Concepto(decimal importe, int idAlicuota) =>
        new(null, "Concepto", 1m, null, null, importe, 0m, idAlicuota);

    private static async Task<CompraDetalle> CrearCompraAsync(
        Contexto ctx, int idTipo, IReadOnlyList<LineaDeCompraSolicitada> items, DateOnly fecha,
        IReadOnlyList<PercepcionSolicitada>? percepciones = null, bool confirmar = true, bool anular = false,
        HttpClient? cliente = null)
    {
        var solicitud = new SolicitudDeCompra(
            ctx.IdProveedor, idTipo, ctx.IdPuntoVenta, DatosDePrueba.NumeroExternoUnico(), fecha, null, items,
            Percepciones: percepciones);
        var admin = cliente ?? ctx.Admin;

        var creada = await LeerAsync<CompraDetalle>(await admin.PostAsJsonAsync("/api/compras", solicitud), HttpStatusCode.Created);
        if (!confirmar)
        {
            return creada;
        }

        var confirmada = await LeerAsync<CompraDetalle>(
            await admin.PostAsync($"/api/compras/{creada.Id}/confirmar", null), HttpStatusCode.OK);
        if (!anular)
        {
            return confirmada;
        }

        return await LeerAsync<CompraDetalle>(
            await admin.PostAsync($"/api/compras/{creada.Id}/anular", null), HttpStatusCode.OK);
    }

    /// <summary>Siembra directo una compra confirmada de un solo importe sin discriminar (factura B):
    /// para los casos de alcance y tope que no necesitan el desglose.</summary>
    private async Task<string> SembrarCompraSimpleAsync(
        Contexto ctx, int idPuntoVenta, DateOnly fecha, decimal total, int? idProveedor = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var numero = $"sembrada-{Interlocked.Increment(ref _numeroSecuencial)}";

        db.ComprobantesCompra.Add(new ComprobanteCompra
        {
            IdTenant = ctx.IdTenant,
            IdProveedor = idProveedor ?? ctx.IdProveedor,
            IdTipoComprobante = ctx.IdTipoCFB,
            NumeroExterno = numero,
            FechaComprobante = fecha,
            FechaRecepcion = new DateTimeOffset(fecha.Year, fecha.Month, fecha.Day, 12, 0, 0, TimeSpan.Zero),
            IdPuntoVenta = idPuntoVenta,
            IdEmpleado = ctx.IdEmpleadoAdmin,
            Subtotal = total,
            DescuentoTotal = 0m,
            Total = total,
            DiscriminaIva = false,
            Estado = EstadoCompra.Confirmada,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
        return numero;
    }

    // ---- ventas: siembra directa ---------------------------------------------------------------------

    private sealed record LineaSembrada(int IdAlicuota, decimal Porcentaje, decimal Total);

    private async Task<long> SembrarVentaAsync(
        Contexto ctx, DateTimeOffset fecha, int idTipo, IReadOnlyList<LineaSembrada> lineas, decimal? total = null,
        ResultadoFiscal? resultado = ResultadoFiscal.Aprobado, EstadoComprobante estado = EstadoComprobante.Emitido,
        int? idPuntoVenta = null, int? idCliente = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var importe = total ?? lineas.Sum(l => l.Total);
        var numero = Interlocked.Increment(ref _numeroSecuencial);
        var aprobado = resultado is ResultadoFiscal.Aprobado or ResultadoFiscal.AprobadoConObservaciones;

        var comprobante = new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant,
            IdTipoComprobante = idTipo,
            Numero = numero,
            Fecha = fecha,
            IdPuntoVenta = idPuntoVenta ?? ctx.IdPuntoVenta,
            IdEmpleado = ctx.IdEmpleadoAdmin,
            IdCliente = idCliente ?? ctx.IdCliente,
            Subtotal = importe,
            DescuentoTotal = 0m,
            Total = importe,
            Estado = estado,
            Cae = aprobado ? "12345678901234" : null,
            CaeVencimiento = aprobado ? new DateOnly(2026, 6, 10) : null,
            ResultadoFiscal = resultado,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        var orden = 1;
        foreach (var linea in lineas)
        {
            db.ItemsComprobanteVenta.Add(new ItemComprobanteVenta
            {
                IdTenant = ctx.IdTenant, IdComprobanteVenta = comprobante.Id, Orden = orden++, Descripcion = "Linea",
                IdArea = ctx.IdArea, IdListaPrecio = ctx.IdListaPrecio, IdAlicuotaIva = linea.IdAlicuota,
                PorcentajeIva = linea.Porcentaje, Cantidad = 1m, PrecioUnitario = linea.Total, Descuento = 0m,
                Total = linea.Total, CreatedAt = ahora, UpdatedAt = ahora
            });
        }

        await db.SaveChangesAsync();
        return numero;
    }

    private static DateTimeOffset MediodiaUtc(DateOnly fecha) =>
        new(fecha.Year, fecha.Month, fecha.Day, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<LineaSembrada> LineasMixtas(Contexto ctx) =>
    [
        new(ctx.IdAlicuota21, 21m, 121m),
        new(ctx.IdAlicuota105, 10.5m, 110.5m),
        new(ctx.IdAlicuotaExento, 0m, 50m),
        new(ctx.IdAlicuotaNoGravado, 0m, 30m)
    ];

    // ---- plomería HTTP -------------------------------------------------------------------------------

    private static string Query(DateOnly desde, DateOnly hasta, int? idEmpresa = null) =>
        $"desde={desde:yyyy-MM-dd}&hasta={hasta:yyyy-MM-dd}" + (idEmpresa is { } id ? $"&idEmpresa={id}" : string.Empty);

    private static async Task<T> LeerAsync<T>(HttpResponseMessage respuesta, HttpStatusCode esperado)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == esperado, cuerpo);
        return JsonSerializer.Deserialize<T>(cuerpo, OpcionesJson)!;
    }

    private static Task<LibroIva> ComprasAsync(HttpClient cliente, DateOnly desde, DateOnly hasta, int? idEmpresa = null) =>
        ObtenerAsync(cliente, "libro-iva-compras", desde, hasta, idEmpresa);

    private static Task<LibroIva> VentasAsync(HttpClient cliente, DateOnly desde, DateOnly hasta, int? idEmpresa = null) =>
        ObtenerAsync(cliente, "libro-iva-ventas", desde, hasta, idEmpresa);

    private static async Task<LibroIva> ObtenerAsync(
        HttpClient cliente, string ruta, DateOnly desde, DateOnly hasta, int? idEmpresa) =>
        await LeerAsync<LibroIva>(
            await cliente.GetAsync($"/api/reportes/{ruta}?{Query(desde, hasta, idEmpresa)}"), HttpStatusCode.OK);

    private static async Task<XLWorkbook> DescargarAsync(HttpClient cliente, string ruta)
    {
        var respuesta = await cliente.GetAsync(ruta);
        var cuerpo = respuesta.IsSuccessStatusCode ? null : await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        Assert.Equal(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);
        return new XLWorkbook(new MemoryStream(await respuesta.Content.ReadAsByteArrayAsync()));
    }

    private static IReadOnlyList<PercepcionSolicitada> Percepciones() =>
    [
        new(TiposDePercepcion.Iibb, 1000m, 3m, 30m),
        new(TiposDePercepcion.Iva, 1000m, 1.5m, 15m)
    ];

    private static async Task<(CompraDetalle Factura, CompraDetalle FacturaB)> CargarFacturasAsync(Contexto ctx)
    {
        var factura = await CrearCompraAsync(
            ctx, ctx.IdTipoCFA,
            [
                Concepto(1000m, ctx.IdAlicuota21), Concepto(200m, ctx.IdAlicuota105),
                Concepto(50m, ctx.IdAlicuotaExento), Concepto(30m, ctx.IdAlicuotaNoGravado)
            ],
            new DateOnly(2026, 5, 10), Percepciones());

        var facturaB = await CrearCompraAsync(
            ctx, ctx.IdTipoCFB, [Concepto(500m, ctx.IdAlicuota21)], new DateOnly(2026, 5, 12),
            [new PercepcionSolicitada(TiposDePercepcion.Iibb, 500m, 4m, 20m)]);

        return (factura, facturaB);
    }

    // ---- compras -------------------------------------------------------------------------------------

    [Fact]
    public async Task ElLibroDeComprasTraeLasFacturasConAlicuotasMixtasBYCPercepcionesYTotales()
    {
        var ctx = await PrepararAsync(nameof(ElLibroDeComprasTraeLasFacturasConAlicuotasMixtasBYCPercepcionesYTotales));
        var (factura, facturaB) = await CargarFacturasAsync(ctx);
        Assert.Equal(1556m, factura.Total);
        Assert.Equal(520m, facturaB.Total);

        var libro = await ComprasAsync(ctx.Admin, Desde, Hasta);

        Assert.Equal(2, libro.Filas.Count);
        var a = libro.Filas[0];
        Assert.Equal(new DateOnly(2026, 5, 10), a.Fecha);
        Assert.Equal("C-FA", a.TipoComprobante);
        Assert.Equal(factura.NumeroExterno, a.Numero);
        Assert.Equal(ctx.RazonSocialProveedor, a.Contraparte);
        Assert.Equal("30-70000000-1", a.Documento);
        Assert.Equal(
            [new AlicuotaDeLibroIva(21m, 1000m, 210m), new AlicuotaDeLibroIva(10.5m, 200m, 21m)], a.Alicuotas);
        Assert.Equal(30m, a.NoGravado);
        Assert.Equal(50m, a.Exento);
        Assert.Equal(15m, a.PercepcionIva);
        Assert.Equal(30m, a.PercepcionIibb);
        Assert.Equal(1556m, a.Total);
        Assert.Equal(0m, a.Diferencia);

        var b = libro.Filas[1];
        Assert.Equal(new DateOnly(2026, 5, 12), b.Fecha);
        Assert.Equal("C-FB", b.TipoComprobante);
        Assert.Empty(b.Alicuotas);
        Assert.Equal(500m, b.NoGravado);
        Assert.Equal(0m, b.Exento);
        Assert.Equal(0m, b.PercepcionIva);
        Assert.Equal(20m, b.PercepcionIibb);
        Assert.Equal(520m, b.Total);
        Assert.Equal(0m, b.Diferencia);

        var t = libro.Totales;
        Assert.Equal(
            [new AlicuotaDeLibroIva(21m, 1000m, 210m), new AlicuotaDeLibroIva(10.5m, 200m, 21m)], t.PorAlicuota);
        Assert.Equal(530m, t.NoGravado);
        Assert.Equal(50m, t.Exento);
        Assert.Equal(15m, t.PercepcionIva);
        Assert.Equal(50m, t.PercepcionIibb);
        Assert.Equal(2076m, t.Total);
        Assert.Equal(0m, t.Diferencia);
        Assert.Null(libro.ZonaHoraria);
        Assert.Null(libro.IdEmpresa);
    }

    [Fact]
    public async Task ElLibroDeComprasExcluyeRemitosBorradoresYAnuladas()
    {
        var ctx = await PrepararAsync(nameof(ElLibroDeComprasExcluyeRemitosBorradoresYAnuladas));
        var fecha = new DateOnly(2026, 5, 15);
        var incluida = await CrearCompraAsync(ctx, ctx.IdTipoCFB, [Concepto(100m, ctx.IdAlicuota21)], fecha);
        await CrearCompraAsync(ctx, ctx.IdTipoCRM, [Concepto(7000m, ctx.IdAlicuota21)], fecha);
        await CrearCompraAsync(ctx, ctx.IdTipoCFB, [Concepto(8000m, ctx.IdAlicuota21)], fecha, confirmar: false);
        await CrearCompraAsync(ctx, ctx.IdTipoCFB, [Concepto(9000m, ctx.IdAlicuota21)], fecha, anular: true);

        var libro = await ComprasAsync(ctx.Admin, Desde, Hasta);

        var fila = Assert.Single(libro.Filas);
        Assert.Equal(incluida.NumeroExterno, fila.Numero);
        Assert.Equal(100m, libro.Totales.Total);
    }

    [Fact]
    public async Task ElLibroDeComprasUsaLaFechaDelComprobanteConLimitesInclusivos()
    {
        var ctx = await PrepararAsync(nameof(ElLibroDeComprasUsaLaFechaDelComprobanteConLimitesInclusivos));
        await SembrarCompraSimpleAsync(ctx, ctx.IdPuntoVenta, new DateOnly(2026, 4, 30), 1m);
        var primero = await SembrarCompraSimpleAsync(ctx, ctx.IdPuntoVenta, new DateOnly(2026, 5, 1), 10m);
        var ultimo = await SembrarCompraSimpleAsync(ctx, ctx.IdPuntoVenta, new DateOnly(2026, 5, 31), 100m);
        await SembrarCompraSimpleAsync(ctx, ctx.IdPuntoVenta, new DateOnly(2026, 6, 1), 1000m);

        var libro = await ComprasAsync(ctx.Admin, Desde, Hasta);

        Assert.Equal([primero, ultimo], libro.Filas.Select(f => f.Numero));
        Assert.Equal(110m, libro.Totales.Total);
    }

    [Fact]
    public async Task ElLibroDeComprasNoMuestraComprasDeOtroTenant()
    {
        var ctxA = await PrepararAsync(nameof(ElLibroDeComprasNoMuestraComprasDeOtroTenant) + "-A");
        var ctxB = await PrepararAsync(nameof(ElLibroDeComprasNoMuestraComprasDeOtroTenant) + "-B");
        await SembrarCompraSimpleAsync(ctxB, ctxB.IdPuntoVenta, new DateOnly(2026, 5, 10), 999m);
        var propia = await SembrarCompraSimpleAsync(ctxA, ctxA.IdPuntoVenta, new DateOnly(2026, 5, 10), 5m);

        var libro = await ComprasAsync(ctxA.Admin, Desde, Hasta);

        Assert.Equal(propia, Assert.Single(libro.Filas).Numero);
    }

    [Fact]
    public async Task ElLibroDeComprasSeAcotaPorEmpresaYSinEmpresaAbarcaTodasLasDelTenant()
    {
        var ctx = await PrepararAsync(nameof(ElLibroDeComprasSeAcotaPorEmpresaYSinEmpresaAbarcaTodasLasDelTenant));
        var fecha = new DateOnly(2026, 5, 10);
        var deEmpresa1 = await SembrarCompraSimpleAsync(ctx, ctx.IdPuntoVenta, fecha, 10m);
        var deEmpresa2 = await SembrarCompraSimpleAsync(ctx, ctx.IdPuntoVenta2, fecha, 200m);

        var soloUna = await ComprasAsync(ctx.Admin, Desde, Hasta, ctx.IdEmpresa);
        var soloLaOtra = await ComprasAsync(ctx.Admin, Desde, Hasta, ctx.IdEmpresa2);
        var todas = await ComprasAsync(ctx.Admin, Desde, Hasta);

        Assert.Equal(deEmpresa1, Assert.Single(soloUna.Filas).Numero);
        Assert.Equal(ctx.IdEmpresa, soloUna.IdEmpresa);
        Assert.Equal(deEmpresa2, Assert.Single(soloLaOtra.Filas).Numero);
        Assert.Equal(2, todas.Filas.Count);
        Assert.Equal(210m, todas.Totales.Total);
    }

    [Fact]
    public async Task UnaEmpresaInexistenteOAjenaDevuelve404EnAmbosLibros()
    {
        var ctxA = await PrepararAsync(nameof(UnaEmpresaInexistenteOAjenaDevuelve404EnAmbosLibros) + "-A");
        var ctxB = await PrepararAsync(nameof(UnaEmpresaInexistenteOAjenaDevuelve404EnAmbosLibros) + "-B");

        foreach (var ruta in new[] { "libro-iva-compras", "libro-iva-ventas" })
        {
            var inexistente = await ctxA.Admin.GetAsync($"/api/reportes/{ruta}?{Query(Desde, Hasta, 999_999)}");
            var ajena = await ctxA.Admin.GetAsync($"/api/reportes/{ruta}?{Query(Desde, Hasta, ctxB.IdEmpresa)}");

            Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, ajena.StatusCode);
        }
    }

    [Fact]
    public async Task UnProveedorDadoDeBajaNoSacaSuCompraDelLibro()
    {
        var ctx = await PrepararAsync(nameof(UnProveedorDadoDeBajaNoSacaSuCompraDelLibro));
        var numero = await SembrarCompraSimpleAsync(ctx, ctx.IdPuntoVenta, new DateOnly(2026, 5, 10), 50m);

        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var proveedor = await db.Proveedores.SingleAsync(p => p.Id == ctx.IdProveedor);
            proveedor.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var fila = Assert.Single((await ComprasAsync(ctx.Admin, Desde, Hasta)).Filas);

        Assert.Equal(numero, fila.Numero);
        Assert.Equal(ServicioDeLibroIva.ContraparteNoDisponible, fila.Contraparte);
        Assert.Null(fila.Documento);
        Assert.Equal(50m, fila.Total);
    }

    [Theory]
    [InlineData("hasta-anterior", "2026-05-31", "2026-05-01", "rango_invalido")]
    [InlineData("rango-amplio", "2025-01-01", "2026-05-31", "rango_demasiado_amplio")]
    public async Task UnPeriodoInvalidoSeRechazaEnAmbosLibros(string sufijo, string desde, string hasta, string codigo)
    {
        var ctx = await PrepararAsync(nameof(UnPeriodoInvalidoSeRechazaEnAmbosLibros) + sufijo);

        foreach (var ruta in new[] { "libro-iva-compras", "libro-iva-ventas" })
        {
            var respuesta = await ctx.Admin.GetAsync($"/api/reportes/{ruta}?desde={desde}&hasta={hasta}");

            Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
            var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(codigo, problema.GetProperty("codigo").GetString());
        }
    }

    // ---- ventas --------------------------------------------------------------------------------------

    [Fact]
    public async Task ElLibroDeVentasTraeSoloLosFiscalesConCaeAprobadoYRestaLasNotasDeCredito()
    {
        var ctx = await PrepararAsync(nameof(ElLibroDeVentasTraeSoloLosFiscalesConCaeAprobadoYRestaLasNotasDeCredito));
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var puntoVenta = await db.PuntosVenta.SingleAsync(p => p.Id == ctx.IdPuntoVenta);
            puntoVenta.NumeroFiscal = 5;
            await db.SaveChangesAsync();
        }

        var fecha = MediodiaUtc(new DateOnly(2026, 5, 10));
        var factura = await SembrarVentaAsync(ctx, fecha, ctx.IdTipoFA, LineasMixtas(ctx));
        var notaDeCredito = await SembrarVentaAsync(
            ctx, MediodiaUtc(new DateOnly(2026, 5, 11)), ctx.IdTipoNCA,
            [new(ctx.IdAlicuota21, 21m, 60.5m), new(ctx.IdAlicuotaExento, 0m, 10m)]);
        var conObservaciones = await SembrarVentaAsync(
            ctx, MediodiaUtc(new DateOnly(2026, 5, 12)), ctx.IdTipoFA, [new(ctx.IdAlicuota21, 21m, 121m)],
            resultado: ResultadoFiscal.AprobadoConObservaciones);
        await SembrarVentaAsync(ctx, fecha, ctx.IdTipoTX, [new(ctx.IdAlicuota21, 21m, 7000m)], resultado: null);
        // Un ticket X con CAE no existe por emisión; se siembra para que solo es_fiscal lo excluya.
        await SembrarVentaAsync(ctx, fecha, ctx.IdTipoTX, [new(ctx.IdAlicuota21, 21m, 7500m)]);
        await SembrarVentaAsync(ctx, fecha, ctx.IdTipoFA, [new(ctx.IdAlicuota21, 21m, 8000m)], resultado: ResultadoFiscal.Pendiente);
        await SembrarVentaAsync(ctx, fecha, ctx.IdTipoFA, [new(ctx.IdAlicuota21, 21m, 9000m)], resultado: ResultadoFiscal.Rechazado);
        var anulada = await SembrarVentaAsync(
            ctx, MediodiaUtc(new DateOnly(2026, 5, 13)), ctx.IdTipoFA, [new(ctx.IdAlicuota21, 21m, 242m)],
            estado: EstadoComprobante.Anulado);
        await SembrarVentaAsync(
            ctx, fecha, ctx.IdTipoFA, [new(ctx.IdAlicuota21, 21m, 9500m)], resultado: ResultadoFiscal.Rechazado,
            estado: EstadoComprobante.Anulado);

        var libro = await VentasAsync(ctx.Admin, Desde, Hasta, ctx.IdEmpresa);

        Assert.Equal(4, libro.Filas.Count);
        var f = libro.Filas[0];
        Assert.Empty(f.Advertencias);
        Assert.Equal(new DateOnly(2026, 5, 10), f.Fecha);
        Assert.Equal("FA", f.TipoComprobante);
        Assert.Equal($"0005-{factura:D8}", f.Numero);
        Assert.EndsWith("Cliente SRL", f.Contraparte);
        Assert.Equal("CUIT 30711111118", f.Documento);
        Assert.Equal(
            [new AlicuotaDeLibroIva(21m, 100m, 21m), new AlicuotaDeLibroIva(10.5m, 100m, 10.5m)], f.Alicuotas);
        Assert.Equal(30m, f.NoGravado);
        Assert.Equal(50m, f.Exento);
        Assert.Equal(0m, f.PercepcionIva);
        Assert.Equal(0m, f.PercepcionIibb);
        Assert.Equal(311.5m, f.Total);
        Assert.Equal(0m, f.Diferencia);

        var nc = libro.Filas[1];
        Assert.Equal("NCA", nc.TipoComprobante);
        Assert.Equal($"0005-{notaDeCredito:D8}", nc.Numero);
        Assert.Equal([new AlicuotaDeLibroIva(21m, -50m, -10.5m)], nc.Alicuotas);
        Assert.Equal(-10m, nc.Exento);
        Assert.Equal(-70.5m, nc.Total);
        Assert.Equal(0m, nc.Diferencia);

        var obs = libro.Filas[2];
        Assert.Equal($"0005-{conObservaciones:D8}", obs.Numero);
        Assert.Equal([new AlicuotaDeLibroIva(21m, 100m, 21m)], obs.Alicuotas);
        Assert.Equal(121m, obs.Total);

        // Un CAE aprobado sigue valiendo ante ARCA aunque se anule local: queda en el libro, advertido.
        var anuladaFila = libro.Filas[3];
        Assert.Equal($"0005-{anulada:D8}", anuladaFila.Numero);
        Assert.Equal([new AlicuotaDeLibroIva(21m, 200m, 42m)], anuladaFila.Alicuotas);
        Assert.Equal(242m, anuladaFila.Total);
        Assert.Equal([AdvertenciasDeLibroIva.AnuladoSinNotaDeCredito], anuladaFila.Advertencias);

        var t = libro.Totales;
        Assert.Equal(
            [new AlicuotaDeLibroIva(21m, 350m, 73.5m), new AlicuotaDeLibroIva(10.5m, 100m, 10.5m)], t.PorAlicuota);
        Assert.Equal(30m, t.NoGravado);
        Assert.Equal(40m, t.Exento);
        Assert.Equal(604m, t.Total);
        Assert.Equal(0m, t.Diferencia);
        Assert.Equal("America/Argentina/Buenos_Aires", libro.ZonaHoraria);
    }

    [Fact]
    public async Task UnaVentaDeUnPuntoDeVentaSinNumeroFiscalMuestraSPVYLoAdvierteEnVezDeUsarElIdInterno()
    {
        var ctx = await PrepararAsync(nameof(UnaVentaDeUnPuntoDeVentaSinNumeroFiscalMuestraSPVYLoAdvierteEnVezDeUsarElIdInterno));
        var numero = await SembrarVentaAsync(
            ctx, MediodiaUtc(new DateOnly(2026, 5, 10)), ctx.IdTipoFA, [new(ctx.IdAlicuota21, 21m, 121m)]);

        var fila = Assert.Single((await VentasAsync(ctx.Admin, Desde, Hasta)).Filas);

        Assert.Equal($"s/PV-{numero:D8}", fila.Numero);
        Assert.Equal([AdvertenciasDeLibroIva.SinNumeroFiscal], fila.Advertencias);
    }

    /// <summary>Cláusula bajo prueba: el rango de ventas se resuelve con la zona de la empresa. Una
    /// venta del 31/05 a las 22:00 locales (01:00 UTC del 01/06) pertenece a mayo, y una del 01/06
    /// a las 00:30 locales (03:30 UTC) ya es de junio.</summary>
    [Fact]
    public async Task ElLibroDeVentasCortaElPeriodoEnLaZonaHorariaDeLaEmpresa()
    {
        var ctx = await PrepararAsync(nameof(ElLibroDeVentasCortaElPeriodoEnLaZonaHorariaDeLaEmpresa));
        var lineas = new List<LineaSembrada> { new(ctx.IdAlicuota21, 21m, 121m) };
        var tarde = await SembrarVentaAsync(ctx, new DateTimeOffset(2026, 6, 1, 1, 0, 0, TimeSpan.Zero), ctx.IdTipoFA, lineas);
        var temprano = await SembrarVentaAsync(ctx, new DateTimeOffset(2026, 6, 1, 3, 30, 0, TimeSpan.Zero), ctx.IdTipoFA, lineas);

        var mayo = await VentasAsync(ctx.Admin, Desde, Hasta, ctx.IdEmpresa);
        var junio = await VentasAsync(ctx.Admin, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), ctx.IdEmpresa);

        var enMayo = Assert.Single(mayo.Filas);
        Assert.Equal(new DateOnly(2026, 5, 31), enMayo.Fecha);
        Assert.EndsWith($"{tarde:D8}", enMayo.Numero);
        var enJunio = Assert.Single(junio.Filas);
        Assert.Equal(new DateOnly(2026, 6, 1), enJunio.Fecha);
        Assert.EndsWith($"{temprano:D8}", enJunio.Numero);
    }

    [Fact]
    public async Task ElLibroDeVentasSeAcotaPorEmpresaYPorTenant()
    {
        var ctxA = await PrepararAsync(nameof(ElLibroDeVentasSeAcotaPorEmpresaYPorTenant) + "-A");
        var ctxB = await PrepararAsync(nameof(ElLibroDeVentasSeAcotaPorEmpresaYPorTenant) + "-B");
        var fecha = MediodiaUtc(new DateOnly(2026, 5, 10));
        var propia = await SembrarVentaAsync(ctxA, fecha, ctxA.IdTipoFA, [new(ctxA.IdAlicuota21, 21m, 121m)]);
        var deLaOtraEmpresa = await SembrarVentaAsync(
            ctxA, fecha, ctxA.IdTipoFA, [new(ctxA.IdAlicuota21, 21m, 242m)], idPuntoVenta: ctxA.IdPuntoVenta2);
        await SembrarVentaAsync(ctxB, fecha, ctxB.IdTipoFA, [new(ctxB.IdAlicuota21, 21m, 9999m)]);

        var soloUna = await VentasAsync(ctxA.Admin, Desde, Hasta, ctxA.IdEmpresa);
        var todas = await VentasAsync(ctxA.Admin, Desde, Hasta);

        Assert.EndsWith($"{propia:D8}", Assert.Single(soloUna.Filas).Numero);
        Assert.Equal(2, todas.Filas.Count);
        Assert.Contains(todas.Filas, f => f.Numero.EndsWith($"{deLaOtraEmpresa:D8}"));
        Assert.Equal(363m, todas.Totales.Total);
        Assert.Null(todas.ZonaHoraria);
    }

    [Fact]
    public async Task UnaVentaCuyoTotalNoCierraContraSusLineasSeInformaConSuDiferencia()
    {
        var ctx = await PrepararAsync(nameof(UnaVentaCuyoTotalNoCierraContraSusLineasSeInformaConSuDiferencia));
        await SembrarVentaAsync(
            ctx, MediodiaUtc(new DateOnly(2026, 5, 10)), ctx.IdTipoFA, [new(ctx.IdAlicuota21, 21m, 121m)], total: 130m);

        var fila = Assert.Single((await VentasAsync(ctx.Admin, Desde, Hasta)).Filas);

        Assert.Equal(9m, fila.Diferencia);
        Assert.Equal(130m, fila.Total);
    }

    // ---- roles ---------------------------------------------------------------------------------------

    public static readonly TheoryData<string> Rutas = new()
    {
        "libro-iva-compras", "libro-iva-ventas", "libro-iva-compras/export", "libro-iva-ventas/export"
    };

    [Theory]
    [MemberData(nameof(Rutas))]
    public async Task UnVendedorEsRechazadoEnLosCuatroEndpointsDelLibro(string ruta)
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoEnLosCuatroEndpointsDelLibro) + ruta.Replace("/", "-"));

        var respuesta = await ctx.Vendedor.GetAsync($"/api/reportes/{ruta}?{Query(Desde, Hasta)}&formato=xlsx");

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Rutas))]
    public async Task UnSupervisorYUnAdminLeenLosCuatroEndpointsDelLibro(string ruta)
    {
        var ctx = await PrepararAsync(nameof(UnSupervisorYUnAdminLeenLosCuatroEndpointsDelLibro) + ruta.Replace("/", "-"));

        var supervisor = await ctx.Supervisor.GetAsync($"/api/reportes/{ruta}?{Query(Desde, Hasta)}&formato=xlsx");
        var admin = await ctx.Admin.GetAsync($"/api/reportes/{ruta}?{Query(Desde, Hasta)}&formato=xlsx");

        Assert.Equal(HttpStatusCode.OK, supervisor.StatusCode);
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
    }

    // ---- export --------------------------------------------------------------------------------------

    [Fact]
    public async Task ElExportDeComprasEsIgualAlEndpointJsonCeldaPorCelda()
    {
        var ctx = await PrepararAsync(nameof(ElExportDeComprasEsIgualAlEndpointJsonCeldaPorCelda));
        await CargarFacturasAsync(ctx);
        var libro = await ComprasAsync(ctx.Admin, Desde, Hasta);

        using var xlsx = await DescargarAsync(
            ctx.Admin, $"/api/reportes/libro-iva-compras/export?{Query(Desde, Hasta)}&formato=xlsx");
        var hoja = xlsx.Worksheets.First();

        Assert.Equal(
            [
                "Fecha", "Tipo", "Número", "Proveedor", "CUIT",
                "Neto 21%", "IVA 21%", "Neto 10,5%", "IVA 10,5%", "Neto 27%", "IVA 27%", "Neto 5%", "IVA 5%",
                "Neto 2,5%", "IVA 2,5%", "Neto otras alícuotas", "IVA otras alícuotas", "No gravado", "Exento",
                "Percepción IVA", "Percepción IIBB", "Total", "Diferencia", "Observaciones"
            ],
            Enumerable.Range(1, 24).Select(c => hoja.Cell(6, c).GetString()));

        for (var i = 0; i < libro.Filas.Count; i++)
        {
            var fila = libro.Filas[i];
            var r = 7 + i;
            Assert.Equal(fila.Fecha, DateOnly.FromDateTime(hoja.Cell(r, 1).GetDateTime()));
            Assert.Equal(fila.TipoComprobante, hoja.Cell(r, 2).GetString());
            Assert.Equal(fila.Numero, hoja.Cell(r, 3).GetString());
            Assert.Equal(fila.Contraparte, hoja.Cell(r, 4).GetString());
            Assert.Equal(fila.Documento, hoja.Cell(r, 5).GetString());
            Assert.Equal(
                [fila.Alicuotas.Sum(x => x.Porcentaje == 21m ? x.Neto : 0m), fila.Alicuotas.Sum(x => x.Porcentaje == 21m ? x.Iva : 0m)],
                [hoja.Cell(r, 6).GetValue<decimal>(), hoja.Cell(r, 7).GetValue<decimal>()]);
            Assert.Equal(
                [fila.Alicuotas.Sum(x => x.Porcentaje == 10.5m ? x.Neto : 0m), fila.Alicuotas.Sum(x => x.Porcentaje == 10.5m ? x.Iva : 0m)],
                [hoja.Cell(r, 8).GetValue<decimal>(), hoja.Cell(r, 9).GetValue<decimal>()]);
            Assert.Equal(
                [0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m],
                Enumerable.Range(10, 8).Select(c => hoja.Cell(r, c).GetValue<decimal>()));
            Assert.Equal(
                [fila.NoGravado, fila.Exento, fila.PercepcionIva, fila.PercepcionIibb, fila.Total, fila.Diferencia],
                Enumerable.Range(18, 6).Select(c => hoja.Cell(r, c).GetValue<decimal>()));
            Assert.Equal(string.Empty, hoja.Cell(r, 24).GetString());
        }

        var total = 7 + libro.Filas.Count;
        Assert.Equal("Empresa: Todas", hoja.Cell(1, 1).GetString());
        Assert.Equal("Total", hoja.Cell(total, 2).GetString());
        Assert.Equal(
            [
                libro.Totales.PorAlicuota[0].Neto, libro.Totales.PorAlicuota[0].Iva,
                libro.Totales.PorAlicuota[1].Neto, libro.Totales.PorAlicuota[1].Iva
            ],
            Enumerable.Range(6, 4).Select(c => hoja.Cell(total, c).GetValue<decimal>()));
        Assert.Equal(
            [
                libro.Totales.NoGravado, libro.Totales.Exento, libro.Totales.PercepcionIva, libro.Totales.PercepcionIibb,
                libro.Totales.Total, libro.Totales.Diferencia
            ],
            Enumerable.Range(18, 6).Select(c => hoja.Cell(total, c).GetValue<decimal>()));
    }

    [Fact]
    public async Task ElExportDeVentasEsIgualAlEndpointJsonCeldaPorCelda()
    {
        var ctx = await PrepararAsync(nameof(ElExportDeVentasEsIgualAlEndpointJsonCeldaPorCelda));
        await SembrarVentaAsync(ctx, MediodiaUtc(new DateOnly(2026, 5, 10)), ctx.IdTipoFA, LineasMixtas(ctx));
        await SembrarVentaAsync(
            ctx, MediodiaUtc(new DateOnly(2026, 5, 11)), ctx.IdTipoNCA,
            [new(ctx.IdAlicuota21, 21m, 60.5m), new(ctx.IdAlicuotaExento, 0m, 10m)]);
        await SembrarVentaAsync(
            ctx, MediodiaUtc(new DateOnly(2026, 5, 12)), ctx.IdTipoFA, [new(ctx.IdAlicuota21, 21m, 121m)],
            estado: EstadoComprobante.Anulado);
        var libro = await VentasAsync(ctx.Admin, Desde, Hasta, ctx.IdEmpresa);
        Assert.Equal(3, libro.Filas.Count);

        using var xlsx = await DescargarAsync(
            ctx.Admin, $"/api/reportes/libro-iva-ventas/export?{Query(Desde, Hasta, ctx.IdEmpresa)}&formato=xlsx");
        var hoja = xlsx.Worksheets.First();

        Assert.Equal(
            [
                "Fecha", "Tipo", "Número", "Cliente", "Documento",
                "Neto 21%", "IVA 21%", "Neto 10,5%", "IVA 10,5%", "Neto 27%", "IVA 27%", "Neto 5%", "IVA 5%",
                "Neto 2,5%", "IVA 2,5%", "Neto otras alícuotas", "IVA otras alícuotas", "No gravado", "Exento",
                "Total", "Diferencia", "Observaciones"
            ],
            Enumerable.Range(1, 22).Select(c => hoja.Cell(6, c).GetString()));

        for (var i = 0; i < libro.Filas.Count; i++)
        {
            var fila = libro.Filas[i];
            var r = 7 + i;
            Assert.Equal(fila.Fecha, DateOnly.FromDateTime(hoja.Cell(r, 1).GetDateTime()));
            Assert.Equal(fila.TipoComprobante, hoja.Cell(r, 2).GetString());
            Assert.Equal(fila.Numero, hoja.Cell(r, 3).GetString());
            Assert.Equal(fila.Contraparte, hoja.Cell(r, 4).GetString());
            Assert.Equal(fila.Documento, hoja.Cell(r, 5).GetString());
            Assert.Equal(
                [
                    fila.Alicuotas.Sum(x => x.Porcentaje == 21m ? x.Neto : 0m), fila.Alicuotas.Sum(x => x.Porcentaje == 21m ? x.Iva : 0m),
                    fila.Alicuotas.Sum(x => x.Porcentaje == 10.5m ? x.Neto : 0m), fila.Alicuotas.Sum(x => x.Porcentaje == 10.5m ? x.Iva : 0m)
                ],
                Enumerable.Range(6, 4).Select(c => hoja.Cell(r, c).GetValue<decimal>()));
            Assert.Equal(
                [fila.NoGravado, fila.Exento, fila.Total, fila.Diferencia],
                Enumerable.Range(18, 4).Select(c => hoja.Cell(r, c).GetValue<decimal>()));
        }

        Assert.Equal("Sin número fiscal de PV", hoja.Cell(7, 22).GetString());
        Assert.Equal("Sin número fiscal de PV", hoja.Cell(8, 22).GetString());
        Assert.Equal("Anulado sin NC; Sin número fiscal de PV", hoja.Cell(9, 22).GetString());
        Assert.Equal($"Empresa: {nameof(ElExportDeVentasEsIgualAlEndpointJsonCeldaPorCelda)} SA", hoja.Cell(1, 1).GetString());
        Assert.Equal("Total", hoja.Cell(10, 2).GetString());
        Assert.Equal(libro.Totales.Total, hoja.Cell(10, 20).GetValue<decimal>());
        Assert.Equal(libro.Totales.Exento, hoja.Cell(10, 19).GetValue<decimal>());
    }

    [Theory]
    [InlineData("libro-iva-compras/export")]
    [InlineData("libro-iva-ventas/export")]
    public async Task UnFormatoNoSoportadoSeRechazaEnLosDosExports(string ruta)
    {
        var ctx = await PrepararAsync(nameof(UnFormatoNoSoportadoSeRechazaEnLosDosExports) + ruta.Replace("/", "-"));

        var respuesta = await ctx.Admin.GetAsync($"/api/reportes/{ruta}?{Query(Desde, Hasta)}&formato=pdf");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
    }

    // ---- tope ----------------------------------------------------------------------------------------

    private WebApplicationFactory<Program> ConTope(int tope) =>
        fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = tope)));

    /// <summary>Siembra <c>tope + 2</c> (no <c>tope + 1</c>): con <c>tope + 1</c> un mutante que
    /// compara contra <c>tope + 1</c> también rechazaría, y la cantidad informada solo es la real
    /// si se cuenta antes de materializar.</summary>
    [Fact]
    public async Task UnLibroDeComprasQueSuperaElTopeSeRechazaConLaCantidadReal()
    {
        using var factoryBajo = ConTope(3);
        var ctx = await PrepararAsync(nameof(UnLibroDeComprasQueSuperaElTopeSeRechazaConLaCantidadReal), factoryBajo);
        for (var i = 0; i < 5; i++)
        {
            await SembrarCompraSimpleAsync(ctx, ctx.IdPuntoVenta, new DateOnly(2026, 5, 10), 1m);
        }

        var respuesta = await ctx.Admin.GetAsync($"/api/reportes/libro-iva-compras?{Query(Desde, Hasta)}");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("libro_iva_demasiado_grande", problema.GetProperty("codigo").GetString());
        Assert.Contains("tiene 5 comprobantes", problema.GetProperty("title").GetString());
    }

    [Fact]
    public async Task UnLibroDeComprasDeExactamenteElTopeSeAceptaCompleto()
    {
        using var factoryBajo = ConTope(3);
        var ctx = await PrepararAsync(nameof(UnLibroDeComprasDeExactamenteElTopeSeAceptaCompleto), factoryBajo);
        for (var i = 0; i < 3; i++)
        {
            await SembrarCompraSimpleAsync(ctx, ctx.IdPuntoVenta, new DateOnly(2026, 5, 10), 1m);
        }

        var libro = await ComprasAsync(ctx.Admin, Desde, Hasta);

        Assert.Equal(3, libro.Filas.Count);
    }

    [Fact]
    public async Task UnLibroDeVentasQueSuperaElTopeSeRechazaSumandoTodasLasEmpresas()
    {
        using var factoryBajo = ConTope(3);
        var ctx = await PrepararAsync(nameof(UnLibroDeVentasQueSuperaElTopeSeRechazaSumandoTodasLasEmpresas), factoryBajo);
        var fecha = MediodiaUtc(new DateOnly(2026, 5, 10));
        for (var i = 0; i < 3; i++)
        {
            await SembrarVentaAsync(ctx, fecha, ctx.IdTipoFA, [new(ctx.IdAlicuota21, 21m, 121m)]);
        }

        for (var i = 0; i < 2; i++)
        {
            await SembrarVentaAsync(
                ctx, fecha, ctx.IdTipoFA, [new(ctx.IdAlicuota21, 21m, 121m)], idPuntoVenta: ctx.IdPuntoVenta2);
        }

        var respuesta = await ctx.Admin.GetAsync($"/api/reportes/libro-iva-ventas?{Query(Desde, Hasta)}");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("tiene 5 comprobantes", problema.GetProperty("title").GetString());

        var unaEmpresa = await VentasAsync(ctx.Admin, Desde, Hasta, ctx.IdEmpresa);
        Assert.Equal(3, unaEmpresa.Filas.Count);
    }
}
