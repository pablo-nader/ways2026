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

export type UsuarioListado = {
  id: number
  usuario: string
  mail: string
  rolId: number
  rol: string
  estado: EstadoUsuario
  ultimaConexion: string | null
  createdAt: string
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

export type CrearUsuario = {
  usuario: string
  mail: string
  rolId: number
  password: string
  estado: EstadoUsuario
}

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

/** Espejo de `Politicas.LecturaDeReportes` (stage-10-agregacion-dashboard, Slice 1): supervisor o
 * admin ven `Tablero` — vendedor y root quedan afuera, mismo criterio que
 * `puedeSupervisarCuentaCorriente`. Puramente cosmético: el servidor vuelve a exigir la misma
 * policy en cada endpoint de `/api/reportes`. */
export function puedeVerReportes(rolId: number) {
  return rolId === ROL.Supervisor || rolId === ROL.Admin
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
}

// --- Parámetros operativos (ADR-13) ---

export type ParametroListado = { id: number; clave: string; valor: string; idPuntoVenta: number | null }
export type ParametroAlta = { clave: string; valor: string; idPuntoVenta: number | null }
export type ParametroResuelto = { clave: string; valor: string }

/** Espejo de `ParametroConocido` (Ways.Domain.Catalogos): clave, tipo declarado y default
 * documentado — el editor solo acepta estas claves, igual que el backend. `zona_horaria` es
 * el primer tipo `'texto'`: el backend lo guarda como string JSON-quoteado (stage-10). */
export const PARAMETROS_CONOCIDOS: {
  clave: string
  etiqueta: string
  tipo: 'entero' | 'decimal' | 'texto'
  porDefecto: string
}[] = [
  { clave: 'tolerancia_pago', etiqueta: 'Tolerancia de pago ($)', tipo: 'decimal', porDefecto: '10' },
  { clave: 'vuelto_maximo', etiqueta: 'Vuelto máximo ($)', tipo: 'decimal', porDefecto: '20' },
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

export type TenantListado = {
  id: number
  nombre: string
  estado: EstadoTenant
  createdAt: string
}

export type TenantEdicion = { nombre: string }

export type EmpresaListado = {
  id: number
  idTenant: number
  razonSocial: string
  nombreFantasia: string | null
  cuit: string | null
}

export type EmpresaEdicion = { razonSocial: string; nombreFantasia: string | null; cuit: string | null }

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
}

/** `idEmpresa` no es editable acá: es estructural, no descriptivo (misma razón que en el
 * backend, `Ways.Application.Organizacion.PuntoVentaEdicion`). */
export type PuntoVentaEdicion = {
  nombre: string
  domicilio: string | null
  horario: string | null
  whatsapp: string | null
  instagram: string | null
  facebook: string | null
  web: string | null
}

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
}

export type EdicionProveedor = AltaProveedor

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
}

/** Sin `codigoInterno`: no es editable por este ABM (valor asignado en el alta, mismo criterio
 * que `ClienteListado.numero`). */
export type EdicionArticulo = Omit<AltaArticulo, 'codigoInterno'>

export type CodigoBarraListado = { id: number; idArticulo: number; codigo: string; activo: boolean }
export type AltaCodigoBarra = { codigo: string }

/** `precioSugerido: null` cuando no hay costo base ni margen suficientes para calcular una
 * sugerencia — nunca se aplica sola (spec: Margin-Based Price Suggestion, "requires explicit
 * apply"). */
export type SugerenciaDePrecio = { precioSugerido: number | null }

// --- Precios (history engine, stage-3-articulos-y-precios) ---

export type AltaPrecio = { idListaPrecio: number; precio: number; confirmarReemplazo?: boolean }
export type ProgramarPrecio = {
  idListaPrecio: number
  precio: number
  vigenteDesde: string
  confirmarReemplazo?: boolean
}
export type PrecioVigente = { idArticulo: number; idListaPrecio: number; precio: number | null; fecha: string }
export type HistorialDePrecio = { id: number; precio: number; vigenteDesde: string; vigenteHasta: string | null }

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
 * `codigoBarra` es `null` cuando la entrada resolvió por `codigoInterno`. */
