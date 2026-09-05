/**
 * Cliente de organización (etapa 4B): tenants (plataforma-only), empresas y puntos de venta
 * (plataforma ve/edita cualquiera, un admin de tenant ve/edita solo los propios — lo
 * garantiza `OrganizacionEndpoints`/`ServicioDeOrganizacion` del lado del servidor, acá no
 * hay lógica de alcance). Alta y baja siguen siendo plataforma-only vía aprovisionamiento
 * (`NuevoTenant.tsx`) — este cliente solo lista/edita datos descriptivos y
 * suspende/reactiva un tenant.
 */
import { api } from './cliente'
import type {
  EmpresaEdicion,
  EmpresaListado,
  EstadoTenant,
  PuntoVentaEdicion,
  PuntoVentaListado,
  TenantEdicion,
  TenantListado,
} from './tipos'

export const clienteDeOrganizacion = {
  listarTenants: () => api.get<TenantListado[]>('/plataforma/tenants'),
  editarTenant: (id: number, datos: TenantEdicion) =>
    api.put<TenantListado>(`/plataforma/tenants/${id}`, datos),
  suspenderTenant: (id: number) => api.post<TenantListado>(`/plataforma/tenants/${id}/suspender`),
  reactivarTenant: (id: number) => api.post<TenantListado>(`/plataforma/tenants/${id}/reactivar`),

  listarEmpresas: () => api.get<EmpresaListado[]>('/empresas'),
  editarEmpresa: (id: number, datos: EmpresaEdicion) => api.put<EmpresaListado>(`/empresas/${id}`, datos),

  listarPuntosVenta: () => api.get<PuntoVentaListado[]>('/puntos-venta'),
  editarPuntoVenta: (id: number, datos: PuntoVentaEdicion) =>
    api.put<PuntoVentaListado>(`/puntos-venta/${id}`, datos),
}

// --- Filtros por dueño y etiquetas (stage-20, slice 2 · design D14, D15) ---
//
// Helpers PUROS: sin React y sin fetch. Las opciones de cada filtro se derivan de las filas YA
// CARGADAS, nunca de una segunda consulta (D15): `GET /api/plataforma/tenants` es
// `Politicas.SoloPlataforma` mientras que `Empresas.tsx` y `PuntosVenta.tsx` los abre también un
// admin de tenant bajo `GestionDeOrganizacion` — pedir la lista de tenants daría 403 justo para
// los usuarios para los que se hizo la pantalla. Derivar de las filas además hace imposible por
// construcción que un filtro delate un tenant fuera del alcance del actor (spec S5).

/** Valor de "sin filtro" de los `<select>`: el string vacío, que es lo que rinde un `<option>`
 * sin `value`. `filtrarPorTenant`/`filtrarPorEmpresa` lo tratan como identidad. */
export const SIN_FILTRO = ''

/** Valor del `<option>` de personal de plataforma. NO es un id: es deliberadamente un token que
 * ningún `String(idTenant)` puede producir, así una cuenta sin tenant y el tenant número 7 nunca
 * comparten clave de filtro. */
export const VALOR_SIN_TENANT = 'sin-tenant'

/** Copia de la web para una cuenta sin tenant (design D14). El servidor NUNCA manda este literal:
 * `nombre` es texto libre, así que un tenant llamado "Plataforma" sería indistinguible del
 * personal de plataforma si el servidor lo fabricara. */
export const ETIQUETA_PLATAFORMA = 'Plataforma'

/** Etiqueta del `<option>` de personal de plataforma. Lleva el sufijo a propósito: un tenant
 * puede llamarse literalmente "Plataforma" y las dos opciones quedarían visualmente idénticas
 * (tendrían claves distintas igual, pero el operador no podría elegir a ciegas). */
export const ETIQUETA_OPCION_PLATAFORMA = 'Plataforma (sin tenant)'

