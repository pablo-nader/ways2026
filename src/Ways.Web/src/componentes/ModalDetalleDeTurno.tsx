import { useEffect, useMemo, useRef, useState } from 'react'
import { clienteDeCaja } from '../api/caja'
import { clienteDeCatalogo } from '../api/catalogos'
import { ErrorApi } from '../api/cliente'
import type {
  DetalleDeTurno,
  MedioPagoAlta,
  MedioPagoListado,
  ResumenDeCierrePorRetiro,
  TurnoConArqueos,
} from '../api/tipos'
import { formatearImporte, redondearImporte } from '../formato/importes'
import { Cargando } from './Cargando'
import { fechaHoraATexto } from '../formato/fechas'
import { DetalleDeTurnoListas } from './DetalleDeTurnoListas'
import { InsigniaDeRecalculo } from './InsigniaDeRecalculo'
import { EncabezadoOrdenable, useOrdenamiento, type ColumnasOrdenables } from './grilla'
import { Modal } from './Modal'
import { VentasPorMedioDeCierre } from './VentasPorMedioDeCierre'

const clienteMediosPago = clienteDeCatalogo<MedioPagoListado, MedioPagoAlta>('medios-pago')

function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

function formatearFechaHora(iso: string): string {
  return fechaHoraATexto(iso)
}

type DatosDelCierre = {
  detalle: DetalleDeTurno
  turno: TurnoConArqueos
  cierre: ResumenDeCierrePorRetiro
  medios: MedioPagoListado[]
}

type Estado = { fase: 'cargando' } | { fase: 'error'; mensaje: string } | { fase: 'listo'; datos: DatosDelCierre }

function nombreDeMedio(idMedioPago: number, medios: MedioPagoListado[]): string {
  return medios.find((m) => m.id === idMedioPago)?.nombre ?? `Medio #${idMedioPago}`
}

type Props = {
  idTurno: number
  onCerrar: () => void
}

/**
 * Detalle de un turno ya cerrado, para "Cierres de caja" del POS: cabecera del turno, arqueo por
 * medio de pago, y los tickets y gastos que trae `GET /api/caja/turnos/{id}/detalle`.
 *
 * El detalle no trae el cajero ni lo declarado, así que se lee junto a `GET /api/caja/turnos/{id}`
 * (que devuelve el turno con sus arqueos) y a `GET .../resumen-de-cierre` (nombres de cajero y punto
 * de venta). Esas tres lecturas son obligatorias: si una falla no se muestra un detalle a medias. La
 * cuarta, el catálogo de medios, solo pone nombres, por eso su falla no impide mostrar el cierre.
 *
 * La diferencia se muestra como la persiste el servidor (`esperado - declarado`, positivo = faltante),
 * igual que el cierre de `CierreDeCaja.tsx`.
 *
 * Cada lectura lleva su generación (`react-async-state` regla 2): cambiar `idTurno` con una lectura
 * en vuelo descarta su respuesta. Al cerrar el modal el componente se desmonta y no hay nada que pisar.
 */
export function ModalDetalleDeTurno({ idTurno, onCerrar }: Props) {
  const [estado, setEstado] = useState<Estado>({ fase: 'cargando' })
  const generacionRef = useRef(0)

  useEffect(() => {
    const miGeneracion = (generacionRef.current += 1)
    setEstado({ fase: 'cargando' })

    Promise.all([
      clienteDeCaja.obtenerDetalle(idTurno),
      clienteDeCaja.obtenerTurno(idTurno),
      clienteDeCaja.obtenerResumenDeCierre(idTurno),
      clienteMediosPago.listar(false).catch(() => [] as MedioPagoListado[]),
    ])
      .then(([detalle, turno, cierre, medios]) => {
        if (generacionRef.current !== miGeneracion) return
        setEstado({ fase: 'listo', datos: { detalle, turno, cierre, medios } })
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setEstado({ fase: 'error', mensaje: e instanceof ErrorApi ? e.message : 'No se pudo cargar el detalle del cierre.' })
      })
  }, [idTurno])

  return (
    <Modal
      titulo={`Detalle del cierre #${idTurno}`}
      tamano="xl"
      desplazable
      etiquetaCerrar="Cerrar detalle"
      onCerrar={onCerrar}
      pie={
        <button type="button" className="btn btn-secondary" onClick={onCerrar}>
          Cerrar
        </button>
      }
    >
      {estado.fase === 'cargando' && <Cargando texto="Cargando detalle…" />}
      {estado.fase === 'error' && <div className="alert alert-danger mb-0">{estado.mensaje}</div>}
      {estado.fase === 'listo' && <ContenidoDelCierre {...estado.datos} />}
    </Modal>
  )
}

