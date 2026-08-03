import { describe, expect, it } from 'vitest'
import { descriptorListasPrecio } from './catalogos'
import type { ListaPrecioListado } from './tipos'

function listaFixture(sobrescribir: Partial<ListaPrecioListado> = {}): ListaPrecioListado {
  return {
    id: 1,
    nombre: 'Lista mayorista',
    activo: true,
    idEmpresa: null,
    esDefault: false,
    modo: 'Fija',
    idListaBase: null,
    porcentaje: null,
    ...sobrescribir,
  }
}

describe('descriptorListasPrecio.aAlta', () => {
  it('fuerza idListaBase y porcentaje a null en modo Fija aunque el formulario tenga valores residuales', () => {
    const alta = descriptorListasPrecio.aAlta('Lista A', true, {
      esDefault: false,
      modo: 'Fija',
      idListaBase: '3',
      porcentaje: '10',
    })

    expect(alta.idListaBase).toBeNull()
    expect(alta.porcentaje).toBeNull()
  })

  it('convierte idListaBase y porcentaje a número en modo Derivada', () => {
    const alta = descriptorListasPrecio.aAlta('Lista B', true, {
      esDefault: false,
      modo: 'Derivada',
      idListaBase: '3',
      porcentaje: '10.5',
    })

    expect(alta.idListaBase).toBe(3)
    expect(alta.porcentaje).toBe(10.5)
  })

  it('convierte los strings vacíos a null en modo Derivada', () => {
    const alta = descriptorListasPrecio.aAlta('Lista C', true, {
      esDefault: false,
      modo: 'Derivada',
      idListaBase: '',
      porcentaje: '',
    })

    expect(alta.idListaBase).toBeNull()
    expect(alta.porcentaje).toBeNull()
  })

  it('booleaniza esDefault', () => {
    const altaTrue = descriptorListasPrecio.aAlta('Lista D', true, {
      esDefault: 'algo' as unknown as boolean,
      modo: 'Fija',
      idListaBase: '',
      porcentaje: '',
    })
    const altaFalse = descriptorListasPrecio.aAlta('Lista E', true, {
      esDefault: false,
      modo: 'Fija',
      idListaBase: '',
      porcentaje: '',
    })

    expect(altaTrue.esDefault).toBe(true)
    expect(altaFalse.esDefault).toBe(false)
  })

  it('pasa nombre y activo sin transformar, e idEmpresa siempre en null', () => {
    const alta = descriptorListasPrecio.aAlta('Lista F', false, {
      esDefault: false,
      modo: 'Fija',
      idListaBase: '',
      porcentaje: '',
    })

    expect(alta.nombre).toBe('Lista F')
    expect(alta.activo).toBe(false)
    expect(alta.idEmpresa).toBeNull()
  })
})

describe('descriptorListasPrecio.aValores', () => {
  it('mapea idListaBase y porcentaje null a string vacío', () => {
    const valores = descriptorListasPrecio.aValores(listaFixture({ idListaBase: null, porcentaje: null }))

    expect(valores.idListaBase).toBe('')
    expect(valores.porcentaje).toBe('')
  })

  it('mapea idListaBase y porcentaje no nulos a String(...)', () => {
    const valores = descriptorListasPrecio.aValores(listaFixture({ idListaBase: 7, porcentaje: 12.5 }))

    expect(valores.idListaBase).toBe('7')
    expect(valores.porcentaje).toBe('12.5')
  })

  it('pasa esDefault y modo sin transformar', () => {
    const valores = descriptorListasPrecio.aValores(listaFixture({ esDefault: true, modo: 'Derivada' }))

    expect(valores.esDefault).toBe(true)
    expect(valores.modo).toBe('Derivada')
  })
})

describe('descriptorListasPrecio campo idListaBase — opcionesDesdeListado', () => {
  const campoIdListaBase = descriptorListasPrecio.campos.find((c) => c.clave === 'idListaBase')
  if (!campoIdListaBase?.opcionesDesdeListado) {
    throw new Error('El campo idListaBase debe declarar opcionesDesdeListado')
  }
  const opcionesDesdeListado = campoIdListaBase.opcionesDesdeListado

  const listas: ListaPrecioListado[] = [
    listaFixture({ id: 1, nombre: 'Fija activa', activo: true, modo: 'Fija' }),
    listaFixture({ id: 2, nombre: 'Fija inactiva', activo: false, modo: 'Fija' }),
    listaFixture({ id: 3, nombre: 'Derivada activa', activo: true, modo: 'Derivada' }),
    listaFixture({ id: 4, nombre: 'Otra fija activa', activo: true, modo: 'Fija' }),
  ]

  it('filtra las listas inactivas', () => {
    const opciones = opcionesDesdeListado(listas, null)

    expect(opciones.some((o) => o.valor === '2')).toBe(false)
  })

  it('filtra las listas en modo Derivada', () => {
    const opciones = opcionesDesdeListado(listas, null)

    expect(opciones.some((o) => o.valor === '3')).toBe(false)
  })

  it('excluye la lista actual cuando idActual está definido (autoexclusión al editar)', () => {
    const opciones = opcionesDesdeListado(listas, 1)

    expect(opciones.map((o) => o.valor)).toEqual(['4'])
  })

  it('incluye todas las Fija activas cuando idActual es null (alta nueva)', () => {
    const opciones = opcionesDesdeListado(listas, null)

    expect(opciones.map((o) => o.valor)).toEqual(['1', '4'])
  })

  it('mapea cada opción a { valor: String(id), etiqueta: nombre }', () => {
    const opciones = opcionesDesdeListado(listas, null)

    expect(opciones).toEqual([
      { valor: '1', etiqueta: 'Fija activa' },
      { valor: '4', etiqueta: 'Otra fija activa' },
    ])
  })
})
