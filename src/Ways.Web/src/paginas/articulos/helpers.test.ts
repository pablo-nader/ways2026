import { describe, expect, it } from 'vitest'
import { elegirAlicuotaPorDefecto, etiquetaDeProveedor, insertarOrdenadoPor, ordenarProveedoresPorEtiqueta } from './helpers'
import type { AlicuotaIvaListado, ProveedorListado } from '../../api/tipos'

function alicuotaFixture(sobrescribir: Partial<AlicuotaIvaListado> = {}): AlicuotaIvaListado {
  return { id: 1, nombre: 'IVA', porcentaje: 21, codigoAfip: 5, activo: true, ...sobrescribir }
}

function proveedorFixture(sobrescribir: Partial<ProveedorListado> = {}): ProveedorListado {
  return {
    id: 1,
    razonSocial: 'Distribuidora Sur SRL',
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
    ...sobrescribir,
  }
}

// ---- elegirAlicuotaPorDefecto ------------------------------------------------------------------
// Cláusula bajo prueba: `a.porcentaje === PORCENTAJE_ALICUOTA_POR_DEFECTO` en helpers.ts. El
// endpoint real devuelve las alícuotas ordenadas por porcentaje DESC (27% primero), así que un
// test que ponga el 21% en la posición 0 no prueba nada — la mutación (skill mutation-proof-tests)
// se verifica con una lista donde el 21% NO es el primero.

describe('elegirAlicuotaPorDefecto', () => {
  it('con el 21% en cualquier posición (no la primera), elige su id — no la primera del arreglo', () => {
    const alicuotas = [
      alicuotaFixture({ id: 10, porcentaje: 27 }),
      alicuotaFixture({ id: 20, porcentaje: 21 }),
      alicuotaFixture({ id: 30, porcentaje: 10.5 }),
    ]

    expect(elegirAlicuotaPorDefecto(alicuotas)).toBe(20)
  })

  it('con el 21% ya en la primera posición, también lo elige (caso trivial)', () => {
    const alicuotas = [alicuotaFixture({ id: 20, porcentaje: 21 }), alicuotaFixture({ id: 10, porcentaje: 10.5 })]

    expect(elegirAlicuotaPorDefecto(alicuotas)).toBe(20)
  })

  it('sin ninguna alícuota del 21%, cae a la primera del arreglo tal cual llega', () => {
    const alicuotas = [alicuotaFixture({ id: 10, porcentaje: 27 }), alicuotaFixture({ id: 30, porcentaje: 10.5 })]

    expect(elegirAlicuotaPorDefecto(alicuotas)).toBe(10)
  })

  it('con el arreglo vacío, devuelve string vacío (sin alícuota seleccionable todavía)', () => {
    expect(elegirAlicuotaPorDefecto([])).toBe('')
  })
})

// ---- etiquetaDeProveedor / ordenarProveedoresPorEtiqueta ---------------------------------------

describe('etiquetaDeProveedor', () => {
  it('con nombre de fantasía cargado, muestra SOLO el nombre de fantasía (recortado)', () => {
    const proveedor = proveedorFixture({ razonSocial: 'Distribuidora Sur SRL', nombreFantasia: '  La Sureña  ' })
    expect(etiquetaDeProveedor(proveedor)).toBe('La Sureña')
  })

  it('sin nombre de fantasía (null), cae a la razón social', () => {
    const proveedor = proveedorFixture({ razonSocial: 'Distribuidora Sur SRL', nombreFantasia: null })
    expect(etiquetaDeProveedor(proveedor)).toBe('Distribuidora Sur SRL')
  })

  it('con nombre de fantasía en blanco (solo espacios), cae a la razón social — no muestra "" ni espacios', () => {
    const proveedor = proveedorFixture({ razonSocial: 'Distribuidora Sur SRL', nombreFantasia: '   ' })
    expect(etiquetaDeProveedor(proveedor)).toBe('Distribuidora Sur SRL')
  })
})

describe('ordenarProveedoresPorEtiqueta', () => {
  it('ordena por la ETIQUETA mostrada, no por razonSocial — un proveedor con razonSocial "Z…" pero fantasía "A…" queda primero', () => {
    const zetaConFantasiaA = proveedorFixture({ id: 1, razonSocial: 'Zeta Insumos SA', nombreFantasia: 'Almacén Central' })
    const beta = proveedorFixture({ id: 2, razonSocial: 'Beta Distribuciones', nombreFantasia: null })

    const ordenados = ordenarProveedoresPorEtiqueta([beta, zetaConFantasiaA])

    // Por razonSocial cruda el orden sería [Beta, Zeta] — por etiqueta ("Almacén Central" vs
    // "Beta Distribuciones") es al revés.
    expect(ordenados.map((p) => p.id)).toEqual([1, 2])
  })

  it("usa comparación case/acento-insensible ('es', sensitivity: base)", () => {
    const conAcento = proveedorFixture({ id: 1, razonSocial: 'útil SRL', nombreFantasia: null })
    const sinAcento = proveedorFixture({ id: 2, razonSocial: 'Util Hnos', nombreFantasia: null })
    const mayuscula = proveedorFixture({ id: 3, razonSocial: 'ZETA', nombreFantasia: null })

    const ordenados = ordenarProveedoresPorEtiqueta([mayuscula, sinAcento, conAcento])

    expect(ordenados.map((p) => p.id)).toEqual([2, 1, 3])
  })
})

// ---- insertarOrdenadoPor ------------------------------------------------------------------------

describe('insertarOrdenadoPor', () => {
  const clave = (item: { nombre: string }) => item.nombre

  it('inserta en el medio de una lista ya ordenada', () => {
    const lista = [{ nombre: 'Bebidas' }, { nombre: 'Lácteos' }]
    const resultado = insertarOrdenadoPor(lista, { nombre: 'Gaseosas' }, clave)
    expect(resultado.map((i) => i.nombre)).toEqual(['Bebidas', 'Gaseosas', 'Lácteos'])
  })

  it('inserta al principio cuando ordena antes que todos', () => {
    const lista = [{ nombre: 'Lácteos' }, { nombre: 'Panificados' }]
    const resultado = insertarOrdenadoPor(lista, { nombre: 'Almacén' }, clave)
    expect(resultado.map((i) => i.nombre)).toEqual(['Almacén', 'Lácteos', 'Panificados'])
  })

  it('inserta al final cuando ordena después que todos', () => {
    const lista = [{ nombre: 'Almacén' }, { nombre: 'Bebidas' }]
    const resultado = insertarOrdenadoPor(lista, { nombre: 'Zapallos' }, clave)
    expect(resultado.map((i) => i.nombre)).toEqual(['Almacén', 'Bebidas', 'Zapallos'])
  })

  it('en una lista vacía, el nuevo item queda solo', () => {
    expect(insertarOrdenadoPor([], { nombre: 'Almacén' }, clave)).toEqual([{ nombre: 'Almacén' }])
  })

  it('no muta la lista original', () => {
    const lista = [{ nombre: 'Bebidas' }]
    insertarOrdenadoPor(lista, { nombre: 'Almacén' }, clave)
    expect(lista).toEqual([{ nombre: 'Bebidas' }])
  })
})
