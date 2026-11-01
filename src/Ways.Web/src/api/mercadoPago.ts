/**
 * Cliente y reglas puras de la conexión de una empresa con Mercado Pago (OAuth, solo Admin).
 *
 * El `code` y el `state` que devuelve Mercado Pago llegan en el FRAGMENTO de la URL (`#`) y nunca en la consulta:
 * el navegador no manda el fragmento al servidor ni en el Referer. Se leen una vez, se borran de la barra y viajan
 * solo en el cuerpo del POST autenticado que completa el canje. Nada de esto se loguea ni se renderiza.
 */
import { api, ErrorApi } from './cliente'

export type EstadoDeConexionMercadoPago = {
  conectada: boolean
  /** `activa`, `requiere_reconexion` o ausente cuando la empresa nunca se conectó (o se desconectó). */
  estado?: 'activa' | 'requiere_reconexion' | null
  idCuentaTesoreria?: number | null
  nombreCuenta?: string | null
  idUsuarioMercadoPago?: number | null
  fechaConexion?: string | null
  accessTokenExpiraEn?: string | null
}

export type InicioDeConexionMercadoPago = { urlAutorizacion: string }

export type SolicitudDeCompletarConexion = { code: string; state: string }

const base = (idEmpresa: number) => `/empresas/${idEmpresa}/mercadopago/conexion`

export const clienteDeMercadoPago = {
  obtener: (idEmpresa: number) => api.get<EstadoDeConexionMercadoPago>(base(idEmpresa)),
  iniciar: (idEmpresa: number, idCuentaTesoreria: number) =>
    api.post<InicioDeConexionMercadoPago>(`${base(idEmpresa)}/inicio`, { idCuentaTesoreria }),
  completar: (idEmpresa: number, solicitud: SolicitudDeCompletarConexion) =>
    api.post<EstadoDeConexionMercadoPago>(`${base(idEmpresa)}/completar`, solicitud),
  desconectar: (idEmpresa: number) => api.delete(base(idEmpresa)),
}

export type EstadoDeLocal = 'sin_conexion' | 'no_registrada' | 'registrada' | 'desactualizada'

/** Estado de una sucursal como local de Mercado Pago. `camposFaltantes` son nombres de columna de Ways (`calle`, `altura`…). */
export type EstadoDeLocalMercadoPago = {
  estado: EstadoDeLocal
  idStoreMercadoPago: number | null
  externalId: string | null
  fechaRegistro: string | null
  fechaSincronizacion: string | null
  camposFaltantes: string[]
}

export type EstadoDeCaja = 'sin_conexion' | 'local_no_registrado' | 'no_registrada' | 'registrada' | 'desactualizada'

/** Estado de un punto de venta como caja de Mercado Pago; `urlQr` es la imagen del QR estático. */
export type EstadoDeCajaMercadoPago = {
  estado: EstadoDeCaja
  idPosMercadoPago: number | null
  idStoreMercadoPago: number | null
  externalId: string | null
  urlQr: string | null
  fechaRegistro: string | null
  fechaSincronizacion: string | null
  idTerminalPoint: string | null
}

export type TerminalDelPuntoDeVenta = { id: string; modoOperativo: string | null; asignada: boolean }

export type ListadoDeTerminales = {
  terminales: TerminalDelPuntoDeVenta[]
  /** Cómo vincular la terminal en la app de Mercado Pago; el servidor la manda solo con la lista vacía. */
  instrucciones: string | null
  idTerminalAsignada: string | null
  /** Falso con una terminal guardada: la terminal ya no figura entre las de la caja (asignación vieja). */
  asignadaVigente: boolean
}

export type AsignacionDeTerminal = { idTerminalPoint: string | null; fechaTerminal: string | null; modoCambiado: boolean }

const rutaDeLocal = (idSucursal: number) => `/sucursales/${idSucursal}/mercadopago`
const rutaDeCaja = (idPuntoVenta: number) => `/puntos-venta/${idPuntoVenta}/mercadopago`

export const clienteDeLocalesMercadoPago = {
  obtener: (idSucursal: number) => api.get<EstadoDeLocalMercadoPago>(rutaDeLocal(idSucursal)),
  registrar: (idSucursal: number) => api.post<EstadoDeLocalMercadoPago>(rutaDeLocal(idSucursal)),
}

