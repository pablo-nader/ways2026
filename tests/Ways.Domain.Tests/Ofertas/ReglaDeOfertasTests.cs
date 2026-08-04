using Ways.Domain.Common;
using Ways.Domain.Ofertas;

namespace Ways.Domain.Tests.Ofertas;

/// <summary>
/// stage-4-ofertas, Slice 1 (task 1.9, spec: ofertas / Domain guard rejects invalid shapes
/// before the database) — función pura, sin base de datos, mismo criterio que
/// <see cref="Articulos.ReglaDeArticulosTests"/>/<see cref="Precios.ResolvedorDePreciosTests"/>.
/// </summary>
public class ReglaDeOfertasTests
{
    private static Oferta CrearOferta(
        int? idArticulo = 1, int? idGrupo = null, int? idCategoria = null,
        decimal? precioUnitario = null, decimal? porcentaje = 10m, decimal? importeFijo = null) =>
        new()
        {
            IdTenant = 1,
            Nombre = "oferta de prueba",
            IdArticulo = idArticulo,
            IdGrupo = idGrupo,
            IdCategoria = idCategoria,
            PrecioUnitario = precioUnitario,
            Porcentaje = porcentaje,
            ImporteFijo = importeFijo,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    // --- LeerAlcance: exclusividad ---

    [Fact]
    public void LeerAlcanceConSoloArticuloDevuelveElAlcanceDeArticulo()
    {
        var alcance = ReglaDeOfertas.LeerAlcance(CrearOferta(idArticulo: 5, idGrupo: null, idCategoria: null));

        Assert.Equal(5, alcance.IdArticulo);
        Assert.Null(alcance.IdGrupo);
        Assert.Null(alcance.IdCategoria);
    }

    [Fact]
    public void LeerAlcanceConSoloGrupoDevuelveElAlcanceDeGrupo()
    {
        var alcance = ReglaDeOfertas.LeerAlcance(CrearOferta(idArticulo: null, idGrupo: 7, idCategoria: null));

        Assert.Null(alcance.IdArticulo);
        Assert.Equal(7, alcance.IdGrupo);
        Assert.Null(alcance.IdCategoria);
    }

    [Fact]
    public void LeerAlcanceConSoloCategoriaDevuelveElAlcanceDeCategoria()
    {
        var alcance = ReglaDeOfertas.LeerAlcance(CrearOferta(idArticulo: null, idGrupo: null, idCategoria: 9));

        Assert.Null(alcance.IdArticulo);
        Assert.Null(alcance.IdGrupo);
        Assert.Equal(9, alcance.IdCategoria);
    }

    [Fact]
    public void LeerAlcanceSinNingunaColumnaSeteadaEsRechazado()
    {
        var error = Assert.Throws<ErrorDominio>(() =>
            ReglaDeOfertas.LeerAlcance(CrearOferta(idArticulo: null, idGrupo: null, idCategoria: null)));

        Assert.Equal("oferta_alcance_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void LeerAlcanceConMasDeUnaColumnaSeteadaEsRechazado()
    {
        var error = Assert.Throws<ErrorDominio>(() =>
            ReglaDeOfertas.LeerAlcance(CrearOferta(idArticulo: 1, idGrupo: 2, idCategoria: null)));

        Assert.Equal("oferta_alcance_invalido", error.Codigo);
    }

    [Fact]
    public void LeerAlcanceConLasTresColumnasSeteadasEsRechazado()
    {
        var error = Assert.Throws<ErrorDominio>(() =>
            ReglaDeOfertas.LeerAlcance(CrearOferta(idArticulo: 1, idGrupo: 2, idCategoria: 3)));

        Assert.Equal("oferta_alcance_invalido", error.Codigo);
    }

    // --- LeerBeneficio: exclusividad ---

    [Fact]
    public void LeerBeneficioSinNingunBeneficioSeteadoEsRechazado()
    {
        var error = Assert.Throws<ErrorDominio>(() =>
            ReglaDeOfertas.LeerBeneficio(CrearOferta(porcentaje: null)));

        Assert.Equal("oferta_beneficio_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void LeerBeneficioConMasDeUnBeneficioSeteadoEsRechazado()
    {
        var error = Assert.Throws<ErrorDominio>(() =>
            ReglaDeOfertas.LeerBeneficio(CrearOferta(precioUnitario: 100m, porcentaje: 10m)));

        Assert.Equal("oferta_beneficio_invalido", error.Codigo);
    }

    // --- LeerBeneficio: rangos ---

    [Fact]
    public void LeerBeneficioConPorcentajeDentroDelRangoDevuelveElBeneficioDePorcentaje()
    {
        var beneficio = ReglaDeOfertas.LeerBeneficio(CrearOferta(porcentaje: 20m));

        Assert.Equal(20m, beneficio.Porcentaje);
        Assert.Null(beneficio.PrecioUnitario);
        Assert.Null(beneficio.ImporteFijo);
    }

    [Fact]
    public void LeerBeneficioConPorcentajeCienEsPermitidoInclusive()
    {
        var beneficio = ReglaDeOfertas.LeerBeneficio(CrearOferta(porcentaje: 100m));

        Assert.Equal(100m, beneficio.Porcentaje);
    }

    [Fact]
    public void LeerBeneficioConPorcentajeCeroEsRechazado()
    {
        var error = Assert.Throws<ErrorDominio>(() => ReglaDeOfertas.LeerBeneficio(CrearOferta(porcentaje: 0m)));

        Assert.Equal("oferta_porcentaje_invalido", error.Codigo);
    }

    [Fact]
    public void LeerBeneficioConPorcentajeMayorACienEsRechazado()
    {
        var error = Assert.Throws<ErrorDominio>(() => ReglaDeOfertas.LeerBeneficio(CrearOferta(porcentaje: 100.01m)));

        Assert.Equal("oferta_porcentaje_invalido", error.Codigo);
    }

    [Fact]
    public void LeerBeneficioConPrecioUnitarioCeroEsPermitido()
    {
        var beneficio = ReglaDeOfertas.LeerBeneficio(CrearOferta(precioUnitario: 0m, porcentaje: null));

        Assert.Equal(0m, beneficio.PrecioUnitario);
    }

    [Fact]
    public void LeerBeneficioConPrecioUnitarioNegativoEsRechazado()
    {
        var error = Assert.Throws<ErrorDominio>(() =>
            ReglaDeOfertas.LeerBeneficio(CrearOferta(precioUnitario: -1m, porcentaje: null)));

        Assert.Equal("oferta_precio_unitario_invalido", error.Codigo);
    }

    [Fact]
    public void LeerBeneficioConImporteFijoCeroEsPermitido()
    {
        var beneficio = ReglaDeOfertas.LeerBeneficio(CrearOferta(importeFijo: 0m, porcentaje: null));

        Assert.Equal(0m, beneficio.ImporteFijo);
    }

    [Fact]
    public void LeerBeneficioConImporteFijoNegativoEsRechazado()
    {
        var error = Assert.Throws<ErrorDominio>(() =>
            ReglaDeOfertas.LeerBeneficio(CrearOferta(importeFijo: -0.01m, porcentaje: null)));

        Assert.Equal("oferta_importe_fijo_invalido", error.Codigo);
    }

    // --- ValidarVentana ---

    [Fact]
    public void ValidarVentanaConFechaHastaAnteriorAFechaDesdeEsRechazado()
    {
        var error = Assert.Throws<ErrorDominio>(() =>
            ReglaDeOfertas.ValidarVentana(
                new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 1), null, null));

        Assert.Equal("ventana_de_oferta_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void ValidarVentanaConHoraHastaAnteriorAHoraDesdeEsRechazado()
    {
        var error = Assert.Throws<ErrorDominio>(() =>
            ReglaDeOfertas.ValidarVentana(
                null, null, new TimeOnly(14, 0), new TimeOnly(10, 0)));

        Assert.Equal("ventana_de_oferta_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void ValidarVentanaConFechasIgualesEsPermitido()
    {
        var excepcion = Record.Exception(() =>
            ReglaDeOfertas.ValidarVentana(
                new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 1), null, null));

        Assert.Null(excepcion);
    }

    [Fact]
    public void ValidarVentanaConHorasIgualesEsPermitido()
    {
        var excepcion = Record.Exception(() =>
            ReglaDeOfertas.ValidarVentana(
                null, null, new TimeOnly(10, 0), new TimeOnly(10, 0)));

        Assert.Null(excepcion);
    }

    [Fact]
    public void ValidarVentanaConFechaDesdeNulaEsPermitido()
    {
        var excepcion = Record.Exception(() =>
            ReglaDeOfertas.ValidarVentana(null, new DateOnly(2026, 8, 1), null, null));

        Assert.Null(excepcion);
    }

    [Fact]
    public void ValidarVentanaConFechaHastaNulaEsPermitido()
    {
        var excepcion = Record.Exception(() =>
            ReglaDeOfertas.ValidarVentana(new DateOnly(2026, 8, 1), null, null, null));

        Assert.Null(excepcion);
    }

    [Fact]
    public void ValidarVentanaConHoraDesdeNulaEsPermitido()
    {
        var excepcion = Record.Exception(() =>
            ReglaDeOfertas.ValidarVentana(null, null, null, new TimeOnly(10, 0)));

        Assert.Null(excepcion);
    }

    [Fact]
    public void ValidarVentanaConHoraHastaNulaEsPermitido()
    {
        var excepcion = Record.Exception(() =>
            ReglaDeOfertas.ValidarVentana(null, null, new TimeOnly(10, 0), null));

        Assert.Null(excepcion);
    }

    [Fact]
    public void ValidarVentanaConTodoNuloEsPermitido()
    {
        var excepcion = Record.Exception(() => ReglaDeOfertas.ValidarVentana(null, null, null, null));

        Assert.Null(excepcion);
    }

    // --- ValidarCantidadMinima ---

    [Fact]
    public void ValidarCantidadMinimaConNullEsPermitido()
    {
        var excepcion = Record.Exception(() => ReglaDeOfertas.ValidarCantidadMinima(null));

        Assert.Null(excepcion);
    }

    [Fact]
    public void ValidarCantidadMinimaPositivaEsPermitido()
    {
        var excepcion = Record.Exception(() => ReglaDeOfertas.ValidarCantidadMinima(3m));

        Assert.Null(excepcion);
    }

    [Fact]
    public void ValidarCantidadMinimaCeroEsRechazado()
    {
        var error = Assert.Throws<ErrorDominio>(() => ReglaDeOfertas.ValidarCantidadMinima(0m));

        Assert.Equal("cantidad_minima_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void ValidarCantidadMinimaNegativaEsRechazado()
    {
        var error = Assert.Throws<ErrorDominio>(() => ReglaDeOfertas.ValidarCantidadMinima(-1m));

        Assert.Equal("cantidad_minima_invalida", error.Codigo);
    }

    // --- LeerDiasSemana ---

    [Fact]
    public void LeerDiasSemanaConNullDevuelveConjuntoVacio()
    {
        var dias = ReglaDeOfertas.LeerDiasSemana(null);

        Assert.Empty(dias);
    }

    [Fact]
    public void LeerDiasSemanaConArrayVacioDevuelveConjuntoVacio()
    {
        var dias = ReglaDeOfertas.LeerDiasSemana([]);

        Assert.Empty(dias);
    }

    [Fact]
    public void LeerDiasSemanaConValoresValidosDevuelveElConjunto()
    {
        var dias = ReglaDeOfertas.LeerDiasSemana([6, 7]);

        Assert.Equal(new HashSet<int> { 6, 7 }, dias);
    }

    [Fact]
    public void LeerDiasSemanaConCeroEsRechazado()
    {
        var error = Assert.Throws<ErrorDominio>(() => ReglaDeOfertas.LeerDiasSemana([0, 1]));

        Assert.Equal("dias_semana_invalidos", error.Codigo);
    }

    [Fact]
    public void LeerDiasSemanaConOchoEsRechazado()
    {
        var error = Assert.Throws<ErrorDominio>(() => ReglaDeOfertas.LeerDiasSemana([1, 8]));

        Assert.Equal("dias_semana_invalidos", error.Codigo);
    }

    [Fact]
    public void LeerDiasSemanaConDuplicadosEsRechazado()
    {
        var error = Assert.Throws<ErrorDominio>(() => ReglaDeOfertas.LeerDiasSemana([3, 3]));

        Assert.Equal("dias_semana_invalidos", error.Codigo);
    }

    // --- CoincideEmpresa (stage-4-ofertas, Slice 3, task 3.9; spec: resolucion-de-ofertas /
    // Candidate Matching, "Empresa-scoped oferta excludes other empresas") ---

    [Fact]
    public void CoincideEmpresaConOfertaSinEmpresaMatcheaCualquierEmpresaDeLinea()
    {
        Assert.True(ReglaDeOfertas.CoincideEmpresa(idEmpresaOferta: null, idEmpresaLinea: 5));
    }

    [Fact]
    public void CoincideEmpresaConOfertaSinEmpresaMatcheaLineaSinEmpresa()
    {
        Assert.True(ReglaDeOfertas.CoincideEmpresa(idEmpresaOferta: null, idEmpresaLinea: null));
    }

    [Fact]
    public void CoincideEmpresaConLaMismaEmpresaMatchea()
    {
        Assert.True(ReglaDeOfertas.CoincideEmpresa(idEmpresaOferta: 5, idEmpresaLinea: 5));
    }

    [Fact]
    public void CoincideEmpresaConEmpresaDistintaNoMatchea()
    {
        Assert.False(ReglaDeOfertas.CoincideEmpresa(idEmpresaOferta: 5, idEmpresaLinea: 6));
    }

    [Fact]
    public void CoincideEmpresaConOfertaDeEmpresaYLineaSinEmpresaNoMatchea()
    {
        Assert.False(ReglaDeOfertas.CoincideEmpresa(idEmpresaOferta: 5, idEmpresaLinea: null));
    }
}
