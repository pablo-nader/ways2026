import { ErrorApi } from '../api/cliente'
import { CODIGO_RESULTADO_INCIERTO, COPIA_RESULTADO_INCIERTO } from '../api/bajas'
import type { ClienteListado, DatosDePersonal, PersonalListado, UsuarioListado } from '../api/tipos'

/** Estado del formulario del ABM de personal. `valorHora` vacío es `null`; los vínculos sueltos son
 * `null`. `idEmpresa` no tiene input (ninguna pantalla de terceros lo expone): viaja tal como vino
 * para que editar no lo borre. */
export type Formulario = {
  id: number | null
  nombre: string
  documento: string
  telefono: string
  observaciones: string
  valorHora: number | null
  idEmpresa: number | null
  idUsuario: number | null
  idCliente: number | null
}

export type CampoConError = 'nombre' | 'valorHora' | 'idUsuario' | 'idCliente' | 'general'

/** Un rechazo (local o del servidor) y el campo donde se muestra; `general` va arriba del formulario. */
export type ErrorDePersonal = { campo: CampoConError; mensaje: string }

export type OpcionDeVinculo = { id: number; etiqueta: string }

/** Lo mínimo del vínculo actual del registro que se edita, para no perderlo si el listado de
 * referencia no lo trae (inactivo o fuera de la primera página). */
export type VinculoActual = { id: number; nombre: string }

/** Tope de `valor_hora` que acepta el servidor. */
const VALOR_HORA_MAXIMO = 999_999_999_999.99

const COPIA_GENERICA = 'No se pudo guardar.'

export function formularioVacio(): Formulario {
  return {
    id: null,
    nombre: '',
    documento: '',
    telefono: '',
    observaciones: '',
    valorHora: null,
    idEmpresa: null,
    idUsuario: null,
    idCliente: null,
  }
}

export function aFormulario(p: PersonalListado): Formulario {
  return {
    id: p.id,
    nombre: p.nombre,
    documento: p.documento ?? '',
    telefono: p.telefono ?? '',
    observaciones: p.observaciones ?? '',
    valorHora: p.valorHora,
    idEmpresa: p.idEmpresa,
    idUsuario: p.idUsuario,
    idCliente: p.idCliente,
  }
}

function aVacioNulo(valor: string): string | null {
  const limpio = valor.trim()
  return limpio === '' ? null : limpio
}

/** Cuerpo del request. Exige un valor hora ya validado: nunca manda un 0 inventado. */
export function aDatos(f: Formulario): DatosDePersonal {
  if (f.valorHora === null) throw new Error('El valor hora es obligatorio.')

  return {
    nombre: f.nombre.trim(),
    documento: aVacioNulo(f.documento),
    telefono: aVacioNulo(f.telefono),
    observaciones: aVacioNulo(f.observaciones),
    valorHora: f.valorHora,
    idEmpresa: f.idEmpresa,
    idUsuario: f.idUsuario,
    idCliente: f.idCliente,
  }
}

export function validarFormulario(f: Formulario): ErrorDePersonal | null {
  if (f.nombre.trim() === '') return { campo: 'nombre', mensaje: 'Ingresá el nombre.' }

  if (f.valorHora === null) return { campo: 'valorHora', mensaje: 'Ingresá el valor hora.' }
  if (f.valorHora < 0 || f.valorHora > VALOR_HORA_MAXIMO) {
    return { campo: 'valorHora', mensaje: COPIA_VALOR_HORA_INVALIDO }
  }

  return null
}

const COPIA_VALOR_HORA_INVALIDO = 'El valor hora debe estar entre 0 y 999.999.999.999,99, con hasta dos decimales.'

/** Se elige por `codigo` y nunca por `mensaje`. Es un `Map` porque el código viene del servidor:
 * sobre un objeto, `'constructor'` resolvería contra el prototipo. */
