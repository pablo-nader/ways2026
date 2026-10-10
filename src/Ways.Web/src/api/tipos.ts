export const ROL = {
  Root: 1,
  Admin: 2,
  Supervisor: 3,
  Vendedor: 4,
} as const

export type EstadoUsuario = 'Activo' | 'Inactivo' | 'Bloqueado'

export const ESTADOS_USUARIO: EstadoUsuario[] = ['Activo', 'Inactivo', 'Bloqueado']

export type UsuarioAutenticado = {
  id: number
  usuario: string
  mail: string
  rolId: number
  rol: string
  ultimaConexion: string | null
  /** null para staff de plataforma (root); el tenant de la cuenta en cualquier otro caso. */
  idTenant: number | null
}

/**
 * Espejo de `Ways.Application.Usuarios.UsuarioListado`. `nombreTenant` llega en `null` en DOS
 * casos distintos y la pantalla los tiene que distinguir (design D13/D14, Reconciliación 9):
 * cuenta de plataforma (`idTenant === null`) y huérfano (tenant dueño dado de baja lógicamente,
 * `idTenant` no nulo). NO es un "si y solo si": un nombre nulo no implica plataforma. El
 * discriminador es `idTenant`, y la etiqueta "Plataforma" la pone la web —
 * `etiquetaDeTenant` en `organizacion.ts` — nunca el servidor.
 */
export type UsuarioListado = {
  id: number
  usuario: string
  mail: string
  rolId: number
  rol: string
  estado: EstadoUsuario
  ultimaConexion: string | null
  createdAt: string
  idTenant: number | null
  nombreTenant: string | null
}

export type RolListado = {
  id: number
  nombre: string
  descripcion: string | null
}

export type PaginaDe<T> = {
  items: T[]
  total: number
  pagina: number
  tamanio: number
}

/**
 * Espejo de `Ways.Application.Usuarios.CrearUsuario`. `idTenant` solo lo aprovecha un actor de
 * plataforma para elegir a qué tenant pertenece la cuenta creada; para un actor de tenant el
 * servidor lo IGNORA y usa el suyo (`ServicioDeUsuarios.CrearAsync`), así que la web manda `null`.
 * El rol root exige `null` y cualquier otro rol exige un valor
 * (`PoliticaDeRoles.ValidarConsistenciaDeRolYAlcance` — 400 `tenant_requerido`).
 */
export type CrearUsuario = {
  usuario: string
  mail: string
  rolId: number
  password: string
  estado: EstadoUsuario
  idTenant: number | null
}

/** Espejo de `Ways.Application.Usuarios.ActualizarUsuario`, que NO acepta `idTenant`: el tenant de
 * una cuenta no se reasigna por edición, el servidor valida el rol contra `usuario.IdTenant`. */
export type ActualizarUsuario = {
  usuario: string
  mail: string
  rolId: number
  estado: EstadoUsuario
}

/** Root y admin son los únicos que ven el ABM de usuarios. */
export function puedeGestionarUsuarios(rolId: number) {
  return rolId === ROL.Root || rolId === ROL.Admin
}

/**
 * Admin administra catálogo y parámetros del tenant; root queda afuera a propósito
 * (doc 09/design.md: "root administra tenants, no opera ninguno" — `Politicas.GestionDeCatalogo`
 * en la API es admin-only, en espejo con `puedeAprovisionarTenants`).
 */
export function puedeGestionarCatalogos(rolId: number) {
  return rolId === ROL.Admin
}

/** Solo root aprovisiona tenants (`Politicas.SoloPlataforma`). */
export function puedeAprovisionarTenants(rolId: number) {
  return rolId === ROL.Root
}

/** Espejo de `Politicas.OperacionDePos` (stage-5-pos-ventas, design decisión 6): vendedor,
 * supervisor o admin pueden operar el POS — root queda afuera, mismo criterio que
 * `puedeGestionarCatalogos` ("root administra tenants, no opera ninguno"). */
export function puedeOperarPos(rolId: number) {
  return rolId === ROL.Vendedor || rolId === ROL.Supervisor || rolId === ROL.Admin
}

/** Espejo de `Politicas.SupervisionDeCuentaCorriente` (stage-7-cuenta-corriente, Slice 6):
 * supervisor o admin pueden hacer un ajuste manual o correr la reliquidación — vendedor queda
 * afuera, la única desviación deliberada de paridad legacy de la etapa. Puramente cosmético: el
 * servidor vuelve a exigir la misma policy en cada request. */
export function puedeSupervisarCuentaCorriente(rolId: number) {
  return rolId === ROL.Supervisor || rolId === ROL.Admin
}

/** Espejo de la guarda de rol de `ServicioDeTurnos.ValidarOverrideDeRendicion`: supervisor o admin
 * pueden forzar un cierre de turno sin la rendición de cola de un dispositivo. Solo da forma a la
 * copia en pantalla — el servidor vuelve a exigir el rol y responde `403 prohibido` igual, así que
 * el control nunca se esconde en base a esto (el rol del cliente no es fuente de verdad). */
export function puedeForzarCierreSinRendicion(rolId: number) {
  return rolId === ROL.Supervisor || rolId === ROL.Admin
}

/** Espejo de `Politicas.SupervisionDeCuentaDeProveedor` (stage-15-cc-proveedores-ledger, Slice 5):
 * supervisor o admin pueden registrar un ajuste manual del ledger de proveedores — vendedor queda
 * afuera, mismo criterio que `puedeSupervisarCuentaCorriente` pero una policy DISTINTA y propia
 * (design decisión 8/12: nunca compuesta con `OperacionDePos`). Puramente cosmético: el servidor
 * vuelve a exigir la misma policy en cada `POST /ajustes`. */
export function puedeSupervisarCuentaDeProveedor(rolId: number) {
  return rolId === ROL.Supervisor || rolId === ROL.Admin
}

/** Espejo de `Politicas.LecturaDeReportes` (stage-10-agregacion-dashboard, Slice 1): supervisor o
 * admin ven `Tablero` — vendedor y root quedan afuera, mismo criterio que
 * `puedeSupervisarCuentaCorriente`. Puramente cosmético: el servidor vuelve a exigir la misma
 * policy en cada endpoint de `/api/reportes`. */
export function puedeVerReportes(rolId: number) {
  return rolId === ROL.Supervisor || rolId === ROL.Admin
}

/** Espejo de `Politicas.LecturaDeRentabilidad` (stage-10-agregacion-dashboard, Slice 9): solo
 * admin ve el panel de rentabilidad — ni siquiera supervisor entra acá, el costo es el número más
 * sensible del sistema y esta etapa no amplía quién lo ve (spec rentabilidad-y-comisiones:
 * LecturaDeRentabilidad Policy Admits Admin Only). Puramente cosmético: el servidor vuelve a
 * exigir la misma policy en `/api/reportes/rentabilidad`. */
export function puedeVerRentabilidad(rolId: number) {
  return rolId === ROL.Admin
}

/** Espejo de `Politicas.LecturaDeRentabilidad` aplicada a `GET /api/reportes/comisiones`
 * (stage-10-agregacion-dashboard, Slice 10, PROVISIONAL): mismo policy admin-only que
 * `puedeVerRentabilidad` — se apila sobre `LecturaDeReportes` igual que rentabilidad (design
 * decisión 7) — nombrada aparte porque es un concern de UI distinto (tarjeta de comisiones, no el
 * panel de margen). Puramente cosmético: el servidor vuelve a exigir la misma policy en
 * `/api/reportes/comisiones`. */
export function puedeVerComisiones(rolId: number) {
  return rolId === ROL.Admin
}

/** Espejo de `Politicas.LecturaDeAuditoria` (stage-14-auditoria-trazabilidad, Slice 5): SOLO admin
 * ve el log de auditoría — ni supervisor, ni vendedor, ni root (spec auditoria-de-operaciones:
 * "GET /api/auditoria Is Filtered, Paginated, And Admin-Only" — la policy NO se apila sobre
 * `LecturaDeReportes`, es su propio gate, mismo criterio admin-only que `puedeVerRentabilidad`).
 * Puramente cosmético: el servidor vuelve a exigir la misma policy en `/api/auditoria` y
 * `/api/auditoria/export`. */
export function puedeVerAuditoria(rolId: number) {
  return rolId === ROL.Admin
}

// --- Catálogos de tenant (ADR-11) ---

export type ComportamientoMedioPago = 'Efectivo' | 'Electronico' | 'CuentaCorriente'

export const COMPORTAMIENTOS_MEDIO_PAGO: { valor: ComportamientoMedioPago; etiqueta: string }[] = [
  { valor: 'Efectivo', etiqueta: 'Efectivo (arqueo físico, admite vuelto)' },
  { valor: 'Electronico', etiqueta: 'Electrónico (pide referencia)' },
  { valor: 'CuentaCorriente', etiqueta: 'Cuenta corriente (mueve saldo del cliente)' },
]

/** Campos comunes de listado que comparten los 5 catálogos de tenant. */
export type CatalogoListado = {
  id: number
  nombre: string
  activo: boolean
  idEmpresa: number | null
}

export type AreaListado = CatalogoListado & { orden: number }
export type AreaAlta = { nombre: string; idEmpresa: number | null; orden: number; activo: boolean }

export type MarcaListado = CatalogoListado
export type MarcaAlta = { nombre: string; idEmpresa: number | null; activo: boolean }

export type GrupoListado = CatalogoListado & { margen: number | null }
export type GrupoAlta = { nombre: string; idEmpresa: number | null; margen: number | null; activo: boolean }

export type MedioPagoListado = CatalogoListado & {
  orden: number
  comportamiento: ComportamientoMedioPago
  admiteVuelto: boolean
  requiereReferencia: boolean
  recargoPorcentaje: number | null
}
export type MedioPagoAlta = {
  nombre: string
  idEmpresa: number | null
  orden: number
  comportamiento: ComportamientoMedioPago
  admiteVuelto: boolean
  requiereReferencia: boolean
  recargoPorcentaje: number | null
  activo: boolean
}

export type CategoriaListado = CatalogoListado & { orden: number; idCategoriaPadre: number | null }
export type CategoriaAlta = {
  nombre: string
  idEmpresa: number | null
  orden: number
  idCategoriaPadre: number | null
  activo: boolean
}

// --- Catálogos fiscales (globales, solo lectura en esta etapa — ADR-11, gate #4) ---

export type ClaseComprobante = 'Venta' | 'Compra'

export type CondicionFiscalListado = {
  id: number
  codigo: string
  nombre: string
  codigoAfip: number | null
  activo: boolean
}

export type AlicuotaIvaListado = {
  id: number
  nombre: string
  porcentaje: number
  codigoAfip: number | null
  activo: boolean
}

export type TipoComprobanteListado = {
  id: number
  clase: ClaseComprobante
  codigo: string
  nombre: string
  letra: string | null
  signo: number
  discriminaIva: boolean
  esFiscal: boolean
  afectaStock: boolean
  codigoAfip: number | null
  activo: boolean
  /** Entra al libro IVA: en una compra, además, el tipo fija si discrimina IVA (factura). Un tipo
   * que no lo registra (remito, comprobante no fiscal) deja elegirlo al cargar el comprobante. */
  registraLibroIva: boolean
}

// --- Parámetros operativos (ADR-13) ---

export type ParametroListado = { id: number; clave: string; valor: string; idPuntoVenta: number | null }
export type ParametroAlta = { clave: string; valor: string; idPuntoVenta: number | null }
export type ParametroResuelto = { clave: string; valor: string }

/** Espejo de `ParametroConocido` (Ways.Domain.Catalogos): clave, tipo declarado y default
 * documentado — el editor solo acepta estas claves, igual que el backend. `zona_horaria` es
 * el primer tipo `'texto'`: el backend lo guarda como string JSON-quoteado (stage-10).
 * `'booleano'` (stage-12-lotes-vencimientos, Slice 15) es el primer tipo `bool` del registro —
 * `lotes_habilitado`, viaja como `true`/`false` JSON crudo, sin comillas (mismo criterio de
 * `JsonSerializer.Deserialize<bool>` que el resto de los lectores tipados del backend). */
export const PARAMETROS_CONOCIDOS: {
  clave: string
  etiqueta: string
  tipo: 'entero' | 'decimal' | 'texto' | 'booleano'
  porDefecto: string
}[] = [
  { clave: 'tolerancia_pago', etiqueta: 'Tolerancia de pago ($)', tipo: 'decimal', porDefecto: '10' },
  {
    clave: 'importe_adicional_recarga',
    etiqueta: 'Adicional por operación de recarga ($)',
    tipo: 'decimal',
    porDefecto: '5',
  },
  { clave: 'slots_tickets_espera', etiqueta: 'Tickets en espera (cantidad)', tipo: 'entero', porDefecto: '10' },
  {
    clave: 'zona_horaria',
    etiqueta: 'Zona horaria',
    tipo: 'texto',
    porDefecto: 'America/Argentina/Buenos_Aires',
  },
  { clave: 'comision_porcentaje', etiqueta: 'Comisión (%)', tipo: 'decimal', porDefecto: '0' },
  // stage-12-lotes-vencimientos (Slice 15, espejo de `ParametroConocido.LotesHabilitado`/
  // `.DiasAlertaVencimiento`): interruptor del módulo de lotes a nivel empresa + horizonte de
  // alerta del reporte de vencimientos.
  { clave: 'lotes_habilitado', etiqueta: 'Control de lotes habilitado', tipo: 'booleano', porDefecto: 'false' },
  {
    clave: 'dias_alerta_vencimiento',
    etiqueta: 'Días de alerta de vencimiento',
    tipo: 'entero',
    porDefecto: '30',
  },
]

/** Zonas IANA ofrecidas en el editor de `zona_horaria` (design decisión 12): un `<select>`
 * cerrado en vez de texto libre, para que un identificador inválido no pueda ni tipearse. */
export const ZONAS_HORARIAS_OFRECIDAS: { id: string; etiqueta: string }[] = [
  { id: 'America/Argentina/Buenos_Aires', etiqueta: 'Buenos Aires' },
  { id: 'America/Argentina/Catamarca', etiqueta: 'Catamarca' },
  { id: 'America/Argentina/Cordoba', etiqueta: 'Córdoba' },
  { id: 'America/Argentina/Jujuy', etiqueta: 'Jujuy' },
  { id: 'America/Argentina/La_Rioja', etiqueta: 'La Rioja' },
  { id: 'America/Argentina/Mendoza', etiqueta: 'Mendoza' },
  { id: 'America/Argentina/Rio_Gallegos', etiqueta: 'Río Gallegos' },
  { id: 'America/Argentina/Salta', etiqueta: 'Salta' },
  { id: 'America/Argentina/San_Juan', etiqueta: 'San Juan' },
  { id: 'America/Argentina/San_Luis', etiqueta: 'San Luis' },
  { id: 'America/Argentina/Tucuman', etiqueta: 'Tucumán' },
  { id: 'America/Argentina/Ushuaia', etiqueta: 'Ushuaia' },
  { id: 'UTC', etiqueta: 'UTC' },
]

// --- Organización: lectura/edición (ServicioDeOrganizacion) ---
// Alta y baja de tenants/empresas/puntos_venta siguen siendo plataforma-only vía
// aprovisionamiento (ver NuevoTenant.tsx); estos tipos son solo listado/detalle/edición de
// datos descriptivos + suspensión de tenants.

export type EstadoTenant = 'Activo' | 'Suspendido' | 'Baja'

/** Los tres contadores son hijos VIVOS del tenant (el servidor los proyecta con el filtro de
 * baja lógica adentro) y `cantidadUsuarios` no cuenta al personal de plataforma. */
export type TenantListado = {
  id: number
  nombre: string
  estado: EstadoTenant
  createdAt: string
  cantidadEmpresas: number
  cantidadPuntosVenta: number
  cantidadUsuarios: number
}

export type TenantEdicion = { nombre: string }

/** `nombreTenant` es nullable a propósito (design D13): si el tenant dueño quedó dado de baja, la
 * empresa se sigue listando con el nombre en `null` y se muestra como anomalía en vez de
 * desaparecer. `idTenant` deja de renderizarse y pasa a ser la clave del filtro por tenant. */
export type EmpresaListado = {
  id: number
  idTenant: number
  razonSocial: string
  nombreFantasia: string | null
  cuit: string | null
  nombreTenant: string | null
  /** Porcentaje de percepción de IIBB que los proveedores le aplican a la empresa (0 a 100).
   * `null` = no informado: la compra no pre-carga la percepción. */
  alicuotaPercepcionIibb: number | null
  alicuotaPercepcionIva: number | null
}

export type EmpresaEdicion = {
  razonSocial: string
  nombreFantasia: string | null
  cuit: string | null
  alicuotaPercepcionIibb: number | null
  alicuotaPercepcionIva: number | null
}

/** stage-desktop-pos (DB CHANGE GATE aprobado): invariante "una PC-caja = un punto de venta" —
 * `Escritorio` exige a lo sumo un dispositivo activo vinculado y que solo ese dispositivo pueda
 * vender contra él; `Web` exige lo contrario (sesión sin dispositivo). Espejo de
 * `Ways.Domain.Organizacion.ModoPuntoVenta`. */
export type ModoPuntoVenta = 'Escritorio' | 'Web'

export const MODOS_PUNTO_VENTA: ModoPuntoVenta[] = ['Escritorio', 'Web']

/** Los dos nombres de dueño son nullable por el mismo criterio que `EmpresaListado.nombreTenant`
 * (design D13); `idTenant` e `idEmpresa` dejan de renderizarse y quedan como claves de los dos
 * filtros. */
