using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Ways.Api.ConectorMcp;
using Ways.Api.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// <see cref="PermisoDelConector"/> sin host: la rama sin request, con un <see cref="IAuthorizationService"/> que
/// aprueba todo para que solo esa rama pueda negar.
/// </summary>
public class ConectorMcpPermisoDelConectorTests
{
    private sealed class AutorizacionQueApruebaTodo : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Success());

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(AuthorizationResult.Success());
    }

    private static readonly IAuthorizationService Autorizacion = new AutorizacionQueApruebaTodo();

    /// <summary>Cláusula bajo prueba: el <c>HttpContext</c> nulo niega aunque la política aprobaría.</summary>
    [Fact]
    public async Task SinRequestNingunaPoliticaSeCumple()
    {
        var accesor = new HttpContextAccessor { HttpContext = null };

        Assert.False(await PermisoDelConector.CumpleAsync(Autorizacion, accesor, Politicas.GestionDeCatalogo));
        var error = await Assert.ThrowsAsync<ErrorDeHerramienta>(() => PermisoDelConector.ExigirAsync(
            Autorizacion, accesor, Politicas.GestionDeCatalogo, PermisoDelConector.MensajeSinPermisoDeCompras));
        Assert.Equal(PermisoDelConector.MensajeSinPermisoDeCompras, error.Message);
    }

    /// <summary>Control: con request, decide la política (acá, aprobar).</summary>
    [Fact]
    public async Task ConRequestDecideLaPolitica()
    {
        var accesor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };

        Assert.True(await PermisoDelConector.CumpleAsync(Autorizacion, accesor, Politicas.GestionDeCatalogo));
        await PermisoDelConector.ExigirAsync(
            Autorizacion, accesor, Politicas.GestionDeCatalogo, PermisoDelConector.MensajeSinPermisoDeCompras);
    }
}
