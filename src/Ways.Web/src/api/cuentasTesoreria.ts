/**
 * Cliente y reglas puras del ABM de cuentas de tesorería (back office, solo Admin). Dedicado y no la máquina
 * genérica de catálogos: la cuenta pertenece a una empresa, muestra un saldo calculado por el servidor y su
 * saldo inicial puede quedar de solo lectura.
 */
import { api, ErrorApi } from './cliente'
import { CODIGO_RESULTADO_INCIERTO, COPIA_RESULTADO_INCIERTO } from './bajas'
import type { CategoriaPago, EmpresaListado, MovimientoDeCuentaListado } from './tipos'

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
  ajustar: (id: number, solicitud: SolicitudDeAjusteDeCuenta) =>
    api.post<ResultadoDeAjusteDeCuenta>(`/cuentas-tesoreria/${id}/ajustes`, solicitud),
}

/** Cuerpo del ajuste: `concepto` y EXACTAMENTE uno de `importe` (con signo) y `saldoObjetivo`. */
export type SolicitudDeAjusteDeCuenta =
  | { concepto: string; importe: number; saldoObjetivo?: undefined }
  | { concepto: string; saldoObjetivo: number; importe?: undefined }

export type ResultadoDeAjusteDeCuenta = { movimiento: MovimientoDeCuentaListado; saldoActual: number }

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

// ---- Ajuste manual del saldo de una cuenta (solo Admin) ---------------------------------------

/** Tope del concepto del ajuste, igual que el servidor (`ReglasDeAjusteDeCuentaTesoreria`). */
export const LARGO_MAXIMO_DE_CONCEPTO_DE_AJUSTE = 200

/** "Tengo el saldo real": el servidor calcula la diferencia bajo el lock. "Sumar o restar": el importe va tal cual. */
export type ModoDeAjuste = 'saldoObjetivo' | 'importe'

export type FormularioDeAjuste = {
  modo: ModoDeAjuste
  saldoObjetivo: number | null
  importe: number | null
  concepto: string
}

export type CampoDeAjuste = 'concepto' | 'saldoObjetivo' | 'importe' | 'general'

export type ErrorDeAjuste = { campo: CampoDeAjuste; mensaje: string }

export function formularioDeAjusteVacio(): FormularioDeAjuste {
  return { modo: 'saldoObjetivo', saldoObjetivo: null, importe: null, concepto: '' }
}

const COPIA_AJUSTE_GENERICA = 'No se pudo registrar el ajuste.'
const COPIA_MONTO_FUERA_DE_RANGO = 'El monto debe estar entre -999.999.999.999,99 y 999.999.999.999,99, con hasta dos decimales.'
const COPIA_SIN_DIFERENCIA = 'El saldo de la cuenta ya coincide con el saldo informado: no hay nada que ajustar.'

function aCentavos(valor: number): number {
  return Math.round(valor * 100)
}

/**
 * Diferencia que mostraría el ajuste por saldo real: es una VISTA PREVIA, el servidor la recalcula contra el saldo
 * que tenga la cuenta al escribir. `null` si todavía no hay un saldo informado. En centavos enteros para no arrastrar
 * el error binario del punto flotante (1000,10 − 999,90 no debe dar 0,2000000000000455).
 */
export function diferenciaDeVistaPrevia(saldoActual: number, saldoObjetivo: number | null): number | null {
  if (saldoObjetivo === null) return null

  return (aCentavos(saldoObjetivo) - aCentavos(saldoActual)) / 100
}

/** Saldo que quedaría tras sumar `importe` (con signo) al actual, en centavos enteros. Vista previa, no el valor del servidor. */
export function saldoResultanteDeVistaPrevia(saldoActual: number, importe: number): number {
  return (aCentavos(saldoActual) + aCentavos(importe)) / 100
}

/** Importe con signo que el ajuste escribiría según el modo, o `null` mientras el campo no es válido. */
export function importeDeVistaPrevia(f: FormularioDeAjuste, saldoActual: number): number | null {
  return f.modo === 'importe' ? f.importe : diferenciaDeVistaPrevia(saldoActual, f.saldoObjetivo)
}

/**
 * Solo formato y campos obligatorios. Que el saldo informado ya coincida con el de la cuenta y que el saldo resultante quepa en
 * el rango son decisiones del servidor (409 `ajuste_sin_diferencia`, 400 `saldo_resultante_fuera_de_rango`): el saldo que ve la
 * pantalla puede estar viejo y un rechazo local bloquearía un ajuste válido.
 */
