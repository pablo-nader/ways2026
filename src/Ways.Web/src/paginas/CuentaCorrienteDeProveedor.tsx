import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useLocation, useParams } from 'react-router'
import { ErrorApi } from '../api/cliente'
import { saldoResultanteDeAjuste, validarAjusteLocal } from '../api/cuentaCorriente'
import {
  aSolicitudDeAjusteDeProveedor,
  clienteDeCuentaCorrienteDeProveedor,
  esSaldoAFavor,
  etiquetaDeTipoDeMovimiento,
  filtrosDeEstadoDeCuentaDeProveedorVacios,
  referenciaDeMovimiento,
  type FiltrosDeEstadoDeCuentaDeProveedor,
} from '../api/cuentaCorrienteDeProveedor'
import { clienteDeOrganizacion } from '../api/organizacion'
import { clienteDeProveedores } from '../api/proveedores'
import type {
  MovimientoDeCuentaDeProveedor,
  PaginaDeEstadoDeCuentaDeProveedor,
  ProveedorListado,
  PuntoVentaListado,
} from '../api/tipos'
import { puedeSupervisarCuentaDeProveedor } from '../api/tipos'
import { useAuth } from '../auth/useAuth'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'

function formatearMoneda(valor: number): string {
  const signo = valor < 0 ? '-' : ''
  return `${signo}$${Math.abs(valor).toLocaleString('es-AR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
}

function formatearFechaHora(iso: string): string {
  return new Date(iso).toLocaleString('es-AR')
}

/** Saldo con la etiqueta "saldo a favor" cuando es negativo (design: Web Composition — columnas,
 * proposal decisión 5), nunca clampeado a cero — mismo criterio que `ResumenSaldoDeProveedor`.
 * Aplica tanto al saldo del header como a `saldoResultante` de cada fila. */
function formatearSaldoConEtiqueta(valor: number): string {
  return esSaldoAFavor(valor) ? `${formatearMoneda(valor)} (saldo a favor)` : formatearMoneda(valor)
}

type PropsModalAjuste = {
  idProveedor: number
  puntosVenta: PuntoVentaListado[]
  saldoActual: number
  onCerrar: () => void
  onAntesDeEscribir: () => void
  onRegistrado: (movimiento: MovimientoDeCuentaDeProveedor) => void
}

/**
 * Modal de ajuste manual del ledger de proveedores (Slice 6, design: Web Composition — droppable
 * pre-autorizado si la slice desborda, tasks.md decisión 3: la pantalla de lectura NUNCA se
 * degrada, este modal es lo primero que cae). Mismo shape que `ModalAjusteDeCuenta`
 * (`CuentaCorriente.tsx`, cliente, stage 7) — reusa `ReglaDeAjusteDeCuenta.Validar` server-side
 * (design decisión 13), así que reusa también su mirror local ya probado (`validarAjusteLocal`,
 * `saldoResultanteDeAjuste`, `cuentaCorriente.ts`) en vez de duplicarlo.
 *
 * `react-async-state`: regla 9 (guard de reentrancia de primera línea + deshabilitado de ventana
 * completa mientras `registrando`), regla 3 (el llamador bumpea la generación del ledger ANTES de
 * este POST, vía `onAntesDeEscribir`), regla 6 (el refetch posterior vive en el padre, aislado de
 * este try/catch). Sin turno (design decisión 14 — "provenance, not authority", mismo criterio que
 * el ajuste de clientes): este endpoint nunca llama a `ServicioDeTurnos`, así que no hay ningún
 * `turno_no_abierto` que recuperar acá (rule 10 sweep).
 */
function ModalAjusteDeProveedor({ idProveedor, puntosVenta, saldoActual, onCerrar, onAntesDeEscribir, onRegistrado }: PropsModalAjuste) {
  const [idPuntoVenta, setIdPuntoVenta] = useState<number>(puntosVenta[0].id)
  const [importe, setImporte] = useState('')
  const [detalle, setDetalle] = useState('')

  const [registrando, setRegistrando] = useState(false)
  const registrandoRef = useRef(false)
  const [error, setError] = useState('')

  const importeNumerico = importe.trim() === '' ? Number.NaN : Number(importe)
  const saldoResultante = Number.isFinite(importeNumerico) ? saldoResultanteDeAjuste(saldoActual, importeNumerico) : null

  async function registrarAjuste() {
    // regla 9: guard de reentrancia de primera línea — dos defensas complementarias contra el
    // doble click: este ref cubre la ventana same-tick, antes de que React re-renderice con
    // `disabled`; el atributo `disabled` cubre el resto de la ventana mientras `registrando` es true.
    if (registrandoRef.current) return

    const rechazo = validarAjusteLocal({ importe: importeNumerico, detalle })
    if (rechazo) {
      setError(rechazo.mensaje)
      return
    }

    registrandoRef.current = true
    setRegistrando(true)
    setError('')

    // regla 3: bumpear la generación del ledger ANTES de la escritura.
    onAntesDeEscribir()

    try {
      const solicitud = aSolicitudDeAjusteDeProveedor(idPuntoVenta, importeNumerico, detalle)
      const movimiento = await clienteDeCuentaCorrienteDeProveedor.registrarAjuste(idProveedor, solicitud)
      registrandoRef.current = false
      setRegistrando(false)
      // regla 6: el refetch del ledger vive en el padre, aislado de este try/catch.
      onRegistrado(movimiento)
    } catch (e) {
      registrandoRef.current = false
      setRegistrando(false)
      setError(e instanceof ErrorApi ? e.message : 'No se pudo registrar el ajuste.')
    }
  }

  return (
    <>
      <div className="modal d-block" tabIndex={-1} role="dialog">
        <div className="modal-dialog" role="document">
          <div className="modal-content rounded-0">
            <div className="modal-header">
              <h5 className="modal-title">Ajuste manual de cuenta corriente</h5>
            </div>
            <div className="modal-body">
              {error && <div className="alert alert-danger rounded-0 py-1 px-2 small">{error}</div>}

              <div className="mb-3" style={{ maxWidth: 320 }}>
                <label className="form-label" htmlFor="ccp-ajuste-punto-venta">
                  Punto de venta
                </label>
                <select
                  id="ccp-ajuste-punto-venta"
                  className="form-select rounded-0"
                  value={idPuntoVenta}
                  disabled={registrando}
                  onChange={(e) => setIdPuntoVenta(Number(e.target.value))}
                >
                  {puntosVenta.map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.nombre}
                    </option>
                  ))}
                </select>
              </div>

              <div className="mb-3">
                <label className="form-label" htmlFor="ccp-ajuste-importe">
                  Importe
                </label>
                <input
                  id="ccp-ajuste-importe"
                  type="number"
                  step="0.01"
                  className="form-control rounded-0"
                  value={importe}
                  disabled={registrando}
                  onChange={(e) => setImporte(e.target.value)}
                />
                <div className="form-text">
                  Positivo aumenta la deuda del proveedor, negativo la reduce. Nunca puede ser cero.
                </div>
              </div>

              <div className="mb-3">
                <label className="form-label" htmlFor="ccp-ajuste-detalle">
                  Detalle (obligatorio)
                </label>
                <input
                  id="ccp-ajuste-detalle"
                  type="text"
                  className="form-control rounded-0"
                  value={detalle}
                  disabled={registrando}
                  onChange={(e) => setDetalle(e.target.value)}
                />
              </div>

              <div className="small text-muted">Saldo actual: {formatearSaldoConEtiqueta(saldoActual)}</div>
              {saldoResultante !== null && <div className="fs-6">Saldo resultante: {formatearSaldoConEtiqueta(saldoResultante)}</div>}
            </div>
            <div className="modal-footer">
              <button type="button" className="btn btn-outline-secondary rounded-0" disabled={registrando} onClick={onCerrar}>
                Cancelar
              </button>
              <button type="button" className="btn btn-primary rounded-0" disabled={registrando} onClick={registrarAjuste}>
                {registrando ? 'Registrando…' : 'Registrar ajuste'}
              </button>
            </div>
          </div>
        </div>
      </div>
      <div className="modal-backdrop show" />
    </>
  )
}

type PropsPantalla = {
  idProveedor: number
  proveedorInfo: ProveedorListado | null
  cargandoProveedor: boolean
  errorProveedor: string
  puntosVenta: PuntoVentaListado[] | null
  errorPuntosVenta: string
}

/** Remontada por `key={idProveedor}` (regla 8) — ningún estado de acá (filtros, página, modal de
 * ajuste) sobrevive a un cambio de proveedor. */
function PantallaCuentaCorrienteDeProveedor({
  idProveedor,
  proveedorInfo,
  cargandoProveedor,
  errorProveedor,
  puntosVenta,
  errorPuntosVenta,
}: PropsPantalla) {
  const [filtros, setFiltros] = useState<FiltrosDeEstadoDeCuentaDeProveedor>(filtrosDeEstadoDeCuentaDeProveedorVacios())

  const [pagina, setPagina] = useState<PaginaDeEstadoDeCuentaDeProveedor | null>(null)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const generacionRef = useRef(0)

  const [modalAjusteAbierto, setModalAjusteAbierto] = useState(false)
  const [aviso, setAviso] = useState('')

  const { usuario } = useAuth()
  const esSupervisorOAdmin = usuario !== null && puedeSupervisarCuentaDeProveedor(usuario.rolId)

  // regla 2: cada cambio de filtro/página dispara una nueva consulta — una respuesta
  // desactualizada nunca puede pisar la más reciente.
  const cargar = useCallback(() => {
    const miGeneracion = (generacionRef.current += 1)
    setCargando(true)
    setError('')

    clienteDeCuentaCorrienteDeProveedor
      .obtenerEstado(idProveedor, filtros)
      .then((datos) => {
        if (generacionRef.current !== miGeneracion) return
        setPagina(datos)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setPagina(null)
        setError(e instanceof ErrorApi ? e.message : 'No se pudo cargar el estado de cuenta.')
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargando(false)
      })
  }, [idProveedor, filtros])

  useEffect(() => {
    cargar()
  }, [cargar])

  function cambiarFiltro(cambios: Partial<Omit<FiltrosDeEstadoDeCuentaDeProveedor, 'pagina' | 'tamanio'>>) {
    setFiltros((prev) => ({ ...prev, ...cambios, pagina: 1 }))
  }

  function cambiarPagina(delta: number) {
    setFiltros((prev) => ({ ...prev, pagina: Math.max(1, prev.pagina + delta) }))
  }

  const totalPaginas = pagina ? Math.max(1, Math.ceil(pagina.total / pagina.tamanio)) : 1

  // El ajuste manual no necesita medios de pago (no mueve plata física), pero sí el proveedor
  // confirmado y al menos un punto de venta — mismo criterio fail-closed que
  // `puedeIngresarPago`/`puedeSupervisarCC` de `CuentaCorriente.tsx` (regla 7: un proveedor NUNCA
  // verificado no puede habilitar el ajuste).
  const puedeAjustar =
    esSupervisorOAdmin &&
    !cargandoProveedor &&
    proveedorInfo !== null &&
    errorProveedor === '' &&
    puntosVenta !== null &&
    errorPuntosVenta === '' &&
    puntosVenta.length > 0

  let motivoBloqueoAjuste: string | undefined
  if (cargandoProveedor) {
    motivoBloqueoAjuste = 'Cargando los datos del proveedor…'
  } else if (errorProveedor) {
    motivoBloqueoAjuste = 'No se pudo confirmar el proveedor — no se puede continuar hasta que esto se resuelva.'
  } else if (!puedeAjustar) {
    motivoBloqueoAjuste = 'No se pudieron cargar los datos necesarios para registrar un ajuste.'
  }

  return (
    <div className="container-fluid py-4">
      <Box
        titulo={`Estado de cuenta — ${proveedorInfo?.razonSocial ?? `Proveedor #${idProveedor}`}`}
        herramientas={
          <Link className="btn btn-sm btn-outline-light rounded-0" to="/proveedores">
            Volver a proveedores
          </Link>
        }
      >
        {aviso && <div className="alert alert-success rounded-0">{aviso}</div>}
        {error && <div className="alert alert-danger rounded-0">{error}</div>}
        {(errorProveedor || errorPuntosVenta) && (
          <div className="alert alert-warning rounded-0 py-1 px-2 small">
            {errorProveedor || errorPuntosVenta} No se puede registrar un ajuste hasta que esto se resuelva.
          </div>
        )}

        {cargando && !pagina && <Cargando />}

        {pagina && (
          <>
            <div className="row g-3 mb-3 align-items-end">
              <div className="col-md-4">
                <div className="small text-muted">Saldo</div>
                <div className="fs-5">{formatearSaldoConEtiqueta(pagina.header.saldo)}</div>
              </div>
              {esSupervisorOAdmin && (
                <div className="col-md-8 text-md-end">
                  <button
                    type="button"
                    className="btn btn-outline-secondary rounded-0"
                    disabled={!puedeAjustar}
                    title={motivoBloqueoAjuste}
                    onClick={() => {
                      setAviso('')
                      setModalAjusteAbierto(true)
                    }}
                  >
                    Ajuste manual
                  </button>
                </div>
              )}
            </div>

            <div className="row g-2 align-items-end mb-3">
              <div className="col-md-3">
                <label className="form-label" htmlFor="ccp-filtro-desde">
                  Desde
                </label>
                <input
                  id="ccp-filtro-desde"
                  type="date"
                  className="form-control rounded-0"
                  value={filtros.desde}
                  disabled={filtros.historico}
                  onChange={(e) => cambiarFiltro({ desde: e.target.value })}
                />
              </div>
              <div className="col-md-3">
                <label className="form-label" htmlFor="ccp-filtro-hasta">
                  Hasta
                </label>
                <input
                  id="ccp-filtro-hasta"
                  type="date"
                  className="form-control rounded-0"
                  value={filtros.hasta}
                  disabled={filtros.historico}
                  onChange={(e) => cambiarFiltro({ hasta: e.target.value })}
                />
              </div>
              <div className="col-md-3">
                <div className="form-check">
                  <input
                    id="ccp-filtro-historico"
                    type="checkbox"
                    className="form-check-input rounded-0"
                    checked={filtros.historico}
                    onChange={(e) => cambiarFiltro({ historico: e.target.checked })}
                  />
                  <label className="form-check-label" htmlFor="ccp-filtro-historico">
                    Ver histórico completo
                  </label>
                </div>
              </div>
            </div>

            {cargando && <p className="text-muted">Actualizando…</p>}

            <div className="table-responsive">
              <table className="table table-sm table-striped table-bordered align-middle">
                <thead>
                  <tr>
                    <th>Fecha</th>
                    <th>Tipo</th>
                    <th>Comprobante/Gasto</th>
                    <th>Detalle</th>
                    <th className="text-end">Importe</th>
                    <th className="text-end">Saldo resultante</th>
                  </tr>
                </thead>
                <tbody>
                  {pagina.items.map((m) => (
                    <tr key={m.idMovimiento}>
                      <td>{formatearFechaHora(m.fecha)}</td>
                      <td>{etiquetaDeTipoDeMovimiento(m)}</td>
                      <td>{referenciaDeMovimiento(m)}</td>
                      <td>{m.detalle ?? '—'}</td>
                      <td className="text-end">{formatearMoneda(m.importe)}</td>
                      <td className="text-end">{formatearSaldoConEtiqueta(m.saldoResultante)}</td>
                    </tr>
                  ))}
                  {pagina.items.length === 0 && (
                    <tr>
                      <td colSpan={6} className="text-center text-muted py-4">
                        No hay movimientos en el período seleccionado.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>

            <div className="d-flex justify-content-between align-items-center">
              <span className="small text-muted">
                Página {pagina.pagina} de {totalPaginas} — {pagina.total} movimiento(s)
              </span>
              <div className="d-flex gap-2">
                <button
                  type="button"
                  className="btn btn-sm btn-outline-secondary rounded-0"
                  disabled={pagina.pagina <= 1 || cargando}
                  onClick={() => cambiarPagina(-1)}
                >
                  Anterior
                </button>
                <button
                  type="button"
                  className="btn btn-sm btn-outline-secondary rounded-0"
                  disabled={pagina.pagina >= totalPaginas || cargando}
                  onClick={() => cambiarPagina(1)}
                >
                  Siguiente
                </button>
              </div>
            </div>
          </>
        )}
      </Box>

      {modalAjusteAbierto && pagina && puntosVenta && (
        <ModalAjusteDeProveedor
          idProveedor={idProveedor}
          puntosVenta={puntosVenta}
          saldoActual={pagina.header.saldo}
          onCerrar={() => setModalAjusteAbierto(false)}
          onAntesDeEscribir={() => {
            generacionRef.current += 1
          }}
          onRegistrado={(movimiento) => {
            setModalAjusteAbierto(false)
            setAviso(`Ajuste registrado: ${formatearMoneda(movimiento.importe)}.`)
            // regla 6: el refetch queda aislado del try/catch de la escritura del modal — si
            // falla, no pisa el aviso de éxito de arriba.
            cargar()
          }}
        />
      )}
    </div>
  )
}

/**
 * Pantalla de estado de cuenta del proveedor (stage-15-cc-proveedores-ledger, Slice 6, design: Web
 * Composition): header (saldo) + ledger paginado con filtros desde/hasta/histórico y el modal de
 * ajuste manual (droppable pre-autorizado, tasks.md decisión 3 — el endpoint sigue sirviendo la
 * operación aunque el modal no se entregue). Entrada desde `ResumenSaldoDeProveedor` (panel de
 * `Proveedores.tsx` y header filtrado de `Compras.tsx`) — `Politicas.OperacionDePos` del lado del
 * servidor (todo rol opera), a diferencia de `/proveedores` que es admin-only: un Vendedor llega
 * acá desde `Compras.tsx`, nunca desde `Proveedores.tsx`.
 */
export function CuentaCorrienteDeProveedor() {
  const { id } = useParams<{ id: string }>()
  const idProveedor = Number(id)
  const idProveedorValido = id !== undefined && Number.isFinite(idProveedor)

  const location = useLocation()
  const proveedorDeState = (location.state as { proveedor?: ProveedorListado } | null)?.proveedor ?? null

  // Sin `location.state` (URL directa) se resuelve por `GET /api/proveedores/{id}` — Admin-only
  // del lado del servidor (`Politicas.GestionDeCatalogo`, fuera del alcance de esta slice: "cero
  // cambios de API"). Un Vendedor/Supervisor llegando así falla CERRADO (regla 7): el nombre no se
  // resuelve, el aviso queda visible y el ajuste se deshabilita — pero el estado de cuenta en sí
  // (`OperacionDePos`) sigue cargando y mostrándose igual, porque es un fetch independiente.
  const [proveedorInfo, setProveedorInfo] = useState<ProveedorListado | null>(proveedorDeState)
  const [cargandoProveedor, setCargandoProveedor] = useState(proveedorDeState === null)
  const [errorProveedor, setErrorProveedor] = useState('')
  const generacionProveedorRef = useRef(0)

  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[] | null>(null)
  const [errorPuntosVenta, setErrorPuntosVenta] = useState('')

  useEffect(() => {
    if (proveedorDeState !== null || !idProveedorValido) {
      setProveedorInfo(proveedorDeState)
      setCargandoProveedor(false)
      setErrorProveedor('')
      return
    }

    let vigente = true
    const miGeneracion = (generacionProveedorRef.current += 1)
    setCargandoProveedor(true)
    setErrorProveedor('')

    clienteDeProveedores
      .obtener(idProveedor)
      .then((proveedor) => {
        if (!vigente || generacionProveedorRef.current !== miGeneracion) return
        setProveedorInfo(proveedor)
      })
      .catch((e) => {
        if (!vigente || generacionProveedorRef.current !== miGeneracion) return
        setProveedorInfo(null)
        setErrorProveedor(e instanceof ErrorApi ? e.message : 'No se pudo confirmar el proveedor.')
      })
      .finally(() => {
        if (!vigente || generacionProveedorRef.current !== miGeneracion) return
        setCargandoProveedor(false)
      })

    return () => {
      vigente = false
    }
  }, [idProveedor, idProveedorValido, proveedorDeState])

  useEffect(() => {
    let vigente = true
    clienteDeOrganizacion
      .listarPuntosVenta()
      .then((lista) => {
        if (!vigente) return
        setPuntosVenta(lista)
      })
      .catch((e) => {
        if (!vigente) return
        setPuntosVenta([])
        setErrorPuntosVenta(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los puntos de venta.')
      })

    return () => {
      vigente = false
    }
  }, [])

  if (!idProveedorValido) {
    return (
      <div className="container-fluid py-4">
        <Box titulo="Estado de cuenta" variante="warning">
          <p className="text-muted">No se especificó el proveedor.</p>
          <Link className="btn btn-outline-secondary rounded-0" to="/proveedores">
            Volver a proveedores
          </Link>
        </Box>
      </div>
    )
  }

  return (
    <PantallaCuentaCorrienteDeProveedor
      key={idProveedor}
      idProveedor={idProveedor}
      proveedorInfo={proveedorInfo}
      cargandoProveedor={cargandoProveedor}
      errorProveedor={errorProveedor}
      puntosVenta={puntosVenta}
      errorPuntosVenta={errorPuntosVenta}
    />
  )
}
