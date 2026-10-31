using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Ways.Domain.Common;
using Ways.Domain.MercadoPago;
using Ways.Infrastructure.MercadoPago;

namespace Ways.Application.Tests.MercadoPago;

/// <summary>
/// El cifrado de los tokens de Mercado Pago: el AAD ata cada blob a tenant, empresa, conexión y tipo de token, la clave
/// maestra viene de configuración y su ausencia deja la conexión inhabilitada sin degradar a texto plano.
/// </summary>
public class CifradoDeTokensMercadoPagoTests
{
    private const string AccessToken = "APP_USR-1111111111111111-103112-acceso-secreto";
    private const string RefreshToken = "TG-6543210fedcba-refresco-secreto";
    private static readonly byte[] ClaveMaestra = RandomNumberGenerator.GetBytes(32);

    private static IConfiguration Configuracion(string idActual = "v1", byte[]? clave = null, params (string Id, byte[] Clave)[] otras)
    {
        var valores = new Dictionary<string, string?>
        {
            ["Ways:MercadoPago:ClaveMaestraActual"] = idActual,
            [$"Ways:MercadoPago:ClavesMaestras:{idActual}"] = Convert.ToBase64String(clave ?? ClaveMaestra)
        };

        foreach (var (id, otra) in otras)
        {
            valores[$"Ways:MercadoPago:ClavesMaestras:{id}"] = Convert.ToBase64String(otra);
        }

        return new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
    }

    private static ConexionMercadoPago ConexionDesde(
        Ways.Application.MercadoPago.TokensCifrados c, int idTenant = 1, int idEmpresa = 10, int idConexion = 100) =>
        new()
        {
            Id = idConexion,
            IdTenant = idTenant,
            IdEmpresa = idEmpresa,
            IdClaveMaestra = c.IdClaveMaestra,
            AccessTokenCifrado = c.AccessTokenCifrado,
            AccessTokenNonce = c.AccessTokenNonce,
            AccessTokenTag = c.AccessTokenTag,
            RefreshTokenCifrado = c.RefreshTokenCifrado,
            RefreshTokenNonce = c.RefreshTokenNonce,
            RefreshTokenTag = c.RefreshTokenTag
        };

    [Fact]
    public void CifrarYDescifrarDevuelveLosDosTokensOriginalesYNingunBlobContieneElTextoPlano()
    {
        var almacen = new CifradoDeTokensMercadoPago(Configuracion());

        var cifrados = almacen.Cifrar(1, 10, 100, AccessToken, RefreshToken);

        Assert.Equal("v1", cifrados.IdClaveMaestra);
        Assert.Equal(CifradoDeTokensMercadoPago.TamanioNonce, cifrados.AccessTokenNonce.Length);
        Assert.Equal(CifradoDeTokensMercadoPago.TamanioTag, cifrados.RefreshTokenTag.Length);
        Assert.False(cifrados.AccessTokenCifrado.AsSpan().IndexOf(Encoding.UTF8.GetBytes(AccessToken)) >= 0);
        Assert.False(cifrados.RefreshTokenCifrado.AsSpan().IndexOf(Encoding.UTF8.GetBytes(RefreshToken)) >= 0);

        var (acceso, renovacion) = almacen.Descifrar(ConexionDesde(cifrados));

        Assert.Equal(AccessToken, acceso);
        Assert.Equal(RefreshToken, renovacion);
    }

    [Fact]
    public void ElMismoTokenCifradoDosVecesSaleConNonceYBlobDistintos()
    {
        var almacen = new CifradoDeTokensMercadoPago(Configuracion());

        var a = almacen.Cifrar(1, 10, 100, AccessToken, RefreshToken);
        var b = almacen.Cifrar(1, 10, 100, AccessToken, RefreshToken);

        Assert.NotEqual(a.AccessTokenNonce, b.AccessTokenNonce);
        Assert.NotEqual(a.AccessTokenCifrado, b.AccessTokenCifrado);
    }

