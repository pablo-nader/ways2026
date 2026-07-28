import { Box } from '../componentes/Box'
import { useAuth } from '../auth/useAuth'

export function Inicio() {
  const { usuario } = useAuth()

  return (
    <div className="container py-4">
      <Box titulo="Inicio" variante="inverse">
        <h2 className="mb-3">Hola mundo</h2>
        <p className="mb-1">
          Sesión iniciada como <strong>{usuario?.usuario}</strong> con rol{' '}
          <strong>{usuario?.rol}</strong>.
        </p>
        {usuario?.ultimaConexion && (
          <p className="text-muted mb-0">
            Conexión anterior: {new Date(usuario.ultimaConexion).toLocaleString('es-AR')}
          </p>
        )}
      </Box>
    </div>
  )
}
