import { useCallback, useEffect, useRef, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { copiaDeFalloDeBaja } from '../api/bajas'
import {
  clienteDeOrganizacion,
  ETIQUETA_SIN_DUENIO,
  etiquetaDeTenant,
  filtrarPorEmpresa,
  filtrarPorTenant,
  opcionesDeEmpresa,
  opcionesDeTenant,
  seleccionVigente,
  SIN_FILTRO,
} from '../api/organizacion'
import type { EmpresaListado, PuntoVentaListado, SucursalListado } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'
import { useAuth } from '../auth/useAuth'
import { usePuntoVenta } from '../puntoVenta/usePuntoVenta'
import { puedeGestionarCatalogos, ROL } from '../api/tipos'
import { BotonIcono } from '../componentes/BotonIcono'
import {
  EncabezadoOrdenable,
  FilaDeFiltros,
  useFiltrosDeColumna,
  useOrdenamiento,
  type ColumnasFiltrables,
  type ColumnasOrdenables,
} from '../componentes/grilla'
import { ModalDeAltaDePuntoVenta } from './organizacion/ModalDeAltaDePuntoVenta'
import { ModalDeMercadoPagoDePuntoVenta } from './organizacion/ModalDeMercadoPagoDePuntoVenta'
import { ModalDeModoDePuntoVenta } from './organizacion/ModalDeModoDePuntoVenta'
import { ModalDePuntoVenta } from './organizacion/ModalDePuntoVenta'
import { ModalDeSucursalDePuntoVenta } from './organizacion/ModalDeSucursalDePuntoVenta'
import {
  ALTA_VACIA,
  aFormularioDePuntoVenta,
  aFormularioDeSucursalDePuntoVenta,
  type FormularioDeAltaDePuntoVenta,
  type FormularioDeModoDePuntoVenta,
  type FormularioDePuntoVenta,
  type FormularioDeSucursalDePuntoVenta,
} from './organizacion/puntoVentaFormulario'
import { toast } from '../toasts/toasts'

const AVISO_REFRESCO_FALLIDO = 'Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.'
const AVISO_REFRESCO_FALLIDO_BAJA =
  'Se eliminó, pero no se pudo actualizar la vista. Recargá la pantalla.'

const OPCIONES_DE_MODO = [
  { valor: 'Escritorio', etiqueta: 'Escritorio' },
  { valor: 'Web', etiqueta: 'Web' },
]

const textoDeId = (puntoVenta: PuntoVentaListado) => String(puntoVenta.id).padStart(4, '0')
const textoDeEmpresa = (puntoVenta: PuntoVentaListado) => puntoVenta.razonSocialEmpresa ?? ETIQUETA_SIN_DUENIO
const textoDeDomicilio = (puntoVenta: PuntoVentaListado) => puntoVenta.domicilio ?? '—'
const textoDeSucursal = (puntoVenta: PuntoVentaListado) => puntoVenta.nombreSucursal ?? '—'
const nombreDeTenant = (puntoVenta: PuntoVentaListado) => etiquetaDeTenant(puntoVenta)
/** Un dueño dado de baja se rotula "—" en la celda, pero para filtrar y ordenar cuenta como vacío. */
const sinMarcaDeDuenio = (texto: string) => (texto === ETIQUETA_SIN_DUENIO ? '' : texto)

const ORDENABLES_BASE: ColumnasOrdenables<PuntoVentaListado> = {
  id: { valor: (puntoVenta) => puntoVenta.id },
  empresa: { valor: (puntoVenta) => sinMarcaDeDuenio(textoDeEmpresa(puntoVenta)) },
  nombre: { valor: (puntoVenta) => puntoVenta.nombre },
  sucursal: { valor: (puntoVenta) => puntoVenta.nombreSucursal ?? '' },
  domicilio: { valor: (puntoVenta) => puntoVenta.domicilio },
  modo: { valor: (puntoVenta) => puntoVenta.modo },
}
const FILTRABLES_BASE: ColumnasFiltrables<PuntoVentaListado> = {
  id: { etiqueta: 'ID', valor: textoDeId },
  empresa: { etiqueta: 'Empresa', valor: (puntoVenta) => sinMarcaDeDuenio(textoDeEmpresa(puntoVenta)) },
  nombre: { etiqueta: 'Nombre', valor: (puntoVenta) => puntoVenta.nombre },
  sucursal: { etiqueta: 'Sucursal', valor: (puntoVenta) => puntoVenta.nombreSucursal ?? '' },
  domicilio: { etiqueta: 'Domicilio', valor: textoDeDomicilio },
  modo: { etiqueta: 'Modo', valor: (puntoVenta) => puntoVenta.modo, opciones: OPCIONES_DE_MODO },
}
/** La columna de tenant solo existe para plataforma: sus definiciones son otro mapa estable, no una condición dentro del hook. */
const ORDENABLES_PLATAFORMA: ColumnasOrdenables<PuntoVentaListado> = {
  ...ORDENABLES_BASE,
  tenant: { valor: (puntoVenta) => sinMarcaDeDuenio(nombreDeTenant(puntoVenta)) },
}
const FILTRABLES_PLATAFORMA: ColumnasFiltrables<PuntoVentaListado> = {
  ...FILTRABLES_BASE,
  tenant: { etiqueta: 'Tenant', valor: (puntoVenta) => sinMarcaDeDuenio(nombreDeTenant(puntoVenta)) },
}

/** Una entrada por columna del encabezado, en su orden; `null` es Acciones. */
const CELDAS_BASE = ['id', 'empresa', 'nombre', 'sucursal', 'domicilio', 'modo', null]
const CELDAS_PLATAFORMA = ['id', 'tenant', 'empresa', 'nombre', 'sucursal', 'domicilio', 'modo', null]

/**
 * Lectura/alta/edición/baja de puntos de venta — mismo patrón que <c>Empresas.tsx</c>. El alta
 * crea un punto de venta sobre una empresa YA existente (la del tenant se crea por
 * aprovisionamiento) y el backend ya filtra por alcance. La baja
 * (etapa 20) es <c>Politicas.GestionDeOrganizacion</c>, no <c>LecturaDePuntosVenta</c>: el
 * selector de PV del POS lee este listado, pero un vendedor no llega a esta pantalla.
 * <c>idEmpresa</c> no se edita acá: es estructural (a qué empresa pertenece), no descriptivo.
 * Los dos filtros (tenant y empresa) operan sobre la lista YA CARGADA y sus opciones salen de
 * esas mismas filas (design D15).
 */
export function PuntosVenta() {
  const { usuario } = useAuth()
  const { recargar } = usePuntoVenta()
  const esPlataforma = usuario?.rolId === ROL.Root
  /** Misma policy que el backend (`GestionDeOrganizacion`: Root y Admin). La ruta ya exige esos dos
   * roles; esto evita ofrecer el alta si la pantalla llegara a abrirse con otro. */
  const puedeCrear = usuario?.rolId === ROL.Root || usuario?.rolId === ROL.Admin
  /** Mercado Pago es solo Admin (`GestionDeCatalogo` en la API): root entra a la pantalla pero no ve la acción. */
  const puedeMercadoPago = !!usuario && puedeGestionarCatalogos(usuario.rolId)

  const [items, setItems] = useState<PuntoVentaListado[]>([])
  const [cargando, setCargando] = useState(true)
  /** Solo la primera lectura reemplaza la tabla por "Cargando…": los refrescos la dejan montada para que los filtros conserven lo escrito y el foco. */
  const [cargada, setCargada] = useState(false)
  const [error, setError] = useState('')
  const [formulario, setFormulario] = useState<FormularioDePuntoVenta | null>(null)
  /** Formulario del flip de modo — separado del de edición descriptiva: precondición y rastro de
   * auditoría propios (ver `organizacion.ts`, `actualizarModoPuntoVenta`). */
  const [formularioModo, setFormularioModo] = useState<FormularioDeModoDePuntoVenta | null>(null)
  /** Formulario para mover la caja de sucursal: endpoint, precondiciones y rastro propios, como el modo. */
  const [formularioSucursal, setFormularioSucursal] = useState<FormularioDeSucursalDePuntoVenta | null>(null)
  const [mercadoPago, setMercadoPago] = useState<PuntoVentaListado | null>(null)
  /** Sucursales que ofrecen el alta y el movimiento; se piden al abrir cualquiera de los dos, por la misma razón que las empresas. */
  const [sucursales, setSucursales] = useState<SucursalListado[] | null>(null)
  const [errorSucursales, setErrorSucursales] = useState('')
  /** Rechazo del guardado de cualquiera de los tres modales. Solo hay uno abierto a la vez, así que
   * comparten el espacio; cada apertura lo vacía. Va aparte del `error` de la pantalla (carga y baja). */
  const [errorFormulario, setErrorFormulario] = useState('')
  const [disparoDeError, setDisparoDeError] = useState(0)
  /** Formulario de alta y las empresas que ofrece. Las empresas se piden al abrirlo (no al montar):
   * cambian desde otra pantalla y un listado viejo ofrecería una empresa dada de baja. */
  const [alta, setAlta] = useState<FormularioDeAltaDePuntoVenta | null>(null)
  const [empresas, setEmpresas] = useState<EmpresaListado[] | null>(null)
  const [errorEmpresas, setErrorEmpresas] = useState('')
  /** `ocupado` es el id de la fila en escritura; el alta no tiene fila todavía y usa 0. */
  const [ocupado, setOcupado] = useState<number | null>(null)
  const [filtroTenant, setFiltroTenant] = useState(SIN_FILTRO)
  const [filtroEmpresa, setFiltroEmpresa] = useState(SIN_FILTRO)
  /** Baja pendiente de confirmación: ver `Tenants.tsx`. La puerta es MODAL —`bloqueado` deja
   * inerte el resto de la pantalla mientras está abierta— y NO acuña token: eso lo hace la
   * escritura, al confirmar. */
  const [baja, setBaja] = useState<PuntoVentaListado | null>(null)
  /** Control que abrió la puerta, capturado en el `onClick` y no dentro de la puerta: ver
   * `ConfirmacionDeBaja.tsx`. Para cuando el efecto de montaje corre, el control ya quedó
   * `disabled` y el navegador se llevó el foco al `<body>`. */
  const [disparadorDeLaPuerta, setDisparadorDeLaPuerta] = useState<HTMLElement | null>(null)

  /** Contrato de invalidación: ver `Tenants.tsx` — mismo patrón en las cuatro pantallas raíz. */
  const generacion = useRef(0)
  /**
   * Espejo SÍNCRONO de `ocupado`, y la ÚNICA guarda de re-entrancia válida: ver `Tenants.tsx`.
   * Dos clicks del MISMO tick leen el mismo render, así que el estado los deja pasar a los dos.
   */
  const ocupadoRef = useRef(false)
  /** Generación PROPIA de la lectura de empresas del alta: abrir o cerrar el formulario no es una
   * escritura y no puede compartir token con `generacion` (una escritura invalida el listado, no
   * la lectura de empresas, y viceversa). Cerrar el formulario la acuña para descartar la respuesta
   * en vuelo. */
  const generacionEmpresas = useRef(0)
  /** Generación PROPIA de la lectura de sucursales (alta y movimiento): ver `generacionEmpresas`. */
  const generacionSucursales = useRef(0)
  /** Espejo SIEMPRE al día de `filtroTenant`. `cargar` es un `useCallback` sin dependencias, así
   * que no puede leer el estado por closure sin quedarse con una foto vieja; y la reconciliación
   * de la empresa necesita el tenant YA reconciliado, no el `prev` de su propio updater. */
  const filtroTenantVigente = useRef(SIN_FILTRO)
  /** Destino de foco al cerrar un modal de edición cuando su "Editar" o "Modo" ya no existe (durante el
   * refresco las filas se reemplazan por "Cargando…"): el botón de alta, que es estable. */
  const nuevoRef = useRef<HTMLButtonElement>(null)

  const cargar = useCallback(async (token: number, propagar = false) => {
    setCargando(true)
    try {
      const filas = await clienteDeOrganizacion.listarPuntosVenta()
      if (generacion.current !== token) return
      setItems(filas)
      // Reconciliación ESCRITA en el estado, no solo derivada al pintar: una selección que las
      // filas nuevas ya no sostienen se apaga, en vez de quedar viva y reaplicarse sola cuando la
      // opción reaparece. La empresa se reconcilia contra el MISMO conjunto ANGOSTADO que rinde la
      // pantalla (`opcionesDeEmpresa(filas, tenant)`, design D15): contra el conjunto sin angostar
      // sobrevivía una empresa de otro tenant que el `<select>` ya no ofrece.
      const tenantReconciliado = seleccionVigente(opcionesDeTenant(filas), filtroTenantVigente.current)
      filtroTenantVigente.current = tenantReconciliado
      setFiltroTenant(tenantReconciliado)
      setFiltroEmpresa((prev) => seleccionVigente(opcionesDeEmpresa(filas, tenantReconciliado), prev))
      setError('')
    } catch (e) {
      if (generacion.current !== token) return
      if (propagar) throw e
      setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los puntos de venta.')
    } finally {
      if (generacion.current === token) {
        setCargando(false)
        setCargada(true)
      }
    }
  }, [])

  useEffect(() => {
    void cargar(++generacion.current)
  }, [cargar])

  /** El refresco post-escritura va fuera del try/catch de la escritura: una escritura que ya
   * commiteó nunca se reporta como fallida (`react-async-state` regla 6). NO apaga `ocupado`: de
   * eso se ocupa el `finally` ungated de cada escritura — ver `Tenants.tsx`. */
  async function refrescarTrasEscribir(token: number, mensajeOk: string, avisoDeFallo: string) {
    if (generacion.current !== token) return

    toast.exito(mensajeOk)
    try {
      await cargar(token, true)
    } catch {
      if (generacion.current === token) toast.advertencia(avisoDeFallo)

      return
    }

    // La sesión mira el mismo listado: un rename o una baja se propagan al punto de venta activo
    // sin esperar un refresco de página. Sin `await` y con el rechazo tragado: esta pantalla no
    // depende de ese resultado y un fallo ahí no cambia lo que ya se rindió.
    void recargar().catch(() => {})
  }

  async function guardar() {
    if (!formulario || ocupadoRef.current) return

    const datos = formulario
    const token = ++generacion.current
    ocupadoRef.current = true
    setOcupado(datos.id)
    setError('')
    setErrorFormulario('')
    try {
      // La respuesta de la escritura no se compara con la generación: el modal queda inerte hasta
      // que el refresco aterriza y nada puede acuñar otro token ni reabrir el formulario mientras tanto.
      try {
        await clienteDeOrganizacion.editarPuntoVenta(datos.id, {
          nombre: datos.nombre,
          domicilio: datos.domicilio || null,
          horario: datos.horario || null,
          whatsapp: datos.whatsapp || null,
          instagram: datos.instagram || null,
          facebook: datos.facebook || null,
          web: datos.web || null,
          envioPorDefecto: datos.envioPorDefecto,
        })
      } catch (e) {
        setErrorFormulario(e instanceof ErrorApi ? e.message : 'No se pudo guardar.')

        return
      }

      await refrescarTrasEscribir(token, `Se actualizó "${datos.nombre}".`, AVISO_REFRESCO_FALLIDO)
      // El modal sigue montado durante el refresco y se cierra en el mismo lote que libera `ocupado`
      // (el `finally` corre en el mismo tick): así los controles ya están habilitados al devolver el foco.
      setFormulario(null)
    } finally {
      ocupadoRef.current = false
      setOcupado(null)
    }
  }

  async function abrirAlta() {
    if (ocupadoRef.current) return

    const token = ++generacionEmpresas.current
    setAlta(ALTA_VACIA)
    setEmpresas(null)
    setErrorEmpresas('')
    setErrorFormulario('')
    setError('')
    void cargarSucursales()
    try {
      const filas = await clienteDeOrganizacion.listarEmpresas()
      if (generacionEmpresas.current !== token) return
      setEmpresas(filas)
    } catch (e) {
      if (generacionEmpresas.current !== token) return
      setErrorEmpresas(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las empresas.')
    }
  }

  function cerrarAlta() {
    generacionEmpresas.current++
    generacionSucursales.current++
    setAlta(null)
  }

  async function cargarSucursales() {
    const token = ++generacionSucursales.current
    setSucursales(null)
    setErrorSucursales('')
    try {
      const filas = await clienteDeOrganizacion.listarSucursales()
      if (generacionSucursales.current !== token) return
      setSucursales(filas)
    } catch (e) {
      if (generacionSucursales.current !== token) return
      setSucursales([])
      setErrorSucursales(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las sucursales.')
    }
  }

  async function guardarAlta() {
    if (!alta || ocupadoRef.current) return

    const datos = alta
    const nombre = datos.nombre.trim()
    if (datos.idEmpresa === '' || datos.modo === '' || nombre === '') {
      setErrorFormulario('Elegí la empresa y el modo, y completá el nombre.')
      setDisparoDeError((n) => n + 1)

      return
    }

    // Una lectura en vuelo queda invalidada por el token de esta escritura: no va a dejar datos ni a
    // bajar `cargando`. Si la escritura falla no hay refresco que lo repare, así que se vuelve a pedir.
    const habiaLecturaEnVuelo = cargando
    const token = ++generacion.current
    ocupadoRef.current = true
    setOcupado(0)
    setError('')
    setErrorFormulario('')
    try {
      // Sin comparar con la generación (ver `guardar`): el modal está inerte durante toda la escritura.
      try {
        await clienteDeOrganizacion.crearPuntoVenta({
          idEmpresa: Number(datos.idEmpresa),
          idSucursal: datos.idSucursal === '' ? null : Number(datos.idSucursal),
          nombre,
          modo: datos.modo,
          domicilio: datos.domicilio || null,
          horario: datos.horario || null,
          whatsapp: datos.whatsapp || null,
          instagram: datos.instagram || null,
          facebook: datos.facebook || null,
          web: datos.web || null,
        })
      } catch (e) {
        setErrorFormulario(e instanceof ErrorApi ? e.message : 'No se pudo crear el punto de venta.')
        if (habiaLecturaEnVuelo) void cargar(++generacion.current)

        return
      }

      // El punto de venta nuevo puede caer fuera de los filtros vigentes: se limpian para que la
      // fila creada se vea en el listado que se refresca a continuación.
      filtroTenantVigente.current = SIN_FILTRO
      setFiltroTenant(SIN_FILTRO)
      setFiltroEmpresa(SIN_FILTRO)
      limpiarFiltros()
      await refrescarTrasEscribir(token, `Se creó el punto de venta "${nombre}".`, AVISO_REFRESCO_FALLIDO)
      cerrarAlta()
    } finally {
      ocupadoRef.current = false
      setOcupado(null)
    }
  }

  async function guardarModo() {
    if (!formularioModo || ocupadoRef.current) return

    const datos = formularioModo
    const token = ++generacion.current
    ocupadoRef.current = true
    setOcupado(datos.id)
    setError('')
    setErrorFormulario('')
    try {
      // Sin comparar con la generación (ver `guardar`): el modal está inerte durante toda la escritura.
      try {
        await clienteDeOrganizacion.actualizarModoPuntoVenta(datos.id, { modo: datos.modo })
      } catch (e) {
        setErrorFormulario(e instanceof ErrorApi ? e.message : 'No se pudo cambiar el modo.')

        return
      }

      await refrescarTrasEscribir(token, `Se cambió el modo de "${datos.nombre}" a ${datos.modo}.`, AVISO_REFRESCO_FALLIDO)
      setFormularioModo(null)
    } finally {
      ocupadoRef.current = false
      setOcupado(null)
    }
  }

  async function guardarSucursal() {
    if (!formularioSucursal || ocupadoRef.current) return

    const datos = formularioSucursal
    if (datos.idSucursal === '' || Number(datos.idSucursal) === datos.idSucursalActual) return

    const token = ++generacion.current
    ocupadoRef.current = true
    setOcupado(datos.id)
    setError('')
    setErrorFormulario('')
    try {
      // Sin comparar con la generación (ver `guardar`): el modal está inerte durante toda la escritura.
      try {
        await clienteDeOrganizacion.moverPuntoVentaDeSucursal(datos.id, { idSucursal: Number(datos.idSucursal) })
      } catch (e) {
        setErrorFormulario(e instanceof ErrorApi ? e.message : 'No se pudo mover el punto de venta de sucursal.')

        return
      }

      await refrescarTrasEscribir(token, `Se movió "${datos.nombre}" de sucursal.`, AVISO_REFRESCO_FALLIDO)
      generacionSucursales.current++
      setFormularioSucursal(null)
    } finally {
      ocupadoRef.current = false
      setOcupado(null)
    }
  }

  function abrirSucursal(puntoVenta: PuntoVentaListado) {
    setErrorFormulario('')
    setFormularioSucursal(aFormularioDeSucursalDePuntoVenta(puntoVenta))
    void cargarSucursales()
  }

  function cerrarSucursal() {
    generacionSucursales.current++
    setFormularioSucursal(null)
  }

  function abrirEdicion(puntoVenta: PuntoVentaListado) {
    setErrorFormulario('')
    setFormulario(aFormularioDePuntoVenta(puntoVenta))
  }

  function abrirModo(puntoVenta: PuntoVentaListado) {
    setErrorFormulario('')
    setFormularioModo({ id: puntoVenta.id, nombre: puntoVenta.nombre, modo: puntoVenta.modo })
  }

  /** Ver `Tenants.tsx`: mismo patrón de puerta, mismo contrato de invalidación, misma re-entrancia.
   * Abrir NO acuña generación: no hay escritura todavía. */
  function pedirBaja(puntoVenta: PuntoVentaListado, disparador: HTMLElement | null) {
    if (ocupadoRef.current) return

    setDisparadorDeLaPuerta(disparador)
    setBaja(puntoVenta)
    setError('')
  }

  /** Cancelar no supersede nada: solo cierra la puerta. Por eso no acuña generación —hacerlo
   * descartaba una lectura en vuelo y clavaba la pantalla en "Cargando…"— y limpia el error,
   * en simetría con la apertura. */
  function cancelarBaja() {
    if (ocupadoRef.current) return

    setDisparadorDeLaPuerta(null)
    setBaja(null)
    setError('')
  }

  async function confirmarBaja() {
    if (!baja || ocupadoRef.current) return

    // El token se acuña ACÁ, primera sentencia síncrona de la escritura (`react-async-state`
    // regla 2): ver `Tenants.tsx`.
    const fila = baja
    const token = ++generacion.current
    ocupadoRef.current = true
    setOcupado(fila.id)
    setError('')
    try {
      try {
        await clienteDeOrganizacion.eliminarPuntoVenta(fila.id)
      } catch (e) {
        // Un rechazo SIEMPRE se rinde, con la puerta abierta y el motivo adentro de ella.
        setError(copiaDeFalloDeBaja(e, 'el punto de venta'))

        return
      }

      // Un 204 SIEMPRE cierra la puerta y refresca; la generación solo gobierna el REFRESCO.
      setBaja(null)
      // La baja de la fila que se está editando se lleva también su formulario: dejarlo abierto
      // ofrecía guardar sobre una entidad que ya no existe, y el PUT moría en 404. Mismo criterio
      // para el formulario de modo.
      setFormulario((prev) => (prev?.id === fila.id ? null : prev))
      setFormularioModo((prev) => (prev?.id === fila.id ? null : prev))
      setFormularioSucursal((prev) => (prev?.id === fila.id ? null : prev))
      await refrescarTrasEscribir(
        token,
        `Se dio de baja el punto de venta "${fila.nombre}".`,
        AVISO_REFRESCO_FALLIDO_BAJA,
      )
    } finally {
      ocupadoRef.current = false
      setOcupado(null)
    }
  }

  /** La puerta abierta bloquea la pantalla entera, no solo la escritura en vuelo: ver `Tenants.tsx`. */
  const bloqueado = ocupado !== null || baja !== null

  // Derivación pura, sin efectos: elegir un tenant ANGOSTA las opciones de empresa (design D15).
  // El fallback a "todas" cubre además el caso en que un refresco se llevó puesta la fila que
  // sostenía la opción elegida, sin dejar el `<select>` apuntando a una opción inexistente.
  const opcionesTenant = opcionesDeTenant(items)
  const tenantVigente = seleccionVigente(opcionesTenant, filtroTenant)
  const opcionesEmpresa = opcionesDeEmpresa(items, tenantVigente)
  const empresaVigente = seleccionVigente(opcionesEmpresa, filtroEmpresa)
  const alcanzados = filtrarPorEmpresa(filtrarPorTenant(items, tenantVigente), empresaVigente)
  const columnasFiltrables = esPlataforma ? FILTRABLES_PLATAFORMA : FILTRABLES_BASE
  const celdasDeFiltros = esPlataforma ? CELDAS_PLATAFORMA : CELDAS_BASE
  const { filas: filtrados, filtros, cambiar: cambiarFiltro, limpiar: limpiarFiltros, hayFiltros } = useFiltrosDeColumna(
    alcanzados,
    columnasFiltrables,
  )
  const { filas: visibles, orden, alternar } = useOrdenamiento(
    filtrados,
    esPlataforma ? ORDENABLES_PLATAFORMA : ORDENABLES_BASE,
  )

  /** Elegir un tenant LIMPIA la empresa seleccionada cuando esa empresa ya no le pertenece
   * (design D15) — si le sigue perteneciendo, la selección se respeta. El updater se arma desde
   * `prev`, nunca leyendo el estado por closure (`react-async-state` regla 1). */
  function cambiarFiltroDeTenant(valor: string) {
    filtroTenantVigente.current = valor
    setFiltroTenant(valor)
    setFiltroEmpresa((prev) =>
      prev === SIN_FILTRO || opcionesDeEmpresa(items, valor).some((o) => o.valor === prev)
        ? prev
        : SIN_FILTRO,
    )
  }

  return (
    <div className="container-fluid py-4">
      <Box titulo="Puntos de venta" variante="inverse">
        {error && !baja && <div className="alert alert-danger">{error}</div>}

        {baja && (
          <ConfirmacionDeBaja
            titulo={`el punto de venta "${baja.nombre}"`}
            ocupado={ocupado !== null}
            disparador={disparadorDeLaPuerta}
            error={error}
            onConfirmar={confirmarBaja}
            onCancelar={cancelarBaja}
          />
        )}

        {puedeCrear && (
          <div className="mb-3">
            <button
              ref={nuevoRef}
              type="button"
              className="btn btn-primary"
              onClick={() => void abrirAlta()}
              disabled={bloqueado}
            >
              Nuevo punto de venta
            </button>
          </div>
        )}

        {/* Los modales van en su propio contenedor, DESPUÉS del botón de alta: al cerrarse, React procesa el
            desmontaje del modal antes de actualizar a sus hermanos directos, y ahí el botón todavía estaría
            deshabilitado y no podría recibir el foco que el modal le devuelve. */}
        <div>
          {alta && (
            <ModalDeAltaDePuntoVenta
              valor={alta}
              empresas={empresas}
              errorEmpresas={errorEmpresas}
              sucursales={sucursales}
              esPlataforma={esPlataforma}
              guardando={ocupado !== null}
              bloqueado={bloqueado}
              error={errorFormulario}
              disparoDeError={disparoDeError}
              focoDeReserva={nuevoRef}
              onCambio={setAlta}
              onGuardar={() => void guardarAlta()}
              onCancelar={cerrarAlta}
            />
          )}

          {formulario && (
            <ModalDePuntoVenta
              valor={formulario}
              guardando={ocupado !== null}
              bloqueado={bloqueado}
              error={errorFormulario}
              focoDeReserva={nuevoRef}
              onCambio={setFormulario}
              onGuardar={() => void guardar()}
              onCancelar={() => setFormulario(null)}
            />
          )}

          {formularioSucursal && (
            <ModalDeSucursalDePuntoVenta
              valor={formularioSucursal}
              sucursales={sucursales}
              errorSucursales={errorSucursales}
              guardando={ocupado !== null}
              bloqueado={bloqueado}
              error={errorFormulario}
              focoDeReserva={nuevoRef}
              onCambio={setFormularioSucursal}
              onGuardar={() => void guardarSucursal()}
              onCancelar={cerrarSucursal}
            />
          )}

          {mercadoPago && puedeMercadoPago && (
            <ModalDeMercadoPagoDePuntoVenta
              key={mercadoPago.id}
              puntoVenta={mercadoPago}
              focoDeReserva={nuevoRef}
              onCerrar={() => setMercadoPago(null)}
            />
          )}

          {formularioModo && (
            <ModalDeModoDePuntoVenta
              valor={formularioModo}
              guardando={ocupado !== null}
              bloqueado={bloqueado}
              error={errorFormulario}
              focoDeReserva={nuevoRef}
              onCambio={setFormularioModo}
              onGuardar={() => void guardarModo()}
              onCancelar={() => setFormularioModo(null)}
            />
          )}
        </div>

        {!cargada ? (
          <Cargando />
        ) : (
          <>
            <div className="row g-2 mb-3">
              {/* El filtro por tenant se rinde con el mismo criterio que la COLUMNA de tenant: solo
                  para un actor de plataforma. Un admin de tenant ve una sola opción —la suya— y
                  filtrar por ella no angosta nada. El de empresa sí le sirve y queda para todos. */}
              {esPlataforma && (
                <div className="col-md-4">
                  <label className="form-label" htmlFor="pv-filtro-tenant">
                    Tenant
                  </label>
                  <select
                    id="pv-filtro-tenant"
                    className="form-select"
                    value={tenantVigente}
                    onChange={(e) => cambiarFiltroDeTenant(e.target.value)}
                    disabled={bloqueado}
                  >
                    <option value={SIN_FILTRO}>Todos</option>
                    {opcionesTenant.map((o) => (
                      <option key={o.valor} value={o.valor}>
                        {o.etiqueta}
                      </option>
                    ))}
                  </select>
                </div>
              )}
              <div className="col-md-4">
                <label className="form-label" htmlFor="pv-filtro-empresa">
                  Empresa
                </label>
                <select
                  id="pv-filtro-empresa"
                  className="form-select"
                  value={empresaVigente}
                  onChange={(e) => setFiltroEmpresa(e.target.value)}
                  disabled={bloqueado}
                >
                  <option value={SIN_FILTRO}>Todas</option>
                  {opcionesEmpresa.map((o) => (
                    <option key={o.valor} value={o.valor}>
                      {o.etiqueta}
                    </option>
                  ))}
                </select>
              </div>
            </div>

            <div className="table-responsive">
              <table className="table table-striped table-hover table-bordered align-middle">
                <thead>
                  <tr>
                    <EncabezadoOrdenable columna="id" orden={orden} onAlternar={alternar}>
                      ID
                    </EncabezadoOrdenable>
                    {esPlataforma && (
                      <EncabezadoOrdenable columna="tenant" orden={orden} onAlternar={alternar}>
                        Tenant
                      </EncabezadoOrdenable>
                    )}
                    <EncabezadoOrdenable columna="empresa" orden={orden} onAlternar={alternar}>
                      Empresa
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="nombre" orden={orden} onAlternar={alternar}>
                      Nombre
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="sucursal" orden={orden} onAlternar={alternar}>
                      Sucursal
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="domicilio" orden={orden} onAlternar={alternar}>
                      Domicilio
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="modo" orden={orden} onAlternar={alternar}>
                      Modo
                    </EncabezadoOrdenable>
                    <th scope="col" className="text-end">
                      Acciones
                    </th>
                  </tr>
                  <FilaDeFiltros
                    celdas={celdasDeFiltros}
                    columnas={columnasFiltrables}
                    filtros={filtros}
                    onCambio={cambiarFiltro}
                    onLimpiar={limpiarFiltros}
                    hayFiltros={hayFiltros}
                    deshabilitado={bloqueado}
                  />
                </thead>
                <tbody>
                  {cargando && (
                    <tr>
                      <td colSpan={esPlataforma ? 8 : 7}>
                        <Cargando />
                      </td>
                    </tr>
                  )}
                  {!cargando && visibles.map((p) => (
                    <tr key={p.id}>
                      <td>{textoDeId(p)}</td>
                      {esPlataforma && <td>{nombreDeTenant(p)}</td>}
                      <td>{textoDeEmpresa(p)}</td>
                      <td>{p.nombre}</td>
                      <td>{textoDeSucursal(p)}</td>
                      <td>{textoDeDomicilio(p)}</td>
                      <td>{p.modo}</td>
                      <td className="text-end text-nowrap">
                        <button
                          type="button"
                          className="btn btn-sm btn-outline-secondary me-1"
                          onClick={() => abrirSucursal(p)}
                          disabled={bloqueado}
                        >
                          Sucursal
                        </button>
                        <button
                          type="button"
                          className="btn btn-sm btn-outline-secondary me-1"
                          onClick={() => abrirModo(p)}
                          disabled={bloqueado}
                        >
                          Modo
                        </button>
                        {puedeMercadoPago && (
                          <button
                            type="button"
                            className="btn btn-sm btn-outline-secondary me-1"
                            onClick={() => setMercadoPago(p)}
                            disabled={bloqueado}
                          >
                            Mercado Pago
                          </button>
                        )}
                        <BotonIcono
                          icono="editar"
                          className="me-1"
                          onClick={() => abrirEdicion(p)}
                          disabled={bloqueado}
                        />
                        <BotonIcono
                          icono="eliminar"
                          onClick={(evento) => pedirBaja(p, evento.currentTarget)}
                          disabled={bloqueado}
                        />
                      </td>
                    </tr>
                  ))}
                  {!cargando && visibles.length === 0 && (
                    <tr>
                      <td colSpan={esPlataforma ? 8 : 7} className="text-center text-muted py-4">
                        {items.length === 0 ? 'No hay puntos de venta cargados.' : 'Ningún registro coincide con los filtros.'}
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>
          </>
        )}
      </Box>
    </div>
  )
}
