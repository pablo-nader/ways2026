import { useRef, useState } from 'react'
import { clienteDeCatalogo } from '../../api/catalogos'
import { ErrorApi } from '../../api/cliente'
import type { CategoriaAlta, CategoriaListado } from '../../api/tipos'
import { Modal } from '../../componentes/Modal'

const clienteCategorias = clienteDeCatalogo<CategoriaListado, CategoriaAlta>('categorias')

type Props = {
  /** Listado ya cargado por `Articulos.tsx` — se ofrece tal cual como opciones de padre, sin
   * reconstruir el árbol de `Categorias.tsx`: una categoría nueva no tiene hijos todavía, así que
   * no hay subárbol propio que excluir. */
  categorias: CategoriaListado[]
  onCreado: (categoria: CategoriaListado) => void
  onCancelar: () => void
}

/**
 * Alta rápida de categoría desde el formulario de artículo — `nombre` + categoría padre opcional.
 * `orden`/`activo` salen con los mismos defaults que `descriptorCategorias.aAlta` (orden 1,
 * activa); el servidor sigue validando profundidad máxima y ciclos como con cualquier alta.
 */
export function AltaRapidaCategoria({ categorias, onCreado, onCancelar }: Props) {
  const [nombre, setNombre] = useState('')
  const [idCategoriaPadre, setIdCategoriaPadre] = useState<number | ''>('')
  const [guardando, setGuardando] = useState(false)
  const [error, setError] = useState('')
  const bloqueadoRef = useRef(false)

  async function guardar(evento: React.FormEvent<HTMLFormElement>) {
    evento.preventDefault()
    evento.stopPropagation()
    if (bloqueadoRef.current) return
    bloqueadoRef.current = true
    setGuardando(true)
    setError('')
    try {
      const creada = await clienteCategorias.crear({
        nombre: nombre.trim(),
        idEmpresa: null,
        orden: 1,
        idCategoriaPadre: idCategoriaPadre === '' ? null : idCategoriaPadre,
        activo: true,
      })
      onCreado(creada)
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo crear la categoría.')
    } finally {
      bloqueadoRef.current = false
      setGuardando(false)
    }
  }

  return (
    <Modal titulo="Nueva categoría" ocupado={guardando} onCerrar={onCancelar}>
      <form onSubmit={guardar}>
        {error && <div className="alert alert-danger rounded-0 py-1 px-2 small">{error}</div>}
        <div className="mb-3">
          <label className="form-label" htmlFor="alta-rapida-categoria-nombre">
            Nombre
          </label>
          <input
            id="alta-rapida-categoria-nombre"
            className="form-control rounded-0"
            maxLength={150}
            value={nombre}
            disabled={guardando}
            onChange={(e) => setNombre(e.target.value)}
            autoFocus
            required
          />
        </div>
        <div className="mb-3">
          <label className="form-label" htmlFor="alta-rapida-categoria-padre">
            Categoría padre
          </label>
          <select
            id="alta-rapida-categoria-padre"
            className="form-select rounded-0"
            value={idCategoriaPadre}
            disabled={guardando}
            onChange={(e) => setIdCategoriaPadre(e.target.value === '' ? '' : Number(e.target.value))}
          >
            <option value="">— Ninguna (raíz) —</option>
            {categorias.map((c) => (
              <option key={c.id} value={c.id}>
                {c.nombre}
              </option>
            ))}
          </select>
        </div>
        <div className="d-flex gap-2">
          <button type="submit" className="btn btn-success rounded-0" disabled={guardando}>
            {guardando ? 'Creando…' : 'Crear'}
          </button>
          <button
            type="button"
            className="btn btn-outline-secondary rounded-0"
            onClick={onCancelar}
            disabled={guardando}
          >
            Cancelar
          </button>
        </div>
      </form>
    </Modal>
  )
}
