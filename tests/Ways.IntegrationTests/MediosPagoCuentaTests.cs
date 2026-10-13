using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ways.Api.Seguridad;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Catalogos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// La cuenta de tesorería, la comisión y las retenciones del medio de pago punta a punta contra Postgres real:
/// contrato y semántica de omisión, reglas de valores y de comportamiento, reglas de la cuenta (existencia, empresa,
/// inactiva), carrera contra la baja de la cuenta y backstops de la base.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class MediosPagoCuentaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string Medios = "/api/catalogos/medios-pago";
    private const string Cuentas = "/api/cuentas-tesoreria";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed record Contexto(int IdTenant, int IdEmpresa, HttpClient Admin);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var alta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        return new Contexto(resultado.IdTenant, resultado.IdEmpresa, admin);
    }

    private static async Task<string?> CodigoAsync(HttpResponseMessage respuesta) =>
        (await respuesta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString();

    private static Dictionary<string, object?> Cuerpo(
        string nombre, int? idEmpresa = null, string comportamiento = "Electronico", params (string Clave, object? Valor)[] extras)
    {
        var cuerpo = new Dictionary<string, object?>
        {
            ["nombre"] = nombre,
            ["idEmpresa"] = idEmpresa,
            ["orden"] = 9,
            ["comportamiento"] = comportamiento,
            ["admiteVuelto"] = false,
            ["requiereReferencia"] = false,
            ["recargoPorcentaje"] = null
        };

        foreach (var (clave, valor) in extras)
        {
            cuerpo[clave] = valor;
        }

        return cuerpo;
    }

    private static async Task<CuentaTesoreriaListado> CrearCuentaAsync(
        Contexto ctx, string nombre, int? idEmpresa = null, bool activo = true)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            Cuentas, new AltaDeCuentaTesoreria(idEmpresa ?? ctx.IdEmpresa, nombre, 0m, activo));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<CuentaTesoreriaListado>())!;
    }

    private static async Task<MedioPagoListado> CrearMedioOkAsync(Contexto ctx, Dictionary<string, object?> cuerpo)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(Medios, cuerpo);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<MedioPagoListado>(OpcionesJson))!;
    }

    private static async Task<MedioPagoListado> LeerAsync(Contexto ctx, int id) =>
        (await ctx.Admin.GetFromJsonAsync<MedioPagoListado>($"{Medios}/{id}", OpcionesJson))!;

    private async Task<int> CrearEmpresaAsync(int idTenant, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var empresa = new Empresa { IdTenant = idTenant, RazonSocial = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();
        return empresa.Id;
    }

    private async Task EjecutarCrudoAsync(int idTenant, string sql, params (string Nombre, object Valor)[] parametros)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);
        await using var comando = new NpgsqlCommand(sql, conexion);
        foreach (var (nombre, valor) in parametros)
        {
            comando.Parameters.AddWithValue(nombre, valor);
        }

        await comando.ExecuteNonQueryAsync();
    }

    private async Task<HttpClient> ClienteConRolAsync(Contexto ctx, string nombre, RolConocido rol)
    {
        var sufijo = rol.ToString().ToLowerInvariant();
        var mail = $"{nombre.ToLowerInvariant()}-{sufijo}-{Guid.NewGuid():N}@ways.test";
        var alta = await ctx.Admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario($"{sufijo}-{Guid.NewGuid():N}"[..16], mail, (int)rol, "otro-rol-password-larga"));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, "otro-rol-password-larga"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    /// <summary>Espera a que haya una sesión bloqueada en un lock cuya consulta contiene el fragmento dado: así no pasa
    /// porque otra sesión de la base esté esperando por su cuenta.</summary>
    private async Task EsperarUnaSesionBloqueadaAsync(string fragmentoDeConsulta)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND pid <> pg_backend_pid() " +
            "AND datname = current_database() AND query LIKE $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = $"%{fragmentoDeConsulta}%" });

        var limite = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < limite)
        {
            if ((long)(await comando.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Ninguna sesión con '{fragmentoDeConsulta}' llegó a quedar bloqueada esperando el lock.");
    }

    [Fact]
    public async Task ElAltaGuardaLaCuentaYLosPorcentajesYElListadoLosDevuelveConElNombreDeLaCuenta()
    {
        var ctx = await PrepararAsync(nameof(ElAltaGuardaLaCuentaYLosPorcentajesYElListadoLosDevuelveConElNombreDeLaCuenta));
        using var _ = ctx.Admin;
        var cuenta = await CrearCuentaAsync(ctx, "Mercado Pago");

        var creado = await CrearMedioOkAsync(ctx, Cuerpo(
            "QR MP", ctx.IdEmpresa, "Electronico",
            ("idCuentaTesoreria", cuenta.Id), ("comisionPorcentaje", 4.5m), ("retencionIibbPorcentaje", 1.25m),
            ("retencionIvaPorcentaje", 0.001m), ("retencionGananciasPorcentaje", 0m)));

        Assert.Equal(cuenta.Id, creado.IdCuentaTesoreria);
        Assert.Equal("Mercado Pago", creado.NombreCuentaTesoreria);
        Assert.False(creado.CuentaTesoreriaInactiva);
        Assert.Equal((4.5m, 1.25m, 0.001m, 0m),
            (creado.ComisionPorcentaje, creado.RetencionIibbPorcentaje, creado.RetencionIvaPorcentaje, creado.RetencionGananciasPorcentaje));

        var listado = await ctx.Admin.GetFromJsonAsync<List<MedioPagoListado>>($"{Medios}?incluirInactivos=true", OpcionesJson);
        var fila = Assert.Single(listado!, m => m.Id == creado.Id);
        Assert.Equal(creado, fila);

        var sinCuenta = Assert.Single(listado!, m => m.Comportamiento == ComportamientoMedioPago.Efectivo);
        Assert.Null(sinCuenta.IdCuentaTesoreria);
        Assert.Null(sinCuenta.NombreCuentaTesoreria);
        Assert.Equal(0m, sinCuenta.ComisionPorcentaje);
    }

    [Fact]
    public async Task UnaEdicionSinLosCamposConservaLaCuentaYLosPorcentajesYUnValorEnviadoLosAplica()
    {
        var ctx = await PrepararAsync(nameof(UnaEdicionSinLosCamposConservaLaCuentaYLosPorcentajesYUnValorEnviadoLosAplica));
        using var _ = ctx.Admin;
        var cuenta = await CrearCuentaAsync(ctx, "Banco");
        var medio = await CrearMedioOkAsync(ctx, Cuerpo(
            "Transf", ctx.IdEmpresa, "Electronico",
            ("idCuentaTesoreria", cuenta.Id), ("comisionPorcentaje", 3m), ("retencionIibbPorcentaje", 2m)));

        var sinCampos = await ctx.Admin.PutAsJsonAsync($"{Medios}/{medio.Id}", Cuerpo("Transf 2", ctx.IdEmpresa));
        Assert.Equal(HttpStatusCode.OK, sinCampos.StatusCode);
        var conservado = (await sinCampos.Content.ReadFromJsonAsync<MedioPagoListado>(OpcionesJson))!;
        Assert.Equal((cuenta.Id, 3m, 2m), (conservado.IdCuentaTesoreria, conservado.ComisionPorcentaje, conservado.RetencionIibbPorcentaje));

        var soloUno = await ctx.Admin.PutAsJsonAsync(
            $"{Medios}/{medio.Id}", Cuerpo("Transf 2", ctx.IdEmpresa, "Electronico", ("comisionPorcentaje", 0m)));
        var cambiado = (await soloUno.Content.ReadFromJsonAsync<MedioPagoListado>(OpcionesJson))!;
        Assert.Equal((cuenta.Id, 0m, 2m), (cambiado.IdCuentaTesoreria, cambiado.ComisionPorcentaje, cambiado.RetencionIibbPorcentaje));
    }

    [Fact]
    public async Task LaCuentaEnviadaEnNullSeQuitaYAusenteNoLaToca()
    {
        var ctx = await PrepararAsync(nameof(LaCuentaEnviadaEnNullSeQuitaYAusenteNoLaToca));
        using var _ = ctx.Admin;
        var cuenta = await CrearCuentaAsync(ctx, "Banco");
        var medio = await CrearMedioOkAsync(ctx, Cuerpo(
            "Transf", ctx.IdEmpresa, "Electronico", ("idCuentaTesoreria", cuenta.Id), ("comisionPorcentaje", 3m)));

        var ausente = await ctx.Admin.PutAsJsonAsync($"{Medios}/{medio.Id}", Cuerpo("Transf", ctx.IdEmpresa));
        Assert.Equal(cuenta.Id, (await ausente.Content.ReadFromJsonAsync<MedioPagoListado>(OpcionesJson))!.IdCuentaTesoreria);

        var enNull = await ctx.Admin.PutAsJsonAsync(
            $"{Medios}/{medio.Id}", Cuerpo("Transf", ctx.IdEmpresa, "Electronico", ("idCuentaTesoreria", null)));
        var quitada = (await enNull.Content.ReadFromJsonAsync<MedioPagoListado>(OpcionesJson))!;
        Assert.Null(quitada.IdCuentaTesoreria);
        Assert.Null(quitada.NombreCuentaTesoreria);
        Assert.Equal(3m, quitada.ComisionPorcentaje);
    }

    [Theory]
    [InlineData("comisionPorcentaje", "-1", "porcentaje_cuenta_invalido")]
    [InlineData("retencionIibbPorcentaje", "100.001", "porcentaje_cuenta_invalido")]
    [InlineData("retencionIvaPorcentaje", "1.2345", "porcentaje_cuenta_invalido")]
    [InlineData("retencionGananciasPorcentaje", "101", "porcentaje_cuenta_invalido")]
    public async Task UnPorcentajeInvalidoDa400ConSuCodigoYLaFilaQuedaIntacta(string campo, string crudo, string codigo)
    {
        var ctx = await PrepararAsync("Pct" + campo[..8] + crudo.Replace(".", "p").Replace("-", "n"));
        using var _ = ctx.Admin;
        var valor = decimal.Parse(crudo, System.Globalization.CultureInfo.InvariantCulture);
        var medio = await CrearMedioOkAsync(ctx, Cuerpo("Transf", ctx.IdEmpresa, "Electronico", ("comisionPorcentaje", 1m)));

        var alCrear = await ctx.Admin.PostAsJsonAsync(Medios, Cuerpo("Otro", ctx.IdEmpresa, "Electronico", (campo, valor)));
        Assert.Equal(HttpStatusCode.BadRequest, alCrear.StatusCode);
        Assert.Equal(codigo, await CodigoAsync(alCrear));

        var alEditar = await ctx.Admin.PutAsJsonAsync($"{Medios}/{medio.Id}", Cuerpo("Transf", ctx.IdEmpresa, "Electronico", (campo, valor)));
        Assert.Equal(HttpStatusCode.BadRequest, alEditar.StatusCode);
        Assert.Equal(codigo, await CodigoAsync(alEditar));

        Assert.Equal(1m, (await LeerAsync(ctx, medio.Id)).ComisionPorcentaje);
    }

    [Fact]
    public async Task LaSumaDeLosCuatroQueSuperaCienDa400YExactamenteCienSeAcepta()
    {
        var ctx = await PrepararAsync(nameof(LaSumaDeLosCuatroQueSuperaCienDa400YExactamenteCienSeAcepta));
        using var _ = ctx.Admin;

        var pasada = await ctx.Admin.PostAsJsonAsync(Medios, Cuerpo(
            "Cien", ctx.IdEmpresa, "Electronico",
            ("comisionPorcentaje", 40m), ("retencionIibbPorcentaje", 30m), ("retencionIvaPorcentaje", 20m), ("retencionGananciasPorcentaje", 10m)));
        Assert.Equal(HttpStatusCode.Created, pasada.StatusCode);

        var excedida = await ctx.Admin.PostAsJsonAsync(Medios, Cuerpo(
            "Mas de cien", ctx.IdEmpresa, "Electronico",
            ("comisionPorcentaje", 40m), ("retencionIibbPorcentaje", 30m), ("retencionIvaPorcentaje", 20m), ("retencionGananciasPorcentaje", 10.001m)));
        Assert.Equal(HttpStatusCode.BadRequest, excedida.StatusCode);
        Assert.Equal("porcentajes_cuenta_suma_invalida", await CodigoAsync(excedida));
    }

    [Fact]
    public async Task UnMedioNoElectronicoRechazaUnaCuentaOUnPorcentajeEnviadosYAceptaCeroYNull()
    {
        var ctx = await PrepararAsync(nameof(UnMedioNoElectronicoRechazaUnaCuentaOUnPorcentajeEnviadosYAceptaCeroYNull));
        using var _ = ctx.Admin;
        var cuenta = await CrearCuentaAsync(ctx, "Banco");

        foreach (var comportamiento in new[] { "Efectivo", "CuentaCorriente" })
        {
            var conCuenta = await ctx.Admin.PostAsJsonAsync(
                Medios, Cuerpo("A" + comportamiento, ctx.IdEmpresa, comportamiento, ("idCuentaTesoreria", cuenta.Id)));
            Assert.Equal(HttpStatusCode.BadRequest, conCuenta.StatusCode);
            Assert.Equal("cuenta_solo_electronico", await CodigoAsync(conCuenta));

            var conPorcentaje = await ctx.Admin.PostAsJsonAsync(
                Medios, Cuerpo("B" + comportamiento, ctx.IdEmpresa, comportamiento, ("retencionIvaPorcentaje", 0.5m)));
            Assert.Equal(HttpStatusCode.BadRequest, conPorcentaje.StatusCode);
            Assert.Equal("cuenta_solo_electronico", await CodigoAsync(conPorcentaje));

            var enBlanco = await ctx.Admin.PostAsJsonAsync(
                Medios, Cuerpo("C" + comportamiento, ctx.IdEmpresa, comportamiento,
                    ("idCuentaTesoreria", null), ("comisionPorcentaje", 0m)));
            Assert.Equal(HttpStatusCode.Created, enBlanco.StatusCode);
        }
    }

    [Fact]
    public async Task CambiarElComportamientoSinEnviarLosCamposLosLimpiaYLaRespuestaLoMuestra()
    {
        var ctx = await PrepararAsync(nameof(CambiarElComportamientoSinEnviarLosCamposLosLimpiaYLaRespuestaLoMuestra));
        using var _ = ctx.Admin;
        var cuenta = await CrearCuentaAsync(ctx, "Banco");
        var medio = await CrearMedioOkAsync(ctx, Cuerpo(
            "Transf", ctx.IdEmpresa, "Electronico",
            ("idCuentaTesoreria", cuenta.Id), ("comisionPorcentaje", 3m), ("retencionGananciasPorcentaje", 1m)));

        var put = await ctx.Admin.PutAsJsonAsync($"{Medios}/{medio.Id}", Cuerpo("Transf", ctx.IdEmpresa, "Efectivo"));

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var respuesta = (await put.Content.ReadFromJsonAsync<MedioPagoListado>(OpcionesJson))!;
        Assert.Equal(ComportamientoMedioPago.Efectivo, respuesta.Comportamiento);
        Assert.Null(respuesta.IdCuentaTesoreria);
        Assert.Equal((0m, 0m, 0m, 0m),
            (respuesta.ComisionPorcentaje, respuesta.RetencionIibbPorcentaje, respuesta.RetencionIvaPorcentaje, respuesta.RetencionGananciasPorcentaje));
        Assert.Equal(respuesta, await LeerAsync(ctx, medio.Id));

        var conCuentaExplicita = await ctx.Admin.PutAsJsonAsync(
            $"{Medios}/{medio.Id}", Cuerpo("Transf", ctx.IdEmpresa, "Efectivo", ("idCuentaTesoreria", cuenta.Id)));
        Assert.Equal(HttpStatusCode.BadRequest, conCuentaExplicita.StatusCode);
        Assert.Equal("cuenta_solo_electronico", await CodigoAsync(conCuentaExplicita));
    }

    [Fact]
    public async Task UnaCuentaInexistenteOLaDeOtroTenantDanElMismo400ReferenciaInvalida()
    {
        var ctx = await PrepararAsync(nameof(UnaCuentaInexistenteOLaDeOtroTenantDanElMismo400ReferenciaInvalida));
        var otro = await PrepararAsync(nameof(UnaCuentaInexistenteOLaDeOtroTenantDanElMismo400ReferenciaInvalida) + "Otro");
        using var _ = ctx.Admin;
        using var _otro = otro.Admin;
        var ajena = await CrearCuentaAsync(otro, "Ajena");

        foreach (var idCuenta in new[] { 999_999, ajena.Id })
        {
            var respuesta = await ctx.Admin.PostAsJsonAsync(
                Medios, Cuerpo("QR" + idCuenta, ctx.IdEmpresa, "Electronico", ("idCuentaTesoreria", idCuenta)));

            Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
            Assert.Equal("referencia_invalida", await CodigoAsync(respuesta));
        }
    }

    [Fact]
    public async Task UnaCuentaDeOtraEmpresaQueLaDelMedioSeRechazaTantoAlAsignarComoAlCambiarLaEmpresa()
    {
        var ctx = await PrepararAsync(nameof(UnaCuentaDeOtraEmpresaQueLaDelMedioSeRechazaTantoAlAsignarComoAlCambiarLaEmpresa));
        using var _ = ctx.Admin;
        var otraEmpresa = await CrearEmpresaAsync(ctx.IdTenant, "Segunda SA");
        var cuentaDeLaPrimera = await CrearCuentaAsync(ctx, "De la primera");
        var cuentaDeLaSegunda = await CrearCuentaAsync(ctx, "De la segunda", otraEmpresa);

        var cruzada = await ctx.Admin.PostAsJsonAsync(
            Medios, Cuerpo("Cruzado", ctx.IdEmpresa, "Electronico", ("idCuentaTesoreria", cuentaDeLaSegunda.Id)));
        Assert.Equal(HttpStatusCode.BadRequest, cruzada.StatusCode);
        Assert.Equal("cuenta_tesoreria_de_otra_empresa", await CodigoAsync(cruzada));

        var medio = await CrearMedioOkAsync(ctx, Cuerpo(
            "Propio", ctx.IdEmpresa, "Electronico", ("idCuentaTesoreria", cuentaDeLaPrimera.Id)));

        var mudado = await ctx.Admin.PutAsJsonAsync($"{Medios}/{medio.Id}", Cuerpo("Propio", otraEmpresa));
        Assert.Equal(HttpStatusCode.BadRequest, mudado.StatusCode);
        Assert.Equal("cuenta_tesoreria_de_otra_empresa", await CodigoAsync(mudado));
        Assert.Equal(ctx.IdEmpresa, (await LeerAsync(ctx, medio.Id)).IdEmpresa);
    }

    [Fact]
    public async Task UnMedioDeTodoElTenantTomaCuentaSoloSiElTenantTieneUnaEmpresaYElEditarOtraCosaNoLoBloquea()
    {
        var ctx = await PrepararAsync(nameof(UnMedioDeTodoElTenantTomaCuentaSoloSiElTenantTieneUnaEmpresaYElEditarOtraCosaNoLoBloquea));
        using var _ = ctx.Admin;
        var cuenta = await CrearCuentaAsync(ctx, "Unica");

        var medio = await CrearMedioOkAsync(ctx, Cuerpo(
            "Global", null, "Electronico", ("idCuentaTesoreria", cuenta.Id), ("comisionPorcentaje", 2m)));
        Assert.Equal(cuenta.Id, medio.IdCuentaTesoreria);

        await CrearEmpresaAsync(ctx.IdTenant, "Segunda SA");

        var renombrado = await ctx.Admin.PutAsJsonAsync($"{Medios}/{medio.Id}", Cuerpo("Global renombrado"));
        Assert.Equal(HttpStatusCode.OK, renombrado.StatusCode);

        var otra = await ctx.Admin.PostAsJsonAsync(
            Medios, Cuerpo("Global 2", null, "Electronico", ("idCuentaTesoreria", cuenta.Id)));
        Assert.Equal(HttpStatusCode.BadRequest, otra.StatusCode);
        Assert.Equal("cuenta_tesoreria_requiere_empresa", await CodigoAsync(otra));
    }

    [Fact]
    public async Task UnaCuentaInactivaNoSeAsignaPeroUnMedioQueYaLaTeniaSigueGuardandoYElListadoLaMarca()
    {
        var ctx = await PrepararAsync(nameof(UnaCuentaInactivaNoSeAsignaPeroUnMedioQueYaLaTeniaSigueGuardandoYElListadoLaMarca));
        using var _ = ctx.Admin;
        var cuenta = await CrearCuentaAsync(ctx, "Se apaga");
        var inactiva = await CrearCuentaAsync(ctx, "Nace apagada", activo: false);
        var medio = await CrearMedioOkAsync(ctx, Cuerpo("Transf", ctx.IdEmpresa, "Electronico", ("idCuentaTesoreria", cuenta.Id)));

        var nueva = await ctx.Admin.PostAsJsonAsync(
            Medios, Cuerpo("Nuevo", ctx.IdEmpresa, "Electronico", ("idCuentaTesoreria", inactiva.Id)));
        Assert.Equal(HttpStatusCode.BadRequest, nueva.StatusCode);
        Assert.Equal("cuenta_tesoreria_inactiva", await CodigoAsync(nueva));

        var desactivada = await ctx.Admin.PutAsJsonAsync($"{Cuentas}/{cuenta.Id}", new EdicionDeCuentaTesoreria("Se apaga", 0m, false));
        Assert.Equal(HttpStatusCode.OK, desactivada.StatusCode);

        var guardado = await ctx.Admin.PutAsJsonAsync(
            $"{Medios}/{medio.Id}", Cuerpo("Transf 2", ctx.IdEmpresa, "Electronico", ("idCuentaTesoreria", cuenta.Id), ("comisionPorcentaje", 1m)));
        Assert.Equal(HttpStatusCode.OK, guardado.StatusCode);
        var fila = (await guardado.Content.ReadFromJsonAsync<MedioPagoListado>(OpcionesJson))!;
        Assert.Equal((cuenta.Id, true, 1m), (fila.IdCuentaTesoreria, fila.CuentaTesoreriaInactiva, fila.ComisionPorcentaje));

        // Cambiar la empresa del medio revalida la cuenta, pero sigue sin ser una asignación nueva.
        var aTodoElTenant = await ctx.Admin.PutAsJsonAsync($"{Medios}/{medio.Id}", Cuerpo("Transf 2", null));
        Assert.Equal(HttpStatusCode.OK, aTodoElTenant.StatusCode);
        Assert.Equal(cuenta.Id, (await LeerAsync(ctx, medio.Id)).IdCuentaTesoreria);
    }

    [Fact]
    public async Task UnaCuentaDadaDeBajaSeListaComoSinCuenta()
    {
        var ctx = await PrepararAsync(nameof(UnaCuentaDadaDeBajaSeListaComoSinCuenta));
        using var _ = ctx.Admin;
        var cuenta = await CrearCuentaAsync(ctx, "Se borra");
        var medio = await CrearMedioOkAsync(ctx, Cuerpo("Transf", ctx.IdEmpresa, "Electronico", ("idCuentaTesoreria", cuenta.Id)));

        await EjecutarCrudoAsync(
            ctx.IdTenant, "UPDATE cuentas_tesoreria SET deleted_at = now() WHERE id_cuenta_tesoreria = @id", ("id", cuenta.Id));

        var leido = await LeerAsync(ctx, medio.Id);
        Assert.Null(leido.IdCuentaTesoreria);
        Assert.Null(leido.NombreCuentaTesoreria);
        var listado = await ctx.Admin.GetFromJsonAsync<List<MedioPagoListado>>(Medios, OpcionesJson);
        Assert.Equal(leido, Assert.Single(listado!, m => m.Id == medio.Id));
    }

    [Fact]
    public async Task EditarUnIdInexistenteDa404AunConUnaCuentaOUnPorcentajeInvalidos()
    {
        var ctx = await PrepararAsync(nameof(EditarUnIdInexistenteDa404AunConUnaCuentaOUnPorcentajeInvalidos));
        using var _ = ctx.Admin;

        var put = await ctx.Admin.PutAsJsonAsync(
            $"{Medios}/999999",
            Cuerpo("X", ctx.IdEmpresa, "Efectivo", ("idCuentaTesoreria", 999_999), ("comisionPorcentaje", 500m)));

        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    [Fact]
    public async Task AsignarUnaCuentaMientrasSeLaDaDeBajaSinComitearTerminaEn400YElMedioNoApuntaAUnaCuentaBorrada()
    {
        var ctx = await PrepararAsync(nameof(AsignarUnaCuentaMientrasSeLaDaDeBajaSinComitearTerminaEn400YElMedioNoApuntaAUnaCuentaBorrada));
        using var _ = ctx.Admin;
        var cuenta = await CrearCuentaAsync(ctx, "Se da de baja");
        var medio = await CrearMedioOkAsync(ctx, Cuerpo("Transf", ctx.IdEmpresa, "Electronico"));

        // La baja toma FOR UPDATE sobre la fila de la cuenta y la marca, y queda sin comitear.
        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        await using var transaccion = await conexion.BeginTransactionAsync();
        await using (var baja = conexion.CreateCommand())
        {
            baja.Transaction = transaccion;
            baja.CommandText =
                "SELECT 1 FROM cuentas_tesoreria WHERE id_cuenta_tesoreria = $1 FOR UPDATE";
            baja.Parameters.Add(new NpgsqlParameter { Value = cuenta.Id });
            await baja.ExecuteScalarAsync();
            baja.CommandText = "UPDATE cuentas_tesoreria SET deleted_at = now() WHERE id_cuenta_tesoreria = $1";
            await baja.ExecuteNonQueryAsync();
        }

        var pedido = ctx.Admin.PutAsJsonAsync(
            $"{Medios}/{medio.Id}", Cuerpo("Transf", ctx.IdEmpresa, "Electronico", ("idCuentaTesoreria", cuenta.Id)));
        await EsperarUnaSesionBloqueadaAsync("FOR KEY SHARE");
        await transaccion.CommitAsync();

        var respuesta = await pedido;
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("referencia_invalida", await CodigoAsync(respuesta));
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Null((await db.MediosPago.SingleAsync(m => m.Id == medio.Id)).IdCuentaTesoreria);
    }

    [Fact]
    public async Task SoloElAdminVeLaCuentaLaComisionYLasRetencionesEnElListadoYEnElGet()
    {
        var ctx = await PrepararAsync(nameof(SoloElAdminVeLaCuentaLaComisionYLasRetencionesEnElListadoYEnElGet));
        using var _ = ctx.Admin;
        using var supervisor = await ClienteConRolAsync(ctx, "pv", RolConocido.Supervisor);
        using var vendedor = await ClienteConRolAsync(ctx, "pv", RolConocido.Vendedor);
        var cuenta = await CrearCuentaAsync(ctx, "Mercado Pago");
        var medio = await CrearMedioOkAsync(ctx, Cuerpo(
            "QR", ctx.IdEmpresa, "Electronico",
            ("idCuentaTesoreria", cuenta.Id), ("comisionPorcentaje", 4.5m), ("retencionIibbPorcentaje", 1m),
            ("retencionIvaPorcentaje", 2m), ("retencionGananciasPorcentaje", 3m)));

        async Task<List<MedioPagoListado>> ListarComoAsync(HttpClient cliente) =>
            (await cliente.GetFromJsonAsync<List<MedioPagoListado>>($"{Medios}?incluirInactivos=true", OpcionesJson))!;

        var delAdmin = await LeerAsync(ctx, medio.Id);
        Assert.Equal(cuenta.Id, delAdmin.IdCuentaTesoreria);
        Assert.Equal(4.5m, delAdmin.ComisionPorcentaje);
        Assert.Equal(3m, delAdmin.RetencionGananciasPorcentaje);
        Assert.Equal(delAdmin, Assert.Single(await ListarComoAsync(ctx.Admin), m => m.Id == medio.Id));
        Assert.Equal("Mercado Pago", delAdmin.NombreCuentaTesoreria);
        Assert.False(delAdmin.CuentaTesoreriaInactiva);

        var sinCuenta = await CrearMedioOkAsync(ctx, Cuerpo("Sin cuenta", ctx.IdEmpresa));
        Assert.False(sinCuenta.CuentaTesoreriaInactiva);
        Assert.False((await LeerAsync(ctx, sinCuenta.Id)).CuentaTesoreriaInactiva);
        Assert.False(Assert.Single(await ListarComoAsync(ctx.Admin), m => m.Id == sinCuenta.Id).CuentaTesoreriaInactiva);

        foreach (var cliente in new[] { supervisor, vendedor })
        {
            var porGet = (await cliente.GetFromJsonAsync<MedioPagoListado>($"{Medios}/{medio.Id}", OpcionesJson))!;
            var enLista = Assert.Single(await ListarComoAsync(cliente), m => m.Id == medio.Id);

            foreach (var visto in new[] { porGet, enLista })
            {
                Assert.Equal("QR", visto.Nombre);
                Assert.Null(visto.IdCuentaTesoreria);
                Assert.Null(visto.ComisionPorcentaje);
                Assert.Null(visto.RetencionIibbPorcentaje);
                Assert.Null(visto.RetencionIvaPorcentaje);
                Assert.Null(visto.RetencionGananciasPorcentaje);
                Assert.Null(visto.NombreCuentaTesoreria);
                Assert.Null(visto.CuentaTesoreriaInactiva);
            }
        }
    }

    [Fact]
    public async Task UnProblemaDeNombreSeReportaAntesQueUnoDeCuentaComoEnElFlujoGenerico()
    {
        var ctx = await PrepararAsync(nameof(UnProblemaDeNombreSeReportaAntesQueUnoDeCuentaComoEnElFlujoGenerico));
        using var _ = ctx.Admin;
        await CrearMedioOkAsync(ctx, Cuerpo("Existente", ctx.IdEmpresa));
        var otro = await CrearMedioOkAsync(ctx, Cuerpo("Otro", ctx.IdEmpresa));

        var sinNombre = await ctx.Admin.PostAsJsonAsync(
            Medios, Cuerpo("  ", ctx.IdEmpresa, "Electronico", ("idCuentaTesoreria", 999_999)));
        Assert.Equal(HttpStatusCode.BadRequest, sinNombre.StatusCode);
        Assert.Equal("nombre_requerido", await CodigoAsync(sinNombre));

        var duplicadoConCuentaInexistente = await ctx.Admin.PutAsJsonAsync(
            $"{Medios}/{otro.Id}", Cuerpo("Existente", ctx.IdEmpresa, "Electronico", ("idCuentaTesoreria", 999_999)));
        Assert.Equal(HttpStatusCode.Conflict, duplicadoConCuentaInexistente.StatusCode);
        Assert.Equal("nombre_duplicado", await CodigoAsync(duplicadoConCuentaInexistente));

        var nombreMalYCuentaEnEfectivo = await ctx.Admin.PostAsJsonAsync(
            Medios, Cuerpo("", ctx.IdEmpresa, "Efectivo", ("idCuentaTesoreria", 1)));
        Assert.Equal("nombre_requerido", await CodigoAsync(nombreMalYCuentaEnEfectivo));
    }

    [Fact]
    public async Task RenombrarUnMedioDeUnaEmpresaConCuentaFuncionaSiSeDevuelveSuEmpresaYSinElla400()
    {
        var ctx = await PrepararAsync(nameof(RenombrarUnMedioDeUnaEmpresaConCuentaFuncionaSiSeDevuelveSuEmpresaYSinElla400));
        using var _ = ctx.Admin;
        await CrearEmpresaAsync(ctx.IdTenant, "Segunda SA");
        var cuenta = await CrearCuentaAsync(ctx, "De la primera");
        var medio = await CrearMedioOkAsync(ctx, Cuerpo(
            "Propio", ctx.IdEmpresa, "Electronico", ("idCuentaTesoreria", cuenta.Id), ("comisionPorcentaje", 2m)));

        var conEmpresa = await ctx.Admin.PutAsJsonAsync($"{Medios}/{medio.Id}", Cuerpo("Propio renombrado", ctx.IdEmpresa));
        Assert.Equal(HttpStatusCode.OK, conEmpresa.StatusCode);
        var guardado = (await conEmpresa.Content.ReadFromJsonAsync<MedioPagoListado>(OpcionesJson))!;
        Assert.Equal((ctx.IdEmpresa, cuenta.Id, 2m), (guardado.IdEmpresa, guardado.IdCuentaTesoreria, guardado.ComisionPorcentaje));

        var sinEmpresa = await ctx.Admin.PutAsJsonAsync($"{Medios}/{medio.Id}", Cuerpo("Propio otra vez", null));
        Assert.Equal(HttpStatusCode.BadRequest, sinEmpresa.StatusCode);
        Assert.Equal("cuenta_tesoreria_requiere_empresa", await CodigoAsync(sinEmpresa));
    }

    [Theory]
    [InlineData("UPDATE medios_pago SET comision_porcentaje = 150 WHERE id_medio_pago = @id AND comportamiento = 'electronico'",
        "ck_medios_pago_porcentajes_cuenta_rango", "23514", "porcentaje_cuenta_invalido")]
    [InlineData("UPDATE medios_pago SET comision_porcentaje = 60, retencion_iibb_porcentaje = 60 WHERE id_medio_pago = @id AND comportamiento = 'electronico'",
        "ck_medios_pago_porcentajes_cuenta_suma", "23514", "porcentajes_cuenta_suma_invalida")]
    [InlineData("UPDATE medios_pago SET comision_porcentaje = 1 WHERE id_medio_pago = @id AND comportamiento = 'efectivo'",
        "ck_medios_pago_cuenta_solo_electronico", "23514", "cuenta_solo_electronico")]
    [InlineData("UPDATE medios_pago SET id_cuenta_tesoreria = 999999 WHERE id_medio_pago = @id AND comportamiento = 'electronico'",
        "fk_medios_pago_cuenta_tesoreria", "23503", "referencia_invalida")]
    public async Task UnaEscrituraCrudaViolaLaRestriccionYElManejadorLaTraduceA400(
        string sql, string restriccion, string sqlState, string codigo)
    {
        var ctx = await PrepararAsync("Cruda" + codigo.Replace("_", ""));
        using var _ = ctx.Admin;
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var comportamiento = sql.Contains("'efectivo'") ? ComportamientoMedioPago.Efectivo : ComportamientoMedioPago.Electronico;
        var idMedio = await db.MediosPago.Where(m => m.Comportamiento == comportamiento).Select(m => m.Id).FirstAsync();

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        await using var comando = new NpgsqlCommand(sql, cruda);
        comando.Parameters.AddWithValue("id", idMedio);
        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());

        Assert.Equal(sqlState, excepcion.SqlState);
        Assert.Equal(restriccion, excepcion.ConstraintName);

        var falso = new ProblemDetailsFalso();
        var manejador = new ManejadorDeErrores(falso, NullLogger<ManejadorDeErrores>.Instance);
        var contexto = new DefaultHttpContext();
        var manejado = await manejador.TryHandleAsync(contexto, new DbUpdateException("restriccion", excepcion), CancellationToken.None);

        Assert.True(manejado);
        Assert.Equal(StatusCodes.Status400BadRequest, contexto.Response.StatusCode);
        Assert.Equal(codigo, falso.Ultimo!.ProblemDetails.Extensions["codigo"] as string);
    }

    private sealed class ProblemDetailsFalso : IProblemDetailsService
    {
        public ProblemDetailsContext? Ultimo { get; private set; }

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
        {
            Ultimo = context;
            return ValueTask.FromResult(true);
        }

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            Ultimo = context;
            return ValueTask.CompletedTask;
        }
    }
}
