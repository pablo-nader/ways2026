/**
 * Cliente de la cuenta del personal: estado de cuenta paginado, carga de horas, ajuste y pago. El
 * estado de cuenta es de `LecturaDePersonal` y las tres escrituras de `GestionDeCuentaDePersonal`
 * (Admin y Supervisor las dos); pagar de la tesorería además exige Admin en el servidor.
 */
import { api } from './cliente'
import { rangoUltimoMes } from './cuentaCorriente'
import { agregarOrdenAlQuery, type OrdenDeServidor } from './reportes'
import type {
  MovimientoDePersonal,
  PaginaDeCuentaDePersonal,
  PersonalListado,
  SolicitudDeAjusteDePersonal,
  SolicitudDeCargaDeHoras,
  SolicitudDePagoDePersonal,
} from './tipos'

/** Claves de `orden` que acepta `GET /api/personal/{id}/cuenta`: una por cada columna de datos del libro. */
export const COLUMNAS_ORDENABLES_DE_CUENTA_DE_PERSONAL = [
  'fecha',
  'tipo',
  'horas',
  'valorHora',
  'importe',
  'saldoResultante',
  'detalle',
] as const

export type ColumnaOrdenableDeCuentaDePersonal = (typeof COLUMNAS_ORDENABLES_DE_CUENTA_DE_PERSONAL)[number]

export type OrdenDeCuentaDePersonal = OrdenDeServidor<ColumnaOrdenableDeCuentaDePersonal>

export function esColumnaOrdenableDeCuentaDePersonal(columna: string): columna is ColumnaOrdenableDeCuentaDePersonal {
  return (COLUMNAS_ORDENABLES_DE_CUENTA_DE_PERSONAL as readonly string[]).includes(columna)
}

export type FiltrosDeCuentaDePersonal = {
  /** `YYYY-MM-DD`. Vacío no pide "sin límite": el servidor aplica su default de un mes atrás, y la única
   * forma de quitar el límite inferior es `historico`. */
  desde: string
  /** `YYYY-MM-DD` o `''` (sin tope superior). */
  hasta: string
  historico: boolean
  pagina: number
  tamanio: number
  /** Opt-in: ausente o `null` = el orden del libro (fecha descendente). Ordenar nunca recalcula saldos. */
  orden?: OrdenDeCuentaDePersonal
}

/** Mismo default que el servidor: desde un mes atrás y sin tope superior, así un movimiento con fecha
 * de hoy en otra zona o a futuro nunca queda oculto. */
export function filtrosDeCuentaDePersonalIniciales(ahora: Date = new Date()): FiltrosDeCuentaDePersonal {
  return { desde: rangoUltimoMes(ahora).desde, hasta: '', historico: false, pagina: 1, tamanio: 25 }
}

/** Los filtros con el rango EFECTIVO que aplicó el servidor: un "Desde" vacío se ve como la ventana de
 * un mes que el servidor usó en su lugar. Con `historico` el servidor no devuelve rango y los filtros
 * quedan como están. */
export function conElRangoEfectivo(
  filtros: FiltrosDeCuentaDePersonal,
  respuesta: Pick<PaginaDeCuentaDePersonal, 'historico' | 'desde' | 'hasta'>,
): FiltrosDeCuentaDePersonal {
  if (respuesta.historico) return filtros

  return { ...filtros, desde: respuesta.desde ?? '', hasta: respuesta.hasta ?? '' }
}

/** Ensancha el rango para que una fecha recién escrita figure en la tabla; `ampliado` dice si hubo que
 * hacerlo. Con `historico`, o con un "Desde" vacío (el servidor decide el suyo), no hay nada que ensanchar. */
export function ampliarRangoParaFecha(
  filtros: FiltrosDeCuentaDePersonal,
  fecha: string,
): { filtros: FiltrosDeCuentaDePersonal; ampliado: boolean } {
  if (filtros.historico) return { filtros, ampliado: false }

  const antes = filtros.desde !== '' && fecha < filtros.desde
  const despues = filtros.hasta !== '' && fecha > filtros.hasta
  if (!antes && !despues) return { filtros, ampliado: false }

  return {
    filtros: { ...filtros, desde: antes ? fecha : filtros.desde, hasta: despues ? fecha : filtros.hasta },
    ampliado: true,
  }
}

/** El servidor recibe `DateOnly`: las fechas viajan planas, sin hora ni desplazamiento. Con `historico`
 * el rango no viaja, porque el servidor lo ignora. Un límite vacío no viaja: sin `desde` el servidor
 * aplica su default de un mes. */
export function construirQueryDeCuentaDePersonal(filtros: FiltrosDeCuentaDePersonal): string {
  const parametros = new URLSearchParams()

  if (filtros.historico) {
    parametros.set('historico', 'true')
  } else {
    if (filtros.desde) parametros.set('desde', filtros.desde)
    if (filtros.hasta) parametros.set('hasta', filtros.hasta)
  }

  agregarOrdenAlQuery(parametros, filtros.orden ?? null)
  parametros.set('pagina', String(filtros.pagina))
  parametros.set('tamanio', String(filtros.tamanio))

  return `?${parametros.toString()}`
}

export const clienteDeCuentaDePersonal = {
  /** `GET /api/personal/{id}`: valor hora vigente y cliente vinculado, que el estado de cuenta no trae. */
  obtenerPersonal: (id: number) => api.get<PersonalListado>(`/personal/${id}`),
  obtener: (id: number, filtros: FiltrosDeCuentaDePersonal) =>
    api.get<PaginaDeCuentaDePersonal>(`/personal/${id}/cuenta${construirQueryDeCuentaDePersonal(filtros)}`),
  cargarHoras: (id: number, solicitud: SolicitudDeCargaDeHoras) =>
    api.post<MovimientoDePersonal>(`/personal/${id}/horas`, solicitud),
  registrarAjuste: (id: number, solicitud: SolicitudDeAjusteDePersonal) =>
    api.post<MovimientoDePersonal>(`/personal/${id}/ajustes`, solicitud),
  pagar: (id: number, solicitud: SolicitudDePagoDePersonal) =>
    api.post<MovimientoDePersonal>(`/personal/${id}/pagos`, solicitud),
}
