import { describe, expect, it } from 'vitest'
import {
  elegirAlicuotaPorDefecto,
  etiquetaDeProveedor,
  insertarOrdenadoPor,
  opcionesConValorActual,
  ordenarProveedoresPorEtiqueta,
} from './helpers'
import type { AlicuotaIvaListado, MarcaListado, ProveedorListado } from '../../api/tipos'

function alicuotaFixture(sobrescribir: Partial<AlicuotaIvaListado> = {}): AlicuotaIvaListado {
  return { id: 1, nombre: 'IVA', porcentaje: 21, codigoAfip: 5, activo: true, ...sobrescribir }
}

function marcaFixture(sobrescribir: Partial<MarcaListado> = {}): MarcaListado {
  return { id: 1, nombre: 'Alfa', activo: true, idEmpresa: null, ...sobrescribir }
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

  // Con `sensitivity: 'base'` las dos etiquetas comparan igual y el sort estable conserva el orden
  // de entrada; con la sensibilidad por defecto (case-sensible) se invierten.
  it('con dos etiquetas que difieren solo en mayúscula/minúscula, conserva el orden de entrada (sensitivity: base) — sin el argumento, el orden se invierte', () => {
    const mayuscula = proveedorFixture({ id: 1, razonSocial: 'Ana', nombreFantasia: null })
    const minuscula = proveedorFixture({ id: 2, razonSocial: 'ana', nombreFantasia: null })

    const ordenados = ordenarProveedoresPorEtiqueta([mayuscula, minuscula])

    expect(ordenados.map((p) => p.id)).toEqual([1, 2])
  })

  // En la intercalación española la Ñ es una letra entre la N y la O; en otros locales (p. ej. `en`)
  // se ordena como una N con diacrítico, antes de "Nube".
  it('ordena la Ñ después de la N según la intercalación española', () => {
    const conEnie = proveedorFixture({ id: 1, razonSocial: 'Ñu Distribuciones', nombreFantasia: null })
    const conEne = proveedorFixture({ id: 2, razonSocial: 'Nube SA', nombreFantasia: null })

    const ordenados = ordenarProveedoresPorEtiqueta([conEnie, conEne])

    expect(ordenados.map((p) => p.id)).toEqual([2, 1])
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

// ---- opcionesConValorActual --------------------------------------------------------------------
// Cláusula bajo prueba: `item.activo || item.id === idActual` en helpers.ts — sin la mitad
// `item.activo`, el alta ofrecería inactivas; sin `item.id === idActual`, la edición perdería el
// valor actual del artículo cuando está inactivo (mutation-proof-tests).

describe('opcionesConValorActual', () => {
  it('en alta (idActual vacío), ofrece solo las activas', () => {
    const listado = [
      marcaFixture({ id: 1, nombre: 'Activa', activo: true }),
      marcaFixture({ id: 2, nombre: 'Inactiva', activo: false }),
    ]

    expect(opcionesConValorActual(listado, '').map((m) => m.id)).toEqual([1])
  })

  it('en edición, incluye el valor actual del artículo aunque esté inactivo', () => {
    const listado = [
      marcaFixture({ id: 1, nombre: 'Activa', activo: true }),
      marcaFixture({ id: 2, nombre: 'Inactiva actual', activo: false }),
    ]

    expect(opcionesConValorActual(listado, 2).map((m) => m.id)).toEqual([1, 2])
  })

  it('en edición, NO ofrece una inactiva que no sea el valor actual del artículo', () => {
    const listado = [
      marcaFixture({ id: 1, nombre: 'Activa', activo: true }),
      marcaFixture({ id: 2, nombre: 'Inactiva actual', activo: false }),
      marcaFixture({ id: 3, nombre: 'Inactiva ajena', activo: false }),
    ]

    // idActual = 2: la marca 3 es inactiva y de OTRO artículo — nunca debe aparecer.
    expect(opcionesConValorActual(listado, 2).map((m) => m.id)).toEqual([1, 2])
  })

  it('con un idActual que no existe en el listado (baja lógica del catálogo referenciado), da lo mismo que sin valor actual', () => {
    const listado = [marcaFixture({ id: 1, nombre: 'Activa', activo: true }), marcaFixture({ id: 2, nombre: 'Inactiva', activo: false })]

    // 999: FK colgante — ninguna marca visible tiene ese id (dangling-fk-read-models). El
    // llamador es quien decide tratar esto como "sin asignar"; acá solo no debe inventar una
    // opción fantasma para el 999.
    expect(opcionesConValorActual(listado, 999)).toEqual(opcionesConValorActual(listado, ''))
    expect(opcionesConValorActual(listado, 999).map((m) => m.id)).toEqual([1])
  })
})
