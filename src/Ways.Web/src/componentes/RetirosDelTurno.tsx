import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'
import { clienteDeCaja, filtrosDeRetirosVacios } from '../api/caja'
import { ErrorApi } from '../api/cliente'
import type { FilaDeRetiro, PaginaDeRetiros } from '../api/tipos'
import { fechaHoraATexto } from '../formato/fechas'
import { formatearImporte } from '../formato/importes'
import { motivoDeNoModificarRetiro } from '../paginas/utilidadesRetirosDeAdministracion'
import { BotonIcono } from './BotonIcono'
import { Cargando } from './Cargando'

/** Tope de filas por página del servidor para `GET /api/caja/retiros`. */
const TAMANIO_MAXIMO_DE_RETIROS = 200

type Props = {
  idTurno: number
  /** Cada cambio vuelve a pedir los retiros del turno sin tocar lo que ya se muestra. */
  recarga: number
  /** `true` mientras hay un diálogo abierto: las filas quedan inertes. */
  inerte: boolean
  onEditar: (retiro: FilaDeRetiro, disparador: HTMLElement) => void
}

/**
 * Retiros de un turno para la administración (el detalle del turno solo trae su total). Pide la primera página de
 * `GET /api/caja/retiros` filtrada por turno; si hay más filas que las que entran, lo dice y manda a la grilla completa.
 * Una generación descarta la respuesta de un turno o de una recarga anterior; un fallo al recargar conserva las filas
 * que ya se mostraban y lo avisa aparte.
 */
export function RetirosDelTurno({ idTurno, recarga, inerte, onEditar }: Props) {
  const [pagina, setPagina] = useState<PaginaDeRetiros | null>(null)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const generacionRef = useRef(0)
  const turnoCargadoRef = useRef<number | null>(null)

  useEffect(() => {
    const miGeneracion = ++generacionRef.current
    if (turnoCargadoRef.current !== idTurno) {
      turnoCargadoRef.current = idTurno
      setPagina(null)
    }
    setCargando(true)
    setError('')

    clienteDeCaja
      .listarRetiros({ ...filtrosDeRetirosVacios(), idTurnoCaja: String(idTurno), tamanio: TAMANIO_MAXIMO_DE_RETIROS }, null)
      .then((datos) => {
        if (generacionRef.current !== miGeneracion) return
        setPagina(datos)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los retiros del turno.')
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargando(false)
      })

    return () => {
      generacionRef.current += 1
    }
  }, [idTurno, recarga])

  const filas = pagina?.items ?? []

  return (
    <>
      <h6>Retiros</h6>
      {error && (
        <div className="alert alert-danger py-1 px-2 small mb-2" role="alert">
          {error}
        </div>
      )}
      {cargando && pagina === null && <Cargando />}
      {pagina !== null && (
        <div className="table-responsive mb-3">
          <table className="table table-sm table-striped table-bordered align-middle">
            <thead>
              <tr>
                <th>Fecha</th>
                <th>Empleado</th>
                <th>Motivo</th>
                <th className="text-end">Importe</th>
                <th>Acciones</th>
              </tr>
            </thead>
            <tbody>
              {filas.map((r) => {
                const motivoDeBloqueo = motivoDeNoModificarRetiro(r)

                return (
                  <tr key={r.id}>
                    <td>{fechaHoraATexto(r.fecha)}</td>
                    <td>{r.empleado ?? '—'}</td>
                    <td>{r.motivo}</td>
                    <td className="text-end">{formatearImporte(r.importe, { simbolo: true })}</td>
                    <td>
                      <span title={motivoDeBloqueo ?? 'Editar'}>
                        <BotonIcono
                          icono="editar"
                          etiqueta={`Editar el retiro de ${r.motivo}`}
                          title={motivoDeBloqueo ?? 'Editar'}
                          disabled={inerte || cargando || motivoDeBloqueo !== null}
                          onClick={(evento) => onEditar(r, evento.currentTarget)}
                        />
                      </span>
                    </td>
                  </tr>
                )
              })}
              {filas.length === 0 && (
                <tr>
                  <td colSpan={5} className="text-center text-muted py-3">
                    Sin retiros.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      )}
      {pagina !== null && pagina.total > filas.length && (
        <p className="small text-muted">
          Se muestran {filas.length} de {pagina.total} retiros del turno. Los demás están en <Link to="/caja/retiros">Retiros</Link>.
        </p>
      )}
    </>
  )
}
