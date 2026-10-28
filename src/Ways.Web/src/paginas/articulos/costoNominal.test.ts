import { describe, expect, it } from 'vitest'
import type { AlicuotaIvaListado } from '../../api/tipos'
import {
  bultoSinUnidades,
  calcularCostoNominal,
  cambiaAlgunaEntrada,
  costoDeListaSinYConIva,
  costoNominalDelFormulario,
  entradasDeCosto,
  porcentajeDeAlicuota,
  type EntradasDeCosto,
} from './costoNominal'
import { formularioVacio, type Formulario } from './FormularioArticulo'

function entradas(sobrescribir: Partial<EntradasDeCosto> = {}): EntradasDeCosto {
  return {
    costoLista: 100,
    por: 'Unidad',
    incluyeIva: true,
    unidadesPorBulto: null,
    porcentajeIva: 21,
    descuentoProveedor: null,
    porcentajeImpuestoInterno: 0,
    ...sobrescribir,
  }
}

const alicuotas: AlicuotaIvaListado[] = [
  { id: 1, nombre: 'IVA 21%', porcentaje: 21, codigoAfip: 5, activo: true },
  { id: 2, nombre: 'IVA 10,5%', porcentaje: 10.5, codigoAfip: 4, activo: true },
]

describe('calcularCostoNominal — mismos casos que CalculadorDeCostoNominalTests del servidor', () => {
  it.each<[string, Partial<EntradasDeCosto>, number]>([
    ['por unidad con IVA incluido y sin descuento es el costo de lista', { costoLista: 121 }, 121],
    ['por unidad sin IVA le suma la alícuota', { costoLista: 100, incluyeIva: false }, 121],
    ['por bulto con IVA incluido divide entre las unidades del bulto', { costoLista: 300, por: 'Bulto', unidadesPorBulto: 12 }, 25],
    [
      'por bulto sin IVA divide y le suma la alícuota',
      { costoLista: 120, por: 'Bulto', incluyeIva: false, unidadesPorBulto: 10 },
      14.52,
    ],
    ['el descuento se aplica sobre el costo con IVA', { costoLista: 100, descuentoProveedor: 10 }, 90],
    ['el descuento y el IVA se combinan en un solo cálculo', { costoLista: 100, incluyeIva: false, descuentoProveedor: 10 }, 108.9],
    ['redondea la mitad lejos de cero (por bulto)', { costoLista: 1.01, por: 'Bulto', unidadesPorBulto: 3 }, 0.34],
    ['redondea la mitad lejos de cero (empate exacto)', { costoLista: 1.005 }, 1.01],
    ['el costo cero da cero', { costoLista: 0, incluyeIva: false, descuentoProveedor: 10 }, 0],
  ])('%s', (_nombre, sobrescribir, esperado) => {
    expect(calcularCostoNominal(entradas(sobrescribir))).toBe(esperado)
  })

  it('el descuento ausente equivale a cero', () => {
    expect(calcularCostoNominal(entradas({ descuentoProveedor: null }))).toBe(calcularCostoNominal(entradas({ descuentoProveedor: 0 })))
  })

  it('sin costo de lista no hay costo nominal', () => {
    expect(calcularCostoNominal(entradas({ costoLista: null, descuentoProveedor: 10 }))).toBeNull()
    expect(calcularCostoNominal(entradas({ costoLista: null, por: 'Bulto', incluyeIva: false }))).toBeNull()
  })

  it('un empate que el punto flotante pierde se redondea como el decimal exacto: 0,7 sin IVA al 15%', () => {
    expect(calcularCostoNominal(entradas({ costoLista: 0.7, incluyeIva: false, porcentajeIva: 15 }))).toBe(0.81)
  })

  it('un descuento con decimales no arrastra ruido binario: 100 menos 0,1', () => {
    expect(calcularCostoNominal(entradas({ costoLista: 1000, descuentoProveedor: 0.1 }))).toBe(999)
  })

  it('un costo por bulto sin unidades mayores que cero no se puede calcular', () => {
    expect(calcularCostoNominal(entradas({ por: 'Bulto', unidadesPorBulto: null }))).toBeNull()
    expect(calcularCostoNominal(entradas({ por: 'Bulto', unidadesPorBulto: 0 }))).toBeNull()
  })

  it('un costo sin IVA con la alícuota desconocida no se puede calcular; con IVA incluido no la necesita', () => {
    expect(calcularCostoNominal(entradas({ incluyeIva: false, porcentajeIva: null }))).toBeNull()
    expect(calcularCostoNominal(entradas({ incluyeIva: true, porcentajeIva: null }))).toBe(100)
  })
})

