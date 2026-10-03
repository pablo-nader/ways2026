import { describe, expect, it } from 'vitest'
import type { EncabezadoDeCompraFormulario, PercepcionFormulario, TotalesDeCompra } from '../api/compras'
import type { EmpresaListado, ProveedorListado, PuntoVentaListado, TipoComprobanteListado } from '../api/tipos'
import {
  alElegirProveedor,
  alicuotaDeEmpresaParaCompra,
  baseDePercepcion,
  conSugerenciasDePercepcion,
  editarPercepcion,
  fusionarSugerencias,
  importeDePercepcion,
  percepcionesDesdeDetalle,
  percepcionesSugeridas,
  percepcionManual,
  resolverPercepciones,
  type ReferenciaDePercepciones,
} from './sugerenciasDePercepcion'

function proveedor(sobrescribir: Partial<ProveedorListado> = {}): ProveedorListado {
  return {
    id: 1,
    razonSocial: 'Proveedor',
    nombreFantasia: null,
    cuit: null,
    idCondicionFiscal: 1,
    domicilio: null,
    telefono: null,
    email: null,
    vendedor: null,
    celularVendedor: null,
    supervisor: null,
    celularSupervisor: null,
    margen: null,
    observaciones: null,
    activo: true,
    idEmpresa: null,
    percibeIibb: true,
    percibeIva: true,
    preciosIncluyenIva: true,
    ...sobrescribir,
  }
}

function tipo(sobrescribir: Partial<TipoComprobanteListado> = {}): TipoComprobanteListado {
  return {
    id: 5,
    clase: 'Compra',
    codigo: 'C-FA',
    nombre: 'Factura A',
    letra: 'A',
    signo: 1,
    discriminaIva: true,
    esFiscal: false,
    afectaStock: true,
    codigoAfip: null,
    activo: true,
    registraLibroIva: true,
    ...sobrescribir,
  }
}

function puntoVenta(sobrescribir: Partial<PuntoVentaListado> = {}): PuntoVentaListado {
  return {
    id: 2,
    idTenant: 1,
    idEmpresa: 10,
    nombre: 'Casa Central',
    domicilio: null,
    horario: null,
    whatsapp: null,
    instagram: null,
    facebook: null,
    web: null,
    nombreTenant: null,
    razonSocialEmpresa: null,
    modo: 'Web',
    ...sobrescribir,
  }
}

function empresa(sobrescribir: Partial<EmpresaListado> = {}): EmpresaListado {
  return {
    id: 10,
    idTenant: 1,
    razonSocial: 'Empresa',
    nombreFantasia: null,
    cuit: null,
    nombreTenant: null,
    alicuotaPercepcionIibb: 3,
    alicuotaPercepcionIva: 1.5,
    ...sobrescribir,
  }
}

function referencia(sobrescribir: Partial<ReferenciaDePercepciones> = {}): ReferenciaDePercepciones {
  return {
    proveedores: [proveedor()],
    tipos: [tipo(), tipo({ id: 7, codigo: 'C-RM', discriminaIva: false, registraLibroIva: false }), tipo({ id: 6, codigo: 'C-FB', discriminaIva: false })],
    puntosVenta: [puntoVenta()],
    empresas: [empresa()],
    ...sobrescribir,
  }
}

function encabezado(sobrescribir: Partial<EncabezadoDeCompraFormulario> = {}): EncabezadoDeCompraFormulario {
  return {
    idProveedor: 1,
    idTipoComprobante: 5,
    idPuntoVenta: 2,
    numeroExterno: '',
    fechaComprobante: '',
    observaciones: '',
    idOrdenCompra: null,
    discriminaIva: null,
    ivaImpreso: {},
    preciosIncluyenIva: false,
    percepciones: [],
    percepcionesDescartadas: [],
    ...sobrescribir,
  }
}

function fila(sobrescribir: Partial<PercepcionFormulario> = {}): PercepcionFormulario {
  return { tipo: 'iibb', baseImponible: null, alicuota: 3, importe: null, automatica: true, ...sobrescribir }
}

function totales(sobrescribir: Partial<TotalesDeCompra> = {}): TotalesDeCompra {
  return {
    items: [],
    subtotal: 1000,
    descuentoTotal: 0,
    ivaTotal: 0,
    total: 1000,
    alicuotas: [],
    percepcionesTotal: 0,
    ...sobrescribir,
  }
}

