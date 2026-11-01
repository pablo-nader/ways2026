using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// Lectura de las cuentas de tesorería (saldos, avisos de medios sin cuenta y libro por cuenta) contra Postgres real:
/// la política de reportes, el saldo por id, las bajas y las otras empresas, el orden por id, los límites inclusivos
/// del rango, el saldo de apertura y los totales por tipo.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CuentasTesoreriaLecturaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdUsuarioAdmin, HttpClient Admin, HttpClient Supervisor, HttpClient Vendedor);

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

        var supervisor = await CrearYLoguearAsync(admin, nombre, "supervisor", RolConocido.Supervisor);
        var vendedor = await CrearYLoguearAsync(admin, nombre, "vendedor", RolConocido.Vendedor);

        return new Contexto(resultado.IdTenant, resultado.IdEmpresa, resultado.IdUsuarioAdmin, admin, supervisor, vendedor);
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

    private static string Iso(DateTimeOffset fecha) =>
        Uri.EscapeDataString(fecha.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));

    private static async Task<string?> CodigoAsync(HttpResponseMessage respuesta)
    {
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        return problema.GetProperty("codigo").GetString();
    }

    private async Task<int> CrearEmpresaAsync(int idTenant, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var empresa = new Empresa { IdTenant = idTenant, RazonSocial = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();
        return empresa.Id;
    }

    private async Task<int> CrearCuentaAsync(
        Contexto ctx, string nombre, decimal saldoInicial = 0m, bool activo = true, int? idEmpresa = null,
        bool eliminada = false)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var cuenta = new CuentaTesoreria
        {
            IdTenant = ctx.IdTenant, IdEmpresa = idEmpresa ?? ctx.IdEmpresa, Nombre = nombre, SaldoInicial = saldoInicial,
            Activo = activo, CreatedAt = ahora, UpdatedAt = ahora, DeletedAt = eliminada ? ahora : null
        };
        db.CuentasTesoreria.Add(cuenta);
        await db.SaveChangesAsync();
        return cuenta.Id;
    }

    private async Task<int> CrearMedioAsync(
        Contexto ctx, string nombre, int? idCuenta, ComportamientoMedioPago comportamiento = ComportamientoMedioPago.Electronico,
        bool activo = true, int? idEmpresa = null, bool eliminado = false)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var medio = new MedioPago
        {
            IdTenant = ctx.IdTenant, IdEmpresa = idEmpresa, Nombre = nombre, Orden = 60, Comportamiento = comportamiento,
            Activo = activo, AdmiteVuelto = false, RequiereReferencia = false, IdCuentaTesoreria = idCuenta,
            CreatedAt = ahora, UpdatedAt = ahora, DeletedAt = eliminado ? ahora : null
        };
        db.MediosPago.Add(medio);
        await db.SaveChangesAsync();
        return medio.Id;
    }

    private async Task InsertarAsync(
        Contexto ctx, int idCuenta, DateTimeOffset fecha, string tipo, decimal inicio, decimal ingreso, decimal egreso,
        int? idMedioPago = null, decimal? baseBruta = null, decimal? porcentaje = null)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "INSERT INTO movimientos_cuenta (id_tenant, id_cuenta_tesoreria, fecha, tipo, concepto, inicio, ingreso, egreso, " +
            "final, id_empleado, id_medio_pago, base_bruta, porcentaje_aplicado) " +
            "VALUES ($1, $2, $3, $4::tipo_movimiento_cuenta, $5, $6, $7, $8, $9, $10, $11, $12, $13)";
        var valores = new object?[]
        {
            ctx.IdTenant, idCuenta, fecha, tipo, $"Movimiento {tipo}", inicio, ingreso, egreso, inicio + ingreso - egreso,
            ctx.IdUsuarioAdmin, idMedioPago, baseBruta, porcentaje
        };
        foreach (var valor in valores)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = valor ?? DBNull.Value });
        }

        await comando.ExecuteNonQueryAsync();
    }

    private static async Task<CuentasDeLaEmpresa> LeerCuentasAsync(HttpClient cliente, int idEmpresa)
    {
        var respuesta = await cliente.GetAsync($"/api/reportes/tesoreria/cuentas?idEmpresa={idEmpresa}");
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync());
        return (await respuesta.Content.ReadFromJsonAsync<CuentasDeLaEmpresa>(OpcionesJson))!;
    }

    private static async Task<LibroDeCuenta> LeerLibroAsync(HttpClient cliente, int idCuenta, string consulta = "")
    {
        var respuesta = await cliente.GetAsync($"/api/reportes/tesoreria/cuentas/{idCuenta}/libro{consulta}");
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync());
        return (await respuesta.Content.ReadFromJsonAsync<LibroDeCuenta>(OpcionesJson))!;
    }

    // ---- política ----------------------------------------------------------------------------------

    [Fact]
    public async Task ElSupervisorYElAdminLeenLasCuentasYSuLibroPeroElVendedorRecibe403()
    {
        var ctx = await PrepararAsync(nameof(ElSupervisorYElAdminLeenLasCuentasYSuLibroPeroElVendedorRecibe403));
        var cuenta = await CrearCuentaAsync(ctx, "Mercado Pago");
        var rutaCuentas = $"/api/reportes/tesoreria/cuentas?idEmpresa={ctx.IdEmpresa}";
        var rutaLibro = $"/api/reportes/tesoreria/cuentas/{cuenta}/libro";

        foreach (var cliente in new[] { ctx.Admin, ctx.Supervisor })
        {
            Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync(rutaCuentas)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync(rutaLibro)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await ctx.Vendedor.GetAsync(rutaCuentas)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ctx.Vendedor.GetAsync(rutaLibro)).StatusCode);
    }

    // ---- cuentas y saldos --------------------------------------------------------------------------

    [Fact]
    public async Task LasCuentasTraenSaldoInicialSinMovimientosYElFinalDelUltimoPorFechaConMovimientos()
    {
        var ctx = await PrepararAsync(nameof(LasCuentasTraenSaldoInicialSinMovimientosYElFinalDelUltimoPorFechaConMovimientos));
        var vacia = await CrearCuentaAsync(ctx, "A vacía", saldoInicial: 250m);
        var conMovimientos = await CrearCuentaAsync(ctx, "B con movimientos", saldoInicial: 100m);
        var ultimaFecha = new DateTimeOffset(2026, 1, 9, 10, 0, 0, TimeSpan.Zero);
        // El id más alto tiene la fecha ANTERIOR: el saldo y la fecha salen de la última fila en orden (fecha, id).
        await InsertarAsync(ctx, conMovimientos, ultimaFecha, "cobro", 100m, 50m, 0m);
        await InsertarAsync(ctx, conMovimientos, new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero), "comision", 150m, 0m, 20m);

        var cuentas = (await LeerCuentasAsync(ctx.Supervisor, ctx.IdEmpresa)).Cuentas;

        Assert.Equal(["A vacía", "B con movimientos"], cuentas.Select(c => c.Nombre).ToArray());
        Assert.Equal(250m, cuentas[0].SaldoActual);
        Assert.Equal(250m, cuentas[0].SaldoInicial);
        Assert.Null(cuentas[0].FechaUltimoMovimiento);
        Assert.Equal(vacia, cuentas[0].Id);
        Assert.Equal(150m, cuentas[1].SaldoActual);
        Assert.Equal(100m, cuentas[1].SaldoInicial);
        Assert.Equal(ultimaFecha, cuentas[1].FechaUltimoMovimiento);
    }

    [Fact]
    public async Task LasCuentasDadasDeBajaYLasDeOtraEmpresaNoSeListanPeroLasInactivasSi()
    {
        var ctx = await PrepararAsync(nameof(LasCuentasDadasDeBajaYLasDeOtraEmpresaNoSeListanPeroLasInactivasSi));
        var otraEmpresa = await CrearEmpresaAsync(ctx.IdTenant, "Otra empresa");
        await CrearCuentaAsync(ctx, "Viva");
        await CrearCuentaAsync(ctx, "Inactiva", activo: false);
        await CrearCuentaAsync(ctx, "Dada de baja", eliminada: true);
        await CrearCuentaAsync(ctx, "De la otra", idEmpresa: otraEmpresa);

        var cuentas = (await LeerCuentasAsync(ctx.Admin, ctx.IdEmpresa)).Cuentas;

        Assert.Equal(["Inactiva", "Viva"], cuentas.Select(c => c.Nombre).ToArray());
        Assert.False(cuentas[0].Activo);
        Assert.True(cuentas[1].Activo);
    }

    [Fact]
    public async Task UnaEmpresaInexistenteOAjenaDa404()
    {
        var a = await PrepararAsync(nameof(UnaEmpresaInexistenteOAjenaDa404) + "A");
        var b = await PrepararAsync(nameof(UnaEmpresaInexistenteOAjenaDa404) + "B");

        var inexistente = await a.Supervisor.GetAsync("/api/reportes/tesoreria/cuentas?idEmpresa=999999");
        var ajena = await a.Supervisor.GetAsync($"/api/reportes/tesoreria/cuentas?idEmpresa={b.IdEmpresa}");

        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ajena.StatusCode);
    }

    // ---- avisos ------------------------------------------------------------------------------------

    [Fact]
    public async Task LosAvisosListanSoloLosMediosElectronicosActivosSinCuentaUtilizable()
    {
        var ctx = await PrepararAsync(nameof(LosAvisosListanSoloLosMediosElectronicosActivosSinCuentaUtilizable));
        var otraEmpresa = await CrearEmpresaAsync(ctx.IdTenant, "Otra empresa");
        var buena = await CrearCuentaAsync(ctx, "Buena");
        var inactiva = await CrearCuentaAsync(ctx, "Inactiva", activo: false);
        var dadaDeBaja = await CrearCuentaAsync(ctx, "Dada de baja", eliminada: true);
        var deOtraEmpresa = await CrearCuentaAsync(ctx, "De otra empresa", idEmpresa: otraEmpresa);

        await CrearMedioAsync(ctx, "Con cuenta buena", buena);
        await CrearMedioAsync(ctx, "Con cuenta inactiva", inactiva);
        await CrearMedioAsync(ctx, "Sin cuenta", null);
        await CrearMedioAsync(ctx, "Cuenta dada de baja", dadaDeBaja);
        await CrearMedioAsync(ctx, "Cuenta de otra empresa", deOtraEmpresa);
        await CrearMedioAsync(ctx, "Sin cuenta pero inactivo", null, activo: false);
        await CrearMedioAsync(ctx, "Sin cuenta pero dado de baja", null, eliminado: true);
        await CrearMedioAsync(ctx, "Efectivo sin cuenta", null, ComportamientoMedioPago.Efectivo);
        await CrearMedioAsync(ctx, "De la otra empresa sin cuenta", null, idEmpresa: otraEmpresa);
        await CrearMedioAsync(ctx, "De esta empresa sin cuenta", null, idEmpresa: ctx.IdEmpresa);

        var avisos = (await LeerCuentasAsync(ctx.Supervisor, ctx.IdEmpresa)).MediosSinCuenta;

        Assert.Equal(
            // "Transferencia" es un medio electrónico que siembra el aprovisionamiento del tenant, sin cuenta.
            ["Cuenta dada de baja", "Cuenta de otra empresa", "De esta empresa sin cuenta", "Sin cuenta", "Transferencia"],
            avisos.Select(m => m.Nombre).ToArray());
    }

    // ---- libro -------------------------------------------------------------------------------------

    [Fact]
    public async Task UnaCuentaInexistenteDadaDeBajaOAjenaDa404AunConUnRangoInvertido()
    {
        var a = await PrepararAsync(nameof(UnaCuentaInexistenteDadaDeBajaOAjenaDa404AunConUnRangoInvertido) + "A");
        var b = await PrepararAsync(nameof(UnaCuentaInexistenteDadaDeBajaOAjenaDa404AunConUnRangoInvertido) + "B");
        var dadaDeBaja = await CrearCuentaAsync(a, "Baja", eliminada: true);
        var ajena = await CrearCuentaAsync(b, "De B");
        var rango = $"?desde={Iso(DateTimeOffset.UtcNow)}&hasta={Iso(DateTimeOffset.UtcNow.AddDays(-5))}&pagina=0&tamanio=0";

        foreach (var id in new[] { 999999, dadaDeBaja, ajena })
        {
            var respuesta = await a.Supervisor.GetAsync($"/api/reportes/tesoreria/cuentas/{id}/libro{rango}");
            Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        }
    }

    [Fact]
    public async Task ElLibroSeOrdenaPorFechaYDespuesPorIdAunqueLosIdsVayanAlReves()
    {
        var ctx = await PrepararAsync(nameof(ElLibroSeOrdenaPorFechaYDespuesPorIdAunqueLosIdsVayanAlReves));
        var cuenta = await CrearCuentaAsync(ctx, "MP");
        var manana = new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);
        var ayer = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        await InsertarAsync(ctx, cuenta, manana, "comision", 100m, 0m, 10m);
        await InsertarAsync(ctx, cuenta, ayer, "cobro", 0m, 100m, 0m);
        await InsertarAsync(ctx, cuenta, ayer, "retencion_iva", 90m, 0m, 5m);

        var libro = await LeerLibroAsync(ctx.Supervisor, cuenta);

        Assert.Equal([ayer, ayer, manana], libro.Items.Select(i => i.Fecha).ToArray());
        Assert.Equal([100m, 85m, 90m], libro.Items.Select(i => i.Final).ToArray());
        Assert.True(libro.Items[0].Id > libro.Items[2].Id);
        Assert.True(libro.Items[0].Id < libro.Items[1].Id);
        Assert.Equal(90m, libro.SaldoActual);
    }

    private async Task<(Contexto Ctx, int Cuenta)> SembrarLibroAsync(string nombre)
    {
        var ctx = await PrepararAsync(nombre);
        var cuenta = await CrearCuentaAsync(ctx, "MP", saldoInicial: 100m);
        var medio = await CrearMedioAsync(ctx, "QR MP", cuenta);
        var noche = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var dia11 = new DateTimeOffset(2026, 1, 11, 9, 0, 0, TimeSpan.Zero);
        await InsertarAsync(ctx, cuenta, noche, "cobro", 100m, 1000m, 0m, medio, 1000m, null);
        await InsertarAsync(ctx, cuenta, noche, "comision", 1100m, 0m, 50m, medio, 1000m, 5m);
        await InsertarAsync(ctx, cuenta, dia11, "retencion_iibb", 1050m, 0m, 10m, medio, 1000m, 1m);
        await InsertarAsync(ctx, cuenta, new DateTimeOffset(2026, 1, 12, 9, 0, 0, TimeSpan.Zero), "ajuste", 1040m, 5m, 0m);
        return (ctx, cuenta);
    }

    [Fact]
    public async Task ElRangoIncluyeSusDosExtremosYTraeLaAperturaYLosTotales()
    {
        var (ctx, cuenta) = await SembrarLibroAsync(nameof(ElRangoIncluyeSusDosExtremosYTraeLaAperturaYLosTotales));
        var dia11 = new DateTimeOffset(2026, 1, 11, 9, 0, 0, TimeSpan.Zero);

        var libro = await LeerLibroAsync(ctx.Supervisor, cuenta, $"?desde={Iso(dia11)}&hasta={Iso(dia11)}");

        var fila = Assert.Single(libro.Items);
        Assert.Equal(TipoMovimientoCuenta.RetencionIibb, fila.Tipo);
        Assert.Equal(1050m, libro.SaldoDeApertura);
        Assert.Equal(1040m, libro.SaldoAlCierre);
        Assert.Equal(1045m, libro.SaldoActual);
        Assert.Equal(10m, libro.Totales.RetencionIibb);
        Assert.Equal(0m, libro.Totales.Cobrado);
        Assert.Equal(-10m, libro.Totales.Neto);
        Assert.Equal(1, libro.Total);
    }

    [Fact]
    public async Task SinRangoElLibroEmpiezaEnElSaldoInicialYLosTotalesCierranContraElSaldoActual()
    {
        var (ctx, cuenta) = await SembrarLibroAsync(nameof(SinRangoElLibroEmpiezaEnElSaldoInicialYLosTotalesCierranContraElSaldoActual));

        var libro = await LeerLibroAsync(ctx.Supervisor, cuenta);

        Assert.Equal(100m, libro.SaldoDeApertura);
        Assert.Equal(1000m, libro.Totales.Cobrado);
        Assert.Equal(50m, libro.Totales.Comision);
        Assert.Equal(10m, libro.Totales.RetencionIibb);
        Assert.Equal(0m, libro.Totales.RetencionIva);
        Assert.Equal(0m, libro.Totales.RetencionGanancias);
        Assert.Equal(5m, libro.Totales.Ajustes);
        Assert.Equal(0m, libro.Totales.Otros);
        Assert.Equal(945m, libro.Totales.Neto);
        Assert.Equal(1045m, libro.SaldoAlCierre);
        Assert.Equal(libro.SaldoActual, libro.SaldoAlCierre);
        Assert.Equal(4, libro.Total);
    }

    [Fact]
    public async Task LosTotalesCubrenTodoElRangoYNoSoloLaPaginaPedida()
    {
        var (ctx, cuenta) = await SembrarLibroAsync(nameof(LosTotalesCubrenTodoElRangoYNoSoloLaPaginaPedida));

        var libro = await LeerLibroAsync(ctx.Supervisor, cuenta, "?pagina=2&tamanio=1");

        var fila = Assert.Single(libro.Items);
        Assert.Equal(TipoMovimientoCuenta.Comision, fila.Tipo);
        Assert.Equal(4, libro.Total);
        Assert.Equal(2, libro.Pagina);
        Assert.Equal(1, libro.Tamanio);
        Assert.Equal(945m, libro.Totales.Neto);
    }

    [Fact]
    public async Task UnaPaginaEnteraEnormeDaUnaPaginaVaciaEnLosDosLibrosYNoUn500()
    {
        var (ctx, cuenta) = await SembrarLibroAsync(nameof(UnaPaginaEnteraEnormeDaUnaPaginaVaciaEnLosDosLibrosYNoUn500));

        var deCuenta = await LeerLibroAsync(ctx.Supervisor, cuenta, "?pagina=2147483647&tamanio=200");
        var deEfectivo = await ctx.Supervisor.GetAsync($"/api/reportes/tesoreria?idEmpresa={ctx.IdEmpresa}&pagina=2147483647&tamanio=200");

        Assert.Empty(deCuenta.Items);
        Assert.Equal(4, deCuenta.Total);
        Assert.Equal(HttpStatusCode.OK, deEfectivo.StatusCode);
    }

    [Fact]
    public async Task LaFilaTraeMedioEmpleadoBaseYPorcentajeYUnMedioDadoDeBajaQuedaSinNombre()
    {
        var ctx = await PrepararAsync(nameof(LaFilaTraeMedioEmpleadoBaseYPorcentajeYUnMedioDadoDeBajaQuedaSinNombre));
        var cuenta = await CrearCuentaAsync(ctx, "MP");
        var vivo = await CrearMedioAsync(ctx, "QR vivo", cuenta);
        var baja = await CrearMedioAsync(ctx, "QR viejo", cuenta);
        var fecha = new DateTimeOffset(2026, 2, 1, 9, 0, 0, TimeSpan.Zero);
        await InsertarAsync(ctx, cuenta, fecha, "comision", 0m, 0m, 5m, vivo, 100m, 5m);
        await InsertarAsync(ctx, cuenta, fecha, "cobro", -5m, 10m, 0m, baja);
        await InsertarAsync(ctx, cuenta, fecha, "ajuste", 5m, 1m, 0m);
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            await db.MediosPago.Where(m => m.Id == baja).ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedAt, DateTimeOffset.UtcNow));
        }

        var libro = await LeerLibroAsync(ctx.Supervisor, cuenta);

        var conMedio = libro.Items[0];
        Assert.Equal("QR vivo", conMedio.NombreMedioPago);
        Assert.Equal(vivo, conMedio.IdMedioPago);
        Assert.Equal(100m, conMedio.BaseBruta);
        Assert.Equal(5m, conMedio.PorcentajeAplicado);
        Assert.Equal(ctx.IdUsuarioAdmin, conMedio.IdEmpleado);
        Assert.False(string.IsNullOrEmpty(conMedio.NombreEmpleado));

        var medioDadoDeBaja = libro.Items[1];
        Assert.Equal(baja, medioDadoDeBaja.IdMedioPago);
        Assert.Null(medioDadoDeBaja.NombreMedioPago);

        var sinMedio = libro.Items[2];
        Assert.Null(sinMedio.IdMedioPago);
        Assert.Null(sinMedio.NombreMedioPago);
    }
}
