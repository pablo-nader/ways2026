using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Parametros;
using Ways.Application.Reportes;
using Ways.Application.Usuarios;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-10-agregacion-dashboard, Slice 10 (task 10.4): <c>GET /api/reportes/comisiones</c> punta
/// a punta — PROVISIONAL, droppable en su totalidad (spec rentabilidad-y-comisiones: Comisiones Is
/// A Provisional, Non-Persisted Report). Reusa el mismo <c>ConsultarPorVendedorAsync</c> ya probado
/// por <see cref="ReportesVentasPorDimensionTests"/> (cross-tenant/soft-delete/anulado son
/// cobertura ORDINARIA acá, mismo criterio que esa clase), así que el patrón de 4 pruebas se centra
/// en la pieza propia de este reporte: la resolución de <c>comision_porcentaje</c> y su
/// multiplicación — más la garantía de que nada se escribe.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ReportesComisionesTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new() { PropertyNameCaseInsensitive = true };

    private static long _numeroSecuencial = 1;

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, HttpClient Supervisor, HttpClient Vendedor,
        HttpClient Root, int IdCliente, int IdEmpleadoAdmin);

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
        var idCliente = await dbTenant.Clientes.Select(c => c.Id).FirstAsync();

        await using var dbPlataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idTipoComprobanteTx = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).SingleAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, supervisor, vendedor, root,
            idCliente, resultado.IdUsuarioAdmin);
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
    /// <c>ReportesVentasPorDimensionTests.SembrarComprobanteAsync</c>.</summary>
    private async Task<int> SembrarComprobanteAsync(
        Contexto ctx, int idTipoComprobante, DateTimeOffset fecha, decimal total, int? idEmpleado = null,
        EstadoComprobante estado = EstadoComprobante.Emitido, bool eliminado = false)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var comprobante = new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant,
            IdTipoComprobante = idTipoComprobante,
            Numero = Interlocked.Increment(ref _numeroSecuencial),
            Fecha = fecha,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdEmpleado = idEmpleado ?? ctx.IdEmpleadoAdmin,
            IdCliente = ctx.IdCliente,
            Subtotal = total,
            DescuentoTotal = 0m,
            Total = total,
            Estado = estado,
            CreatedAt = ahora,
            UpdatedAt = ahora,
            DeletedAt = eliminado ? ahora : null
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();
        return comprobante.Id;
    }

    private async Task<int> IdTipoComprobanteTxAsync(Contexto ctx)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        return await db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).SingleAsync();
    }

    private static async Task ConfigurarComisionAsync(Contexto ctx, string valorJson)
    {
        var respuesta = await ctx.Admin.PutAsJsonAsync(
            $"/api/parametros?idEmpresa={ctx.IdEmpresa}", new ParametroAlta("comision_porcentaje", valorJson, null));
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    private static Task<HttpResponseMessage> LlamarComisionesAsync(
        HttpClient cliente, int idEmpresa, DateOnly desde, DateOnly hasta) =>
        cliente.GetAsync($"/api/reportes/comisiones?idEmpresa={idEmpresa}&desde={desde:yyyy-MM-dd}&hasta={hasta:yyyy-MM-dd}");

    private static async Task<Comisiones> ObtenerComisionesAsync(
        HttpClient cliente, int idEmpresa, DateOnly desde, DateOnly hasta)
    {
        var respuesta = await LlamarComisionesAsync(cliente, idEmpresa, desde, hasta);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<Comisiones>(cuerpo, OpcionesJson)!;
    }

    // ---- task 10.4: el patrón de 4 pruebas ----------------------------------------------------

    [Fact]
    public async Task UnaVentaDeOtroTenantNuncaApareceEnLasComisiones()
    {
        var ctxA = await PrepararAsync(nameof(UnaVentaDeOtroTenantNuncaApareceEnLasComisiones) + "-A");
        var ctxB = await PrepararAsync(nameof(UnaVentaDeOtroTenantNuncaApareceEnLasComisiones) + "-B");
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var idTipoTxB = await IdTipoComprobanteTxAsync(ctxB);

        await SembrarComprobanteAsync(ctxB, idTipoTxB, DateTimeOffset.UtcNow, 999_999m);
        await ConfigurarComisionAsync(ctxA, "5");

        var comisiones = await ObtenerComisionesAsync(ctxA.Admin, ctxA.IdEmpresa, hoy, hoy);

        Assert.Empty(comisiones.Filas);
    }

    [Fact]
    public async Task UnaVentaSoftDeletedNuncaApareceEnLasComisiones()
    {
        var ctx = await PrepararAsync(nameof(UnaVentaSoftDeletedNuncaApareceEnLasComisiones));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodiaUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);
        var idTipoTx = await IdTipoComprobanteTxAsync(ctx);
        await ConfigurarComisionAsync(ctx, "10");

        await SembrarComprobanteAsync(ctx, idTipoTx, mediodiaUtc, 999_999m, eliminado: true);
        await SembrarComprobanteAsync(ctx, idTipoTx, mediodiaUtc, 100m);

        var comisiones = await ObtenerComisionesAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        var fila = Assert.Single(comisiones.Filas);
        Assert.Equal(100m, fila.NetoVendido);
        Assert.Equal(10m, fila.Comision);
    }

    [Fact]
    public async Task UnaVentaAnuladaNuncaApareceEnLasComisiones()
    {
        var ctx = await PrepararAsync(nameof(UnaVentaAnuladaNuncaApareceEnLasComisiones));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodiaUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);
        var idTipoTx = await IdTipoComprobanteTxAsync(ctx);
        await ConfigurarComisionAsync(ctx, "10");

        await SembrarComprobanteAsync(ctx, idTipoTx, mediodiaUtc, 999_999m, estado: EstadoComprobante.Anulado);
        await SembrarComprobanteAsync(ctx, idTipoTx, mediodiaUtc, 250m);

        var comisiones = await ObtenerComisionesAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        var fila = Assert.Single(comisiones.Filas);
        Assert.Equal(250m, fila.NetoVendido);
        Assert.Equal(25m, fila.Comision);
    }

    [Fact]
    public async Task LaComisionCoincideConElCalculoAMano()
    {
        var ctx = await PrepararAsync(nameof(LaComisionCoincideConElCalculoAMano));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodiaUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);
        var idTipoTx = await IdTipoComprobanteTxAsync(ctx);
        await ConfigurarComisionAsync(ctx, "5");

        // Vendedor emisor único (el admin sembrado por PrepararAsync): $10.000 netos → 5% = $500,
        // el ejemplo textual del spec (Scenario "A configured rate computes a non-zero commission").
        await SembrarComprobanteAsync(ctx, idTipoTx, mediodiaUtc, 4_000m);
        await SembrarComprobanteAsync(ctx, idTipoTx, mediodiaUtc, 6_000m);

        var comisiones = await ObtenerComisionesAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(5m, comisiones.ComisionPorcentaje);
        var fila = Assert.Single(comisiones.Filas);
        Assert.Equal(ctx.IdEmpleadoAdmin, fila.IdEmpleado);
        Assert.Equal(10_000m, fila.NetoVendido);
        Assert.Equal(500m, fila.Comision);
    }

    // ---- task 10.4: tasa default 0 ⇒ toda comisión en cero (spec: "Default rate yields zero
    // commission") ------------------------------------------------------------------------------

    [Fact]
    public async Task SinParametroConfiguradoLaTasaDefaultEsCeroYTodaComisionEsCero()
    {
        var ctx = await PrepararAsync(nameof(SinParametroConfiguradoLaTasaDefaultEsCeroYTodaComisionEsCero));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var mediodiaUtc = new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, TimeSpan.Zero);
        var idTipoTx = await IdTipoComprobanteTxAsync(ctx);

        // Sin ConfigurarComisionAsync: ninguna fila de `parametros` para esta clave — el default
        // declarado en ParametroConocido.ComisionPorcentaje ("0") es lo único que puede resolver.
        await SembrarComprobanteAsync(ctx, idTipoTx, mediodiaUtc, 10_000m);

        var comisiones = await ObtenerComisionesAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        Assert.Equal(0m, comisiones.ComisionPorcentaje);
        var fila = Assert.Single(comisiones.Filas);
        Assert.Equal(10_000m, fila.NetoVendido);
        Assert.Equal(0m, fila.Comision);
        Assert.True(comisiones.Provisional);
    }

    // ---- task 10.4: la respuesta viaja SIEMPRE etiquetada PROVISIONAL --------------------------

    [Fact]
    public async Task ConTasaConfiguradaLaRespuestaSigueEtiquetadaProvisional()
    {
        var ctx = await PrepararAsync(nameof(ConTasaConfiguradaLaRespuestaSigueEtiquetadaProvisional));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        await ConfigurarComisionAsync(ctx, "8");

        var comisiones = await ObtenerComisionesAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        Assert.True(comisiones.Provisional);
        Assert.Equal(8m, comisiones.ComisionPorcentaje);
    }

    // ---- task 10.4: nada se persiste — el endpoint es de solo lectura --------------------------

    [Fact]
    public async Task LlamarComisionesNoEscribeNingunaFilaNueva()
    {
        var ctx = await PrepararAsync(nameof(LlamarComisionesNoEscribeNingunaFilaNueva));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var idTipoTx = await IdTipoComprobanteTxAsync(ctx);
        await ConfigurarComisionAsync(ctx, "5");
        await SembrarComprobanteAsync(ctx, idTipoTx, DateTimeOffset.UtcNow, 1_000m);

        async Task<(int Comprobantes, int Items, int Parametros)> ContarAsync()
        {
            await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
            return (
                await db.ComprobantesVenta.CountAsync(),
                await db.ItemsComprobanteVenta.CountAsync(),
                await db.Parametros.CountAsync());
        }

        var antes = await ContarAsync();

        // Dos llamadas (no solo una): si el endpoint tuviera cualquier escritura idempotente-solo-
        // a-la-primera-vez, la segunda llamada la expondría igual.
        await ObtenerComisionesAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);
        await ObtenerComisionesAsync(ctx.Admin, ctx.IdEmpresa, hoy, hoy);

        var despues = await ContarAsync();

        Assert.Equal(antes, despues);
    }
}
