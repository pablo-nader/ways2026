using Ways.Domain.Common;
using Ways.Domain.Organizacion;

namespace Ways.Domain.Tests.Organizacion;

public class DireccionDeSucursalTests
{
    private static DireccionDeSucursal Normalizar(
        string? calle = null, string? altura = null, string? ciudad = null, string? provincia = null,
        string? codigoPostal = null, decimal? latitud = null, decimal? longitud = null, string? referencia = null) =>
        DireccionDeSucursal.Normalizar(calle, altura, ciudad, provincia, codigoPostal, latitud, longitud, referencia);

    private static string CodigoDe(Action accion) => Assert.Throws<ErrorDominio>(accion).Codigo;

    [Fact]
    public void SinDatosLaDireccionEstaVaciaYEsValida()
    {
        var direccion = Normalizar();

        Assert.Equal(DireccionDeSucursal.Vacia, direccion);
    }

    [Fact]
    public void RecortaElTextoYUnTextoEnBlancoQuedaNulo()
    {
        var direccion = Normalizar(calle: "  Mitre ", altura: "1234 bis", ciudad: "   ", referencia: "\t");

        Assert.Equal("Mitre", direccion.Calle);
        Assert.Equal("1234 bis", direccion.Altura);
        Assert.Null(direccion.Ciudad);
        Assert.Null(direccion.Referencia);
    }

    [Theory]
    [InlineData("calle_muy_largo", 121)]
    [InlineData("altura_muy_largo", 21)]
    [InlineData("ciudad_muy_largo", 81)]
    [InlineData("codigo_postal_muy_largo", 11)]
    [InlineData("referencia_muy_largo", 201)]
    public void UnTextoQueSuperaElLargoDeLaColumnaSeRechaza(string codigo, int largo)
    {
        var texto = new string('a', largo);

        var obtenido = CodigoDe(() => _ = codigo switch
        {
            "calle_muy_largo" => Normalizar(calle: texto),
            "altura_muy_largo" => Normalizar(altura: texto),
            "ciudad_muy_largo" => Normalizar(ciudad: texto),
            "codigo_postal_muy_largo" => Normalizar(codigoPostal: texto),
            _ => Normalizar(referencia: texto)
        });

        Assert.Equal(codigo, obtenido);
    }

    [Fact]
    public void ElTextoEnElLargoMaximoSeAcepta()
    {
        var direccion = Normalizar(calle: new string('a', 120), referencia: new string('a', 200));

        Assert.Equal(120, direccion.Calle!.Length);
        Assert.Equal(200, direccion.Referencia!.Length);
    }

    [Theory]
    [InlineData("cordoba", "Córdoba")]
    [InlineData("  CÓRDOBA ", "Córdoba")]
    [InlineData("entre rios", "Entre Ríos")]
    [InlineData("TUCUMAN", "Tucumán")]
    [InlineData("neuquen", "Neuquén")]
    [InlineData("santiago   del estero", "Santiago del Estero")]
    [InlineData("ciudad autonoma de buenos aires", "Ciudad Autónoma de Buenos Aires")]
    [InlineData("Buenos Aires", "Buenos Aires")]
    public void LaProvinciaSeReconoceSinMayusculasNiAcentosYSeGuardaConSuNombreCanonico(string entrada, string canonico)
    {
        Assert.Equal(canonico, Normalizar(provincia: entrada).Provincia);
    }

    [Fact]
    public void LaListaTiene24JurisdiccionesYCadaUnaSeReconocePorSuNombre()
    {
        Assert.Equal(24, ProvinciasArgentinas.Nombres.Count);
        Assert.Equal(24, ProvinciasArgentinas.Nombres.Distinct().Count());

        foreach (var nombre in ProvinciasArgentinas.Nombres)
        {
            Assert.Equal(nombre, Normalizar(provincia: nombre).Provincia);
        }
    }

    [Theory]
    [InlineData("Narnia")]
    [InlineData("Buenos")]
    [InlineData("CABA")]
    public void UnaProvinciaFueraDeLaListaSeRechaza(string provincia)
    {
        Assert.Equal("provincia_invalida", CodigoDe(() => Normalizar(provincia: provincia)));
    }

    [Fact]
    public void UnaProvinciaEnBlancoQuedaNula()
    {
        Assert.Null(Normalizar(provincia: "  ").Provincia);
    }

    [Fact]
    public void LasCoordenadasEnLosLimitesSeAceptan()
    {
        var direccion = Normalizar(latitud: -90m, longitud: 180m);

        Assert.Equal(-90m, direccion.Latitud);
        Assert.Equal(180m, direccion.Longitud);
    }

    [Theory]
    [InlineData(90.000001, 0)]
    [InlineData(-90.1, 0)]
    public void LaLatitudFueraDeRangoSeRechaza(double latitud, double longitud)
    {
        Assert.Equal("latitud_invalida", CodigoDe(() => Normalizar(latitud: (decimal)latitud, longitud: (decimal)longitud)));
    }

    [Theory]
    [InlineData(0, 180.5)]
    [InlineData(0, -181)]
    public void LaLongitudFueraDeRangoSeRechaza(double latitud, double longitud)
    {
        Assert.Equal("longitud_invalida", CodigoDe(() => Normalizar(latitud: (decimal)latitud, longitud: (decimal)longitud)));
    }

    [Theory]
    [InlineData(-34.6, null)]
    [InlineData(null, -58.4)]
    public void LasCoordenadasSeDanJuntasOSeOmitenJuntas(double? latitud, double? longitud)
    {
        var codigo = CodigoDe(() => Normalizar(latitud: (decimal?)latitud, longitud: (decimal?)longitud));

        Assert.Equal("coordenadas_incompletas", codigo);
    }

    [Fact]
    public void LasCoordenadasSeRedondeanALosSeisDecimalesDeLaColumna()
    {
        var direccion = Normalizar(latitud: -34.6037389m, longitud: -58.3815704m);

        Assert.Equal(-34.603739m, direccion.Latitud);
        Assert.Equal(-58.381570m, direccion.Longitud);
    }
}
