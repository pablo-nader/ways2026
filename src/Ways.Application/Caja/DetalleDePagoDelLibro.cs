namespace Ways.Application.Caja;

/// <summary>El texto que identifica a quién va un pago en el libro: el proveedor o la persona del pago y, si no tiene
/// ninguno, el concepto. La categoría viaja aparte, en su propio campo. Un proveedor o una persona dados de baja siguen
/// referenciados por el pago: se muestran como no disponibles.</summary>
public static class DetalleDePagoDelLibro
{
    public const string ProveedorNoDisponible = "(proveedor no disponible)";
    public const string PersonalNoDisponible = "(persona no disponible)";

    public static string Armar(
        string concepto, int? idProveedor, string? nombreProveedor, int? idMovimientoPersonal, string? nombrePersonal)
    {
        return idProveedor is not null
            ? nombreProveedor ?? ProveedorNoDisponible
            : idMovimientoPersonal is not null ? nombrePersonal ?? PersonalNoDisponible : concepto;
    }
}
