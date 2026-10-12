import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useParams } from 'react-router'
import { ErrorApi } from '../api/cliente'
import { clienteDeCatalogo } from '../api/catalogos'
import { clienteDeClientes } from '../api/clientes'
import { fechaDeHoyParaPago } from '../api/compras'
import {
  clienteDeCuentaDePersonal,
  filtrosDeCuentaDePersonalIniciales,
  type FiltrosDeCuentaDePersonal,
} from '../api/cuentaDePersonal'
import { clienteDeOrganizacion } from '../api/organizacion'
import { puedeGestionarCatalogos } from '../api/tipos'
import type {
  EmpresaListado,
  MedioPagoAlta,
  MedioPagoListado,
  MovimientoDePersonal,
  PaginaDeCuentaDePersonal,
  PersonalListado,
} from '../api/tipos'
import { useAuth } from '../auth/useAuth'
import { Box } from '../componentes/Box'
import { CampoFecha } from '../componentes/CampoFecha'
import { Cargando } from '../componentes/Cargando'
import { formatearImporte, formatearImporteConSigno } from '../formato/importes'
import { isoATexto } from '../formato/fechas'
import { usePuntoVenta } from '../puntoVenta/usePuntoVenta'
import { ModalDeAjuste, ModalDeHoras, ModalDePago } from './CuentaDePersonalModales'
import { errorDeEscritura, type Escritura } from './cuentaDePersonalErrores'
import {
  aSolicitudDeAjuste,
  aSolicitudDeHoras,
  aSolicitudDePago,
  deudaDeCliente,
  etiquetaDeTipo,
  mediosOfrecidos,
  textoDeLineaDeDesglose,
  textoDeSaldo,
  validarAjuste,
  validarHoras,
  validarPago,
  type ContextoDePago,
  type ErrorDeCuenta,
  type FormularioDeAjuste,
  type FormularioDeHoras,
  type FormularioDePago,
} from './cuentaDePersonalFormularios'

const clienteMediosPago = clienteDeCatalogo<MedioPagoListado, MedioPagoAlta>('medios-pago')

const AVISO_REFRESCO_FALLIDO = 'Pero no se pudo actualizar la vista. Recargá la pantalla.'

function moneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

type ModalAbierta = 'horas' | 'ajuste' | 'pago' | null

function SinEmpleado() {
  return (
    <div className="container-fluid py-4">
      <Box titulo="Cuenta del personal" variante="warning">
        <p className="text-muted">No se encontró al empleado. Puede haberse dado de baja.</p>
        <Link className="btn btn-outline-secondary" to="/personal">
          Volver al personal
        </Link>
      </Box>
    </div>
  )
}

/**
 * Cuenta de un empleado: saldo, estado de cuenta paginado y las tres escrituras del libro (cargar horas,
 * ajuste y pago). La remonta `key={id}`: ningún estado sobrevive a un cambio de empleado.
 *
 * `react-async-state`: cada lectura tiene su propia generación (empleado, estado de cuenta, deuda del
 * cliente) y una respuesta tardía de una anterior se descarta. Las tres escrituras pasan por `escribir`, cuya
 * guarda de reentrancia es `ocupadoRef` (un doble clic en el mismo tick emite una sola request). `bloqueado`
 * deja inerte la pantalla entera —botones, filtros, paginación y el enlace de vuelta— desde el clic hasta que
 * el refresco posterior aterrizó, y la modal no se puede cerrar mientras tanto; por eso la respuesta propia de
 * la escritura no lleva generación: nada puede reemplazar ni reabrir lo que escribe y no navega. Un refresco
 * fallido nunca convierte en error una escritura ya confirmada.
 */
