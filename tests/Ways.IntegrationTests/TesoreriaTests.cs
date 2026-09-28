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
}
