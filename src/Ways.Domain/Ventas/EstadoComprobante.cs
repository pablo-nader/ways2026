namespace Ways.Domain.Ventas;

/// <summary>
/// Estado de máquina de estados de un <see cref="ComprobanteVenta"/> (doc 10 §4). Enum nativo
/// de Postgres (<c>estado_comprobante</c>), mismo criterio que <c>estado_tenant</c>/
/// <c>estado_usuario</c>. Única transición válida: <see cref="Emitido"/> → <see cref="Anulado"/>
/// (<c>ReglaDeComprobantes.ValidarTransicionAEstado</c>) — nunca al revés, no hay
/// <c>restaurar</c> (doc 10 principio 6).
/// </summary>
public enum EstadoComprobante
{
    Emitido,
    Anulado
}
