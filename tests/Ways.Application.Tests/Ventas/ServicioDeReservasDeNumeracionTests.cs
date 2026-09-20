using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Ventas;
using Ways.Domain.Common;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Ventas;

/// <summary>
/// stage-pos-reserva-de-numeracion (judgment-day, GAP 2): aísla la guarda PROPIA de
/// <see cref="ServicioDeReservasDeNumeracion.ReservarAsync"/> (<c>contexto.IdDispositivo ?? throw
/// ... 403</c>) de la policy del endpoint (<see cref="Ways.Api.Seguridad.Politicas.RequiereDispositivo"/>,
/// <c>VentasEndpoints.cs</c>). Las dos leen la MISMA claim
/// (<c>ContextoDeUsuarioHttp.IdDispositivo</c> / <c>ClaimsWays.IdDispositivo</c>) construida por el
/// MISMO login, así que ningún actor real puede hacerlas discrepar y
/// <c>ReservaDeNumeracionEndpointsTests.UnActorWebNoPuedeReservarUnBloque</c> no puede aislarlas a
/// nivel HTTP (su propio doc-comment lo documenta). Este test llama al servicio DIRECTO, sin
/// pasar por ASP.NET Core ni por su pipeline de autorización — si la guarda cayera, dejaría de
/// verse "prohibido"/403 acá adentro, sin que la policy del endpoint (que no existe en este
/// camino) pueda enmascararlo. La policy del endpoint se prueba aparte, de forma estructural, en
/// <c>SuperficieDeAutorizacionTests.CadaRutaConPolicyAdicionalSobreSuGrupoLaApila</c>
/// (Ways.IntegrationTests) — ningún test de ESTE archivo la ejercita.
/// </summary>
public class ServicioDeReservasDeNumeracionTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private sealed class ContextoFijo(int? idTenant, int? idDispositivo) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => 1;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Vendedor;
        public int? IdTenant { get; } = idTenant;
        public int? IdDispositivo { get; } = idDispositivo;
    }

    /// <summary>Mutación: reemplazar <c>contexto.IdDispositivo ?? throw new ErrorDominio(
    /// "prohibido", ..., 403)</c> por <c>contexto.IdDispositivo ?? 0</c> (sigue compilando, ya no
    /// tira). Con la guarda viva, el método tira ANTES de mirar <c>Cantidad</c>; con la guarda
    /// caída, el flujo sigue hasta el siguiente chequeo (<c>Cantidad &lt; 1</c>) y tira
    /// "cantidad_invalida"/400 en su lugar — la aserción sobre "prohibido"/403 falla. La
    /// <c>Cantidad</c> de la solicitud es 0 A PROPÓSITO (inválida también) para que la mutación
    /// quede aislada sin depender de que el proveedor InMemory soporte lo que venga después
    /// (no hace falta sembrar ningún punto de venta: ninguna de las dos ramas llega a leer la
    /// base). Confirmado — RED al mutar, GREEN al revertir.</summary>
    [Fact]
    public async Task ReservarSinClaimDeDispositivoEsProhibido()
    {
        await using var db = new WaysDbContext(
            new DbContextOptionsBuilder<WaysDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options,
            new TenantActualFijo(ModoDeAcceso.Tenant, 1));

        var contexto = new ContextoFijo(idTenant: 1, idDispositivo: null);
        var servicio = new ServicioDeReservasDeNumeracion(db, contexto, new RelojFijo(Ahora));

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.ReservarAsync(new SolicitudDeReservaDeNumeracion(1, "TX", 0)));

        Assert.Equal("prohibido", error.Codigo);
        Assert.Equal(403, error.EstadoHttp);
    }
}
