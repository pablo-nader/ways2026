using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Ways.Api.Seguridad;
using Ways.Domain.Usuarios;

namespace Ways.IntegrationTests;

/// <summary>
/// Matriz de claims a nivel policy, independiente de cualquier endpoint (stage-10, task 1.7):
/// construye el mismo <see cref="AuthorizationBuilder"/> que <c>Program.cs</c> registra vía
/// <see cref="Politicas.AgregarPoliticasWays"/> y evalúa <see cref="IAuthorizationService"/>
/// contra un <see cref="ClaimsPrincipal"/> por rol, sin levantar la app ni Docker.
/// </summary>
public class PoliticasTests
{
    private static IAuthorizationService ConstruirServicioDeAutorizacion()
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddAuthorizationBuilder().AgregarPoliticasWays();
        return servicios.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static ClaimsPrincipal Usuario(RolConocido rol)
    {
        var identidad = new ClaimsIdentity(
            [new Claim(ClaimsWays.RolId, ((int)rol).ToString())], "test");
        return new ClaimsPrincipal(identidad);
    }

    [Theory]
    [InlineData(RolConocido.Vendedor, false)]
    [InlineData(RolConocido.Root, false)]
    [InlineData(RolConocido.Supervisor, true)]
    [InlineData(RolConocido.Admin, true)]
    public async Task LecturaDeReportesAdmiteSupervisorYAdminUnicamente(RolConocido rol, bool admitido)
    {
        var servicio = ConstruirServicioDeAutorizacion();

        var resultado = await servicio.AuthorizeAsync(Usuario(rol), Politicas.LecturaDeReportes);

        Assert.Equal(admitido, resultado.Succeeded);
    }

    [Theory]
    [InlineData(RolConocido.Vendedor, false)]
    [InlineData(RolConocido.Supervisor, false)]
    [InlineData(RolConocido.Root, false)]
    [InlineData(RolConocido.Admin, true)]
    public async Task LecturaDeRentabilidadAdmiteSoloAdmin(RolConocido rol, bool admitido)
    {
        var servicio = ConstruirServicioDeAutorizacion();

        var resultado = await servicio.AuthorizeAsync(Usuario(rol), Politicas.LecturaDeRentabilidad);

        Assert.Equal(admitido, resultado.Succeeded);
    }

    /// <summary>Design decisión 7: ASP.NET Core compone metadata de autorización con AND, así
    /// que apilar ambas policies (como hacen <c>/rentabilidad</c> y <c>/comisiones</c>) tiene
    /// que dar Admin-only sin ningún mecanismo nuevo — lo prueba acá, a nivel policy, sin
    /// depender de que un endpoint concreto exista.</summary>
    [Theory]
    [InlineData(RolConocido.Vendedor, false)]
    [InlineData(RolConocido.Supervisor, false)]
    [InlineData(RolConocido.Root, false)]
    [InlineData(RolConocido.Admin, true)]
    public async Task ApilarLecturaDeReportesConLecturaDeRentabilidadEquivaleAAdminOnly(
        RolConocido rol, bool admitido)
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddAuthorizationBuilder().AgregarPoliticasWays();
        var proveedor = servicios.BuildServiceProvider();
        var opciones = proveedor.GetRequiredService<IAuthorizationPolicyProvider>();

        var reportes = await opciones.GetPolicyAsync(Politicas.LecturaDeReportes);
        var rentabilidad = await opciones.GetPolicyAsync(Politicas.LecturaDeRentabilidad);
        var apilada = AuthorizationPolicy.Combine(reportes!, rentabilidad!);

        var servicio = proveedor.GetRequiredService<IAuthorizationService>();
        var resultado = await servicio.AuthorizeAsync(Usuario(rol), apilada);

        Assert.Equal(admitido, resultado.Succeeded);
    }
}
