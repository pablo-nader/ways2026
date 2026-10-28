using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Pagos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Catalogos;
using Ways.Domain.Pagos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-6-turnos-caja, Slice 3 (tasks 3.2, 3.3, 3.5): <c>POST /api/gastos</c>, <c>GET
/// /api/gastos</c> punta a punta — captura contra el turno abierto resuelto server-side
/// (<c>ServicioDeTurnos.ResolverTurnoAbiertoAsync</c> compartido), importe positivo y
/// autorización <c>OperacionDePos</c> (spec: pagos).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class PagosEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web);
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
            "/api/usuarios", new CrearUsuario("vendedor-pagos", mailVendedor, (int)RolConocido.Vendedor, PasswordVendedor));
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
    public async Task UnPagoPersisteConSuCategoriaYSuMedio()
    {
        var ctx = await PrepararAsync(nameof(UnPagoPersisteConSuCategoriaYSuMedio));
        var turno = await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDePago(
                ctx.IdPuntoVenta, CategoriaPago.Servicios, null, null, "Internet del local", null,
                ctx.IdMedioPago, null, 300m));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        var pago = JsonSerializer.Deserialize<PagoRegistrado>(cuerpo, OpcionesJson)!;
        Assert.Equal(CategoriaPago.Servicios, pago.Categoria);
        Assert.Equal(ctx.IdMedioPago, pago.IdMedioPago);
        Assert.Equal(300m, pago.Importe);
        // spec: Pago succeeds with an open turno — id_turno_caja resuelto server-side, nunca
        // client-supplied (SolicitudDePago no tiene ese campo).
        Assert.Equal(turno.Id, pago.IdTurnoCaja);

        // E2-S1: escritura doble, exactamente un ítem con el medio y el importe de la cabecera.
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var item = await db.ItemsPago.SingleAsync(i => i.IdPago == pago.Id);
        Assert.Equal((ctx.IdMedioPago, 300m), (item.IdMedioPago, item.Importe));
        Assert.Null(item.Referencia);
    }

    [Fact]
    public async Task UnPagoSinTurnoAbiertoSeRechaza()
    {
        var ctx = await PrepararAsync(nameof(UnPagoSinTurnoAbiertoSeRechaza));

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDePago(
                ctx.IdPuntoVenta, CategoriaPago.Otros, null, null, "Gasto sin turno", null,
                ctx.IdMedioPago, null, 100m));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("turno_no_abierto", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnPagoContraUnPuntoDeVentaInexistenteDevuelve404()
    {
        var ctx = await PrepararAsync(nameof(UnPagoContraUnPuntoDeVentaInexistenteDevuelve404));

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDePago(
                999999, CategoriaPago.Otros, null, null, "Gasto contra PV apócrifo", null,
                ctx.IdMedioPago, null, 100m));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    // ---- task 3.3: importe positivo -------------------------------------------------------------

    [Fact]
    public async Task UnPagoConImporteCeroSeRechazaAntesDeLlegarALaBaseDeDatos()
    {
        var ctx = await PrepararAsync(nameof(UnPagoConImporteCeroSeRechazaAntesDeLlegarALaBaseDeDatos));
        await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDePago(
                ctx.IdPuntoVenta, CategoriaPago.Otros, null, null, "Gasto en cero", null,
                ctx.IdMedioPago, null, 0m));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("gasto_importe_invalido", problema.GetProperty("codigo").GetString());
    }

    // ---- concepto obligatorio (mutation-proof-tests: ExigirConceptoValido) -----------------------

    /// <summary>Cláusula bajo prueba: <c>ServicioDePagos.ExigirConceptoValido</c>. Mutación
    /// verificada (mutation-proof-tests): comentando el cuerpo del guard, este test pasa a
    /// recibir 201 en vez del 400 esperado; restaurado el guard, vuelve a verde.</summary>
    [Fact]
    public async Task UnPagoConConceptoEnBlancoSeRechazaAntesDeLlegarALaBaseDeDatos()
    {
        var ctx = await PrepararAsync(nameof(UnPagoConConceptoEnBlancoSeRechazaAntesDeLlegarALaBaseDeDatos));
        await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDePago(
                ctx.IdPuntoVenta, CategoriaPago.Otros, null, null, "   ", null,
                ctx.IdMedioPago, null, 100m));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("gasto_concepto_requerido", problema.GetProperty("codigo").GetString());
    }

    // ---- pre-checks de FK: referencias inválidas nunca llegan como 500 --------------------------

    [Fact]
    public async Task UnPagoConMedioDePagoInexistenteEsRechazadoCon400()
    {
        var ctx = await PrepararAsync(nameof(UnPagoConMedioDePagoInexistenteEsRechazadoCon400));
        await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDePago(
                ctx.IdPuntoVenta, CategoriaPago.Otros, null, null, "Medio de pago apócrifo", null,
                999999, null, 100m));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("referencia_invalida", problema.GetProperty("codigo").GetString());
    }

    // stage-gasto-a-compra (PR4), judgment-day PR3 follow-up: antes de este chequeo, un
    // id_proveedor apócrifo llegaba hasta el INSERT y salía como 400 referencia_invalida por el
    // backstop genérico de fk_pagos_proveedor (23503) — o, en el peor caso, como el 500 sin
    // traducir de ActualizarSaldoProveedorAsync si la fila de pagos llegaba a insertarse contra
    // un proveedor que desaparecía justo después. ResolverProveedorAsync (ANTES de abrir la
    // transacción) da el mismo 404 ADR-8 que el resto del repo usa para una referencia nueva que
    // no existe — nunca 400 ni 500.
    [Fact]
    public async Task UnPagoConProveedorInexistenteEsRechazadoConNoEncontrado()
    {
        var ctx = await PrepararAsync(nameof(UnPagoConProveedorInexistenteEsRechazadoConNoEncontrado));
        await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDePago(
                ctx.IdPuntoVenta, CategoriaPago.Proveedor, 999999, null, "Proveedor apócrifo", null,
                ctx.IdMedioPago, null, 100m));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    // ---- task 3.5: autorización -------------------------------------------------------------------

    [Fact]
    public async Task UnVendedorRegistraUnPago()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorRegistraUnPago));
        using var vendedor = await CrearVendedorAsync(ctx, nameof(UnVendedorRegistraUnPago));
        await AbrirTurnoAsync(vendedor, ctx.IdPuntoVenta);

        var respuesta = await vendedor.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDePago(
                ctx.IdPuntoVenta, CategoriaPago.Viaticos, null, null, "Viáticos de reparto", null,
                ctx.IdMedioPago, null, 150m));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnRolFueraDeOperacionDePosEsRechazadoDePagos()
    {
        var ctx = await PrepararAsync(nameof(UnRolFueraDeOperacionDePosEsRechazadoDePagos));
        await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta);

        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin("test@test.com", "root"));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var respuesta = await root.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDePago(
                ctx.IdPuntoVenta, CategoriaPago.Otros, null, null, "Intento de root", null,
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
                new SolicitudDePago(
                    ctx.IdPuntoVenta, CategoriaPago.Otros, null, null, "Gasto de historial", null,
                    ctx.IdMedioPago, null, importe));
            Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        }

        var pagina = await ctx.Admin.GetFromJsonAsync<PaginaDePagos>(
            $"/api/gastos?idPuntoVenta={ctx.IdPuntoVenta}", OpcionesJson);
        Assert.NotNull(pagina);
        Assert.Equal(2, pagina!.Total);
        Assert.All(pagina.Items, item => Assert.Equal(ctx.IdPuntoVenta, item.IdPuntoVenta));
        Assert.Equal(1, pagina.Pagina);
        Assert.Equal(25, pagina.Tamanio);
    }

    // ---- judgment-day precedent (Slice 2): guard de autenticación / cross-tenant ------------------

    [Fact]
    public async Task ListarPagosSinTokenDevuelve401()
    {
        using var cliente = fixture.CreateClient();

        var respuesta = await cliente.GetAsync("/api/gastos?idPuntoVenta=1");

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
    }
}
