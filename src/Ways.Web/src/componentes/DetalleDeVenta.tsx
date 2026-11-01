import { useEffect, useMemo } from 'react'
import { formatearPorcentajeDeAjuste, rotuloDeAjusteManual } from '../api/ajusteManual'
import type { ComprobanteEmitido, MedioPagoListado, VentaDeTurnoListado } from '../api/tipos'
import { formatearImporteConSigno } from '../formato/importes'
import {
  badgeDeNotaDeCredito,
  badgeFiscalDeVenta,
  claseDeBadgeDeEstadoVenta,
  etiquetaDeEstadoVenta,
  formatearFechaHora,
  formatearMoneda,
  esAlcanzable,
  nombreDeMedio,
  tieneDescuentoManual,
  tieneRecargoManual,
} from '../paginas/utilidadesVentasDelTurno'
import { Icono } from './BotonIcono'
import { Cargando } from './Cargando'
import { EncabezadoOrdenable, useOrdenamiento, type ColumnasOrdenables } from './grilla'
import { Modal } from './Modal'

type ItemDelComprobante = ComprobanteEmitido['items'][number]
type PagoDelComprobante = ComprobanteEmitido['pagos'][number]

const ITEMS_SIN_DATOS: ItemDelComprobante[] = []
const PAGOS_SIN_DATOS: PagoDelComprobante[] = []

const COLUMNAS_DE_ITEMS: ColumnasOrdenables<ItemDelComprobante> = {
  descripcion: { valor: (i) => i.descripcion },
  cantidad: { valor: (i) => i.cantidad },
  precio: { valor: (i) => i.precioUnitario },
  descuento: { valor: (i) => i.descuento },
  ajuste: { valor: (i) => i.ajusteManualPorcentaje },
  total: { valor: (i) => i.total },
}

function BadgeFiscal({ estadoFiscal }: { estadoFiscal: string | undefined }) {
  const badge = badgeFiscalDeVenta(estadoFiscal)
  return badge ? <span className={`badge ${badge.clase} ms-1`}>{badge.etiqueta}</span> : null
}

function BadgeNotaDeCredito({ estado }: { estado: string | null | undefined }) {
  const badge = badgeDeNotaDeCredito(estado)
  return badge ? <span className={`badge ${badge.clase} ms-1`}>{badge.etiqueta}</span> : null
}

/** Las marcas del estado de una venta en una fila de listado: estado, factura, nota de crédito y ajuste manual. */
export function EstadoDeVentaEnFila({ venta }: { venta: VentaDeTurnoListado }) {
  return (
    <>
      <span className={`badge ${claseDeBadgeDeEstadoVenta(venta.estado)}`}>{etiquetaDeEstadoVenta(venta.estado)}</span>
      <BadgeFiscal estadoFiscal={venta.estadoFiscal} />
      <BadgeNotaDeCredito estado={venta.estadoNotaDeCredito} />
      {tieneDescuentoManual(venta) && (
        <span className="badge text-bg-warning ms-1" title={`Descuento manual: ${formatearMoneda(venta.descuentoManualTotal)}`}>
          Desc. manual
        </span>
      )}
      {tieneRecargoManual(venta) && (
        <span className="badge text-bg-info ms-1" title={`Recargo manual: ${formatearMoneda(venta.recargoManualTotal)}`}>
          Recargo
        </span>
      )}
    </>
  )
}

