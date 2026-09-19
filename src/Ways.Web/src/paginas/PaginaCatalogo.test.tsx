import { act, fireEvent, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { PaginaCatalogo } from './PaginaCatalogo'
import { etiquetaParaValorFaltante } from './etiquetaParaValorFaltante'
import { descriptorMarcas, descriptorListasPrecio } from '../api/catalogos'
import type { DescriptorDeCatalogo } from '../api/catalogos'
import { ErrorApi } from '../api/cliente'
import type { CatalogoListado, ListaPrecioListado, MarcaListado } from '../api/tipos'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()
const apiPutMock = vi.fn()
const apiDeleteMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown])),
    put: (...args: unknown[]) => apiPutMock(...(args as [string, unknown])),
    delete: (...args: unknown[]) => apiDeleteMock(...(args as [string])),
  },
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

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPutMock.mockReset()
  apiDeleteMock.mockReset()
})

describe('PaginaCatalogo — visibilidad condicional de idListaBase/porcentaje', () => {
  it('con modo Fija (default en un alta nueva) los campos idListaBase/porcentaje no están en el DOM', async () => {
    apiGetMock.mockResolvedValue([listaFixture({ id: 1, nombre: 'Fija A' })])
    render(<PaginaCatalogo definicion={descriptorListasPrecio} />)

    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo' }))

    expect(screen.queryByLabelText('Lista base')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Porcentaje sobre la base (%)')).not.toBeInTheDocument()
  })

  it('cambiar modo a Derivada hace aparecer idListaBase y porcentaje', async () => {
    apiGetMock.mockResolvedValue([listaFixture({ id: 1, nombre: 'Fija A' })])
    render(<PaginaCatalogo definicion={descriptorListasPrecio} />)

    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo' }))
    await userEvent.selectOptions(screen.getByLabelText('Modo'), 'Derivada')

    expect(screen.getByLabelText('Lista base')).toBeInTheDocument()
    expect(screen.getByLabelText('Porcentaje sobre la base (%)')).toBeInTheDocument()
  })

  it('al editar una Derivada cuya lista base está inactiva, renderiza la opción faltante con la etiqueta de etiquetaParaValorFaltante', async () => {
    const listaBaseInactiva = listaFixture({ id: 5, nombre: 'Lista vieja', activo: false, modo: 'Fija' })
    const listaDerivadaOrfana = listaFixture({
      id: 6,
      nombre: 'Lista derivada huérfana',
      modo: 'Derivada',
      idListaBase: 5,
      porcentaje: 20,
    })
    apiGetMock.mockResolvedValue([listaBaseInactiva, listaDerivadaOrfana])
    render(<PaginaCatalogo definicion={descriptorListasPrecio} />)

    const fila = (await screen.findByText('Lista derivada huérfana')).closest('tr')
    if (!fila) throw new Error('No se encontró la fila de la lista derivada huérfana')
    await userEvent.click(within(fila).getByRole('button', { name: 'Editar' }))

    const etiquetaEsperada = etiquetaParaValorFaltante('5', [listaBaseInactiva, listaDerivadaOrfana])
    expect(etiquetaEsperada).toBe('Lista vieja (inactiva)')

    const selectListaBase = screen.getByLabelText('Lista base') as HTMLSelectElement
    expect(within(selectListaBase).getByRole('option', { name: etiquetaEsperada })).toBeInTheDocument()
    expect(selectListaBase.value).toBe('5')
  })
})

describe('PaginaCatalogo — fallback de opción faltante acotado a opcionesDesdeListado', () => {
  type FooListado = CatalogoListado
  type FooAlta = { nombre: string; idEmpresa: number | null; activo: boolean; estado: string }

  const descriptorEstaticoDePrueba: DescriptorDeCatalogo<FooListado, FooAlta> = {
    recurso: 'foo-de-prueba',
    titulo: 'Foo de prueba',
    tituloSingular: 'foo',
    campos: [
      {
        clave: 'estado',
        etiqueta: 'Estado personalizado',
        tipo: 'select',
        // Opciones estáticas (no `opcionesDesdeListado`): no deben buscar en `items` un valor
        // faltante — regresión del hallazgo de judgment-day slice 6 ronda 2.
        opciones: [
          { valor: 'activo', etiqueta: 'Activo' },
          { valor: 'inactivo', etiqueta: 'Inactivo' },
        ],
      },
    ],
    valoresPorDefecto: { estado: 'activo' },
    // El valor '3' coincide "por casualidad" con el id de otro item del listado, para probar
    // que ya no se usa ese listado como fuente de la opción faltante de un select estático.
    aValores: () => ({ estado: '3' }),
    aAlta: (nombre, activo) => ({ nombre, idEmpresa: null, activo, estado: 'activo' }),
    sujetoDeBaja: 'el área',
  }

  it('un select de opciones estáticas cuyo valor no está entre las opciones NO recibe una opción de fallback', async () => {
    const itemNoRelacionado: FooListado = { id: 3, nombre: 'Item Tres', activo: true, idEmpresa: null }
    apiGetMock.mockResolvedValue([itemNoRelacionado])
    render(<PaginaCatalogo definicion={descriptorEstaticoDePrueba} />)

    const fila = (await screen.findByText('Item Tres')).closest('tr')
    if (!fila) throw new Error('No se encontró la fila del item de prueba')
    await userEvent.click(within(fila).getByRole('button', { name: 'Editar' }))

    const selectEstado = screen.getByLabelText('Estado personalizado') as HTMLSelectElement
    expect(within(selectEstado).queryByRole('option', { name: 'Item Tres' })).not.toBeInTheDocument()
    expect(within(selectEstado).getAllByRole('option')).toHaveLength(2)
  })
})

describe('etiquetaParaValorFaltante', () => {
  const items: CatalogoListado[] = [
    { id: 1, nombre: 'Activa', activo: true, idEmpresa: null },
    { id: 2, nombre: 'Inactiva', activo: false, idEmpresa: null },
  ]

  it('devuelve "Opción no disponible (<id>)" cuando el id no está en items', () => {
    expect(etiquetaParaValorFaltante('99', items)).toBe('Opción no disponible (99)')
  })

  it('devuelve el nombre cuando el item existe y está activo', () => {
    expect(etiquetaParaValorFaltante('1', items)).toBe('Activa')
  })

  it('devuelve "<nombre> (inactiva)" cuando el item existe y está inactivo', () => {
    expect(etiquetaParaValorFaltante('2', items)).toBe('Inactiva (inactiva)')
  })
})

// fix/web-bajas-catalogos: la baja de un catálogo reusa `ConfirmacionDeBaja` + `copiaDeFalloDeBaja`,
// mismo patrón que `Empresas.test.tsx` (`react-async-state` regla 10). Se ejercita con
// `descriptorMarcas` (sin campos propios) por ser el más simple de los que pasan por la máquina
// genérica.

function marcaFixture(sobrescribir: Partial<MarcaListado> = {}): MarcaListado {
  return { id: 1, nombre: 'Nike', activo: true, idEmpresa: null, ...sobrescribir }
}

describe('PaginaCatalogo — baja lógica (fix/web-bajas-catalogos)', () => {
  beforeEach(() => {
    apiDeleteMock.mockResolvedValue(undefined)
  })

  it('el botón de baja no llama a la API hasta que se confirma', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([marcaFixture()])
    render(<PaginaCatalogo definicion={descriptorMarcas} />)

    await screen.findByText('Nike')
    await usuario.click(screen.getByRole('button', { name: 'Baja' }))

    expect(apiDeleteMock).not.toHaveBeenCalled()
    expect(screen.getByRole('alertdialog', { name: 'Confirmar baja' })).toHaveTextContent(
      '¿Dar de baja la marca "Nike"?',
    )

    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))
    await screen.findByText('Se dio de baja "Nike".')
    expect(apiDeleteMock).toHaveBeenCalledWith('/catalogos/marcas/1')
  })

  it('cancelar cierra la puerta y no llama nunca a la API', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([marcaFixture()])
    render(<PaginaCatalogo definicion={descriptorMarcas} />)

    await screen.findByText('Nike')
    await usuario.click(screen.getByRole('button', { name: 'Baja' }))
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()
  })

  /** Cláusula bajo prueba: `ocupadoRef`, la guarda de re-entrancia del mismo tick. */
  it('un segundo click sobre la confirmación en vuelo se descarta', async () => {
    apiGetMock.mockResolvedValue([marcaFixture()])
    apiDeleteMock.mockImplementation(() => new Promise<void>(() => {}))
    render(<PaginaCatalogo definicion={descriptorMarcas} />)

    await screen.findByText('Nike')
    await userEvent.setup().click(screen.getByRole('button', { name: 'Baja' }))
    const confirmar = screen.getByRole('button', { name: 'Confirmar baja' })
    await act(async () => {
      confirmar.click()
      confirmar.click()
      await Promise.resolve()
    })

    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  /** Cláusula bajo prueba: la ventana inerte completa — ver el test gemelo de `Empresas.test.tsx`. */
  it('durante el DELETE y su refresco no queda ninguna acción alcanzable', async () => {
    const usuario = userEvent.setup()
    let resolverDelete!: () => void
    let resolverRefresco!: (items: MarcaListado[]) => void

    let cargas = 0
    apiGetMock.mockImplementation(() => {
      cargas += 1
      if (cargas === 1) return Promise.resolve([marcaFixture(), marcaFixture({ id: 2, nombre: 'Adidas' })])

      return new Promise<MarcaListado[]>((resolver) => {
        resolverRefresco = resolver
      })
    })
    apiDeleteMock.mockImplementation(
      () =>
        new Promise<void>((resolver) => {
          resolverDelete = resolver
        }),
    )

    render(<PaginaCatalogo definicion={descriptorMarcas} />)
    await screen.findByText('Nike')

    await usuario.click(within(screen.getByRole('row', { name: /Nike/ })).getByRole('button', { name: 'Baja' }))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    expect(screen.getByRole('button', { name: 'Dando de baja…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Nuevo' })).toBeDisabled()
    expect(screen.getByLabelText('Incluir inactivos')).toBeDisabled()
    for (const boton of [
      ...screen.getAllByRole('button', { name: 'Editar' }),
      ...screen.getAllByRole('button', { name: 'Baja' }),
    ]) {
      expect(boton).toBeDisabled()
    }

    await act(async () => {
      resolverDelete()
      await Promise.resolve()
    })

    await screen.findByText('Se dio de baja "Nike".')
    expect(screen.getByText('Cargando…')).toBeInTheDocument()

    await act(async () => {
      resolverRefresco([marcaFixture({ id: 2, nombre: 'Adidas' })])
      await Promise.resolve()
    })

    await screen.findByText('Adidas')
    expect(screen.queryByText('Nike')).not.toBeInTheDocument()
    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  /**
   * Cláusula bajo prueba: la elección de copia por `codigo` vía `copiaDeFalloDeBaja`, con el
   * `sujetoDeBaja` del DESCRIPTOR (`la marca`), no un switch sobre `recurso`.
   */
  it('un 409 marca_en_uso rinde el mensaje del servidor y la guía de la marca, y deja la puerta abierta', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([marcaFixture()])
    apiDeleteMock.mockRejectedValue(
      new ErrorApi(409, 'marca_en_uso', 'No se puede dar de baja la marca porque tiene artículos.'),
    )
    render(<PaginaCatalogo definicion={descriptorMarcas} />)

    await screen.findByText('Nike')
    await usuario.click(screen.getByRole('button', { name: 'Baja' }))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await screen.findByText(/porque tiene artículos/)
    expect(screen.getByText(/Reasigná esos datos o desactivá la marca/)).toBeInTheDocument()
    expect(screen.getByRole('alertdialog', { name: 'Confirmar baja' })).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: el `setError('')` de `cancelarBaja` — ver `Empresas.test.tsx`. */
  it('cancelar después de un rechazo se lleva el motivo con la puerta', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([marcaFixture()])
    apiDeleteMock.mockRejectedValue(
      new ErrorApi(409, 'marca_en_uso', 'No se puede dar de baja la marca porque tiene artículos.'),
    )
    render(<PaginaCatalogo definicion={descriptorMarcas} />)

    await screen.findByText('Nike')
    await usuario.click(screen.getByRole('button', { name: 'Baja' }))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))
    await screen.findByText(/porque tiene artículos/)

    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.queryByText(/porque tiene artículos/)).not.toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: el token acuñado al CONFIRMAR, no al abrir la puerta — ver
   * `Empresas.test.tsx`. El "incluir inactivos" NO sirve para acuñar una generación intermedia
   * acá: el checkbox queda `disabled` en cuanto la puerta está abierta (`bloqueado`), así que un
   * click no dispara nada. La única ventana real es la que usa `Empresas.test.tsx` (líneas
   * 498-527): con el form de edición de OTRA fila todavía abierto (`bloqueado` lo deshabilita,
   * pero el `<form>` sigue montado), `fireEvent.submit(form)` dispara `onSubmit` sin pasar por el
   * botón deshabilitado, y `guardar()` acuña su propia generación mientras la puerta de baja de
   * Adidas sigue abierta.
   */
  it('una generación acuñada entre abrir la puerta y confirmar no se traga el 204', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([marcaFixture(), marcaFixture({ id: 2, nombre: 'Adidas' })])
    apiPutMock.mockResolvedValue(undefined)
    const { container } = render(<PaginaCatalogo definicion={descriptorMarcas} />)
    await screen.findByText('Nike')

    // Editar Nike y, sin cerrarlo, abrir la puerta de baja de Adidas: `bloqueado` sigue en false
    // hasta que se abre la puerta, así que las dos conviven montadas.
    await usuario.click(within(screen.getByRole('row', { name: /Nike/ })).getByRole('button', { name: 'Editar' }))
    await usuario.click(within(screen.getByRole('row', { name: /Adidas/ })).getByRole('button', { name: 'Baja' }))

    const form = container.querySelector('form')
    if (!form) throw new Error('no hay formulario de edición abierto')
    await act(async () => {
      fireEvent.submit(form)
    })
    await screen.findByText('Se actualizó "Nike".')
    expect(apiPutMock).toHaveBeenCalledTimes(1)

    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await screen.findByText('Se dio de baja "Adidas".')
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
    expect(apiDeleteMock).toHaveBeenCalledWith('/catalogos/marcas/2')
  })

  it('la baja de la fila que se está editando se lleva también su formulario', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([marcaFixture()])
    render(<PaginaCatalogo definicion={descriptorMarcas} />)

    await screen.findByText('Nike')
    await usuario.click(screen.getByRole('button', { name: 'Editar' }))
    expect(screen.getByText('Editando marca 1')).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Baja' }))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await screen.findByText('Se dio de baja "Nike".')
    expect(screen.queryByText('Editando marca 1')).not.toBeInTheDocument()
  })
})
