import { api, ErrorApi } from './cliente'

export type SolicitudDeAjusteDeEfectivo = { idEmpresa: number; concepto: string; importe: number }

export type ResultadoDeAjusteDeEfectivo = { idMovimiento: number; fecha: string; concepto: string; importe: number }

export const clienteDeTesoreria = {
  /** `POST /api/tesoreria/efectivo/ajustes` (Admin): importe con signo distinto de cero y concepto obligatorio. Sin clave de idempotencia. */
  ajustarEfectivo: (solicitud: SolicitudDeAjusteDeEfectivo) =>
    api.post<ResultadoDeAjusteDeEfectivo>('/tesoreria/efectivo/ajustes', solicitud),
}

export const LARGO_MAXIMO_DE_CONCEPTO_DE_AJUSTE_DE_EFECTIVO = 200

export type FormularioDeAjusteDeEfectivo = { importe: number | null; concepto: string }

export type CampoDeAjusteDeEfectivo = 'importe' | 'concepto' | 'general'

export type ErrorDeAjusteDeEfectivo = { campo: CampoDeAjusteDeEfectivo; mensaje: string }

export function formularioDeAjusteDeEfectivoVacio(): FormularioDeAjusteDeEfectivo {
  return { importe: null, concepto: '' }
}

const COPIA_GENERICA = 'No se pudo registrar el ajuste.'
const COPIA_IMPORTE = 'Ingresá un importe distinto de cero, con hasta dos decimales.'
const COPIA_CONCEPTO = 'Ingresá el concepto del ajuste.'

/** Solo formato y obligatoriedad: la decisión final es del servidor. */
export function validarAjusteDeEfectivo(f: FormularioDeAjusteDeEfectivo): ErrorDeAjusteDeEfectivo | null {
  const concepto = f.concepto.trim()
  if (concepto === '') return { campo: 'concepto', mensaje: COPIA_CONCEPTO }
  if (concepto.length > LARGO_MAXIMO_DE_CONCEPTO_DE_AJUSTE_DE_EFECTIVO) {
    return {
      campo: 'concepto',
      mensaje: `El concepto no puede superar los ${LARGO_MAXIMO_DE_CONCEPTO_DE_AJUSTE_DE_EFECTIVO} caracteres.`,
    }
  }

  if (f.importe === null || Math.round(f.importe * 100) === 0) return { campo: 'importe', mensaje: COPIA_IMPORTE }
  return null
}

export function aSolicitudDeAjusteDeEfectivo(idEmpresa: number, f: FormularioDeAjusteDeEfectivo): SolicitudDeAjusteDeEfectivo {
  if (f.importe === null) throw new Error('El importe es obligatorio.')
  return { idEmpresa, concepto: f.concepto.trim(), importe: f.importe }
}

/** `true` cuando no se sabe si el ajuste se escribió (red cortada o 5xx): no se reintenta a ciegas. */
export function esResultadoInciertoDeEfectivo(e: unknown): boolean {
  return !(e instanceof ErrorApi) || e.estado >= 500
}

/** El rechazo del ajuste ubicado en su campo. Se elige por `codigo` y por estado, nunca por `mensaje`. */
export function errorAlAjustarEfectivo(e: unknown): ErrorDeAjusteDeEfectivo {
  if (!(e instanceof ErrorApi)) return { campo: 'general', mensaje: COPIA_GENERICA }

  switch (e.codigo) {
    case 'concepto_requerido':
      return { campo: 'concepto', mensaje: COPIA_CONCEPTO }
    case 'concepto_muy_largo':
      return {
        campo: 'concepto',
        mensaje: `El concepto no puede superar los ${LARGO_MAXIMO_DE_CONCEPTO_DE_AJUSTE_DE_EFECTIVO} caracteres.`,
      }
    case 'importe_invalido':
      return { campo: 'importe', mensaje: COPIA_IMPORTE }
  }

  if (e.estado === 404) return { campo: 'general', mensaje: 'La empresa ya no existe. Actualizá la pantalla.' }
  if (e.estado === 403) return { campo: 'general', mensaje: 'Solo un administrador puede ajustar el efectivo.' }

  return { campo: 'general', mensaje: e.message.trim() || COPIA_GENERICA }
}
