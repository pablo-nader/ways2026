import { describe, expect, it } from 'vitest'
import { cuerpoDeEdicionDeFamilia } from './edicion'
import type { FormularioDeFamilia } from './ModalDeFamilia'

function formulario(sobrescribir: Partial<FormularioDeFamilia> = {}): FormularioDeFamilia {
  return { id: 1, nombre: '  Sabores ', activo: true, permiteDesglosarCosto: false, permiteDesglosarCostoInicial: false, ...sobrescribir }
}

describe('cuerpoDeEdicionDeFamilia', () => {
  it('sin cambiar la casilla no manda la marca, esté apagada o encendida', () => {
    expect(cuerpoDeEdicionDeFamilia(formulario())).toEqual({ nombre: 'Sabores', activo: true })
    expect(cuerpoDeEdicionDeFamilia(formulario({ permiteDesglosarCosto: true, permiteDesglosarCostoInicial: true }))).toEqual({
      nombre: 'Sabores',
      activo: true,
    })
    expect(cuerpoDeEdicionDeFamilia(formulario())).not.toHaveProperty('permiteDesglosarCosto')
  })

  it('al encenderla manda true', () => {
    expect(cuerpoDeEdicionDeFamilia(formulario({ permiteDesglosarCosto: true }))).toEqual({
      nombre: 'Sabores',
      activo: true,
      permiteDesglosarCosto: true,
    })
  })

  it('al apagarla manda false', () => {
    expect(cuerpoDeEdicionDeFamilia(formulario({ permiteDesglosarCostoInicial: true }))).toEqual({
      nombre: 'Sabores',
      activo: true,
      permiteDesglosarCosto: false,
    })
  })
})
