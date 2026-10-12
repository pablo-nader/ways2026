import { useRef, useState } from 'react'
import { clienteDeArticulos } from '../../api/articulos'
import { ErrorApi } from '../../api/cliente'
import { esArticuloElegibleParaCombo } from '../../api/combosAbm'
import type { ArticuloListado } from '../../api/tipos'

type Props = {
  /** Posición (desde 1) de la fila: nombra los controles para que sean únicos en el editor. */
  numero: number
  idArticulo: number | ''
  nombre: string
  disabled: boolean
  onSeleccionar: (id: number, nombre: string) => void
}

/**
 * Buscador de artículo de una fila del editor de componentes. Busca contra `/api/articulos` (sin catálogo completo en
 * memoria) y deja afuera lo que no puede ser componente. Búsqueda generacional: una respuesta tardía de una búsqueda
 * anterior, o de una que se superó al elegir, no pisa los resultados (`react-async-state` regla 2).
 */
export function SelectorDeArticuloDeCombo({ numero, idArticulo, nombre, disabled, onSeleccionar }: Props) {
  const [termino, setTermino] = useState('')
  const [resultados, setResultados] = useState<ArticuloListado[]>([])
  const [ocultos, setOcultos] = useState(0)
  const [buscado, setBuscado] = useState(false)
  const [buscando, setBuscando] = useState(false)
  const [error, setError] = useState('')
  const generacionRef = useRef(0)
  const idDelCampo = `cb-buscar-articulo-${numero}`

  async function buscar() {
    if (disabled) return
    const generacion = (generacionRef.current += 1)
    setBuscando(true)
    setError('')
    try {
      const pagina = await clienteDeArticulos.listar(termino, false)
      if (generacionRef.current !== generacion) return
      const elegibles = pagina.items.filter(esArticuloElegibleParaCombo)
      setResultados(elegibles)
      setOcultos(pagina.items.length - elegibles.length)
      setBuscado(true)
    } catch (e) {
      if (generacionRef.current === generacion) {
        setError(e instanceof ErrorApi ? e.message : 'No se pudieron buscar artículos.')
      }
    } finally {
      if (generacionRef.current === generacion) setBuscando(false)
    }
  }

  function elegir(articulo: ArticuloListado) {
    generacionRef.current += 1
    setBuscando(false)
    setResultados([])
    setOcultos(0)
    setBuscado(false)
    setTermino('')
    onSeleccionar(articulo.id, articulo.nombre)
  }

  return (
    <div>
      {idArticulo !== '' && (
        <div className="mb-1 small">
          Elegido: <strong>{nombre || `Artículo #${idArticulo}`}</strong>
        </div>
      )}
      <div className="input-group input-group-sm">
        <input
          id={idDelCampo}
          type="search"
          className="form-control"
          aria-label={`Buscar artículo del componente ${numero}`}
          placeholder="Buscar por nombre o código interno…"
          value={termino}
          disabled={disabled}
          onChange={(e) => setTermino(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && (e.preventDefault(), void buscar())}
        />
        <button
          type="button"
          className="btn btn-outline-primary"
          aria-label={`Buscar artículos del componente ${numero}`}
          disabled={disabled || buscando}
          onClick={() => void buscar()}
        >
          {buscando ? 'Buscando…' : 'Buscar'}
        </button>
      </div>
      {error && <div className="alert alert-danger py-1 px-2 small mt-1 mb-0">{error}</div>}
      {buscado && resultados.length === 0 && <div className="form-text">No hay artículos por unidad que coincidan.</div>}
      {resultados.length > 0 && (
        <select
          className="form-select form-select-sm mt-1"
          aria-label={`Resultados del componente ${numero}`}
          size={Math.min(resultados.length + 1, 6)}
          disabled={disabled}
          value=""
          onChange={(e) => {
            const encontrado = resultados.find((a) => a.id === Number(e.target.value))
            if (encontrado) elegir(encontrado)
          }}
        >
          <option value="" disabled>
            Elegí un resultado…
          </option>
          {resultados.map((a) => (
            <option key={a.id} value={a.id}>
              {a.codigoInterno} — {a.nombre}
            </option>
          ))}
        </select>
      )}
      {ocultos > 0 && (
        <div className="form-text">
          {ocultos === 1 ? 'Se ocultó 1 resultado' : `Se ocultaron ${ocultos} resultados`}: solo los artículos activos que se venden
          por unidad pueden ser componentes.
        </div>
      )}
    </div>
  )
}
