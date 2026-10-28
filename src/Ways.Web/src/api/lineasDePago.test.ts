import { describe, expect, it } from 'vitest'
import {
  agregarLineaDePago,
  aLineasDeSolicitud,
  cambiarLineaDePago,
  diferenciaContraObjetivo,
  errorDeLineaDePago,
  errorDeLineasDePago,
  lineaDePagoVacia,
  lineasDePagoValidas,
  lineasEditablesDesdeListado,
  MAXIMO_DE_LINEAS_DE_PAGO,
  MAXIMO_DE_REFERENCIA_DE_LINEA,
  quitarLineaDePago,
  textoDeMediosDelPago,
  totalDeLineasDePago,
  type LineaDePagoEditable,
} from './lineasDePago'

function linea(sobrescribir: Partial<LineaDePagoEditable> = {}): LineaDePagoEditable {
  return { clave: 1, idMedioPago: 7, importe: 100, referencia: '', ...sobrescribir }
}

describe('totalDeLineasDePago / diferenciaContraObjetivo', () => {
  it('suma los importes y redondea a centavos sin arrastrar ruido binario', () => {
    expect(totalDeLineasDePago([linea({ importe: 0.1 }), linea({ clave: 2, importe: 0.2 })])).toBe(0.3)
  })

  it('una línea sin importe suma cero', () => {
    expect(totalDeLineasDePago([linea({ importe: null }), linea({ clave: 2, importe: 50 })])).toBe(50)
  })

  it('la diferencia es positiva si falta, negativa si sobra y cero si cierra', () => {
    const lineas = [linea({ importe: 600 }), linea({ clave: 2, importe: 300.5 })]
    expect(diferenciaContraObjetivo(1000, lineas)).toBe(99.5)
    expect(diferenciaContraObjetivo(800, lineas)).toBe(-100.5)
    expect(diferenciaContraObjetivo(900.5, lineas)).toBe(0)
  })
})

describe('agregar / quitar / cambiar líneas', () => {
  it('agrega una línea vacía con una clave que no repite ninguna', () => {
    const lineas = agregarLineaDePago([linea({ clave: 1 }), linea({ clave: 4 })])
    expect(lineas).toHaveLength(3)
    expect(lineas[2]).toEqual({ clave: 5, idMedioPago: '', importe: null, referencia: '' })
  })

  it('puede arrancar la línea nueva con un importe (lo que falta)', () => {
    expect(agregarLineaDePago([linea()], 250)[1].importe).toBe(250)
  })

  it('no pasa del máximo de líneas', () => {
    const llena = Array.from({ length: MAXIMO_DE_LINEAS_DE_PAGO }, (_, i) => linea({ clave: i + 1 }))
    expect(agregarLineaDePago(llena)).toHaveLength(MAXIMO_DE_LINEAS_DE_PAGO)
  })

  it('quita por clave, no por posición, y respeta el orden del resto', () => {
    const lineas = [linea({ clave: 1 }), linea({ clave: 2 }), linea({ clave: 3 })]
    expect(quitarLineaDePago(lineas, 2).map((l) => l.clave)).toEqual([1, 3])
  })

  it('la última línea no se quita', () => {
    expect(quitarLineaDePago([linea()], 1)).toHaveLength(1)
  })

  it('cambiar toca solo la línea de la clave y no muta la original', () => {
    const original = [linea({ clave: 1 }), linea({ clave: 2, importe: 5 })]
    const cambiadas = cambiarLineaDePago(original, 2, { importe: 80, referencia: 'R' })
    expect(cambiadas[0]).toBe(original[0])
    expect(cambiadas[1]).toEqual({ clave: 2, idMedioPago: 7, importe: 80, referencia: 'R' })
    expect(original[1].importe).toBe(5)
  })

  it('la línea vacía parte sin medio, sin importe y sin referencia', () => {
    expect(lineaDePagoVacia()).toEqual({ clave: 1, idMedioPago: '', importe: null, referencia: '' })
  })
})

// Cláusula bajo prueba: cada regla de `errorDeLineaDePago` (el servidor vuelve a validar todo).
describe('errorDeLineaDePago', () => {
  it('una línea completa es válida', () => {
    expect(errorDeLineaDePago(linea())).toBeNull()
  })

  it('exige medio', () => {
    expect(errorDeLineaDePago(linea({ idMedioPago: '' }))).toBe('elegí el medio de pago.')
  })

  it.each([null, 0, -1])('exige un importe mayor a 0 (%s)', (importe) => {
    expect(errorDeLineaDePago(linea({ importe }))).toBe('ingresá un importe mayor a 0.')
  })

  it('rechaza más de 2 decimales y acepta exactamente 2', () => {
    expect(errorDeLineaDePago(linea({ importe: 10.005 }))).toBe('el importe admite hasta 2 decimales.')
    expect(errorDeLineaDePago(linea({ importe: 10.01 }))).toBeNull()
  })

  it('rechaza un importe fuera del rango soportado', () => {
    expect(errorDeLineaDePago(linea({ importe: 1e15 }))).toBe('el importe es demasiado grande.')
  })

  it('la referencia admite 100 caracteres (recortada) y rechaza 101', () => {
    expect(errorDeLineaDePago(linea({ referencia: ` ${'a'.repeat(MAXIMO_DE_REFERENCIA_DE_LINEA)} ` }))).toBeNull()
    expect(errorDeLineaDePago(linea({ referencia: 'a'.repeat(MAXIMO_DE_REFERENCIA_DE_LINEA + 1) }))).toBe(
      'la referencia admite hasta 100 caracteres.',
    )
  })
})

