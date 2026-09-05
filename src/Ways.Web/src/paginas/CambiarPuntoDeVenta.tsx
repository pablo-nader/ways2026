import { useLocation, useNavigate } from 'react-router'
import { Box } from '../componentes/Box'
import { ElegirPuntoDeVenta } from '../puntoVenta/ElegirPuntoDeVenta'
import { usePuntoVenta } from '../puntoVenta/usePuntoVenta'

type EstadoDeRuta = { desde?: { pathname: string; search: string } }

const RUTA_PROPIA = '/punto-de-venta'

export function CambiarPuntoDeVenta() {
  const { puntosVenta, puntoVenta, elegir } = usePuntoVenta()
  const navegar = useNavigate()
  const ubicacion = useLocation()

  const desde = (ubicacion.state as EstadoDeRuta | null)?.desde
  // Se vuelve a la pantalla que pidió el cambio; si fue esta misma o no hubo ninguna, al inicio.
  const destino = desde && desde.pathname !== RUTA_PROPIA ? `${desde.pathname}${desde.search ?? ''}` : '/'

  function alElegir(id: number) {
    elegir(id)
    navegar(destino, { replace: true })
  }

  if (puntosVenta.length > 1) {
    return <ElegirPuntoDeVenta puntosVenta={puntosVenta} actual={puntoVenta?.id ?? null} alElegir={alElegir} />
  }

  const unico = puntosVenta.at(0)

  return (
    <div className="container py-4">
      <Box titulo="Punto de venta" variante="inverse">
        {unico ? (
          <>
            <p className="h4 mb-2">{unico.nombre}</p>
            <p className="text-muted mb-0">Este es el único punto de venta disponible.</p>
          </>
        ) : (
          <div className="alert alert-warning rounded-0 mb-0">Sin puntos de venta disponibles</div>
        )}
      </Box>
    </div>
  )
}
