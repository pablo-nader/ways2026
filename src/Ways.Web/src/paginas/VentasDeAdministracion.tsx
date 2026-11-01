import { useEffect, useRef, useState } from 'react'
import { clienteDeCatalogo, clienteDeCatalogosFiscales } from '../api/catalogos'
import { clienteDeOrganizacion } from '../api/organizacion'
import {
  clienteDeVentas,
  esColumnaOrdenableDeVentasDeAdministracion,
  filtrosDeVentasDeAdministracionVacios,
  type FiltrosDeVentasDeAdministracion,
  type OrdenDeVentasDeAdministracion,
} from '../api/ventas'
import {
  puedeGestionarCatalogos,
  type ComprobanteEmitido,
  type FilaDeVentaDeAdministracion,
  type MedioPagoAlta,
  type MedioPagoListado,
  type PuntoVentaListado,
  type TipoComprobanteListado,
} from '../api/tipos'
import { useAuth } from '../auth/useAuth'
import { Box } from '../componentes/Box'
import { BotonIcono } from '../componentes/BotonIcono'
import { CampoFecha } from '../componentes/CampoFecha'
import { CampoImporte } from '../componentes/CampoImporte'
import { Cargando } from '../componentes/Cargando'
import { EncabezadoOrdenable } from '../componentes/grilla'
import { PaginadorDeServidor } from '../componentes/grilla/PaginadorDeServidor'
import { useGrillaDeServidor } from '../componentes/grilla/useGrillaDeServidor'
import { ModalDeCambioDeMedios } from '../componentes/ModalDeCambioDeMedios'
import { fechaHoraATexto } from '../formato/fechas'
import { formatearImporte } from '../formato/importes'
import { toast } from '../toasts/toasts'
import {
  booleanoDeSelect,
  etiquetaDeEstadoDeTurno,
  ETIQUETA_DE_ESTADO_FISCAL,
  SIN_DATO,
  tituloDeCambiarMedio,
  valorDeSelectBooleano,
} from './utilidadesVentasDeAdministracion'
import { useOpcionesDeEmpleado } from './useOpcionesDeEmpleado'

const clienteMediosPago = clienteDeCatalogo<MedioPagoListado, MedioPagoAlta>('medios-pago')

const CLAVES_DE_BORRADOR = ['numero', 'idTurnoCaja', 'cliente', 'totalDesde', 'totalHasta'] as const satisfies readonly (keyof FiltrosDeVentasDeAdministracion)[]
const SIN_FILAS: readonly FilaDeVentaDeAdministracion[] = []

function consultarVentas(filtros: FiltrosDeVentasDeAdministracion, orden: OrdenDeVentasDeAdministracion) {
  return clienteDeVentas.listarDeAdministracion(filtros, orden)
}

function numeroOVacio(valor: string): number | null {
  return valor === '' ? null : Number(valor)
}

/** Texto de un select de filtro numérico: '' = sin filtro. */
function valorDeSelectNumerico(valor: number | null): string {
  return valor === null ? '' : String(valor)
}

/**
 * Ventas de la administración: todas las ventas del tenant con un filtro y un orden en cada columna, resueltos en el servidor.
 * Supervisor y Admin leen; solo Admin corrige el medio de pago de una venta (`PUT /api/ventas/administracion/{id}/medios`).
 * Mientras el modal de corrección está abierto, o hay una consulta en vuelo, las acciones de fila y el paginador quedan inertes;
 * el modal es dueño de su lectura y de su escritura y esta pantalla solo avisa y vuelve a pedir el listado cuando termina.
 */