export type PropsModalDetalle = {
  comprobante: ComprobanteEmitido | null
  /** `ComprobanteEmitido` no lleva el nombre del cliente (solo `idCliente`) — se toma de la fila
   * ya cargada del listado (`ventaEnDetalle.nombreCliente`), disponible desde que se abre el
   * modal, sin esperar el `GET /api/ventas/{id}`. */
  nombreCliente: string | undefined
  cargando: boolean
  error: string
  medios: MedioPagoListado[]
  puedeReimprimirAca: boolean
  reimprimiendo: boolean
  errorReimprimir: string
  /** Botón que abrió el modal, capturado síncronamente en `VentasDelTurno.abrirDetalle` (nunca
   * leído acá con `document.activeElement`: react-async-state regla 12). El foco se restaura en
   * el cleanup del efecto de abajo — mismo criterio que `ConfirmacionDeBaja`: para cuando ese
   * cleanup corre, el commit que sacó `disabled` del disparador ya se aplicó, así que
   * `esAlcanzable` ve el DOM al día (un `.focus()` síncrono en el handler de cierre vería
   * todavía el `disabled` del render anterior). */
  disparador: HTMLElement | null
  onReimprimir: () => void
  onCerrar: () => void
}

/**
 * Modal de detalle de una venta, montado sobre `Modal`: Escape/X/"Cerrar" cierran.
 * `restaurarFoco={false}` porque el disparador sigue deshabilitado mientras corre la restauración
 * de `Modal` (fase de layout del commit de cierre); la de acá corre después de ese commit.
 */
