using Ways.Domain.Catalogos;
using Ways.Domain.Common;

namespace Ways.Domain.Caja;

public enum DestinoDeLineaDePago
{
    Efectivo,
    Cuenta,
    SinCuenta
}

/// <summary>Adónde va el importe de una línea de pago: al efectivo, a una cuenta de tesorería (<see cref="IdCuenta"/>) o a ninguna.</summary>
public readonly record struct DestinoDeLinea(DestinoDeLineaDePago Destino, int? IdCuenta);

/// <summary>Un movimiento de cuenta de un turno ya agrupado por cuenta, medio y tipo.</summary>
public sealed record FilaDeCuentaDeCaja(int IdCuentaTesoreria, int? IdMedioPago, TipoMovimientoCuenta Tipo, decimal Ingreso, decimal Egreso);

public sealed record DesgloseDeMedio(int? IdMedioPago, decimal Importe, decimal Comision, decimal Retenciones, decimal Neto);

public sealed record DesgloseDeCuenta(int IdCuentaTesoreria, decimal Neto, IReadOnlyList<DesgloseDeMedio> Medios);

/// <summary>Reglas puras del libro de tesorería, sin base de datos.</summary>
public static class ReglasDeLibroDeTesoreria
{
    public const int RangoMaximoEnDias = 366;

    public static void ValidarRango(DateTimeOffset desde, DateTimeOffset hasta)
    {
        if (hasta < desde)
        {
            throw new ErrorDominio("rango_invalido", "El campo hasta no puede ser anterior a desde.", 400);
        }

        if ((hasta - desde).TotalDays > RangoMaximoEnDias)
        {
            throw new ErrorDominio(
                "rango_demasiado_amplio", $"El rango no puede superar los {RangoMaximoEnDias} días.", 400);
        }
    }

    /// <summary>El efectivo es el medio de comportamiento efectivo o, en un pago de un turno, el ancla que el turno fijó al cerrar
    /// (el comportamiento del catálogo puede haber cambiado después).</summary>
    public static bool EsMedioEfectivo(ComportamientoMedioPago? comportamiento, int idMedioPago, int? idMedioEfectivoDelTurno) =>
        comportamiento == ComportamientoMedioPago.Efectivo || idMedioEfectivoDelTurno == idMedioPago;

    /// <summary>Una línea que no es efectivo y cuyo medio no acredita en una cuenta de la empresa no se descarta: queda sin cuenta.</summary>
    public static DestinoDeLinea RutearLineaDePago(bool esEfectivo, int? idCuentaDeLaEmpresa) =>
        esEfectivo
            ? new DestinoDeLinea(DestinoDeLineaDePago.Efectivo, null)
            : idCuentaDeLaEmpresa is { } cuenta
                ? new DestinoDeLinea(DestinoDeLineaDePago.Cuenta, cuenta)
                : new DestinoDeLinea(DestinoDeLineaDePago.SinCuenta, null);

    /// <summary>Diferencia de arqueo (declarado menos esperado) a mostrar en la fila de caja: nada cuando es cero o cuando su valor absoluto
    /// no supera la tolerancia de pago del punto de venta (la misma que usa el POS, que acepta hasta ese valor inclusive).</summary>
    public static decimal? DiferenciaDeArqueoVisible(decimal diferencia, decimal tolerancia) =>
        diferencia == 0m || Math.Abs(diferencia) <= tolerancia ? null : diferencia;

    /// <summary>Por cuenta y por medio: importe cobrado, comisión, retenciones y neto. Solo cuentan cobro, comisión y retenciones; un
    /// ajuste, transferencia, depósito o retiro de cuenta no forma parte de un cierre.</summary>
    public static IReadOnlyList<DesgloseDeCuenta> ArmarDesglose(IEnumerable<FilaDeCuentaDeCaja> filas) =>
        filas
            .Where(f => f.Tipo is TipoMovimientoCuenta.Cobro or TipoMovimientoCuenta.Comision or TipoMovimientoCuenta.RetencionIibb
                or TipoMovimientoCuenta.RetencionIva or TipoMovimientoCuenta.RetencionGanancias)
            .GroupBy(f => f.IdCuentaTesoreria)
            .OrderBy(g => g.Key)
            .Select(porCuenta =>
            {
                var medios = porCuenta
                    .GroupBy(f => f.IdMedioPago)
                    .OrderBy(g => g.Key)
                    .Select(porMedio =>
                    {
                        var importe = porMedio.Where(f => f.Tipo == TipoMovimientoCuenta.Cobro).Sum(f => f.Ingreso - f.Egreso);
                        var comision = porMedio.Where(f => f.Tipo == TipoMovimientoCuenta.Comision).Sum(f => f.Egreso - f.Ingreso);
                        var retenciones = porMedio
                            .Where(f => f.Tipo is TipoMovimientoCuenta.RetencionIibb or TipoMovimientoCuenta.RetencionIva
                                or TipoMovimientoCuenta.RetencionGanancias)
                            .Sum(f => f.Egreso - f.Ingreso);
                        return new DesgloseDeMedio(porMedio.Key, importe, comision, retenciones, importe - comision - retenciones);
                    })
                    .ToList();

                return new DesgloseDeCuenta(porCuenta.Key, medios.Sum(m => m.Neto), medios);
            })
            .ToList();
}
