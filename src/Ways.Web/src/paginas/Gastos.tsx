import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate } from 'react-router'
import {
  aSolicitudDeGastoDeAdministracion,
  clienteDeGastos,
  clienteDeGastosDeAdministracion,
  filtrosDeGastosDeAdministracionVacios,
  formularioDeGastoDeAdministracionCompleto,
  formularioDeGastoDeAdministracionVacio,
  categoriaAlElegirProveedorAdministracion,
  type FiltrosDeGastosDeAdministracion,
  type FormularioDeGastoDeAdministracion,
} from '../api/gastos'
import { clienteDeCatalogo } from '../api/catalogos'
import { clienteDeCompras, filtrosDeComprasVacios } from '../api/compras'
import { clienteDeOrganizacion } from '../api/organizacion'
import { clienteDeProveedores } from '../api/proveedores'
import { ErrorApi } from '../api/cliente'
import { CATEGORIAS_GASTO, ORIGENES_DE_FONDOS_GASTO } from '../api/tipos'
import type {
  AreaListado,
  AreaAlta,
  CategoriaGasto,
  CompraListada,
  EmpresaListado,
  MedioPagoAlta,
  MedioPagoListado,
  OpcionDeProveedor,
  OrigenFondosGasto,
  PaginaDeGastosDeAdministracion,
  PuntoVentaListado,
} from '../api/tipos'
import { Box } from '../componentes/Box'
import { CampoImporte } from '../componentes/CampoImporte'
import { Cargando } from '../componentes/Cargando'
import { etiquetaDeProveedor, ordenarProveedoresPorEtiqueta } from './articulos/helpers'
import { formatearImporte } from '../formato/importes'

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
 * `utilidadesGastosDelTurno.mediosValidosParaGasto` (POS): la cuenta corriente es el crédito del
 * CLIENTE en una venta, nunca una salida real de dinero de una caja/tesorería. Duplicado acá
 * (sibling helper) porque esa función vive en un módulo nombrado para el POS. */
function mediosValidosParaGastoDeAdministracion(medios: MedioPagoListado[]): MedioPagoListado[] {
  return medios.filter((m) => m.comportamiento !== 'CuentaCorriente')
}

/**
 * "Gastos" (administración) — stage-gastos-admin-retroactivos, PR3, owner's use case 2: el admin
 * registra en cualquier momento un gasto SIN turno, pagado directo de la tesorería (caja general)
 * de la empresa, incluida una fecha RETROACTIVA. Ruta admin-only (`GestionDeCatalogo` del lado
 * del servidor y `RutaProtegida rolesPermitidos={[ROL.Admin]}` acá) — a diferencia de "Gastos del
 * turno" (POS), esta pantalla no depende del punto de venta de sesión: empresa es el filtro/campo
 * ancla, punto de venta es opcional.
 *
 * react-async-state: `generacionRef` compartido entre la carga del listado y el alta — una
 * respuesta desactualizada de cualquiera de las dos nunca pisa un estado más nuevo (regla 3); el
 * formulario entero queda inerte mientras un alta está en vuelo (regla 5); un doble click en el
 * mismo tick lo bloquea `guardandoRef` antes de que el re-render deshabilite el botón (regla 11).
 */
