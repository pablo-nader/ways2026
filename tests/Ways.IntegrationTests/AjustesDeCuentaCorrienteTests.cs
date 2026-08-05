using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.CuentaCorriente;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Clientes;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-7-cuenta-corriente, Slice 4 (tasks 4.6-4.7): <c>POST
/// /api/clientes/{id}/cuenta-corriente/ajustes</c> punta a punta — validación de detalle, signo,
/// distinción estructural contra el contramovimiento de anulación, atomicidad y autorización bajo
/// <see cref="Ways.Api.Seguridad.Politicas.SupervisionDeCuentaCorriente"/>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class AjustesDeCuentaCorrienteTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordUsuario = "una-contraseña-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(int IdTenant, int IdPuntoVenta, int IdListaPrecio, int IdConsumidorFinal, HttpClient Admin);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
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

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idConsumidorFinal = await db.Clientes
            .Where(c => c.Numero == ReglaDeClientes.NumeroConsumidorFinal).Select(c => c.Id).FirstAsync();
        var idListaPrecio = await db.Clientes.Select(c => c.IdListaPrecio).FirstAsync();

        return new Contexto(resultado.IdTenant, resultado.IdPuntoVenta, idListaPrecio, idConsumidorFinal, admin);
    }

    private async Task<int> SembrarClienteAsync(Contexto ctx, string nombre, decimal saldo = 0m)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();

        var cliente = new Cliente
        {
            IdTenant = ctx.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = nombre,
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = ctx.IdListaPrecio, LimiteCredito = 0m,
            CreditoIlimitado = true, Saldo = saldo, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        return cliente.Id;
    }

    private static async Task<HttpResponseMessage> RegistrarAjusteAsync(
        Contexto ctx, int idCliente, SolicitudDeAjuste solicitud, HttpClient? cliente = null) =>
        await (cliente ?? ctx.Admin).PostAsJsonAsync($"/api/clientes/{idCliente}/cuenta-corriente/ajustes", solicitud);

    // ---- task 4.6: detalle requerido, signo, atomicidad ---------------------------------------

    [Fact]
    public async Task UnAjusteSinDetalleEsRechazadoAntesDeCualquierEscritura()
    {
        var ctx = await PrepararAsync(nameof(UnAjusteSinDetalleEsRechazadoAntesDeCualquierEscritura));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente ajuste sin detalle", saldo: 300m);

        var respuesta = await RegistrarAjusteAsync(ctx, idCliente, new SolicitudDeAjuste(ctx.IdPuntoVenta, -50m, ""));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo);
        Assert.Equal("ajuste_detalle_requerido", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.MovimientosCuentaCorriente.CountAsync(m => m.IdCliente == idCliente));
        var saldo = await db.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).FirstAsync();
        Assert.Equal(300m, saldo);
    }

    [Fact]
    public async Task UnAjusteConImporteCeroEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnAjusteConImporteCeroEsRechazado));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente ajuste importe cero");

        var respuesta = await RegistrarAjusteAsync(ctx, idCliente, new SolicitudDeAjuste(ctx.IdPuntoVenta, 0m, "Detalle válido"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo);
        Assert.Equal("ajuste_importe_invalido", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnAjusteNegativoReduceElSaldo()
    {
        var ctx = await PrepararAsync(nameof(UnAjusteNegativoReduceElSaldo));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente ajuste negativo", saldo: 300m);

        var respuesta = await RegistrarAjusteAsync(
            ctx, idCliente, new SolicitudDeAjuste(ctx.IdPuntoVenta, -50m, "Descuento por reclamo"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var saldo = await db.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).FirstAsync();
        Assert.Equal(250m, saldo);
    }

    [Fact]
    public async Task UnAjustePositivoAumentaElSaldo()
    {
        var ctx = await PrepararAsync(nameof(UnAjustePositivoAumentaElSaldo));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente ajuste positivo", saldo: 100m);

        var respuesta = await RegistrarAjusteAsync(
            ctx, idCliente, new SolicitudDeAjuste(ctx.IdPuntoVenta, 40m, "Cargo por diferencia de precio"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var movimiento = JsonSerializer.Deserialize<MovimientoDeCuentaCorriente>(cuerpo, OpcionesJson)!;

        // spec: Ajuste snapshots the resulting saldo — saldo_resultante == Cliente.Saldo tras el
        // UPDATE, misma transacción.
        Assert.Equal(140m, movimiento.SaldoResultante);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var saldo = await db.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).FirstAsync();
        Assert.Equal(140m, saldo);
    }

    [Fact]
    public async Task UnAjusteManualCarecerDeComprobanteYQuedaDistinguibleDeUnaAnulacion()
    {
        var ctx = await PrepararAsync(nameof(UnAjusteManualCarecerDeComprobanteYQuedaDistinguibleDeUnaAnulacion));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente ajuste sin comprobante", saldo: 200m);

        var respuesta = await RegistrarAjusteAsync(
            ctx, idCliente, new SolicitudDeAjuste(ctx.IdPuntoVenta, -20m, "Ajuste manual de prueba"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimiento = await db.MovimientosCuentaCorriente.Where(m => m.IdCliente == idCliente).SingleAsync();

        // spec: A manual ajuste carries no comprobante link.
        Assert.Equal(TipoMovimientoCc.Ajuste, movimiento.Tipo);
        Assert.Null(movimiento.IdComprobanteVenta);
        Assert.Null(movimiento.IdPagoComprobante);
        Assert.Equal("Ajuste manual de prueba", movimiento.Detalle);
        Assert.Equal(ctx.IdPuntoVenta, movimiento.IdPuntoVenta);
    }

    [Fact]
    public async Task UnClienteConsumidorFinalEsRechazadoDelAjuste()
    {
        var ctx = await PrepararAsync(nameof(UnClienteConsumidorFinalEsRechazadoDelAjuste));

        var respuesta = await RegistrarAjusteAsync(
            ctx, ctx.IdConsumidorFinal, new SolicitudDeAjuste(ctx.IdPuntoVenta, 50m, "Ajuste sobre CF"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo);
        Assert.Equal("cliente_sin_cuenta_corriente", problema.GetProperty("codigo").GetString());
    }

    // ---- task 4.7: matriz de autorización ------------------------------------------------------

    private async Task<HttpClient> CrearUsuarioConRolAsync(Contexto ctx, string nombre, RolConocido rol)
    {
        var mail = $"{nombre.ToLowerInvariant()}@ways.test";
        var alta = await ctx.Admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario(nombre, mail, (int)rol, PasswordUsuario));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordUsuario));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    [Fact]
    public async Task UnSupervisorPuedePostearUnAjuste()
    {
        var ctx = await PrepararAsync(nameof(UnSupervisorPuedePostearUnAjuste));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente ajuste supervisor");
        using var supervisor = await CrearUsuarioConRolAsync(ctx, "supervisor-ajuste", RolConocido.Supervisor);

        var respuesta = await RegistrarAjusteAsync(
            ctx, idCliente, new SolicitudDeAjuste(ctx.IdPuntoVenta, 10m, "Ajuste de supervisor"), supervisor);

        Assert.True(respuesta.IsSuccessStatusCode);
    }

    [Fact]
    public async Task UnAdminPuedePostearUnAjuste()
    {
        var ctx = await PrepararAsync(nameof(UnAdminPuedePostearUnAjuste));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente ajuste admin");

        var respuesta = await RegistrarAjusteAsync(
            ctx, idCliente, new SolicitudDeAjuste(ctx.IdPuntoVenta, 10m, "Ajuste de admin"));

        Assert.True(respuesta.IsSuccessStatusCode);
    }

    [Fact]
    public async Task UnVendedorEsRechazadoDelAjuste()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoDelAjuste));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente ajuste vendedor");
        using var vendedor = await CrearUsuarioConRolAsync(ctx, "vendedor-ajuste", RolConocido.Vendedor);

        var respuesta = await RegistrarAjusteAsync(
            ctx, idCliente, new SolicitudDeAjuste(ctx.IdPuntoVenta, 10m, "Ajuste de vendedor"), vendedor);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnAjusteSinTokenDevuelve401()
    {
        using var cliente = fixture.CreateClient();

        var respuesta = await cliente.PostAsJsonAsync(
            "/api/clientes/1/cuenta-corriente/ajustes", new SolicitudDeAjuste(1, 10m, "Ajuste sin token"));

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnAjusteContraUnClienteDeOtroTenantDevuelve404()
    {
        var ctxA = await PrepararAsync(nameof(UnAjusteContraUnClienteDeOtroTenantDevuelve404) + "-A");
        var idClienteDeA = await SembrarClienteAsync(ctxA, "Cliente A cross-tenant ajuste");

        var ctxB = await PrepararAsync(nameof(UnAjusteContraUnClienteDeOtroTenantDevuelve404) + "-B");

        var respuesta = await RegistrarAjusteAsync(
            ctxB, idClienteDeA, new SolicitudDeAjuste(ctxB.IdPuntoVenta, 10m, "Ajuste cross-tenant"));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }
}
