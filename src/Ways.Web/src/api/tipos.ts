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
 * documentado — el editor solo acepta estas claves, igual que el backend. */
export const PARAMETROS_CONOCIDOS: {
  clave: string
  etiqueta: string
  tipo: 'entero' | 'decimal'
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

// --- POS: checkout (stage-5-pos-ventas, Slice 6 → wireado en Slice 7) ---
// Espejo pinneado por design.md (Checkout Contract) del futuro `SolicitudDeVenta`/comprobante
// emitido de `POST /api/ventas` — ese endpoint vive en la Slice 4 (todavía en review, no
// mergeada a main). Estos tipos solo sostienen los mappers puros de `ventas.ts`; ningún fetch
// de esta slice los usa. Slice 7 debe confirmar el shape contra el DTO real antes de invocar el
// endpoint — ver el comentario de `ComprobanteVenta`.

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

export type ItemComprobanteVenta = {
  idArticulo: number | null
  descripcion: string
  codigoBarra: string | null
  cantidad: number
  precioUnitario: number
  descuento: number
  total: number
}

/** TODO(slice-7): shape inferido del Checkout Orchestration Contract + Table Shapes A de
 * design.md — no existe todavía un DTO real de `POST /api/ventas` en main (Slice 4 en review).
 * Confirmar/ajustar contra ese contrato definitivo antes de wirear el fetch. */
export type ComprobanteVenta = {
  id: number
  numeroVisible: string
  estado: 'emitido' | 'anulado'
  subtotal: number
  descuentoTotal: number
  total: number
  items: ItemComprobanteVenta[]
}
