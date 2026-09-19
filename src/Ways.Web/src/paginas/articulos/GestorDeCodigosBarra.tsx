import { useEffect, useState } from 'react'
import { clienteDeArticulos } from '../../api/articulos'
import { ErrorApi } from '../../api/cliente'
import type { CodigoBarraListado } from '../../api/tipos'
import { Cargando } from '../../componentes/Cargando'

/**
 * Códigos de barra: alta/baja independientes de editar el resto del artículo (spec: Barcode
 * Add/Remove Management). Hidrata desde `GET /api/articulos/{id}/codigos-barra` al montar, así
 * que también refleja los códigos cargados en altas anteriores, no solo los de esta sesión.
 */
export function GestorDeCodigosBarra({
  idArticulo,
  bloqueadoPorPadre,
  alDeEscribir,
}: {
  idArticulo: number
  bloqueadoPorPadre: boolean
  alDeEscribir: (enCurso: boolean) => void
}) {
  const [codigos, setCodigos] = useState<CodigoBarraListado[]>([])
  const [nuevoCodigo, setNuevoCodigo] = useState('')
  const [error, setError] = useState('')
  const [ocupado, setOcupado] = useState(false)
  const [cargando, setCargando] = useState(true)

  useEffect(() => {
    let cancelado = false
    setCargando(true)
    clienteDeArticulos
      .codigosBarra(idArticulo)
      .then((lista) => {
        if (!cancelado) setCodigos(lista)
      })
      .catch((e) => {
        if (!cancelado) setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los códigos de barra.')
      })
      .finally(() => {
        if (!cancelado) setCargando(false)
      })
    return () => {
      cancelado = true
    }
  }, [idArticulo])

  async function agregar() {
    if (ocupado || bloqueadoPorPadre) return
    const codigo = nuevoCodigo.trim()
    if (!codigo) return

    setOcupado(true)
    setError('')
    alDeEscribir(true)
    try {
      const creado = await clienteDeArticulos.agregarCodigoBarra(idArticulo, { codigo })
      setCodigos((prev) => [...prev, creado])
      setNuevoCodigo('')
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo agregar el código de barras.')
    } finally {
      setOcupado(false)
      alDeEscribir(false)
    }
  }

  async function quitar(codigoBarra: CodigoBarraListado) {
    if (ocupado || bloqueadoPorPadre) return
    setOcupado(true)
    setError('')
    alDeEscribir(true)
    try {
      await clienteDeArticulos.eliminarCodigoBarra(idArticulo, codigoBarra.id)
      setCodigos((prev) => prev.filter((c) => c.id !== codigoBarra.id))
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo quitar el código de barras.')
    } finally {
      setOcupado(false)
      alDeEscribir(false)
    }
  }

  return (
    <div>
      <strong className="text-muted small text-uppercase">Códigos de barra</strong>
      {error && <div className="alert alert-danger rounded-0 py-1 px-2 small mt-2">{error}</div>}

      {cargando ? (
        <Cargando texto="Cargando códigos de barra…" />
      ) : (
        <div className="d-flex flex-wrap gap-2 mb-2 mt-2">
          {codigos.map((c) => (
            <span key={c.id} className="badge rounded-0 text-bg-light border d-flex align-items-center gap-2 py-2 px-2">
              {c.codigo}
              <button
                type="button"
                className="btn btn-sm btn-outline-danger rounded-0 py-0 px-1"
                disabled={ocupado || bloqueadoPorPadre}
                onClick={() => quitar(c)}
              >
                ×
              </button>
            </span>
          ))}
          {codigos.length === 0 && <span className="text-muted small">Sin códigos de barra cargados.</span>}
        </div>
      )}

      <div className="input-group" style={{ maxWidth: 320 }}>
        <input
          type="text"
          className="form-control rounded-0"
          maxLength={50}
          placeholder="Código de barras"
          value={nuevoCodigo}
          disabled={ocupado || bloqueadoPorPadre}
          onChange={(e) => setNuevoCodigo(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && (e.preventDefault(), agregar())}
        />
        <button
          type="button"
          className="btn btn-outline-primary rounded-0"
          disabled={ocupado || bloqueadoPorPadre}
          onClick={agregar}
        >
          Agregar
        </button>
      </div>
    </div>
  )
}
