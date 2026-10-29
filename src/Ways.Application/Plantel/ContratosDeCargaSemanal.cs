namespace Ways.Application.Plantel;

/// <summary>Un día de la grilla semanal. <see cref="HorasCargadas"/> es la suma de los asientos de horas del día
/// en el libro. <see cref="Confirmado"/> es verdadero si existe la jornada o si el libro tiene horas ese día
/// (cargas individuales anteriores a la grilla). <see cref="Horas"/> es lo que la grilla muestra: lo cargado si
/// está confirmado; si no, la precarga del horario habitual (o <c>null</c> sin horario, o en un día futuro).
/// <see cref="Precargado"/> marca que <see cref="Horas"/> sale del horario habitual y todavía no se guardó.
/// <see cref="Futuro"/> es relativo a la fecha local del personal: un día futuro no se precarga ni se acepta.</summary>
public record DiaDeCargaSemanal(
    DateOnly Fecha,
    decimal HorasCargadas,
    bool Confirmado,
    decimal? Horas,
    bool Precargado,
    bool Futuro);

public record FilaDeCargaSemanal(
    int IdPersonal,
    string Nombre,
    decimal ValorHora,
    decimal[]? HorarioHabitual,
    IReadOnlyList<DiaDeCargaSemanal> Dias);

/// <summary>Respuesta de <c>GET /api/personal/horas/semana</c>: lunes a domingo por cada personal vivo del
/// tenant, ordenado por nombre. <see cref="Hoy"/> es la fecha local de la zona horaria por defecto; cada día
/// usa la zona de la empresa de su personal para <see cref="DiaDeCargaSemanal.Futuro"/>.</summary>
public record SemanaDeCargaDeHoras(
    DateOnly Desde,
    DateOnly Hasta,
    DateOnly Hoy,
    IReadOnlyList<FilaDeCargaSemanal> Filas);

/// <summary>Un día a guardar: <see cref="Horas"/> es el TOTAL deseado del día (de 0 a 24, hasta dos decimales),
/// no una diferencia; 0 confirma el día sin horas. Ambos campos y <see cref="Fecha"/> son obligatorios.</summary>
public record DiaACargarSemanal(int IdPersonal, DateOnly? Fecha, decimal? Horas);

/// <summary>Cuerpo de <c>POST /api/personal/horas/semana</c>: entre 1 y 1000 días, sin repetir
/// <c>(idPersonal, fecha)</c>, ninguno posterior a hoy.</summary>
public record SolicitudDeCargaSemanal(IReadOnlyList<DiaACargarSemanal>? Dias);

/// <summary><see cref="MovimientosCreados"/> son los asientos nuevos del libro (los días sin diferencia no
/// escriben nada); <see cref="JornadasConfirmadas"/> son los días de la solicitud, todos quedan confirmados.</summary>
public record ResultadoDeCargaSemanal(int MovimientosCreados, int JornadasConfirmadas);
