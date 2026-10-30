using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Ways.Application.Organizacion;
using Ways.Application.Plantel;
using Ways.Application.Usuarios;
using Ways.Domain.Organizacion;
using Ways.Domain.Plantel;
using Ways.Domain.Usuarios;

namespace Ways.IntegrationTests;

/// <summary>
/// Carga semanal de horas punta a punta contra Postgres real: la grilla con su precarga, el guardado que
/// escribe solo diferencias en el libro, la reversión LIFO al valor hora de cada capa, el tope de 24 horas por
/// día en la carga semanal y en la individual, la confirmación en cero, las validaciones previas, las
/// políticas, el commit ambiguo sin reintento y la concurrencia sobre el saldo y las jornadas.
/// Las semanas se cargan en el pasado (hace tres semanas) para que ningún día sea futuro.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CargaSemanalDeHorasTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";

    private static readonly decimal[] HorarioDeOficina = [8m, 8m, 8m, 8m, 8m, 0m, 0m];

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

    private sealed record Asiento(int Id, DateOnly Fecha, decimal Horas, decimal ValorHora, decimal Importe, string? Detalle);

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

    private static async Task<int> CrearPersonalAsync(
        HttpClient admin, decimal valorHora = 1000m, string nombre = "Ana", decimal[]? horario = null)
    {
        var respuesta = await admin.PostAsJsonAsync(
            "/api/personal", new DatosDePersonal(nombre, null, null, null, valorHora, null, null, null, horario));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static async Task CambiarValorHoraAsync(HttpClient admin, int id, decimal valorHora)
    {
        var edicion = await admin.PutAsJsonAsync(
            $"/api/personal/{id}", new DatosDePersonal("Ana", null, null, null, valorHora, null, null, null));
        Assert.Equal(HttpStatusCode.OK, edicion.StatusCode);
    }

    private static DateOnly HoyLocal() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
            DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires")).DateTime);

    /// <summary>El lunes de hace tres semanas: toda la semana es pasada.</summary>
    private static DateOnly LunesPasado() => ReglaDeSemanaDeCarga.LunesDe(HoyLocal()).AddDays(-21);

    private static string Texto(DateOnly fecha) => fecha.ToString("yyyy-MM-dd");

    private static Task<HttpResponseMessage> GuardarAsync(HttpClient cliente, params (int IdPersonal, DateOnly Fecha, decimal? Horas)[] dias) =>
        cliente.PostAsJsonAsync(
            "/api/personal/horas/semana",
            new { dias = dias.Select(d => new { idPersonal = d.IdPersonal, fecha = Texto(d.Fecha), horas = d.Horas }).ToArray() });

    private static Task<HttpResponseMessage> CargarIndividualAsync(HttpClient cliente, int id, DateOnly fecha, decimal horas) =>
        cliente.PostAsJsonAsync($"/api/personal/{id}/horas", new { fecha = Texto(fecha), horas });

    private static async Task<JsonElement> CuerpoAsync(HttpResponseMessage respuesta, HttpStatusCode esperado)
    {
        var texto = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == esperado, $"{respuesta.StatusCode}: {texto}");
        return JsonDocument.Parse(texto).RootElement.Clone();
    }

    private static async Task<string?> CodigoAsync(HttpResponseMessage respuesta, HttpStatusCode esperado) =>
        (await CuerpoAsync(respuesta, esperado)).GetProperty("codigo").GetString();

    private static async Task<JsonElement> SemanaAsync(HttpClient cliente, DateOnly? desde = null)
    {
        var consulta = desde is { } d ? $"?desde={Texto(d)}" : string.Empty;
        return await CuerpoAsync(await cliente.GetAsync($"/api/personal/horas/semana{consulta}"), HttpStatusCode.OK);
    }

    private static JsonElement Dia(JsonElement semana, int idPersonal, int indice) =>
        semana.GetProperty("filas").EnumerateArray()
            .Single(f => f.GetProperty("idPersonal").GetInt32() == idPersonal)
            .GetProperty("dias")[indice];

    private static async Task<decimal> SaldoAsync(HttpClient cliente, int id)
    {
        var respuesta = await cliente.GetAsync($"/api/personal/{id}/cuenta?historico=true");
        return (await CuerpoAsync(respuesta, HttpStatusCode.OK)).GetProperty("encabezado").GetProperty("saldo").GetDecimal();
    }

    private async Task<List<Asiento>> LibroAsync(int idTenant, int idPersonal)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT id_movimiento, fecha, horas, valor_hora, importe, detalle FROM movimientos_personal " +
            "WHERE id_personal = $1 AND tipo = 'horas' ORDER BY id_movimiento";
        comando.Parameters.Add(new NpgsqlParameter { Value = idPersonal });

        var asientos = new List<Asiento>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            asientos.Add(new Asiento(
                lector.GetInt32(0), lector.GetFieldValue<DateOnly>(1), lector.GetDecimal(2), lector.GetDecimal(3),
                lector.GetDecimal(4), lector.IsDBNull(5) ? null : lector.GetString(5)));
        }

        return asientos;
    }

    private async Task<decimal?> JornadaAsync(int idTenant, int idPersonal, DateOnly fecha)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT horas FROM jornadas_personal WHERE id_personal = $1 AND fecha = $2";
        comando.Parameters.Add(new NpgsqlParameter { Value = idPersonal });
        comando.Parameters.Add(new NpgsqlParameter { Value = fecha });

        var valor = await comando.ExecuteScalarAsync();
        return valor is null or DBNull ? null : Convert.ToDecimal(valor);
    }

    private async Task<long> CantidadDeJornadasAsync(int idTenant, int idPersonal)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM jornadas_personal WHERE id_personal = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idPersonal });
        return (long)(await comando.ExecuteScalarAsync())!;
    }

    /// <summary>Invariante del módulo: por cada día la jornada vale lo mismo que la suma de horas del libro.</summary>
    private async Task ExigirJornadasIgualesAlLibroAsync(int idTenant, int idPersonal)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT count(*) FROM jornadas_personal j WHERE j.id_personal = $1 AND j.horas <> " +
            "(SELECT coalesce(sum(m.horas), 0) FROM movimientos_personal m " +
            " WHERE m.id_personal = j.id_personal AND m.fecha = j.fecha AND m.tipo = 'horas')";
        comando.Parameters.Add(new NpgsqlParameter { Value = idPersonal });
        Assert.Equal(0L, await comando.ExecuteScalarAsync());
    }

    [Fact]
    public async Task LaGrillaPrecargaElHorarioMuestraLoConfirmadoYMarcaLosDiasFuturos()
    {
        var ctx = await PrepararAsync(nameof(LaGrillaPrecargaElHorarioMuestraLoConfirmadoYMarcaLosDiasFuturos));
        var id = await CrearPersonalAsync(ctx.Admin, horario: HorarioDeOficina);
        var sinHorario = await CrearPersonalAsync(ctx.Admin, nombre: "Zoe");
        var lunes = LunesPasado();
        await CuerpoAsync(await CargarIndividualAsync(ctx.Admin, id, lunes.AddDays(1), 3m), HttpStatusCode.Created);

        var semana = await SemanaAsync(ctx.Admin, lunes);

        Assert.Equal(Texto(lunes), semana.GetProperty("desde").GetString());
        Assert.Equal(Texto(lunes.AddDays(6)), semana.GetProperty("hasta").GetString());
        Assert.Equal(Texto(HoyLocal()), semana.GetProperty("hoy").GetString());
        Assert.Equal(["Ana", "Zoe"], semana.GetProperty("filas").EnumerateArray().Select(f => f.GetProperty("nombre").GetString()));

        var precargado = Dia(semana, id, 0);
        Assert.Equal(8m, precargado.GetProperty("horas").GetDecimal());
        Assert.True(precargado.GetProperty("precargado").GetBoolean());
        Assert.False(precargado.GetProperty("confirmado").GetBoolean());
        Assert.Equal(0m, precargado.GetProperty("horasCargadas").GetDecimal());
        Assert.False(precargado.GetProperty("futuro").GetBoolean());

        var franco = Dia(semana, id, 5);
        Assert.Equal(0m, franco.GetProperty("horas").GetDecimal());
        Assert.True(franco.GetProperty("precargado").GetBoolean());

        var cargado = Dia(semana, id, 1);
        Assert.Equal(3m, cargado.GetProperty("horas").GetDecimal());
        Assert.Equal(3m, cargado.GetProperty("horasCargadas").GetDecimal());
        Assert.True(cargado.GetProperty("confirmado").GetBoolean());
        Assert.False(cargado.GetProperty("precargado").GetBoolean());

        var vacio = Dia(semana, sinHorario, 0);
        Assert.Equal(JsonValueKind.Null, vacio.GetProperty("horas").ValueKind);
        Assert.False(vacio.GetProperty("precargado").GetBoolean());

        var actual = await SemanaAsync(ctx.Admin);
        var hoy = HoyLocal();
        Assert.Equal(Texto(ReglaDeSemanaDeCarga.LunesDe(hoy)), actual.GetProperty("desde").GetString());
        foreach (var dia in actual.GetProperty("filas")[0].GetProperty("dias").EnumerateArray())
        {
            var futuro = DateOnly.Parse(dia.GetProperty("fecha").GetString()!) > hoy;
            Assert.Equal(futuro, dia.GetProperty("futuro").GetBoolean());
            if (futuro)
            {
                Assert.Equal(JsonValueKind.Null, dia.GetProperty("horas").ValueKind);
                Assert.False(dia.GetProperty("precargado").GetBoolean());
            }
        }
    }

    [Fact]
    public async Task UnDesdeQueNoEsLunesResponde400SemanaInvalida()
    {
        var ctx = await PrepararAsync(nameof(UnDesdeQueNoEsLunesResponde400SemanaInvalida));

        var respuesta = await ctx.Admin.GetAsync($"/api/personal/horas/semana?desde={Texto(LunesPasado().AddDays(1))}");

        Assert.Equal("semana_invalida", await CodigoAsync(respuesta, HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task GuardarEscribeSoloLasDiferenciasConfirmaCadaDiaYElSaldoCoincide()
    {
        var ctx = await PrepararAsync(nameof(GuardarEscribeSoloLasDiferenciasConfirmaCadaDiaYElSaldoCoincide));
        var id = await CrearPersonalAsync(ctx.Admin, valorHora: 1000m, horario: HorarioDeOficina);
        var lunes = LunesPasado();

        var primero = await CuerpoAsync(
            await GuardarAsync(ctx.Admin, (id, lunes, 8m), (id, lunes.AddDays(1), 8m), (id, lunes.AddDays(2), 0m)),
            HttpStatusCode.OK);

        Assert.Equal(2, primero.GetProperty("movimientosCreados").GetInt32());
        Assert.Equal(3, primero.GetProperty("jornadasConfirmadas").GetInt32());
        Assert.Equal(16000m, await SaldoAsync(ctx.Admin, id));

        var repetido = await CuerpoAsync(
            await GuardarAsync(ctx.Admin, (id, lunes, 8m), (id, lunes.AddDays(1), 8m), (id, lunes.AddDays(2), 0m)),
            HttpStatusCode.OK);
        Assert.Equal(0, repetido.GetProperty("movimientosCreados").GetInt32());
        Assert.Equal(3, repetido.GetProperty("jornadasConfirmadas").GetInt32());

        var libro = await LibroAsync(ctx.IdTenant, id);
        Assert.Equal(2, libro.Count);
        Assert.All(libro, a => Assert.Equal("Carga semanal", a.Detalle));
        Assert.Equal(16000m, await SaldoAsync(ctx.Admin, id));
        Assert.Equal(0m, await JornadaAsync(ctx.IdTenant, id, lunes.AddDays(2)));
        await ExigirJornadasIgualesAlLibroAsync(ctx.IdTenant, id);
    }

    [Fact]
    public async Task UnDiaConfirmadoEnCeroNoSeVuelveAPrecargar()
    {
        var ctx = await PrepararAsync(nameof(UnDiaConfirmadoEnCeroNoSeVuelveAPrecargar));
        var id = await CrearPersonalAsync(ctx.Admin, horario: HorarioDeOficina);
        var lunes = LunesPasado();

        await CuerpoAsync(await GuardarAsync(ctx.Admin, (id, lunes, 0m)), HttpStatusCode.OK);

        var dia = Dia(await SemanaAsync(ctx.Admin, lunes), id, 0);
        Assert.True(dia.GetProperty("confirmado").GetBoolean());
        Assert.Equal(0m, dia.GetProperty("horas").GetDecimal());
        Assert.False(dia.GetProperty("precargado").GetBoolean());
        Assert.Empty(await LibroAsync(ctx.IdTenant, id));
    }

    [Fact]
    public async Task BajarLasHorasRevierteElImporteExactoConElValorHoraDeCadaCapaEnOrdenLifo()
    {
        var ctx = await PrepararAsync(nameof(BajarLasHorasRevierteElImporteExactoConElValorHoraDeCadaCapaEnOrdenLifo));
        var id = await CrearPersonalAsync(ctx.Admin, valorHora: 1000m);
        var dia = LunesPasado();

        await CuerpoAsync(await GuardarAsync(ctx.Admin, (id, dia, 8m)), HttpStatusCode.OK);
        await CambiarValorHoraAsync(ctx.Admin, id, 2000m);
        await CuerpoAsync(await GuardarAsync(ctx.Admin, (id, dia, 10m)), HttpStatusCode.OK);
        Assert.Equal(12000m, await SaldoAsync(ctx.Admin, id));

        await CuerpoAsync(await GuardarAsync(ctx.Admin, (id, dia, 9m)), HttpStatusCode.OK);
        Assert.Equal(10000m, await SaldoAsync(ctx.Admin, id));

        var anulacion = await CuerpoAsync(await GuardarAsync(ctx.Admin, (id, dia, 0m)), HttpStatusCode.OK);
        Assert.Equal(2, anulacion.GetProperty("movimientosCreados").GetInt32());
        Assert.Equal(0m, await SaldoAsync(ctx.Admin, id));

        var libro = await LibroAsync(ctx.IdTenant, id);
        Assert.Equal(
            [(8m, 1000m, 8000m), (2m, 2000m, 4000m), (-1m, 2000m, -2000m), (-1m, 2000m, -2000m), (-8m, 1000m, -8000m)],
            libro.Select(a => (a.Horas, a.ValorHora, a.Importe)));
        Assert.Equal(0m, await JornadaAsync(ctx.IdTenant, id, dia));
        await ExigirJornadasIgualesAlLibroAsync(ctx.IdTenant, id);
    }

    [Fact]
    public async Task EditarVariasVecesElMismoDiaMantieneSaldoYJornadaIgualesAlLibro()
    {
        var ctx = await PrepararAsync(nameof(EditarVariasVecesElMismoDiaMantieneSaldoYJornadaIgualesAlLibro));
        var id = await CrearPersonalAsync(ctx.Admin, valorHora: 1500m);
        var dia = LunesPasado();

        foreach (var total in new[] { 8m, 3m, 12.5m, 0.25m, 0m, 24m, 7m })
        {
            await CuerpoAsync(await GuardarAsync(ctx.Admin, (id, dia, total)), HttpStatusCode.OK);

            var libro = await LibroAsync(ctx.IdTenant, id);
            Assert.Equal(total, libro.Sum(a => a.Horas));
            Assert.Equal(libro.Sum(a => a.Importe), await SaldoAsync(ctx.Admin, id));
            Assert.Equal(total, await JornadaAsync(ctx.IdTenant, id, dia));
        }
    }

    [Fact]
    public async Task UnDiaHeredadoPorEncimaDeVeinticuatroSeReduceAlTopeAlGuardar()
    {
        var ctx = await PrepararAsync(nameof(UnDiaHeredadoPorEncimaDeVeinticuatroSeReduceAlTopeAlGuardar));
        var id = await CrearPersonalAsync(ctx.Admin, valorHora: 1000m);
        var dia = LunesPasado();

        await using (var conexion = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant))
        {
            await using var usuario = conexion.CreateCommand();
            usuario.CommandText = "SELECT min(id_usuario) FROM usuarios WHERE id_tenant = $1";
            usuario.Parameters.Add(new NpgsqlParameter { Value = ctx.IdTenant });
            var idUsuario = Convert.ToInt32(await usuario.ExecuteScalarAsync());

            foreach (var (horas, valorHora) in new[] { (20m, 1000m), (10m, 1200m) })
            {
                await using var insertar = conexion.CreateCommand();
                insertar.CommandText =
                    "INSERT INTO movimientos_personal " +
                    "(id_tenant, id_personal, id_empleado, fecha, tipo, horas, valor_hora, importe, saldo_resultante, created_at) " +
                    "VALUES ($1, $2, $3, $4, 'horas', $5, $6, round($5 * $6, 2), 0, now())";
                insertar.Parameters.Add(new NpgsqlParameter { Value = ctx.IdTenant });
                insertar.Parameters.Add(new NpgsqlParameter { Value = id });
                insertar.Parameters.Add(new NpgsqlParameter { Value = idUsuario });
                insertar.Parameters.Add(new NpgsqlParameter { Value = dia });
                insertar.Parameters.Add(new NpgsqlParameter { Value = horas });
                insertar.Parameters.Add(new NpgsqlParameter { Value = valorHora });
                Assert.Equal(1, await insertar.ExecuteNonQueryAsync());
            }
        }

        var semana = await SemanaAsync(ctx.Admin, ReglaDeSemanaDeCarga.LunesDe(dia));
        var heredado = Dia(semana, id, ReglaDeSemanaDeCarga.IndiceIso(dia));
        Assert.Equal(30m, heredado.GetProperty("horasCargadas").GetDecimal());
        Assert.True(heredado.GetProperty("confirmado").GetBoolean());

        await CuerpoAsync(await GuardarAsync(ctx.Admin, (id, dia, 24m)), HttpStatusCode.OK);

        var libro = await LibroAsync(ctx.IdTenant, id);
        var reduccion = libro[^1];
        Assert.Equal((-6m, 1200m, -7200m), (reduccion.Horas, reduccion.ValorHora, reduccion.Importe));
        Assert.Equal(24m, libro.Sum(a => a.Horas));
        Assert.Equal(24m, await JornadaAsync(ctx.IdTenant, id, dia));
    }

    [Fact]
    public async Task LaCargaIndividualRechazaSuperarLas24HorasDelDiaYConfirmaLaJornada()
    {
        var ctx = await PrepararAsync(nameof(LaCargaIndividualRechazaSuperarLas24HorasDelDiaYConfirmaLaJornada));
        var id = await CrearPersonalAsync(ctx.Admin, valorHora: 100m);
        var dia = LunesPasado();

        await CuerpoAsync(await CargarIndividualAsync(ctx.Admin, id, dia, 20m), HttpStatusCode.Created);
        Assert.Equal(20m, await JornadaAsync(ctx.IdTenant, id, dia));

        var excedida = await CargarIndividualAsync(ctx.Admin, id, dia, 5m);
        Assert.Equal("horas_del_dia_excedidas", await CodigoAsync(excedida, HttpStatusCode.BadRequest));
        Assert.Equal(2000m, await SaldoAsync(ctx.Admin, id));
        Assert.Single(await LibroAsync(ctx.IdTenant, id));

        await CuerpoAsync(await CargarIndividualAsync(ctx.Admin, id, dia, 4m), HttpStatusCode.Created);
        Assert.Equal(24m, await JornadaAsync(ctx.IdTenant, id, dia));
        await ExigirJornadasIgualesAlLibroAsync(ctx.IdTenant, id);

        var otroDia = await CargarIndividualAsync(ctx.Admin, id, dia.AddDays(1), 24m);
        await CuerpoAsync(otroDia, HttpStatusCode.Created);
    }

    [Fact]
    public async Task LaCargaIndividualSumaLasBajasDeLaCargaSemanalAlTopeDelDia()
    {
        var ctx = await PrepararAsync(nameof(LaCargaIndividualSumaLasBajasDeLaCargaSemanalAlTopeDelDia));
        var id = await CrearPersonalAsync(ctx.Admin, valorHora: 100m);
        var dia = LunesPasado();

        await CuerpoAsync(await GuardarAsync(ctx.Admin, (id, dia, 20m)), HttpStatusCode.OK);
        await CuerpoAsync(await GuardarAsync(ctx.Admin, (id, dia, 10m)), HttpStatusCode.OK);

        await CuerpoAsync(await CargarIndividualAsync(ctx.Admin, id, dia, 14m), HttpStatusCode.Created);
        Assert.Equal("horas_del_dia_excedidas", await CodigoAsync(
            await CargarIndividualAsync(ctx.Admin, id, dia, 0.5m), HttpStatusCode.BadRequest));
        Assert.Equal(24m, await JornadaAsync(ctx.IdTenant, id, dia));
        await ExigirJornadasIgualesAlLibroAsync(ctx.IdTenant, id);
    }

    [Fact]
    public async Task UnDiaFuturoUnaFilaRepetidaHorasInvalidasOUnaSolicitudVaciaSeRechazanSinEscribirNada()
    {
        var ctx = await PrepararAsync(nameof(UnDiaFuturoUnaFilaRepetidaHorasInvalidasOUnaSolicitudVaciaSeRechazanSinEscribirNada));
        var id = await CrearPersonalAsync(ctx.Admin, valorHora: 1000m);
        var dia = LunesPasado();
        var futuro = HoyLocal().AddDays(2);

        Assert.Equal("fecha_futura", await CodigoAsync(
            await GuardarAsync(ctx.Admin, (id, dia, 8m), (id, futuro, 8m)), HttpStatusCode.BadRequest));
        Assert.Equal("dia_duplicado", await CodigoAsync(
            await GuardarAsync(ctx.Admin, (id, dia, 8m), (id, dia, 4m)), HttpStatusCode.BadRequest));
        Assert.Equal("horas_invalidas", await CodigoAsync(
            await GuardarAsync(ctx.Admin, (id, dia, 8m), (id, dia.AddDays(1), 24.01m)), HttpStatusCode.BadRequest));
        Assert.Equal("horas_invalidas", await CodigoAsync(
            await GuardarAsync(ctx.Admin, (id, dia, 8.123m)), HttpStatusCode.BadRequest));
        Assert.Equal("horas_invalidas", await CodigoAsync(
            await GuardarAsync(ctx.Admin, (id, dia, null)), HttpStatusCode.BadRequest));
        Assert.Equal("solicitud_invalida", await CodigoAsync(
            await ctx.Admin.PostAsJsonAsync("/api/personal/horas/semana", new { dias = Array.Empty<object>() }),
            HttpStatusCode.BadRequest));
        Assert.Equal("solicitud_invalida", await CodigoAsync(
            await ctx.Admin.PostAsJsonAsync("/api/personal/horas/semana", new { }), HttpStatusCode.BadRequest));

        Assert.Empty(await LibroAsync(ctx.IdTenant, id));
        Assert.Equal(0L, await CantidadDeJornadasAsync(ctx.IdTenant, id));
        Assert.Equal(0m, await SaldoAsync(ctx.Admin, id));
    }

    [Fact]
    public async Task MasDeMilDiasSeRechazanConSolicitudInvalida()
    {
        var ctx = await PrepararAsync(nameof(MasDeMilDiasSeRechazanConSolicitudInvalida));
        var id = await CrearPersonalAsync(ctx.Admin);
        var inicio = LunesPasado().AddDays(-2000);

        var dias = Enumerable.Range(0, 1001).Select(i => (id, inicio.AddDays(i), (decimal?)1m)).ToArray();

        Assert.Equal("solicitud_invalida", await CodigoAsync(await GuardarAsync(ctx.Admin, dias), HttpStatusCode.BadRequest));
        Assert.Empty(await LibroAsync(ctx.IdTenant, id));
    }

    [Fact]
    public async Task UnPersonalInexistenteDeBajaOAjenoResponde404AunConUnCuerpoInvalido()
    {
        var ctx = await PrepararAsync(nameof(UnPersonalInexistenteDeBajaOAjenoResponde404AunConUnCuerpoInvalido));
        var otro = await PrepararAsync(nameof(UnPersonalInexistenteDeBajaOAjenoResponde404AunConUnCuerpoInvalido) + "B");
        var propio = await CrearPersonalAsync(ctx.Admin);
        var ajeno = await CrearPersonalAsync(otro.Admin);
        var dadoDeBaja = await CrearPersonalAsync(ctx.Admin, nombre: "Baja");
        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"/api/personal/{dadoDeBaja}")).StatusCode);
        var dia = LunesPasado();

        foreach (var id in new[] { 987654, ajeno, dadoDeBaja })
        {
            var respuesta = await GuardarAsync(ctx.Admin, (propio, dia, 8m), (id, HoyLocal().AddDays(5), 99m));

            Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        }

        Assert.Empty(await LibroAsync(ctx.IdTenant, propio));
        Assert.Empty(await LibroAsync(otro.IdTenant, ajeno));
    }

    [Fact]
    public async Task GuardarVariosPersonalesEscribeCadaUnoYConfirmaSusJornadas()
    {
        var ctx = await PrepararAsync(nameof(GuardarVariosPersonalesEscribeCadaUnoYConfirmaSusJornadas));
        var ana = await CrearPersonalAsync(ctx.Admin, nombre: "Ana");
        var beto = await CrearPersonalAsync(ctx.Admin, nombre: "Beto");
        var dia = LunesPasado();

        await CuerpoAsync(await GuardarAsync(ctx.Admin, (ana, dia, 8m), (beto, dia, 8m)), HttpStatusCode.OK);

        Assert.Equal(8000m, await SaldoAsync(ctx.Admin, ana));
        Assert.Equal(8000m, await SaldoAsync(ctx.Admin, beto));
        Assert.Equal(1L, await CantidadDeJornadasAsync(ctx.IdTenant, ana));
        Assert.Equal(1L, await CantidadDeJornadasAsync(ctx.IdTenant, beto));
    }

    [Fact]
    public async Task UnPersonalConUnaJornadaConfirmadaEnCeroNoSePuedeDarDeBaja()
    {
        var ctx = await PrepararAsync(nameof(UnPersonalConUnaJornadaConfirmadaEnCeroNoSePuedeDarDeBaja));
        var id = await CrearPersonalAsync(ctx.Admin);
        await CuerpoAsync(await GuardarAsync(ctx.Admin, (id, LunesPasado(), 0m)), HttpStatusCode.OK);
        Assert.Empty(await LibroAsync(ctx.IdTenant, id));

        var baja = await ctx.Admin.DeleteAsync($"/api/personal/{id}");

        Assert.Equal("personal_en_uso", await CodigoAsync(baja, HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task LectoraYGestionRespetanLasPoliticasDeCadaRol()
    {
        var ctx = await PrepararAsync(nameof(LectoraYGestionRespetanLasPoliticasDeCadaRol));
        var id = await CrearPersonalAsync(ctx.Admin);
        var dia = LunesPasado();
        using var anonimo = fixture.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await ctx.Supervisor.GetAsync("/api/personal/horas/semana")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GuardarAsync(ctx.Supervisor, (id, dia, 2m))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ctx.Vendedor.GetAsync("/api/personal/horas/semana")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GuardarAsync(ctx.Vendedor, (id, dia, 2m))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/personal/horas/semana")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GuardarAsync(anonimo, (id, dia, 2m))).StatusCode);
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
            respuesta = await GuardarAsync(ctx.Admin, (id, LunesPasado(), 8m));
        }

        Assert.Equal("resultado_incierto", await CodigoAsync(respuesta, HttpStatusCode.ServiceUnavailable));
        Assert.Equal(1, interceptor.Commits);
        Assert.Empty(await LibroAsync(ctx.IdTenant, id));
        Assert.Equal(0L, await CantidadDeJornadasAsync(ctx.IdTenant, id));
        Assert.Equal(0m, await SaldoAsync(ctx.Admin, id));
    }

    [Fact]
    public async Task GuardadosConcurrentesDelMismoDiaDejanSaldoYJornadaIgualesAlLibro()
    {
        var ctx = await PrepararAsync(nameof(GuardadosConcurrentesDelMismoDiaDejanSaldoYJornadaIgualesAlLibro));
        var id = await CrearPersonalAsync(ctx.Admin, valorHora: 1300m);
        var dia = LunesPasado();
        var totales = new[] { 8m, 5m, 12m, 0m, 24m, 3.5m, 9m, 1m };

        var respuestas = await Task.WhenAll(totales.Select(total => GuardarAsync(ctx.Admin, (id, dia, total))));

        foreach (var respuesta in respuestas)
        {
            Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        }

        var libro = await LibroAsync(ctx.IdTenant, id);
        Assert.Equal(libro.Sum(a => a.Importe), await SaldoAsync(ctx.Admin, id));
        Assert.Contains(libro.Sum(a => a.Horas), totales);
        Assert.Equal(libro.Sum(a => a.Horas), await JornadaAsync(ctx.IdTenant, id, dia));
        Assert.All(libro, a => Assert.Equal(Math.Round(a.Horas * a.ValorHora, 2, MidpointRounding.AwayFromZero), a.Importe));
    }

    [Fact]
    public async Task GuardadosConcurrentesEnOrdenOpuestoDePersonalNoSeAbrazan()
    {
        var ctx = await PrepararAsync(nameof(GuardadosConcurrentesEnOrdenOpuestoDePersonalNoSeAbrazan));
        var ana = await CrearPersonalAsync(ctx.Admin, nombre: "Ana");
        var beto = await CrearPersonalAsync(ctx.Admin, nombre: "Beto");
        var dia = LunesPasado();

        var respuestas = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => i % 2 == 0
            ? GuardarAsync(ctx.Admin, (ana, dia.AddDays(i % 5), i + 1), (beto, dia.AddDays(i % 5), i + 1))
            : GuardarAsync(ctx.Admin, (beto, dia.AddDays(i % 5), i + 1), (ana, dia.AddDays(i % 5), i + 1))));

        foreach (var respuesta in respuestas)
        {
            Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        }

        foreach (var id in new[] { ana, beto })
        {
            var libro = await LibroAsync(ctx.IdTenant, id);
            Assert.Equal(libro.Sum(a => a.Importe), await SaldoAsync(ctx.Admin, id));
            await ExigirJornadasIgualesAlLibroAsync(ctx.IdTenant, id);
        }
    }

    [Fact]
    public async Task CargasIndividualesConcurrentesDelMismoDiaNuncaSuperanLas24Horas()
    {
        var ctx = await PrepararAsync(nameof(CargasIndividualesConcurrentesDelMismoDiaNuncaSuperanLas24Horas));
        var id = await CrearPersonalAsync(ctx.Admin, valorHora: 100m);
        var dia = LunesPasado();

        var respuestas = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => CargarIndividualAsync(ctx.Admin, id, dia, 5m)));

        Assert.Equal(4, respuestas.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(4, respuestas.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
        Assert.Equal(20m, (await LibroAsync(ctx.IdTenant, id)).Sum(a => a.Horas));
        Assert.Equal(20m, await JornadaAsync(ctx.IdTenant, id, dia));
        Assert.Equal(2000m, await SaldoAsync(ctx.Admin, id));
    }
}
