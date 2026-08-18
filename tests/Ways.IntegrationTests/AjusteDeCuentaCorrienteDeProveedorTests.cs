using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.CuentaCorriente;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Catalogos;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-15-cc-proveedores-ledger, Slice 5 (tasks 5.7-5.11, design: API Surface;
/// Transactions — AJUSTE MANUAL): <c>POST /api/proveedores/{id}/cuenta-corriente/ajustes</c>
/// punta a punta — matriz 403/200, rechazos de detalle/importe, orden de 404s ANTES de la
/// transacción sin turno, escritura del movimiento y backstops de FK reachable desde el request.
/// Mutation target #27 (task 5.12) — evidencia de mutación registrada en el PR body, no en este
/// archivo; <see cref="UnVendedorEsRechazadoDelAjuste"/> es su discriminador.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class AjusteDeCuentaCorrienteDeProveedorTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordUsuario = "una-contraseña-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(int IdTenant, int IdPuntoVenta, HttpClient Admin, HttpClient Root, int IdProveedor);

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

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var proveedor = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = nombre, IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        return new Contexto(resultado.IdTenant, resultado.IdPuntoVenta, admin, root, proveedor.Id);
    }

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

    private static async Task<HttpResponseMessage> RegistrarAjusteAsync(
        Contexto ctx, int idProveedor, SolicitudDeAjusteDeProveedor solicitud, HttpClient? cliente = null) =>
        await (cliente ?? ctx.Admin).PostAsJsonAsync(
            $"/api/proveedores/{idProveedor}/cuenta-corriente/ajustes", solicitud);

    private async Task<decimal> LeerSaldoAsync(Contexto ctx, int idProveedor)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.Proveedores.Where(p => p.Id == idProveedor).Select(p => p.Saldo).FirstAsync();
    }

    private async Task<int> ContarMovimientosAsync(Contexto ctx, int idProveedor)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.MovimientosCuentaCorrienteProveedor.CountAsync(m => m.IdProveedor == idProveedor);
    }

    // ---- task 5.8: detalle/importe rejections --------------------------------------------------

    [Fact]
    public async Task UnAjusteSinDetalleEsRechazadoAntesDeCualquierEscritura()
    {
        var ctx = await PrepararAsync(nameof(UnAjusteSinDetalleEsRechazadoAntesDeCualquierEscritura));

        var respuesta = await RegistrarAjusteAsync(
            ctx, ctx.IdProveedor, new SolicitudDeAjusteDeProveedor(ctx.IdPuntoVenta, -50m, ""));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo);
        Assert.Equal("ajuste_detalle_requerido", problema.GetProperty("codigo").GetString());

        Assert.Equal(0, await ContarMovimientosAsync(ctx, ctx.IdProveedor));
        Assert.Equal(0m, await LeerSaldoAsync(ctx, ctx.IdProveedor));
    }

    [Fact]
    public async Task UnAjusteConImporteCeroEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(UnAjusteConImporteCeroEsRechazado));

        var respuesta = await RegistrarAjusteAsync(
            ctx, ctx.IdProveedor, new SolicitudDeAjusteDeProveedor(ctx.IdPuntoVenta, 0m, "Detalle válido"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo);
        Assert.Equal("ajuste_importe_invalido", problema.GetProperty("codigo").GetString());
    }

    // ---- task 5.9 / 5.11: PV apócrifo — 404 ANTES de la transacción sin turno -------------------

    [Fact]
    public async Task UnPuntoDeVentaInexistenteDevuelve404AntesDeAbrirLaTransaccion()
    {
        var ctx = await PrepararAsync(nameof(UnPuntoDeVentaInexistenteDevuelve404AntesDeAbrirLaTransaccion));

        var respuesta = await RegistrarAjusteAsync(
            ctx, ctx.IdProveedor, new SolicitudDeAjusteDeProveedor(999999, -50m, "PV apócrifo"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo);
        Assert.Equal("no_encontrado", problema.GetProperty("codigo").GetString());

        // Ningún movimiento ni cambio de saldo — el 404 corre ANTES del BEGIN (sin turno del que
        // derivar un rollback "parcial": la transacción nunca se abre).
        Assert.Equal(0, await ContarMovimientosAsync(ctx, ctx.IdProveedor));
        Assert.Equal(0m, await LeerSaldoAsync(ctx, ctx.IdProveedor));
    }

    // ---- task 5.11: fk_..._proveedor — 404 traducido, no el tipo de excepción --------------------

    [Fact]
    public async Task UnProveedorInexistenteDevuelve404ConElCodigoDeDominioTraducido()
    {
        var ctx = await PrepararAsync(nameof(UnProveedorInexistenteDevuelve404ConElCodigoDeDominioTraducido));

        var respuesta = await RegistrarAjusteAsync(
            ctx, 999999, new SolicitudDeAjusteDeProveedor(ctx.IdPuntoVenta, -50m, "Proveedor apócrifo"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo);
        Assert.Equal("no_encontrado", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnAjusteContraUnProveedorDeOtroTenantDevuelve404()
    {
        var ctxA = await PrepararAsync(nameof(UnAjusteContraUnProveedorDeOtroTenantDevuelve404) + "-A");
        var ctxB = await PrepararAsync(nameof(UnAjusteContraUnProveedorDeOtroTenantDevuelve404) + "-B");

        var respuesta = await RegistrarAjusteAsync(
            ctxB, ctxA.IdProveedor, new SolicitudDeAjusteDeProveedor(ctxB.IdPuntoVenta, 10m, "Ajuste cross-tenant"));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    // ---- task 5.10: coverage — el ajuste escribe y mueve el saldo --------------------------------

    [Fact]
    public async Task UnSupervisorPosteaUnAjusteYElSaldoDelProveedorBaja()
    {
        var ctx = await PrepararAsync(nameof(UnSupervisorPosteaUnAjusteYElSaldoDelProveedorBaja));
        using var supervisor = await CrearUsuarioConRolAsync(ctx, "supervisor-ajuste-proveedor", RolConocido.Supervisor);

        // Deuda previa != 0 (ajuste manual anterior), para que el saldo no dependa de una
        // coincidencia con `-200` (mutation-proof-tests rule 6).
        var previo = await RegistrarAjusteAsync(
            ctx, ctx.IdProveedor, new SolicitudDeAjusteDeProveedor(ctx.IdPuntoVenta, 500m, "Deuda previa"), supervisor);
        Assert.Equal(HttpStatusCode.Created, previo.StatusCode);

        var respuesta = await RegistrarAjusteAsync(
            ctx, ctx.IdProveedor, new SolicitudDeAjusteDeProveedor(ctx.IdPuntoVenta, -200m, "Descuento por reclamo"),
            supervisor);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var movimiento = JsonSerializer.Deserialize<MovimientoDeCuentaDeProveedor>(cuerpo, OpcionesJson)!;

        // spec: Saldo Is The Single-Write-Authority Cache — saldo_resultante == proveedores.saldo
        // tras el UPDATE, misma transacción. 500 - 200 = 300, nunca -200 a secas.
        Assert.Equal(300m, movimiento.SaldoResultante);
        Assert.Equal(TipoMovimientoCcProveedor.Ajuste, movimiento.Tipo);
        Assert.Null(movimiento.IdComprobanteCompra);
        Assert.Null(movimiento.IdGasto);
        Assert.Equal("Descuento por reclamo", movimiento.Detalle);
        Assert.Equal(EtiquetaDeAjuste.Manual, movimiento.Etiqueta);

        Assert.Equal(300m, await LeerSaldoAsync(ctx, ctx.IdProveedor));
        Assert.Equal(2, await ContarMovimientosAsync(ctx, ctx.IdProveedor));
    }

    // ---- task 5.7: matriz 403/200 ------------------------------------------------------------

    [Fact]
    public async Task UnAdminPuedePostearUnAjuste()
    {
        var ctx = await PrepararAsync(nameof(UnAdminPuedePostearUnAjuste));

        var respuesta = await RegistrarAjusteAsync(
            ctx, ctx.IdProveedor, new SolicitudDeAjusteDeProveedor(ctx.IdPuntoVenta, 10m, "Ajuste de admin"));

        Assert.True(respuesta.IsSuccessStatusCode);
    }

    [Fact]
    public async Task UnSupervisorPuedePostearUnAjuste()
    {
        var ctx = await PrepararAsync(nameof(UnSupervisorPuedePostearUnAjuste));
        using var supervisor = await CrearUsuarioConRolAsync(ctx, "supervisor-ajuste-matriz", RolConocido.Supervisor);

        var respuesta = await RegistrarAjusteAsync(
            ctx, ctx.IdProveedor, new SolicitudDeAjusteDeProveedor(ctx.IdPuntoVenta, 10m, "Ajuste de supervisor"),
            supervisor);

        Assert.True(respuesta.IsSuccessStatusCode);
    }

    [Fact]
    public async Task UnVendedorEsRechazadoDelAjuste()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoDelAjuste));
        using var vendedor = await CrearUsuarioConRolAsync(ctx, "vendedor-ajuste-proveedor", RolConocido.Vendedor);

        var respuesta = await RegistrarAjusteAsync(
            ctx, ctx.IdProveedor, new SolicitudDeAjusteDeProveedor(ctx.IdPuntoVenta, 10m, "Ajuste de vendedor"),
            vendedor);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
        Assert.Equal(0, await ContarMovimientosAsync(ctx, ctx.IdProveedor));
    }

    [Fact]
    public async Task UnRootEsRechazadoDelAjuste()
    {
        var ctx = await PrepararAsync(nameof(UnRootEsRechazadoDelAjuste));

        var respuesta = await RegistrarAjusteAsync(
            ctx, ctx.IdProveedor, new SolicitudDeAjusteDeProveedor(ctx.IdPuntoVenta, 10m, "Ajuste de root"), ctx.Root);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnAjusteSinTokenDevuelve401()
    {
        using var cliente = fixture.CreateClient();

        var respuesta = await cliente.PostAsJsonAsync(
            "/api/proveedores/1/cuenta-corriente/ajustes", new SolicitudDeAjusteDeProveedor(1, 10m, "Ajuste sin token"));

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
    }
}
