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

public record MovimientoDePersonal(
    int IdMovimiento,
    DateOnly Fecha,
    TipoMovimientoPersonal Tipo,
    decimal? Horas,
    decimal? ValorHora,
    decimal Importe,
    decimal SaldoResultante,
    string? Detalle,
    DateTimeOffset CreadoEn);

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
