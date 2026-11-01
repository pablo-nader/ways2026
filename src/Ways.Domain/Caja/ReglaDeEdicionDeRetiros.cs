using Ways.Domain.Common;

namespace Ways.Domain.Caja;

/// <summary>
/// Reglas puras de la edición y la baja administrativa de un retiro (<see cref="MovimientoCaja"/> de tipo
/// <see cref="TipoMovimientoCaja.Retiro"/>). El importe y el motivo nuevos se validan con <see cref="ReglaDeMovimientosDeCaja"/>, las
/// mismas reglas del alta.
/// </summary>
public static class ReglaDeEdicionDeRetiros
{
    /// <summary>El retiro que escribe el cierre por retiro es parte del cierre: corregirlo es corregir el cierre.</summary>
    public static void ExigirEditable(bool esRetiroDeCierre)
    {
        if (esRetiroDeCierre)
        {
            throw new ErrorDominio(
                "retiro_de_cierre_no_editable", "El retiro del cierre de turno no se puede editar ni eliminar.", 409);
        }
    }

    /// <summary>El cierre de un turno acreditó a la tesorería la suma de sus retiros; un retiro editado o dado de baja (<paramref
    /// name="importeNuevo"/> <c>null</c>) después del cierre compensa la diferencia. <c>null</c> si el importe no cambió.</summary>
    public static (decimal Ingreso, decimal Egreso)? AjusteDeTesoreria(decimal importeAnterior, decimal? importeNuevo)
    {
        var diferencia = (importeNuevo ?? 0m) - importeAnterior;
        return diferencia switch
        {
            > 0m => (diferencia, 0m),
            < 0m => (0m, -diferencia),
            _ => null
        };
    }

    /// <summary>El libro de tesorería asienta los retiros de un turno en la fecha de su cierre. Con el turno abierto todavía no
    /// están en el libro y van a entrar con un cierre posterior a <paramref name="ahora"/>, así que cuenta <paramref name="ahora"/>.</summary>
    public static DateTimeOffset FechaQueCuentaParaElLibro(DateTimeOffset? fechaCierre, DateTimeOffset ahora) =>
        fechaCierre ?? ahora;
}
