using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Pagos;
using Ways.Application.Plantel;
using Ways.Application.Usuarios;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Application.CuentaCorriente;
using Ways.Domain.Organizacion;
using Ways.Domain.Pagos;
using Ways.Domain.Plantel;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>POST /api/pagos/administracion/{id}/reasignar-personal</c> punta a punta contra Postgres real: el desplazamiento de
/// los saldos resultantes de los dos libros con asientos antes y después del movido, los saldos cacheados, los textos con
/// el nombre, la auditoría, el orden 404-antes-que-400 y cada rechazo sin escrituras parciales.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ReasignacionDePagoDePersonalTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordOtroRol = "otro-rol-password-larga";

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdMedioEfectivo, HttpClient Admin, HttpClient Supervisor,
        HttpClient Vendedor);

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

        var supervisor = await CrearYLoguearAsync(admin, nombre, "supervisor", RolConocido.Supervisor);
        var vendedor = await CrearYLoguearAsync(admin, nombre, "vendedor", RolConocido.Vendedor);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, idMedioEfectivo, admin, supervisor, vendedor);
    }

    private async Task<HttpClient> CrearYLoguearAsync(HttpClient admin, string nombre, string sufijo, RolConocido rol)
    {
        var corto = Guid.NewGuid().ToString("N")[..8];
        var mail = $"{nombre.ToLowerInvariant()}-{sufijo}@ways.test";
        var alta = await admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario($"{sufijo}-{corto}", mail, (int)rol, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private static async Task<JsonElement> CuerpoAsync(HttpResponseMessage respuesta, HttpStatusCode esperado)
    {
        var texto = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == esperado, $"{respuesta.StatusCode}: {texto}");
        return JsonDocument.Parse(texto).RootElement.Clone();
    }

    private static async Task<string?> CodigoAsync(HttpResponseMessage respuesta, HttpStatusCode esperado) =>
        (await CuerpoAsync(respuesta, esperado)).GetProperty("codigo").GetString();

    private static async Task<int> CrearPersonalAsync(HttpClient admin, string nombre, int? idCliente = null)
    {
        var respuesta = await admin.PostAsJsonAsync(
            "/api/personal", new DatosDePersonal(nombre, null, null, null, 1000m, null, null, idCliente));
        return (await CuerpoAsync(respuesta, HttpStatusCode.Created)).GetProperty("id").GetInt32();
    }

    private static async Task AjustarAsync(HttpClient admin, int idPersonal, decimal importe)
    {
        var ajuste = await admin.PostAsJsonAsync(
            $"/api/personal/{idPersonal}/ajustes", new { importe, detalle = "Saldo de la prueba", fecha = "2026-10-01" });
        Assert.Equal(HttpStatusCode.Created, ajuste.StatusCode);
    }

    /// <summary>Pago de tesorería de una sola línea de efectivo; devuelve el asiento del libro del personal.</summary>
    private static async Task<int> PagarAsync(Contexto ctx, int idPersonal, decimal importe, decimal? descuento = null)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync($"/api/personal/{idPersonal}/pagos", new
        {
            fecha = FechaDelNegocio.Hoy().ToString("yyyy-MM-dd"),
            idEmpresa = ctx.IdEmpresa,
            idPuntoVenta = ctx.IdPuntoVenta,
            origenFondos = "Tesoreria",
            descuentoDeCuentaCorriente = descuento,
            lineas = new object[] { new { idMedioPago = ctx.IdMedioEfectivo, importe, referencia = (string?)null } }
        });
        return (await CuerpoAsync(respuesta, HttpStatusCode.Created)).GetProperty("idMovimiento").GetInt32();
    }

    private Infrastructure.Persistencia.WaysDbContext Abrir(Contexto ctx) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

    private async Task<int> IdDelPagoAsync(Contexto ctx, int idMovimiento)
    {
        await using var db = Abrir(ctx);
        return await db.Pagos.Where(p => p.IdMovimientoPersonal == idMovimiento).Select(p => p.Id).SingleAsync();
    }

    private static Task<HttpResponseMessage> ReasignarAsync(HttpClient cliente, int idPago, object cuerpo) =>
        cliente.PostAsJsonAsync($"/api/pagos/administracion/{idPago}/reasignar-personal", cuerpo);

    private async Task<List<(int IdMovimiento, int IdPersonal, decimal Saldo)>> LibroAsync(Contexto ctx, int idPersonal)
    {
        await using var db = Abrir(ctx);
        var filas = await db.MovimientosPersonal
            .Where(m => m.IdPersonal == idPersonal)
            .OrderBy(m => m.Id)
            .Select(m => new { m.Id, m.IdPersonal, m.SaldoResultante })
            .ToListAsync();
        return filas.Select(f => (f.Id, f.IdPersonal, f.SaldoResultante)).ToList();
    }

    private async Task<decimal> SaldoAsync(Contexto ctx, int idPersonal)
    {
        await using var db = Abrir(ctx);
        return await db.Personal.Where(p => p.Id == idPersonal).Select(p => p.Saldo).SingleAsync();
    }

    [Fact]
    public async Task ReasignarMueveElAsientoYDesplazaLosSaldosResultantesDeLosDosLibros()
    {
        var ctx = await PrepararAsync(nameof(ReasignarMueveElAsientoYDesplazaLosSaldosResultantesDeLosDosLibros));
        var origen = await CrearPersonalAsync(ctx.Admin, "Agustin");
        var destino = await CrearPersonalAsync(ctx.Admin, "Diego");

        await AjustarAsync(ctx.Admin, destino, 5000m);
        await AjustarAsync(ctx.Admin, origen, 10000m);
        var a1 = await PagarAsync(ctx, origen, 1000m);
        var b1 = await PagarAsync(ctx, destino, 500m);
        var movido = await PagarAsync(ctx, origen, 2000m);
        var b2 = await PagarAsync(ctx, destino, 300m);
        var a3 = await PagarAsync(ctx, origen, 700m);
        var idPago = await IdDelPagoAsync(ctx, movido);

        var respuesta = await CuerpoAsync(
            await ReasignarAsync(ctx.Admin, idPago, new { idPersonal = destino }), HttpStatusCode.OK);

        Assert.Equal(idPago, respuesta.GetProperty("idPago").GetInt32());
        Assert.Equal(origen, respuesta.GetProperty("idPersonalAnterior").GetInt32());
        Assert.Equal(destino, respuesta.GetProperty("idPersonal").GetInt32());
        Assert.Equal("Pago de personal: Diego", respuesta.GetProperty("concepto").GetString());

        var libroOrigen = await LibroAsync(ctx, origen);
        Assert.Equal(new[] { 10000m, 9000m, 8300m }, libroOrigen.Select(f => f.Saldo).ToArray());
        Assert.DoesNotContain(libroOrigen, f => f.IdMovimiento == movido);
        Assert.Equal(a1, libroOrigen[1].IdMovimiento);
        Assert.Equal(a3, libroOrigen[2].IdMovimiento);

        var libroDestino = await LibroAsync(ctx, destino);
        Assert.Equal(new[] { 5000m, 4500m, 2500m, 2200m }, libroDestino.Select(f => f.Saldo).ToArray());
        Assert.Equal(new[] { b1, movido, b2 }, libroDestino.Skip(1).Select(f => f.IdMovimiento).ToArray());

        Assert.Equal(8300m, await SaldoAsync(ctx, origen));
        Assert.Equal(2200m, await SaldoAsync(ctx, destino));

        await using var db = Abrir(ctx);
        var pago = await db.Pagos.SingleAsync(p => p.Id == idPago);
        Assert.Equal("Pago de personal: Diego", pago.Concepto);
        Assert.Equal(movido, pago.IdMovimientoPersonal);
        var tesoreria = await db.MovimientosTesoreria.SingleAsync(m => m.IdPago == idPago);
        Assert.Equal("Pago de personal: Diego", tesoreria.Concepto);

        var auditoria = await db.Auditoria.SingleAsync(a => a.Accion == "pago.reasignacion" && a.IdEntidad == idPago);
        Assert.Equal("pago", auditoria.Entidad);
        using var anterior = JsonDocument.Parse(auditoria.ValorAnterior!);
        using var nuevo = JsonDocument.Parse(auditoria.ValorNuevo);
        Assert.Equal(origen, anterior.RootElement.GetProperty("id_personal").GetInt32());
        Assert.Equal(destino, nuevo.RootElement.GetProperty("id_personal").GetInt32());
        Assert.Equal("Pago de personal: Agustin", anterior.RootElement.GetProperty("concepto").GetString());
        Assert.Equal("Pago de personal: Diego", nuevo.RootElement.GetProperty("concepto").GetString());
    }

    [Fact]
    public async Task UnDestinoSinAsientosAnterioresArrancaElSaldoDelMovidoDesdeCero()
    {
        var ctx = await PrepararAsync(nameof(UnDestinoSinAsientosAnterioresArrancaElSaldoDelMovidoDesdeCero));
        var origen = await CrearPersonalAsync(ctx.Admin, "Agustin");
        var destino = await CrearPersonalAsync(ctx.Admin, "Diego");

        await AjustarAsync(ctx.Admin, origen, 10000m);
        var movido = await PagarAsync(ctx, origen, 2000m);
        await AjustarAsync(ctx.Admin, destino, 5000m);
        var idPago = await IdDelPagoAsync(ctx, movido);

        await CuerpoAsync(await ReasignarAsync(ctx.Admin, idPago, new { idPersonal = destino }), HttpStatusCode.OK);

        Assert.Equal(new[] { 10000m }, (await LibroAsync(ctx, origen)).Select(f => f.Saldo).ToArray());
        Assert.Equal(new[] { -2000m, 3000m }, (await LibroAsync(ctx, destino)).Select(f => f.Saldo).ToArray());
        Assert.Equal(10000m, await SaldoAsync(ctx, origen));
        Assert.Equal(3000m, await SaldoAsync(ctx, destino));
    }

    [Fact]
    public async Task ElPagoInexistenteOEnOtroTenantEs404AntesDeValidarElCuerpo()
    {
        var ctx = await PrepararAsync(nameof(ElPagoInexistenteOEnOtroTenantEs404AntesDeValidarElCuerpo));
        var ajeno = await PrepararAsync(nameof(ElPagoInexistenteOEnOtroTenantEs404AntesDeValidarElCuerpo) + "Ajeno");
        var personalAjeno = await CrearPersonalAsync(ajeno.Admin, "Ajeno");
        await AjustarAsync(ajeno.Admin, personalAjeno, 1000m);
        var idPagoAjeno = await IdDelPagoAsync(ajeno, await PagarAsync(ajeno, personalAjeno, 100m));

        var inexistente = await ReasignarAsync(ctx.Admin, 999_999, new { idPersonal = 0 });
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);

        var sinCuerpo = await ctx.Admin.PostAsync("/api/pagos/administracion/999999/reasignar-personal", content: null);
        Assert.Equal(HttpStatusCode.NotFound, sinCuerpo.StatusCode);

        var otroTenant = await ReasignarAsync(ctx.Admin, idPagoAjeno, new { idPersonal = 0 });
        Assert.Equal(HttpStatusCode.NotFound, otroTenant.StatusCode);

        Assert.Equal(1000m - 100m, await SaldoAsync(ajeno, personalAjeno));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task UnIdPersonalNoPositivoOAusenteEs400(int idPersonal)
    {
        var ctx = await PrepararAsync($"{nameof(UnIdPersonalNoPositivoOAusenteEs400)}{Math.Abs(idPersonal)}");
        var origen = await CrearPersonalAsync(ctx.Admin, "Agustin");
        await AjustarAsync(ctx.Admin, origen, 1000m);
        var idPago = await IdDelPagoAsync(ctx, await PagarAsync(ctx, origen, 100m));

        Assert.Equal("personal_requerido", await CodigoAsync(
            await ReasignarAsync(ctx.Admin, idPago, new { idPersonal }), HttpStatusCode.BadRequest));
        Assert.Equal("personal_requerido", await CodigoAsync(
            await ReasignarAsync(ctx.Admin, idPago, new { }), HttpStatusCode.BadRequest));
        Assert.Equal("personal_requerido", await CodigoAsync(
            await ctx.Admin.PostAsync($"/api/pagos/administracion/{idPago}/reasignar-personal", content: null),
            HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task UnPagoQueNoEsDeSueldoEs409()
    {
        var ctx = await PrepararAsync(nameof(UnPagoQueNoEsDeSueldoEs409));
        var destino = await CrearPersonalAsync(ctx.Admin, "Diego");
        var comun = await CuerpoAsync(
            await ctx.Admin.PostAsJsonAsync(
                "/api/pagos/administracion",
                PagoDeUnMedio.Administracion(
                    FechaDelNegocio.Hoy(), ctx.IdEmpresa, null, CategoriaPago.Otros, null, null, "Gasto común", null,
                    ctx.IdMedioEfectivo, null, 20m)),
            HttpStatusCode.Created);

        Assert.Equal("pago_sin_movimiento_de_personal", await CodigoAsync(
            await ReasignarAsync(ctx.Admin, comun.GetProperty("id").GetInt32(), new { idPersonal = destino }),
            HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task UnDestinoInexistenteOEnOtroTenantOEnBajaEs404SinEscribirNada()
    {
        var ctx = await PrepararAsync(nameof(UnDestinoInexistenteOEnOtroTenantOEnBajaEs404SinEscribirNada));
        var ajeno = await PrepararAsync(nameof(UnDestinoInexistenteOEnOtroTenantOEnBajaEs404SinEscribirNada) + "Ajeno");
        var origen = await CrearPersonalAsync(ctx.Admin, "Agustin");
        var deBaja = await CrearPersonalAsync(ctx.Admin, "Baja");
        var deOtroTenant = await CrearPersonalAsync(ajeno.Admin, "Ajeno");
        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"/api/personal/{deBaja}")).StatusCode);
        await AjustarAsync(ctx.Admin, origen, 1000m);
        var idPago = await IdDelPagoAsync(ctx, await PagarAsync(ctx, origen, 100m));

        foreach (var destino in new[] { 999_999, deBaja, deOtroTenant })
        {
            var respuesta = await ReasignarAsync(ctx.Admin, idPago, new { idPersonal = destino });
            Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        }

        Assert.Equal(900m, await SaldoAsync(ctx, origen));
        Assert.Equal(new[] { 1000m, 900m }, (await LibroAsync(ctx, origen)).Select(f => f.Saldo).ToArray());
    }

    [Fact]
    public async Task ReasignarAlMismoEmpleadoEs409()
    {
        var ctx = await PrepararAsync(nameof(ReasignarAlMismoEmpleadoEs409));
        var origen = await CrearPersonalAsync(ctx.Admin, "Agustin");
        await AjustarAsync(ctx.Admin, origen, 1000m);
        var idPago = await IdDelPagoAsync(ctx, await PagarAsync(ctx, origen, 100m));

        Assert.Equal("reasignacion_al_mismo_personal", await CodigoAsync(
            await ReasignarAsync(ctx.Admin, idPago, new { idPersonal = origen }), HttpStatusCode.Conflict));
        Assert.Equal(900m, await SaldoAsync(ctx, origen));
    }

    [Fact]
    public async Task UnPagoConDescuentoDeCuentaCorrienteEs409SinEscribirNada()
    {
        var ctx = await PrepararAsync(nameof(UnPagoConDescuentoDeCuentaCorrienteEs409SinEscribirNada));
        int idCliente;
        await using (var db = Abrir(ctx))
        {
            var ahora = DateTimeOffset.UtcNow;
            var cliente = new Cliente
            {
                IdTenant = ctx.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = "Cliente empleado",
                IdCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync(),
                IdListaPrecio = await db.Clientes.Select(c => c.IdListaPrecio).FirstAsync(),
                LimiteCredito = 0m, CreditoIlimitado = true, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Clientes.Add(cliente);
            await db.SaveChangesAsync();
            idCliente = cliente.Id;
        }

        var deuda = await ctx.Admin.PostAsJsonAsync(
            $"/api/clientes/{idCliente}/cuenta-corriente/ajustes",
            new SolicitudDeAjuste(ctx.IdPuntoVenta, 4000m, "Deuda inicial de la prueba"));
        Assert.Equal(HttpStatusCode.Created, deuda.StatusCode);

        var origen = await CrearPersonalAsync(ctx.Admin, "Agustin", idCliente);
        var destino = await CrearPersonalAsync(ctx.Admin, "Diego");
        await AjustarAsync(ctx.Admin, origen, 10000m);
        var idPago = await IdDelPagoAsync(ctx, await PagarAsync(ctx, origen, 2000m, descuento: 500m));

        Assert.Equal("pago_con_descuento_de_cuenta_corriente", await CodigoAsync(
            await ReasignarAsync(ctx.Admin, idPago, new { idPersonal = destino }), HttpStatusCode.Conflict));

        Assert.Equal(new[] { 10000m, 7500m }, (await LibroAsync(ctx, origen)).Select(f => f.Saldo).ToArray());
        Assert.Empty(await LibroAsync(ctx, destino));
        Assert.Equal(7500m, await SaldoAsync(ctx, origen));
        Assert.Equal(0m, await SaldoAsync(ctx, destino));
    }

    [Fact]
    public async Task SoloElAdminPuedeReasignar()
    {
        var ctx = await PrepararAsync(nameof(SoloElAdminPuedeReasignar));
        var origen = await CrearPersonalAsync(ctx.Admin, "Agustin");
        var destino = await CrearPersonalAsync(ctx.Admin, "Diego");
        await AjustarAsync(ctx.Admin, origen, 1000m);
        var idPago = await IdDelPagoAsync(ctx, await PagarAsync(ctx, origen, 100m));

        Assert.Equal(
            HttpStatusCode.Forbidden, (await ReasignarAsync(ctx.Supervisor, idPago, new { idPersonal = destino })).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden, (await ReasignarAsync(ctx.Vendedor, idPago, new { idPersonal = destino })).StatusCode);
        Assert.Equal(900m, await SaldoAsync(ctx, origen));
    }
}
