using System.Net;
using System.Net.Http.Json;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Usuarios;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-10-agregacion-dashboard, Slice 10 (task 10.6): la matriz de roles completa de las
/// rutas de <c>/api/reportes/*</c> (spec rentabilidad-y-comisiones / reportes-de-gestion) en un
/// único archivo dedicado — hasta esta slice cada endpoint probaba su propia matriz de roles por
/// separado (<c>ReportesVentasResumenTests</c>, <c>ReportesEgresosTests</c>,
/// <c>RentabilidadTests</c>); esto consolida las nueve originales en un solo lugar, parametrizado
/// sobre la lista de rutas (design: Testing Strategy, "Integration — role matrix"). La composición
/// de políticas en sí (AND de <c>LecturaDeReportes</c> + <c>LecturaDeRentabilidad</c>) ya está
/// probada a nivel unitario en <c>PoliticasTests</c> — acá solo se prueba el <em>wiring</em> del
/// grupo de endpoints completo. Judgment-day slice 7 juez B (ronda 1, WARNING autorizado): sumadas
/// las dos rutas de resumen de stock (<c>reposicion/resumen</c>, <c>vencimientos/resumen</c>) —
/// ninguna tenía cobertura de autorización, el gap de <c>vencimientos/resumen</c> era preexistente
/// (desde slice 6) y se cerró de paso, test-only.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ReportesAutorizacionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";

    private sealed record Contexto(
        int IdEmpresa, int IdPuntoVenta, HttpClient Admin, HttpClient Supervisor, HttpClient Vendedor, HttpClient Root);

    /// <summary>Las nueve rutas originales de <c>ReportesEndpoints.cs</c> — <c>rentabilidad</c> y
    /// <c>comisiones</c> son las dos que apilan <c>LecturaDeRentabilidad</c> (design decisión 7) —
    /// más las dos rutas de resumen de stock (<c>stock/reposicion/resumen</c>,
    /// <c>stock/vencimientos/resumen</c>, ambas <c>LecturaDeReportes</c> sola, sin rentabilidad),
    /// que no tenían test de autorización propio.</summary>
    public static readonly TheoryData<string> TodasLasRutas = new()
    {
        "ventas/resumen", "compras/por-proveedor", "gastos/resumen", "articulos/top",
        "ventas/por-punto-venta", "ventas/por-vendedor", "ventas/por-medio-pago",
        "rentabilidad", "comisiones",
        "stock/reposicion/resumen", "stock/vencimientos/resumen"
    };

    public static readonly TheoryData<string> RutasSinLecturaDeRentabilidad = new()
    {
        "ventas/resumen", "compras/por-proveedor", "gastos/resumen", "articulos/top",
        "ventas/por-punto-venta", "ventas/por-vendedor", "ventas/por-medio-pago"
    };

    public static readonly TheoryData<string> RutasConLecturaDeRentabilidad = new() { "rentabilidad", "comisiones" };

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        var root = fixture.CreateClient();
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

        return new Contexto(resultado.IdEmpresa, resultado.IdPuntoVenta, admin, supervisor, vendedor, root);
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

    /// <summary>`ventas/resumen`/`gastos/resumen` exigen `granularidad`; el resto no lo lee
    /// (dto-contract-honesty) — agregarlo de más no rompe nada, así que viaja siempre para
    /// simplificar la matriz. Las dos rutas de resumen de stock (<c>stock/*/resumen</c>) no leen
    /// ninguno de esos campos, solo <c>idPuntoVenta</c> (judgment-day slice 7 juez B, WARNING
    /// autorizado) — build separado para no mandarles query params que no existen en su firma.</summary>
    private static string RutaCon(string ruta, int idEmpresa, int idPuntoVenta, DateOnly hoy) =>
        ruta.StartsWith("stock/", StringComparison.Ordinal)
            ? $"/api/reportes/{ruta}?idPuntoVenta={idPuntoVenta}"
            : $"/api/reportes/{ruta}?idEmpresa={idEmpresa}&desde={hoy:yyyy-MM-dd}&hasta={hoy:yyyy-MM-dd}&granularidad=Dia";

    [Theory]
    [MemberData(nameof(TodasLasRutas))]
    public async Task UnVendedorEsRechazadoEnLasNueveRutas(string ruta)
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoEnLasNueveRutas) + ruta.Replace("/", "-"));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var respuesta = await ctx.Vendedor.GetAsync(RutaCon(ruta, ctx.IdEmpresa, ctx.IdPuntoVenta, hoy));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Theory]
    [MemberData(nameof(TodasLasRutas))]
    public async Task UnRootEsRechazadoEnLasNueveRutas(string ruta)
    {
        var ctx = await PrepararAsync(nameof(UnRootEsRechazadoEnLasNueveRutas) + ruta.Replace("/", "-"));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var respuesta = await ctx.Root.GetAsync(RutaCon(ruta, ctx.IdEmpresa, ctx.IdPuntoVenta, hoy));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Theory]
    [MemberData(nameof(TodasLasRutas))]
    public async Task UnAdminEsAceptadoEnLasNueveRutas(string ruta)
    {
        var ctx = await PrepararAsync(nameof(UnAdminEsAceptadoEnLasNueveRutas) + ruta.Replace("/", "-"));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var respuesta = await ctx.Admin.GetAsync(RutaCon(ruta, ctx.IdEmpresa, ctx.IdPuntoVenta, hoy));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    [Theory]
    [MemberData(nameof(RutasSinLecturaDeRentabilidad))]
    public async Task UnSupervisorEsAceptadoEnLasSieteRutasSinLecturaDeRentabilidad(string ruta)
    {
        var ctx = await PrepararAsync(nameof(UnSupervisorEsAceptadoEnLasSieteRutasSinLecturaDeRentabilidad) + ruta.Replace("/", "-"));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var respuesta = await ctx.Supervisor.GetAsync(RutaCon(ruta, ctx.IdEmpresa, ctx.IdPuntoVenta, hoy));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    /// <summary>La prueba distintiva de esta matriz: a diferencia de las otras siete,
    /// <c>rentabilidad</c> y <c>comisiones</c> apilan <c>LecturaDeRentabilidad</c> (admin-only)
    /// sobre <c>LecturaDeReportes</c> — <c>LecturaDeReportes</c> sola no alcanza (design decisión
    /// 7).</summary>
    [Theory]
    [MemberData(nameof(RutasConLecturaDeRentabilidad))]
    public async Task UnSupervisorEsRechazadoEnRentabilidadYComisiones(string ruta)
    {
        var ctx = await PrepararAsync(nameof(UnSupervisorEsRechazadoEnRentabilidadYComisiones) + ruta.Replace("/", "-"));
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var respuesta = await ctx.Supervisor.GetAsync(RutaCon(ruta, ctx.IdEmpresa, ctx.IdPuntoVenta, hoy));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }
}
