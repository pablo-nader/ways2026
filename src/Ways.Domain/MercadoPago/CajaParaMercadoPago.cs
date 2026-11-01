namespace Ways.Domain.MercadoPago;

/// <summary>
/// Lo que Ways le manda a Mercado Pago para registrar un punto de venta como caja (<i>POS</i>): el identificador estable y el
/// nombre limpio. Los límites son los de la referencia de la API (<c>name</c> hasta 45, <c>external_id</c> alfanumérico hasta 40).
/// </summary>
public static class CajaParaMercadoPago
{
    public const int LargoMaximoNombre = 45;

    public const int LargoMaximoExternalId = 40;

    public const string CodigoDeNombreInvalido = "nombre_invalido_para_mercadopago";

    /// <summary>Letras y dígitos solamente, estable y único por vendedor: <c>WAYSPV</c> + el id del punto de venta.</summary>
    public static string ExternalIdDe(int idPuntoVenta) => $"WAYSPV{idPuntoVenta}";

    /// <summary>El nombre del punto de venta como lo acepta Mercado Pago, o vacío si no queda nada admitido.</summary>
    public static string NombreDe(string nombre) => TextoParaMercadoPago.Sanear(nombre, LargoMaximoNombre);
}