export type ArticuloEscaneado = {
  idArticulo: number
  codigoInterno: string
  nombre: string
  codigoBarra: string | null
  cantidad: number
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
 * esperado (spec: No Request Shape Accepts A Total). */
export type SolicitudDeCierre = { conteos: ConteoDeclarado[]; observaciones: string | null }

/** Una fila ya persistida de `arqueos_turno` — `diferencia` la calcula la columna `GENERATED
 * ALWAYS` del servidor (design decisión 6); positivo = faltante. */
export type LineaDeArqueoResumen = {
  idMedioPago: number
  importeEsperado: number
  importeDeclarado: number
  diferencia: number
}

/** Respuesta de `POST /api/caja/turnos/{id}/cierre` y de `GET /api/caja/turnos/{id}` (el
 * payload del comprobante Z) — mismos campos planos que `TurnoResumen` más `arqueos`. */
export type TurnoConArqueos = TurnoResumen & { arqueos: LineaDeArqueoResumen[] }

// --- POS: checkout (stage-5-pos-ventas, Slice 6 → wireado en Slice 7) ---
// Espejo de `Ways.Application.Ventas.Contratos` (confirmado contra el DTO real de
// `POST /api/ventas`, mergeado en Slice 4) — usado por `ventas.ts` (mappers) y `Pos.tsx`
// (wireado en Slice 7).

export type LineaDeVenta = { idArticulo: number; cantidad: number; codigoBarra: string | null }
export type PagoDeVenta = { idMedioPago: number; importe: number; referencia: string | null; vuelto: number }

export type SolicitudDeVenta = {
  idPuntoVenta: number
  idCliente: number
  codigoTipoComprobante: 'TX' | 'NCX'
  idComprobanteAsociado: number | null
  lineas: LineaDeVenta[]
  pagos: PagoDeVenta[]
  direccionEntrega: string | null
  observaciones: string | null
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
}

/** Pago ya emitido — espejo de `PagoEmitido`. */
export type PagoEmitido = { idMedioPago: number; importe: number; referencia: string | null; vuelto: number }

/** Respuesta de `POST /api/ventas` (checkout) y `GET /api/ventas/{id}` (reimpresión) — espejo
 * de `ComprobanteEmitido`. `numeroVisible` ya viene formateado `PPPP-NNNNNNNN`. */
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
  total: number
  direccionEntrega: string | null
  observaciones: string | null
  items: ItemEmitido[]
  pagos: PagoEmitido[]
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

/** Una línea del cuerpo de `POST`/`PUT /api/compras` (espejo de `LineaDeCompraSolicitada`). */
export type LineaDeCompraSolicitada = {
  idArticulo: number
  descripcion: string
  unidades: number
  bultos: number | null
  unidadesPorBulto: number | null
  costoUnitario: number
  descuento: number
  idAlicuotaIva: number
  actualizaCosto: boolean
}

/** Cuerpo de `POST /api/compras` (crea un borrador) y `PUT /api/compras/{id}` (replace-set
 * completo del header + los items — espejo de `SolicitudDeCompra`). */
export type SolicitudDeCompra = {
  idProveedor: number
  idTipoComprobante: number
  idPuntoVenta: number
  numeroExterno: string | null
  fechaComprobante: string | null
  observaciones: string | null
  items: LineaDeCompraSolicitada[]
}

/** Un item ya persistido, con su `precioSugerido` (espejo de `ItemDeCompra`). */
export type ItemDeCompra = {
  orden: number
  idArticulo: number
  descripcion: string
  cantidad: number
  bultos: number | null
  unidadesPorBulto: number | null
  costoUnitario: number
  descuento: number
  idAlicuotaIva: number
  porcentajeIva: number
  total: number
  actualizaCosto: boolean
  precioSugerido: number | null
}

/** Detalle completo de una compra (espejo de `CompraDetalle`). */
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
}

/** Fila de `GET /api/compras` — shape reducido (espejo de `CompraListada`). */
export type CompraListada = {
  id: number
  idProveedor: number
  idTipoComprobante: number
  numeroExterno: string | null
  estado: EstadoCompra
  fechaRecepcion: string | null
  total: number
}

/** Página de resultados de `GET /api/compras` (espejo de `PaginaDeCompras`). */
export type PaginaDeCompras = { items: CompraListada[]; total: number; pagina: number; tamanio: number }

/** Respuesta de `POST /api/compras/{id}/anular` — `gastosLigados` es la regla invertida
 * (design decisión 6): la anulación NUNCA bloquea por gastos ligados, solo REPORTA cuántos pagos
 * quedaron colgados de la compra anulada (espejo de `ResultadoAnulacion`). */
export type ResultadoAnulacion = { compra: CompraDetalle; gastosLigados: number }

/** Cuerpo de `POST /api/compras/{id}/precios` (espejo de `SolicitudDeAplicarPrecios`). */
export type SolicitudDeAplicarPrecios = { idListaPrecio: number; confirmarReemplazo: boolean }

/** Resultado por línea de aplicar `precioSugerido` — partial success es el contrato honesto
 * (espejo de `ResultadoAplicarPrecio`). */
export type ResultadoAplicarPrecio = { idArticulo: number; aplicado: boolean; precio: number | null; error: string | null }

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
 * mentir en cualquiera de las dos direcciones). */
export type ResultadoConteo = {
  idPuntoVenta: number
  idArticulo: number
  cantidad: number
  cantidadAnterior: number
  delta: number
  movimientoRegistrado: boolean
}

/** Una línea del cuerpo de `POST /api/stock/transferencias` — `cantidad` siempre positiva
 * (espejo de `LineaDeTransferencia`). */
export type LineaDeTransferencia = { idArticulo: number; cantidad: number }

/** Cuerpo de `POST /api/stock/transferencias` (espejo de `SolicitudDeTransferencia`).
 * `observaciones` es obligatoria, mismo criterio que el ajuste manual. */
export type SolicitudDeTransferencia = {
  idPuntoVentaOrigen: number
  idPuntoVentaDestino: number
  observaciones: string
  lineas: LineaDeTransferencia[]
}

/** El stock resultante de un artículo en AMBOS puntos de venta tras la transacción (espejo de
 * `LineaTransferida`). */
export type LineaTransferida = { idArticulo: number; cantidadOrigen: number; cantidadDestino: number }

/** Respuesta de `POST /api/stock/transferencias` (espejo de `ResultadoTransferencia`). */
export type ResultadoTransferencia = {
  idPuntoVentaOrigen: number
  idPuntoVentaDestino: number
  lineas: LineaTransferida[]
}

/** Cuerpo de `POST /api/stock/conteos` — `contada` es el TOTAL físicamente contado, nunca un
 * delta (espejo de `SolicitudDeConteo`; spec: conteo-de-inventario / Conteo Input Is The Counted
 * Total, Never A Delta). */
export type SolicitudDeConteo = { idPuntoVenta: number; idArticulo: number; contada: number; observaciones: string }

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
