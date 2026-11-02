/**
 * Cliente y reglas puras de la conciliación de Mercado Pago (`/api/mercadopago/conciliaciones`, `cobros-sin-venta` y
 * `pagos-sin-verificar`). Subir el reporte y confirmar una fila son del Admin; el resto lo lee también el Supervisor.
 * La subida es multipart (campo `archivo`) y exige el encabezado `X-Ways-Origen: web`: sin él el servidor responde
 * 403 `solicitud_no_permitida`.
 */
import { api, ErrorApi, ErrorDeRed } from './cliente'
import { formatearImporte } from '../formato/importes'
import { fechaHoraATexto } from '../formato/fechas'

export type ResultadoDeFila =
  | 'conciliado'
  | 'duplicada'
  | 'ya_conciliado'
  | 'sin_venta'
  | 'importe_distinto'
  | 'sin_cobro_en_ways'
  | 'devolucion'
  | 'contracargo'
  | 'retiro'
  | 'invalida'

export type ResumenDeResultado = {
  resultado: string
  cantidad: number
  importe: number
  comision: number
  neto: number
}

export type ConciliacionListado = {
  id: number
  idEmpresa: number
  nombreArchivo: string | null
  fechaDesde: string | null
  fechaHasta: string | null
  idUsuario: number
  usuario: string | null
  subidaEl: string
  cantidadFilas: number
  cantidadInvalidas: number
  resumen: ResumenDeResultado[]
}

export type FilaInvalida = { numeroLinea: number; motivo: string }

export type ConciliacionImportada = {
  conciliacion: ConciliacionListado
  invalidas: FilaInvalida[]
  invalidasTruncado: boolean
}

export type CandidatoDeConfirmacion = {
  idCobro: number
  idComprobanteVenta: number
  numeroVisible: string
  fecha: string
  importe: number
  referencia: string | null
}

export type FilaDeConciliacion = {
  id: number
  numeroLinea: number
  resultado: string
  sourceId: string | null
  externalReference: string | null
  tipoTransaccion: string | null
  importe: number | null
  comision: number | null
  neto: number | null
  fechaTransaccion: string | null
  fechaLiberacion: string | null
  externalPosId: string | null
  motivo: string | null
  idCobroMercadoPago: number | null
  idCobro: number | null
  candidatos: CandidatoDeConfirmacion[]
  aplicadaAlCobro: boolean
  comisionGuardada: number | null
  netoGuardado: number | null
}

export type DetalleDeConciliacion = {
  conciliacion: ConciliacionListado
  filas: FilaDeConciliacion[]
  total: number
  pagina: number
  tamano: number
}

export type CobroSinVenta = {
  idCobro: number
  idPuntoVenta: number
  idMedioPago: number
  numeroPreasignado: number
  importe: number
  importePagado: number | null
  idPagoMercadoPago: string | null
  idOrdenMercadoPago: string | null
  fechaAprobacion: string | null
  fechaApartado: string | null
  pagoInformadoSinDatos: boolean
}

export type PagoSinVerificar = {
  idPago: number
  idComprobanteVenta: number
  numeroVisible: string
  idPuntoVenta: number
  fecha: string
  idMedioPago: number
  importe: number
  referencia: string | null
  idEmpleado: number
  empleado: string | null
  anulada: boolean
  idsCobroEnConflicto: number[] | null
}

export type PagosSinVerificar = { items: PagoSinVerificar[]; truncado: boolean }

export type ConsultaDeFilas = { resultado: string | null; pagina: number; tamano: number }

export type TipoDePropuesta = 'diferencia_comision' | 'retiro'

export type EstadoDePropuesta = 'pendiente' | 'aplicada' | 'descartada'

export type LineaDeDiferenciaDeComision = {
  idCobroMercadoPago: number
  idCobro: number
  idTurnoCaja: number
  idMedioPago: number
  idCuentaTesoreria: number
  importe: number
  porcentajeEstimado: number
  comisionEstimada: number
  comisionReal: number
  diferencia: number
}

/** Los dos conteos "sin" son de TODA la conciliación, no de la cuenta de la propuesta. */
export type DetalleDeDiferenciaDeComision = {
  cantidadDeCobros: number
  comisionRealTotal: number
  comisionEstimadaTotal: number
  cobrosSinCierre: number
  cobrosSinComisionReal: number
  lineas: LineaDeDiferenciaDeComision[]
  lineasTruncado: boolean
}