describe('impuestos internos en el costo nominal — mismos casos que CalculadorDeCostoNominalTests del servidor', () => {
  it.each<[string, Partial<EntradasDeCosto>, number]>([
    ['sin IVA suma al neto el impuesto interno y el IVA, y el IVA no grava al impuesto', { costoLista: 100, incluyeIva: false, porcentajeImpuestoInterno: 8 }, 129],
    ['con IVA incluido se toma tal cual, sin sumar el impuesto', { costoLista: 121, incluyeIva: true, porcentajeImpuestoInterno: 8 }, 121],
    ['el descuento se aplica sobre el total con impuesto', { costoLista: 100, incluyeIva: false, porcentajeImpuestoInterno: 8, descuentoProveedor: 10 }, 116.1],
    ['por bulto divide antes de sumar', { costoLista: 1200, por: 'Bulto', unidadesPorBulto: 12, incluyeIva: false, porcentajeImpuestoInterno: 10 }, 131],
    ['sin porcentaje es el cálculo de siempre', { costoLista: 100, incluyeIva: false }, 121],
  ])('%s', (_nombre, sobrescribir, esperado) => {
    expect(calcularCostoNominal(entradas(sobrescribir))).toBe(esperado)
  })

  it('cambiar el porcentaje cuenta solo mientras el costo se guarda sin IVA, antes o después', () => {
    const base = entradas({ incluyeIva: false })
    expect(cambiaAlgunaEntrada(base, { ...base, porcentajeImpuestoInterno: 8 })).toBe(true)
    const conIva = entradas({ incluyeIva: true })
    expect(cambiaAlgunaEntrada(conIva, { ...conIva, porcentajeImpuestoInterno: 8 })).toBe(false)
  })

  it('entradasDeCosto lee el porcentaje del formulario (vacío = 0) y el costo del formulario lo usa', () => {
    const f: Formulario = { ...formularioVacio(), idAlicuotaIva: 1, costoLista: '100', costoListaIncluyeIva: false, porcentajeImpuestoInterno: '8,5' }

    expect(entradasDeCosto(f, alicuotas).porcentajeImpuestoInterno).toBe(8.5)
    expect(entradasDeCosto({ ...f, porcentajeImpuestoInterno: '' }, alicuotas).porcentajeImpuestoInterno).toBe(0)
    expect(costoNominalDelFormulario(f, alicuotas, false)).toBe(129.5)
  })
})

describe('bultoSinUnidades', () => {
  it.each([null, 0, -1])('por bulto con %s unidades no se puede calcular', (unidades) => {
    expect(bultoSinUnidades('Bulto', unidades)).toBe(true)
  })

  it('las unidades por bulto solo importan cuando el costo es por bulto', () => {
    expect(bultoSinUnidades('Unidad', null)).toBe(false)
    expect(bultoSinUnidades('Bulto', 6)).toBe(false)
  })
})

