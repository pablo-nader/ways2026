import { useRef, useState } from 'react'
import { clienteDeCatalogo } from '../../api/catalogos'
import { ErrorApi } from '../../api/cliente'
import type { GrupoAlta, GrupoListado } from '../../api/tipos'
import { Modal } from '../../componentes/Modal'

const clienteGrupos = clienteDeCatalogo<GrupoListado, GrupoAlta>('grupos')

type Props = {
  onCreado: (grupo: GrupoListado) => void
  onCancelar: () => void
}

function margenOVacio(valor: string): number | null {
  const limpio = valor.trim()
  return limpio === '' ? null : Number(limpio)
}

/**
 * Alta rápida de grupo desde el formulario de artículo — `nombre` + `margen` opcional, con los
 * mismos defaults que `descriptorGrupos.aAlta` (`idEmpresa: null`, `activo: true`).
 */
export function AltaRapidaGrupo({ onCreado, onCancelar }: Props) {
  const [nombre, setNombre] = useState('')
  const [margen, setMargen] = useState('')
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
      const creado = await clienteGrupos.crear({
        nombre: nombre.trim(),
        idEmpresa: null,
        activo: true,
        margen: margenOVacio(margen),
      })
      onCreado(creado)
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo crear el grupo.')
    } finally {
      bloqueadoRef.current = false
      setGuardando(false)
    }
  }

  return (
    <Modal titulo="Nuevo grupo" ocupado={guardando} onCerrar={onCancelar}>
      <form onSubmit={guardar}>
        {error && <div className="alert alert-danger rounded-0 py-1 px-2 small">{error}</div>}
        <div className="mb-3">
          <label className="form-label" htmlFor="alta-rapida-grupo-nombre">
            Nombre
          </label>
          <input
            id="alta-rapida-grupo-nombre"
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
          <label className="form-label" htmlFor="alta-rapida-grupo-margen">
            Margen sugerido (%)
          </label>
          <input
            id="alta-rapida-grupo-margen"
            type="number"
            step="0.01"
            min="0"
            className="form-control rounded-0"
            value={margen}
            disabled={guardando}
            onChange={(e) => setMargen(e.target.value)}
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
