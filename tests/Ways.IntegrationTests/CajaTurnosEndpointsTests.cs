using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
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
/// stage-6-turnos-caja, Slice 2 (tasks 2.6, 2.8, 2.9, 2.10): <c>POST /api/caja/turnos</c>,
/// <c>GET …/abierto</c>, <c>POST …/{id}/movimientos</c> punta a punta — apertura detrás de
/// <c>ux_turnos_caja_abierto</c> con su carrera genuina, motivo/importe de movimientos, y
/// autorización <c>OperacionDePos</c> (spec: turnos-de-caja, movimientos-de-caja).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CajaTurnosEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordVendedor = "una-contraseña-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, int IdEmpleadoAdmin, string MailAdmin, string PasswordAdmin, HttpClient Admin);

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

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, mailAdmin, resultado.PasswordTemporal, admin);
    }

    private async Task<HttpClient> CrearVendedorAsync(Contexto ctx, string nombre)
    {
        var mailVendedor = $"{nombre.ToLowerInvariant()}-vendedor@ways.test";
        var alta = await ctx.Admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario("vendedor-caja", mailVendedor, (int)RolConocido.Vendedor, PasswordVendedor));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var vendedor = fixture.CreateClient();
        var login = await vendedor.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailVendedor, PasswordVendedor));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return vendedor;
    }

    private async Task<int> SembrarSegundoPuntoDeVentaAsync(Contexto ctx)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idEmpresa = await db.PuntosVenta.Where(p => p.Id == ctx.IdPuntoVenta).Select(p => p.IdEmpresa).FirstAsync();

        var otro = new PuntoVenta
        {
            IdTenant = ctx.IdTenant, IdEmpresa = idEmpresa, Nombre = "Local 2", CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(otro);
        await db.SaveChangesAsync();

        return otro.Id;
    }

    /// <summary>Seed directo (sin pasar por la API — el cierre real es Slice 4): un turno YA
    /// cerrado, para probar <c>409 turno_no_abierto</c> sobre un movimiento sin turno abierto
    /// (spec: Movimiento Requires An Open Turno).</summary>
    private async Task<int> SembrarTurnoCerradoAsync(Contexto ctx)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var turno = new TurnoCaja
        {
            IdTenant = ctx.IdTenant,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdEmpleadoApertura = ctx.IdEmpleadoAdmin,
            IdEmpleadoCierre = ctx.IdEmpleadoAdmin,
            FechaApertura = ahora.AddHours(-2),
            FechaCierre = ahora.AddHours(-1),
            FondoInicial = 100m,
            Estado = EstadoTurno.Cerrado,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.TurnosCaja.Add(turno);
        await db.SaveChangesAsync();

        return turno.Id;
    }

    // ---- task 2.6: apertura --------------------------------------------------------------------

    [Fact]
    public async Task LaAperturaCreaUnTurnoAbiertoConSuFondo()
    {
        var ctx = await PrepararAsync(nameof(LaAperturaCreaUnTurnoAbiertoConSuFondo));

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, 500m, "Apertura de prueba"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        var turno = JsonSerializer.Deserialize<TurnoResumen>(cuerpo, OpcionesJson)!;
        Assert.Equal(EstadoTurno.Abierto, turno.Estado);
        Assert.Equal(500m, turno.FondoInicial);
        Assert.Null(turno.FechaCierre);

        var abierto = await ctx.Admin.GetFromJsonAsync<TurnoResumen>(
            $"/api/caja/turnos/abierto?idPuntoVenta={ctx.IdPuntoVenta}", OpcionesJson);
        Assert.NotNull(abierto);
        Assert.Equal(turno.Id, abierto!.Id);

        var porId = await ctx.Admin.GetFromJsonAsync<TurnoResumen>($"/api/caja/turnos/{turno.Id}", OpcionesJson);
        Assert.Equal(turno.Id, porId!.Id);
    }

    [Fact]
    public async Task SinTurnoAbiertoElGateSeamDevuelveNull()
    {
        var ctx = await PrepararAsync(nameof(SinTurnoAbiertoElGateSeamDevuelveNull));

        var respuesta = await ctx.Admin.GetAsync($"/api/caja/turnos/abierto?idPuntoVenta={ctx.IdPuntoVenta}");
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.Equal("null", cuerpo);
    }

    [Fact]
    public async Task UnaSegundaAperturaEnElMismoPuntoDeVentaSeRechaza()
    {
        var ctx = await PrepararAsync(nameof(UnaSegundaAperturaEnElMismoPuntoDeVentaSeRechaza));

        var primera = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, 100m, "Primera apertura"));
        Assert.Equal(HttpStatusCode.Created, primera.StatusCode);

        var segunda = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, 200m, "Segunda apertura"));
        Assert.Equal(HttpStatusCode.Conflict, segunda.StatusCode);

        var problema = await segunda.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("turno_ya_abierto", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task AperturasEnDistintosPuntosDeVentaSonIndependientes()
    {
        var ctx = await PrepararAsync(nameof(AperturasEnDistintosPuntosDeVentaSonIndependientes));
        var idOtroPuntoVenta = await SembrarSegundoPuntoDeVentaAsync(ctx);

        var primera = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, 100m, "Local 1"));
        Assert.Equal(HttpStatusCode.Created, primera.StatusCode);

        var segunda = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(idOtroPuntoVenta, 100m, "Local 2"));
        Assert.Equal(HttpStatusCode.Created, segunda.StatusCode);
    }

    // ---- task 2.8: la carrera genuina -----------------------------------------------------------

    [Fact]
    public async Task DosAperturasConcurrentesEnElMismoPuntoDeVentaProducenExactamenteUnGanador()
    {
        var ctx = await PrepararAsync(nameof(DosAperturasConcurrentesEnElMismoPuntoDeVentaProducenExactamenteUnGanador));

        using var gate = new CountdownEvent(2);
        var interceptor = new InterceptorDeRendezVousDeTurnos(gate);
        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) =>
                    options.AddInterceptors(interceptor))));

        using var cliente = factory.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tareaA = cliente.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, 100m, "Apertura A"));
        var tareaB = cliente.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, 200m, "Apertura B"));

        var respuestas = await Task.WhenAll(tareaA, tareaB);
        var estados = respuestas.Select(r => r.StatusCode).ToList();

        Assert.True(interceptor.Participantes >= 2, $"participantes={interceptor.Participantes}");
        Assert.Contains(HttpStatusCode.Created, estados);
        Assert.Contains(HttpStatusCode.Conflict, estados);

        var respuestaConflicto = respuestas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        var problema = await respuestaConflicto.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("turno_ya_abierto", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Retiene las dos primeras <c>INSERT INTO turnos_caja</c> hasta que ambas
    /// llegaron — apertura es un INSERT llano sin lectura previa (design decisión 7), así que a
    /// diferencia de <c>ParametrosTests.InterceptorDeRendezVous</c> (que intercepta el SELECT
    /// "existente" de un upsert) acá se intercepta directamente el INSERT: es el único statement
    /// que la apertura ejecuta, y forzar su simultaneidad es lo único que hace la carrera
    /// genuina en vez de depender del timing real del pool de conexiones.</summary>
    private sealed class InterceptorDeRendezVousDeTurnos(CountdownEvent gate) : DbCommandInterceptor
    {
        private int _participantes;

        public int Participantes => _participantes;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            EsperarSiCorresponde(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            EsperarSiCorresponde(command);
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void EsperarSiCorresponde(DbCommand command)
        {
            if (!command.CommandText.Contains("INSERT INTO turnos_caja", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (Interlocked.Increment(ref _participantes) > 2)
            {
                return;
            }

            gate.Signal();

            var senializo = gate.Wait(TimeSpan.FromSeconds(10));
            Assert.True(senializo, "El rendezvous de InterceptorDeRendezVousDeTurnos no llegó a los 2 participantes a tiempo.");
        }
    }

    // ---- task 2.9: movimientos_caja --------------------------------------------------------------

    [Fact]
    public async Task UnRetiroSinMotivoSeRechaza()
    {
        var ctx = await PrepararAsync(nameof(UnRetiroSinMotivoSeRechaza));
        var turno = await AbrirTurnoAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{turno.Id}/movimientos",
            new SolicitudDeMovimiento(TipoMovimientoCaja.Retiro, 200m, ""));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("movimiento_de_caja_sin_motivo", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnRefuerzoSinMotivoSeRechaza()
    {
        var ctx = await PrepararAsync(nameof(UnRefuerzoSinMotivoSeRechaza));
        var turno = await AbrirTurnoAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{turno.Id}/movimientos",
            new SolicitudDeMovimiento(TipoMovimientoCaja.Refuerzo, 200m, null));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("movimiento_de_caja_sin_motivo", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnRetiroConMotivoEsAceptado()
    {
        var ctx = await PrepararAsync(nameof(UnRetiroConMotivoEsAceptado));
        var turno = await AbrirTurnoAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{turno.Id}/movimientos",
            new SolicitudDeMovimiento(TipoMovimientoCaja.Retiro, 200m, "pago a proveedor en efectivo"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        var movimiento = JsonSerializer.Deserialize<MovimientoRegistrado>(cuerpo, OpcionesJson)!;
        Assert.Equal(TipoMovimientoCaja.Retiro, movimiento.Tipo);
        Assert.Equal(200m, movimiento.Importe);
        Assert.Equal(turno.Id, movimiento.IdTurnoCaja);
    }

    [Fact]
    public async Task UnaAperturaDeCajonConImporteNoCeroSeRechaza()
    {
        var ctx = await PrepararAsync(nameof(UnaAperturaDeCajonConImporteNoCeroSeRechaza));
        var turno = await AbrirTurnoAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{turno.Id}/movimientos",
            new SolicitudDeMovimiento(TipoMovimientoCaja.AperturaCajon, 50m, "conteo inicial de turno"));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("movimiento_de_caja_importe_invalido", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnaAperturaDeCajonConMotivoCortoSeRechaza()
    {
        var ctx = await PrepararAsync(nameof(UnaAperturaDeCajonConMotivoCortoSeRechaza));
        var turno = await AbrirTurnoAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{turno.Id}/movimientos",
            new SolicitudDeMovimiento(TipoMovimientoCaja.AperturaCajon, 0m, "abc"));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("motivo_de_apertura_cajon_invalido", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnaAperturaDeCajonValidaEsAceptada()
    {
        var ctx = await PrepararAsync(nameof(UnaAperturaDeCajonValidaEsAceptada));
        var turno = await AbrirTurnoAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{turno.Id}/movimientos",
            new SolicitudDeMovimiento(TipoMovimientoCaja.AperturaCajon, 0m, "conteo inicial de turno"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        var movimiento = JsonSerializer.Deserialize<MovimientoRegistrado>(cuerpo, OpcionesJson)!;
        Assert.Equal(0m, movimiento.Importe);
    }

    [Fact]
    public async Task UnMovimientoSinTurnoAbiertoSeRechaza()
    {
        var ctx = await PrepararAsync(nameof(UnMovimientoSinTurnoAbiertoSeRechaza));
        var idTurnoCerrado = await SembrarTurnoCerradoAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{idTurnoCerrado}/movimientos",
            new SolicitudDeMovimiento(TipoMovimientoCaja.Retiro, 100m, "retiro contra turno cerrado"));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("turno_no_abierto", problema.GetProperty("codigo").GetString());
    }

    // ---- task 2.10: autorización ------------------------------------------------------------------

    [Fact]
    public async Task UnVendedorAbreUnTurnoYRegistraUnMovimiento()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorAbreUnTurnoYRegistraUnMovimiento));
        using var vendedor = await CrearVendedorAsync(ctx, nameof(UnVendedorAbreUnTurnoYRegistraUnMovimiento));

        var apertura = await vendedor.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, 300m, "Apertura de vendedor"));
        var cuerpoApertura = await apertura.Content.ReadAsStringAsync();
        Assert.True(apertura.StatusCode == HttpStatusCode.Created, cuerpoApertura);
        var turno = JsonSerializer.Deserialize<TurnoResumen>(cuerpoApertura, OpcionesJson)!;

        var movimiento = await vendedor.PostAsJsonAsync(
            $"/api/caja/turnos/{turno.Id}/movimientos",
            new SolicitudDeMovimiento(TipoMovimientoCaja.Refuerzo, 50m, "refuerzo de vendedor"));
        Assert.Equal(HttpStatusCode.Created, movimiento.StatusCode);
    }

    [Fact]
    public async Task UnRolFueraDeOperacionDePosEsRechazadoDeAperturaYDeMovimientos()
    {
        var ctx = await PrepararAsync(nameof(UnRolFueraDeOperacionDePosEsRechazadoDeAperturaYDeMovimientos));
        var turno = await AbrirTurnoAsync(ctx);

        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var apertura = await root.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, 100m, "Intento de root"));
        Assert.Equal(HttpStatusCode.Forbidden, apertura.StatusCode);

        var movimiento = await root.PostAsJsonAsync(
            $"/api/caja/turnos/{turno.Id}/movimientos",
            new SolicitudDeMovimiento(TipoMovimientoCaja.Retiro, 100m, "intento de root"));
        Assert.Equal(HttpStatusCode.Forbidden, movimiento.StatusCode);
    }

    // ---- judgment-day (Slice 2, ronda 2): auth guard blind spot ------------------------------------

    /// <summary>Confirmed issue (judgment-day, Slice 2 ronda 2, MAJOR): companion directa del
    /// guard de <see cref="SuperficieDeAutorizacionTests.TodoEndpointGetBajoLasSuperficiesReGateadasApilaOperacionDePos"/>
    /// — mismo criterio que <c>OperacionDePosLecturaTests.ResolverOfertasSinTokenDevuelve401</c>:
    /// sin token, <c>OperacionDePos</c> exige <c>RequireAuthenticatedUser()</c> antes que nada.</summary>
    [Fact]
    public async Task ObtenerTurnoAbiertoSinTokenDevuelve401()
    {
        using var cliente = fixture.CreateClient();

        var respuesta = await cliente.GetAsync("/api/caja/turnos/abierto?idPuntoVenta=1");

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
    }

    // ---- judgment-day (Slice 2, ronda 2): regresión cross-tenant ------------------------------------

    /// <summary>Confirmed issue (judgment-day, Slice 2 ronda 2, MAJOR): mismo criterio ADR-8 que
    /// <c>ArticulosEndpointsTests.AgregarCodigoBarraAUnArticuloDeOtroTenantDevuelve404</c> — pin
    /// contra una regresión futura de <c>IgnoreQueryFilters</c> sobre <c>ObtenerAsync</c>.</summary>
    [Fact]
    public async Task ObtenerUnTurnoDeOtroTenantDevuelve404()
    {
        var ctxA = await PrepararAsync(nameof(ObtenerUnTurnoDeOtroTenantDevuelve404) + "-A");
        var turnoDeA = await AbrirTurnoAsync(ctxA);

        var ctxB = await PrepararAsync(nameof(ObtenerUnTurnoDeOtroTenantDevuelve404) + "-B");

        var respuesta = await ctxB.Admin.GetAsync($"/api/caja/turnos/{turnoDeA.Id}");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    /// <summary>Confirmed issue (judgment-day, Slice 2 ronda 2, MAJOR): mismo criterio que
    /// <see cref="ObtenerUnTurnoDeOtroTenantDevuelve404"/>, pin contra
    /// <c>ResolverTurnoPorIdAbiertoAsync</c> sobre el turno ABIERTO de otro tenant.</summary>
    [Fact]
    public async Task RegistrarMovimientoContraElTurnoAbiertoDeOtroTenantDevuelve404()
    {
        var ctxA = await PrepararAsync(nameof(RegistrarMovimientoContraElTurnoAbiertoDeOtroTenantDevuelve404) + "-A");
        var turnoDeA = await AbrirTurnoAsync(ctxA);

        var ctxB = await PrepararAsync(nameof(RegistrarMovimientoContraElTurnoAbiertoDeOtroTenantDevuelve404) + "-B");

        var respuesta = await ctxB.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{turnoDeA.Id}/movimientos",
            new SolicitudDeMovimiento(TipoMovimientoCaja.Retiro, 100m, "intento cross-tenant"));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    // ---- judgment-day (Slice 2, ronda 2): cobertura del historial y del 404 puntual ------------------

    /// <summary>Confirmed issue (judgment-day, Slice 2 ronda 2, MINOR): <c>ServicioDeTurnos.ListarAsync</c>
    /// sin cobertura punta a punta — pagina (Total/Pagina/Tamanio) y el filtro <c>idPuntoVenta</c>.</summary>
    [Fact]
    public async Task ElHistorialPaginaYFiltraPorPuntoDeVenta()
    {
        var ctx = await PrepararAsync(nameof(ElHistorialPaginaYFiltraPorPuntoDeVenta));
        var idOtroPuntoVenta = await SembrarSegundoPuntoDeVentaAsync(ctx);

        await SembrarTurnoCerradoAsync(ctx);
        await AbrirTurnoAsync(ctx);

        var enOtroPunto = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(idOtroPuntoVenta, 100m, "Local 2"));
        Assert.Equal(HttpStatusCode.Created, enOtroPunto.StatusCode);

        var completo = await ctx.Admin.GetFromJsonAsync<PaginaDeTurnos>("/api/caja/turnos", OpcionesJson);
        Assert.NotNull(completo);
        Assert.Equal(3, completo!.Total);
        Assert.Equal(3, completo.Items.Count);
        Assert.Equal(1, completo.Pagina);
        Assert.Equal(25, completo.Tamanio);

        var filtrado = await ctx.Admin.GetFromJsonAsync<PaginaDeTurnos>(
            $"/api/caja/turnos?idPuntoVenta={ctx.IdPuntoVenta}", OpcionesJson);
        Assert.NotNull(filtrado);
        Assert.Equal(2, filtrado!.Total);
        Assert.All(filtrado.Items, item => Assert.Equal(ctx.IdPuntoVenta, item.IdPuntoVenta));
    }

    /// <summary>Confirmed issue (judgment-day, Slice 2 ronda 2, MINOR): <c>GET …/{id}</c> con un id
    /// que no existe (a diferencia de <see cref="ObtenerUnTurnoDeOtroTenantDevuelve404"/>, que
    /// existe pero es de otro tenant) también cae en el mismo <c>404</c> ADR-8.</summary>
    [Fact]
    public async Task ObtenerUnTurnoInexistenteDevuelve404()
    {
        var ctx = await PrepararAsync(nameof(ObtenerUnTurnoInexistenteDevuelve404));

        var respuesta = await ctx.Admin.GetAsync("/api/caja/turnos/999999");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    // ---- helper compartido ----------------------------------------------------------------------

    private static async Task<TurnoResumen> AbrirTurnoAsync(Contexto ctx)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, 500m, "Apertura de soporte"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        return JsonSerializer.Deserialize<TurnoResumen>(cuerpo, OpcionesJson)!;
    }
}
