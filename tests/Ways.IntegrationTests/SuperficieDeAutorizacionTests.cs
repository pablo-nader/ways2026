using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Ways.Api.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Stage-5-pos-ventas, Slice 1 (task 1.7, design: Authorization Surface — omission guard,
/// mandatory). ASP.NET Core compone metadata de autorización con AND (design decisión 6): un
/// endpoint de escritura bajo un grupo relajado a <see cref="Politicas.OperacionDePos"/> queda
/// abierto a Vendedor si alguien olvida apilar <see cref="Politicas.GestionDeCatalogo"/>. Esta
/// prueba camina el <see cref="EndpointDataSource"/> real (no una lista mantenida a mano de
/// rutas) y falla la build ante ese olvido, en vez de depender de la disciplina del siguiente
/// PR.
///
/// El allowlist cubre dos familias, ambas explícitas y comentadas: (a) las cuatro rutas que el
/// design nombra por su cuenta — el carryover de <c>/api/ofertas/resolver</c> (etapa 4) y el
/// contrato de checkout/anulación que Slice 4/5 todavía no aterrizan (quedan acá ya, para que
/// esta prueba no requiera edición cuando aparezcan); (b) los endpoints de escritura
/// preexistentes que NUNCA estuvieron bajo <c>GestionDeCatalogo</c> porque viven en una
/// superficie administrativa distinta y más estricta (usuarios, organización, aprovisionamiento
/// de plataforma, login) — ninguno de esos grupos admite Vendedor, así que no son el riesgo que
/// esta prueba vigila, pero tienen que declararse a propósito para que el chequeo sea honesto
/// sobre TODO el <see cref="EndpointDataSource"/>, no solo sobre los cinco grupos que este slice
/// re-gateó.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class SuperficieDeAutorizacionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private static readonly HashSet<(string Metodo, string Ruta)> Allowlist =
    [
        // Los cuatro que el design nombra explícitamente.
        ("POST", "/api/auth/login"),
        ("POST", "/api/auth/logout"),
        ("POST", "/api/ofertas/resolver"),
        // Slice 4 (task 4.6): MapGroup("/api/ventas").MapPost("/", ...) — el RoutePattern.RawText
        // real lleva la barra final (mismo shape que "/api/plataforma/tenants/"/"/api/usuarios/"
        // más abajo), a diferencia del literal sin barra que este allowlist traía adelantado
        // desde Slice 1.
        ("POST", "/api/ventas/"),
        // stage-5-pos-ventas (Slice 5, task 5.2): el RawText real lleva la restricción ":int"
        // (mismo criterio que "/api/empresas/{id:int}"/"/api/puntos-venta/{id:int}" más abajo,
        // y que el propio GET "/api/ventas/{id:int}" del mismo grupo) — corrige el literal sin
        // restricción que este allowlist traía adelantado desde Slice 1.
        ("POST", "/api/ventas/{id:int}/anulacion"),

        // Aprovisionamiento y administración de tenants — SoloPlataforma, root-only, jamás
        // admite Vendedor (Politicas.cs).
        ("POST", "/api/plataforma/tenants/"),
        ("PUT", "/api/plataforma/tenants/{id:int}"),
        ("POST", "/api/plataforma/tenants/{id:int}/suspender"),
        ("POST", "/api/plataforma/tenants/{id:int}/reactivar"),

        // Organización (empresas/puntos de venta) — GestionDeOrganizacion (Root + Admin, sin
        // Vendedor).
        ("PUT", "/api/empresas/{id:int}"),
        ("PUT", "/api/puntos-venta/{id:int}"),

        // ABM de usuarios — GestionDeUsuarios (Root + Admin, sin Vendedor).
        ("POST", "/api/usuarios/"),
        ("PUT", "/api/usuarios/{id:int}"),
        ("POST", "/api/usuarios/{id:int}/password"),
        ("POST", "/api/usuarios/{id:int}/desbloquear"),
        ("DELETE", "/api/usuarios/{id:int}")
    ];

    [Fact]
    public void TodoEndpointNoGetFueraDelAllowlistApilaGestionDeCatalogo()
    {
        var fuente = fixture.Services.GetRequiredService<EndpointDataSource>();

        var faltantes = new List<string>();

        foreach (var endpoint in fuente.Endpoints)
        {
            if (endpoint is not RouteEndpoint ruta)
            {
                continue;
            }

            var metodos = ruta.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            if (metodos is null || metodos.Contains("GET"))
            {
                // Sin restricción de método (MapFallback, MapOpenApi) o GET: fuera del alcance
                // del guard — la superficie de lectura es justamente lo que este slice abrió.
                continue;
            }

            var patron = ruta.RoutePattern.RawText ?? string.Empty;
            var metodo = metodos.Single();

            if (Allowlist.Contains((metodo, patron)))
            {
                continue;
            }

            var apilaGestionDeCatalogo = ruta.Metadata
                .GetOrderedMetadata<IAuthorizeData>()
                .Any(dato => dato.Policy == Politicas.GestionDeCatalogo);

            if (!apilaGestionDeCatalogo)
            {
                faltantes.Add($"{metodo} {patron}");
            }
        }

        Assert.True(
            faltantes.Count == 0,
            $"Endpoint(s) de escritura sin GestionDeCatalogo y fuera del allowlist: {string.Join(", ", faltantes)}");
    }

    /// <summary>
    /// WARNING real (judgment-day, Judge A): el guard de arriba salta explícitamente todo
    /// endpoint GET (<c>metodos.Contains("GET")</c>) — quedaba ciego a un grupo GET sin
    /// <c>RequireAuthorization</c> (el caso real: <c>/api/catalogos-fiscales</c>, que caía al
    /// fallback autenticado-only). Este segundo guard cubre justo ese punto ciego: camina el
    /// mismo <see cref="EndpointDataSource"/> real y falla-cerrado sobre las superficies de
    /// lectura que este slice re-gateó a <see cref="Politicas.OperacionDePos"/> — cualquier GET
    /// nuevo bajo esos prefijos que no apile una policy al menos tan estricta como
    /// <see cref="Politicas.OperacionDePos"/> (o quede en el fallback autenticado-only, sin
    /// policy nombrada) rompe la build.
    /// </summary>
    private static readonly string[] PrefijosDeLecturaReGateados =
    [
        "/api/articulos",
        "/api/clientes",
        "/api/listas-precio",
        "/api/catalogos/",
        "/api/catalogos-fiscales",
        "/api/parametros",
        "/api/ofertas",
        // GET /api/puntos-venta (listado) — re-gateado a Politicas.LecturaDePuntosVenta para
        // habilitar el selector de PV del POS (Vendedor/Supervisor) sin sacarle el acceso a
        // Root/Admin (PuntosVenta.tsx). GET /{id:int} sigue bajo GestionDeOrganizacion, ya
        // cubierto por el allowlist de policies de abajo.
        "/api/puntos-venta",
        // stage-5-pos-ventas (Slice 5, task 5.4): GET /api/stock — balance del badge del POS,
        // spec: stock / Stock Read Access Under OperacionDePos.
        "/api/stock"
    ];

    // Policies que, de aparecer en vez de OperacionDePos, siguen siendo un gate válido —
    // ninguna relaja la superficie que este guard vigila a "autenticado sin rol". La única
    // excepción documentada es LecturaDePuntosVenta, que sí agrega Root frente a OperacionDePos
    // pero sigue exigiendo un rol conocido (nunca cae al fallback autenticado-only).
    private static readonly HashSet<string> PoliticasAlMenosTanEstrictasComoOperacionDePos =
    [
        Politicas.OperacionDePos,
        Politicas.GestionDeCatalogo,
        Politicas.GestionDeUsuarios,
        Politicas.GestionDeOrganizacion,
        Politicas.SoloPlataforma,
        Politicas.LecturaDePuntosVenta
    ];

    [Fact]
    public void TodoEndpointGetBajoLasSuperficiesReGateadasApilaOperacionDePos()
    {
        var fuente = fixture.Services.GetRequiredService<EndpointDataSource>();

        var faltantes = new List<string>();

        foreach (var endpoint in fuente.Endpoints)
        {
            if (endpoint is not RouteEndpoint ruta)
            {
                continue;
            }

            var metodos = ruta.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            if (metodos is null || !metodos.Contains("GET"))
            {
                continue;
            }

            var patron = ruta.RoutePattern.RawText ?? string.Empty;
            if (!PrefijosDeLecturaReGateados.Any(prefijo => patron.StartsWith(prefijo, StringComparison.Ordinal)))
            {
                continue;
            }

            var tienePoliticaValida = ruta.Metadata
                .GetOrderedMetadata<IAuthorizeData>()
                .Any(dato => dato.Policy is not null && PoliticasAlMenosTanEstrictasComoOperacionDePos.Contains(dato.Policy));

            if (!tienePoliticaValida)
            {
                faltantes.Add($"GET {patron}");
            }
        }

        Assert.True(
            faltantes.Count == 0,
            $"Endpoint(s) GET sin OperacionDePos (o una policy más estricta) bajo las superficies re-gateadas: {string.Join(", ", faltantes)}");
    }
}
