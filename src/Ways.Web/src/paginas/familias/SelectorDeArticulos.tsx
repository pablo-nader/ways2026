import { useState } from 'react'
import { clienteDeArticulos } from '../../api/articulos'
import { ErrorApi } from '../../api/cliente'
import type { ArticuloListado } from '../../api/tipos'
import { Modal } from '../../componentes/Modal'

/** Lo que una pantalla de familias necesita de un artículo elegido: cómo nombrarlo y si ya tiene familia. */
export type ArticuloElegido = Pick<ArticuloListado, 'id' | 'codigoInterno' | 'nombre' | 'idFamilia'>

const LARGO_MINIMO_DE_BUSQUEDA = 2

type Props = {
  titulo: string
  /** `false`: se elige uno solo (el artículo de referencia) y elegir otro reemplaza al anterior. */
  multiple: boolean
  /** Lo ya elegido: arranca marcado y se puede desmarcar. */
  elegidos: readonly ArticuloElegido[]
  /** Artículos que se ven pero no se pueden elegir, con el motivo que se les muestra (por ejemplo la referencia, al
   * elegir a los demás). */
  excluidos?: ReadonlyMap<number, string>
  /** La familia a la que se agrega: sus miembros se rotulan "Ya es miembro de esta familia". */
  idFamiliaDeDestino?: number
  onListo: (elegidos: ArticuloElegido[]) => void
  onCerrar: () => void
}

function aElegido(a: ArticuloListado): ArticuloElegido {
  return { id: a.id, codigoInterno: a.codigoInterno, nombre: a.nombre, idFamilia: a.idFamilia }
}

/**
 * Buscador de artículos para armar una familia: por nombre, con selección que se conserva entre búsquedas. Agrupar
 * no mueve a nadie de la familia que ya tiene (doc 10 §3), así que un artículo con familia aparece pero no se puede
 * elegir. No reusa `ModalDeBusquedaDeArticulos`: ese es el buscador del POS, resuelve precios de venta y se cierra
 * al agregar un artículo.
 */
export function SelectorDeArticulos({ titulo, multiple, elegidos, excluidos, idFamiliaDeDestino, onListo, onCerrar }: Props) {
  const [termino, setTermino] = useState('')
  const [buscando, setBuscando] = useState(false)
  const [resultados, setResultados] = useState<ArticuloListado[] | null>(null)
  const [total, setTotal] = useState(0)
  const [error, setError] = useState('')
  const [seleccion, setSeleccion] = useState<ReadonlyMap<number, ArticuloElegido>>(() => new Map(elegidos.map((a) => [a.id, a])))

  const terminoValido = termino.trim().length >= LARGO_MINIMO_DE_BUSQUEDA

  // Una sola búsqueda a la vez: mientras una está en vuelo el botón está deshabilitado y Enter no hace nada, así que
  // la respuesta que llega siempre es la de lo que se pidió último.
  async function buscar() {
    if (!terminoValido || buscando) return

    setBuscando(true)
    setError('')
    try {
      const pagina = await clienteDeArticulos.listar(termino.trim(), false)
      setResultados(pagina.items)
      setTotal(pagina.total)
    } catch (e) {
      setResultados(null)
      setError(e instanceof ErrorApi ? e.message : 'No se pudo buscar artículos.')
    } finally {
      setBuscando(false)
    }
  }

  function alternar(articulo: ArticuloListado) {
    setSeleccion((previa) => {
      const siguiente = new Map<number, ArticuloElegido>(multiple ? previa : [])
      if (previa.has(articulo.id)) siguiente.delete(articulo.id)
      else siguiente.set(articulo.id, aElegido(articulo))
      return siguiente
    })
  }

  function motivoDeBloqueo(articulo: ArticuloListado): string | null {
    const motivoDeExclusion = excluidos?.get(articulo.id)
    if (motivoDeExclusion !== undefined) return motivoDeExclusion
    if (articulo.idFamilia === null) return null
    return articulo.idFamilia === idFamiliaDeDestino ? 'Ya es miembro de esta familia' : 'Ya está en una familia'
  }

  return (
    <Modal
      titulo={titulo}
      tamano="lg"
      desplazable
      onCerrar={onCerrar}
      pie={
        <>
          <span className="me-auto text-body-secondary small">
            {seleccion.size === 1 ? '1 elegido' : `${seleccion.size} elegidos`}
          </span>
          <button type="button" className="btn btn-outline-secondary" onClick={onCerrar}>
            Cancelar
          </button>
          <button type="button" className="btn btn-primary" disabled={seleccion.size === 0} onClick={() => onListo([...seleccion.values()])}>
            Listo
          </button>
        </>
      }
    >
      <form
        className="input-group mb-3"
        onSubmit={(e) => {
          e.preventDefault()
          void buscar()
        }}
      >
        <input
          type="search"
          className="form-control"
          placeholder="Buscar por nombre o código…"
          aria-label="Buscar artículo"
          value={termino}
          onChange={(e) => setTermino(e.target.value)}
        />
        <button type="submit" className="btn btn-primary" disabled={buscando || !terminoValido}>
          {buscando ? 'Buscando…' : 'Buscar'}
        </button>
      </form>
      {!terminoValido && <p className="small text-body-secondary">Escribí al menos {LARGO_MINIMO_DE_BUSQUEDA} caracteres para buscar.</p>}

      {error && <div className="alert alert-danger py-1 px-2 small">{error}</div>}

      {resultados !== null && (
        <div className="table-responsive">
          <table className="table table-sm table-hover table-bordered align-middle">
            <thead>
              <tr>
                <th>
                  <span className="visually-hidden">Elegir</span>
                </th>
                <th>Código</th>
                <th>Nombre</th>
                <th>Estado</th>
              </tr>
            </thead>
            <tbody>
              {resultados.map((a) => {
                const motivo = motivoDeBloqueo(a)
                return (
                  <tr key={a.id}>
                    <td>
                      <input
                        type={multiple ? 'checkbox' : 'radio'}
                        className="form-check-input"
                        name="articulo-elegido"
                        aria-label={`Elegir ${a.codigoInterno} ${a.nombre}`}
                        checked={seleccion.has(a.id)}
                        disabled={motivo !== null}
                        onChange={() => alternar(a)}
                      />
                    </td>
                    <td>{a.codigoInterno}</td>
                    <td>{a.nombre}</td>
                    <td>{motivo ?? (a.activo ? 'Activo' : 'Inactivo')}</td>
                  </tr>
                )
              })}
              {resultados.length === 0 && (
                <tr>
                  <td colSpan={4} className="text-center text-muted py-3">
                    Sin resultados
                  </td>
                </tr>
              )}
            </tbody>
          </table>
          {total > resultados.length && (
            <p className="small text-body-secondary">Se muestran {resultados.length} de {total} artículos: afiná la búsqueda para ver el resto.</p>
          )}
        </div>
      )}
    </Modal>
  )
}
