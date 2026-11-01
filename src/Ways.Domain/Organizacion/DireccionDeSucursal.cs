using Ways.Domain.Common;

namespace Ways.Domain.Organizacion;

/// <summary>
/// La dirección estructurada de una sucursal, toda opcional. <see cref="Normalizar"/> es la única forma de armarla desde
/// datos del cliente: recorta el texto, deja en <c>null</c> lo que quedó vacío, rechaza lo que no entra en su columna y
/// exige coordenadas completas y en rango. Los CHECK de <c>sucursales</c> repiten el rango y el par completo como
/// backstop de una escritura cruda. Ninguna regla de completitud vive acá: qué necesita Mercado Pago lo decide quien lo usa.
/// </summary>
public sealed record DireccionDeSucursal(
    string? Calle,
    string? Altura,
    string? Ciudad,
    string? Provincia,
    string? CodigoPostal,
    decimal? Latitud,
    decimal? Longitud,
    string? Referencia)
{
    public const int LargoMaximoCalle = 120;
    public const int LargoMaximoAltura = 20;
    public const int LargoMaximoCiudad = 80;
    public const int LargoMaximoProvincia = 40;
    public const int LargoMaximoCodigoPostal = 10;
    public const int LargoMaximoReferencia = 200;

    /// <summary>Decimales de <c>latitud</c> y <c>longitud</c> (numeric(9,6)).</summary>
    public const int DecimalesDeCoordenada = 6;

    public static readonly DireccionDeSucursal Vacia = new(null, null, null, null, null, null, null, null);

    public static DireccionDeSucursal Normalizar(
        string? calle, string? altura, string? ciudad, string? provincia, string? codigoPostal,
        decimal? latitud, decimal? longitud, string? referencia)
    {
        var provinciaLimpia = Texto(provincia, "provincia", "provincia", LargoMaximoProvincia);
        if (provinciaLimpia is not null)
        {
            provinciaLimpia = ProvinciasArgentinas.Canonica(provinciaLimpia)
                ?? throw new ErrorDominio(
                    "provincia_invalida", "La provincia tiene que ser una de las 24 jurisdicciones argentinas.", 400);
        }

        var (latitudRedondeada, longitudRedondeada) = Coordenadas(latitud, longitud);

        return new DireccionDeSucursal(
            Texto(calle, "calle", "calle", LargoMaximoCalle),
            Texto(altura, "altura", "altura", LargoMaximoAltura),
            Texto(ciudad, "ciudad", "ciudad", LargoMaximoCiudad),
            provinciaLimpia,
            Texto(codigoPostal, "codigo_postal", "código postal", LargoMaximoCodigoPostal),
            latitudRedondeada,
            longitudRedondeada,
            Texto(referencia, "referencia", "referencia", LargoMaximoReferencia));
    }

    private static (decimal? Latitud, decimal? Longitud) Coordenadas(decimal? latitud, decimal? longitud)
    {
        if (latitud is null != longitud is null)
        {
            throw new ErrorDominio(
                "coordenadas_incompletas", "La latitud y la longitud se completan juntas o no se completan.", 400);
        }

        if (latitud is null || longitud is null)
        {
            return (null, null);
        }

        var lat = Math.Round(latitud.Value, DecimalesDeCoordenada, MidpointRounding.AwayFromZero);
        var lng = Math.Round(longitud.Value, DecimalesDeCoordenada, MidpointRounding.AwayFromZero);

        if (lat is < -90m or > 90m)
        {
            throw new ErrorDominio("latitud_invalida", "La latitud tiene que estar entre -90 y 90.", 400);
        }

        if (lng is < -180m or > 180m)
        {
            throw new ErrorDominio("longitud_invalida", "La longitud tiene que estar entre -180 y 180.", 400);
        }

        return (lat, lng);
    }

    private static string? Texto(string? valor, string codigo, string campo, int largoMaximo)
    {
        if (string.IsNullOrWhiteSpace(valor))
        {
            return null;
        }

        var limpio = valor.Trim();

        if (limpio.Length > largoMaximo)
        {
            throw new ErrorDominio(
                $"{codigo}_muy_largo", $"El campo {campo} no puede superar los {largoMaximo} caracteres.", 400);
        }

        return limpio;
    }
}
