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
/// Corrección administrativa de retiros: <c>PUT</c> y <c>DELETE /api/caja/retiros/{id}</c> (Admin). Cubre la edición y la baja con
/// el turno abierto (el resumen parcial la refleja) y cerrado (recálculo del arqueo con su marca, ajuste en la cadena de
/// tesorería, libro), el retiro del cierre rechazado, la conciliación, los 404 antes que el cuerpo, la validación del alta, los
/// roles y la auditoría.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class RetirosDeAdministracionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordUsuario = "una-contraseña-larga-de-prueba";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    internal sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdAdmin, HttpClient Admin, int IdEfectivo, string Unico);

    // ---- edición ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EditarUnRetiroDeUnTurnoAbiertoCambiaImporteYMotivoYElResumenParcialLoRefleja()
    {
        var ctx = await PrepararAsync(fixture, "retiro-edita-abierto");
        var turno = await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta, 1000m);
        var retiro = await RegistrarAsync(ctx.Admin, turno, TipoMovimientoCaja.Retiro, 200m, "Retiro mal cargado");

        var respuesta = await ctx.Admin.PutAsJsonAsync(
            $"/api/caja/retiros/{retiro}", new SolicitudDeEdicionDeRetiro(150m, "  Retiro corregido  "));

        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        var fila = JsonSerializer.Deserialize<FilaDeRetiro>(cuerpo, OpcionesJson)!;
        Assert.Equal((retiro, 150m, "Retiro corregido", false), (fila.Id, fila.Importe, fila.Motivo, fila.TurnoCerrado));

        Assert.Equal(150m, await RetirosDelResumenAsync(ctx.Admin, turno));

        await using var db = Db(ctx.IdTenant);
        Assert.Equal(0, await db.MovimientosTesoreria.CountAsync(m => m.IdTurnoCaja == turno));
        Assert.Null(await db.TurnosCaja.Where(t => t.Id == turno).Select(t => t.FechaRecalculo).FirstAsync());

        var auditoria = await db.Auditoria.SingleAsync(a => a.Accion == "retiro.edicion" && a.IdEntidad == retiro);
        Assert.Equal("movimiento_caja", auditoria.Entidad);
        Assert.Equal(ctx.IdPuntoVenta, auditoria.IdPuntoVenta);
        Assert.Equal(ctx.IdAdmin, auditoria.IdActor);
        using var anterior = JsonDocument.Parse(auditoria.ValorAnterior!);
        Assert.Equal(200m, anterior.RootElement.GetProperty("importe").GetDecimal());
        Assert.Equal("Retiro mal cargado", anterior.RootElement.GetProperty("motivo").GetString());
        using var nuevo = JsonDocument.Parse(auditoria.ValorNuevo!);
        Assert.Equal(150m, nuevo.RootElement.GetProperty("importe").GetDecimal());
        Assert.Equal("Retiro corregido", nuevo.RootElement.GetProperty("motivo").GetString());
        Assert.Equal(JsonValueKind.Null, nuevo.RootElement.GetProperty("id_turno_recalculado").ValueKind);
    }

    /// <summary>Fondo 1000, un retiro manual de 200 y el retiro del cierre de 300: el esperado del efectivo es 500 y el cierre
    /// acreditó 500 a la tesorería. Bajar el retiro a 120 deja el esperado en 580 (el original 500 queda guardado, con la marca del
    /// recálculo), agrega un ajuste de 80 que sale de la tesorería y el libro muestra 420 en la fila de ese cierre.</summary>
    [Fact]
    public async Task EditarUnRetiroDeUnTurnoCerradoRecalculaElArqueoConLaMarcaAgregaElAjusteYElLibroLoRefleja()
    {
        var ctx = await PrepararAsync(fixture, "retiro-edita-cerrado");
        var turno = await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta, 1000m);
        var retiro = await RegistrarAsync(ctx.Admin, turno, TipoMovimientoCaja.Retiro, 200m, "Retiro para el banco");
        await CerrarPorRetiroAsync(ctx.Admin, turno, 300m);

        await using (var antes = Db(ctx.IdTenant))
        {
            Assert.Equal(500m, await EsperadoDelEfectivoAsync(antes, turno, ctx.IdEfectivo));
            Assert.Equal(500m, await UltimoFinalDeTesoreriaAsync(antes, ctx.IdEmpresa));
        }

        var respuesta = await ctx.Admin.PutAsJsonAsync(
            $"/api/caja/retiros/{retiro}", new SolicitudDeEdicionDeRetiro(120m, "Retiro para el banco, corregido"));
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync());

        await using var db = Db(ctx.IdTenant);
        var arqueo = await db.ArqueosTurno.SingleAsync(a => a.IdTurnoCaja == turno && a.IdMedioPago == ctx.IdEfectivo);
        Assert.Equal((580m, 500m), (arqueo.ImporteEsperado, arqueo.ImporteEsperadoOriginal));
        var marca = await db.TurnosCaja.Where(t => t.Id == turno).Select(t => new { t.FechaRecalculo, t.IdEmpleadoRecalculo }).FirstAsync();
        Assert.NotNull(marca.FechaRecalculo);
        Assert.Equal(ctx.IdAdmin, marca.IdEmpleadoRecalculo);

        var ajuste = await db.MovimientosTesoreria
            .Where(m => m.IdEmpresa == ctx.IdEmpresa).OrderByDescending(m => m.Id).FirstAsync();
        Assert.Equal(
            (TipoMovimientoTesoreria.Ajuste, turno, 0m, 80m, 500m, 420m, $"Edición del retiro #{retiro}"),
            (ajuste.Tipo, ajuste.IdTurnoCaja, ajuste.Ingreso, ajuste.Egreso, ajuste.Inicio, ajuste.Final, ajuste.Concepto));

        Assert.Equal(420m, await EfectivoDelLibroAsync(ctx, turno));

        var auditoria = await db.Auditoria.SingleAsync(a => a.Accion == "retiro.edicion" && a.IdEntidad == retiro);
        using var nuevo = JsonDocument.Parse(auditoria.ValorNuevo!);
        Assert.Equal(turno, nuevo.RootElement.GetProperty("id_turno_recalculado").GetInt32());
    }

    [Fact]
    public async Task EditarSoloElMotivoDeUnTurnoCerradoNoTocaElArqueoNiLaTesoreria()
    {
        var ctx = await PrepararAsync(fixture, "retiro-motivo-cerrado");
        var turno = await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta, 1000m);
        var retiro = await RegistrarAsync(ctx.Admin, turno, TipoMovimientoCaja.Retiro, 200m, "Retiro sin detalle");
        await CerrarPorRetiroAsync(ctx.Admin, turno, 0m);

        var respuesta = await ctx.Admin.PutAsJsonAsync(
            $"/api/caja/retiros/{retiro}", new SolicitudDeEdicionDeRetiro(200m, "Retiro para pagar el alquiler"));
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync());

        await using var db = Db(ctx.IdTenant);
        Assert.Null(await db.TurnosCaja.Where(t => t.Id == turno).Select(t => t.FechaRecalculo).FirstAsync());
        Assert.Equal(0, await db.MovimientosTesoreria.CountAsync(m => m.Tipo == TipoMovimientoTesoreria.Ajuste && m.IdTurnoCaja == turno));
        Assert.Equal("Retiro para pagar el alquiler", (await db.MovimientosCaja.SingleAsync(m => m.Id == retiro)).Motivo);
    }

    // ---- baja ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EliminarUnRetiroDeUnTurnoCerradoLoSacaDelArqueoDelLibroYDelResumenConUnAjuste()
    {
        var ctx = await PrepararAsync(fixture, "retiro-baja-cerrado");
        var turno = await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta, 1000m);
        var retiro = await RegistrarAsync(ctx.Admin, turno, TipoMovimientoCaja.Retiro, 200m, "Retiro duplicado");
        await CerrarPorRetiroAsync(ctx.Admin, turno, 300m);

        var respuesta = await ctx.Admin.DeleteAsync($"/api/caja/retiros/{retiro}");
        Assert.Equal(HttpStatusCode.NoContent, respuesta.StatusCode);

        await using var db = Db(ctx.IdTenant);
        var fila = await db.MovimientosCaja.IgnoreQueryFilters(["BajaLogica"]).SingleAsync(m => m.Id == retiro);
        Assert.NotNull(fila.DeletedAt);
        Assert.False(await db.MovimientosCaja.AnyAsync(m => m.Id == retiro));

        Assert.Equal(700m, await EsperadoDelEfectivoAsync(db, turno, ctx.IdEfectivo));
        var ajuste = await db.MovimientosTesoreria
            .Where(m => m.IdEmpresa == ctx.IdEmpresa).OrderByDescending(m => m.Id).FirstAsync();
        Assert.Equal(
            (TipoMovimientoTesoreria.Ajuste, 0m, 200m, 300m, $"Baja del retiro #{retiro}"),
            (ajuste.Tipo, ajuste.Ingreso, ajuste.Egreso, ajuste.Final, ajuste.Concepto));

        Assert.Equal(300m, await EfectivoDelLibroAsync(ctx, turno));
        Assert.Equal(300m, await RetirosDelResumenAsync(ctx.Admin, turno));

        var resumenDeCierre = await ctx.Admin.GetFromJsonAsync<ResumenDeCierrePorRetiro>(
            $"/api/caja/turnos/{turno}/resumen-de-cierre", OpcionesJson);
        Assert.Equal(300m, Assert.Single(resumenDeCierre!.Retiros).Importe);

        var auditoria = await db.Auditoria.SingleAsync(a => a.Accion == "retiro.baja" && a.IdEntidad == retiro);
        using var nuevo = JsonDocument.Parse(auditoria.ValorNuevo!);
        Assert.Equal(JsonValueKind.String, nuevo.RootElement.GetProperty("deleted_at").ValueKind);
        Assert.Equal(turno, nuevo.RootElement.GetProperty("id_turno_recalculado").GetInt32());
    }

    [Fact]
    public async Task EliminarUnRetiroDeUnTurnoAbiertoLoSacaDelResumenParcialSinTocarLaTesoreria()
    {
        var ctx = await PrepararAsync(fixture, "retiro-baja-abierto");
        var turno = await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta, 1000m);
        await RegistrarAsync(ctx.Admin, turno, TipoMovimientoCaja.Retiro, 50m, "Retiro que se queda");
        var retiro = await RegistrarAsync(ctx.Admin, turno, TipoMovimientoCaja.Retiro, 200m, "Retiro duplicado");

        var respuesta = await ctx.Admin.DeleteAsync($"/api/caja/retiros/{retiro}");
        Assert.Equal(HttpStatusCode.NoContent, respuesta.StatusCode);

        Assert.Equal(50m, await RetirosDelResumenAsync(ctx.Admin, turno));
        await using var db = Db(ctx.IdTenant);
        Assert.Equal(0, await db.MovimientosTesoreria.CountAsync(m => m.IdTurnoCaja == turno));
        Assert.Equal(1, await db.Auditoria.CountAsync(a => a.Accion == "retiro.baja" && a.IdEntidad == retiro));

        // El cierre posterior acredita solo el retiro vivo.
        await CerrarPorRetiroAsync(ctx.Admin, turno, 0m);
        Assert.Equal(950m, await EsperadoDelEfectivoAsync(db, turno, ctx.IdEfectivo));
        Assert.Equal(50m, await UltimoFinalDeTesoreriaAsync(db, ctx.IdEmpresa));
    }

    // ---- guardas ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ElRetiroDelCierreNoSeEditaNiSeElimina()
    {
        var ctx = await PrepararAsync(fixture, "retiro-de-cierre");
        var turno = await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta, 1000m);
        await CerrarPorRetiroAsync(ctx.Admin, turno, 300m);

        int idDelCierre;
        await using (var db = Db(ctx.IdTenant))
        {
            idDelCierre = await db.MovimientosCaja.Where(m => m.IdTurnoCaja == turno && m.EsRetiroDeCierre).Select(m => m.Id).SingleAsync();
        }

        await AsertarErrorAsync(
            await ctx.Admin.PutAsJsonAsync($"/api/caja/retiros/{idDelCierre}", new SolicitudDeEdicionDeRetiro(100m, "Corrijo el cierre")),
            HttpStatusCode.Conflict, "retiro_de_cierre_no_editable");
        await AsertarErrorAsync(
            await ctx.Admin.DeleteAsync($"/api/caja/retiros/{idDelCierre}"), HttpStatusCode.Conflict, "retiro_de_cierre_no_editable");

        await using var despues = Db(ctx.IdTenant);
        Assert.Equal(300m, (await despues.MovimientosCaja.SingleAsync(m => m.Id == idDelCierre)).Importe);
    }

    [Fact]
    public async Task UnRetiroDeUnTurnoConciliadoNoSeEditaNiSeElimina()
    {
        var ctx = await PrepararAsync(fixture, "retiro-conciliado");
        var turno = await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta, 1000m);
        var retiro = await RegistrarAsync(ctx.Admin, turno, TipoMovimientoCaja.Retiro, 200m, "Retiro conciliado");
        var cierre = await CerrarPorRetiroAsync(ctx.Admin, turno, 0m);
        await ConciliarAsync(ctx, cierre);

        await AsertarErrorAsync(
            await ctx.Admin.PutAsJsonAsync($"/api/caja/retiros/{retiro}", new SolicitudDeEdicionDeRetiro(100m, "Retiro corregido")),
            HttpStatusCode.Conflict, "periodo_conciliado");
        await AsertarErrorAsync(await ctx.Admin.DeleteAsync($"/api/caja/retiros/{retiro}"), HttpStatusCode.Conflict, "periodo_conciliado");

        await using var db = Db(ctx.IdTenant);
        var fila = await db.MovimientosCaja.SingleAsync(m => m.Id == retiro);
        Assert.Equal((200m, "Retiro conciliado"), (fila.Importe, fila.Motivo));
        Assert.Equal(0, await db.Auditoria.CountAsync(a => a.IdEntidad == retiro && a.Accion.StartsWith("retiro.")));
    }

    /// <summary>LA CLÁUSULA: el 404 es lo primero (update-404-first). Un cuerpo inválido sobre un id que no existe, que es un
    /// refuerzo, que es de otro tenant o que ya está dado de baja responde 404, no 400.</summary>
    [Fact]
    public async Task UnIdQueNoEsUnRetiroVivoDelTenantRespondeNoEncontradoAntesQueElCuerpo()
    {
        var ctx = await PrepararAsync(fixture, "retiro-404");
        var otro = await PrepararAsync(fixture, "retiro-404-otro");
        var turno = await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta, 1000m);
        var refuerzo = await RegistrarAsync(ctx.Admin, turno, TipoMovimientoCaja.Refuerzo, 100m, "Refuerzo de cambio");
        var dadoDeBaja = await RegistrarAsync(ctx.Admin, turno, TipoMovimientoCaja.Retiro, 100m, "Retiro que se elimina");
        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"/api/caja/retiros/{dadoDeBaja}")).StatusCode);
        var turnoDelOtro = await AbrirTurnoAsync(otro.Admin, otro.IdPuntoVenta, 1000m);
        var delOtro = await RegistrarAsync(otro.Admin, turnoDelOtro, TipoMovimientoCaja.Retiro, 100m, "Retiro de otro tenant");

        var cuerpoInvalido = new SolicitudDeEdicionDeRetiro(0m, "");
        foreach (var id in new[] { int.MaxValue, refuerzo, dadoDeBaja, delOtro })
        {
            var put = await ctx.Admin.PutAsJsonAsync($"/api/caja/retiros/{id}", cuerpoInvalido);
            Assert.True(put.StatusCode == HttpStatusCode.NotFound, $"id {id}: {put.StatusCode} {await put.Content.ReadAsStringAsync()}");
            await AsertarErrorAsync(put, HttpStatusCode.NotFound, "no_encontrado");
            Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.DeleteAsync($"/api/caja/retiros/{id}")).StatusCode);
        }

        await using var db = Db(otro.IdTenant);
        Assert.Equal(100m, (await db.MovimientosCaja.SingleAsync(m => m.Id == delOtro)).Importe);
    }

    [Theory]
    [InlineData(0, "Retiro corregido", "movimiento_de_caja_importe_invalido")]
    [InlineData(-5, "Retiro corregido", "movimiento_de_caja_importe_invalido")]
    [InlineData(100, "abc", "movimiento_de_caja_sin_motivo")]
    [InlineData(100, null, "movimiento_de_caja_sin_motivo")]
    public async Task ElCuerpoSeValidaConLasReglasDelAlta(int importe, string? motivo, string codigo)
    {
        var ctx = await PrepararAsync(fixture, $"retiro-400-{importe}-{motivo?.Length ?? -1}");
        var turno = await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta, 1000m);
        var retiro = await RegistrarAsync(ctx.Admin, turno, TipoMovimientoCaja.Retiro, 200m, "Retiro original");

        await AsertarErrorAsync(
            await ctx.Admin.PutAsJsonAsync($"/api/caja/retiros/{retiro}", new SolicitudDeEdicionDeRetiro(importe, motivo)),
            HttpStatusCode.BadRequest, codigo);

        await using var db = Db(ctx.IdTenant);
        Assert.Equal(200m, (await db.MovimientosCaja.SingleAsync(m => m.Id == retiro)).Importe);
    }

    [Fact]
    public async Task SoloElAdminCorrigeYElSupervisorSoloLista()
    {
        var ctx = await PrepararAsync(fixture, "retiro-roles");
        var turno = await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta, 1000m);
        var retiro = await RegistrarAsync(ctx.Admin, turno, TipoMovimientoCaja.Retiro, 200m, "Retiro original");
        var (supervisor, _) = await CrearUsuarioAsync(fixture, ctx, RolConocido.Supervisor, "supervisor");
        var (vendedor, _) = await CrearUsuarioAsync(fixture, ctx, RolConocido.Vendedor, "vendedor");

        foreach (var cliente in new[] { supervisor, vendedor })
        {
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await cliente.PutAsJsonAsync($"/api/caja/retiros/{retiro}", new SolicitudDeEdicionDeRetiro(100m, "Retiro corregido"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await cliente.DeleteAsync($"/api/caja/retiros/{retiro}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await vendedor.GetAsync("/api/caja/retiros")).StatusCode);
        var pagina = await supervisor.GetFromJsonAsync<PaginaDeRetiros>("/api/caja/retiros", OpcionesJson);
        Assert.Equal(retiro, Assert.Single(pagina!.Items).Id);

        await using var db = Db(ctx.IdTenant);
        Assert.Equal(200m, (await db.MovimientosCaja.SingleAsync(m => m.Id == retiro)).Importe);
    }

    // ---- backfill de la marca del retiro del cierre --------------------------------------------------------------------------

    /// <summary>LA CLÁUSULA (rls-migration-backfills): el <c>SET LOCAL app.acceso = 'plataforma'</c> del backfill. Se simula una fila
    /// anterior a la columna (el retiro del cierre sin marcar) y un retiro manual con el mismo motivo pero otra fecha, y se corre la
    /// constante REAL sobre <c>ways_app</c> (NOBYPASSRLS): sin su primera línea no ve ninguna fila; con ella marca solo el del
    /// cierre, y una segunda corrida no cambia nada.</summary>
    [Fact]
    public async Task ElBackfillMarcaSoloElRetiroDelCierreYNecesitaElGucDePlataformaSobreWaysApp()
    {
        var ctx = await PrepararAsync(fixture, "retiro-backfill");
        var turno = await AbrirTurnoAsync(ctx.Admin, ctx.IdPuntoVenta, 1000m);
        var manual = await RegistrarAsync(ctx.Admin, turno, TipoMovimientoCaja.Retiro, 100m, "Retiro de cierre de turno");
        await CerrarPorRetiroAsync(ctx.Admin, turno, 300m);

        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        var idDelCierre = await EscalarAsync<int>(
            owner, "SELECT id_movimiento FROM movimientos_caja WHERE id_turno_caja = $1 AND es_retiro_de_cierre", turno);
        await EjecutarAsync(owner, "UPDATE movimientos_caja SET es_retiro_de_cierre = false WHERE id_turno_caja = $1", turno);

        var bloque = Ways.Infrastructure.Persistencia.Migraciones.EdicionDeRetiros.SqlDelBackfill;
        Assert.StartsWith("SET LOCAL app.acceso = 'plataforma';", bloque, StringComparison.Ordinal);
        var sinElSetLocal = string.Join(
            '\n', bloque.Split('\n').Where(l => !l.TrimStart().StartsWith("SET LOCAL", StringComparison.Ordinal)));

        await EjecutarComoWaysAppAsync(sinElSetLocal);
        Assert.Equal(0L, await EscalarAsync<long>(
            owner, "SELECT count(*) FROM movimientos_caja WHERE id_turno_caja = $1 AND es_retiro_de_cierre", turno));

        await EjecutarComoWaysAppAsync(bloque);
        await EjecutarComoWaysAppAsync(bloque);
        Assert.Equal(idDelCierre, await EscalarAsync<int>(
            owner, "SELECT id_movimiento FROM movimientos_caja WHERE id_turno_caja = $1 AND es_retiro_de_cierre", turno));
        Assert.False(await EscalarAsync<bool>(
            owner, "SELECT es_retiro_de_cierre FROM movimientos_caja WHERE id_movimiento = $1", manual));
    }

    private async Task EjecutarComoWaysAppAsync(string sql)
    {
        await using var app = new NpgsqlConnection(fixture.AppConnectionString);
        await app.OpenAsync();
        await using var transaccion = await app.BeginTransactionAsync();
        await using var comando = app.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText = sql;
        await comando.ExecuteNonQueryAsync();
        await transaccion.CommitAsync();
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------

    private WaysDbContext Db(int idTenant) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));

    private static async Task<decimal> EsperadoDelEfectivoAsync(WaysDbContext db, int turno, int idEfectivo) =>
        await db.ArqueosTurno.AsNoTracking()
            .Where(a => a.IdTurnoCaja == turno && a.IdMedioPago == idEfectivo)
            .Select(a => a.ImporteEsperado)
            .SingleAsync();

    private static async Task<decimal> UltimoFinalDeTesoreriaAsync(WaysDbContext db, int idEmpresa) =>
        await db.MovimientosTesoreria.AsNoTracking()
            .Where(m => m.IdEmpresa == idEmpresa)
            .OrderByDescending(m => m.Id)
            .Select(m => m.Final)
            .FirstAsync();

    private static async Task<decimal> RetirosDelResumenAsync(HttpClient cliente, int turno)
    {
        var resumen = await cliente.GetFromJsonAsync<ResumenDeTurno>($"/api/caja/turnos/{turno}/resumen", OpcionesJson);
        return resumen!.Egresos.Retiros;
    }

    private static async Task<decimal?> EfectivoDelLibroAsync(Contexto ctx, int turno)
    {
        var desde = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-2).ToString("O"));
        var hasta = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
        var libro = await ctx.Admin.GetFromJsonAsync<LibroDeTesoreria>(
            $"/api/reportes/libro-tesoreria?idEmpresa={ctx.IdEmpresa}&desde={desde}&hasta={hasta}", OpcionesJson);
        return libro!.Filas.Single(f => f.Caja?.IdTurnoCaja == turno).Efectivo;
    }

    private static async Task ConciliarAsync(Contexto ctx, DateTimeOffset hasta)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/tesoreria/conciliaciones", new SolicitudDeConciliacion(ctx.IdEmpresa, hasta, null));
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, await respuesta.Content.ReadAsStringAsync());
    }

    private static async Task AsertarErrorAsync(HttpResponseMessage respuesta, HttpStatusCode estado, string codigo)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == estado, $"{respuesta.StatusCode}: {cuerpo}");
        using var problema = JsonDocument.Parse(cuerpo);
        Assert.Equal(codigo, problema.RootElement.GetProperty("codigo").GetString());
    }

    internal static async Task<Contexto> PrepararAsync(WaysApiFixture fixture, string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var unico = $"{nombre}-{Guid.NewGuid().ToString("N")[..6]}";
        var mailAdmin = $"{unico.ToLowerInvariant()}@ways.test";
        var alta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(unico, $"{unico} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).SingleAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, admin, idEfectivo, unico);
    }

    internal static async Task<(HttpClient Cliente, int IdUsuario)> CrearUsuarioAsync(
        WaysApiFixture fixture, Contexto ctx, RolConocido rol, string sufijo)
    {
        var mail = $"{ctx.Unico.ToLowerInvariant()}-{sufijo}@ways.test";
        var alta = await ctx.Admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario($"{sufijo}-retiros", mail, (int)rol, PasswordUsuario));
        Assert.True(alta.StatusCode == HttpStatusCode.Created, await alta.Content.ReadAsStringAsync());
        using var creado = JsonDocument.Parse(await alta.Content.ReadAsStringAsync());

        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordUsuario));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (cliente, creado.RootElement.GetProperty("id").GetInt32());
    }

    internal static async Task<int> AbrirTurnoAsync(HttpClient cliente, int idPuntoVenta, decimal fondoInicial)
    {
        var respuesta = await cliente.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(idPuntoVenta, fondoInicial, "Apertura de prueba"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<TurnoResumen>(cuerpo, OpcionesJson)!.Id;
    }

    internal static async Task<int> RegistrarAsync(
        HttpClient cliente, int turno, TipoMovimientoCaja tipo, decimal importe, string motivo)
    {
        var respuesta = await cliente.PostAsJsonAsync(
            $"/api/caja/turnos/{turno}/movimientos", new SolicitudDeMovimiento(tipo, importe, motivo));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<MovimientoRegistrado>(cuerpo, OpcionesJson)!.Id;
    }

    /// <summary>Devuelve la fecha del cierre.</summary>
    internal static async Task<DateTimeOffset> CerrarPorRetiroAsync(HttpClient cliente, int turno, decimal importeRetirado)
    {
        var respuesta = await cliente.PostAsJsonAsync(
            $"/api/caja/turnos/{turno}/cierre-por-retiro", new SolicitudDeCierrePorRetiro(importeRetirado, null));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<ResumenDeCierrePorRetiro>(cuerpo, OpcionesJson)!.FechaCierre;
    }

    internal static async Task<T> EscalarAsync<T>(NpgsqlConnection conexion, string sql, params object?[] parametros)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        foreach (var valor in parametros)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = valor ?? DBNull.Value });
        }

        var resultado = await comando.ExecuteScalarAsync();
        return resultado is null or DBNull ? default! : (T)resultado;
    }

    internal static async Task EjecutarAsync(NpgsqlConnection conexion, string sql, params object?[] parametros)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        foreach (var valor in parametros)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = valor ?? DBNull.Value });
        }

        await comando.ExecuteNonQueryAsync();
    }
}
