import { createContext, useCallback, useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { api, alPerderLaSesion, ErrorApi } from '../api/cliente'
import type { UsuarioAutenticado } from '../api/tipos'

type EstadoAuth = {
  usuario: UsuarioAutenticado | null
  cargando: boolean
  iniciarSesion: (mail: string, password: string) => Promise<void>
  cerrarSesion: () => Promise<void>
}

export const AuthContext = createContext<EstadoAuth | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [usuario, setUsuario] = useState<UsuarioAutenticado | null>(null)
  const [cargando, setCargando] = useState(true)

  // Al arrancar se pregunta quién es: si la cookie sigue viva la sesión continúa,
  // si venció por inactividad la API responde 401 y quedamos deslogueados.
  useEffect(() => {
    let vigente = true

    api
      .get<UsuarioAutenticado>('/auth/me')
      .then((u) => vigente && setUsuario(u))
      .catch(() => vigente && setUsuario(null))
      .finally(() => vigente && setCargando(false))

    return () => {
      vigente = false
    }
  }, [])

  // Cualquier 401 en cualquier llamada tira la sesión abajo.
  useEffect(() => alPerderLaSesion(() => setUsuario(null)), [])

  const iniciarSesion = useCallback(async (mail: string, password: string) => {
    const autenticado = await api.post<UsuarioAutenticado>('/auth/login', {
      mail,
      password,
    })
    setUsuario(autenticado)
  }, [])

  const cerrarSesion = useCallback(async () => {
    try {
      await api.post('/auth/logout')
    } catch (error) {
      // Si ya estaba vencida no importa: igual limpiamos del lado del cliente.
      if (!(error instanceof ErrorApi && error.esNoAutenticado)) throw error
    } finally {
      setUsuario(null)
    }
  }, [])

  const valor = useMemo(
    () => ({ usuario, cargando, iniciarSesion, cerrarSesion }),
    [usuario, cargando, iniciarSesion, cerrarSesion],
  )

  return <AuthContext.Provider value={valor}>{children}</AuthContext.Provider>
}
