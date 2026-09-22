/**
 * Cliente HTTP de la API.
 *
 * En el navegador normal todas las llamadas van con `credentials: 'include'` porque la sesión
 * vive en una cookie HttpOnly: el token nunca pasa por JavaScript.
 *
 * stage-desktop-pos, slice 3: bajo Tauri (`corriendoEnTauri`) el shell corre en
 * `http://tauri.localhost`, cross-site respecto de la API — dos cosas cambian ahí, y solo ahí:
 * 1. La URL se antepone con `urlBaseApi()` (`entornoTauri.ts`, cacheada desde `info_app`): una
 *    ruta relativa a `http://tauri.localhost` la resuelve el protocolo de asset de Tauri, no la
 *    red.
 * 2. `credentials` pasa a `'omit'` en vez de `'include'`. La sesión bajo Tauri viaja por el
 *    header `Authorization: Bearer <token>` (adjuntado abajo cuando hay uno guardado, ver
 *    `entornoTauri.ts`) — nunca por cookie, a propósito: la API no habilita
 *    `AllowCredentials` en su política CORS para ese origen (`Program.cs`), así que un fetch
 *    cross-site con `credentials: 'include'` ahí haría fallar la respuesta completa por el
 *    chequeo de credenciales de CORS, sin que la cookie siquiera existiera del otro lado.
 *
 * El camino del navegador normal queda intacto: sin Tauri, `corriendoEnTauri()` es `false` y las
 * dos ramas de arriba son no-ops.
 */
import {
  corriendoEnTauri,
  establecerTokenDeSesionBearer,
  tokenDeSesionBearerActual,
  urlBaseApi,
} from './entornoTauri'

export class ErrorApi extends Error {
  readonly estado: number
  readonly codigo: string

  constructor(estado: number, codigo: string, mensaje: string) {
    super(mensaje)
    this.name = 'ErrorApi'
    this.estado = estado
    this.codigo = codigo
  }

  get esNoAutenticado() {
    return this.estado === 401
  }
}

/**
 * stage-pos-venta-offline-backend (Parte C): lanzado cuando `fetch` mismo rechaza — sin red, DNS
 * caído, conexión rechazada, CORS bloqueado — es decir, cuando el servidor NUNCA llegó a
 * participar. A diferencia de `ErrorApi`, que siempre nace de una `Response` real (aunque sea un
 * 5xx), `ErrorDeRed` no tiene `estado`/`codigo` porque no hay nada del servidor que leer: "el
 * servidor dijo que no" y "no hubo servidor" son hechos distintos, y hasta esta clase todo call
 * site que hacía `e instanceof ErrorApi ? ... : 'generic'` los colapsaba en el mismo string
 * genérico (ver `bajas.ts`, `copiaDeFalloDeBaja`, que ya distinguía "resultado incierto" de un
 * `ErrorApi` confirmado pero sin poder nombrar la causa: red vs. excepción rara). `causa` guarda
 * el error crudo (típicamente un `TypeError` con mensaje "Failed to fetch") para diagnóstico, sin
 * asumir su forma — un motor de navegador distinto puede lanzar otra cosa.
 */
export class ErrorDeRed extends Error {
  readonly causa: unknown

  constructor(causa: unknown) {
    super('No se pudo contactar al servidor. Revisá tu conexión.')
    this.name = 'ErrorDeRed'
    this.causa = causa
  }
}

type ProblemDetails = {
  title?: string
  detail?: string
  codigo?: string
}

/** Se dispara ante cualquier 401 para que el contexto de auth limpie la sesión. */
type ObservadorDeSesion = () => void
const observadores = new Set<ObservadorDeSesion>()

export function alPerderLaSesion(observador: ObservadorDeSesion) {
  observadores.add(observador)
  return () => {
    observadores.delete(observador)
  }
}

/**
 * Valida una `Response` cruda y lanza `ErrorApi` ante 401 (disparando los observadores de sesión)
 * o cualquier otro estado no-ok. Extraída de `pedir` (stage-11 slice 4, design decisión 12) para
 * que `descargar` comparta el mismo camino de error en vez de duplicarlo — dos copias del camino
 * de sesión expirada es una copia que se va a olvidar actualizar (`react-async-state` regla 10).
 *
 * `tokenDeLaSolicitud` (judgment-day ronda 1, FIX 5, WARNING "worsened"): el token bearer que
 * ESA solicitud llevaba adjunto, capturado por el llamador (`pedir`/`descargar`) ANTES del
 * `fetch` — nunca `tokenDeSesionBearerActual()` leído acá adentro, que para entonces ya podría ser
 * uno más nuevo. Bajo Tauri, una request vieja armada con un token ya superado por un relogueo
 * exitoso puede resolver TARDE, después de que ese token nuevo ya está instalado (memoria + disco,
 * `pos/main.tsx`) — sin este chequeo, el 401 de la request vieja pisaría esa sesión fresca en
 * ambos lados. En el navegador normal (sin Tauri) los dos lados de la comparación son siempre
 * `null`, así que el gate nunca cambia ese camino. */
async function exigirRespuestaOk(respuesta: Response, tokenDeLaSolicitud: string | null): Promise<void> {
  if (respuesta.status === 401) {
    // Solo se limpia (memoria + observadores, que a su vez limpian la sesión persistida en disco,
    // ver `pos/main.tsx`) si el token de ESTA solicitud todavía es el vigente — de lo contrario, un
    // login más nuevo ya lo reemplazó y este 401 es tardío, no hay nada que cerrar.
    if (tokenDeLaSolicitud === tokenDeSesionBearerActual()) {
      establecerTokenDeSesionBearer(null)
      observadores.forEach((o) => o())
    }
    throw new ErrorApi(401, 'no_autenticado', 'Tu sesión expiró.')
  }

  if (!respuesta.ok) {
    let problema: ProblemDetails = {}
    try {
      problema = await respuesta.json()
    } catch {
      // Respuesta sin cuerpo JSON: se usa el mensaje genérico de abajo.
    }
    throw new ErrorApi(
      respuesta.status,
      problema.codigo ?? 'error',
      problema.title ?? problema.detail ?? `Error ${respuesta.status}.`,
    )
  }
}

