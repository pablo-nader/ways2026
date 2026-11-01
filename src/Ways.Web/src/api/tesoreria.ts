import { fechaHoraATexto } from '../formato/fechas'
import { api, ErrorApi } from './cliente'

export type SolicitudDeAjusteDeEfectivo = {
  idEmpresa: number
  concepto: string
  importe: number
}

export type ResultadoDeAjusteDeEfectivo = {
  idMovimiento: number
  fecha: string
  concepto: string
  importe: number
}

export type SolicitudDeConciliacion = {
  idEmpresa: number
  hasta: string
  nota?: string
}

export type ResultadoDeConciliacion = {
  idConciliacion: number
  idEmpresa: number
  hasta: string
  nota: string | null
  creadoEl: string
  idUsuario: number
}

export const clienteDeTesoreria = {
  /** `POST /api/tesoreria/efectivo/ajustes` (Admin): importe con signo distinto de cero y concepto obligatorio. Sin clave de idempotencia. */
  ajustarEfectivo: (solicitud: SolicitudDeAjusteDeEfectivo) =>
    api.post<ResultadoDeAjusteDeEfectivo>('/tesoreria/efectivo/ajustes', solicitud),

  /** `POST /api/tesoreria/conciliaciones` (Admin): irreversible y sin clave de idempotencia. */
  conciliar: (solicitud: SolicitudDeConciliacion) => api.post<ResultadoDeConciliacion>('/tesoreria/conciliaciones', solicitud),
}

export const LARGO_MAXIMO_DE_CONCEPTO_DE_AJUSTE_DE_EFECTIVO = 200

export type FormularioDeAjusteDeEfectivo = {
  importe: number | null
  concepto: string
}

export type CampoDeAjusteDeEfectivo = 'importe' | 'concepto' | 'general'

export type ErrorDeAjusteDeEfectivo = {
  campo: CampoDeAjusteDeEfectivo
  mensaje: string
}

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

  if (e.estado === 404)
    return {
      campo: 'general',
      mensaje: 'La empresa ya no existe. Actualizá la pantalla.',
    }
  if (e.estado === 403)
    return {
      campo: 'general',
      mensaje: 'Solo un administrador puede ajustar el efectivo.',
    }

  return { campo: 'general', mensaje: e.message.trim() || COPIA_GENERICA }
}

export const LARGO_MAXIMO_DE_NOTA_DE_CONCILIACION = 500

export type FormularioDeConciliacion = {
  /** Valor de un `datetime-local`: `AAAA-MM-DDTHH:mm`, hora local. */
  hasta: string
  nota: string
}

export type ErrorDeConciliacion = {
  campo: 'hasta' | 'nota' | 'general'
  mensaje: string
}

function dosDigitos(n: number): string {
  return String(n).padStart(2, '0')
}

/** Valor de un `datetime-local` para `ahora`, redondeado hacia abajo al minuto. */
export function valorLocalDeAhora(ahora: Date = new Date()): string {
  const fecha = `${ahora.getFullYear()}-${dosDigitos(ahora.getMonth() + 1)}-${dosDigitos(ahora.getDate())}`
  return `${fecha}T${dosDigitos(ahora.getHours())}:${dosDigitos(ahora.getMinutes())}`
}

export function formularioDeConciliacionInicial(ahora: Date = new Date()): FormularioDeConciliacion {
  return { hasta: valorLocalDeAhora(ahora), nota: '' }
}

const VALOR_LOCAL = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/

/** `AAAA-MM-DDTHH:mm` local → ISO con el offset local de ese instante (`2026-11-01T10:05:00-03:00`); `null` si no es una fecha y hora reales. */
export function aInstanteConOffset(valorLocal: string): string | null {
  const m = VALOR_LOCAL.exec(valorLocal)
  if (!m) return null

  const [anio, mes, dia, hora, minuto] = m.slice(1).map(Number)
  const fecha = new Date(anio, mes - 1, dia, hora, minuto)
  const esReal =
    fecha.getFullYear() === anio &&
    fecha.getMonth() === mes - 1 &&
    fecha.getDate() === dia &&
    fecha.getHours() === hora &&
    fecha.getMinutes() === minuto
  if (!esReal) return null

  const minutosAlOeste = fecha.getTimezoneOffset()
  const absolutos = Math.abs(minutosAlOeste)
  const desplazamiento = `${minutosAlOeste > 0 ? '-' : '+'}${dosDigitos(Math.floor(absolutos / 60))}:${dosDigitos(absolutos % 60)}`
  return `${valorLocal}:00${desplazamiento}`
}

