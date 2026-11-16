/**
 * Cliente de las devoluciones del POS: espejo de `Ways.Application.Ventas.ContratosDeDevolucion`. El ticket de origen con lo que queda
 * por devolver, y la previsualización del crédito y del reintegro en efectivo. La emisión de la nota de crédito es `POST /api/ventas`
 * (`clienteDeVentas.emitir`), no se repite acá.
 */
import { api } from './cliente'
import type { LineaDeVenta } from './tipos'

/** Por qué un ticket no admite una devolución (`TicketDevolvible.motivo`); `null` si la admite. */
export type MotivoDeNoDevolvible = 'anulado' | 'con_factura_fiscal' | 'sin_unidades_por_devolver' | 'no_es_un_ticket_de_venta'

/** Un artículo del ticket de origen. `importe` solo viene en un artículo por importe y la línea de la nota de crédito lo reenvía. */
export type LineaDevolvible = {
  idArticulo: number
  descripcion: string
  codigoBarra: string | null
  importe: number | null
  cantidadVendida: number
  cantidadDevuelta: number
  cantidadDevolvible: number
  importeUnitario: number
  importeDeLoRestante: number
}

export type TicketDevolvible = {
  id: number
  numero: number
  numeroVisible: string
  fecha: string
  idPuntoVenta: number
  idCliente: number
  nombreCliente: string | null
  total: number
  devolvible: boolean
  motivo: MotivoDeNoDevolvible | null
  lineas: LineaDevolvible[]
}

export type SolicitudDePrevisualizacionDeDevolucion = {
  idPuntoVenta: number
  idComprobanteAsociado: number
  lineas: LineaDeVenta[]
}

/** El crédito (positivo) de cada línea mandada, en el mismo orden. */
export type LineaPrevisualizadaDeDevolucion = { idArticulo: number; cantidad: number; credito: number }

export type PrevisualizacionDeDevolucion = { lineas: LineaPrevisualizadaDeDevolucion[]; reintegro: number }

export const clienteDeDevoluciones = {
  /** `GET /api/ventas/devolvibles`: 404 si el ticket no existe en ese punto de venta. */
  buscarPorNumero: (idPuntoVenta: number, numero: number) =>
    api.get<TicketDevolvible>(`/ventas/devolvibles?idPuntoVenta=${idPuntoVenta}&numero=${numero}`),
  /** `GET /api/ventas/{id}/devolvibles`: lo mismo para un ticket ya elegido de un listado. */
  obtener: (idTicket: number) => api.get<TicketDevolvible>(`/ventas/${idTicket}/devolvibles`),
  /** `POST /api/ventas/devoluciones/previsualizacion`: lectura pura, con los mismos rechazos que la emisión. */
  previsualizar: (solicitud: SolicitudDePrevisualizacionDeDevolucion, senal?: AbortSignal) =>
    senal
      ? api.post<PrevisualizacionDeDevolucion>('/ventas/devoluciones/previsualizacion', solicitud, undefined, senal)
      : api.post<PrevisualizacionDeDevolucion>('/ventas/devoluciones/previsualizacion', solicitud),
}
