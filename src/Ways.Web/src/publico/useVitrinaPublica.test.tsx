import { act, renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { ErrorApi } from '../api/cliente'
import type { VitrinaPublica } from '../api/tipos'
import { useVitrinaPublica } from './useVitrinaPublica'

const obtenerVitrinaMock = vi.fn()

vi.mock('../api/publico', () => ({
  clienteDePublico: { obtenerVitrina: () => obtenerVitrinaMock() },
}))

function vitrina(nombre: string): VitrinaPublica {
  return {
    nombre,
    tagline: null,
    descripcion: null,
    colorAcento: null,
    tema: 'oscuro',
    tieneLogo: false,
    logoVersion: null,
    sucursales: [],
  }
}

function diferida() {
  let resolver: (valor: VitrinaPublica) => void = () => {}
  let rechazar: (error: unknown) => void = () => {}
  const promesa = new Promise<VitrinaPublica>((resolve, reject) => {
    resolver = resolve
    rechazar = reject
  })

  return { promesa, resolver, rechazar }
}

beforeEach(() => {
  obtenerVitrinaMock.mockReset()
})

afterEach(() => {
  document.documentElement.removeAttribute('data-bs-theme')
  document.documentElement.style.removeProperty('--vitrina-acento')
  document.title = ''
  document.head.querySelector('meta[name="description"]')?.remove()
})

describe('useVitrinaPublica', () => {
  it('arranca cargando y pasa a publicada con la vitrina que llegó', async () => {
    obtenerVitrinaMock.mockResolvedValue(vitrina('Sur'))

    const { result } = renderHook(() => useVitrinaPublica())

    expect(result.current.resultado).toEqual({ estado: 'cargando' })
    await waitFor(() => expect(result.current.resultado).toEqual({ estado: 'publicada', vitrina: vitrina('Sur') }))
    expect(document.title).toBe('Sur')
  })

  it('un 404 es sin publicar y cualquier otro fallo es un error', async () => {
    obtenerVitrinaMock.mockRejectedValueOnce(new ErrorApi(404, 'vitrina_no_publicada', 'No publicada'))
    const { result } = renderHook(() => useVitrinaPublica())
    await waitFor(() => expect(result.current.resultado.estado).toBe('sin_publicar'))

    obtenerVitrinaMock.mockRejectedValueOnce(new ErrorApi(503, 'x', 'Caído'))
    await act(() => result.current.recargar())
    expect(result.current.resultado.estado).toBe('error')
  })

  it('un 404 con otro código no es sin publicar: es un error que se puede reintentar', async () => {
    obtenerVitrinaMock.mockRejectedValueOnce(new ErrorApi(404, 'ruta_no_encontrada', 'No existe'))
    const { result } = renderHook(() => useVitrinaPublica())
    await waitFor(() => expect(result.current.resultado.estado).toBe('error'))

    obtenerVitrinaMock.mockResolvedValueOnce(vitrina('Sur'))
    await act(() => result.current.recargar())
    expect(result.current.resultado.estado).toBe('publicada')
  })

  it('con un prefijo de sección el título del documento lo lleva', async () => {
    obtenerVitrinaMock.mockResolvedValue(vitrina('Sur'))

    const { result } = renderHook(() => useVitrinaPublica('Tienda online'))
    await waitFor(() => expect(result.current.resultado.estado).toBe('publicada'))

    expect(document.title).toBe('Tienda online · Sur')
  })

  it('una respuesta vieja que llega tarde no pisa a la de la recarga más nueva', async () => {
    const primera = diferida()
    const segunda = diferida()
    obtenerVitrinaMock.mockReturnValueOnce(primera.promesa).mockReturnValueOnce(segunda.promesa)

    const { result } = renderHook(() => useVitrinaPublica())
    let recarga: Promise<void> = Promise.resolve()
    act(() => {
      recarga = result.current.recargar()
    })

    await act(async () => {
      segunda.resolver(vitrina('Nueva'))
      await recarga
    })
    await act(async () => {
      primera.resolver(vitrina('Vieja'))
      await primera.promesa
    })

    expect(result.current.resultado).toEqual({ estado: 'publicada', vitrina: vitrina('Nueva') })
    expect(document.title).toBe('Nueva')
  })

  it('un fallo viejo que llega tarde tampoco pisa a la recarga más nueva', async () => {
    const primera = diferida()
    obtenerVitrinaMock.mockReturnValueOnce(primera.promesa).mockResolvedValueOnce(vitrina('Nueva'))

    const { result } = renderHook(() => useVitrinaPublica())
    await act(() => result.current.recargar())
    await act(async () => {
      primera.rechazar(new Error('red'))
      await primera.promesa.catch(() => {})
    })

    expect(result.current.resultado).toEqual({ estado: 'publicada', vitrina: vitrina('Nueva') })
  })
})
