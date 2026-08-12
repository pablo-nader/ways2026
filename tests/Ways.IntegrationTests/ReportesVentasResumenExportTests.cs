using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Exportacion;
using Ways.Application.Organizacion;
using Ways.Application.Reportes;
using Ways.Application.Usuarios;
using Ways.Domain.Reportes;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-11-exportacion-reportes, Slice 1b: <c>GET /api/reportes/ventas/resumen/export</c> — el
/// primer export sibling, patrón que toda slice siguiente copia (spec exportacion-de-reportes;
/// design: Data Flow). Archivo separado de <see cref="ReportesVentasResumenTests"/> (mismo
/// criterio de <c>PoliticaDeRoles</c>-style split que el resto del repo): esta clase necesita su
/// propio contenedor porque algunas de sus pruebas corren contra un host con
/// <see cref="OpcionesDeExportacion.TopeDeFilas"/> pisado, y <c>WaysApiFixture</c> es sellada
/// (no se puede heredar para exponer ese override) — <c>WithWebHostBuilder</c> arma un host
/// adicional que comparte la MISMA base (mismo <c>ConnectionStrings__Ways</c> ya seteado por
/// <see cref="WaysApiFixture.InitializeAsync"/>), sin tocar la fixture compartida.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ReportesVentasResumenExportTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, HttpClient Supervisor, HttpClient Vendedor,
        int IdCliente, int IdEmpleadoAdmin, int IdTipoComprobanteTx);

    /// <summary>Igual que <c>ReportesVentasResumenTests.PrepararAsync</c>, parametrizado por
    /// <paramref name="factory"/>: las pruebas de tope usan un <c>WithWebHostBuilder</c> propio
    /// para ejercitar la mutación (task 1b.9/1b.10) sin afectar al resto de esta clase.</summary>
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
        var idCliente = await dbTenant.Clientes.Select(c => c.Id).FirstAsync();

        await using var dbPlataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idTipoComprobanteTx = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).SingleAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, supervisor, vendedor,
            idCliente, resultado.IdUsuarioAdmin, idTipoComprobanteTx);
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
    /// <c>ReportesVentasResumenTests.SembrarComprobanteAsync</c>.</summary>
    private async Task SembrarComprobanteAsync(Contexto ctx, DateTimeOffset fecha, decimal total)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        db.ComprobantesVenta.Add(new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant,
            IdTipoComprobante = ctx.IdTipoComprobanteTx,
            Numero = Interlocked.Increment(ref _numeroSecuencial),
            Fecha = fecha,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdEmpleado = ctx.IdEmpleadoAdmin,
            IdCliente = ctx.IdCliente,
            Subtotal = total,
            DescuentoTotal = 0m,
            Total = total,
            Estado = EstadoComprobante.Emitido,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private static string ConstruirQuery(
        int idEmpresa, int idPuntoVenta, DateOnly desde, DateOnly hasta, Granularidad granularidad, string? formato) =>
        $"idEmpresa={idEmpresa}&idPuntoVenta={idPuntoVenta}&desde={desde:yyyy-MM-dd}&hasta={hasta:yyyy-MM-dd}" +
        $"&granularidad={granularidad}" + (formato is null ? string.Empty : $"&formato={formato}");

    private static Task<HttpResponseMessage> LlamarResumenAsync(
        HttpClient cliente, int idEmpresa, int idPuntoVenta, DateOnly desde, DateOnly hasta, Granularidad granularidad) =>
        cliente.GetAsync($"/api/reportes/ventas/resumen?{ConstruirQuery(idEmpresa, idPuntoVenta, desde, hasta, granularidad, null)}");

    private static Task<HttpResponseMessage> LlamarExportAsync(
        HttpClient cliente, int idEmpresa, int idPuntoVenta, DateOnly desde, DateOnly hasta, Granularidad granularidad,
        string formato = "xlsx") =>
        cliente.GetAsync($"/api/reportes/ventas/resumen/export?{ConstruirQuery(idEmpresa, idPuntoVenta, desde, hasta, granularidad, formato)}");

    // ---- task 1b.7: la exportación es igual al endpoint JSON para los mismos parámetros --------

    /// <summary>Rango de 2 días ⇒ 2 buckets (granularidad Día): cada fila de bucket se compara
    /// contra su entrada de <see cref="ResumenDeVentas.Serie"/> EN ORDEN (fila 7 = fila 6 = título
    /// de tabla + serie[0], fila 8 = serie[1]), más la fila de totales (fila 9) contra las figuras
    /// de nivel respuesta — un solo bucket comparado dejaba pasar mutaciones que solo afectan al
    /// mapeo por-bucket (orden invertido, un valor corrido) porque la fila de totales las absorbe.
    /// Mutación aplicada en <c>ExportacionDeReportes.De</c> (a) <c>.Reverse()</c> antes del
    /// <c>Select</c>: esta prueba pasó de verde a rojo (bucket[0] esperado vs. bucket[1] real en
    /// fila 7); revertida, vuelve a pasar. (b) <c>Celda.Moneda(bucket.Neto + 1m)</c>: mismo
    /// resultado (Neto de fila 7/8 desviado en 1); revertida, vuelve a pasar.</summary>
    [Fact]
    public async Task ElExportEsIgualAlEndpointJsonParaLosMismosParametros()
    {
        var ctx = await PrepararAsync(nameof(ElExportEsIgualAlEndpointJsonParaLosMismosParametros), fixture);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var ayer = hoy.AddDays(-1);
        var mediodiaAyerUtc = new DateTimeOffset(ayer.Year, ayer.Month, ayer.Day, 12, 0, 0, TimeSpan.Zero);
        var mediodiaHoyUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);

        await SembrarComprobanteAsync(ctx, mediodiaAyerUtc, 100m);
        await SembrarComprobanteAsync(ctx, mediodiaAyerUtc, 50m);
        await SembrarComprobanteAsync(ctx, mediodiaHoyUtc, 200m);
        await SembrarComprobanteAsync(ctx, mediodiaHoyUtc, 300m);
        await SembrarComprobanteAsync(ctx, mediodiaHoyUtc, 400m);

        var jsonRespuesta = await LlamarResumenAsync(ctx.Admin, ctx.IdEmpresa, ctx.IdPuntoVenta, ayer, hoy, Granularidad.Dia);
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var resumen = JsonSerializer.Deserialize<ResumenDeVentas>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        Assert.Equal(2, resumen.Serie.Count);

        var exportRespuesta = await LlamarExportAsync(ctx.Admin, ctx.IdEmpresa, ctx.IdPuntoVenta, ayer, hoy, Granularidad.Dia);
        Assert.Equal(HttpStatusCode.OK, exportRespuesta.StatusCode);
        Assert.Equal(ContentTypeXlsx, exportRespuesta.Content.Headers.ContentType?.MediaType);

        var nombreEsperado = NombreDeArchivo.Construir("ventas_resumen", $"pv{ctx.IdPuntoVenta}", ayer, hoy);
        var disposicion = exportRespuesta.Content.Headers.ContentDisposition?.ToString() ?? string.Empty;
        Assert.Contains($"filename=\"{nombreEsperado}\"", disposicion);
        Assert.Contains($"filename*=UTF-8''{nombreEsperado}", disposicion);

        using var libro = new XLWorkbook(new MemoryStream(await exportRespuesta.Content.ReadAsByteArrayAsync()));
        var hoja = libro.Worksheets.First();

        // Primera fila de datos es la 7 (fila 6 = título de tabla) — una fila por bucket, EN
        // ORDEN, seguida de la fila de totales.
        const int primeraFilaDeDatos = 7;
        for (var i = 0; i < resumen.Serie.Count; i++)
        {
            var bucket = resumen.Serie[i];
            var filaDeBucket = hoja.Row(primeraFilaDeDatos + i);
            Assert.Equal(bucket.Etiqueta, filaDeBucket.Cell(1).GetString());
            Assert.Equal(bucket.Neto, filaDeBucket.Cell(2).GetValue<decimal>());
            Assert.Equal(bucket.CantidadTx, filaDeBucket.Cell(3).GetValue<int>());
            Assert.Equal(bucket.TicketPromedio, (decimal?)filaDeBucket.Cell(4).GetValue<decimal>());
        }

        var filaDeTotales = hoja.Row(primeraFilaDeDatos + resumen.Serie.Count);
        Assert.Equal(resumen.NetoVendido, filaDeTotales.Cell(2).GetValue<decimal>());
        Assert.Equal(resumen.CantidadTx, filaDeTotales.Cell(3).GetValue<int>());
        Assert.Equal(resumen.TicketPromedio, (decimal?)filaDeTotales.Cell(4).GetValue<decimal>());
    }

    // ---- task 1b.8: 403 para el rol un escalón debajo del gate -----------------------------------

    [Fact]
    public async Task UnVendedorEsRechazadoDelExportDeVentas()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoDelExportDeVentas), fixture);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var respuesta = await LlamarExportAsync(ctx.Vendedor, ctx.IdEmpresa, ctx.IdPuntoVenta, hoy, hoy, Granularidad.Dia);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    /// <summary>Complementa <c>FormatoDeExportacionTests</c> (unitaria, sin fixture): un caso a
    /// nivel HTTP que atraviesa el pipeline real (routing, binding, middleware de errores) y
    /// confirma que <see cref="FormatoDeExportacion.Parsear"/> corta la request ANTES de tocar el
    /// servicio de reportes, con el <c>codigo</c> de dominio propagado en el ProblemDetails.</summary>
    [Fact]
    public async Task UnFormatoNoSoportadoRechazaConProblemDetailsAtravesDelPipelineHttp()
    {
        var ctx = await PrepararAsync(nameof(UnFormatoNoSoportadoRechazaConProblemDetailsAtravesDelPipelineHttp), fixture);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var respuesta = await LlamarExportAsync(ctx.Admin, ctx.IdEmpresa, ctx.IdPuntoVenta, hoy, hoy, Granularidad.Dia, formato: "pdf");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("formato_no_soportado", problema.GetProperty("codigo").GetString());
    }

    // ---- task 1b.11: el encabezado identifica alcance y generador --------------------------------

    [Fact]
    public async Task ElEncabezadoIdentificaElAlcanceYElGenerador()
    {
        var ctx = await PrepararAsync(nameof(ElEncabezadoIdentificaElAlcanceYElGenerador), fixture);
        var desde = new DateOnly(2026, 8, 1);
        var hasta = new DateOnly(2026, 8, 1);

        var respuesta = await LlamarExportAsync(ctx.Admin, ctx.IdEmpresa, ctx.IdPuntoVenta, desde, hasta, Granularidad.Dia);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        using var libro = new XLWorkbook(new MemoryStream(await respuesta.Content.ReadAsByteArrayAsync()));
        var hoja = libro.Worksheets.First();

        Assert.Contains($"Empresa: {ctx.IdEmpresa}", hoja.Cell(1, 1).GetString());
        Assert.Contains($"PV {ctx.IdPuntoVenta}", hoja.Cell(2, 1).GetString());
        Assert.Contains("2026-08-01", hoja.Cell(3, 1).GetString());
        Assert.Contains("admin", hoja.Cell(4, 1).GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(hoja.Cell(5, 1).Value.IsBlank);
        Assert.Equal("Período", hoja.Cell(6, 1).GetString());
    }

    // ---- task 1b.9: rechazo por tope (mutation-proof-tests) --------------------------------------

    /// <summary>Bindea <see cref="OpcionesDeExportacion.TopeDeFilas"/> a <c>3</c> en un host
    /// propio de esta prueba. Este reporte es un AGREGADO (design decisión 6): la guarda corre
    /// sobre <c>TablaExportable.Filas.Count</c>, no sobre un <c>COUNT(*)</c> — no hay ninguna fila
    /// de negocio que "sembrar" para llegar a 4 filas exportadas. En su lugar se ejercita la
    /// LONGITUD DE LA SERIE (spec: gap-fill, un bucket sin ventas nunca desaparece): un rango de 3
    /// días en granularidad Día siempre produce 3 buckets + 1 fila de totales = 4 filas, sin
    /// sembrar ningún comprobante — la escapatoria que las tasks.md de la slice autorizan
    /// explícitamente para reportes agregados.
    /// Mutación aplicada (comentar el <c>if</c> de <c>GuardaDeTope.Exigir</c>): esta prueba pasó
    /// de FALLAR (200 en vez de 400) a pasar al revertir — evidencia registrada en el cuerpo del PR.</summary>
    [Fact]
    public async Task UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadReal()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 3)));

        var ctx = await PrepararAsync(nameof(UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadReal), factoryBajo);
        var desde = new DateOnly(2026, 8, 1);
        var hasta = new DateOnly(2026, 8, 3);

        var respuesta = await LlamarExportAsync(ctx.Admin, ctx.IdEmpresa, ctx.IdPuntoVenta, desde, hasta, Granularidad.Dia);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exportacion_demasiado_grande", problema.GetProperty("codigo").GetString());
        Assert.Contains("4", problema.GetProperty("title").GetString());
    }

    // ---- task 1b.10: éxito exactamente en el tope -------------------------------------------------

    [Fact]
    public async Task UnaExportacionExactamenteEnElTopeSeAcepta()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 3)));

        var ctx = await PrepararAsync(nameof(UnaExportacionExactamenteEnElTopeSeAcepta), factoryBajo);
        var desde = new DateOnly(2026, 8, 1);
        var hasta = new DateOnly(2026, 8, 2);

        var respuesta = await LlamarExportAsync(ctx.Admin, ctx.IdEmpresa, ctx.IdPuntoVenta, desde, hasta, Granularidad.Dia);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        using var libro = new XLWorkbook(new MemoryStream(await respuesta.Content.ReadAsByteArrayAsync()));
        var hoja = libro.Worksheets.First();

        // 2 buckets + 1 fila de totales = 3 filas de datos, filas 7-9; la fila 10 tiene que estar
        // vacía (nada más allá del tope).
        Assert.False(hoja.Cell(7, 1).Value.IsBlank);
        Assert.False(hoja.Cell(8, 1).Value.IsBlank);
        Assert.False(hoja.Cell(9, 1).Value.IsBlank);
        Assert.True(hoja.Cell(10, 1).Value.IsBlank);
    }

    // ---- task 1b.12 vive en FormatoDeExportacionTests (unitaria, sin fixture) --------------------
}
