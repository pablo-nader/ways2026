import { useEffect, useRef, useState } from 'react'
import { clienteDeCaja } from '../api/caja'
import { ErrorApi } from '../api/cliente'
import { clienteDeVentas } from '../api/ventas'
import { ROL } from '../api/tipos'
import type { TurnoResumen, VentaDeTurnoListado } from '../api/tipos'
import { useAuth } from '../auth/useAuth'
import { usePuntoVenta } from '../puntoVenta/usePuntoVenta'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'
import {
  claseDeBadgeDeEstadoVenta,
  etiquetaDeEstadoVenta,
  formatearFechaHora,
  formatearMoneda,
  puedeAnular,
  totalesDeVentas,
} from './utilidadesVentasDelTurno'

function mensajeDeErrorAnulacion(e: unknown): string {
  if (e instanceof ErrorApi) {
    if (e.estado === 403) return 'No tenés permiso para anular esta venta.'
    return e.message
  }
  return 'No se pudo anular la venta.'
}

/**
 * "Ventas del turno" (stage-desktop-pos, POS de escritorio): consulta las ventas del turno
 * ABIERTO del punto de venta fijo del dispositivo y permite anular una — mismo endpoint que
 * `POST /api/ventas/{id}/anulacion` (ya existente, sin `motivo`: revierte stock y cuenta corriente
 * en la misma transacción, nunca hay "restaurar").
 *
 * `generacionRef` es COMPARTIDO entre la carga inicial/refresco y la anulación (mismo criterio que
 * `Remito.tsx`/`ShellPos.tsx`): una respuesta desactualizada de cualquiera de las dos nunca pisa un
 * estado más nuevo. Mientras el modal de confirmación está montado O una anulación está en vuelo,
 * TODA la pantalla (refrescar + cada botón "Anular") queda inerte — react-async-state regla 9.
 */