function ContenidoDelCierre({ detalle, turno, cierre, medios }: DatosDelCierre) {
  const columnas = useMemo<ColumnasOrdenables<TurnoConArqueos['arqueos'][number]>>(
    () => ({
      medio: { valor: (a) => nombreDeMedio(a.idMedioPago, medios) },
      esperado: { valor: (a) => a.importeEsperado },
      declarado: { valor: (a) => a.importeDeclarado },
      diferencia: { valor: (a) => a.diferencia },
    }),
    [medios],
  )
  const { filas: arqueos, orden, alternar } = useOrdenamiento(turno.arqueos, columnas)
  const totalEsperado = redondearImporte(turno.arqueos.reduce((suma, a) => suma + a.importeEsperado, 0))
  const totalDeclarado = redondearImporte(turno.arqueos.reduce((suma, a) => suma + a.importeDeclarado, 0))
  const totalDiferencia = redondearImporte(turno.arqueos.reduce((suma, a) => suma + a.diferencia, 0))

  return (
    <>
      <dl className="row mb-3">
        <dt className="col-3">Punto de venta</dt>
        <dd className="col-9">
          PV {cierre.puntoVenta.numero} — {cierre.puntoVenta.nombre}
          <InsigniaDeRecalculo fechaRecalculo={detalle.fechaRecalculo} />
        </dd>
        <dt className="col-3">Apertura</dt>
        <dd className="col-9">{formatearFechaHora(turno.fechaApertura)}</dd>
        <dt className="col-3">Cierre</dt>
        <dd className="col-9">{turno.fechaCierre ? formatearFechaHora(turno.fechaCierre) : '—'}</dd>
        <dt className="col-3">Cajero</dt>
        <dd className="col-9">{cierre.vendedor}</dd>
        <dt className="col-3">Cerró</dt>
        <dd className="col-9">{cierre.empleadoCierre}</dd>
        <dt className="col-3">Fondo inicial</dt>
        <dd className="col-9">{formatearMoneda(turno.fondoInicial)}</dd>
      </dl>

      <h6>Arqueo por medio de pago</h6>
      <div className="table-responsive mb-3">
        <table className="table table-sm table-striped table-bordered align-middle">
          <thead>
            <tr>
              <EncabezadoOrdenable columna="medio" orden={orden} onAlternar={alternar}>
                Medio
              </EncabezadoOrdenable>
              <EncabezadoOrdenable columna="esperado" orden={orden} onAlternar={alternar} className="text-end">
                Esperado
              </EncabezadoOrdenable>
              <EncabezadoOrdenable columna="declarado" orden={orden} onAlternar={alternar} className="text-end">
                Declarado
              </EncabezadoOrdenable>
              <EncabezadoOrdenable columna="diferencia" orden={orden} onAlternar={alternar} className="text-end">
                Diferencia
              </EncabezadoOrdenable>
            </tr>
          </thead>
          <tbody>
            {arqueos.map((a) => (
              <tr key={a.idMedioPago}>
                <td>{nombreDeMedio(a.idMedioPago, medios)}</td>
                <td className="text-end">
                  {formatearMoneda(a.importeEsperado)}
                  {a.importeEsperadoOriginal !== null && (
                    <div className="small text-muted">Esperado original: {formatearMoneda(a.importeEsperadoOriginal)}</div>
                  )}
                </td>
                <td className="text-end">{formatearMoneda(a.importeDeclarado)}</td>
                <td className="text-end">{formatearMoneda(a.diferencia)}</td>
              </tr>
            ))}
            {turno.arqueos.length === 0 && (
              <tr>
                <td colSpan={4} className="text-center text-muted">
                  Este turno no tuvo actividad: no hay ningún medio arqueado.
                </td>
              </tr>
            )}
          </tbody>
          {turno.arqueos.length > 0 && (
            <tfoot>
              <tr className="fw-bold">
                <td>Total</td>
                <td className="text-end">{formatearMoneda(totalEsperado)}</td>
                <td className="text-end">{formatearMoneda(totalDeclarado)}</td>
                <td className="text-end">{formatearMoneda(totalDiferencia)}</td>
              </tr>
            </tfoot>
          )}
        </table>
      </div>

      <VentasPorMedioDeCierre cierre={cierre} />

      <DetalleDeTurnoListas tickets={detalle.tickets} gastos={detalle.gastos} />
    </>
  )
}
