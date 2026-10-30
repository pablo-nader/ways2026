import { useCallback, useEffect, useRef, useState } from 'react'
import { copiaDeFalloDeBaja } from '../api/bajas'
import { ErrorApi } from '../api/cliente'
import { clienteDeDispositivos } from '../api/dispositivos'
import type { DispositivoListado } from '../api/dispositivos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'
import {
  EncabezadoOrdenable,
  FilaDeFiltros,
  useFiltrosDeColumna,
  useOrdenamiento,
  type ColumnasFiltrables,
  type ColumnasOrdenables,
} from '../componentes/grilla'
import { fechaHoraATexto } from '../formato/fechas'

const AVISO_REFRESCO_FALLIDO = 'Se revocó, pero no se pudo actualizar la vista. Recargá la pantalla.'

/** OD4: la revocación no libera las bajas que el equipo bloquea, y la puerta tiene que decirlo antes
 * de confirmar. También avisa que la cola offline sin rendir queda perdida (`LectorDeRendicionDeDispositivos`). */
export const NOTA_DE_REVOCACION =
  'El equipo deja de poder iniciar sesión y las sesiones abiertas en él se cortan en su próxima operación. ' +
  'La revocación es lógica: el equipo sigue contando como uso, así que su punto de venta, el tenant y el ' +
  'usuario que lo vinculó no se van a poder dar de baja. Si el equipo tiene ventas sin conexión que todavía ' +
  'no sincronizó, se pierden: un equipo revocado ya no puede rendir su cola.'

function formatearFechaHora(iso: string): string {
  return fechaHoraATexto(iso)
}

const textoDeUltimoUso = (equipo: DispositivoListado) => (equipo.ultimoUsoAt ? formatearFechaHora(equipo.ultimoUsoAt) : 'Nunca')

const ORDENABLES: ColumnasOrdenables<DispositivoListado> = {
  nombre: { valor: (equipo) => equipo.nombre },
  puntoVenta: { valor: (equipo) => equipo.puntoVenta.nombre },
  vinculadoEl: { valor: (equipo) => new Date(equipo.createdAt) },
  ultimoUso: { valor: (equipo) => (equipo.ultimoUsoAt ? new Date(equipo.ultimoUsoAt) : null) },
}

const FILTRABLES: ColumnasFiltrables<DispositivoListado> = {
  nombre: { etiqueta: 'Nombre', valor: (equipo) => equipo.nombre },
  puntoVenta: { etiqueta: 'Punto de venta', valor: (equipo) => equipo.puntoVenta.nombre },
  vinculadoEl: { etiqueta: 'Vinculado el', valor: (equipo) => formatearFechaHora(equipo.createdAt) },
  ultimoUso: { etiqueta: 'Último uso', valor: textoDeUltimoUso },
}

/** Una entrada por columna del encabezado, en su orden; `null` es Acciones. */
const CELDAS_DE_FILTROS = ['nombre', 'puntoVenta', 'vinculadoEl', 'ultimoUso', null]

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

  const { filas: filasFiltradas, filtros, cambiar: cambiarFiltro, limpiar: limpiarFiltros, hayFiltros } = useFiltrosDeColumna(
    items,
    FILTRABLES,
  )
  const { filas: filasVisibles, orden, alternar } = useOrdenamiento(filasFiltradas, ORDENABLES)

  return (
    <div className="container-fluid py-4">
      <Box titulo="Equipos POS" variante="inverse">
        {error && !revocacion && <div className="alert alert-danger">{error}</div>}
        {aviso && <div className="alert alert-success">{aviso}</div>}

        {revocacion && (
          <ConfirmacionDeBaja
            titulo={`el equipo "${revocacion.nombre}"`}
            pregunta="Revocar"
            nota={NOTA_DE_REVOCACION}
            etiquetaConfirmar="Confirmar revocación"
            etiquetaEnCurso="Revocando…"
            ocupado={ocupado !== null}
            disparador={disparadorDeLaPuerta}
            error={error}
            onConfirmar={confirmarRevocacion}
            onCancelar={cancelarRevocacion}
          />
        )}

        {cargando && <Cargando />}

        {cargando && items.length === 0 ? null : (
          <div className="table-responsive">
            <table className="table table-striped table-hover table-bordered align-middle">
              <thead>
                <tr>
                  <EncabezadoOrdenable columna="nombre" orden={orden} onAlternar={alternar}>
                    Nombre
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="puntoVenta" orden={orden} onAlternar={alternar}>
                    Punto de venta
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="vinculadoEl" orden={orden} onAlternar={alternar}>
                    Vinculado el
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="ultimoUso" orden={orden} onAlternar={alternar}>
                    Último uso
                  </EncabezadoOrdenable>
                  <th scope="col" className="text-end">
                    Acciones
                  </th>
                </tr>
                <FilaDeFiltros
                  celdas={CELDAS_DE_FILTROS}
                  columnas={FILTRABLES}
                  filtros={filtros}
                  onCambio={cambiarFiltro}
                  onLimpiar={limpiarFiltros}
                  hayFiltros={hayFiltros}
                  deshabilitado={bloqueado}
                />
              </thead>
              <tbody>
                {filasVisibles.map((d) => (
                  <tr key={d.id}>
                    <td>{d.nombre}</td>
                    <td>{d.puntoVenta.nombre}</td>
                    <td>{formatearFechaHora(d.createdAt)}</td>
                    <td>{textoDeUltimoUso(d)}</td>
                    <td className="text-end text-nowrap">
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-danger"
                        onClick={(evento) => pedirRevocacion(d, evento.currentTarget)}
                        disabled={bloqueado}
                      >
                        Revocar
                      </button>
                    </td>
                  </tr>
                ))}
                {filasVisibles.length === 0 && (
                  <tr>
                    <td colSpan={5} className="text-center text-muted py-4">
                      {items.length === 0 ? 'No hay equipos vinculados.' : 'Ningún registro coincide con los filtros.'}
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
