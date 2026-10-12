import { useEffect, useId, useRef, useState } from 'react'
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
  /** Mensaje de error de la fila (si lo hay): el campo lo referencia con `aria-describedby`. */
  idDelError?: string
  invalido?: boolean
  onSeleccionar: (id: number, nombre: string) => void
}

type Truncado = { recibidos: number; total: number }

/**
 * Buscador de artículo de una fila del editor de componentes. Busca contra `/api/articulos` (sin catálogo completo en
 * memoria) y deja afuera lo que no puede ser componente. El listado del servidor no filtra por unidad de venta, así que
 * el filtro es del cliente y solo ve la página que llegó: si el servidor dice que hay más resultados, lo avisa en vez de
 * afirmar que no hay ninguno. Búsqueda generacional: una respuesta tardía de una búsqueda anterior, o de una que se
 * superó al elegir, no pisa los resultados (`react-async-state` regla 2).
 *
 * Los resultados son un combobox con listbox: las flechas mueven la opción resaltada sin elegir; Enter o un clic eligen;
 * Escape o salir del campo cierran la lista sin elegir.
 */
export function SelectorDeArticuloDeCombo({ numero, idArticulo, nombre, disabled, idDelError, invalido, onSeleccionar }: Props) {
  const [termino, setTermino] = useState('')
  const [resultados, setResultados] = useState<ArticuloListado[]>([])
  const [ocultos, setOcultos] = useState(0)
  const [truncado, setTruncado] = useState<Truncado | null>(null)
  const [buscado, setBuscado] = useState(false)
  const [buscando, setBuscando] = useState(false)
  const [error, setError] = useState('')
  const [abierta, setAbierta] = useState(false)
  const [indiceActivo, setIndiceActivo] = useState(0)
  const generacionRef = useRef(0)
  const campoRef = useRef<HTMLInputElement>(null)
  const botonRef = useRef<HTMLButtonElement>(null)
  const idDeLista = useId()
  const idDelCampo = `cb-buscar-articulo-${numero}`
  const activo = resultados.length === 0 ? -1 : Math.min(indiceActivo, resultados.length - 1)
  const idDeOpcion = (id: number) => `${idDeLista}-${id}`

  useEffect(() => {
    if (!abierta || activo < 0) return
    document.getElementById(idDeOpcion(resultados[activo].id))?.scrollIntoView?.({ block: 'nearest' })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [abierta, activo])

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
      setTruncado(pagina.total > pagina.items.length ? { recibidos: pagina.items.length, total: pagina.total } : null)
      setBuscado(true)
      setIndiceActivo(0)
      setAbierta(elegibles.length > 0)
      // Si la búsqueda salió del botón, el foco pasa al campo para que las flechas funcionen sobre la lista.
      const enfocado = document.activeElement
      if (elegibles.length > 0 && (enfocado === botonRef.current || enfocado === document.body)) campoRef.current?.focus()
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
    setTruncado(null)
    setBuscado(false)
    setAbierta(false)
    setTermino('')
    onSeleccionar(articulo.id, articulo.nombre)
  }

  function alTeclear(evento: React.KeyboardEvent<HTMLInputElement>) {
    if (evento.key === 'ArrowDown' || evento.key === 'ArrowUp') {
      if (resultados.length === 0) return
      evento.preventDefault()
      if (!abierta) {
        setAbierta(true)
        return
      }
      const paso = evento.key === 'ArrowDown' ? 1 : -1
      setIndiceActivo((actual) => (Math.min(actual, resultados.length - 1) + paso + resultados.length) % resultados.length)
      return
    }
    if (evento.key === 'Enter') {
      evento.preventDefault()
      if (abierta && activo >= 0) elegir(resultados[activo])
      else void buscar()
      return
    }
    if (evento.key === 'Escape' && abierta) {
      // Cierra solo la lista: el Escape no llega al modal.
      evento.preventDefault()
      evento.stopPropagation()
      setAbierta(false)
    }
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
          ref={campoRef}
          id={idDelCampo}
          type="search"
          role="combobox"
          className={`form-control ${invalido ? 'is-invalid' : ''}`}
          aria-label={`Buscar artículo del componente ${numero}`}
          aria-invalid={invalido ? true : undefined}
          aria-describedby={idDelError}
          aria-expanded={abierta}
          aria-controls={idDeLista}
          aria-autocomplete="list"
          aria-activedescendant={abierta && activo >= 0 ? idDeOpcion(resultados[activo].id) : undefined}
          placeholder="Buscar por nombre o código interno…"
          value={termino}
          disabled={disabled}
          onChange={(e) => {
            // Invalida la búsqueda en vuelo: su respuesta es de un término que ya no está en el campo.
            generacionRef.current += 1
            setBuscando(false)
            setTermino(e.target.value)
            setAbierta(false)
          }}
          onKeyDown={alTeclear}
          onBlur={() => setAbierta(false)}
        />
        <button
          ref={botonRef}
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
      {buscado && resultados.length === 0 && truncado === null && (
        <div className="form-text">No hay artículos por unidad que coincidan.</div>
      )}
      {buscado && resultados.length === 0 && truncado !== null && (
        <div className="form-text">Ninguno de los resultados mostrados puede ser componente.</div>
      )}
      <div
        id={idDeLista}
        role="listbox"
        aria-label={`Resultados del componente ${numero}`}
        className="list-group list-group-flush border rounded mt-1"
        style={{ maxHeight: '11rem', overflowY: 'auto' }}
        hidden={!abierta || resultados.length === 0}
        // Ni las opciones, ni la barra de desplazamiento, ni el hueco entre ellas le sacan el foco al campo: si no, el blur
        // cerraría la lista a mitad de un desplazamiento o antes de elegir.
        onMouseDown={(e) => e.preventDefault()}
      >
        {abierta &&
          resultados.map((a, i) => (
            <div
              key={a.id}
              id={idDeOpcion(a.id)}
              role="option"
              aria-selected={i === activo}
              className={`list-group-item list-group-item-action py-1 px-2 small${i === activo ? ' active' : ''}`}
              style={{ cursor: 'pointer' }}
              onMouseEnter={() => setIndiceActivo(i)}
              onClick={() => elegir(a)}
            >
              {a.codigoInterno} — {a.nombre}
            </div>
          ))}
      </div>
      {truncado !== null && (
        <div className="form-text">
          Se muestran los primeros {truncado.recibidos} de {truncado.total} resultados. Refiná la búsqueda.
        </div>
      )}
      {ocultos > 0 && (
        <div className="form-text">
          {ocultos === 1 ? 'Se ocultó 1 resultado' : `Se ocultaron ${ocultos} resultados`} de los mostrados: solo los artículos
          activos que se venden por unidad pueden ser componentes.
        </div>
      )}
    </div>
  )
}
