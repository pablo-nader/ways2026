using System.Reflection;
using Ways.Domain.Common;
using Ways.Domain.Fiscal;

namespace Ways.Application.Tests.Fiscal;

/// <summary>
/// stage-19a-slice3 (task 3.2, 3.16, design D4, target 47): la máquina de estados del CAE — pura,
/// sin base de datos, con las TRES respuestas de la spec comprobante-fiscal ("The CAE State
/// Machine Has Three Response States, Not Two"), <see cref="MaquinaDeEstadosCae.Decidir"/> (I2), y
/// la evidencia estructural de que <see cref="PermisoDeSolicitud"/> NO se puede construir fuera de
/// este archivo/ensamblado (D4 — anticipa target 69 de la slice 5, que reasegura esto mismo con un
/// caller real).
/// </summary>
public class MaquinaDeEstadosCaeTests
{
    [Theory]
    [InlineData(ResultadoFiscal.Aprobado, true)]
    [InlineData(ResultadoFiscal.AprobadoConObservaciones, true)]
    [InlineData(ResultadoFiscal.Rechazado, false)]
    [InlineData(ResultadoFiscal.Pendiente, false)]
    public void EsTerminalSoloParaLasDosAprobaciones(ResultadoFiscal resultado, bool esperadoTerminal)
    {
        Assert.Equal(esperadoTerminal, MaquinaDeEstadosCae.EsTerminal(resultado));
    }

    [Theory]
    [InlineData('A', false, ResultadoFiscal.Aprobado)]
    [InlineData('A', true, ResultadoFiscal.AprobadoConObservaciones)]
    [InlineData('R', false, ResultadoFiscal.Rechazado)]
    [InlineData('R', true, ResultadoFiscal.Rechazado)]
    public void MapearTraduceElResultadoCrudoDeArcaALosTresEstados(
        char resultadoArca, bool hayObservaciones, ResultadoFiscal esperado)
    {
        Assert.Equal(esperado, MaquinaDeEstadosCae.Mapear(resultadoArca, hayObservaciones));
    }

    [Fact]
    public void MapearRechazaUnResultadoNoReconocido()
    {
        var error = Assert.Throws<ErrorDominio>(() => MaquinaDeEstadosCae.Mapear('X', false));

        Assert.Equal("arca_resultado_no_reconocido", error.Codigo);
        Assert.Equal(502, error.EstadoHttp);
    }

    [Fact]
    public void DecidirEmiteDirectoSinIntentoPrevio()
    {
        Assert.Equal(DecisionDeReintento.EmitirDirecto, MaquinaDeEstadosCae.Decidir(EstadoDeIntento.SinIntentoPrevio));
    }

    [Fact]
    public void DecidirConsultaPrimeroTrasUnIntentoNoDefinitivo()
    {
        Assert.Equal(
            DecisionDeReintento.ConsultarPrimero, MaquinaDeEstadosCae.Decidir(EstadoDeIntento.NoDefinitivo));
    }

    [Fact]
    public void AutorizarSolicitudConstruyeElPermisoConLosDatosProvistos()
    {
        var permiso = MaquinaDeEstadosCae.AutorizarSolicitud(idComprobante: 42, numero: 105);

        Assert.Equal(42, permiso.IdComprobante);
        Assert.Equal(105, permiso.Numero);
    }

    /// <summary>D4: "intentá construirlo fuera de la máquina y verificá que NO COMPILA" — la
    /// evidencia estructural de esa propiedad, corrida en vivo (no razonada): el ÚNICO constructor
    /// declarado por <see cref="PermisoDeSolicitud"/> es <c>internal</c>, así que ningún tipo de
    /// <c>Ways.Application</c>/<c>Ways.Infrastructure</c> puede invocarlo — un intento real de
    /// <c>new PermisoDeSolicitud(1, 1)</c> escrito en este mismo ensamblado (<c>Ways.Domain</c>) SÍ
    /// compila (es el propio ensamblado del tipo); el gate real es el de ENSAMBLADO, confirmado acá
    /// por reflexión en vez de reproducir el error de compilación como comentario no verificable.
    /// </summary>
    [Fact]
    public void PermisoDeSolicitudNoTieneNingunConstructorPublico()
    {
        var constructores = typeof(PermisoDeSolicitud).GetConstructors(
            BindingFlags.Public | BindingFlags.Instance);

        Assert.Empty(constructores);
    }
}
