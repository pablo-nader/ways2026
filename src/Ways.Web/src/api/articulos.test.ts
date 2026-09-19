import { describe, expect, it } from 'vitest'
import { construirQueryDeGrillaDeArticulos, filtrosDeGrillaDeArticulosVacios } from './articulos'
import type { FiltrosDeGrillaDeArticulos } from './tipos'

function filtros(sobrescribir: Partial<FiltrosDeGrillaDeArticulos> = {}): FiltrosDeGrillaDeArticulos {
  return { ...filtrosDeGrillaDeArticulosVacios(), ...sobrescribir }
}

describe('construirQueryDeGrillaDeArticulos', () => {
  it('con los filtros vacíos, solo manda pagina y tamanio', () => {
    expect(construirQueryDeGrillaDeArticulos(filtros())).toBe('?pagina=1&tamanio=25')
  })

  it('manda codigo recortado, nunca en blanco', () => {
    expect(construirQueryDeGrillaDeArticulos(filtros({ codigo: '  A001  ' }))).toContain('codigo=A001')
    expect(construirQueryDeGrillaDeArticulos(filtros({ codigo: '   ' }))).not.toContain('codigo=')
  })

  it('manda nombre recortado, nunca en blanco', () => {
    expect(construirQueryDeGrillaDeArticulos(filtros({ nombre: '  Coca  ' }))).toContain('nombre=Coca')
    expect(construirQueryDeGrillaDeArticulos(filtros({ nombre: '' }))).not.toContain('nombre=')
  })

  it('manda precioDesde/precioHasta solo cuando no son null', () => {
    const cadena = construirQueryDeGrillaDeArticulos(filtros({ precioDesde: 10, precioHasta: 99.5 }))
    expect(cadena).toContain('precioDesde=10')
    expect(cadena).toContain('precioHasta=99.5')
    expect(construirQueryDeGrillaDeArticulos(filtros())).not.toContain('precio')
  })

  it('precioDesde/precioHasta en 0 SÍ viajan (0 es un filtro válido, no "vacío")', () => {
    const cadena = construirQueryDeGrillaDeArticulos(filtros({ precioDesde: 0 }))
    expect(cadena).toContain('precioDesde=0')
  })

  it('manda idProveedor cuando está seteado y sinProveedor es false', () => {
    expect(construirQueryDeGrillaDeArticulos(filtros({ idProveedor: 7 }))).toContain('idProveedor=7')
  })

  it('manda sinProveedor=true y omite idProveedor cuando sinProveedor es true', () => {
    const cadena = construirQueryDeGrillaDeArticulos(filtros({ sinProveedor: true }))
    expect(cadena).toContain('sinProveedor=true')
    expect(cadena).not.toContain('idProveedor=')
  })

  /**
   * Cláusula bajo prueba: la exclusión mutua idProveedor/sinProveedor de
   * `construirQueryDeGrillaDeArticulos` — el servidor rechaza mandar los dos (400
   * `filtro_proveedor_ambiguo`), así que esta capa nunca puede ser la que dispare esa
   * ambigüedad, incluso si el llamador (un bug futuro en el estado del filtro) los setea juntos.
   * Mutation-proof-tests: sacar el `if (filtros.sinProveedor)` y dejar los dos `set` sin
   * exclusión hace fallar este test (el resultado incluiría `idProveedor=7` junto a
   * `sinProveedor=true`).
   */
  it('con ambos seteados a la vez, sinProveedor gana y idProveedor nunca viaja (XOR)', () => {
    const cadena = construirQueryDeGrillaDeArticulos(filtros({ idProveedor: 7, sinProveedor: true }))
    expect(cadena).toContain('sinProveedor=true')
    expect(cadena).not.toContain('idProveedor=')
  })

  it('manda activo solo cuando no es null (true y false son valores válidos)', () => {
    expect(construirQueryDeGrillaDeArticulos(filtros({ activo: true }))).toContain('activo=true')
    expect(construirQueryDeGrillaDeArticulos(filtros({ activo: false }))).toContain('activo=false')
    expect(construirQueryDeGrillaDeArticulos(filtros({ activo: null }))).not.toContain('activo=')
  })

  it('siempre manda pagina y tamanio, cualquiera sea su valor', () => {
    const cadena = construirQueryDeGrillaDeArticulos(filtros({ pagina: 3, tamanio: 100 }))
    expect(cadena).toContain('pagina=3')
    expect(cadena).toContain('tamanio=100')
  })
})

describe('filtrosDeGrillaDeArticulosVacios', () => {
  it('arranca en la página 1, tamaño 25, sin ningún filtro aplicado', () => {
    expect(filtrosDeGrillaDeArticulosVacios()).toEqual({
      codigo: '',
      nombre: '',
      precioDesde: null,
      precioHasta: null,
      idProveedor: null,
      sinProveedor: false,
      activo: null,
      pagina: 1,
      tamanio: 25,
    })
  })
})
