import { act, fireEvent, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ErrorApi } from '../../api/cliente'
import type { ArticuloListado, PaginaDe } from '../../api/tipos'
import { SelectorDeArticulos, type ArticuloElegido } from './SelectorDeArticulos'

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

function articulo(id: number, sobrescribir: Partial<ArticuloListado> = {}): ArticuloListado {
  return {
    id,
    codigoInterno: `A00${id}`,
    nombre: `Articulo ${id}`,
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
    descuentoProveedor: null,
    costoNominal: null,
    disponibleParaTodas: true,
    idsEmpresas: [],
    activo: true,
    controlaLote: false,
    acumulaEnVenta: true,
    idFamilia: null,
    ...sobrescribir,
  }
}

function pagina(items: ArticuloListado[], total = items.length): PaginaDe<ArticuloListado> {
  return { items, total, pagina: 1, tamanio: 25 }
}

function elegido(id: number): ArticuloElegido {
  return { id, codigoInterno: `A00${id}`, nombre: `Articulo ${id}` }
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

type PropsDelSelector = Parameters<typeof SelectorDeArticulos>[0]

function montar(props: Partial<PropsDelSelector> = {}) {
  const onListo = vi.fn()
  const onCerrar = vi.fn()
  render(<SelectorDeArticulos titulo="Elegir artículos" multiple elegidos={[]} onListo={onListo} onCerrar={onCerrar} {...props} />)
  return { onListo, onCerrar }
}

async function buscar(texto: string) {
  await userEvent.type(screen.getByLabelText('Buscar artículo'), texto)
  await userEvent.click(screen.getByRole('button', { name: 'Buscar' }))
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiGetMock.mockResolvedValue(pagina([articulo(1), articulo(2), articulo(3)]))
})

