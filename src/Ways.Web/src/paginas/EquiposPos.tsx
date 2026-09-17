import { useCallback, useEffect, useRef, useState } from 'react'
import { copiaDeFalloDeBaja } from '../api/bajas'
import { ErrorApi } from '../api/cliente'
import { clienteDeDispositivos } from '../api/dispositivos'
import type { DispositivoListado } from '../api/dispositivos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'

const AVISO_REFRESCO_FALLIDO = 'Se revocó, pero no se pudo actualizar la vista. Recargá la pantalla.'

/** OD4: la revocación no libera las bajas que el equipo bloquea, y la puerta tiene que decirlo antes
 * de confirmar. */
export const NOTA_DE_REVOCACION =
  'El equipo deja de poder iniciar sesión y las sesiones abiertas en él se cortan en su próxima operación. ' +
  'La revocación es lógica: el equipo sigue contando como uso, así que su punto de venta, el tenant y el ' +
  'usuario que lo vinculó no se van a poder dar de baja.'

function formatearFechaHora(iso: string): string {
  return new Date(iso).toLocaleString('es-AR')
}

/**
 * Listado y revocación de los equipos del POS de escritorio del tenant. Mismo patrón de puerta,
 * generación y re-entrancia que `PuntosVenta.tsx`.
 */
export function EquiposPos() {
  const [items, setItems] = useState<DispositivoListado[]>([])
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [ocupado, setOcupado] = useState<number | null>(null)
  const [revocacion, setRevocacion] = useState<DispositivoListado | null>(null)
  const [disparadorDeLaPuerta, setDisparadorDeLaPuerta] = useState<HTMLElement | null>(null)

  /** Se incrementa en la carga inicial y al confirmar una revocación; abrir o cancelar la puerta no
   * superseden nada y no lo tocan. */
  const generacion = useRef(0)
  const ocupadoRef = useRef(false)

  const cargar = useCallback(async (token: number, propagar = false) => {
    setCargando(true)
    try {
      const filas = await clienteDeDispositivos.listar()
      if (generacion.current !== token) return
      setItems(filas)
      setError('')
    } catch (e) {
      if (generacion.current !== token) return
      if (propagar) throw e
      setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los equipos.')
    } finally {
      if (generacion.current === token) setCargando(false)
    }
  }, [])

  useEffect(() => {
    void cargar(++generacion.current)
  }, [cargar])

  function pedirRevocacion(dispositivo: DispositivoListado, disparador: HTMLElement | null) {
    if (ocupadoRef.current) return

    setDisparadorDeLaPuerta(disparador)
    setRevocacion(dispositivo)
    setError('')
    setAviso('')
  }

  function cancelarRevocacion() {
    if (ocupadoRef.current) return

    setDisparadorDeLaPuerta(null)
    setRevocacion(null)
    setError('')
    setAviso('')
  }

  async function confirmarRevocacion() {
    if (!revocacion || ocupadoRef.current) return

    const fila = revocacion
    const token = ++generacion.current
    ocupadoRef.current = true
    setOcupado(fila.id)
    setError('')
    setAviso('')
    try {
      try {
        await clienteDeDispositivos.revocar(fila.id)
      } catch (e) {
        setError(copiaDeFalloDeBaja(e, 'el equipo', 'revocar'))

        return
      }

      setRevocacion(null)
      const mensajeOk = `Se revocó el equipo "${fila.nombre}".`
      setAviso(mensajeOk)
      // El refresco va fuera del try/catch del DELETE: una revocación confirmada nunca se reporta
      // como fallida.
      try {
        await cargar(token, true)
      } catch {
        if (generacion.current === token) setAviso(`${mensajeOk} ${AVISO_REFRESCO_FALLIDO}`)
      }
    } finally {
      ocupadoRef.current = false
      setOcupado(null)
    }
  }

  const bloqueado = ocupado !== null || revocacion !== null

  return (
    <div className="container-fluid py-4">
      <Box titulo="Equipos POS" variante="inverse">
        {error && <div className="alert alert-danger rounded-0">{error}</div>}
        {aviso && <div className="alert alert-success rounded-0">{aviso}</div>}

        {revocacion && (
          <ConfirmacionDeBaja
            titulo={`el equipo "${revocacion.nombre}"`}
            pregunta="Revocar"
            nota={NOTA_DE_REVOCACION}
            etiquetaConfirmar="Confirmar revocación"
            etiquetaEnCurso="Revocando…"
            ocupado={ocupado !== null}
            disparador={disparadorDeLaPuerta}
            onConfirmar={confirmarRevocacion}
            onCancelar={cancelarRevocacion}
          />
        )}

        {cargando ? (
          <Cargando />
        ) : (
          <div className="table-responsive">
            <table className="table table-striped table-hover table-bordered align-middle">
              <thead>
                <tr>
                  <th>Nombre</th>
                  <th>Punto de venta</th>
                  <th>Vinculado el</th>
                  <th>Último uso</th>
                  <th className="text-end">Acciones</th>
                </tr>
              </thead>
              <tbody>
                {items.map((d) => (
                  <tr key={d.id}>
                    <td>{d.nombre}</td>
                    <td>{d.puntoVenta.nombre}</td>
                    <td>{formatearFechaHora(d.createdAt)}</td>
                    <td>{d.ultimoUsoAt ? formatearFechaHora(d.ultimoUsoAt) : 'Nunca'}</td>
                    <td className="text-end text-nowrap">
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-danger rounded-0"
                        onClick={(evento) => pedirRevocacion(d, evento.currentTarget)}
                        disabled={bloqueado}
                      >
                        Revocar
                      </button>
                    </td>
                  </tr>
                ))}
                {items.length === 0 && (
                  <tr>
                    <td colSpan={5} className="text-center text-muted py-4">
                      No hay equipos vinculados.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        )}
      </Box>
    </div>
  )
}