/**
 * Un movimiento de tesorería que la conciliación propone. `importe` es, en una diferencia de comisión, el efecto con signo sobre la
 * cuenta de origen (negativo si Mercado Pago cobró más de lo que el cierre descontó) y, en un retiro, el monto positivo que sale de ella.
 * `idCuentaOrigen` es nulo en un retiro de una empresa sin conexión con Mercado Pago: no se puede aplicar.
 */
export type PropuestaDeTesoreria = {
  id: number
  tipo: TipoDePropuesta
  estado: EstadoDePropuesta
  importe: number
  idCuentaOrigen: number | null
  nombreCuentaOrigen: string | null
  idCuentaDestino: number | null
  nombreCuentaDestino: string | null
  numeroLinea: number | null
  sourceId: string | null
  fechaTransaccion: string | null
  detalle: DetalleDeDiferenciaDeComision | null
  idMovimientoCuenta: number | null
  idMovimientoCuentaDestino: number | null
  resueltaEl: string | null
  resueltaPor: string | null
}

export type AccionDePropuesta = 'aplicar' | 'descartar'

export const ORIGEN_WEB = 'web'
export const ENCABEZADO_DE_ORIGEN = 'X-Ways-Origen'
export const TAMANO_MAXIMO_DE_ARCHIVO = 10 * 1024 * 1024
export const TAMANO_DE_PAGINA = 100

const RUTA = '/mercadopago/conciliaciones'

function aQuery(parametros: Record<string, string | number | null | undefined>): string {
  const consulta = new URLSearchParams()
  for (const [clave, valor] of Object.entries(parametros)) {
    if (valor !== null && valor !== undefined && valor !== '') consulta.set(clave, String(valor))
  }
  return `?${consulta.toString()}`
}

export const clienteDeConciliacionMercadoPago = {
  subir: (idEmpresa: number, archivo: File) => {
    const formulario = new FormData()
    formulario.append('archivo', archivo, archivo.name)
    return api.postFormulario<ConciliacionImportada>(`${RUTA}${aQuery({ idEmpresa })}`, formulario, { [ENCABEZADO_DE_ORIGEN]: ORIGEN_WEB })
  },
  listar: (idEmpresa: number) => api.get<ConciliacionListado[]>(`${RUTA}${aQuery({ idEmpresa })}`),
  detalle: (idConciliacion: number, consulta: ConsultaDeFilas) =>
    api.get<DetalleDeConciliacion>(
      `${RUTA}/${idConciliacion}${aQuery({ resultado: consulta.resultado, pagina: consulta.pagina, tamano: consulta.tamano })}`,
    ),
  confirmar: (idConciliacion: number, idFila: number, idCobro: number) =>
    api.post<void>(`${RUTA}/${idConciliacion}/filas/${idFila}/confirmar`, { idCobro }),
  propuestas: (idConciliacion: number) => api.get<PropuestaDeTesoreria[]>(`${RUTA}/${idConciliacion}/propuestas`),
  aplicarPropuesta: (idConciliacion: number, idPropuesta: number, idCuentaDestino: number | null) =>
    api.post<PropuestaDeTesoreria>(`${RUTA}/${idConciliacion}/propuestas/${idPropuesta}/aplicar`, { idCuentaDestino }),
  descartarPropuesta: (idConciliacion: number, idPropuesta: number) =>
    api.post<PropuestaDeTesoreria>(`${RUTA}/${idConciliacion}/propuestas/${idPropuesta}/descartar`, {}),
  cobrosSinVenta: (idEmpresa: number) => api.get<CobroSinVenta[]>(`/mercadopago/cobros-sin-venta${aQuery({ idEmpresa })}`),
  pagosSinVerificar: (idEmpresa: number, desde: string, hasta: string) =>
    api.get<PagosSinVerificar>(`/mercadopago/pagos-sin-verificar${aQuery({ idEmpresa, desde, hasta })}`),
}

const ETIQUETA_DE_RESULTADO: Record<string, string> = {
  conciliado: 'Conciliado',
  duplicada: 'Duplicada en el archivo',
  ya_conciliado: 'Ya conciliado antes',
  sin_venta: 'Cobro sin venta',
  importe_distinto: 'Importe distinto',
  sin_cobro_en_ways: 'Sin cobro en Ways',
  devolucion: 'Devolución',
  contracargo: 'Contracargo',
  retiro: 'Retiro',
  invalida: 'Fila inválida',
}

