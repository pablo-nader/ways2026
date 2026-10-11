import { describe, expect, it } from 'vitest'
import type { ListaPrecioListado } from '../../api/tipos'
import { ordenarListasParaTabla } from './ordenDeListas'

function lista(id: number, nombre: string, modo: 'Fija' | 'Derivada'): ListaPrecioListado {
  return {
    id,
    nombre,
    activo: true,
    idEmpresa: null,
    esDefault: false,
    modo,
    idListaBase: modo === 'Derivada' ? 1 : null,
    porcentaje: modo === 'Derivada' ? 10 : null,
  }
}

describe('ordenarListasParaTabla', () => {
  it('pone las manuales primero y las derivadas después, cada grupo por nombre', () => {
    const entrada = [
      lista(1, 'Mayorista', 'Derivada'),
      lista(2, 'General', 'Fija'),
      lista(3, 'Gremio', 'Derivada'),
      lista(4, 'Especial', 'Fija'),
    ]

    expect(ordenarListasParaTabla(entrada).map((l) => l.nombre)).toEqual(['Especial', 'General', 'Gremio', 'Mayorista'])
  })

  it('ordena por nombre sin distinguir mayúsculas ni acentos', () => {
    const entrada = [lista(1, 'zeta', 'Fija'), lista(2, 'Álamo', 'Fija'), lista(3, 'beta', 'Fija')]

    expect(ordenarListasParaTabla(entrada).map((l) => l.nombre)).toEqual(['Álamo', 'beta', 'zeta'])
  })

  it('no muta la lista recibida', () => {
    const entrada = [lista(1, 'B', 'Derivada'), lista(2, 'A', 'Fija')]

    ordenarListasParaTabla(entrada)

    expect(entrada.map((l) => l.id)).toEqual([1, 2])
  })
})
