/**
 * Cliente HTTP + mappers puros del ledger de proveedores (stage-15-cc-proveedores-ledger, Slice 6):
 * estado de cuenta PAGINADO (`GET /api/proveedores/{id}/cuenta-corriente`, design decisión 10) y
 * el ajuste manual (`POST …/ajustes`, `Politicas.SupervisionDeCuentaDeProveedor`). El ajuste manual
 * reusa server-side la MISMA `ReglaDeAjusteDeCuenta.Validar` que la cuenta corriente de clientes
 * (design decisión 13) — este módulo reusa por lo tanto el mirror local ya probado
 * (`validarAjusteLocal`, `saldoResultanteDeAjuste`, `LONGITUD_MINIMA_DETALLE_AJUSTE`) de
 * `./cuentaCorriente` en vez de duplicarlo: es la misma regla, no una prima de forma parecida.
 */
import { api } from './cliente'
import { rangoUltimoMes } from './cuentaCorriente'
import type {
  EtiquetaDeAjuste,
  MovimientoDeCuentaDeProveedor,
  PaginaDeEstadoDeCuentaDeProveedor,
  SolicitudDeAjusteDeProveedor,
} from './tipos'

export { rangoUltimoMes }

// ---- Offset local para desde/hasta (mismo criterio que cuentaCorriente.ts/compras.ts: el
// servidor corre en UTC, `fecha` es `timestamptz`, un `<input type="date">` sin offset se
// interpretaría como UTC — mutation-proof-tests regla 10). Duplicado a propósito: no hay un módulo
// compartido de utilidades de fecha en esta web todavía. -----------------------------------------
function desplazamientoUtcLocal(anio: number, mes: number, dia: number): string {
  const minutos = new Date(anio, mes - 1, dia).getTimezoneOffset()
  const signo = minutos > 0 ? '-' : '+'
  const minutosAbsolutos = Math.abs(minutos)
  const horas = String(Math.floor(minutosAbsolutos / 60)).padStart(2, '0')
  const restoMinutos = String(minutosAbsolutos % 60).padStart(2, '0')
  return `${signo}${horas}:${restoMinutos}`
}

function fechaIsoConOffset(fechaIso: string, horaLimite: string): string {
  const [anio, mes, dia] = fechaIso.split('-').map(Number)
  return `${fechaIso}T${horaLimite}${desplazamientoUtcLocal(anio, mes, dia)}`
}

export type FiltrosDeEstadoDeCuentaDeProveedor = {
  desde: string
  hasta: string
  historico: boolean
  pagina: number
  tamanio: number
}

/** Ventana por defecto de la pantalla — mismo default de último mes que
 * `ServicioDeCuentaCorrienteDeProveedor.ObtenerEstadoDeCuentaAsync` aplica del lado del servidor
 * cuando no llega ningún filtro, precargado acá para que los inputs nunca queden vacíos. */
export function filtrosDeEstadoDeCuentaDeProveedorVacios(): FiltrosDeEstadoDeCuentaDeProveedor {
  const rango = rangoUltimoMes()
  return { desde: rango.desde, hasta: rango.hasta, historico: false, pagina: 1, tamanio: 25 }
}

/** Arma el query string de `GET …/cuenta-corriente` — `historico` gana sobre `desde`/`hasta`
 * (mismo criterio que `ObtenerEstadoDeCuentaAsync`); `pagina`/`tamanio` viajan siempre (design
 * decisión 10, la superficie PAGINADA). */
export function construirQueryEstadoDeCuentaDeProveedor(filtros: FiltrosDeEstadoDeCuentaDeProveedor): string {
  const parametros = new URLSearchParams()
  if (filtros.historico) {
    parametros.set('historico', 'true')
  } else {
    if (filtros.desde) parametros.set('desde', fechaIsoConOffset(filtros.desde, '00:00:00'))
    if (filtros.hasta) parametros.set('hasta', fechaIsoConOffset(filtros.hasta, '23:59:59.999'))
  }
  parametros.set('pagina', String(filtros.pagina))
  parametros.set('tamanio', String(filtros.tamanio))
  return `?${parametros.toString()}`
}

