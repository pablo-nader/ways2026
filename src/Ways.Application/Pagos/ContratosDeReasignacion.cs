namespace Ways.Application.Pagos;

/// <summary>Cuerpo de <c>POST /api/pagos/administracion/{idPago}/reasignar-personal</c>: el empleado destino. Nulo si el cuerpo
/// falta o no trae el campo.</summary>
public sealed record SolicitudDeReasignacionDePago(int? IdPersonal);

/// <summary>Respuesta de la reasignación: el empleado anterior, el nuevo y el concepto del pago ya con el nombre nuevo.</summary>
public sealed record ResultadoDeReasignacionDePago(int IdPago, int IdPersonalAnterior, int IdPersonal, string Concepto);
