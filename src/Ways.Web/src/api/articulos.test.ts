import { beforeEach, describe, expect, it, vi } from 'vitest'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()
const apiPutMock = vi.fn()

vi.mock('./cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...args),
    post: (...args: unknown[]) => apiPostMock(...args),
    put: (...args: unknown[]) => apiPutMock(...args),
  },
}))

import {
  clienteDeArticulos,
  COLUMNAS_ORDENABLES_DE_ARTICULOS,
  construirQueryDeGrillaDeArticulos,
  esColumnaOrdenableDeArticulos,
  filtrosDeGrillaDeArticulosVacios,
} from './articulos'
import type { AltaArticulo, FiltrosDeGrillaDeArticulos } from './tipos'

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

describe('construirQueryDeGrillaDeArticulos — orden', () => {
  it('sin orden no manda orden ni direccion', () => {
    expect(construirQueryDeGrillaDeArticulos(filtros(), null)).toBe('?pagina=1&tamanio=25')
  })

  it('con orden manda columna y direccion tal cual las espera el servidor, junto a los filtros y la página', () => {
    const parametros = new URLSearchParams(
      construirQueryDeGrillaDeArticulos(filtros({ activo: true, pagina: 2 }), { columna: 'precio', direccion: 'descendente' }),
    )
    expect(parametros.get('orden')).toBe('precio')
    expect(parametros.get('direccion')).toBe('descendente')
    expect(parametros.get('activo')).toBe('true')
    expect(parametros.get('pagina')).toBe('2')
  })

  it('la lista cerrada coincide con las columnas de datos de la grilla y rechaza claves ajenas', () => {
    expect([...COLUMNAS_ORDENABLES_DE_ARTICULOS]).toEqual([
      'codigo',
      'nombre',
      'costo',
      'precio',
      'proveedor',
      'disponibilidad',
      'estado',
    ])
    expect(esColumnaOrdenableDeArticulos('estado')).toBe(true)
    expect(esColumnaOrdenableDeArticulos('acciones')).toBe(false)
  })

  it('clienteDeArticulos.grilla pide /articulos/grilla con el orden en la query', () => {
    apiGetMock.mockReset()
    apiGetMock.mockResolvedValue({})

    clienteDeArticulos.grilla(filtros(), { columna: 'nombre', direccion: 'ascendente' })

    expect(apiGetMock).toHaveBeenCalledWith('/articulos/grilla?orden=nombre&direccion=ascendente&pagina=1&tamanio=25')
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

describe('clienteDeArticulos — códigos de proveedor', () => {
  beforeEach(() => {
    apiGetMock.mockReset()
    apiPostMock.mockReset()
  })

  it('listar sin idProveedor no manda el parámetro', () => {
    clienteDeArticulos.listar('leche', false)
    expect(apiGetMock).toHaveBeenCalledWith('/articulos?busqueda=leche')
  })

  it('listar con idProveedor lo manda junto a la búsqueda', () => {
    clienteDeArticulos.listar('AB-12', false, 7)
    expect(apiGetMock).toHaveBeenCalledWith('/articulos?busqueda=AB-12&idProveedor=7')
  })

  it('listar con soloVendibles lo manda como parámetro, solo cuando se pide', () => {
    clienteDeArticulos.listar('leche', false, undefined, { soloVendibles: true })
    expect(apiGetMock).toHaveBeenLastCalledWith('/articulos?busqueda=leche&soloVendibles=true')

    clienteDeArticulos.listar('leche', false, 7, { soloVendibles: true })
    expect(apiGetMock).toHaveBeenLastCalledWith('/articulos?busqueda=leche&idProveedor=7&soloVendibles=true')

    clienteDeArticulos.listar('leche', false, undefined, { soloVendibles: false })
    expect(apiGetMock).toHaveBeenLastCalledWith('/articulos?busqueda=leche')

    clienteDeArticulos.listar('leche', false, undefined, {})
    expect(apiGetMock).toHaveBeenLastCalledWith('/articulos?busqueda=leche')
  })

  it('asociarCodigoProveedor hace POST con idProveedor y codigo al artículo indicado', () => {
    clienteDeArticulos.asociarCodigoProveedor(20, { idProveedor: 7, codigo: 'AB-12' })
    expect(apiPostMock).toHaveBeenCalledWith('/articulos/20/codigos-proveedor', { idProveedor: 7, codigo: 'AB-12' })
  })
})

describe('clienteDeArticulos — familia (doc 10 §3)', () => {
  const alta: AltaArticulo = {
    codigoInterno: null,
    nombre: 'Vainilla',
    descripcion: null,
    idArea: 1,
    idCategoria: null,
    idMarca: null,
    idGrupo: null,
    idProveedorHabitual: null,
    idAlicuotaIva: 1,
    unidadVenta: 'Unidad',
    unidadesPorBulto: null,
    esProducto: true,
    costoLista: null,
    costoListaPor: 'Unidad',
    costoListaIncluyeIva: true,
    descuentoProveedor: null,
    disponibleParaTodas: true,
    idsEmpresas: null,
    activo: true,
    controlaLote: false,
  }

  beforeEach(() => {
    apiPostMock.mockReset()
    apiPutMock.mockReset()
  })

  it('crear manda idFamilia en el cuerpo del POST /articulos, tal cual', () => {
    clienteDeArticulos.crear({ ...alta, idFamilia: 7 })

    expect(apiPostMock).toHaveBeenCalledExactlyOnceWith('/articulos', { ...alta, idFamilia: 7 })
  })

  it('actualizar manda el alcance en el cuerpo del PUT /articulos/{id}, por nombre', () => {
    const { codigoInterno: _codigoInterno, ...edicion } = alta
    clienteDeArticulos.actualizar(31, { ...edicion, alcance: 'SoloEste' })

    expect(apiPutMock).toHaveBeenCalledExactlyOnceWith('/articulos/31', { ...edicion, alcance: 'SoloEste' })
  })
})

describe('clienteDeArticulos.sugerenciaDePrecio', () => {
  beforeEach(() => {
    apiGetMock.mockReset()
  })

  it('sin empresa pide la ruta pelada', () => {
    clienteDeArticulos.sugerenciaDePrecio(31)

    expect(apiGetMock).toHaveBeenCalledExactlyOnceWith('/articulos/31/sugerencia-precio')
  })

  it('con empresa la manda como idEmpresa en la query', () => {
    clienteDeArticulos.sugerenciaDePrecio(31, 4)

    expect(apiGetMock).toHaveBeenCalledExactlyOnceWith('/articulos/31/sugerencia-precio?idEmpresa=4')
  })
})
