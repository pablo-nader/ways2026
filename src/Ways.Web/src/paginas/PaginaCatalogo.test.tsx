import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { PaginaCatalogo } from './PaginaCatalogo'
import { etiquetaParaValorFaltante } from './etiquetaParaValorFaltante'
import { descriptorMarcas, descriptorListasPrecio, descriptorMediosPago } from '../api/catalogos'
import type { DescriptorDeCatalogo } from '../api/catalogos'
import { ErrorApi } from '../api/cliente'
import type { CatalogoListado, ListaPrecioListado, MarcaListado, MedioPagoAdministrado } from '../api/tipos'

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

describe('PaginaCatalogo — alta y edición en un modal', () => {
  it('la primera pantalla es solo la grilla: ni formulario ni diálogo', async () => {
    apiGetMock.mockResolvedValue([marcaFixture()])
    render(<PaginaCatalogo definicion={descriptorMarcas} />)

    await screen.findByText('Nike')

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Nombre')).not.toBeInTheDocument()
    expect(document.querySelector('form')).toBeNull()
  })

  it('agregar abre el modal con el título de alta y el formulario vacío', async () => {
    apiGetMock.mockResolvedValue([marcaFixture()])
    render(<PaginaCatalogo definicion={descriptorMarcas} />)

    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))

    const dialogo = screen.getByRole('dialog', { name: 'Nueva marca' })
    expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('')
    expect(within(dialogo).getByRole('button', { name: 'Guardar' })).toBeInTheDocument()
  })

  it('editar abre el modal con el título de edición y la fila cargada', async () => {
    apiGetMock.mockResolvedValue([marcaFixture({ activo: false })])
    render(<PaginaCatalogo definicion={descriptorMarcas} />)

    await userEvent.click(await screen.findByRole('button', { name: 'Editar' }))

    const dialogo = screen.getByRole('dialog', { name: 'Editar marca' })
    expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('Nike')
    expect(within(dialogo).getByLabelText('Estado')).toHaveValue('inactivo')
  })

  it('cancelar cierra el modal sin escribir nada', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([marcaFixture()])
    render(<PaginaCatalogo definicion={descriptorMarcas} />)

    await usuario.click(await screen.findByRole('button', { name: 'Agregar' }))
    await usuario.type(screen.getByLabelText('Nombre'), 'Puma')
    await usuario.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
    expect(apiPutMock).not.toHaveBeenCalled()
  })

  it('un guardado exitoso cierra el modal, deja el aviso en la pantalla y recarga el listado', async () => {
    const usuario = userEvent.setup()
    let cargas = 0
    apiGetMock.mockImplementation(() => {
      cargas += 1
      return Promise.resolve(
        cargas === 1 ? [marcaFixture()] : [marcaFixture(), marcaFixture({ id: 2, nombre: 'Puma' })],
      )
    })
    apiPostMock.mockResolvedValue(undefined)
    render(<PaginaCatalogo definicion={descriptorMarcas} />)

    await usuario.click(await screen.findByRole('button', { name: 'Agregar' }))
    await usuario.type(screen.getByLabelText('Nombre'), 'Puma')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await screen.findByText('Se creó "Puma".')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(apiPostMock.mock.calls[0][0]).toBe('/catalogos/marcas')
    expect(await screen.findByRole('cell', { name: 'Puma' })).toBeInTheDocument()
  })

  it('Enter en el campo envía el formulario del modal', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([marcaFixture()])
    apiPostMock.mockResolvedValue(undefined)
    render(<PaginaCatalogo definicion={descriptorMarcas} />)

    await usuario.click(await screen.findByRole('button', { name: 'Agregar' }))
    await usuario.type(screen.getByLabelText('Nombre'), 'Puma{Enter}')

    await screen.findByText('Se creó "Puma".')
    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  it('un guardado rechazado muestra el error adentro del modal, lo deja abierto y conserva lo tipeado', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([marcaFixture()])
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'nombre_duplicado', 'Ya existe una marca con ese nombre.'))
    render(<PaginaCatalogo definicion={descriptorMarcas} />)

    await usuario.click(await screen.findByRole('button', { name: 'Agregar' }))
    await usuario.type(screen.getByLabelText('Nombre'), 'Nike')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    const dialogo = screen.getByRole('dialog', { name: 'Nueva marca' })
    expect(await within(dialogo).findByText('Ya existe una marca con ese nombre.')).toBeInTheDocument()
    expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('Nike')
    expect(within(dialogo).getByRole('button', { name: 'Guardar' })).toBeEnabled()
  })

  it('el error de un guardado anterior no sobrevive a cancelar y reabrir', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([marcaFixture()])
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'nombre_duplicado', 'Ya existe una marca con ese nombre.'))
    render(<PaginaCatalogo definicion={descriptorMarcas} />)

    await usuario.click(await screen.findByRole('button', { name: 'Agregar' }))
    await usuario.type(screen.getByLabelText('Nombre'), 'Nike')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))
    await screen.findByText('Ya existe una marca con ese nombre.')

    await usuario.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancelar' }))
    await usuario.click(screen.getByRole('button', { name: 'Agregar' }))

    expect(screen.queryByText('Ya existe una marca con ese nombre.')).not.toBeInTheDocument()
  })

  it('un guardado ya hecho cuyo refresco falla cierra el modal y avisa en la pantalla', async () => {
    const usuario = userEvent.setup()
    let cargas = 0
    apiGetMock.mockImplementation(() => {
      cargas += 1
      return cargas === 1
        ? Promise.resolve([marcaFixture()])
        : Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó.'))
    })
    apiPostMock.mockResolvedValue(undefined)
    render(<PaginaCatalogo definicion={descriptorMarcas} />)

    await usuario.click(await screen.findByRole('button', { name: 'Agregar' }))
    await usuario.type(screen.getByLabelText('Nombre'), 'Puma')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await screen.findByText('Se creó "Puma". Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('con el guardado en vuelo el modal no se cierra ni con la × ni con Escape, y un segundo envío se descarta', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([marcaFixture()])
    apiPostMock.mockImplementation(() => new Promise<void>(() => {}))
    render(<PaginaCatalogo definicion={descriptorMarcas} />)

    await usuario.click(await screen.findByRole('button', { name: 'Agregar' }))
    await usuario.type(screen.getByLabelText('Nombre'), 'Puma')
    const guardar = screen.getByRole('button', { name: 'Guardar' })
    await act(async () => {
      guardar.click()
      guardar.click()
      await Promise.resolve()
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('button', { name: 'Guardando…' })).toBeDisabled()
    expect(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cerrar' })).toBeDisabled()
    await usuario.keyboard('{Escape}')
    expect(screen.getByRole('dialog', { name: 'Nueva marca' })).toBeInTheDocument()
  })
})

describe('PaginaCatalogo — visibilidad condicional de idListaBase/porcentaje', () => {
  it('con modo Fija (default en un alta nueva) los campos idListaBase/porcentaje no están en el DOM', async () => {
    apiGetMock.mockResolvedValue([listaFixture({ id: 1, nombre: 'Fija A' })])
    render(<PaginaCatalogo definicion={descriptorListasPrecio} />)

    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))

    expect(screen.queryByLabelText('Lista base')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Porcentaje sobre la base (%)')).not.toBeInTheDocument()
  })

  it('cambiar modo a Derivada hace aparecer idListaBase y porcentaje', async () => {
    apiGetMock.mockResolvedValue([listaFixture({ id: 1, nombre: 'Fija A' })])
    render(<PaginaCatalogo definicion={descriptorListasPrecio} />)

    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))
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
    await usuario.click(screen.getByRole('button', { name: 'Eliminar' }))

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
    await usuario.click(screen.getByRole('button', { name: 'Eliminar' }))
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
    await userEvent.setup().click(screen.getByRole('button', { name: 'Eliminar' }))
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

    await usuario.click(within(screen.getByRole('row', { name: /Nike/ })).getByRole('button', { name: 'Eliminar' }))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    expect(screen.getByRole('button', { name: 'Dando de baja…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Agregar' })).toBeDisabled()
    expect(screen.getByLabelText('Incluir inactivos')).toBeDisabled()
    for (const boton of [
      ...screen.getAllByRole('button', { name: 'Editar' }),
      ...screen.getAllByRole('button', { name: 'Eliminar' }),
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

  /** Cláusula bajo prueba: `AVISO_REFRESCO_FALLIDO_BAJA` — ver `Empresas.test.tsx`. Un DELETE que
   * ya commiteó nunca se reporta como fallido, aunque el refresco posterior explote. */
  it('un refresco fallido después de la baja no la reporta como fallida', async () => {
    const usuario = userEvent.setup()
    let cargas = 0
    apiGetMock.mockImplementation(() => {
      cargas += 1
      if (cargas === 1) return Promise.resolve([marcaFixture()])

      return Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó.'))
    })
    render(<PaginaCatalogo definicion={descriptorMarcas} />)
    await screen.findByText('Nike')

    await usuario.click(screen.getByRole('button', { name: 'Eliminar' }))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await screen.findByText(
      'Se dio de baja "Nike". Se eliminó, pero no se pudo actualizar la vista. Recargá la pantalla.',
    )
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
    await usuario.click(screen.getByRole('button', { name: 'Eliminar' }))
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
    await usuario.click(screen.getByRole('button', { name: 'Eliminar' }))
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
   * botón deshabilitado (el modal del formulario vive en un portal, por eso se lo busca en el
   * `document`), y `guardar()` acuña su propia generación mientras la puerta de baja de
   * Adidas sigue abierta.
   */
  it('una generación acuñada entre abrir la puerta y confirmar no se traga el 204', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([marcaFixture(), marcaFixture({ id: 2, nombre: 'Adidas' })])
    apiPutMock.mockResolvedValue(undefined)
    render(<PaginaCatalogo definicion={descriptorMarcas} />)
    await screen.findByText('Nike')

    // Editar Nike y, sin cerrarlo, abrir la puerta de baja de Adidas: `bloqueado` sigue en false
    // hasta que se abre la puerta, así que las dos conviven montadas.
    await usuario.click(within(screen.getByRole('row', { name: /Nike/ })).getByRole('button', { name: 'Editar' }))
    await usuario.click(within(screen.getByRole('row', { name: /Adidas/ })).getByRole('button', { name: 'Eliminar' }))

    const form = document.querySelector('form')
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
    expect(screen.getByRole('dialog', { name: 'Editar marca' })).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Eliminar' }))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await screen.findByText('Se dio de baja "Nike".')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
})

describe('PaginaCatalogo — error y foco del modal', () => {
  it('un guardado rechazado expone el error como alerta adentro del diálogo', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([marcaFixture()])
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'nombre_duplicado', 'Ya existe una marca con ese nombre.'))
    render(<PaginaCatalogo definicion={descriptorMarcas} />)

    await usuario.click(await screen.findByRole('button', { name: 'Agregar' }))
    await usuario.type(screen.getByLabelText('Nombre'), 'Nike')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    const alerta = await within(screen.getByRole('dialog')).findByRole('alert')
    expect(alerta).toHaveTextContent('Ya existe una marca con ese nombre.')
  })

  it('al abrir, el foco queda en el primer campo y no en la ×', async () => {
    apiGetMock.mockResolvedValue([marcaFixture()])
    render(<PaginaCatalogo definicion={descriptorMarcas} />)

    await userEvent.click(await screen.findByRole('button', { name: 'Agregar' }))

    await waitFor(() => expect(within(screen.getByRole('dialog')).getByLabelText('Nombre')).toHaveFocus())
  })

  it('con el refresco en vuelo tras guardar, el foco queda en el contenedor de la pantalla y no en <body>', async () => {
    const usuario = userEvent.setup()
    let liberarRefresco: (filas: MarcaListado[]) => void = () => {}
    let cargas = 0
    apiGetMock.mockImplementation(() => {
      cargas += 1
      return cargas === 1
        ? Promise.resolve([marcaFixture()])
        : new Promise<MarcaListado[]>((resolver) => {
            liberarRefresco = resolver
          })
    })
    apiPostMock.mockResolvedValue(undefined)
    const { container } = render(<PaginaCatalogo definicion={descriptorMarcas} />)

    await usuario.click(await screen.findByRole('button', { name: 'Agregar' }))
    await usuario.type(screen.getByLabelText('Nombre'), 'Puma')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await screen.findByText('Se creó "Puma".')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Agregar' })).toBeDisabled()
    expect(document.activeElement).not.toBe(document.body)
    expect(document.activeElement).toBe(container.firstElementChild)

    await act(async () => liberarRefresco([marcaFixture()]))
    expect(screen.getByRole('button', { name: 'Agregar' })).toBeEnabled()
    expect(document.activeElement).toBe(container.firstElementChild)
  })

  it('cancelar devuelve el foco al botón que abrió el modal', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([marcaFixture()])
    render(<PaginaCatalogo definicion={descriptorMarcas} />)

    const abrir = await screen.findByRole('button', { name: 'Agregar' })
    await usuario.click(abrir)
    await usuario.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancelar' }))

    expect(abrir).toHaveFocus()
  })
})

describe('PaginaCatalogo — medios de pago con cuenta de tesorería', () => {
  function medio(sobrescribir: Partial<MedioPagoAdministrado> = {}): MedioPagoAdministrado {
    return {
      id: 1,
      nombre: 'QR',
      activo: true,
      idEmpresa: null,
      orden: 1,
      comportamiento: 'Electronico',
      admiteVuelto: false,
      requiereReferencia: true,
      recargoPorcentaje: null,
      porcentajeFacturacion: 0,
      idCuentaTesoreria: 3,
      nombreCuentaTesoreria: 'Mercado Pago',
      cuentaTesoreriaInactiva: false,
      comisionPorcentaje: 4.5,
      retencionIibbPorcentaje: 1,
      retencionIvaPorcentaje: 0,
      retencionGananciasPorcentaje: 0,
      ...sobrescribir,
    }
  }

  function cuentaDeApi(id: number, nombre: string, activo = true) {
    return { id, idEmpresa: 1, nombreEmpresa: 'Comercio', nombre, saldoInicial: 0, saldoActual: 0, activo, saldoInicialEditable: true }
  }

  function responder(medios: MedioPagoAdministrado[], cuentas: () => Promise<unknown>) {
    apiGetMock.mockImplementation((ruta: string) =>
      ruta.startsWith('/catalogos/medios-pago') ? Promise.resolve(medios) : cuentas(),
    )
  }

  async function abrirEdicion(posicion = 0) {
    const botones = await screen.findAllByRole('button', { name: 'Editar' })
    await userEvent.click(botones[posicion])
    return screen.getByRole('dialog', { name: 'Editar medio de pago' })
  }

  it('al editar un medio electrónico trae las cuentas activas y deja elegida la actual', async () => {
    responder([medio()], () =>
      Promise.resolve([cuentaDeApi(3, 'Mercado Pago'), cuentaDeApi(4, 'Banco'), cuentaDeApi(5, 'Vieja', false)]),
    )
    render(<PaginaCatalogo definicion={descriptorMediosPago} />)

    const dialogo = await abrirEdicion()
    const cuenta = within(dialogo).getByLabelText('Cuenta de tesorería') as HTMLSelectElement
    await waitFor(() => expect(within(cuenta).getByRole('option', { name: 'Banco' })).toBeInTheDocument())

    expect(within(cuenta).queryByRole('option', { name: /Vieja/ })).not.toBeInTheDocument()
    expect(within(cuenta).getByRole('option', { name: '— Sin cuenta —' })).toBeInTheDocument()
    expect(cuenta).toHaveValue('3')
    expect(within(dialogo).getByLabelText('Comisión (%)')).toHaveValue(4.5)
    expect(within(dialogo).getByText(/neto de la comisión y las retenciones/)).toBeInTheDocument()
  })

  it('guardar manda la cuenta elegida y los cuatro porcentajes con tres decimales', async () => {
    const usuario = userEvent.setup()
    responder([medio()], () => Promise.resolve([cuentaDeApi(3, 'Mercado Pago'), cuentaDeApi(4, 'Banco')]))
    apiPutMock.mockResolvedValue(undefined)
    render(<PaginaCatalogo definicion={descriptorMediosPago} />)

    const dialogo = await abrirEdicion()
    const cuenta = within(dialogo).getByLabelText('Cuenta de tesorería')
    await waitFor(() => expect(within(cuenta).getByRole('option', { name: 'Banco' })).toBeInTheDocument())
    await usuario.selectOptions(cuenta, '4')
    const iva = within(dialogo).getByLabelText('Retención IVA (%)')
    await usuario.clear(iva)
    await usuario.type(iva, '0.125')
    await usuario.click(within(dialogo).getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    expect(apiPutMock.mock.calls[0][0]).toBe('/catalogos/medios-pago/1')
    expect(apiPutMock.mock.calls[0][1]).toMatchObject({
      idCuentaTesoreria: 4,
      comisionPorcentaje: 4.5,
      retencionIibbPorcentaje: 1,
      retencionIvaPorcentaje: 0.125,
      retencionGananciasPorcentaje: 0,
    })
  })

  it('si la lectura de cuentas falla avisa, muestra la cuenta actual y el formulario se puede guardar', async () => {
    const usuario = userEvent.setup()
    responder([medio()], () => Promise.reject(new ErrorApi(500, 'x', 'caída')))
    apiPutMock.mockResolvedValue(undefined)
    render(<PaginaCatalogo definicion={descriptorMediosPago} />)

    const dialogo = await abrirEdicion()
    expect(await within(dialogo).findByText(/No se pudieron cargar las cuentas de tesorería/)).toBeInTheDocument()
    const cuenta = within(dialogo).getByLabelText('Cuenta de tesorería')
    expect(cuenta).toHaveValue('3')
    expect(within(cuenta).getByRole('option', { name: 'Mercado Pago' })).toBeInTheDocument()

    await usuario.click(within(dialogo).getByRole('button', { name: 'Guardar' }))
    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    expect(apiPutMock.mock.calls[0][1]).toMatchObject({ idCuentaTesoreria: 3 })
  })

  it('con comportamiento Efectivo los campos no están y lo que se manda va limpio', async () => {
    const usuario = userEvent.setup()
    responder([medio()], () => Promise.resolve([cuentaDeApi(3, 'Mercado Pago')]))
    apiPutMock.mockResolvedValue(undefined)
    render(<PaginaCatalogo definicion={descriptorMediosPago} />)

    const dialogo = await abrirEdicion()
    await usuario.selectOptions(within(dialogo).getByLabelText('Comportamiento'), 'Efectivo')

    expect(within(dialogo).queryByLabelText('Cuenta de tesorería')).not.toBeInTheDocument()
    expect(within(dialogo).queryByLabelText('Comisión (%)')).not.toBeInTheDocument()

    await usuario.click(within(dialogo).getByRole('button', { name: 'Guardar' }))
    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    expect(apiPutMock.mock.calls[0][1]).toMatchObject({
      comportamiento: 'Efectivo',
      idCuentaTesoreria: null,
      comisionPorcentaje: 0,
      retencionIibbPorcentaje: 0,
      retencionIvaPorcentaje: 0,
      retencionGananciasPorcentaje: 0,
    })
  })

  it('una lectura de cuentas que llega después de reabrir otro formulario no pisa al nuevo', async () => {
    const usuario = userEvent.setup()
    let resolverPrimera: (v: unknown) => void = () => {}
    let llamadas = 0
    responder(
      [medio({ id: 1 }), medio({ id: 2, nombre: 'Otro', idCuentaTesoreria: null, nombreCuentaTesoreria: null })],
      () => {
        llamadas += 1
        return llamadas === 1
          ? new Promise((resolver) => {
              resolverPrimera = resolver
            })
          : Promise.resolve([cuentaDeApi(8, 'Segunda lectura')])
      },
    )
    render(<PaginaCatalogo definicion={descriptorMediosPago} />)

    await abrirEdicion(0)
    await usuario.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancelar' }))
    await abrirEdicion(1)

    const cuenta = within(screen.getByRole('dialog')).getByLabelText('Cuenta de tesorería')
    await waitFor(() => expect(within(cuenta).getByRole('option', { name: 'Segunda lectura' })).toBeInTheDocument())

    await act(async () => {
      resolverPrimera([cuentaDeApi(7, 'Primera lectura')])
    })

    expect(within(cuenta).queryByRole('option', { name: 'Primera lectura' })).not.toBeInTheDocument()
    expect(within(cuenta).getByRole('option', { name: 'Segunda lectura' })).toBeInTheDocument()
  })
})
