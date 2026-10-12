/**
 * Cliente y reglas puras del ABM de cuentas de tesorería (back office, solo Admin). Dedicado y no la máquina
 * genérica de catálogos: la cuenta pertenece a una empresa, muestra un saldo calculado por el servidor y su
 * saldo inicial puede quedar de solo lectura.
 */
import { api, ErrorApi } from './cliente'
import { CODIGO_RESULTADO_INCIERTO, COPIA_RESULTADO_INCIERTO } from './bajas'
import type { EmpresaListado } from './tipos'

/** Fila del listado. `saldoActual` y `saldoInicialEditable` los calcula el servidor. */
export type CuentaTesoreriaListado = {
  id: number
  idEmpresa: number
  nombreEmpresa: string | null
  nombre: string
  saldoInicial: number
  saldoActual: number
  activo: boolean
  saldoInicialEditable: boolean
}

export type AltaDeCuentaTesoreria = { idEmpresa: number; nombre: string; saldoInicial: number; activo: boolean }

export type EdicionDeCuentaTesoreria = { nombre: string; saldoInicial: number; activo: boolean }

export const clienteDeCuentasTesoreria = {
  listar: () => api.get<CuentaTesoreriaListado[]>('/cuentas-tesoreria'),
  crear: (datos: AltaDeCuentaTesoreria) => api.post<CuentaTesoreriaListado>('/cuentas-tesoreria', datos),
  actualizar: (id: number, datos: EdicionDeCuentaTesoreria) =>
    api.put<CuentaTesoreriaListado>(`/cuentas-tesoreria/${id}`, datos),
  eliminar: (id: number) => api.delete(`/cuentas-tesoreria/${id}`),
}

/** Tope de `numeric(14,2)`, en valor absoluto. */
export const SALDO_INICIAL_MAXIMO = 999_999_999_999.99

/** Largo de `cuentas_tesoreria.nombre`. */
export const LARGO_MAXIMO_DE_NOMBRE = 150

/**
 * Estado del formulario. `saldoInicialOriginal` es el guardado: cuando el saldo inicial no es editable es lo que
 * viaja, sin importar lo que haya quedado en `saldoInicial`.
 */
export type Formulario = {
  id: number | null
  idEmpresa: number | null
  /** Nombre de la empresa tal como vino en la fila: respaldo para mostrarla al editar si el listado de empresas no
   * cargó o no la incluye. */
  nombreEmpresa: string | null
  nombre: string
  saldoInicial: number | null
  saldoInicialOriginal: number | null
  saldoInicialEditable: boolean
  activo: boolean
}

export type CampoConError = 'nombre' | 'idEmpresa' | 'saldoInicial' | 'general'

/** Un rechazo (local o del servidor) y el campo donde se muestra; `general` va arriba del formulario. */
export type ErrorDeCuenta = { campo: CampoConError; mensaje: string }

const COPIA_GENERICA = 'No se pudo guardar.'
const COPIA_SALDO_INVALIDO =
  'El saldo inicial debe estar entre -999.999.999.999,99 y 999.999.999.999,99, con hasta dos decimales.'

export const COPIA_SALDO_CON_MOVIMIENTOS = 'El saldo inicial no se puede modificar porque la cuenta ya tiene movimientos.'

/** `idEmpresa` por defecto: la única empresa del tenant, si hay una sola; con varias, hay que elegir. */
export function formularioVacio(empresas: readonly EmpresaListado[]): Formulario {
  return {
    id: null,
    idEmpresa: empresas.length === 1 ? empresas[0].id : null,
    nombreEmpresa: null,
    nombre: '',
    saldoInicial: 0,
    saldoInicialOriginal: null,
    saldoInicialEditable: true,
    activo: true,
  }
}

export function aFormulario(cuenta: CuentaTesoreriaListado): Formulario {
  return {
    id: cuenta.id,
    idEmpresa: cuenta.idEmpresa,
    nombreEmpresa: cuenta.nombreEmpresa,
    nombre: cuenta.nombre,
    saldoInicial: cuenta.saldoInicial,
    saldoInicialOriginal: cuenta.saldoInicial,
    saldoInicialEditable: cuenta.saldoInicialEditable,
    activo: cuenta.activo,
  }
}

