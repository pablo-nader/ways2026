import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react'

import type { CategoriaPublica } from '../api/publico'

/** Pausa entre la última tecla y la búsqueda; Enter la salta. */
const RETARDO_DE_BUSQUEDA_MS = 300

type PropsDeBarra = {
  categorias: readonly CategoriaPublica[]
  idCategoria: number | null
  alBuscar: (buscar: string) => void
  alElegirCategoria: (idCategoria: number | null) => void
}

/**
 * Barra fija sobre el catálogo: caja de búsqueda y categorías como filtros. Avisa una búsqueda solo
 * cuando difiere de la última que avisó, así que al montarse no agenda nada y volver al texto de
 * antes dentro de la pausa no dispara una búsqueda de más. Si hay una categoría elegida, "Todos"
 * se ve aunque la respuesta no traiga categorías, para poder salir de ese filtro.
 */
export function BarraDelCatalogo({ categorias, idCategoria, alBuscar, alElegirCategoria }: PropsDeBarra) {
  const [texto, setTexto] = useState('')
  const ultimaBusqueda = useRef('')

  const buscar = useCallback(
    (valor: string) => {
      if (valor === ultimaBusqueda.current) return
      ultimaBusqueda.current = valor
      alBuscar(valor)
    },
    [alBuscar],
  )

  useEffect(() => {
    const limpio = texto.trim()
    if (limpio === ultimaBusqueda.current) return undefined
    const temporizador = setTimeout(() => buscar(limpio), RETARDO_DE_BUSQUEDA_MS)

    return () => clearTimeout(temporizador)
  }, [texto, buscar])

  const buscarYa = (evento: FormEvent) => {
    evento.preventDefault()
    buscar(texto.trim())
  }

  return (
    <div className="publico-barra-catalogo">
      <form role="search" className="publico-busqueda" onSubmit={buscarYa}>
        <input
          className="publico-busqueda-campo"
          type="search"
          maxLength={100}
          placeholder="Buscar artículos"
          aria-label="Buscar artículos"
          autoComplete="off"
          value={texto}
          onChange={(evento) => setTexto(evento.target.value)}
        />
      </form>
      {(categorias.length > 0 || idCategoria !== null) && (
        <div className="publico-chips" role="group" aria-label="Categorías">
          <button
            type="button"
            className="publico-chip"
            aria-pressed={idCategoria === null}
            onClick={() => alElegirCategoria(null)}
          >
            Todos
          </button>
          {categorias.map((categoria) => (
            <button
              key={categoria.id}
              type="button"
              className="publico-chip"
              aria-pressed={idCategoria === categoria.id}
              onClick={() => alElegirCategoria(categoria.id)}
            >
              {categoria.nombre}{' '}
              <span className="publico-chip-cuenta">{categoria.cantidad}</span>
            </button>
          ))}
        </div>
      )}
    </div>
  )
}
