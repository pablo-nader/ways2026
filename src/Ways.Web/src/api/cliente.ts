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

  if (respuesta.status === 204) {
    return undefined as T
  }

  return (await respuesta.json()) as T
}

export const api = {
  get: <T>(ruta: string) => pedir<T>(ruta),
  post: <T>(ruta: string, cuerpo?: unknown) =>
    pedir<T>(ruta, { method: 'POST', body: cuerpo ? JSON.stringify(cuerpo) : undefined }),
  put: <T>(ruta: string, cuerpo: unknown) =>
    pedir<T>(ruta, { method: 'PUT', body: JSON.stringify(cuerpo) }),
  delete: <T>(ruta: string) => pedir<T>(ruta, { method: 'DELETE' }),
}
