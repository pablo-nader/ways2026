using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Caja;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// ABM de cuentas de tesorería punta a punta contra Postgres real: contrato, política (solo Admin, también para leer),
/// nombre único por empresa con su backstop de base, regla del saldo inicial bajo lock, baja solo sin referencias,
/// 404 antes que cualquier validación y aislamiento entre tenants.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CuentasTesoreriaAbmTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";
    private const string Ruta = "/api/cuentas-tesoreria";

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdUsuarioAdmin, HttpClient Admin, HttpClient Supervisor, HttpClient Vendedor);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
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

    private static async Task<string?> CodigoAsync(HttpResponseMessage respuesta)
    {
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        return problema.GetProperty("codigo").GetString();
    }

    private static async Task<CuentaTesoreriaListado> CrearOkAsync(
        Contexto ctx, string nombre = "Mercado Pago", decimal saldoInicial = 0m, bool activo = true, int? idEmpresa = null)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            Ruta, new AltaDeCuentaTesoreria(idEmpresa ?? ctx.IdEmpresa, nombre, saldoInicial, activo));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<CuentaTesoreriaListado>())!;
    }

    private static async Task<List<CuentaTesoreriaListado>> ListarAsync(HttpClient cliente, string consulta = "") =>
        (await cliente.GetFromJsonAsync<List<CuentaTesoreriaListado>>($"{Ruta}{consulta}"))!;

    private async Task<int> CrearEmpresaAsync(int idTenant, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var empresa = new Empresa { IdTenant = idTenant, RazonSocial = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();
        return empresa.Id;
    }

    private const string SqlMovimiento =
        "INSERT INTO movimientos_cuenta (id_tenant, id_cuenta_tesoreria, fecha, tipo, concepto, inicio, ingreso, egreso, final, id_empleado) " +
        "VALUES ($1, $2, now(), 'ajuste', 'prueba', $3, $4, $5, $6, $7)";

    private static void Parametros(NpgsqlCommand comando, params object[] valores)
    {
        foreach (var valor in valores)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = valor });
        }
    }

    private async Task InsertarMovimientoAsync(
        Contexto ctx, int idCuenta, decimal inicio, decimal ingreso, decimal egreso)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = SqlMovimiento;
        Parametros(comando, ctx.IdTenant, idCuenta, inicio, ingreso, egreso, inicio + ingreso - egreso, ctx.IdUsuarioAdmin);
        await comando.ExecuteNonQueryAsync();
    }

    private async Task<int> SembrarMedioAsync(Contexto ctx, int idCuenta, bool eliminado = false)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var medio = new MedioPago
        {
            IdTenant = ctx.IdTenant,
            Nombre = $"QR {Guid.NewGuid():N}"[..12],
            Orden = 1,
            Comportamiento = ComportamientoMedioPago.Electronico,
            AdmiteVuelto = false,
            RequiereReferencia = true,
            IdCuentaTesoreria = idCuenta,
            CreatedAt = ahora,
            UpdatedAt = ahora,
            DeletedAt = eliminado ? ahora : null
        };
        db.MediosPago.Add(medio);
        await db.SaveChangesAsync();
        return medio.Id;
    }

    /// <summary>Espera, desde una conexión aparte, a que alguna sesión esté bloqueada esperando un lock.</summary>
    private async Task EsperarUnaSesionBloqueadaAsync()
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND pid <> pg_backend_pid() " +
            "AND datname = current_database()";

        var limite = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < limite)
        {
            if ((long)(await comando.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("Ninguna sesión llegó a quedar bloqueada esperando el lock.");
    }

    [Fact]
    public async Task ElAbmCompletoDelAdminDevuelveElContratoYLaBajaLoOcultaDeLaLectura()
    {
        var ctx = await PrepararAsync(nameof(ElAbmCompletoDelAdminDevuelveElContratoYLaBajaLoOcultaDeLaLectura));

        var alta = await ctx.Admin.PostAsJsonAsync(Ruta, new AltaDeCuentaTesoreria(ctx.IdEmpresa, "  Mercado Pago ", 1500.25m));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var creada = (await alta.Content.ReadFromJsonAsync<CuentaTesoreriaListado>())!;
        Assert.Equal($"{Ruta}/{creada.Id}", alta.Headers.Location!.OriginalString);
        Assert.Equal(ctx.IdEmpresa, creada.IdEmpresa);
        Assert.Equal($"{nameof(ElAbmCompletoDelAdminDevuelveElContratoYLaBajaLoOcultaDeLaLectura)} SA", creada.NombreEmpresa);
        Assert.Equal("Mercado Pago", creada.Nombre);
        Assert.Equal(1500.25m, creada.SaldoInicial);
        Assert.Equal(1500.25m, creada.SaldoActual);
        Assert.True(creada.Activo);
        Assert.True(creada.SaldoInicialEditable);

        var obtenida = (await ctx.Admin.GetFromJsonAsync<CuentaTesoreriaListado>($"{Ruta}/{creada.Id}"))!;
        Assert.Equal(creada, obtenida);

        var edicion = await ctx.Admin.PutAsJsonAsync(
            $"{Ruta}/{creada.Id}", new EdicionDeCuentaTesoreria("Banco Nación", 2000m, false));
        Assert.Equal(HttpStatusCode.OK, edicion.StatusCode);
        var editada = (await edicion.Content.ReadFromJsonAsync<CuentaTesoreriaListado>())!;
        Assert.Equal("Banco Nación", editada.Nombre);
        Assert.Equal(2000m, editada.SaldoInicial);
        Assert.Equal(2000m, editada.SaldoActual);
        Assert.False(editada.Activo);

        Assert.Equal(creada.Id, Assert.Single(await ListarAsync(ctx.Admin)).Id);

        var baja = await ctx.Admin.DeleteAsync($"{Ruta}/{creada.Id}");
        Assert.Equal(HttpStatusCode.NoContent, baja.StatusCode);

        Assert.Empty(await ListarAsync(ctx.Admin));
        Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.GetAsync($"{Ruta}/{creada.Id}")).StatusCode);
    }

    [Fact]
    public async Task ElListadoIncluyeLasInactivasYSePuedeFiltrarPorEmpresa()
    {
        var ctx = await PrepararAsync(nameof(ElListadoIncluyeLasInactivasYSePuedeFiltrarPorEmpresa));
        var otraEmpresa = await CrearEmpresaAsync(ctx.IdTenant, "Otra empresa");
        var activa = await CrearOkAsync(ctx, "Activa");
        var inactiva = await CrearOkAsync(ctx, "Inactiva", activo: false);
        var deOtra = await CrearOkAsync(ctx, "De otra", idEmpresa: otraEmpresa);

        var todas = await ListarAsync(ctx.Admin);
        Assert.Equal(
            new[] { activa.Id, inactiva.Id, deOtra.Id }.Order(), todas.Select(c => c.Id).Order());
        Assert.False(todas.Single(c => c.Id == inactiva.Id).Activo);

        var deLaPrimera = await ListarAsync(ctx.Admin, $"?idEmpresa={ctx.IdEmpresa}");
        Assert.Equal(new[] { activa.Id, inactiva.Id }.Order(), deLaPrimera.Select(c => c.Id).Order());
        Assert.Equal(deOtra.Id, Assert.Single(await ListarAsync(ctx.Admin, $"?idEmpresa={otraEmpresa}")).Id);
    }

    [Fact]
    public async Task ElSaldoInicialPuedeSerNegativoYElAltaRechazaLoQueNoEsValido()
    {
        var ctx = await PrepararAsync(nameof(ElSaldoInicialPuedeSerNegativoYElAltaRechazaLoQueNoEsValido));

        var negativa = await CrearOkAsync(ctx, "En descubierto", saldoInicial: -250.5m);
        Assert.Equal(-250.5m, negativa.SaldoInicial);
        Assert.Equal(-250.5m, negativa.SaldoActual);

        var casos = new (AltaDeCuentaTesoreria Cuerpo, HttpStatusCode Estado, string Codigo)[]
        {
            (new(ctx.IdEmpresa, "   ", 0m), HttpStatusCode.BadRequest, "nombre_requerido"),
            (new(ctx.IdEmpresa, new string('a', 151), 0m), HttpStatusCode.BadRequest, "nombre_muy_largo"),
            (new(ctx.IdEmpresa, "Tres decimales", 0.001m), HttpStatusCode.BadRequest, "saldo_inicial_invalido"),
            (new(ctx.IdEmpresa, "Fuera de rango", 1_000_000_000_000m), HttpStatusCode.BadRequest, "saldo_inicial_invalido"),
            (new(0, "Sin empresa", 0m), HttpStatusCode.BadRequest, "referencia_invalida"),
            (new(int.MaxValue, "Empresa inexistente", 0m), HttpStatusCode.BadRequest, "referencia_invalida"),
        };

        foreach (var (cuerpo, estado, codigo) in casos)
        {
            var respuesta = await ctx.Admin.PostAsJsonAsync(Ruta, cuerpo);
            Assert.Equal(estado, respuesta.StatusCode);
            Assert.Equal(codigo, await CodigoAsync(respuesta));
        }

        Assert.Single(await ListarAsync(ctx.Admin));
    }

    [Fact]
    public async Task NoSePuedeCrearUnaCuentaEnUnaEmpresaDeOtroTenant()
    {
        var a = await PrepararAsync(nameof(NoSePuedeCrearUnaCuentaEnUnaEmpresaDeOtroTenant) + "A");
        var b = await PrepararAsync(nameof(NoSePuedeCrearUnaCuentaEnUnaEmpresaDeOtroTenant) + "B");

        var respuesta = await a.Admin.PostAsJsonAsync(Ruta, new AltaDeCuentaTesoreria(b.IdEmpresa, "Intrusa", 0m));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("referencia_invalida", await CodigoAsync(respuesta));
        Assert.Empty(await ListarAsync(b.Admin));
    }

    [Fact]
    public async Task ElNombreRepetidoEnLaEmpresaDa409SinDistinguirMayusculasYSeLiberaConLaBaja()
    {
        var ctx = await PrepararAsync(nameof(ElNombreRepetidoEnLaEmpresaDa409SinDistinguirMayusculasYSeLiberaConLaBaja));
        var otraEmpresa = await CrearEmpresaAsync(ctx.IdTenant, "Otra empresa");
        var primera = await CrearOkAsync(ctx, "Mercado Pago");
        var segunda = await CrearOkAsync(ctx, "Banco");

        var repetidaAlCrear = await ctx.Admin.PostAsJsonAsync(
            Ruta, new AltaDeCuentaTesoreria(ctx.IdEmpresa, "MERCADO pago", 0m));
        Assert.Equal(HttpStatusCode.Conflict, repetidaAlCrear.StatusCode);
        Assert.Equal("cuenta_tesoreria_duplicada", await CodigoAsync(repetidaAlCrear));

        var repetidaAlEditar = await ctx.Admin.PutAsJsonAsync(
            $"{Ruta}/{segunda.Id}", new EdicionDeCuentaTesoreria("Mercado Pago", 0m, true));
        Assert.Equal(HttpStatusCode.Conflict, repetidaAlEditar.StatusCode);
        Assert.Equal("cuenta_tesoreria_duplicada", await CodigoAsync(repetidaAlEditar));

        var mismoNombreEnOtraEmpresa = await CrearOkAsync(ctx, "Mercado Pago", idEmpresa: otraEmpresa);
        Assert.NotEqual(primera.Id, mismoNombreEnOtraEmpresa.Id);

        var conservaSuPropioNombre = await ctx.Admin.PutAsJsonAsync(
            $"{Ruta}/{primera.Id}", new EdicionDeCuentaTesoreria("Mercado Pago", 5m, true));
        Assert.Equal(HttpStatusCode.OK, conservaSuPropioNombre.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"{Ruta}/{primera.Id}")).StatusCode);
        var reutilizada = await CrearOkAsync(ctx, "Mercado Pago");
        Assert.NotEqual(primera.Id, reutilizada.Id);
    }

    [Fact]
    public async Task UnAltaQueChocaConUnaFilaSinComitearLlegaComo409PorElIndiceUnico()
    {
        var ctx = await PrepararAsync(nameof(UnAltaQueChocaConUnaFilaSinComitearLlegaComo409PorElIndiceUnico));

        // La fila queda sin comitear: el pre-chequeo del servicio no la ve y el INSERT espera en el índice único.
        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        await using var transaccion = await conexion.BeginTransactionAsync();
        await using (var insert = conexion.CreateCommand())
        {
            insert.Transaction = transaccion;
            insert.CommandText =
                "INSERT INTO cuentas_tesoreria (id_tenant, id_empresa, nombre, created_at, updated_at) " +
                "VALUES ($1, $2, 'Carrera', now(), now())";
            Parametros(insert, ctx.IdTenant, ctx.IdEmpresa);
            await insert.ExecuteNonQueryAsync();
        }

        var pedido = ctx.Admin.PostAsJsonAsync(Ruta, new AltaDeCuentaTesoreria(ctx.IdEmpresa, "Carrera", 0m));
        await EsperarUnaSesionBloqueadaAsync();
        await transaccion.CommitAsync();

        var respuesta = await pedido;
        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("cuenta_tesoreria_duplicada", await CodigoAsync(respuesta));
        Assert.Single(await ListarAsync(ctx.Admin));
    }

    [Fact]
    public async Task ElSaldoActualEsElFinalDelUltimoMovimientoYSinMovimientosElSaldoInicial()
    {
        var ctx = await PrepararAsync(nameof(ElSaldoActualEsElFinalDelUltimoMovimientoYSinMovimientosElSaldoInicial));
        var sinMovimientos = await CrearOkAsync(ctx, "Sin movimientos", saldoInicial: 100m);
        var conMovimientos = await CrearOkAsync(ctx, "Con movimientos", saldoInicial: 100m);

        await InsertarMovimientoAsync(ctx, conMovimientos.Id, inicio: 100m, ingreso: 50m, egreso: 0m);
        await InsertarMovimientoAsync(ctx, conMovimientos.Id, inicio: 150m, ingreso: 0m, egreso: 400m);

        var filas = await ListarAsync(ctx.Admin);

        var sin = filas.Single(c => c.Id == sinMovimientos.Id);
        Assert.Equal(100m, sin.SaldoActual);
        Assert.True(sin.SaldoInicialEditable);

        var con = filas.Single(c => c.Id == conMovimientos.Id);
        Assert.Equal(-250m, con.SaldoActual);
        Assert.Equal(100m, con.SaldoInicial);
        Assert.False(con.SaldoInicialEditable);

        var obtenida = (await ctx.Admin.GetFromJsonAsync<CuentaTesoreriaListado>($"{Ruta}/{conMovimientos.Id}"))!;
        Assert.Equal(con, obtenida);
    }

    [Fact]
    public async Task ElSaldoInicialNoCambiaConMovimientosPeroSiSinElloYElResto()
    {
        var ctx = await PrepararAsync(nameof(ElSaldoInicialNoCambiaConMovimientosPeroSiSinElloYElResto));
        var cuenta = await CrearOkAsync(ctx, "Cuenta", saldoInicial: 100m);

        var sinMovimientos = await ctx.Admin.PutAsJsonAsync(
            $"{Ruta}/{cuenta.Id}", new EdicionDeCuentaTesoreria("Cuenta", 300m, true));
        Assert.Equal(HttpStatusCode.OK, sinMovimientos.StatusCode);

        await InsertarMovimientoAsync(ctx, cuenta.Id, inicio: 300m, ingreso: 10m, egreso: 0m);

        var conCambio = await ctx.Admin.PutAsJsonAsync(
            $"{Ruta}/{cuenta.Id}", new EdicionDeCuentaTesoreria("Cuenta renombrada", 999m, true));
        Assert.Equal(HttpStatusCode.Conflict, conCambio.StatusCode);
        Assert.Equal("saldo_inicial_con_movimientos", await CodigoAsync(conCambio));

        var sinCambio = await ctx.Admin.PutAsJsonAsync(
            $"{Ruta}/{cuenta.Id}", new EdicionDeCuentaTesoreria("Cuenta renombrada", 300m, false));
        Assert.Equal(HttpStatusCode.OK, sinCambio.StatusCode);
        var editada = (await sinCambio.Content.ReadFromJsonAsync<CuentaTesoreriaListado>())!;
        Assert.Equal("Cuenta renombrada", editada.Nombre);
        Assert.False(editada.Activo);
        Assert.Equal(300m, editada.SaldoInicial);
        Assert.Equal(310m, editada.SaldoActual);
    }

    [Fact]
    public async Task UnPrimerMovimientoSinComitearHaceEsperarALaEdicionYLuegoElSaldoInicialQuedaProtegido()
    {
        var ctx = await PrepararAsync(nameof(UnPrimerMovimientoSinComitearHaceEsperarALaEdicionYLuegoElSaldoInicialQuedaProtegido));
        var cuenta = await CrearOkAsync(ctx, "Cuenta", saldoInicial: 100m);

        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        await using var transaccion = await conexion.BeginTransactionAsync();
        await using (var insert = conexion.CreateCommand())
        {
            insert.Transaction = transaccion;
            insert.CommandText = SqlMovimiento;
            Parametros(insert, ctx.IdTenant, cuenta.Id, 100m, 10m, 0m, 110m, ctx.IdUsuarioAdmin);
            await insert.ExecuteNonQueryAsync();
        }

        var pedido = ctx.Admin.PutAsJsonAsync(
            $"{Ruta}/{cuenta.Id}", new EdicionDeCuentaTesoreria("Cuenta", 500m, true));
        await EsperarUnaSesionBloqueadaAsync();
        await transaccion.CommitAsync();

        var respuesta = await pedido;
        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("saldo_inicial_con_movimientos", await CodigoAsync(respuesta));
        Assert.Equal(100m, (await ctx.Admin.GetFromJsonAsync<CuentaTesoreriaListado>($"{Ruta}/{cuenta.Id}"))!.SaldoInicial);
    }

    [Fact]
    public async Task LaBajaSeRechazaConUnMedioDePagoQueLaUsaAunqueEsteDadoDeBajaYDesactivarSiempreSePuede()
    {
        var ctx = await PrepararAsync(nameof(LaBajaSeRechazaConUnMedioDePagoQueLaUsaAunqueEsteDadoDeBajaYDesactivarSiempreSePuede));
        var conMedioVivo = await CrearOkAsync(ctx, "Con medio vivo");
        var conMedioEliminado = await CrearOkAsync(ctx, "Con medio eliminado");
        await SembrarMedioAsync(ctx, conMedioVivo.Id);
        await SembrarMedioAsync(ctx, conMedioEliminado.Id, eliminado: true);

        foreach (var cuenta in new[] { conMedioVivo, conMedioEliminado })
        {
            var baja = await ctx.Admin.DeleteAsync($"{Ruta}/{cuenta.Id}");
            Assert.Equal(HttpStatusCode.Conflict, baja.StatusCode);
            Assert.Equal("cuenta_tesoreria_en_uso", await CodigoAsync(baja));

            var desactivada = await ctx.Admin.PutAsJsonAsync(
                $"{Ruta}/{cuenta.Id}", new EdicionDeCuentaTesoreria(cuenta.Nombre, 0m, false));
            Assert.Equal(HttpStatusCode.OK, desactivada.StatusCode);
            Assert.False((await ctx.Admin.GetFromJsonAsync<CuentaTesoreriaListado>($"{Ruta}/{cuenta.Id}"))!.Activo);
        }
    }

    [Fact]
    public async Task LaBajaSeRechazaConMovimientosYElMensajeLosNombra()
    {
        var ctx = await PrepararAsync(nameof(LaBajaSeRechazaConMovimientosYElMensajeLosNombra));
        var cuenta = await CrearOkAsync(ctx, "Con movimientos");
        await InsertarMovimientoAsync(ctx, cuenta.Id, inicio: 0m, ingreso: 5m, egreso: 0m);

        var baja = await ctx.Admin.DeleteAsync($"{Ruta}/{cuenta.Id}");

        Assert.Equal(HttpStatusCode.Conflict, baja.StatusCode);
        var problema = await baja.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cuenta_tesoreria_en_uso", problema.GetProperty("codigo").GetString());
        Assert.Contains("movimientos de cuentas de tesorería", problema.GetProperty("title").GetString());
        Assert.Equal(cuenta.Id, Assert.Single(await ListarAsync(ctx.Admin)).Id);
    }

    [Fact]
    public async Task UnaCuentaSinReferenciasSeDaDeBajaAunqueEstuvieraInactiva()
    {
        var ctx = await PrepararAsync(nameof(UnaCuentaSinReferenciasSeDaDeBajaAunqueEstuvieraInactiva));
        var cuenta = await CrearOkAsync(ctx, "Inactiva", activo: false);

        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"{Ruta}/{cuenta.Id}")).StatusCode);
        Assert.Empty(await ListarAsync(ctx.Admin));
    }

    [Fact]
    public async Task ActualizarYEliminarUnIdInexistenteDan404AntesDeValidarElCuerpoOElNombre()
    {
        var ctx = await PrepararAsync(nameof(ActualizarYEliminarUnIdInexistenteDan404AntesDeValidarElCuerpoOElNombre));
        var existente = await CrearOkAsync(ctx, "Existente");
        var eliminada = await CrearOkAsync(ctx, "Eliminada");
        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"{Ruta}/{eliminada.Id}")).StatusCode);

        var cuerposInvalidos = new[]
        {
            new EdicionDeCuentaTesoreria("", 0m, true),
            new EdicionDeCuentaTesoreria("Existente", 0.001m, true),
            new EdicionDeCuentaTesoreria("Existente", 5m, true),
        };

        foreach (var id in new[] { int.MaxValue, eliminada.Id })
        {
            foreach (var cuerpo in cuerposInvalidos)
            {
                var respuesta = await ctx.Admin.PutAsJsonAsync($"{Ruta}/{id}", cuerpo);
                Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
                Assert.Equal("no_encontrado", await CodigoAsync(respuesta));
            }

            Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.DeleteAsync($"{Ruta}/{id}")).StatusCode);
        }

        Assert.Equal(existente.Id, Assert.Single(await ListarAsync(ctx.Admin)).Id);
    }

    [Fact]
    public async Task LaCuentaDeOtroTenantEsInvisibleEInalterable()
    {
        var a = await PrepararAsync(nameof(LaCuentaDeOtroTenantEsInvisibleEInalterable) + "A");
        var b = await PrepararAsync(nameof(LaCuentaDeOtroTenantEsInvisibleEInalterable) + "B");
        var deA = await CrearOkAsync(a, "De A", saldoInicial: 10m);

        Assert.Empty(await ListarAsync(b.Admin));
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.GetAsync($"{Ruta}/{deA.Id}")).StatusCode);

        var edicion = await b.Admin.PutAsJsonAsync($"{Ruta}/{deA.Id}", new EdicionDeCuentaTesoreria("Tomada", 99m, false));
        Assert.Equal(HttpStatusCode.NotFound, edicion.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.DeleteAsync($"{Ruta}/{deA.Id}")).StatusCode);

        Assert.Equal(deA, (await ListarAsync(a.Admin)).Single());
    }

    [Fact]
    public async Task SoloElAdminUsaLaSuperficieYSupervisorVendedorYAnonimoQuedanAfuera()
    {
        var ctx = await PrepararAsync(nameof(SoloElAdminUsaLaSuperficieYSupervisorVendedorYAnonimoQuedanAfuera));
        var cuenta = await CrearOkAsync(ctx, "Cuenta");
        using var anonimo = fixture.CreateClient();

        foreach (var (cliente, esperado) in new[]
        {
            (ctx.Supervisor, HttpStatusCode.Forbidden),
            (ctx.Vendedor, HttpStatusCode.Forbidden),
            (anonimo, HttpStatusCode.Unauthorized),
        })
        {
            Assert.Equal(esperado, (await cliente.GetAsync(Ruta)).StatusCode);
            Assert.Equal(esperado, (await cliente.GetAsync($"{Ruta}/{cuenta.Id}")).StatusCode);
            Assert.Equal(
                esperado,
                (await cliente.PostAsJsonAsync(Ruta, new AltaDeCuentaTesoreria(ctx.IdEmpresa, "Otra", 0m))).StatusCode);
            Assert.Equal(
                esperado,
                (await cliente.PutAsJsonAsync($"{Ruta}/{cuenta.Id}", new EdicionDeCuentaTesoreria("Otra", 0m, true))).StatusCode);
            Assert.Equal(esperado, (await cliente.DeleteAsync($"{Ruta}/{cuenta.Id}")).StatusCode);
        }

        Assert.Equal(cuenta, Assert.Single(await ListarAsync(ctx.Admin)));
    }
}