export const clienteDeCajasMercadoPago = {
  obtener: (idPuntoVenta: number) => api.get<EstadoDeCajaMercadoPago>(rutaDeCaja(idPuntoVenta)),
  registrar: (idPuntoVenta: number) => api.post<EstadoDeCajaMercadoPago>(rutaDeCaja(idPuntoVenta)),
  listarTerminales: (idPuntoVenta: number) => api.get<ListadoDeTerminales>(`${rutaDeCaja(idPuntoVenta)}/terminales`),
  asignarTerminal: (idPuntoVenta: number, idTerminal: string) =>
    api.post<AsignacionDeTerminal>(`${rutaDeCaja(idPuntoVenta)}/terminal`, { idTerminal }),
  quitarTerminal: (idPuntoVenta: number) => api.delete<AsignacionDeTerminal>(`${rutaDeCaja(idPuntoVenta)}/terminal`),
}

/** Lo que dejó el callback del servidor en el fragmento de la URL. */
export type RetornoDeMercadoPago =
  | { tipo: 'completar'; idEmpresa: number; code: string; state: string }
  | { tipo: 'cancelado'; idEmpresa: number }

/**
 * Interpreta `location.hash`. Devuelve `null` si no es un retorno de Mercado Pago o si viene incompleto o con una empresa
 * que no es un entero positivo: ante la duda no se canjea nada.
 */
export function leerRetornoDeMercadoPago(hash: string): RetornoDeMercadoPago | null {
  const parametros = new URLSearchParams(hash.startsWith('#') ? hash.slice(1) : hash)
  const resultado = parametros.get('mercadopago')
  const textoDeEmpresa = parametros.get('empresa') ?? ''
  if (!/^[1-9]\d{0,9}$/.test(textoDeEmpresa)) return null
  const idEmpresa = Number(textoDeEmpresa)

  if (resultado === 'cancelado') return { tipo: 'cancelado', idEmpresa }

  if (resultado === 'completar') {
    const code = parametros.get('code')
    const state = parametros.get('state')
    if (!code || !state) return null

    return { tipo: 'completar', idEmpresa, code, state }
  }

  return null
}

/** Solo se navega a una URL http(s) bien formada: la respuesta del servidor no se pasa a `location` sin mirarla. */
export function esUrlDeAutorizacionNavegable(url: unknown): url is string {
  if (typeof url !== 'string') return false
  try {
    const { protocol } = new URL(url)

    return protocol === 'https:' || protocol === 'http:'
  } catch {
    return false
  }
}

export const AVISO_DE_CONEXION_CANCELADA = 'Se canceló la conexión con Mercado Pago. No se guardó nada.'
export const AVISO_DE_CONEXION_COMPLETADA = 'Mercado Pago quedó conectado.'
export const AVISO_DE_DESCONEXION = 'Se desconectó Mercado Pago.'

const COPIAS_POR_CODIGO: Readonly<Record<string, string>> = {
  mercadopago_no_configurado: 'Mercado Pago no está configurado en el servidor. Pedile al administrador de la plataforma que lo configure.',
  mercadopago_ya_conectada: 'La empresa ya tiene Mercado Pago conectado.',
  mercadopago_cuenta_en_uso: 'Esa cuenta de Mercado Pago ya está conectada a otra empresa.',
  mercadopago_intercambio_fallido: 'Mercado Pago no aceptó la conexión. Volvé a intentarlo.',
  mercadopago_estado_ajeno: 'Esa conexión la inició otra sesión o otra empresa. Iniciala de nuevo desde acá.',
  mercadopago_estado_invalido: 'El enlace de conexión venció o no es válido. Iniciá la conexión de nuevo.',
  cuenta_tesoreria_inactiva: 'La cuenta de tesorería elegida está inactiva.',
  cuenta_tesoreria_de_otra_empresa: 'La cuenta de tesorería elegida pertenece a otra empresa.',
  referencia_invalida: 'La cuenta de tesorería elegida ya no existe.',
  mercadopago_no_conectada: 'La empresa no tiene Mercado Pago conectado. Conectalo desde la pantalla de Empresas.',
  mercadopago_requiere_reconexion: 'Hay que volver a conectar Mercado Pago desde la pantalla de Empresas.',
  mercadopago_renovacion_rechazada: 'Mercado Pago rechazó la renovación de la conexión. Volvé a conectarla desde la pantalla de Empresas.',
  mercadopago_credenciales_rechazadas: 'Mercado Pago rechazó las credenciales de la plataforma. Pedile al administrador de la plataforma que las revise.',
  mercadopago_tokens_ilegibles: 'La conexión con Mercado Pago no se puede leer. Volvé a conectarla desde la pantalla de Empresas.',
  direccion_incompleta_para_mercadopago: 'A la sucursal le faltan datos de la dirección para registrarla en Mercado Pago. Completalos y volvé a intentar.',
  nombre_invalido_para_mercadopago: 'El nombre del punto de venta no es válido para Mercado Pago. Editalo y volvé a intentar.',
  mercadopago_local_no_registrado: 'Primero hay que registrar la sucursal de este punto de venta en Mercado Pago.',
  mercadopago_local_duplicado: 'Ya existe en Mercado Pago más de un local con el identificador de esta sucursal. Resolvelo en Mercado Pago.',
  mercadopago_caja_duplicada: 'Ya existe en Mercado Pago más de una caja con el identificador de este punto de venta. Resolvelo en Mercado Pago.',
  mercadopago_punto_venta_movido: 'El punto de venta cambió de sucursal mientras se hacía la operación. Cerrá esta ventana y volvé a abrirla.',
  mercadopago_caja_no_registrada: 'El punto de venta todavía no está registrado como caja en Mercado Pago. Registralo primero.',
  mercadopago_terminal_no_vinculada: 'Esa terminal no está vinculada a la caja de este punto de venta. Vinculala desde la app de Mercado Pago y actualizá la lista.',
  mercadopago_terminal_modelo_no_admitido: 'Ese modelo de terminal no admite el modo punto de venta (PDV).',
  mercadopago_caja_con_terminal_en_pdv: 'La caja tiene una terminal en modo PDV que Mercado Pago no deja asignar así. Revisala en la app de Mercado Pago.',
  terminal_point_invalida: 'El identificador de la terminal no es válido.',
  mercadopago_solicitud_rechazada: 'Mercado Pago rechazó la solicitud. Revisá los datos y volvé a intentar.',
  mercadopago_respuesta_invalida: 'Mercado Pago respondió algo inesperado. No se guardó nada; volvé a intentar.',
  mercadopago_no_disponible: 'Mercado Pago no está disponible en este momento. Volvé a intentar en unos minutos.',
}

