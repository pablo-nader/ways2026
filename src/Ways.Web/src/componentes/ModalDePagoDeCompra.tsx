import { useEffect, useRef, useState } from 'react'
import { clienteDeCatalogo } from '../api/catalogos'
import { ErrorApi } from '../api/cliente'
import {
  clienteDeCompras,
  diferenciaDelPago,
  fechaDeHoyParaPago,
  pagosAplicablesVisibles,
  prepararPagoDeCompra,
  sePuedeCerrarEnCero,
} from '../api/compras'
import { lineaDePagoVacia, totalDeLineasDePago, type LineaDePagoEditable } from '../api/lineasDePago'
import type { MedioPagoAlta, MedioPagoListado, PagoAplicable, ResultadoDePagoDeCompra } from '../api/tipos'
import { formatearImporte, redondearImporte } from '../formato/importes'
import { CampoImporte } from './CampoImporte'
import { EditorDeLineasDePago } from './EditorDeLineasDePago'
import { Modal } from './Modal'
import { CampoFecha } from './CampoFecha'

const clienteMediosPago = clienteDeCatalogo<MedioPagoListado, MedioPagoAlta>('medios-pago')

const CLAVE_DE_LA_LINEA_PRECARGADA = 1

type Props = {
  idCompra: number
  /** Cómo se nombra la compra en pantalla: su número externo o `#id`. */
  etiquetaDeLaCompra: string
  saldoPendiente: number
  onCerrar: () => void
  /** Se llama una sola vez con el pago ya registrado; quien abrió el modal lo cierra y refresca. */
  onPagado: (resultado: ResultadoDePagoDeCompra) => void
}

/**
 * Pago de una compra confirmada, compartido por el detalle de la compra y la cuenta corriente del
 * proveedor. El modal es dueño de su escritura: los dos llamadores solo lo abren y, con el pago ya
 * registrado, lo cierran y refrescan su pantalla.
 *
 * `react-async-state`: guard de reentrancia de primera línea (ref, cubre el doble click del mismo
 * tick) más ventana completa deshabilitada y cierre bloqueado mientras `pagando`; el medio de pago se
 * carga con un flag de vigencia y el botón queda inerte hasta que llegan los medios; un pago que
 * resuelve con el modal ya desmontado no llama a `onPagado` dos veces ni toca estado. Un pago nunca se
 * reintenta solo: ante un error (incluido el resultado incierto) el operador decide.
 *
 * Se puede pagar con crédito que ya existe (pagos a cuenta tildados, cada uno con el importe a aplicar) y con dinero
 * nuevo (las líneas por medio), que puede superar el saldo: el excedente queda a favor en el pago nuevo. Si hay pagos
 * tildados, las líneas vacías (sin medio ni importe) se descartan y no se paga dinero nuevo; un importe tipeado sin medio es
 * un error, nunca se descarta en silencio. El importe de la línea precargada sigue a lo que resta de pagar mientras
 * quien paga no lo edite; si lo aplicado cubre el saldo queda vacía y no se pueden tildar más pagos. La lista de pagos aplicables es una lectura
 * con flag de vigencia; si falla, el modal sigue sirviendo para pagar solo con líneas.
 *
 * «Cerrar en cero»: la tolerancia de redondeo llega con esa misma lectura (sin ella no se ofrece). El tilde solo se puede
 * usar mientras la diferencia (saldo − pagos a cuenta − dinero nuevo) no sea cero y no supere la tolerancia; si el usuario
 * cambia los importes y deja de entrar, el tilde queda sin efecto y no se envía. Es del servidor la última palabra.
 */
