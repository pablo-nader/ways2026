import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { PaginaCatalogo } from './PaginaCatalogo'
import { etiquetaParaValorFaltante } from './etiquetaParaValorFaltante'
import { descriptorListasPrecio } from '../api/catalogos'
import type { DescriptorDeCatalogo } from '../api/catalogos'
import type { CatalogoListado, ListaPrecioListado } from '../api/tipos'

const apiGetMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: { get: (...args: unknown[]) => apiGetMock(...(args as [string])) },
  ErrorApi: class ErrorApiMock extends Error {},
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
