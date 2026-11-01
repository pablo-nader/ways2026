using Ways.Domain.Common;
using Ways.Domain.Organizacion;

namespace Ways.Domain.MercadoPago;

/// <summary>
/// Lo que Mercado Pago necesita saber de una sucursal para registrarla como local (<i>store</i>): el nombre y la ubicación, ya con
/// el texto limpio (<see cref="TextoParaMercadoPago"/>). <see cref="CamposFaltantes"/> es la regla de completitud: la dirección de
/// la sucursal es toda opcional en Ways, y recién acá se decide qué exige Mercado Pago. El código postal no se manda y la
/// referencia es opcional.
/// </summary>
public sealed record LocalParaMercadoPago(
    string Nombre,
    string Calle,
    string Altura,
    string Ciudad,
    string Provincia,
    decimal Latitud,
    decimal Longitud,
    string? Referencia)
{
    public const int LargoMaximoNombre = 60;
    public const int LargoMaximoCalle = DireccionDeSucursal.LargoMaximoCalle;
    public const int LargoMaximoAltura = DireccionDeSucursal.LargoMaximoAltura;
    public const int LargoMaximoCiudad = DireccionDeSucursal.LargoMaximoCiudad;
    public const int LargoMaximoProvincia = DireccionDeSucursal.LargoMaximoProvincia;
    public const int LargoMaximoReferencia = DireccionDeSucursal.LargoMaximoReferencia;

    public const string CodigoDeDireccionIncompleta = "direccion_incompleta_para_mercadopago";

    /// <summary>Alfanumérico, estable y único por sucursal: es el <c>external_id</c> con el que Mercado Pago conoce al local.</summary>
    public static string ExternalIdDe(int idSucursal) => $"WAYSS{idSucursal}";

    /// <summary>Los campos que faltan, en orden, con el nombre de la columna de Ways. Un campo cuyo texto no deja ningún carácter
    /// admitido por Mercado Pago cuenta como faltante.</summary>
    public static IReadOnlyList<string> CamposFaltantes(string? nombre, DireccionDeSucursal direccion)
    {
        var faltantes = new List<string>();

        if (TextoParaMercadoPago.Sanear(nombre, LargoMaximoNombre).Length == 0)
        {
            faltantes.Add("nombre");
        }

        if (TextoParaMercadoPago.Sanear(direccion.Calle, LargoMaximoCalle).Length == 0)
        {
            faltantes.Add("calle");
        }

        if (TextoParaMercadoPago.Sanear(direccion.Altura, LargoMaximoAltura).Length == 0)
        {
            faltantes.Add("altura");
        }

        if (TextoParaMercadoPago.Sanear(direccion.Ciudad, LargoMaximoCiudad).Length == 0)
        {
            faltantes.Add("ciudad");
        }

        if (TextoParaMercadoPago.Sanear(direccion.Provincia, LargoMaximoProvincia).Length == 0)
        {
            faltantes.Add("provincia");
        }

        if (direccion.Latitud is null)
        {
            faltantes.Add("latitud");
        }

        if (direccion.Longitud is null)
        {
            faltantes.Add("longitud");
        }

        return faltantes;
    }

    /// <summary>400 <see cref="CodigoDeDireccionIncompleta"/> nombrando lo que falta, o el local listo para enviar.</summary>
    public static LocalParaMercadoPago Armar(string nombre, DireccionDeSucursal direccion)
    {
        var faltantes = CamposFaltantes(nombre, direccion);
        if (faltantes.Count > 0)
        {
            throw new ErrorDominio(
                CodigoDeDireccionIncompleta,
                $"Para registrar la sucursal en Mercado Pago completá: {string.Join(", ", faltantes)}.",
                400);
        }

        var referencia = TextoParaMercadoPago.Sanear(direccion.Referencia, LargoMaximoReferencia);

        return new LocalParaMercadoPago(
            TextoParaMercadoPago.Sanear(nombre, LargoMaximoNombre),
            TextoParaMercadoPago.Sanear(direccion.Calle, LargoMaximoCalle),
            TextoParaMercadoPago.Sanear(direccion.Altura, LargoMaximoAltura),
            TextoParaMercadoPago.Sanear(direccion.Ciudad, LargoMaximoCiudad),
            TextoParaMercadoPago.Sanear(direccion.Provincia, LargoMaximoProvincia),
            direccion.Latitud!.Value,
            direccion.Longitud!.Value,
            referencia.Length == 0 ? null : referencia);
    }
}