const COPIA_CONCILIACION_GENERICA = 'No se pudo registrar la conciliación.'
const COPIA_HASTA_REQUERIDO = 'Ingresá la fecha y la hora hasta las que se concilia.'
const COPIA_NOTA_LARGA = `La nota no puede superar los ${LARGO_MAXIMO_DE_NOTA_DE_CONCILIACION} caracteres.`

/** Solo formato y obligatoriedad: que no sea futura o anterior a la marca vigente lo decide el servidor. */
export function validarConciliacion(f: FormularioDeConciliacion): ErrorDeConciliacion | null {
  if (aInstanteConOffset(f.hasta) === null) return { campo: 'hasta', mensaje: COPIA_HASTA_REQUERIDO }
  if (f.nota.trim().length > LARGO_MAXIMO_DE_NOTA_DE_CONCILIACION) return { campo: 'nota', mensaje: COPIA_NOTA_LARGA }
  return null
}

export function aSolicitudDeConciliacion(idEmpresa: number, f: FormularioDeConciliacion): SolicitudDeConciliacion {
  const hasta = aInstanteConOffset(f.hasta)
  if (hasta === null) throw new Error('La fecha y la hora son obligatorias.')

  const nota = f.nota.trim()
  return nota === '' ? { idEmpresa, hasta } : { idEmpresa, hasta, nota }
}

/** `true` cuando no se sabe si la marca se escribió (red cortada o 5xx): no se reintenta a ciegas. */
export function esResultadoInciertoDeConciliacion(e: unknown): boolean {
  return !(e instanceof ErrorApi) || e.estado >= 500
}

/**
 * El rechazo de la conciliación, por `codigo` y por estado, nunca por `mensaje`. `conciliadoHasta` es la marca que mostraba el
 * libro: con ella el rechazo `conciliacion_anterior` dice hasta cuándo ya está conciliado.
 */
export function errorAlConciliar(e: unknown, conciliadoHasta: string | null): ErrorDeConciliacion {
  if (!(e instanceof ErrorApi)) return { campo: 'general', mensaje: COPIA_CONCILIACION_GENERICA }

  switch (e.codigo) {
    case 'hasta_requerido':
      return { campo: 'hasta', mensaje: COPIA_HASTA_REQUERIDO }
    case 'nota_muy_larga':
      return { campo: 'nota', mensaje: COPIA_NOTA_LARGA }
    case 'conciliacion_futura':
      return { campo: 'hasta', mensaje: 'La fecha no puede ser futura.' }
    case 'conciliacion_anterior':
      return {
        campo: 'hasta',
        mensaje:
          conciliadoHasta === null
            ? 'Ya hay una conciliación igual o posterior; elegí un instante posterior.'
            : `Ya está conciliado hasta ${fechaHoraATexto(conciliadoHasta)}; elegí un instante posterior.`,
      }
    case 'conciliacion_con_turno_abierto':
      return {
        campo: 'hasta',
        mensaje: 'Hay una caja abierta desde antes de esa fecha; cerrala primero.',
      }
  }

  if (e.estado === 404)
    return {
      campo: 'general',
      mensaje: 'La empresa ya no existe. Actualizá la pantalla.',
    }
  if (e.estado === 403)
    return {
      campo: 'general',
      mensaje: 'Solo un administrador puede conciliar la tesorería.',
    }

  return {
    campo: 'general',
    mensaje: e.message.trim() || COPIA_CONCILIACION_GENERICA,
  }
}
