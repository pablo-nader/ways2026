import { describe, expect, it } from 'vitest'
import { analizarRutaModal } from './rutaModal'

describe('analizarRutaModal', () => {
  it('/articulos (sin sufijo) no abre ningún modal', () => {
    expect(analizarRutaModal('/articulos')).toEqual({ modo: null, idParam: null })
  })

  it('/articulos/ (con barra final) tampoco abre ningún modal', () => {
    expect(analizarRutaModal('/articulos/')).toEqual({ modo: null, idParam: null })
  })

  it('/articulos/create abre el modo crear, sin idParam', () => {
    expect(analizarRutaModal('/articulos/create')).toEqual({ modo: 'crear', idParam: null })
  })

  it('/articulos/edit/5 abre el modo editar con idParam "5"', () => {
    expect(analizarRutaModal('/articulos/edit/5')).toEqual({ modo: 'editar', idParam: '5' })
  })

  it('/articulos/edit/abc (id no numérico) igual entra en modo editar — la validación del número es responsabilidad del llamador', () => {
    expect(analizarRutaModal('/articulos/edit/abc')).toEqual({ modo: 'editar', idParam: 'abc' })
  })

  it('/articulos/edit (sin id) entra en modo editar con idParam null', () => {
    expect(analizarRutaModal('/articulos/edit')).toEqual({ modo: 'editar', idParam: null })
  })

  it('un segmento desconocido no abre ningún modal (sin crashear)', () => {
    expect(analizarRutaModal('/articulos/lo-que-sea')).toEqual({ modo: null, idParam: null })
  })

  it('/articulos/create con un segmento extra no cuenta como crear válido', () => {
    expect(analizarRutaModal('/articulos/create/algo')).toEqual({ modo: null, idParam: null })
  })
})