describe('cambiaAlgunaEntrada — misma regla que el servidor', () => {
  const base = () => entradas({ unidadesPorBulto: 6, descuentoProveedor: 10 })

  it('sin cambios ninguna entrada cambió', () => {
    expect(cambiaAlgunaEntrada(base(), base())).toBe(false)
  })

  it.each<[string, Partial<EntradasDeCosto>]>([
    ['el costo de lista', { costoLista: 101 }],
    ['el costo de lista vaciado', { costoLista: null }],
    ['la base', { por: 'Bulto' }],
    ['el IVA incluido', { incluyeIva: false }],
    ['el descuento', { descuentoProveedor: 5 }],
    ['el descuento vaciado', { descuentoProveedor: null }],
  ])('%s siempre cuenta', (_nombre, cambio) => {
    expect(cambiaAlgunaEntrada(base(), { ...base(), ...cambio })).toBe(true)
  })

  it('las unidades por bulto solo cuentan si la base es bulto, antes o después', () => {
    expect(cambiaAlgunaEntrada(base(), { ...base(), unidadesPorBulto: 12 })).toBe(false)
    expect(cambiaAlgunaEntrada({ ...base(), por: 'Bulto' }, { ...base(), por: 'Bulto', unidadesPorBulto: 12 })).toBe(true)
    expect(cambiaAlgunaEntrada({ ...base(), por: 'Bulto' }, { ...base(), por: 'Unidad', unidadesPorBulto: 12 })).toBe(true)
  })

  it('el porcentaje de la alícuota solo cuenta si el costo se guarda sin IVA, antes o después', () => {
    expect(cambiaAlgunaEntrada(base(), { ...base(), porcentajeIva: 10.5 })).toBe(false)
    expect(cambiaAlgunaEntrada({ ...base(), incluyeIva: false }, { ...base(), incluyeIva: false, porcentajeIva: 10.5 })).toBe(true)
  })
})

describe('costoDeListaSinYConIva', () => {
  it('el costo cargado sin IVA queda como está y el con IVA se deriva', () => {
    expect(costoDeListaSinYConIva(100, false, 21)).toEqual({ sinIva: 100, conIva: 121 })
  })

  it('el costo cargado con IVA queda como está y el sin IVA se deriva', () => {
    expect(costoDeListaSinYConIva(121, true, 21)).toEqual({ sinIva: 100, conIva: 121 })
    expect(costoDeListaSinYConIva(150, true, 21)).toEqual({ sinIva: 123.97, conIva: 150 })
  })

  it('sin alícuota el campo derivado queda vacío', () => {
    expect(costoDeListaSinYConIva(100, false, null)).toEqual({ sinIva: 100, conIva: null })
    expect(costoDeListaSinYConIva(121, true, null)).toEqual({ sinIva: null, conIva: 121 })
  })

  it('sin costo cargado los dos quedan vacíos', () => {
    expect(costoDeListaSinYConIva(null, true, 21)).toEqual({ sinIva: null, conIva: null })
  })

  it('la alícuota con decimales se aplica exacta', () => {
    expect(costoDeListaSinYConIva(100, false, 10.5)).toEqual({ sinIva: 100, conIva: 110.5 })
  })
})

