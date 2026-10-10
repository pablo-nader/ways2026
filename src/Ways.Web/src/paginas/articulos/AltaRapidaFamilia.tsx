import { useRef, useState } from 'react'
import type { FamiliaListado } from '../../api/tipos'
import { Modal } from '../../componentes/Modal'
import { validarNombreDeFamilia } from './familia'

type Props = {
  /** Todas las familias vivas, también las inactivas y las vacías: no se ofrecen para elegir pero conservan su nombre. */
  familias: FamiliaListado[]
  /** El nombre con el que se abre, cuando se cambia el de una familia nueva ya anotada. */
  nombreInicial?: string
  /** Hace lo que pide el alta con el nombre ya recortado. Resuelve el motivo del rechazo, o `''` si salió bien. */
  onConfirmar: (nombre: string) => Promise<string>
  onCerrar: () => void
}

/**
 * Alta rápida de familia desde el formulario de artículo: pide solo el nombre. Valida lo mismo que el servidor (obligatorio, hasta
 * 150 caracteres, distinto de las familias que ya existen), pero el servidor sigue siendo la autoridad. Qué se hace con el nombre
 * lo decide quien la abre: en un alta lo anota para crear la familia al guardar; en un artículo existente la crea en el momento.
 */
export function AltaRapidaFamilia({ familias, nombreInicial = '', onConfirmar, onCerrar }: Props) {
  const [nombre, setNombre] = useState(nombreInicial)
  const [guardando, setGuardando] = useState(false)
  const [error, setError] = useState('')
  const bloqueadoRef = useRef(false)

  async function guardar(evento: React.FormEvent<HTMLFormElement>) {
    evento.preventDefault()
    // Este mini-formulario se renderiza dentro del árbol del formulario del artículo: sin esto su submit burbujearía hasta él.
    evento.stopPropagation()
    if (bloqueadoRef.current) return

    const motivo = validarNombreDeFamilia(nombre, familias)
    if (motivo !== '') {
      setError(motivo)
      return
    }

    bloqueadoRef.current = true
    setGuardando(true)
    setError('')
    try {
      const rechazo = await onConfirmar(nombre.trim())
      if (rechazo === '') onCerrar()
      else setError(rechazo)
    } finally {
      bloqueadoRef.current = false
      setGuardando(false)
    }
  }

  return (
    <Modal titulo="Nueva familia" ocupado={guardando} onCerrar={onCerrar}>
      <form onSubmit={guardar}>
        {error && (
          <div className="alert alert-danger py-1 px-2 small" role="alert">
            {error}
          </div>
        )}
        <div className="mb-3">
          <label className="form-label" htmlFor="alta-rapida-familia-nombre">
            Nombre
          </label>
          <input
            id="alta-rapida-familia-nombre"
            className="form-control"
            value={nombre}
            disabled={guardando}
            onChange={(e) => setNombre(e.target.value)}
            autoFocus
          />
        </div>
        <div className="d-flex gap-2">
          <button type="submit" className="btn btn-success" disabled={guardando}>
            {guardando ? 'Aceptando…' : 'Aceptar'}
          </button>
          <button type="button" className="btn btn-outline-secondary" onClick={onCerrar} disabled={guardando}>
            Cancelar
          </button>
        </div>
      </form>
    </Modal>
  )
}
