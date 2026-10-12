import { useEffect, useState, type FormEvent } from 'react'

import type { CategoriaPublica } from '../api/publico'

/** Pausa entre la última tecla y la búsqueda; Enter la salta. */
const RETARDO_DE_BUSQUEDA_MS = 300

type PropsDeBarra = {
  categorias: readonly CategoriaPublica[]
  idCategoria: number | null
  alBuscar: (buscar: string) => void
  alElegirCategoria: (idCategoria: number | null) => void
}

/** Barra fija sobre el catálogo: caja de búsqueda y categorías como filtros. */
export function BarraDelCatalogo({ categorias, idCategoria, alBuscar, alElegirCategoria }: PropsDeBarra) {
  const [texto, setTexto] = useState('')

  useEffect(() => {
    const temporizador = setTimeout(() => alBuscar(texto.trim()), RETARDO_DE_BUSQUEDA_MS)

    return () => clearTimeout(temporizador)
  }, [texto, alBuscar])

  const buscarYa = (evento: FormEvent) => {
    evento.preventDefault()
    alBuscar(texto.trim())
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
      {categorias.length > 0 && (
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