/** Orden en que se ofrece el filtro por resultado y se listan los resúmenes. */
export const RESULTADOS_EN_ORDEN: readonly ResultadoDeFila[] = [
  'conciliado',
  'sin_venta',
  'importe_distinto',
  'sin_cobro_en_ways',
  'devolucion',
  'contracargo',
  'retiro',
  'duplicada',
  'ya_conciliado',
  'invalida',
]

/** Un resultado que el cliente no conoce se muestra tal cual en vez de ocultarse. */
export function etiquetaDeResultado(resultado: string): string {
  return ETIQUETA_DE_RESULTADO[resultado] ?? resultado
}

/** Clase de la insignia: verde lo resuelto, ámbar lo que pide mirar, rojo lo que no se pudo leer, gris el resto. */
export function claseDeResultado(resultado: string): string {
  switch (resultado) {
    case 'conciliado':
      return 'text-bg-success'
    case 'sin_venta':
    case 'importe_distinto':
    case 'sin_cobro_en_ways':
    case 'contracargo':
      return 'text-bg-warning'
    case 'invalida':
      return 'text-bg-danger'
    default:
      return 'text-bg-secondary'
  }
}

export type TotalesDeResumen = { cantidad: number; importe: number; comision: number; neto: number }

/** Suma el resumen tal como lo informa el servidor: las filas `duplicada` y `ya_conciliado` se cuentan pero llegan sin importes. */
export function totalesDelResumen(resumen: readonly ResumenDeResultado[]): TotalesDeResumen {
  return resumen.reduce<TotalesDeResumen>(
    (acumulado, r) => ({
      cantidad: acumulado.cantidad + r.cantidad,
      importe: acumulado.importe + r.importe,
      comision: acumulado.comision + r.comision,
      neto: acumulado.neto + r.neto,
    }),
    { cantidad: 0, importe: 0, comision: 0, neto: 0 },
  )
}

/** Los resultados del resumen en el orden de `RESULTADOS_EN_ORDEN`; los desconocidos van al final. */
export function resumenOrdenado(resumen: readonly ResumenDeResultado[]): ResumenDeResultado[] {
  const posicion = (resultado: string) => {
    const indice = RESULTADOS_EN_ORDEN.indexOf(resultado as ResultadoDeFila)
    return indice === -1 ? RESULTADOS_EN_ORDEN.length : indice
  }
  return [...resumen].sort((a, b) => posicion(a.resultado) - posicion(b.resultado))
}

export function textoDeImporte(valor: number | null | undefined): string {
  return formatearImporte(valor, { simbolo: true })
}

export function textoDeFecha(valor: string | null | undefined): string {
  return valor ? fechaHoraATexto(valor) : '—'
}

/** `desde – hasta` del período leído del archivo; `—` si el reporte no trajo ninguna fecha. */
export function textoDePeriodo(desde: string | null, hasta: string | null): string {
  if (!desde && !hasta) return '—'
  return `${textoDeFecha(desde)} – ${textoDeFecha(hasta)}`
}

export type ValidacionDeArchivo = { valido: true } | { valido: false; mensaje: string }

/** Chequeo previo a subir: el servidor repite estos límites, pero acá se evita mandar 10 MB para recibir el rechazo. */
export function validarArchivo(archivo: Pick<File, 'size' | 'name'> | null): ValidacionDeArchivo {
  if (archivo === null) return { valido: false, mensaje: 'Elegí el archivo CSV del reporte.' }
  if (archivo.size === 0) return { valido: false, mensaje: 'El archivo está vacío.' }
  if (archivo.size > TAMANO_MAXIMO_DE_ARCHIVO) {
    return { valido: false, mensaje: 'El archivo supera los 10 MB. Descargá el reporte por períodos más cortos.' }
  }
  return { valido: true }
}

export function totalDePaginas(total: number, tamano: number): number {
  return tamano > 0 ? Math.max(1, Math.ceil(total / tamano)) : 1
}

/** `desde-hasta` de las filas que muestra una página (`0-0` si no hay). */
export function rangoDeFilas(total: number, pagina: number, tamano: number): { desde: number; hasta: number } {
  if (total <= 0) return { desde: 0, hasta: 0 }
  const desde = (pagina - 1) * tamano + 1
  return { desde, hasta: Math.min(total, pagina * tamano) }
}