describe('porcentajeDeAlicuota y entradasDeCosto', () => {
  it('busca el porcentaje por id; sin alícuota elegida o inexistente es null', () => {
    expect(porcentajeDeAlicuota(alicuotas, 2)).toBe(10.5)
    expect(porcentajeDeAlicuota(alicuotas, '')).toBeNull()
    expect(porcentajeDeAlicuota(alicuotas, 99)).toBeNull()
  })

  it('convierte el texto del formulario: vacío es null y un cero es un valor', () => {
    expect(
      entradasDeCosto(
        { costoLista: '', costoListaPor: 'Unidad', costoListaIncluyeIva: true, unidadesPorBulto: ' ', descuentoProveedor: '0', porcentajeImpuestoInterno: '0', idAlicuotaIva: 1 },
        alicuotas,
      ),
    ).toEqual({ costoLista: null, por: 'Unidad', incluyeIva: true, unidadesPorBulto: null, porcentajeIva: 21, descuentoProveedor: 0, porcentajeImpuestoInterno: 0 })
  })

  it.each(['', '  '])('un descuento vacío (%j) equivale a cero, como lo guarda el servidor', (descuentoProveedor) => {
    const costos = { costoLista: '100', costoListaPor: 'Unidad' as const, costoListaIncluyeIva: true, unidadesPorBulto: '', porcentajeImpuestoInterno: '', idAlicuotaIva: 1 }

    expect(entradasDeCosto({ ...costos, descuentoProveedor }, alicuotas).descuentoProveedor).toBe(0)
  })

  it('un descuento tipeado se conserva', () => {
    const costos = { costoLista: '100', costoListaPor: 'Unidad' as const, costoListaIncluyeIva: true, unidadesPorBulto: '', porcentajeImpuestoInterno: '', idAlicuotaIva: 1 }

    expect(entradasDeCosto({ ...costos, descuentoProveedor: '12.5' }, alicuotas).descuentoProveedor).toBe(12.5)
  })

  it('un descuento y unidades por bulto con coma decimal valen igual que con punto', () => {
    const costos = { costoLista: '100', costoListaPor: 'Unidad' as const, costoListaIncluyeIva: true, porcentajeImpuestoInterno: '', idAlicuotaIva: 1 }

    const entradas = entradasDeCosto({ ...costos, descuentoProveedor: '12,5', unidadesPorBulto: '2,5' }, alicuotas)

    expect(entradas.descuentoProveedor).toBe(12.5)
    expect(entradas.unidadesPorBulto).toBe(2.5)
  })
})

