import type { ReactNode } from 'react'

import type { VitrinaPublica } from '../api/tipos'
import { MarcaWays } from './MarcaWays'
import type { ResultadoDeVitrina } from './useVitrinaPublica'

function Esqueleto() {
  return (
    <div className="publico-pagina publico-esqueleto" role="status" aria-busy="true" aria-label="Cargando">
      <div className="publico-bloque publico-bloque-barra" />
      <div className="publico-bloque publico-bloque-titulo" />
      <div className="publico-bloque publico-bloque-titulo publico-bloque-corto" />
      <div className="publico-bloque publico-bloque-texto" />
      <div className="publico-bloque publico-bloque-texto" />
    </div>
  )
}

function PantallaCalma({ mensaje, alReintentar }: { mensaje: string; alReintentar?: () => void }) {
  return (
    <main className="publico-calma">
      <MarcaWays tema="oscuro" className="publico-calma-marca" />
      <p className="publico-calma-mensaje">{mensaje}</p>
      {alReintentar && (
        <button type="button" className="publico-boton publico-boton-contorno" onClick={alReintentar}>
          Reintentar
        </button>
      )}
    </main>
  )
}

/**
 * Decide qué se ve según el estado de la vitrina: el esqueleto mientras carga, una pantalla
 * tranquila si no está publicada o falló, y la página (`children`) cuando hay vitrina.
 */
export function PorEstadoDeVitrina({
  resultado,
  recargar,
  children,
}: {
  resultado: ResultadoDeVitrina
  recargar: () => void
  children: (vitrina: VitrinaPublica) => ReactNode
}) {
  return (
    <div className="publico">
      {resultado.estado === 'cargando' && <Esqueleto />}
      {resultado.estado === 'sin_publicar' && <PantallaCalma mensaje="Este comercio todavía no publicó su página." />}
      {resultado.estado === 'error' && (
        <PantallaCalma mensaje="No pudimos cargar la página. Probá de nuevo en un momento." alReintentar={recargar} />
      )}
      {resultado.estado === 'publicada' && children(resultado.vitrina)}
    </div>
  )
}
