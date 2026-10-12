import type {
  CuentasDeLaEmpresa,
  MedioElectronicoSinCuenta,
  MovimientoDeCuentaListado,
  TipoMovimientoCuenta,
  TotalesDeCuentaEnElRango,
} from '../../api/tipos'

/** Etiqueta en español de `TipoMovimientoCuenta`. */
export function etiquetaDeTipoMovimientoCuenta(tipo: TipoMovimientoCuenta): string {
  switch (tipo) {
    case 'Cobro':
      return 'Cobro'
    case 'Comision':
      return 'Comisión'
    case 'RetencionIibb':
      return 'Retención IIBB'
    case 'RetencionIva':
      return 'Retención IVA'
    case 'RetencionGanancias':
      return 'Retención Ganancias'
    case 'Ajuste':
      return 'Ajuste'
    case 'Transferencia':
      return 'Transferencia'
    case 'Deposito':
      return 'Depósito'
    case 'Retiro':
      return 'Retiro'
  }
}

/** Comisión más las tres retenciones del rango (positivo cuando resta), en centavos enteros. */
export function descuentosDelRango(totales: TotalesDeCuentaEnElRango): number {
  const centavos = [totales.comision, totales.retencionIibb, totales.retencionIva, totales.retencionGanancias]
    .map((valor) => Math.round(valor * 100))
    .reduce((suma, valor) => suma + valor, 0)

  return centavos / 100
}

/** El empleado de la fila: su nombre, o el id si el usuario ya no está disponible. */
export function nombreDelEmpleado(m: MovimientoDeCuentaListado): string {
  return m.nombreEmpleado ?? `Empleado #${m.idEmpleado}`
}

/** El medio de la fila: "—" sin medio, "(no disponible)" si el medio existe pero está dado de baja. */
export function nombreDelMedio(m: MovimientoDeCuentaListado): string {
  if (m.idMedioPago === null) return '—'

  return m.nombreMedioPago ?? '(no disponible)'
}

/** `5,5 %`: el porcentaje congelado en la fila, con coma decimal y sin ceros de más. */
export function formatearPorcentaje(valor: number): string {
  return `${String(valor).replace('.', ',')} %`
}

/** Aviso de los medios electrónicos activos sin cuenta utilizable, o `null` si no hay ninguno. */
export function avisoDeMediosSinCuenta(medios: readonly MedioElectronicoSinCuenta[]): string | null {
  if (medios.length === 0) return null

  const nombres = medios.map((m) => m.nombre).join(', ')
  const sujeto = medios.length === 1 ? `el medio ${nombres}` : `los medios ${nombres}`

  return `Lo cobrado con ${sujeto} no entra a ninguna cuenta.`
}

/** Cuentas de la empresa ya cargadas: la elegida solo vale si sigue en la lista (una baja la saca de ahí). */
export function cuentaElegidaVigente(cuentas: CuentasDeLaEmpresa | null, idElegida: number | null): number | null {
  if (cuentas === null || idElegida === null) return null

  return cuentas.cuentas.some((c) => c.id === idElegida) ? idElegida : null
}