/** El servidor rechazó un cambio de saldo inicial: la cuenta ya tiene movimientos. El formulario vuelve al valor
 * guardado y deja el campo de solo lectura. */
export function conSaldoInicialBloqueado(f: Formulario): Formulario {
  return { ...f, saldoInicial: f.saldoInicialOriginal, saldoInicialEditable: false }
}

export function nombreDeEmpresa(empresa: EmpresaListado): string {
  return empresa.nombreFantasia?.trim() || empresa.razonSocial
}

/** Primero lo que el usuario puede corregir en el campo; recién después lo que depende del servidor. */
export function validarFormulario(f: Formulario): ErrorDeCuenta | null {
  const nombre = f.nombre.trim()
  if (nombre === '') return { campo: 'nombre', mensaje: 'Ingresá el nombre.' }
  if (nombre.length > LARGO_MAXIMO_DE_NOMBRE) {
    return { campo: 'nombre', mensaje: `El nombre no puede superar los ${LARGO_MAXIMO_DE_NOMBRE} caracteres.` }
  }

  if (f.id === null && f.idEmpresa === null) return { campo: 'idEmpresa', mensaje: 'Elegí la empresa.' }

  if (f.saldoInicialEditable) {
    if (f.saldoInicial === null) return { campo: 'saldoInicial', mensaje: 'Ingresá el saldo inicial.' }
    if (Math.abs(f.saldoInicial) > SALDO_INICIAL_MAXIMO) return { campo: 'saldoInicial', mensaje: COPIA_SALDO_INVALIDO }
  }

  return null
}

/** Saldo que viaja: el del campo si se puede editar; si no, el guardado, aunque el campo lo haya tocado alguien. */
function saldoAEnviar(f: Formulario): number {
  const saldo = f.saldoInicialEditable ? f.saldoInicial : f.saldoInicialOriginal
  if (saldo === null) throw new Error('El saldo inicial es obligatorio.')

  return saldo
}

/** Cuerpo del alta. Exige un formulario ya validado: nunca manda una empresa o un saldo inventados. */
export function aAlta(f: Formulario): AltaDeCuentaTesoreria {
  if (f.idEmpresa === null) throw new Error('La empresa es obligatoria.')

  return { idEmpresa: f.idEmpresa, nombre: f.nombre.trim(), saldoInicial: saldoAEnviar(f), activo: f.activo }
}

/** Cuerpo de la edición: la empresa no viaja, una cuenta no cambia de empresa. */
export function aEdicion(f: Formulario): EdicionDeCuentaTesoreria {
  return { nombre: f.nombre.trim(), saldoInicial: saldoAEnviar(f), activo: f.activo }
}

/** Se elige por `codigo` y nunca por `mensaje`. Es un `Map` porque el código viene del servidor: sobre un objeto,
 * `'constructor'` resolvería contra el prototipo. */
const ERROR_POR_CODIGO: ReadonlyMap<string, ErrorDeCuenta> = new Map<string, ErrorDeCuenta>([
  [
    'cuenta_tesoreria_duplicada',
    { campo: 'nombre', mensaje: 'Ya existe una cuenta de tesorería con ese nombre en esta empresa.' },
  ],
  ['saldo_inicial_con_movimientos', { campo: 'saldoInicial', mensaje: COPIA_SALDO_CON_MOVIMIENTOS }],
  ['nombre_requerido', { campo: 'nombre', mensaje: 'Ingresá el nombre.' }],
  [
    'nombre_muy_largo',
    { campo: 'nombre', mensaje: `El nombre no puede superar los ${LARGO_MAXIMO_DE_NOMBRE} caracteres.` },
  ],
  ['saldo_inicial_invalido', { campo: 'saldoInicial', mensaje: COPIA_SALDO_INVALIDO }],
  [
    'referencia_invalida',
    { campo: 'idEmpresa', mensaje: 'La empresa elegida ya no existe. Actualizá la pantalla y elegí otra.' },
  ],
])

