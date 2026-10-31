import { api } from './cliente'
import { formatearImporte } from '../formato/importes'

export type AlcanceDeOfertaVigente = 'Articulo' | 'Grupo' | 'Categoria' | 'Familia'

export type OfertaVigente = {
  idOferta: number
  nombre: string
  alcance: AlcanceDeOfertaVigente
  cantidadMinima: number | null
  soloMultiplos: boolean
  porcentaje: number | null
  precioUnitario: number | null
  importeFijo: number | null
  /** yyyy-MM-dd */
  fechaHasta: string | null
  acumulable: boolean
}

export type OfertasVigentesDeArticulo = {
  idArticulo: number
  ofertas: OfertaVigente[]
}

export type SolicitudDeOfertasVigentes = {
  idsArticulos: number[]
  idEmpresa: number
  idListaPrecio?: number
  momento?: string
}

/** `POST /api/ofertas/vigentes`: las ofertas vivas de cada artículo; los que no tienen ninguna no vienen. */
export function obtenerOfertasVigentes(solicitud: SolicitudDeOfertasVigentes): Promise<OfertasVigentesDeArticulo[]> {
  return api.post<OfertasVigentesDeArticulo[]>('/ofertas/vigentes', solicitud)
}

function textoDeBeneficio(o: OfertaVigente): string {
  if (o.porcentaje !== null) return `${o.porcentaje}% off`
  if (o.precioUnitario !== null) return `${formatearImporte(o.precioUnitario, { simbolo: true })} c/u`
  if (o.importeFijo !== null) return `${formatearImporte(o.importeFijo, { simbolo: true })} menos`
  return ''
}

function textoDeCondicion(o: OfertaVigente): string {
  if (o.cantidadMinima === null) return ''
  return o.soloMultiplos ? `cada ${o.cantidadMinima}` : `Llevando ${o.cantidadMinima}`
}

function textoDeFechaHasta(fechaHasta: string | null): string {
  if (fechaHasta === null) return ''
  const partes = /^\d{4}-(\d{2})-(\d{2})/.exec(fechaHasta)
  return partes === null ? '' : `hasta ${partes[2]}/${partes[1]}`
}

/** Una línea corta para el tooltip: "Nombre — beneficio · condición · hasta dd/mm". */
export function textoDeOfertaVigente(o: OfertaVigente): string {
  const detalle = [textoDeBeneficio(o), textoDeCondicion(o), textoDeFechaHasta(o.fechaHasta)].filter((t) => t !== '')
  return detalle.length === 0 ? o.nombre : `${o.nombre} — ${detalle.join(' · ')}`
}
