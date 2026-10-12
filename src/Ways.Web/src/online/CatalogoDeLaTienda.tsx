import { useState } from 'react'

import { Chevrones } from '../publico/Glifos'
import { BarraDelCatalogo } from './BarraDelCatalogo'
import { TarjetaDeArticulo } from './TarjetaDeArticulo'
import { useCatalogo } from './useCatalogo'

function CatalogoEnPreparacion() {
  return (
    <main className="publico-portada">
      <Chevrones className="publico-portada-chevrones" />
      <h1 className="publico-titulo publico-portada-titular">El catálogo se está preparando.</h1>
      <div className="publico-barra" aria-hidden="true" />
      <div className="publico-portada-acciones">
        <a className="publico-boton publico-boton-contorno" href="/">
          Volver al inicio
        </a>
      </div>
    </main>
  )
}

function AvisoDeError({ mensaje, alReintentar }: { mensaje: string; alReintentar: () => void }) {
  return (
    <div className="publico-catalogo-aviso" role="alert">
      <p>{mensaje}</p>
      <button type="button" className="publico-boton publico-boton-contorno" onClick={alReintentar}>
        Reintentar
      </button>
    </div>
  )
}

function EsqueletoDelCatalogo() {
  return (
    <main className="publico-catalogo" role="status" aria-busy="true" aria-label="Cargando el catálogo">
      <ul className="publico-grilla" aria-hidden="true">
        {Array.from({ length: 6 }, (_, i) => (
          <li key={i} className="publico-articulo publico-articulo-esqueleto" />
        ))}
      </ul>
    </main>
  )
}

/**
 * Catálogo de la tienda: barra de búsqueda y categorías, grilla de artículos y "Ver más".
 * Mientras no hay una primera respuesta buena se ve el esqueleto o el error; si el comercio no
 * publicó nada (sin filtros y sin artículos) se ve la pantalla de "se está preparando".
 */
export function CatalogoDeLaTienda() {
  const [buscar, setBuscar] = useState('')
  const [idCategoria, setIdCategoria] = useState<number | null>(null)
  const catalogo = useCatalogo({ buscar, idCategoria })

  if (!catalogo.cargadoAlgunaVez) {
    return catalogo.fase === 'error' ? (
      <main className="publico-catalogo">
        <AvisoDeError mensaje="No pudimos cargar el catálogo. Probá de nuevo en un momento." alReintentar={catalogo.reintentar} />
      </main>
    ) : (
      <EsqueletoDelCatalogo />
    )
  }

  const sinFiltros = buscar === '' && idCategoria === null
  if (catalogo.fase === 'listo' && sinFiltros && catalogo.total === 0) return <CatalogoEnPreparacion />

  const cargando = catalogo.fase === 'cargando'

  return (
    <main className="publico-catalogo">
      <h1 className="publico-solo-lectura">Catálogo</h1>
      <BarraDelCatalogo
        categorias={catalogo.categorias}
        idCategoria={idCategoria}
        alBuscar={setBuscar}
        alElegirCategoria={setIdCategoria}
      />
      {catalogo.fase === 'error' ? (
        <AvisoDeError mensaje="No pudimos cargar los artículos. Probá de nuevo en un momento." alReintentar={catalogo.reintentar} />
      ) : (
        <>
          <p className="publico-resultados" role="status">
            {catalogo.total} {catalogo.total === 1 ? 'artículo' : 'artículos'}
          </p>
          {catalogo.articulos.length === 0 && !cargando ? (
            <p className="publico-catalogo-aviso">No encontramos artículos para esa búsqueda.</p>
          ) : (
            <ul className={cargando ? 'publico-grilla publico-grilla-atenuada' : 'publico-grilla'} aria-busy={cargando}>
              {catalogo.articulos.map((articulo) => (
                <TarjetaDeArticulo key={articulo.id} articulo={articulo} />
              ))}
            </ul>
          )}
          {catalogo.errorDeMas && (
            <p className="publico-catalogo-aviso" role="alert">
              No pudimos cargar más artículos. Probá de nuevo.
            </p>
          )}
          {catalogo.hayMas && !cargando && (
            <div className="publico-ver-mas">
              <button
                type="button"
                className="publico-boton publico-boton-contorno"
                disabled={catalogo.cargandoMas}
                onClick={() => void catalogo.cargarMas()}
              >
                {catalogo.cargandoMas ? 'Cargando…' : 'Ver más'}
              </button>
            </div>
          )}
        </>
      )}
    </main>
  )
}
