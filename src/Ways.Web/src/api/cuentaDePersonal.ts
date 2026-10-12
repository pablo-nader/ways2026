/**
 * Cliente de la cuenta del personal: estado de cuenta paginado, carga de horas, ajuste y pago. El
 * estado de cuenta es de `LecturaDePersonal` y las tres escrituras de `GestionDeCuentaDePersonal`
 * (Admin y Supervisor las dos); pagar de la tesorería además exige Admin en el servidor.
 */
import { api } from './cliente'
import { rangoUltimoMes } from './cuentaCorriente'
import type {
  MovimientoDePersonal,
  PaginaDeCuentaDePersonal,
  PersonalListado,
  SolicitudDeAjusteDePersonal,
  SolicitudDeCargaDeHoras,
  SolicitudDePagoDePersonal,
} from './tipos'

export type FiltrosDeCuentaDePersonal = {
  /** `YYYY-MM-DD` o `''` (sin límite). */
  desde: string
  /** `YYYY-MM-DD` o `''` (sin límite). */
  hasta: string
  historico: boolean
  pagina: number
  tamanio: number
}

/** Mismo default que el servidor: desde un mes atrás y sin tope superior, así un movimiento con fecha
 * de hoy en otra zona o a futuro nunca queda oculto. */
export function filtrosDeCuentaDePersonalIniciales(ahora: Date = new Date()): FiltrosDeCuentaDePersonal {
  return { desde: rangoUltimoMes(ahora).desde, hasta: '', historico: false, pagina: 1, tamanio: 25 }
}

/** El servidor recibe `DateOnly`: las fechas viajan planas, sin hora ni desplazamiento. Con `historico`
 * el rango no viaja, porque el servidor lo ignora. */
export function construirQueryDeCuentaDePersonal(filtros: FiltrosDeCuentaDePersonal): string {
  const parametros = new URLSearchParams()

  if (filtros.historico) {
    parametros.set('historico', 'true')
  } else {
    if (filtros.desde) parametros.set('desde', filtros.desde)
    if (filtros.hasta) parametros.set('hasta', filtros.hasta)
  }

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