    [Theory]
    [InlineData(2, 10, 100)]
    [InlineData(1, 11, 100)]
    [InlineData(1, 10, 101)]
    public void UnBlobDeUnaConexionNoSeDescifraBajoElAadDeOtra(int idTenant, int idEmpresa, int idConexion)
    {
        var almacen = new CifradoDeTokensMercadoPago(Configuracion());
        var cifrados = almacen.Cifrar(1, 10, 100, AccessToken, RefreshToken);

        var error = Assert.Throws<ErrorDominio>(() =>
            almacen.Descifrar(ConexionDesde(cifrados, idTenant, idEmpresa, idConexion)));

        Assert.Equal("mercadopago_tokens_ilegibles", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
    }

    [Fact]
    public void ElBlobDeAccesoNoSeDescifraComoTokenDeRenovacion()
    {
        var cifrados = new CifradoDeTokensMercadoPago(Configuracion()).Cifrar(1, 10, 100, AccessToken, RefreshToken);

        Assert.Throws<AuthenticationTagMismatchException>(() =>
            CifradoDeTokensMercadoPago.Descifrar(
                ClaveMaestra, cifrados.AccessTokenCifrado, cifrados.AccessTokenNonce, cifrados.AccessTokenTag,
                1, 10, 100, CifradoDeTokensMercadoPago.TokenDeRenovacion));
    }

    [Fact]
    public void UnaClaveMaestraDistintaNoDescifra()
    {
        var cifrados = new CifradoDeTokensMercadoPago(Configuracion()).Cifrar(1, 10, 100, AccessToken, RefreshToken);
        var otra = new CifradoDeTokensMercadoPago(Configuracion(clave: RandomNumberGenerator.GetBytes(32)));

        var error = Assert.Throws<ErrorDominio>(() => otra.Descifrar(ConexionDesde(cifrados)));

        Assert.Equal("mercadopago_tokens_ilegibles", error.Codigo);
    }

    [Fact]
    public void LaRotacionDeClaveMaestraSigueDescifrandoLoCifradoConLaVersionAnterior()
    {
        var claveNueva = RandomNumberGenerator.GetBytes(32);
        var antes = new CifradoDeTokensMercadoPago(Configuracion("v1"));
        var cifrados = antes.Cifrar(1, 10, 100, AccessToken, RefreshToken);

        var despues = new CifradoDeTokensMercadoPago(Configuracion("v2", claveNueva, ("v1", ClaveMaestra)));

        Assert.Equal("v2", despues.Cifrar(1, 10, 101, AccessToken, RefreshToken).IdClaveMaestra);
        Assert.Equal((AccessToken, RefreshToken), despues.Descifrar(ConexionDesde(cifrados)));
    }

    [Fact]
    public void UnaConexionDesconectadaSinBlobsNoSeDescifra()
    {
        var almacen = new CifradoDeTokensMercadoPago(Configuracion());

        var error = Assert.Throws<ErrorDominio>(() =>
            almacen.Descifrar(new ConexionMercadoPago { Id = 1, IdClaveMaestra = "v1" }));

        Assert.Equal("mercadopago_tokens_ilegibles", error.Codigo);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("v1", null)]
    [InlineData("v1", "no-es-base64!!")]
    [InlineData("v1", "AAAA")]
    public void SinClaveMaestraUtilNoSePuedeCifrarYCifrarDaMercadoPagoNoConfigurado(string? id, string? valor)
    {
        var valores = new Dictionary<string, string?>
        {
            ["Ways:MercadoPago:ClaveMaestraActual"] = id,
            ["Ways:MercadoPago:ClavesMaestras:v1"] = valor
        };
        var almacen = new CifradoDeTokensMercadoPago(new ConfigurationBuilder().AddInMemoryCollection(valores).Build());

        Assert.False(almacen.PuedeCifrar);
        var error = Assert.Throws<ErrorDominio>(() => almacen.Cifrar(1, 10, 100, AccessToken, RefreshToken));
        Assert.Equal("mercadopago_no_configurado", error.Codigo);
        Assert.Equal(503, error.EstadoHttp);
    }

    [Fact]
    public void LaClaveMaestraDeMercadoPagoEsIndependienteDeLaFiscal()
    {
        var valores = new Dictionary<string, string?>
        {
            ["Ways:Fiscal:ClaveMaestraActual"] = "v1",
            ["Ways:Fiscal:ClavesMaestras:v1"] = Convert.ToBase64String(ClaveMaestra)
        };

        Assert.False(new CifradoDeTokensMercadoPago(new ConfigurationBuilder().AddInMemoryCollection(valores).Build()).PuedeCifrar);
    }
}
