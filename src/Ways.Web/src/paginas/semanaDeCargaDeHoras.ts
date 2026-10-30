import type { ErrorApi } from '../api/cliente'
import type { DiaDeCargaSemanal, FilaDeCargaSemanal, SemanaDeCargaDeHoras, SolicitudDeCargaSemanalDeHoras } from '../api/tipos'
import { MAXIMO_DE_HORAS, importeDeHoras, parsearHoras, sanearTipeoDeHoras } from './cuentaDePersonalFormularios'

/** Lo que el usuario escribió y difiere de lo que trajo el servidor, por celda (`claveDeCelda`). */
export type Ediciones = ReadonlyMap<string, string>

export function claveDeCelda(idPersonal: number, fecha: string): string {
  return `${idPersonal}|${fecha}`
}

// ---- Semanas -----------------------------------------------------------------------------

const MS_POR_DIA = 86_400_000
const NOMBRES_DE_DIA = ['Dom', 'Lun', 'Mar', 'Mié', 'Jue', 'Vie', 'Sáb']

function aUtc(iso: string): Date {
  const [anio, mes, dia] = iso.split('-').map(Number)

  return new Date(Date.UTC(anio, mes - 1, dia))
}

function aIso(fecha: Date): string {
  return fecha.toISOString().slice(0, 10)
}

export function sumarDias(iso: string, dias: number): string {
  return aIso(new Date(aUtc(iso).getTime() + dias * MS_POR_DIA))
}

/** Lunes de la semana de `iso`: el domingo cierra la semana, no abre la siguiente. */
export function lunesDe(iso: string): string {
  const diaDeSemana = aUtc(iso).getUTCDay()

  return sumarDias(iso, -((diaDeSemana + 6) % 7))
}

function diaYMes(iso: string): string {
  return `${iso.slice(8, 10)}/${iso.slice(5, 7)}`
}

/** "Lun 27/10". */
export function etiquetaDeDia(iso: string): string {
  return `${NOMBRES_DE_DIA[aUtc(iso).getUTCDay()]} ${diaYMes(iso)}`
}

/** "27/10 – 02/11". */
export function etiquetaDeSemana(desde: string, hasta: string): string {
  return `${diaYMes(desde)} – ${diaYMes(hasta)}`
}

// ---- Celdas ------------------------------------------------------------------------------

function formatearHoras(horas: number): string {
  return String(horas).replace('.', ',')
}

/** Lo que muestra la celda sin ediciones: lo confirmado o el horario habitual precargado. */
export function textoBaseDeDia(dia: DiaDeCargaSemanal): string {
  return dia.horas === null ? '' : formatearHoras(dia.horas)
}

export function textoDeCelda(idPersonal: number, dia: DiaDeCargaSemanal, ediciones: Ediciones): string {
  return ediciones.get(claveDeCelda(idPersonal, dia.fecha)) ?? textoBaseDeDia(dia)
}

/** Registra lo tipeado; si vuelve a ser igual a lo que trajo el servidor deja de contar como cambio. */
export function editarCelda(ediciones: Ediciones, idPersonal: number, dia: DiaDeCargaSemanal, bruto: string): Ediciones {
  const siguiente = new Map(ediciones)
  const clave = claveDeCelda(idPersonal, dia.fecha)
  const texto = sanearTipeoDeHoras(bruto)

  if (texto === textoBaseDeDia(dia)) siguiente.delete(clave)
  else siguiente.set(clave, texto)

  return siguiente
}

const ERROR_DE_FORMATO = 'Las horas tienen que ser un número con hasta dos decimales, por ejemplo 7,5.'
const ERROR_DE_MAXIMO = `Un día no puede superar las ${MAXIMO_DE_HORAS} horas.`

/** Rechazo de cada celda editada, por clave. Las celdas sin editar vienen del servidor y no se revalidan. */
export function erroresDeCeldas(semana: SemanaDeCargaDeHoras, ediciones: Ediciones): Map<string, string> {
  const errores = new Map<string, string>()

  for (const fila of semana.filas) {
    for (const dia of fila.dias) {
      const clave = claveDeCelda(fila.idPersonal, dia.fecha)
      const texto = ediciones.get(clave)
      if (texto === undefined) continue

      const horas = parsearHoras(texto)
      if (horas !== null && Number.isNaN(horas)) errores.set(clave, ERROR_DE_FORMATO)
      else if (horas !== null && horas > MAXIMO_DE_HORAS) errores.set(clave, ERROR_DE_MAXIMO)
    }
  }

  return errores
}

