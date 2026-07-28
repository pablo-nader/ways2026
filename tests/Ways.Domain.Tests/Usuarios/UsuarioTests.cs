using Ways.Domain.Usuarios;

namespace Ways.Domain.Tests.Usuarios;

public class UsuarioTests
{
    private static readonly DateTimeOffset Ahora =
        new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    private static Usuario Nuevo() => new()
    {
        NombreUsuario = "vendedor1",
        Mail = "v1@ways.test",
        RolId = (int)RolConocido.Vendedor,
        PasswordHash = "hash",
        PasswordAlgoritmo = "test"
    };

    [Fact]
    public void UnUsuarioActivoSinBajaPuedeIniciarSesion()
    {
        Assert.True(Nuevo().PuedeIniciarSesion);
    }

    [Theory]
    [InlineData(EstadoUsuario.Inactivo)]
    [InlineData(EstadoUsuario.Bloqueado)]
    public void UnUsuarioQueNoEstaActivoNoPuedeIniciarSesion(EstadoUsuario estado)
    {
        var usuario = Nuevo();
        usuario.Estado = estado;

        Assert.False(usuario.PuedeIniciarSesion);
    }

    [Fact]
    public void UnUsuarioDadoDeBajaNoPuedeIniciarSesion()
    {
        var usuario = Nuevo();
        usuario.DeletedAt = Ahora;

        Assert.False(usuario.PuedeIniciarSesion);
        Assert.True(usuario.EstaEliminada);
    }

    [Fact]
    public void AlLlegarAlUmbralDeIntentosLaCuentaSeBloquea()
    {
        var usuario = Nuevo();
        var umbral = PoliticaDeRoles.UmbralBloqueoPorIntentosFallidos;

        for (var intento = 1; intento < umbral; intento++)
        {
            Assert.False(usuario.RegistrarIntentoFallido(Ahora, umbral));
            Assert.Equal(EstadoUsuario.Activo, usuario.Estado);
        }

        Assert.True(usuario.RegistrarIntentoFallido(Ahora, umbral));
        Assert.Equal(EstadoUsuario.Bloqueado, usuario.Estado);
        Assert.Equal(umbral, usuario.IntentosFallidos);
    }

    [Fact]
    public void UnIngresoExitosoLimpiaLosIntentosFallidos()
    {
        var usuario = Nuevo();
        usuario.RegistrarIntentoFallido(Ahora, 5);
        usuario.RegistrarIntentoFallido(Ahora, 5);

        usuario.RegistrarIngreso(Ahora);

        Assert.Equal(0, usuario.IntentosFallidos);
        Assert.Null(usuario.UltimoIntentoFallido);
        Assert.Equal(Ahora, usuario.UltimaConexion);
    }

    [Fact]
    public void DesbloquearReactivaYReiniciaElContador()
    {
        var usuario = Nuevo();
        for (var i = 0; i < 5; i++) usuario.RegistrarIntentoFallido(Ahora, 5);
        Assert.Equal(EstadoUsuario.Bloqueado, usuario.Estado);

        usuario.Desbloquear(Ahora);

        Assert.Equal(EstadoUsuario.Activo, usuario.Estado);
        Assert.Equal(0, usuario.IntentosFallidos);
    }

    [Fact]
    public void DesbloquearNoReactivaUnaCuentaInactiva()
    {
        var usuario = Nuevo();
        usuario.Estado = EstadoUsuario.Inactivo;

        usuario.Desbloquear(Ahora);

        Assert.Equal(EstadoUsuario.Inactivo, usuario.Estado);
    }
}
