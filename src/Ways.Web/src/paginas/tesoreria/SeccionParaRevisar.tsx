import { useCallback, useState } from 'react'
import {
  TAMANO_DE_PAGINA,
  clienteDeConciliacionMercadoPago,
  esFilaConfirmable,
  mensajeDeErrorDeConciliacion,
  motivoDeRangoInvalido,
  rangoUltimosDias,
  textoDeFecha,
  textoDeImporte,
  ultimaConciliacion,
  type CandidatoDeConfirmacion,
  type ConciliacionListado,
  type FilaDeConciliacion,
} from '../../api/conciliacionMercadoPago'
import { Cargando } from '../../componentes/Cargando'
import { CampoFecha } from '../../componentes/CampoFecha'
import { TablaDeFilasDeConciliacion } from './TablaDeFilasDeConciliacion'
import { useLectura } from './useLectura'

type Props = {
  idEmpresa: number
  conciliaciones: readonly ConciliacionListado[] | null
  /** Cambia cuando una escritura ya aplicada pide volver a leer todo. */
  version: number
  puedeConfirmar: boolean
  bloqueado: boolean
  onConfirmar: (idConciliacion: number, fila: FilaDeConciliacion, candidato: CandidatoDeConfirmacion) => void
}

const DIAS_POR_DEFECTO = 30
const mensajeDeLectura = (e: unknown) => mensajeDeErrorDeConciliacion(e, 'lectura')

function AvisoDeError({ texto, onReintentar }: { texto: string; onReintentar: () => void }) {
  return (
    <div className="alert alert-danger d-flex justify-content-between align-items-center gap-2" role="alert">
      <span>{texto}</span>
      <button type="button" className="btn btn-sm btn-outline-danger" onClick={onReintentar}>
        Reintentar
      </button>
    </div>
  )
}

