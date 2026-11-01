import { describe, expect, it } from 'vitest'
import type { DesgloseDeMedioDelLibro, FilaDelLibro, LibroDeTesoreria } from '../../api/tipos'
import {
  CLASE_DE_IMPORTE_NEGATIVO,
  claseDeImporte,
  columnasDeCuentas,
  detalleDeAjuste,
  etiquetaDeCaja,
  hayFilasSinCuenta,
  hayRetenciones,
  importeDeCuenta,
  saldoDeCuenta,
  textoDeDiferencia,
  textoDeImporte,
} from './libro'

function fila(sobrescribir: Partial<FilaDelLibro> = {}): FilaDelLibro {
  return {
    fecha: '2026-10-30T12:00:00Z',
    tipo: 'Pago',
    efectivo: null,
    cuentas: [],
    sinCuenta: null,
    caja: null,
    pago: null,
    ajuste: null,
    ...sobrescribir,
  }
}

describe('textoDeDiferencia', () => {
  it.each([
    [-1500, '$ -1.500'],
    [1300, '$ +1.300'],
    [-0.5, '$ -0,50'],
    [1300.25, '$ +1.300,25'],
  ])('%s se muestra como %s, con signo siempre y centavos solo si existen', (valor, esperado) => {
    expect(textoDeDiferencia(valor)).toBe(esperado)
  })
})

describe('etiquetaDeCaja', () => {
  it('sin diferencia es solo el número del turno', () => {
    expect(etiquetaDeCaja(137, null)).toBe('Caja #137')
  })

  it('con diferencia la agrega entre paréntesis', () => {
    expect(etiquetaDeCaja(137, -1500)).toBe('Caja #137 ($ -1.500)')
    expect(etiquetaDeCaja(150, 1300)).toBe('Caja #150 ($ +1.300)')
  })
})

describe('claseDeImporte', () => {
  it('solo un importe negativo lleva la clase de rojo', () => {
    expect(claseDeImporte(-0.01)).toBe(CLASE_DE_IMPORTE_NEGATIVO)
    expect(claseDeImporte(0)).toBe('')
    expect(claseDeImporte(10)).toBe('')
    expect(claseDeImporte(null)).toBe('')
  })

  it('un valor que redondea a cero centavos no es negativo', () => {
    expect(claseDeImporte(-0.001)).toBe('')
  })
})

describe('textoDeImporte', () => {
  it('una celda que la fila no toca queda vacía', () => {
    expect(textoDeImporte(null)).toBe('')
    expect(textoDeImporte(undefined)).toBe('')
  })

  it('formatea es-AR con símbolo', () => {
    expect(textoDeImporte(-1234.5)).toBe('-$ 1.234,50')
  })
})

describe('mapeo de columnas', () => {
  const cuentas = [
    { idCuentaTesoreria: 1, nombre: 'Mercado Pago', activo: true },
    { idCuentaTesoreria: 2, nombre: 'Banco', activo: false },
  ]

  it('columnasDeCuentas marca las inactivas en el encabezado', () => {
    expect(columnasDeCuentas(cuentas)).toEqual([
      { id: 1, titulo: 'Mercado Pago' },
      { id: 2, titulo: 'Banco (inactiva)' },
    ])
  })

  it('importeDeCuenta devuelve null si la fila no toca esa cuenta', () => {
    const f = fila({ cuentas: [{ idCuentaTesoreria: 2, importe: -80 }] })

    expect(importeDeCuenta(f, 2)).toBe(-80)
    expect(importeDeCuenta(f, 1)).toBeNull()
  })

  it('saldoDeCuenta usa cero para una cuenta que el saldo no lista', () => {
    const saldos = { efectivo: 0, porCuenta: [{ idCuentaTesoreria: 1, importe: 250 }] }

    expect(saldoDeCuenta(saldos, 1)).toBe(250)
    expect(saldoDeCuenta(saldos, 9)).toBe(0)
  })

  it('hayFilasSinCuenta solo es verdadero si alguna fila trae importe sin cuenta', () => {
    const libro = { filas: [fila(), fila({ sinCuenta: -30 })] } as LibroDeTesoreria

    expect(hayFilasSinCuenta(libro)).toBe(true)
    expect(hayFilasSinCuenta({ filas: [fila()] } as LibroDeTesoreria)).toBe(false)
  })
})

describe('hayRetenciones', () => {
  const medio = (retenciones: number): DesgloseDeMedioDelLibro => ({
    idMedioPago: 1,
    medio: 'Tarjeta',
    importe: 100,
    comision: 5,
    retenciones,
    neto: 95 - retenciones,
  })

  it('agrega la columna solo si algún medio tiene retenciones', () => {
    expect(hayRetenciones([medio(0), medio(0)])).toBe(false)
    expect(hayRetenciones([medio(0), medio(3)])).toBe(true)
  })
})

describe('detalleDeAjuste', () => {
  it('antepone el rótulo al concepto', () => {
    expect(detalleDeAjuste('Conteo de cierre')).toBe('Ajuste: Conteo de cierre')
  })
})
