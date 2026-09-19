import { useRef, useState } from 'react'
import { clienteDeCatalogo } from '../../api/catalogos'
import { ErrorApi } from '../../api/cliente'
import type { MarcaAlta, MarcaListado } from '../../api/tipos'
import { Modal } from '../../componentes/Modal'

const clienteMarcas = clienteDeCatalogo<MarcaListado, MarcaAlta>('marcas')

type Props = {
  onCreado: (marca: MarcaListado) => void
  onCancelar: () => void
}

/**
 * Alta rápida de marca desde el formulario de artículo — solo `nombre`, con los mismos defaults
 * que `descriptorMarcas.aAlta` usa en `PaginaCatalogo` (`idEmpresa: null`, `activo: true`).
 */
export function AltaRapidaMarca({ onCreado, onCancelar }: Props) {
  const [nombre, setNombre] = useState('')
  const [guardando, setGuardando] = useState(false)
  const [error, setError] = useState('')
  // react-async-state regla 11: guarda de reentrancia sincrónica — dos submits en el mismo tick
  // (Enter + click, o doble click) no deben disparar dos POST.
  const bloqueadoRef = useRef(false)

  async function guardar(evento: React.FormEvent<HTMLFormElement>) {
    evento.preventDefault()
    // El form de este modal está anidado (vía createPortal) en el árbol de React del form del
    // artículo — sin esto, el submit burbujea por el árbol de componentes y dispara el guardado
    // del artículo también.
    evento.stopPropagation()
    if (bloqueadoRef.current) return
    bloqueadoRef.current = true
    setGuardando(true)
    setError('')
    try {
      const creada = await clienteMarcas.crear({ nombre: nombre.trim(), idEmpresa: null, activo: true })
      onCreado(creada)
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo crear la marca.')
    } finally {
      bloqueadoRef.current = false
      setGuardando(false)
    }
  }

  return (
    <Modal titulo="Nueva marca" ocupado={guardando} onCerrar={onCancelar}>
      <form onSubmit={guardar}>
        {error && <div className="alert alert-danger rounded-0 py-1 px-2 small">{error}</div>}
        <div className="mb-3">
          <label className="form-label" htmlFor="alta-rapida-marca-nombre">
            Nombre
          </label>
          <input
            id="alta-rapida-marca-nombre"
            className="form-control rounded-0"
            maxLength={150}
            value={nombre}
            disabled={guardando}
            onChange={(e) => setNombre(e.target.value)}
            autoFocus
            required
          />
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
