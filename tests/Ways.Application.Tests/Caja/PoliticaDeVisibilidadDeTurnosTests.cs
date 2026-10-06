using Ways.Application.Caja;
using Ways.Domain.Caja;
using Ways.Domain.Usuarios;

namespace Ways.Application.Tests.Caja;

/// <summary>Regla de visibilidad de turnos sin base de datos: el predicado se compila y se evalúa
/// contra turnos en memoria.</summary>
public class PoliticaDeVisibilidadDeTurnosTests
{
    private const int Yo = 10;
    private const int Otro = 20;
    private const int PuntoVentaDelDispositivo = 5;
    private const int OtroPuntoVenta = 6;
    private const int PuntoVentaWeb = 7;
    private static readonly int[] PuntosVentaWeb = [PuntoVentaWeb];

    private static TurnoCaja Turno(
        int apertura, int? cierre, EstadoTurno estado, int puntoVenta = OtroPuntoVenta) => new()
    {
        IdEmpleadoApertura = apertura,
        IdEmpleadoCierre = cierre,
        Estado = estado,
        IdPuntoVenta = puntoVenta
    };

    private static bool Visible(RolConocido rol, int? puntoVentaDelDispositivo, TurnoCaja turno) =>
        PoliticaDeVisibilidadDeTurnos.Predicado(rol, Yo, puntoVentaDelDispositivo, PuntosVentaWeb).Compile()(turno);

    [Theory]
    [InlineData(RolConocido.Admin)]
    [InlineData(RolConocido.Supervisor)]
    public void AdminYSupervisorVenTurnosAjenos(RolConocido rol) =>
        Assert.True(Visible(rol, null, Turno(Otro, Otro, EstadoTurno.Cerrado)));

    [Fact]
    public void ElVendedorVeElTurnoQueAbrio() =>
        Assert.True(Visible(RolConocido.Vendedor, null, Turno(Yo, null, EstadoTurno.Abierto)));

    [Fact]
    public void ElVendedorVeElTurnoQueSoloCerro() =>
        Assert.True(Visible(RolConocido.Vendedor, null, Turno(Otro, Yo, EstadoTurno.Cerrado)));

    [Fact]
    public void ElVendedorNoVeUnTurnoCerradoAjeno() =>
        Assert.False(Visible(RolConocido.Vendedor, null, Turno(Otro, Otro, EstadoTurno.Cerrado)));

    [Fact]
    public void ElVendedorWebNoVeElTurnoAbiertoAjeno() =>
        Assert.False(Visible(RolConocido.Vendedor, null, Turno(Otro, null, EstadoTurno.Abierto, PuntoVentaDelDispositivo)));

    [Fact]
    public void ElVendedorWebVeElTurnoAbiertoAjenoDeUnPuntoDeVentaWeb() =>
        Assert.True(Visible(RolConocido.Vendedor, null, Turno(Otro, null, EstadoTurno.Abierto, PuntoVentaWeb)));

    [Fact]
    public void ElVendedorWebNoVeUnTurnoCerradoAjenoDeUnPuntoDeVentaWeb() =>
        Assert.False(Visible(RolConocido.Vendedor, null, Turno(Otro, Otro, EstadoTurno.Cerrado, PuntoVentaWeb)));

    [Fact]
    public void ElVendedorDeDispositivoNoVeElTurnoAbiertoAjenoDeUnPuntoDeVentaWebSalvoQueSeaSuyo() =>
        Assert.False(Visible(
            RolConocido.Vendedor, PuntoVentaDelDispositivo, Turno(Otro, null, EstadoTurno.Abierto, PuntoVentaWeb)));

    [Fact]
    public void ElVendedorWebSinPuntosDeVentaWebSoloVeLosPropios() =>
        Assert.False(PoliticaDeVisibilidadDeTurnos
            .Predicado(RolConocido.Vendedor, Yo, null, [])
            .Compile()(Turno(Otro, null, EstadoTurno.Abierto, PuntoVentaWeb)));

    [Fact]
    public void ElVendedorDeDispositivoVeElTurnoAbiertoAjenoDeSuPuntoDeVenta() =>
        Assert.True(Visible(
            RolConocido.Vendedor, PuntoVentaDelDispositivo,
            Turno(Otro, null, EstadoTurno.Abierto, PuntoVentaDelDispositivo)));

    [Fact]
    public void ElVendedorDeDispositivoVeUnTurnoCerradoAjenoDeSuPuntoDeVenta() =>
        Assert.True(Visible(
            RolConocido.Vendedor, PuntoVentaDelDispositivo,
            Turno(Otro, Otro, EstadoTurno.Cerrado, PuntoVentaDelDispositivo)));

    [Fact]
    public void ElVendedorDeDispositivoNoVeUnTurnoCerradoAjenoDeOtroPuntoDeVenta() =>
        Assert.False(Visible(
            RolConocido.Vendedor, PuntoVentaDelDispositivo, Turno(Otro, Otro, EstadoTurno.Cerrado, OtroPuntoVenta)));

    [Fact]
    public void ElVendedorDeDispositivoVeElTurnoQueAbrioEnOtroPuntoDeVenta() =>
        Assert.True(Visible(
            RolConocido.Vendedor, PuntoVentaDelDispositivo, Turno(Yo, null, EstadoTurno.Abierto, OtroPuntoVenta)));

    [Fact]
    public void ElVendedorDeDispositivoVeElTurnoQueSoloCerroEnOtroPuntoDeVenta() =>
        Assert.True(Visible(
            RolConocido.Vendedor, PuntoVentaDelDispositivo, Turno(Otro, Yo, EstadoTurno.Cerrado, OtroPuntoVenta)));

    [Fact]
    public void ElVendedorDeDispositivoNoVeElTurnoAbiertoAjenoDeOtroPuntoDeVenta() =>
        Assert.False(Visible(
            RolConocido.Vendedor, PuntoVentaDelDispositivo, Turno(Otro, null, EstadoTurno.Abierto, OtroPuntoVenta)));

    [Fact]
    public void UnRolSinPermisoDeSupervisionNoSeTrataComoSupervisor() =>
        Assert.False(Visible(RolConocido.Root, null, Turno(Otro, Otro, EstadoTurno.Cerrado)));
}