// ---- Solicitud ---------------------------------------------------------------------------

/** Qué se manda al guardar: cada día no futuro que el usuario editó o que viene precargado (guardar lo
 * confirma). Una celda editada y vacía manda 0. El resto —lo ya confirmado sin tocar y lo vacío sin
 * horario habitual— no se manda, así guardar nunca pisa lo que otro cargó. Se asume sin errores de celda. */
export function armarSolicitud(semana: SemanaDeCargaDeHoras, ediciones: Ediciones): SolicitudDeCargaSemanalDeHoras {
  const dias: SolicitudDeCargaSemanalDeHoras['dias'] = []

  for (const fila of semana.filas) {
    for (const dia of fila.dias) {
      if (dia.futuro) continue

      const texto = ediciones.get(claveDeCelda(fila.idPersonal, dia.fecha))
      if (texto !== undefined) {
        dias.push({ idPersonal: fila.idPersonal, fecha: dia.fecha, horas: parsearHoras(texto) ?? 0 })
      } else if (dia.precargado && dia.horas !== null) {
        dias.push({ idPersonal: fila.idPersonal, fecha: dia.fecha, horas: dia.horas })
      }
    }
  }

  return { dias }
}

// ---- Totales -----------------------------------------------------------------------------

function horasVisibles(idPersonal: number, dia: DiaDeCargaSemanal, ediciones: Ediciones): number {
  if (dia.futuro) return 0

  const horas = parsearHoras(textoDeCelda(idPersonal, dia, ediciones))

  return horas === null || Number.isNaN(horas) ? 0 : horas
}

function sumarHoras(valores: number[]): number {
  return Math.round(valores.reduce((suma, v) => suma + v, 0) * 100) / 100
}

export function totalDeHorasDeFila(fila: FilaDeCargaSemanal, ediciones: Ediciones): number {
  return sumarHoras(fila.dias.map((d) => horasVisibles(fila.idPersonal, d, ediciones)))
}

export function importeDeFila(fila: FilaDeCargaSemanal, ediciones: Ediciones): number {
  return importeDeHoras(totalDeHorasDeFila(fila, ediciones), fila.valorHora) ?? 0
}

export function totalDeHorasDelDia(semana: SemanaDeCargaDeHoras, indice: number, ediciones: Ediciones): number {
  return sumarHoras(semana.filas.map((f) => horasVisibles(f.idPersonal, f.dias[indice], ediciones)))
}

// ---- Errores del servidor ----------------------------------------------------------------

const COPIA_GENERICA = 'No se pudo guardar la semana.'

/** Se elige por `codigo`, nunca por `mensaje`; es un `Map` porque el código viene del servidor. */
const MENSAJE_POR_CODIGO: ReadonlyMap<string, string> = new Map([
  ['semana_invalida', 'La semana elegida no es válida. Actualizá la pantalla e intentá de nuevo.'],
  ['fecha_futura', 'No se pueden cargar horas de días futuros.'],
  ['horas_invalidas', `Las horas deben estar entre 0 y ${MAXIMO_DE_HORAS}, con hasta dos decimales.`],
  ['horas_del_dia_excedidas', `Un empleado no puede superar las ${MAXIMO_DE_HORAS} horas en un día.`],
  ['solicitud_invalida', 'La carga de la semana no es válida. Actualizá la pantalla e intentá de nuevo.'],
])

export function errorDeGuardadoSemanal(e: ErrorApi): string {
  const propio = MENSAJE_POR_CODIGO.get(e.codigo)
  if (propio) return propio

  if (e.estado === 403) return 'No tenés permiso para cargar horas.'
  if (e.estado === 404) return 'Un empleado de la carga ya no existe. Actualizá la pantalla e intentá de nuevo.'

  return e.message.trim() || COPIA_GENERICA
}
