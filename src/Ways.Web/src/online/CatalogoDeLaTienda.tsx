import { useEffect, useState } from 'react'

import { Chevrones } from '../publico/Glifos'
import { BarraDelCatalogo } from './BarraDelCatalogo'
import { articuloParaElCarrito } from './carrito'
import { useCarrito } from './CarritoContext'
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
    <main className="publico-catalogo">
      <div role="status" aria-busy="true" aria-label="Cargando el catálogo">
        <ul className="publico-grilla" aria-hidden="true">
          {Array.from({ length: 6 }, (_, i) => (
            <li key={i} className="publico-articulo publico-articulo-esqueleto" />
          ))}
        </ul>
      </div>
    </main>
  )
}

/**
 * Catálogo de la tienda: barra de búsqueda y categorías, grilla de artículos y "Ver más".
 * Mientras no hay una primera respuesta buena se ve el esqueleto o el error. Si esa primera
 * respuesta, pedida sin filtros, no trae artículos (el comercio no publicó nada) se ve la pantalla
 * de "se está preparando". Si en cambio trajo artículos, la barra no se desmonta más: ni un
 * resultado vacío ni limpiar la búsqueda la reemplazan, así no se pierden el foco ni lo tipeado.
 */
export function CatalogoDeLaTienda() {
  const [buscar, setBuscar] = useState('')
  const [idCategoria, setIdCategoria] = useState<number | null>(null)
  const catalogo = useCatalogo({ buscar, idCategoria })
  const { cantidades, sincronizar } = useCarrito()

  // Cada lista que llega (primera lectura, otro filtro, "Ver más") pone al día las líneas del
  // carrito cuyo artículo figura en ella: nombre, precio, unidad e imagen salen de la última lectura.
  const articulosLeidos = catalogo.articulos
  useEffect(() => {
    sincronizar(articulosLeidos.map(articuloParaElCarrito))
  }, [articulosLeidos, sincronizar])

  if (!catalogo.cargadoAlgunaVez) {
    return catalogo.fase === 'error' ? (
      <main className="publico-catalogo">
        <AvisoDeError mensaje="No pudimos cargar el catálogo. Probá de nuevo en un momento." alReintentar={catalogo.reintentar} />
      </main>
    ) : (
      <EsqueletoDelCatalogo />
    )
  }

  if (catalogo.vacioAlEntrar) return <CatalogoEnPreparacion />

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
            <p className="publico-catalogo-aviso">
              {catalogo.respuestaSinFiltros
                ? 'Por ahora no hay artículos publicados.'
                : 'No encontramos artículos para esa búsqueda.'}
            </p>
          ) : (
            <ul className={cargando ? 'publico-grilla publico-grilla-atenuada' : 'publico-grilla'} aria-busy={cargando}>
              {catalogo.articulos.map((articulo) => (
                <TarjetaDeArticulo key={articulo.id} articulo={articulo} cantidad={cantidades.get(articulo.id) ?? 0} />
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