export function validarAjuste(f: FormularioDeAjuste): ErrorDeAjuste | null {
  const concepto = f.concepto.trim()
  if (concepto === '') return { campo: 'concepto', mensaje: 'Ingresá el concepto del ajuste.' }
  if (concepto.length > LARGO_MAXIMO_DE_CONCEPTO_DE_AJUSTE) {
    return {
      campo: 'concepto',
      mensaje: `El concepto no puede superar los ${LARGO_MAXIMO_DE_CONCEPTO_DE_AJUSTE} caracteres.`,
    }
  }

  if (f.modo === 'saldoObjetivo') {
    if (f.saldoObjetivo === null) return { campo: 'saldoObjetivo', mensaje: 'Ingresá el saldo real de la plataforma.' }
    if (Math.abs(f.saldoObjetivo) > SALDO_INICIAL_MAXIMO) {
      return { campo: 'saldoObjetivo', mensaje: COPIA_MONTO_FUERA_DE_RANGO }
    }
    return null
  }

  if (f.importe === null) return { campo: 'importe', mensaje: 'Ingresá el importe a sumar o restar.' }
  if (aCentavos(f.importe) === 0) return { campo: 'importe', mensaje: 'El importe no puede ser cero.' }
  if (Math.abs(f.importe) > SALDO_INICIAL_MAXIMO) return { campo: 'importe', mensaje: COPIA_MONTO_FUERA_DE_RANGO }
  return null
}

/** Cuerpo del ajuste: viaja SOLO el campo del modo elegido, nunca los dos. Exige un formulario ya validado. */
export function aSolicitudDeAjuste(f: FormularioDeAjuste): SolicitudDeAjusteDeCuenta {
  const concepto = f.concepto.trim()

  if (f.modo === 'importe') {
    if (f.importe === null) throw new Error('El importe es obligatorio.')
    return { concepto, importe: f.importe }
  }

  if (f.saldoObjetivo === null) throw new Error('El saldo objetivo es obligatorio.')
  return { concepto, saldoObjetivo: f.saldoObjetivo }
}

const ERROR_DE_AJUSTE_POR_CODIGO: ReadonlyMap<string, ErrorDeAjuste> = new Map<string, ErrorDeAjuste>([
  ['concepto_requerido', { campo: 'concepto', mensaje: 'Ingresá el concepto del ajuste.' }],
  [
    'concepto_muy_largo',
    {
      campo: 'concepto',
      mensaje: `El concepto no puede superar los ${LARGO_MAXIMO_DE_CONCEPTO_DE_AJUSTE} caracteres.`,
    },
  ],
  ['importe_invalido', { campo: 'importe', mensaje: `El importe no puede ser cero. ${COPIA_MONTO_FUERA_DE_RANGO}` }],
  ['saldo_objetivo_invalido', { campo: 'saldoObjetivo', mensaje: COPIA_MONTO_FUERA_DE_RANGO }],
  ['ajuste_sin_diferencia', { campo: 'saldoObjetivo', mensaje: COPIA_SIN_DIFERENCIA }],
  [
    'saldo_resultante_fuera_de_rango',
    { campo: 'general', mensaje: 'El saldo resultante quedaría fuera del rango permitido.' },
  ],
  [
    'importe_o_saldo_objetivo_requerido',
    { campo: 'general', mensaje: 'Ingresá el saldo real o el importe a sumar o restar.' },
  ],
  [
    'importe_y_saldo_objetivo_excluyentes',
    { campo: 'general', mensaje: 'Elegí un solo modo: el saldo real o el importe.' },
  ],
])

/** `true` cuando no se sabe si el ajuste se escribió (red cortada o cualquier 5xx): el llamador no debe dejar reintentar a ciegas. */
export function esResultadoIncierto(e: unknown): boolean {
  return !(e instanceof ErrorApi) || e.estado >= 500
}