export type PuntoVentaListado = {
  id: number
  idTenant: number
  idEmpresa: number
  nombre: string
  domicilio: string | null
  horario: string | null
  whatsapp: string | null
  instagram: string | null
  facebook: string | null
  web: string | null
  nombreTenant: string | null
  razonSocialEmpresa: string | null
  modo: ModoPuntoVenta
}

/** `idEmpresa` no es editable acá: es estructural, no descriptivo (misma razón que en el
 * backend, `Ways.Application.Organizacion.PuntoVentaEdicion`). `modo` tampoco: tiene su propio
 * flip dedicado (`POST /api/puntos-venta/{id}/modo`, `PuntoVentaModoEdicion`), con su propia
 * precondición (sin dispositivo activo) y su propio rastro de auditoría. */
export type PuntoVentaEdicion = {
  nombre: string
  domicilio: string | null
  horario: string | null
  whatsapp: string | null
  instagram: string | null
  facebook: string | null
  web: string | null
}

/** Cuerpo de `POST /api/puntos-venta` — espejo de `Ways.Application.Organizacion.PuntoVentaAlta`.
 * `modo` y `idEmpresa` son obligatorios y no tienen valor por defecto: el servidor responde 400 si
 * faltan (docs/10 §9.1: el modo nunca se infiere). Los seis datos descriptivos son los mismos de
 * `PuntoVentaEdicion`. */
export type PuntoVentaAlta = {
  idEmpresa: number
  nombre: string
  modo: ModoPuntoVenta
  domicilio: string | null
  horario: string | null
  whatsapp: string | null
  instagram: string | null
  facebook: string | null
  web: string | null
}

/** Cuerpo de `POST /api/puntos-venta/{id}/modo` — espejo de
 * `Ways.Application.Organizacion.PuntoVentaModoEdicion`. */
export type PuntoVentaModoEdicion = { modo: ModoPuntoVenta }

// --- Clientes (stage-2-clientes-proveedores, ADR-8) ---
// Entidad dedicada, no la máquina genérica de catálogos (design decision 1): `numero` lo
// asigna el servidor (contador atómico por tenant), nunca es un campo editable acá.

export type TipoDocumento = 'Dni' | 'Cuit' | 'Cuil' | 'Pasaporte' | 'Otro'

export const TIPOS_DOCUMENTO: TipoDocumento[] = ['Dni', 'Cuit', 'Cuil', 'Pasaporte', 'Otro']

export type ClienteListado = {
  id: number
  numero: number
  nombre: string
  apellido: string | null
  razonSocial: string | null
  tipoDocumento: TipoDocumento | null
  numeroDocumento: string | null
  idCondicionFiscal: number
  nacimiento: string | null
  domicilio: string | null
  telefono: string | null
  celular: string | null
  email: string | null
  observaciones: string | null
  idListaPrecio: number
  limiteCredito: number
  creditoIlimitado: boolean
  saldo: number
  activo: boolean
  idEmpresa: number | null
  esConsumidorFinal: boolean
}

/** `idListaPrecio`/`idCondicionFiscal` son requeridos (spec: "id_lista_precio and
 * id_condicion_fiscal are required") — sin default automático cuando se omiten. */
export type AltaCliente = {
  nombre: string
  apellido: string | null
  razonSocial: string | null
  tipoDocumento: TipoDocumento | null
  numeroDocumento: string | null
  idCondicionFiscal: number
  nacimiento: string | null
  domicilio: string | null
  telefono: string | null
  celular: string | null
  email: string | null
  observaciones: string | null
  idListaPrecio: number
  limiteCredito: number
  creditoIlimitado: boolean
  idEmpresa: number | null
  activo: boolean
}

/** Sin `saldo`: no hay motor de cuenta corriente todavía (etapa 7) — no es editable acá. */
export type EdicionCliente = AltaCliente

/** Referencia mínima para el selector de lista de precios — no un ABM de `listas_precio`
 * (design decision 1, spec: listas_precio ABM Is Out of Scope This Stage). */
export type ListaPrecioAsignable = { id: number; nombre: string; esDefault: boolean }

// --- Listas de precio (stage-3-articulos-y-precios, Slice 4/6): ABM completo, ambos modos ---

export type ModoLista = 'Fija' | 'Derivada'

export type ListaPrecioListado = CatalogoListado & {
  esDefault: boolean
  modo: ModoLista
  idListaBase: number | null
  porcentaje: number | null
}

export type ListaPrecioAlta = {
  nombre: string
  idEmpresa: number | null
  esDefault: boolean
  modo: ModoLista
  idListaBase: number | null
  porcentaje: number | null
  activo: boolean
}

// --- Proveedores (stage-2-clientes-proveedores) ---
// Entidad dedicada, no la máquina genérica de catálogos (design decision 1): dedupe por `cuit`
// tenant-wide (partial index, NULL permitido y no comparado), no por nombre/empresa-par.

export type ProveedorListado = {
  id: number
  razonSocial: string
  nombreFantasia: string | null
  cuit: string | null
  idCondicionFiscal: number
  domicilio: string | null
  telefono: string | null
  email: string | null
  vendedor: string | null
  celularVendedor: string | null
  supervisor: string | null
  celularSupervisor: string | null
  margen: number | null
  observaciones: string | null
  activo: boolean
  idEmpresa: number | null
  /** El proveedor es agente de percepción: pre-carga la percepción en sus compras. */
  percibeIibb: boolean
  percibeIva: boolean
  /** Sus precios ya traen el IVA incluido: pre-carga el modo "precio final" de la compra. */
  preciosIncluyenIva: boolean
}

export type AltaProveedor = {
  razonSocial: string
  nombreFantasia: string | null
  cuit: string | null
  idCondicionFiscal: number
  domicilio: string | null
  telefono: string | null
  email: string | null
  vendedor: string | null
  celularVendedor: string | null
  supervisor: string | null
  celularSupervisor: string | null
  margen: number | null
  observaciones: string | null
  idEmpresa: number | null
  activo: boolean
  percibeIibb: boolean
  percibeIva: boolean
  preciosIncluyenIva: boolean
}

export type EdicionProveedor = AltaProveedor

/** Espejo de `Ways.Application.Proveedores.OpcionDeProveedor` (JD-A1, judgment-day): proyección
 * MÍNIMA para selectores que no requieren gestión de catálogo (`GET /api/proveedores/opciones`,
 * `Politicas.OperacionDePos`) — nunca margen, cuit, datos de contacto ni otro campo sensible del
 * `ProveedorListado` completo (que sigue exigiendo `GestionDeCatalogo`). */
export type OpcionDeProveedor = {
  id: number
  razonSocial: string
  nombreFantasia: string | null
}

// --- Artículos y precios (stage-3-articulos-y-precios) ---
// Entidad dedicada, no la máquina genérica de catálogos (design decision 1): 14+ campos,
// junction de disponibilidad, colección de códigos de barra — ninguno encaja en el shape
// genérico de `CatalogoListado`/`DescriptorDeCatalogo`.

export type UnidadVenta = 'Unidad' | 'Peso'

export const UNIDADES_VENTA: { valor: UnidadVenta; etiqueta: string }[] = [
  { valor: 'Unidad', etiqueta: 'Por unidad' },
  { valor: 'Peso', etiqueta: 'Por peso' },
]

/** `idsEmpresas` viene vacío en el listado paginado (el backend evita el N+1 de una query por
 * fila listada) — solo `obtener`/`crear`/`actualizar` lo completan con el subconjunto real
 * (ver el doc-comment de `ArticuloListado` en el backend). */
export type ArticuloListado = {
  id: number
  codigoInterno: string
  nombre: string
  descripcion: string | null
  idArea: number
  idCategoria: number | null
  idMarca: number | null
  idGrupo: number | null
  idProveedorHabitual: number | null
  idAlicuotaIva: number
  unidadVenta: UnidadVenta
  unidadesPorBulto: number | null
  esProducto: boolean
  costoLista: number | null
  descuentoProveedor: number | null
  costoNominal: number | null
  disponibleParaTodas: boolean
  idsEmpresas: number[]
  activo: boolean
  /** stage-12-lotes-vencimientos (Slices 14/15, espejo de `Articulo.ControlaLote`): acá viaja
   * solo el flag propio del artículo; el control EFECTIVO es este flag AND `lotes_habilitado`
   * de la empresa (`ReglaDeLotes.ControlEfectivo`, decisión 2 del proposal). El toggle de
   * edición vive en el editor de `Articulos.tsx`; el picker del POS no necesita este flag
   * porque `GET /api/stock/lotes` ya resuelve todo server-side. */
  controlaLote: boolean
  /** `true`: volver a agregarlo en el carrito del POS suma cantidad a su línea; `false`: cada
   * agregado es una línea nueva. */
  acumulaEnVenta: boolean
  /** Familia a la que pertenece el artículo (`null` = sin familia), tal como está guardada: el servidor
   * no la anula aunque la fila de la familia esté dada de baja (doc 10 §3, "Familias de artículos"). */
  idFamilia: number | null
  /** Código que el proveedor consultado imprime para este artículo: solo viene cuando el listado
   * se pidió con `idProveedor` y la búsqueda coincidió exactamente con ese código. */
  codigoProveedor?: string | null
}

/** `codigoInterno: null` deja que el servidor lo autogenere desde el contador atómico del
 * tenant (spec: codigo_interno Mandatory And Autogenerated). `idsEmpresas` solo se usa cuando
 * `disponibleParaTodas` es `false`. */
export type AltaArticulo = {
  codigoInterno: string | null
  nombre: string
  descripcion: string | null
  idArea: number
  idCategoria: number | null
  idMarca: number | null
  idGrupo: number | null
  idProveedorHabitual: number | null
  idAlicuotaIva: number
  unidadVenta: UnidadVenta
  unidadesPorBulto: number | null
  esProducto: boolean
  costoLista: number | null
  descuentoProveedor: number | null
  costoNominal: number | null
  disponibleParaTodas: boolean
  idsEmpresas: number[] | null
  activo: boolean
  controlaLote: boolean
  /** Ausente: en el alta el servidor asume `true`; en la edición conserva el valor guardado. */
  acumulaEnVenta?: boolean
  /** Código del proveedor habitual para este artículo; el servidor exige `idProveedorHabitual`. */
  codigoProveedor?: string | null
  /** El artículo nace como miembro de esa familia: los trece campos compartidos tienen que ser idénticos
   * a los de la familia (409 `familia_valores_distintos`) y el servidor le copia sus precios. */
  idFamilia?: number | null
}

/** Lo que el cliente elige cuando el artículo escrito pertenece a una familia (espejo de
 * `Ways.Application.Precios.AlcanceDeFamilia`, que viaja por nombre): `Familia` aplica el cambio a todos los
 * miembros vivos y `SoloEste` saca al artículo de la familia y lo escribe solo a él. */
export type AlcanceDeFamilia = 'Familia' | 'SoloEste'

/** Sin `codigoInterno`: no es editable por este ABM (valor asignado en el alta, mismo criterio
 * que `ClienteListado.numero`). Tampoco `idFamilia`: la pertenencia no se cambia editando; el `alcance`
 * decide qué se escribe cuando el artículo es miembro de una familia. */
export type EdicionArticulo = Omit<AltaArticulo, 'codigoInterno' | 'codigoProveedor' | 'idFamilia'> & {
  alcance?: AlcanceDeFamilia
}

export type CodigoBarraListado = { id: number; idArticulo: number; codigo: string; activo: boolean }
export type AltaCodigoBarra = { codigo: string }
export type AltaCodigoProveedor = { idProveedor: number; codigo: string }
export type CodigoProveedorListado = { idCodigoProveedor: number; idArticulo: number; idProveedor: number; codigo: string }

// --- Grilla de artículos con filtros por columna (feat: articulos-grilla-web) ---
// Espejo de `GET /api/articulos/grilla`: reemplaza la búsqueda libre + primera página fija de
// `listar` por filtro multi-columna con paginación real. `idProveedor`/`sinProveedor` son
// mutuamente excluyentes (mandar ambos es 400 `filtro_proveedor_ambiguo` del lado del servidor).

/** `activo: null` viaja como filtro OMITIDO ("todos") — a diferencia de `ArticuloListado.activo`,
 * que nunca es nullable porque ahí es el estado de una fila ya conocida, no un filtro. */
export type FiltrosDeGrillaDeArticulos = {
  codigo: string
  nombre: string
  precioDesde: number | null
  precioHasta: number | null
  idProveedor: number | null
  sinProveedor: boolean
  activo: boolean | null
  pagina: number
  tamanio: number
}

/** Fila de `GET /api/articulos/grilla` — `precio` es el vigente en la lista de precio DEFAULT del
 * tenant (nunca una lista a elección) y `proveedor` ya llega como la etiqueta de display (nombre
 * de fantasía si lo hay, si no razón social) — nunca se recalcula acá, a diferencia de
 * `etiquetaDeProveedor` que solo aplica a `ProveedorListado` completo (selects del formulario). */
export type FilaDeGrillaDeArticulos = {
  id: number
  codigoInterno: string
  nombre: string
  precio: number | null
  /** Costo real de reposición; `null` sin costo cargado (o para el vendedor). */
  costoNominal: number | null
  idProveedorHabitual: number | null
  proveedor: string | null
  activo: boolean
}

/** `nombreListaPrecio: null` cuando el tenant no tiene lista de precio default configurada — en
 * ese caso todo `precio` de `items` viaja `null` y el filtro de precio queda deshabilitado en la
 * UI (no tiene contra qué lista comparar). */
export type PaginaDeGrillaDeArticulos = {
  items: FilaDeGrillaDeArticulos[]
  total: number
  pagina: number
  tamanio: number
  nombreListaPrecio: string | null
}

/** `precioSugerido: null` cuando no hay costo base ni margen suficientes para calcular una
 * sugerencia — nunca se aplica sola (spec: Margin-Based Price Suggestion, "requires explicit
 * apply"). */
export type SugerenciaDePrecio = { precioSugerido: number | null }

// --- Precios (history engine, stage-3-articulos-y-precios) ---

/** `alcance` solo se manda cuando el artículo es miembro de una familia (doc 10 §3): sin él, el servidor
 * rechaza el cambio de un miembro con 409 `alcance_requerido`. */
export type AltaPrecio = {
  idListaPrecio: number
  precio: number
  confirmarReemplazo?: boolean
  alcance?: AlcanceDeFamilia
}
export type ProgramarPrecio = {
  idListaPrecio: number
  precio: number
  vigenteDesde: string
  confirmarReemplazo?: boolean
  alcance?: AlcanceDeFamilia
}
export type PrecioVigente = { idArticulo: number; idListaPrecio: number; precio: number | null; fecha: string }
export type HistorialDePrecio = { id: number; precio: number; vigenteDesde: string; vigenteHasta: string | null }

// --- Familias de artículos (doc 10 §3) ---
// Una familia agrupa artículos idénticos en sus trece campos compartidos y en el estado de precios de cada
// lista fija. No guarda valores: sus miembros son la fuente de verdad, y la referencia es el miembro vivo de
// menor id.

/** Fila de `GET /api/familias`: `cantidadArticulos` cuenta solo los miembros vivos. */
export type FamiliaListado = { id: number; nombre: string; activo: boolean; cantidadArticulos: number }

/** Miembro vivo de una familia. `idMarca` viaja `null` cuando el artículo no tiene marca o la suya está
 * dada de baja: el servidor nunca expone un id colgante. */
export type MiembroDeFamilia = { id: number; codigoInterno: string; nombre: string; idMarca: number | null; activo: boolean }

/** Los trece campos compartidos de un artículo, tal como los lee un cliente. Los cuatro ids de catálogo
 * (`idArea`, `idCategoria`, `idGrupo`, `idProveedorHabitual`) viajan `null` cuando apuntan a una fila dada
 * de baja: un área dada de baja que conserva artículos se lee como "sin asignar". */
export type ValoresCompartidosDeLaFamilia = {
  idArea: number | null
  idCategoria: number | null
  idGrupo: number | null
  idProveedorHabitual: number | null
  idAlicuotaIva: number
  unidadVenta: UnidadVenta
  unidadesPorBulto: number | null
  esProducto: boolean
  controlaLote: boolean
  acumulaEnVenta: boolean
  costoLista: number | null
  descuentoProveedor: number | null
  costoNominal: number | null
}

/** Un precio programado: su monto y la fecha desde la que rige. */
export type PrecioPendiente = { monto: number; vigenteDesde: string }

/** Estado de precios de un artículo en UNA lista fija a "ahora": el precio vigente y, si lo hay, el pendiente.
 * Sin ningún precio en esa lista, los dos vienen `null`. */
export type EstadoDePrecios = { vigente: number | null; pendiente: PrecioPendiente | null }

export type EstadoDePreciosDeLista = { idListaPrecio: number; estado: EstadoDePrecios }

/** Respuesta de `GET /api/familias/{id}`. `articulos` son los miembros vivos ascendentes por id: el primero es
 * la referencia, de quien salen `valores` y `precios`. Una familia sin ningún miembro vivo no tiene referencia:
 * `valores` es `null` y `precios` viene vacío. Con referencia, `precios` trae una entrada por cada lista fija
 * del tenant, también las que la referencia no tiene precios. */
export type FamiliaDetalle = {
  id: number
  nombre: string
  activo: boolean
  articulos: MiembroDeFamilia[]
  valores: ValoresCompartidosDeLaFamilia | null
  precios: EstadoDePreciosDeLista[]
}

/** Cuerpo de `PUT /api/familias/{id}`: lo único de una familia que se edita. Los dos campos son obligatorios: el
 * nombre se guarda sin espacios en los extremos y es único entre las familias vivas sin distinguir mayúsculas. */
export type EdicionFamilia = { nombre: string; activo: boolean }