export function Gastos() {
  const navigate = useNavigate()
  const [empresas, setEmpresas] = useState<EmpresaListado[] | null>(null)
  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[] | null>(null)
  const [medios, setMedios] = useState<MedioPagoListado[] | null>(null)
  const [areas, setAreas] = useState<AreaListado[] | null>(null)
  const [proveedores, setProveedores] = useState<OpcionDeProveedor[] | null>(null)
  const [errorCatalogos, setErrorCatalogos] = useState('')

  const [filtros, setFiltros] = useState<FiltrosDeGastosDeAdministracion>(filtrosDeGastosDeAdministracionVacios())
  const [pagina, setPagina] = useState<PaginaDeGastosDeAdministracion | null>(null)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')

  const [mostrarFormulario, setMostrarFormulario] = useState(false)
  const [formulario, setFormulario] = useState<FormularioDeGastoDeAdministracion>(() =>
    formularioDeGastoDeAdministracionVacio(fechaDeHoy()),
  )
  const [guardando, setGuardando] = useState(false)
  const guardandoRef = useRef(false)
  const [errorGuardar, setErrorGuardar] = useState('')
  const [aviso, setAviso] = useState('')

  const generacionRef = useRef(0)

  // ---- stage-gasto-a-compra (PR4): picker "Vincular a compra" para un gasto sin compra ligada --
  const [pickerGastoId, setPickerGastoId] = useState<number | null>(null)
  const [pickerCompras, setPickerCompras] = useState<CompraListada[] | null>(null)
  const [pickerError, setPickerError] = useState('')
  const [vinculando, setVinculando] = useState(false)
  const vinculandoRef = useRef(false)
  const generacionPickerRef = useRef(0)

  function abrirPicker(gasto: PaginaDeGastosDeAdministracion['items'][number]) {
    setPickerGastoId(gasto.id)
    setPickerCompras(null)
    setPickerError('')

    const miGeneracion = (generacionPickerRef.current += 1)
    clienteDeCompras
      .listar({ ...filtrosDeComprasVacios(), idProveedor: gasto.idProveedor, estado: 'Confirmada', tamanio: 50 })
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
    setPickerGastoId(null)
    setPickerCompras(null)
    setPickerError('')
  }

  async function vincularACompra(idComprobanteCompra: number) {
    if (vinculandoRef.current || pickerGastoId === null) return
    vinculandoRef.current = true
    setVinculando(true)
    setPickerError('')

    const idGasto = pickerGastoId
    const miGeneracion = (generacionRef.current += 1)

    try {
      await clienteDeGastos.vincularCompra(idGasto, idComprobanteCompra)
      if (generacionRef.current !== miGeneracion) return
      cerrarPicker()
      setAviso('Gasto vinculado a la compra.')
    } catch (e) {
      if (generacionRef.current !== miGeneracion) return
      setPickerError(e instanceof ErrorApi ? e.message : 'No se pudo vincular el gasto a la compra.')
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

    clienteDeGastosDeAdministracion
      .listar(filtros)
      .then((datos) => {
        if (generacionRef.current !== miGeneracion) return
        setPagina(datos)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setPagina(null)
        setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los gastos.')
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargando(false)
      })
  }, [filtros])

  useEffect(() => {
    cargar()
  }, [cargar])

  function cambiarFiltro(cambios: Partial<Omit<FiltrosDeGastosDeAdministracion, 'pagina' | 'tamanio'>>) {
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
    setFormulario((prev) => formularioDeGastoDeAdministracionVacio(fechaDeHoy(), prev.idEmpresa))
  }

  async function registrarGasto() {
    // regla 11: guard de reentrancia de primera línea.
    if (guardandoRef.current) return

    setAviso('')

    if (!formularioDeGastoDeAdministracionCompleto(formulario)) {
      setErrorGuardar('Completá fecha, empresa, medio de pago, concepto e importe (mayor a 0).')
      return
    }
    if (formulario.fecha > fechaDeHoy()) {
      setErrorGuardar('La fecha del gasto no puede ser futura.')
      return
    }

    guardandoRef.current = true
    setGuardando(true)
    setErrorGuardar('')

    // regla 3: bumpea la generación ANTES de la escritura — un refresco del listado en vuelo
    // desde antes de este alta queda obsoleto aunque su respuesta llegue después.
    const miGeneracion = (generacionRef.current += 1)

    try {
      await clienteDeGastosDeAdministracion.registrar(aSolicitudDeGastoDeAdministracion(formulario))
      if (generacionRef.current !== miGeneracion) return
      limpiarFormulario()
      setAviso('Gasto registrado.')
      setMostrarFormulario(false)
    } catch (e) {
      if (generacionRef.current !== miGeneracion) return
      setErrorGuardar(e instanceof ErrorApi ? e.message : 'No se pudo registrar el gasto.')
      return
    } finally {
      guardandoRef.current = false
      if (generacionRef.current === miGeneracion) setGuardando(false)
    }

    cargar()
  }

  const puntosVentaDeLaEmpresaDelFiltro = useMemo(
    () => (puntosVenta ?? []).filter((pv) => filtros.idEmpresa === null || pv.idEmpresa === filtros.idEmpresa),
    [puntosVenta, filtros.idEmpresa],
  )
  const puntosVentaDeLaEmpresaDelFormulario = useMemo(
    () => (puntosVenta ?? []).filter((pv) => formulario.idEmpresa === '' || pv.idEmpresa === formulario.idEmpresa),
    [puntosVenta, formulario.idEmpresa],
  )
  const mediosParaGasto = mediosValidosParaGastoDeAdministracion(medios ?? [])
  const proveedoresOrdenados = ordenarProveedoresPorEtiqueta(proveedores ?? [])
  const proveedorPorId = useMemo(() => {
    const indice: Record<number, OpcionDeProveedor> = {}
    for (const p of proveedores ?? []) indice[p.id] = p
    return indice
  }, [proveedores])

  const totalPaginas = pagina ? Math.max(1, Math.ceil(pagina.total / pagina.tamanio)) : 1

  const herramientas = (
    <nav className="p-2 d-flex gap-2">
      <button
        type="button"
        className="btn btn-sm btn-success rounded-0 text-nowrap"
        onClick={() => setMostrarFormulario((prev) => !prev)}
      >
        {mostrarFormulario ? 'Cancelar' : 'Nuevo gasto'}
      </button>
    </nav>
  )

  return (
    <div className="container-fluid py-4">
      <Box titulo="Gastos" variante="inverse" herramientas={herramientas}>
        {errorCatalogos && <div className="alert alert-warning rounded-0 py-1 px-2 small">{errorCatalogos}</div>}
        {aviso && <div className="alert alert-success rounded-0">{aviso}</div>}

        {mostrarFormulario && (
          <fieldset disabled={guardando} className="row g-2 align-items-end border p-3 mb-3 bg-white m-0">
            {errorGuardar && (
              <div className="col-12">
                <div className="alert alert-danger rounded-0 py-1 px-2 small mb-2">{errorGuardar}</div>
              </div>
            )}

            <div className="col-md-2">
              <label className="form-label" htmlFor="gasto-admin-fecha">
                Fecha
              </label>
              <input
                id="gasto-admin-fecha"
                type="date"
                className="form-control rounded-0"
                max={fechaDeHoy()}
                value={formulario.fecha}
                onChange={(e) => setFormulario((prev) => ({ ...prev, fecha: e.target.value }))}
              />
            </div>

            <div className="col-md-2">
              <label className="form-label" htmlFor="gasto-admin-empresa">
                Empresa
              </label>
              <select
                id="gasto-admin-empresa"
                className="form-select rounded-0"
                value={formulario.idEmpresa}
                onChange={(e) =>
                  setFormulario((prev) => ({
                    ...prev,
                    idEmpresa: e.target.value === '' ? '' : Number(e.target.value),
                    idPuntoVenta: '',
                  }))
                }
              >
                <option value="">Elegir…</option>
                {(empresas ?? []).map((emp) => (
                  <option key={emp.id} value={emp.id}>
                    {emp.razonSocial}
                  </option>
                ))}
              </select>
            </div>

            <div className="col-md-2">
              <label className="form-label" htmlFor="gasto-admin-pv">
                Punto de venta (opcional)
              </label>
              <select
                id="gasto-admin-pv"
                className="form-select rounded-0"
                value={formulario.idPuntoVenta}
                onChange={(e) => setFormulario((prev) => ({ ...prev, idPuntoVenta: e.target.value === '' ? '' : Number(e.target.value) }))}
              >
                <option value="">Sin punto de venta</option>
                {puntosVentaDeLaEmpresaDelFormulario.map((pv) => (
                  <option key={pv.id} value={pv.id}>
                    {pv.nombre}
                  </option>
                ))}
              </select>
            </div>

            <div className="col-md-2">
              <label className="form-label" htmlFor="gasto-admin-categoria">
                Categoría
              </label>
              <select
                id="gasto-admin-categoria"
                className="form-select rounded-0"
                value={formulario.categoria}
                onChange={(e) => setFormulario((prev) => ({ ...prev, categoria: e.target.value as CategoriaGasto }))}
              >
                {CATEGORIAS_GASTO.map((c) => (
                  <option key={c.valor} value={c.valor}>
                    {c.etiqueta}
                  </option>
                ))}
              </select>
            </div>

            <div className="col-md-2">
              <label className="form-label" htmlFor="gasto-admin-proveedor">
                Proveedor (opcional)
              </label>
              <select
                id="gasto-admin-proveedor"
                className="form-select rounded-0"
                value={formulario.idProveedor}
                onChange={(e) => alCambiarProveedorDelFormulario(e.target.value)}
              >
                <option value="">Sin proveedor</option>
                {proveedoresOrdenados.map((p) => (
                  <option key={p.id} value={p.id}>
                    {etiquetaDeProveedor(p)}
                  </option>
                ))}
              </select>
            </div>

            <div className="col-md-2">
              <label className="form-label" htmlFor="gasto-admin-area">
                Área (opcional)
              </label>
              <select
                id="gasto-admin-area"
                className="form-select rounded-0"
                value={formulario.idArea}
                onChange={(e) => setFormulario((prev) => ({ ...prev, idArea: e.target.value === '' ? '' : Number(e.target.value) }))}
              >
                <option value="">Sin área</option>
                {(areas ?? []).map((a) => (
                  <option key={a.id} value={a.id}>
                    {a.nombre}
                  </option>
                ))}
              </select>
            </div>

            <div className="col-md-3">
              <label className="form-label" htmlFor="gasto-admin-concepto">
                Concepto
              </label>
              <input
                id="gasto-admin-concepto"
                type="text"
                className="form-control rounded-0"
                value={formulario.concepto}
                onChange={(e) => setFormulario((prev) => ({ ...prev, concepto: e.target.value }))}
              />
            </div>

            <div className="col-md-3">
              <label className="form-label" htmlFor="gasto-admin-detalle">
                Detalle (opcional)
              </label>
              <input
                id="gasto-admin-detalle"
                type="text"
                className="form-control rounded-0"
                value={formulario.detalle}
                onChange={(e) => setFormulario((prev) => ({ ...prev, detalle: e.target.value }))}
              />
            </div>

            <div className="col-md-2">
              <label className="form-label" htmlFor="gasto-admin-medio-pago">
                Medio de pago
              </label>
              <select
                id="gasto-admin-medio-pago"
                className="form-select rounded-0"
                value={formulario.idMedioPago}
                onChange={(e) => setFormulario((prev) => ({ ...prev, idMedioPago: e.target.value === '' ? '' : Number(e.target.value) }))}
              >
                <option value="">Elegir…</option>
                {mediosParaGasto.map((m) => (
                  <option key={m.id} value={m.id}>
                    {m.nombre}
                  </option>
                ))}
              </select>
            </div>

            <div className="col-md-2">
              <label className="form-label" htmlFor="gasto-admin-factura">
                N° de factura (opcional)
              </label>
              <input
                id="gasto-admin-factura"
                type="text"
                className="form-control rounded-0"
                value={formulario.numeroFactura}
                onChange={(e) => setFormulario((prev) => ({ ...prev, numeroFactura: e.target.value }))}
              />
            </div>

            <div className="col-md-2">
              <label className="form-label" htmlFor="gasto-admin-importe">
                Importe
              </label>
              <CampoImporte
                id="gasto-admin-importe"
                className="form-control rounded-0"
                valor={formulario.importe}
                onChange={(v) => setFormulario((prev) => ({ ...prev, importe: v }))}
              />
            </div>

            <div className="col-md-2">
              <button type="button" className="btn btn-primary rounded-0 w-100" onClick={() => void registrarGasto()}>
                {guardando ? 'Guardando…' : 'Registrar'}
              </button>
            </div>
          </fieldset>
        )}

        <div className="row g-2 align-items-end mb-3">
          <div className="col-md-2">
            <label className="form-label" htmlFor="gastos-admin-filtro-empresa">
              Empresa
            </label>
            <select
              id="gastos-admin-filtro-empresa"
              className="form-select rounded-0"
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
            <label className="form-label" htmlFor="gastos-admin-filtro-pv">
              Punto de venta
            </label>
            <select
              id="gastos-admin-filtro-pv"
              className="form-select rounded-0"
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
            <label className="form-label" htmlFor="gastos-admin-filtro-categoria">
              Categoría
            </label>
            <select
              id="gastos-admin-filtro-categoria"
              className="form-select rounded-0"
              value={filtros.categoria ?? ''}
              onChange={(e) => cambiarFiltro({ categoria: e.target.value === '' ? null : (e.target.value as CategoriaGasto) })}
            >
              <option value="">Todas</option>
              {CATEGORIAS_GASTO.map((c) => (
                <option key={c.valor} value={c.valor}>
                  {c.etiqueta}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-2">
            <label className="form-label" htmlFor="gastos-admin-filtro-origen">
              Origen
            </label>
            <select
              id="gastos-admin-filtro-origen"
              className="form-select rounded-0"
              value={filtros.origenFondos ?? ''}
              onChange={(e) => cambiarFiltro({ origenFondos: e.target.value === '' ? null : (e.target.value as OrigenFondosGasto) })}
            >
              <option value="">Todos</option>
              {ORIGENES_DE_FONDOS_GASTO.map((o) => (
                <option key={o.valor} value={o.valor}>
                  {o.etiqueta}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-2">
            <label className="form-label" htmlFor="gastos-admin-filtro-desde">
              Desde
            </label>
            <input
              id="gastos-admin-filtro-desde"
              type="date"
              className="form-control rounded-0"
              value={filtros.desde}
              onChange={(e) => cambiarFiltro({ desde: e.target.value })}
            />
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="gastos-admin-filtro-hasta">
              Hasta
            </label>
            <input
              id="gastos-admin-filtro-hasta"
              type="date"
              className="form-control rounded-0"
              value={filtros.hasta}
              onChange={(e) => cambiarFiltro({ hasta: e.target.value })}
            />
          </div>
        </div>

        {error && <div className="alert alert-danger rounded-0">{error}</div>}
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
                    <th>Compra</th>
                  </tr>
                </thead>
                <tbody>
                  {pagina.items.map((g) => (
                    <tr key={g.id}>
                      <td>{formatearFecha(g.fecha)}</td>
                      <td>{empresas?.find((e) => e.id === g.idEmpresa)?.razonSocial ?? `Empresa #${g.idEmpresa}`}</td>
                      <td>{g.idPuntoVenta === null ? '—' : (puntosVenta?.find((pv) => pv.id === g.idPuntoVenta)?.nombre ?? `PV #${g.idPuntoVenta}`)}</td>
                      <td>{CATEGORIAS_GASTO.find((c) => c.valor === g.categoria)?.etiqueta ?? g.categoria}</td>
                      <td>{g.idProveedor === null ? '—' : (g.nombreProveedor ?? proveedorPorId[g.idProveedor]?.razonSocial ?? '(no disponible)')}</td>
                      <td>{g.idArea === null ? '—' : (g.nombreArea ?? '(no disponible)')}</td>
                      <td>{g.concepto}</td>
                      <td>{g.nombreMedioPago ?? '(no disponible)'}</td>
                      <td>
                        <span className={`badge rounded-0 ${g.origenFondos === 'Tesoreria' ? 'text-bg-secondary' : 'text-bg-light text-dark border'}`}>
                          {ORIGENES_DE_FONDOS_GASTO.find((o) => o.valor === g.origenFondos)?.etiqueta ?? g.origenFondos}
                        </span>
                      </td>
                      <td className="text-end">{formatearMoneda(g.importe)}</td>
                      <td>
                        {g.idComprobanteCompra !== null ? (
                          <a href={`/compras/${g.idComprobanteCompra}`} onClick={(e) => { e.preventDefault(); navigate(`/compras/${g.idComprobanteCompra}`) }}>
                            Compra #{g.idComprobanteCompra}
                          </a>
                        ) : (
                          <div className="d-flex gap-1">
                            <button
                              type="button"
                              className="btn btn-sm btn-outline-primary rounded-0"
                              onClick={() => abrirPicker(g)}
                            >
                              Vincular a compra
                            </button>
                            <button
                              type="button"
                              className="btn btn-sm btn-outline-secondary rounded-0"
                              onClick={() => navigate(`/compras/nueva?desdeGasto=${g.id}`)}
                            >
                              Crear compra
                            </button>
                          </div>
                        )}
                      </td>
                    </tr>
                  ))}
                  {pagina.items.length === 0 && (
                    <tr>
                      <td colSpan={11} className="text-center text-muted py-4">
                        No hay gastos que coincidan con los filtros.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>

            <div className="d-flex justify-content-between align-items-center">
              <span className="small text-muted">
                Página {pagina.pagina} de {totalPaginas} — {pagina.total} gasto(s)
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

        {pickerGastoId !== null && (
          <div
            className="position-fixed top-0 start-0 w-100 h-100 d-flex align-items-center justify-content-center"
            style={{ background: 'rgba(0,0,0,0.5)', zIndex: 1050 }}
          >
            <div className="bg-white p-3 border" style={{ minWidth: 420, maxWidth: 600 }}>
              <div className="d-flex justify-content-between align-items-center mb-2">
                <h5 className="m-0">Vincular a compra</h5>
                <button type="button" className="btn btn-sm btn-outline-secondary rounded-0" onClick={cerrarPicker} disabled={vinculando}>
                  Cerrar
                </button>
              </div>

              {pickerError && <div className="alert alert-danger rounded-0 py-1 px-2 small">{pickerError}</div>}

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
                        <th></th>
                      </tr>
                    </thead>
                    <tbody>
                      {pickerCompras.map((c) => (
                        <tr key={c.id}>
                          <td>{c.numeroExterno ?? `#${c.id}`}</td>
                          <td>{c.fechaRecepcion ? formatearFecha(c.fechaRecepcion) : '—'}</td>
                          <td className="text-end">{formatearMoneda(c.total)}</td>
                          <td>
                            <button type="button" className="btn btn-sm btn-primary rounded-0" onClick={() => void vincularACompra(c.id)}>
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
    </div>
  )
}