export function VentasDeAdministracion() {
  const { usuario } = useAuth()
  const puedeEditar = usuario !== null && puedeGestionarCatalogos(usuario.rolId)

  const grilla = useGrillaDeServidor({
    filtrosVacios: filtrosDeVentasDeAdministracionVacios,
    clavesDeBorrador: CLAVES_DE_BORRADOR,
    consultar: consultarVentas,
    esColumna: esColumnaOrdenableDeVentasDeAdministracion,
    mensajeDeError: 'No se pudieron cargar las ventas.',
  })
  const { filtros, borrador, orden, pagina, cargando, cargaInicialHecha, error } = grilla

  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[]>([])
  const [medios, setMedios] = useState<MedioPagoListado[] | null>(null)
  const [tipos, setTipos] = useState<TipoComprobanteListado[]>([])
  const [erroresDeCatalogo, setErroresDeCatalogo] = useState<string[]>([])
  const empleados = useOpcionesDeEmpleado(puedeEditar, pagina?.items ?? SIN_FILAS)

  const [filaACambiar, setFilaACambiar] = useState<FilaDeVentaDeAdministracion | null>(null)
  const disparadorRef = useRef<HTMLElement | null>(null)
  const limpiarRef = useRef<HTMLButtonElement>(null)
  const focoPendienteRef = useRef(false)

  useEffect(() => {
    let vigente = true
    const falla = (nombre: string) => () => {
      if (vigente) setErroresDeCatalogo((previos) => [...previos, nombre])
    }
    clienteDeOrganizacion
      .listarPuntosVenta()
      .then((lista) => vigente && setPuntosVenta(lista))
      .catch(falla('puntos de venta'))
    clienteMediosPago
      .listar(true)
      .then((lista) => vigente && setMedios(lista))
      .catch(falla('medios de pago'))
    clienteDeCatalogosFiscales
      .tiposComprobante()
      .then((lista) => vigente && setTipos(lista.filter((t) => t.clase === 'Venta')))
      .catch(falla('tipos de comprobante'))

    return () => {
      vigente = false
    }
  }, [])

  const bloqueado = filaACambiar !== null
  const filasInertes = bloqueado || cargando

  useEffect(() => {
    if (bloqueado || !focoPendienteRef.current) return
    focoPendienteRef.current = false
    const disparador = disparadorRef.current
    const destino = disparador?.isConnected && !disparador.matches(':disabled') ? disparador : limpiarRef.current
    destino?.focus()
  }, [bloqueado, cargando])

  function pedirCambioDeMedios(fila: FilaDeVentaDeAdministracion, disparador: HTMLElement) {
    disparadorRef.current = disparador
    setFilaACambiar(fila)
  }

  function cerrarCambioDeMedios() {
    focoPendienteRef.current = true
    setFilaACambiar(null)
  }

  /** El servidor ya corrigió la venta: se avisa y se vuelve a pedir el listado con los mismos filtros (un fallo de ese refresco
   * tiene su propio aviso y no desmiente la corrección). */
  function alGuardarCambioDeMedios(corregida: ComprobanteEmitido) {
    toast.exito(`Se cambió el medio de pago de la venta ${corregida.numeroVisible}.`)
    cerrarCambioDeMedios()
    void grilla.recargar()
  }

  const hayFiltros =
    filtros.desde !== '' ||
    filtros.hasta !== '' ||
    filtros.numero.trim() !== '' ||
    filtros.idTipoComprobante !== null ||
    filtros.idPuntoVenta !== null ||
    filtros.idTurnoCaja.trim() !== '' ||
    filtros.idEmpleado !== null ||
    filtros.cliente.trim() !== '' ||
    filtros.idMedioPago !== null ||
    filtros.totalDesde !== null ||
    filtros.totalHasta !== null ||
    filtros.estado !== '' ||
    filtros.estadoFiscal !== '' ||
    filtros.turnoCerrado !== null ||
    filtros.conciliado !== null

  const encabezado = (columna: string, titulo: string, clase?: string) => (
    <EncabezadoOrdenable columna={columna} orden={orden} onAlternar={grilla.alternarOrden} className={clase}>
      {titulo}
    </EncabezadoOrdenable>
  )

  return (
    <Box titulo="Listado de ventas" variante="inverse">
      {error && (
        <div className="alert alert-danger d-flex justify-content-between align-items-center gap-2" role="alert">
          <span>{error}</span>
          <button type="button" className="btn btn-sm btn-outline-danger" onClick={() => void grilla.recargar()}>
            Reintentar
          </button>
        </div>
      )}
      {erroresDeCatalogo.length > 0 && (
        <div className="alert alert-warning" role="alert">
          No se pudieron cargar los filtros de {erroresDeCatalogo.join(', ')}: esas opciones pueden faltar.
        </div>
      )}
      {empleados.error && (
        <div className="alert alert-warning" role="alert">
          No se pudo cargar el listado de empleados: el filtro por empleado puede estar incompleto.
        </div>
      )}

      {cargando && !cargaInicialHecha ? (
        <Cargando />
      ) : (
        <>
          <div className="table-responsive">
            <table className="table table-striped table-hover table-bordered align-middle">
              <thead>
                <tr>
                  {encabezado('fecha', 'Fecha')}
                  {encabezado('numero', 'Número')}
                  {encabezado('tipo', 'Tipo')}
                  {encabezado('puntoVenta', 'Punto de venta')}
                  {encabezado('turno', 'Turno')}
                  {encabezado('cliente', 'Cliente')}
                  {encabezado('empleado', 'Empleado')}
                  {encabezado('medios', 'Medios de pago')}
                  {encabezado('total', 'Total', 'text-end')}
                  {encabezado('estado', 'Estado')}
                  {encabezado('estadoFiscal', 'Estado fiscal')}
                  {encabezado('estadoTurno', 'Estado del turno')}
                  {encabezado('conciliado', 'Conciliado')}
                  <th scope="col" className="text-end">
                    Acciones
                  </th>
                </tr>
                <tr>
                  <th scope="col">
                    <div className="d-flex gap-1">
                      <CampoFecha
                        aria-label="Fecha desde"
                        className="form-control form-control-sm"
                        value={filtros.desde}
                        max={filtros.hasta || undefined}
                        onChange={(valor) => grilla.cambiarFiltro({ desde: valor })}
                      />
                      <CampoFecha
                        aria-label="Fecha hasta"
                        className="form-control form-control-sm"
                        value={filtros.hasta}
                        min={filtros.desde || undefined}
                        onChange={(valor) => grilla.cambiarFiltro({ hasta: valor })}
                      />
                    </div>
                  </th>
                  <th scope="col">
                    <input
                      type="search"
                      inputMode="numeric"
                      className="form-control form-control-sm"
                      aria-label="Filtrar por número"
                      placeholder="Número"
                      value={borrador.numero}
                      onChange={(e) => grilla.cambiarBorrador({ numero: e.target.value })}
                    />
                  </th>
                  <th scope="col">
                    <select
                      className="form-select form-select-sm"
                      aria-label="Filtrar por tipo"
                      value={valorDeSelectNumerico(filtros.idTipoComprobante)}
                      onChange={(e) => grilla.cambiarFiltro({ idTipoComprobante: numeroOVacio(e.target.value) })}
                    >
                      <option value="">Todos</option>
                      {tipos.map((t) => (
                        <option key={t.id} value={t.id}>
                          {t.nombre}
                        </option>
                      ))}
                    </select>
                  </th>
                  <th scope="col">
                    <select
                      className="form-select form-select-sm"
                      aria-label="Filtrar por punto de venta"
                      value={valorDeSelectNumerico(filtros.idPuntoVenta)}
                      onChange={(e) => grilla.cambiarFiltro({ idPuntoVenta: numeroOVacio(e.target.value) })}
                    >
                      <option value="">Todos</option>
                      {puntosVenta.map((pv) => (
                        <option key={pv.id} value={pv.id}>
                          {pv.nombre}
                        </option>
                      ))}
                    </select>
                  </th>
                  <th scope="col">
                    <input
                      type="search"
                      inputMode="numeric"
                      className="form-control form-control-sm"
                      aria-label="Filtrar por turno"
                      placeholder="Turno"
                      value={borrador.idTurnoCaja}
                      onChange={(e) => grilla.cambiarBorrador({ idTurnoCaja: e.target.value })}
                    />
                  </th>
                  <th scope="col">
                    <input
                      type="search"
                      className="form-control form-control-sm"
                      aria-label="Filtrar por cliente"
                      placeholder="Cliente"
                      value={borrador.cliente}
                      onChange={(e) => grilla.cambiarBorrador({ cliente: e.target.value })}
                    />
                  </th>
                  <th scope="col">
                    <select
                      className="form-select form-select-sm"
                      aria-label="Filtrar por empleado"
                      value={valorDeSelectNumerico(filtros.idEmpleado)}
                      onChange={(e) => grilla.cambiarFiltro({ idEmpleado: numeroOVacio(e.target.value) })}
                    >
                      <option value="">Todos</option>
                      {empleados.opciones.map((e) => (
                        <option key={e.id} value={e.id}>
                          {e.nombre}
                        </option>
                      ))}
                    </select>
                  </th>
                  <th scope="col">
                    <select
                      className="form-select form-select-sm"
                      aria-label="Filtrar por medio de pago"
                      value={valorDeSelectNumerico(filtros.idMedioPago)}
                      onChange={(e) => grilla.cambiarFiltro({ idMedioPago: numeroOVacio(e.target.value) })}
                    >
                      <option value="">Todos</option>
                      {(medios ?? []).map((m) => (
                        <option key={m.id} value={m.id}>
                          {m.nombre}
                        </option>
                      ))}
                    </select>
                  </th>
                  <th scope="col">
                    <div className="d-flex gap-1">
                      <CampoImporte
                        aria-label="Total desde"
                        placeholder="Desde"
                        className="form-control form-control-sm"
                        valor={borrador.totalDesde}
                        onChange={(valor) => grilla.cambiarBorrador({ totalDesde: valor })}
                      />
                      <CampoImporte
                        aria-label="Total hasta"
                        placeholder="Hasta"
                        className="form-control form-control-sm"
                        valor={borrador.totalHasta}
                        onChange={(valor) => grilla.cambiarBorrador({ totalHasta: valor })}
                      />
                    </div>
                  </th>
                  <th scope="col">
                    <select
                      className="form-select form-select-sm"
                      aria-label="Filtrar por estado"
                      value={filtros.estado}
                      onChange={(e) => grilla.cambiarFiltro({ estado: e.target.value as FiltrosDeVentasDeAdministracion['estado'] })}
                    >
                      <option value="">Todos</option>
                      <option value="Emitido">Emitido</option>
                      <option value="Anulado">Anulado</option>
                    </select>
                  </th>
                  <th scope="col">
                    <select
                      className="form-select form-select-sm"
                      aria-label="Filtrar por estado fiscal"
                      value={filtros.estadoFiscal}
                      onChange={(e) =>
                        grilla.cambiarFiltro({ estadoFiscal: e.target.value as FiltrosDeVentasDeAdministracion['estadoFiscal'] })
                      }
                    >
                      <option value="">Todos</option>
                      {Object.entries(ETIQUETA_DE_ESTADO_FISCAL).map(([valor, etiqueta]) => (
                        <option key={valor} value={valor}>
                          {etiqueta}
                        </option>
                      ))}
                    </select>
                  </th>
                  <th scope="col">
                    <select
                      className="form-select form-select-sm"
                      aria-label="Filtrar por estado del turno"
                      value={valorDeSelectBooleano(filtros.turnoCerrado)}
                      onChange={(e) => grilla.cambiarFiltro({ turnoCerrado: booleanoDeSelect(e.target.value) })}
                    >
                      <option value="">Todos</option>
                      <option value="false">Abierto</option>
                      <option value="true">Cerrado</option>
                    </select>
                  </th>
                  <th scope="col">
                    <select
                      className="form-select form-select-sm"
                      aria-label="Filtrar por conciliado"
                      value={valorDeSelectBooleano(filtros.conciliado)}
                      onChange={(e) => grilla.cambiarFiltro({ conciliado: booleanoDeSelect(e.target.value) })}
                    >
                      <option value="">Todos</option>
                      <option value="true">Sí</option>
                      <option value="false">No</option>
                    </select>
                  </th>
                  <th scope="col" className="text-end">
                    <button
                      ref={limpiarRef}
                      type="button"
                      className="btn btn-sm btn-outline-secondary"
                      disabled={bloqueado || !hayFiltros}
                      onClick={grilla.limpiar}
                    >
                      Limpiar
                    </button>
                  </th>
                </tr>
              </thead>
              <tbody style={cargando ? { opacity: 0.6 } : undefined}>
                {pagina?.items.map((v) => (
                  <tr key={v.id}>
                    <td className="text-nowrap">{fechaHoraATexto(v.fecha)}</td>
                    <td className="text-nowrap fw-semibold">{v.numeroVisible}</td>
                    <td>{v.tipo}</td>
                    <td>{v.puntoVenta ?? <span className="text-body-secondary">{SIN_DATO}</span>}</td>
                    <td>{v.idTurnoCaja ?? '—'}</td>
                    <td>{v.cliente ?? '—'}</td>
                    <td>{v.empleado ?? <span className="text-body-secondary">{SIN_DATO}</span>}</td>
                    <td>{v.medios ?? '—'}</td>
                    <td className="text-end text-nowrap">{formatearImporte(v.total, { simbolo: true })}</td>
                    <td>
                      <span className={`badge ${v.estado === 'Anulado' ? 'text-bg-secondary' : 'text-bg-success'}`}>{v.estado}</span>
                    </td>
                    <td>{ETIQUETA_DE_ESTADO_FISCAL[v.estadoFiscal]}</td>
                    <td>{etiquetaDeEstadoDeTurno(v.turnoCerrado)}</td>
                    <td>{v.conciliado ? 'Sí' : 'No'}</td>
                    <td className="text-end text-nowrap">
                      {puedeEditar && (
                        <span title={tituloDeCambiarMedio(v)}>
                          <BotonIcono
                            icono="editar"
                            etiqueta={`Cambiar medio de pago de la venta ${v.numeroVisible}`}
                            title={tituloDeCambiarMedio(v)}
                            disabled={filasInertes || !v.medioEditable || medios === null}
                            onClick={(evento) => pedirCambioDeMedios(v, evento.currentTarget)}
                          />
                        </span>
                      )}
                    </td>
                  </tr>
                ))}
                {pagina !== null && pagina.items.length === 0 && (
                  <tr>
                    <td colSpan={14} className="text-center text-muted py-4">
                      {hayFiltros ? (
                        <>
                          <p className="mb-2">No hay ventas que coincidan con los filtros.</p>
                          <button type="button" className="btn btn-sm btn-outline-secondary" onClick={grilla.limpiar} disabled={bloqueado}>
                            Limpiar filtros
                          </button>
                        </>
                      ) : (
                        'Todavía no hay ventas.'
                      )}
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>

          {pagina && (
            <PaginadorDeServidor
              pagina={pagina.pagina}
              tamanio={filtros.tamanio}
              total={pagina.total}
              sustantivo="venta"
              etiquetaDeTamanio="Ventas por página"
              inerte={filasInertes}
              onTamanio={grilla.cambiarTamanio}
              onPagina={grilla.cambiarPagina}
            />
          )}
        </>
      )}

      {filaACambiar && medios && (
        <ModalDeCambioDeMedios
          key={filaACambiar.id}
          venta={{ id: filaACambiar.id, numeroVisible: filaACambiar.numeroVisible }}
          medios={medios}
          modo="administracion"
          onGuardado={alGuardarCambioDeMedios}
          onCerrar={cerrarCambioDeMedios}
        />
      )}
    </Box>
  )
}