/** Marca de dueño sin nombre — el huérfano de design D13, que NO es personal de plataforma. */
export const ETIQUETA_SIN_DUENIO = '—'

export type OpcionDeFiltro = { valor: string; etiqueta: string }

/** Lo mínimo que una fila necesita para entrar a los filtros/etiquetas de tenant. `idTenant` es
 * `number` en organización y `number | null` en usuarios; el tipo acepta los dos. */
export type FilaConTenant = { idTenant: number | null; nombreTenant: string | null }

export type FilaConEmpresa = FilaConTenant & { idEmpresa: number; razonSocialEmpresa: string | null }

/**
 * Etiqueta de la columna "Tenant". El discriminador es `idTenant`, NUNCA el nombre
 * (Reconciliación 9): un `nombreTenant` nulo con `idTenant` nulo es personal de plataforma, y un
 * `nombreTenant` nulo con `idTenant` presente es un huérfano — un tenant dado de baja lógicamente
 * que todavía tiene hijos vivos. Son dos cosas distintas y se rinden distinto.
 */
export function etiquetaDeTenant(fila: FilaConTenant): string {
  if (fila.idTenant === null) return ETIQUETA_PLATAFORMA

  return fila.nombreTenant ?? ETIQUETA_SIN_DUENIO
}

function etiquetaDeOpcionDeTenant(fila: FilaConTenant): string {
  if (fila.idTenant === null) return ETIQUETA_OPCION_PLATAFORMA

  // El id desempata: dos tenants huérfanos distintos no pueden compartir etiqueta.
  return fila.nombreTenant ?? `${ETIQUETA_SIN_DUENIO} (tenant ${fila.idTenant})`
}

function claveDeTenant(idTenant: number | null): string {
  return idTenant === null ? VALOR_SIN_TENANT : String(idTenant)
}

function ordenarPorEtiqueta(opciones: OpcionDeFiltro[]): OpcionDeFiltro[] {
  return opciones.sort((a, b) => a.etiqueta.localeCompare(b.etiqueta, 'es'))
}

/**
 * Desempata etiquetas repetidas con el id de cada opción. `nombre`/`razon_social` son texto libre:
 * dos dueños DISTINTOS pueden compartirlo y las opciones quedarían byte a byte idénticas, así que
 * el operador elegiría a ciegas. Solo se toca a las que colisionan — es el mismo desempate que ya
 * llevan los huérfanos, aplicado ahora también a los homónimos con nombre.
 */
function desempatarHomonimos(opciones: OpcionDeFiltro[], sustantivo: string): OpcionDeFiltro[] {
  const repeticiones = new Map<string, number>()
  for (const opcion of opciones) {
    repeticiones.set(opcion.etiqueta, (repeticiones.get(opcion.etiqueta) ?? 0) + 1)
  }

  return opciones.map((opcion) =>
    (repeticiones.get(opcion.etiqueta) ?? 0) > 1
      ? { ...opcion, etiqueta: `${opcion.etiqueta} (${sustantivo} ${opcion.valor})` }
      : opcion,
  )
}

/**
 * Reconcilia una selección de filtro contra las opciones vigentes: si la opción elegida ya no
 * existe, la selección vuelve a "sin filtro". Se usa para DERIVAR lo que se pinta y para escribir
 * de vuelta el estado después de cada carga — derivarlo solo al pintar dejaba viva una selección
 * inválida, que se reaplicaba sola en cuanto la opción reaparecía.
 */
export function seleccionVigente(opciones: readonly OpcionDeFiltro[], seleccion: string): string {
  return seleccion === SIN_FILTRO || opciones.some((o) => o.valor === seleccion) ? seleccion : SIN_FILTRO
}

/**
 * Opciones del filtro por tenant, deduplicadas por `idTenant` y ordenadas por etiqueta. La opción
 * de plataforma va primero (no es un tenant, es la ausencia de uno) y solo aparece si alguna fila
 * cargada la tiene: un dataset de un solo tenant ofrece exactamente una opción (S5).
 */
