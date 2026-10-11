import { useEffect, useId, useRef, useState } from 'react'
import type { ChangeEvent, CSSProperties, KeyboardEvent } from 'react'
import { clienteDeArticulos } from '../../api/articulos'
import type { ArticuloListado } from '../../api/tipos'

const MINIMO_DE_CARACTERES = 2
const ESPERA_DE_TIPEO_MS = 300

type Props = {
  /** Nombre accesible de la caja; por defecto "Detalle". La grilla lo hace propio de cada fila. */
  etiqueta?: string
  etiquetaDeCrear?: string
  /** Texto del Detalle de la fila: lo tipeado o el nombre del artículo ya elegido. */
  texto: string
  /** `true` mientras la fila tiene un artículo: no se ofrece crear otro desde ahí. */
  conArticulo: boolean
  /** El artículo se resolvió por código: el Detalle se muestra pero no se edita. */
  soloLectura: boolean
  disabled: boolean
  /** Con proveedor, la búsqueda prioriza los artículos cuyo código de ese proveedor coincide. */
  idProveedor: number | null
  /** Sin permiso para crear artículos no se ofrece el "+" ni el atajo. */
  puedeCrear?: boolean
  invalido?: boolean
  onTexto: (texto: string) => void
  onElegir: (articulo: ArticuloListado) => void
  onCrear: () => void
}

function etiquetaDeOpcion(a: ArticuloListado): string {
  return `${a.codigoInterno} — ${a.nombre}${a.codigoProveedor ? ` (Cód. prov.: ${a.codigoProveedor})` : ''}`
}

/**
 * Detalle de una fila de la grilla de compras: caja de texto que busca artículos mientras se
 * tipea (mínimo 2 caracteres, con espera de tipeo y respuesta tardía descartada) y ofrece
 * resultados en un listbox navegable por teclado. Flechas mueven la opción activa; Enter o Tab
 * sobre una opción activa la eligen (Tab sigue de largo a la celda siguiente); Escape cierra la
 * lista conservando el texto; Enter sin lista abierta no se maneja acá, para que la grilla pase a
 * la fila siguiente. Un texto que se deja sin elegir nada es la descripción de un concepto.
 * Ctrl+Enter y el botón "+" piden crear el artículo.
 */
