using Ways.Domain.Pagos;

namespace Ways.Application.Pagos;

/// <summary>Cuerpo de <c>PUT /api/pagos/administracion/{id}/lineas-de-personal</c>: las líneas nuevas del pago de un asiento del
/// personal. Es el único campo a propósito: el resto del pago (categoría, concepto, origen, importe) pertenece al asiento y no
/// se edita por acá. Las líneas tienen que sumar el importe actual del pago. Nulo si el cuerpo falta.</summary>
public sealed record SolicitudDeLineasDePagoDePersonal(IReadOnlyList<LineaDePago?>? Lineas);
