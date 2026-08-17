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
using Ways.Application.Usuarios;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.Proveedores;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-11-exportacion-reportes, Slice 3: <c>GET /api/compras/export</c> — mismo patrón de
/// <c>ConstruirQuery</c> compartido que <c>VentasListadoExportTests</c>, sin
/// <c>idPuntoVenta</c> (el listado JSON de compras tampoco lo tiene): Empresa/zona usan el
/// default de <c>AlcanceDeListadoHttp</c>. El backstop de carrera del <c>+1</c> se prueba una
/// sola vez para toda la slice, en <c>VentasListadoExportTests</c> (design decisión de la
/// slice: la única query real de tipo COUNT contra la que compras/estado-de-cuenta compartirían
/// el mismo mecanismo, ya cubierto).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ComprasListadoExportTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string ContentTypeXlsx =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, HttpClient Admin, HttpClient Root, int IdProveedor, int IdTipoCFA,
        int IdEmpleadoAdmin);

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

        var rootLogueado = factory.CreateClient();
        var reloginRoot = await rootLogueado.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, reloginRoot.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var proveedor = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = nombre, IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        var idTipoCFA = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, admin, rootLogueado, proveedor.Id, idTipoCFA,
            resultado.IdUsuarioAdmin);
    }

    /// <summary>Siembra directo en estado <c>Confirmada</c> — sin pasar por
    /// <c>ServicioDeCompras</c>, mismo criterio que las siembras directas de reportes. Fecha fija
    /// a mediodía UTC.</summary>
    private async Task SembrarCompraAsync(Contexto ctx, DateOnly fecha, decimal total, string numeroExterno)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var mediodia = new DateTimeOffset(fecha.Year, fecha.Month, fecha.Day, 12, 0, 0, TimeSpan.Zero);

        db.ComprobantesCompra.Add(new ComprobanteCompra
        {
            IdTenant = ctx.IdTenant,
            IdProveedor = ctx.IdProveedor,
            IdTipoComprobante = ctx.IdTipoCFA,
            NumeroExterno = numeroExterno,
            FechaComprobante = fecha,
            FechaRecepcion = mediodia,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdEmpleado = ctx.IdEmpleadoAdmin,
            Subtotal = total,
            DescuentoTotal = 0m,
            Total = total,
            Estado = EstadoCompra.Confirmada,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private static string ConstruirQuery(DateOnly desde, DateOnly hasta, string? formato) =>
        $"desde={desde:yyyy-MM-dd}T00:00:00Z&hasta={hasta:yyyy-MM-dd}T23:59:59Z" +
        (formato is null ? string.Empty : $"&formato={formato}");

    private static Task<HttpResponseMessage> LlamarListadoAsync(HttpClient cliente, DateOnly desde, DateOnly hasta) =>
        cliente.GetAsync($"/api/compras?{ConstruirQuery(desde, hasta, null)}&tamanio=200");

    private static Task<HttpResponseMessage> LlamarExportAsync(
        HttpClient cliente, DateOnly desde, DateOnly hasta, string formato = "xlsx") =>
        cliente.GetAsync($"/api/compras/export?{ConstruirQuery(desde, hasta, formato)}");

    // ---- task 3.5: la exportación es igual al listado JSON --------------------------------------

    [Fact]
    public async Task ElExportEsIgualAlListadoJsonParaLosMismosParametros()
    {
        var ctx = await PrepararAsync(nameof(ElExportEsIgualAlListadoJsonParaLosMismosParametros), fixture);
        var desde = new DateOnly(2026, 8, 1);
        var hasta = new DateOnly(2026, 8, 2);

        await SembrarCompraAsync(ctx, desde, 500m, "0001-00000001");
        await SembrarCompraAsync(ctx, hasta, 300m, "0001-00000002");

        var jsonRespuesta = await LlamarListadoAsync(ctx.Admin, desde, hasta);
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var pagina = JsonSerializer.Deserialize<PaginaDeCompras>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        Assert.Equal(2, pagina.Items.Count);

        var exportRespuesta = await LlamarExportAsync(ctx.Admin, desde, hasta);
        var cuerpoError = exportRespuesta.IsSuccessStatusCode ? string.Empty : await exportRespuesta.Content.ReadAsStringAsync();
        Assert.True(exportRespuesta.StatusCode == HttpStatusCode.OK, cuerpoError);
        Assert.Equal(ContentTypeXlsx, exportRespuesta.Content.Headers.ContentType?.MediaType);

        using var libro = new XLWorkbook(new MemoryStream(await exportRespuesta.Content.ReadAsByteArrayAsync()));
        var hoja = libro.Worksheets.First();

        // Fila 6 = título de tabla (mutation-proof-tests regla 8): el header es lo que ata cada
        // celda de datos a su columna, sin este assert un swap de labels pasa inadvertido porque
        // el test de igualdad de abajo solo lee celdas por posición.
        const int filaDeEncabezados = 6;
        Assert.Equal(
            ["Comprobante", "Proveedor", "Fecha de recepción", "Estado", "Total"],
            Enumerable.Range(1, 5).Select(c => hoja.Cell(filaDeEncabezados, c).GetString()));

        var zona = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");
        const int primeraFilaDeDatos = 7;
        for (var i = 0; i < pagina.Items.Count; i++)
        {
            var item = pagina.Items[i];
            var fila = hoja.Row(primeraFilaDeDatos + i);
            Assert.Equal(item.NumeroExterno, fila.Cell(1).GetString());
            Assert.Equal(item.IdProveedor, fila.Cell(2).GetValue<int>());
            Assert.Equal(TimeZoneInfo.ConvertTime(item.FechaRecepcion!.Value, zona).DateTime, fila.Cell(3).GetValue<DateTime>());
            Assert.Equal(item.Estado.ToString(), fila.Cell(4).GetString());
            Assert.Equal(item.Total, fila.Cell(5).GetValue<decimal>());
        }
    }

    // ---- task 3.6: 403 para el rol excluido de OperacionDePos ------------------------------------

    [Fact]
    public async Task UnRootEsRechazadoDelExportDeCompras()
    {
        var ctx = await PrepararAsync(nameof(UnRootEsRechazadoDelExportDeCompras), fixture);
        var hoy = new DateOnly(2026, 8, 1);

        var respuesta = await LlamarExportAsync(ctx.Root, hoy, hoy);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    // ---- task 3.7: rechazo por tope ---------------------------------------------------------------

    /// <summary>Discriminador real del PRIMER <c>GuardaDeTope.Exigir</c> (sobre el <c>COUNT(*)</c>):
    /// se siembra tope+2 (5, no tope+1) filas porque con solo 4 filas el <c>COUNT(*)</c> real y la
    /// lectura truncada por <c>.Take(tope + 1)</c> coinciden en "4" — borrar el primer <c>Exigir</c>
    /// sobrevive porque el segundo rechaza igual con el mismo número. Con 5 filas el <c>Take(4)</c>
    /// trunca: el mutante reporta "4" (el truncado) en vez de la cantidad REAL "5", y el assert de
    /// abajo lo discrimina.</summary>
    [Fact]
    public async Task UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadReal()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 3)));

        var ctx = await PrepararAsync(nameof(UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadReal), factoryBajo);
        var dia = new DateOnly(2026, 8, 1);

        for (var i = 0; i < 5; i++)
        {
            await SembrarCompraAsync(ctx, dia, 100m + i, $"0001-0000000{i}");
        }

        var respuesta = await LlamarExportAsync(ctx.Admin, dia, dia);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exportacion_demasiado_grande", problema.GetProperty("codigo").GetString());
        Assert.Contains("tiene 5 filas", problema.GetProperty("title").GetString());
    }

    // ---- FormatoDeExportacion.Parsear en esta ruta (barrido de gaps compartidos) ------------------

    /// <summary>Sin este test, borrar la llamada a <see cref="FormatoDeExportacion.Parsear"/> en
    /// <c>/api/compras/export</c> sobrevive — un <c>formato=pdf</c> devolvería 200 XLSX en vez de
    /// 400.</summary>
    [Fact]
    public async Task UnFormatoNoSoportadoRechazaConProblemDetailsEnElExportDeCompras()
    {
        var ctx = await PrepararAsync(nameof(UnFormatoNoSoportadoRechazaConProblemDetailsEnElExportDeCompras), fixture);
        var hoy = new DateOnly(2026, 8, 1);

        var respuesta = await LlamarExportAsync(ctx.Admin, hoy, hoy, formato: "pdf");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("formato_no_soportado", problema.GetProperty("codigo").GetString());
    }

    // ---- exportar exactamente el tope de filas es legítimo (barrido de gaps compartidos) ----------

    /// <summary>Discriminador real del SEGUNDO <c>GuardaDeTope.Exigir</c> del lado del ÉXITO: sin
    /// este test, mutar ese segundo <c>Exigir</c> a <c>Exigir(items.Count, tope - 1)</c> sobrevive
    /// — <c>UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadReal</c> solo cubre el rechazo por
    /// ARRIBA del tope. Acá se exportan EXACTAMENTE <c>tope</c> filas y se espera 200 con el
    /// workbook completo.</summary>
    [Fact]
    public async Task UnaExportacionDeExactamenteElTopeDeFilasSeAceptaCompleta()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 3)));

        var ctx = await PrepararAsync(nameof(UnaExportacionDeExactamenteElTopeDeFilasSeAceptaCompleta), factoryBajo);
        var dia = new DateOnly(2026, 8, 1);

        for (var i = 0; i < 3; i++)
        {
            await SembrarCompraAsync(ctx, dia, 100m + i, $"0001-0000000{i}");
        }

        var respuesta = await LlamarExportAsync(ctx.Admin, dia, dia);
        var cuerpoError = respuesta.IsSuccessStatusCode ? string.Empty : await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpoError);
        Assert.Equal(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        using var libro = new XLWorkbook(new MemoryStream(await respuesta.Content.ReadAsByteArrayAsync()));
        var hoja = libro.Worksheets.First();

        // Encabezado en la fila 6, datos desde la 7 (mismo layout que
        // ElExportEsIgualAlListadoJsonParaLosMismosParametros): las tope=3 filas ocupan 7-9, la
        // fila 10 debe quedar vacía.
        const int primeraFilaDeDatos = 7;
        for (var i = 0; i < 3; i++)
        {
            Assert.False(hoja.Row(primeraFilaDeDatos + i).IsEmpty());
        }
        Assert.True(hoja.Row(primeraFilaDeDatos + 3).IsEmpty());
    }
}