function CobrosSinVenta({ idEmpresa, version }: Pick<Props, 'idEmpresa' | 'version'>) {
  const leer = useCallback(() => {
    void version
    return clienteDeConciliacionMercadoPago.cobrosSinVenta(idEmpresa)
  }, [idEmpresa, version])
  const lectura = useLectura(leer, mensajeDeLectura)

  return (
    <section aria-label="Cobros sin venta" className="mb-4">
      <h6>Cobros sin venta</h6>
      <p className="small text-muted">El dinero entró en Mercado Pago y ninguna venta lo usó.</p>
      {lectura.error && <AvisoDeError texto={lectura.error} onReintentar={lectura.recargar} />}
      {lectura.cargando && !lectura.datos && <Cargando />}
      {lectura.datos &&
        (lectura.datos.length === 0 ? (
          <p className="text-muted">No hay cobros sin venta.</p>
        ) : (
          <div className="table-responsive">
            <table className="table table-sm align-middle">
              <thead>
                <tr>
                  <th>Cobro</th>
                  <th>Número</th>
                  <th>Punto de venta</th>
                  <th className="text-end">Importe</th>
                  <th className="text-end">Pagado</th>
                  <th>Aprobado</th>
                  <th>Apartado</th>
                  <th>Pago MP</th>
                </tr>
              </thead>
              <tbody>
                {lectura.datos.map((cobro) => (
                  <tr key={cobro.idCobro}>
                    <td>#{cobro.idCobro}</td>
                    <td>{cobro.numeroPreasignado}</td>
                    <td>{cobro.idPuntoVenta}</td>
                    <td className="text-end text-nowrap">{textoDeImporte(cobro.importe)}</td>
                    <td className="text-end text-nowrap">
                      {cobro.importePagado === null ? '—' : textoDeImporte(cobro.importePagado)}
                      {cobro.pagoInformadoSinDatos && <div className="badge text-bg-warning">Pago informado sin datos</div>}
                    </td>
                    <td className="text-nowrap">{textoDeFecha(cobro.fechaAprobacion)}</td>
                    <td className="text-nowrap">{textoDeFecha(cobro.fechaApartado)}</td>
                    <td>{cobro.idPagoMercadoPago ?? '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ))}
    </section>
  )
}

function PagosSinVerificar({ idEmpresa, version }: Pick<Props, 'idEmpresa' | 'version'>) {
  const [rango, setRango] = useState(() => rangoUltimosDias(DIAS_POR_DEFECTO))
  const motivo = motivoDeRangoInvalido(rango.desde, rango.hasta)
  const { desde, hasta } = rango

  const leer = useCallback(() => {
    void version
    return clienteDeConciliacionMercadoPago.pagosSinVerificar(idEmpresa, desde, hasta)
  }, [idEmpresa, desde, hasta, version])
  const lectura = useLectura(motivo === null ? leer : null, mensajeDeLectura)

  return (
    <section aria-label="Pagos sin verificar" className="mb-4">
      <h6>Pagos sin verificar</h6>
      <p className="small text-muted">Pagos de venta que el cajero aprobó a mano sin red; la conciliación los cotea.</p>
      <div className="row g-2 align-items-end mb-2">
        <div className="col-md-3">
          <label className="form-label" htmlFor="sin-verificar-desde">
            Desde
          </label>
          <CampoFecha id="sin-verificar-desde" className="form-control" value={rango.desde} onChange={(valor) => setRango((r) => ({ ...r, desde: valor }))} />
        </div>
        <div className="col-md-3">
          <label className="form-label" htmlFor="sin-verificar-hasta">
            Hasta
          </label>
          <CampoFecha id="sin-verificar-hasta" className="form-control" value={rango.hasta} onChange={(valor) => setRango((r) => ({ ...r, hasta: valor }))} />
        </div>
      </div>
      {motivo && <div className="alert alert-warning py-1 px-2 small">{motivo}</div>}
      {lectura.error && <AvisoDeError texto={lectura.error} onReintentar={lectura.recargar} />}
      {lectura.cargando && !lectura.datos && motivo === null && <Cargando />}
      {lectura.datos && (
        <>
          {lectura.datos.truncado && (
            <div className="alert alert-warning py-1 px-2 small">
              Hay más pagos que los listados: se muestran los más recientes. Acotá el rango para ver el resto.
            </div>
          )}
          {lectura.datos.items.length === 0 ? (
            <p className="text-muted">No hay pagos sin verificar en el rango.</p>
          ) : (
            <div className="table-responsive">
              <table className="table table-sm align-middle">
                <thead>
                  <tr>
                    <th>Fecha</th>
                    <th>Venta</th>
                    <th className="text-end">Importe</th>
                    <th>Empleado</th>
                    <th>Referencia</th>
                    <th>Aviso</th>
                  </tr>
                </thead>
                <tbody>
                  {lectura.datos.items.map((pago) => {
                    const conflictos = pago.idsCobroEnConflicto ?? []
                    return (
                      <tr key={pago.idPago}>
                        <td className="text-nowrap">{textoDeFecha(pago.fecha)}</td>
                        <td>
                          {pago.numeroVisible}
                          {pago.anulada && <span className="badge text-bg-secondary ms-1">Anulada</span>}
                        </td>
                        <td className="text-end text-nowrap">{textoDeImporte(pago.importe)}</td>
                        <td>{pago.empleado ?? '—'}</td>
                        <td>{pago.referencia ?? '—'}</td>
                        <td>
                          {conflictos.length > 0 ? (
                            <span className="badge text-bg-warning">
                              Chocó con {conflictos.length === 1 ? 'el cobro' : 'los cobros'} de Mercado Pago {conflictos.map((id) => `#${id}`).join(', ')}
                            </span>
                          ) : (
                            '—'
                          )}
                        </td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}
    </section>
  )
}

function CandidatosDeLaUltima({ ultima, version, puedeConfirmar, bloqueado, onConfirmar }: Omit<Props, 'idEmpresa' | 'conciliaciones'> & { ultima: ConciliacionListado }) {
  const leer = useCallback(() => {
    void version
    return clienteDeConciliacionMercadoPago.detalle(ultima.id, { resultado: 'sin_cobro_en_ways', pagina: 1, tamano: TAMANO_DE_PAGINA })
  }, [ultima.id, version])
  const lectura = useLectura(leer, mensajeDeLectura)
  const confirmables = lectura.datos?.filas.filter(esFilaConfirmable) ?? []

  return (
    <section aria-label="Filas por confirmar" className="mb-4">
      <h6>Filas del último reporte por confirmar</h6>
      <p className="small text-muted">
        Filas sin cobro en Ways que coinciden con un pago sin verificar ({ultima.nombreArchivo ?? `conciliación ${ultima.id}`}).
      </p>
      {lectura.error && <AvisoDeError texto={lectura.error} onReintentar={lectura.recargar} />}
      {lectura.cargando && !lectura.datos && <Cargando />}
      {lectura.datos &&
        (confirmables.length === 0 ? (
          <p className="text-muted">No hay filas con pagos candidatos.</p>
        ) : (
          <>
            <TablaDeFilasDeConciliacion
              filas={confirmables}
              puedeConfirmar={puedeConfirmar}
              bloqueado={bloqueado}
              onConfirmar={(fila, candidato) => onConfirmar(ultima.id, fila, candidato)}
            />
            {lectura.datos.total > lectura.datos.filas.length && (
              <p className="small text-muted">
                Se leyeron las primeras {lectura.datos.filas.length} de {lectura.datos.total} filas sin cobro: abrí la conciliación para ver el resto.
              </p>
            )}
          </>
        ))}
    </section>
  )
}

/** Lo que pide una mirada: cobros que entraron sin venta, pagos aprobados a mano sin verificar y las filas del último reporte con candidatos. */
export function SeccionParaRevisar({ idEmpresa, conciliaciones, version, puedeConfirmar, bloqueado, onConfirmar }: Props) {
  const ultima = conciliaciones ? ultimaConciliacion(conciliaciones) : null

  return (
    <div>
      {ultima && (
        <CandidatosDeLaUltima
          key={ultima.id}
          ultima={ultima}
          version={version}
          puedeConfirmar={puedeConfirmar}
          bloqueado={bloqueado}
          onConfirmar={onConfirmar}
        />
      )}
      <CobrosSinVenta idEmpresa={idEmpresa} version={version} />
      <PagosSinVerificar idEmpresa={idEmpresa} version={version} />
    </div>
  )
}
