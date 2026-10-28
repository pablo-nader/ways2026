import { useCallback, useEffect, useRef, useState } from 'react'
import { api, ErrorApi } from '../api/cliente'
import { copiaDeFalloDeBaja } from '../api/bajas'
import {
  clienteDeOrganizacion,
  etiquetaDeTenant,
  opcionesDeFiltroPorTenant,
  opcionesDeTenantAsignable,
} from '../api/organizacion'
import { ultimaPaginaDePadron } from '../api/padron'
import {
  clienteDeUsuarios,
  esColumnaOrdenableDeUsuarios,
  FILTROS_DE_USUARIOS_VACIOS,
  type ColumnaOrdenableDeUsuarios,
  type ConsultaDeUsuarios,
  type FiltrosDeUsuarios,
} from '../api/usuarios'
import { ROL, ESTADOS_USUARIO } from '../api/tipos'
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
import { EncabezadoOrdenable } from '../componentes/grilla'
import { FilaDeFiltrosDePadron, type CeldaDeFiltroDePadron } from '../componentes/padron/FilaDeFiltrosDePadron'
import { PaginadorDePadron } from '../componentes/padron/PaginadorDePadron'
import { usePadronEnServidor } from '../componentes/padron/usePadronEnServidor'
import { useAuth } from '../auth/useAuth'
import { BotonIcono } from '../componentes/BotonIcono'
import { fechaHoraATexto } from '../formato/fechas'
import { ModalDeUsuario } from './usuarios/ModalDeUsuario'
import { FORMULARIO_VACIO, type Formulario } from './usuarios/usuarioFormulario'

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
 * `Empresas`/`PuntosVenta`: solo para un actor de plataforma. Un admin de tenant no tiene
 * columna, filtro ni orden por tenant: todas sus filas son de su tenant, no le serviría de nada y el servidor
 * rechaza el parámetro con 400 (spec S5). El filtro lo resuelve el servidor, con las opciones del universo completo
 * de tenants.
 */
