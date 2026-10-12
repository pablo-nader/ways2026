using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Ways.Application.Organizacion;
using Ways.Application.Plantel;
using Ways.Application.Usuarios;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;

namespace Ways.IntegrationTests;

/// <summary>
/// Cuenta corriente del personal punta a punta contra Postgres real: carga de horas con valor hora
/// congelado, ajuste con signo, estado de cuenta paginado, políticas, 404 antes de validar, guarda
/// de baja y concurrencia sobre el saldo.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CuentaDePersonalEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";

    /// <summary>El INSERT crudo no pasa por los interceptores de comandos de EF, pero el COMMIT sí:
    /// rompe el primero con un error transitorio. Una estrategia con reintento volvería a ejecutar la
    /// escritura y respondería 201; sin reintento el error llega como 503.</summary>
    private sealed class InterceptorQueRompeElPrimerCommit : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
    {
        private int commits;

        public int Commits => Volatile.Read(ref commits);

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult> TransactionCommittingAsync(
            System.Data.Common.DbTransaction transaction,
            Microsoft.EntityFrameworkCore.Diagnostics.TransactionEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref commits) == 1)
            {
                throw new PostgresException("falla inyectada por la prueba al comitear", "ERROR", "ERROR", "40001");
            }

            return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
        }
    }

    private sealed record Contexto(int IdTenant, HttpClient Admin, HttpClient Supervisor, HttpClient Vendedor);

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

        return new Contexto(resultado.IdTenant, admin, supervisor, vendedor);
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

    private static async Task<int> CrearPersonalAsync(HttpClient admin, decimal valorHora = 1000m, string nombre = "Ana")
    {
        var respuesta = await admin.PostAsJsonAsync(
            "/api/personal", new DatosDePersonal(nombre, null, null, null, valorHora, null, null, null));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static Task<HttpResponseMessage> CargarAsync(
        HttpClient cliente, int id, string fecha, decimal horas, decimal? valorHora = null, string? detalle = null) =>
        cliente.PostAsJsonAsync(
            $"/api/personal/{id}/horas", new { fecha, horas, valorHora, detalle });

    private static Task<HttpResponseMessage> AjustarAsync(
        HttpClient cliente, int id, decimal importe, string? detalle = "corrección de carga", string? fecha = null) =>
        cliente.PostAsJsonAsync($"/api/personal/{id}/ajustes", new { importe, detalle, fecha });

    private static async Task<JsonElement> CuerpoAsync(HttpResponseMessage respuesta, HttpStatusCode esperado)
    {
        var texto = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == esperado, $"{respuesta.StatusCode}: {texto}");
        return JsonDocument.Parse(texto).RootElement.Clone();
    }

    private static async Task<string?> CodigoAsync(HttpResponseMessage respuesta, HttpStatusCode esperado) =>
        (await CuerpoAsync(respuesta, esperado)).GetProperty("codigo").GetString();

    private static async Task<JsonElement> CuentaAsync(HttpClient cliente, int id, string consulta = "")
    {
        var respuesta = await cliente.GetAsync($"/api/personal/{id}/cuenta{consulta}");
        return await CuerpoAsync(respuesta, HttpStatusCode.OK);
    }

    private static async Task<decimal> SaldoAsync(HttpClient cliente, int id) =>
        (await CuentaAsync(cliente, id, "?historico=true")).GetProperty("encabezado").GetProperty("saldo").GetDecimal();

    [Fact]
    public async Task LaCargaUsaElValorHoraDelPersonalLoCongelaYElCambioPosteriorNoToca()
    {
        var ctx = await PrepararAsync(nameof(LaCargaUsaElValorHoraDelPersonalLoCongelaYElCambioPosteriorNoToca));
        var id = await CrearPersonalAsync(ctx.Admin, valorHora: 1000m);

        var respuesta = await CargarAsync(ctx.Admin, id, "2026-10-01", 8m, detalle: "  turno mañana ");
        var primera = await CuerpoAsync(respuesta, HttpStatusCode.Created);
        Assert.Equal($"/api/personal/{id}/cuenta", respuesta.Headers.Location!.OriginalString);
        Assert.Equal(8000m, primera.GetProperty("importe").GetDecimal());
        Assert.Equal(1000m, primera.GetProperty("valorHora").GetDecimal());
        Assert.Equal(8m, primera.GetProperty("horas").GetDecimal());
        Assert.Equal(8000m, primera.GetProperty("saldoResultante").GetDecimal());
        Assert.Equal("2026-10-01", primera.GetProperty("fecha").GetString());
        Assert.Equal("turno mañana", primera.GetProperty("detalle").GetString());
        Assert.Equal("horas", primera.GetProperty("tipo").GetString(), ignoreCase: true);

        var edicion = await ctx.Admin.PutAsJsonAsync(
            $"/api/personal/{id}", new DatosDePersonal("Ana", null, null, null, 2000m, null, null, null));
        Assert.Equal(HttpStatusCode.OK, edicion.StatusCode);

        var segunda = await CuerpoAsync(await CargarAsync(ctx.Admin, id, "2026-10-02", 1.5m), HttpStatusCode.Created);
        Assert.Equal(2000m, segunda.GetProperty("valorHora").GetDecimal());
        Assert.Equal(3000m, segunda.GetProperty("importe").GetDecimal());
        Assert.Equal(11000m, segunda.GetProperty("saldoResultante").GetDecimal());

        var cuenta = await CuentaAsync(ctx.Admin, id, "?historico=true");
        var items = cuenta.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        // La más nueva primero; la carga vieja conserva el valor hora con el que se hizo.
        Assert.Equal(3000m, items[0].GetProperty("importe").GetDecimal());
        Assert.Equal(1000m, items[1].GetProperty("valorHora").GetDecimal());
        Assert.Equal(11000m, cuenta.GetProperty("encabezado").GetProperty("saldo").GetDecimal());
    }

    [Fact]
    public async Task UnValorHoraExplicitoGanaSobreElDelPersonalYElImporteSeRedondeaADosDecimales()
    {
        var ctx = await PrepararAsync(nameof(UnValorHoraExplicitoGanaSobreElDelPersonalYElImporteSeRedondeaADosDecimales));
        var id = await CrearPersonalAsync(ctx.Admin, valorHora: 1000m);

        var carga = await CuerpoAsync(
            await CargarAsync(ctx.Admin, id, "2026-10-01", 0.25m, valorHora: 1200.50m), HttpStatusCode.Created);

        Assert.Equal(1200.50m, carga.GetProperty("valorHora").GetDecimal());
        Assert.Equal(300.13m, carga.GetProperty("importe").GetDecimal());
    }

    [Fact]
    public async Task VariasCargasDelMismoDiaConviven()
    {
        var ctx = await PrepararAsync(nameof(VariasCargasDelMismoDiaConviven));
        var id = await CrearPersonalAsync(ctx.Admin, valorHora: 500m);

        await CuerpoAsync(await CargarAsync(ctx.Admin, id, "2026-10-01", 4m), HttpStatusCode.Created);
        await CuerpoAsync(await CargarAsync(ctx.Admin, id, "2026-10-01", 3m), HttpStatusCode.Created);

        var cuenta = await CuentaAsync(ctx.Admin, id, "?historico=true");
        Assert.Equal(2, cuenta.GetProperty("total").GetInt32());
        Assert.Equal(3500m, cuenta.GetProperty("encabezado").GetProperty("saldo").GetDecimal());
    }

    [Fact]
    public async Task ElAjusteConSignoCorrigeElSaldoYGuardaElDetalle()
    {
        var ctx = await PrepararAsync(nameof(ElAjusteConSignoCorrigeElSaldoYGuardaElDetalle));
        var id = await CrearPersonalAsync(ctx.Admin, valorHora: 1000m);
        await CuerpoAsync(await CargarAsync(ctx.Admin, id, "2026-10-01", 10m), HttpStatusCode.Created);

        var menos = await CuerpoAsync(
            await AjustarAsync(ctx.Admin, id, -2500.25m, " cargó dos horas de más ", "2026-10-02"), HttpStatusCode.Created);
        Assert.Equal("ajuste", menos.GetProperty("tipo").GetString(), ignoreCase: true);
        Assert.Equal(-2500.25m, menos.GetProperty("importe").GetDecimal());
        Assert.Equal(7499.75m, menos.GetProperty("saldoResultante").GetDecimal());
        Assert.Equal("cargó dos horas de más", menos.GetProperty("detalle").GetString());
        Assert.Equal("2026-10-02", menos.GetProperty("fecha").GetString());
        Assert.Equal(JsonValueKind.Null, menos.GetProperty("horas").ValueKind);
        Assert.Equal(JsonValueKind.Null, menos.GetProperty("valorHora").ValueKind);

        var mas = await CuerpoAsync(await AjustarAsync(ctx.Admin, id, 100m), HttpStatusCode.Created);
        Assert.Equal(7599.75m, mas.GetProperty("saldoResultante").GetDecimal());
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"), mas.GetProperty("fecha").GetString());
        Assert.Equal(7599.75m, await SaldoAsync(ctx.Admin, id));
    }

    [Theory]
    [InlineData(0, "ajuste_importe_invalido", "corrección de carga")]
    [InlineData(10, "ajuste_detalle_requerido", null)]
    [InlineData(10, "ajuste_detalle_requerido", "abc")]
    [InlineData(10.005, "ajuste_importe_invalido", "corrección de carga")]
    [InlineData(1000000000000, "importe_fuera_de_rango", "corrección de carga")]
    public async Task UnAjusteInvalidoSeRechazaConSuCodigoYNoEscribeNada(double importe, string codigo, string? detalle)
    {
        var ctx = await PrepararAsync(nameof(UnAjusteInvalidoSeRechazaConSuCodigoYNoEscribeNada) + codigo + importe + detalle);
        var id = await CrearPersonalAsync(ctx.Admin);

        var respuesta = await AjustarAsync(ctx.Admin, id, (decimal)importe, detalle);

        Assert.Equal(codigo, await CodigoAsync(respuesta, HttpStatusCode.BadRequest));
        Assert.Equal(0, (await CuentaAsync(ctx.Admin, id, "?historico=true")).GetProperty("total").GetInt32());
    }

    [Theory]
    [InlineData("2026-10-01", 0, null, "horas_invalidas")]
    [InlineData("2026-10-01", 24.5, null, "horas_invalidas")]
    [InlineData("2026-10-01", 1.001, null, "horas_invalidas")]
    [InlineData("2026-10-01", 8, -1.0, "valor_hora_invalido")]
    [InlineData("2026-10-01", 8, 1000000000000.0, "valor_hora_invalido")]
    [InlineData(null, 8, null, "fecha_requerida")]
    public async Task UnaCargaInvalidaSeRechazaConSuCodigoYNoEscribeNada(
        string? fecha, double horas, double? valorHora, string codigo)
    {
        var ctx = await PrepararAsync(nameof(UnaCargaInvalidaSeRechazaConSuCodigoYNoEscribeNada) + codigo + horas + valorHora);
        var id = await CrearPersonalAsync(ctx.Admin);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/personal/{id}/horas", new { fecha, horas = (decimal)horas, valorHora = (decimal?)valorHora });

        Assert.Equal(codigo, await CodigoAsync(respuesta, HttpStatusCode.BadRequest));
        Assert.Equal(0, (await CuentaAsync(ctx.Admin, id, "?historico=true")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task ElEstadoDeCuentaFiltraPorRangoYPaginaConOrdenEstable()
    {
        var ctx = await PrepararAsync(nameof(ElEstadoDeCuentaFiltraPorRangoYPaginaConOrdenEstable));
        var id = await CrearPersonalAsync(ctx.Admin, valorHora: 100m);
        foreach (var fecha in new[] { "2026-08-30", "2026-09-10", "2026-09-10", "2026-09-20", "2026-10-05" })
        {
            await CuerpoAsync(await CargarAsync(ctx.Admin, id, fecha, 1m), HttpStatusCode.Created);
        }

        var septiembre = await CuentaAsync(ctx.Admin, id, "?desde=2026-09-01&hasta=2026-09-30");
        Assert.Equal(3, septiembre.GetProperty("total").GetInt32());
        Assert.Equal("2026-09-01", septiembre.GetProperty("desde").GetString());
        Assert.Equal("2026-09-30", septiembre.GetProperty("hasta").GetString());
        Assert.Equal(500m, septiembre.GetProperty("encabezado").GetProperty("saldo").GetDecimal());

        var soloDesde = await CuentaAsync(ctx.Admin, id, "?desde=2026-09-15");
        Assert.Equal(2, soloDesde.GetProperty("total").GetInt32());

        var soloHasta = await CuentaAsync(ctx.Admin, id, "?hasta=2026-09-10&historico=false");
        Assert.Equal(3, soloHasta.GetProperty("total").GetInt32());

        // Páginas de 2 sobre todo el historial: sin repetidos ni salteados, por fecha e id descendentes.
        var filas = new List<(string Fecha, int Id)>();
        for (var pagina = 1; pagina <= 3; pagina++)
        {
            var cuenta = await CuentaAsync(ctx.Admin, id, $"?historico=true&pagina={pagina}&tamanio=2");
            Assert.Equal(5, cuenta.GetProperty("total").GetInt32());
            filas.AddRange(cuenta.GetProperty("items").EnumerateArray().Select(
                i => (i.GetProperty("fecha").GetString()!, i.GetProperty("idMovimiento").GetInt32())));
        }

        Assert.Equal(5, filas.Select(f => f.Id).Distinct().Count());
        Assert.Equal(
            ["2026-10-05", "2026-09-20", "2026-09-10", "2026-09-10", "2026-08-30"], filas.Select(f => f.Fecha));
        Assert.Equal(filas.OrderByDescending(f => f.Fecha).ThenByDescending(f => f.Id), filas);
    }

    [Fact]
    public async Task SinHistoricoNiRangoLaVentanaEsElUltimoMes()
    {
        var ctx = await PrepararAsync(nameof(SinHistoricoNiRangoLaVentanaEsElUltimoMes));
        var id = await CrearPersonalAsync(ctx.Admin, valorHora: 100m);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        await CuerpoAsync(await CargarAsync(ctx.Admin, id, hoy.AddDays(-90).ToString("yyyy-MM-dd"), 1m), HttpStatusCode.Created);
        await CuerpoAsync(await CargarAsync(ctx.Admin, id, hoy.ToString("yyyy-MM-dd"), 1m), HttpStatusCode.Created);

        var porDefecto = await CuentaAsync(ctx.Admin, id);
        var historico = await CuentaAsync(ctx.Admin, id, "?historico=true");

        Assert.Equal(1, porDefecto.GetProperty("total").GetInt32());
        Assert.Equal(2, historico.GetProperty("total").GetInt32());
        Assert.Equal(200m, porDefecto.GetProperty("encabezado").GetProperty("saldo").GetDecimal());
    }

    [Fact]
    public async Task ElListadoYElDetalleDelPersonalExponenElSaldo()
    {
        var ctx = await PrepararAsync(nameof(ElListadoYElDetalleDelPersonalExponenElSaldo));
        var id = await CrearPersonalAsync(ctx.Admin, valorHora: 100m);
        await CuerpoAsync(await CargarAsync(ctx.Admin, id, "2026-10-01", 5m), HttpStatusCode.Created);

        var detalle = await CuerpoAsync(await ctx.Supervisor.GetAsync($"/api/personal/{id}"), HttpStatusCode.OK);
        var listado = await CuerpoAsync(await ctx.Supervisor.GetAsync("/api/personal"), HttpStatusCode.OK);

        Assert.Equal(500m, detalle.GetProperty("saldo").GetDecimal());
        Assert.Equal(500m, listado.GetProperty("items")[0].GetProperty("saldo").GetDecimal());
    }

    [Fact]
    public async Task AdminYSupervisorEscribenYLeenPeroElVendedorQuedaAfuera()
    {
        var ctx = await PrepararAsync(nameof(AdminYSupervisorEscribenYLeenPeroElVendedorQuedaAfuera));
        var id = await CrearPersonalAsync(ctx.Admin, valorHora: 100m);

        await CuerpoAsync(await CargarAsync(ctx.Supervisor, id, "2026-10-01", 1m), HttpStatusCode.Created);
        await CuerpoAsync(await AjustarAsync(ctx.Supervisor, id, 5m), HttpStatusCode.Created);
        await CuentaAsync(ctx.Supervisor, id);

        Assert.Equal(HttpStatusCode.Forbidden, (await CargarAsync(ctx.Vendedor, id, "2026-10-01", 1m)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AjustarAsync(ctx.Vendedor, id, 5m)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ctx.Vendedor.GetAsync($"/api/personal/{id}/cuenta")).StatusCode);

        using var anonimo = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await CargarAsync(anonimo, id, "2026-10-01", 1m)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync($"/api/personal/{id}/cuenta")).StatusCode);
    }

    [Fact]
    public async Task UnPersonalInexistenteBajaOAjenoResponde404AntesDeValidarElCuerpo()
    {
        var ctx = await PrepararAsync(nameof(UnPersonalInexistenteBajaOAjenoResponde404AntesDeValidarElCuerpo));
        var otro = await PrepararAsync(nameof(UnPersonalInexistenteBajaOAjenoResponde404AntesDeValidarElCuerpo) + "otro");
        var ajeno = await CrearPersonalAsync(otro.Admin);
        var dadoDeBaja = await CrearPersonalAsync(ctx.Admin, nombre: "Baja");
        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"/api/personal/{dadoDeBaja}")).StatusCode);

        foreach (var id in new[] { 987654, ajeno, dadoDeBaja })
        {
            // Cuerpos inválidos a propósito: horas negativas, sin fecha, ajuste en cero y sin detalle.
            var horas = await ctx.Admin.PostAsJsonAsync($"/api/personal/{id}/horas", new { horas = -1, valorHora = -5 });
            var ajuste = await ctx.Admin.PostAsJsonAsync($"/api/personal/{id}/ajustes", new { importe = 0 });

            Assert.Equal(HttpStatusCode.NotFound, horas.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, ajuste.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.GetAsync($"/api/personal/{id}/cuenta")).StatusCode);
        }
    }

    [Fact]
    public async Task UnPersonalConMovimientosNoSePuedeDarDeBajaPeroUnoSinMovimientosSi()
    {
        var ctx = await PrepararAsync(nameof(UnPersonalConMovimientosNoSePuedeDarDeBajaPeroUnoSinMovimientosSi));
        var conMovimientos = await CrearPersonalAsync(ctx.Admin, nombre: "Con");
        var sinMovimientos = await CrearPersonalAsync(ctx.Admin, nombre: "Sin");
        await CuerpoAsync(await CargarAsync(ctx.Admin, conMovimientos, "2026-10-01", 1m), HttpStatusCode.Created);

        var rechazo = await ctx.Admin.DeleteAsync($"/api/personal/{conMovimientos}");

        Assert.Equal("personal_en_uso", await CodigoAsync(rechazo, HttpStatusCode.Conflict));
        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.GetAsync($"/api/personal/{conMovimientos}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"/api/personal/{sinMovimientos}")).StatusCode);
    }

    [Fact]
    public async Task CargasConcurrentesDejanElSaldoExactoYLaCadenaDeSaldosResultantesConsistente()
    {
        var ctx = await PrepararAsync(nameof(CargasConcurrentesDejanElSaldoExactoYLaCadenaDeSaldosResultantesConsistente));
        var id = await CrearPersonalAsync(ctx.Admin, valorHora: 100m);
        const int cargas = 16;

        var respuestas = await Task.WhenAll(Enumerable.Range(1, cargas).Select(i =>
            i % 4 == 0
                ? AjustarAsync(ctx.Admin, id, -10m)
                : CargarAsync(ctx.Admin, id, "2026-10-01", i % 8 + 1)));

        foreach (var respuesta in respuestas)
        {
            Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        }

        var cuenta = await CuentaAsync(ctx.Admin, id, "?historico=true&tamanio=200");
        var movimientos = cuenta.GetProperty("items").EnumerateArray()
            .Select(m => (Id: m.GetProperty("idMovimiento").GetInt32(),
                          Importe: m.GetProperty("importe").GetDecimal(),
                          Saldo: m.GetProperty("saldoResultante").GetDecimal()))
            .OrderBy(m => m.Id)
            .ToList();

        Assert.Equal(cargas, movimientos.Count);
        var acumulado = 0m;
        foreach (var movimiento in movimientos)
        {
            acumulado += movimiento.Importe;
            Assert.Equal(acumulado, movimiento.Saldo);
        }

        Assert.Equal(acumulado, cuenta.GetProperty("encabezado").GetProperty("saldo").GetDecimal());
    }

    private static async Task<bool> EsperarBackendBloqueadoAsync(NpgsqlConnection conexionPoll, int pidExcluido1, int pidExcluido2)
    {
        var limite = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < limite)
        {
            await using var comando = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND pid <> $1 AND pid <> $2",
                conexionPoll);
            comando.Parameters.AddWithValue(pidExcluido1);
            comando.Parameters.AddWithValue(pidExcluido2);

            if ((long)(await comando.ExecuteScalarAsync())! > 0)
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    private static async Task<int> BackendPidAsync(NpgsqlConnection conexion) =>
        (int)(await new NpgsqlCommand("SELECT pg_backend_pid()", conexion).ExecuteScalarAsync())!;

    private async Task<T> EscalarDelDuenoAsync<T>(string sql, params object[] parametros)
    {
        await using var conexion = new NpgsqlConnection(fixture.OwnerConnectionString);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexion);
        foreach (var valor in parametros)
        {
            comando.Parameters.AddWithValue(valor);
        }

        return (T)(await comando.ExecuteScalarAsync())!;
    }

    /// <summary>Una carga sin comitear toma <c>FOR KEY SHARE</c> sobre el personal por su FK, que choca
    /// contra el <c>FOR UPDATE</c> de <c>BloquearFilaAsync</c>: la baja tiene que esperar, y al comitear
    /// la carga la baja ve la referencia ya comiteada y responde 409. Sin el lock la baja no espera, no
    /// ve la fila sin comitear y responde 204.</summary>
    [Fact]
    public async Task UnaCargaSinComitearHaceEsperarALaBajaYGanaLaReferencia()
    {
        var ctx = await PrepararAsync(nameof(UnaCargaSinComitearHaceEsperarALaBajaYGanaLaReferencia));
        var id = await CrearPersonalAsync(ctx.Admin);
        var idUsuario = await EscalarDelDuenoAsync<int>(
            "SELECT id_usuario FROM usuarios WHERE id_tenant = $1 ORDER BY id_usuario LIMIT 1", ctx.IdTenant);

        await using var conexionEscritor = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        var pidEscritor = await BackendPidAsync(conexionEscritor);
        await using var transaccionEscritor = await conexionEscritor.BeginTransactionAsync();
        await using (var insertar = new NpgsqlCommand(
            "INSERT INTO movimientos_personal " +
            "(id_tenant, id_personal, id_empleado, fecha, tipo, importe, saldo_resultante, created_at) " +
            "VALUES ($1, $2, $3, DATE '2026-10-01', 'ajuste', 10, 10, now())",
            conexionEscritor, transaccionEscritor))
        {
            insertar.Parameters.AddWithValue(ctx.IdTenant);
            insertar.Parameters.AddWithValue(id);
            insertar.Parameters.AddWithValue(idUsuario);
            await insertar.ExecuteNonQueryAsync();
        }

        var bajaTask = ctx.Admin.DeleteAsync($"/api/personal/{id}");

        await using var conexionPoll = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        var pidPoll = await BackendPidAsync(conexionPoll);
        var observado = await EsperarBackendBloqueadoAsync(conexionPoll, pidEscritor, pidPoll);
        Assert.True(observado, "La baja nunca se observó esperando un lock: la prueba no está probando la carrera.");

        await transaccionEscritor.CommitAsync();

        Assert.Equal("personal_en_uso", await CodigoAsync(await bajaTask, HttpStatusCode.Conflict));
        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.GetAsync($"/api/personal/{id}")).StatusCode);
    }

    [Fact]
    public async Task ElEncabezadoDelEstadoDeCuentaEsPersonalSaldoYNuncaSeDerivaDelLibro()
    {
        var ctx = await PrepararAsync(nameof(ElEncabezadoDelEstadoDeCuentaEsPersonalSaldoYNuncaSeDerivaDelLibro));
        var id = await CrearPersonalAsync(ctx.Admin, valorHora: 100m);
        await CuerpoAsync(await CargarAsync(ctx.Admin, id, "2026-10-01", 1m), HttpStatusCode.Created);

        await using (var conexion = new NpgsqlConnection(fixture.OwnerConnectionString))
        {
            await conexion.OpenAsync();
            await using var desincronizar = new NpgsqlCommand(
                "UPDATE personal SET saldo = 777.77 WHERE id_personal = $1", conexion);
            desincronizar.Parameters.AddWithValue(id);
            Assert.Equal(1, await desincronizar.ExecuteNonQueryAsync());
        }

        var cuenta = await CuentaAsync(ctx.Admin, id, "?historico=true");

        Assert.Equal(777.77m, cuenta.GetProperty("encabezado").GetProperty("saldo").GetDecimal());
        Assert.Equal(100m, cuenta.GetProperty("items")[0].GetProperty("saldoResultante").GetDecimal());
    }

    [Fact]
    public async Task ElEstadoDeCuentaDevuelveCadaCampoPersistidoYElUsuarioQueCargo()
    {
        const string nombre = nameof(ElEstadoDeCuentaDevuelveCadaCampoPersistidoYElUsuarioQueCargo);
        var ctx = await PrepararAsync(nombre);
        var id = await CrearPersonalAsync(ctx.Admin, valorHora: 1000m);
        var idAdmin = await EscalarDelDuenoAsync<int>(
            "SELECT id_usuario FROM usuarios WHERE mail = $1", $"{nombre.ToLowerInvariant()}@ways.test");
        var idSupervisor = await EscalarDelDuenoAsync<int>(
            "SELECT id_usuario FROM usuarios WHERE mail = $1", $"{nombre.ToLowerInvariant()}-supervisor@ways.test");
        Assert.NotEqual(idAdmin, idSupervisor);

        var carga = await CuerpoAsync(
            await CargarAsync(ctx.Supervisor, id, "2026-10-01", 2.5m, detalle: "  turno tarde "), HttpStatusCode.Created);
        var ajuste = await CuerpoAsync(
            await AjustarAsync(ctx.Admin, id, -100m, " descuento acordado ", "2026-10-02"), HttpStatusCode.Created);

        var cuenta = await CuentaAsync(ctx.Admin, id, "?historico=true");
        var items = cuenta.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("idMovimiento").GetInt32());

        var deLaCarga = items[carga.GetProperty("idMovimiento").GetInt32()];
        Assert.Equal("horas", deLaCarga.GetProperty("tipo").GetString(), ignoreCase: true);
        Assert.Equal(2.5m, deLaCarga.GetProperty("horas").GetDecimal());
        Assert.Equal(1000m, deLaCarga.GetProperty("valorHora").GetDecimal());
        Assert.Equal(2500m, deLaCarga.GetProperty("importe").GetDecimal());
        Assert.Equal("turno tarde", deLaCarga.GetProperty("detalle").GetString());
        Assert.Equal("2026-10-01", deLaCarga.GetProperty("fecha").GetString());

        var delAjuste = items[ajuste.GetProperty("idMovimiento").GetInt32()];
        Assert.Equal("ajuste", delAjuste.GetProperty("tipo").GetString(), ignoreCase: true);
        Assert.Equal(JsonValueKind.Null, delAjuste.GetProperty("horas").ValueKind);
        Assert.Equal("descuento acordado", delAjuste.GetProperty("detalle").GetString());
        Assert.Equal(2400m, delAjuste.GetProperty("saldoResultante").GetDecimal());

        await using var conexion = new NpgsqlConnection(fixture.OwnerConnectionString);
        await conexion.OpenAsync();
        foreach (var (movimiento, esperado) in new[]
        {
            (deLaCarga, idSupervisor), (delAjuste, idAdmin)
        })
        {
            await using var lectura = new NpgsqlCommand(
                "SELECT id_empleado, created_at FROM movimientos_personal WHERE id_movimiento = $1", conexion);
            lectura.Parameters.AddWithValue(movimiento.GetProperty("idMovimiento").GetInt32());
            await using var lector = await lectura.ExecuteReaderAsync();
            Assert.True(await lector.ReadAsync());

            Assert.Equal(esperado, lector.GetInt32(0));
            var creadoEn = movimiento.GetProperty("creadoEn").GetDateTimeOffset();
            Assert.Equal(lector.GetFieldValue<DateTimeOffset>(1), creadoEn);
            Assert.True(Math.Abs((creadoEn - DateTimeOffset.UtcNow).TotalMinutes) < 5);
        }
    }

    [Fact]
    public async Task ElEstadoDeCuentaDeUnPersonalNoMezclaLosMovimientosDeOtroDelMismoTenant()
    {
        var ctx = await PrepararAsync(nameof(ElEstadoDeCuentaDeUnPersonalNoMezclaLosMovimientosDeOtroDelMismoTenant));
        var ana = await CrearPersonalAsync(ctx.Admin, valorHora: 100m, nombre: "Ana");
        var beto = await CrearPersonalAsync(ctx.Admin, valorHora: 10m, nombre: "Beto");

        var deAna = new List<int>
        {
            (await CuerpoAsync(await CargarAsync(ctx.Admin, ana, "2026-10-01", 2m), HttpStatusCode.Created))
                .GetProperty("idMovimiento").GetInt32(),
            (await CuerpoAsync(await AjustarAsync(ctx.Admin, ana, -50m), HttpStatusCode.Created))
                .GetProperty("idMovimiento").GetInt32()
        };
        var deBeto = new List<int>
        {
            (await CuerpoAsync(await CargarAsync(ctx.Admin, beto, "2026-10-01", 3m), HttpStatusCode.Created))
                .GetProperty("idMovimiento").GetInt32(),
            (await CuerpoAsync(await CargarAsync(ctx.Admin, beto, "2026-10-02", 1m), HttpStatusCode.Created))
                .GetProperty("idMovimiento").GetInt32(),
            (await CuerpoAsync(await AjustarAsync(ctx.Admin, beto, 5m), HttpStatusCode.Created))
                .GetProperty("idMovimiento").GetInt32()
        };

        var cuentaAna = await CuentaAsync(ctx.Admin, ana, "?historico=true");
        var cuentaBeto = await CuentaAsync(ctx.Admin, beto, "?historico=true");

        Assert.Equal(2, cuentaAna.GetProperty("total").GetInt32());
        Assert.Equal(
            deAna.Order(),
            cuentaAna.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("idMovimiento").GetInt32()).Order());
        Assert.Equal(150m, cuentaAna.GetProperty("encabezado").GetProperty("saldo").GetDecimal());

        Assert.Equal(3, cuentaBeto.GetProperty("total").GetInt32());
        Assert.Equal(
            deBeto.Order(),
            cuentaBeto.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("idMovimiento").GetInt32()).Order());
        Assert.Equal(45m, cuentaBeto.GetProperty("encabezado").GetProperty("saldo").GetDecimal());
    }

    [Fact]
    public async Task LosExtremosDelRangoDeFechasSonInclusivos()
    {
        var ctx = await PrepararAsync(nameof(LosExtremosDelRangoDeFechasSonInclusivos));
        var id = await CrearPersonalAsync(ctx.Admin, valorHora: 100m);
        foreach (var fecha in new[] { "2026-09-09", "2026-09-10", "2026-09-11" })
        {
            await CuerpoAsync(await CargarAsync(ctx.Admin, id, fecha, 1m), HttpStatusCode.Created);
        }

        Assert.Equal(2, (await CuentaAsync(ctx.Admin, id, "?desde=2026-09-10")).GetProperty("total").GetInt32());
        Assert.Equal(2, (await CuentaAsync(ctx.Admin, id, "?desde=2026-09-01&hasta=2026-09-10")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await CuentaAsync(ctx.Admin, id, "?desde=2026-09-10&hasta=2026-09-10")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task LosImportesExactamenteEnElMaximoSeAceptan()
    {
        var ctx = await PrepararAsync(nameof(LosImportesExactamenteEnElMaximoSeAceptan));
        var conCarga = await CrearPersonalAsync(ctx.Admin, nombre: "Carga");
        var conAjuste = await CrearPersonalAsync(ctx.Admin, nombre: "Ajuste");

        var carga = await CuerpoAsync(
            await CargarAsync(ctx.Admin, conCarga, "2026-10-01", 1m, valorHora: 999_999_999_999.99m), HttpStatusCode.Created);
        var ajuste = await CuerpoAsync(await AjustarAsync(ctx.Admin, conAjuste, 999_999_999_999.99m), HttpStatusCode.Created);

        Assert.Equal(999_999_999_999.99m, carga.GetProperty("importe").GetDecimal());
        Assert.Equal(999_999_999_999.99m, ajuste.GetProperty("saldoResultante").GetDecimal());
    }

    [Theory]
    [InlineData("?hasta=0001-01-15", "fecha_invalida")]
    [InlineData("?pagina=2147483647", "pagina_invalida")]
    public async Task UnRangoOUnaPaginaFueraDeLimiteRespondeBadRequestYNoUn500(string consulta, string codigo)
    {
        var ctx = await PrepararAsync(nameof(UnRangoOUnaPaginaFueraDeLimiteRespondeBadRequestYNoUn500) + codigo);
        var id = await CrearPersonalAsync(ctx.Admin);

        var respuesta = await ctx.Admin.GetAsync($"/api/personal/{id}/cuenta{consulta}");

        Assert.Equal(codigo, await CodigoAsync(respuesta, HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task UnHastaEnElPrimerMesDeLaEraTodaviaSeResuelve()
    {
        var ctx = await PrepararAsync(nameof(UnHastaEnElPrimerMesDeLaEraTodaviaSeResuelve));
        var id = await CrearPersonalAsync(ctx.Admin);

        var cuenta = await CuentaAsync(ctx.Admin, id, "?hasta=0001-02-01");

        Assert.Equal("0001-01-01", cuenta.GetProperty("desde").GetString());
    }

    [Fact]
    public async Task UnFalloTransitorioAlComitearNoSeReintentaYNoDejaNadaEscrito()
    {
        var ctx = await PrepararAsync(nameof(UnFalloTransitorioAlComitearNoSeReintentaYNoDejaNadaEscrito));
        var id = await CrearPersonalAsync(ctx.Admin, valorHora: 100m);
        var interceptor = new InterceptorQueRompeElPrimerCommit();

        HttpResponseMessage respuesta;
        using (fixture.ConInterceptorEnElHost(interceptor))
        {
            respuesta = await CargarAsync(ctx.Admin, id, "2026-10-01", 1m);
        }

        Assert.Equal("resultado_incierto", await CodigoAsync(respuesta, HttpStatusCode.ServiceUnavailable));
        Assert.Equal(1, interceptor.Commits);
        var cuenta = await CuentaAsync(ctx.Admin, id, "?historico=true");
        Assert.Equal(0, cuenta.GetProperty("total").GetInt32());
        Assert.Equal(0m, cuenta.GetProperty("encabezado").GetProperty("saldo").GetDecimal());
    }

    [Fact]
    public async Task ElUpdateDelSaldoNoTocaAUnPersonalDadoDeBajaNiAUnoDeOtroTenant()
    {
        var ctx = await PrepararAsync(nameof(ElUpdateDelSaldoNoTocaAUnPersonalDadoDeBajaNiAUnoDeOtroTenant));
        var id = await CrearPersonalAsync(ctx.Admin);

        // Conexión del dueño (sin RLS): cada conjunción del WHERE decide por sí sola.
        await using var conexion = new NpgsqlConnection(fixture.OwnerConnectionString);
        await conexion.OpenAsync();

        Assert.Null(await Application.Plantel.EscriturasDePersonal.ActualizarSaldoAsync(
            conexion, null, ctx.IdTenant + 1000, id, 10m, default));

        await using (var baja = new NpgsqlCommand("UPDATE personal SET deleted_at = now() WHERE id_personal = $1", conexion))
        {
            baja.Parameters.Add(new NpgsqlParameter { Value = id });
            Assert.Equal(1, await baja.ExecuteNonQueryAsync());
        }

        Assert.Null(await Application.Plantel.EscriturasDePersonal.ActualizarSaldoAsync(
            conexion, null, ctx.IdTenant, id, 10m, default));
    }
}
