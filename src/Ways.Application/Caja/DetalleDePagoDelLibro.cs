using Ways.Domain.Pagos;

namespace Ways.Application.Caja;

/// <summary>El texto que identifica un pago en el libro: lo mismo que muestra la pantalla de pagos (categoría y concepto) más el
/// proveedor o la persona a quien se pagó. Un proveedor o una persona dados de baja siguen referenciados por el pago: se muestran como
/// no disponibles, igual que la pantalla de pagos.</summary>
public static class DetalleDePagoDelLibro
{
    public const string ProveedorNoDisponible = "(proveedor no disponible)";
    public const string PersonalNoDisponible = "(persona no disponible)";

    public static string Armar(
        CategoriaPago categoria, string concepto, int? idProveedor, string? nombreProveedor, int? idMovimientoPersonal,
        string? nombrePersonal)
    {
        var texto = $"{EtiquetasDeCategoriaDePago.De(categoria)} — {concepto}";

        if (idProveedor is not null)
        {
            texto += $" · {nombreProveedor ?? ProveedorNoDisponible}";
        }

        if (idMovimientoPersonal is not null)
        {
            texto += $" · {nombrePersonal ?? PersonalNoDisponible}";
        }

        return texto;
    }
}
