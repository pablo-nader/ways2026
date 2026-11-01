import { useEffect, useRef, useState } from 'react'
import {
  clienteDeCaja,
  esColumnaOrdenableDeRetiros,
  filtrosDeRetirosVacios,
  type FiltrosDeRetiros,
  type OrdenDeRetiros,
} from '../api/caja'
import { clienteDeOrganizacion } from '../api/organizacion'
import { puedeGestionarCatalogos, type FilaDeRetiro, type PuntoVentaListado } from '../api/tipos'
import { useAuth } from '../auth/useAuth'
import { Box } from '../componentes/Box'
import { BotonIcono } from '../componentes/BotonIcono'
import { CampoFecha } from '../componentes/CampoFecha'
import { CampoImporte } from '../componentes/CampoImporte'
import { Cargando } from '../componentes/Cargando'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'
import { EncabezadoOrdenable } from '../componentes/grilla'
import { PaginadorDeServidor } from '../componentes/grilla/PaginadorDeServidor'
import { useGrillaDeServidor } from '../componentes/grilla/useGrillaDeServidor'
import { fechaHoraATexto } from '../formato/fechas'
import { formatearImporte } from '../formato/importes'
import { toast } from '../toasts/toasts'
import { ModalDeEdicionDeRetiro } from './ModalDeEdicionDeRetiro'
import { useOpcionesDeEmpleado } from './useOpcionesDeEmpleado'
import { filaQuedoObsoleta, mensajeDeErrorDeRetiro, motivoDeNoModificarRetiro } from './utilidadesRetirosDeAdministracion'
import { booleanoDeSelect, etiquetaDeEstadoDeTurno, SIN_DATO, valorDeSelectBooleano } from './utilidadesVentasDeAdministracion'

const CLAVES_DE_BORRADOR = ['idTurnoCaja', 'importeDesde', 'importeHasta', 'motivo'] as const satisfies readonly (keyof FiltrosDeRetiros)[]
const SIN_FILAS: readonly FilaDeRetiro[] = []

function consultarRetiros(filtros: FiltrosDeRetiros, orden: OrdenDeRetiros) {
  return clienteDeCaja.listarRetiros(filtros, orden)
}

function valorDeSelectNumerico(valor: number | null): string {
  return valor === null ? '' : String(valor)
}

/**
 * Retiros de caja de la administración: todos los retiros del tenant con un filtro y un orden en cada columna, resueltos en el
 * servidor. Supervisor y Admin leen; solo Admin edita (importe y motivo) o elimina un retiro, salvo el de cierre de turno y los de
 * una fecha ya conciliada. La edición y la baja dejan inerte la pantalla desde el clic hasta que el refresco posterior aterrizó: se
 * bloquea el supersede en vez de reconciliarlo con tokens. El doble clic sobre Confirmar lo absorbe `ConfirmacionDeBaja` y el de
 * Guardar, el ref del modal de edición.
 */