export function ModalDePagoDeCompra({ idCompra, etiquetaDeLaCompra, saldoPendiente, onCerrar, onPagado }: Props) {
  const [hoy] = useState(() => fechaDeHoyParaPago())
  const [medios, setMedios] = useState<MedioPagoListado[] | null>(null)
  const [errorMedios, setErrorMedios] = useState('')

  const [aplicables, setAplicables] = useState<PagoAplicable[] | null>(null)
  const [errorAplicables, setErrorAplicables] = useState('')
  const [tolerancia, setTolerancia] = useState<number | null>(null)
  const [cerrarEnCero, setCerrarEnCero] = useState(false)
  // idPago -> importe a aplicar; la presencia de la clave es el tilde.
  const [seleccion, setSeleccion] = useState<Record<number, number | null>>({})
  const [incluirSinProveedor, setIncluirSinProveedor] = useState(false)

  const [lineas, setLineas] = useState<LineaDePagoEditable[]>(() => [lineaDePagoVacia(CLAVE_DE_LA_LINEA_PRECARGADA, saldoPendiente)])
  // Mientras quien paga no edite el importe de la línea precargada, ese importe sigue a lo que resta de pagar.
  const [importeTocado, setImporteTocado] = useState(false)
  const [fecha, setFecha] = useState(hoy)
  const [concepto, setConcepto] = useState('')

  const [pagando, setPagando] = useState(false)
  const pagandoRef = useRef(false)
  const [error, setError] = useState('')
  const vigenteRef = useRef(true)

  useEffect(() => {
    vigenteRef.current = true
    return () => {
      vigenteRef.current = false
    }
  }, [])

  useEffect(() => {
    let vigente = true

    clienteMediosPago
      .listar(false)
      .then((lista) => {
        if (!vigente) return
        // La cuenta corriente es el crédito del cliente en una venta, nunca una salida real de dinero.
        setMedios(lista.filter((m) => m.comportamiento !== 'CuentaCorriente'))
      })
      .catch((e) => {
        if (!vigente) return
        setMedios([])
        setErrorMedios(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los medios de pago.')
      })

    return () => {
      vigente = false
    }
  }, [])

  useEffect(() => {
    let vigente = true

    clienteDeCompras
      .obtenerPagosAplicables(idCompra)
      .then((respuesta) => {
        if (!vigente) return
        setAplicables(respuesta.pagos)
        setTolerancia(respuesta.toleranciaRedondeo ?? null)
      })
      .catch((e) => {
        if (!vigente) return
        setAplicables([])
        setErrorAplicables(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los pagos a cuenta.')
      })

    return () => {
      vigente = false
    }
  }, [idCompra])

  const hayPagosSinProveedor = (aplicables ?? []).some((p) => p.idProveedor === null)
  const visibles = pagosAplicablesVisibles(aplicables ?? [], incluirSinProveedor, seleccion)

  const totalAplicado = redondearImporte(Object.values(seleccion).reduce<number>((suma, importe) => suma + (importe ?? 0), 0))
  const restante = Math.max(0, redondearImporte(saldoPendiente - totalAplicado))
  const lineasVigentes = importeTocado
    ? lineas
    : lineas.map((l) =>
        l.clave === CLAVE_DE_LA_LINEA_PRECARGADA ? { ...l, importe: restante > 0 ? restante : null } : l,
      )
  const hayDineroNuevo = lineasVigentes.some((l) => l.idMedioPago !== '')
  const excedente = hayDineroNuevo ? redondearImporte(totalDeLineasDePago(lineasVigentes) - restante) : 0
  // Sin pagos a cuenta tildados todas las líneas cuentan (la precargada también, aunque falte elegir el medio); con ellos, las
  // líneas sin medio se descartan y no son dinero nuevo.
  const hayPagosATildados = Object.keys(seleccion).length > 0
  const diferencia = diferenciaDelPago(
    saldoPendiente,
    totalAplicado,
    hayPagosATildados && !hayDineroNuevo ? 0 : totalDeLineasDePago(lineasVigentes),
  )
  const puedeCerrarEnCero = sePuedeCerrarEnCero(diferencia, tolerancia)
  const cierraEnCero = cerrarEnCero && puedeCerrarEnCero

  function cambiarLineas(reductor: (prev: LineaDePagoEditable[]) => LineaDePagoEditable[]) {
    const despues = reductor(lineasVigentes)
    const importeAntes = lineasVigentes.find((l) => l.clave === CLAVE_DE_LA_LINEA_PRECARGADA)?.importe
    const importeDespues = despues.find((l) => l.clave === CLAVE_DE_LA_LINEA_PRECARGADA)?.importe
    if (importeAntes !== importeDespues) setImporteTocado(true)
    setLineas(despues)
  }

  function alternarPago(pago: PagoAplicable, marcado: boolean) {
    setSeleccion((prev) => {
      const resto = { ...prev }
      delete resto[pago.idPago]
      if (!marcado) return resto
      const yaAplicado = Object.values(resto).reduce<number>((suma, importe) => suma + (importe ?? 0), 0)
      const libre = Math.max(0, redondearImporte(saldoPendiente - yaAplicado))
      return { ...resto, [pago.idPago]: Math.min(pago.disponible, libre) }
    })
  }

  async function pagar() {
    if (pagandoRef.current) return

    const aplicaciones = Object.entries(seleccion).map(([idPago, importe]) => ({ idPago: Number(idPago), importe: importe ?? 0 }))
    const excedido = (aplicables ?? []).find((p) => (seleccion[p.idPago] ?? 0) > p.disponible)
    if (excedido) {
      setError(`El pago #${excedido.idPago} tiene disponible ${formatearImporte(excedido.disponible, { simbolo: true })}.`)
      return
    }

    const preparado = prepararPagoDeCompra(
      { lineas: lineasVigentes, fecha, concepto, aplicaciones, cerrarEnCero: cierraEnCero },
      saldoPendiente,
      hoy,
    )
    if ('error' in preparado) {
      setError(preparado.error)
      return
    }

    pagandoRef.current = true
    setPagando(true)
    setError('')

    try {
      const resultado = await clienteDeCompras.pagar(idCompra, preparado.solicitud)
      if (vigenteRef.current) onPagado(resultado)
    } catch (e) {
      pagandoRef.current = false
      if (!vigenteRef.current) return
      setPagando(false)
      setError(e instanceof ErrorApi ? e.message : 'No se pudo registrar el pago.')
    }
  }

  const mediosCargando = medios === null || aplicables === null
  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCerrar} disabled={pagando}>
        Cancelar
      </button>
      <button type="button" className="btn btn-primary" onClick={() => void pagar()} disabled={pagando || mediosCargando}>
        {pagando ? 'Registrando…' : 'Registrar pago'}
      </button>
    </>
  )

  return (
    <Modal titulo={`Pagar compra ${etiquetaDeLaCompra}`} tamano="xl" desplazable ocupado={pagando} pie={pie} onCerrar={onCerrar}>
      {(error || errorMedios) && <div className="alert alert-danger py-2 px-3 small">{error || errorMedios}</div>}

      <div className="small text-muted mb-3">
        Saldo pendiente de la compra: {formatearImporte(saldoPendiente, { simbolo: true })}
      </div>

      <fieldset disabled={pagando} className="row g-2 border-0 p-0 m-0">
        <div className="col-md-6">
          <label className="form-label" htmlFor="pago-compra-fecha">
            Fecha
          </label>
          <CampoFecha
            id="pago-compra-fecha"
            className="form-control"
            value={fecha}
            max={hoy}
            onChange={(valor) => setFecha(valor)}
          />
        </div>

        <div className="col-md-6">
          <label className="form-label" htmlFor="pago-compra-concepto">
            Concepto (opcional)
          </label>
          <input
            id="pago-compra-concepto"
            type="text"
            className="form-control"
            value={concepto}
            onChange={(e) => setConcepto(e.target.value)}
          />
          <div className="form-text">Si queda vacío se registra como «Pago &lt;tipo&gt; &lt;número&gt;».</div>
        </div>

        {errorAplicables && <div className="col-12 small text-warning">{errorAplicables}</div>}

        {aplicables !== null && aplicables.length > 0 && (
          <div className="col-12">
            <div className="d-flex flex-wrap justify-content-between align-items-center gap-2 mb-1">
              <div className="fw-semibold">Usar pagos a cuenta</div>
              {hayPagosSinProveedor && (
                <div className="form-check mb-0">
                  <input
                    id="pago-compra-incluir-sin-proveedor"
                    type="checkbox"
                    className="form-check-input"
                    checked={incluirSinProveedor}
                    onChange={(e) => setIncluirSinProveedor(e.target.checked)}
                  />
                  <label className="form-check-label" htmlFor="pago-compra-incluir-sin-proveedor">
                    Incluir pagos sin proveedor
                  </label>
                </div>
              )}
            </div>
            <div className="table-responsive border rounded" style={{ maxHeight: 'min(18rem, 35vh)', overflowY: 'auto' }}>
              <table className="table table-sm table-hover align-middle mb-0">
                <thead className="sticky-top">
                  <tr>
                    <th scope="col" style={{ width: '2.5rem' }}>
                      <span className="visually-hidden">Usar</span>
                    </th>
                    <th scope="col">Pago</th>
                    <th scope="col">Fecha</th>
                    <th scope="col">Concepto</th>
                    <th scope="col" className="text-end">
                      Disponible
                    </th>
                    <th scope="col" style={{ width: '11rem' }}>
                      A aplicar
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {visibles.length === 0 && (
                    <tr>
                      <td colSpan={6} className="text-muted small">
                        No hay pagos a cuenta de este proveedor.
                      </td>
                    </tr>
                  )}
                  {visibles.map((pago) => {
                    const marcado = pago.idPago in seleccion
                    return (
                      <tr key={pago.idPago}>
                        <td>
                          <input
                            type="checkbox"
                            className="form-check-input"
                            aria-label={`Usar el pago #${pago.idPago}`}
                            checked={marcado}
                            disabled={!marcado && restante === 0}
                            onChange={(e) => alternarPago(pago, e.target.checked)}
                          />
                        </td>
                        <td className="text-nowrap">#{pago.idPago}</td>
                        <td className="text-nowrap">{new Date(pago.fecha).toLocaleDateString('es-AR')}</td>
                        <td>
                          {pago.concepto}
                          {pago.idProveedor === null && <span className="badge text-bg-secondary ms-2">Sin proveedor</span>}
                        </td>
                        <td className="text-end text-nowrap">{formatearImporte(pago.disponible, { simbolo: true })}</td>
                        <td>
                          <CampoImporte
                            aria-label={`Importe a aplicar del pago ${pago.idPago}`}
                            className="form-control form-control-sm"
                            valor={marcado ? seleccion[pago.idPago] : null}
                            disabled={!marcado}
                            onChange={(importe) => setSeleccion((prev) => ({ ...prev, [pago.idPago]: importe }))}
                          />
                        </td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
            {totalAplicado > 0 && (
              <div className="form-text">
                Se aplican {formatearImporte(totalAplicado, { simbolo: true })} de pagos a cuenta. Una línea sin medio ni
                importe no paga dinero nuevo.
              </div>
            )}
          </div>
        )}

        <div className="col-12">
          <EditorDeLineasDePago
            lineas={lineasVigentes}
            medios={medios ?? []}
            onCambio={cambiarLineas}
            objetivo={excedente > 0 ? null : restante}
          />
          {excedente > 0 && !cierraEnCero && (
            <div className="small text-info mt-1">Quedan {formatearImporte(excedente, { simbolo: true })} a cuenta</div>
          )}
        </div>

        {tolerancia !== null && (
          <div className="col-12">
            <div className="form-check">
              <input
                id="pago-compra-cerrar-en-cero"
                type="checkbox"
                className="form-check-input"
                checked={cierraEnCero}
                disabled={!puedeCerrarEnCero}
                onChange={(e) => setCerrarEnCero(e.target.checked)}
              />
              <label className="form-check-label" htmlFor="pago-compra-cerrar-en-cero">
                Cerrar en cero
              </label>
            </div>
            <div className="form-text">
              {puedeCerrarEnCero
                ? diferencia > 0
                  ? `Se perdonan ${formatearImporte(diferencia, { simbolo: true })} y la compra queda paga.`
                  : `Se absorben ${formatearImporte(-diferencia, { simbolo: true })} del pago: no queda nada a favor.`
                : `Disponible cuando falta o sobra hasta ${formatearImporte(tolerancia, { simbolo: true })}.`}
            </div>
          </div>
        )}
      </fieldset>
    </Modal>
  )
}