/** Header `Authorization: Bearer <token>` cuando corresponde (Tauri + token de sesión guardado)
 * — objeto vacío en cualquier otro caso, para poder spread-earlo sin un `if` en cada llamada. */
function headerBearerSiCorresponde(): Record<string, string> {
  if (!corriendoEnTauri()) return {}
  const token = tokenDeSesionBearerActual()
  return token ? { Authorization: `Bearer ${token}` } : {}
}

/**
 * stage-pos-venta-offline-backend (Parte C): el ÚNICO lugar que llama a `fetch` crudo — tanto
 * `pedir` como `descargar` pasan por acá para compartir el mismo camino de error (mismo motivo
 * que `exigirRespuestaOk` está extraída, arriba). Sin este `try/catch`, un `fetch` que rechaza
 * (sin red) propaga un `TypeError` crudo indistinguible de un bug de programación — con él, se
 * relanza como `ErrorDeRed`, así que todo llamador puede `instanceof`-earlo exactamente como ya
 * hace con `ErrorApi`.
 */
async function ejecutarFetch(url: string, init: RequestInit): Promise<Response> {
  try {
    return await fetch(url, init)
  } catch (error) {
    throw new ErrorDeRed(error)
  }
}

async function pedir<T>(ruta: string, init?: RequestInit): Promise<T> {
  // Capturado ANTES del fetch (FIX 5, ver el doc-comment de `exigirRespuestaOk`): es el token con
  // el que ESTA solicitud sale a la red, no el que esté vigente cuando la respuesta vuelva.
  const tokenDeLaSolicitud = tokenDeSesionBearerActual()
  const respuesta = await ejecutarFetch(`${urlBaseApi()}/api${ruta}`, {
    ...init,
    credentials: corriendoEnTauri() ? 'omit' : 'include',
    headers: {
      Accept: 'application/json',
      ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
      ...headerBearerSiCorresponde(),
      ...init?.headers,
    },
  })

  await exigirRespuestaOk(respuesta, tokenDeLaSolicitud)

  if (respuesta.status === 204) {
    return undefined as T
  }

  return (await respuesta.json()) as T
}

/** Nombre de archivo desde `Content-Disposition`: `filename*` (RFC 5987, UTF-8) gana sobre
 * `filename` cuando ambos están presentes — el ASCII es el fallback, no la fuente de verdad
 * (`NombreDeArchivo.Construir` del lado del servidor siempre manda ambos). Sin ninguno de los dos
 * (p. ej. si la API algún día deja de ser same-origin, design: Open Questions) cae a un nombre
 * genérico en vez de romper la descarga. */
export function nombreDeArchivo(respuesta: Response): string {
  const disposicion = respuesta.headers.get('Content-Disposition') ?? ''

  const conFilenameEstrella = /filename\*=UTF-8''([^;]+)/i.exec(disposicion)
  if (conFilenameEstrella) return decodeURIComponent(conFilenameEstrella[1].trim())

  const conFilename = /filename="?([^";]+)"?/i.exec(disposicion)
  if (conFilename) return conFilename[1].trim()

  return 'descarga.xlsx'
}

/**
 * Descarga un archivo binario (`GET {ruta}`, típicamente un `/export`): comparte el camino de
 * error de `pedir` vía `exigirRespuestaOk` — 401 dispara `alPerderLaSesion`, cualquier otro estado
 * no-ok lanza `ErrorApi` para que el llamador lo funnelee a su propio estado (nunca una navegación
 * a un JSON crudo). El `URL.revokeObjectURL` corre en un `setTimeout(…, 0)` posterior al click del
 * enlace sintético: revocar en el mismo tick cancela la descarga en algunos navegadores (design:
 * Open Questions).
 */
async function descargar(ruta: string): Promise<void> {
  const tokenDeLaSolicitud = tokenDeSesionBearerActual()
  const respuesta = await ejecutarFetch(`${urlBaseApi()}/api${ruta}`, {
    credentials: corriendoEnTauri() ? 'omit' : 'include',
    headers: headerBearerSiCorresponde(),
  })
  await exigirRespuestaOk(respuesta, tokenDeLaSolicitud)

  const blob = await respuesta.blob()
  const nombre = nombreDeArchivo(respuesta)
  const url = URL.createObjectURL(blob)

  const enlace = document.createElement('a')
  enlace.href = url
  enlace.download = nombre
  document.body.appendChild(enlace)
  enlace.click()
  enlace.remove()

  setTimeout(() => URL.revokeObjectURL(url), 0)
}

export const api = {
  get: <T>(ruta: string) => pedir<T>(ruta),
  post: <T>(ruta: string, cuerpo?: unknown) =>
    pedir<T>(ruta, { method: 'POST', body: cuerpo ? JSON.stringify(cuerpo) : undefined }),
  put: <T>(ruta: string, cuerpo: unknown) =>
    pedir<T>(ruta, { method: 'PUT', body: JSON.stringify(cuerpo) }),
  delete: <T>(ruta: string) => pedir<T>(ruta, { method: 'DELETE' }),
  descargar: (ruta: string) => descargar(ruta),
}