/** El rechazo de un alta o una edición, ubicado en el campo que corresponde. */
export function errorAlGuardar(e: unknown): ErrorDeCuenta {
  if (!(e instanceof ErrorApi)) return { campo: 'general', mensaje: COPIA_GENERICA }

  const propio = ERROR_POR_CODIGO.get(e.codigo)
  if (propio) return propio

  if (e.estado === 404) return { campo: 'general', mensaje: 'La cuenta ya no existe. Actualizá la pantalla.' }

  if (e.estado >= 500) {
    const delServidor = e.message.trim()
    const esCommitAmbiguo = e.codigo === CODIGO_RESULTADO_INCIERTO && delServidor.length > 0

    return { campo: 'general', mensaje: esCommitAmbiguo ? delServidor : `${COPIA_GENERICA} ${COPIA_RESULTADO_INCIERTO}` }
  }

  return { campo: 'general', mensaje: e.message.trim() || COPIA_GENERICA }
}

/**
 * Copia del rechazo de una baja. Mismas ramas que `copiaDeFalloDeBaja`, que solo nombra sujetos de su lista
 * cerrada: acá el sujeto es la cuenta, con su nombre. El mensaje del servidor dice qué la bloquea y va primero.
 */
export function copiaDeBajaDeCuenta(e: unknown, nombre: string): string {
  const encabezado = `No se pudo dar de baja la cuenta "${nombre}".`

  if (!(e instanceof ErrorApi)) return `${encabezado} ${COPIA_RESULTADO_INCIERTO}`

  if (e.codigo === 'cuenta_tesoreria_en_uso') {
    const detalle = e.message.trim() || `No se puede dar de baja la cuenta "${nombre}" porque está en uso.`

    return `${detalle} Desactivala para que no se ofrezca más.`
  }

  if (e.estado === 404) return `${encabezado} Ya no existe o no está a tu alcance. Actualizá el listado.`

  if (e.estado >= 500) {
    const delServidor = e.message.trim()
    const esCommitAmbiguo = e.codigo === CODIGO_RESULTADO_INCIERTO && delServidor.length > 0

    return `${encabezado} ${esCommitAmbiguo ? delServidor : COPIA_RESULTADO_INCIERTO}`
  }

  return e.message.trim() || encabezado
}

export type OpcionDeCuenta = { valor: string; etiqueta: string }

/**
 * Opciones del selector de cuenta de un medio de pago: solo las que el medio puede tomar. Las de su empresa; si es de
 * todo el tenant (`idEmpresaDelMedio` vacío), las de la única empresa del tenant y ninguna si hay varias, que es lo que
 * exige el servidor. Solo las activas, más la que el medio ya tiene aunque se haya desactivado: una cuenta inactiva no
 * se ofrece para asignaciones nuevas, pero quitarla en silencio al editar el medio sería pisarla. Si la lectura falló
 * (`cuentas` o `empresas` en null) o no trae la actual, la actual sale con el nombre que ya conoce el formulario.
 */
export function opcionesDeCuentas(
  cuentas: readonly CuentaTesoreriaListado[] | null,
  empresas: readonly { id: number }[] | null,
  idEmpresaDelMedio: string,
  idActual: string,
  nombreActual: string,
  actualInactiva: boolean,
): OpcionDeCuenta[] {
  const empresaPermitida =
    idEmpresaDelMedio !== '' ? Number(idEmpresaDelMedio) : empresas !== null && empresas.length === 1 ? empresas[0].id : null
  const lista = (cuentas ?? []).filter((c) => c.idEmpresa === empresaPermitida)
  const etiqueta = (nombre: string, inactiva: boolean) => `${nombre}${inactiva ? ' (inactiva)' : ''}`

  const opciones = lista
    .filter((c) => c.activo || String(c.id) === idActual)
    .map((c) => ({ valor: String(c.id), etiqueta: etiqueta(c.nombre, !c.activo) }))

  if (idActual !== '' && !opciones.some((o) => o.valor === idActual)) {
    opciones.unshift({ valor: idActual, etiqueta: etiqueta(nombreActual || `Cuenta ${idActual}`, actualInactiva) })
  }

  return opciones
}