/** Una fila se confirma solo si no tiene cobro en Ways, nadie la confirmó y hay al menos un pago candidato. */
export function esFilaConfirmable(fila: Pick<FilaDeConciliacion, 'resultado' | 'idCobro' | 'candidatos'>): boolean {
  return fila.resultado === 'sin_cobro_en_ways' && fila.idCobro === null && fila.candidatos.length > 0
}

export type LineaDeConfirmacion = { etiqueta: string; valor: string }

/** Lo que el diálogo de confirmación pone lado a lado: la fila del reporte y el pago de la venta. */
export function lineasDeConfirmacion(fila: FilaDeConciliacion, candidato: CandidatoDeConfirmacion): LineaDeConfirmacion[] {
  return [
    { etiqueta: 'Fila del reporte', valor: `Línea ${fila.numeroLinea}${fila.sourceId ? ` · ${fila.sourceId}` : ''}` },
    { etiqueta: 'Importe del reporte', valor: textoDeImporte(fila.importe) },
    { etiqueta: 'Fecha del reporte', valor: textoDeFecha(fila.fechaTransaccion) },
    { etiqueta: 'Venta', valor: candidato.numeroVisible },
    { etiqueta: 'Importe del pago', valor: textoDeImporte(candidato.importe) },
    { etiqueta: 'Fecha del pago', valor: textoDeFecha(candidato.fecha) },
  ]
}

/** `true` si los dos importes difieren (el servidor igual rechaza con `importe_distinto`): el diálogo lo advierte. */
export function importesDifieren(fila: Pick<FilaDeConciliacion, 'importe'>, candidato: Pick<CandidatoDeConfirmacion, 'importe'>): boolean {
  return fila.importe !== null && Math.round(fila.importe * 100) !== Math.round(candidato.importe * 100)
}

export type ContextoDeError = 'lectura' | 'subida' | 'confirmacion' | 'resolucion'

const MENSAJE_POR_CODIGO: Record<string, string> = {
  solicitud_no_permitida: 'La subida del reporte solo se acepta desde la web de Ways. Recargá la página e intentá de nuevo desde ahí.',
  conciliacion_archivo_requerido: 'Elegí el archivo CSV del reporte.',
  conciliacion_archivo_demasiado_grande: 'El archivo supera los 10 MB. Descargá el reporte por períodos más cortos.',
  conciliacion_reporte_invalido:
    'El archivo no es un reporte de transacciones de Mercado Pago válido. Revisá que sea el CSV descargado desde Mercado Pago y que conserve sus columnas.',
  conciliacion_ya_importada: 'Este archivo ya se importó para esta empresa. Buscalo en la solapa Conciliaciones.',
  conciliacion_resultado_invalido: 'El resultado elegido para filtrar no es válido.',
  conciliacion_paginacion_invalida: 'La página pedida no es válida.',
  conciliacion_pago_invalido: 'El pago elegido no es válido para confirmar esta fila.',
  conciliacion_fila_no_confirmable: 'Esta fila no se puede confirmar: solo se confirman las filas sin cobro en Ways.',
  conciliacion_fila_ya_confirmada: 'Esta fila ya fue confirmada contra un pago.',
  conciliacion_pago_no_esta_sin_verificar: 'Ese pago ya no está sin verificar.',
  conciliacion_pago_ya_verificado: 'Ese pago ya fue verificado.',
  venta_anulada: 'La venta de ese pago está anulada: no se puede confirmar contra ella.',
  importe_distinto: 'El importe del pago no coincide con el de la fila del reporte: no se puede confirmar.',
  propuesta_ya_aplicada: 'La propuesta ya se aplicó.',
  propuesta_descartada: 'La propuesta ya se descartó: no se puede aplicar.',
  propuesta_ya_descartada: 'La propuesta ya se descartó.',
  cuenta_destino_requerida: 'Elegí la cuenta que recibe el retiro.',
  cuenta_destino_no_aplica: 'Una diferencia de comisión no lleva cuenta de destino.',
  cuenta_destino_invalida: 'La cuenta de destino tiene que ser una cuenta activa de la misma empresa.',
  cuentas_iguales: 'La cuenta de destino es la misma de la que sale el retiro.',
  propuesta_sin_cuenta_origen:
    'La empresa no tiene una conexión con Mercado Pago: no hay cuenta de la que retirar. Descartá la propuesta.',
  cuenta_origen_no_disponible: 'La cuenta de Mercado Pago ya no está disponible.',
  periodo_conciliado: 'La fecha del movimiento cae en un período de tesorería ya conciliado: no se puede escribir.',
}