export function RetirosDeAdministracion() {
  const { usuario } = useAuth()
  const puedeEditar = usuario !== null && puedeGestionarCatalogos(usuario.rolId)

  const grilla = useGrillaDeServidor({
    filtrosVacios: filtrosDeRetirosVacios,
    clavesDeBorrador: CLAVES_DE_BORRADOR,
    consultar: consultarRetiros,
    esColumna: esColumnaOrdenableDeRetiros,
    mensajeDeError: 'No se pudieron cargar los retiros.',
  })
  const { filtros, borrador, orden, pagina, cargando, cargaInicialHecha, error } = grilla

  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[]>([])
  const [errorPuntosVenta, setErrorPuntosVenta] = useState(false)
  const empleados = useOpcionesDeEmpleado(puedeEditar, pagina?.items ?? SIN_FILAS)

  const [edicion, setEdicion] = useState<FilaDeRetiro | null>(null)
  const [baja, setBaja] = useState<FilaDeRetiro | null>(null)
  const [disparadorDeLaBaja, setDisparadorDeLaBaja] = useState<HTMLElement | null>(null)
  const [eliminando, setEliminando] = useState(false)
  const [errorBaja, setErrorBaja] = useState('')
  const disparadorRef = useRef<HTMLElement | null>(null)
  const limpiarRef = useRef<HTMLButtonElement>(null)
  const focoPendienteRef = useRef(false)

  useEffect(() => {
    let vigente = true
    clienteDeOrganizacion
      .listarPuntosVenta()
      .then((lista) => {
        if (vigente) setPuntosVenta(lista)
      })
      .catch(() => {
        if (vigente) setErrorPuntosVenta(true)
      })

    return () => {
      vigente = false
    }
  }, [])

  const bloqueado = edicion !== null || baja !== null || eliminando
  const filasInertes = bloqueado || cargando

  useEffect(() => {
    if (bloqueado || !focoPendienteRef.current) return
    focoPendienteRef.current = false
    const disparador = disparadorRef.current
    const destino = disparador?.isConnected && !disparador.matches(':disabled') ? disparador : limpiarRef.current
    destino?.focus()
  }, [bloqueado, cargando])

  function pedirEdicion(fila: FilaDeRetiro, disparador: HTMLElement) {
    disparadorRef.current = disparador
    setEdicion(fila)
  }

  function cerrarEdicion() {
    focoPendienteRef.current = true
    setEdicion(null)
  }

  /** El servidor ya corrigió el retiro: se avisa y se vuelve a pedir el listado con los mismos filtros (un fallo de ese refresco
   * tiene su propio aviso y no desmiente la corrección). */
  function alGuardarEdicion() {
    toast.exito('Retiro actualizado.')
    cerrarEdicion()
    void grilla.recargar()
  }

  function pedirBaja(fila: FilaDeRetiro, disparador: HTMLElement) {
    setDisparadorDeLaBaja(disparador)
    setErrorBaja('')
    setBaja(fila)
  }

  function cancelarBaja() {
    setDisparadorDeLaBaja(null)
    setBaja(null)
    setErrorBaja('')
  }

  async function confirmarBaja() {
    if (!baja) return
    setEliminando(true)
    setErrorBaja('')
    try {
      try {
        await clienteDeCaja.eliminarRetiro(baja.id)
      } catch (e) {
        setErrorBaja(mensajeDeErrorDeRetiro(e, 'eliminar'))
        if (filaQuedoObsoleta(e)) await grilla.recargar()
        return
      }
      setBaja(null)
      setDisparadorDeLaBaja(null)
      toast.exito('Retiro eliminado.')
      await grilla.recargar()
    } finally {
      setEliminando(false)
    }
  }

  const hayFiltros =
    filtros.desde !== '' ||
    filtros.hasta !== '' ||
    filtros.idPuntoVenta !== null ||
    filtros.idTurnoCaja.trim() !== '' ||
    filtros.idEmpleado !== null ||
    filtros.importeDesde !== null ||
    filtros.importeHasta !== null ||
    filtros.motivo.trim() !== '' ||
    filtros.turnoCerrado !== null ||
    filtros.conciliado !== null ||
    filtros.esRetiroDeCierre !== null

  const encabezado = (columna: string, titulo: string, clase?: string) => (
    <EncabezadoOrdenable columna={columna} orden={orden} onAlternar={grilla.alternarOrden} className={clase}>
      {titulo}
    </EncabezadoOrdenable>
  )

  return (
    <Box titulo="Retiros" variante="inverse">
      {error && (
        <div className="alert alert-danger d-flex justify-content-between align-items-center gap-2" role="alert">
          <span>{error}</span>
          <button type="button" className="btn btn-sm btn-outline-danger" onClick={() => void grilla.recargar()}>
            Reintentar
          </button>
        </div>
      )}
      {errorPuntosVenta && (
        <div className="alert alert-warning" role="alert">
          No se pudieron cargar los puntos de venta: el filtro por punto de venta no tiene opciones.
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
                  {encabezado('puntoVenta', 'Punto de venta')}
                  {encabezado('turno', 'Turno')}
                  {encabezado('empleado', 'Empleado')}
                  {encabezado('importe', 'Importe', 'text-end')}
                  {encabezado('motivo', 'Motivo')}
                  {encabezado('estadoTurno', 'Estado del turno')}
                  {encabezado('conciliado', 'Conciliado')}
                  {encabezado('esRetiroDeCierre', 'Retiro de cierre')}
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
                    <select
                      className="form-select form-select-sm"
                      aria-label="Filtrar por punto de venta"
                      value={valorDeSelectNumerico(filtros.idPuntoVenta)}
                      onChange={(e) => grilla.cambiarFiltro({ idPuntoVenta: e.target.value === '' ? null : Number(e.target.value) })}
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
                    <select
                      className="form-select form-select-sm"
                      aria-label="Filtrar por empleado"
                      value={valorDeSelectNumerico(filtros.idEmpleado)}
                      onChange={(e) => grilla.cambiarFiltro({ idEmpleado: e.target.value === '' ? null : Number(e.target.value) })}
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
                    <div className="d-flex gap-1">
                      <CampoImporte
                        aria-label="Importe desde"
                        placeholder="Desde"
                        className="form-control form-control-sm"
                        valor={borrador.importeDesde}
                        onChange={(valor) => grilla.cambiarBorrador({ importeDesde: valor })}
                      />
                      <CampoImporte
                        aria-label="Importe hasta"
                        placeholder="Hasta"
                        className="form-control form-control-sm"
                        valor={borrador.importeHasta}
                        onChange={(valor) => grilla.cambiarBorrador({ importeHasta: valor })}
                      />
                    </div>
                  </th>
                  <th scope="col">
                    <input
                      type="search"
                      className="form-control form-control-sm"
                      aria-label="Filtrar por motivo"
                      placeholder="Motivo"
                      value={borrador.motivo}
                      onChange={(e) => grilla.cambiarBorrador({ motivo: e.target.value })}
                    />
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
                  <th scope="col">
                    <select
                      className="form-select form-select-sm"
                      aria-label="Filtrar por retiro de cierre"
                      value={valorDeSelectBooleano(filtros.esRetiroDeCierre)}
                      onChange={(e) => grilla.cambiarFiltro({ esRetiroDeCierre: booleanoDeSelect(e.target.value) })}
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
                {pagina?.items.map((r) => {
                  const motivoDeBloqueo = motivoDeNoModificarRetiro(r)
                  const titulo = (porDefecto: string) => motivoDeBloqueo ?? porDefecto

                  return (
                    <tr key={r.id}>
                      <td className="text-nowrap">{fechaHoraATexto(r.fecha)}</td>
                      <td>{r.puntoVenta ?? <span className="text-body-secondary">{SIN_DATO}</span>}</td>
                      <td>{r.idTurnoCaja}</td>
                      <td>{r.empleado ?? <span className="text-body-secondary">{SIN_DATO}</span>}</td>
                      <td className="text-end text-nowrap">{formatearImporte(r.importe, { simbolo: true })}</td>
                      <td>{r.motivo}</td>
                      <td>{etiquetaDeEstadoDeTurno(r.turnoCerrado)}</td>
                      <td>{r.conciliado ? 'Sí' : 'No'}</td>
                      <td>{r.esRetiroDeCierre ? 'Sí' : 'No'}</td>
                      <td className="text-end text-nowrap">
                        {puedeEditar && (
                          <>
                            <span title={titulo('Editar')}>
                              <BotonIcono
                                icono="editar"
                                etiqueta={`Editar el retiro de ${r.motivo}`}
                                title={titulo('Editar')}
                                className="me-1"
                                disabled={filasInertes || motivoDeBloqueo !== null}
                                onClick={(evento) => pedirEdicion(r, evento.currentTarget)}
                              />
                            </span>
                            <span title={titulo('Eliminar')}>
                              <BotonIcono
                                icono="eliminar"
                                etiqueta={`Eliminar el retiro de ${r.motivo}`}
                                title={titulo('Eliminar')}
                                disabled={filasInertes || motivoDeBloqueo !== null}
                                onClick={(evento) => pedirBaja(r, evento.currentTarget)}
                              />
                            </span>
                          </>
                        )}
                      </td>
                    </tr>
                  )
                })}
                {pagina !== null && pagina.items.length === 0 && (
                  <tr>
                    <td colSpan={10} className="text-center text-muted py-4">
                      {hayFiltros ? (
                        <>
                          <p className="mb-2">No hay retiros que coincidan con los filtros.</p>
                          <button type="button" className="btn btn-sm btn-outline-secondary" onClick={grilla.limpiar} disabled={bloqueado}>
                            Limpiar filtros
                          </button>
                        </>
                      ) : (
                        'Todavía no hay retiros.'
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
              sustantivo="retiro"
              etiquetaDeTamanio="Retiros por página"
              inerte={filasInertes}
              onTamanio={grilla.cambiarTamanio}
              onPagina={grilla.cambiarPagina}
            />
          )}
        </>
      )}

      {edicion && (
        <ModalDeEdicionDeRetiro
          key={edicion.id}
          retiro={edicion}
          onGuardado={alGuardarEdicion}
          onObsoleto={() => void grilla.recargar()}
          onCerrar={cerrarEdicion}
        />
      )}

      {baja && (
        <ConfirmacionDeBaja
          titulo={`el retiro de ${formatearImporte(baja.importe, { simbolo: true })} (${baja.motivo})`}
          pregunta="Eliminar"
          nota={baja.turnoCerrado ? 'El turno está cerrado: al eliminar se recalcula el arqueo.' : null}
          etiquetaConfirmar="Confirmar eliminación"
          etiquetaEnCurso="Eliminando…"
          ocupado={eliminando}
          disparador={disparadorDeLaBaja}
          error={errorBaja}
          onConfirmar={() => void confirmarBaja()}
          onCancelar={cancelarBaja}
        />
      )}
    </Box>
  )
}
