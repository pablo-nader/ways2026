import { useEffect, useRef, useState } from 'react'
import { clienteDeArticulos } from '../../api/articulos'
import { ErrorApi } from '../../api/cliente'
import type { CodigoBarraListado } from '../../api/tipos'
import { Cargando } from '../../componentes/Cargando'
import { advertenciaDeCodigoBarra } from './validacionCodigoBarra'

/**
 * Códigos de barra: alta/baja independientes de editar el resto del artículo (spec: Barcode
 * Add/Remove Management). Hidrata desde `GET /api/articulos/{id}/codigos-barra` al montar, así
 * que también refleja los códigos cargados en altas anteriores, no solo los de esta sesión.
 *
 * Un código que no parece un GTIN estándar no se envía de inmediato: se advierte y recién se agrega
 * con la confirmación explícita "Agregar igual". Es solo una advertencia; el servidor no cambia.
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
  // Código ya recortado que quedó a la espera de confirmación, con el motivo de la advertencia.
  const [pendiente, setPendiente] = useState<{ codigo: string; motivo: string } | null>(null)
  // Espejo sincrónico de `ocupado`: dos clics en el mismo tick pasan ambos la guarda de estado.
  const ocupadoRef = useRef(false)

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

  function intentarAgregar() {
    if (ocupadoRef.current || bloqueadoPorPadre) return
    const codigo = nuevoCodigo.trim()
    if (!codigo) return

    const motivo = advertenciaDeCodigoBarra(codigo)
    if (motivo) {
      setPendiente({ codigo, motivo })
      return
    }
    void enviar(codigo)
  }

  async function enviar(codigo: string) {
    if (ocupadoRef.current || bloqueadoPorPadre) return
    ocupadoRef.current = true
    setOcupado(true)
    setError('')
    setPendiente(null)
    alDeEscribir(true)
    try {
      const creado = await clienteDeArticulos.agregarCodigoBarra(idArticulo, { codigo })
      setCodigos((prev) => [...prev, creado])
      setNuevoCodigo('')
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo agregar el código de barras.')
    } finally {
      ocupadoRef.current = false
      setOcupado(false)
      alDeEscribir(false)
    }
  }

  async function quitar(codigoBarra: CodigoBarraListado) {
    if (ocupadoRef.current || bloqueadoPorPadre) return
    ocupadoRef.current = true
    setOcupado(true)
    setError('')
    alDeEscribir(true)
    try {
      await clienteDeArticulos.eliminarCodigoBarra(idArticulo, codigoBarra.id)
      setCodigos((prev) => prev.filter((c) => c.id !== codigoBarra.id))
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo quitar el código de barras.')
    } finally {
      ocupadoRef.current = false
      setOcupado(false)
      alDeEscribir(false)
    }
  }

  return (
    <div>
      <strong className="text-muted small text-uppercase">Códigos de barra</strong>
      {error && <div className="alert alert-danger py-1 px-2 small mt-2">{error}</div>}

      {cargando ? (
        <Cargando texto="Cargando códigos de barra…" />
      ) : (
        <div className="d-flex flex-wrap gap-2 mb-2 mt-2">
          {codigos.map((c) => (
            <span key={c.id} className="badge bg-body-secondary text-body border d-flex align-items-center gap-2 py-2 px-2">
              {c.codigo}
              <button
                type="button"
                className="btn btn-sm btn-outline-danger py-0 px-1"
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
          className="form-control"
          maxLength={50}
          placeholder="Código de barras"
          value={nuevoCodigo}
          disabled={ocupado || bloqueadoPorPadre}
          onChange={(e) => {
            setNuevoCodigo(e.target.value)
            setPendiente(null)
          }}
          onKeyDown={(e) => {
            if (e.key !== 'Enter') return
            e.preventDefault()
            // Enter nunca confirma una advertencia: solo lo hace el botón "Agregar igual".
            intentarAgregar()
          }}
        />
        <button
          type="button"
          className="btn btn-outline-primary"
          disabled={ocupado || bloqueadoPorPadre}
          onClick={intentarAgregar}
        >
          Agregar
        </button>
      </div>

      {pendiente && (
        <div className="alert alert-warning py-1 px-2 small mt-2 d-flex flex-wrap align-items-center gap-2" role="alert">
          <span>{pendiente.motivo}</span>
          <button
            type="button"
            className="btn btn-sm btn-warning py-0 px-2"
            disabled={ocupado || bloqueadoPorPadre}
            onClick={() => enviar(pendiente.codigo)}
          >
            Agregar igual
          </button>
        </div>
      )}
    </div>
  )
}
