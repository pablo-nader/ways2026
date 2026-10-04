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

    // =================================================================================================
    // Edición de un miembro: la tabla modo × (cambia campos compartidos | no cambia)
    // =================================================================================================

    public static TheoryData<ModoDeAlcanceDeFamilia, bool, ResolucionDeAlcanceDeFamilia> TablaDeEdicionDeMiembro() => new()
    {
        // ExigirDecision: sin alcance, un cambio compartido obliga a decidir; sin cambio compartido no hay nada
        // que decidir y se escribe solo el artículo.
        { ModoDeAlcanceDeFamilia.ExigirDecision, true, ResolucionDeAlcanceDeFamilia.AlcanceRequerido },
        { ModoDeAlcanceDeFamilia.ExigirDecision, false, ResolucionDeAlcanceDeFamilia.SoloElArticulo },

        // Familia: replica el cambio compartido; sin cambio compartido no hay nada que replicar.
        { ModoDeAlcanceDeFamilia.Familia, true, ResolucionDeAlcanceDeFamilia.TodaLaFamilia },
        { ModoDeAlcanceDeFamilia.Familia, false, ResolucionDeAlcanceDeFamilia.SoloElArticulo },

        // SoloEste: decisión explícita de salir de la familia, haya o no cambio compartido.
        { ModoDeAlcanceDeFamilia.SoloEste, true, ResolucionDeAlcanceDeFamilia.SalirDeLaFamilia },
        { ModoDeAlcanceDeFamilia.SoloEste, false, ResolucionDeAlcanceDeFamilia.SalirDeLaFamilia },

        // FamiliaSiCorresponde: para llamadores internos; mismo criterio que Familia sobre un miembro.
        { ModoDeAlcanceDeFamilia.FamiliaSiCorresponde, true, ResolucionDeAlcanceDeFamilia.TodaLaFamilia },
        { ModoDeAlcanceDeFamilia.FamiliaSiCorresponde, false, ResolucionDeAlcanceDeFamilia.SoloElArticulo }
    };

    [Theory]
    [MemberData(nameof(TablaDeEdicionDeMiembro))]
    public void CadaCeldaDeLaEdicionDeUnMiembroDaSuResolucion(
        ModoDeAlcanceDeFamilia modo, bool cambiaCamposCompartidos, ResolucionDeAlcanceDeFamilia esperada)
    {
        Assert.Equal(esperada, ReglaDeFamilias.ResolverEdicionDeMiembro(modo, cambiaCamposCompartidos));
    }

    [Fact]
    public void LaTablaDeEdicionCubreCadaModoContraAmbosEstadosDelCambio()
    {
        var celdas = TablaDeEdicionDeMiembro().Select(fila => ((ModoDeAlcanceDeFamilia)fila[0], (bool)fila[1])).ToList();

        var esperadas = Enum.GetValues<ModoDeAlcanceDeFamilia>()
            .SelectMany(modo => new[] { (modo, false), (modo, true) })
            .ToList();

        Assert.Equal(esperadas.Count, celdas.Count);
        Assert.Equal(esperadas.Order(), celdas.Order());
    }

    /// <summary>La edición solo afina la decisión sobre un miembro cuando NO cambia nada compartido: con un
    /// cambio compartido el resultado es el de <see cref="ReglaDeFamilias.ResolverAlcance"/> tal cual.</summary>
    [Fact]
    public void ConUnCambioCompartidoLaEdicionDeUnMiembroEsLaDecisionDeAlcanceSinAfinar()
    {
        foreach (var modo in Enum.GetValues<ModoDeAlcanceDeFamilia>())
        {
            Assert.Equal(
                ReglaDeFamilias.ResolverAlcance(modo, esMiembro: true),
                ReglaDeFamilias.ResolverEdicionDeMiembro(modo, cambiaCamposCompartidos: true));
        }
    }

    /// <summary>El rechazo de quien no es miembro (<c>FamiliaCambio</c>) se decide antes de leer ningún
    /// valor, con <see cref="ReglaDeFamilias.ResolverAlcance"/>: la edición de un miembro nunca lo
    /// produce.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LaEdicionDeUnMiembroNuncaDaFamiliaCambio(bool cambiaCamposCompartidos)
    {
        foreach (var modo in Enum.GetValues<ModoDeAlcanceDeFamilia>())
        {
            Assert.NotEqual(
                ResolucionDeAlcanceDeFamilia.FamiliaCambio,
                ReglaDeFamilias.ResolverEdicionDeMiembro(modo, cambiaCamposCompartidos));
        }
    }

    [Fact]
    public void UnModoDesconocidoEnLaEdicionDeUnMiembroSeRechazaEnVezDeResolverseEnSilencio()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ReglaDeFamilias.ResolverEdicionDeMiembro((ModoDeAlcanceDeFamilia)99, cambiaCamposCompartidos: false));

        Assert.Equal("modo", error.ParamName);
    }
}