export function VentasDelTurno() {
  const { puntoVenta } = usePuntoVenta()
  const { usuario } = useAuth()
  // Rol más restrictivo (Root, que nunca opera el POS) sin sesión resuelta todavía — mismo
  // criterio defensivo que el resto del shell: `usuario`/`puntoVenta` son `| null` a nivel de
  // tipo (contextos genéricos), aunque `ShellPos` siempre los provee ya resueltos acá.
  const rolId = usuario?.rolId ?? ROL.Root

  const [turno, setTurno] = useState<TurnoResumen | null>(null)
  const [buscandoTurno, setBuscandoTurno] = useState(true)
  const [errorTurno, setErrorTurno] = useState('')

  const [ventas, setVentas] = useState<VentaDeTurnoListado[]>([])
  const [cargandoVentas, setCargandoVentas] = useState(false)
  const [errorVentas, setErrorVentas] = useState('')

  const generacionRef = useRef(0)

  const [filaAAnular, setFilaAAnular] = useState<VentaDeTurnoListado | null>(null)
  const disparadorRef = useRef<HTMLElement | null>(null)
  const [anulando, setAnulando] = useState(false)
  const anulandoRef = useRef(false)
  const [errorAnular, setErrorAnular] = useState('')

  async function cargarVentas(idTurno: number, miGeneracion: number) {
    setCargandoVentas(true)
    setErrorVentas('')
    try {
      const filas = await clienteDeVentas.listarPorTurno(idTurno)
      if (generacionRef.current !== miGeneracion) return
      setVentas(filas)
    } catch (e) {
      if (generacionRef.current !== miGeneracion) return
      setVentas([])
      setErrorVentas(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las ventas del turno.')
    } finally {
      if (generacionRef.current === miGeneracion) setCargandoVentas(false)
    }
  }

  async function cargarTurnoYVentas() {
    if (!puntoVenta) return
    const miGeneracion = (generacionRef.current += 1)
    setBuscandoTurno(true)
    setErrorTurno('')

    try {
      const turnoAbierto = await clienteDeCaja.obtenerAbierto(puntoVenta.id)
      if (generacionRef.current !== miGeneracion) return
      setTurno(turnoAbierto)

      if (!turnoAbierto) {
        setVentas([])
        return
      }
      await cargarVentas(turnoAbierto.id, miGeneracion)
    } catch (e) {
      if (generacionRef.current !== miGeneracion) return
      setTurno(null)
      setVentas([])
      setErrorTurno(e instanceof ErrorApi ? e.message : 'No se pudo consultar el turno abierto.')
    } finally {
      if (generacionRef.current === miGeneracion) setBuscandoTurno(false)
    }
  }

  useEffect(() => {
    void cargarTurnoYVentas()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [puntoVenta?.id])

  const ocupado = buscandoTurno || cargandoVentas || anulando
  const bloqueado = ocupado || filaAAnular !== null

  function pedirAnular(fila: VentaDeTurnoListado, disparador: HTMLElement) {
    if (bloqueado) return
    disparadorRef.current = disparador
    setErrorAnular('')
    setFilaAAnular(fila)
  }

  function cancelarAnular() {
    setFilaAAnular(null)
    setErrorAnular('')
  }

  async function confirmarAnular() {
    if (anulandoRef.current || !filaAAnular) return
    const fila = filaAAnular
    anulandoRef.current = true
    setAnulando(true)
    setErrorAnular('')
    const miGeneracion = (generacionRef.current += 1)

    try {
      await clienteDeVentas.anular(fila.id)
      if (generacionRef.current !== miGeneracion) return
      setFilaAAnular(null)
      if (turno) await cargarVentas(turno.id, miGeneracion)
    } catch (e) {
      if (generacionRef.current !== miGeneracion) return
      setErrorAnular(mensajeDeErrorAnulacion(e))
    } finally {
      anulandoRef.current = false
      if (generacionRef.current === miGeneracion) setAnulando(false)
    }
  }

  const totales = totalesDeVentas(ventas)

  const herramientas = (
    <button
      type="button"
      className="btn btn-sm btn-outline-light rounded-0"
      disabled={bloqueado}
      onClick={() => void cargarTurnoYVentas()}
    >
      {buscandoTurno || cargandoVentas ? 'Actualizando…' : 'Refrescar'}
    </button>
  )

  return (
    <div className="container-fluid py-4">
      <Box titulo="Ventas del turno" variante="inverse" herramientas={herramientas}>
        {!puntoVenta && <div className="alert alert-warning rounded-0 mb-0">No hay un punto de venta asociado a este dispositivo.</div>}

        {puntoVenta && errorTurno && <div className="alert alert-danger rounded-0">{errorTurno}</div>}

        {puntoVenta && !errorTurno && buscandoTurno && !turno && <Cargando />}

        {puntoVenta && !errorTurno && !buscandoTurno && !turno && (
          <div className="alert alert-warning rounded-0 mb-0">No hay un turno abierto en este punto de venta.</div>
        )}

        {turno && (
          <>
            {errorVentas && <div className="alert alert-danger rounded-0">{errorVentas}</div>}
            {errorAnular && !filaAAnular && <div className="alert alert-danger rounded-0">{errorAnular}</div>}

            {cargandoVentas && ventas.length === 0 && <Cargando />}

            <div className="table-responsive">
              <table className="table table-sm table-striped table-bordered align-middle">
                <thead>
                  <tr>
                    <th>Número</th>
                    <th>Fecha</th>
                    <th>Cliente</th>
                    <th className="text-end">Total</th>
                    <th>Medios de pago</th>
                    <th>Estado</th>
                    <th>Acciones</th>
                  </tr>
                </thead>
                <tbody>
                  {ventas.map((v) => (
                    <tr key={v.id}>
                      <td>{v.numeroVisible}</td>
                      <td>{formatearFechaHora(v.fecha)}</td>
                      <td>{v.nombreCliente}</td>
                      <td className="text-end">{formatearMoneda(v.total)}</td>
                      <td>{v.mediosDePago.join(', ') || '—'}</td>
                      <td>
                        <span className={`badge rounded-0 ${claseDeBadgeDeEstadoVenta(v.estado)}`}>
                          {etiquetaDeEstadoVenta(v.estado)}
                        </span>
                      </td>
                      <td>
                        {puedeAnular(v, rolId) && (
                          <button
                            type="button"
                            className="btn btn-sm btn-outline-danger rounded-0"
                            disabled={bloqueado}
                            onClick={(e) => pedirAnular(v, e.currentTarget)}
                          >
                            Anular
                          </button>
                        )}
                      </td>
                    </tr>
                  ))}
                  {ventas.length === 0 && !cargandoVentas && (
                    <tr>
                      <td colSpan={7} className="text-center text-muted py-4">
                        Este turno todavía no tiene ventas.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>

            <div className="d-flex justify-content-between align-items-center">
              <span className="small text-muted">
                {totales.cantidad} venta(s) — total {formatearMoneda(totales.total)}
              </span>
            </div>

            {filaAAnular && (
              <div className="mt-3">
                <ConfirmacionDeBaja
                  titulo={`la venta ${filaAAnular.numeroVisible} por ${formatearMoneda(filaAAnular.total)}`}
                  pregunta="Anular"
                  nota="Se revierte el stock y la cuenta corriente en la misma operación. No existe restaurar."
                  etiquetaConfirmar="Anular venta"
                  etiquetaEnCurso="Anulando…"
                  ocupado={anulando}
                  disparador={disparadorRef.current}
                  onConfirmar={() => void confirmarAnular()}
                  onCancelar={cancelarAnular}
                />
                {errorAnular && <div className="alert alert-danger rounded-0 mt-2 mb-0">{errorAnular}</div>}
              </div>
            )}
          </>
        )}
      </Box>
    </div>
  )
}
