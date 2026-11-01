import { useCallback, useEffect, useRef, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { copiaDeFalloDeBaja } from '../api/bajas'
import {
  clienteDeOrganizacion,
  ETIQUETA_SIN_DUENIO,
  etiquetaDeTenant,
  filtrarPorTenant,
  opcionesDeTenant,
  seleccionVigente,
  SIN_FILTRO,
} from '../api/organizacion'
import type { RetornoDeMercadoPago } from '../api/mercadoPago'
import type { EmpresaListado } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'
import { useAuth } from '../auth/useAuth'
import { ROL } from '../api/tipos'
import { BotonIcono } from '../componentes/BotonIcono'
import {
  EncabezadoOrdenable,
  FilaDeFiltros,
  useFiltrosDeColumna,
  useOrdenamiento,
  type ColumnasFiltrables,
  type ColumnasOrdenables,
} from '../componentes/grilla'
import {
  aEdicionDeEmpresa,
  aFormularioDeEmpresa,
  alicuotasDePercepcionValidas,
  MENSAJE_ALICUOTA_DE_PERCEPCION_INVALIDA,
  type FormularioDeEmpresa,
} from './empresaFormulario'
import { ModalDeEmpresa } from './organizacion/ModalDeEmpresa'
import { tomarRetornoDeMercadoPago } from './organizacion/fragmentoMercadoPago'
import { toast } from '../toasts/toasts'
import { SeccionMercadoPago } from './organizacion/SeccionMercadoPago'

const AVISO_REFRESCO_FALLIDO = 'Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.'
const AVISO_REFRESCO_FALLIDO_BAJA =
  'Se eliminó, pero no se pudo actualizar la vista. Recargá la pantalla.'

/** `EmpresaListado` no trae un contador de puntos de venta, así que la puerta no puede decir
 * cuántos son. Se nombra la familia sin cantidad —y "activos", que es lo que efectivamente cae en
 * la cascada— en vez de inventar un número que la fila no tiene. */
const ARRASTRE_DE_EMPRESA = ['Sus puntos de venta activos'] as const

const textoDeId = (empresa: EmpresaListado) => String(empresa.id).padStart(4, '0')
const textoDeFantasia = (empresa: EmpresaListado) => empresa.nombreFantasia ?? '—'
const textoDeCuit = (empresa: EmpresaListado) => empresa.cuit ?? '—'
const nombreDeTenant = (empresa: EmpresaListado) => etiquetaDeTenant(empresa)
/** Un dueño dado de baja se rotula "—" en la celda, pero para filtrar y ordenar cuenta como vacío. */
const tenantParaFiltrarYOrdenar = (empresa: EmpresaListado) => {
  const etiqueta = nombreDeTenant(empresa)

  return etiqueta === ETIQUETA_SIN_DUENIO ? '' : etiqueta
}

const ORDENABLES_BASE: ColumnasOrdenables<EmpresaListado> = {
  id: { valor: (empresa) => empresa.id },
  razonSocial: { valor: (empresa) => empresa.razonSocial },
  nombreFantasia: { valor: (empresa) => empresa.nombreFantasia },
  cuit: { valor: (empresa) => empresa.cuit },
}
const FILTRABLES_BASE: ColumnasFiltrables<EmpresaListado> = {
  id: { etiqueta: 'ID', valor: textoDeId },
  razonSocial: { etiqueta: 'Razón social', valor: (empresa) => empresa.razonSocial },
  nombreFantasia: { etiqueta: 'Nombre de fantasía', valor: textoDeFantasia },
  cuit: { etiqueta: 'CUIT', valor: textoDeCuit },
}
/** La columna de tenant solo existe para plataforma: sus definiciones son otro mapa estable, no una condición dentro del hook. */
const ORDENABLES_PLATAFORMA: ColumnasOrdenables<EmpresaListado> = {
  ...ORDENABLES_BASE,
  tenant: { valor: tenantParaFiltrarYOrdenar },
}
const FILTRABLES_PLATAFORMA: ColumnasFiltrables<EmpresaListado> = {
  ...FILTRABLES_BASE,
  tenant: { etiqueta: 'Tenant', valor: tenantParaFiltrarYOrdenar },
}

/** Una entrada por columna del encabezado, en su orden; `null` es Acciones. */
const CELDAS_BASE = ['id', 'razonSocial', 'nombreFantasia', 'cuit', null]
const CELDAS_PLATAFORMA = ['id', 'tenant', 'razonSocial', 'nombreFantasia', 'cuit', null]

/**
 * Lectura/edición/baja de empresas — el alta sigue siendo plataforma-only vía aprovisionamiento
 * (`paginas/tenants/ModalDeNuevoTenant.tsx`); la baja (etapa 20) la puede ejecutar también un admin de tenant sobre las
 * propias, porque `DELETE /api/empresas/{id}` reusa la policy del grupo
 * (`GestionDeOrganizacion`) y esta pantalla ya está detrás de esos mismos roles.
 * El backend ya filtra por alcance: la plataforma ve
 * todas las empresas de todos los tenants, un admin de tenant solo ve la(s) propia(s) — esta
 * pantalla no filtra nada por su cuenta contra el servidor. El filtro por tenant de abajo opera
 * sobre la lista YA CARGADA y sus opciones salen de esas mismas filas (design D15), así que no
 * puede delatar un tenant fuera del alcance del actor.
 */
export function Empresas() {
  const { usuario } = useAuth()
  const esPlataforma = usuario?.rolId === ROL.Root
  /** Mercado Pago lo administra solo un Admin de tenant: los endpoints exigen un tenant en la sesión. */
  const esAdmin = usuario?.rolId === ROL.Admin

  const [items, setItems] = useState<EmpresaListado[]>([])
  /** Solo la primera lectura reemplaza la tabla por "Cargando…": los refrescos la dejan montada para que los filtros conserven lo escrito y el foco. */
  const [cargada, setCargada] = useState(false)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [formulario, setFormulario] = useState<FormularioDeEmpresa | null>(null)
  /** Rechazo del guardado: vive adentro del modal, aparte del `error` de la pantalla (carga y baja). */
  const [errorFormulario, setErrorFormulario] = useState('')
  const [disparoDeError, setDisparoDeError] = useState(0)
  const [ocupado, setOcupado] = useState<number | null>(null)
  const [filtroTenant, setFiltroTenant] = useState(SIN_FILTRO)
  /** Baja pendiente de confirmación: ver `Tenants.tsx`. La puerta es MODAL —`bloqueado` deja
   * inerte el resto de la pantalla mientras está abierta— y NO acuña token: eso lo hace la
   * escritura, al confirmar. */
  const [baja, setBaja] = useState<EmpresaListado | null>(null)
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
  /** Destino de foco al cerrar el modal cuando el "Editar" que lo abrió ya no existe (durante el
   * refresco las filas se reemplazan por "Cargando…"): esta pantalla no tiene botón de alta, así que
   * el punto estable es la zona de avisos, un contenedor fijo que siempre existe. */
  const zonaDeAvisosRef = useRef<HTMLDivElement>(null)

  const cargar = useCallback(async (token: number, propagar = false) => {
    setCargando(true)
    try {
      const filas = await clienteDeOrganizacion.listarEmpresas()
      if (generacion.current !== token) return
      setItems(filas)
      setFiltroTenant((prev) => seleccionVigente(opcionesDeTenant(filas), prev))
      setError('')
    } catch (e) {
      if (generacion.current !== token) return
      if (propagar) throw e
      setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las empresas.')
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

  /**
   * El retorno de Mercado Pago se toma y se borra de la URL al llegar, para cualquier usuario y antes de cualquier espera:
   * no depende de que la carga termine ni de que la sección se monte. Solo un Admin lo pasa a la sección, que lo canjea
   * cuando se monta (también si la carga de empresas falló). La referencia hace que la segunda corrida del efecto bajo
   * StrictMode no pise lo ya leído.
   */
  const retornoLeido = useRef(false)
  const [retornoDeMercadoPago, setRetornoDeMercadoPago] = useState<RetornoDeMercadoPago | null>(null)
  useEffect(() => {
    if (retornoLeido.current) return

    retornoLeido.current = true
    setRetornoDeMercadoPago(tomarRetornoDeMercadoPago())
  }, [])

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
    }
  }

  async function guardar() {
    if (!formulario || ocupadoRef.current) return

    if (!alicuotasDePercepcionValidas(formulario)) {
      setErrorFormulario(MENSAJE_ALICUOTA_DE_PERCEPCION_INVALIDA)
      setDisparoDeError((n) => n + 1)
      return
    }

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
        await clienteDeOrganizacion.editarEmpresa(datos.id, aEdicionDeEmpresa(datos))
      } catch (e) {
        setErrorFormulario(e instanceof ErrorApi ? e.message : 'No se pudo guardar.')

        return
      }

      await refrescarTrasEscribir(token, `Se actualizó "${datos.razonSocial}".`, AVISO_REFRESCO_FALLIDO)
      // El modal sigue montado durante el refresco y se cierra en el mismo lote que libera `ocupado`
      // (el `finally` corre en el mismo tick): así los controles ya están habilitados al devolver el foco.
      setFormulario(null)
    } finally {
      ocupadoRef.current = false
      setOcupado(null)
    }
  }

  function abrirEdicion(empresa: EmpresaListado) {
    setErrorFormulario('')
    setFormulario(aFormularioDeEmpresa(empresa))
  }

  /** Ver `Tenants.tsx`: mismo patrón de puerta, mismo contrato de invalidación, misma re-entrancia.
   * Abrir NO acuña generación: no hay escritura todavía. */
  function pedirBaja(empresa: EmpresaListado, disparador: HTMLElement | null) {
    if (ocupadoRef.current) return

    setDisparadorDeLaPuerta(disparador)
    setBaja(empresa)
    setError('')
  }

  /** Cancelar no supersede nada: solo cierra la puerta. Por eso no acuña generación —hacerlo
   * descartaba una lectura en vuelo y clavaba la pantalla en "Cargando…"— y limpia los dos avisos,
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
        await clienteDeOrganizacion.eliminarEmpresa(fila.id)
      } catch (e) {
        // Un rechazo SIEMPRE se rinde, con la puerta abierta y el motivo adentro de ella.
        setError(copiaDeFalloDeBaja(e, 'la empresa'))

        return
      }

      // Un 204 SIEMPRE cierra la puerta y refresca; la generación solo gobierna el REFRESCO.
      setBaja(null)
      // La baja de la fila que se está editando se lleva también su formulario: dejarlo abierto
      // ofrecía guardar sobre una entidad que ya no existe, y el PUT moría en 404.
      setFormulario((prev) => (prev?.id === fila.id ? null : prev))
      await refrescarTrasEscribir(
        token,
        `Se dio de baja la empresa "${fila.razonSocial}".`,
        AVISO_REFRESCO_FALLIDO_BAJA,
      )
    } finally {
      ocupadoRef.current = false
      setOcupado(null)
    }
  }

  /** La puerta abierta bloquea la pantalla entera, no solo la escritura en vuelo: ver `Tenants.tsx`. */
  const bloqueado = ocupado !== null || baja !== null

  // Las opciones se derivan de las filas cargadas; si la selección vigente desaparece de la lista
  // (un refresco trajo otras filas), el filtro cae a "todos". Además de derivarlo acá, `cargar`
  // escribe la reconciliación en el ESTADO: si solo se derivara, la selección inválida seguiría
  // viva y se reaplicaría sola en cuanto la opción reapareciera.
  const opcionesTenant = opcionesDeTenant(items)
  const tenantVigente = seleccionVigente(opcionesTenant, filtroTenant)
  const alcanzadas = filtrarPorTenant(items, tenantVigente)
  const columnasFiltrables = esPlataforma ? FILTRABLES_PLATAFORMA : FILTRABLES_BASE
  const celdasDeFiltros = esPlataforma ? CELDAS_PLATAFORMA : CELDAS_BASE
  const { filas: filtradas, filtros, cambiar: cambiarFiltro, limpiar: limpiarFiltros, hayFiltros } = useFiltrosDeColumna(
    alcanzadas,
    columnasFiltrables,
  )
  const { filas: visibles, orden, alternar } = useOrdenamiento(
    filtradas,
    esPlataforma ? ORDENABLES_PLATAFORMA : ORDENABLES_BASE,
  )

  return (
    <div className="container-fluid py-4">
      <Box titulo="Empresas" variante="inverse">
        <div ref={zonaDeAvisosRef} tabIndex={-1} style={{ outline: 'none' }}>
          {error && !baja && <div className="alert alert-danger">{error}</div>}
        </div>

        {baja && (
          <ConfirmacionDeBaja
            titulo={`la empresa "${baja.razonSocial}"`}
            arrastra={ARRASTRE_DE_EMPRESA}
            ocupado={ocupado !== null}
            disparador={disparadorDeLaPuerta}
            error={error}
            onConfirmar={confirmarBaja}
            onCancelar={cancelarBaja}
          />
        )}

        {formulario && (
          <ModalDeEmpresa
            valor={formulario}
            guardando={ocupado !== null}
            bloqueado={bloqueado}
            error={errorFormulario}
            disparoDeError={disparoDeError}
            focoDeReserva={zonaDeAvisosRef}
            onCambio={setFormulario}
            onGuardar={() => void guardar()}
            onCancelar={() => setFormulario(null)}
          />
        )}

        {!cargada ? (
          <Cargando />
        ) : (
          <>
            {/* El filtro por tenant se rinde con el mismo criterio que la COLUMNA de tenant: solo
                para un actor de plataforma. Un admin de tenant ve una sola opción —la suya— y
                filtrar por ella no angosta nada. */}
            {esPlataforma && (
              <div className="row g-2 mb-3">
                <div className="col-md-4">
                  <label className="form-label" htmlFor="e-filtro-tenant">
                    Tenant
                  </label>
                  <select
                    id="e-filtro-tenant"
                    className="form-select"
                    value={tenantVigente}
                    onChange={(e) => setFiltroTenant(e.target.value)}
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
              </div>
            )}

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
                    <EncabezadoOrdenable columna="razonSocial" orden={orden} onAlternar={alternar}>
                      Razón social
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="nombreFantasia" orden={orden} onAlternar={alternar}>
                      Nombre de fantasía
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="cuit" orden={orden} onAlternar={alternar}>
                      CUIT
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
                      <td colSpan={esPlataforma ? 6 : 5}>
                        <Cargando />
                      </td>
                    </tr>
                  )}
                  {!cargando && visibles.map((e) => (
                    <tr key={e.id}>
                      <td>{textoDeId(e)}</td>
                      {esPlataforma && <td>{nombreDeTenant(e)}</td>}
                      <td>{e.razonSocial}</td>
                      <td>{textoDeFantasia(e)}</td>
                      <td>{textoDeCuit(e)}</td>
                      <td className="text-end text-nowrap">
                        <BotonIcono
                          icono="editar"
                          className="me-1"
                          onClick={() => abrirEdicion(e)}
                          disabled={bloqueado}
                        />
                        <BotonIcono
                          icono="eliminar"
                          onClick={(evento) => pedirBaja(e, evento.currentTarget)}
                          disabled={bloqueado}
                        />
                      </td>
                    </tr>
                  ))}
                  {!cargando && visibles.length === 0 && (
                    <tr>
                      <td colSpan={esPlataforma ? 6 : 5} className="text-center text-muted py-4">
                        {items.length === 0 ? 'No hay empresas cargadas.' : 'Ningún registro coincide con los filtros.'}
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>
          </>
        )}
      </Box>

      {esAdmin && cargada && <SeccionMercadoPago empresas={items} retorno={retornoDeMercadoPago} />}
    </div>
  )
}
