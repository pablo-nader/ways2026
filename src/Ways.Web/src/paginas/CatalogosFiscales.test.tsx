import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { CatalogosFiscales } from './CatalogosFiscales'
import { ErrorApi } from '../api/cliente'
import type { AlicuotaIvaListado, CondicionFiscalListado, TipoComprobanteListado } from '../api/tipos'

const apiGetMock = vi.fn()

vi.mock('../api/cliente', () => ({
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

function condicion(sobrescribir: Partial<CondicionFiscalListado> & Pick<CondicionFiscalListado, 'id' | 'codigo' | 'nombre'>) {
  return { codigoAfip: null, activo: true, ...sobrescribir } satisfies CondicionFiscalListado
}

function alicuota(sobrescribir: Partial<AlicuotaIvaListado> & Pick<AlicuotaIvaListado, 'id' | 'nombre' | 'porcentaje'>) {
  return { codigoAfip: null, activo: true, ...sobrescribir } satisfies AlicuotaIvaListado
}

function tipo(sobrescribir: Partial<TipoComprobanteListado> & Pick<TipoComprobanteListado, 'id' | 'codigo' | 'nombre'>) {
  return {
    clase: 'Venta',
    letra: 'A',
    signo: 1,
    discriminaIva: true,
    esFiscal: true,
    afectaStock: true,
    codigoAfip: null,
    activo: true,
    registraLibroIva: true,
    ...sobrescribir,
  } satisfies TipoComprobanteListado
}

const condiciones = [
  condicion({ id: 1, codigo: 'RI', nombre: 'Responsable Inscripto', codigoAfip: 13 }),
  condicion({ id: 2, codigo: 'EX', nombre: 'Exento', codigoAfip: null, activo: false }),
  condicion({ id: 3, codigo: 'CF', nombre: 'Consumidor Final', codigoAfip: 5 }),
]

const alicuotas = [
  alicuota({ id: 1, nombre: 'IVA 21%', porcentaje: 21, codigoAfip: 5 }),
  alicuota({ id: 2, nombre: 'Exento', porcentaje: 0, codigoAfip: 2, activo: false }),
  alicuota({ id: 3, nombre: 'IVA 10,5%', porcentaje: 10.5, codigoAfip: 4 }),
  alicuota({ id: 4, nombre: 'IVA 2,5%', porcentaje: 2.5 }),
]

const tipos = [
  tipo({ id: 3, codigo: 'REM', nombre: 'Remito', clase: 'Venta', letra: null, discriminaIva: false, esFiscal: false, activo: false }),
  tipo({ id: 1, codigo: 'FA', nombre: 'Factura A', clase: 'Venta', letra: 'A' }),
  tipo({ id: 2, codigo: 'NCC', nombre: 'Nota de crédito compra', clase: 'Compra', letra: 'B', signo: -1, afectaStock: false }),
]

function mockearApiGet(datos = { condiciones, alicuotas, tipos }) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/catalogos-fiscales/condiciones-fiscales') return Promise.resolve(datos.condiciones)
    if (ruta === '/catalogos-fiscales/alicuotas-iva') return Promise.resolve(datos.alicuotas)
    if (ruta === '/catalogos-fiscales/tipos-comprobante') return Promise.resolve(datos.tipos)

    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

async function montar(datos?: Parameters<typeof mockearApiGet>[0]) {
  mockearApiGet(datos)
  render(<CatalogosFiscales />)
  await screen.findByRole('table', { name: 'Tipos de comprobante' })
}

function tabla(nombre: string) {
  return within(screen.getByRole('table', { name: nombre }))
}

/** Las dos primeras filas son el encabezado y los filtros; `columna` es la celda que identifica cada fila. */
function columnaEnPantalla(nombreDeTabla: string, columna: number) {
  return tabla(nombreDeTabla)
    .getAllByRole('row')
    .slice(2)
    .map((f) => within(f).getAllByRole('cell')[columna]?.textContent)
}

beforeEach(() => {
  apiGetMock.mockReset()
})

describe('CatalogosFiscales — carga', () => {
  it('rinde las tres tablas con sus filas', async () => {
    await montar()

    expect(columnaEnPantalla('Condiciones fiscales', 0)).toEqual(['RI', 'EX', 'CF'])
    expect(columnaEnPantalla('Alícuotas de IVA', 0)).toEqual(['IVA 21%', 'Exento', 'IVA 10,5%', 'IVA 2,5%'])
    expect(columnaEnPantalla('Tipos de comprobante', 1)).toEqual(['REM', 'FA', 'NCC'])
  })

  it('un fallo de carga rinde el mensaje del servidor', async () => {
    apiGetMock.mockRejectedValue(new ErrorApi(500, 'error_interno', 'Se cayó el catálogo.'))
    render(<CatalogosFiscales />)

    expect(await screen.findByText('Se cayó el catálogo.')).toBeInTheDocument()
  })
})

describe('CatalogosFiscales — orden y filtros por columna', () => {
  it.each(['Condiciones fiscales', 'Alícuotas de IVA', 'Tipos de comprobante'])(
    'en %s la fila de filtros tiene una celda por columna del encabezado',
    async (nombre) => {
      await montar()

      const [encabezados, filtros] = within(screen.getByRole('table', { name: nombre })).getAllByRole('row')
      const celdasDeEncabezado = within(encabezados).getAllByRole('columnheader')

      expect(within(filtros).getAllByRole('columnheader')).toHaveLength(celdasDeEncabezado.length)
      expect(within(filtros).getAllByRole('searchbox').length + within(filtros).getAllByRole('combobox').length).toBe(
        celdasDeEncabezado.length,
      )
    },
  )

  it.each([
    ['Condiciones fiscales', ['Código', 'Nombre', 'Código AFIP/ARCA', 'Estado']],
    ['Alícuotas de IVA', ['Nombre', 'Porcentaje', 'Código AFIP/ARCA', 'Estado']],
    [
      'Tipos de comprobante',
      ['Clase', 'Código', 'Nombre', 'Letra', 'Signo', 'Discrimina IVA', 'Fiscal', 'Afecta stock', 'Estado'],
    ],
  ])('en %s todas las columnas se pueden ordenar', async (nombre, columnas) => {
    await montar()

    for (const columna of columnas) {
      expect(tabla(nombre).getByRole('button', { name: columna })).toBeInTheDocument()
    }
  })

  describe('condiciones fiscales', () => {
    it('el código AFIP se ordena como número y la condición sin código va al final en ambas direcciones', async () => {
      const usuario = userEvent.setup()
      await montar()
      const boton = tabla('Condiciones fiscales').getByRole('button', { name: 'Código AFIP/ARCA' })
      const encabezado = tabla('Condiciones fiscales').getByRole('columnheader', { name: 'Código AFIP/ARCA' })

      await usuario.click(boton)
      expect(columnaEnPantalla('Condiciones fiscales', 0)).toEqual(['CF', 'RI', 'EX'])
      expect(encabezado).toHaveAttribute('aria-sort', 'ascending')

      await usuario.click(boton)
      expect(columnaEnPantalla('Condiciones fiscales', 0)).toEqual(['RI', 'CF', 'EX'])
      expect(encabezado).toHaveAttribute('aria-sort', 'descending')

      await usuario.click(boton)
      expect(columnaEnPantalla('Condiciones fiscales', 0)).toEqual(['RI', 'EX', 'CF'])
      expect(encabezado).toHaveAttribute('aria-sort', 'none')
    })

    it('el nombre se ordena sin distinguir acentos ni mayúsculas', async () => {
      const usuario = userEvent.setup()
      await montar()

      await usuario.click(tabla('Condiciones fiscales').getByRole('button', { name: 'Nombre' }))

      expect(columnaEnPantalla('Condiciones fiscales', 0)).toEqual(['CF', 'EX', 'RI'])
    })

    it('el filtro de texto y el de estado se combinan; limpiar los quita', async () => {
      const usuario = userEvent.setup()
      await montar()
      const fiscales = tabla('Condiciones fiscales')

      await usuario.type(fiscales.getByRole('searchbox', { name: 'Filtrar por nombre' }), 'INSCRIPTO')
      expect(columnaEnPantalla('Condiciones fiscales', 0)).toEqual(['RI'])

      await usuario.click(screen.getByRole('button', { name: 'Limpiar filtros de condiciones fiscales' }))
      await usuario.selectOptions(fiscales.getByRole('combobox', { name: 'Filtrar por estado' }), 'Inactivo')
      expect(columnaEnPantalla('Condiciones fiscales', 0)).toEqual(['EX'])

      await usuario.type(fiscales.getByRole('searchbox', { name: 'Filtrar por código' }), 'RI')
      expect(fiscales.getByText('Ningún registro coincide con los filtros.')).toBeInTheDocument()

      await usuario.click(screen.getByRole('button', { name: 'Limpiar filtros de condiciones fiscales' }))
      expect(columnaEnPantalla('Condiciones fiscales', 0)).toEqual(['RI', 'EX', 'CF'])
      expect(fiscales.getByRole('searchbox', { name: 'Filtrar por código' })).toHaveValue('')
    })

    it('la condición sin código AFIP se filtra por el guion que muestra su celda', async () => {
      const usuario = userEvent.setup()
      await montar()

      await usuario.type(tabla('Condiciones fiscales').getByRole('searchbox', { name: 'Filtrar por código afip/arca' }), '—')

      expect(columnaEnPantalla('Condiciones fiscales', 0)).toEqual(['EX'])
    })

    it('filtrar una tabla no toca a las otras', async () => {
      const usuario = userEvent.setup()
      await montar()

      await usuario.type(tabla('Condiciones fiscales').getByRole('searchbox', { name: 'Filtrar por nombre' }), 'exento')

      expect(columnaEnPantalla('Condiciones fiscales', 0)).toEqual(['EX'])
      expect(columnaEnPantalla('Alícuotas de IVA', 0)).toEqual(['IVA 21%', 'Exento', 'IVA 10,5%', 'IVA 2,5%'])
      expect(tabla('Alícuotas de IVA').getByRole('searchbox', { name: 'Filtrar por nombre' })).toHaveValue('')
    })
  })

  describe('alícuotas de IVA', () => {
    it('el porcentaje se ordena como número, ascendente, descendente y vuelve al orden original', async () => {
      const usuario = userEvent.setup()
      await montar()
      const boton = tabla('Alícuotas de IVA').getByRole('button', { name: 'Porcentaje' })

      await usuario.click(boton)
      expect(columnaEnPantalla('Alícuotas de IVA', 1)).toEqual(['0%', '2.5%', '10.5%', '21%'])

      await usuario.click(boton)
      expect(columnaEnPantalla('Alícuotas de IVA', 1)).toEqual(['21%', '10.5%', '2.5%', '0%'])

      await usuario.click(boton)
      expect(columnaEnPantalla('Alícuotas de IVA', 1)).toEqual(['21%', '0%', '10.5%', '2.5%'])
    })

    it('el filtro de porcentaje busca en lo que muestra la celda', async () => {
      const usuario = userEvent.setup()
      await montar()

      await usuario.type(tabla('Alícuotas de IVA').getByRole('searchbox', { name: 'Filtrar por porcentaje' }), '10.5')

      expect(columnaEnPantalla('Alícuotas de IVA', 0)).toEqual(['IVA 10,5%'])
    })

    it('el filtro de estado es un select y el de código AFIP busca el guion de la celda vacía', async () => {
      const usuario = userEvent.setup()
      await montar()
      const iva = tabla('Alícuotas de IVA')

      await usuario.selectOptions(iva.getByRole('combobox', { name: 'Filtrar por estado' }), 'Inactivo')
      expect(columnaEnPantalla('Alícuotas de IVA', 0)).toEqual(['Exento'])

      await usuario.click(screen.getByRole('button', { name: 'Limpiar filtros de alícuotas de iva' }))
      await usuario.type(iva.getByRole('searchbox', { name: 'Filtrar por código afip/arca' }), '—')
      expect(columnaEnPantalla('Alícuotas de IVA', 0)).toEqual(['IVA 2,5%'])
    })
  })

  describe('tipos de comprobante', () => {
    it('la letra se ordena como texto y el tipo sin letra va al final en ambas direcciones', async () => {
      const usuario = userEvent.setup()
      await montar()
      const boton = tabla('Tipos de comprobante').getByRole('button', { name: 'Letra' })

      await usuario.click(boton)
      expect(columnaEnPantalla('Tipos de comprobante', 1)).toEqual(['FA', 'NCC', 'REM'])

      await usuario.click(boton)
      expect(columnaEnPantalla('Tipos de comprobante', 1)).toEqual(['NCC', 'FA', 'REM'])
    })

    it('el signo se ordena como número: −1 antes que +1', async () => {
      const usuario = userEvent.setup()
      await montar()

      await usuario.click(tabla('Tipos de comprobante').getByRole('button', { name: 'Signo' }))

      expect(columnaEnPantalla('Tipos de comprobante', 1)).toEqual(['NCC', 'REM', 'FA'])
    })

    it('el nombre se ordena alfabéticamente en ambas direcciones', async () => {
      const usuario = userEvent.setup()
      await montar()

      await usuario.click(tabla('Tipos de comprobante').getByRole('button', { name: 'Nombre' }))

      expect(columnaEnPantalla('Tipos de comprobante', 1)).toEqual(['FA', 'NCC', 'REM'])

      await usuario.click(tabla('Tipos de comprobante').getByRole('button', { name: 'Nombre' }))

      expect(columnaEnPantalla('Tipos de comprobante', 1)).toEqual(['REM', 'NCC', 'FA'])
    })

    it.each([
      ['clase', 'Compra', ['NCC']],
      ['signo', '−1', ['NCC']],
      ['discrimina iva', 'No', ['REM']],
      ['fiscal', 'No', ['REM']],
      ['afecta stock', 'No', ['NCC']],
      ['estado', 'Inactivo', ['REM']],
    ])('el filtro de %s es un select que acota las filas', async (columna, opcion, codigos) => {
      const usuario = userEvent.setup()
      await montar()

      await usuario.selectOptions(tabla('Tipos de comprobante').getByRole('combobox', { name: `Filtrar por ${columna}` }), opcion)

      expect(columnaEnPantalla('Tipos de comprobante', 1)).toEqual(codigos)
    })

    it.each([
      ['código', 'rem', ['REM']],
      ['nombre', 'CRÉDITO', ['NCC']],
      ['letra', 'b', ['NCC']],
    ])('el filtro de %s es de texto y no distingue acentos ni mayúsculas', async (columna, texto, codigos) => {
      const usuario = userEvent.setup()
      await montar()

      await usuario.type(tabla('Tipos de comprobante').getByRole('searchbox', { name: `Filtrar por ${columna}` }), texto)

      expect(columnaEnPantalla('Tipos de comprobante', 1)).toEqual(codigos)
    })

    it('los filtros se combinan, sin coincidencias avisa y limpiar los quita', async () => {
      const usuario = userEvent.setup()
      await montar()
      const comprobantes = tabla('Tipos de comprobante')

      await usuario.selectOptions(comprobantes.getByRole('combobox', { name: 'Filtrar por clase' }), 'Venta')
      expect(columnaEnPantalla('Tipos de comprobante', 1)).toEqual(['REM', 'FA'])

      await usuario.selectOptions(comprobantes.getByRole('combobox', { name: 'Filtrar por fiscal' }), 'Sí')
      expect(columnaEnPantalla('Tipos de comprobante', 1)).toEqual(['FA'])

      await usuario.type(comprobantes.getByRole('searchbox', { name: 'Filtrar por código' }), 'REM')
      expect(comprobantes.getByText('Ningún registro coincide con los filtros.')).toBeInTheDocument()

      await usuario.click(screen.getByRole('button', { name: 'Limpiar filtros de tipos de comprobante' }))
      expect(columnaEnPantalla('Tipos de comprobante', 1)).toEqual(['REM', 'FA', 'NCC'])
    })

    it('limpiar filtros está deshabilitado mientras no hay ninguno', async () => {
      const usuario = userEvent.setup()
      await montar()
      const limpiar = screen.getByRole('button', { name: 'Limpiar filtros de tipos de comprobante' })
      expect(limpiar).toBeDisabled()

      await usuario.type(tabla('Tipos de comprobante').getByRole('searchbox', { name: 'Filtrar por nombre' }), 'f')

      await waitFor(() => expect(limpiar).toBeEnabled())
    })
  })

  it('un catálogo vacío avisa que no hay registros y no que los filtros no coinciden', async () => {
    await montar({ condiciones: [], alicuotas, tipos })

    expect(tabla('Condiciones fiscales').getByText('No hay registros cargados.')).toBeInTheDocument()
  })
})
