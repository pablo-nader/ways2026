import { useCallback, useEffect, useRef, useState } from 'react'
import { api, ErrorApi } from '../api/cliente'
import { copiaDeFalloDeBaja } from '../api/bajas'
import {
  clienteDeOrganizacion,
  etiquetaDeTenant,
  filtrarPorTenant,
  opcionesDeTenant,
  opcionesDeTenantAsignable,
  seleccionVigente,
  SIN_FILTRO,
} from '../api/organizacion'
import type { OpcionDeFiltro } from '../api/organizacion'
import { ESTADOS_USUARIO, ROL } from '../api/tipos'
import type {
  ActualizarUsuario,
  CrearUsuario,
  EstadoUsuario,
  PaginaDe,
  RolListado,
  TenantListado,
  UsuarioListado,
} from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'
import { useAuth } from '../auth/useAuth'

type Formulario = {
  id: number | null
  usuario: string
  mail: string
  rolId: number
  estado: EstadoUsuario
  password: string
  /** Solo se manda en el alta: `ActualizarUsuario` no acepta tenant. `null` es a la vez el valor
   * del rol root y el que manda un admin de tenant, a quien el servidor le impone el suyo. */
  idTenant: number | null
}

const FORMULARIO_VACIO: Formulario = {
  id: null,
  usuario: '',
  mail: '',
  rolId: ROL.Vendedor,
  estado: 'Activo',
  password: '',
  idTenant: null,
}

const AVISO_REFRESCO_FALLIDO = 'Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.'
const AVISO_REFRESCO_FALLIDO_BAJA =
  'Se eliminó, pero no se pudo actualizar la vista. Recargá la pantalla.'

const ERROR_TENANTS =
  'No se pudo cargar la lista de tenants: no se pueden crear usuarios hasta que llegue. Abrí "Nuevo" para reintentar.'

const ERROR_ALTA_SIN_TENANTS = 'No se puede crear el usuario: todavía falta la lista de tenants.'

/**
 * ABM de usuarios. La columna "Tenant" rinde el nombre del tenant de la cuenta o el literal
 * "Plataforma" cuando `idTenant` es null — esa copia la pone la web, nunca el servidor
 * (design D14), y el discriminador es `idTenant`, no el nombre: un `nombreTenant` nulo con
 * `idTenant` presente es un huérfano (tenant dado de baja), no personal de plataforma
 * (Reconciliación 9). La columna y el filtro por tenant se rinden con el MISMO criterio que en
 * `Empresas`/`PuntosVenta`: solo para un actor de plataforma. Un admin de tenant ve una sola
 * opción —la suya— y filtrar por ella no angosta nada (spec S5).
 */