export type AccionDeMercadoPago =
  | 'leer'
  | 'iniciar'
  | 'completar'
  | 'desconectar'
  | 'leer_local'
  | 'registrar_local'
  | 'leer_caja'
  | 'registrar_caja'
  | 'leer_terminales'
  | 'asignar_terminal'
  | 'quitar_terminal'

const COPIA_GENERICA: Readonly<Record<AccionDeMercadoPago, string>> = {
  leer: 'No se pudo leer el estado de Mercado Pago.',
  iniciar: 'No se pudo iniciar la conexión con Mercado Pago.',
  completar: 'No se pudo completar la conexión con Mercado Pago.',
  desconectar: 'No se pudo desconectar Mercado Pago.',
  leer_local: 'No se pudo leer el estado de la sucursal en Mercado Pago.',
  registrar_local: 'No se pudo registrar la sucursal en Mercado Pago.',
  leer_caja: 'No se pudo leer el estado del punto de venta en Mercado Pago.',
  registrar_caja: 'No se pudo registrar el punto de venta en Mercado Pago.',
  leer_terminales: 'No se pudieron leer las terminales de Mercado Pago.',
  asignar_terminal: 'No se pudo asignar la terminal.',
  quitar_terminal: 'No se pudo quitar la terminal.',
}

const COPIA_DE_404: Readonly<Partial<Record<AccionDeMercadoPago, string>>> = {
  leer_local: 'La sucursal ya no existe. Recargá la pantalla.',
  registrar_local: 'La sucursal ya no existe. Recargá la pantalla.',
  leer_caja: 'El punto de venta ya no existe. Recargá la pantalla.',
  registrar_caja: 'El punto de venta ya no existe. Recargá la pantalla.',
  leer_terminales: 'El punto de venta ya no existe. Recargá la pantalla.',
  asignar_terminal: 'El punto de venta ya no existe. Recargá la pantalla.',
  quitar_terminal: 'El punto de venta ya no existe. Recargá la pantalla.',
}

/**
 * Copia de un rechazo. Se elige por `codigo` (contrato) y nunca por el mensaje del servidor; un código desconocido cae en
 * la copia genérica de la acción. Un 404 de la empresa tiene su propia copia; un 403 sin código conocido también.
 */
export function copiaDeFalloDeMercadoPago(error: unknown, accion: AccionDeMercadoPago): string {
  if (error instanceof ErrorApi) {
    const porCodigo = COPIAS_POR_CODIGO[error.codigo]
    if (porCodigo) return porCodigo
    if (error.estado === 404) {
      return COPIA_DE_404[accion] ?? 'La empresa o su conexión con Mercado Pago ya no existe. Recargá la pantalla.'
    }
    if (error.estado === 403) return 'No tenés permiso para administrar Mercado Pago.'
  }

  return COPIA_GENERICA[accion]
}
