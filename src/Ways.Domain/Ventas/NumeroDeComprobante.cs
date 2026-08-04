namespace Ways.Domain.Ventas;

/// <summary>
/// Formatea el número visible de un comprobante (design: API Surface — <c>PPPP-NNNNNNNN</c>).
/// <c>PPPP</c> es el <c>id_punto_venta</c> zero-padded — una identidad global, no un número de
/// negocio por empresa (design's Open Questions: <c>puntos_venta</c> no tiene columna
/// <c>numero</c> todavía; inofensivo mientras TX/NCX sean no fiscales). Sin bound superior: un
/// id o un número que excedan 4/8 dígitos simplemente imprimen más dígitos, no se truncan.
/// </summary>
public static class NumeroDeComprobante
{
    public static string Formatear(int idPuntoVenta, long numero) =>
        $"{idPuntoVenta:D4}-{numero:D8}";
}
