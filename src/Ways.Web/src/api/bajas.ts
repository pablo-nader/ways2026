/**
 * Copia de los rechazos de una baja lógica (etapa 20, slice 5).
 *
 * Módulo PURO: sin React y sin fetch, para que cada rama se pueda probar sin montar una pantalla.
 *
 * Dos reglas, y las dos vienen del spec `bajas-de-organizacion` → *The Complete 409 Code Set Is
 * Exactly Six Codes*:
 *
 * 1. La copia se elige por `codigo` y NUNCA por `mensaje`. El código es contrato; el mensaje es
 *    texto libre que el servidor degrada a una frase genérica cuando la tabla que bloquea no tiene
 *    etiqueta. Cambiar el `mensaje` no puede cambiar qué copia se rinde.
 * 2. El `mensaje` tampoco se tira. Es lo ÚNICO que dice QUÉ bloquea ("…porque tiene 3 ventas") y
 *    va PRIMERO; la copia elegida por el código va detrás y agrega lo que el mensaje no trae: qué
 *    hacer al respecto. Tragarlo en un error genérico dejaba al operador sin la única información
 *    accionable de la respuesta.
 *
 * Las dos excepciones a (2) son el 404 y el 5xx, y las dos están justificadas abajo.
 */
import { ErrorApi } from './cliente'

/** El sujeto de la baja, tal como entra en la copia ("No se pudo dar de baja **el tenant**"). */
export type SujetoDeBaja = 'el tenant' | 'la empresa' | 'el punto de venta' | 'el usuario'

/**
 * Los SEIS códigos de conflicto de la etapa 20, cada uno con su propia guía. Ninguna repite lo que
 * el mensaje del servidor ya dice: las cuatro de uso indican qué hacer con los datos que bloquean,
 * y las dos de mínimo estructural indican DÓNDE se hace la baja que sí corresponde.
 *
 * Congelado a propósito y sin `Record<CodigoConocido, …>`: si el servidor sumara un séptimo código,
 * cae por el fallback genérico —que igual rinde el mensaje— en vez de romper el build.
 */
const GUIA_POR_CODIGO: Readonly<Record<string, string>> = {
  tenant_en_uso: 'Dá de baja o reasigná esos datos antes de eliminar el tenant.',
  empresa_en_uso: 'Dá de baja o reasigná esos datos antes de eliminar la empresa.',
  punto_venta_en_uso: 'Dá de baja o reasigná esos datos antes de eliminar el punto de venta.',
  usuario_en_uso: 'Reasigná o dá de baja esas operaciones antes de eliminar la cuenta.',
  ultima_empresa_del_tenant: 'La baja del tenant se hace desde la pantalla de Tenants.',
  ultimo_punto_venta_de_la_empresa: 'La baja de la empresa se hace desde la pantalla de Empresas.',
}

/**
 * Copia del 404. Es deliberadamente NEUTRA y NO anexa el mensaje del servidor: un admin de tenant
 * que apunta a una entidad de otro tenant recibe exactamente este 404
 * (`PoliticaDeRoles.ValidarAlcanceDeTenant`, ADR-8), y la pantalla no puede insinuar que la fila
 * existe en otro lado. "Ya no existe" es lo mismo que ve quien apunta a un id inventado — que es
 * justo lo que el anti-oráculo pide, también en la capa de UI.
 */
const COPIA_NO_ENCONTRADO = 'Ya no existe o no está a tu alcance. Actualizá el listado.'

/**
 * Copia del 5xx, y tampoco anexa el mensaje: un 500 no trae detalle útil, trae `error_interno`.
 * Lo que sí importa es que la baja NO es reintentable automáticamente — las tres bajas corren con
 * `FabricaDeEstrategiaSinReintento`, así que un commit cuyo ACK se perdió llega como 500 y la baja
 * PUEDE haber quedado hecha. Decir "reintentá" a secas invitaba a un segundo intento sobre algo ya
 * borrado; por eso la copia manda a verificar primero.
 */
const COPIA_RESULTADO_INCIERTO =
  'No se pudo confirmar el resultado: verificá el listado antes de reintentar.'

/**
 * Texto a rendir ante un fallo de baja: `{mensaje del servidor} {guía elegida por el código}`.
 *
 * Un error que no es `ErrorApi` (la red se cayó, el fetch explotó) no trae ni código ni mensaje
 * confiable y comparte la copia del resultado incierto: tampoco se sabe si el servidor commiteó.
 */
export function copiaDeFalloDeBaja(error: unknown, sujeto: SujetoDeBaja): string {
  const encabezado = `No se pudo dar de baja ${sujeto}.`

  if (!(error instanceof ErrorApi)) return `${encabezado} ${COPIA_RESULTADO_INCIERTO}`
  if (error.estado === 404) return `${encabezado} ${COPIA_NO_ENCONTRADO}`
  if (error.estado >= 500) return `${encabezado} ${COPIA_RESULTADO_INCIERTO}`

  // El mensaje del servidor va primero porque es el que nombra el bloqueo; el encabezado solo lo
  // reemplaza cuando vino vacío, para que nunca quede un alert sin texto.
  const detalle = error.message.trim() || encabezado
  const guia = GUIA_POR_CODIGO[error.codigo]

  return guia ? `${detalle} ${guia}` : detalle
}

/** Lo mínimo que la puerta de confirmación del tenant necesita: sus contadores de hijos vivos. */
export type ContadoresDeTenant = {
  cantidadEmpresas: number
  cantidadPuntosVenta: number
  cantidadUsuarios: number
}

/**
 * Lo que se va con el tenant en la MISMA cascada (spec `bajas-de-organizacion` → *Cascade Is
 * Bounded To The Organization Projection And Shares One Instant*): sus empresas, sus puntos de
 * venta y sus usuarios. La puerta de confirmación tiene que nombrarlo — dar de baja un tenant no
 * es dar de baja una fila, y el operador no puede enterarse después.
 *
 * Los contadores en cero NO se listan: anunciar "0 usuarios" entre las cosas que se dan de baja es
 * ruido que compite con las que sí se van.
 */
export function arrastreDeTenant(contadores: ContadoresDeTenant): string[] {
  return [
    frase(contadores.cantidadEmpresas, 'empresa', 'empresas'),
    frase(contadores.cantidadPuntosVenta, 'punto de venta', 'puntos de venta'),
    frase(contadores.cantidadUsuarios, 'usuario', 'usuarios'),
  ].filter((linea): linea is string => linea !== null)
}

function frase(cantidad: number, singular: string, plural: string): string | null {
  if (cantidad <= 0) return null

  return `${cantidad} ${cantidad === 1 ? singular : plural}`
}