export function Usuarios() {
  const { usuario: actual } = useAuth()

  const [pagina, setPagina] = useState<PaginaDe<UsuarioListado> | null>(null)
  const [roles, setRoles] = useState<RolListado[]>([])
  const [busqueda, setBusqueda] = useState('')
  /** Término REALMENTE aplicado a la tabla: lo escribe `buscar()`, nunca el tipeo. El refresco
   * post-escritura usa este y no el borrador del input, que puede tener texto sin buscar. */
  const [busquedaAplicada, setBusquedaAplicada] = useState('')
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  /** Slot PROPIO del fallo de la segunda escritura del alta/edición (el POST de contraseña). No
   * puede viajar en `aviso` —el PUT sí commiteó, pero esto es un fallo y va en rojo— ni en `error`
   * —el refresco post-escritura lo limpia al terminar bien. */
  const [errorPassword, setErrorPassword] = useState('')
  /** Slot PROPIO del rechazo LOCAL del alta sin universo de tenants. En el slot compartido `error`
   * lo borraba el `setError('')` de cualquier carga posterior (misma clase que R2-1): es un aviso
   * del FORMULARIO, no de la tabla, y comparte dueño con él — un slot, un dueño. */
  const [errorAlta, setErrorAlta] = useState('')
  const [aviso, setAviso] = useState('')
  const [formulario, setFormulario] = useState<Formulario | null>(null)
  const [ocupado, setOcupado] = useState(false)
  const [filtroTenant, setFiltroTenant] = useState(SIN_FILTRO)
  /** Baja pendiente de confirmación: ver `Tenants.tsx`. La puerta es MODAL —`bloqueado` deja
   * inerte el resto de la pantalla mientras está abierta— y NO acuña token: eso lo hace la
   * escritura, al confirmar. */
  const [baja, setBaja] = useState<UsuarioListado | null>(null)
  /** Control que abrió la puerta, capturado en el `onClick` y no dentro de la puerta: ver
   * `ConfirmacionDeBaja.tsx`. Para cuando el efecto de montaje corre, el control ya quedó
   * `disabled` y el navegador se llevó el foco al `<body>`. */
  const [disparadorDeLaPuerta, setDisparadorDeLaPuerta] = useState<HTMLElement | null>(null)
  const [tenantsDePlataforma, setTenantsDePlataforma] = useState<TenantListado[]>([])
  const [tenantsDePlataformaCargando, setTenantsDePlataformaCargando] = useState(false)
  const [tenantsDePlataformaFallo, setTenantsDePlataformaFallo] = useState(false)

  /** Contrato de invalidación: ver `Tenants.tsx` — mismo patrón en las cuatro pantallas raíz. */
  const generacion = useRef(0)
  /** Generación propia: el universo de tenants no depende del ciclo búsqueda/escritura de
   * `generacion` y no debe descartarse por una acción no relacionada (ej. un alta en curso). */
  const generacionTenants = useRef(0)
  /**
   * Espejo SÍNCRONO de `ocupado`, y la ÚNICA guarda de re-entrancia válida: ver `Tenants.tsx`.
   * Dos clicks del MISMO tick leen el mismo render, así que el estado los deja pasar a los dos.
   */
  const ocupadoRef = useRef(false)

  const cargar = useCallback(async (token: number, termino: string, propagar = false) => {
    setCargando(true)
    try {
      const parametros = termino ? `?busqueda=${encodeURIComponent(termino)}` : ''
      const respuesta = await api.get<PaginaDe<UsuarioListado>>(`/usuarios${parametros}`)
      if (generacion.current !== token) return
      setPagina(respuesta)
      setFiltroTenant((prev) => seleccionVigente(opcionesDeTenant(respuesta.items), prev))
      setError('')
    } catch (e) {
      if (generacion.current !== token) return
      if (propagar) throw e
      setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los usuarios.')
    } finally {
      if (generacion.current === token) setCargando(false)
    }
  }, [])

  function buscar(termino: string) {
    if (ocupadoRef.current) return
    // `errorPassword` reporta el fallo parcial de una escritura YA TERMINADA: una búsqueda nueva
    // es una tabla nueva y arrastrarlo era ruido de la pantalla anterior. `errorAlta` NO se apaga
    // acá a propósito — reporta una precondición VIVA del formulario abierto (falta el universo de
    // tenants), que la búsqueda no cambia; apagarlo escondería algo que sigue siendo cierto.
    setErrorPassword('')
    setBusquedaAplicada(termino)
    void cargar(++generacion.current, termino)
  }

  useEffect(() => {
    void cargar(++generacion.current, '')
    api.get<RolListado[]>('/roles').then(setRoles).catch(() => setRoles([]))
  }, [cargar])

  const esPlataforma = actual?.rolId === ROL.Root

  /** Universo COMPLETO de tenants para el selector del ALTA (tarea 2.17, gap del tamaño de
   * página): `opcionesDeTenant(filas)` solo ve tenants con un usuario en la página actual
   * (`tamanio` 25), así que un tenant sin usuario ahí quedaba imposible de asignar. Se pide
   * SOLO para un actor de plataforma — `GET /plataforma/tenants` es `SoloPlataforma` y jamás
   * 403 para este actor, pero un admin de tenant no debe enumerar tenants (spec S5) — y el
   * filtro de la tabla sigue sin tocar (design D15, sin segunda consulta ahí).
   *
   * El fallo NO se traga (`react-async-state` regla 7): sin universo el `<select>` requerido
   * quedaría vacío y el alta sería imposible sin decir por qué. Se rinde el error y abrir "Nuevo"
   * reintenta, que es el único momento en que el universo hace falta.
   *
   * Ese fallo tiene BANNER PROPIO, derivado de `tenantsDePlataformaFallo`, y no pasa por `error`:
   * son dos fuentes independientes y el slot compartido las pisaba en los dos sentidos — un
   * `setError('')` de la tabla borraba este aviso con el universo todavía caído, y este aviso
   * tapaba un fallo real del listado de usuarios. Un reintento exitoso lo apaga. */
  const cargarTenantsDePlataforma = useCallback(() => {
    const token = ++generacionTenants.current
    setTenantsDePlataformaCargando(true)
    clienteDeOrganizacion
      .listarTenants()
      .then((lista) => {
        if (generacionTenants.current !== token) return
        setTenantsDePlataforma(lista)
        setTenantsDePlataformaFallo(false)
      })
      .catch(() => {
        if (generacionTenants.current !== token) return
        setTenantsDePlataforma([])
        setTenantsDePlataformaFallo(true)
      })
      .finally(() => {
        if (generacionTenants.current === token) setTenantsDePlataformaCargando(false)
      })
  }, [])

  useEffect(() => {
    if (!esPlataforma) return

    cargarTenantsDePlataforma()
  }, [esPlataforma, cargarTenantsDePlataforma])

  /** El alta de un actor de plataforma NO puede mandarse sin universo de tenants: el `<select>` es
   * `required` pero mientras está `disabled` la validación HTML del formulario no lo mira, así que
   * el POST saldría con `idTenant: null` y el servidor lo rechazaría con 400 `tenant_requerido`.
   * Guardar queda inerte en esa ventana (`react-async-state` regla 5) y `guardar()` lo re-chequea,
   * porque un doble click en el mismo tick le gana al atributo `disabled` (regla 9). */
  const universoDeTenantsIndisponible =
    esPlataforma && (tenantsDePlataformaCargando || tenantsDePlataformaFallo)

  async function guardar() {
    if (!formulario || ocupadoRef.current) return
    if (formulario.id === null && universoDeTenantsIndisponible) {
      setErrorAlta(ERROR_ALTA_SIN_TENANTS)

      return
    }

    const datos = formulario
    const token = ++generacion.current
    ocupadoRef.current = true
    setOcupado(true)
    setError('')
    setErrorPassword('')
    setErrorAlta('')
    setAviso('')

    try {
      let mensajeOk: string
      try {
        if (datos.id === null) {
          const alta: CrearUsuario = {
            usuario: datos.usuario,
            mail: datos.mail,
            rolId: datos.rolId,
            password: datos.password,
            estado: datos.estado,
            idTenant: datos.idTenant,
          }
          await api.post('/usuarios', alta)
          mensajeOk = `Usuario "${datos.usuario}" creado.`
        } else {
          const edicion: ActualizarUsuario = {
            usuario: datos.usuario,
            mail: datos.mail,
            rolId: datos.rolId,
            estado: datos.estado,
          }
          await api.put(`/usuarios/${datos.id}`, edicion)
          mensajeOk = `Usuario "${datos.usuario}" actualizado.`
        }
      } catch (e) {
        if (generacion.current === token) setError(e instanceof ErrorApi ? e.message : 'No se pudo guardar.')

        return
      }

      if (generacion.current !== token) return

      // El cambio de contraseña es una SEGUNDA escritura y lleva su propio try: el PUT de arriba ya
      // commiteó, así que un fallo acá no puede reportarse como "no se pudo guardar"
      // (`react-async-state` regla 6). Las dos mitades se rinden POR SEPARADO y a la vez: la
      // confirmación del PUT commiteado en el aviso verde, el fallo de la contraseña en rojo. Meter
      // el fallo en el aviso verde lo anunciaba como parte de un éxito.
      if (datos.id !== null && datos.password) {
        try {
          await api.post(`/usuarios/${datos.id}/password`, { passwordNueva: datos.password })
        } catch (e) {
          if (generacion.current !== token) return
          const detalle = e instanceof ErrorApi ? e.message : 'Reintentá el cambio de contraseña.'
          setErrorPassword(`Se guardó el perfil, pero no se pudo cambiar la contraseña. ${detalle}`)
        }
      }

      setFormulario(null)
      await refrescarTrasEscribir(token, mensajeOk, AVISO_REFRESCO_FALLIDO)
    } finally {
      ocupadoRef.current = false
      setOcupado(false)
    }
  }

  /** El refresco post-escritura va fuera del try/catch de la escritura: una escritura que ya
   * commiteó nunca se reporta como fallida (`react-async-state` regla 6). Refresca con el término
   * REALMENTE aplicado, no con el borrador del input: tipear sin buscar y dar de baja una fila no
   * puede angostar la tabla por un texto que el operador nunca aplicó.
   *
   * NO apaga `ocupado`: de eso se ocupa el `finally` ungated de cada escritura — ver `Tenants.tsx`. */
  async function refrescarTrasEscribir(token: number, mensajeOk: string, avisoDeFallo: string) {
    if (generacion.current !== token) return

    setAviso(mensajeOk)
    try {
      await cargar(token, busquedaAplicada, true)
    } catch {
      if (generacion.current === token) setAviso(`${mensajeOk} ${avisoDeFallo}`)
    }
  }

  /** Acciones sin puerta de confirmación (hoy solo "Desbloquear"): la baja tiene la suya y su
   * propia copia por `codigo`, así que no pasa por acá. */
  async function accion(construirPromesa: () => Promise<unknown>, mensajeOk: string) {
    if (ocupadoRef.current) return

    const token = ++generacion.current
    ocupadoRef.current = true
    setOcupado(true)
    setError('')
    setErrorPassword('')
    setErrorAlta('')
    setAviso('')
    try {
      try {
        await construirPromesa()
      } catch (e) {
        if (generacion.current === token) {
          setError(e instanceof ErrorApi ? e.message : 'No se pudo completar la acción.')
        }

        return
      }

      await refrescarTrasEscribir(token, mensajeOk, AVISO_REFRESCO_FALLIDO)
    } finally {
      ocupadoRef.current = false
      setOcupado(false)
    }
  }

  /** Ver `Tenants.tsx`: mismo patrón de puerta, mismo contrato de invalidación, misma re-entrancia.
   * Reemplaza al `confirm()` nativo: la puerta tiene que quedar inerte mientras el DELETE está en
   * vuelo, y un diálogo del navegador no puede. Abrir NO acuña generación: no hay escritura
   * todavía.
   *
   * `errorAlta` NO se apaga acá, igual que en `buscar()`: reporta una precondición VIVA del
   * formulario abierto —falta el universo de tenants—, y abrir la puerta de baja de OTRA fila no
   * la cambia. Apagarlo escondía algo que seguía siendo cierto. */
  function pedirBaja(u: UsuarioListado, disparador: HTMLElement | null) {
    if (ocupadoRef.current) return

    setDisparadorDeLaPuerta(disparador)
    setBaja(u)
    setError('')
    setErrorPassword('')
    setAviso('')
  }

  /** Cancelar no supersede nada: solo cierra la puerta. Acá el `++generacion.current` era además el
   * único camino ALCANZABLE que clavaba la pantalla: con una búsqueda en vuelo, descartarla dejaba
   * sin ejecutar el `finally` gateado de `cargar` y "Cargando…" quedaba para siempre. Limpia los
   * avisos en simetría con la apertura, para no dejar un 409 en rojo sin puerta al lado. `errorAlta`
   * queda fuera de esa simetría por la misma razón que en `pedirBaja`: es del formulario, no de la
   * puerta, y sigue siendo cierto después de cancelarla. */
  function cancelarBaja() {
    if (ocupadoRef.current) return

    setDisparadorDeLaPuerta(null)
    setBaja(null)
    setError('')
    setErrorPassword('')
    setAviso('')
  }

  async function confirmarBaja() {
    if (!baja || ocupadoRef.current) return

    // El token se acuña ACÁ, primera sentencia síncrona de la escritura (`react-async-state`
    // regla 2): ver `Tenants.tsx`.
    const fila = baja
    const token = ++generacion.current
    ocupadoRef.current = true
    setOcupado(true)
    setError('')
    setErrorPassword('')
    setErrorAlta('')
    setAviso('')
    try {
      try {
        await api.delete(`/usuarios/${fila.id}`)
      } catch (e) {
        // Un rechazo SIEMPRE se rinde, con la puerta abierta al lado del motivo. La copia la elige
        // el `codigo` (`usuario_en_uso` y los rechazos preexistentes de `PoliticaDeRoles`), nunca
        // el `mensaje` — que igual se rinde porque es lo único que nombra QUÉ hay a nombre de la
        // cuenta.
        setError(copiaDeFalloDeBaja(e, 'el usuario'))

        return
      }

      // Un 204 SIEMPRE cierra la puerta y refresca; la generación solo gobierna el REFRESCO.
      setBaja(null)
      // La baja de la fila que se está editando se lleva también su formulario: dejarlo abierto
      // ofrecía guardar sobre una entidad que ya no existe, y el PUT moría en 404.
      setFormulario((prev) => (prev?.id === fila.id ? null : prev))
      await refrescarTrasEscribir(
        token,
        `Usuario "${fila.usuario}" dado de baja.`,
        AVISO_REFRESCO_FALLIDO_BAJA,
      )
    } finally {
      ocupadoRef.current = false
      setOcupado(false)
    }
  }

  /** La puerta abierta bloquea la pantalla entera, no solo la escritura en vuelo: ver `Tenants.tsx`.
   * Es lo que hace que la búsqueda —el único bumper de generación que quedaba alcanzable con la
   * puerta abierta— no pueda dispararse mientras el operador decide. */
  const bloqueado = ocupado || baja !== null

  // El backend valida igual; esto solo evita mostrar botones que van a fallar.
  const puedeEditar = (u: UsuarioListado) =>
    u.rolId !== ROL.Root || actual?.rolId === ROL.Root

  // Derivación pura sobre la página YA CARGADA: sin fetch nuevo y sin parámetro de consulta. Esto
  // sigue rigiendo el FILTRO de la tabla (design D15) — solo el selector del ALTA cambia de fuente.
  const filas = pagina?.items ?? []
  const opcionesTenant = opcionesDeTenant(filas)
  const tenantVigente = seleccionVigente(opcionesTenant, filtroTenant)
  const visibles = filtrarPorTenant(filas, tenantVigente)

  // El selector del alta solo se rinde para un actor de plataforma (`ofreceTenant`), así que su
  // única fuente es el universo pedido arriba.
  const tenantsAsignables = opcionesDeTenantAsignable(tenantsDePlataforma)

  const herramientas = (
    <nav className="p-2 d-flex gap-2">
      <input
        type="search"
        className="form-control form-control-sm rounded-0"
        placeholder="Buscar usuario o mail…"
        value={busqueda}
        onChange={(e) => setBusqueda(e.target.value)}
        onKeyDown={(e) => e.key === 'Enter' && buscar(busqueda)}
        disabled={bloqueado}
      />
      <button
        type="button"
        className="btn btn-sm btn-outline-light rounded-0"
        onClick={() => buscar(busqueda)}
        disabled={bloqueado}
      >
        Buscar
      </button>
      <button
        type="button"
        className="btn btn-sm btn-success rounded-0 text-nowrap"
        onClick={() => {
          setFormulario({ ...FORMULARIO_VACIO })
          setAviso('')
          setError('')
          setErrorPassword('')
          setErrorAlta('')
          // Reintento del universo de tenants: abrir el alta es el único momento en que hace falta.
          // `!tenantsDePlataformaCargando` evita que reabrir "Nuevo" con el reintento EN VUELO
          // dispare un segundo `GET /plataforma/tenants` por click.
          if (esPlataforma && tenantsDePlataformaFallo && !tenantsDePlataformaCargando) {
            cargarTenantsDePlataforma()
          }
        }}
        disabled={bloqueado}
      >
        Nuevo
      </button>
    </nav>
  )

  return (
    <div className="container-fluid py-4">
      <Box titulo="Usuarios" variante="inverse" herramientas={herramientas}>
        {error && <div className="alert alert-danger rounded-0">{error}</div>}
        {/* Gateado en `esPlataforma` como todo lo demás que depende del universo de tenants: para
            un admin de tenant ese `GET` ni se dispara, así que la bandera no puede prender — el
            gate es paridad con sus elementos hermanos, no una rama viva. */}
        {esPlataforma && tenantsDePlataformaFallo && (
          <div className="alert alert-danger rounded-0">{ERROR_TENANTS}</div>
        )}
        {errorAlta && <div className="alert alert-danger rounded-0">{errorAlta}</div>}
        {errorPassword && <div className="alert alert-danger rounded-0">{errorPassword}</div>}
        {aviso && <div className="alert alert-success rounded-0">{aviso}</div>}

        {baja && (
          <ConfirmacionDeBaja
            titulo={`al usuario "${baja.usuario}"`}
            ocupado={ocupado}
            disparador={disparadorDeLaPuerta}
            onConfirmar={confirmarBaja}
            onCancelar={cancelarBaja}
          />
        )}

        {formulario && (
          <FormularioUsuario
            key={formulario.id ?? 'nuevo'}
            valor={formulario}
            roles={roles}
            tenants={tenantsAsignables}
            ofreceTenant={esPlataforma}
            tenantsCargando={esPlataforma ? tenantsDePlataformaCargando : cargando}
            tenantIndisponible={universoDeTenantsIndisponible}
            guardando={ocupado}
            bloqueado={bloqueado}
            onCambio={setFormulario}
            onGuardar={guardar}
            onCancelar={() => {
              setFormulario(null)
              setErrorPassword('')
              setErrorAlta('')
            }}
          />
        )}

        {cargando ? (
          <Cargando />
        ) : (
          <>
            {/* El filtro por tenant se rinde con el mismo criterio que la COLUMNA de tenant: solo
                para un actor de plataforma. Para un admin de tenant TODAS las filas son de su
                propio tenant, así que la columna repite el mismo nombre y el filtro no angosta
                nada — misma paridad que `Empresas.tsx` y `PuntosVenta.tsx`. */}
            {esPlataforma && (
              <div className="row g-2 mb-3">
                <div className="col-md-4">
                  <label className="form-label" htmlFor="u-filtro-tenant">
                    Tenant
                  </label>
                  <select
                    id="u-filtro-tenant"
                    className="form-select rounded-0"
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
                    <th>ID</th>
                    <th>Usuario</th>
                    <th>Mail</th>
                    {esPlataforma && <th>Tenant</th>}
                    <th>Rol</th>
                    <th>Estado</th>
                    <th>Última conexión</th>
                    <th className="text-end">Acciones</th>
                  </tr>
                </thead>
                <tbody>
                  {visibles.map((u) => (
                    <tr key={u.id}>
                      <td>{String(u.id).padStart(4, '0')}</td>
                      <td>{u.usuario}</td>
                      <td>{u.mail}</td>
                      {esPlataforma && <td>{etiquetaDeTenant(u)}</td>}
                      <td>{u.rol}</td>
                      <td>
                        <EtiquetaEstado estado={u.estado} />
                      </td>
                      <td>
                        {u.ultimaConexion
                          ? new Date(u.ultimaConexion).toLocaleString('es-AR')
                          : '—'}
                      </td>
                      <td className="text-end text-nowrap">
                        {puedeEditar(u) && (
                          <button
                            type="button"
                            className="btn btn-sm btn-outline-primary rounded-0 me-1"
                            onClick={() => {
                              setFormulario({
                                id: u.id,
                                usuario: u.usuario,
                                mail: u.mail,
                                rolId: u.rolId,
                                estado: u.estado,
                                password: '',
                                idTenant: u.idTenant,
                              })
                              setAviso('')
                              setError('')
                              setErrorPassword('')
                              setErrorAlta('')
                            }}
                            disabled={bloqueado}
                          >
                            Editar
                          </button>
                        )}
                        {u.estado === 'Bloqueado' && (
                          <button
                            type="button"
                            className="btn btn-sm btn-outline-warning rounded-0 me-1"
                            onClick={() =>
                              accion(
                                () => api.post(`/usuarios/${u.id}/desbloquear`),
                                `Usuario "${u.usuario}" desbloqueado.`,
                              )
                            }
                            disabled={bloqueado}
                          >
                            Desbloquear
                          </button>
                        )}
                        {puedeEditar(u) && u.rolId !== ROL.Root && u.id !== actual?.id && (
                          <button
                            type="button"
                            className="btn btn-sm btn-outline-danger rounded-0"
                            onClick={(evento) => pedirBaja(u, evento.currentTarget)}
                            disabled={bloqueado}
                          >
                            Baja
                          </button>
                        )}
                      </td>
                    </tr>
                  ))}
                  {visibles.length === 0 && (
                    <tr>
                      <td colSpan={esPlataforma ? 8 : 7} className="text-center text-muted py-4">
                        No hay usuarios que coincidan con la búsqueda.
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

function EtiquetaEstado({ estado }: { estado: EstadoUsuario }) {
  const clase =
    estado === 'Activo'
      ? 'text-bg-success'
      : estado === 'Bloqueado'
        ? 'text-bg-danger'
        : 'text-bg-secondary'

  return <span className={`badge rounded-0 ${clase}`}>{estado}</span>
}

/**
 * `tenants` es el conjunto de tenants ASIGNABLES, y solo se ofrece a un actor de plataforma
 * (`ofreceTenant`): un admin de tenant no enumera tenants — crea siempre dentro del suyo y el
 * servidor se lo impone (spec S5).
 *
 * El selector espeja `PoliticaDeRoles.ValidarConsistenciaDeRolYAlcance`, que sigue siendo la
 * autoridad: root SIEMPRE es de plataforma (tenant nulo) y cualquier otro rol SIEMPRE necesita
 * uno. La rama de rol root es DEFENSA EN PROFUNDIDAD, no un camino vivo: `GET /roles`
 * (`PoliticaDeRoles.RolesAsignablesPor`) no devuelve Root para NINGÚN actor, ni siquiera para un
 * root, así que la opción no llega al `<select>` de rol y la combinación rol-root-con-tenant no es
 * alcanzable desde esta pantalla. La rama existe por si el catálogo de roles cambiara. Lo que sí
 * es camino vivo es el `required` del tenant para cualquier otro rol: sin él el servidor contesta
 * 400 `tenant_requerido`, y ese error se rinde por el camino de siempre.
 */
function FormularioUsuario({
  valor,
  roles,
  tenants,
  ofreceTenant,
  tenantsCargando,
  tenantIndisponible,
  guardando,
  bloqueado,
  onCambio,
  onGuardar,
  onCancelar,
}: {
  valor: Formulario
  roles: RolListado[]
  tenants: OpcionDeFiltro[]
  ofreceTenant: boolean
  tenantsCargando: boolean
  tenantIndisponible: boolean
  /** Solo gobierna la ETIQUETA del botón: "Guardando…" es cierto de una escritura en vuelo, no de
   * una puerta de confirmación abierta. */
  guardando: boolean
  /** Escritura en vuelo O puerta de confirmación abierta: gobierna todo `disabled`. */
  bloqueado: boolean
  onCambio: (f: Formulario) => void
  onGuardar: () => void
  onCancelar: () => void
}) {
  const esNuevo = valor.id === null
  const esRolDePlataforma = valor.rolId === ROL.Root
  const sinTenantAsignable = esNuevo && ofreceTenant && tenantIndisponible

  return (
    <form
      className="row g-3 border p-3 mb-4 bg-white"
      autoComplete="off"
      onSubmit={(e) => {
        e.preventDefault()
        onGuardar()
      }}
    >
      <div className="col-12">
        <strong>{esNuevo ? 'Nuevo usuario' : `Editando usuario ${valor.id}`}</strong>
      </div>

      <div className="col-md-3">
        <label className="form-label" htmlFor="f-usuario">
          Usuario
        </label>
        <input
          id="f-usuario"
          className="form-control rounded-0"
          maxLength={40}
          value={valor.usuario}
          onChange={(e) => onCambio({ ...valor, usuario: e.target.value })}
          disabled={bloqueado}
          required
        />
      </div>

      <div className="col-md-3">
        <label className="form-label" htmlFor="f-mail">
          Mail
        </label>
        <input
          id="f-mail"
          type="email"
          className="form-control rounded-0"
          maxLength={255}
          value={valor.mail}
          onChange={(e) => onCambio({ ...valor, mail: e.target.value })}
          disabled={bloqueado}
          required
        />
      </div>

      <div className="col-md-2">
        <label className="form-label" htmlFor="f-rol">
          Rol
        </label>
        <select
          id="f-rol"
          className="form-select rounded-0"
          value={valor.rolId}
          onChange={(e) => {
            // El tenant se limpia en el ESTADO, no solo al pintarlo: el alta manda `idTenant` tal
            // cual. Defensa en profundidad — ver el doc-comment: `GET /roles` no ofrece Root.
            const rolId = Number(e.target.value)
            onCambio({ ...valor, rolId, idTenant: rolId === ROL.Root ? null : valor.idTenant })
          }}
          disabled={bloqueado}
        >
          {roles.map((r) => (
            <option key={r.id} value={r.id}>
              {r.nombre}
            </option>
          ))}
        </select>
      </div>

      {esNuevo && ofreceTenant && (
        <div className="col-md-3">
          <label className="form-label" htmlFor="f-tenant">
            Tenant de la cuenta
          </label>
          <select
            id="f-tenant"
            className="form-select rounded-0"
            value={valor.idTenant === null ? '' : String(valor.idTenant)}
            onChange={(e) =>
              onCambio({ ...valor, idTenant: e.target.value === '' ? null : Number(e.target.value) })
            }
            disabled={bloqueado || tenantsCargando || esRolDePlataforma || sinTenantAsignable}
            required
          >
            <option value="">
              {esRolDePlataforma
                ? 'Sin tenant (plataforma)'
                : tenantsCargando
                  ? 'Cargando…'
                  : sinTenantAsignable
                    ? 'No se pudo cargar la lista'
                    : 'Elegí un tenant'}
            </option>
            {tenants.map((t) => (
              <option key={t.valor} value={t.valor}>
                {t.etiqueta}
              </option>
            ))}
          </select>
        </div>
      )}

      <div className="col-md-2">
        <label className="form-label" htmlFor="f-estado">
          Estado
        </label>
        <select
          id="f-estado"
          className="form-select rounded-0"
          value={valor.estado}
          onChange={(e) => onCambio({ ...valor, estado: e.target.value as EstadoUsuario })}
          disabled={bloqueado}
        >
          {ESTADOS_USUARIO.map((e) => (
            <option key={e} value={e}>
              {e}
            </option>
          ))}
        </select>
      </div>

      <div className="col-md-2">
        <label className="form-label" htmlFor="f-password">
          Contraseña
        </label>
        <input
          id="f-password"
          type="password"
          className="form-control rounded-0"
          placeholder={esNuevo ? 'Mínimo 8 caracteres' : 'Dejar vacío para no cambiar'}
          value={valor.password}
          onChange={(e) => onCambio({ ...valor, password: e.target.value })}
          disabled={bloqueado}
          required={esNuevo}
        />
      </div>

      <div className="col-12 d-flex gap-2">
        <button
          type="submit"
          className="btn btn-success rounded-0"
          disabled={bloqueado || sinTenantAsignable}
        >
          {guardando ? 'Guardando…' : 'Guardar'}
        </button>
        <button
          type="button"
          className="btn btn-outline-secondary rounded-0"
          onClick={onCancelar}
          disabled={bloqueado}
        >
          Cancelar
        </button>
      </div>
    </form>
  )
}
