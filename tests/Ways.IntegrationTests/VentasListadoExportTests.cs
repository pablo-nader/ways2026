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
    private async Task<int> SembrarComprobanteAsync(Contexto ctx, DateOnly fecha, decimal total)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var mediodia = new DateTimeOffset(fecha.Year, fecha.Month, fecha.Day, 12, 0, 0, TimeSpan.Zero);

        var comprobante = new ComprobanteVenta
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
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        return comprobante.Id;
    }

    private static string ConstruirQuery(int idPuntoVenta, DateOnly desde, DateOnly hasta, string? formato, EstadoComprobante? estado = null) =>
        $"idPuntoVenta={idPuntoVenta}&desde={desde:yyyy-MM-dd}T00:00:00Z&hasta={hasta:yyyy-MM-dd}T23:59:59Z" +
        (formato is null ? string.Empty : $"&formato={formato}") +
        (estado is null ? string.Empty : $"&estado={estado}");

    private static Task<HttpResponseMessage> LlamarListadoAsync(
        HttpClient cliente, int idPuntoVenta, DateOnly desde, DateOnly hasta, EstadoComprobante? estado = null) =>
        cliente.GetAsync($"/api/ventas?{ConstruirQuery(idPuntoVenta, desde, hasta, null, estado)}&tamanio=200");

    private static Task<HttpResponseMessage> LlamarExportAsync(
        HttpClient cliente, int idPuntoVenta, DateOnly desde, DateOnly hasta, string formato = "xlsx", EstadoComprobante? estado = null) =>
        cliente.GetAsync($"/api/ventas/export?{ConstruirQuery(idPuntoVenta, desde, hasta, formato, estado)}");

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

        // Fila 6 = título de tabla (mutation-proof-tests regla 8): el header es lo que ata cada
        // celda de datos a su columna, sin este assert un swap de labels pasa inadvertido porque
        // el test de igualdad de abajo solo lee celdas por posición.
        const int filaDeEncabezados = 6;
        Assert.Equal(
            ["Número", "Fecha", "Punto de venta", "Cliente", "Estado", "Total"],
            Enumerable.Range(1, 6).Select(c => hoja.Cell(filaDeEncabezados, c).GetString()));

        // Los datos empiezan en la fila 7, mismo orden que el listado JSON (newest-first, ver
        // ServicioDeVentas.ConstruirQuery).
        var zona = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");
        const int primeraFilaDeDatos = 7;
        for (var i = 0; i < pagina.Items.Count; i++)
        {
            var item = pagina.Items[i];
            var fila = hoja.Row(primeraFilaDeDatos + i);
            Assert.Equal(item.NumeroVisible, fila.Cell(1).GetString());
            Assert.Equal(TimeZoneInfo.ConvertTime(item.Fecha, zona).DateTime, fila.Cell(2).GetValue<DateTime>());
            Assert.Equal(item.IdPuntoVenta, fila.Cell(3).GetValue<int>());
            Assert.Equal(item.IdCliente, fila.Cell(4).GetValue<int>());
            Assert.Equal(item.Estado.ToString(), fila.Cell(5).GetString());
            Assert.Equal(item.Total, fila.Cell(6).GetValue<decimal>());
        }
    }

    /// <summary>
    /// Cubre el filtro <c>estado</c> compartido por <c>ServicioDeVentas.ConstruirQuery</c> — sin
    /// esta prueba, borrar el bloque <c>if (estado is { } e)</c> no lo detecta ninguna de las
    /// otras equality tests (ninguna filtra por estado). Mutación aplicada: borrar el bloque del
    /// filtro de estado en <c>ConstruirQuery</c> → esta prueba pasa de FALLAR (el anulado aparece
    /// en ambas respuestas) a pasar al revertir.
    /// </summary>
    [Fact]
    public async Task ElFiltroDeEstadoExcluyeElAnuladoTantoEnElListadoComoEnElExport()
    {
        var ctx = await PrepararAsync(nameof(ElFiltroDeEstadoExcluyeElAnuladoTantoEnElListadoComoEnElExport), fixture);
        var dia = new DateOnly(2026, 8, 1);

        await SembrarComprobanteAsync(ctx, dia, 100m);
        var idAnulado = await SembrarComprobanteAsync(ctx, dia, 200m);
        var anulacion = await ctx.Admin.PostAsync($"/api/ventas/{idAnulado}/anulacion", null);
        Assert.Equal(HttpStatusCode.OK, anulacion.StatusCode);

        var jsonRespuesta = await LlamarListadoAsync(ctx.Admin, ctx.IdPuntoVenta, dia, dia, EstadoComprobante.Emitido);
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var pagina = JsonSerializer.Deserialize<PaginaDeVentas>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        Assert.Single(pagina.Items);
        Assert.DoesNotContain(pagina.Items, i => i.Id == idAnulado);

        var exportRespuesta = await LlamarExportAsync(ctx.Admin, ctx.IdPuntoVenta, dia, dia, estado: EstadoComprobante.Emitido);
        Assert.Equal(HttpStatusCode.OK, exportRespuesta.StatusCode);

        using var libro = new XLWorkbook(new MemoryStream(await exportRespuesta.Content.ReadAsByteArrayAsync()));
        var hoja = libro.Worksheets.First();
        Assert.Equal(pagina.Items[0].NumeroVisible, hoja.Row(7).Cell(1).GetString());
        Assert.Empty(hoja.Row(8).Cell(1).GetString());
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
            await SembrarComprobanteAsync(ctx, dia, 100m + i);
        }

        var respuesta = await LlamarExportAsync(ctx.Admin, ctx.IdPuntoVenta, dia, dia);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exportacion_demasiado_grande", problema.GetProperty("codigo").GetString());
        Assert.Contains("tiene 5 filas", problema.GetProperty("title").GetString());
    }

    // ---- FormatoDeExportacion.Parsear en esta ruta (barrido de gaps compartidos) ------------------

    /// <summary>Sin este test, borrar la llamada a <see cref="FormatoDeExportacion.Parsear"/> en
    /// <c>/api/ventas/export</c> sobrevive — un <c>formato=pdf</c> devolvería 200 XLSX en vez de
    /// 400.</summary>
    [Fact]
    public async Task UnFormatoNoSoportadoRechazaConProblemDetailsEnElExportDeVentas()
    {
        var ctx = await PrepararAsync(nameof(UnFormatoNoSoportadoRechazaConProblemDetailsEnElExportDeVentas), fixture);
        var hoy = new DateOnly(2026, 8, 1);

        var respuesta = await LlamarExportAsync(ctx.Admin, ctx.IdPuntoVenta, hoy, hoy, formato: "pdf");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("formato_no_soportado", problema.GetProperty("codigo").GetString());
    }

    // ---- exportar exactamente el tope de filas es legítimo (barrido de gaps compartidos) ----------

    /// <summary>Discriminador real del SEGUNDO <c>GuardaDeTope.Exigir</c> del lado del ÉXITO: sin
    /// este test, mutar ese segundo <c>Exigir</c> a <c>Exigir(crudos.Count, tope - 1)</c> sobrevive
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
            await SembrarComprobanteAsync(ctx, dia, 100m + i);
        }

        var respuesta = await LlamarExportAsync(ctx.Admin, ctx.IdPuntoVenta, dia, dia);
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
        Assert.Contains("tiene 4 filas", problema.GetProperty("title").GetString());
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
