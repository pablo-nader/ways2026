import { useContext } from 'react'
import { AuthContext } from './AuthContext'

export function useAuth() {
  const contexto = useContext(AuthContext)

  if (!contexto) {
    throw new Error('useAuth tiene que usarse dentro de un AuthProvider.')
  }

  return contexto
}