describe('errorDeLineasDePago', () => {
  it('sin líneas pide agregar un medio', () => {
    expect(errorDeLineasDePago([])).toBe('Agregá al menos un medio de pago.')
  })

  it('nombra el número de la primera línea inválida', () => {
    const lineas = [linea({ clave: 1 }), linea({ clave: 2, importe: 0 }), linea({ clave: 3, idMedioPago: '' })]
    expect(errorDeLineasDePago(lineas)).toBe('Línea 2: ingresá un importe mayor a 0.')
  })

  it('rechaza más del máximo de líneas', () => {
    const demasiadas = Array.from({ length: MAXIMO_DE_LINEAS_DE_PAGO + 1 }, (_, i) => linea({ clave: i + 1 }))
    expect(errorDeLineasDePago(demasiadas)).toBe('Un pago admite hasta 20 líneas.')
    expect(lineasDePagoValidas(demasiadas.slice(1))).toBe(true)
  })

  it('rechaza un total fuera del rango soportado aunque cada línea lo esté', () => {
    const casiElLimite = 9e13
    const lineas = [linea({ clave: 1, importe: casiElLimite }), linea({ clave: 2, importe: casiElLimite })]
    expect(errorDeLineaDePago(lineas[0])).toBeNull()
    expect(errorDeLineasDePago(lineas)).toBe('El total del pago es demasiado grande.')
  })

  it('un mismo medio repetido en varias líneas es válido', () => {
    expect(lineasDePagoValidas([linea({ clave: 1 }), linea({ clave: 2 })])).toBe(true)
  })
})

describe('aLineasDeSolicitud / lineasEditablesDesdeListado', () => {
  it('manda medio, importe y referencia recortada (en blanco, null)', () => {
    expect(aLineasDeSolicitud([linea({ referencia: '   ' }), linea({ clave: 2, idMedioPago: 9, referencia: ' R-1 ' })])).toEqual([
      { idMedioPago: 7, importe: 100, referencia: null },
      { idMedioPago: 9, importe: 100, referencia: 'R-1' },
    ])
  })

  it('precarga desde el listado conservando medio, importe y referencia', () => {
    expect(
      lineasEditablesDesdeListado([
        { idItem: 10, idMedioPago: 3, nombreMedioPago: 'Efectivo', importe: 5, referencia: null },
        { idItem: 11, idMedioPago: 4, nombreMedioPago: null, importe: 6, referencia: 'X' },
      ]),
    ).toEqual([
      { clave: 1, idMedioPago: 3, importe: 5, referencia: '', nombreMedioPago: 'Efectivo' },
      { clave: 2, idMedioPago: 4, importe: 6, referencia: 'X', nombreMedioPago: null },
    ])
  })
})

describe('textoDeMediosDelPago', () => {
  it('con varias líneas lista el reparto por medio', () => {
    const texto = textoDeMediosDelPago({
      nombreMedioPago: 'Efectivo',
      lineas: [
        { idItem: 1, idMedioPago: 1, nombreMedioPago: 'Efectivo', importe: 600, referencia: null },
        { idItem: 2, idMedioPago: 2, nombreMedioPago: 'Transferencia', importe: 400, referencia: null },
      ],
    })
    expect(texto).toBe('Efectivo $ 600,00 · Transferencia $ 400,00')
  })

  it('un medio dado de baja en una de varias líneas sale como no disponible', () => {
    const texto = textoDeMediosDelPago({
      lineas: [
        { idItem: 1, idMedioPago: 1, nombreMedioPago: null, importe: 600, referencia: null },
        { idItem: 2, idMedioPago: 2, nombreMedioPago: 'Cheque', importe: 400, referencia: null },
      ],
    })
    expect(texto).toBe('(no disponible) $ 600,00 · Cheque $ 400,00')
  })

  it('con una sola línea muestra solo el medio, como siempre', () => {
    expect(
      textoDeMediosDelPago({
        nombreMedioPago: 'Efectivo',
        lineas: [{ idItem: 1, idMedioPago: 1, nombreMedioPago: 'Efectivo', importe: 1000, referencia: null }],
      }),
    ).toBe('Efectivo')
  })

  it('sin líneas (respuesta anterior) usa el nombre de la cabecera o no disponible', () => {
    expect(textoDeMediosDelPago({ nombreMedioPago: 'Cheque' })).toBe('Cheque')
    expect(textoDeMediosDelPago({ nombreMedioPago: null })).toBe('(no disponible)')
  })
})
