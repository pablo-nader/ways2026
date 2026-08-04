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
        ("POST", "/api/ventas"),
        ("POST", "/api/ventas/{id}/anulacion"),

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
}