/** Cuerpo de `POST /api/familias/previsualizacion`: qué pasaría si se agruparan `idsArticulos` con
 * `idArticuloReferencia` como modelo. Se admiten hasta 100 destinos además de la referencia. Si la referencia ya es
 * miembro de una familia, agrupar sería sumar los artículos a ESA familia; si no, crear una nueva. */
export type SolicitudDePrevisualizacion = { idArticuloReferencia: number; idsArticulos: number[] }

/** El cambio de UNA lista fija para un artículo que se alinea: su estado de precios actual y el de la referencia. */
export type CambioDePreciosDeLista = { idListaPrecio: number; actual: EstadoDePrecios; nuevo: EstadoDePrecios }

/** Lo que cambia en UN artículo al alinearlo con la referencia: `campos` son las columnas compartidas de
 * `articulos` (`id_area`, `costo_lista`, …) que difieren, en el orden de declaración de
 * `ValoresCompartidosDeFamilia`; `actual` y `nuevo`, los trece valores del artículo y de la referencia; `precios`,
 * las listas fijas en las que el estado de precios cambia. Un artículo ya alineado trae `campos` y `precios` vacíos. */
export type CambiosDeUnArticulo = {
  idArticulo: number
  campos: string[]
  actual: ValoresCompartidosDeLaFamilia
  nuevo: ValoresCompartidosDeLaFamilia
  precios: CambioDePreciosDeLista[]
}

/** Algo que impediría agrupar, con el código y el mensaje del error que daría el pedido real. Sin `idArticulo` es un
 * problema de la familia o del pedido entero. */
export type ProblemaDeAgrupacion = { codigo: string; mensaje: string; idArticulo: number | null; idListaPrecio: number | null }

/** Respuesta de `POST /api/familias/previsualizacion`: `articulos` trae, por cada destino que se puede alinear y
 * ascendente por id, lo que cambiaría; `problemas`, todo lo que impediría agrupar, en el orden en que el pedido real
 * lo rechaza. `idFamilia` es la familia a la que se sumarían (la de la referencia) o `null` si se crearía una nueva.
 * Es una foto sin locks. */
export type PrevisualizacionDeAgrupacion = {
  idArticuloReferencia: number
  idFamilia: number | null
  articulos: CambiosDeUnArticulo[]
  problemas: ProblemaDeAgrupacion[]
}

/** Cuerpo de `POST /api/familias`: crea la familia con la referencia, que es siempre miembro aunque no figure en
 * `idsArticulos`, y los artículos, que se alinean con ella. Ninguno puede pertenecer ya a una familia. */
export type AltaDeFamilia = { nombre: string; idArticuloReferencia: number; idsArticulos: number[] }

/** Cuerpo de `POST /api/familias/{id}/articulos`: suma artículos a la familia, alineados con su referencia. */
export type AgregadoDeArticulos = { idsArticulos: number[] }

/** Respuesta de `POST /api/familias` y de `POST /api/familias/{id}/articulos`: por cada artículo pedido distinto de
 * la referencia, lo que la agrupación cambió en él. */
export type ResultadoDeAgrupacion = {
  idFamilia: number
  nombre: string
  idArticuloReferencia: number
  articulos: CambiosDeUnArticulo[]
}

// --- Ofertas (stage-4-ofertas) ---
// Entidad dedicada (design decision 9): alcance (id_articulo/id_grupo/id_categoria) y
// beneficio (precio_unitario/porcentaje/importe_fijo) viajan como las tres columnas nullable
// crudas de cada grupo (design decision 1) — la exclusividad la valida el servidor
// (ReglaDeOfertas), acá solo se refleja/arma.

/** ISO-8601: 1 = lunes … 7 = domingo. */
export const DIAS_SEMANA: { valor: number; etiqueta: string }[] = [
  { valor: 1, etiqueta: 'Lunes' },
  { valor: 2, etiqueta: 'Martes' },
  { valor: 3, etiqueta: 'Miércoles' },
  { valor: 4, etiqueta: 'Jueves' },
  { valor: 5, etiqueta: 'Viernes' },
  { valor: 6, etiqueta: 'Sábado' },
  { valor: 7, etiqueta: 'Domingo' },
]

/** `idsListas` vacío ⇒ la oferta aplica a todas las listas del tenant (spec: Multi-Lista
 * Targeting via ofertas_listas). El listado no completa `idsListas` por fila (evita el N+1,
 * mismo criterio que `ArticuloListado.idsEmpresas`) — solo `obtener`/`crear`/`actualizar` lo
 * completan con el subconjunto real. */
export type OfertaListado = {
  id: number
  nombre: string
  idEmpresa: number | null
  idArticulo: number | null
  idGrupo: number | null
  idCategoria: number | null
  fechaDesde: string | null
  fechaHasta: string | null
  horaDesde: string | null
  horaHasta: string | null
  diasSemana: number[]
  cantidadMinima: number | null
  precioUnitario: number | null
  porcentaje: number | null
  importeFijo: number | null
  prioridad: number
  acumulable: boolean
  activo: boolean
  idsListas: number[]
}

/** Mismo shape para alta y edición: a diferencia de `ArticuloListado.codigoInterno`, acá no hay
 * ninguna columna inmutable en edición (backend: `Contratos.cs`, task 2.3). */
export type AltaOferta = {
  nombre: string
  idEmpresa: number | null
  idArticulo: number | null
  idGrupo: number | null
  idCategoria: number | null
  fechaDesde: string | null
  fechaHasta: string | null
  horaDesde: string | null
  horaHasta: string | null
  diasSemana: number[] | null
  cantidadMinima: number | null
  precioUnitario: number | null
  porcentaje: number | null
  importeFijo: number | null
  prioridad: number
  acumulable: boolean
  idsListas: number[] | null
  activo: boolean
}

export type EdicionOferta = AltaOferta

// --- Aprovisionamiento de tenants (ADR-16, plataforma) ---

export type SolicitudDeAprovisionamiento = {
  nombreTenant: string
  razonSocialEmpresa: string
  nombrePuntoVenta: string
  mailAdmin: string
  modo: ModoPuntoVenta
}

/** `passwordTemporal` se muestra UNA sola vez: la API no la vuelve a exponer. */
export type ResultadoAprovisionamiento = {
  idTenant: number
  idEmpresa: number
  idPuntoVenta: number
  idUsuarioAdmin: number
  passwordTemporal: string
}

// --- POS: escaneo y resolución de precios (stage-5-pos-ventas, Slice 6) ---
// Espejo de Ways.Application.Ventas.ArticuloEscaneado y Ways.Application.Ofertas.Contratos —
// identidad de escaneo y resolución de precios, nunca el checkout en sí (design decisión 7/10).

/** Respuesta de `GET /api/articulos/escaneo` — identidad y snapshot únicamente, nunca precio ni
 * oferta (design decisión 7: la resolución de precio queda en `POST /api/ofertas/resolver`).
 * `codigoBarra` es `null` cuando la entrada resolvió por `codigoInterno`. `unidadVenta` decide si
 * la línea admite fracciones (`cantidadPorUnidad.ts`); ausente (una instantánea offline guardada
 * antes de que el dato existiera) se trata como `Peso`, el comportamiento de siempre. */
export type ArticuloEscaneado = {
  idArticulo: number
  codigoInterno: string
  nombre: string
  codigoBarra: string | null
  cantidad: number
  acumulaEnVenta: boolean
  unidadVenta?: UnidadVenta
}

/** Línea de entrada de `POST /api/ofertas/resolver` (espejo de `LineaDeResolucion`, stage-4). */
export type LineaDeResolucion = { idArticulo: number; idEmpresa: number | null; idListaPrecio: number; cantidad: number }

export type OfertaAplicada = { idOferta: number; nombre: string; descuentoUnitario: number }

/** `precioOriginal`/`precioFinal` son `null` cuando no hay precio vigente para el par (artículo,
 * lista) — sin nada que descontar, `aplicadas` queda vacía (espejo de `ResultadoDeResolucion`). */
export type ResultadoDeResolucion = {
  idArticulo: number
  idListaPrecio: number
  precioOriginal: number | null
  precioFinal: number | null
  descuentoUnitario: number
  aplicadas: OfertaAplicada[]
}

// --- POS: instantánea offline (stage-pos-venta-offline-web) ---
// Espejo de `Ways.Application.Pos.Contratos` — catálogo con precio ya resuelto y congelado en cada
// lista, más los clientes, que el POS de escritorio persiste localmente para cotizar y vender sin
// red. `precioOriginal`/`precioFinal` NUNCA son `null` acá (a diferencia de
// `ResultadoDeResolucion`): una lista sin precio vigente para el artículo directamente no tiene
// entrada en `preciosPorLista`.

/**
 * Un tramo de precio por cantidad de un artículo de la instantánea — espejo de
 * `EscalonDeCantidad`. Existe porque una oferta con `cantidadMinima > 1` (un "3 o más") no podía
 * aplicarse offline: la instantánea congelaba UN solo precio, resuelto server-side en
 * `cantidad = 1`. Cada escalón lo calcula el motor de ofertas REAL del servidor — el dispositivo
 * nunca evalúa reglas, solo elige el tramo vigente (`elegirEscalon` en `instantaneaOffline.ts`).
 *
 * `precioOriginal` NO está acá a propósito: es constante entre cantidades, así que el escalón pisa
 * `precioFinal`/`descuentoUnitario`/`aplicadas` y deja el precio de lista del artículo intacto.
 */
export type EscalonDeCantidad = {
  cantidadDesde: number
  precioFinal: number
  descuentoUnitario: number
  aplicadas: OfertaAplicada[]
}

/** Precio de un artículo en UNA lista — espejo de `PrecioDeListaDeInstantanea`. */
export type PrecioDeListaDeInstantanea = {
  idListaPrecio: number
  precioOriginal: number
  precioFinal: number
  descuentoUnitario: number
  aplicadas: OfertaAplicada[]
  /** Tramos de precio por cantidad, ascendentes por `cantidadDesde` y todos con
   * `cantidadDesde > 1` — nunca incluyen la entrada de cantidad 1, que es el precio plano de
   * arriba. Ausente/`null` significa que el precio plano rige a toda cantidad. */
  escalones?: EscalonDeCantidad[] | null
}

/** Un artículo dentro de `InstantaneaDePos` — espejo de `ArticuloDeInstantanea`. Un artículo sin
 * precio en una lista no tiene entrada para esa lista en `preciosPorLista`. Sin `idArea`: ese
 * campo lo resuelve el servidor del artículo FRESCO al sincronizar. */
export type ArticuloDeInstantanea = {
  idArticulo: number
  codigoInterno: string
  nombre: string
  codigosBarra: string[]
  idAlicuotaIva: number
  porcentajeIva: number
  /** Ausente en una instantánea guardada antes de que el servidor la enviara: se trata como
   * `Peso`, sin bloquear ninguna venta. El servidor siempre la manda. */
  unidadVenta?: UnidadVenta
  preciosPorLista: PrecioDeListaDeInstantanea[]
  /** Ausente en una instantánea guardada antes de que existiera el campo: se toma como `true`. */
  acumulaEnVenta?: boolean
}

/** Un cliente visible para el punto de venta — espejo de `ClienteDeInstantanea`. `idListaPrecio`
 * es `null` cuando la lista del cliente está dada de baja. */
export type ClienteDeInstantanea = {
  idCliente: number
  numero: number
  nombre: string
  apellido: string | null
  razonSocial: string | null
  tipoDocumento: TipoDocumento | null
  numeroDocumento: string | null
  idCondicionFiscal: number
  idEmpresa: number | null
  idListaPrecio: number | null
  esConsumidorFinal: boolean
  saldo: number
  limiteCredito: number
  creditoIlimitado: boolean
}

/** Recorte de `MedioPagoListado` para el checkout offline — espejo de `MedioPagoDeInstantanea`. */
export type MedioPagoDeInstantanea = {
  idMedioPago: number
  nombre: string
  comportamiento: ComportamientoMedioPago
  admiteVuelto: boolean
  requiereReferencia: boolean
}

/** Respuesta de `GET /api/pos/instantanea?version=2` — espejo de `InstantaneaDePos`. `momento` es
 * el instante en que el servidor resolvió los precios; la vejez que ve el cajero es la de la última
 * verificación (`InstantaneaLocal.verificadaEn`), que un `304` renueva sin cambiar el contenido. */
export type InstantaneaDePos = {
  momento: string
  idPuntoVenta: number
  articulos: ArticuloDeInstantanea[]
  clientes: ClienteDeInstantanea[]
  mediosDePago: MedioPagoDeInstantanea[]
  toleranciaPago: number
}

// --- Caja: turnos, movimientos y resumen (stage-6-turnos-caja, Slice 6) ---
// Espejo de `Ways.Application.Caja.Contratos` — apertura, movimientos físicos fuera de la venta
// (retiro/refuerzo/apertura de cajón) y el resumen parcial (misma derivación que va a usar el
// cierre, Slice 7). El turno SIEMPRE se resuelve server-side (spec: turnos-de-caja / Turno Is
// Always Server-Resolved) — ningún contrato de acá acepta un `idTurnoCaja` como campo de entrada.

export type EstadoTurno = 'Abierto' | 'Cerrado'

/** Cuerpo de `POST /api/caja/turnos` — sin campo de empleado, `id_empleado_apertura` siempre
 * sale de la sesión del lado del servidor. */
export type SolicitudDeApertura = { idPuntoVenta: number; fondoInicial: number; observaciones: string | null }

/** Proyección de un turno — respuesta de apertura, `GET …/abierto` y `GET …/{id}`. */
export type TurnoResumen = {
  id: number
  idPuntoVenta: number
  idEmpleadoApertura: number
  idEmpleadoCierre: number | null
  fechaApertura: string
  fechaCierre: string | null
  fondoInicial: number
  estado: EstadoTurno
  observaciones: string | null
}

/** Fila de `GET /api/caja/turnos` — espejo de `TurnoListado`. */
export type TurnoListado = {
  id: number
  idPuntoVenta: number
  fechaApertura: string
  fechaCierre: string | null
  estado: EstadoTurno
}

/** Página de `GET /api/caja/turnos` — espejo de `PaginaDeTurnos`. */
export type PaginaDeTurnos = { items: TurnoListado[]; total: number; pagina: number; tamanio: number }

export type TipoMovimientoCaja = 'Retiro' | 'Refuerzo' | 'AperturaCajon'

export const TIPOS_MOVIMIENTO_CAJA: { valor: TipoMovimientoCaja; etiqueta: string }[] = [
  { valor: 'Retiro', etiqueta: 'Retiro' },
  { valor: 'Refuerzo', etiqueta: 'Refuerzo' },
  { valor: 'AperturaCajon', etiqueta: 'Apertura de cajón' },
]

/** Cuerpo de `POST /api/caja/turnos/{id}/movimientos` — el turno lo identifica la ruta, nunca
 * este cuerpo. */
export type SolicitudDeMovimiento = { tipo: TipoMovimientoCaja; importe: number; motivo: string | null }

export type MovimientoRegistrado = {
  id: number
  idTurnoCaja: number
  tipo: TipoMovimientoCaja
  importe: number
  motivo: string
  idEmpleado: number
  creadoEl: string
}

export type LineaDeResumen = { idMedioPago: number; importeEsperado: number }

/** Un ticket límite del turno (legacy doc 01 D6: "primer y último ticket") — `codigo` es el tipo
 * de comprobante (`TX`, `RC`, …): cada tipo numera su propia serie independiente, así que el
 * número solo no alcanza para identificar el ticket — espejo de `Ways.Application.Caja.TicketLimite`. */
export type TicketLimite = { numero: number; fecha: string; codigo: string }

/** Ingresos de un área dentro del turno (legacy D6, primer bloque: "por área") — espejo de
 * `IngresoPorArea`. */
export type IngresoPorArea = { idArea: number; nombreArea: string; total: number }

export type CategoriaGasto = 'Proveedor' | 'Sueldos' | 'Viaticos' | 'Impuestos' | 'Servicios' | 'Otros'

export const CATEGORIAS_GASTO: { valor: CategoriaGasto; etiqueta: string }[] = [
  { valor: 'Proveedor', etiqueta: 'Proveedores' },
  { valor: 'Sueldos', etiqueta: 'Sueldos' },
  { valor: 'Viaticos', etiqueta: 'Viáticos' },
  { valor: 'Impuestos', etiqueta: 'Impuestos' },
  { valor: 'Servicios', etiqueta: 'Servicios' },
  { valor: 'Otros', etiqueta: 'Otros' },
]

/** Egresos de una categoría de gasto dentro del turno (legacy D6, segundo bloque: "por tipo") —
 * espejo de `EgresoPorCategoria`. */
export type EgresoPorCategoria = { categoria: CategoriaGasto; total: number }

/** Egresos de un área dentro del turno (legacy D6, segundo bloque: "por área") — `idArea` es
 * `null` para el bucket "Sin área" (gastos sin área declarada) — espejo de `EgresoPorArea`. */
export type EgresoPorArea = { idArea: number | null; nombreArea: string; total: number }

/** Egresos del turno — gastos por categoría y por área más el total de retiros físicos; nunca
 * incluye refuerzos ni la apertura de cajón, que no son egresos — espejo de `EgresosDeTurno`. */
export type EgresosDeTurno = { porCategoria: EgresoPorCategoria[]; porArea: EgresoPorArea[]; retiros: number }

/** Respuesta de `GET /api/caja/turnos/{id}/resumen` (espejo de `ResumenDeTurno`, Slice 4 +
 * follow-up "Resumen parcial D6-content enrichment") — `medios` es el `importeEsperado`
 * derivado por medio y el medio ancla (efectivo), la misma derivación que el cierre va a
 * persistir (invariante intacto). `cantidadTickets`/`primerTicket`/`ultimoTicket`/
 * `ingresosPorArea`/`egresos` son contenido de reporte aditivo (legacy D6 parity) — nunca
 * alimentan la derivación del arqueo. */
