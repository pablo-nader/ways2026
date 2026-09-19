/**
 * Helpers puros de "Ventas del turno" (stage-desktop-pos) — separados de `VentasDelTurno.tsx`
 * para poder testearlos sin DOM (`web-descriptor-tests`/`mutation-proof-tests`): las reglas de
 * negocio ("solo las no anuladas cuentan en el total", "cada clausula del filtro", "el monto neto
 * por medio") viven acá, no inline en el componente.
 */
import { ROL } from '../api/tipos'
import type { EstadoComprobante, MedioPagoListado, VentaDeTurnoListado } from '../api/tipos'
import { formatearImporte } from '../formato/importes'

export function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

export function formatearFechaHora(iso: string): string {
  return new Date(iso).toLocaleString('es-AR')
}

export function etiquetaDeEstadoVenta(estado: EstadoComprobante): string {
  return estado === 'Anulado' ? 'Anulada' : 'Emitida'
}

export function claseDeBadgeDeEstadoVenta(estado: EstadoComprobante): string {
  return estado === 'Anulado' ? 'bg-danger' : 'bg-success'
}

/** Cantidad y total EXCLUYENDO anuladas — mutation-proof-tests: el `filter` de acá es el
 * predicado bajo prueba. Se aplica tanto sobre el listado completo como sobre el filtrado
 * (spec: "los totales excluyen anuladas y siguen a los filtros"). */
export function totalesDeVentas(ventas: VentaDeTurnoListado[]): { cantidad: number; total: number } {
  const vigentes = ventas.filter((v) => v.estado !== 'Anulado')
  return {
    cantidad: vigentes.length,
    total: vigentes.reduce((acumulado, v) => acumulado + v.total, 0),
  }
}

/** Un total acumulado por medio de pago — resultado de `totalesPorMedioDeVentas`. */
export type TotalPorMedio = { idMedioPago: number; nombre: string; total: number }

/** Total neto por medio de pago EXCLUYENDO anuladas (spec: totales arriba de la tabla) — suma
 * `mediosDePago[].importe` (ya neto de vuelto, ver `MedioDeVentaNeto` del lado del servidor)
 * agrupado por `idMedioPago` sobre las ventas vigentes. Orden alfabético (es) por nombre, estable
 * sin importar el orden de llegada de las filas. */
export function totalesPorMedioDeVentas(ventas: VentaDeTurnoListado[]): TotalPorMedio[] {
  const vigentes = ventas.filter((v) => v.estado !== 'Anulado')
  const acumuladoPorId = new Map<number, TotalPorMedio>()

  for (const venta of vigentes) {
    for (const medio of venta.mediosDePago) {
      const existente = acumuladoPorId.get(medio.idMedioPago)
      if (existente) {
        existente.total += medio.importe
      } else {
        acumuladoPorId.set(medio.idMedioPago, { idMedioPago: medio.idMedioPago, nombre: medio.nombre, total: medio.importe })
      }
    }
  }

  return Array.from(acumuladoPorId.values()).sort((a, b) => a.nombre.localeCompare(b.nombre, 'es'))
}

/** Nombre de un medio de pago dado su id, resuelto contra el catálogo — mismo criterio de
 * fallback que `impresion/plantillas.ts` (`medioPorId.get(...)?.nombre ?? 'Medio #id'`): nunca se
 * asume un nombre, un medio ausente del catálogo (no debería pasar, pero nunca se confía en
 * silencio) muestra su id. */
export function nombreDeMedio(idMedioPago: number, medios: MedioPagoListado[]): string {
  return medios.find((m) => m.id === idMedioPago)?.nombre ?? `Medio #${idMedioPago}`
}

/** Espejo cliente de `Politicas.OperacionDePos` (Vendedor/Supervisor/Admin, nunca Root) + la
 * transición válida del servidor (`Emitido → Anulado` únicamente, `ReglaDeComprobantes`) — nunca
 * autoritativo (el servidor vuelve a validar todo, 403/409 se manejan igual si este espejo
 * queda desactualizado). */
export function puedeAnular(fila: VentaDeTurnoListado, rolId: number): boolean {
  return fila.estado === 'Emitido' && rolId !== ROL.Root
}

/** Una venta puede reimprimirse solo si está Emitida (nunca una anulada: no hay nada que
 * reimprimir de algo que se dejó sin efecto) — mismo criterio de transición que `puedeAnular`,
 * pero sin restricción de rol (reimprimir no es una operación sensible). */
