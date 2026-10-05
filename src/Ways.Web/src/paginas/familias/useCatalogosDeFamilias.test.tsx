import { StrictMode } from 'react'
import { act, renderHook, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const apiGetMock = vi.fn()

vi.mock('../../api/cliente', () => ({
  api: { get: (...args: unknown[]) => apiGetMock(...(args as [string])) },
  ErrorApi: class ErrorApiMock extends Error {
    estado: number
    codigo: string
    constructor(estado: number, codigo: string, mensaje: string) {
      super(mensaje)
      this.estado = estado
      this.codigo = codigo
    }
  },
}))

import { useCatalogosDeFamilias } from './useCatalogosDeFamilias'

const RUTAS = {
  areas: '/catalogos/areas?incluirInactivos=true',
  categorias: '/catalogos/categorias?incluirInactivos=true',
  grupos: '/catalogos/grupos?incluirInactivos=true',
  proveedores: '/proveedores?tamanio=200',
  alicuotas: '/catalogos-fiscales/alicuotas-iva',
  marcas: '/catalogos/marcas?incluirInactivos=true',
  listas: '/catalogos/listas-precio?incluirInactivos=true',
}

function respuestas(): Record<string, unknown> {
  return {
    [RUTAS.areas]: [{ id: 1, nombre: 'Bebidas' }],
    [RUTAS.categorias]: [{ id: 2, nombre: 'Gaseosas' }],
    [RUTAS.grupos]: [{ id: 3, nombre: 'Línea 1' }],
    [RUTAS.proveedores]: { items: [{ id: 4, razonSocial: 'Distribuidora Sur SA', nombreFantasia: 'Sur' }], total: 1, pagina: 1, tamanio: 200 },
    [RUTAS.alicuotas]: [{ id: 5, nombre: 'IVA 21%' }],
    [RUTAS.marcas]: [{ id: 6, nombre: 'Marca X' }],
    [RUTAS.listas]: [{ id: 7, nombre: 'Mostrador' }],
  }
}

function diferida<T>() {
  let resolver!: (valor: T) => void
  let rechazar!: (motivo: unknown) => void
  const promesa = new Promise<T>((resolve, reject) => {
    resolver = resolve
    rechazar = reject
  })
  return { promesa, resolver, rechazar }
}

/** La API simulada: cada ruta responde con su fixture, salvo las que `fallan` y las que `sobrescribe` decide. */
function simular(opciones: { fallan?: string[]; sobrescribe?: (ruta: string, llamada: number) => Promise<unknown> | undefined } = {}) {
  const llamadasPorRuta = new Map<string, number>()
  apiGetMock.mockImplementation((ruta: string) => {
    const llamada = (llamadasPorRuta.get(ruta) ?? 0) + 1
    llamadasPorRuta.set(ruta, llamada)

    const propia = opciones.sobrescribe?.(ruta, llamada)
    if (propia !== undefined) return propia
    if (opciones.fallan?.includes(ruta)) return Promise.reject(new Error('sin red'))

    return Promise.resolve(respuestas()[ruta])
  })

  return llamadasPorRuta
}

beforeEach(() => {
  apiGetMock.mockReset()
})

describe('useCatalogosDeFamilias', () => {
  it('pide los siete catálogos, con las filas dadas de baja, y expone el nombre de cada id', async () => {
    simular()

    const { result } = renderHook(() => useCatalogosDeFamilias())

    await waitFor(() => {
      expect(result.current.nombres.areas.get(1)).toBe('Bebidas')
      expect(result.current.nombres.categorias.get(2)).toBe('Gaseosas')
      expect(result.current.nombres.grupos.get(3)).toBe('Línea 1')
      expect(result.current.nombres.proveedores.get(4)).toBe('Sur')
      expect(result.current.nombres.alicuotas.get(5)).toBe('IVA 21%')
      expect(result.current.marcas.get(6)).toBe('Marca X')
      expect(result.current.listas.get(7)).toBe('Mostrador')
    })
    expect(apiGetMock.mock.calls.map(([ruta]) => ruta).sort()).toEqual(Object.values(RUTAS).sort())
    expect(result.current.aviso).toBe('')
  })

  it('un catálogo que no carga deja su mapa vacío, se nombra en el aviso y no frena a los demás', async () => {
    simular({ fallan: [RUTAS.areas] })

    const { result } = renderHook(() => useCatalogosDeFamilias())

    await waitFor(() => {
      expect(result.current.aviso).not.toBe('')
      expect(result.current.listas.get(7)).toBe('Mostrador')
    })
    expect(result.current.aviso).toBe('No se pudieron cargar: áreas. Sus valores se muestran con el código en vez del nombre.')
    expect(result.current.nombres.areas.size).toBe(0)
    expect(result.current.nombres.categorias.get(2)).toBe('Gaseosas')
  })

  it('con varios catálogos caídos el aviso los nombra a todos', async () => {
    simular({ fallan: [RUTAS.grupos, RUTAS.alicuotas, RUTAS.marcas] })

    const { result } = renderHook(() => useCatalogosDeFamilias())

    await waitFor(() => expect(result.current.aviso).toContain('marcas'))
    expect(result.current.aviso).toBe(
      'No se pudieron cargar: grupos, alícuotas de IVA, marcas. Sus valores se muestran con el código en vez del nombre.',
    )
  })

  /** Cláusula bajo prueba: el `cancelado` de la lectura (react-async-state regla 1). En StrictMode el efecto corre dos
   * veces; la respuesta tardía de la primera corrida no puede pisar a la de la segunda. */
  it('bajo StrictMode, la respuesta tardía de la primera corrida no pisa a la de la segunda', async () => {
    const vieja = diferida<unknown>()
    const pedidos = simular({
      sobrescribe: (ruta, llamada) => {
        if (ruta !== RUTAS.areas) return undefined
        return llamada === 1 ? vieja.promesa : Promise.resolve([{ id: 1, nombre: 'Bebidas vigente' }])
      },
    })

    const { result } = renderHook(() => useCatalogosDeFamilias(), { wrapper: StrictMode })
    await waitFor(() => expect(result.current.nombres.areas.get(1)).toBe('Bebidas vigente'))
    expect(pedidos.get(RUTAS.areas)).toBe(2)

    await act(async () => {
      vieja.resolver([{ id: 1, nombre: 'Bebidas vieja' }])
    })

    expect(result.current.nombres.areas.get(1)).toBe('Bebidas vigente')
  })

  it('bajo StrictMode, el fallo tardío de la primera corrida no se cuenta como fallo de la vigente', async () => {
    const vieja = diferida<unknown>()
    const pedidos = simular({
      sobrescribe: (ruta, llamada) => {
        if (ruta !== RUTAS.marcas) return undefined
        return llamada === 1 ? vieja.promesa : Promise.resolve([{ id: 6, nombre: 'Marca X' }])
      },
    })

    const { result } = renderHook(() => useCatalogosDeFamilias(), { wrapper: StrictMode })
    await waitFor(() => expect(result.current.marcas.get(6)).toBe('Marca X'))
    expect(pedidos.get(RUTAS.marcas)).toBe(2)

    await act(async () => {
      vieja.rechazar(new Error('sin red'))
    })

    expect(result.current.aviso).toBe('')
  })
})
