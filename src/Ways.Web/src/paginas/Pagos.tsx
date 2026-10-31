import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import {
  advertenciasDePagoDeAdministracion,
  aSolicitudDePagoDeAdministracion,
  clienteDePagos,
  clienteDePagosDeAdministracion,
  filtrosDePagosDeAdministracionVacios,
  formularioDePagoDeAdministracionCompleto,
  formularioDePagoDeAdministracionVacio,
  categoriaAlElegirProveedorAdministracion,
  copiaDeFalloDeEdicion,
  esPagoOriginadoEnPersonal,
  MOTIVO_PAGO_ORIGINADO_EN_PERSONAL,
  type FiltrosDePagosDeAdministracion,
  type FormularioDePagoDeAdministracion,
} from '../api/registroDePagos'
import { clienteDeCatalogo } from '../api/catalogos'
import { clienteDeCompras, filtrosDeComprasVacios } from '../api/compras'
import { clienteDeOrganizacion } from '../api/organizacion'
import { clienteDeProveedores } from '../api/proveedores'
import { ErrorApi } from '../api/cliente'
import { CATEGORIAS_PAGO, ORIGENES_DE_FONDOS_PAGO } from '../api/tipos'
import type {
  AreaListado,
  AreaAlta,
  CategoriaPago,
  CompraListada,
  EmpresaListado,
  PagoDeAdministracionListado,
  MedioPagoAlta,
  MedioPagoListado,
  OpcionDeProveedor,
  OrigenFondosPago,
  PaginaDePagosDeAdministracion,
  PuntoVentaListado,
  SolicitudDeEdicionDePagoConLineas,
} from '../api/tipos'
import { Box } from '../componentes/Box'
import { BotonIcono } from '../componentes/BotonIcono'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'
import { Cargando } from '../componentes/Cargando'
import { ordenarProveedoresPorEtiqueta } from './articulos/helpers'
import { formatearImporte } from '../formato/importes'
import { ModalDeEdicionDePago } from './ModalDeEdicionDePago'
import { ModalDeAltaDePago } from './pagos/ModalDeAltaDePago'
import { areaPorDefecto } from './utilidadesPagosDelTurno'
import { errorDeLineasDePago, textoDeMediosDelPago } from '../api/lineasDePago'
import { CampoFecha } from '../componentes/CampoFecha'
import { pluralizar } from '../formato/pluralizar'

const clienteMediosPago = clienteDeCatalogo<MedioPagoListado, MedioPagoAlta>('medios-pago')
const clienteAreas = clienteDeCatalogo<AreaListado, AreaAlta>('areas')

function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

function formatearFecha(iso: string): string {
  return new Date(iso).toLocaleDateString('es-AR')
}

function fechaDeHoy(): string {
  const ahora = new Date()
  const anio = ahora.getFullYear()
  const mes = String(ahora.getMonth() + 1).padStart(2, '0')
  const dia = String(ahora.getDate()).padStart(2, '0')
  return `${anio}-${mes}-${dia}`
}

/** Medio de pago excluye `CuentaCorriente`, mismo criterio y mismo motivo que
 * `utilidadesPagosDelTurno.mediosValidosParaPago` (POS): la cuenta corriente es el crédito del
 * CLIENTE en una venta, nunca una salida real de dinero de una caja/tesorería. Duplicado acá
 * (sibling helper) porque esa función vive en un módulo nombrado para el POS. */
function mediosValidosParaPagoDeAdministracion(medios: MedioPagoListado[]): MedioPagoListado[] {
  return medios.filter((m) => m.comportamiento !== 'CuentaCorriente')
}

