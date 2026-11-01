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
}

export type AccionDeMercadoPago = 'leer' | 'iniciar' | 'completar' | 'desconectar'

const COPIA_GENERICA: Readonly<Record<AccionDeMercadoPago, string>> = {
  leer: 'No se pudo leer el estado de Mercado Pago.',
  iniciar: 'No se pudo iniciar la conexión con Mercado Pago.',
  completar: 'No se pudo completar la conexión con Mercado Pago.',
  desconectar: 'No se pudo desconectar Mercado Pago.',
}

/**
 * Copia de un rechazo. Se elige por `codigo` (contrato) y nunca por el mensaje del servidor; un código desconocido cae en
 * la copia genérica de la acción. Un 404 de la empresa tiene su propia copia; un 403 sin código conocido también.
 */
export function copiaDeFalloDeMercadoPago(error: unknown, accion: AccionDeMercadoPago): string {
  if (error instanceof ErrorApi) {
    const porCodigo = COPIAS_POR_CODIGO[error.codigo]
    if (porCodigo) return porCodigo
    if (error.estado === 404) return 'La empresa o su conexión con Mercado Pago ya no existe. Recargá la pantalla.'
    if (error.estado === 403) return 'No tenés permiso para administrar Mercado Pago.'
  }

  return COPIA_GENERICA[accion]
}
