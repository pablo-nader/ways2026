/**
 * Cliente de `POST /api/fiscal/facturaciones/procesar` (política `OperacionDePos`): pide al
 * servidor que procese las facturaciones pendientes de un punto de venta. El POS no lee la
 * respuesta (el estado de cada venta se consulta aparte), así que no se tipa.
 */
import { api } from './cliente'

export const clienteDeFacturacionFiscal = {
  /** El cuerpo lleva solo el punto de venta: el máximo por llamada lo decide el servidor. */
  procesarLote: (idPuntoVenta: number, senal?: AbortSignal) =>
    api.post<unknown>('/fiscal/facturaciones/procesar', { idPuntoVenta }, undefined, senal),
}