export type ResumenDeTurno = {
  idTurnoCaja: number
  idMedioAncla: number
  medios: LineaDeResumen[]
  cantidadTickets: number
  primerTicket: TicketLimite | null
  ultimoTicket: TicketLimite | null
  ingresosPorArea: IngresoPorArea[]
  egresos: EgresosDeTurno
}

// --- Caja: cierre y comprobante Z (stage-6-turnos-caja, Slice 7) ---
// Espejo de `Ways.Application.Caja.Contratos` (Slice 4/7) — el cierre y el detalle con arqueos.

/** Un conteo declarado por el cajero — el ÚNICO dato que viaja en `SolicitudDeCierre.conteos`
 * (spec: Cierre Payload Carries Only Declared Counts). `importeEsperado` NUNCA es un campo de
 * entrada acá, lo deriva siempre el servidor. */
export type ConteoDeclarado = { idMedioPago: number; importeDeclarado: number }

/** Cuerpo de `POST /api/caja/turnos/{id}/cierre` — sin ningún campo de total, subtotal o
 * esperado (spec: No Request Shape Accepts A Total).
 *
 * `forzarSinRendicion`/`motivoSinRendicion` son el override supervisado de la guarda de rendición
 * de dispositivos, opcionales porque el servidor los rechaza cruzados: forzar sin motivo es `400
 * motivo_requerido` y un motivo sin forzar es `400 motivo_sin_forzado`, así que un cierre normal
 * viaja sin ninguno de los dos (nunca `false`/`null` explícitos). */
export type SolicitudDeCierre = {
  conteos: ConteoDeclarado[]
  observaciones: string | null
  forzarSinRendicion?: boolean
  motivoSinRendicion?: string
}

/** Una fila ya persistida de `arqueos_turno` — `diferencia` la calcula la columna `GENERATED
 * ALWAYS` del servidor (design decisión 6); positivo = faltante. */
export type LineaDeArqueoResumen = {
  idMedioPago: number
  importeEsperado: number
  importeDeclarado: number
  diferencia: number
  /** Esperado del cierre cuando un recálculo administrativo lo cambió; `null` si nunca cambió. */
  importeEsperadoOriginal: number | null
}

/** Respuesta de `POST /api/caja/turnos/{id}/cierre` y de `GET /api/caja/turnos/{id}` (el
 * payload del comprobante Z) — mismos campos planos que `TurnoResumen` más `arqueos`. */
export type TurnoConArqueos = TurnoResumen & {
  arqueos: LineaDeArqueoResumen[]
  /** Último recálculo administrativo del arqueo (edición/baja de un gasto de un turno cerrado);
   * `null` si nunca hubo uno. */
  fechaRecalculo: string | null
  idEmpleadoRecalculo: number | null
}

// --- Cierre por retiro (etapa 5): segundo modo de cierre — el cajero retira el efectivo
// contado y deja el fondo inicial en el cajón, nada se cuenta. Espejo de
// `Ways.Application.Caja.Contratos` (spec arqueo-de-cierre: Cierre Por Retiro). El cierre
// clásico de arriba (`SolicitudDeCierre`/`TurnoConArqueos`) queda intacto — es el modo web/arqueo.

/** Cuerpo de `POST /api/caja/turnos/{id}/cierre-por-retiro` — `importeRetirado` es un
 * MOVIMIENTO (lo que el cajero se lleva), nunca un total de ventas ni un conteo por medio
 * (spec: Cierre Por Retiro Payload Carries Only The Withdrawal Amount). `>= 0`; `0` es válido
 * y no genera ningún movimiento de retiro. `forzarSinRendicion`/`motivoSinRendicion`: mismo
 * contrato exacto que `SolicitudDeCierre` (los dos modos de cierre comparten la guarda). */
export type SolicitudDeCierrePorRetiro = {
  importeRetirado: number
  observaciones: string | null
  forzarSinRendicion?: boolean
  motivoSinRendicion?: string
}

/** Punto de venta de `ResumenDeCierrePorRetiro` — `numero` es el mismo valor que `id` (no hay
 * una columna de numeración operativa separada, ver el doc-comment del lado del servidor). */
export type PuntoVentaDeCierre = { id: number; numero: number; nombre: string }

/** Una fila de `ResumenDeCierrePorRetiro.ventasPorMedio` — ventas netas de vuelto por medio
 * (nunca de gastos), nombre ya resuelto. */
export type VentaPorMedio = { idMedioPago: number; nombre: string; importe: number }

/** Una fila de `ResumenDeCierrePorRetiro.retiros` — TODOS los retiros del turno, incluido el
 * de cierre, con el nombre del empleado ya resuelto. */
export type RetiroDeCierre = { fecha: string; importe: number; motivo: string; empleado: string }

/** Respuesta de `POST /api/caja/turnos/{id}/cierre-por-retiro` y de
 * `GET /api/caja/turnos/{id}/resumen-de-cierre` (reimpresión / recuperación tras una falla de
 * red ambigua sobre un turno ya cerrado, por cualquiera de los dos modos de cierre — las dos
 * rutas devuelven exactamente lo mismo).
 *
 * `diferencia` (judgment-day JD-E5a-1) se LEE de la fila ya persistida de `arqueos_turno` para
 * el medio ancla: `diferencia = -arqueo.diferencia = declarado - esperado` (positivo = sobrante,
 * negativo = faltante — el signo OPUESTO al de `arqueo.diferencia`, que persiste
 * `esperado - declarado`). NUNCA una fórmula sobre `totalRetiros`/`ventasEnEfectivoNetas`/
 * `gastosEnEfectivo`/`refuerzos`: esa fórmula solo vale cuando el ancla se declaró con
 * `fondoInicial` (cierto en el modo retiro, nunca en el clásico). Sin actividad física de
 * efectivo en el turno (el ancla sin fila en `arqueos_turno`), `diferencia = 0`. */
export type ResumenDeCierrePorRetiro = {
  idTurnoCaja: number
  puntoVenta: PuntoVentaDeCierre
  fechaApertura: string
  fechaCierre: string
  vendedor: string
  empleadoCierre: string
  fondoInicial: number
  ventasPorMedio: VentaPorMedio[]
  totalVentas: number
  retiros: RetiroDeCierre[]
  totalRetiros: number
  ventasEnEfectivoNetas: number
  gastosEnEfectivo: number
  refuerzos: number
  diferencia: number
}

// --- Histórico de cajas (G2) y detalle de turno (stage-11-exportacion-reportes, Slice 5a/5b,
// Slices 6a/6b web): espejo de `Ways.Application.Caja.Contratos` — turnos cerrados con totales
// ya sumados de sus `arqueos_turno` persistidos (nunca re-derivados) y el detalle del turno
// (mismo `ResumenDeTurno` que `/resumen` más los tickets y gastos leídos por
// `LectorDeLineasDelTurno`).

/** Fila de `GET /api/reportes/cajas` — espejo de `FilaDeHistoricoDeCajas`. */
export type FilaDeHistoricoDeCajas = {
  idTurnoCaja: number
  idPuntoVenta: number
  fechaApertura: string
  fechaCierre: string
  esperado: number
  declarado: number
  diferencia: number
  egresos: EgresosDeTurno
  fechaRecalculo: string | null
  idEmpleadoRecalculo: number | null
}

/** Página de `GET /api/reportes/cajas` — espejo de `PaginaDeHistoricoDeCajas`. */
export type PaginaDeHistoricoDeCajas = { items: FilaDeHistoricoDeCajas[]; total: number; pagina: number; tamanio: number }

/** Un ticket del turno dentro de `DetalleDeTurno.tickets` — espejo reducido de
 * `Ways.Application.Ventas.ComprobanteListado` (los mismos campos que `GET /api/ventas` lista,
 * anulados excluidos por `LectorDeLineasDelTurno`). */
export type TicketDeTurno = {
  id: number
  numero: number
  numeroVisible: string
  estado: EstadoComprobante
  fecha: string
  idPuntoVenta: number
  idCliente: number
  total: number
}

/** Monto neto cobrado por UN medio de pago dentro de una `VentaDeTurnoListado` — espejo de
 * `MedioDeVentaNeto`. `importe` ya es neto de vuelto (`Σ importe − Σ vuelto` agrupado por medio),
 * nunca lo que tecleó el cajero en caja. */
export type MedioDeVentaNeto = { idMedioPago: number; nombre: string; importe: number }

/** Fila de `GET /api/ventas/por-turno/{idTurno}` — espejo de
 * `Ways.Application.Ventas.VentaDeTurnoListado` (pantalla "Ventas del turno" del POS de
 * escritorio). A diferencia de `TicketDeTurno`, SÍ incluye anuladas y trae `nombreCliente`/
 * `mediosDePago` — ver el doc-comment del record del lado del servidor. */
export type VentaDeTurnoListado = {
  id: number
  numero: number
  numeroVisible: string
  estado: EstadoComprobante
  fecha: string
  idCliente: number
  nombreCliente: string
  total: number
  /** Totales de ajuste manual del comprobante (mismo criterio que en `ComprobanteEmitido`): la
   * pantalla los usa para marcar las ventas con precios cambiados a mano. */
  descuentoManualTotal: number
  recargoManualTotal: number
  mediosDePago: MedioDeVentaNeto[]
}

/** Un gasto del turno dentro de `DetalleDeTurno.gastos` — espejo de
 * `Ways.Application.Gastos.GastoListado`. `idPuntoVenta` es `number | null` porque el DTO de C#
 * ya lo declara `int?` (stage-gastos-origen-fondos-pos, PR2: el mirror lo tenía mal tipado desde
 * antes de este PR). `origenFondos` es informativo acá (Z-report/historial, nunca un total de
 * arqueo) — la UI lo usa para etiquetar "Caja general". */
export type GastoDeTurno = {
  id: number
  idPuntoVenta: number | null
  fecha: string
  categoria: CategoriaGasto
  idMedioPago: number
  importe: number
  origenFondos: OrigenFondosGasto
  idTurnoCaja: number | null
  /** `true` solo si el gasto tiene turno y ese turno sigue abierto: habilita la edición del POS. */
  turnoAbierto: boolean
  idProveedor: number | null
  idArea: number | null
  concepto: string
  detalle: string | null
  numeroFactura: string | null
  /** Compra a la que está ligado el gasto; no `null` bloquea categoría y proveedor en la edición. */
  idComprobanteCompra: number | null
}

/** Respuesta de `GET /api/caja/turnos/{id}/detalle` — espejo de `DetalleDeTurno`: el mismo
 * `ResumenDeTurno` que `/resumen` devuelve, sin tocarlo, más las dos listas del turno. */
export type DetalleDeTurno = {
  resumen: ResumenDeTurno
  tickets: TicketDeTurno[]
  gastos: GastoDeTurno[]
  /** Último recálculo administrativo del arqueo; `null` si nunca hubo uno. */
  fechaRecalculo: string | null
  idEmpleadoRecalculo: number | null
}

// --- Gastos del turno (stage-gastos-turno-carga-simple, POS): alta contra el turno abierto —
// espejo de `Ways.Application.Gastos.Contratos`.

/** Espejo del enum `OrigenFondosGasto` (Ways.Domain.Gastos) — viaja como texto, mismas dos
 * variantes PascalCase que el resto de los enums de este archivo (`CategoriaGasto`,
 * `TipoMovimientoTesoreria`): nunca camelCase. `CajaTurno` descuenta el efectivo del cajón del
 * turno; `Tesoreria` (stage-gastos-origen-fondos-pos, PR2) paga directo del fondo de tesorería de
 * la empresa, sin afectar el arqueo. */
export type OrigenFondosGasto = 'CajaTurno' | 'Tesoreria'

export const ORIGENES_DE_FONDOS_GASTO: { valor: OrigenFondosGasto; etiqueta: string }[] = [
  { valor: 'CajaTurno', etiqueta: 'Caja del turno' },
  { valor: 'Tesoreria', etiqueta: 'Caja general' },
]

/** Cuerpo de `POST /api/gastos` — espejo de `Ways.Application.Gastos.SolicitudDeGasto`. Sin
 * `idTurnoCaja`: el servidor lo resuelve del `idPuntoVenta` (spec: Gasto Requires An Open
 * Turno), nunca viaja en el cuerpo. `origenFondos` aterriza en stage-gastos-origen-fondos-pos
 * (PR2) — el POS siempre lo manda explícito (la UI no tiene default silencioso), aunque el
 * contrato de C# lo acepta opcional para retrocompatibilidad de otros clientes. */
export type SolicitudDeGasto = {
  idPuntoVenta: number
  categoria: CategoriaGasto
  idProveedor: number | null
  idArea: number | null
  concepto: string
  detalle: string | null
  idMedioPago: number
  numeroFactura: string | null
  importe: number
  idComprobanteCompra: number | null
  origenFondos: OrigenFondosGasto
}

/** Respuesta de `POST /api/gastos` — espejo de `Ways.Application.Gastos.GastoRegistrado`.
 * `idTurnoCaja`/`idPuntoVenta` son `number | null` porque el DTO de C# ya los declara `int?`
 * (stage-gastos-origen-fondos-pos, PR2: el mirror los tenía mal tipados desde antes de este PR). */
export type GastoRegistrado = {
  id: number
  idTurnoCaja: number | null
  idPuntoVenta: number | null
  fecha: string
  categoria: CategoriaGasto
  idProveedor: number | null
  idArea: number | null
  concepto: string
  detalle: string | null
  idMedioPago: number
  numeroFactura: string | null
  importe: number
  idEmpleado: number
  idComprobanteCompra: number | null
  origenFondos: OrigenFondosGasto
}

// --- Gastos de administración (stage-gastos-admin-retroactivos, PR3): alta SIN turno, pagada de
// la tesorería de la empresa, con fecha de negocio potencialmente retroactiva — espejo de los
// gastos de administración de `Ways.Application.Gastos.Contratos`.

/** Cuerpo de `POST /api/gastos/administracion` — espejo de
 * `SolicitudDeGastoDeAdministracion`. `fecha` viaja como `YYYY-MM-DD` (mismo shape que
 * `SolicitudDeCompra.fechaComprobante`) — nunca un ISO con hora: la hora la pone el servidor. */
export type SolicitudDeGastoDeAdministracion = {
  fecha: string
  idEmpresa: number
  idPuntoVenta: number | null
  categoria: CategoriaGasto
  idProveedor: number | null
  idArea: number | null
  concepto: string
  detalle: string | null
  idMedioPago: number
  numeroFactura: string | null
  importe: number
  idComprobanteCompra: number | null
}

/** Fila de `GET /api/gastos/administracion` — espejo de `GastoDeAdministracionListado`, con
 * nombres ya resueltos (proveedor/área/medio) — un catálogo dado de baja lógica los deja en
 * `null`, la fila nunca desaparece. */
export type GastoDeAdministracionListado = {
  id: number
  fecha: string
  idEmpresa: number
  idPuntoVenta: number | null
  idTurnoCaja: number | null
  categoria: CategoriaGasto
  idProveedor: number | null
  nombreProveedor: string | null
  idArea: number | null
  nombreArea: string | null
  concepto: string
  detalle: string | null
  idMedioPago: number
  nombreMedioPago: string | null
  numeroFactura: string | null
  importe: number
  origenFondos: OrigenFondosGasto
  idComprobanteCompra: number | null
  /** `true` solo si el gasto tiene turno y ese turno sigue abierto (editar uno de un turno
   * cerrado recalcula su arqueo). */
  turnoAbierto: boolean
}

/** Cuerpo de `PUT /api/gastos/{id}` y `PUT /api/gastos/administracion/{id}` — espejo de
 * `SolicitudDeEdicionDeGasto`: solo los campos editables (origen de fondos, punto de venta,
 * empresa, turno, fecha y compra ligada no se cambian por esta vía). */
export type SolicitudDeEdicionDeGasto = {
  categoria: CategoriaGasto
  idProveedor: number | null
  idArea: number | null
  concepto: string
  detalle: string | null
  idMedioPago: number
  numeroFactura: string | null
  importe: number
}

/** Página de resultados de `GET /api/gastos/administracion` — espejo de
 * `PaginaDeGastosDeAdministracion`. */
export type PaginaDeGastosDeAdministracion = {
  items: GastoDeAdministracionListado[]
  total: number
  pagina: number
  tamanio: number
}

// --- Tesorería (G3): libro encadenado (stage-11-exportacion-reportes, Slice 7) — espejo de
// `Ways.Application.Caja.Contratos`. `inicio`/`final` ya vienen calculados y persistidos al
// cierre (design decisión 6 de stage-6-turnos-caja); este contrato solo los transporta.

/** Espejo del enum `TipoMovimientoTesoreria` (Ways.Domain.Caja) — viaja como texto. */
export type TipoMovimientoTesoreria = 'RetiroCaja' | 'Deposito' | 'Gasto' | 'Ajuste'

/** Fila de `GET /api/reportes/tesoreria` — espejo de `MovimientoTesoreriaListado`, SIN
 * `idTenant` (nunca expuesto en una respuesta, doc 09). `idPuntoVenta` es `number | null` porque
 * el DTO de C# ya lo declara `int?` (stage-gastos-origen-fondos-pos, PR2: el mirror lo tenía mal
 * tipado desde antes de este PR — un gasto de origen Tesoreria puede originar una fila con
 * `idPuntoVenta` presente hoy, pero el esquema siempre lo permitió nulo).
 *
 * stage-tesoreria-por-empresa (PR5): agrega `idEmpresa` (la cadena es por empresa),
 * `nombrePuntoVenta` (dangling-fk-read-models: `null` sin punto de venta o con uno dado de baja
 * lógica, mismo criterio que `nombreProveedor`/`nombreArea` de `GastoDeAdministracionListado`) y,
 * para una fila `tipo: 'Gasto'`, `gastoCategoria`/`gastoConcepto` resueltos del gasto de origen. */