export function ComboboxDeArticulos({
  etiqueta = 'Detalle',
  etiquetaDeCrear = 'Crear artículo',
  texto,
  conArticulo,
  soloLectura,
  disabled,
  idProveedor,
  puedeCrear = true,
  invalido = false,
  onTexto,
  onElegir,
  onCrear,
}: Props) {
  const idLista = useId()
  const entradaRef = useRef<HTMLInputElement>(null)
  // Término que escribió el operador: nunca el texto que la fila recibe al elegir un artículo,
  // para que elegir no dispare otra búsqueda.
  const [termino, setTermino] = useState('')
  const [resultados, setResultados] = useState<ArticuloListado[]>([])
  const [terminoBuscado, setTerminoBuscado] = useState<string | null>(null)
  const [buscando, setBuscando] = useState(false)
  const [abierta, setAbierta] = useState(false)
  const [activa, setActiva] = useState(-1)
  const [posicion, setPosicion] = useState<CSSProperties | null>(null)
  // Cada búsqueda toma una generación; elegir, vaciar o desmontar la invalida: una respuesta
  // tardía nunca reabre ni rellena una lista que el operador ya cerró.
  const generacionRef = useRef(0)

  useEffect(
    () => () => {
      generacionRef.current += 1
    },
    [],
  )

  useEffect(() => {
    const limpio = termino.trim()
    if (limpio.length < MINIMO_DE_CARACTERES) {
      generacionRef.current += 1
      setResultados([])
      setTerminoBuscado(null)
      setBuscando(false)
      setAbierta(false)
      return
    }

    const miGeneracion = (generacionRef.current += 1)
    setBuscando(true)

    const temporizador = setTimeout(() => {
      clienteDeArticulos
        .listar(limpio, false, idProveedor ?? undefined)
        .then((pagina) => {
          if (generacionRef.current !== miGeneracion) return
          setResultados(pagina.items)
          setTerminoBuscado(limpio)
          setActiva(-1)
          setAbierta(document.activeElement === entradaRef.current)
        })
        .catch(() => {
          if (generacionRef.current !== miGeneracion) return
          setResultados([])
          setTerminoBuscado(null)
          setAbierta(false)
        })
        .finally(() => {
          if (generacionRef.current !== miGeneracion) return
          setBuscando(false)
        })
    }, ESPERA_DE_TIPEO_MS)

    return () => clearTimeout(temporizador)
  }, [termino, idProveedor])

  const visible = abierta && resultados.length > 0

  // La lista se ancla a la ventana: dentro de un contenedor con scroll horizontal (la tabla) la
  // de la última fila quedaría recortada. Se recalcula al abrir y se cierra si la página se mueve.
  useEffect(() => {
    if (!visible || entradaRef.current === null) {
      setPosicion(null)
      return
    }
    const caja = entradaRef.current.getBoundingClientRect()
    setPosicion({ position: 'fixed', top: caja.bottom, left: caja.left, minWidth: Math.max(caja.width, 320), zIndex: 1080 })
    const cerrar = () => setAbierta(false)
    window.addEventListener('scroll', cerrar, true)
    window.addEventListener('resize', cerrar)
    return () => {
      window.removeEventListener('scroll', cerrar, true)
      window.removeEventListener('resize', cerrar)
    }
  }, [visible, resultados])

  function cambiar(evento: ChangeEvent<HTMLInputElement>) {
    setTermino(evento.target.value)
    onTexto(evento.target.value)
  }

  function elegir(articulo: ArticuloListado) {
    generacionRef.current += 1
    setTermino('')
    setResultados([])
    setTerminoBuscado(null)
    setBuscando(false)
    setAbierta(false)
    setActiva(-1)
    onElegir(articulo)
  }

  function alTeclear(evento: KeyboardEvent<HTMLInputElement>) {
    if (evento.key === 'Enter' && evento.ctrlKey && !conArticulo && puedeCrear) {
      evento.preventDefault()
      onCrear()
      return
    }
    if (evento.key === 'ArrowDown' || evento.key === 'ArrowUp') {
      if (resultados.length === 0) return
      evento.preventDefault()
      if (!abierta) {
        setAbierta(true)
        setActiva(evento.key === 'ArrowDown' ? 0 : resultados.length - 1)
        return
      }
      const paso = evento.key === 'ArrowDown' ? 1 : -1
      setActiva((actual) => (actual < 0 ? (paso > 0 ? 0 : resultados.length - 1) : (actual + paso + resultados.length) % resultados.length))
      return
    }
    if (evento.key === 'Enter' && visible) {
      evento.preventDefault()
      if (activa >= 0) elegir(resultados[activa])
      else setAbierta(false)
      return
    }
    if (evento.key === 'Tab' && visible) {
      if (activa >= 0) elegir(resultados[activa])
      else setAbierta(false)
      return
    }
    if (evento.key === 'Escape' && abierta) {
      evento.preventDefault()
      evento.stopPropagation()
      setAbierta(false)
    }
  }

  const anuncio = buscando
    ? ''
    : terminoBuscado === null
      ? ''
      : resultados.length === 0
        ? 'Sin resultados.'
        : `${resultados.length} ${resultados.length === 1 ? 'resultado' : 'resultados'}.`
  const sinResultados = !buscando && terminoBuscado !== null && resultados.length === 0

  return (
    <div className="position-relative">
      <div className="input-group input-group-sm flex-nowrap">
        <input
          ref={entradaRef}
          type="text"
          role="combobox"
          className={`form-control form-control-sm${soloLectura ? ' bg-body-secondary text-muted' : ''}${invalido ? ' is-invalid' : ''}`}
          aria-label={etiqueta}
          aria-autocomplete="list"
          aria-expanded={visible}
          aria-controls={idLista}
          aria-activedescendant={visible && activa >= 0 ? `${idLista}-${activa}` : undefined}
          aria-invalid={invalido || undefined}
          data-celda="detalle"
          placeholder={soloLectura ? undefined : 'Buscar artículo o describir concepto…'}
          title={soloLectura ? 'Artículo resuelto por el código del proveedor: editá el código para cambiarlo.' : undefined}
          autoComplete="off"
          value={texto}
          readOnly={soloLectura}
          disabled={disabled}
          onChange={cambiar}
          onKeyDown={alTeclear}
          onBlur={() => setAbierta(false)}
        />
        {!conArticulo && puedeCrear && (
          <button
            type="button"
            className="btn btn-outline-secondary btn-sm"
            tabIndex={-1}
            aria-label={etiquetaDeCrear}
            title="Crear artículo (Ctrl+Enter)"
            aria-keyshortcuts="Control+Enter"
            disabled={disabled}
            onClick={onCrear}
          >
            +
          </button>
        )}
      </div>
      <ul
        id={idLista}
        role="listbox"
        aria-label="Artículos encontrados"
        className="list-group shadow"
        style={visible && posicion !== null ? posicion : { display: 'none' }}
      >
        {visible &&
          resultados.map((a, indice) => (
            <li
              key={a.id}
              id={`${idLista}-${indice}`}
              role="option"
              aria-selected={indice === activa}
              className={`list-group-item list-group-item-action py-1 px-2 small${indice === activa ? ' active' : ''}`}
              style={{ cursor: 'pointer' }}
              onMouseMove={() => {
                if (activa !== indice) setActiva(indice)
              }}
              onMouseDown={(evento) => {
                evento.preventDefault()
                elegir(a)
              }}
            >
              {etiquetaDeOpcion(a)}
            </li>
          ))}
      </ul>
      <div role="status" aria-live="polite" className="visually-hidden" data-anuncio="resultados">
        {anuncio}
      </div>
      {buscando && <div className="small text-muted">Buscando…</div>}
      {sinResultados && !conArticulo && <div className="small text-muted">Sin resultados: se carga como concepto.</div>}
    </div>
  )
}