export function opcionesDeTenant(filas: readonly FilaConTenant[]): OpcionDeFiltro[] {
  const porClave = new Map<string, OpcionDeFiltro>()

  for (const fila of filas) {
    const valor = claveDeTenant(fila.idTenant)
    if (!porClave.has(valor)) porClave.set(valor, { valor, etiqueta: etiquetaDeOpcionDeTenant(fila) })
  }

  const plataforma = porClave.get(VALOR_SIN_TENANT)
  const tenants = ordenarPorEtiqueta(
    desempatarHomonimos([...porClave.values()].filter((o) => o.valor !== VALOR_SIN_TENANT), 'tenant'),
  )

  return plataforma ? [plataforma, ...tenants] : tenants
}

/**
 * Opciones del selector de tenant del ALTA de usuarios: el universo COMPLETO que devuelve
 * `listarTenants()`, no las filas cargadas. Un tenant que no está Activo se ofrece IGUAL — el
 * servidor es la autoridad y `ServicioDeUsuarios.CrearAsync` no mira el estado del tenant destino,
 * así que el operador puede pre-crear dentro de uno suspendido — pero la etiqueta lo marca: un
 * usuario creado ahí no va a poder iniciar sesión, y sin la marca eso sería invisible.
 */
export function opcionesDeTenantAsignable(tenants: readonly TenantListado[]): OpcionDeFiltro[] {
  const opciones = tenants.map((t) => ({
    valor: String(t.id),
    etiqueta: etiquetaDeTenantAsignable(t.nombre, t.estado),
  }))

  return ordenarPorEtiqueta(desempatarHomonimos(opciones, 'tenant'))
}

function etiquetaDeTenantAsignable(nombre: string, estado: EstadoTenant): string {
  return estado === 'Activo' ? nombre : `${nombre} (${estado.toLowerCase()})`
}

/**
 * Opciones del filtro por empresa, deduplicadas por `idEmpresa` y ordenadas por razón social.
 * Cuando hay un tenant seleccionado las opciones se ANGOSTAN a ese tenant (design D15): el
 * operador no puede elegir una empresa que el filtro de arriba ya sacó de la vista.
 */
export function opcionesDeEmpresa(
  filas: readonly FilaConEmpresa[],
  tenantSeleccionado: string = SIN_FILTRO,
): OpcionDeFiltro[] {
  const porClave = new Map<string, OpcionDeFiltro>()

  for (const fila of filtrarPorTenant(filas, tenantSeleccionado)) {
    const valor = String(fila.idEmpresa)
    if (porClave.has(valor)) continue

    porClave.set(valor, {
      valor,
      etiqueta: fila.razonSocialEmpresa ?? `${ETIQUETA_SIN_DUENIO} (empresa ${fila.idEmpresa})`,
    })
  }

  return ordenarPorEtiqueta(desempatarHomonimos([...porClave.values()], 'empresa'))
}

/** Filtra sobre la lista YA CARGADA. `SIN_FILTRO` es la identidad: devuelve la lista entera. */
export function filtrarPorTenant<T extends FilaConTenant>(filas: readonly T[], seleccion: string): T[] {
  if (seleccion === SIN_FILTRO) return [...filas]
  if (seleccion === VALOR_SIN_TENANT) return filas.filter((f) => f.idTenant === null)

  const id = Number(seleccion)

  return filas.filter((f) => f.idTenant === id)
}

/** Filtra sobre la lista YA CARGADA. `SIN_FILTRO` es la identidad: devuelve la lista entera. */
export function filtrarPorEmpresa<T extends { idEmpresa: number }>(
  filas: readonly T[],
  seleccion: string,
): T[] {
  if (seleccion === SIN_FILTRO) return [...filas]

  const id = Number(seleccion)

  return filas.filter((f) => f.idEmpresa === id)
}
