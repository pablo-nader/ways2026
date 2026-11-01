using Ways.Domain.Pagos;

namespace Ways.Application.Caja;

/// <summary>El texto que identifica un pago en el libro: la categoría y, si el pago tiene proveedor o persona, su nombre; si no,
/// el concepto. Un proveedor o una persona dados de baja siguen referenciados por el pago: se muestran como
/// no disponibles.</summary>
public static class DetalleDePagoDelLibro
{
    public const string ProveedorNoDisponible = "(proveedor no disponible)";
    public const string PersonalNoDisponible = "(persona no disponible)";

    public static string Armar(
        CategoriaPago categoria, string concepto, int? idProveedor, string? nombreProveedor, int? idMovimientoPersonal,
        string? nombrePersonal)
    {
        var destinatario = idProveedor is not null
            ? nombreProveedor ?? ProveedorNoDisponible
            : idMovimientoPersonal is not null ? nombrePersonal ?? PersonalNoDisponible : concepto;

        return $"{EtiquetasDeCategoriaDePago.De(categoria)} — {destinatario}";
    }
}
