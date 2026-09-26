using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Pos;
using Ways.Domain.Common;
using Ways.Domain.Dispositivos;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Pos;

/// <summary>
/// Las guardas PROPIAS de <see cref="ServicioDeRendicionDeCola.RegistrarAsync"/>, aisladas de la
/// capa HTTP — mismo criterio exacto (y mismo motivo) que
/// <c>ServicioDeReservasDeNumeracionTests</c>: la falta de claim de dispositivo la rechazan TAMBIÉN
/// la policy del endpoint (<see cref="Ways.Api.Seguridad.Politicas.RequiereDispositivo"/>) y las dos
/// leen la MISMA claim, así que ningún actor real las hace discrepar y una prueba HTTP no puede
/// aislarlas. Acá el servicio se llama DIRECTO, sin pipeline de autorización: si la guarda cayera,
/// se vería, porque no hay ninguna otra capa en este camino. La policy del endpoint se prueba
/// aparte, de forma estructural, en
/// <c>SuperficieDeAutorizacionTests.CadaRutaConPolicyAdicionalSobreSuGrupoLaApila</c>.
/// </summary>
public class ServicioDeRendicionDeColaTests
{
    private const int IdTenant = 1;
    private static readonly DateTimeOffset Ahora = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private sealed class ContextoFijo(int? idTenant, int? idDispositivo) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => 1;
        public string NombreUsuario => "cajero-de-prueba";
        public RolConocido Rol => RolConocido.Vendedor;
        public int? IdTenant { get; } = idTenant;
        public int? IdDispositivo { get; } = idDispositivo;
    }

    private static WaysDbContext CrearContexto() =>
        new(
            new DbContextOptionsBuilder<WaysDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options,
            new TenantActualFijo(ModoDeAcceso.Tenant, IdTenant));

    /// <summary>Mutación corrida: reemplazar <c>contexto.IdDispositivo ?? throw new ErrorDominio(
    /// "prohibido", ..., 403)</c> por <c>contexto.IdDispositivo ?? 0</c> (sigue compilando). Con la
    /// guarda viva tira 403 ANTES de leer nada; con la guarda caída sigue hasta la lectura de
    /// <c>dispositivos</c>, no encuentra el id 0 y tira "Este dispositivo ya no está vigente" —
    /// mismo código y mismo estado, así que la aserción discriminante es el MENSAJE, no el
    /// 403.</summary>
    [Fact]
    public async Task RendirSinClaimDeDispositivoEsProhibido()
    {
        await using var db = CrearContexto();
        var servicio = new ServicioDeRendicionDeCola(
            db, new RelojFijo(Ahora), new ContextoFijo(IdTenant, idDispositivo: null));

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.RegistrarAsync(new SolicitudDeRendicionDeCola("TX", 10, 0)));

        Assert.Equal("prohibido", error.Codigo);
        Assert.Equal(403, error.EstadoHttp);
        Assert.Equal("Esta operación requiere un dispositivo autenticado.", error.Message);
    }

    /// <summary>Un dispositivo invisible (revocado por baja lógica, o un id que nunca existió) es
    /// 403, NUNCA 404: distinguir "no existe" de "revocado" le daría información a un bearer
    /// robado. Kill del <c>is null =&gt; 403</c>: cambiarlo por <c>ErrorDominio.NoEncontrado</c>
    /// pone en rojo la aserción del 403.</summary>
    [Fact]
    public async Task RendirConUnDispositivoInvisibleEsProhibidoYNoNoEncontrado()
    {
        await using var db = CrearContexto();
        var servicio = new ServicioDeRendicionDeCola(
            db, new RelojFijo(Ahora), new ContextoFijo(IdTenant, idDispositivo: 777));

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.RegistrarAsync(new SolicitudDeRendicionDeCola("TX", 10, 0)));

        Assert.Equal("prohibido", error.Codigo);
        Assert.Equal(403, error.EstadoHttp);
        Assert.Equal("Este dispositivo ya no está vigente.", error.Message);
    }

    /// <summary>La pre-validación que <c>db-error-backstops</c> exige sobre
    /// <c>pendientes</c> (input de cliente): sin ella el valor negativo llegaría a
    /// <c>ck_reservas_numeracion_pendientes_no_negativo</c> y saldría como 500. Corre ANTES de
    /// resolver punto de venta/tipo/bloque, así que este test no necesita sembrar nada más que el
    /// dispositivo — y ese orden es parte de lo que prueba: con la guarda borrada, el flujo sigue y
    /// falla con otra cosa (no existe el punto de venta), nunca con
    /// <c>pendientes_invalido</c>.</summary>
    [Fact]
    public async Task UnaCantidadDePendientesNegativaEsInvalida()
    {
        await using var db = CrearContexto();
        db.Dispositivos.Add(new Dispositivo
        {
            IdTenant = IdTenant,
            IdPuntoVenta = 5,
            Nombre = "Caja 1",
            TokenHash = new string('a', 64),
            IdUsuarioAlta = 1,
            CreatedAt = Ahora,
            UpdatedAt = Ahora
        });
        await db.SaveChangesAsync();

        var servicio = new ServicioDeRendicionDeCola(
            db, new RelojFijo(Ahora), new ContextoFijo(IdTenant, idDispositivo: 1));

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.RegistrarAsync(new SolicitudDeRendicionDeCola("TX", 10, -1)));

        Assert.Equal("pendientes_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    /// <summary>Borde inferior de la misma guarda: <c>0</c> es válido (un dispositivo con la cola
    /// vacía tiene que poder rendir limpio) — un mutante que cambie <c>&lt; 0</c> por <c>&lt;= 0</c>
    /// pone este test en rojo. Sigue hasta el chequeo siguiente, que es lo que se afirma.</summary>
    [Fact]
    public async Task CeroPendientesNoEsInvalido()
    {
        await using var db = CrearContexto();
        db.Dispositivos.Add(new Dispositivo
        {
            IdTenant = IdTenant,
            IdPuntoVenta = 5,
            Nombre = "Caja 1",
            TokenHash = new string('a', 64),
            IdUsuarioAlta = 1,
            CreatedAt = Ahora,
            UpdatedAt = Ahora
        });
        await db.SaveChangesAsync();

        var servicio = new ServicioDeRendicionDeCola(
            db, new RelojFijo(Ahora), new ContextoFijo(IdTenant, idDispositivo: 1));

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.RegistrarAsync(new SolicitudDeRendicionDeCola("TX", 10, 0)));

        Assert.NotEqual("pendientes_invalido", error.Codigo);
        Assert.Equal("no_encontrado", error.Codigo);
    }
}
