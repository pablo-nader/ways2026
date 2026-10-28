using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Gastos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Caja;
using Ways.Domain.Gastos;
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
///
/// stage-tesoreria-por-empresa (PR5): <c>idEmpresa</c> pasa a ser el filtro OBLIGATORIO (ADR-8) y
/// <c>idPuntoVenta</c> un filtro OPCIONAL adicional sobre la misma cadena — se agregan las pruebas
/// de empresa multi-PV, 404 de empresa de otro tenant, filas sin punto de venta/con gasto de
/// origen, y punto de venta dado de baja lógica.
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

    /// <summary>stage-tesoreria-por-empresa (PR5, mismo criterio que
    /// <c>GastosDeAdministracionEndpointsTests.SembrarOtraEmpresaAsync</c>): segunda empresa +
    /// punto de venta del MISMO tenant, para probar el 404 ADR-8 de una empresa "ajena" que
    /// igualmente existe en la base (nunca visible desde el tenant de <paramref name="ctx"/> si el
    /// filtro/RLS estuviera roto).</summary>
    private async Task<(int IdEmpresa, int IdPuntoVenta)> SembrarOtraEmpresaDelMismoTenantAsync(Contexto ctx, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var empresa = new Empresa { IdTenant = ctx.IdTenant, RazonSocial = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        var puntoVenta = new PuntoVenta
        {
            IdTenant = ctx.IdTenant, IdEmpresa = empresa.Id, Nombre = $"{nombre} - Local", CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();

        return (empresa.Id, puntoVenta.Id);
    }

    private async Task SoftDeletePuntoVentaAsync(Contexto ctx, int idPuntoVenta)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var pv = await db.PuntosVenta.SingleAsync(p => p.Id == idPuntoVenta);
        pv.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>Siembra directo un <see cref="MovimientoTesoreria"/> — nunca a través del cierre:
    /// esta clase prueba la LECTURA del libro, no la escritura encadenada (ya cubierta por las
    /// pruebas de cierre de stage-6). <paramref name="fecha"/> fija a mediodía UTC (evita la
    /// ventana 00-03 UTC, fix/tests-reportes-ventana-utc).</summary>
    private async Task<int> SembrarMovimientoAsync(
        Contexto ctx, int? idPuntoVenta, DateOnly dia, decimal inicio, decimal ingreso, decimal egreso, decimal final,
        string concepto = "Cierre de turno", int idEmpresa = 0)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var fecha = new DateTimeOffset(dia.Year, dia.Month, dia.Day, 12, 0, 0, TimeSpan.Zero);

        var movimiento = new MovimientoTesoreria
        {
            IdTenant = ctx.IdTenant,
            IdEmpresa = idEmpresa == 0 ? ctx.IdEmpresa : idEmpresa,
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

    private static string ConstruirQuery(int idEmpresa, int? idPuntoVenta, DateOnly? desde, DateOnly? hasta)
    {
        var query = $"idEmpresa={idEmpresa}";
        if (idPuntoVenta is { } pv)
        {
            query += $"&idPuntoVenta={pv}";
        }

        if (desde is { } d)
        {
            query += $"&desde={d:yyyy-MM-dd}T00:00:00Z";
        }

        if (hasta is { } h)
        {
            query += $"&hasta={h:yyyy-MM-dd}T23:59:59Z";
        }

        return query;
    }

    private static Task<HttpResponseMessage> LlamarLibroCrudoAsync(
        HttpClient cliente, int idEmpresa, int? idPuntoVenta = null, DateOnly? desde = null, DateOnly? hasta = null) =>
        cliente.GetAsync($"/api/reportes/tesoreria?{ConstruirQuery(idEmpresa, idPuntoVenta, desde, hasta)}");

    private static async Task<PaginaDeMovimientosTesoreria?> ListarAsync(
        HttpClient cliente, int idEmpresa, int? idPuntoVenta = null, DateOnly? desde = null, DateOnly? hasta = null)
    {
        var respuesta = await LlamarLibroCrudoAsync(cliente, idEmpresa, idPuntoVenta, desde, hasta);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        return JsonSerializer.Deserialize<PaginaDeMovimientosTesoreria>(await respuesta.Content.ReadAsStringAsync(), OpcionesJson);
    }

    // ---- task 7.8: house 4-test pattern -------------------------------------------------------

    [Fact]
    public async Task UnMovimientoDeOtroTenantNuncaApareceEnElLibro()
    {
        var ctxA = await PrepararAsync(nameof(UnMovimientoDeOtroTenantNuncaApareceEnElLibro) + "A");
        var ctxB = await PrepararAsync(nameof(UnMovimientoDeOtroTenantNuncaApareceEnElLibro) + "B");
        var dia = new DateOnly(2026, 8, 1);

        var idMovimientoB = await SembrarMovimientoAsync(ctxB, ctxB.IdPuntoVenta, dia, 0m, 100m, 0m, 100m);

        var libroDeA = await ListarAsync(ctxA.Admin, ctxA.IdEmpresa);

        Assert.NotNull(libroDeA);
        Assert.DoesNotContain(libroDeA!.Items, m => m.Id == idMovimientoB);
    }

    /// <summary>task 7.8 (mutation-proof-tests): la cláusula bajo prueba es
    /// <c>Where(m => m.IdPuntoVenta == idPuntoVenta)</c> en <see cref="ServicioDeTesoreria"/>. Un
    /// mismo tenant con dos puntos de venta — el libro filtrado por uno NUNCA puede traer filas del
    /// otro. Mutación aplicada (reemplazar el <c>Where</c> por <c>AsQueryable()</c> en
    /// <c>ServicioDeTesoreria.ConstruirQuery</c>): esta prueba pasó de FALLAR (el movimiento del PV
    /// secundario aparece en el libro filtrado por el PV principal) a pasar al revertir — evidencia
    /// registrada en el cuerpo del commit (esta rama no abre PR).</summary>
    [Fact]
    public async Task UnMovimientoDeOtroPuntoDeVentaNuncaApareceEnElLibroConFiltroDePuntoVenta()
    {
        var ctx = await PrepararAsync(nameof(UnMovimientoDeOtroPuntoDeVentaNuncaApareceEnElLibroConFiltroDePuntoVenta));
        var otroPuntoVenta = await SembrarPuntoVentaAsync(ctx, "PV secundario");
        var dia = new DateOnly(2026, 8, 1);

        var idPrincipal = await SembrarMovimientoAsync(ctx, ctx.IdPuntoVenta, dia, 0m, 60m, 0m, 60m);
        var idSecundario = await SembrarMovimientoAsync(ctx, otroPuntoVenta, dia, 0m, 999m, 0m, 999m);

        var libro = await ListarAsync(ctx.Admin, ctx.IdEmpresa, ctx.IdPuntoVenta);

        Assert.NotNull(libro);
        Assert.Contains(libro!.Items, m => m.Id == idPrincipal);
        Assert.DoesNotContain(libro.Items, m => m.Id == idSecundario);
    }

    /// <summary>stage-tesoreria-por-empresa (PR5, spec: la cadena es por empresa) — SIN filtro de
    /// punto de venta, el libro de la empresa trae las filas de AMBOS puntos de venta: la cadena es
    /// una sola por empresa, no una por PV.</summary>
    [Fact]
    public async Task SinFiltroDePuntoVentaElLibroIncluyeLosDosPuntosVentaDeLaEmpresa()
    {
        var ctx = await PrepararAsync(nameof(SinFiltroDePuntoVentaElLibroIncluyeLosDosPuntosVentaDeLaEmpresa));
        var segundoPuntoVenta = await SembrarPuntoVentaAsync(ctx, "PV secundario");
        var dia = new DateOnly(2026, 8, 1);

        var id1 = await SembrarMovimientoAsync(ctx, ctx.IdPuntoVenta, dia, 0m, 60m, 0m, 60m);
        var id2 = await SembrarMovimientoAsync(ctx, segundoPuntoVenta, dia, 60m, 40m, 0m, 100m);

        var libro = await ListarAsync(ctx.Admin, ctx.IdEmpresa);

        Assert.NotNull(libro);
        Assert.Contains(libro!.Items, m => m.Id == id1);
        Assert.Contains(libro.Items, m => m.Id == id2);
    }

    /// <summary>ADR-8: una empresa que existe en la base pero es de OTRO tenant (o directamente no
    /// existe) devuelve 404 — nunca 200 con un libro vacío, mismo criterio que
    /// <c>ServicioDeReportesDeArticulos.ExigirEmpresaAsync</c>.</summary>
    [Fact]
    public async Task UnaEmpresaDeOtroTenantDevuelve404()
    {
        var ctxA = await PrepararAsync(nameof(UnaEmpresaDeOtroTenantDevuelve404) + "A");
        var ctxB = await PrepararAsync(nameof(UnaEmpresaDeOtroTenantDevuelve404) + "B");

        var respuesta = await LlamarLibroCrudoAsync(ctxA.Admin, ctxB.IdEmpresa);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    /// <summary>Una empresa que directamente no existe (id inventado) también es 404, mismo
    /// criterio que una empresa de otro tenant (ADR-8: mismo 404 para ambos casos).</summary>
    [Fact]
    public async Task UnaEmpresaInexistenteDevuelve404()
    {
        var ctx = await PrepararAsync(nameof(UnaEmpresaInexistenteDevuelve404));

        var respuesta = await LlamarLibroCrudoAsync(ctx.Admin, 999_999);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    /// <summary>judgment-day PR5, ronda 1, hallazgo confirmado #1 (export==JSON parity): un
    /// <c>idPuntoVenta</c> que NO pertenece a <c>idEmpresa</c> (pero sí existe, de otra empresa del
    /// MISMO tenant) tiene que rechazar EXACTAMENTE igual en <c>GET /tesoreria</c> y
    /// <c>GET /tesoreria/export</c> — mismo status 400, mismo código
    /// <c>punto_venta_no_pertenece_a_la_empresa</c>. Antes de este fix, el JSON devolvía 200 con
    /// una página vacía (el filtro `IdPuntoVenta == pv` de <c>ConstruirQuery</c> simplemente no
    /// matcheaba ninguna fila) mientras el export ya rechazaba con 400 vía
    /// <c>ServicioDeParametros.ResolverAsync</c> — mutación aplicada (comentar la llamada a
    /// <c>ValidarPuntoVentaDeLaEmpresaAsync</c> en <c>ServicioDeTesoreria.ListarAsync</c>): esta
    /// prueba pasó de FALLAR (200 en vez de 400 en la ruta JSON) a pasar al revertir — evidencia
    /// registrada en el cuerpo de esta corrección.</summary>
    [Fact]
    public async Task UnPuntoDeVentaQueNoPerteneceALaEmpresaRechazaIgualEnJsonYEnExport()
    {
        var ctx = await PrepararAsync(nameof(UnPuntoDeVentaQueNoPerteneceALaEmpresaRechazaIgualEnJsonYEnExport));
        var (_, idPuntoVentaDeOtraEmpresa) = await SembrarOtraEmpresaDelMismoTenantAsync(ctx, "Otra empresa");
        var dia = new DateOnly(2026, 8, 1);

        var respuestaJson = await LlamarLibroCrudoAsync(ctx.Admin, ctx.IdEmpresa, idPuntoVentaDeOtraEmpresa, dia, dia);
        var respuestaExport = await ctx.Admin.GetAsync(
            $"/api/reportes/tesoreria/export?idEmpresa={ctx.IdEmpresa}&idPuntoVenta={idPuntoVentaDeOtraEmpresa}" +
            $"&desde={dia:yyyy-MM-dd}T00:00:00-03:00&hasta={dia:yyyy-MM-dd}T23:59:59-03:00&formato=xlsx");

        Assert.Equal(HttpStatusCode.BadRequest, respuestaJson.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, respuestaExport.StatusCode);

        var problemaJson = await respuestaJson.Content.ReadFromJsonAsync<JsonElement>();
        var problemaExport = await respuestaExport.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("punto_venta_no_pertenece_a_la_empresa", problemaJson.GetProperty("codigo").GetString());
        Assert.Equal("punto_venta_no_pertenece_a_la_empresa", problemaExport.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task ElFiltroDeFechaExcluyeMovimientosFueraDelRango()
    {
        var ctx = await PrepararAsync(nameof(ElFiltroDeFechaExcluyeMovimientosFueraDelRango));
        var dentro = new DateOnly(2026, 8, 5);
        var fueraPorHasta = new DateOnly(2026, 8, 20);
        // Una fila ANTERIOR a `desde`: sin ella, la cota inferior queda sin discriminar
        // (borrar el bloque `desde` pasaba verde — hallazgo de judgment-day).
        var fueraPorDesde = new DateOnly(2026, 7, 20);

        var idDentro = await SembrarMovimientoAsync(ctx, ctx.IdPuntoVenta, dentro, 0m, 100m, 0m, 100m);
        var idFueraPorHasta = await SembrarMovimientoAsync(ctx, ctx.IdPuntoVenta, fueraPorHasta, 100m, 50m, 0m, 150m);
        var idFueraPorDesde = await SembrarMovimientoAsync(ctx, ctx.IdPuntoVenta, fueraPorDesde, 0m, 30m, 0m, 30m);

        var libro = await ListarAsync(ctx.Admin, ctx.IdEmpresa, ctx.IdPuntoVenta, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 10));

        Assert.NotNull(libro);
        Assert.Contains(libro!.Items, m => m.Id == idDentro);
        Assert.DoesNotContain(libro.Items, m => m.Id == idFueraPorHasta);
        Assert.DoesNotContain(libro.Items, m => m.Id == idFueraPorDesde);
    }

    [Fact]
    public async Task LosCamposDelMovimientoCoincidenConLaFilaSembrada()
    {
        var ctx = await PrepararAsync(nameof(LosCamposDelMovimientoCoincidenConLaFilaSembrada));
        var dia = new DateOnly(2026, 8, 1);

        var id = await SembrarMovimientoAsync(ctx, ctx.IdPuntoVenta, dia, 500m, 120m, 20m, 600m, "Cierre de turno de prueba");

        var libro = await ListarAsync(ctx.Admin, ctx.IdEmpresa, ctx.IdPuntoVenta);

        Assert.NotNull(libro);
        var fila = Assert.Single(libro!.Items, m => m.Id == id);
        Assert.Equal(ctx.IdEmpresa, fila.IdEmpresa);
        Assert.Equal(ctx.IdPuntoVenta, fila.IdPuntoVenta);
        Assert.Equal(TipoMovimientoTesoreria.RetiroCaja, fila.Tipo);
        Assert.Equal("Cierre de turno de prueba", fila.Concepto);
        Assert.Null(fila.IdGasto);
        Assert.Null(fila.GastoCategoria);
        Assert.Null(fila.GastoConcepto);
        Assert.Equal(500m, fila.Inicio);
        Assert.Equal(120m, fila.Ingreso);
        Assert.Equal(20m, fila.Egreso);
        Assert.Equal(600m, fila.Final);
        Assert.Equal(ctx.IdEmpleadoAdmin, fila.IdEmpleado);
    }

    // ---- dangling-fk-read-models: punto de venta null / dado de baja lógica -------------------

    /// <summary>Una fila sin punto de venta (gasto de administración sin PV, spec owner's use case
    /// 2) trae <c>idPuntoVenta</c>/<c>nombrePuntoVenta</c> ambos <c>null</c> — nunca rompe la
    /// fila.</summary>
    [Fact]
    public async Task UnaFilaSinPuntoDeVentaTraeNombreNulo()
    {
        var ctx = await PrepararAsync(nameof(UnaFilaSinPuntoDeVentaTraeNombreNulo));

        var id = await SembrarMovimientoAsync(ctx, null, new DateOnly(2026, 8, 1), 0m, 50m, 0m, 50m);

        var libro = await ListarAsync(ctx.Admin, ctx.IdEmpresa);

        Assert.NotNull(libro);
        var fila = Assert.Single(libro!.Items, m => m.Id == id);
        Assert.Null(fila.IdPuntoVenta);
        Assert.Null(fila.NombrePuntoVenta);
    }

    /// <summary>dangling-fk-read-models: un punto de venta dado de baja lógica DESPUÉS de que la
    /// fila fue escrita deja <c>nombrePuntoVenta</c> en <c>null</c> (mismo criterio que
    /// <c>nombreProveedor</c>/<c>nombreArea</c> en <c>GastoDeAdministracionListado</c>), pero la
    /// fila sigue apareciendo con su <c>idPuntoVenta</c> intacto — nunca desaparece ni rompe el
    /// listado.</summary>
    [Fact]
    public async Task UnPuntoDeVentaDadoDeBajaLogicaDejaElNombreNuloSinPerderLaFila()
    {
        var ctx = await PrepararAsync(nameof(UnPuntoDeVentaDadoDeBajaLogicaDejaElNombreNuloSinPerderLaFila));
        var otroPuntoVenta = await SembrarPuntoVentaAsync(ctx, "PV a dar de baja");
        var id = await SembrarMovimientoAsync(ctx, otroPuntoVenta, new DateOnly(2026, 8, 1), 0m, 25m, 0m, 25m);

        await SoftDeletePuntoVentaAsync(ctx, otroPuntoVenta);

        var libro = await ListarAsync(ctx.Admin, ctx.IdEmpresa);

        Assert.NotNull(libro);
        var fila = Assert.Single(libro!.Items, m => m.Id == id);
        Assert.Equal(otroPuntoVenta, fila.IdPuntoVenta);
        Assert.Null(fila.NombrePuntoVenta);
    }

    // ---- gasto de origen tesorería: la fila trae categoría/concepto del gasto ------------------

    /// <summary>Un gasto de administración (PR3, sin turno, pagado de la tesorería de la empresa)
    /// escribe su propia fila `Gasto` en la cadena — el libro tiene que poder mostrarla sin una
    /// segunda consulta: <c>idGasto</c>/<c>gastoCategoria</c>/<c>gastoConcepto</c> resueltos.</summary>
    [Fact]
    public async Task UnaFilaDeGastoDeAdministracionTraeCategoriaYConceptoDelGasto()
    {
        var ctx = await PrepararAsync(nameof(UnaFilaDeGastoDeAdministracionTraeCategoriaYConceptoDelGasto));

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == Ways.Domain.Catalogos.ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();

        var solicitud = new SolicitudDeGastoDeAdministracion(
            new DateOnly(2026, 8, 1), ctx.IdEmpresa, null, CategoriaGasto.Otros, null, null,
            "Alquiler de depósito", null, idMedioEfectivo, null, 15000m);
        var alta = await ctx.Admin.PostAsJsonAsync("/api/gastos/administracion", solicitud);
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var libro = await ListarAsync(ctx.Admin, ctx.IdEmpresa);

        Assert.NotNull(libro);
        var fila = Assert.Single(libro!.Items, m => m.Tipo == TipoMovimientoTesoreria.Gasto);
        Assert.NotNull(fila.IdGasto);
        Assert.Null(fila.IdPuntoVenta);
        Assert.Equal(CategoriaGasto.Otros, fila.GastoCategoria);
        Assert.Equal("Alquiler de depósito", fila.GastoConcepto);
        Assert.Equal(15000m, fila.Egreso);
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

        var libro = await ListarAsync(ctx.Admin, ctx.IdEmpresa, ctx.IdPuntoVenta);

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

        var respuesta = await ctx.Vendedor.GetAsync($"/api/reportes/tesoreria?idEmpresa={ctx.IdEmpresa}");

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnSupervisorLeeElLibroDeTesoreria()
    {
        var ctx = await PrepararAsync(nameof(UnSupervisorLeeElLibroDeTesoreria));

        var respuesta = await ctx.Supervisor.GetAsync($"/api/reportes/tesoreria?idEmpresa={ctx.IdEmpresa}");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    // ---- mutation-proof-tests: filtro por idEmpresa (nuevo en PR5) -----------------------------

    /// <summary>mutation-proof-tests — la cláusula bajo prueba es
    /// <c>Where(m => m.IdEmpresa == idEmpresa)</c> en <see cref="ServicioDeTesoreria.ConstruirQuery"/>
    /// (PR5: <c>idEmpresa</c> pasó de filtro opcional a ancla obligatoria de la cadena). Dos
    /// empresas del MISMO tenant, cada una con su propio movimiento — el libro de una NUNCA puede
    /// traer la fila de la otra. Mutación aplicada (reemplazar el <c>Where(m => m.IdEmpresa ==
    /// idEmpresa)</c> por <c>AsQueryable()</c> en <c>ConstruirQuery</c>): esta prueba pasó de
    /// FALLAR (el movimiento de la otra empresa del mismo tenant aparece en el libro) a pasar al
    /// revertir — evidencia registrada en el cuerpo del commit de esta PR.</summary>
    [Fact]
    public async Task UnMovimientoDeOtraEmpresaDelMismoTenantNuncaApareceEnElLibro()
    {
        var ctx = await PrepararAsync(nameof(UnMovimientoDeOtraEmpresaDelMismoTenantNuncaApareceEnElLibro));
        var (idOtraEmpresa, idPuntoVentaDeOtraEmpresa) =
            await SembrarOtraEmpresaDelMismoTenantAsync(ctx, "Otra empresa");
        var dia = new DateOnly(2026, 8, 1);

        var idPrincipal = await SembrarMovimientoAsync(ctx, ctx.IdPuntoVenta, dia, 0m, 60m, 0m, 60m);
        var idDeOtraEmpresa = await SembrarMovimientoAsync(
            ctx, idPuntoVentaDeOtraEmpresa, dia, 0m, 999m, 0m, 999m, idEmpresa: idOtraEmpresa);

        var libro = await ListarAsync(ctx.Admin, ctx.IdEmpresa);

        Assert.NotNull(libro);
        Assert.Contains(libro!.Items, m => m.Id == idPrincipal);
        Assert.DoesNotContain(libro.Items, m => m.Id == idDeOtraEmpresa);
    }

    // ---- orden en el servidor: GET /api/reportes/tesoreria?orden=&direccion= --------------------------

    private async Task<int> SembrarMovimientoConAsync(
        Contexto ctx, TipoMovimientoTesoreria tipo, string concepto, int? idPuntoVenta, int idEmpleado, int diaDeAgosto,
        decimal inicio, decimal ingreso, decimal egreso)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        var movimiento = new MovimientoTesoreria
        {
            IdTenant = ctx.IdTenant,
            IdEmpresa = ctx.IdEmpresa,
            IdPuntoVenta = idPuntoVenta,
            Fecha = new DateTimeOffset(2026, 8, diaDeAgosto, 12, 0, 0, TimeSpan.Zero),
            Tipo = tipo,
            Concepto = concepto,
            Inicio = inicio,
            Ingreso = ingreso,
            Egreso = egreso,
            Final = inicio + ingreso - egreso,
            IdEmpleado = idEmpleado
        };
        db.MovimientosTesoreria.Add(movimiento);
        await db.SaveChangesAsync();

        return movimiento.Id;
    }

    /// <summary>Los tres usuarios del contexto por rol, de menor a mayor id (admin, supervisor, vendedor).</summary>
    private async Task<(int Admin, int Supervisor, int Vendedor)> EmpleadosPorIdAsync(Contexto ctx)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var supervisor = await db.Usuarios.Where(u => u.RolId == (int)RolConocido.Supervisor).Select(u => u.Id).SingleAsync();
        var vendedor = await db.Usuarios.Where(u => u.RolId == (int)RolConocido.Vendedor).Select(u => u.Id).SingleAsync();
        return (ctx.IdEmpleadoAdmin, supervisor, vendedor);
    }

    /// <summary>Cinco movimientos (posiciones 1 a 5, en orden de alta, es decir de id), con cada columna en un orden
    /// distinto del de id, empates (el desempate por id se ve) y un punto de venta dado de baja que ordena como vacío.
    /// <code>
    /// pos tipo        concepto inicio ingreso egreso final pv              empleado fecha
    ///  1  RetiroCaja  c3       40     10      0      50    Local 1         vendedor 3
    ///  2  Deposito    c1       20     0       5      15    PV secundario   admin    5
    ///  3  Gasto       c5       30     30      10     50    -               superv.  1
    ///  4  Ajuste      c2       5      5       0      10    Local 1         admin    4
    ///  5  RetiroCaja  c4       60     0       20     40    (baja)          superv.  2
    /// </code></summary>
    private async Task<int[]> SembrarCincoMovimientosAsync(Contexto ctx)
    {
        var (admin, supervisor, vendedor) = await EmpleadosPorIdAsync(ctx);
        var pvSecundario = await SembrarPuntoVentaAsync(ctx, "PV secundario");
        var pvDeBaja = await SembrarPuntoVentaAsync(ctx, "PV a dar de baja");

        var ids = new[]
        {
            await SembrarMovimientoConAsync(ctx, TipoMovimientoTesoreria.RetiroCaja, "c3", ctx.IdPuntoVenta, vendedor, 3, 40m, 10m, 0m),
            await SembrarMovimientoConAsync(ctx, TipoMovimientoTesoreria.Deposito, "c1", pvSecundario, admin, 5, 20m, 0m, 5m),
            await SembrarMovimientoConAsync(ctx, TipoMovimientoTesoreria.Gasto, "c5", null, supervisor, 1, 30m, 30m, 10m),
            await SembrarMovimientoConAsync(ctx, TipoMovimientoTesoreria.Ajuste, "c2", ctx.IdPuntoVenta, admin, 4, 5m, 5m, 0m),
            await SembrarMovimientoConAsync(ctx, TipoMovimientoTesoreria.RetiroCaja, "c4", pvDeBaja, supervisor, 2, 60m, 0m, 20m),
        };

        await SoftDeletePuntoVentaAsync(ctx, pvDeBaja);

        return ids;
    }

    private static async Task<List<int>> RecorrerOrdenadoAsync(HttpClient cliente, int idEmpresa, string orden, int tamanio = 2)
    {
        var ids = new List<int>();
        for (var pagina = 1; pagina <= 10; pagina++)
        {
            var respuesta = await cliente.GetAsync(
                $"/api/reportes/tesoreria?{ConstruirQuery(idEmpresa, null, null, null)}&{orden}&pagina={pagina}&tamanio={tamanio}");
            var cuerpo = await respuesta.Content.ReadAsStringAsync();
            Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
            var p = JsonSerializer.Deserialize<PaginaDeMovimientosTesoreria>(cuerpo, OpcionesJson)!;
            ids.AddRange(p.Items.Select(m => m.Id));
            if (pagina * tamanio >= p.Total) break;
        }

        return ids;
    }

    private static List<int> PorPosicion(int[] ids, string posiciones) =>
        [.. posiciones.Split(',').Select(p => ids[int.Parse(p) - 1])];

    /// <summary>Cláusula bajo prueba: el <c>ORDER BY</c> aplicado ANTES de <c>Skip/Take</c> en
    /// <c>ServicioDeTesoreria.ListarAsync</c>. Cada caso recorre las páginas de a dos filas y compara la secuencia
    /// completa: un orden aplicado después de paginar (o ausente) la rompe. Un punto de venta ausente o dado de
    /// baja va al final en las dos direcciones y las filas empatadas salen por id ascendente.</summary>
    [Theory]
    [InlineData("tipo", "ascendente", "4,2,3,1,5")]
    [InlineData("tipo", "descendente", "1,5,3,2,4")]
    [InlineData("inicio", "ascendente", "4,2,3,1,5")]
    [InlineData("inicio", "descendente", "5,1,3,2,4")]
    [InlineData("ingreso", "ascendente", "2,5,4,1,3")]
    [InlineData("ingreso", "descendente", "3,1,4,2,5")]
    [InlineData("egreso", "ascendente", "1,4,2,3,5")]
    [InlineData("egreso", "descendente", "5,3,2,1,4")]
    [InlineData("final", "ascendente", "4,2,5,1,3")]
    [InlineData("final", "descendente", "1,3,5,2,4")]
    [InlineData("concepto", "ascendente", "2,4,1,5,3")]
    [InlineData("concepto", "descendente", "3,5,1,4,2")]
    [InlineData("puntoVenta", "ascendente", "1,4,2,3,5")]
    [InlineData("puntoVenta", "descendente", "2,1,4,3,5")]
    [InlineData("empleado", "ascendente", "2,4,3,5,1")]
    [InlineData("empleado", "descendente", "1,3,5,2,4")]
    [InlineData("fecha", "ascendente", "3,5,1,4,2")]
    [InlineData("fecha", "descendente", "2,4,1,5,3")]
    public async Task CadaColumnaOrdenaTodoElLibroAtravesDeLasPaginas(string orden, string direccion, string esperadoPorPosicion)
    {
        var ctx = await PrepararAsync($"TesOrden{orden}{direccion}");
        var ids = await SembrarCincoMovimientosAsync(ctx);

        var obtenido = await RecorrerOrdenadoAsync(ctx.Admin, ctx.IdEmpresa, $"orden={orden}&direccion={direccion}");

        Assert.Equal(PorPosicion(ids, esperadoPorPosicion), obtenido);
    }

    /// <summary>El libro sigue en orden de cadena (id ascendente) mientras nadie pida otro orden, y cada fila
    /// conserva su inicio/final persistidos al reordenar: el orden es opt-in y no recalcula nada.</summary>
    [Fact]
    public async Task SinOrdenSigueElOrdenDeCadenaYAlReordenarCadaFilaConservaSuSaldo()
    {
        var ctx = await PrepararAsync(nameof(SinOrdenSigueElOrdenDeCadenaYAlReordenarCadaFilaConservaSuSaldo));
        var ids = await SembrarCincoMovimientosAsync(ctx);

        var sinOrden = await RecorrerOrdenadoAsync(ctx.Admin, ctx.IdEmpresa, "sinOrden=1");
        Assert.Equal(ids.ToList(), sinOrden);

        var respuesta = await ctx.Admin.GetAsync(
            $"/api/reportes/tesoreria?{ConstruirQuery(ctx.IdEmpresa, null, null, null)}&orden=fecha&direccion=ascendente&tamanio=10");
        var libro = JsonSerializer.Deserialize<PaginaDeMovimientosTesoreria>(await respuesta.Content.ReadAsStringAsync(), OpcionesJson)!;
        var primera = libro.Items[0];
        Assert.Equal(ids[2], primera.Id);
        Assert.Equal(30m, primera.Inicio);
        Assert.Equal(50m, primera.Final);
    }

    /// <summary>El concepto ordena por el texto de la celda: "Etiqueta de categoría — concepto" en las filas de gasto
    /// (con el concepto del gasto, que puede editarse después del movimiento) y el concepto propio en el resto. El
    /// orden por etiqueta difiere del orden por concepto a secas: "Impuestos — zz…" va antes que "Sueldos — aa…".
    /// Mutación: ordenar por <c>GastoConcepto ?? M.Concepto</c> deja el gasto de Sueldos antes que el de Impuestos.
    /// Lista espejo de etiquetas: <c>EtiquetasDeCategoriaDeGastoTests</c> (backend) y <c>tipos.test.ts</c> (web).</summary>
    [Theory]
    [InlineData("ascendente", "I,M,S")]
    [InlineData("descendente", "S,M,I")]
    public async Task ElConceptoOrdenaPorElTextoDeLaCeldaConLaEtiquetaDeCategoria(string direccion, string esperado)
    {
        var ctx = await PrepararAsync($"TesConcepto{direccion}");
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var idMedioEfectivo = await db.MediosPago
                .Where(m => m.Comportamiento == Ways.Domain.Catalogos.ComportamientoMedioPago.Efectivo)
                .Select(m => m.Id).FirstAsync();
            foreach (var (categoria, concepto) in new[] { (CategoriaGasto.Sueldos, "zz original"), (CategoriaGasto.Impuestos, "zz impuesto") })
            {
                var alta = await ctx.Admin.PostAsJsonAsync(
                    "/api/gastos/administracion",
                    new SolicitudDeGastoDeAdministracion(
                        new DateOnly(2026, 8, 1), ctx.IdEmpresa, null, categoria, null, null,
                        concepto, null, idMedioEfectivo, null, 15000m));
                Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
            }

            var gastoDeSueldos = await db.Gastos.SingleAsync(g => g.Categoria == CategoriaGasto.Sueldos);
            gastoDeSueldos.Concepto = "aa editado";
            await db.SaveChangesAsync();
        }

        var (admin, _, _) = await EmpleadosPorIdAsync(ctx);
        var idM = await SembrarMovimientoConAsync(ctx, TipoMovimientoTesoreria.RetiroCaja, "Mm uno", null, admin, 2, 0m, 5m, 0m);

        var respuesta = await ctx.Admin.GetAsync(
            $"/api/reportes/tesoreria?{ConstruirQuery(ctx.IdEmpresa, null, null, null)}&orden=concepto&direccion={direccion}&tamanio=10");
        var libro = JsonSerializer.Deserialize<PaginaDeMovimientosTesoreria>(await respuesta.Content.ReadAsStringAsync(), OpcionesJson)!;

        var filaDeSueldos = Assert.Single(libro.Items, m => m.GastoCategoria == CategoriaGasto.Sueldos);
        var filaDeImpuestos = Assert.Single(libro.Items, m => m.GastoCategoria == CategoriaGasto.Impuestos);
        Assert.Equal("aa editado", filaDeSueldos.GastoConcepto);
        var porClave = new Dictionary<string, int> { ["S"] = filaDeSueldos.Id, ["I"] = filaDeImpuestos.Id, ["M"] = idM };
        Assert.Equal(esperado.Split(',').Select(k => porClave[k]).ToList(), libro.Items.Select(m => m.Id).ToList());
    }

    [Theory]
    [InlineData("orden=acciones", "orden_invalido")]
    [InlineData("orden=Fecha", "orden_invalido")]
    [InlineData("orden=fecha&direccion=asc", "direccion_invalida")]
    [InlineData("direccion=descendente", "direccion_sin_orden")]
    public async Task UnOrdenODireccionFueraDeLaListaSeRechazaConCuatrocientos(string query, string codigoEsperado)
    {
        var ctx = await PrepararAsync($"TesRechazo{codigoEsperado}{query.Length}");

        var respuesta = await ctx.Admin.GetAsync($"/api/reportes/tesoreria?idEmpresa={ctx.IdEmpresa}&{query}");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(codigoEsperado, problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task ElSupervisorTambienOrdenaYElVendedorSigueRechazado()
    {
        var ctx = await PrepararAsync(nameof(ElSupervisorTambienOrdenaYElVendedorSigueRechazado));
        var ids = await SembrarCincoMovimientosAsync(ctx);

        var delSupervisor = await RecorrerOrdenadoAsync(ctx.Supervisor, ctx.IdEmpresa, "orden=fecha&direccion=ascendente");
        var delVendedor = await ctx.Vendedor.GetAsync(
            $"/api/reportes/tesoreria?idEmpresa={ctx.IdEmpresa}&orden=fecha&direccion=ascendente");

        Assert.Equal(PorPosicion(ids, "3,5,1,4,2"), delSupervisor);
        Assert.Equal(HttpStatusCode.Forbidden, delVendedor.StatusCode);
    }

    [Fact]
    public async Task ElOrdenNoMezclaMovimientosDeOtroTenantNiDeOtraEmpresa()
    {
        var propio = await PrepararAsync("TesAislamientoA");
        var ajeno = await PrepararAsync("TesAislamientoB");
        var (idOtraEmpresa, idPuntoVentaDeOtraEmpresa) = await SembrarOtraEmpresaDelMismoTenantAsync(propio, "Otra empresa");
        var idsPropios = await SembrarCincoMovimientosAsync(propio);
        var idsAjenos = await SembrarCincoMovimientosAsync(ajeno);
        var idDeOtraEmpresa = await SembrarMovimientoAsync(
            propio, idPuntoVentaDeOtraEmpresa, new DateOnly(2026, 8, 6), 0m, 999m, 0m, 999m, idEmpresa: idOtraEmpresa);

        var obtenido = await RecorrerOrdenadoAsync(propio.Admin, propio.IdEmpresa, "orden=fecha&direccion=descendente");

        Assert.Equal(PorPosicion(idsPropios, "2,4,1,5,3"), obtenido);
        Assert.Empty(obtenido.Intersect(idsAjenos));
        Assert.DoesNotContain(idDeOtraEmpresa, obtenido);
    }
}