export type MovimientoTesoreriaListado = {
  id: number
  idEmpresa: number
  idPuntoVenta: number | null
  nombrePuntoVenta: string | null
  fecha: string
  tipo: TipoMovimientoTesoreria
  idTurnoCaja: number | null
  idGasto: number | null
  gastoCategoria: CategoriaGasto | null
  gastoConcepto: string | null
  concepto: string
  inicio: number
  ingreso: number
  egreso: number
  final: number
  idEmpleado: number
}

/** Página de `GET /api/reportes/tesoreria` — espejo de `PaginaDeMovimientosTesoreria`. */
export type PaginaDeMovimientosTesoreria = {
  items: MovimientoTesoreriaListado[]
  total: number
  pagina: number
  tamanio: number
}

// --- Existencias (stage-11-exportacion-reportes, Slice 9 — droppable a Etapa 13) — espejo de
// `Ways.Application.Reportes.Contratos`. Sin fecha/zona horaria: el stock es estado ACTUAL, no
// tiene dimensión temporal (a diferencia del resto de los reportes de esta etapa).
//
// stage-13-stock-inteligente (Slice 2/3): `FilaExistencia` gana `minimo`/`reposicion`/`estado` —
// espejo de `ReglaDeReposicion.EstadoDeReposicion` (nombres de miembro de C#, no snake_case:
// mismo criterio que el resto de los enums de wire de esta web, p. ej. `EstadoDeVencimiento`).

/** Espejo de `Ways.Domain.Stock.ReglaDeReposicion.EstadoDeReposicion` — `minimo` nulo nunca
 * alerta (`SinMinimo`), el borde es `cantidad <= minimo` (`Bajo`), nunca `<`. */
export type EstadoDeReposicion = 'SinMinimo' | 'Bajo' | 'Ok'

/** Fila de `GET /api/reportes/stock/existencias` — espejo de `FilaExistencia`. */
export type FilaExistencia = {
  idArticulo: number
  nombre: string
  cantidad: number
  minimo: number | null
  reposicion: number | null
  estado: EstadoDeReposicion
}

/** Respuesta de `GET /api/reportes/stock/existencias` — espejo de `Existencias`. */
export type Existencias = {
  idPuntoVenta: number
  filas: FilaExistencia[]
}

/** Cuerpo de `PUT /api/stock/minimos` — espejo de
 * `Ways.Application.Stock.Contratos.SolicitudDeMinimos` (stage-13-stock-inteligente, Slice 1/3;
 * design decisión 11). REEMPLAZO COMPLETO de ambos umbrales, no PATCH: `null` limpia el campo
 * (la operación de "unmanage"). */
export type SolicitudDeMinimos = {
  idPuntoVenta: number
  idArticulo: number
  minimo: number | null
  reposicion: number | null
}

/** Respuesta de `PUT /api/stock/minimos` — espejo de `MinimosDeStock`. La fila PERSISTIDA, leída
 * del mismo `RETURNING` que escribió, con `estado` ya derivado — la grilla la aplica sin volver a
 * pedir el reporte (design decisión 16). */
export type MinimosDeStock = {
  idPuntoVenta: number
  idArticulo: number
  cantidad: number
  minimo: number | null
  reposicion: number | null
  estado: EstadoDeReposicion
}

// --- POS: checkout (stage-5-pos-ventas, Slice 6 → wireado en Slice 7) ---
// Espejo de `Ways.Application.Ventas.Contratos` (confirmado contra el DTO real de
// `POST /api/ventas`, mergeado en Slice 4) — usado por `ventas.ts` (mappers) y `Pos.tsx`
// (wireado en Slice 7).

/** `idLote` (stage-12-lotes-vencimientos, Slice 14): `null` es el camino feliz de cero tecleo
 * (design decisión 19) — el servidor resuelve FEFO solo; solo viaja no-nulo cuando el cajero
 * eligió explícitamente un lote distinto del sugerido en el picker.
 *
 * stage-pos-venta-offline-web: `precioUnitario`/`descuentoUnitario` (espejo de la ampliación de
 * `Ways.Application.Ventas.LineaDeVenta`) SOLO viajan junto a `SolicitudDeVenta.numeroPreasignado`
 * — el precio que el dispositivo ya cobró offline desde su última instantánea. El camino
 * web/online sigue sin mandarlos nunca (quedan `undefined`), la única fuente de precio sigue
 * siendo `POST /api/ofertas/resolver` server-side. */
export type LineaDeVenta = {
  idArticulo: number
  cantidad: number
  codigoBarra: string | null
  idLote: number | null
  precioUnitario?: number
  descuentoUnitario?: number
  /** Ajuste manual de precio de la línea, en porcentaje con signo: negativo = descuento, positivo =
   * recargo; distinto de 0, entre -100 y 100, hasta 2 decimales. Solo viaja la línea que lo tiene
   * (ausente = sin ajuste, el payload queda idéntico al de antes): el servidor recalcula el monto
   * sobre el neto posterior a las ofertas y es la única autoridad del importe. */
  ajusteManualPorcentaje?: number | null
}
export type PagoDeVenta = { idMedioPago: number; importe: number; referencia: string | null; vuelto: number }

/** stage-17-presupuestos-y-remitos, Slice 7 (design: Interfaces/Contracts, decisión 2/tensión
 * T7): `idCliente`/`lineas` pasan a opcionales — con `idPresupuestoOrigen` presente, `lineas` NO
 * viaja (`dto-contract-honesty` regla 1: un campo que el servidor ignoraría no se manda) y
 * `idCliente` se omite para que el servidor lo derive del presupuesto (mandar uno en conflicto se
 * rechaza en vez de sobreescribirse en silencio — espejo de `SolicitudDeVenta.IdCliente` en
 * `Ways.Application.Ventas.Contratos`). Una venta común sigue mandando ambos como siempre. */
/** stage-pos-venta-offline-web: `numeroPreasignado` es el número que el dispositivo ya reservó
 * (`POST /api/ventas/reservas-numeracion`) y consumió para vender sin red — presente SOLO en un
 * reenvío del outbox offline; el camino web/online nunca lo manda (queda `undefined`, el servidor
 * sigue asignando como siempre). */
export type SolicitudDeVenta = {
  idPuntoVenta: number
  idCliente?: number
  codigoTipoComprobante: 'TX' | 'NCX'
  idComprobanteAsociado: number | null
  lineas?: LineaDeVenta[]
  pagos: PagoDeVenta[]
  direccionEntrega: string | null
  observaciones: string | null
  idPresupuestoOrigen?: number | null
  numeroPreasignado?: number
  /** Solo en una venta local con cuenta corriente: `true` si el dispositivo no pudo consultar el
   * límite de crédito al servidor y la registró igual; `false` si el servidor confirmó que cabía.
   * El servidor lo rechaza sin `numeroPreasignado`. */
  limiteDeCreditoNoValidado?: boolean
}

// --- POS: reserva de numeración offline (stage-pos-reserva-de-numeracion) ---
// Espejo de `SolicitudDeReservaDeNumeracion`/`BloqueDeNumeracionReservado`.

export type SolicitudDeReservaDeNumeracion = { idPuntoVenta: number; codigoTipoComprobante: string; cantidad: number }

/** `desde`/`hasta` inclusive — el rango que el dispositivo reparte localmente hasta agotarlo o
 * hasta pedir uno nuevo (que abandona este). */
export type BloqueDeNumeracionReservado = {
  desde: number
  hasta: number
  idPuntoVenta: number
  codigoTipoComprobante: string
}

/** Cuerpo de `POST /api/pos/rendicion-de-cola` — espejo de `SolicitudDeRendicionDeCola`: el
 * dispositivo declara hasta qué número repartió (`proximo - 1` de su bloque local; `desde - 1`
 * cuando todavía no repartió ninguno) y cuántas de esas ventas no llegaron al servidor (outbox +
 * rechazadas), para que el cierre de turno pueda verificarlo contra `comprobantes_venta`. Sin
 * `idPuntoVenta`/`idDispositivo`: los dos los deriva el servidor del dispositivo autenticado. */
export type SolicitudDeRendicionDeCola = {
  codigoTipoComprobante: string
  entregadoHasta: number
  pendientes: number
}

export type EstadoComprobante = 'Emitido' | 'Anulado'

/** Item ya emitido — snapshot inmutable (espejo de `ItemEmitido`). */
export type ItemEmitido = {
  orden: number
  idArticulo: number | null
  descripcion: string
  codigoBarra: string | null
  idArea: number
  idListaPrecio: number
  idOferta: number | null
  idAlicuotaIva: number
  porcentajeIva: number
  cantidad: number
  precioUnitario: number
  descuento: number
  total: number
  /** stage-12-lotes-vencimientos (Slice 14): `null`/`false` para una línea sin lote — el ticket
   * y la reimpresión muestran lo mismo (`items_comprobante_venta.id_lote` es el snapshot
   * congelado). `loteVencido` es un warning, nunca un bloqueo (design decisión 12). */
  idLote: number | null
  codigoLote: string | null
  loteVencido: boolean
  /** stage-pos-venta-offline-web: warning, nunca bloqueo (mismo criterio que `loteVencido`) —
   * `true` cuando el precio que el dispositivo cobró offline difere de lo que el servidor
   * hubiera resuelto al sincronizar. Espejo de `ItemEmitido.PrecioDiscrepante`; `false`/ausente
   * para cualquier línea online, y para una relectura/reprint (nunca se persiste). Opcional (no
   * `false` fijo) para no forzar a cada fixture preexistente de `ItemEmitido` en el resto de la
   * suite a declararlo — un consumidor lo trata como `?? false`. */
  precioDiscrepante?: boolean
  /** Ajuste manual de la línea (espejo de `ItemEmitido.AjusteManualPorcentaje`/`AjusteManual`):
   * `ajusteManualPorcentaje` es `null` en una línea sin ajuste; `ajusteManual` es el monto con signo
   * (negativo = descuento, positivo = recargo) ya incluido en `total`. */
  ajusteManualPorcentaje: number | null
  ajusteManual: number
}

/** Pago ya emitido — espejo de `PagoEmitido`. */
export type PagoEmitido = { idMedioPago: number; importe: number; referencia: string | null; vuelto: number }

/** Respuesta de `POST /api/ventas` (checkout) y `GET /api/ventas/{id}` (reimpresión) — espejo
 * de `ComprobanteEmitido`. `numeroVisible` ya viene formateado `PPPP-NNNNNNNN`.
 *
 * stage-17-presupuestos-y-remitos, Slice 7 (design: Interfaces/Contracts, OD9/T7):
 * `idPresupuestoOrigen` — `null` en el 100% del tráfico que no nace de un presupuesto, el id del
 * presupuesto convertido en el resto (`dto-contract-honesty` regla 2: round-trip, siempre
 * presente en la respuesta — nunca una clave ausente). */
export type ComprobanteEmitido = {
  id: number
  numero: number
  numeroVisible: string
  estado: EstadoComprobante
  fecha: string
  idPuntoVenta: number
  idCliente: number
  idComprobanteAsociado: number | null
  subtotal: number
  descuentoTotal: number
  /** Totales de los ajustes manuales por línea (espejo de `ComprobanteEmitido`): descuento y
   * recargo van separados, ambos en positivo en una venta normal, para que un recargo no oculte un
   * descuento. `total = subtotal - descuentoTotal - descuentoManualTotal + recargoManualTotal`. */
  descuentoManualTotal: number
  recargoManualTotal: number
  total: number
  direccionEntrega: string | null
  observaciones: string | null
  items: ItemEmitido[]
  pagos: PagoEmitido[]
  idPresupuestoOrigen: number | null
}

// --- Cuenta corriente: estado de cuenta y pago a cuenta (stage-7-cuenta-corriente, Slice 5) ---
// Espejo de `Ways.Application.CuentaCorriente.Contratos` — header + movimientos en un único GET
// (design decisión 9), pago a cuenta (RC) sin ningún campo de importe propio (design decisión 6,
// `importeAplicado = Σ importe − Σ vuelto`).

export type TipoMovimientoCc = 'Consumo' | 'Pago' | 'Ajuste' | 'ActualizacionPrecios'

/** Distingue un movimiento `Ajuste` por su origen — solo viene poblado para ese tipo (design
 * decisión 8/9, espejo de `EtiquetaDeAjuste`). */
export type EtiquetaDeAjuste = 'Manual' | 'AnulacionContramovimiento'

/** Header de estado de cuenta — `disponibilidad: null` cuando `creditoIlimitado` (nunca un
 * número fabricado, espejo de `EstadoDeCuentaHeader`). */
export type EstadoDeCuentaHeader = {
  saldo: number
  limiteCredito: number
  creditoIlimitado: boolean
  disponibilidad: number | null
}

/** Una fila del ledger — `saldoResultante` es la ÚNICA fuente del saldo corrido, nunca
 * re-derivada en pantalla (espejo de `MovimientoDeCuentaCorriente`). */
export type MovimientoDeCuentaCorriente = {
  id: number
  fecha: string
  tipo: TipoMovimientoCc
  importe: number
  saldoResultante: number
  detalle: string | null
  idComprobanteVenta: number | null
  etiqueta: EtiquetaDeAjuste | null
}

/** Respuesta de `GET /api/clientes/{id}/cuenta-corriente` — `historico`/`desde`/`hasta` reflejan
 * la ventana EFECTIVA aplicada por el servidor, no lo pedido crudo (espejo de `EstadoDeCuenta`). */
export type EstadoDeCuenta = {
  header: EstadoDeCuentaHeader
  movimientos: MovimientoDeCuentaCorriente[]
  historico: boolean
  desde: string | null
  hasta: string | null
}

/** Un medio de pago de la RC — mismo shape que `PagoDeVenta`, redeclarado porque una RC no es un
 * checkout (design decisión 1, espejo de `PagoDeCuenta`). */
export type PagoDeCuenta = { idMedioPago: number; importe: number; referencia: string | null; vuelto: number }

/** Cuerpo de `POST /api/clientes/{id}/cuenta-corriente/pagos` — sin ningún campo de importe
 * propio (design decisión 6, espejo de `SolicitudDePagoACuenta`). */
export type SolicitudDePagoACuenta = { idPuntoVenta: number; pagos: PagoDeCuenta[]; observaciones: string | null }

// --- Cuenta corriente: ajuste manual y reliquidación (stage-7-cuenta-corriente, Slice 6) ------
// Espejo de `Ways.Application.CuentaCorriente.Contratos` (ajuste) y
// `Ways.Domain.CuentaCorriente.ReliquidadorDeConsumos` (reliquidación) — ninguna de las dos trae
// turno (design: Open Questions — "provenance, not authority").

/** Cuerpo de `POST /api/clientes/{id}/cuenta-corriente/ajustes` — `importe` viaja con signo,
 * decidido por quien llama (espejo de `SolicitudDeAjuste`). */
export type SolicitudDeAjuste = { idPuntoVenta: number; importe: number; detalle: string | null }

/** Cuerpo de `POST /api/clientes/{id}/cuenta-corriente/reliquidacion` — `idPuntoVenta` es
 * provenance, no autoridad (espejo de `SolicitudDeReliquidacion`). */
export type SolicitudDeReliquidacion = { idPuntoVenta: number }

/** Detalle auditable de una línea re-precificada — `motivo` no nulo ⇒ línea omitida
 * (`precioActual`/`totalDelDia` quedan `null`, `delta` en `0`), nunca fatal (espejo de
 * `DetalleDeLinea`). */
export type DetalleDeLinea = {
  idArticulo: number | null
  cantidad: number
  precioHistorico: number
  precioActual: number | null
  totalHistorico: number
  totalDelDia: number | null
  delta: number
  motivo: string | null
  /** Porcentaje manual con signo (negativo = descuento) que la reliquidación conservó en la línea.
   * El servidor omite la clave cuando no hay ajuste: sin ajuste, la respuesta de preview/commit lo
   * trae `undefined` y `parsearDetalleDeActualizacionPrecios` (detalle guardado en el ledger) lo
   * normaliza a `null`. */
  ajusteManualPorcentaje?: number | null
}

/** Detalle auditable de un consumo cubierto — `delta` ya lleva aplicada la fracción financiada
 * (espejo de `DetalleDeConsumo`). */
export type DetalleDeConsumo = {
  idMovimiento: number
  idComprobanteVenta: number
  delta: number
  lineas: DetalleDeLinea[]
}

/** Respuesta de `GET`/`POST …/reliquidacion` — la MISMA forma para preview y commit (nunca dos
 * fórmulas, design: "never two formulas"). `idsMovimientosCubiertos` vacío + `delta === 0` es un
 * no-op limpio, distinguible de un error (espejo de `ResultadoDeReliquidacion`). */
export type ResultadoDeReliquidacion = {
  delta: number
  idsMovimientosCubiertos: number[]
  detalle: DetalleDeConsumo[]
  hayMas: boolean
}

// --- Compras (stage-8-compras-transferencias-inventario, Slice 5) --------------------------
// Espejo de `Ways.Application.Compras.Contratos` — ningún request lleva `cantidad`, `total` ni
// `delta` (design decisión 3): `CalculadorDeCompra` deriva todo eso server-side, el mirror de
// `compras.ts` es puramente informativo (dto-contract-honesty: el servidor siempre gana).

export type EstadoCompra = 'Borrador' | 'Confirmada' | 'Anulada'

