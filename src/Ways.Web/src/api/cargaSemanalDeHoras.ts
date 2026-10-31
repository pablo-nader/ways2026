/**
 * Cliente de la carga semanal de horas del personal: `GET` y `POST /api/personal/horas/semana`
 * (Admin y Supervisor, las mismas políticas que la carga de horas de la cuenta).
 */
import { api } from './cliente'
import type { ResultadoDeCargaSemanalDeHoras, SemanaDeCargaDeHoras, SolicitudDeCargaSemanalDeHoras } from './tipos'

/** Sin `desde` el servidor responde la semana en curso. Las fechas viajan planas (`DateOnly`). */
export function rutaDeSemana(desde: string | null): string {
  return desde === null ? '/personal/horas/semana' : `/personal/horas/semana?desde=${encodeURIComponent(desde)}`
}

export const clienteDeCargaSemanalDeHoras = {
  obtener: (desde: string | null) => api.get<SemanaDeCargaDeHoras>(rutaDeSemana(desde)),
  guardar: (solicitud: SolicitudDeCargaSemanalDeHoras) =>
    api.post<ResultadoDeCargaSemanalDeHoras>('/personal/horas/semana', solicitud),
}