describe('SelectorDeArticulos — la búsqueda', () => {
  it('es un diálogo con el título que le dan', () => {
    montar({ titulo: 'Elegir el artículo de referencia' })

    expect(screen.getByRole('dialog', { name: 'Elegir el artículo de referencia' })).toBeInTheDocument()
  })

  it('con menos de dos caracteres no busca: lo dice y el botón está deshabilitado', async () => {
    montar()

    expect(screen.getByText('Escribí al menos 2 caracteres para buscar.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Buscar' })).toBeDisabled()
    await userEvent.type(screen.getByLabelText('Buscar artículo'), 'a')
    expect(screen.getByRole('button', { name: 'Buscar' })).toBeDisabled()
    expect(apiGetMock).not.toHaveBeenCalled()
  })

  it('con dos caracteres busca por el término recortado y lista los resultados, sin artículos dados de baja', async () => {
    montar()

    await buscar('  va ')

    expect(await screen.findByText('Articulo 1')).toBeInTheDocument()
    expect(apiGetMock).toHaveBeenCalledExactlyOnceWith('/articulos?busqueda=va')
    expect(screen.getByText('Articulo 2')).toBeInTheDocument()
    expect(screen.queryByText(/Escribí al menos/)).not.toBeInTheDocument()
  })

  it('Enter en el campo busca igual que el botón', async () => {
    montar()

    await userEvent.type(screen.getByLabelText('Buscar artículo'), 'va{Enter}')

    expect(await screen.findByText('Articulo 1')).toBeInTheDocument()
    expect(apiGetMock).toHaveBeenCalledTimes(1)
  })

  it('sin resultados lo dice', async () => {
    apiGetMock.mockResolvedValue(pagina([]))
    montar()

    await buscar('zz')

    expect(await screen.findByText('Sin resultados')).toBeInTheDocument()
  })

  it('un resultado inactivo se rotula "Inactivo"; el activo, "Activo"', async () => {
    apiGetMock.mockResolvedValue(pagina([articulo(1), articulo(2, { activo: false })]))
    montar()

    await buscar('ar')

    const filaUno = (await screen.findByText('Articulo 1')).closest('tr') as HTMLElement
    const filaDos = screen.getByText('Articulo 2').closest('tr') as HTMLElement
    expect(within(filaUno).getByText('Activo')).toBeInTheDocument()
    expect(within(filaDos).getByText('Inactivo')).toBeInTheDocument()
  })

  it('si hay más resultados que los que se muestran, lo dice', async () => {
    apiGetMock.mockResolvedValue(pagina([articulo(1), articulo(2)], 80))
    montar()

    await buscar('ar')

    expect(await screen.findByText('Se muestran 2 de 80 artículos: afiná la búsqueda para ver el resto.')).toBeInTheDocument()
  })

  it('si se muestran todos los resultados no dice que falten', async () => {
    apiGetMock.mockResolvedValue(pagina([articulo(1), articulo(2)]))
    montar()

    await buscar('ar')

    expect(await screen.findByText('Articulo 2')).toBeInTheDocument()
    expect(screen.queryByText(/afiná la búsqueda/)).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `!terminoValido` en la guarda de `buscar`. El botón deshabilitado ya lo impide con un clic; esto
   * cubre el envío del formulario que no pasa por el botón. */
  it('un envío del formulario con menos de dos caracteres no busca', async () => {
    montar()
    await userEvent.type(screen.getByLabelText('Buscar artículo'), 'a')
    const formulario = screen.getByLabelText('Buscar artículo').closest('form')
    if (!formulario) throw new Error('No se encontró el formulario')

    fireEvent.submit(formulario)

    expect(apiGetMock).not.toHaveBeenCalled()
  })

  it('buscar de nuevo borra el error de la búsqueda anterior apenas empieza', async () => {
    apiGetMock.mockRejectedValueOnce(new ErrorApi(500, 'error_interno', 'Se cayó la búsqueda.'))
    montar()
    await buscar('ar')
    await screen.findByText('Se cayó la búsqueda.')
    const lectura = diferida<PaginaDe<ArticuloListado>>()
    apiGetMock.mockReturnValueOnce(lectura.promesa)

    await userEvent.click(screen.getByRole('button', { name: 'Buscar' }))

    expect(screen.queryByText('Se cayó la búsqueda.')).not.toBeInTheDocument()
    await act(async () => {
      lectura.resolver(pagina([articulo(1)]))
    })
    expect(await screen.findByText('Articulo 1')).toBeInTheDocument()
  })

  it('un fallo de la búsqueda muestra el mensaje del servidor y no deja una tabla vieja', async () => {
    montar()
    await buscar('ar')
    await screen.findByText('Articulo 1')

    apiGetMock.mockRejectedValueOnce(new ErrorApi(500, 'error_interno', 'Se cayó la búsqueda.'))
    await userEvent.click(screen.getByRole('button', { name: 'Buscar' }))

    expect(await screen.findByText('Se cayó la búsqueda.')).toBeInTheDocument()
    expect(screen.queryByText('Articulo 1')).not.toBeInTheDocument()
  })

  it('un fallo que no es del servidor usa el mensaje genérico', async () => {
    apiGetMock.mockRejectedValue(new TypeError('Failed to fetch'))
    montar()

    await buscar('ar')

    expect(await screen.findByText('No se pudo buscar artículos.')).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `buscando` en la guarda de `buscar`: un submit con una búsqueda en vuelo no emite otra.
   * Evidencia de mutación (mutation-proof-tests): sacar `|| buscando` hace fallar este test; revertido, vuelve a verde. */
  it('con una búsqueda en vuelo, el botón queda deshabilitado y un segundo envío no emite otra request', async () => {
    const lectura = diferida<PaginaDe<ArticuloListado>>()
    apiGetMock.mockReturnValue(lectura.promesa)
    montar()
    await userEvent.type(screen.getByLabelText('Buscar artículo'), 'va')
    await userEvent.click(screen.getByRole('button', { name: 'Buscar' }))

    expect(screen.getByRole('button', { name: 'Buscando…' })).toBeDisabled()
    const formulario = screen.getByLabelText('Buscar artículo').closest('form')
    if (!formulario) throw new Error('No se encontró el formulario')
    fireEvent.submit(formulario)

    expect(apiGetMock).toHaveBeenCalledTimes(1)
    await act(async () => {
      lectura.resolver(pagina([articulo(1)]))
    })
    expect(await screen.findByText('Articulo 1')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Buscar' })).toBeEnabled()
  })
})

describe('SelectorDeArticulos — elegir', () => {
  it('"Listo" arranca deshabilitado y cuenta lo elegido', async () => {
    montar()

    expect(screen.getByRole('button', { name: 'Listo' })).toBeDisabled()
    expect(screen.getByText('0 elegidos')).toBeInTheDocument()
  })

  it('elige varios con casillas y "Listo" devuelve exactamente lo elegido', async () => {
    const { onListo } = montar()
    await buscar('ar')
    await screen.findByText('Articulo 1')

    await userEvent.click(screen.getByLabelText('Elegir A001 Articulo 1'))
    await userEvent.click(screen.getByLabelText('Elegir A003 Articulo 3'))

    expect(screen.getByText('2 elegidos')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Listo' }))

    expect(onListo).toHaveBeenCalledExactlyOnceWith([elegido(1), elegido(3)])
  })

  it('con uno solo dice "1 elegido"', async () => {
    montar()
    await buscar('ar')
    await userEvent.click(await screen.findByLabelText('Elegir A001 Articulo 1'))

    expect(screen.getByText('1 elegido')).toBeInTheDocument()
  })

  it('desmarcar saca el artículo de lo elegido', async () => {
    const { onListo } = montar()
    await buscar('ar')
    await userEvent.click(await screen.findByLabelText('Elegir A001 Articulo 1'))
    await userEvent.click(screen.getByLabelText('Elegir A002 Articulo 2'))

    await userEvent.click(screen.getByLabelText('Elegir A001 Articulo 1'))
    await userEvent.click(screen.getByRole('button', { name: 'Listo' }))

    expect(onListo).toHaveBeenCalledExactlyOnceWith([elegido(2)])
  })

  it('lo ya elegido arranca marcado y se puede desmarcar', async () => {
    const { onListo } = montar({ elegidos: [elegido(1), elegido(2)] })
    await buscar('ar')

    expect(await screen.findByLabelText('Elegir A001 Articulo 1')).toBeChecked()
    expect(screen.getByLabelText('Elegir A002 Articulo 2')).toBeChecked()
    expect(screen.getByLabelText('Elegir A003 Articulo 3')).not.toBeChecked()
    expect(screen.getByText('2 elegidos')).toBeInTheDocument()

    await userEvent.click(screen.getByLabelText('Elegir A002 Articulo 2'))
    await userEvent.click(screen.getByRole('button', { name: 'Listo' }))
    expect(onListo).toHaveBeenCalledExactlyOnceWith([elegido(1)])
  })

  it('la selección se conserva entre búsquedas: lo elegido en una sigue contando cuando se busca otra cosa', async () => {
    const { onListo } = montar()
    await buscar('ar')
    await userEvent.click(await screen.findByLabelText('Elegir A001 Articulo 1'))

    apiGetMock.mockResolvedValueOnce(pagina([articulo(7)]))
    await userEvent.clear(screen.getByLabelText('Buscar artículo'))
    await buscar('zz')
    await userEvent.click(await screen.findByLabelText('Elegir A007 Articulo 7'))
    await userEvent.click(screen.getByRole('button', { name: 'Listo' }))

    expect(onListo).toHaveBeenCalledExactlyOnceWith([elegido(1), elegido(7)])
  })

  it('en modo de uno solo se usan botones de opción y elegir otro reemplaza al anterior', async () => {
    const { onListo } = montar({ multiple: false })
    await buscar('ar')

    const primero = await screen.findByLabelText('Elegir A001 Articulo 1')
    expect(primero).toHaveAttribute('type', 'radio')
    await userEvent.click(primero)
    await userEvent.click(screen.getByLabelText('Elegir A002 Articulo 2'))

    expect(screen.getByText('1 elegido')).toBeInTheDocument()
    expect(screen.getByLabelText('Elegir A001 Articulo 1')).not.toBeChecked()
    await userEvent.click(screen.getByRole('button', { name: 'Listo' }))
    expect(onListo).toHaveBeenCalledExactlyOnceWith([elegido(2)])
  })

  it('en modo múltiple se usan casillas', async () => {
    montar()
    await buscar('ar')

    expect(await screen.findByLabelText('Elegir A001 Articulo 1')).toHaveAttribute('type', 'checkbox')
  })

  it('"Cancelar" cierra sin devolver nada', async () => {
    const { onListo, onCerrar } = montar()
    await buscar('ar')
    await userEvent.click(await screen.findByLabelText('Elegir A001 Articulo 1'))

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(onCerrar).toHaveBeenCalledTimes(1)
    expect(onListo).not.toHaveBeenCalled()
  })
})

describe('SelectorDeArticulos — lo que no se puede elegir', () => {
  /** Cláusula bajo prueba: `motivoDeBloqueo` — agrupar no mueve a nadie de la familia que ya tiene (doc 10 §3). */
  it('un artículo que ya tiene familia aparece pero no se puede elegir, y dice por qué', async () => {
    apiGetMock.mockResolvedValue(pagina([articulo(1), articulo(2, { idFamilia: 8 })]))
    montar()

    await buscar('ar')

    const casilla = await screen.findByLabelText('Elegir A002 Articulo 2')
    expect(casilla).toBeDisabled()
    expect(within(casilla.closest('tr') as HTMLElement).getByText('Ya está en una familia')).toBeInTheDocument()
    expect(screen.getByLabelText('Elegir A001 Articulo 1')).toBeEnabled()
  })

  it('un miembro de la familia a la que se agrega se rotula "Ya es miembro de esta familia"', async () => {
    apiGetMock.mockResolvedValue(pagina([articulo(1, { idFamilia: 7 }), articulo(2, { idFamilia: 8 })]))
    montar({ idFamiliaDeDestino: 7 })

    await buscar('ar')

    const deEsta = await screen.findByLabelText('Elegir A001 Articulo 1')
    const deOtra = screen.getByLabelText('Elegir A002 Articulo 2')
    expect(deEsta).toBeDisabled()
    expect(deOtra).toBeDisabled()
    expect(within(deEsta.closest('tr') as HTMLElement).getByText('Ya es miembro de esta familia')).toBeInTheDocument()
    expect(within(deOtra.closest('tr') as HTMLElement).getByText('Ya está en una familia')).toBeInTheDocument()
  })

  it('un artículo excluido se ve, deshabilitado, con el motivo que le dio quien abrió el selector', async () => {
    montar({ excluidos: new Map([[2, 'Ya es el artículo de referencia']]) })

    await buscar('ar')

    const casilla = await screen.findByLabelText('Elegir A002 Articulo 2')
    expect(casilla).toBeDisabled()
    expect(within(casilla.closest('tr') as HTMLElement).getByText('Ya es el artículo de referencia')).toBeInTheDocument()
    expect(screen.getByLabelText('Elegir A001 Articulo 1')).toBeEnabled()
  })

  it('el motivo de una exclusión gana sobre el de la familia', async () => {
    apiGetMock.mockResolvedValue(pagina([articulo(2, { idFamilia: 8 })]))
    montar({ excluidos: new Map([[2, 'Ya está elegido']]) })

    await buscar('ar')

    expect(await screen.findByText('Ya está elegido')).toBeInTheDocument()
    expect(screen.queryByText('Ya está en una familia')).not.toBeInTheDocument()
  })

  it('un artículo bloqueado no suma a lo elegido', async () => {
    apiGetMock.mockResolvedValue(pagina([articulo(2, { idFamilia: 8 })]))
    montar()
    await buscar('ar')

    await userEvent.click(await screen.findByLabelText('Elegir A002 Articulo 2'))

    expect(screen.getByRole('button', { name: 'Listo' })).toBeDisabled()
  })
})