/** Una línea del cuerpo de `POST`/`PUT /api/compras` (espejo de `LineaDeCompraSolicitada`).
 * `idArticulo: null` declara una línea por concepto: no admite lote, bultos ni `actualizaCosto`
 * verdadero (el servidor responde 400 en vez de descartarlos). */
export type LineaDeCompraSolicitada = {
  idArticulo: number | null
  descripcion: string
  unidades: number
  bultos: number | null
  unidadesPorBulto: number | null
  costoUnitario: number
  descuento: number
  idAlicuotaIva: number
  actualizaCosto: boolean
  /** stage-12-lotes-vencimientos (Slice 14): input crudo de recepción para un artículo
   * lote-efectivo — nada se resuelve a esta altura (design: "nothing is resolved at draft
   * time"), solo se persiste tal cual mientras la compra es borrador. `null` para un artículo
   * que no controla lote (el servidor los ignora, `ReglaDeLotes.ControlEfectivo`). */
  codigoLote: string | null
  fechaVencimiento: string | null
  /** Código que el proveedor imprimió para la línea (máx. 50). El servidor lo recorta; vacío o
   * `null` = sin código. Se guarda en toda línea que lo trae; al confirmar, una línea con artículo
   * lo asocia al artículo y al proveedor de la compra si está libre. */
  codigoProveedor: string | null
}

/** Cuerpo de `POST /api/compras` (crea un borrador) y `PUT /api/compras/{id}` (replace-set
 * completo del header + los items — espejo de `SolicitudDeCompra`).
 *
 * `idOrdenCompra` — stage-16-ordenes-de-compra, Slice 3/6: liga esta compra a una orden de compra
 * existente (recepción). `null` = compra sin OC (100% del tráfico previo a esta etapa, sin cambio
 * de comportamiento). Seteable/cambiable solo mientras la compra es `Borrador`; congelado después
 * (espejo del campo posicional final de `SolicitudDeCompra`, C#, default `null`). */
export type SolicitudDeCompra = {
  idProveedor: number
  idTipoComprobante: number
  idPuntoVenta: number
  numeroExterno: string | null
  fechaComprobante: string | null
  observaciones: string | null
  items: LineaDeCompraSolicitada[]
  idOrdenCompra: number | null
  /** `null` = el valor del tipo. En una factura lo fija el tipo y pedir lo contrario es 400. */
  discriminaIva: boolean | null
  /** Override de redondeo: el IVA que el proveedor imprimió por alícuota (tolerancia $1,00). */
  ivaImpreso: IvaImpresoSolicitado[] | null
  /** El costo unitario tipeado ya trae el IVA. Solo vale si el comprobante discrimina IVA: en otro
   * caso el servidor responde 400, así que el editor lo manda en `false`. */
  preciosIncluyenIva: boolean
  /** Reemplaza el conjunto completo de percepciones; `null` = ninguna. */
  percepciones: PercepcionSolicitada[] | null
}

export type TipoDePercepcion = 'iibb' | 'iva'

/** Una percepción tal como la imprimió el proveedor (espejo de `PercepcionSolicitada`): el importe
 * es lo que dice la factura; base y alícuota son informativas. */
export type PercepcionSolicitada = {
  tipo: TipoDePercepcion
  baseImponible: number
  alicuota: number
  importe: number
}

/** Una percepción persistida (espejo de `PercepcionDeCompraDetalle`). `baseImponible`/`importe` son
 * `null` para el rol vendedor, igual que el desglose de IVA. */
export type PercepcionDeCompra = {
  tipo: TipoDePercepcion
  alicuota: number
  baseImponible: number | null
  importe: number | null
}

/** El IVA impreso en el comprobante para una alícuota (espejo de `IvaImpresoSolicitado`). */
export type IvaImpresoSolicitado = { idAlicuotaIva: number; iva: number }

/** Una fila del desglose de IVA de una compra (espejo de `AlicuotaDeCompra`). `neto`/`iva` son `null`
 * para el rol vendedor, igual que el total de cada ítem. */
export type AlicuotaDeCompra = { idAlicuotaIva: number; porcentaje: number; neto: number | null; iva: number | null }

/** Un item ya persistido, con su `precioSugerido` (espejo de `ItemDeCompra`). */
export type ItemDeCompra = {
  orden: number
  /** `null` = línea por concepto (sin artículo, sin stock ni costo). */
  idArticulo: number | null
  descripcion: string
  cantidad: number
  bultos: number | null
  unidadesPorBulto: number | null
  /** `costoUnitario`/`descuento`/`total`/`precioSugerido` son `null` para el rol vendedor: no ve
   * el costo de los artículos (los totales del encabezado sí llegan). */
  costoUnitario: number | null
  descuento: number | null
  idAlicuotaIva: number
  porcentajeIva: number
  total: number | null
  actualizaCosto: boolean
  precioSugerido: number | null
  /** `codigoLote`/`fechaVencimiento`: mismo input crudo de `LineaDeCompraSolicitada`, ya
   * persistido. `idLote` es el lote resuelto (get-or-create) — `null` mientras la compra es
   * borrador y para un artículo que no controla lote (stage-12-lotes-vencimientos, Slice 14). */
  codigoLote: string | null
  fechaVencimiento: string | null
  idLote: number | null
  /** Código del proveedor tal como se guardó en la línea, haya quedado asociado o no. */
  codigoProveedor: string | null
}

/** Detalle completo de una compra (espejo de `CompraDetalle`).
 *
 * `idOrdenCompra` — stage-16-ordenes-de-compra, Slice 3/6 (`dto-contract-honesty` regla 2: un
 * campo request-only no satisface el round-trip). `null` = compra sin OC ligada. */
export type CompraDetalle = {
  id: number
  idProveedor: number
  idTipoComprobante: number
  idPuntoVenta: number
  numeroExterno: string | null
  fechaComprobante: string | null
  fechaRecepcion: string | null
  subtotal: number
  descuentoTotal: number
  ivaTotal: number | null
  total: number
  observaciones: string | null
  estado: EstadoCompra
  items: ItemDeCompra[]
  idOrdenCompra: number | null
  discriminaIva: boolean
  /** Vacío cuando la compra no discrimina IVA. */
  alicuotas: AlicuotaDeCompra[]
  preciosIncluyenIva: boolean
  percepciones: PercepcionDeCompra[]
  /** Lo ya pagado de la compra y lo que falta pagar (cero fuera de una compra confirmada): datos de
   * encabezado, los ve también el vendedor. */
  pagado: number
  saldoPendiente: number
}

/** Fila de `GET /api/compras` — shape reducido (espejo de `CompraListada`). `saldoPendiente` es lo
 * que falta pagar de la compra (cero si no está confirmada). */
export type CompraListada = {
  id: number
  idProveedor: number
  idTipoComprobante: number
  numeroExterno: string | null
  estado: EstadoCompra
  fechaRecepcion: string | null
  total: number
  saldoPendiente: number
}

/** Cuerpo de `POST /api/compras/{id}/pagos` (espejo de `SolicitudDePagoDeCompra`). `fecha` viaja como
 * `YYYY-MM-DD` (fecha de negocio, nunca futura); `concepto` nulo deja el default del servidor. */
export type SolicitudDePagoDeCompra = { fecha: string; importe: number; idMedioPago: number; concepto: string | null }

/** Respuesta de `POST /api/compras/{id}/pagos` (espejo de `ResultadoDePagoDeCompra`): el gasto creado
 * y el estado de pago de la compra después de este pago. */
export type ResultadoDePagoDeCompra = { gasto: GastoRegistrado; pagado: number; saldoPendiente: number }

/** Página de resultados de `GET /api/compras` (espejo de `PaginaDeCompras`). */
export type PaginaDeCompras = { items: CompraListada[]; total: number; pagina: number; tamanio: number }

/** Respuesta de `POST /api/compras/{id}/anular` — `gastosLigados` es la regla invertida
 * (design decisión 6): la anulación NUNCA bloquea por gastos ligados, solo REPORTA cuántos pagos
 * quedaron colgados de la compra anulada (espejo de `ResultadoAnulacion`). */
export type ResultadoAnulacion = { compra: CompraDetalle; gastosLigados: number }

/** Cuerpo de `POST /api/compras/{id}/precios` (espejo de `SolicitudDeAplicarPrecios`). */
export type SolicitudDeAplicarPrecios = { idListaPrecio: number; confirmarReemplazo: boolean }

/** Resultado por línea de aplicar `precioSugerido` — partial success es el contrato honesto
 * (espejo de `ResultadoAplicarPrecio`). `orden` identifica la línea (su `orden` en la compra, único
 * dentro de ella); `idArticulo` por sí solo puede repetirse entre líneas. */
export type ResultadoAplicarPrecio = {
  orden: number
  idArticulo: number
  aplicado: boolean
  precio: number | null
  error: string | null
}

// --- Saldo de proveedor (stage-8, Slice 4 backend / Slice 5 web) ----------------------------
// `GET /api/proveedores/{id}/saldo` — mapeado top-level, no dentro de `/api/proveedores`
// (design: API Surface, "the AND-composition trap"). Espejo de `ServicioDeSaldoDeProveedor`.

export type EstadoPago = 'Pagada' | 'Parcial' | 'Impaga'

export type CompraConEstadoPago = {
  idComprobanteCompra: number
  numeroExterno: string | null
  total: number
  pagado: number
  estadoPago: EstadoPago
}

export type SaldoDeProveedor = { idProveedor: number; saldo: number; compras: CompraConEstadoPago[] }

// --- Cuenta corriente de proveedores (stage-15-cc-proveedores-ledger, Slice 4 backend / Slice 6
// web) — espejo de `Ways.Application.CuentaCorriente.ContratosDeProveedor`. `GET
// /api/proveedores/{id}/cuenta-corriente` es la lectura PAGINADA del ledger completo (distinta de
// `SaldoDeProveedor`/`/saldo`, el resumen por-compra — design decisión 9, dos read models a
// propósito, ninguno amplía al otro). `POST …/ajustes` es la única escritura de esta superficie.

export type TipoMovimientoCcProveedor = 'Apertura' | 'Compra' | 'Pago' | 'Ajuste'

/** Una fila del ledger de proveedores — `saldoResultante` es la ÚNICA fuente del saldo corrido,
 * nunca re-derivada en pantalla (design decisión 11). `etiqueta` reutiliza el mismo enum
 * `EtiquetaDeAjuste` que la cuenta corriente de clientes (el backend reusa la clase, Contratos.cs)
 * — solo viene poblado cuando `tipo === 'Ajuste'`. */
export type MovimientoDeCuentaDeProveedor = {
  idMovimiento: number
  fecha: string
  tipo: TipoMovimientoCcProveedor
  importe: number
  saldoResultante: number
  detalle: string | null
  idComprobanteCompra: number | null
  idGasto: number | null
  etiqueta: EtiquetaDeAjuste | null
  /** Solo en las filas de tipo `Compra`: lo que falta pagar de ESA compra hoy (cero si está anulada). */
  saldoPendienteDeLaCompra: number | null
}

/** `saldo` viene de `proveedores.saldo` — NUNCA re-derivado de los movimientos de esta misma
 * página (design decisión 11). */
export type EstadoDeCuentaDeProveedorHeader = { idProveedor: number; saldo: number }

/** Forma PAGINADA (design decisión 10 / `state.yaml` OD9): `pagina`/`tamanio`/`total` habilitan
 * "Página N de M" en el web — a diferencia de `EstadoDeCuenta` (clientes, stage 7), que no pagina. */
export type PaginaDeEstadoDeCuentaDeProveedor = {
  header: EstadoDeCuentaDeProveedorHeader
  items: MovimientoDeCuentaDeProveedor[]
  total: number
  pagina: number
  tamanio: number
  historico: boolean
  desde: string | null
  hasta: string | null
}

/** Cuerpo de `POST /api/proveedores/{id}/cuenta-corriente/ajustes` — deliberadamente SIN `tipo`
 * ni `saldoResultante` (design decisión 15, `dto-contract-honesty`): ningún endpoint de esta etapa
 * acepta un saldo o un delta ya calculado por el cliente. */
export type SolicitudDeAjusteDeProveedor = { idPuntoVenta: number; importe: number; detalle: string }

// --- Stock: transferencias y conteo de inventario (stage-8, Slice 6) -----------------------
// Espejo de `Ways.Application.Stock.Contratos` — ningún request lleva un delta como input
// (dto-contract-honesty): la transferencia manda una cantidad siempre POSITIVA por línea (el
// signo por punto de venta lo decide el servidor), el conteo manda el TOTAL contado, nunca el
// ajuste (server-derived bajo el lock de la fila de stock).

/** Balance de `GET /api/stock` (espejo de `StockActual`) — `cantidad` es `0` mientras no exista
 * todavía una fila de `stock` para el par. */
export type StockActual = { idPuntoVenta: number; idArticulo: number; cantidad: number }

/** Respuesta de `POST /api/stock/conteos` (espejo de `ResultadoConteo`, judgment-day stage-8
 * Slice 6): a diferencia de `StockActual`, lleva la verdad de escritura tal como el servidor la
 * calculó bajo el mismo lock de fila que derivó el ajuste — `movimientoRegistrado` distingue el
 * no-op de diferencia cero de la rama que sí escribió un movimiento, sin que el cliente tenga que
 * releer `GET /api/stock` (esa segunda lectura puede correr después de una venta concurrente y
 * mentir en cualquiera de las dos direcciones).
 *
 * stage-12-lotes-vencimientos (Slice 15, espejo de `Ways.Application.Stock.Contratos.
 * ResultadoConteo`): `lotes` lleva el resultado POR LOTE cuando el conteo llegó vía
 * `SolicitudDeConteo.lotes` — `null`/ausente para un conteo agregado. `cantidad`/
 * `cantidadAnterior`/`delta` siguen siendo el AGREGADO (la suma de los deltas por lote cuando
 * `lotes` está presente) — el cliente nunca necesita sumar a mano. */
export type ResultadoConteo = {
  idPuntoVenta: number
  idArticulo: number
  cantidad: number
  cantidadAnterior: number
  delta: number
  movimientoRegistrado: boolean
  lotes?: LoteContado[] | null
}

/** Resultado por lote de un conteo (stage-12-lotes-vencimientos, Slice 15, espejo de
 * `Ways.Application.Stock.Contratos.LoteContado`) — una fila por cada `ConteoDeLote` del
 * request, incluidos los lotes sin diferencia (`movimientoRegistrado` en `false`). */
export type LoteContado = {
  idLote: number
  cantidad: number
  cantidadAnterior: number
  delta: number
  movimientoRegistrado: boolean
}

/** Una línea del desglose por lote de un conteo (stage-12-lotes-vencimientos, Slice 15, espejo
 * de `Ways.Application.Stock.Contratos.ConteoDeLote`) — `contada` es el total físicamente
 * contado de ESE lote, misma disciplina que el agregado: nunca un delta. */
export type ConteoDeLote = { idLote: number; contada: number }

/** Una línea del cuerpo de `POST /api/stock/transferencias` — `cantidad` siempre positiva
 * (espejo de `LineaDeTransferencia`). stage-12-lotes-vencimientos (Slice 15, misma forma exacta
 * que agrega el Slice 14): `idLote` es opcional para un artículo lote-efectivo — omitido, el
 * servidor lo resuelve vía FEFO. */
export type LineaDeTransferencia = { idArticulo: number; cantidad: number; idLote?: number | null }

/** Cuerpo de `POST /api/stock/transferencias` (espejo de `SolicitudDeTransferencia`).
 * `observaciones` es obligatoria, mismo criterio que el ajuste manual. */
export type SolicitudDeTransferencia = {
  idPuntoVentaOrigen: number
  idPuntoVentaDestino: number
  observaciones: string
  lineas: LineaDeTransferencia[]
}

/** El stock resultante de un artículo en AMBOS puntos de venta tras la transacción (espejo de
 * `LineaTransferida`). `idLote` (stage-12-lotes-vencimientos, Slice 10): el backend lo manda con
 * clave de agregación `(idArticulo, idLote)` — dos líneas del mismo artículo con lotes distintos
 * son filas separadas; la columna de lote de la grilla de `Transferencias.tsx` es del Slice 15. */
export type LineaTransferida = { idArticulo: number; idLote: number | null; cantidadOrigen: number; cantidadDestino: number }

/** Respuesta de `POST /api/stock/transferencias` (espejo de `ResultadoTransferencia`). */
export type ResultadoTransferencia = {
  idPuntoVentaOrigen: number
  idPuntoVentaDestino: number
  lineas: LineaTransferida[]
}

/** Cuerpo de `POST /api/stock/conteos` — `contada` es el TOTAL físicamente contado, nunca un
 * delta (espejo de `SolicitudDeConteo`; spec: conteo-de-inventario / Conteo Input Is The Counted
 * Total, Never A Delta).
 *
 * stage-12-lotes-vencimientos (Slice 15, espejo de `Ways.Application.Stock.Contratos.
 * SolicitudDeConteo`, design decisión 18): `contada` se ensancha a `number | null` — el contrato
 * pasa a EXACTLY-ONE-OF `contada`/`lotes` (`400 conteo_contada_y_lotes` si vienen ambos o
 * ninguno). Un artículo lote-efectivo cuenta por lote (`lotes`, un total contado por cada
 * `idLote`); uno sin lote efectivo sigue mandando el total agregado (`contada`). */
export type SolicitudDeConteo = {
  idPuntoVenta: number
  idArticulo: number
  contada: number | null
  observaciones: string
  lotes?: ConteoDeLote[] | null
}

// --- Lotes y vencimientos (stage-12-lotes-vencimientos) --------------------------------------
// Espejo de `Ways.Application.Stock.Contratos`/`Ways.Application.Reportes.Contratos` — mismos
// nombres de campo que el backend serializa en camelCase. `EstadoDeVencimiento` viaja como texto
// (`JsonStringEnumConverter` sin política de naming en `Program.cs`, mismo criterio que
// `Granularidad`/`EstadoPago`) — los NOMBRES de los miembros de C#, no una variante snake_case.