/**
 * "Pagos" (administración) — stage-gastos-admin-retroactivos, PR3, owner's use case 2: el admin
 * registra en cualquier momento un pago SIN turno, pagado directo de la tesorería (caja general)
 * de la empresa, incluida una fecha RETROACTIVA. Ruta admin-only (`GestionDeCatalogo` del lado
 * del servidor y `RutaProtegida rolesPermitidos={[ROL.Admin]}` acá) — a diferencia de "Pagos del
 * turno" (POS), esta pantalla no depende del punto de venta de sesión: empresa es el filtro/campo
 * ancla, punto de venta es opcional.
 *
 * react-async-state: `generacionRef` compartido entre la carga del listado y el alta — una
 * respuesta desactualizada de cualquiera de las dos nunca pisa un estado más nuevo (regla 3); el
 * formulario entero queda inerte mientras un alta está en vuelo (regla 5); un doble click en el
 * mismo tick lo bloquea `guardandoRef` antes de que el re-render deshabilite el botón (regla 11).
 *
 * Edición y baja de un pago (modal / `ConfirmacionDeBaja`): comparten `escribiendoRef` y un
 * `bloqueado` que deja inerte la pantalla entera desde el click hasta que el refresco posterior
 * aterrizó (reglas 5, 9 y 13) — se bloquea el supersede en vez de reconciliarlo con tokens. Si el
 * pago es de caja de un turno cerrado, ambos avisan que el servidor recalcula el arqueo.
 */