describe('importeDePercepcion', () => {
  it('redondea base por alícuota sobre cien a dos decimales, mitad hacia arriba', () => {
    expect(importeDePercepcion(1000, 3)).toBe(30)
    expect(importeDePercepcion(333.33, 3)).toBe(10)
    expect(importeDePercepcion(100.5, 1.5)).toBe(1.51)
    expect(importeDePercepcion(0, 3)).toBe(0)
  })
})

describe('baseDePercepcion', () => {
  it('con IVA discriminado suma el neto de las alícuotas gravadas y deja afuera el 0%', () => {
    const base = baseDePercepcion(
      totales({
        alicuotas: [
          { idAlicuotaIva: 1, porcentaje: 21, neto: 1000, ivaCalculado: 210, iva: 210, fueraDeTolerancia: false },
          { idAlicuotaIva: 2, porcentaje: 10.5, neto: 200.5, ivaCalculado: 21.05, iva: 21.05, fueraDeTolerancia: false },
          { idAlicuotaIva: 5, porcentaje: 0, neto: 300, ivaCalculado: 0, iva: 0, fueraDeTolerancia: false },
        ],
      }),
      true,
    )

    expect(base).toBe(1200.5)
  })

  it('sin IVA discriminado no hay neto: la base es lo comprado, subtotal menos descuento', () => {
    expect(baseDePercepcion(totales({ subtotal: 1000, descuentoTotal: 100 }), false)).toBe(900)
  })

  it('un comprobante discriminado sin alícuotas gravadas tiene base cero', () => {
    expect(baseDePercepcion(totales({ alicuotas: [] }), true)).toBe(0)
  })
})

describe('percepcionesSugeridas', () => {
  it('sugiere las dos cuando el proveedor las percibe, la empresa tiene la alícuota y la factura discrimina', () => {
    const sugeridas = percepcionesSugeridas(encabezado(), referencia())

    expect(sugeridas).toEqual([
      { tipo: 'iibb', baseImponible: null, alicuota: 3, importe: null, automatica: true },
      { tipo: 'iva', baseImponible: null, alicuota: 1.5, importe: null, automatica: true },
    ])
  })

  it('no sugiere el impuesto que el proveedor no percibe', () => {
    const sugeridas = percepcionesSugeridas(
      encabezado(),
      referencia({ proveedores: [proveedor({ percibeIibb: false })] }),
    )

    expect(sugeridas.map((s) => s.tipo)).toEqual(['iva'])
  })

  it('no sugiere el impuesto cuya alícuota la empresa no informó', () => {
    const sugeridas = percepcionesSugeridas(
      encabezado(),
      referencia({ empresas: [empresa({ alicuotaPercepcionIva: null })] }),
    )

    expect(sugeridas.map((s) => s.tipo)).toEqual(['iibb'])
  })

  it('una alícuota de cero informada sí sugiere (es un dato, no una ausencia)', () => {
    const sugeridas = percepcionesSugeridas(
      encabezado(),
      referencia({ empresas: [empresa({ alicuotaPercepcionIibb: 0, alicuotaPercepcionIva: null })] }),
    )

    expect(sugeridas).toEqual([{ tipo: 'iibb', baseImponible: null, alicuota: 0, importe: null, automatica: true }])
  })

  it('toma la alícuota de la empresa del punto de venta de la compra, no de otra', () => {
    const sugeridas = percepcionesSugeridas(
      encabezado({ idPuntoVenta: 3 }),
      referencia({
        puntosVenta: [puntoVenta(), puntoVenta({ id: 3, idEmpresa: 11 })],
        empresas: [empresa(), empresa({ id: 11, alicuotaPercepcionIibb: 5, alicuotaPercepcionIva: null })],
      }),
    )

    expect(sugeridas).toEqual([{ tipo: 'iibb', baseImponible: null, alicuota: 5, importe: null, automatica: true }])
  })

  it('en una factura que no discrimina solo sugiere IIBB', () => {
    const sugeridas = percepcionesSugeridas(encabezado({ idTipoComprobante: 6 }), referencia())

    expect(sugeridas.map((s) => s.tipo)).toEqual(['iibb'])
  })

  it('un tipo que no registra libro IVA no sugiere nada, discrimine o no', () => {
    expect(percepcionesSugeridas(encabezado({ idTipoComprobante: 7 }), referencia())).toEqual([])
    expect(percepcionesSugeridas(encabezado({ idTipoComprobante: 7, discriminaIva: true }), referencia())).toEqual([])
  })

  it('sin proveedor, tipo, punto de venta o empresas conocidas no sugiere nada', () => {
    expect(percepcionesSugeridas(encabezado({ idProveedor: '' }), referencia())).toEqual([])
    expect(percepcionesSugeridas(encabezado({ idTipoComprobante: '' }), referencia())).toEqual([])
    expect(percepcionesSugeridas(encabezado({ idPuntoVenta: '' }), referencia())).toEqual([])
    expect(percepcionesSugeridas(encabezado(), referencia({ empresas: [] }))).toEqual([])
  })
})

