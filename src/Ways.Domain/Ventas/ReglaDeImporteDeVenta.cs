using System.Globalization;
using Ways.Domain.Common;

namespace Ways.Domain.Ventas;

/// <summary>
/// Regla pura del importe que el cajero tipea para una línea de un artículo por importe
/// (<see cref="Articulos.UnidadVenta.Importe"/>). Es un precio unitario: mayor a cero, con hasta dos decimales y dentro
/// del rango de <c>items_comprobante_venta.precio_unitario</c> (<c>numeric(14,2)</c>).
/// </summary>
public static class ReglaDeImporteDeVenta
{
    public const int DecimalesMaximos = 2;
    public const decimal ImporteMaximo = 999_999_999_999.99m;

    public static bool EsValido(decimal importe) =>
        importe > 0m
        && importe <= ImporteMaximo
        && decimal.Round(importe, DecimalesMaximos, MidpointRounding.AwayFromZero) == importe;

    /// <summary><c>null</c> es "la línea no trae importe" y siempre es válido: quién lo exige lo decide el llamador.</summary>
    public static void Validar(decimal? importe)
    {
        if (importe is { } valor && !EsValido(valor))
        {
            throw new ErrorDominio(
                "importe_invalido",
                "El importe tiene que ser mayor a cero, con hasta 2 decimales y dentro del rango admitido.",
                400);
        }
    }

    /// <summary>Lee el texto que el cajero escribe antes del <c>*</c> de un escaneo. Solo dígitos con punto decimal
    /// opcional: sin signo, sin separador de miles y sin coma, para que <c>150,50</c> o <c>1,500</c> nunca se lean
    /// como otro monto. Con más de dos dígitos tras el punto, ceros incluidos, también se rechaza: <c>1.500</c> es
    /// mil quinientos para quien lo tipea y no un peso con cincuenta. Espeja <c>leerImporteTipeado</c> de la web
    /// (<c>importeDeVenta.ts</c>). Devuelve <c>false</c> si el texto no es un importe válido.</summary>
    public static bool TryParsear(string? texto, out decimal importe)
    {
        importe = 0m;

        var punto = texto?.IndexOf('.') ?? -1;
        if (punto >= 0 && texto!.Length - punto - 1 > DecimalesMaximos)
        {
            return false;
        }

        if (!decimal.TryParse(texto, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var leido)
            || !EsValido(leido))
        {
            return false;
        }

        importe = leido;
        return true;
    }
}
