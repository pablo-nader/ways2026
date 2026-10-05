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

    // =================================================================================================
    // Alta dentro de una familia: la tabla estado de la familia × valores compartidos
    // =================================================================================================

    private static readonly ValoresCompartidosDeFamilia ValoresDeReferencia = new(
        IdArea: 11, IdCategoria: 12, IdGrupo: 13, IdProveedorHabitual: 14, IdAlicuotaIva: 15,
        UnidadVenta: UnidadVenta.Unidad, UnidadesPorBulto: 6m, EsProducto: true, ControlaLote: false,
        AcumulaEnVenta: true, CostoLista: 50m, DescuentoProveedor: 10m, CostoNominal: 40m);

    private static readonly ValoresCompartidosDeFamilia ValoresQueDifieren = ValoresDeReferencia with { CostoLista = 51m };

    public static TheoryData<bool, bool, bool, ResolucionDeIngresoAFamilia> TablaDeIngreso() => new()
    {
        // La familia inactiva se informa primero, sin importar lo que siga.
        { false, false, true, ResolucionDeIngresoAFamilia.FamiliaInactiva },
        { false, true, true, ResolucionDeIngresoAFamilia.FamiliaInactiva },
        { false, true, false, ResolucionDeIngresoAFamilia.FamiliaInactiva },

        // Activa y sin ningún miembro vivo: no hay con qué comparar ni qué copiar.
        { true, false, true, ResolucionDeIngresoAFamilia.FamiliaSinArticulos },

        // Activa y con referencia: lo que decide es si los trece valores coinciden.
        { true, true, false, ResolucionDeIngresoAFamilia.ValoresDistintos },
        { true, true, true, ResolucionDeIngresoAFamilia.Permitido }
    };

    [Theory]
    [MemberData(nameof(TablaDeIngreso))]
    public void CadaCeldaDeLaTablaDeIngresoDaSuResolucion(
        bool familiaActiva, bool hayReferencia, bool valoresIdenticos, ResolucionDeIngresoAFamilia esperada)
    {
        var referencia = hayReferencia ? ValoresDeReferencia : null;
        var pedidos = valoresIdenticos ? ValoresDeReferencia : ValoresQueDifieren;

        Assert.Equal(esperada, ReglaDeFamilias.ResolverIngreso(familiaActiva, referencia, pedidos));
    }

    /// <summary>Una familia inactiva y sin miembros, con valores distintos a la vez: gana la inactiva. Es la
    /// precedencia de los tres rechazos, afirmada en el caso que los tiene a los tres.</summary>
    [Fact]
    public void LaPrecedenciaDeLosRechazosEsInactivaLuegoSinArticulosLuegoValoresDistintos()
    {
        Assert.Equal(
            ResolucionDeIngresoAFamilia.FamiliaInactiva,
            ReglaDeFamilias.ResolverIngreso(familiaActiva: false, referencia: null, ValoresQueDifieren));
        Assert.Equal(
            ResolucionDeIngresoAFamilia.FamiliaSinArticulos,
            ReglaDeFamilias.ResolverIngreso(familiaActiva: true, referencia: null, ValoresQueDifieren));
        Assert.Equal(
            ResolucionDeIngresoAFamilia.ValoresDistintos,
            ReglaDeFamilias.ResolverIngreso(familiaActiva: true, ValoresDeReferencia, ValoresQueDifieren));
    }

    /// <summary>Cada uno de los trece campos compartidos, solo, alcanza para que el ingreso sea
    /// <c>ValoresDistintos</c>: la regla no deja pasar una diferencia en ninguno.</summary>
    [Theory]
    [InlineData("id_area")]
    [InlineData("id_categoria")]
    [InlineData("id_grupo")]
    [InlineData("id_proveedor_habitual")]
    [InlineData("id_alicuota_iva")]
    [InlineData("unidad_venta")]
    [InlineData("unidades_por_bulto")]
    [InlineData("es_producto")]
    [InlineData("controla_lote")]
    [InlineData("acumula_en_venta")]
    [InlineData("costo_lista")]
    [InlineData("descuento_proveedor")]
    [InlineData("costo_nominal")]
    public void UnaDiferenciaEnCualquieraDeLosTreceCamposImpideElIngreso(string columna)
    {
        var distinto = columna switch
        {
            "id_area" => ValoresDeReferencia with { IdArea = 21 },
            "id_categoria" => ValoresDeReferencia with { IdCategoria = 22 },
            "id_grupo" => ValoresDeReferencia with { IdGrupo = 23 },
            "id_proveedor_habitual" => ValoresDeReferencia with { IdProveedorHabitual = 24 },
            "id_alicuota_iva" => ValoresDeReferencia with { IdAlicuotaIva = 25 },
            "unidad_venta" => ValoresDeReferencia with { UnidadVenta = UnidadVenta.Peso },
            "unidades_por_bulto" => ValoresDeReferencia with { UnidadesPorBulto = 12m },
            "es_producto" => ValoresDeReferencia with { EsProducto = false },
            "controla_lote" => ValoresDeReferencia with { ControlaLote = true },
            "acumula_en_venta" => ValoresDeReferencia with { AcumulaEnVenta = false },
            "costo_lista" => ValoresDeReferencia with { CostoLista = 60m },
            "descuento_proveedor" => ValoresDeReferencia with { DescuentoProveedor = 15m },
            "costo_nominal" => ValoresDeReferencia with { CostoNominal = 45m },
            _ => throw new ArgumentOutOfRangeException(nameof(columna), columna, "Columna compartida desconocida.")
        };

        Assert.Equal(
            ResolucionDeIngresoAFamilia.ValoresDistintos,
            ReglaDeFamilias.ResolverIngreso(familiaActiva: true, ValoresDeReferencia, distinto));
    }
}