describe('fusionarSugerencias', () => {
  it('agrega lo sugerido que falta', () => {
    expect(fusionarSugerencias([], [fila()], [])).toEqual([fila()])
  })

  it('respeta la fila que el operador tocó o agregó aunque ya no se sugiera', () => {
    const manual = fila({ automatica: false, baseImponible: 500, importe: 17 })

    expect(fusionarSugerencias([manual], [], [])).toEqual([manual])
    expect(fusionarSugerencias([manual], [fila({ alicuota: 9 })], [])).toEqual([manual])
  })

  it('retira la automática que dejó de corresponder', () => {
    expect(fusionarSugerencias([fila()], [], [])).toEqual([])
  })

  it('actualiza la alícuota de la automática que sigue correspondiendo', () => {
    expect(fusionarSugerencias([fila({ alicuota: 3 })], [fila({ alicuota: 4 })], [])).toEqual([fila({ alicuota: 4 })])
  })

  it('no vuelve a agregar un tipo que el operador quitó', () => {
    expect(fusionarSugerencias([], [fila()], ['iibb'])).toEqual([])
    expect(fusionarSugerencias([], [fila(), fila({ tipo: 'iva' })], ['iibb']).map((p) => p.tipo)).toEqual(['iva'])
  })
})

describe('conSugerenciasDePercepcion / alElegirProveedor', () => {
  it('elegir un proveedor pre-carga su modo de precios y sus percepciones', () => {
    const resultado = alElegirProveedor(encabezado({ idProveedor: '' }), 1, referencia())

    expect(resultado.idProveedor).toBe(1)
    expect(resultado.preciosIncluyenIva).toBe(true)
    expect(resultado.percepciones.map((p) => p.tipo)).toEqual(['iibb', 'iva'])
  })

  it('cambiar a un proveedor sin esos flags apaga el modo y retira las sugeridas', () => {
    const referenciaConOtro = referencia({
      proveedores: [proveedor(), proveedor({ id: 2, percibeIibb: false, percibeIva: false, preciosIncluyenIva: false })],
    })
    const conPrimero = alElegirProveedor(encabezado({ idProveedor: '' }), 1, referenciaConOtro)

    const conSegundo = alElegirProveedor(conPrimero, 2, referenciaConOtro)

    expect(conSegundo.preciosIncluyenIva).toBe(false)
    expect(conSegundo.percepciones).toEqual([])
  })

  it('cambiar de proveedor reinicia los tipos descartados del anterior', () => {
    const resultado = alElegirProveedor(encabezado({ percepcionesDescartadas: ['iibb'] }), 1, referencia())

    expect(resultado.percepcionesDescartadas).toEqual([])
    expect(resultado.percepciones.map((p) => p.tipo)).toEqual(['iibb', 'iva'])
  })

  it('volver a elegir "Elegir…" deja el modo apagado y sin sugerencias', () => {
    const resultado = alElegirProveedor(alElegirProveedor(encabezado({ idProveedor: '' }), 1, referencia()), '', referencia())

    expect(resultado.preciosIncluyenIva).toBe(false)
    expect(resultado.percepciones).toEqual([])
  })

  it('cambiar el tipo a uno sin libro IVA retira las automáticas y conserva las manuales', () => {
    const manual = fila({ tipo: 'iva', automatica: false, baseImponible: 10, importe: 1 })
    const inicial = encabezado({ percepciones: [fila(), manual] })

    const resultado = conSugerenciasDePercepcion({ ...inicial, idTipoComprobante: 7 }, referencia())

    expect(resultado.percepciones).toEqual([manual])
  })
})