export function Usuarios() {
  const { usuario: actual } = useAuth()

  const [pagina, setPagina] = useState<PaginaDe<UsuarioListado> | null>(null)
  const [roles, setRoles] = useState<RolListado[]>([])
  /** Texto de la caja de búsqueda general, que se aplica con Enter o con Buscar. */
  const [textoDeBusqueda, setTextoDeBusqueda] = useState('')
  const [cargando, setCargando] = useState(true)
  /** Hasta la primera respuesta la pantalla es un "Cargando…"; después la tabla y sus filtros no se desmontan. */
  const [iniciando, setIniciando] = useState(true)
  const [error, setError] = useState('')
  /** Slot PROPIO del fallo de la segunda escritura del alta/edición (el POST de contraseña). No
   * puede viajar en `aviso` —el PUT sí commiteó, pero esto es un fallo y va en rojo— ni en `error`
   * —el refresco post-escritura lo limpia al terminar bien. */
  const [errorPassword, setErrorPassword] = useState('')
  /** Slot PROPIO del rechazo LOCAL del alta sin universo de tenants. En el slot compartido `error`
   * lo borraba el `setError('')` de cualquier carga posterior (misma clase que R2-1): es un aviso
   * del FORMULARIO, no de la tabla, y comparte dueño con él — un slot, un dueño. */
  const [errorAlta, setErrorAlta] = useState('')
  /** Rechazo del servidor al guardar: se rinde DENTRO del modal, porque un alert de la pantalla
   * quedaría detrás del fondo. Slot propio, igual que `errorAlta`. */
  const [errorFormulario, setErrorFormulario] = useState('')
  const [aviso, setAviso] = useState('')
  const [formulario, setFormulario] = useState<Formulario | null>(null)
  const [ocupado, setOcupado] = useState(false)
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
  /** Destino de foco al cerrar el modal cuando su disparador ya no existe (la tabla se reemplaza por
   * "Cargando…" durante el refresco): el botón de alta, que es estable. */
  const agregarRef = useRef<HTMLButtonElement>(null)
  /** Espejo de `bloqueado` (una escritura en vuelo o la puerta de baja abierta) para el temporizador de los filtros, que no lee el render: un filtro tipeado antes espera a que termine. */
  const bloqueadoRef = useRef(false)

  const esPlataforma = actual?.rolId === ROL.Root

  /** La consulta aplicada (filtros, orden, página y tamaño) vive en el hook: el refresco posterior a una escritura
   * recarga `consultaRef`, nunca el texto tipeado en un filtro y todavía no aplicado. El orden y el filtro por tenant son
   * solo del actor de plataforma (para otro el servidor responde 400): su encabezado y su filtro solo se rinden para él,
   * así que un actor de tenant no tiene cómo pedirlos. */
  const padron = usePadronEnServidor<FiltrosDeUsuarios, ColumnaOrdenableDeUsuarios>(FILTROS_DE_USUARIOS_VACIOS, {
    esColumnaOrdenable: esColumnaOrdenableDeUsuarios,
    ocupado: () => ocupadoRef.current || bloqueadoRef.current,
  })
  const { irAPagina } = padron

  const cargar = useCallback(
    async (token: number, consulta: ConsultaDeUsuarios, propagar = false) => {
      setCargando(true)
      try {
        const respuesta = await clienteDeUsuarios.listarGrilla(consulta)
        if (generacion.current !== token) return

        // Una baja puede vaciar la última página: se pide la última válida en vez de mostrar "Página 3 de 2".
        const ultima = ultimaPaginaDePadron(respuesta.total, respuesta.tamanio)
        if (respuesta.pagina > ultima) {
          irAPagina(ultima)

          return
        }

        setPagina(respuesta)
        setError('')
      } catch (e) {
        if (generacion.current !== token) return
        if (propagar) throw e
        // Sin filas: el encabezado ya declara el orden pedido y no puede quedar sobre las del orden anterior.
        setPagina(null)
        setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los usuarios.')
      } finally {
        if (generacion.current === token) {
          setCargando(false)
          setIniciando(false)
        }
      }
    },
    [irAPagina],
  )

  function buscar(termino: string) {
    if (ocupadoRef.current) return
    // `errorPassword` reporta el fallo parcial de una escritura YA TERMINADA: una búsqueda nueva
    // es una tabla nueva y arrastrarlo era ruido de la pantalla anterior. `errorAlta` NO se apaga
    // acá a propósito — reporta una precondición VIVA del formulario abierto (falta el universo de
    // tenants), que la búsqueda no cambia; apagarlo escondería algo que sigue siendo cierto.
    setErrorPassword('')
    padron.cambiarFiltros({ busqueda: termino }, true)
  }

  // Se vuelve a pedir el listado cada vez que cambia la consulta aplicada (filtros, orden, página o tamaño).
  // `errorPassword` es de una escritura ya terminada: otra consulta (filtro, orden, página o tamaño) es una tabla nueva.
  useEffect(() => {
    setErrorPassword('')
    void cargar(++generacion.current, padron.consulta)
  }, [cargar, padron.consulta])

  useEffect(() => {
    api.get<RolListado[]>('/roles').then(setRoles).catch(() => setRoles([]))
  }, [])

  /** Universo COMPLETO de tenants para el selector del ALTA y para el filtro por tenant de la
   * tabla: derivarlo de las filas solo vería los tenants con un usuario en la página actual, así
   * que un tenant sin usuario ahí quedaba imposible de asignar o de filtrar. Se pide SOLO para
   * un actor de plataforma — `GET /plataforma/tenants` es `SoloPlataforma` y jamás 403 para este
   * actor, pero un admin de tenant no debe enumerar tenants (spec S5). Es la excepción a la
   * regla D15 de `Empresas`/`PuntosVenta`, que derivan sus opciones de las filas cargadas.
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
    // Una lectura en vuelo queda invalidada por el token de esta escritura: no va a dejar datos ni a
    // bajar `cargando`. Si la escritura falla no hay refresco que lo repare, así que se vuelve a pedir.
    const habiaLecturaEnVuelo = cargando
    const token = ++generacion.current
    ocupadoRef.current = true
    setOcupado(true)
    setError('')
    setErrorPassword('')
    setErrorAlta('')
    setErrorFormulario('')
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
        if (generacion.current === token) {
          setErrorFormulario(e instanceof ErrorApi ? e.message : 'No se pudo guardar.')
          if (habiaLecturaEnVuelo) void cargar(++generacion.current, padron.consultaRef.current)
        }

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

      // El modal sigue montado durante el refresco y se cierra en el mismo lote que libera `ocupado`:
      // así el disparador ya está habilitado cuando el modal restaura el foco.
      await refrescarTrasEscribir(token, mensajeOk, AVISO_REFRESCO_FALLIDO)
      setFormulario(null)
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
      await cargar(token, padron.consultaRef.current, true)
    } catch {
      if (generacion.current === token) setAviso(`${mensajeOk} ${avisoDeFallo}`)
    }
  }

  /** Acciones sin puerta de confirmación (hoy solo "Desbloquear"): la baja tiene la suya y su
   * propia copia por `codigo`, así que no pasa por acá. */
  async function accion(construirPromesa: () => Promise<unknown>, mensajeOk: string) {
    if (ocupadoRef.current) return

    // Una lectura en vuelo queda invalidada por el token de esta escritura y su `finally` no baja `cargando`: si la
    // acción falla no hay refresco que lo repare, así que se vuelve a pedir.
    const habiaLecturaEnVuelo = cargando
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
          if (habiaLecturaEnVuelo) void cargar(++generacion.current, padron.consultaRef.current)
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
    // Igual que en `guardar`: la lectura en vuelo que este token invalida se repone si la baja falla.
    const habiaLecturaEnVuelo = cargando
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
        // Un rechazo SIEMPRE se rinde, con la puerta abierta y el motivo adentro de ella. La copia la elige
        // el `codigo` (`usuario_en_uso` y los rechazos preexistentes de `PoliticaDeRoles`), nunca
        // el `mensaje` — que igual se rinde porque es lo único que nombra QUÉ hay a nombre de la
        // cuenta.
        setError(copiaDeFalloDeBaja(e, 'el usuario'))
        if (habiaLecturaEnVuelo && generacion.current === token) {
          void cargar(++generacion.current, padron.consultaRef.current)
        }

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
  useEffect(() => {
    bloqueadoRef.current = bloqueado
  })

  // El backend valida igual; esto solo evita mostrar botones que van a fallar.
  const puedeEditar = (u: UsuarioListado) =>
    u.rolId !== ROL.Root || actual?.rolId === ROL.Root

  const filas = pagina?.items ?? []

  // El selector del alta y el filtro por tenant solo se rinden para un actor de plataforma
  // (`ofreceTenant`), así que su única fuente es el universo pedido arriba.
  const tenantsAsignables = opcionesDeTenantAsignable(tenantsDePlataforma)

  const celdasDeFiltro: readonly CeldaDeFiltroDePadron<FiltrosDeUsuarios>[] = [
    { tipo: 'entero', clave: 'id', etiqueta: 'ID' },
    { tipo: 'texto', clave: 'usuario', etiqueta: 'usuario' },
    { tipo: 'texto', clave: 'mail', etiqueta: 'mail' },
    ...(esPlataforma
      ? [
          {
            tipo: 'opciones',
            clave: 'tenant',
            etiqueta: 'tenant',
            opciones: opcionesDeFiltroPorTenant(tenantsDePlataforma),
          } satisfies CeldaDeFiltroDePadron<FiltrosDeUsuarios>,
        ]
      : []),
    {
      tipo: 'opciones',
      clave: 'rolId',
      etiqueta: 'rol',
      opciones: roles.map((r) => ({ valor: String(r.id), etiqueta: r.nombre })),
    },
    {
      tipo: 'opciones',
      clave: 'estado',
      etiqueta: 'estado',
      opciones: ESTADOS_USUARIO.map((e) => ({ valor: e, etiqueta: e })),
    },
    {
      tipo: 'opciones',
      clave: 'conectado',
      etiqueta: 'conexión',
      opciones: [
        { valor: 'true', etiqueta: 'Con conexión' },
        { valor: 'false', etiqueta: 'Sin conexión' },
      ],
    },
    { tipo: 'acciones' },
  ]

  const herramientas = (
    <nav className="p-2 d-flex gap-2">
      <input
        type="search"
        className="form-control form-control-sm"
        placeholder="Buscar usuario o mail…"
        value={textoDeBusqueda}
        onChange={(e) => setTextoDeBusqueda(e.target.value)}
        onKeyDown={(e) => e.key === 'Enter' && buscar(textoDeBusqueda)}
        disabled={bloqueado}
      />
      <button
        type="button"
        className="btn btn-sm btn-outline-secondary"
        onClick={() => buscar(textoDeBusqueda)}
        disabled={bloqueado}
      >
        Buscar
      </button>
      <BotonIcono
        ref={agregarRef}
        icono="agregar"
        className="text-nowrap"
        onClick={() => {
          setFormulario({ ...FORMULARIO_VACIO })
          setAviso('')
          setError('')
          setErrorPassword('')
          setErrorAlta('')
          setErrorFormulario('')
          // Reintento del universo de tenants: abrir el alta es el único momento en que hace falta.
          // `!tenantsDePlataformaCargando` evita que reabrir "Nuevo" con el reintento EN VUELO
          // dispare un segundo `GET /plataforma/tenants` por click.
          if (esPlataforma && tenantsDePlataformaFallo && !tenantsDePlataformaCargando) {
            cargarTenantsDePlataforma()
          }
        }}
        disabled={bloqueado}
      />
    </nav>
  )

  return (
    <div className="container-fluid py-4">
      <Box titulo="Usuarios" variante="inverse" herramientas={herramientas}>
        {error && !baja && <div className="alert alert-danger">{error}</div>}
        {/* Gateado en `esPlataforma` como todo lo demás que depende del universo de tenants: para
            un admin de tenant ese `GET` ni se dispara, así que la bandera no puede prender — el
            gate es paridad con sus elementos hermanos, no una rama viva. */}
        {esPlataforma && tenantsDePlataformaFallo && (
          <div className="alert alert-danger">{ERROR_TENANTS}</div>
        )}
        {errorPassword && <div className="alert alert-danger">{errorPassword}</div>}
        {aviso && <div className="alert alert-success">{aviso}</div>}

        {baja && (
          <ConfirmacionDeBaja
            titulo={`al usuario "${baja.usuario}"`}
            ocupado={ocupado}
            disparador={disparadorDeLaPuerta}
            error={error}
            onConfirmar={confirmarBaja}
            onCancelar={cancelarBaja}
          />
        )}

        {formulario && (
          <ModalDeUsuario
            key={formulario.id ?? 'nuevo'}
            valor={formulario}
            roles={roles}
            tenants={tenantsAsignables}
            ofreceTenant={esPlataforma}
            tenantsCargando={esPlataforma ? tenantsDePlataformaCargando : cargando}
            tenantIndisponible={universoDeTenantsIndisponible}
            guardando={ocupado}
            bloqueado={bloqueado}
            error={errorAlta || errorFormulario}
            focoDeReserva={agregarRef}
            onCambio={setFormulario}
            onGuardar={guardar}
            onCancelar={() => {
              setFormulario(null)
              setErrorPassword('')
              setErrorAlta('')
              setErrorFormulario('')
            }}
          />
        )}

        {iniciando ? (
          <Cargando />
        ) : (
          <>
            <div className="table-responsive">
              <table className="table table-striped table-hover table-bordered align-middle">
                <thead>
                  <tr>
                    <EncabezadoOrdenable columna="id" orden={padron.consulta.orden} onAlternar={padron.alternarOrden}>
                      ID
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="usuario" orden={padron.consulta.orden} onAlternar={padron.alternarOrden}>
                      Usuario
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="mail" orden={padron.consulta.orden} onAlternar={padron.alternarOrden}>
                      Mail
                    </EncabezadoOrdenable>
                    {esPlataforma && (
                      <EncabezadoOrdenable columna="tenant" orden={padron.consulta.orden} onAlternar={padron.alternarOrden}>
                        Tenant
                      </EncabezadoOrdenable>
                    )}
                    <EncabezadoOrdenable columna="rol" orden={padron.consulta.orden} onAlternar={padron.alternarOrden}>
                      Rol
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable columna="estado" orden={padron.consulta.orden} onAlternar={padron.alternarOrden}>
                      Estado
                    </EncabezadoOrdenable>
                    <EncabezadoOrdenable
                      columna="ultimaConexion"
                      orden={padron.consulta.orden}
                      onAlternar={padron.alternarOrden}
                    >
                      Última conexión
                    </EncabezadoOrdenable>
                    <th scope="col" className="text-end">
                      Acciones
                    </th>
                  </tr>
                  <FilaDeFiltrosDePadron
                    celdas={celdasDeFiltro}
                    valores={padron.borrador}
                    onCambio={padron.cambiarFiltros}
                    onLimpiar={() => {
                      setTextoDeBusqueda('')
                      padron.limpiar()
                    }}
                    hayFiltros={padron.hayFiltros}
                    deshabilitado={bloqueado}
                  />
                </thead>
                <tbody style={cargando ? { opacity: 0.6 } : undefined}>
                  {filas.map((u) => (
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
                          ? fechaHoraATexto(u.ultimaConexion)
                          : '—'}
                      </td>
                      <td className="text-end text-nowrap">
                        {puedeEditar(u) && (
                          <BotonIcono
                            icono="editar"
                            className="me-1"
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
                              setErrorFormulario('')
                            }}
                            disabled={bloqueado}
                          />
                        )}
                        {u.estado === 'Bloqueado' && (
                          <button
                            type="button"
                            className="btn btn-sm btn-outline-warning me-1"
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
                          <BotonIcono
                            icono="eliminar"
                            onClick={(evento) => pedirBaja(u, evento.currentTarget)}
                            disabled={bloqueado}
                          />
                        )}
                      </td>
                    </tr>
                  ))}
                  {pagina?.items.length === 0 && (
                    <tr>
                      <td colSpan={esPlataforma ? 8 : 7} className="text-center text-muted py-4">
                        No hay usuarios que coincidan con los filtros.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>
            {pagina && (
              <PaginadorDePadron
                pagina={pagina.pagina}
                tamanio={pagina.tamanio}
                total={pagina.total}
                unidad="usuario(s)"
                deshabilitado={cargando || bloqueado}
                onCambiarPagina={padron.cambiarPagina}
                onCambiarTamanio={padron.cambiarTamanio}
              />
            )}
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

  return <span className={`badge ${clase}`}>{estado}</span>
}