/** El rechazo de un ajuste, ubicado en el campo que corresponde. Se elige por `codigo`, nunca por `mensaje`. */
export function errorAlAjustar(e: unknown): ErrorDeAjuste {
  if (!(e instanceof ErrorApi)) return { campo: 'general', mensaje: `${COPIA_AJUSTE_GENERICA} ${COPIA_RESULTADO_INCIERTO}` }

  const propio = ERROR_DE_AJUSTE_POR_CODIGO.get(e.codigo)
  if (propio) return propio

  if (e.estado === 404) return { campo: 'general', mensaje: 'La cuenta ya no existe. Actualizá la pantalla.' }
  if (e.estado === 403) return { campo: 'general', mensaje: 'Solo un administrador puede ajustar el saldo de una cuenta.' }

  if (e.estado >= 500) {
    const delServidor = e.message.trim()
    const esCommitAmbiguo = e.codigo === CODIGO_RESULTADO_INCIERTO && delServidor.length > 0

    return {
      campo: 'general',
      mensaje: esCommitAmbiguo ? delServidor : `${COPIA_AJUSTE_GENERICA} ${COPIA_RESULTADO_INCIERTO}`,
    }
  }

  return { campo: 'general', mensaje: e.message.trim() || COPIA_AJUSTE_GENERICA }
}

// ---- Reconstrucción histórica de las cuentas (solo Admin) --------------------------------------

/** Tope del rango que acepta el servidor: la diferencia entre `hasta` y `desde`, en días. */
export const DIAS_MAXIMOS_DE_RECONSTRUCCION = 366

export type TipoDeComponenteDeReconstruccion =
  | 'Cobro'
  | 'Comision'
  | 'RetencionIibb'
  | 'RetencionIva'
  | 'RetencionGanancias'
  | 'Ajuste'
  | 'Transferencia'
  | 'Deposito'
  | 'Retiro'
  | 'Pago'

export type ComponenteDeReconstruccion = {
  tipo: TipoDeComponenteDeReconstruccion
  ingreso: number
  egreso: number
  porcentajeAplicado: number | null
}

export type AcreditacionDeReconstruccion = {
  idTurnoCaja: number
  fechaCierre: string
  idMedioPago: number
  nombreMedio: string
  idCuentaTesoreria: number
  nombreCuenta: string
  baseBruta: number
  neto: number
  componentes: ComponenteDeReconstruccion[]
}

/** Débito que se escribiría (o se escribió) por un pago sin filas en la cuenta, fechado con la fecha del pago. */
export type DebitoDeReconstruccion = {
  idPago: number
  fecha: string
  categoria: CategoriaPago
  concepto: string
  idMedioPago: number
  nombreMedio: string
  idCuentaTesoreria: number
  nombreCuenta: string
  /** Lo que sale de la cuenta. */
  importe: number
}

export type CuentaDeReconstruccion = {
  idCuentaTesoreria: number
  nombre: string
  filas: number
  neto: number
  saldoAntes: number
  saldoDespues: number
}

export type PlanDeReconstruccion = {
  idEmpresa: number
  desde: string
  hasta: string
  turnos: number
  pagos: number
  /** Filas de cierre de las acreditaciones más una por débito. */
  filas: number
  acreditaciones: AcreditacionDeReconstruccion[]
  debitos: DebitoDeReconstruccion[]
  cuentas: CuentaDeReconstruccion[]
  /** Marca de conciliación de la empresa; lo que tiene fecha a la marca o antes está congelado. */
  conciliadoHasta: string | null
  /** Acreditaciones de turnos cerrados a la marca de conciliación o antes: no se escriben ni cuentan en `turnos` ni `filas`. */
  congeladas: AcreditacionDeReconstruccion[]
  /** Débitos de pagos con fecha a la marca de conciliación o antes: no se escriben ni cuentan en `pagos` ni `filas`. */
  debitosCongelados: DebitoDeReconstruccion[]
}

/** `desde` y `hasta` son `yyyy-MM-dd`, ambos inclusivos, en la zona horaria de la empresa. */
export type SolicitudDeReconstruccion = { idEmpresa: number; desde: string; hasta: string }

export const clienteDeReconstruccion = {
  vistaPrevia: (solicitud: SolicitudDeReconstruccion) =>
    api.post<PlanDeReconstruccion>('/cuentas-tesoreria/reconstruccion/vista-previa', solicitud),
  aplicar: (solicitud: SolicitudDeReconstruccion) =>
    api.post<PlanDeReconstruccion>('/cuentas-tesoreria/reconstruccion', solicitud),
}

function aIso(fecha: Date): string {
  const mes = String(fecha.getMonth() + 1).padStart(2, '0')
  const dia = String(fecha.getDate()).padStart(2, '0')

  return `${fecha.getFullYear()}-${mes}-${dia}`
}

