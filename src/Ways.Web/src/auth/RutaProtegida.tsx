import type { ReactNode } from 'react'
import { Navigate, useLocation } from 'react-router'
import { useAuth } from './useAuth'
import { Cargando } from '../componentes/Cargando'

type Props = {
  children: ReactNode
  /** Si se indica, además de estar autenticado el rol tiene que estar en la lista. */
  rolesPermitidos?: number[]
}

/**
 * No hay zonas públicas. Sin sesión, se redirige a /login guardando la ruta original
 * para volver ahí una vez autenticado.
 */
export function RutaProtegida({ children, rolesPermitidos }: Props) {
  const { usuario, cargando } = useAuth()
  const ubicacion = useLocation()

  if (cargando) {
    return <Cargando />
  }

  if (!usuario) {
    return <Navigate to="/login" replace state={{ desde: ubicacion }} />
  }

  if (rolesPermitidos && !rolesPermitidos.includes(usuario.rolId)) {
    return <Navigate to="/" replace />
  }

  return <>{children}</>
}
