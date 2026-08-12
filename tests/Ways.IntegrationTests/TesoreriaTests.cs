using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Caja;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-11-exportacion-reportes, Slice 7 (design: G2/G3 — minimal aggregation, "G3:
/// MovimientosTesoreria by PV, OrderBy(m => m.Id), paginated. Zero derivation."; spec tesoreria:
/// Tesorería Book Has A Read/Listing Endpoint): <c>GET /api/reportes/tesoreria</c> — la casa de las
/// 4 pruebas (cruce de tenant, discriminación por punto de venta, discriminación por rango de
/// fecha, fixture hand-computed) más el chain-order assertion que el spec fija explícitamente y el
/// rol un escalón debajo del gate. Siembra directa vía <c>IWaysDbContext.MovimientosTesoreria</c>
/// (nunca a través del cierre): cada fila lleva su propio <c>Inicio</c>/<c>Final</c> explícito, sin
/// depender del reloj del servidor — misma disciplina "time-safe seeding" que el resto de esta
/// etapa (fechas fijas a mediodía UTC, nunca <c>DateTime.UtcNow</c> puro).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class TesoreriaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdEmpleadoAdmin, HttpClient Admin, HttpClient Supervisor,
        HttpClient Vendedor);

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

        var supervisor = await CrearYLoguearAsync(admin, nombre, "supervisor", RolConocido.Supervisor);
        var vendedor = await CrearYLoguearAsync(admin, nombre, "vendedor", RolConocido.Vendedor);

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, admin,
            supervisor, vendedor);
    }

    private async Task<HttpClient> CrearYLoguearAsync(HttpClient admin, string nombre, string sufijo, RolConocido rol)
    {
        var corto = Guid.NewGuid().ToString("N")[..8];
        var mail = $"{nombre.ToLowerInvariant()}-{sufijo}@ways.test";
        var alta = await admin.PostAsJsonAsync("/api/usuarios", new CrearUsuario($"{sufijo}-{corto}", mail, (int)rol, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private async Task<int> SembrarPuntoVentaAsync(Contexto ctx, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var puntoVenta = new PuntoVenta { IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();

        return puntoVenta.Id;
    }

    /// <summary>Siembra directo un <see cref="MovimientoTesoreria"/> — nunca a través del cierre:
    /// esta clase prueba la LECTURA del libro, no la escritura encadenada (ya cubierta por las
    /// pruebas de cierre de stage-6). <paramref name="fecha"/> fija a mediodía UTC (evita la
    /// ventana 00-03 UTC, fix/tests-reportes-ventana-utc).</summary>
    private async Task<int> SembrarMovimientoAsync(
        Contexto ctx, int idPuntoVenta, DateOnly dia, decimal inicio, decimal ingreso, decimal egreso, decimal final,
        string concepto = "Cierre de turno")
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var fecha = new DateTimeOffset(dia.Year, dia.Month, dia.Day, 12, 0, 0, TimeSpan.Zero);

        var movimiento = new MovimientoTesoreria
        {
            IdTenant = ctx.IdTenant,
            IdPuntoVenta = idPuntoVenta,
            Fecha = fecha,
            Tipo = TipoMovimientoTesoreria.RetiroCaja,
            IdTurnoCaja = null,
            Concepto = concepto,
            Inicio = inicio,
            Ingreso = ingreso,
            Egreso = egreso,
            Final = final,
            IdEmpleado = ctx.IdEmpleadoAdmin
        };
        db.MovimientosTesoreria.Add(movimiento);
        await db.SaveChangesAsync();

        return movimiento.Id;
    }

    private static Task<PaginaDeMovimientosTesoreria?> ListarAsync(
        HttpClient cliente, int idPuntoVenta, DateOnly? desde = null, DateOnly? hasta = null)
    {
        var query = $"idPuntoVenta={idPuntoVenta}";
        if (desde is { } d)
        {
            query += $"&desde={d:yyyy-MM-dd}T00:00:00Z";
        }

        if (hasta is { } h)
        {
            query += $"&hasta={h:yyyy-MM-dd}T23:59:59Z";
        }

        return cliente.GetFromJsonAsync<PaginaDeMovimientosTesoreria>($"/api/reportes/tesoreria?{query}", OpcionesJson);
    }

    // ---- task 7.8: house 4-test pattern -------------------------------------------------------

    [Fact]
    public async Task UnMovimientoDeOtroTenantNuncaApareceEnElLibro()
    {
        var ctxA = await PrepararAsync(nameof(UnMovimientoDeOtroTenantNuncaApareceEnElLibro) + "A");
        var ctxB = await PrepararAsync(nameof(UnMovimientoDeOtroTenantNuncaApareceEnElLibro) + "B");
        var dia = new DateOnly(2026, 8, 1);

        var idMovimientoB = await SembrarMovimientoAsync(ctxB, ctxB.IdPuntoVenta, dia, 0m, 100m, 0m, 100m);

        var libroDeA = await ListarAsync(ctxA.Admin, ctxA.IdPuntoVenta);

        Assert.NotNull(libroDeA);
        Assert.DoesNotContain(libroDeA!.Items, m => m.Id == idMovimientoB);
    }

    /// <summary>task 7.8 (mutation-proof-tests): la cláusula bajo prueba es
    /// <c>Where(m => m.IdPuntoVenta == idPuntoVenta)</c> en <see cref="ServicioDeTesoreria"/>. Un
    /// mismo tenant con dos puntos de venta — el libro de uno NUNCA puede traer filas del otro,
    /// porque mezclarlas rompería el propio significado de la cadena (design decisión 11).
    /// Mutación aplicada (reemplazar el <c>Where</c> por <c>AsQueryable()</c> en
    /// <c>ServicioDeTesoreria.ConstruirQuery</c>): esta prueba pasó de FALLAR (el movimiento del PV
    /// secundario aparece en el libro del PV principal) a pasar al revertir — evidencia registrada
    /// en el cuerpo del commit (esta rama no abre PR).</summary>
    [Fact]
    public async Task UnMovimientoDeOtroPuntoDeVentaNuncaApareceEnElLibro()
    {
        var ctx = await PrepararAsync(nameof(UnMovimientoDeOtroPuntoDeVentaNuncaApareceEnElLibro));
        var otroPuntoVenta = await SembrarPuntoVentaAsync(ctx, "PV secundario");
        var dia = new DateOnly(2026, 8, 1);

        var idPrincipal = await SembrarMovimientoAsync(ctx, ctx.IdPuntoVenta, dia, 0m, 60m, 0m, 60m);
        var idSecundario = await SembrarMovimientoAsync(ctx, otroPuntoVenta, dia, 0m, 999m, 0m, 999m);

        var libro = await ListarAsync(ctx.Admin, ctx.IdPuntoVenta);

        Assert.NotNull(libro);
        Assert.Contains(libro!.Items, m => m.Id == idPrincipal);
        Assert.DoesNotContain(libro.Items, m => m.Id == idSecundario);
    }

    [Fact]
    public async Task ElFiltroDeFechaExcluyeMovimientosFueraDelRango()
    {
        var ctx = await PrepararAsync(nameof(ElFiltroDeFechaExcluyeMovimientosFueraDelRango));
        var dentro = new DateOnly(2026, 8, 5);
        var fuera = new DateOnly(2026, 8, 20);

        var idDentro = await SembrarMovimientoAsync(ctx, ctx.IdPuntoVenta, dentro, 0m, 100m, 0m, 100m);
        var idFuera = await SembrarMovimientoAsync(ctx, ctx.IdPuntoVenta, fuera, 100m, 50m, 0m, 150m);

        var libro = await ListarAsync(ctx.Admin, ctx.IdPuntoVenta, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 10));

        Assert.NotNull(libro);
        Assert.Contains(libro!.Items, m => m.Id == idDentro);
        Assert.DoesNotContain(libro.Items, m => m.Id == idFuera);
    }

    [Fact]
    public async Task LosCamposDelMovimientoCoincidenConLaFilaSembrada()
    {
        var ctx = await PrepararAsync(nameof(LosCamposDelMovimientoCoincidenConLaFilaSembrada));
        var dia = new DateOnly(2026, 8, 1);

        var id = await SembrarMovimientoAsync(ctx, ctx.IdPuntoVenta, dia, 500m, 120m, 20m, 600m, "Cierre de turno de prueba");

        var libro = await ListarAsync(ctx.Admin, ctx.IdPuntoVenta);

        Assert.NotNull(libro);
        var fila = Assert.Single(libro!.Items, m => m.Id == id);
        Assert.Equal(ctx.IdPuntoVenta, fila.IdPuntoVenta);
        Assert.Equal(TipoMovimientoTesoreria.RetiroCaja, fila.Tipo);
        Assert.Equal("Cierre de turno de prueba", fila.Concepto);
        Assert.Equal(500m, fila.Inicio);
        Assert.Equal(120m, fila.Ingreso);
        Assert.Equal(20m, fila.Egreso);
        Assert.Equal(600m, fila.Final);
        Assert.Equal(ctx.IdEmpleadoAdmin, fila.IdEmpleado);
    }

    // ---- task 7.9: chain-order assertion (spec: Book Preserves Chain Order; mutation-proof) ----

    /// <summary>spec tesoreria: "Book preserves chain order" — tres filas encadenadas con
    /// <c>final</c> 60, 100, 145 tienen que volver EN ESE ORDEN, y el <c>inicio</c> de cada una
    /// tiene que ser el <c>final</c> de la anterior. La cláusula bajo prueba es
    /// <c>OrderBy(m => m.Id)</c> en <see cref="ServicioDeTesoreria"/> (design decisión 11: nunca
    /// <c>OrderBy(m => m.Fecha)</c>). Mutación aplicada (reemplazar <c>OrderBy(m => m.Id)</c> por
    /// <c>OrderByDescending(m => m.Id)</c>): esta prueba pasó de FALLAR (las filas vuelven 145,
    /// 100, 60 — orden invertido, la cadena Inicio/Final deja de encajar) a pasar al revertir —
    /// evidencia registrada en el cuerpo del commit.</summary>
    [Fact]
    public async Task TresFilasEncadenadasSeDevuelvenEnOrdenDeCadena()
    {
        var ctx = await PrepararAsync(nameof(TresFilasEncadenadasSeDevuelvenEnOrdenDeCadena));
        var dia = new DateOnly(2026, 8, 1);

        var id1 = await SembrarMovimientoAsync(ctx, ctx.IdPuntoVenta, dia, 0m, 60m, 0m, 60m);
        var id2 = await SembrarMovimientoAsync(ctx, ctx.IdPuntoVenta, dia, 60m, 40m, 0m, 100m);
        var id3 = await SembrarMovimientoAsync(ctx, ctx.IdPuntoVenta, dia, 100m, 60m, 15m, 145m);

        var libro = await ListarAsync(ctx.Admin, ctx.IdPuntoVenta);

        Assert.NotNull(libro);
        Assert.Equal(3, libro!.Items.Count);
        Assert.Equal([id1, id2, id3], libro.Items.Select(m => m.Id));
        Assert.Equal([60m, 100m, 145m], libro.Items.Select(m => m.Final));

        for (var i = 1; i < libro.Items.Count; i++)
        {
            Assert.Equal(libro.Items[i - 1].Final, libro.Items[i].Inicio);
        }
    }

    // ---- task 7.11: rol un escalón debajo del gate ---------------------------------------------

    [Fact]
    public async Task UnVendedorEsRechazadoDelLibroDeTesoreria()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoDelLibroDeTesoreria));

        var respuesta = await ctx.Vendedor.GetAsync($"/api/reportes/tesoreria?idPuntoVenta={ctx.IdPuntoVenta}");

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnSupervisorLeeElLibroDeTesoreria()
    {
        var ctx = await PrepararAsync(nameof(UnSupervisorLeeElLibroDeTesoreria));

        var respuesta = await ctx.Supervisor.GetAsync($"/api/reportes/tesoreria?idPuntoVenta={ctx.IdPuntoVenta}");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }
}