function PantallaDeCuentaDePersonal({ idPersonal }: { idPersonal: number }) {
  const { usuario } = useAuth()
  const esAdmin = usuario !== null && puedeGestionarCatalogos(usuario.rolId)
  const { puntoVenta } = usePuntoVenta()

  const [hoy] = useState(() => fechaDeHoyParaPago())
  const [empleado, setEmpleado] = useState<PersonalListado | null>(null)
  const [errorEmpleado, setErrorEmpleado] = useState('')
  const [noEncontrado, setNoEncontrado] = useState(false)

  const [filtros, setFiltros] = useState<FiltrosDeCuentaDePersonal>(() => filtrosDeCuentaDePersonalIniciales())
  const filtrosAlMontar = useRef(filtros)
  const [cuenta, setCuenta] = useState<PaginaDeCuentaDePersonal | null>(null)
  const [cargandoCuenta, setCargandoCuenta] = useState(true)
  const [errorCuenta, setErrorCuenta] = useState('')

  const [saldoDelCliente, setSaldoDelCliente] = useState<number | null>(null)
  const [errorDeuda, setErrorDeuda] = useState(false)

  const [medios, setMedios] = useState<MedioPagoListado[] | null>(null)
  const [empresas, setEmpresas] = useState<EmpresaListado[] | null>(null)
  const [errorDeCatalogos, setErrorDeCatalogos] = useState('')

  const [modal, setModal] = useState<ModalAbierta>(null)
  const [errorDeModal, setErrorDeModal] = useState<ErrorDeCuenta | null>(null)
  const [aviso, setAviso] = useState('')
  const [escribiendo, setEscribiendo] = useState(false)
  const contenedorRef = useRef<HTMLDivElement>(null)

  /** Contrato: una lectura nueva del estado de cuenta (filtro, página o refresco) invalida a la anterior. */
  const generacionDeLaCuenta = useRef(0)
  /** Contrato: el refresco de la deuda posterior a una escritura invalida a la lectura inicial en vuelo. */
  const generacionDeLaDeuda = useRef(0)
  /** Espejo síncrono de "hay una escritura en vuelo": la ÚNICA guarda de reentrancia válida. */
  const ocupadoRef = useRef(false)

  // El empleado se lee una vez por montaje (la ruta remonta por id) y solo se relee con "Reintentar", que
  // aparece cuando la lectura anterior ya terminó: ninguna lectura puede reemplazar a otra en vuelo.
  const cargarEmpleado = useCallback(async () => {
    setErrorEmpleado('')
    try {
      setEmpleado(await clienteDeCuentaDePersonal.obtenerPersonal(idPersonal))
    } catch (e) {
      if (e instanceof ErrorApi && e.estado === 404) setNoEncontrado(true)
      else setErrorEmpleado(e instanceof ErrorApi ? e.message : 'No se pudo cargar al empleado.')
    }
  }, [idPersonal])

  const cargarCuenta = useCallback(
    async (pedidos: FiltrosDeCuentaDePersonal, propagar = false) => {
      const token = ++generacionDeLaCuenta.current
      setCargandoCuenta(true)
      try {
        const resultado = await clienteDeCuentaDePersonal.obtener(idPersonal, pedidos)
        if (generacionDeLaCuenta.current !== token) return
        setCuenta(resultado)
        setErrorCuenta('')
      } catch (e) {
        if (generacionDeLaCuenta.current !== token) return
        if (e instanceof ErrorApi && e.estado === 404) setNoEncontrado(true)
        if (propagar) throw e
        setErrorCuenta(e instanceof ErrorApi ? e.message : 'No se pudo cargar el estado de cuenta.')
      } finally {
        if (generacionDeLaCuenta.current === token) setCargandoCuenta(false)
      }
    },
    [idPersonal],
  )

  const cargarDeuda = useCallback(async (idCliente: number, propagar = false) => {
    const token = ++generacionDeLaDeuda.current
    try {
      const cliente = await clienteDeClientes.obtener(idCliente)
      if (generacionDeLaDeuda.current !== token) return
      setSaldoDelCliente(cliente.saldo)
      setErrorDeuda(false)
    } catch (e) {
      if (generacionDeLaDeuda.current !== token) return
      if (propagar) throw e
      setErrorDeuda(true)
    }
  }, [])

  useEffect(() => {
    void cargarEmpleado()
    void cargarCuenta(filtrosAlMontar.current)
  }, [cargarEmpleado, cargarCuenta])

  // `nombreCliente` llega en `null` cuando el cliente vinculado se dio de baja: el servidor lo trata como sin vincular.
  const idCliente = empleado !== null && empleado.nombreCliente !== null ? empleado.idCliente : null
  const hayClienteVigente = idCliente !== null

  useEffect(() => {
    if (idCliente === null) return
    void cargarDeuda(idCliente)
  }, [idCliente, cargarDeuda])

  // Catálogos del pago. La empresa solo la elige el Admin (pagar de la tesorería); el Supervisor ni la pide.
  useEffect(() => {
    let vigente = true
    const falla = () => {
      if (vigente) setErrorDeCatalogos('No se pudieron cargar todos los datos del pago: no se pueden agregar líneas de dinero.')
    }

    clienteMediosPago
      .listar(false)
      .then((lista) => {
        if (vigente) setMedios(mediosOfrecidos(lista))
      })
      .catch(() => {
        if (!vigente) return
        setMedios([])
        falla()
      })

    if (esAdmin) {
      clienteDeOrganizacion
        .listarEmpresas()
        .then((lista) => {
          if (vigente) setEmpresas(lista)
        })
        .catch(() => {
          if (!vigente) return
          setEmpresas([])
          falla()
        })
    }

    return () => {
      vigente = false
    }
  }, [esAdmin])

  function cambiarFiltros(cambios: Partial<FiltrosDeCuentaDePersonal>, volverALaPrimera = true) {
    const siguientes = { ...filtros, ...cambios, ...(volverALaPrimera && { pagina: 1 }) }
    setFiltros(siguientes)
    void cargarCuenta(siguientes)
  }

  /** El refresco va fuera del try/catch de la escritura: una escritura ya confirmada nunca se reporta fallida. */
  async function refrescarTrasEscribir(mensajeOk: string) {
    setAviso(mensajeOk)
    const primera = { ...filtros, pagina: 1 }
    setFiltros(primera)

    const lecturas = await Promise.allSettled([
      cargarCuenta(primera, true),
      idCliente === null ? Promise.resolve() : cargarDeuda(idCliente, true),
    ])
    if (lecturas.some((l) => l.status === 'rejected')) setAviso(`${mensajeOk} ${AVISO_REFRESCO_FALLIDO}`)
  }

  async function escribir(escritura: Escritura, accion: () => Promise<MovimientoDePersonal>, mensajeOk: string) {
    if (ocupadoRef.current) return

    ocupadoRef.current = true
    setEscribiendo(true)
    setErrorDeModal(null)
    try {
      try {
        await accion()
      } catch (e) {
        setErrorDeModal(errorDeEscritura(e, escritura))

        return
      }

      setModal(null)
      await refrescarTrasEscribir(mensajeOk)
    } finally {
      ocupadoRef.current = false
      setEscribiendo(false)
    }
  }

  function enviarHoras(f: FormularioDeHoras) {
    const rechazo = validarHoras(f)
    if (rechazo) return setErrorDeModal(rechazo)

    void escribir('horas', () => clienteDeCuentaDePersonal.cargarHoras(idPersonal, aSolicitudDeHoras(f)), 'Horas cargadas.')
  }

  function enviarAjuste(f: FormularioDeAjuste) {
    const rechazo = validarAjuste(f)
    if (rechazo) return setErrorDeModal(rechazo)

    void escribir('ajuste', () => clienteDeCuentaDePersonal.registrarAjuste(idPersonal, aSolicitudDeAjuste(f)), 'Ajuste registrado.')
  }

  function enviarPago(f: FormularioDePago, ctx: ContextoDePago) {
    const rechazo = validarPago(f, ctx)
    if (rechazo) return setErrorDeModal(rechazo)

    void escribir('pago', () => clienteDeCuentaDePersonal.pagar(idPersonal, aSolicitudDePago(f, ctx)), 'Pago registrado.')
  }

  function abrir(cual: Exclude<ModalAbierta, null>) {
    setAviso('')
    setErrorDeModal(null)
    setModal(cual)
  }

  function cerrarModal() {
    setModal(null)
    setErrorDeModal(null)
  }

  if (noEncontrado) return <SinEmpleado />

  /** La pantalla entera queda inerte mientras una escritura está en vuelo, hasta que el refresco aterrizó. */
  const bloqueado = escribiendo
  const catalogosListos = medios !== null && (!esAdmin || empresas !== null)
  const puedeEscribir = empleado !== null && cuenta !== null && !bloqueado
  const totalDePaginas = cuenta ? Math.max(1, Math.ceil(cuenta.total / cuenta.tamanio)) : 1
  const deudaDelCliente = saldoDelCliente === null || errorDeuda ? null : deudaDeCliente(saldoDelCliente)

  const volver = (
    <Link
      className={`btn btn-sm btn-outline-secondary${bloqueado ? ' disabled' : ''}`}
      to="/personal"
      aria-disabled={bloqueado}
      tabIndex={bloqueado ? -1 : undefined}
      onClick={(evento) => {
        if (bloqueado) evento.preventDefault()
      }}
    >
      Volver al personal
    </Link>
  )

  return (
    <div ref={contenedorRef} tabIndex={-1} style={{ outline: 'none' }} className="container-fluid py-4">
      <Box titulo={`Cuenta de ${cuenta?.encabezado.nombre ?? empleado?.nombre ?? 'personal'}`} variante="inverse" herramientas={volver}>
        {aviso && <div className="alert alert-success">{aviso}</div>}
        {errorEmpleado && (
          <div className="alert alert-danger d-flex justify-content-between align-items-center">
            {errorEmpleado}
            <button type="button" className="btn btn-sm btn-outline-danger" onClick={() => void cargarEmpleado()}>
              Reintentar
            </button>
          </div>
        )}
        {errorCuenta && (
          <div className="alert alert-danger d-flex justify-content-between align-items-center">
            {errorCuenta}
            <button type="button" className="btn btn-sm btn-outline-danger" disabled={bloqueado} onClick={() => void cargarCuenta(filtros)}>
              Reintentar
            </button>
          </div>
        )}

        <div className="row g-3 mb-3 align-items-start">
          <div className="col-md-3">
            <div className="small text-muted">Saldo</div>
            <div className="fs-5">{cuenta ? textoDeSaldo(cuenta.encabezado.saldo) : '—'}</div>
            <div className="small text-muted">Saldo positivo: la empresa le debe al empleado. Saldo negativo: adelanto ya pagado al empleado.</div>
          </div>
          <div className="col-md-3">
            <div className="small text-muted">Valor hora vigente</div>
            <div className="fs-5">{empleado ? moneda(empleado.valorHora) : '—'}</div>
          </div>
          <div className="col-md-3">
            <div className="small text-muted">Cliente vinculado</div>
            {empleado && !hayClienteVigente && <div className="text-muted fst-italic">sin vincular</div>}
            {empleado && hayClienteVigente && (
              <>
                <div>{empleado.nombreCliente}</div>
                <div className="small">
                  {errorDeuda && <span className="text-danger">No se pudo leer su deuda.</span>}
                  {!errorDeuda && saldoDelCliente === null && <span className="text-muted">Leyendo su deuda…</span>}
                  {!errorDeuda && saldoDelCliente !== null && (saldoDelCliente < 0 ? `Saldo a favor: ${moneda(-saldoDelCliente)}` : `Deuda: ${moneda(saldoDelCliente)}`)}
                </div>
              </>
            )}
          </div>
          <div className="col-md-3 d-flex flex-wrap gap-2 justify-content-md-end">
            <button type="button" className="btn btn-outline-secondary" disabled={!puedeEscribir} onClick={() => abrir('horas')}>
              Cargar horas
            </button>
            <button type="button" className="btn btn-outline-secondary" disabled={!puedeEscribir} onClick={() => abrir('ajuste')}>
              Ajuste manual
            </button>
            <button
              type="button"
              className="btn btn-primary"
              disabled={!puedeEscribir || !catalogosListos}
              title={catalogosListos ? undefined : 'Cargando los datos del pago…'}
              onClick={() => abrir('pago')}
            >
              Registrar pago
            </button>
          </div>
        </div>

        <div className="row g-2 align-items-end mb-3">
          <div className="col-md-3">
            <label className="form-label" htmlFor="cuenta-personal-desde">
              Desde
            </label>
            <CampoFecha id="cuenta-personal-desde" className="form-control" value={filtros.desde} disabled={filtros.historico || bloqueado} onChange={(desde) => cambiarFiltros({ desde })} />
          </div>
          <div className="col-md-3">
            <label className="form-label" htmlFor="cuenta-personal-hasta">
              Hasta
            </label>
            <CampoFecha id="cuenta-personal-hasta" className="form-control" value={filtros.hasta} disabled={filtros.historico || bloqueado} onChange={(hasta) => cambiarFiltros({ hasta })} />
          </div>
          <div className="col-md-3">
            <div className="form-check">
              <input id="cuenta-personal-historico" type="checkbox" className="form-check-input" checked={filtros.historico} disabled={bloqueado} onChange={(e) => cambiarFiltros({ historico: e.target.checked })} />
              <label className="form-check-label" htmlFor="cuenta-personal-historico">
                Ver histórico completo
              </label>
            </div>
          </div>
        </div>

        {cargandoCuenta && !cuenta && <Cargando />}
        {cuenta && (
          <>
            {cargandoCuenta && <p className="text-muted">Actualizando…</p>}
            <div className="table-responsive">
              <table className="table table-sm table-striped table-bordered align-middle">
                <thead>
                  <tr>
                    <th>Fecha</th>
                    <th>Tipo</th>
                    <th className="text-end">Horas</th>
                    <th className="text-end">Valor hora</th>
                    <th className="text-end">Importe</th>
                    <th className="text-end">Saldo resultante</th>
                    <th>Detalle</th>
                  </tr>
                </thead>
                <tbody>
                  {cuenta.items.map((m) => (
                    <tr key={m.idMovimiento}>
                      <td>{isoATexto(m.fecha)}</td>
                      <td>{etiquetaDeTipo(m.tipo)}</td>
                      <td className="text-end">{m.horas === null ? '' : formatearImporte(m.horas)}</td>
                      <td className="text-end">{m.valorHora === null ? '' : moneda(m.valorHora)}</td>
                      <td className="text-end">{formatearImporteConSigno(m.importe, { simbolo: true })}</td>
                      <td className="text-end">{textoDeSaldo(m.saldoResultante)}</td>
                      <td>
                        {m.detalle}
                        {m.desglose && (
                          <ul className="list-unstyled small text-muted mb-0">
                            {m.desglose.descuentoDeCuentaCorriente !== null && (
                              <li>Descuento de cuenta corriente: {moneda(m.desglose.descuentoDeCuentaCorriente)}</li>
                            )}
                            {m.desglose.lineas.map((l) => (
                              <li key={l.idGasto}>{textoDeLineaDeDesglose(l)}</li>
                            ))}
                          </ul>
                        )}
                      </td>
                    </tr>
                  ))}
                  {cuenta.items.length === 0 && (
                    <tr>
                      <td colSpan={7} className="text-center text-muted py-4">
                        No hay movimientos en el período seleccionado.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>

            <div className="d-flex justify-content-between align-items-center">
              <span className="small text-muted">
                Página {cuenta.pagina} de {totalDePaginas} — {cuenta.total} movimiento(s)
              </span>
              <div className="d-flex gap-2">
                <button type="button" className="btn btn-sm btn-outline-secondary" disabled={cuenta.pagina <= 1 || cargandoCuenta || bloqueado} onClick={() => cambiarFiltros({ pagina: filtros.pagina - 1 }, false)}>
                  Anterior
                </button>
                <button type="button" className="btn btn-sm btn-outline-secondary" disabled={cuenta.pagina >= totalDePaginas || cargandoCuenta || bloqueado} onClick={() => cambiarFiltros({ pagina: filtros.pagina + 1 }, false)}>
                  Siguiente
                </button>
              </div>
            </div>
          </>
        )}
      </Box>

      {modal === 'horas' && empleado && (
        <ModalDeHoras
          hoy={hoy}
          valorHoraInicial={empleado.valorHora}
          error={errorDeModal}
          escribiendo={escribiendo}
          focoDeReserva={contenedorRef}
          onCambio={() => setErrorDeModal(null)}
          onCerrar={cerrarModal}
          onEnviar={enviarHoras}
        />
      )}
      {modal === 'ajuste' && cuenta && (
        <ModalDeAjuste
          hoy={hoy}
          saldo={cuenta.encabezado.saldo}
          error={errorDeModal}
          escribiendo={escribiendo}
          focoDeReserva={contenedorRef}
          onCambio={() => setErrorDeModal(null)}
          onCerrar={cerrarModal}
          onEnviar={enviarAjuste}
        />
      )}
      {modal === 'pago' && cuenta && empleado && (
        <ModalDePago
          hoy={hoy}
          saldo={cuenta.encabezado.saldo}
          esAdmin={esAdmin}
          puntoVenta={puntoVenta}
          nombreCliente={hayClienteVigente ? empleado.nombreCliente : null}
          deudaDelCliente={deudaDelCliente}
          medios={medios ?? []}
          empresas={empresas ?? []}
          errorDeCatalogos={errorDeCatalogos}
          error={errorDeModal}
          escribiendo={escribiendo}
          focoDeReserva={contenedorRef}
          onCambio={() => setErrorDeModal(null)}
          onCerrar={cerrarModal}
          onEnviar={enviarPago}
        />
      )}
    </div>
  )
}

/** Ruta `/personal/:id/cuenta`. Un id que no es un entero positivo se trata como un empleado inexistente. */
export function CuentaDePersonal() {
  const { id } = useParams<{ id: string }>()
  const idPersonal = Number(id)

  if (!Number.isInteger(idPersonal) || idPersonal <= 0) return <SinEmpleado />

  return <PantallaDeCuentaDePersonal key={idPersonal} idPersonal={idPersonal} />
}