export function puedeReimprimir(fila: VentaDeTurnoListado): boolean {
  return fila.estado === 'Emitido'
}

// ---- Filtros (spec: "filtros en cada columna de la tabla") --------------------------------

export type EstadoFiltro = 'Todas' | EstadoComprobante

export type FiltrosDeVentasDelTurno = {
  numero: string
  fecha: string
  cliente: string
  totalMinimo: number | null
  totalMaximo: number | null
  idMedioPago: number | null
  estado: EstadoFiltro
}

export const FILTROS_VACIOS: FiltrosDeVentasDelTurno = {
  numero: '',
  fecha: '',
  cliente: '',
  totalMinimo: null,
  totalMaximo: null,
  idMedioPago: null,
  estado: 'Todas',
}

/** Quita diacríticos (acentos) vía descomposición Unicode — "Pérez" y "Perez" deben matchear
 * igual (spec: "case- y accent-insensitive"). */
function normalizarTexto(texto: string): string {
  return texto
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .toLocaleLowerCase('es-AR')
}

/** `true` si `buscado` está vacío (sin filtro) o aparece como substring de `contenedor`,
 * ignorando mayúsculas/minúsculas y acentos de ambos lados. */
function contieneTexto(contenedor: string, buscado: string): boolean {
  if (buscado.trim() === '') return true
  return normalizarTexto(contenedor).includes(normalizarTexto(buscado))
}

/** Filtro puro — cada clausula es independiente y todas deben cumplirse (AND), spec: filtros en
 * cada columna de la tabla. mutation-proof-tests: cada `if` de acá es la clausula bajo prueba de
 * su propio test, nombrada por columna. */
export function filtrarVentas(ventas: VentaDeTurnoListado[], filtros: FiltrosDeVentasDelTurno): VentaDeTurnoListado[] {
  return ventas.filter((venta) => {
    if (!contieneTexto(venta.numeroVisible, filtros.numero)) return false
    if (!contieneTexto(formatearFechaHora(venta.fecha), filtros.fecha)) return false
    if (!contieneTexto(venta.nombreCliente, filtros.cliente)) return false
    if (filtros.totalMinimo !== null && venta.total < filtros.totalMinimo) return false
    if (filtros.totalMaximo !== null && venta.total > filtros.totalMaximo) return false
    if (filtros.idMedioPago !== null && !venta.mediosDePago.some((m) => m.idMedioPago === filtros.idMedioPago)) return false
    if (filtros.estado !== 'Todas' && venta.estado !== filtros.estado) return false
    return true
  })
}

/** `true` si algún filtro está activo — gatea el botón "Limpiar filtros" (deshabilitado cuando no
 * hay nada que limpiar). */
export function hayFiltrosActivos(filtros: FiltrosDeVentasDelTurno): boolean {
  return (
    filtros.numero !== '' ||
    filtros.fecha !== '' ||
    filtros.cliente !== '' ||
    filtros.totalMinimo !== null ||
    filtros.totalMaximo !== null ||
    filtros.idMedioPago !== null ||
    filtros.estado !== 'Todas'
  )
}

/** Un medio de pago disponible para el `<select>` del filtro — solo `id`/`nombre`, no todo
 * `MedioPagoListado` (el filtro no necesita comportamiento/orden/etc.). */
export type MedioDisponible = { idMedioPago: number; nombre: string }

/** Medios de pago presentes en el listado COMPLETO del turno (no el filtrado: así la opción
 * seleccionada nunca desaparece del `<select>` mientras el propio filtro de medio la excluye del
 * resultado) — deduplicados por id, orden alfabético (es). */
export function mediosDisponibles(ventas: VentaDeTurnoListado[]): MedioDisponible[] {
  const nombrePorId = new Map<number, string>()
  for (const venta of ventas) {
    for (const medio of venta.mediosDePago) {
      if (!nombrePorId.has(medio.idMedioPago)) nombrePorId.set(medio.idMedioPago, medio.nombre)
    }
  }
  return Array.from(nombrePorId.entries())
    .map(([idMedioPago, nombre]) => ({ idMedioPago, nombre }))
    .sort((a, b) => a.nombre.localeCompare(b.nombre, 'es'))
}
