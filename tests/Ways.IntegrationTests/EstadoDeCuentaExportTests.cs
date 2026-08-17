using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.CuentaCorriente;
using Ways.Application.Exportacion;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.CuentaCorriente;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-11-exportacion-reportes, Slice 3: <c>GET /api/clientes/{id}/cuenta-corriente/export</c>
/// — mismo patrón de <c>ConstruirQuery</c> compartido que <c>VentasListadoExportTests</c>. Sin
/// <c>histórico</c>: un export es por definición un rango acotado (design decisión 7), lo que
/// <c>histórico</c> deliberadamente evita, así que la ruta lo excluye y <c>desde</c>/<c>hasta</c>
/// son siempre obligatorios acá.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class EstadoDeCuentaExportTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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
        int IdTenant, int IdPuntoVenta, HttpClient Admin, HttpClient Root, int IdCliente, int IdEmpleadoAdmin);

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

        return new Contexto(resultado.IdTenant, resultado.IdPuntoVenta, admin, rootLogueado, idCliente, resultado.IdUsuarioAdmin);
    }

    /// <summary>Siembra un ajuste manual directo — sin pasar por <c>ServicioDeCuentaCorriente</c>,
    /// mismo criterio que las otras siembras directas de esta slice. Fecha fija a mediodía UTC.</summary>
    private async Task SembrarMovimientoAsync(Contexto ctx, DateOnly fecha, decimal importe, decimal saldoResultante)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var mediodia = new DateTimeOffset(fecha.Year, fecha.Month, fecha.Day, 12, 0, 0, TimeSpan.Zero);

        db.MovimientosCuentaCorriente.Add(new MovimientoCuentaCorriente
        {
            IdTenant = ctx.IdTenant,
            IdCliente = ctx.IdCliente,
            Fecha = mediodia,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdEmpleado = ctx.IdEmpleadoAdmin,
            Tipo = TipoMovimientoCc.Ajuste,
            Importe = importe,
            SaldoResultante = saldoResultante,
            Detalle = "Ajuste de prueba"
        });
        await db.SaveChangesAsync();
    }

    // judgment-day fix (Juez B, WARNING, residual cerrado): offset -03:00 REAL (no "Z") — así
    // revertir el call site de /cuenta-corriente/export al viejo
    // `DateOnly.FromDateTime(...UtcDateTime)` corre la fecha MOSTRADA (ver assert de nombre de
    // archivo en ElExportEsIgualAlEstadoDeCuentaJsonParaLosMismosParametros), algo que un offset
    // "Z" nunca discrimina.
    private static string ConstruirQuery(DateOnly desde, DateOnly hasta, string? formato) =>
        $"desde={desde:yyyy-MM-dd}T00:00:00-03:00&hasta={hasta:yyyy-MM-dd}T23:59:59-03:00" +
        (formato is null ? string.Empty : $"&formato={formato}");

    private static Task<HttpResponseMessage> LlamarEstadoDeCuentaAsync(
        HttpClient cliente, int idCliente, DateOnly desde, DateOnly hasta) =>
        cliente.GetAsync($"/api/clientes/{idCliente}/cuenta-corriente?{ConstruirQuery(desde, hasta, null)}");

    private static Task<HttpResponseMessage> LlamarExportAsync(
        HttpClient cliente, int idCliente, DateOnly desde, DateOnly hasta, string formato = "xlsx") =>
        cliente.GetAsync($"/api/clientes/{idCliente}/cuenta-corriente/export?{ConstruirQuery(desde, hasta, formato)}");

    // ---- task 3.5: la exportación es igual al estado de cuenta JSON -----------------------------

    [Fact]
    public async Task ElExportEsIgualAlEstadoDeCuentaJsonParaLosMismosParametros()
    {
        var ctx = await PrepararAsync(nameof(ElExportEsIgualAlEstadoDeCuentaJsonParaLosMismosParametros), fixture);
        var desde = new DateOnly(2026, 8, 1);
        var hasta = new DateOnly(2026, 8, 2);

        await SembrarMovimientoAsync(ctx, desde, 500m, 500m);
        await SembrarMovimientoAsync(ctx, hasta, -200m, 300m);

        var jsonRespuesta = await LlamarEstadoDeCuentaAsync(ctx.Admin, ctx.IdCliente, desde, hasta);
        Assert.Equal(HttpStatusCode.OK, jsonRespuesta.StatusCode);
        var estado = JsonSerializer.Deserialize<EstadoDeCuenta>(await jsonRespuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        Assert.Equal(2, estado.Movimientos.Count);

        var exportRespuesta = await LlamarExportAsync(ctx.Admin, ctx.IdCliente, desde, hasta);
        var cuerpoError = exportRespuesta.IsSuccessStatusCode ? string.Empty : await exportRespuesta.Content.ReadAsStringAsync();
        Assert.True(exportRespuesta.StatusCode == HttpStatusCode.OK, cuerpoError);
        Assert.Equal(ContentTypeXlsx, exportRespuesta.Content.Headers.ContentType?.MediaType);

        // judgment-day fix (Juez B, WARNING, residual cerrado): nombre de archivo derivado de
        // FechaDelRango.De — con el offset -03:00 real de ConstruirQuery, revertir el call site de
        // CuentaCorrienteEndpoints.cs al viejo `DateOnly.FromDateTime(hasta.UtcDateTime)` correría
        // `hasta` un día (23:59:59-03:00 cae en 02:59:59Z del día siguiente) y este assert lo atrapa.
        var nombreEsperado = NombreDeArchivo.Construir("estado_de_cuenta", $"cliente{ctx.IdCliente}", desde, hasta);
        var disposicionExport = exportRespuesta.Content.Headers.ContentDisposition?.ToString() ?? string.Empty;
        Assert.Contains($"filename=\"{nombreEsperado}\"", disposicionExport);

        using var libro = new XLWorkbook(new MemoryStream(await exportRespuesta.Content.ReadAsByteArrayAsync()));
        var hoja = libro.Worksheets.First();

        // Fila 6 = título de tabla (mutation-proof-tests regla 8): el header es lo que ata cada
        // celda de datos a su columna, sin este assert un swap de labels pasa inadvertido porque
        // el test de igualdad de abajo solo lee celdas por posición.
        const int filaDeEncabezados = 6;
        Assert.Equal(
            ["Fecha", "Tipo", "Importe", "Saldo", "Detalle"],
            Enumerable.Range(1, 5).Select(c => hoja.Cell(filaDeEncabezados, c).GetString()));

        var zona = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");
        const int primeraFilaDeDatos = 7;
        for (var i = 0; i < estado.Movimientos.Count; i++)
        {
            var movimiento = estado.Movimientos[i];
            var fila = hoja.Row(primeraFilaDeDatos + i);
            Assert.Equal(TimeZoneInfo.ConvertTime(movimiento.Fecha, zona).DateTime, fila.Cell(1).GetValue<DateTime>());
            Assert.Equal(movimiento.Tipo.ToString(), fila.Cell(2).GetString());
            Assert.Equal(movimiento.Importe, fila.Cell(3).GetValue<decimal>());
            Assert.Equal(movimiento.SaldoResultante, fila.Cell(4).GetValue<decimal>());
            Assert.Equal(movimiento.Detalle, fila.Cell(5).GetString());
        }
    }

    // ---- task 3.6: 403 para el rol excluido de OperacionDePos ------------------------------------

    [Fact]
    public async Task UnRootEsRechazadoDelExportDeEstadoDeCuenta()
    {
        var ctx = await PrepararAsync(nameof(UnRootEsRechazadoDelExportDeEstadoDeCuenta), fixture);
        var hoy = new DateOnly(2026, 8, 1);

        var respuesta = await LlamarExportAsync(ctx.Root, ctx.IdCliente, hoy, hoy);

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
        var saldo = 0m;

        for (var i = 0; i < 5; i++)
        {
            saldo += 100m;
            await SembrarMovimientoAsync(ctx, dia, 100m, saldo);
        }

        var respuesta = await LlamarExportAsync(ctx.Admin, ctx.IdCliente, dia, dia);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exportacion_demasiado_grande", problema.GetProperty("codigo").GetString());
        Assert.Contains("tiene 5 filas", problema.GetProperty("title").GetString());
    }

    // ---- FormatoDeExportacion.Parsear en esta ruta (barrido de gaps compartidos) ------------------

    /// <summary>Sin este test, borrar la llamada a <see cref="FormatoDeExportacion.Parsear"/> en
    /// <c>/api/clientes/{id}/cuenta-corriente/export</c> sobrevive — un <c>formato=pdf</c>
    /// devolvería 200 XLSX en vez de 400.</summary>
    [Fact]
    public async Task UnFormatoNoSoportadoRechazaConProblemDetailsEnElExportDeEstadoDeCuenta()
    {
        var ctx = await PrepararAsync(nameof(UnFormatoNoSoportadoRechazaConProblemDetailsEnElExportDeEstadoDeCuenta), fixture);
        var hoy = new DateOnly(2026, 8, 1);

        var respuesta = await LlamarExportAsync(ctx.Admin, ctx.IdCliente, hoy, hoy, formato: "pdf");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("formato_no_soportado", problema.GetProperty("codigo").GetString());
    }

    // ---- exportar exactamente el tope de filas es legítimo (barrido de gaps compartidos) ----------

    /// <summary>Discriminador real del SEGUNDO <c>GuardaDeTope.Exigir</c> del lado del ÉXITO: sin
    /// este test, mutar ese segundo <c>Exigir</c> a <c>Exigir(filas.Count, tope - 1)</c> sobrevive
    /// — <c>UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadReal</c> solo cubre el rechazo por
    /// ARRIBA del tope. Acá se exportan EXACTAMENTE <c>tope</c> movimientos y se espera 200 con el
    /// workbook completo. El servicio no antepone fila de saldo inicial ni agrega totales
    /// (<c>ObtenerEstadoDeCuentaParaExportacionAsync</c> mapea 1:1 <c>filas → Movimientos</c>), así
    /// que tope=3 movimientos son exactamente 3 filas de datos.</summary>
    [Fact]
    public async Task UnaExportacionDeExactamenteElTopeDeFilasSeAceptaCompleta()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 3)));

        var ctx = await PrepararAsync(nameof(UnaExportacionDeExactamenteElTopeDeFilasSeAceptaCompleta), factoryBajo);
        var dia = new DateOnly(2026, 8, 1);
        var saldo = 0m;

        for (var i = 0; i < 3; i++)
        {
            saldo += 100m;
            await SembrarMovimientoAsync(ctx, dia, 100m, saldo);
        }

        var respuesta = await LlamarExportAsync(ctx.Admin, ctx.IdCliente, dia, dia);
        var cuerpoError = respuesta.IsSuccessStatusCode ? string.Empty : await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpoError);
        Assert.Equal(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        using var libro = new XLWorkbook(new MemoryStream(await respuesta.Content.ReadAsByteArrayAsync()));
        var hoja = libro.Worksheets.First();

        // Encabezado en la fila 6, datos desde la 7 (mismo layout que
        // ElExportEsIgualAlEstadoDeCuentaJsonParaLosMismosParametros): las tope=3 filas ocupan
        // 7-9, la fila 10 debe quedar vacía.
        const int primeraFilaDeDatos = 7;
        for (var i = 0; i < 3; i++)
        {
            Assert.False(hoja.Row(primeraFilaDeDatos + i).IsEmpty());
        }
        Assert.True(hoja.Row(primeraFilaDeDatos + 3).IsEmpty());
    }
}
