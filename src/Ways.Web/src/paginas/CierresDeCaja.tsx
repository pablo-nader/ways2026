import { useEffect, useRef, useState } from 'react'
import { clienteDeCaja } from '../api/caja'
import { ErrorApi } from '../api/cliente'
import type { PaginaDeTurnos, ResumenDeCierrePorRetiro } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { usePuntoVenta } from '../puntoVenta/usePuntoVenta'

const TAMANIO_DE_PAGINA = 10

function formatearFechaHora(iso: string): string {
  return new Date(iso).toLocaleString('es-AR')
}

type Props = {
  /** Encola el ticket de cierre en la cola de impresión del shell (la misma que usa el cierre). */
  alReimprimir: (resumen: ResumenDeCierrePorRetiro) => void
}

/**
 * "Cierres de caja" del POS: los turnos cerrados del punto de venta del dispositivo, el más
 * reciente primero, para reimprimir el ticket de cierre cuando la impresora falló al cerrar. El
 * punto de venta y el estado se piden al servidor (`GET /api/caja/turnos?idPuntoVenta=&estado=Cerrado`),
 * nunca se filtran acá.
 *
 * Cada lectura de la lista lleva su generación (`react-async-state` regla 2); la reimpresión es
 * una por vez (`reimprimiendoRef`, regla 11) y mientras corre quedan inertes todos los botones.
 */
export function CierresDeCaja({ alReimprimir }: Props) {
  const { puntoVenta } = usePuntoVenta()
  const idPuntoVenta = puntoVenta?.id ?? null

  const [pagina, setPagina] = useState(1)
  const [datos, setDatos] = useState<PaginaDeTurnos | null>(null)
  const [cargando, setCargando] = useState(false)
  const [error, setError] = useState('')
  const generacionRef = useRef(0)

  const [idReimprimiendo, setIdReimprimiendo] = useState<number | null>(null)
  const reimprimiendoRef = useRef(false)
  const [errorReimprimir, setErrorReimprimir] = useState('')

  useEffect(() => {
    if (idPuntoVenta === null) return
    const miGeneracion = (generacionRef.current += 1)
    setCargando(true)
    setError('')
    setErrorReimprimir('')

    clienteDeCaja
      .listarCerrados(idPuntoVenta, pagina, TAMANIO_DE_PAGINA)
      .then((respuesta) => {
        if (generacionRef.current !== miGeneracion) return
        setDatos(respuesta)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setDatos(null)
        setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los cierres de caja.')
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargando(false)
      })
  }, [idPuntoVenta, pagina])

  async function reimprimir(idTurno: number) {
    if (reimprimiendoRef.current) return
    reimprimiendoRef.current = true
    setIdReimprimiendo(idTurno)
    setErrorReimprimir('')
    try {
      alReimprimir(await clienteDeCaja.obtenerResumenDeCierre(idTurno))
    } catch (e) {
      setErrorReimprimir(e instanceof ErrorApi ? e.message : 'No se pudo obtener el ticket de cierre.')
    } finally {
      reimprimiendoRef.current = false
      setIdReimprimiendo(null)
    }
  }

  if (idPuntoVenta === null) {
    return (
      <div className="container-fluid py-4">
        <Box titulo="Cierres de caja" variante="warning">
          <p className="text-muted mb-0">No hay un punto de venta seleccionado.</p>
        </Box>
      </div>
    )
  }

  const totalPaginas = datos ? Math.max(1, Math.ceil(datos.total / datos.tamanio)) : 1
  const reimprimiendo = idReimprimiendo !== null

  return (
    <div className="container-fluid py-4">
      <Box titulo={`Cierres de caja — ${puntoVenta?.nombre ?? ''}`} variante="inverse">
        {error && <div className="alert alert-danger">{error}</div>}
        {errorReimprimir && <div className="alert alert-danger py-1 px-2 small">{errorReimprimir}</div>}

        {cargando && !datos && <Cargando />}

        {datos && (
          <>
            <div className="table-responsive">
              <table className="table table-sm table-striped align-middle">
                <thead>
                  <tr>
                    <th>Turno</th>
                    <th>Apertura</th>
                    <th>Cierre</th>
                    <th />
                  </tr>
                </thead>
                <tbody>
                  {datos.items.map((turno) => (
                    <tr key={turno.id}>
                      <td>#{turno.id}</td>
                      <td>{formatearFechaHora(turno.fechaApertura)}</td>
                      <td>{turno.fechaCierre ? formatearFechaHora(turno.fechaCierre) : '—'}</td>
                      <td className="text-end">
                        <button
                          type="button"
                          className="btn btn-sm btn-outline-secondary"
                          disabled={reimprimiendo}
                          onClick={() => void reimprimir(turno.id)}
                        >
                          {idReimprimiendo === turno.id ? 'Reimprimiendo…' : 'Reimprimir ticket de cierre'}
                        </button>
                      </td>
                    </tr>
                  ))}
                  {datos.items.length === 0 && (
                    <tr>
                      <td colSpan={4} className="text-center text-muted py-4">
                        Este punto de venta todavía no tiene cierres de caja.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>

            <div className="d-flex justify-content-between align-items-center">
              <span className="small text-muted">
                Página {datos.pagina} de {totalPaginas} — {datos.total} cierre(s)
              </span>
              <div className="d-flex gap-2">
                <button
                  type="button"
                  className="btn btn-sm btn-outline-secondary"
                  disabled={datos.pagina <= 1 || cargando}
                  onClick={() => setPagina((p) => p - 1)}
                >
                  Anterior
                </button>
                <button
                  type="button"
                  className="btn btn-sm btn-outline-secondary"
                  disabled={datos.pagina >= totalPaginas || cargando}
                  onClick={() => setPagina((p) => p + 1)}
                >
                  Siguiente
                </button>
              </div>
            </div>
          </>
        )}
      </Box>
    </div>
  )
}
