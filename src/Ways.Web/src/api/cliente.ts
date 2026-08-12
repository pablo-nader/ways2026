/**
 * Cliente HTTP de la API.
 *
 * Todas las llamadas van con `credentials: 'include'` porque la sesión vive en una
 * cookie HttpOnly: el token nunca pasa por JavaScript.
 */

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
 */
async function exigirRespuestaOk(respuesta: Response): Promise<void> {
  if (respuesta.status === 401) {
    observadores.forEach((o) => o())
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

async function pedir<T>(ruta: string, init?: RequestInit): Promise<T> {
  const respuesta = await fetch(`/api${ruta}`, {
    ...init,
    credentials: 'include',
    headers: {
      Accept: 'application/json',
      ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
      ...init?.headers,
    },
  })

  await exigirRespuestaOk(respuesta)

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
  const respuesta = await fetch(`/api${ruta}`, { credentials: 'include' })
  await exigirRespuestaOk(respuesta)

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