/** Espejo del enum `EstadoDeVencimiento` (Ways.Domain.Stock) — clasificación de un lote respecto
 * de "hoy". `SinFecha` es el lote sin identificar: se incluye en el reporte, nunca se excluye. */
export type EstadoDeVencimiento = 'Vencido' | 'PorVencer' | 'Vigente' | 'SinFecha'

/** Fila de `GET /api/stock/lotes` y resultado de `POST /api/stock/lotes` (espejo de
 * `LoteListado`, design decisión 19). `sugerido` es el pick FEFO server-computed
 * (`ReglaDeLotes.ElegirFefo`) — el picker lo renderiza, nunca lo recalcula. Misma forma exacta
 * que agrega el Slice 14. */
export type LoteListado = {
  idLote: number
  idArticulo: number
  codigo: string
  fechaVencimiento: string | null
  esSinIdentificar: boolean
  cantidad: number
  estado: EstadoDeVencimiento
  sugerido: boolean
}

/** Fila de `GET /api/reportes/stock/vencimientos` (espejo de `FilaDeVencimiento`) — solo lotes
 * con `stock_lotes.cantidad` positivo. `fechaVencimiento` es `null` exactamente para el lote sin
 * identificar, que clasifica `SinFecha` y SE INCLUYE en el reporte. */
export type FilaDeVencimiento = {
  idArticulo: number
  articulo: string
  idLote: number
  codigoLote: string
  fechaVencimiento: string | null
  cantidad: number
  estado: EstadoDeVencimiento
}

/** Respuesta de `GET /api/reportes/stock/vencimientos` (espejo de `Vencimientos`). `hoy`/
 * `zonaHoraria` son la fecha y la zona efectivamente resueltas para clasificar cada fila — "hoy"
 * se calcula en la zona horaria del punto de venta, NUNCA en UTC. `diasDeAlerta` es el horizonte
 * efectivamente aplicado: el parámetro `dias` de la query si vino, si no el
 * `dias_alerta_vencimiento` resuelto (PV → empresa → default). */
export type Vencimientos = {
  idPuntoVenta: number
  hoy: string
  diasDeAlerta: number
  zonaHoraria: string
  filas: FilaDeVencimiento[]
}

/** Respuesta de `GET /api/reportes/stock/vencimientos/resumen` (espejo de
 * `ResumenDeVencimientos`) — el tile de Tablero. Mismos tres conteos que `Vencimientos.filas`
 * agrupados por `FilaDeVencimiento.estado` — nunca una query de agregación separada. */
export type ResumenDeVencimientos = { idPuntoVenta: number; vencidos: number; porVencer: number; sinFecha: number }

// --- Reposición de stock (stage-13-stock-inteligente, Slices 4/5) ----------------------------
// Espejo de `Ways.Application.Reportes.Contratos` — alerta y sugerencia de compra
// (`minimo IS NOT NULL AND cantidad <= minimo`), agrupable por proveedor habitual, más el feed
// independiente de rotación que alimenta `minimoSugerido` (Slice 5).

/** Fila de `GET /api/reportes/stock/reposicion` — espejo de `FilaDeReposicion`. `sugerido` es
 * `null`, NUNCA `0`, cuando `reposicion` no está configurado. `idProveedor`/`proveedor` viajan
 * `null` en conjunto para la fila que cae en el grupo "Sin proveedor" — un `idProveedorHabitual`
 * que apunta a un proveedor soft-deleted también proyecta `null` acá (orchestrator decision 12,
 * tasks.md stage-13): un solo bucket "Sin proveedor", nunca un FK colgante ni un segundo bucket.
 * `consumoDiarioPromedio`/`diasDeCobertura` son `null`, NUNCA `0`, cuando el artículo no tiene
 * historia calificada en la ventana de rotación. */
export type FilaDeReposicion = {
  idArticulo: number
  articulo: string
  cantidad: number
  minimo: number
  reposicion: number | null
  sugerido: number | null
  idProveedor: number | null
  proveedor: string | null
  consumoDiarioPromedio: number | null
  diasDeCobertura: number | null
}

/** Respuesta de `GET /api/reportes/stock/reposicion` — espejo de `Reposicion`. `hoy`/
 * `zonaHoraria` son la fecha y la zona efectivamente resueltas (echo obligatorio, mismo criterio
 * que `Vencimientos.hoy`/`Vencimientos.zonaHoraria`); `diasDeRotacion` es el horizonte efectivo —
 * el parámetro `dias` de la query si vino, si no `dias_rotacion` (default 30). */
export type Reposicion = {
  idPuntoVenta: number
  hoy: string
  diasDeRotacion: number
  zonaHoraria: string
  filas: FilaDeReposicion[]
}

/** Respuesta de `GET /api/reportes/stock/reposicion/resumen` (stage-13-stock-inteligente, Slice 7)
 * — espejo de `ResumenDeReposicion`, el tile de Tablero. Mismos tres conteos que `Reposicion.filas`
 * foldeados por `ObtenerResumenDeReposicionAsync` — nunca una query de agregación separada.
 * `sinProveedor` cuenta el grupo "Sin proveedor" (`idProveedor` nulo en `FilaDeReposicion`) — NUNCA
 * conflado con "sin sugerido" (`sugerido` nulo, `reposicion` sin configurar): orchestrator decision
 * 5 (tasks.md) corrigió este nombre sobre la `sinSugerencia` vieja de design.md. */
export type ResumenDeReposicion = { idPuntoVenta: number; bajoMinimo: number; sinStock: number; sinProveedor: number }

/** Fila de `GET /api/reportes/stock/rotacion` — espejo de `FilaDeRotacion`. Solo existe porque el
 * artículo tuvo AL MENOS UN movimiento calificado (venta o su anulación, nunca la anulación de
 * una compra) en la ventana — un artículo sin historia NO ES UNA FILA de esta lista, nunca una
 * fila con `minimoSugerido` en `0`. */
export type FilaDeRotacion = {
  idArticulo: number
  articulo: string
  consumoEnVentana: number
  consumoDiarioPromedio: number
  minimoSugerido: number
}

/** Respuesta de `GET /api/reportes/stock/rotacion` — espejo de `Rotacion`. `diasCoberturaObjetivo`
 * es el horizonte de cobertura efectivamente resuelto que multiplica `minimoSugerido` — mismo
 * criterio de echo obligatorio que `diasDeRotacion`. */
export type Rotacion = {
  idPuntoVenta: number
  hoy: string
  diasDeRotacion: number
  diasCoberturaObjetivo: number
  zonaHoraria: string
  filas: FilaDeRotacion[]
}

// --- Reportes (stage-10-agregacion-dashboard, Slice 7): G1 parity — ventas/resumen y
// gastos/resumen. Espejo de `Ways.Application.Reportes.Contratos` — mismos nombres de campo que
// el backend serializa en camelCase, ningún dato de negocio recalculado en el cliente.

/** Espejo del enum `Granularidad` (Ways.Domain.Reportes) — viaja como texto (`JsonStringEnumConverter`
 * en `Program.cs`), nunca como ordinal. */
export type Granularidad = 'Dia' | 'Semana' | 'Mes'

/** Un bucket de la serie de ventas ya rellenada (sin huecos) — espejo de `BucketDeVentas`.
 * `ticketPromedio` es `null`, nunca `0`, cuando el bucket no tuvo ningún TX. */
export type BucketDeVentas = {
  etiqueta: string
  inicio: string
  neto: number
  cantidadTx: number
  ticketPromedio: number | null
}

/** Respuesta de `GET /api/reportes/ventas/resumen` — espejo de `ResumenDeVentas`. `zonaHoraria`
 * es la zona efectivamente resuelta y aplicada al bucketing (echo obligatorio, design decisión 5). */
export type ResumenDeVentas = {
  desde: string
  hasta: string
  granularidad: Granularidad
  zonaHoraria: string
  serie: BucketDeVentas[]
  netoVendido: number
  cantidadTx: number
  ticketPromedio: number | null
  cantidadNcx: number
  netoNcx: number
}

/** Un bucket de la serie de gastos ya rellenada — espejo de `BucketDeGastos`, mismo criterio de
 * gap-fill que `BucketDeVentas`. */
export type BucketDeGastos = { etiqueta: string; inicio: string; importe: number }

/** Desglose por categoría de `GET /api/reportes/gastos/resumen` — espejo de `GastoPorCategoria`. */
export type GastoPorCategoria = { categoria: CategoriaGasto; importe: number; cantidadGastos: number }

/** Respuesta de `GET /api/reportes/gastos/resumen` — espejo de `ResumenDeGastos`; sin NCX ni
 * ticket promedio, `gastos` no tiene esa semántica. */
export type ResumenDeGastos = {
  desde: string
  hasta: string
  granularidad: Granularidad
  zonaHoraria: string
  serie: BucketDeGastos[]
  importeTotal: number
  porCategoria: GastoPorCategoria[]
}

// --- Reportes por dimensión (stage-10-agregacion-dashboard, Slice 8): sin granularidad ni
// bucketing — cada fila es un subtotal propio del período completo, nunca un porcentaje de un
// total implícito (espejo de `Contratos.cs`, mismo criterio de `netoVendido`/`ticketPromedio`
// nullable que `BucketDeVentas`).

/** Fila de `GET /api/reportes/ventas/por-punto-venta` — espejo de `FilaVentasPorPuntoVenta`. */
export type FilaVentasPorPuntoVenta = {
  idPuntoVenta: number
  neto: number
  cantidadTx: number
  ticketPromedio: number | null
}

/** Respuesta de `GET /api/reportes/ventas/por-punto-venta` — espejo de `VentasPorPuntoVenta`. */
export type VentasPorPuntoVenta = {
  desde: string
  hasta: string
  zonaHoraria: string
  filas: FilaVentasPorPuntoVenta[]
}

/** Fila de `GET /api/reportes/ventas/por-vendedor`, agrupada por `id_empleado` (el vendedor
 * emisor) — espejo de `FilaVentasPorVendedor`. */
export type FilaVentasPorVendedor = {
  idEmpleado: number
  neto: number
  cantidadTx: number
  ticketPromedio: number | null
}

/** Respuesta de `GET /api/reportes/ventas/por-vendedor` — espejo de `VentasPorVendedor`. */
export type VentasPorVendedor = {
  desde: string
  hasta: string
  zonaHoraria: string
  filas: FilaVentasPorVendedor[]
}

/** Fila de `GET /api/reportes/ventas/por-medio-pago`, agrupada por
 * `pagos_comprobante.id_medio_pago` — espejo de `FilaVentasPorMedioPago`. `cantidadPagos` cuenta
 * filas de `pagos_comprobante`, no comprobantes (un pago dividido aporta una fila a cada medio). */
export type FilaVentasPorMedioPago = {
  idMedioPago: number
  neto: number
  cantidadPagos: number
}

/** Respuesta de `GET /api/reportes/ventas/por-medio-pago` — espejo de `VentasPorMedioPago`. */
export type VentasPorMedioPago = {
  desde: string
  hasta: string
  zonaHoraria: string
  filas: FilaVentasPorMedioPago[]
}

/** Fila de `GET /api/reportes/articulos/top`, agrupada por `id_articulo` — espejo de
 * `ArticuloTop`. `descripcion` es el snapshot de la línea, nunca un re-join contra `articulos`
 * (design decisión 10). `cantidad`/`total` son netos: una NCX resta por construcción, sin rama
 * de signo. */
export type ArticuloTop = {
  idArticulo: number
  descripcion: string
  cantidad: number
  total: number
}

/** Respuesta de `GET /api/reportes/articulos/top` — espejo de `TopArticulos`. `articulos` viene
 * ordenada por `total` descendente. */
export type TopArticulos = {
  desde: string
  hasta: string
  zonaHoraria: string
  articulos: ArticuloTop[]
}

/** Fila de `GET /api/reportes/articulos` (reporte de completitud de catálogo) — espejo de
 * `ArticuloDeReporte`. `area`/`categoria`/`marca`/`grupo`/`proveedor` son `null` cuando el
 * artículo no tiene esa clasificación asignada, o cuando el FK apunta a una fila dada de baja
 * lógica; la UI decide cómo mostrar el hueco ("Sin asignar"), nunca el servidor. `proveedor` ya
 * viene resuelto como nombre de fantasía (o razón social si no tiene). */
export type ArticuloDeReporte = {
  id: number
  codigoInterno: string
  nombre: string
  area: string | null
  categoria: string | null
  marca: string | null
  grupo: string | null
  proveedor: string | null
  activo: boolean
}

/** Cobertura del costo de un período de rentabilidad (stage-9-costo-congelado, tres estados:
 * real / estimado / desconocido) — espejo de `CoberturaDeCosto`. Viaja SIEMPRE en la respuesta de
 * `/rentabilidad` (spec rentabilidad-y-comisiones: NULL Cost Is Never Treated As Zero, And
 * Coverage Is Mandatory); cada campo alimenta el banner obligatorio del panel (spec tablero:
 * Margin Panel Is Invisible, Not Disabled, For Non-Admin). */
export type CoberturaDeCosto = {
  lineasTotales: number
  lineasConCostoReal: number
  lineasConCostoEstimado: number
  lineasSinCosto: number
  ventaTotal: number
  ventaConCostoReal: number
  ventaConCostoEstimado: number
  ventaSinCosto: number
  incluyeEstimados: boolean
}

/** Fila de margen por artículo dentro de `/rentabilidad`, agrupada por `id_articulo` — espejo de
 * `RentabilidadPorArticulo`. `idArticulo` es `null` en una línea de concepto libre;
 * `margenPorcentaje` es `null`, nunca `0`, mismo criterio que `ticketPromedio`. */
export type RentabilidadPorArticulo = {
  idArticulo: number | null
  descripcion: string
  ventaConsiderada: number
  costoConsiderado: number
  margen: number
  margenPorcentaje: number | null
}

/** Respuesta de `GET /api/reportes/rentabilidad` — espejo de `Rentabilidad`. `margenPorcentaje`
 * es `null`, nunca `0`, cuando `ventaConsiderada` es cero (denominador vacío, mismo criterio que
 * `ResumenDeVentas.ticketPromedio`). */
export type Rentabilidad = {
  desde: string
  hasta: string
  zonaHoraria: string
  ventaConsiderada: number
  costoConsiderado: number
  margen: number
  margenPorcentaje: number | null
  cobertura: CoberturaDeCosto
  porArticulo: RentabilidadPorArticulo[]
}

// --- Comisiones (stage-10-agregacion-dashboard, Slice 10): reporte PROVISIONAL — sin tabla
// propia, calculado on the fly (espejo de `Ways.Application.Reportes.Contratos`, `Comisiones`/
// `ComisionPorEmpleado`). Droppable en su totalidad (proposal Rollback Plan step 4).

/** Fila de `GET /api/reportes/comisiones`, agrupada por `id_empleado` — espejo de
 * `ComisionPorEmpleado`. `comision` = `netoVendido` × la tasa resuelta de la respuesta
 * (`comisionPorcentaje`). */
export type ComisionPorEmpleado = {
  idEmpleado: number
  netoVendido: number
  comision: number
}

/** Respuesta de `GET /api/reportes/comisiones` — espejo de `Comisiones` (spec
 * rentabilidad-y-comisiones: Comisiones Is A Provisional, Non-Persisted Report).
 * `comisionPorcentaje` es la tasa efectivamente resuelta (echo obligatorio, mismo criterio que
 * `zonaHoraria` en el resto de los reportes) — con el default `0` ninguna fila tiene comisión
 * distinta de cero. `provisional` viaja SIEMPRE en `true`. */
export type Comisiones = {
  desde: string
  hasta: string
  zonaHoraria: string
  comisionPorcentaje: number
  filas: ComisionPorEmpleado[]
  provisional: boolean
}

// --- Auditoría (stage-14-auditoria-trazabilidad, Slice 7) — espejo de
// `Ways.Application.Auditoria.Contratos` (Slice 5). `dto-contract-honesty`: `FilaDeAuditoria`/
// `PaginaDeAuditoria` abajo son los únicos DTOs mirroreados acá — cada campo se consume
// directamente en `Auditoria.tsx` al renderizar cada columna/el panel de detalle.
// `FiltrosDeAuditoria` (`Contratos.cs`) NO tiene mirror en este archivo (judgment-day, ronda 2,
// juez A): su forma difiere genuinamente de los filtros de pantalla (`desde`/`hasta` viajan como
// `DateTimeOffset?` nullable en el backend vs. `string` no-nulo `input[type=date]` en
// `FiltrosDeAlcanceDeAuditoria`/`FiltrosDeConsultaDeAuditoria`, `api/auditoria.ts`) — un mirror sin
// consumidor de tipo no ata nada, así que se optó por no crearlo en vez de dejarlo inerte.

/** Espejo de `FilaDeAuditoria` (Slice 5). `actor` `null` significa "el nombre no es visible para
 * esta sesión" (un actor de plataforma, excluido por el filtro de tenant/RLS de `usuarios` bajo
 * el LEFT JOIN) — NUNCA "sin actor": `idActor` siempre viaja, `Auditoria.tsx` lo muestra como
 * `#idActor`. `valorAnterior`/`valorNuevo` viajan con sus claves TAL CUAL fueron serializadas por
 * `SerializadorDeAuditoria` (snake_case) — la pantalla no las reinterpreta, solo las compara
 * clave por clave (`compararPayloads`, `PanelDeCambio`). */