export function ModalDetalleDeVenta({
  comprobante,
  nombreCliente,
  cargando,
  error,
  medios,
  puedeReimprimirAca,
  reimprimiendo,
  errorReimprimir,
  disparador,
  onReimprimir,
  onCerrar,
}: PropsModalDetalle) {
  // Restaura el foco al desmontar (cierre por Escape/X/"Cerrar") — ver el doc-comment de
  // `disparador` arriba. Un efecto normal (no layout): el commit que quitó `disabled` del
  // disparador corre ANTES de este cleanup, nunca al revés.
  useEffect(() => {
    return () => {
      if (esAlcanzable(disparador)) disparador.focus()
    }
  }, [disparador])

  const ordenDeItems = useOrdenamiento(comprobante?.items ?? ITEMS_SIN_DATOS, COLUMNAS_DE_ITEMS)
  const columnasDePagos = useMemo<ColumnasOrdenables<PagoDelComprobante>>(
    () => ({
      medio: { valor: (p) => nombreDeMedio(p.idMedioPago, medios) },
      importe: { valor: (p) => p.importe },
      referencia: { valor: (p) => p.referencia },
      vuelto: { valor: (p) => (p.vuelto > 0 ? p.vuelto : null) },
    }),
    [medios],
  )
  const ordenDePagos = useOrdenamiento(comprobante?.pagos ?? PAGOS_SIN_DATOS, columnasDePagos)

  const titulo = comprobante ? `Detalle de la venta ${comprobante.numeroVisible}` : 'Detalle de la venta'

  return (
    <Modal
      titulo={titulo}
      tamano="lg"
      restaurarFoco={false}
      etiquetaCerrar="Cerrar detalle"
      onCerrar={onCerrar}
      pie={
        <>
          {puedeReimprimirAca && (
            <button
              type="button"
              className="btn btn-outline-secondary d-inline-flex align-items-center gap-2"
              aria-busy={reimprimiendo || undefined}
              disabled={cargando || reimprimiendo}
              onClick={onReimprimir}
            >
              <Icono icono="imprimir" />
              Imprimir
            </button>
          )}
          <button type="button" className="btn btn-secondary" onClick={onCerrar}>
            Cerrar
          </button>
        </>
      }
    >
      {cargando && <Cargando texto="Cargando detalle…" />}
      {!cargando && error && <div className="alert alert-danger">{error}</div>}

      {!cargando && !error && comprobante && (
        <>
          <dl className="row mb-3">
            <dt className="col-3">Fecha</dt>
            <dd className="col-9">{formatearFechaHora(comprobante.fecha)}</dd>
            <dt className="col-3">Cliente</dt>
            <dd className="col-9">{nombreCliente ?? '—'}</dd>
            <dt className="col-3">Estado</dt>
            <dd className="col-9">
              <span className={`badge ${claseDeBadgeDeEstadoVenta(comprobante.estado)}`}>
                {etiquetaDeEstadoVenta(comprobante.estado)}
              </span>
            </dd>
          </dl>

          <div className="table-responsive">
            <table className="table table-sm table-bordered align-middle">
              <thead>
                <tr>
                  <EncabezadoOrdenable columna="descripcion" orden={ordenDeItems.orden} onAlternar={ordenDeItems.alternar}>
                    Descripción
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="cantidad" orden={ordenDeItems.orden} onAlternar={ordenDeItems.alternar} className="text-end">
                    Cantidad
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="precio" orden={ordenDeItems.orden} onAlternar={ordenDeItems.alternar} className="text-end">
                    Precio unit.
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="descuento" orden={ordenDeItems.orden} onAlternar={ordenDeItems.alternar} className="text-end">
                    Descuento
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="ajuste" orden={ordenDeItems.orden} onAlternar={ordenDeItems.alternar}>
                    Ajuste manual
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="total" orden={ordenDeItems.orden} onAlternar={ordenDeItems.alternar} className="text-end">
                    Total
                  </EncabezadoOrdenable>
                </tr>
              </thead>
              <tbody>
                {ordenDeItems.filas.map((item) => (
                  <tr key={item.orden}>
                    <td>
                      {item.descripcion}
                      {item.codigoLote && <span className="text-muted"> — Lote {item.codigoLote}</span>}
                    </td>
                    <td className="text-end">{item.cantidad}</td>
                    <td className="text-end">{formatearMoneda(item.precioUnitario)}</td>
                    <td className="text-end">{formatearMoneda(item.descuento)}</td>
                    <td>
                      {item.ajusteManualPorcentaje != null
                        ? `${rotuloDeAjusteManual(item.ajusteManualPorcentaje)} ${formatearPorcentajeDeAjuste(item.ajusteManualPorcentaje)}% ${formatearImporteConSigno(item.ajusteManual, { simbolo: true })}`
                        : '—'}
                    </td>
                    <td className="text-end">{formatearMoneda(item.total)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <div className="table-responsive">
            <table className="table table-sm table-bordered align-middle">
              <thead>
                <tr>
                  <EncabezadoOrdenable columna="medio" orden={ordenDePagos.orden} onAlternar={ordenDePagos.alternar}>
                    Medio
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="importe" orden={ordenDePagos.orden} onAlternar={ordenDePagos.alternar} className="text-end">
                    Importe
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="referencia" orden={ordenDePagos.orden} onAlternar={ordenDePagos.alternar}>
                    Referencia
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="vuelto" orden={ordenDePagos.orden} onAlternar={ordenDePagos.alternar} className="text-end">
                    Vuelto
                  </EncabezadoOrdenable>
                </tr>
              </thead>
              <tbody>
                {ordenDePagos.filas.map((pago, indice) => (
                  <tr key={`${pago.idMedioPago}-${indice}`}>
                    <td>{nombreDeMedio(pago.idMedioPago, medios)}</td>
                    <td className="text-end">{formatearMoneda(pago.importe)}</td>
                    <td>{pago.referencia ?? '—'}</td>
                    <td className="text-end">{pago.vuelto > 0 ? formatearMoneda(pago.vuelto) : '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <div className="d-flex justify-content-end gap-4 small">
            <span>Subtotal: {formatearMoneda(comprobante.subtotal)}</span>
            <span>Descuento: {formatearMoneda(comprobante.descuentoTotal)}</span>
            {(comprobante.descuentoManualTotal ?? 0) !== 0 && (
              <span>Desc. manual: {formatearImporteConSigno(-comprobante.descuentoManualTotal, { simbolo: true })}</span>
            )}
            {(comprobante.recargoManualTotal ?? 0) !== 0 && (
              <span>Recargo: {formatearImporteConSigno(comprobante.recargoManualTotal, { simbolo: true })}</span>
            )}
            <strong>Total: {formatearMoneda(comprobante.total)}</strong>
          </div>

          {errorReimprimir && <div className="alert alert-danger mt-3 mb-0 py-1 px-2 small">{errorReimprimir}</div>}
        </>
      )}
    </Modal>
  )
}