const ERROR_POR_CODIGO: ReadonlyMap<string, ErrorDePersonal> = new Map<string, ErrorDePersonal>([
  ['usuario_ya_vinculado', { campo: 'idUsuario', mensaje: 'Ese usuario ya está vinculado a otro personal.' }],
  ['cliente_ya_vinculado', { campo: 'idCliente', mensaje: 'Ese cliente ya está vinculado a otro personal.' }],
  ['cliente_consumidor_final', { campo: 'idCliente', mensaje: 'El consumidor final no puede vincularse a un personal.' }],
  ['usuario_invalido', { campo: 'idUsuario', mensaje: 'El usuario elegido ya no existe. Actualizá la pantalla y elegí otro.' }],
  ['nombre_requerido', { campo: 'nombre', mensaje: 'Ingresá el nombre.' }],
  ['valor_hora_invalido', { campo: 'valorHora', mensaje: COPIA_VALOR_HORA_INVALIDO }],
  [
    'referencia_invalida',
    { campo: 'general', mensaje: 'Alguno de los datos elegidos ya no existe. Actualizá la pantalla e intentá de nuevo.' },
  ],
])

/** El rechazo de un alta o una edición, ubicado en el campo que corresponde. */
export function errorAlGuardar(e: unknown): ErrorDePersonal {
  if (!(e instanceof ErrorApi)) return { campo: 'general', mensaje: COPIA_GENERICA }

  const propio = ERROR_POR_CODIGO.get(e.codigo)
  if (propio) return propio

  if (e.estado === 404) return { campo: 'general', mensaje: 'El personal ya no existe. Actualizá la pantalla.' }

  return { campo: 'general', mensaje: e.message.trim() || COPIA_GENERICA }
}

/** Copia del rechazo de una baja. Mismas ramas que `copiaDeFalloDeBaja`, que solo sabe nombrar
 * sujetos de su lista cerrada: acá el sujeto es el empleado, con su nombre. */
export function copiaDeBajaDePersonal(e: unknown, nombre: string): string {
  const encabezado = `No se pudo dar de baja a "${nombre}".`

  if (!(e instanceof ErrorApi)) return `${encabezado} ${COPIA_RESULTADO_INCIERTO}`

  if (e.codigo === 'personal_en_uso') {
    return `No se puede dar de baja a "${nombre}": tiene movimientos registrados en su cuenta (horas, pagos o ajustes).`
  }

  if (e.estado === 404) return `${encabezado} Ya no existe o no está a tu alcance. Actualizá el listado.`

  if (e.estado >= 500) {
    const delServidor = e.message.trim()
    const esCommitAmbiguo = e.codigo === CODIGO_RESULTADO_INCIERTO && delServidor.length > 0

    return `${encabezado} ${esCommitAmbiguo ? delServidor : COPIA_RESULTADO_INCIERTO}`
  }

  return e.message.trim() || encabezado
}

/** Usuarios ofrecidos para vincular: los activos, más el vínculo actual aunque ya no cumpla. */
export function opcionesDeUsuarios(usuarios: UsuarioListado[], actual: VinculoActual | null): OpcionDeVinculo[] {
  const opciones = usuarios
    .filter((u) => u.estado === 'Activo' || u.id === actual?.id)
    .map((u) => ({ id: u.id, etiqueta: `${u.usuario} (${u.mail})` }))

  return conVinculoActual(opciones, actual)
}

function etiquetaDeCliente(c: ClienteListado): string {
  const nombreCompleto = c.razonSocial ?? [c.nombre, c.apellido].filter(Boolean).join(' ')
  return `#${c.numero} — ${nombreCompleto}`
}

/** Clientes ofrecidos para vincular: los activos que no son el consumidor final, más el vínculo
 * actual aunque ya no cumpla. */
export function opcionesDeClientes(clientes: ClienteListado[], actual: VinculoActual | null): OpcionDeVinculo[] {
  const opciones = clientes
    .filter((c) => !c.esConsumidorFinal && (c.activo || c.id === actual?.id))
    .map((c) => ({ id: c.id, etiqueta: etiquetaDeCliente(c) }))

  return conVinculoActual(opciones, actual)
}

function conVinculoActual(opciones: OpcionDeVinculo[], actual: VinculoActual | null): OpcionDeVinculo[] {
  if (actual === null || opciones.some((o) => o.id === actual.id)) return opciones

  return [...opciones, { id: actual.id, etiqueta: actual.nombre }]
}
