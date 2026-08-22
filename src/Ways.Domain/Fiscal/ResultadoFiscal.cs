namespace Ways.Domain.Fiscal;

/// <summary>
/// Estados de la máquina CAE (design.md `MaquinaDeEstadosCae`, invariantes I1-I4). Enum nativo
/// de Postgres (<c>resultado_fiscal</c>), mismo criterio que <c>estado_comprobante</c>/
/// <c>estado_remito</c>. Orden de declaración = orden de ciclo de vida = orden de
/// <c>CREATE TYPE</c> (proposal.md §A) — <c>pendiente</c> antes de la llamada a WSFE,
/// <c>aprobado</c>/<c>aprobado_con_observaciones</c>/<c>rechazado</c> desde el mapeo de la
/// respuesta (design.md `MaquinaDeEstadosCae.Mapear`). <c>AprobadoConObservaciones</c> es
/// TERMINAL igual que <c>Aprobado</c> (I3) — una aprobación con observaciones es una factura
/// válida, tratarla como fallo duplicaría documentos en silencio.
/// </summary>
public enum ResultadoFiscal
{
    Pendiente,
    Aprobado,
    AprobadoConObservaciones,
    Rechazado
}