export function Pagos() {
  const navigate = useNavigate()
  const [empresas, setEmpresas] = useState<EmpresaListado[] | null>(null)
  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[] | null>(null)
  const [medios, setMedios] = useState<MedioPagoListado[] | null>(null)
  const [areas, setAreas] = useState<AreaListado[] | null>(null)
  const [proveedores, setProveedores] = useState<OpcionDeProveedor[] | null>(null)
  const [errorCatalogos, setErrorCatalogos] = useState('')

  const [filtros, setFiltros] = useState<FiltrosDePagosDeAdministracion>(filtrosDePagosDeAdministracionVacios())
  const [pagina, setPagina] = useState<PaginaDePagosDeAdministracion | null>(null)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')

  const [mostrarFormulario, setMostrarFormulario] = useState(false)
  const [formulario, setFormulario] = useState<FormularioDePagoDeAdministracion>(() =>
    formularioDePagoDeAdministracionVacio(fechaDeHoy()),
  )
  const [guardando, setGuardando] = useState(false)
  const guardandoRef = useRef(false)
  const [errorGuardar, setErrorGuardar] = useState('')
  /** Cambia en cada rechazo: el mismo mensaje repetido igual se vuelve a anunciar y a traer a la vista. */
  const [disparoDeError, setDisparoDeError] = useState(0)
  const [aviso, setAviso] = useState('')

  const generacionRef = useRef(0)
  /** Destino de foco si el disparador ya no es alcanzable al cerrar un modal. */
  const contenedorRef = useRef<HTMLDivElement>(null)

  const [edicion, setEdicion] = useState<PagoDeAdministracionListado | null>(null)
  const [guardandoEdicion, setGuardandoEdicion] = useState(false)
  const [errorEdicion, setErrorEdicion] = useState('')
  const [baja, setBaja] = useState<PagoDeAdministracionListado | null>(null)
  const [disparadorDeLaBaja, setDisparadorDeLaBaja] = useState<HTMLElement | null>(null)
  const [eliminando, setEliminando] = useState(false)
  const [errorBaja, setErrorBaja] = useState('')
  const escribiendoRef = useRef(false)

  // ---- stage-gasto-a-compra (PR4): picker "Vincular a compra" para un pago sin compra ligada --
  const [pickerPagoId, setPickerPagoId] = useState<number | null>(null)
  const [pickerCompras, setPickerCompras] = useState<CompraListada[] | null>(null)
  const [pickerError, setPickerError] = useState('')
  const [vinculando, setVinculando] = useState(false)
  const vinculandoRef = useRef(false)
  const generacionPickerRef = useRef(0)
  // regla 10 (react-async-state): guard AISLADO de la escritura de vínculo — a diferencia del
  // `generacionRef` compartido con el listado (que `vincularACompra` usaba antes), este token es
  // propio, mismo criterio que `vinculandoPagoRef` de CompraEditor.tsx: un refresco del listado
  // en vuelo nunca puede pisar/invalidar la finalización de este vínculo puntual.
  const generacionVinculoRef = useRef(0)

  function abrirPicker(pago: PaginaDePagosDeAdministracion['items'][number]) {
    setPickerPagoId(pago.id)
    setPickerCompras(null)
    setPickerError('')

    const miGeneracion = (generacionPickerRef.current += 1)
    clienteDeCompras
      .listar({ ...filtrosDeComprasVacios(), idProveedor: pago.idProveedor, idEmpresa: pago.idEmpresa, estado: 'Confirmada', tamanio: 50 })
      .then((datos) => {
        if (generacionPickerRef.current !== miGeneracion) return
        setPickerCompras(datos.items)
      })
      .catch((e) => {
        if (generacionPickerRef.current !== miGeneracion) return
        setPickerCompras([])
        setPickerError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las compras confirmadas.')
      })
  }

  function cerrarPicker() {
    // regla 3 (react-async-state): bumpea la generación al cerrar — una respuesta del fetch en
    // vuelo (u otro vínculo en curso) nunca reabre/repuebla el picker después de cerrado.
    generacionPickerRef.current += 1
    setPickerPagoId(null)
    setPickerCompras(null)
    setPickerError('')
  }

  async function vincularACompra(idComprobanteCompra: number) {
    if (vinculandoRef.current || pickerPagoId === null) return
    vinculandoRef.current = true
    setVinculando(true)
    setPickerError('')

    const idGasto = pickerPagoId
    const miGeneracion = (generacionVinculoRef.current += 1)

    try {
      await clienteDePagos.vincularCompra(idGasto, idComprobanteCompra)
      if (generacionVinculoRef.current !== miGeneracion) return
      cerrarPicker()
      setAviso('Pago vinculado a la compra.')
    } catch (e) {
      if (generacionVinculoRef.current !== miGeneracion) return
      setPickerError(e instanceof ErrorApi ? e.message : 'No se pudo vincular el pago a la compra.')
      return
    } finally {
      vinculandoRef.current = false
      setVinculando(false)
    }

    cargar()
  }

  // Carga inicial: catálogos de tenant, independientes del filtro/formulario.
  useEffect(() => {
    let vigente = true

    Promise.all([
      clienteDeOrganizacion.listarEmpresas(),
      clienteDeOrganizacion.listarPuntosVenta(),
      clienteMediosPago.listar(false),
      clienteAreas.listar(false),
      clienteDeProveedores.opciones(),
    ])
      .then(([empresasCargadas, puntosVentaCargados, mediosCargados, areasCargadas, proveedoresCargados]) => {
        if (!vigente) return
        setEmpresas(empresasCargadas)
        setPuntosVenta(puntosVentaCargados)
        setMedios(mediosCargados)
        setAreas(areasCargadas)
        setProveedores(proveedoresCargados)
        setFormulario((prev) => (prev.idArea === '' ? { ...prev, idArea: areaPorDefecto(areasCargadas) } : prev))
        // Empresa única: se preselecciona tanto el filtro como el formulario de alta.
        if (empresasCargadas.length === 1) {
          const unica = empresasCargadas[0].id
          setFiltros((prev) => ({ ...prev, idEmpresa: unica }))
          setFormulario((prev) => ({ ...prev, idEmpresa: unica }))
        }
      })
      .catch((e) => {
        if (!vigente) return
        setEmpresas([])
        setPuntosVenta([])
        setMedios([])
        setAreas([])
        setProveedores([])
        setErrorCatalogos(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los catálogos.')
      })

    return () => {
      vigente = false
    }
  }, [])

  const cargar = useCallback(() => {
    const miGeneracion = (generacionRef.current += 1)
    setCargando(true)
    setError('')

    return clienteDePagosDeAdministracion
      .listar(filtros)
      .then((datos) => {
        if (generacionRef.current !== miGeneracion) return
        setPagina(datos)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setPagina(null)
        setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los pagos.')
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargando(false)
      })
  }, [filtros])

  useEffect(() => {
    cargar()
  }, [cargar])

  function cambiarFiltro(cambios: Partial<Omit<FiltrosDePagosDeAdministracion, 'pagina' | 'tamanio'>>) {
    setFiltros((prev) => ({ ...prev, ...cambios, pagina: 1 }))
  }

  function cambiarPagina(delta: number) {
    setFiltros((prev) => ({ ...prev, pagina: Math.max(1, prev.pagina + delta) }))
  }

  function alCambiarProveedorDelFormulario(valorCrudo: string) {
    const nuevoIdProveedor = valorCrudo === '' ? null : Number(valorCrudo)
    setFormulario((prev) => ({
      ...prev,
      idProveedor: nuevoIdProveedor ?? '',
      // regla 1 (react-async-state): construye desde `prev`, nunca desde un `categoria` de
      // closure — dos cambios de proveedor en el mismo tick no pueden pisarse entre sí.
      categoria: categoriaAlElegirProveedorAdministracion(nuevoIdProveedor, prev.categoria),
    }))
  }

  function limpiarFormulario() {
    setFormulario((prev) => ({
      ...formularioDePagoDeAdministracionVacio(fechaDeHoy(), prev.idEmpresa),
      idArea: areaPorDefecto(areas ?? []),
    }))
  }

  async function registrarPago() {
    // regla 11: guard de reentrancia de primera línea.
    if (guardandoRef.current) return

    setAviso('')

    if (!formularioDePagoDeAdministracionCompleto(formulario)) {
      setErrorGuardar(
        formulario.fecha.trim() !== '' && formulario.idEmpresa !== '' && formulario.concepto.trim() !== ''
          ? (errorDeLineasDePago(formulario.lineas) ?? 'Revisá los medios de pago.')
          : 'Completá fecha, empresa, concepto y, en cada medio de pago, el medio y un importe mayor a 0.',
      )
      setDisparoDeError((n) => n + 1)
      return
    }

    guardandoRef.current = true
    setGuardando(true)
    setErrorGuardar('')

    // regla 3: bumpea la generación ANTES de la escritura — un refresco del listado en vuelo
    // desde antes de este alta queda obsoleto aunque su respuesta llegue después.
    generacionRef.current += 1

    // La respuesta de la escritura no se gatea por la generación del listado: el modal queda inerte
    // mientras dura y nada puede reabrirlo ni reemplazarlo, así que siempre se aplica y siempre libera
    // `guardando`. Gatearla dejaba el modal trabado si otra lectura acuñaba una generación en el medio.
    try {
      await clienteDePagosDeAdministracion.registrar(aSolicitudDePagoDeAdministracion(formulario))
      limpiarFormulario()
      setAviso('Pago registrado.')
      setMostrarFormulario(false)
    } catch (e) {
      setErrorGuardar(e instanceof ErrorApi ? e.message : 'No se pudo registrar el pago.')
      setDisparoDeError((n) => n + 1)
      return
    } finally {
      guardandoRef.current = false
      setGuardando(false)
    }

    cargar()
  }

  function abrirAlta() {
    setAviso('')
    setErrorGuardar('')
    setMostrarFormulario(true)
  }

  /** El modal no ofrece cancelar con un alta en vuelo (`ocupado`), así que cerrar no supersede nada.
   * Cancelar descarta lo escrito: el próximo alta abre con los valores por defecto. */
  function cancelarAlta() {
    limpiarFormulario()
    setErrorGuardar('')
    setMostrarFormulario(false)
  }

  function ocupadoEscribiendo() {
    return escribiendoRef.current || guardandoRef.current || vinculandoRef.current
  }

  function abrirEdicion(pago: PagoDeAdministracionListado) {
    if (ocupadoEscribiendo()) return
    setAviso('')
    setErrorEdicion('')
    setEdicion(pago)
  }

  function cancelarEdicion() {
    if (escribiendoRef.current) return
    setEdicion(null)
    setErrorEdicion('')
  }

  async function guardarEdicion(solicitud: SolicitudDeEdicionDePagoConLineas) {
    if (!edicion || ocupadoEscribiendo()) return

    const idGasto = edicion.id
    escribiendoRef.current = true
    setGuardandoEdicion(true)
    setErrorEdicion('')

    // regla 3: un refresco del listado anterior a esta escritura queda obsoleto.
    generacionRef.current += 1

    try {
      try {
        await clienteDePagosDeAdministracion.actualizar(idGasto, solicitud)
      } catch (e) {
        setErrorEdicion(copiaDeFalloDeEdicion(e))
        return
      }

      setEdicion(null)
      setAviso('Pago actualizado.')
      // regla 6: el refresco está aislado de la escritura; si falla, `error` lo dice aparte y el
      // aviso de éxito se mantiene.
      await cargar()
    } finally {
      // Sin gate de token a propósito: la guarda de ref impide otra escritura en vuelo.
      escribiendoRef.current = false
      setGuardandoEdicion(false)
    }
  }

  function pedirBaja(pago: PagoDeAdministracionListado, disparador: HTMLElement | null) {
    if (ocupadoEscribiendo()) return
    setDisparadorDeLaBaja(disparador)
    setBaja(pago)
    setErrorBaja('')
    setAviso('')
  }

  function cancelarBaja() {
    if (escribiendoRef.current) return
    setDisparadorDeLaBaja(null)
    setBaja(null)
    setErrorBaja('')
  }

  async function confirmarBaja() {
    if (!baja || ocupadoEscribiendo()) return

    const idGasto = baja.id
    escribiendoRef.current = true
    setEliminando(true)
    setErrorBaja('')
    generacionRef.current += 1

    try {
      try {
        await clienteDePagosDeAdministracion.eliminar(idGasto)
      } catch (e) {
        setErrorBaja(copiaDeFalloDeEdicion(e, 'eliminar'))
        return
      }

      setBaja(null)
      setDisparadorDeLaBaja(null)
      setAviso('Pago eliminado.')
      await cargar()
    } finally {
      escribiendoRef.current = false
      setEliminando(false)
    }
  }

  const puntosVentaDeLaEmpresaDelFiltro = useMemo(
    () => (puntosVenta ?? []).filter((pv) => filtros.idEmpresa === null || pv.idEmpresa === filtros.idEmpresa),
    [puntosVenta, filtros.idEmpresa],
  )
  const puntosVentaDeLaEmpresaDelFormulario = useMemo(
    () => (puntosVenta ?? []).filter((pv) => formulario.idEmpresa === '' || pv.idEmpresa === formulario.idEmpresa),
    [puntosVenta, formulario.idEmpresa],
  )
  const mediosParaPago = mediosValidosParaPagoDeAdministracion(medios ?? [])
  const proveedoresOrdenados = ordenarProveedoresPorEtiqueta(proveedores ?? [])
  const proveedorPorId = useMemo(() => {
    const indice: Record<number, OpcionDeProveedor> = {}
    for (const p of proveedores ?? []) indice[p.id] = p
    return indice
  }, [proveedores])

  /** La edición o la baja abiertas, o cualquier escritura (y su refresco) en vuelo, dejan inerte la
   * pantalla entera: ver el comentario del componente. */
  const bloqueado =
    guardando ||
    vinculando ||
    pickerPagoId !== null ||
    guardandoEdicion ||
    eliminando ||
    edicion !== null ||
    baja !== null ||
    mostrarFormulario
  /** Las acciones de fila además esperan al refresco posterior a una escritura. */
  const filasBloqueadas = bloqueado || cargando

  const totalPaginas = pagina ? Math.max(1, Math.ceil(pagina.total / pagina.tamanio)) : 1

  const herramientas = (
    <nav className="p-2 d-flex gap-2">
      <button
        type="button"
        className="btn btn-sm btn-success text-nowrap"
        disabled={bloqueado}
        onClick={abrirAlta}
      >
        Nuevo pago
      </button>
    </nav>
  )

  return (
    <div ref={contenedorRef} tabIndex={-1} style={{ outline: 'none' }} className="container-fluid py-4">
      <Box titulo="Pagos" variante="inverse" herramientas={herramientas}>
        {errorCatalogos && <div className="alert alert-warning py-1 px-2 small">{errorCatalogos}</div>}
        {aviso && <div className="alert alert-success">{aviso}</div>}

        {baja && (
          <ConfirmacionDeBaja
            titulo={`el pago "${baja.concepto}"`}
            pregunta="Eliminar"
            nota={advertenciasDePagoDeAdministracion(baja).join(' ') || null}
            etiquetaConfirmar="Confirmar eliminación"
            etiquetaEnCurso="Eliminando…"
            ocupado={eliminando}
            disparador={disparadorDeLaBaja}
            error={errorBaja}
            onConfirmar={() => void confirmarBaja()}
            onCancelar={cancelarBaja}
          />
        )}

        {mostrarFormulario && (
          <ModalDeAltaDePago
            valor={formulario}
            onCambio={setFormulario}
            onProveedorCambio={alCambiarProveedorDelFormulario}
            empresas={empresas ?? []}
            puntosVenta={puntosVentaDeLaEmpresaDelFormulario}
            medios={mediosParaPago}
            areas={areas ?? []}
            proveedores={proveedoresOrdenados}
            fechaMaxima={fechaDeHoy()}
            error={errorGuardar}
            disparoDeError={disparoDeError}
            guardando={guardando}
            focoDeReserva={contenedorRef}
            onRegistrar={() => void registrarPago()}
            onCancelar={cancelarAlta}
          />
        )}

        <fieldset disabled={bloqueado} className="row g-2 align-items-end mb-3 border-0 p-0 m-0">
          <div className="col-md-2">
            <label className="form-label" htmlFor="pagos-admin-filtro-empresa">
              Empresa
            </label>
            <select
              id="pagos-admin-filtro-empresa"
              className="form-select"
              value={filtros.idEmpresa ?? ''}
              onChange={(e) => cambiarFiltro({ idEmpresa: e.target.value === '' ? null : Number(e.target.value) })}
            >
              <option value="">Todas</option>
              {(empresas ?? []).map((emp) => (
                <option key={emp.id} value={emp.id}>
                  {emp.razonSocial}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-2">
            <label className="form-label" htmlFor="pagos-admin-filtro-pv">
              Punto de venta
            </label>
            <select
              id="pagos-admin-filtro-pv"
              className="form-select"
              value={filtros.idPuntoVenta ?? ''}
              onChange={(e) => cambiarFiltro({ idPuntoVenta: e.target.value === '' ? null : Number(e.target.value) })}
            >
              <option value="">Todos</option>
              {puntosVentaDeLaEmpresaDelFiltro.map((pv) => (
                <option key={pv.id} value={pv.id}>
                  {pv.nombre}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-2">
            <label className="form-label" htmlFor="pagos-admin-filtro-categoria">
              Categoría
            </label>
            <select
              id="pagos-admin-filtro-categoria"
              className="form-select"
              value={filtros.categoria ?? ''}
              onChange={(e) => cambiarFiltro({ categoria: e.target.value === '' ? null : (e.target.value as CategoriaPago) })}
            >
              <option value="">Todas</option>
              {CATEGORIAS_PAGO.map((c) => (
                <option key={c.valor} value={c.valor}>
                  {c.etiqueta}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-2">
            <label className="form-label" htmlFor="pagos-admin-filtro-origen">
              Origen
            </label>
            <select
              id="pagos-admin-filtro-origen"
              className="form-select"
              value={filtros.origenFondos ?? ''}
              onChange={(e) => cambiarFiltro({ origenFondos: e.target.value === '' ? null : (e.target.value as OrigenFondosPago) })}
            >
              <option value="">Todos</option>
              {ORIGENES_DE_FONDOS_PAGO.map((o) => (
                <option key={o.valor} value={o.valor}>
                  {o.etiqueta}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-2">
            <label className="form-label" htmlFor="pagos-admin-filtro-desde">
              Desde
            </label>
            <CampoFecha
              id="pagos-admin-filtro-desde"
              className="form-control"
              value={filtros.desde}
              onChange={(valor) => cambiarFiltro({ desde: valor })}
            />
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="pagos-admin-filtro-hasta">
              Hasta
            </label>
            <CampoFecha
              id="pagos-admin-filtro-hasta"
              className="form-control"
              value={filtros.hasta}
              onChange={(valor) => cambiarFiltro({ hasta: valor })}
            />
          </div>
        </fieldset>

        {error && <div className="alert alert-danger">{error}</div>}
        {cargando && !pagina && <Cargando />}

        {pagina && (
          <>
            <div className="table-responsive">
              <table className="table table-sm table-striped table-bordered align-middle">
                <thead>
                  <tr>
                    <th>Fecha</th>
                    <th>Empresa</th>
                    <th>Punto de venta</th>
                    <th>Categoría</th>
                    <th>Proveedor</th>
                    <th>Área</th>
                    <th>Concepto</th>
                    <th>Medio de pago</th>
                    <th>Origen</th>
                    <th className="text-end">Importe</th>
                    <th className="text-end">Disponible</th>
                    <th>Compra</th>
                    <th className="text-end">Acciones</th>
                  </tr>
                </thead>
                <tbody>
                  {pagina.items.map((g) => (
                    <tr key={g.id}>
                      <td>{formatearFecha(g.fecha)}</td>
                      <td>{empresas?.find((e) => e.id === g.idEmpresa)?.razonSocial ?? `Empresa #${g.idEmpresa}`}</td>
                      <td>{g.idPuntoVenta === null ? '—' : (puntosVenta?.find((pv) => pv.id === g.idPuntoVenta)?.nombre ?? `PV #${g.idPuntoVenta}`)}</td>
                      <td>{CATEGORIAS_PAGO.find((c) => c.valor === g.categoria)?.etiqueta ?? g.categoria}</td>
                      <td>{g.idProveedor === null ? '—' : (g.nombreProveedor ?? proveedorPorId[g.idProveedor]?.razonSocial ?? '(no disponible)')}</td>
                      <td>{g.idArea === null ? '—' : (g.nombreArea ?? '(no disponible)')}</td>
                      <td>{g.concepto}</td>
                      <td>{textoDeMediosDelPago(g)}</td>
                      <td>
                        <span className={`badge ${g.origenFondos === 'Tesoreria' ? 'text-bg-secondary' : 'bg-body-secondary text-body border'}`}>
                          {ORIGENES_DE_FONDOS_PAGO.find((o) => o.valor === g.origenFondos)?.etiqueta ?? g.origenFondos}
                        </span>
                      </td>
                      <td className="text-end">{formatearMoneda(g.importe)}</td>
                      <td className="text-end">
                        {g.disponible === undefined || g.disponible === null ? '—' : formatearMoneda(g.disponible)}
                      </td>
                      <td>
                        {g.idComprobanteCompra !== null ? (
                          <Link to={`/compras/${g.idComprobanteCompra}`}>
                            Compra #{g.idComprobanteCompra}
                          </Link>
                        ) : (
                          <div className="d-flex gap-1">
                            <button
                              type="button"
                              className="btn btn-sm btn-outline-primary"
                              disabled={filasBloqueadas || esPagoOriginadoEnPersonal(g)}
                              title={esPagoOriginadoEnPersonal(g) ? MOTIVO_PAGO_ORIGINADO_EN_PERSONAL : undefined}
                              onClick={() => abrirPicker(g)}
                            >
                              Vincular a compra
                            </button>
                            <button
                              type="button"
                              className="btn btn-sm btn-outline-secondary"
                              disabled={filasBloqueadas || esPagoOriginadoEnPersonal(g)}
                              title={esPagoOriginadoEnPersonal(g) ? MOTIVO_PAGO_ORIGINADO_EN_PERSONAL : undefined}
                              onClick={() => navigate(`/compras/nueva?desdePago=${g.id}`)}
                            >
                              Crear compra
                            </button>
                          </div>
                        )}
                      </td>
                      <td className="text-end text-nowrap">
                        <BotonIcono
                          icono="editar"
                          etiqueta={`Editar pago ${g.concepto}`}
                          className="me-1"
                          disabled={filasBloqueadas || esPagoOriginadoEnPersonal(g)}
                          title={esPagoOriginadoEnPersonal(g) ? MOTIVO_PAGO_ORIGINADO_EN_PERSONAL : undefined}
                          onClick={() => abrirEdicion(g)}
                        />
                        <BotonIcono
                          icono="eliminar"
                          etiqueta={`Eliminar pago ${g.concepto}`}
                          disabled={filasBloqueadas || esPagoOriginadoEnPersonal(g)}
                          title={esPagoOriginadoEnPersonal(g) ? MOTIVO_PAGO_ORIGINADO_EN_PERSONAL : undefined}
                          onClick={(evento) => pedirBaja(g, evento.currentTarget)}
                        />
                      </td>
                    </tr>
                  ))}
                  {pagina.items.length === 0 && (
                    <tr>
                      <td colSpan={13} className="text-center text-muted py-4">
                        No hay pagos que coincidan con los filtros.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>

            <div className="d-flex justify-content-between align-items-center">
              <span className="small text-muted">
                Página {pagina.pagina} de {totalPaginas} — {pluralizar(pagina.total, 'pago')}
              </span>
              <div className="d-flex gap-2">
                <button
                  type="button"
                  className="btn btn-sm btn-outline-secondary"
                  disabled={pagina.pagina <= 1 || cargando || bloqueado}
                  onClick={() => cambiarPagina(-1)}
                >
                  Anterior
                </button>
                <button
                  type="button"
                  className="btn btn-sm btn-outline-secondary"
                  disabled={pagina.pagina >= totalPaginas || cargando || bloqueado}
                  onClick={() => cambiarPagina(1)}
                >
                  Siguiente
                </button>
              </div>
            </div>
          </>
        )}

        {pickerPagoId !== null && (
          <div
            className="position-fixed top-0 start-0 w-100 h-100 d-flex align-items-center justify-content-center"
            style={{ background: 'rgba(0,0,0,0.5)', zIndex: 1050 }}
          >
            <div className="bg-body p-3 border" style={{ minWidth: 420, maxWidth: 600 }}>
              <div className="d-flex justify-content-between align-items-center mb-2">
                <h5 className="m-0">Vincular a compra</h5>
                <button type="button" className="btn btn-sm btn-outline-secondary" onClick={cerrarPicker} disabled={vinculando}>
                  Cerrar
                </button>
              </div>

              {pickerError && <div className="alert alert-danger py-1 px-2 small">{pickerError}</div>}

              {pickerCompras === null && <Cargando />}

              {pickerCompras !== null && pickerCompras.length === 0 && (
                <p className="text-muted mb-0">No hay compras confirmadas para este proveedor.</p>
              )}

              {pickerCompras !== null && pickerCompras.length > 0 && (
                <fieldset disabled={vinculando} className="table-responsive">
                  <table className="table table-sm table-striped">
                    <thead>
                      <tr>
                        <th>N° externo</th>
                        <th>Fecha</th>
                        <th className="text-end">Total</th>
                        <th className="text-end">Saldo pendiente</th>
                        <th></th>
                      </tr>
                    </thead>
                    <tbody>
                      {pickerCompras.map((c) => (
                        <tr key={c.id}>
                          <td>{c.numeroExterno ?? `#${c.id}`}</td>
                          <td>{c.fechaRecepcion ? formatearFecha(c.fechaRecepcion) : '—'}</td>
                          <td className="text-end">{formatearMoneda(c.total)}</td>
                          <td className="text-end">{formatearMoneda(c.saldoPendiente)}</td>
                          <td>
                            <button type="button" className="btn btn-sm btn-primary" onClick={() => void vincularACompra(c.id)}>
                              {vinculando ? 'Vinculando…' : 'Elegir'}
                            </button>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </fieldset>
              )}
            </div>
          </div>
        )}
      </Box>

      {edicion && (
        <ModalDeEdicionDePago
          key={edicion.id}
          pago={edicion}
          medios={mediosParaPago}
          proveedores={proveedores ?? []}
          areas={areas ?? []}
          ligadoACompra={edicion.idComprobanteCompra !== null}
          advertencias={advertenciasDePagoDeAdministracion(edicion)}
          guardando={guardandoEdicion}
          error={errorEdicion}
          onGuardarConLineas={(solicitud) => void guardarEdicion(solicitud)}
          onCancelar={cancelarEdicion}
        />
      )}
    </div>
  )
}
