using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Exportacion;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-11-exportacion-reportes, Slice 3: <c>GET /api/ventas/export</c> — el primer export de
/// tipo LISTADO (a diferencia de los agregados de Slice 1b/2), donde <c>GuardaDeTope</c> corre
/// dos veces: sobre el <c>COUNT(*)</c> y sobre la lectura de <c>.Take(tope + 1)</c> (design
/// decisión 7). Mismo patrón de fixture propia por <c>WithWebHostBuilder</c> que
/// <c>ReportesVentasResumenExportTests</c> — <c>OpcionesDeExportacion.TopeDeFilas</c> pisado por
/// prueba.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class VentasListadoExportTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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
        int IdTenant, int IdPuntoVenta, HttpClient Admin, HttpClient Root, int IdCliente,
        int IdEmpleadoAdmin, int IdTipoComprobanteTx);

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

        await using var dbTenant = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idCliente = await dbTenant.Clientes.Select(c => c.Id).FirstAsync();

        await using var dbPlataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idTipoComprobanteTx = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).SingleAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, admin, rootLogueado, idCliente, resultado.IdUsuarioAdmin,
            idTipoComprobanteTx);
    }

    /// <summary>Siembra directo, sin pasar por <c>ServicioDeVentas</c> — mismo criterio que
    /// <c>ReportesVentasResumenExportTests.SembrarComprobanteAsync</c>. Fecha fija a mediodía UTC
    /// (evita la ventana 00-03 UTC que corre el día en zonas horarias -03).</summary>
    private async Task SembrarComprobanteAsync(Contexto ctx, DateOnly fecha, decimal total)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var mediodia = new DateTimeOffset(fecha.Year, fecha.Month, fecha.Day, 12, 0, 0, TimeSpan.Zero);

        db.ComprobantesVenta.Add(new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant,
            IdTipoComprobante = ctx.IdTipoComprobanteTx,
            Numero = Interlocked.Increment(ref _numeroSecuencial),
            Fecha = mediodia,
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

    private static string ConstruirQuery(int idPuntoVenta, DateOnly desde, DateOnly hasta, string? formato) =>
        $"idPuntoVenta={idPuntoVenta}&desde={desde:yyyy-MM-dd}T00:00:00Z&hasta={hasta:yyyy-MM-dd}T23:59:59Z" +
        (formato is null ? string.Empty : $"&formato={formato}");

    private static Task<HttpResponseMessage> LlamarListadoAsync(
        HttpClient cliente, int idPuntoVenta, DateOnly desde, DateOnly hasta) =>
        cliente.GetAsync($"/api/ventas?{ConstruirQuery(idPuntoVenta, desde, hasta, null)}&tamanio=200");

    private static Task<HttpResponseMessage> LlamarExportAsync(
        HttpClient cliente, int idPuntoVenta, DateOnly desde, DateOnly hasta, string formato = "xlsx") =>
        cliente.GetAsync($"/api/ventas/export?{ConstruirQuery(idPuntoVenta, desde, hasta, formato)}");

    // ---- task 3.5: la exportación es igual al listado JSON --------------------------------------

    [Fact]
    public async Task ElExportEsIgualAlListadoJsonParaLosMismosParametros()
    {
        var ctx = await PrepararAsync(nameof(ElExportEsIgualAlListadoJsonParaLosMismosParametros), fixture);
        var desde = new DateOnly(2026, 8, 1);
        var hasta = new DateOnly(2026, 8, 2);

        await SembrarComprobanteAsync(ctx, desde, 100m);
        await SembrarComprobanteAsync(ctx, desde, 50m);
        await SembrarComprobanteAsync(ctx, hasta, 200m);

        var jsonRespuesta = await LlamarListadoAsync(ctx.Admin, ctx.IdPuntoVenta, desde, hasta);
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var pagina = JsonSerializer.Deserialize<PaginaDeVentas>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        Assert.Equal(3, pagina.Items.Count);

        var exportRespuesta = await LlamarExportAsync(ctx.Admin, ctx.IdPuntoVenta, desde, hasta);
        var cuerpoError = exportRespuesta.IsSuccessStatusCode ? string.Empty : await exportRespuesta.Content.ReadAsStringAsync();
        Assert.True(exportRespuesta.StatusCode == HttpStatusCode.OK, cuerpoError);
        Assert.Equal(ContentTypeXlsx, exportRespuesta.Content.Headers.ContentType?.MediaType);

        using var libro = new XLWorkbook(new MemoryStream(await exportRespuesta.Content.ReadAsByteArrayAsync()));
        var hoja = libro.Worksheets.First();

        // Fila 6 = título de tabla; los datos empiezan en la fila 7, mismo orden que el listado
        // JSON (newest-first, ver ServicioDeVentas.ConstruirQuery).
        const int primeraFilaDeDatos = 7;
        for (var i = 0; i < pagina.Items.Count; i++)
        {
            var item = pagina.Items[i];
            var fila = hoja.Row(primeraFilaDeDatos + i);
            Assert.Equal(item.NumeroVisible, fila.Cell(1).GetString());
            Assert.Equal(item.IdPuntoVenta, fila.Cell(3).GetValue<int>());
            Assert.Equal(item.IdCliente, fila.Cell(4).GetValue<int>());
            Assert.Equal(item.Estado.ToString(), fila.Cell(5).GetString());
            Assert.Equal(item.Total, fila.Cell(6).GetValue<decimal>());
        }
    }

    // ---- task 3.6: 403 para el rol excluido de OperacionDePos ------------------------------------

    [Fact]
    public async Task UnRootEsRechazadoDelExportDeVentas()
    {
        var ctx = await PrepararAsync(nameof(UnRootEsRechazadoDelExportDeVentas), fixture);
        var hoy = new DateOnly(2026, 8, 1);

        var respuesta = await LlamarExportAsync(ctx.Root, ctx.IdPuntoVenta, hoy, hoy);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    // ---- task 3.7: rechazo por tope (COUNT real, no una serie gap-filled) ------------------------

    [Fact]
    public async Task UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadReal()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 3)));

        var ctx = await PrepararAsync(nameof(UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadReal), factoryBajo);
        var dia = new DateOnly(2026, 8, 1);

        for (var i = 0; i < 4; i++)
        {
            await SembrarComprobanteAsync(ctx, dia, 100m + i);
        }

        var respuesta = await LlamarExportAsync(ctx.Admin, ctx.IdPuntoVenta, dia, dia);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exportacion_demasiado_grande", problema.GetProperty("codigo").GetString());
        Assert.Contains("4", problema.GetProperty("title").GetString());
    }

    // ---- task 3.8: backstop de carrera del `+1` (mutation-proof-tests) ---------------------------

    /// <summary>
    /// Simula la carrera que el <c>+1</c> de <c>.Take(topeDeFilas + 1)</c> existe para atrapar:
    /// un <c>COUNT(*)</c> que ve <c>tope</c> filas (pasa la primera <see cref="GuardaDeTope.Exigir"/>)
    /// seguido de una fila insertada ANTES de la lectura — sin coordinar dos requests HTTP reales
    /// (no reproducible de forma determinística), un <c>DbCommandInterceptor</c> intercepta la
    /// SEGUNDA consulta que toca <c>comprobantes_venta</c> (la lectura <c>.Take(tope + 1)</c>) e
    /// inserta la fila extra JUSTO ANTES de dejarla correr — mismo patrón de rendezvous que
    /// <c>ParametrosTests.InterceptorDeRendezVous</c>, aplicado acá a una carrera de un solo
    /// participante (el interceptor mismo hace de "segundo escritor").
    /// Mutación aplicada (design decisión 7, <c>ServicioDeVentas.ListarParaExportacionAsync</c>):
    /// <c>.Take(topeDeFilas + 1)</c> reemplazado por <c>.Take(topeDeFilas)</c> — esta prueba pasó
    /// de FALLAR (200 con 3 filas, el archivo truncado escapaba) a pasar al revertir. Evidencia
    /// registrada en el cuerpo del PR.
    /// </summary>
    [Fact]
    public async Task UnaFilaInsertadaEntreElConteoYLaLecturaSigueRechazandoLaExportacion()
    {
        var gate = new SemaphoreSlim(0, 1);
        Contexto? ctxRef = null;
        DateOnly dia = default;

        var interceptor = new InterceptorDeCarreraDeExportacion(async () =>
        {
            if (ctxRef is null)
            {
                return;
            }

            await SembrarComprobanteAsync(ctxRef, dia, 999m);
            gate.Release();
        });

        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 3);
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor));
            }));

        var ctx = await PrepararAsync(nameof(UnaFilaInsertadaEntreElConteoYLaLecturaSigueRechazandoLaExportacion), factoryBajo);
        dia = new DateOnly(2026, 8, 1);
        ctxRef = ctx;

        for (var i = 0; i < 3; i++)
        {
            await SembrarComprobanteAsync(ctx, dia, 100m + i);
        }

        var respuesta = await LlamarExportAsync(ctx.Admin, ctx.IdPuntoVenta, dia, dia);

        Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(10)), "El interceptor de carrera nunca insertó la fila extra.");
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exportacion_demasiado_grande", problema.GetProperty("codigo").GetString());
        Assert.Contains("4", problema.GetProperty("title").GetString());
    }

    /// <summary>Retiene la SEGUNDA consulta que toca <c>comprobantes_venta</c> (la lectura
    /// <c>.Take(tope + 1)</c> — la primera es el <c>COUNT(*)</c>) e inyecta <paramref
    /// name="alSegundaConsulta"/> antes de dejarla correr. Cubre tanto <c>ReaderExecutingAsync</c>
    /// como <c>ScalarExecutingAsync</c>: si <c>CountAsync</c> se traduce a un escalar en vez de un
    /// reader, el contador compartido sigue contando en orden.</summary>
    private sealed class InterceptorDeCarreraDeExportacion(Func<Task> alSegundaConsulta) : DbCommandInterceptor
    {
        private int _coincidencias;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await ConsiderarAsync(command);
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            await ConsiderarAsync(command);
            return await base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        private async Task ConsiderarAsync(DbCommand command)
        {
            if (!command.CommandText.Contains("comprobantes_venta", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (Interlocked.Increment(ref _coincidencias) == 2)
            {
                await alSegundaConsulta();
            }
        }
    }
}
