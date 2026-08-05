using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Gastos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Catalogos;
using Ways.Domain.Gastos;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-6-turnos-caja, Slice 3 (tasks 3.2, 3.3, 3.5): <c>POST /api/gastos</c>, <c>GET
/// /api/gastos</c> punta a punta — captura contra el turno abierto resuelto server-side
/// (<c>ServicioDeTurnos.ResolverTurnoAbiertoAsync</c> compartido), importe positivo y
/// autorización <c>OperacionDePos</c> (spec: gastos).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class GastosEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordVendedor = "una-contraseña-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, int IdMedioPago, HttpClient Admin);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin("test@test.com", "root"));
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

        await using var db = fixture.CrearContextoDeAplicacion(
            new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idMedioPago = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id)
            .FirstAsync();

        return new Contexto(resultado.IdTenant, resultado.IdPuntoVenta, idMedioPago, admin);
    }

    private async Task<HttpClient> CrearVendedorAsync(Contexto ctx, string nombre)
    {
        var mailVendedor = $"{nombre.ToLowerInvariant()}-vendedor@ways.test";
        var alta = await ctx.Admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario("vendedor-gastos", mailVendedor, (int)RolConocido.Vendedor, PasswordVendedor));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var vendedor = fixture.CreateClient();
        var login = await vendedor.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailVendedor, PasswordVendedor));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return vendedor;
    }

    private static async Task<TurnoResumen> AbrirTurnoAsync(HttpClient cliente, int idPuntoVenta)
    {
        var respuesta = await cliente.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(idPuntoVenta, 500m, "Apertura de soporte"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        return JsonSerializer.Deserialize<TurnoResumen>(cuerpo, OpcionesJson)!;
    }

    // ---- task 3.3: happy path + resolución server-side del turno --------------------------------

    [Fact]
    public async Task UnGastoPersisteConSuCategoriaYSuMedio()
    {
        var ctx = await PrepararAsync(nameof(UnGastoPersisteConSuCategoriaYSuMedio));
        var turno = await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDeGasto(
                ctx.IdPuntoVenta, CategoriaGasto.Servicios, null, null, "Internet del local", null,
                ctx.IdMedioPago, null, 300m));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        var gasto = JsonSerializer.Deserialize<GastoRegistrado>(cuerpo, OpcionesJson)!;
        Assert.Equal(CategoriaGasto.Servicios, gasto.Categoria);
        Assert.Equal(ctx.IdMedioPago, gasto.IdMedioPago);
        Assert.Equal(300m, gasto.Importe);
        // spec: Gasto succeeds with an open turno — id_turno_caja resuelto server-side, nunca
        // client-supplied (SolicitudDeGasto no tiene ese campo).
        Assert.Equal(turno.Id, gasto.IdTurnoCaja);
    }

    [Fact]
    public async Task UnGastoSinTurnoAbiertoSeRechaza()
    {
        var ctx = await PrepararAsync(nameof(UnGastoSinTurnoAbiertoSeRechaza));

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDeGasto(
                ctx.IdPuntoVenta, CategoriaGasto.Otros, null, null, "Gasto sin turno", null,
                ctx.IdMedioPago, null, 100m));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("turno_no_abierto", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnGastoContraUnPuntoDeVentaInexistenteDevuelve404()
    {
        var ctx = await PrepararAsync(nameof(UnGastoContraUnPuntoDeVentaInexistenteDevuelve404));

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDeGasto(
                999999, CategoriaGasto.Otros, null, null, "Gasto contra PV apócrifo", null,
                ctx.IdMedioPago, null, 100m));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    // ---- task 3.3: importe positivo -------------------------------------------------------------

    [Fact]
    public async Task UnGastoConImporteCeroSeRechazaAntesDeLlegarALaBaseDeDatos()
    {
        var ctx = await PrepararAsync(nameof(UnGastoConImporteCeroSeRechazaAntesDeLlegarALaBaseDeDatos));
        await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDeGasto(
                ctx.IdPuntoVenta, CategoriaGasto.Otros, null, null, "Gasto en cero", null,
                ctx.IdMedioPago, null, 0m));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("gasto_importe_invalido", problema.GetProperty("codigo").GetString());
    }

    // ---- pre-checks de FK: referencias inválidas nunca llegan como 500 --------------------------

    [Fact]
    public async Task UnGastoConMedioDePagoInexistenteEsRechazadoCon400()
    {
        var ctx = await PrepararAsync(nameof(UnGastoConMedioDePagoInexistenteEsRechazadoCon400));
        await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDeGasto(
                ctx.IdPuntoVenta, CategoriaGasto.Otros, null, null, "Medio de pago apócrifo", null,
                999999, null, 100m));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("referencia_invalida", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnGastoConProveedorInexistenteEsRechazadoCon400()
    {
        var ctx = await PrepararAsync(nameof(UnGastoConProveedorInexistenteEsRechazadoCon400));
        await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDeGasto(
                ctx.IdPuntoVenta, CategoriaGasto.Proveedor, 999999, null, "Proveedor apócrifo", null,
                ctx.IdMedioPago, null, 100m));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("referencia_invalida", problema.GetProperty("codigo").GetString());
    }

    // ---- task 3.5: autorización -------------------------------------------------------------------

    [Fact]
    public async Task UnVendedorRegistraUnGasto()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorRegistraUnGasto));
        using var vendedor = await CrearVendedorAsync(ctx, nameof(UnVendedorRegistraUnGasto));
        await AbrirTurnoAsync(vendedor, ctx.IdPuntoVenta);

        var respuesta = await vendedor.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDeGasto(
                ctx.IdPuntoVenta, CategoriaGasto.Viaticos, null, null, "Viáticos de reparto", null,
                ctx.IdMedioPago, null, 150m));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnRolFueraDeOperacionDePosEsRechazadoDeGastos()
    {
        var ctx = await PrepararAsync(nameof(UnRolFueraDeOperacionDePosEsRechazadoDeGastos));
        await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta);

        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin("test@test.com", "root"));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var respuesta = await root.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDeGasto(
                ctx.IdPuntoVenta, CategoriaGasto.Otros, null, null, "Intento de root", null,
                ctx.IdMedioPago, null, 100m));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    // ---- historial paginado (GET /api/gastos) ------------------------------------------------------

    [Fact]
    public async Task ElHistorialPaginaYFiltraPorPuntoDeVenta()
    {
        var ctx = await PrepararAsync(nameof(ElHistorialPaginaYFiltraPorPuntoDeVenta));
        await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta);

        foreach (var importe in new[] { 100m, 200m })
        {
            var respuesta = await ctx.Admin.PostAsJsonAsync(
                "/api/gastos",
                new SolicitudDeGasto(
                    ctx.IdPuntoVenta, CategoriaGasto.Otros, null, null, "Gasto de historial", null,
                    ctx.IdMedioPago, null, importe));
            Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        }

        var pagina = await ctx.Admin.GetFromJsonAsync<PaginaDeGastos>(
            $"/api/gastos?idPuntoVenta={ctx.IdPuntoVenta}", OpcionesJson);
        Assert.NotNull(pagina);
        Assert.Equal(2, pagina!.Total);
        Assert.All(pagina.Items, item => Assert.Equal(ctx.IdPuntoVenta, item.IdPuntoVenta));
        Assert.Equal(1, pagina.Pagina);
        Assert.Equal(25, pagina.Tamanio);
    }

    // ---- judgment-day precedent (Slice 2): guard de autenticación / cross-tenant ------------------

    [Fact]
    public async Task ListarGastosSinTokenDevuelve401()
    {
        using var cliente = fixture.CreateClient();

        var respuesta = await cliente.GetAsync("/api/gastos?idPuntoVenta=1");

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
    }
}
