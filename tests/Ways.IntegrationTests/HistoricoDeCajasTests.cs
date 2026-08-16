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
using Ways.Application.Usuarios;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-11-exportacion-reportes, Slice 5a (design: G2/G3 — minimal aggregation; spec
/// historico-de-cajas: G2 Histórico Lists Closed Turnos Only, With Totals From Persisted
/// Arqueos): <c>GET /api/reportes/cajas</c> — la casa de las 4 pruebas (cruce de tenant, ausencia
/// de turno abierto con evidencia de mutación, discriminación por punto de venta, fixture
/// hand-computed) más el rol un escalón debajo del gate.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class HistoricoDeCajasTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";

    // Sin siembra de fechas propias en este archivo (apertura/cierre las derivan del reloj del
    // servidor, nunca de un DateTimeOffset del test) — el riesgo de ventana 00-03 UTC
    // (fix/tests-reportes-ventana-utc) no aplica: nada acá bucketea por fecha de cliente.

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdEmpleadoAdmin, int IdCliente, int IdTipoComprobanteTx,
        int IdMedioEfectivo, HttpClient Admin, HttpClient Supervisor, HttpClient Vendedor);

    /// <summary>Igual que <c>ReportesVentasResumenExportTests.PrepararAsync</c>: <paramref
    /// name="factory"/> permite a las pruebas de tope (Slice 5b, export de <c>/cajas</c>) usar un
    /// <c>WithWebHostBuilder</c> propio con <see cref="OpcionesDeExportacion.TopeDeFilas"/>
    /// pisado, sin afectar al resto de esta clase (que sigue usando <c>fixture</c> directo).
    /// </summary>
    private async Task<Contexto> PrepararAsync(string nombre, WebApplicationFactory<Program>? factory = null)
    {
        factory ??= fixture;

        using var root = factory.CreateClient();
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

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();
        var idCliente = await db.Clientes.Select(c => c.Id).FirstAsync();
        var idTipoComprobanteTx = await db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).FirstAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, idCliente,
            idTipoComprobanteTx, idMedioEfectivo, admin, supervisor, vendedor);
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

    private async Task<int> SembrarPuntoVentaAsync(Contexto ctx, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var puntoVenta = new PuntoVenta { IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();

        return puntoVenta.Id;
    }

    private static async Task<TurnoResumen> AbrirTurnoAsync(Contexto ctx, int idPuntoVenta, decimal fondoInicial = 0m) =>
        (await (await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(idPuntoVenta, fondoInicial, "Apertura de prueba")))
            .Content.ReadFromJsonAsync<TurnoResumen>(OpcionesJson))!;

    private static async Task<TurnoConArqueos> CerrarTurnoAsync(
        Contexto ctx, int idTurno, IReadOnlyList<ConteoDeclarado> conteos)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{idTurno}/cierre", new SolicitudDeCierre(conteos, "Cierre de prueba histórico"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        return JsonSerializer.Deserialize<TurnoConArqueos>(cuerpo, OpcionesJson)!;
    }

    private static Task<PaginaDeHistoricoDeCajas?> ListarAsync(HttpClient cliente, int? idPuntoVenta = null) =>
        cliente.GetFromJsonAsync<PaginaDeHistoricoDeCajas>(
            idPuntoVenta is { } pv ? $"/api/reportes/cajas?idPuntoVenta={pv}" : "/api/reportes/cajas", OpcionesJson);

    private long _numeroSecuencial = 1;

    /// <summary>Siembra directo un pago (comprobante + pago) — mismo criterio que
    /// <c>CajaCierreEndpointsTests.SembrarPagoAsync</c>, necesario para que el medio quede
    /// "con actividad" y sea arqueable (<c>CalculadorDeArqueo</c>: <c>TuvoFilas</c>).</summary>
    private async Task SembrarPagoAsync(Contexto ctx, int idTurno, int idMedioPago, decimal importe)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var comprobante = new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant,
            IdTipoComprobante = ctx.IdTipoComprobanteTx,
            Numero = Interlocked.Increment(ref _numeroSecuencial),
            Fecha = ahora,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdTurnoCaja = idTurno,
            IdEmpleado = ctx.IdEmpleadoAdmin,
            IdCliente = ctx.IdCliente,
            Subtotal = importe,
            DescuentoTotal = 0m,
            Total = importe,
            Estado = EstadoComprobante.Emitido,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        db.PagosComprobante.Add(new PagoComprobante
        {
            IdTenant = ctx.IdTenant,
            IdComprobanteVenta = comprobante.Id,
            IdMedioPago = idMedioPago,
            Importe = importe,
            Vuelto = 0m,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    // ---- task 5a.7: 4-test pattern -----------------------------------------------------------

    [Fact]
    public async Task UnTurnoCerradoDeOtroTenantNuncaApareceEnElListado()
    {
        var ctxA = await PrepararAsync(nameof(UnTurnoCerradoDeOtroTenantNuncaApareceEnElListado) + "A");
        var ctxB = await PrepararAsync(nameof(UnTurnoCerradoDeOtroTenantNuncaApareceEnElListado) + "B");

        var turnoB = await AbrirTurnoAsync(ctxB, ctxB.IdPuntoVenta, 100m);
        await CerrarTurnoAsync(ctxB, turnoB.Id, [new ConteoDeclarado(ctxB.IdMedioEfectivo, 100m)]);

        var listadoDeA = await ListarAsync(ctxA.Admin);

        Assert.NotNull(listadoDeA);
        Assert.DoesNotContain(listadoDeA!.Items, f => f.IdTurnoCaja == turnoB.Id);
    }

    [Fact]
    public async Task ElFiltroPorPuntoVentaExcluyeTurnosCerradosDeOtroPuntoDeVenta()
    {
        var ctx = await PrepararAsync(nameof(ElFiltroPorPuntoVentaExcluyeTurnosCerradosDeOtroPuntoDeVenta));
        var otroPuntoVenta = await SembrarPuntoVentaAsync(ctx, "PV secundario");

        var turnoPv1 = await AbrirTurnoAsync(ctx, ctx.IdPuntoVenta, 100m);
        await CerrarTurnoAsync(ctx, turnoPv1.Id, [new ConteoDeclarado(ctx.IdMedioEfectivo, 100m)]);

        var turnoPv2 = await AbrirTurnoAsync(ctx, otroPuntoVenta, 200m);
        await CerrarTurnoAsync(ctx, turnoPv2.Id, [new ConteoDeclarado(ctx.IdMedioEfectivo, 200m)]);

        var listadoFiltrado = await ListarAsync(ctx.Admin, ctx.IdPuntoVenta);

        Assert.NotNull(listadoFiltrado);
        Assert.Contains(listadoFiltrado!.Items, f => f.IdTurnoCaja == turnoPv1.Id);
        Assert.DoesNotContain(listadoFiltrado.Items, f => f.IdTurnoCaja == turnoPv2.Id);
    }

    /// <summary>task 5a.8 (mutation-proof-tests): seed un turno <c>abierto</c> y uno
    /// <c>cerrado</c> para el MISMO punto de venta — el listado tiene que excluir el conjunto de
    /// filas del abierto, no solo el conteo. Mutación aplicada (reemplazar
    /// <c>Where(t => t.Estado == EstadoTurno.Cerrado)</c> por <c>AsQueryable()</c> en
    /// <see cref="ServicioDeHistoricoDeCajas.ListarCierresAsync"/>): esta prueba pasó de FALLAR
    /// (500 — el turno abierto entra a la proyección, y <c>FechaCierre!.Value</c> revienta con
    /// <c>NullReferenceException</c> porque un turno abierto nunca tiene <c>fecha_cierre</c>) a
    /// pasar al revertir — evidencia registrada en el cuerpo del commit (esta rama no abre PR).
    /// </summary>
    [Fact]
    public async Task UnTurnoAbiertoQuedaExcluidoDelListadoJuntoAUnoCerradoDelMismoPuntoDeVenta()
    {
        var ctx = await PrepararAsync(nameof(UnTurnoAbiertoQuedaExcluidoDelListadoJuntoAUnoCerradoDelMismoPuntoDeVenta));

        var turnoCerrado = await AbrirTurnoAsync(ctx, ctx.IdPuntoVenta, 100m);
        await CerrarTurnoAsync(ctx, turnoCerrado.Id, [new ConteoDeclarado(ctx.IdMedioEfectivo, 100m)]);

        var turnoAbierto = await AbrirTurnoAsync(ctx, await SembrarPuntoVentaAsync(ctx, "PV para turno abierto"), 50m);

        var listado = await ListarAsync(ctx.Admin);

        Assert.NotNull(listado);
        Assert.DoesNotContain(listado!.Items, f => f.IdTurnoCaja == turnoAbierto.Id);
        var fila = Assert.Single(listado.Items, f => f.IdTurnoCaja == turnoCerrado.Id);
        Assert.Equal(100m, fila.Esperado);
        Assert.Equal(100m, fila.Declarado);
        Assert.Equal(0m, fila.Diferencia);
    }

    /// <summary>task 5a.7 (hand-computed fixture equality): esperado/declarado/diferencia del
    /// listado tienen que ser la Σ EXACTA de las filas de <c>arqueos_turno</c> que el cierre
    /// acaba de persistir — un turno con dos medios (efectivo con diferencia, tarjeta exacto).
    /// </summary>
    [Fact]
    public async Task LosTotalesDelListadoSonLaSumaExactaDeLosArqueosPersistidos()
    {
        var ctx = await PrepararAsync(nameof(LosTotalesDelListadoSonLaSumaExactaDeLosArqueosPersistidos));
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var idMedioTarjeta = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Electronico).Select(m => m.Id).FirstAsync();

        var turno = await AbrirTurnoAsync(ctx, ctx.IdPuntoVenta, 500m);
        await SembrarPagoAsync(ctx, turno.Id, idMedioTarjeta, 200m);

        // esperado efectivo (ancla) = fondo inicial (500) — declarado 470 ⇒ diferencia +30
        // (faltante). Esperado tarjeta = 200 (pago), declarado exacto ⇒ diferencia 0.
        await CerrarTurnoAsync(
            ctx, turno.Id,
            [new ConteoDeclarado(ctx.IdMedioEfectivo, 470m), new ConteoDeclarado(idMedioTarjeta, 200m)]);

        var listado = await ListarAsync(ctx.Admin);

        Assert.NotNull(listado);
        var fila = Assert.Single(listado!.Items, f => f.IdTurnoCaja == turno.Id);
        Assert.Equal(700m, fila.Esperado); // 500 (efectivo) + 200 (tarjeta)
        Assert.Equal(670m, fila.Declarado); // 470 (efectivo) + 200 (tarjeta)
        Assert.Equal(30m, fila.Diferencia); // (500-470) + (200-200)
    }

    // ---- task 5a.10: rol un escalón debajo del gate -----------------------------------------

    [Fact]
    public async Task UnVendedorEsRechazadoDelHistoricoListado()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoDelHistoricoListado));

        var respuesta = await ctx.Vendedor.GetAsync("/api/reportes/cajas");

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnSupervisorLeeElHistoricoListado()
    {
        var ctx = await PrepararAsync(nameof(UnSupervisorLeeElHistoricoListado));

        var respuesta = await ctx.Supervisor.GetAsync("/api/reportes/cajas");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    // ---- task 5a.9 / 5a.10 (mitad export, diferida de Slice 5a): GET /api/reportes/cajas/export --

    private const string ContentTypeXlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static Task<HttpResponseMessage> LlamarExportDelHistoricoAsync(
        HttpClient cliente, DateTimeOffset desde, DateTimeOffset hasta, int? idPuntoVenta = null, string formato = "xlsx") =>
        cliente.GetAsync(
            $"/api/reportes/cajas/export?desde={Uri.EscapeDataString(desde.ToString("O"))}" +
            $"&hasta={Uri.EscapeDataString(hasta.ToString("O"))}" +
            (idPuntoVenta is { } pv ? $"&idPuntoVenta={pv}" : string.Empty) + $"&formato={formato}");

    /// <summary>task 5a.9 (spec: G2 Listing Export Figures Equal The JSON Listing): compara las
    /// 8 columnas del workbook (Turno, Punto de venta, Apertura, Cierre, Esperado, Declarado,
    /// Diferencia, Retiros) contra <see cref="FilaDeHistoricoDeCajas"/> TURNO POR TURNO (no solo
    /// la suma) — un turno ausente del export o desviado en cualquiera de sus columnas queda
    /// expuesto aunque el total combinado coincida por casualidad.</summary>
    [Fact]
    public async Task ElExportDelHistoricoEsIgualAlListadoJsonTurnoPorTurno()
    {
        var ctx = await PrepararAsync(nameof(ElExportDelHistoricoEsIgualAlListadoJsonTurnoPorTurno));

        var turnoUno = await AbrirTurnoAsync(ctx, ctx.IdPuntoVenta, 500m);
        await CerrarTurnoAsync(ctx, turnoUno.Id, [new ConteoDeclarado(ctx.IdMedioEfectivo, 470m)]);

        var turnoDos = await AbrirTurnoAsync(ctx, ctx.IdPuntoVenta, 300m);
        await CerrarTurnoAsync(ctx, turnoDos.Id, [new ConteoDeclarado(ctx.IdMedioEfectivo, 300m)]);

        var listado = await ListarAsync(ctx.Admin);
        Assert.NotNull(listado);
        Assert.Equal(2, listado!.Items.Count);

        var ahora = DateTimeOffset.UtcNow;
        var respuesta = await LlamarExportDelHistoricoAsync(ctx.Admin, ahora.AddMinutes(-5), ahora.AddMinutes(5));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        using var libro = new XLWorkbook(new MemoryStream(await respuesta.Content.ReadAsByteArrayAsync()));
        var hoja = libro.Worksheets.First();

        // Fila 6 = título de tabla (mutation-proof-tests regla 8): el header es lo que ata cada
        // celda de datos a su columna, sin este assert un swap de labels pasa inadvertido.
        Assert.Equal(
            ["Turno", "Punto de venta", "Apertura", "Cierre", "Esperado", "Declarado", "Diferencia", "Retiros"],
            Enumerable.Range(1, 8).Select(c => hoja.Cell(6, c).GetString()));

        // Las 8 columnas completas, no solo Diferencia — Apertura/Cierre se comparan
        // zone-converted (mismo patrón que VentasListadoExportTests) porque el mapper convierte
        // el DateTimeOffset a America/Argentina/Buenos_Aires antes de escribir la celda.
        var zona = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");
        const int primeraFilaDeDatos = 7;
        var filasPorTurno = new Dictionary<int, (
            int IdPuntoVenta, DateTime Apertura, DateTime Cierre, decimal Esperado, decimal Declarado,
            decimal Diferencia, decimal Retiros)>();
        for (var fila = primeraFilaDeDatos; !hoja.Cell(fila, 1).Value.IsBlank; fila++)
        {
            filasPorTurno[hoja.Cell(fila, 1).GetValue<int>()] = (
                hoja.Cell(fila, 2).GetValue<int>(),
                hoja.Cell(fila, 3).GetValue<DateTime>(),
                hoja.Cell(fila, 4).GetValue<DateTime>(),
                hoja.Cell(fila, 5).GetValue<decimal>(),
                hoja.Cell(fila, 6).GetValue<decimal>(),
                hoja.Cell(fila, 7).GetValue<decimal>(),
                hoja.Cell(fila, 8).GetValue<decimal>());
        }

        foreach (var item in listado.Items)
        {
            Assert.True(
                filasPorTurno.TryGetValue(item.IdTurnoCaja, out var fila),
                $"Falta el turno {item.IdTurnoCaja} en el export.");
            Assert.Equal(item.IdPuntoVenta, fila.IdPuntoVenta);
            // Truncado a segundos: el double de OLE Automation date que ClosedXML persiste no
            // retiene la precisión de microsegundos de Postgres — sin el truncado, la comparación
            // flakearía por redondeo del formato xlsx, no por un bug real (mismo criterio que el
            // comentario de PreciosEndpointsTests sobre no comparar precisión distinta).
            Assert.Equal(TruncarASegundos(TimeZoneInfo.ConvertTime(item.FechaApertura, zona).DateTime), TruncarASegundos(fila.Apertura));
            Assert.Equal(TruncarASegundos(TimeZoneInfo.ConvertTime(item.FechaCierre, zona).DateTime), TruncarASegundos(fila.Cierre));
            Assert.Equal(item.Esperado, fila.Esperado);
            Assert.Equal(item.Declarado, fila.Declarado);
            Assert.Equal(item.Diferencia, fila.Diferencia);
            Assert.Equal(item.Egresos.Retiros, fila.Retiros);
        }
    }

    private static DateTime TruncarASegundos(DateTime valor) => valor.AddTicks(-(valor.Ticks % TimeSpan.TicksPerSecond));

    [Fact]
    public async Task UnVendedorEsRechazadoDelExportDelHistorico()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoDelExportDelHistorico));
        var ahora = DateTimeOffset.UtcNow;

        var respuesta = await LlamarExportDelHistoricoAsync(ctx.Vendedor, ahora.AddMinutes(-5), ahora.AddMinutes(5));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    /// <summary>Cap guard del export del histórico — a diferencia del resto de los reportes
    /// agregados de la etapa (design decisión 6), un turno NO es un catálogo acotado: reusa el
    /// mismo patrón Contar → rechazar de <see cref="ServicioDeHistoricoDeCajas.ListarCierresParaExportacionAsync"/>
    /// (design decisión 7). La cláusula (<c>GuardaDeTope.Exigir</c>) ya tiene evidencia de mutación
    /// registrada en Slice 1b (función compartida, reusada tal cual acá) — esta prueba cubre el
    /// comportamiento end-to-end del nuevo llamador, no repite la mutación de la cláusula
    /// compartida.</summary>
    [Fact]
    public async Task UnaExportacionDelHistoricoQueSuperaElTopeSeRechazaConLaCantidadReal()
    {
        using var factoryBajo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<OpcionesDeExportacion>(o => o.TopeDeFilas = 3)));

        var ctx = await PrepararAsync(nameof(UnaExportacionDelHistoricoQueSuperaElTopeSeRechazaConLaCantidadReal), factoryBajo);

        for (var i = 0; i < 4; i++)
        {
            var turno = await AbrirTurnoAsync(ctx, ctx.IdPuntoVenta, 100m);
            await CerrarTurnoAsync(ctx, turno.Id, [new ConteoDeclarado(ctx.IdMedioEfectivo, 100m)]);
        }

        var ahora = DateTimeOffset.UtcNow;
        var respuesta = await LlamarExportDelHistoricoAsync(ctx.Admin, ahora.AddMinutes(-5), ahora.AddMinutes(5));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.NotEqual(ContentTypeXlsx, respuesta.Content.Headers.ContentType?.MediaType);

        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("exportacion_demasiado_grande", problema.GetProperty("codigo").GetString());
        Assert.Contains("4", problema.GetProperty("title").GetString());
    }
}
