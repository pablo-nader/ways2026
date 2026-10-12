using Ways.Domain.Common;
using Ways.Domain.Gastos;

namespace Ways.Domain.Plantel;

/// <summary>Una entrega de dinero del pago: se vuelve un gasto de categoría sueldos. El medio de pago y su
/// referencia (cupón u operación) los elige quien paga.</summary>
public sealed record LineaDePagoDePersonal(int IdMedioPago, decimal Importe, string? Referencia);

/// <summary>Pago ya validado y normalizado: lo que el servicio escribe en una sola transacción.</summary>
public sealed record PlanDePagoDePersonal(
    decimal? DescuentoDeCuentaCorriente,
    IReadOnlyList<LineaDePagoDePersonal> Lineas,
    OrigenFondosGasto? OrigenFondos,
    decimal Total,
    string? Detalle);

/// <summary>
/// Combinaciones legales de un pago de personal y su total, sin base de datos. Un pago tiene un descuento
/// de la cuenta corriente del cliente vinculado (a lo sumo uno, no mueve dinero), líneas de dinero (cero o
/// más, cada una un gasto) o ambos, pero no puede estar vacío. El origen de los fondos solo existe cuando
/// hay líneas de dinero. Los límites de importe son los de <c>numeric(14,2)</c>.
/// </summary>
public static class ReglaDePagoDePersonal
{
    public const int MaximoDeLineas = 20;
    public const int LongitudMaximaDeReferencia = 100;

    public static PlanDePagoDePersonal Planificar(
        decimal? descuento, IReadOnlyList<LineaDePagoDePersonal>? lineas, OrigenFondosGasto? origenFondos, string? detalle)
    {
        lineas ??= [];

        if (descuento is null && lineas.Count == 0)
        {
            throw new ErrorDominio(
                "pago_personal_vacio", "El pago necesita un descuento de cuenta corriente o al menos una línea de dinero.", 400);
        }

        ValidarOrigen(lineas.Count, origenFondos);

        if (descuento is { } descuentoPedido && !EsImporteValido(descuentoPedido))
        {
            throw new ErrorDominio(
                "pago_personal_descuento_invalido",
                "El descuento debe ser mayor que 0 y de hasta 999999999999.99, con hasta dos decimales.", 400);
        }

        if (lineas.Count > MaximoDeLineas)
        {
            throw new ErrorDominio(
                "pago_personal_demasiadas_lineas", $"Un pago admite hasta {MaximoDeLineas} líneas de dinero.", 400);
        }

        var normalizadas = lineas.Select(NormalizarLinea).ToList();
        var total = (descuento ?? 0m) + normalizadas.Sum(l => l.Importe);

        if (total > ReglaDeMovimientoDePersonal.ImporteMaximo)
        {
            throw new ErrorDominio("importe_fuera_de_rango", "El total del pago supera el máximo admitido.", 400);
        }

        return new PlanDePagoDePersonal(
            descuento, normalizadas, origenFondos, total, ReglaDeMovimientoDePersonal.NormalizarDetalleOpcional(detalle));
    }

    /// <summary>Qué datos de ubicación acepta cada combinación. La empresa solo se pide para pagar de la
    /// tesorería sin pasar por un punto de venta; en el resto sale del punto de venta, así que mandarla sería un
    /// dato aceptado y descartado. El cajón (turno) y el descuento (el asiento de cuenta corriente lleva punto de
    /// venta) exigen el punto de venta.</summary>
    public static void ExigirUbicacion(PlanDePagoDePersonal plan, int? idEmpresa, int? idPuntoVenta)
    {
        var tesoreria = plan.OrigenFondos == OrigenFondosGasto.Tesoreria;

        if (tesoreria && idEmpresa is null)
        {
            throw new ErrorDominio(
                "pago_personal_empresa_requerida", "Un pago de la tesorería tiene que indicar la empresa.", 400);
        }

        if (!tesoreria && idEmpresa is not null)
        {
            throw new ErrorDominio(
                "pago_personal_empresa_no_aplica",
                "La empresa solo se indica en un pago de la tesorería; en los demás sale del punto de venta.", 400);
        }

        var exigePuntoVenta = plan.OrigenFondos == OrigenFondosGasto.CajaTurno || plan.DescuentoDeCuentaCorriente is not null;
        if (exigePuntoVenta && idPuntoVenta is null)
        {
            throw new ErrorDominio(
                "pago_personal_punto_venta_requerido",
                "Un pago del cajón o con descuento de cuenta corriente tiene que indicar el punto de venta.", 400);
        }
    }

    private static void ValidarOrigen(int cantidadDeLineas, OrigenFondosGasto? origenFondos)
    {
        if (cantidadDeLineas > 0 && origenFondos is null)
        {
            throw new ErrorDominio(
                "pago_personal_origen_requerido", "Un pago con líneas de dinero tiene que indicar el origen de los fondos.", 400);
        }

        if (cantidadDeLineas == 0 && origenFondos is not null)
        {
            throw new ErrorDominio(
                "pago_personal_origen_sin_lineas", "Un pago sin líneas de dinero no lleva origen de fondos.", 400);
        }
    }

    private static LineaDePagoDePersonal NormalizarLinea(LineaDePagoDePersonal? linea)
    {
        if (linea is null || !EsImporteValido(linea.Importe))
        {
            throw new ErrorDominio(
                "pago_personal_importe_invalido",
                "El importe de cada línea debe ser mayor que 0 y de hasta 999999999999.99, con hasta dos decimales.", 400);
        }

        var referencia = linea.Referencia?.Trim();
        if (string.IsNullOrEmpty(referencia))
        {
            return linea with { Referencia = null };
        }

        return referencia.Length <= LongitudMaximaDeReferencia
            ? linea with { Referencia = referencia }
            : throw new ErrorDominio(
                "pago_personal_referencia_muy_larga",
                $"La referencia no puede superar los {LongitudMaximaDeReferencia} caracteres.", 400);
    }

    private static bool EsImporteValido(decimal importe) =>
        importe > 0 && importe <= ReglaDeMovimientoDePersonal.ImporteMaximo && decimal.Round(importe, 2) == importe;
}