describe('resolverPercepciones', () => {
  it('llena base e importe de las automáticas a partir de la base vigente', () => {
    const [resuelta] = resolverPercepciones([fila({ alicuota: 3 })], 1500)

    expect(resuelta).toMatchObject({ baseImponible: 1500, importe: 45, automatica: true })
  })

  it('una automática sin alícuota queda sin importe', () => {
    const [resuelta] = resolverPercepciones([fila({ alicuota: null })], 1500)

    expect(resuelta.importe).toBeNull()
  })

  it('no toca una fila manual: manda el importe que se tipeó', () => {
    const manual = fila({ automatica: false, baseImponible: 100, alicuota: 3, importe: 7 })

    expect(resolverPercepciones([manual], 9999)).toEqual([manual])
  })
})

describe('editarPercepcion', () => {
  const resuelta = fila({ baseImponible: 1000, importe: 30 })

  it('tocar el importe lo deja verbatim, congela la fila y conserva base y alícuota', () => {
    expect(editarPercepcion(resuelta, { importe: 31.07 })).toEqual({
      tipo: 'iibb',
      baseImponible: 1000,
      alicuota: 3,
      importe: 31.07,
      automatica: false,
    })
  })

  it('tocar la base re-propone el importe con la alícuota vigente', () => {
    expect(editarPercepcion(resuelta, { baseImponible: 2000 })).toMatchObject({
      baseImponible: 2000,
      importe: 60,
      automatica: false,
    })
  })

  it('tocar la alícuota re-propone el importe con la base vigente', () => {
    expect(editarPercepcion(resuelta, { alicuota: 2 })).toMatchObject({ alicuota: 2, importe: 20, automatica: false })
  })

  it('vaciar la base no inventa un importe: conserva el que había', () => {
    expect(editarPercepcion(resuelta, { baseImponible: null })).toMatchObject({
      baseImponible: null,
      importe: 30,
      automatica: false,
    })
  })
})

describe('percepcionManual', () => {
  it('arranca congelada con la base vigente y el importe propuesto cuando la empresa tiene alícuota', () => {
    expect(percepcionManual('iibb', 1000, 3)).toEqual({
      tipo: 'iibb',
      baseImponible: 1000,
      alicuota: 3,
      importe: 30,
      automatica: false,
    })
  })

  it('sin alícuota conocida deja alícuota e importe vacíos para que se carguen a mano', () => {
    expect(percepcionManual('iva', 1000, null)).toMatchObject({ alicuota: null, importe: null, automatica: false })
  })
})

describe('alicuotaDeEmpresaParaCompra', () => {
  it('devuelve la alícuota del impuesto para la empresa del punto de venta elegido', () => {
    expect(alicuotaDeEmpresaParaCompra(encabezado(), referencia(), 'iibb')).toBe(3)
    expect(alicuotaDeEmpresaParaCompra(encabezado(), referencia(), 'iva')).toBe(1.5)
  })

  it('devuelve null si no hay punto de venta o la empresa no se conoce', () => {
    expect(alicuotaDeEmpresaParaCompra(encabezado({ idPuntoVenta: '' }), referencia(), 'iibb')).toBeNull()
    expect(alicuotaDeEmpresaParaCompra(encabezado(), referencia({ empresas: [] }), 'iibb')).toBeNull()
  })
})

describe('percepcionesDesdeDetalle', () => {
  it('convierte lo persistido en filas manuales, incluso con base e importe ocultos', () => {
    expect(
      percepcionesDesdeDetalle([
        { tipo: 'iibb', alicuota: 3, baseImponible: 1000, importe: 30 },
        { tipo: 'iva', alicuota: 1.5, baseImponible: null, importe: null },
      ]),
    ).toEqual([
      { tipo: 'iibb', alicuota: 3, baseImponible: 1000, importe: 30, automatica: false },
      { tipo: 'iva', alicuota: 1.5, baseImponible: null, importe: null, automatica: false },
    ])
  })
})