export type FilaDeAuditoria = {
  idAuditoria: number
  creadoEl: string
  accion: string
  entidad: string
  idEntidad: number
  idActor: number
  actor: string | null
  idPuntoVenta: number | null
  valorAnterior: Record<string, unknown> | null
  valorNuevo: Record<string, unknown>
}

/** Página de `GET /api/auditoria` — espejo de `PaginaDeAuditoria` (Slice 5). */
export type PaginaDeAuditoria = { items: FilaDeAuditoria[]; total: number; pagina: number; tamanio: number }

/** Espejo del catálogo de 15 pares `(accion, entidad)` de `AccionAuditada`
 * (`Ways.Domain.Auditoria`, Slice 1 + las tres bajas de organización de la etapa 20 slice 4) —
 * alimenta el `<select>` de acción de `Auditoria.tsx`. El conteo lo congela
 * `Auditoria.test.tsx`, espejo de `AccionAuditadaTests.cs:21`: sin las tres últimas, la pantalla
 * renderizaba el código crudo (`tenant.baja`) y el filtro no las podía seleccionar.
 * Etiquetas en español; el VALOR que viaja al backend es siempre el `accion` crudo
 * (`precio.cambio`, ...) — la base no valida `accion` contra este catálogo (design decisión 15),
 * así que una fila con una acción retirada de acá sigue siendo consultable filtrando por su texto
 * exacto (no aparece en el `<select>`, pero el filtro por texto libre seguiría funcionando si se
 * expusiera). */
export const CATALOGO_DE_ACCIONES_AUDITADAS: { valor: string; etiqueta: string }[] = [
  { valor: 'precio.cambio', etiqueta: 'Cambio de precio' },
  { valor: 'venta.anulacion', etiqueta: 'Anulación de venta' },
  { valor: 'compra.anulacion', etiqueta: 'Anulación de compra' },
  { valor: 'stock.ajuste', etiqueta: 'Ajuste de stock' },
  { valor: 'stock.decomiso', etiqueta: 'Decomiso de stock' },
  { valor: 'stock.conteo', etiqueta: 'Conteo de inventario' },
  { valor: 'cc.reliquidacion', etiqueta: 'Reliquidación de cuenta corriente' },
  { valor: 'usuario.alta', etiqueta: 'Alta de usuario' },
  { valor: 'usuario.actualizacion', etiqueta: 'Actualización de usuario' },
  { valor: 'usuario.baja', etiqueta: 'Baja de usuario' },
  { valor: 'usuario.desbloqueo', etiqueta: 'Desbloqueo de usuario' },
  { valor: 'usuario.password', etiqueta: 'Cambio de contraseña' },
  { valor: 'tenant.baja', etiqueta: 'Baja de tenant' },
  { valor: 'empresa.baja', etiqueta: 'Baja de empresa' },
  { valor: 'pv.baja', etiqueta: 'Baja de punto de venta' },
]

// --- Órdenes de compra (stage-16-ordenes-de-compra, Slice 6) --------------------------------
// Espejo de `Ways.Application.Compras.ContratosDeOrdenDeCompra`/`EstadoOrdenCompra`
// (`Ways.Domain.Compras`) — la intención puesta en un proveedor antes de que exista un
// `comprobantes_compra`. `estado` viaja como el string del enum nativo (`JsonStringEnumConverter`
// sin naming policy, mismo criterio que `EstadoCompra`).

/** Espejo de `EstadoOrdenCompra` — el orden de los miembros ES el orden de ciclo de vida
 * (`design.md`: "member order = native type order"). */
export type EstadoOrdenCompra = 'Borrador' | 'Enviada' | 'RecibidaParcial' | 'Cerrada' | 'Anulada'

/** Una línea del cuerpo de `POST`/`PUT /api/ordenes-compra` (espejo de `LineaDeOrdenSolicitada`).
 * `orden` NO viaja: es server-asignado 1..N dentro del replace-set. `costoUnitarioEstimado` es
 * intención de precio, jamás un hecho — `null` = no cotizado. */
export type LineaDeOrdenSolicitada = {
  idArticulo: number
  descripcion: string
  cantidadPedida: number
  costoUnitarioEstimado: number | null
}

/** Cuerpo de `POST /api/ordenes-compra` (crea un borrador) y `PUT /api/ordenes-compra/{id}`
 * (replace-set completo del header + los items — espejo de `SolicitudDeOrdenDeCompra`). */
export type SolicitudDeOrdenDeCompra = {
  idProveedor: number
  idPuntoVenta: number
  fechaEsperada: string | null
  observaciones: string | null
  items: LineaDeOrdenSolicitada[]
}

/** Un item ya persistido — `orden` es el valor server-asignado (espejo de `ItemDeOrden`). */
export type ItemDeOrden = {
  orden: number
  idArticulo: number
  descripcion: string
  cantidadPedida: number
  costoUnitarioEstimado: number | null
}

/** Respuesta de `POST /`, `PUT /{id}`, `POST /{id}/enviar`, `POST /{id}/cerrar` y
 * `POST /{id}/anular` (espejo de `OrdenDeCompraBorrador`) — header + items, SIN cobertura (esa
 * derivación solo la trae `GET /{id}`, `dto-contract-honesty`: un campo que este camino de
 * escritura no puede llenar honestamente no viaja acá). */
export type OrdenDeCompraBorrador = {
  id: number
  idProveedor: number
  idPuntoVenta: number
  numero: number | null
  fechaEmision: string
  fechaEnvio: string | null
  fechaEsperada: string | null
  fechaCierre: string | null
  idEmpleadoCierre: number | null
  observaciones: string | null
  estado: EstadoOrdenCompra
  items: ItemDeOrden[]
}

/** Cobertura POR ARTÍCULO (nunca por línea — espejo de `CoberturaDeArticulo`). `pedida` puede ser
 * `0` (recibido-no-pedido); `pendiente` nunca es negativa (`Math.Max(pedida - recibida, 0)`).
 * `costoEstimado`/`costoReal`/`desvio` son `null` cuando no hay dato comparable — JAMÁS `0`
 * (spec ordenes-de-compra: "no comparable, never zero"). `desvio` es un PORCENTAJE ya redondeado
 * (`(costoReal - costoEstimado) / costoEstimado * 100`), listo para renderizar con signo. */
export type CoberturaDeArticulo = {
  idArticulo: number
  pedida: number
  recibida: number
  pendiente: number
  costoEstimado: number | null
  costoReal: number | null
  desvio: number | null
}

/** Detalle de `GET /api/ordenes-compra/{id}` (espejo de `OrdenDeCompraDetalle`). `estado` es
 * SIEMPRE la columna proyectada server-side — esta pantalla nunca la re-deriva. `totalEstimado`/
 * `totalReal`/`desvioTotal` son `null` cuando ningún artículo aporta ese lado comparable.
 * `comprobantesLigados` son ids de TODOS los comprobantes ligados (cualquier estado). */
export type OrdenDeCompraDetalle = {
  id: number
  idProveedor: number
  idPuntoVenta: number
  numero: number | null
  fechaEmision: string
  fechaEnvio: string | null
  fechaEsperada: string | null
  fechaCierre: string | null
  cierreManual: boolean
  observaciones: string | null
  estado: EstadoOrdenCompra
  items: ItemDeOrden[]
  cobertura: CoberturaDeArticulo[]
  totalEstimado: number | null
  totalReal: number | null
  desvioTotal: number | null
  comprobantesLigados: number[]
}

/** Fila de `GET /api/ordenes-compra` — shape reducido, sin cobertura ni desvío (espejo de
 * `OrdenDeCompraListada`). */
export type OrdenDeCompraListada = {
  id: number
  idProveedor: number
  idPuntoVenta: number
  numero: number | null
  fechaEmision: string
  fechaEsperada: string | null
  estado: EstadoOrdenCompra
}

/** Página de `GET /api/ordenes-compra` (espejo de `PaginaDeOrdenesDeCompra`). */
export type PaginaDeOrdenesDeCompra = { items: OrdenDeCompraListada[]; total: number; pagina: number; tamanio: number }

// --- Presupuestos (stage-17-presupuestos-y-remitos, Slice 7) — espejo de
// `Ways.Application.Ventas.ContratosDePresupuesto` / `Ways.Domain.Ventas.EstadoPresupuesto` -------

/** Espejo de `EstadoPresupuesto` — el orden de los miembros ES el orden de ciclo de vida
 * (design.md: "Declaration order = lifecycle = C# member order"). */
export type EstadoPresupuesto = 'Borrador' | 'Enviado' | 'Convertido' | 'Anulado'

/** Una línea del cuerpo de `POST`/`PUT /api/presupuestos` (espejo de `LineaDePresupuesto`). Sin
 * dinero — el precio lo resuelve el motor al guardar el borrador, igual que el checkout (design
 * decisión 2). */
export type LineaDePresupuesto = { idArticulo: number; cantidad: number }

/** Cuerpo de `POST /api/presupuestos` (crea un borrador) y `PUT /api/presupuestos/{id}`
 * (replace-set completo del header + los items — espejo de `SolicitudDePresupuesto`).
 * `idCliente` omitido resuelve a Consumidor Final, mismo criterio que `SolicitudDeVenta`. */
export type SolicitudDePresupuesto = {
  idPuntoVenta: number
  idCliente: number | null
  observaciones: string | null
  lineas: LineaDePresupuesto[]
}

/** Cuerpo de `POST /api/presupuestos/{id}/enviar` (espejo de `SolicitudDeEnvio`) —
 * `vencimiento` es un `DateOnly`/`<input type="date">`, sin offset horario. */
export type SolicitudDeEnvio = { vencimiento: string }

/** Un item ya persistido — `orden` es el valor server-asignado; el resto es la procedencia de
 * precio congelada al guardar el borrador (espejo de `ItemDePresupuesto`). */
export type ItemDePresupuesto = {
  orden: number
  idArticulo: number
  descripcion: string
  cantidad: number
  precioUnitario: number
  descuento: number
  total: number
  idListaPrecio: number
  idOferta: number | null
  idAlicuotaIva: number
  porcentajeIva: number
}

/** Respuesta de `POST/PUT /api/presupuestos`, `POST /{id}/enviar`, `POST /{id}/anular` y
 * `GET /{id}` — espejo de `PresupuestoDetalle`. `vencido`/`convertible` son DERIVADOS en cada
 * lectura (`ReglaDePresupuestos`, zona horaria del punto de venta), nunca columnas propias de
 * este shape. */
export type PresupuestoDetalle = {
  id: number
  idPuntoVenta: number
  idCliente: number
  idEmpleado: number
  numero: number | null
  numeroFormateado: string | null
  fechaEmision: string
  fechaEnvio: string | null
  vencimiento: string | null
  vencido: boolean
  convertible: boolean
  zonaId: string
  observaciones: string | null
  subtotal: number
  descuentoTotal: number
  total: number
  estado: EstadoPresupuesto
  idComprobanteVenta: number | null
  items: ItemDePresupuesto[]
}

/** `GET /{id}/para-venta` — espejo de `PresupuestoParaVenta`. Lectura PARA MOSTRAR, jamás un
 * `SolicitudDeVenta` pre-armado (`dto-contract-honesty` regla 1): un shape que el POS pudiera
 * postear tal cual haría creíble al carrito para el dinero, exactamente lo que la congelación de
 * precio (proposal decisión 4) existe para impedir. */
export type PresupuestoParaVenta = {
  idPresupuesto: number
  numero: number | null
  idPuntoVenta: number
  idCliente: number
  vencimiento: string | null
  vencido: boolean
  convertible: boolean
  subtotal: number
  descuentoTotal: number
  total: number
  items: ItemDePresupuesto[]
}

/** Fila de `GET /api/presupuestos` — espejo de `PresupuestoListado`. `vencido`/`convertible`
 * viajan acá también: se resuelve una zona por punto de venta DISTINTO de la página completa
 * (design decisión 16), no una consulta agregada por fila. */
export type PresupuestoListado = {
  id: number
  idPuntoVenta: number
  idCliente: number
  numero: number | null
  numeroFormateado: string | null
  fechaEmision: string
  vencimiento: string | null
  vencido: boolean
  convertible: boolean
  total: number
  estado: EstadoPresupuesto
}

/** Página de `GET /api/presupuestos` (espejo de `PaginaDePresupuestos`). */
export type PaginaDePresupuestos = { items: PresupuestoListado[]; total: number; pagina: number; tamanio: number }

// --- Remitos (stage-17-presupuestos-y-remitos, Slice 8) — espejo de
// `Ways.Application.Ventas.ContratosDeRemito` / `Ways.Domain.Ventas.EstadoRemito` -----------------

/** Espejo de `EstadoRemito` — el orden de los miembros ES el orden de ciclo de vida (mismo
 * criterio que `EstadoPresupuesto`). */
export type EstadoRemito = 'Borrador' | 'Emitido' | 'Facturado' | 'Anulado'

/** Una línea del cuerpo de `POST`/`PUT /api/remitos` (espejo de `LineaDeRemito`). `idLote` es la
 * elección EXPLÍCITA del cliente antes de `emitir` (a diferencia de `LineaDePresupuesto`) — `null`
 * es el camino feliz de cero tecleo, el servidor corre FEFO solo. */
export type LineaDeRemito = { idArticulo: number; cantidad: number; idLote: number | null }

/** Cuerpo de `POST /api/remitos` (crea un borrador) y `PUT /api/remitos/{id}` (replace-set
 * completo del header + los items — espejo de `SolicitudDeRemito`). `idCliente` omitido resuelve a
 * Consumidor Final, mismo criterio que `SolicitudDePresupuesto`. */
export type SolicitudDeRemito = {
  idPuntoVenta: number
  idCliente: number | null
  direccionEntrega: string | null
  observaciones: string | null
  lineas: LineaDeRemito[]
}

/** Un item ya persistido — espejo de `ItemDeRemito`. `costoUnitario`/`costoEsEstimado`/`idLote`
 * quedan `null`/`false` mientras el remito está en `borrador` (salvo `idLote`, que el cliente
 * puede fijar de antemano) y recién se congelan al `emitir`. */
export type ItemDeRemito = {
  orden: number
  idArticulo: number
  descripcion: string
  cantidad: number
  precioUnitario: number
  descuento: number
  total: number
  idListaPrecio: number
  idOferta: number | null
  idAlicuotaIva: number
  porcentajeIva: number
  costoUnitario: number | null
  costoEsEstimado: boolean
  idLote: number | null
}

/** Respuesta de `POST/PUT /api/remitos`, `POST /{id}/emitir`, `POST /{id}/anular` y `GET /{id}` —
 * espejo de `RemitoDetalle`. Sin `vencido`/`convertible`: un remito no expira. */
export type RemitoDetalle = {
  id: number
  idPuntoVenta: number
  idCliente: number
  idEmpleado: number
  numero: number | null
  numeroFormateado: string | null
  fechaEmision: string
  fechaSalida: string | null
  direccionEntrega: string | null
  observaciones: string | null
  subtotal: number
  descuentoTotal: number
  total: number
  estado: EstadoRemito
  idComprobanteVenta: number | null
  items: ItemDeRemito[]
}

/** Fila de `GET /api/remitos` — espejo de `RemitoListado`. */
export type RemitoListado = {
  id: number
  idPuntoVenta: number
  idCliente: number
  numero: number | null
  numeroFormateado: string | null
  fechaEmision: string
  total: number
  estado: EstadoRemito
  idComprobanteVenta: number | null
}

/** Página de `GET /api/remitos` (espejo de `PaginaDeRemitos`). */
export type PaginaDeRemitos = { items: RemitoListado[]; total: number; pagina: number; tamanio: number }

/** Cuerpo de `POST /api/remitos/facturacion` (espejo de `SolicitudDeFacturacionDeRemitos`). Sin
 * `idCliente` (`dto-contract-honesty` regla 1): el cliente se DERIVA de los remitos mismos — un
 * valor en conflicto no tendría destino real. `pagos` mismo shape que el checkout. */
export type SolicitudDeFacturacionDeRemitos = {
  idPuntoVenta: number
  idsRemito: number[]
  pagos: PagoDeVenta[]
  observaciones: string | null
}

/** Neto gravado e IVA de una alícuota dentro de una fila del libro IVA — espejo de
 * `AlicuotaDeLibroIva`. */
export type AlicuotaDeLibroIva = { porcentaje: number; neto: number; iva: number }

/** Un comprobante del libro IVA (espejo de `FilaDeLibroIva`). `total` lleva signo (una nota de
 * crédito resta). `diferencia` es `total − componentes`: distinta de cero cuando los importes del
 * comprobante no cierran. En ventas las percepciones vienen siempre en cero. */
export type FilaDeLibroIva = {
  fecha: string
  tipoComprobante: string
  numero: string
  contraparte: string
  documento: string | null
  alicuotas: AlicuotaDeLibroIva[]
  noGravado: number
  exento: number
  percepcionIva: number
  percepcionIibb: number
  total: number
  diferencia: number
  /** Códigos de aviso de la fila (`anulado_sin_nc`, `alicuota_sin_clasificar`, `sin_numero_fiscal`). */
  advertencias: string[]
  netoGravado: number
  ivaTotal: number
}

export type TotalesDeLibroIva = {
  porAlicuota: AlicuotaDeLibroIva[]
  noGravado: number
  exento: number
  percepcionIva: number
  percepcionIibb: number
  total: number
  diferencia: number
  netoGravado: number
  ivaTotal: number
}

/** Respuesta de `GET /api/reportes/libro-iva-compras` y `libro-iva-ventas` — espejo de `LibroIva`.
 * `idEmpresa` nulo = todas las empresas del tenant. */
export type LibroIva = {
  desde: string
  hasta: string
  idEmpresa: number | null
  zonaHoraria: string | null
  filas: FilaDeLibroIva[]
  totales: TotalesDeLibroIva
}