export const clienteDeCuentaCorrienteDeProveedor = {
  /** `GET /api/proveedores/{id}/cuenta-corriente` — `Politicas.OperacionDePos` (grupo, sin policy
   * apilada): 200 siempre, header + página en un único payload (design decisión 9). */
  obtenerEstado: (idProveedor: number, filtros: FiltrosDeEstadoDeCuentaDeProveedor) =>
    api.get<PaginaDeEstadoDeCuentaDeProveedor>(
      `/proveedores/${idProveedor}/cuenta-corriente${construirQueryEstadoDeCuentaDeProveedor(filtros)}`,
    ),
  /** `POST /api/proveedores/{id}/cuenta-corriente/ajustes` — TOP-LEVEL, `Politicas.
   * SupervisionDeCuentaDeProveedor` SOLA (design decisión 12): 201 con el movimiento `Ajuste`. Sin
   * turno (design decisión 14 — "provenance, not authority"), a diferencia de un pago. */
  registrarAjuste: (idProveedor: number, solicitud: SolicitudDeAjusteDeProveedor) =>
    api.post<MovimientoDeCuentaDeProveedor>(`/proveedores/${idProveedor}/cuenta-corriente/ajustes`, solicitud),
}

/** Etiqueta de pantalla de un movimiento `Ajuste` por su origen — espejo de
 * `CalculadorDeEstadoDeCuentaDeProveedor.EtiquetarAjuste`, ya resuelto server-side en
 * `m.etiqueta` (el backend reusa el mismo enum `EtiquetaDeAjuste` que la cuenta corriente de
 * clientes, `Contratos.cs`/`ContratosDeProveedor.cs`). Mismas dos etiquetas de pantalla que
 * `etiquetaDeMovimiento` (`cuentaCorriente.ts`). */
export function etiquetarAjuste(etiqueta: EtiquetaDeAjuste | null): string {
  return etiqueta === 'AnulacionContramovimiento' ? 'Contramov. de anulación' : 'Ajuste manual'
}

/** Etiqueta de pantalla de la columna "Tipo" — espejo de `TipoMovimientoCcProveedor`, delegando en
 * `etiquetarAjuste` para distinguir un ajuste manual de un contramovimiento de anulación. */
export function etiquetaDeTipoDeMovimiento(m: Pick<MovimientoDeCuentaDeProveedor, 'tipo' | 'etiqueta'>): string {
  switch (m.tipo) {
    case 'Apertura':
      return 'Apertura'
    case 'Compra':
      return 'Compra'
    case 'Pago':
      return 'Pago'
    case 'Ajuste':
      return etiquetarAjuste(m.etiqueta)
  }
}

/** Columna "Comprobante/Gasto": el origen del movimiento, cuando lo tiene — un `pago` referencia
 * su `gasto`, un `compra`/contramovimiento de anulación referencia la compra, un `apertura` o un
 * ajuste manual no referencian nada. */
export function referenciaDeMovimiento(m: Pick<MovimientoDeCuentaDeProveedor, 'idComprobanteCompra' | 'idGasto'>): string {
  if (m.idComprobanteCompra !== null) return `Compra #${m.idComprobanteCompra}`
  if (m.idGasto !== null) return `Gasto #${m.idGasto}`
  return '—'
}

/** Un saldo negativo es "saldo a favor" (spec: Anulación decisión — "MAY be negative... MUST NOT
 * be clamped to zero") — nunca un valor recortado a cero. Compartido por la tabla de movimientos y
 * el header de esta pantalla. */
export function esSaldoAFavor(valor: number): boolean {
  return valor < 0
}

/** Importe/detalle → `SolicitudDeAjusteDeProveedor` — recorta el detalle (mismo criterio que
 * `aSolicitudDeAjuste`, `cuentaCorriente.ts`; el servidor también lo recorta, esto es solo para
 * que el `POST` viaje ya prolijo). */
export function aSolicitudDeAjusteDeProveedor(idPuntoVenta: number, importe: number, detalle: string): SolicitudDeAjusteDeProveedor {
  return { idPuntoVenta, importe, detalle: detalle.trim() }
}
