using System.Linq.Expressions;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Plantel;

namespace Ways.Application.Listados;

/// <summary>Etiquetas en español de la columna Tipo de los libros de cuenta, con las que los listados ordenan (se
/// ordena por lo que la pantalla muestra). Espejo de <c>etiquetaDeTipoDeMovimiento</c> (proveedores) y
/// <c>etiquetaDeTipo</c> (personal) en <c>Ways.Web</c>; <c>EtiquetasDeMovimientoDeCuentaTests</c> (backend) y
/// <c>etiquetasDeMovimientoDeCuenta.test.ts</c> (web) fijan la misma lista literal.</summary>
public static class EtiquetasDeMovimientoDeCuenta
{
    public const string AjusteManual = "Ajuste manual";
    public const string ContramovimientoDeAnulacion = "Contramov. de anulación";

    /// <summary>Un ajuste del libro de proveedores se rotula según tenga o no comprobante (ver
    /// <see cref="ContramovimientoDeAnulacion"/>); su entrada acá es la de un ajuste manual.</summary>
    public static readonly IReadOnlyDictionary<TipoMovimientoCcProveedor, string> Proveedor =
        new Dictionary<TipoMovimientoCcProveedor, string>
        {
            [TipoMovimientoCcProveedor.Apertura] = "Apertura",
            [TipoMovimientoCcProveedor.Compra] = "Compra",
            [TipoMovimientoCcProveedor.Pago] = "Pago",
            [TipoMovimientoCcProveedor.Ajuste] = AjusteManual,
        };

    public static readonly IReadOnlyDictionary<TipoMovimientoPersonal, string> Personal =
        new Dictionary<TipoMovimientoPersonal, string>
        {
            [TipoMovimientoPersonal.Horas] = "Horas trabajadas",
            [TipoMovimientoPersonal.Pago] = "Pago",
            [TipoMovimientoPersonal.Ajuste] = "Ajuste",
        };

    /// <summary>Selector del texto de la columna Tipo del libro de proveedores: un ajuste con comprobante de compra
    /// es el contramovimiento de una anulación, el resto sale de <see cref="Proveedor"/>.</summary>
    public static Expression<Func<MovimientoCuentaCorrienteProveedor, string>> SelectorDeTipoDeProveedor()
    {
        var movimiento = Expression.Parameter(typeof(MovimientoCuentaCorrienteProveedor), "m");
        var tipo = Expression.Property(movimiento, nameof(MovimientoCuentaCorrienteProveedor.Tipo));
        var comprobante = Expression.Property(movimiento, nameof(MovimientoCuentaCorrienteProveedor.IdComprobanteCompra));

        var esContramovimiento = Expression.AndAlso(
            Expression.Equal(tipo, Expression.Constant(TipoMovimientoCcProveedor.Ajuste)),
            Expression.NotEqual(comprobante, Expression.Constant(null, typeof(int?))));

        var cuerpo = Expression.Condition(
            esContramovimiento,
            Expression.Constant(ContramovimientoDeAnulacion),
            EtiquetaPorCaso.De(tipo, Proveedor, Expression.Constant(string.Empty)));

        return Expression.Lambda<Func<MovimientoCuentaCorrienteProveedor, string>>(cuerpo, movimiento);
    }
}
