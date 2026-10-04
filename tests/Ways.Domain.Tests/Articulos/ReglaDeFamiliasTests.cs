using Ways.Domain.Articulos;

namespace Ways.Domain.Tests.Articulos;

/// <summary>
/// La tabla de decisión de <see cref="ReglaDeFamilias.ResolverAlcance"/> (modo × pertenencia →
/// resolución) completa, una fila por celda: los cuatro modos contra "es miembro" y "no es miembro".
/// Cada celda tiene su caso porque cada una tiene su mutante — cambiar una sola resolución de la
/// tabla deja verdes las otras siete, y un caso representativo no lo vería.
/// </summary>
public class ReglaDeFamiliasTests
{
    public static TheoryData<ModoDeAlcanceDeFamilia, bool, ResolucionDeAlcanceDeFamilia> TablaDeDecision() => new()
    {
        // ExigirDecision: la solicitud de la API sin alcance.
        { ModoDeAlcanceDeFamilia.ExigirDecision, false, ResolucionDeAlcanceDeFamilia.SoloElArticulo },
        { ModoDeAlcanceDeFamilia.ExigirDecision, true, ResolucionDeAlcanceDeFamilia.AlcanceRequerido },

        // Familia: decisión explícita; sobre quien ya no es miembro, el cliente miraba una pantalla vieja.
        { ModoDeAlcanceDeFamilia.Familia, false, ResolucionDeAlcanceDeFamilia.FamiliaCambio },
        { ModoDeAlcanceDeFamilia.Familia, true, ResolucionDeAlcanceDeFamilia.TodaLaFamilia },

        // SoloEste: decisión explícita; es el único que cambia la pertenencia.
        { ModoDeAlcanceDeFamilia.SoloEste, false, ResolucionDeAlcanceDeFamilia.FamiliaCambio },
        { ModoDeAlcanceDeFamilia.SoloEste, true, ResolucionDeAlcanceDeFamilia.SalirDeLaFamilia },

        // FamiliaSiCorresponde: llamadores internos, sin error en ningún caso.
        { ModoDeAlcanceDeFamilia.FamiliaSiCorresponde, false, ResolucionDeAlcanceDeFamilia.SoloElArticulo },
        { ModoDeAlcanceDeFamilia.FamiliaSiCorresponde, true, ResolucionDeAlcanceDeFamilia.TodaLaFamilia }
    };

    [Theory]
    [MemberData(nameof(TablaDeDecision))]
    public void CadaCeldaDeLaTablaDaSuResolucion(
        ModoDeAlcanceDeFamilia modo, bool esMiembro, ResolucionDeAlcanceDeFamilia esperada)
    {
        Assert.Equal(esperada, ReglaDeFamilias.ResolverAlcance(modo, esMiembro));
    }

    [Fact]
    public void LaTablaCubreCadaModoContraAmbasPertenencias()
    {
        var celdas = TablaDeDecision().Select(fila => ((ModoDeAlcanceDeFamilia)fila[0], (bool)fila[1])).ToList();

        var esperadas = Enum.GetValues<ModoDeAlcanceDeFamilia>()
            .SelectMany(modo => new[] { (modo, false), (modo, true) })
            .ToList();

        Assert.Equal(esperadas.Count, celdas.Count);
        Assert.Equal(esperadas.Order(), celdas.Order());
    }

    [Fact]
    public void UnModoDesconocidoSeRechazaEnVezDeResolverSeEnSilencio()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ReglaDeFamilias.ResolverAlcance((ModoDeAlcanceDeFamilia)99, esMiembro: true));

        Assert.Equal("modo", error.ParamName);
    }

    /// <summary>El lock de membresía exclusivo es solo para quien puede cambiar la pertenencia. Un
    /// escritor que toma el exclusivo sin necesitarlo serializa a todos los demás; uno que toma el
    /// compartido y después saca al artículo de la familia puede formar un deadlock.</summary>
    [Theory]
    [InlineData(ModoDeAlcanceDeFamilia.ExigirDecision, false)]
    [InlineData(ModoDeAlcanceDeFamilia.Familia, false)]
    [InlineData(ModoDeAlcanceDeFamilia.SoloEste, true)]
    [InlineData(ModoDeAlcanceDeFamilia.FamiliaSiCorresponde, false)]
    public void SoloElModoQueSacaAlArticuloDeLaFamiliaRequiereElLockExclusivo(
        ModoDeAlcanceDeFamilia modo, bool esperado)
    {
        Assert.Equal(esperado, ReglaDeFamilias.RequiereLockExclusivo(modo));
    }

    /// <summary>Coherencia entre las dos reglas: el único modo cuya resolución sobre un miembro
    /// cambia la pertenencia es el único que pide el lock exclusivo.</summary>
    [Fact]
    public void ElLockExclusivoCoincideConLaResolucionQueCambiaLaPertenencia()
    {
        foreach (var modo in Enum.GetValues<ModoDeAlcanceDeFamilia>())
        {
            var cambiaLaPertenencia =
                ReglaDeFamilias.ResolverAlcance(modo, esMiembro: true) == ResolucionDeAlcanceDeFamilia.SalirDeLaFamilia;

            Assert.Equal(cambiaLaPertenencia, ReglaDeFamilias.RequiereLockExclusivo(modo));
        }
    }
}
