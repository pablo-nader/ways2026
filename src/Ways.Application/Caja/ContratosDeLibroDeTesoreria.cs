using Ways.Domain.Caja;
using Ways.Domain.Pagos;

namespace Ways.Application.Caja;

public enum TipoDeFilaDelLibro
{
    Caja,
    Pago,
    AjusteEfectivo,
    AjusteCuenta
}

public sealed record CuentaDelLibro(int IdCuentaTesoreria, string Nombre, bool Activo);

public sealed record ImporteDeCuenta(int IdCuentaTesoreria, decimal Importe);

/// <summary>Cuánto hay en el efectivo y en cada cuenta. <see cref="PorCuenta"/> trae todas las cuentas del libro, también las
/// que están en cero.</summary>
public sealed record SaldosDelLibro(decimal Efectivo, IReadOnlyList<ImporteDeCuenta> PorCuenta);

public sealed record DesgloseDeMedioDelLibro(
    int? IdMedioPago, string? Medio, decimal Importe, decimal Comision, decimal Retenciones, decimal Neto);

public sealed record DesgloseDeCuentaDelLibro(int IdCuentaTesoreria, decimal Neto, IReadOnlyList<DesgloseDeMedioDelLibro> Medios);

/// <summary>Datos de la fila de un turno cerrado. <see cref="Numero"/> es el número con el que lo muestra el histórico de cajas
/// (el id del turno). <see cref="DiferenciaArqueo"/> es declarado menos esperado (el faltante es negativo) y viene
/// <c>null</c> cuando es cero o no supera la tolerancia de pago del punto de venta.</summary>
public sealed record DatosDeCajaDelLibro(
    int IdTurnoCaja,
    int Numero,
    DateTimeOffset FechaApertura,
    DateTimeOffset FechaCierre,
    int IdPuntoVenta,
    string? PuntoVenta,
    decimal? DiferenciaArqueo,
    IReadOnlyList<DesgloseDeCuentaDelLibro> Desglose);

public sealed record DatosDePagoDelLibro(
    int IdPago, string Detalle, CategoriaPago Categoria, OrigenFondosPago OrigenFondos, int? IdTurnoCaja);

public sealed record DatosDeAjusteDelLibro(string Concepto);

/// <summary>Una fila del libro. <see cref="Efectivo"/> es <c>null</c> cuando la fila no toca el efectivo; <see cref="Cuentas"/> trae solo
/// importes distintos de cero; <see cref="SinCuenta"/> es lo pagado con un medio que no es efectivo ni acredita en una cuenta de la
/// empresa (<c>null</c> si no hay). Exactamente uno de <see cref="Caja"/>, <see cref="Pago"/> y <see cref="Ajuste"/> viene poblado,
/// según <see cref="Tipo"/>.</summary>
public sealed record FilaDelLibro(
    DateTimeOffset Fecha,
    TipoDeFilaDelLibro Tipo,
    decimal? Efectivo,
    IReadOnlyList<ImporteDeCuenta> Cuentas,
    decimal? SinCuenta,
    DatosDeCajaDelLibro? Caja,
    DatosDePagoDelLibro? Pago,
    DatosDeAjusteDelLibro? Ajuste);

public sealed record LibroDeTesoreria(
    int IdEmpresa,
    DateTimeOffset Desde,
    DateTimeOffset Hasta,
    IReadOnlyList<CuentaDelLibro> Cuentas,
    SaldosDelLibro SaldoAnterior,
    SaldosDelLibro SaldoActual,
    IReadOnlyList<FilaDelLibro> Filas);
