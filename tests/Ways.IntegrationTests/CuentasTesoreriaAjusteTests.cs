using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Caja;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// Ajuste manual del saldo de una cuenta de tesorería contra Postgres real: la política (solo Admin), los dos modos, la
/// fila escrita, los 400 con su código, el 404 antes que la validación, el 409 sin diferencia y el lock de la cuenta, que
/// hace que el saldo objetivo se calcule sobre la cola de la cadena ya comiteada.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CuentasTesoreriaAjusteTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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

    private WaysDbContext Db(Contexto ctx) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

    private async Task<int> CrearCuentaAsync(
        Contexto ctx, string nombre = "Mercado Pago", decimal saldoInicial = 0m, bool activo = true, bool eliminada = false)
    {
        await using var db = Db(ctx);
        var ahora = DateTimeOffset.UtcNow;
        var cuenta = new CuentaTesoreria
        {
            IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, Nombre = nombre, SaldoInicial = saldoInicial,
            Activo = activo, CreatedAt = ahora, UpdatedAt = ahora, DeletedAt = eliminada ? ahora : null
        };
        db.CuentasTesoreria.Add(cuenta);
        await db.SaveChangesAsync();
        return cuenta.Id;
    }

    private async Task<List<MovimientoCuenta>> FilasAsync(Contexto ctx, int idCuenta)
    {
        await using var db = Db(ctx);
        return await db.MovimientosCuenta
            .Where(m => m.IdCuentaTesoreria == idCuenta).OrderBy(m => m.Id).AsNoTracking().ToListAsync();
    }

    private static string Ruta(int idCuenta) => $"/api/cuentas-tesoreria/{idCuenta}/ajustes";

    private static Task<HttpResponseMessage> PostearJsonAsync(HttpClient cliente, int idCuenta, string json) =>
        cliente.PostAsync(Ruta(idCuenta), new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<string?> CodigoAsync(HttpResponseMessage respuesta)
    {
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        return problema.GetProperty("codigo").GetString();
    }

    private static async Task<ResultadoDeAjusteDeCuenta> AjustarOkAsync(
        HttpClient cliente, int idCuenta, SolicitudDeAjusteDeCuenta solicitud)
    {
        var respuesta = await cliente.PostAsJsonAsync(Ruta(idCuenta), solicitud);
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, await respuesta.Content.ReadAsStringAsync());
        return (await respuesta.Content.ReadFromJsonAsync<ResultadoDeAjusteDeCuenta>(OpcionesJson))!;
    }

    private async Task InsertarMovimientoAsync(
        Contexto ctx, int idCuenta, decimal inicio, decimal ingreso, decimal egreso, NpgsqlConnection? conexion = null,
        NpgsqlTransaction? transaccion = null)
    {
        var propia = conexion is null;
        conexion ??= await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        try
        {
            await using var comando = new NpgsqlCommand(
                "INSERT INTO movimientos_cuenta (id_tenant, id_cuenta_tesoreria, fecha, tipo, concepto, inicio, ingreso, egreso, " +
                "final, id_empleado) VALUES ($1, $2, now(), 'cobro', 'previo', $3, $4, $5, $6, $7)",
                conexion, transaccion);
            foreach (var valor in new object[]
                     { ctx.IdTenant, idCuenta, inicio, ingreso, egreso, inicio + ingreso - egreso, ctx.IdUsuarioAdmin })
            {
                comando.Parameters.Add(new NpgsqlParameter { Value = valor });
            }

            await comando.ExecuteNonQueryAsync();
        }
        finally
        {
            if (propia)
            {
                await conexion.DisposeAsync();
            }
        }
    }

    /// <summary>Espera a que haya una sesión bloqueada en un lock cuya consulta contiene el fragmento dado.</summary>
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

    // ---- escritura ---------------------------------------------------------------------------------

    [Fact]
    public async Task UnImportePositivoEscribeUnaFilaDeAjusteEnElLadoIngresoYResponde201ConElSaldoNuevo()
    {
        var ctx = await PrepararAsync(nameof(UnImportePositivoEscribeUnaFilaDeAjusteEnElLadoIngresoYResponde201ConElSaldoNuevo));
        var cuenta = await CrearCuentaAsync(ctx, saldoInicial: 100m);
        var antes = DateTimeOffset.UtcNow.AddSeconds(-5);

        var resultado = await AjustarOkAsync(ctx.Admin, cuenta, new SolicitudDeAjusteDeCuenta("  Conciliación  ", 25.5m, null));

        var fila = Assert.Single(await FilasAsync(ctx, cuenta));
        Assert.Equal(TipoMovimientoCuenta.Ajuste, fila.Tipo);
        Assert.Equal("Conciliación", fila.Concepto);
        Assert.Equal((100m, 25.5m, 0m, 125.5m), (fila.Inicio, fila.Ingreso, fila.Egreso, fila.Final));
        Assert.Equal(ctx.IdUsuarioAdmin, fila.IdEmpleado);
        Assert.Null(fila.IdTurnoCaja);
        Assert.Null(fila.IdMedioPago);
        Assert.Null(fila.BaseBruta);
        Assert.Null(fila.PorcentajeAplicado);
        Assert.InRange(fila.Fecha, antes, DateTimeOffset.UtcNow.AddSeconds(5));

        Assert.Equal(125.5m, resultado.SaldoActual);
        Assert.Equal(fila.Id, resultado.Movimiento.Id);
        Assert.Equal(25.5m, resultado.Movimiento.Ingreso);
        Assert.Equal(125.5m, resultado.Movimiento.Final);
        Assert.False(string.IsNullOrEmpty(resultado.Movimiento.NombreEmpleado));
    }

    [Fact]
    public async Task UnImporteNegativoEscribeElLadoEgresoYPuedeDejarElSaldoEnNegativo()
    {
        var ctx = await PrepararAsync(nameof(UnImporteNegativoEscribeElLadoEgresoYPuedeDejarElSaldoEnNegativo));
        var cuenta = await CrearCuentaAsync(ctx, saldoInicial: 10m);

        var resultado = await AjustarOkAsync(ctx.Admin, cuenta, new SolicitudDeAjusteDeCuenta("Retiro no registrado", -30m, null));

        var fila = Assert.Single(await FilasAsync(ctx, cuenta));
        Assert.Equal((10m, 0m, 30m, -20m), (fila.Inicio, fila.Ingreso, fila.Egreso, fila.Final));
        Assert.Equal(-20m, resultado.SaldoActual);
    }

    [Fact]
    public async Task ElSaldoObjetivoEscribeLaDiferenciaContraLaColaDeLaCadena()
    {
        var ctx = await PrepararAsync(nameof(ElSaldoObjetivoEscribeLaDiferenciaContraLaColaDeLaCadena));
        var cuenta = await CrearCuentaAsync(ctx, saldoInicial: 100m);
        await InsertarMovimientoAsync(ctx, cuenta, 100m, 30m, 0m);

        var resultado = await AjustarOkAsync(ctx.Admin, cuenta, new SolicitudDeAjusteDeCuenta("Saldo de la plataforma", null, 90m));

        var ajuste = (await FilasAsync(ctx, cuenta)).Last();
        Assert.Equal(TipoMovimientoCuenta.Ajuste, ajuste.Tipo);
        Assert.Equal((130m, 0m, 40m, 90m), (ajuste.Inicio, ajuste.Ingreso, ajuste.Egreso, ajuste.Final));
        Assert.Equal(90m, resultado.SaldoActual);
    }

    [Fact]
    public async Task UnSaldoObjetivoIgualAlActualDa409SinEscribirNada()
    {
        var ctx = await PrepararAsync(nameof(UnSaldoObjetivoIgualAlActualDa409SinEscribirNada));
        var cuenta = await CrearCuentaAsync(ctx, saldoInicial: 100m);

        var respuesta = await ctx.Admin.PostAsJsonAsync(Ruta(cuenta), new SolicitudDeAjusteDeCuenta("Sin cambios", null, 100m));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("ajuste_sin_diferencia", await CodigoAsync(respuesta));
        Assert.Empty(await FilasAsync(ctx, cuenta));
    }

    [Fact]
    public async Task UnaCuentaInactivaSePuedeAjustar()
    {
        var ctx = await PrepararAsync(nameof(UnaCuentaInactivaSePuedeAjustar));
        var cuenta = await CrearCuentaAsync(ctx, activo: false);

        var resultado = await AjustarOkAsync(ctx.Admin, cuenta, new SolicitudDeAjusteDeCuenta("Cuenta vieja", 5m, null));

        Assert.Equal(5m, resultado.SaldoActual);
    }

    [Fact]
    public async Task DosAjustesSeguidosEncadenanYElLibroLosMuestra()
    {
        var ctx = await PrepararAsync(nameof(DosAjustesSeguidosEncadenanYElLibroLosMuestra));
        var cuenta = await CrearCuentaAsync(ctx, saldoInicial: 50m);

        await AjustarOkAsync(ctx.Admin, cuenta, new SolicitudDeAjusteDeCuenta("Primero", 10m, null));
        await AjustarOkAsync(ctx.Admin, cuenta, new SolicitudDeAjusteDeCuenta("Segundo", -25m, null));

        var filas = await FilasAsync(ctx, cuenta);
        Assert.Equal([(50m, 60m), (60m, 35m)], filas.Select(f => (f.Inicio, f.Final)).ToArray());

        var libro = await ctx.Supervisor.GetFromJsonAsync<LibroDeCuenta>(
            $"/api/reportes/tesoreria/cuentas/{cuenta}/libro", OpcionesJson);
        Assert.Equal(35m, libro!.SaldoActual);
        Assert.Equal(-15m, libro.Totales.Ajustes);
    }

    // ---- 404 antes que 400 y política --------------------------------------------------------------

    [Fact]
    public async Task UnaCuentaInexistenteDadaDeBajaOAjenaDa404AunConElCuerpoInvalido()
    {
        var a = await PrepararAsync(nameof(UnaCuentaInexistenteDadaDeBajaOAjenaDa404AunConElCuerpoInvalido) + "A");
        var b = await PrepararAsync(nameof(UnaCuentaInexistenteDadaDeBajaOAjenaDa404AunConElCuerpoInvalido) + "B");
        var dadaDeBaja = await CrearCuentaAsync(a, eliminada: true);
        var ajena = await CrearCuentaAsync(b);

        foreach (var id in new[] { 999999, dadaDeBaja, ajena })
        {
            var respuesta = await PostearJsonAsync(a.Admin, id, "{}");
            Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        }

        Assert.Empty(await FilasAsync(b, ajena));
    }

    [Fact]
    public async Task SoloElAdminAjustaYElSupervisorYElVendedorReciben403SinEscribir()
    {
        var ctx = await PrepararAsync(nameof(SoloElAdminAjustaYElSupervisorYElVendedorReciben403SinEscribir));
        var cuenta = await CrearCuentaAsync(ctx);

        foreach (var cliente in new[] { ctx.Supervisor, ctx.Vendedor })
        {
            var respuesta = await cliente.PostAsJsonAsync(Ruta(cuenta), new SolicitudDeAjusteDeCuenta("No debería", 10m, null));
            Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
        }

        Assert.Empty(await FilasAsync(ctx, cuenta));
    }

    // ---- validación --------------------------------------------------------------------------------

    [Theory]
    [InlineData("{}", "concepto_requerido")]
    [InlineData("{\"concepto\":\"   \",\"importe\":5}", "concepto_requerido")]
    [InlineData("{\"concepto\":\"x\"}", "importe_o_saldo_objetivo_requerido")]
    [InlineData("{\"concepto\":\"x\",\"importe\":5,\"saldoObjetivo\":10}", "importe_y_saldo_objetivo_excluyentes")]
    [InlineData("{\"concepto\":\"x\",\"importe\":0}", "importe_invalido")]
    [InlineData("{\"concepto\":\"x\",\"importe\":1.234}", "importe_invalido")]
    [InlineData("{\"concepto\":\"x\",\"saldoObjetivo\":1.234}", "saldo_objetivo_invalido")]
    public async Task UnCuerpoInvalidoDa400ConSuCodigoYNoEscribeNada(string json, string codigo)
    {
        var ctx = await PrepararAsync(nameof(UnCuerpoInvalidoDa400ConSuCodigoYNoEscribeNada) + Math.Abs(json.GetHashCode()));
        var cuenta = await CrearCuentaAsync(ctx);

        var respuesta = await PostearJsonAsync(ctx.Admin, cuenta, json);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal(codigo, await CodigoAsync(respuesta));
        Assert.Empty(await FilasAsync(ctx, cuenta));
    }

    [Fact]
    public async Task UnSaldoResultanteFueraDeNumeric14_2Da400EnVezDeUn500()
    {
        var ctx = await PrepararAsync(nameof(UnSaldoResultanteFueraDeNumeric14_2Da400EnVezDeUn500));
        var cuenta = await CrearCuentaAsync(ctx, saldoInicial: 999_999_999_999m);

        var respuesta = await ctx.Admin.PostAsJsonAsync(Ruta(cuenta), new SolicitudDeAjusteDeCuenta("Desborde", 5m, null));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("saldo_resultante_fuera_de_rango", await CodigoAsync(respuesta));
        Assert.Empty(await FilasAsync(ctx, cuenta));
    }

    // ---- lock y concurrencia -----------------------------------------------------------------------

    [Fact]
    public async Task ElSaldoObjetivoEsperaElLockDeLaCuentaYSeCalculaSobreLaColaYaComiteada()
    {
        var ctx = await PrepararAsync(nameof(ElSaldoObjetivoEsperaElLockDeLaCuentaYSeCalculaSobreLaColaYaComiteada));
        var cuenta = await CrearCuentaAsync(ctx, saldoInicial: 100m);

        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        await using var transaccion = await conexion.BeginTransactionAsync();
        await using (var bloquear = new NpgsqlCommand(
            $"SELECT 1 FROM cuentas_tesoreria WHERE id_cuenta_tesoreria = {cuenta} FOR NO KEY UPDATE", conexion, transaccion))
        {
            await bloquear.ExecuteScalarAsync();
        }

        await InsertarMovimientoAsync(ctx, cuenta, 100m, 50m, 0m, conexion, transaccion);

        var ajuste = ctx.Admin.PostAsJsonAsync(Ruta(cuenta), new SolicitudDeAjusteDeCuenta("Saldo real", null, 200m));
        await EsperarUnaSesionBloqueadaAsync("FOR NO KEY UPDATE");
        Assert.False(ajuste.IsCompleted);

        await transaccion.CommitAsync();
        var respuesta = await ajuste;
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, await respuesta.Content.ReadAsStringAsync());

        var escrito = (await FilasAsync(ctx, cuenta)).Last();
        Assert.Equal((150m, 50m, 0m, 200m), (escrito.Inicio, escrito.Ingreso, escrito.Egreso, escrito.Final));
    }

    [Fact]
    public async Task UnaColaConFechaFuturaHaceQueLosAjustesSiguientesNuncaFechenAntes()
    {
        var ctx = await PrepararAsync(nameof(UnaColaConFechaFuturaHaceQueLosAjustesSiguientesNuncaFechenAntes));
        var cuenta = await CrearCuentaAsync(ctx, saldoInicial: 0m);
        var ahora = DateTimeOffset.UtcNow.AddHours(2);
        var futuro = new DateTimeOffset(ahora.Ticks - ahora.Ticks % 10, TimeSpan.Zero);
        await using (var conexion = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant))
        await using (var comando = new NpgsqlCommand(
            "INSERT INTO movimientos_cuenta (id_tenant, id_cuenta_tesoreria, fecha, tipo, concepto, inicio, ingreso, egreso, " +
            "final, id_empleado) VALUES ($1, $2, $3, 'ajuste', 'previo', 0, 10, 0, 10, $4)", conexion))
        {
            foreach (var valor in new object[] { ctx.IdTenant, cuenta, futuro, ctx.IdUsuarioAdmin })
            {
                comando.Parameters.Add(new NpgsqlParameter { Value = valor });
            }

            await comando.ExecuteNonQueryAsync();
        }

        await Task.WhenAll(Enumerable.Range(1, 4).Select(i =>
            AjustarOkAsync(ctx.Admin, cuenta, new SolicitudDeAjusteDeCuenta($"Ajuste {i}", i, null))));

        var fechas = (await FilasAsync(ctx, cuenta)).Select(f => f.Fecha).ToList();
        Assert.Equal(5, fechas.Count);
        Assert.Equal(fechas.Order().ToList(), fechas);
        Assert.All(fechas, f => Assert.True(f >= futuro));
    }

    [Fact]
    public async Task DosAjustesSimultaneosSeSerializanYLaCadenaQuedaContinua()
    {
        var ctx = await PrepararAsync(nameof(DosAjustesSimultaneosSeSerializanYLaCadenaQuedaContinua));
        var cuenta = await CrearCuentaAsync(ctx, saldoInicial: 100m);

        var respuestas = await Task.WhenAll(
            Enumerable.Range(1, 6).Select(i =>
                ctx.Admin.PostAsJsonAsync(Ruta(cuenta), new SolicitudDeAjusteDeCuenta($"Ajuste {i}", i % 2 == 0 ? -i : i, null))));

        foreach (var respuesta in respuestas)
        {
            Assert.True(respuesta.StatusCode == HttpStatusCode.Created, await respuesta.Content.ReadAsStringAsync());
        }

        var filas = await FilasAsync(ctx, cuenta);
        Assert.Equal(6, filas.Count);
        Assert.Equal(100m, filas[0].Inicio);
        for (var i = 1; i < filas.Count; i++)
        {
            Assert.Equal(filas[i - 1].Final, filas[i].Inicio);
        }

        // 1 - 2 + 3 - 4 + 5 - 6 = -3
        Assert.Equal(97m, filas[^1].Final);
    }
}
