import { useEffect, useRef, useState } from 'react'
import { clienteDeArticulos } from '../api/articulos'
import { ErrorApi } from '../api/cliente'
import { clienteDeOfertas } from '../api/ofertas'
import type { LineaCarrito } from '../api/carrito'
import type { ArticuloListado, LineaDeResolucion, ResultadoDeResolucion } from '../api/tipos'

/** Spec: "minimum 2 characters" — por debajo de este largo ni se debounce ni se dispara Enter. */
const LARGO_MINIMO_DE_BUSQUEDA = 2
const DEMORA_DEBOUNCE_MS = 300
/** Cantidad por defecto de una línea agregada desde el buscador — mismo default que un código de
 * barra escaneado sin multiplicador ("N*codigo"), spec: "same quantity default". */
const CANTIDAD_POR_DEFECTO = 1

function formatearMoneda(valor: number): string {
  const signo = valor < 0 ? '-' : ''
  return `${signo}$${Math.abs(valor).toLocaleString('es-AR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
}

export type PropsModalDeBusquedaDeArticulos = {
  /** Contexto de precio de la venta en curso — mismo par que alimenta la resolución del carrito
   * (`Pos.tsx`): lista de precio del cliente, empresa del punto de venta. `null` mientras
   * cualquiera de los dos todavía no cargó — el buscador queda inerte en vez de resolver contra
   * un contexto incompleto. */
  idListaPrecio: number | null
  idEmpresa: number | null
  onAgregar: (linea: Omit<LineaCarrito, 'cantidad'>, cantidad: number) => void
  onCerrar: () => void
}

/**
 * Modal de búsqueda de artículos por nombre (contains) del POS — F2 la abre, Escape/X la cierran
 * (`Pos.tsx` es quien cablea F2 y devuelve el foco al input de código). "Agregar" de una fila
 * dispara el MISMO camino que un código escaneado (`AccionCarrito` tipo `escanear`, vía
 * `onAgregar`): misma resolución de precio, mismas reglas de ofertas/stock/lote — el precio que
 * se ve acá es solo una vista previa a `cantidad = 1` (`ofertas/resolver`), la línea real la
 * resuelve el efecto de precios de `Pos.tsx` como a cualquier otra.
 */
export function ModalDeBusquedaDeArticulos({ idListaPrecio, idEmpresa, onAgregar, onCerrar }: PropsModalDeBusquedaDeArticulos) {
  const [termino, setTermino] = useState('')
  const [buscando, setBuscando] = useState(false)
  const [resultados, setResultados] = useState<ArticuloListado[] | null>(null)
  const [precios, setPrecios] = useState<Record<number, ResultadoDeResolucion>>({})
  const [error, setError] = useState('')

  // react-async-state regla 2/3: una búsqueda nueva (debounce o Enter) invalida cualquier
  // resolución en vuelo de la anterior — nunca pisa un resultado más nuevo con uno viejo.
  const generacionRef = useRef(0)
  const timeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  // regla 9: reentrancia de primera línea — un doble click en la misma fila (o en dos filas
  // distintas) antes de que el cierre del modal se comite no dispara un segundo agregado.
  const agregadoRef = useRef(false)

  useEffect(() => {
    return () => {
      if (timeoutRef.current) clearTimeout(timeoutRef.current)
    }
  }, [])

  useEffect(() => {
    function alTeclado(evento: KeyboardEvent) {
      if (evento.key !== 'Escape') return
      evento.preventDefault()
      onCerrar()
    }
    document.addEventListener('keydown', alTeclado)
    return () => document.removeEventListener('keydown', alTeclado)
  }, [onCerrar])

  async function buscar(terminoBuscado: string) {
    const propio = terminoBuscado.trim()
    if (propio.length < LARGO_MINIMO_DE_BUSQUEDA) {
      generacionRef.current += 1
      setBuscando(false)
      setResultados(null)
      setPrecios({})
      setError('')
      return
    }

    const generacion = (generacionRef.current += 1)
    setBuscando(true)
    setError('')

    try {
      const pagina = await clienteDeArticulos.listar(propio, false)
      if (generacionRef.current !== generacion) return
      setResultados(pagina.items)

      if (pagina.items.length === 0 || idListaPrecio === null) {
        setPrecios({})
        return
      }

      const lineas: LineaDeResolucion[] = pagina.items.map((a) => ({
        idArticulo: a.id,
        idEmpresa,
        idListaPrecio,
        cantidad: CANTIDAD_POR_DEFECTO,
      }))
      const resueltos = await clienteDeOfertas.resolver(lineas)
      if (generacionRef.current !== generacion) return
      const indice: Record<number, ResultadoDeResolucion> = {}
      for (const r of resueltos) indice[r.idArticulo] = r
      setPrecios(indice)
    } catch (e) {
      if (generacionRef.current !== generacion) return
      setError(e instanceof ErrorApi ? e.message : 'No se pudo buscar artículos.')
      setResultados(null)
      setPrecios({})
    } finally {
      if (generacionRef.current === generacion) setBuscando(false)
    }
  }

  function cambiarTermino(valor: string) {
    setTermino(valor)
    if (timeoutRef.current) clearTimeout(timeoutRef.current)
    timeoutRef.current = setTimeout(() => void buscar(valor), DEMORA_DEBOUNCE_MS)
  }

  function buscarInmediato() {
    if (timeoutRef.current) clearTimeout(timeoutRef.current)
    void buscar(termino)
  }

  function agregar(articulo: ArticuloListado) {
    if (agregadoRef.current) return
    agregadoRef.current = true
    onAgregar(
      { idArticulo: articulo.id, codigoInterno: articulo.codigoInterno, nombre: articulo.nombre, codigoBarra: null },
      CANTIDAD_POR_DEFECTO,
    )
  }

  return (
    <>
      <div className="modal d-block" tabIndex={-1} role="dialog" aria-modal="true" aria-label="Buscar artículo">
        <div className="modal-dialog modal-lg" role="document">
          <div className="modal-content rounded-0">
            <div className="modal-header">
              <h5 className="modal-title">Buscar artículo</h5>
              <button type="button" className="btn-close" aria-label="Cerrar" onClick={onCerrar} />
            </div>
            <div className="modal-body">
              <div className="input-group mb-3">
                <input
                  type="search"
                  className="form-control rounded-0"
                  placeholder="Buscar por nombre…"
                  aria-label="Buscar artículo por nombre"
                  value={termino}
                  onChange={(e) => cambiarTermino(e.target.value)}
                  onKeyDown={(e) => e.key === 'Enter' && (e.preventDefault(), buscarInmediato())}
                  autoFocus
                />
                <button type="button" className="btn btn-primary rounded-0" disabled={buscando} onClick={buscarInmediato}>
                  {buscando ? 'Buscando…' : 'Buscar'}
                </button>
              </div>

              {error && <div className="alert alert-danger rounded-0 py-1 px-2 small">{error}</div>}

              <div className="table-responsive">
                <table className="table table-striped table-hover table-bordered align-middle">
                  <thead>
                    <tr>
                      <th>Código</th>
                      <th>Nombre</th>
                      <th className="text-end">Precio</th>
                      <th className="text-end">Acciones</th>
                    </tr>
                  </thead>
                  <tbody>
                    {buscando && (
                      <tr>
                        <td colSpan={4} className="text-center text-muted py-3">
                          Buscando…
                        </td>
                      </tr>
                    )}
                    {!buscando && resultados !== null && resultados.length === 0 && (
                      <tr>
                        <td colSpan={4} className="text-center text-muted py-3">
                          Sin resultados
                        </td>
                      </tr>
                    )}
                    {!buscando &&
                      resultados?.map((a) => {
                        const resultado = precios[a.id]
                        const precioTexto = resultado?.precioFinal != null ? formatearMoneda(resultado.precioFinal) : '—'
                        return (
                          <tr key={a.id}>
                            <td>{a.codigoInterno}</td>
                            <td>{a.nombre}</td>
                            <td className="text-end">{precioTexto}</td>
                            <td className="text-end">
                              <button type="button" className="btn btn-sm btn-primary rounded-0" onClick={() => agregar(a)}>
                                Agregar
                              </button>
                            </td>
                          </tr>
                        )
                      })}
                  </tbody>
                </table>
              </div>
            </div>
          </div>
        </div>
      </div>
      <div className="modal-backdrop show" />
    </>
  )
}
