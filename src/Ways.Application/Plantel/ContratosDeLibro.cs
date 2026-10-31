using System.Text.Json.Serialization;
using Ways.Domain.Pagos;
using Ways.Domain.Plantel;

namespace Ways.Application.Plantel;

/// <summary>Cuerpo de <c>POST /api/personal/{id}/horas</c>. <see cref="Fecha"/> es el día trabajado y es
/// obligatoria. <see cref="ValorHora"/> es opcional: si falta se usa el valor hora vigente del personal y
/// en ambos casos queda congelado en el movimiento. <see cref="Horas"/> va de 0 (exclusivo) a 24, con
/// hasta dos decimales.</summary>
public record SolicitudDeCargaDeHoras(DateOnly? Fecha, decimal Horas, decimal? ValorHora, string? Detalle);

/// <summary>Cuerpo de <c>POST /api/personal/{id}/ajustes</c>. <see cref="Importe"/> lleva signo (positivo
/// aumenta lo que se le debe al empleado, negativo lo reduce) y no puede ser cero. <see cref="Detalle"/>
/// es obligatorio. <see cref="Fecha"/> es obligatoria: la manda el cliente con su día local y el servidor nunca la
/// deduce.</summary>
public record SolicitudDeAjusteDePersonal(decimal Importe, string? Detalle, DateOnly? Fecha);

/// <summary><see cref="Desglose"/> solo viene en los movimientos de tipo pago: cómo se compuso ese pago.</summary>
public record MovimientoDePersonal(
    int IdMovimiento,
    DateOnly Fecha,
    TipoMovimientoPersonal Tipo,
    decimal? Horas,
    decimal? ValorHora,
    decimal Importe,
    decimal SaldoResultante,
    string? Detalle,
    DateTimeOffset CreadoEn,
    DesgloseDePago? Desglose = null);

/// <summary>Cuerpo de <c>POST /api/personal/{id}/pagos</c>. <see cref="Fecha"/> es obligatoria: es el día del
/// asiento en el libro del personal y, con origen tesorería, la fecha de negocio de los pagos (puede ser
/// retroactiva, nunca futura). <see cref="DescuentoDeCuentaCorriente"/> baja la deuda del cliente vinculado al
/// personal y no mueve dinero. <see cref="Lineas"/> son las entregas de dinero, cada una un ítem del único pago de sueldos;
/// <see cref="OrigenFondos"/> es obligatorio con líneas y se rechaza sin ellas. <see cref="IdEmpresa"/> solo se
/// indica con origen tesorería; <see cref="IdPuntoVenta"/> es obligatorio con el cajón o con descuento.
/// <see cref="Detalle"/> queda en el asiento del personal.</summary>
public record SolicitudDePagoDePersonal(
    DateOnly? Fecha,
    int? IdEmpresa,
    int? IdPuntoVenta,
    OrigenFondosPago? OrigenFondos,
    decimal? DescuentoDeCuentaCorriente,
    IReadOnlyList<LineaDeSolicitudDePago>? Lineas,
    string? Detalle);

/// <summary><see cref="Referencia"/> (cupón u operación) queda en el ítem de la línea.</summary>
public record LineaDeSolicitudDePago(int IdMedioPago, decimal Importe, string? Referencia);

/// <summary>Composición de un pago: el descuento de cuenta corriente (si hubo) y una línea por cada ítem de
/// cada pago generado. <see cref="LineaDeDesglose.NombreMedioPago"/> sale en <c>null</c> si el medio se dio de baja.</summary>
public record DesgloseDePago(decimal? DescuentoDeCuentaCorriente, IReadOnlyList<LineaDeDesglose> Lineas);

public record LineaDeDesglose(
    [property: JsonPropertyName("idGasto")] int IdPago,
    int IdItem,
    int IdMedioPago,
    string? NombreMedioPago,
    decimal Importe,
    OrigenFondosPago OrigenFondos,
    string? Referencia);

/// <summary><see cref="Saldo"/> sale de <c>personal.saldo</c>, nunca de la suma de la página.</summary>
public record EncabezadoDeCuentaDePersonal(int IdPersonal, string Nombre, decimal Saldo);

/// <summary>Los movimientos se ordenan por fecha y luego por id, del más nuevo al más viejo.
/// Sin <c>historico</c> ni rango, la ventana va desde un mes atrás (contado desde la fecha UTC) y no
/// tiene tope superior, así un movimiento con fecha de hoy en otra zona o a futuro nunca queda oculto.</summary>
public record PaginaDeCuentaDePersonal(
    EncabezadoDeCuentaDePersonal Encabezado,
    IReadOnlyList<MovimientoDePersonal> Items,
    int Total,
    int Pagina,
    int Tamanio,
    bool Historico,
    DateOnly? Desde,
    DateOnly? Hasta);