describe('costoNominalDelFormulario', () => {
  function cargado(sobrescribir: Partial<Formulario> = {}): Formulario {
    const costos = {
      costoLista: '100',
      costoListaPor: 'Unidad' as const,
      costoListaIncluyeIva: true,
      unidadesPorBulto: '',
      descuentoProveedor: '',
      porcentajeImpuestoInterno: '',
      idAlicuotaIva: 1,
    }
    // El nominal guardado (73,5) lo dejó una compra: no sale de la fórmula.
    return { ...formularioVacio(), id: 5, ...costos, costoNominal: '73.5', costoGuardado: costos, ...sobrescribir }
  }

  it('un artículo cargado sin cambios en las entradas muestra el costo nominal guardado, aunque no salga de la fórmula', () => {
    expect(costoNominalDelFormulario(cargado(), alicuotas, false)).toBe(73.5)
  })

  /** Cláusula bajo prueba: el descuento vacío se lee como 0 en `entradasDeCosto`; sin ella, '' (null) != '0' (0)
   * contaría como cambio y reemplazaría el nominal guardado por la fórmula. */
  it('vaciar un descuento guardado en 0 no cuenta como cambio: se conserva el nominal guardado', () => {
    const guardado = { ...cargado().costoGuardado!, descuentoProveedor: '0' }

    expect(costoNominalDelFormulario(cargado({ descuentoProveedor: '', costoGuardado: guardado }), alicuotas, false)).toBe(73.5)
  })

  it('el descuento guardado en 0 y tipeado como 10 sí recalcula', () => {
    const guardado = { ...cargado().costoGuardado!, descuentoProveedor: '0' }

    expect(costoNominalDelFormulario(cargado({ descuentoProveedor: '10', costoGuardado: guardado }), alicuotas, false)).toBe(90)
  })

  it('un cambio que no cuenta (unidades por bulto con base unidad, otra alícuota con IVA incluido) conserva el guardado', () => {
    expect(costoNominalDelFormulario(cargado({ unidadesPorBulto: '12' }), alicuotas, false)).toBe(73.5)
    expect(costoNominalDelFormulario(cargado({ idAlicuotaIva: 2 }), alicuotas, false)).toBe(73.5)
  })

  it('apenas cambia una entrada que cuenta se muestra la vista previa calculada', () => {
    expect(costoNominalDelFormulario(cargado({ costoLista: '200' }), alicuotas, false)).toBe(200)
    expect(costoNominalDelFormulario(cargado({ descuentoProveedor: '10' }), alicuotas, false)).toBe(90)
    expect(costoNominalDelFormulario(cargado({ costoListaIncluyeIva: false }), alicuotas, false)).toBe(121)
  })

  it('volver a las entradas originales vuelve a mostrar el guardado', () => {
    expect(costoNominalDelFormulario(cargado({ costoLista: '200' }), alicuotas, false)).toBe(200)
    expect(costoNominalDelFormulario(cargado({ costoLista: '100' }), alicuotas, false)).toBe(73.5)
  })

  it('un artículo guardado sin IVA recalcula al cambiar la alícuota y conserva el guardado si no cambia', () => {
    const base = cargado()
    const sinIva = { costoListaIncluyeIva: false, costoGuardado: { ...base.costoGuardado!, costoListaIncluyeIva: false } }

    expect(costoNominalDelFormulario(cargado(sinIva), alicuotas, false)).toBe(73.5)
    expect(costoNominalDelFormulario(cargado({ ...sinIva, idAlicuotaIva: 2 }), alicuotas, false)).toBe(110.5)
  })

  it('un alta siempre muestra la vista previa: sin costo de lista no hay nada que mostrar', () => {
    expect(costoNominalDelFormulario({ ...formularioVacio(), idAlicuotaIva: 1 }, alicuotas, false)).toBeNull()
    expect(
      costoNominalDelFormulario({ ...formularioVacio(), idAlicuotaIva: 1, costoLista: '121', costoListaIncluyeIva: false }, alicuotas, false),
    ).toBe(146.41)
  })

  it('por bulto sin unidades por bulto no hay vista previa', () => {
    expect(costoNominalDelFormulario({ ...formularioVacio(), costoLista: '300', costoListaPor: 'Bulto' }, alicuotas, false)).toBeNull()
    expect(
      costoNominalDelFormulario({ ...formularioVacio(), costoLista: '300', costoListaPor: 'Bulto', unidadesPorBulto: '12' }, alicuotas, false),
    ).toBe(25)
  })

  it('quien no ve los costos no recibe ninguno, ni siquiera calculado', () => {
    expect(costoNominalDelFormulario(cargado({ costosOcultos: true, costoLista: '999' }), alicuotas, false)).toBeNull()
  })

  describe('un alta bloqueada a los valores de una familia', () => {
    // El nominal de la referencia (120) lo dejó una compra: la fórmula con estas entradas da 142,5 (150 con IVA y 5 % de descuento).
    const deLaFamilia = (sobrescribir: Partial<Formulario> = {}): Formulario => ({
      ...formularioVacio(),
      idFamilia: 7,
      idAlicuotaIva: 1,
      costoLista: '150',
      descuentoProveedor: '5',
      costoNominal: '120',
      ...sobrescribir,
    })

    it('muestra el nominal guardado de la familia y no el que daría la fórmula', () => {
      expect(costoNominalDelFormulario(deLaFamilia(), alicuotas, true)).toBe(120)
    })

    it('al dejar la familia (campos desbloqueados) vuelve a la vista previa calculada', () => {
      expect(costoNominalDelFormulario(deLaFamilia(), alicuotas, false)).toBe(142.5)
    })

    it('antes de que lleguen los valores de la familia no hay nominal que mostrar, aunque haya entradas con las que calcular', () => {
      expect(costoNominalDelFormulario(deLaFamilia({ costoNominal: '' }), alicuotas, true)).toBeNull()
    })

    it('quien no ve los costos sigue sin recibir ninguno', () => {
      expect(costoNominalDelFormulario(deLaFamilia({ costosOcultos: true }), alicuotas, true)).toBeNull()
    })
  })
})