const MENSAJE_GENERICO: Record<ContextoDeError, string> = {
  lectura: 'No se pudo cargar la información de la conciliación.',
  subida: 'No se pudo importar el archivo.',
  confirmacion: 'No se pudo confirmar la fila.',
  resolucion: 'No se pudo resolver la propuesta.',
}

/**
 * El texto para el usuario de un fallo de la conciliación. Una caída de red en una escritura es incierta (el servidor pudo
 * haberla aplicado) y se dice así, con dónde verificar; en una lectura es solo falta de conexión.
 */
export function mensajeDeErrorDeConciliacion(error: unknown, contexto: ContextoDeError): string {
  if (error instanceof ErrorDeRed) {
    if (contexto === 'subida') {
      return 'No se pudo confirmar si el archivo se importó. Revisá la solapa Conciliaciones antes de volver a subirlo.'
    }
    if (contexto === 'confirmacion') {
      return 'No se pudo confirmar si la fila quedó confirmada. Recargá la pantalla y revisala antes de reintentar.'
    }
    if (contexto === 'resolucion') {
      return 'No se pudo confirmar si la propuesta quedó resuelta. Recargá la pantalla y revisala antes de reintentar.'
    }
    return 'Sin conexión con el servidor. Revisá tu conexión y probá de nuevo.'
  }
  if (error instanceof ErrorApi) {
    const porCodigo = MENSAJE_POR_CODIGO[error.codigo]
    if (porCodigo) return porCodigo
    if (error.estado === 403) return 'No tenés permiso para esta operación.'
    if (error.estado === 404) {
      if (contexto === 'resolucion') return 'La conciliación o la propuesta ya no existen. Recargá la pantalla.'
      return contexto === 'confirmacion'
        ? 'La conciliación, la fila o el pago ya no existen. Recargá la pantalla.'
        : 'No se encontró lo pedido. Recargá la pantalla.'
    }
    if (error.estado === 413) return MENSAJE_POR_CODIGO.conciliacion_archivo_demasiado_grande
    return error.message || MENSAJE_GENERICO[contexto]
  }
  return MENSAJE_GENERICO[contexto]
}

export const DIAS_MAXIMOS_DE_RANGO = 366

function aFechaIso(fecha: Date): string {
  const mes = String(fecha.getMonth() + 1).padStart(2, '0')
  const dia = String(fecha.getDate()).padStart(2, '0')
  return `${fecha.getFullYear()}-${mes}-${dia}`
}

/** `[hoy - (dias - 1), hoy]`: `dias` días inclusive, con `ahora` inyectable para no depender del reloj. */
export function rangoUltimosDias(dias: number, ahora: Date = new Date()): { desde: string; hasta: string } {
  const inicio = new Date(ahora)
  inicio.setDate(inicio.getDate() - (dias - 1))
  return { desde: aFechaIso(inicio), hasta: aFechaIso(ahora) }
}

/** El motivo por el que un rango no se consulta, o `null` si se puede consultar. El borde de los 366 días lo decide el servidor. */
export function motivoDeRangoInvalido(desde: string, hasta: string): string | null {
  if (desde === '' || hasta === '') return 'Elegí las fechas "Desde" y "Hasta".'
  if (desde > hasta) return 'La fecha "Desde" no puede ser posterior a "Hasta".'
  const dias = (Date.parse(hasta) - Date.parse(desde)) / 86_400_000
  if (dias > DIAS_MAXIMOS_DE_RANGO) return `El rango no puede superar los ${DIAS_MAXIMOS_DE_RANGO} días.`
  return null
}

/** La conciliación subida más recientemente; `null` si no hay ninguna. */
export function ultimaConciliacion(lista: readonly ConciliacionListado[]): ConciliacionListado | null {
  return lista.reduce<ConciliacionListado | null>(
    (ultima, actual) => (ultima === null || Date.parse(actual.subidaEl) > Date.parse(ultima.subidaEl) ? actual : ultima),
    null,
  )
}