/** Del primer día del mes de `hoy` hasta `hoy`. */
export function rangoDeReconstruccionPorDefecto(hoy: Date = new Date()): { desde: string; hasta: string } {
  return { desde: aIso(new Date(hoy.getFullYear(), hoy.getMonth(), 1)), hasta: aIso(hoy) }
}

function diasEntre(desde: string, hasta: string): number {
  const [ad, md, dd] = desde.split('-').map(Number)
  const [ah, mh, dh] = hasta.split('-').map(Number)

  return Math.round((Date.UTC(ah, mh - 1, dh) - Date.UTC(ad, md - 1, dd)) / 86_400_000)
}

/** Reglas que el servidor también aplica; acá solo evitan el viaje de ida y vuelta. */
export function validarRangoDeReconstruccion(idEmpresa: string, desde: string, hasta: string): string | null {
  if (idEmpresa === '') return 'Elegí la empresa.'
  if (desde === '' || hasta === '') return 'Indicá las fechas desde y hasta.'
  if (desde > hasta) return 'La fecha "desde" no puede ser posterior a "hasta".'
  if (diasEntre(desde, hasta) > DIAS_MAXIMOS_DE_RECONSTRUCCION) {
    return `El rango no puede superar los ${DIAS_MAXIMOS_DE_RECONSTRUCCION} días.`
  }

  return null
}

/** "1 turno", "3 pagos", "2 turnos y 1 pago": de dónde salen las filas; sin pagos nombra solo los turnos. */
export function origenesDeReconstruccion(turnos: number, pagos: number): string {
  const deTurnos = `${turnos} ${turnos === 1 ? 'turno' : 'turnos'}`
  const dePagos = `${pagos} ${pagos === 1 ? 'pago' : 'pagos'}`
  if (pagos === 0) return deTurnos
  if (turnos === 0) return dePagos
  return `${deTurnos} y ${dePagos}`
}

/** Clave de lo que el usuario ve en los campos: la vista previa solo vale para la clave con la que se pidió. */
export function claveDeReconstruccion(idEmpresa: string, desde: string, hasta: string): string {
  return `${idEmpresa}|${desde}|${hasta}`
}

const TIPOS_DE_RETENCION: ReadonlySet<TipoDeComponenteDeReconstruccion> = new Set([
  'RetencionIibb',
  'RetencionIva',
  'RetencionGanancias',
])

function descontado(componentes: readonly ComponenteDeReconstruccion[], incluye: (c: ComponenteDeReconstruccion) => boolean) {
  const centavos = componentes.filter(incluye).reduce((suma, c) => suma + aCentavos(c.egreso) - aCentavos(c.ingreso), 0)

  return centavos / 100
}

/** Comisión y retenciones de una acreditación, en positivo cuando se descuentan. */
export function descuentosDeAcreditacion(a: AcreditacionDeReconstruccion): { comision: number; retenciones: number } {
  return {
    comision: descontado(a.componentes, (c) => c.tipo === 'Comision'),
    retenciones: descontado(a.componentes, (c) => TIPOS_DE_RETENCION.has(c.tipo)),
  }
}

export function errorAlReconstruir(e: unknown, escritura: boolean): string {
  const generico = escritura ? 'No se pudo reconstruir el historial.' : 'No se pudo generar la vista previa.'
  if (!(e instanceof ErrorApi)) {
    return escritura ? `${generico} ${COPIA_RESULTADO_INCIERTO}` : generico
  }

  switch (e.codigo) {
    case 'rango_invalido':
      return 'La fecha "desde" no puede ser posterior a "hasta".'
    case 'rango_demasiado_amplio':
      return `El rango no puede superar los ${DIAS_MAXIMOS_DE_RECONSTRUCCION} días.`
    case 'reconstruccion_concurrente':
      return 'Otra reconstrucción está en curso. Esperá unos segundos y generá la vista previa de nuevo.'
  }

  if (e.estado === 404) return 'La empresa ya no existe. Actualizá la pantalla.'
  if (e.estado === 403) return 'Solo un administrador puede reconstruir el historial de las cuentas.'

  if (e.estado >= 500) {
    if (!escritura) return generico

    const delServidor = e.message.trim()
    const esCommitAmbiguo = e.codigo === CODIGO_RESULTADO_INCIERTO && delServidor.length > 0

    return esCommitAmbiguo ? delServidor : `${generico} ${COPIA_RESULTADO_INCIERTO}`
  }

  return e.message.trim() || generico
}
