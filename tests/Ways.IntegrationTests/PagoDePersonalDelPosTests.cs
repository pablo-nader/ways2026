using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Organizacion;
using Ways.Application.Plantel;
using Ways.Application.Usuarios;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Pagos;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Pago de personal desde el POS: <c>GET /api/pos/personal/opciones</c> y <c>POST /api/pos/personal/{id}/pagos</c>,
/// ambas bajo <c>OperacionDePos</c>. El cajero (Vendedor) ve solo id y nombre del personal y paga únicamente del cajón
/// del turno abierto y sin descontar cuenta corriente; el back office (<c>/api/personal/{id}/pagos</c>) conserva todo.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class PagoDePersonalDelPosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordVendedor = "vendedor-password-larga";

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdMedioEfectivo, HttpClient Admin, HttpClient Vendedor);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin("test@test.com", "root"));
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

        var mailVendedor = $"{nombre.ToLowerInvariant()}-vendedor@ways.test";
        var alta = await admin.PostAsJsonAsync(
            "/api/usuarios",
            new CrearUsuario($"vend-{Guid.NewGuid().ToString("N")[..8]}", mailVendedor, (int)RolConocido.Vendedor, PasswordVendedor));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var vendedor = fixture.CreateClient();
        var loginVendedor = await vendedor.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailVendedor, PasswordVendedor));
        Assert.Equal(HttpStatusCode.OK, loginVendedor.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();

        return new Contexto(resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, idMedioEfectivo, admin, vendedor);
    }

    private static async Task<int> CrearPersonalAsync(HttpClient admin, string nombre, decimal saldoInicial = 0m)
    {
        var respuesta = await admin.PostAsJsonAsync(
            "/api/personal", new DatosDePersonal(nombre, "30111222", "1155550000", "obs", 1234m, null, null, null));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var id = (await respuesta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        if (saldoInicial != 0m)
        {
            var ajuste = await admin.PostAsJsonAsync(
                $"/api/personal/{id}/ajustes", new { importe = saldoInicial, detalle = "Saldo inicial de la prueba", fecha = "2026-10-01" });
            Assert.Equal(HttpStatusCode.Created, ajuste.StatusCode);
        }

        return id;
    }

    private static async Task<int> AbrirTurnoAsync(Contexto ctx, decimal fondoInicial)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, fondoInicial, "Apertura de prueba"));
        return (await CuerpoAsync(respuesta, HttpStatusCode.Created)).GetProperty("id").GetInt32();
    }

    private static async Task<JsonElement> CuerpoAsync(HttpResponseMessage respuesta, HttpStatusCode esperado)
    {
        var texto = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == esperado, $"{respuesta.StatusCode}: {texto}");
        return JsonDocument.Parse(texto).RootElement.Clone();
    }

    private static string Dia() => FechaDelNegocio.Hoy().ToString("yyyy-MM-dd");

    private static object Cuerpo(
        Contexto ctx, string origen = "CajaTurno", decimal? descuento = null, decimal importe = 10m, int? idEmpresa = null) =>
        new
        {
            fecha = Dia(),
            idEmpresa,
            idPuntoVenta = ctx.IdPuntoVenta,
            origenFondos = origen,
            descuentoDeCuentaCorriente = descuento,
            lineas = new[] { new { idMedioPago = ctx.IdMedioEfectivo, importe, referencia = (string?)null } },
            detalle = "Sueldo desde el POS"
        };

    private sealed record Foto(decimal Saldo, int Asientos, int Pagos, int AsientosDeTesoreria);

    private async Task<Foto> FotoAsync(Contexto ctx, int idPersonal)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return new Foto(
            await db.Personal.Where(p => p.Id == idPersonal).Select(p => p.Saldo).SingleAsync(),
            await db.MovimientosPersonal.CountAsync(),
            await db.Pagos.CountAsync(),
            await db.MovimientosTesoreria.CountAsync());
    }

    [Fact]
    public async Task ElCajeroListaSoloIdYNombreDelPersonalVivoYElAnonimoNo()
    {
        var ctx = await PrepararAsync(nameof(ElCajeroListaSoloIdYNombreDelPersonalVivoYElAnonimoNo));
        var idAna = await CrearPersonalAsync(ctx.Admin, "Ana", saldoInicial: 5000m);
        var idBeto = await CrearPersonalAsync(ctx.Admin, "Beto");
        var idBaja = await CrearPersonalAsync(ctx.Admin, "Dado de baja");
        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"/api/personal/{idBaja}")).StatusCode);

        var otro = await PrepararAsync(nameof(ElCajeroListaSoloIdYNombreDelPersonalVivoYElAnonimoNo) + "Otro");
        await CrearPersonalAsync(otro.Admin, "Ajeno");

        var texto = await ctx.Vendedor.GetStringAsync("/api/pos/personal/opciones");
        var filas = JsonDocument.Parse(texto).RootElement.EnumerateArray().ToList();

        Assert.Equal(["Ana", "Beto"], filas.Select(f => f.GetProperty("nombre").GetString()).ToList());
        Assert.Equal([idAna, idBeto], filas.Select(f => f.GetProperty("id").GetInt32()).ToList());
        Assert.All(filas, f => Assert.Equal(["id", "nombre"], f.EnumerateObject().Select(p => p.Name).Order().ToList()));
        Assert.DoesNotContain("saldo", texto, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("30111222", texto);

        using var anonimo = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/pos/personal/opciones")).StatusCode);
    }

    [Fact]
    public async Task ElCajeroPagaDelCajonDelTurnoYBajaElSaldoYElEfectivoEsperado()
    {
        var ctx = await PrepararAsync(nameof(ElCajeroPagaDelCajonDelTurnoYBajaElSaldoYElEfectivoEsperado));
        var idPersonal = await CrearPersonalAsync(ctx.Admin, "Ana", saldoInicial: 1000m);
        var idTurno = await AbrirTurnoAsync(ctx, fondoInicial: 500m);

        var respuesta = await ctx.Vendedor.PostAsJsonAsync($"/api/pos/personal/{idPersonal}/pagos", Cuerpo(ctx, importe: 200m));
        var pago = await CuerpoAsync(respuesta, HttpStatusCode.Created);

        Assert.Equal(800m, pago.GetProperty("saldoResultante").GetDecimal());
        Assert.Equal(-200m, pago.GetProperty("importe").GetDecimal());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(800m, await db.Personal.Where(p => p.Id == idPersonal).Select(p => p.Saldo).SingleAsync());
        var unico = await db.Pagos.SingleAsync();
        Assert.Equal(OrigenFondosPago.CajaTurno, unico.OrigenFondos);
        Assert.Equal(idTurno, unico.IdTurnoCaja);
        Assert.Equal(CategoriaPago.Sueldos, unico.Categoria);
        Assert.Equal(0, await db.MovimientosTesoreria.CountAsync());

        var resumen = await ctx.Admin.GetFromJsonAsync<JsonElement>($"/api/caja/turnos/{idTurno}/resumen");
        var efectivo = resumen.GetProperty("medios").EnumerateArray()
            .Single(m => m.GetProperty("idMedioPago").GetInt32() == ctx.IdMedioEfectivo);
        Assert.Equal(300m, efectivo.GetProperty("importeEsperado").GetDecimal());
    }

    [Fact]
    public async Task ElCajeroNoPuedeDescontarCuentaCorrienteNiPagarDeLaTesoreriaNiSinOrigen()
    {
        var ctx = await PrepararAsync(nameof(ElCajeroNoPuedeDescontarCuentaCorrienteNiPagarDeLaTesoreriaNiSinOrigen));
        var idPersonal = await CrearPersonalAsync(ctx.Admin, "Ana", saldoInicial: 1000m);
        await AbrirTurnoAsync(ctx, fondoInicial: 500m);
        var antes = await FotoAsync(ctx, idPersonal);

        var rechazados = new object[]
        {
            Cuerpo(ctx, descuento: 100m),
            Cuerpo(ctx, origen: "Tesoreria", idEmpresa: ctx.IdEmpresa),
            new { fecha = Dia(), idPuntoVenta = ctx.IdPuntoVenta, descuentoDeCuentaCorriente = 100m }
        };

        foreach (var cuerpo in rechazados)
        {
            var respuesta = await ctx.Vendedor.PostAsJsonAsync($"/api/pos/personal/{idPersonal}/pagos", cuerpo);
            var json = await CuerpoAsync(respuesta, HttpStatusCode.Forbidden);
            Assert.Equal("prohibido", json.GetProperty("codigo").GetString());
        }

        Assert.Equal(antes, await FotoAsync(ctx, idPersonal));
    }

    [Fact]
    public async Task ElPagoDelPosSinTurnoAbiertoYDeUnPersonalInexistenteRespondenSusCodigos()
    {
        var ctx = await PrepararAsync(nameof(ElPagoDelPosSinTurnoAbiertoYDeUnPersonalInexistenteRespondenSusCodigos));
        var idPersonal = await CrearPersonalAsync(ctx.Admin, "Ana", saldoInicial: 1000m);

        var sinTurno = await CuerpoAsync(
            await ctx.Vendedor.PostAsJsonAsync($"/api/pos/personal/{idPersonal}/pagos", Cuerpo(ctx)), HttpStatusCode.Conflict);
        Assert.Equal("turno_no_abierto", sinTurno.GetProperty("codigo").GetString());

        // El 404 gana al 403: el personal inexistente no revela qué origen se rechazaría.
        var inexistente = await ctx.Vendedor.PostAsJsonAsync("/api/pos/personal/999999/pagos", Cuerpo(ctx, descuento: 100m));
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);
    }

    [Fact]
    public async Task ElBackOfficeConservaLaTesoreriaParaElAdminYElCajeroSigueFueraDeEsaRuta()
    {
        var ctx = await PrepararAsync(nameof(ElBackOfficeConservaLaTesoreriaParaElAdminYElCajeroSigueFueraDeEsaRuta));
        var idPersonal = await CrearPersonalAsync(ctx.Admin, "Ana", saldoInicial: 1000m);

        var admin = await ctx.Admin.PostAsJsonAsync(
            $"/api/personal/{idPersonal}/pagos", Cuerpo(ctx, origen: "Tesoreria", idEmpresa: ctx.IdEmpresa));
        await CuerpoAsync(admin, HttpStatusCode.Created);

        var vendedor = await ctx.Vendedor.PostAsJsonAsync($"/api/personal/{idPersonal}/pagos", Cuerpo(ctx));
        Assert.Equal(HttpStatusCode.Forbidden, vendedor.StatusCode);
    }
}