/** `Conciliado 12 · Sin venta 1`: el conteo por resultado en una línea, en el orden de `RESULTADOS_EN_ORDEN`. */
export function textoDeResumen(resumen: readonly ResumenDeResultado[]): string {
  const conFilas = resumenOrdenado(resumen).filter((r) => r.cantidad > 0)
  return conFilas.length === 0 ? '—' : conFilas.map((r) => `${etiquetaDeResultado(r.resultado)} ${r.cantidad}`).join(' · ')
}

const ETIQUETA_DE_TIPO_DE_PROPUESTA: Record<string, string> = {
  diferencia_comision: 'Diferencia de comisión',
  retiro: 'Retiro',
}

export function etiquetaDeTipoDePropuesta(tipo: string): string {
  return ETIQUETA_DE_TIPO_DE_PROPUESTA[tipo] ?? tipo
}

const ETIQUETA_DE_ESTADO_DE_PROPUESTA: Record<string, string> = {
  pendiente: 'Pendiente',
  aplicada: 'Aplicada',
  descartada: 'Descartada',
}

export function etiquetaDeEstadoDePropuesta(estado: string): string {
  return ETIQUETA_DE_ESTADO_DE_PROPUESTA[estado] ?? estado
}

export function claseDeEstadoDePropuesta(estado: string): string {
  if (estado === 'aplicada') return 'bg-success'
  if (estado === 'descartada') return 'bg-secondary'
  return 'bg-warning text-dark'
}

/**
 * Qué hace la propuesta en la tesorería, en una frase. Una diferencia con importe negativo debita la cuenta (Mercado Pago cobró más de
 * lo que el cierre descontó); positivo, la acredita. Un retiro sale de la cuenta de origen y entra a la que se elija.
 */
export function textoDeEfecto(propuesta: Pick<PropuestaDeTesoreria, 'tipo' | 'importe' | 'nombreCuentaOrigen' | 'nombreCuentaDestino'>): string {
  const origen = propuesta.nombreCuentaOrigen ?? 'la cuenta de Mercado Pago'
  if (propuesta.tipo === 'retiro') {
    const destino = propuesta.nombreCuentaDestino ?? 'la cuenta que elijas'
    return `Pasa ${textoDeImporte(propuesta.importe)} de ${origen} a ${destino}`
  }
  return propuesta.importe < 0
    ? `Debita ${textoDeImporte(-propuesta.importe)} de ${origen}`
    : `Acredita ${textoDeImporte(propuesta.importe)} en ${origen}`
}

/** Una propuesta se puede aplicar si está pendiente y, siendo un retiro, tiene cuenta de origen (la de la conexión con Mercado Pago). */
export function sePuedeAplicar(propuesta: Pick<PropuestaDeTesoreria, 'estado' | 'tipo' | 'idCuentaOrigen'>): boolean {
  return propuesta.estado === 'pendiente' && (propuesta.tipo !== 'retiro' || propuesta.idCuentaOrigen !== null)
}

export type CuentaParaElegir = { id: number; idEmpresa: number; nombre: string; activo: boolean }

/** Las cuentas que puede recibir un retiro: activas, de la misma empresa y distintas de la de origen. */
export function cuentasDeDestino<T extends CuentaParaElegir>(cuentas: readonly T[], idEmpresa: number, idCuentaOrigen: number | null): T[] {
  return cuentas
    .filter((c) => c.activo && c.idEmpresa === idEmpresa && c.id !== idCuentaOrigen)
    .sort((a, b) => a.nombre.localeCompare(b.nombre, 'es'))
}

/** Lo que el cálculo dejó fuera de una diferencia de comisión, para avisarlo: cobros cuyo turno no acreditó el medio y cobros sin comisión real. */
export function avisosDeDetalle(detalle: Pick<DetalleDeDiferenciaDeComision, 'cobrosSinCierre' | 'cobrosSinComisionReal'>): string[] {
  const avisos: string[] = []
  if (detalle.cobrosSinCierre > 0) {
    avisos.push(`${detalle.cobrosSinCierre} cobro(s) de turnos que todavía no acreditaron el medio no entran al cálculo.`)
  }
  if (detalle.cobrosSinComisionReal > 0) {
    avisos.push(`${detalle.cobrosSinComisionReal} cobro(s) vinieron sin comisión en el reporte y no entran al cálculo.`)
  }
  return avisos
}
