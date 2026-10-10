import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { LibroIva } from './LibroIva'
import { RutaProtegida } from '../auth/RutaProtegida'
import { ROL } from '../api/tipos'
import type { EmpresaListado, FilaDeLibroIva, LibroIva as LibroIvaRespuesta, UsuarioAutenticado } from '../api/tipos'

const apiGetMock = vi.fn()
const apiDescargarMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: vi.fn(),
    put: vi.fn(),
    delete: vi.fn(),
    descargar: (...args: unknown[]) => apiDescargarMock(...(args as [string])),
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

function usuarioFixture(sobrescribir: Partial<UsuarioAutenticado> = {}): UsuarioAutenticado {
  return {
    id: 9,
    usuario: 'supervisor',
    mail: 'supervisor@ways.test',
    rolId: ROL.Supervisor,
    rol: 'Supervisor',
    ultimaConexion: null,
    idTenant: 1,
    ...sobrescribir,
  }
}

let usuarioActual: UsuarioAutenticado | null = usuarioFixture()

vi.mock('../auth/useAuth', () => ({
  useAuth: () => ({ usuario: usuarioActual, cargando: false, iniciarSesion: vi.fn(), cerrarSesion: vi.fn() }),
}))

function empresaFixture(id: number, razonSocial: string): EmpresaListado {
  return {
    id,
    idTenant: 1,
    razonSocial,
    nombreFantasia: null,
    cuit: null,
    nombreTenant: 'Tenant Demo',
    alicuotaPercepcionIibb: null,
    alicuotaPercepcionIva: null,
  }
}

const EMPRESAS = [empresaFixture(1, 'Empresa Uno'), empresaFixture(2, 'Empresa Dos')]

const RUTA_COMPRAS = '/reportes/libro-iva-compras?desde=2026-05-01&hasta=2026-05-31'
const RUTA_VENTAS = '/reportes/libro-iva-ventas?desde=2026-05-01&hasta=2026-05-31'

function filaFixture(sobrescribir: Partial<FilaDeLibroIva> = {}): FilaDeLibroIva {
  return {
    fecha: '2026-05-10',
    tipoComprobante: 'C-FA',
    numero: '0001-00000001',
    contraparte: 'Proveedor Uno SA',
    documento: '30-70000000-1',
    alicuotas: [
      { porcentaje: 21, neto: 1000, iva: 210 },
      { porcentaje: 10.5, neto: 200, iva: 21 },
    ],
    noGravado: 30,
    exento: 50,
    percepcionIva: 15,
    percepcionIibb: 30,
    total: 1556,
    diferencia: 0,
    advertencias: [],
    netoGravado: 1200,
    ivaTotal: 231,
    ...sobrescribir,
  }
}

function libroDeCompras(sobrescribir: Partial<LibroIvaRespuesta> = {}): LibroIvaRespuesta {
  const segunda = filaFixture({
    fecha: '2026-05-12',
    tipoComprobante: 'C-FB',
    numero: '0002-00000007',
    contraparte: 'Proveedor Dos SRL',
    documento: null,
    alicuotas: [],
    noGravado: 500,
    exento: 0,
    percepcionIva: 0,
    percepcionIibb: 20,
    total: 520,
    netoGravado: 0,
    ivaTotal: 0,
  })

  return {
    desde: '2026-05-01',
    hasta: '2026-05-31',
    idEmpresa: null,
    zonaHoraria: null,
    filas: [filaFixture(), segunda],
    totales: {
      porAlicuota: [
        { porcentaje: 21, neto: 1000, iva: 210 },
        { porcentaje: 10.5, neto: 200, iva: 21 },
      ],
      noGravado: 530,
      exento: 50,
      percepcionIva: 15,
      percepcionIibb: 50,
      total: 2076,
      diferencia: 0,
      netoGravado: 1200,
      ivaTotal: 231,
    },
    ...sobrescribir,
  }
}

function libroDeVentas(): LibroIvaRespuesta {
  const factura = filaFixture({
    tipoComprobante: 'FA',
    numero: '0005-00000042',
    contraparte: 'Cliente Uno SRL',
    documento: 'CUIT 30711111118',
    alicuotas: [{ porcentaje: 21, neto: 100, iva: 21 }],
    noGravado: 0,
    exento: 0,
    percepcionIva: 0,
    percepcionIibb: 0,
    total: 121,
    netoGravado: 100,
    ivaTotal: 21,
  })

  return {
    desde: '2026-05-01',
    hasta: '2026-05-31',
    idEmpresa: null,
    zonaHoraria: null,
    filas: [factura],
    totales: {
      porAlicuota: [{ porcentaje: 21, neto: 100, iva: 21 }],
      noGravado: 0,
      exento: 0,
      percepcionIva: 0,
      percepcionIibb: 0,
      total: 121,
      diferencia: 0,
      netoGravado: 100,
      ivaTotal: 21,
    },
  }
}

function renderLibroIva() {
  return render(<LibroIva />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

function mockearRutas(sobrescribir?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    const propia = sobrescribir?.(ruta)
    if (propia) return propia
    if (ruta === '/empresas') return Promise.resolve(EMPRESAS)
    if (ruta.startsWith('/reportes/libro-iva-compras?')) return Promise.resolve(libroDeCompras())
    if (ruta.startsWith('/reportes/libro-iva-ventas?')) return Promise.resolve(libroDeVentas())
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

function llamadasAlLibro(prefijo: string): string[] {
  return apiGetMock.mock.calls.map((c: unknown[]) => c[0] as string).filter((r) => r.startsWith(prefijo))
}

function diferido<T>() {
  let resolver!: (valor: T) => void
  const promesa = new Promise<T>((resolve) => {
    resolver = resolve
  })
  return { promesa, resolver }
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['Date'] })
  vi.setSystemTime(new Date(2026, 4, 15, 12, 0, 0))
  apiGetMock.mockReset()
  apiDescargarMock.mockReset()
  apiDescargarMock.mockResolvedValue(undefined)
  usuarioActual = usuarioFixture()
})

afterEach(() => {
  vi.useRealTimers()
})

describe('LibroIva — pantalla con pestañas Compras y Ventas', () => {
  it('arranca en Compras con el mes actual y sin empresa, y muestra cada fila y los totales', async () => {
    mockearRutas()
    renderLibroIva()

    await screen.findByText('Proveedor Uno SA')
    expect(llamadasAlLibro('/reportes/libro-iva-compras?')).toEqual([RUTA_COMPRAS])
    expect(screen.getByLabelText('Desde')).toHaveValue('01/05/2026')
    expect(screen.getByLabelText('Hasta')).toHaveValue('31/05/2026')
    expect(screen.getByRole('tab', { name: 'Compras' })).toHaveAttribute('aria-selected', 'true')

    const encabezados = screen.getAllByRole('columnheader').map((c) => c.textContent)
    expect(encabezados).toEqual([
      'Fecha', 'Tipo', 'Número', 'Proveedor', 'CUIT', 'Neto 21%', 'IVA 21%', 'Neto 10,5%', 'IVA 10,5%',
      'No gravado', 'Exento', 'Perc. IVA', 'Perc. IIBB', 'Total',
    ])

    const primera = screen.getByRole('row', { name: /0001-00000001/ })
    expect(within(primera).getAllByRole('cell').map((c) => c.textContent)).toEqual([
      '10/05/2026', 'C-FA', '0001-00000001', 'Proveedor Uno SA', '30-70000000-1',
      '$ 1.000,00', '$ 210,00', '$ 200,00', '$ 21,00', '$ 30,00', '$ 50,00', '$ 15,00', '$ 30,00', '$ 1.556,00',
    ])

    const segunda = screen.getByRole('row', { name: /0002-00000007/ })
    expect(within(segunda).getAllByRole('cell').map((c) => c.textContent)).toEqual([
      '12/05/2026', 'C-FB', '0002-00000007', 'Proveedor Dos SRL', '—',
      '$ 0,00', '$ 0,00', '$ 0,00', '$ 0,00', '$ 500,00', '$ 0,00', '$ 0,00', '$ 20,00', '$ 520,00',
    ])

    const totales = screen.getByRole('row', { name: /Totales/ })
    expect(within(totales).getAllByRole('cell').map((c) => c.textContent)).toEqual([
      'Totales', '$ 1.000,00', '$ 210,00', '$ 200,00', '$ 21,00', '$ 530,00', '$ 50,00', '$ 15,00', '$ 50,00',
      '$ 2.076,00',
    ])
  })

  it('el selector de empresa ofrece Todas y cada empresa; elegir una vuelve a pedir con idEmpresa', async () => {
    mockearRutas()
    const usuario = userEvent.setup()
    renderLibroIva()

    const selector = screen.getByLabelText('Empresa')
    await waitFor(() => expect(within(selector).getAllByRole('option')).toHaveLength(3))
    expect(within(selector).getAllByRole('option').map((o) => o.textContent)).toEqual([
      'Todas', 'Empresa Uno', 'Empresa Dos',
    ])
    await screen.findByText('Proveedor Uno SA')

    await usuario.selectOptions(selector, '2')

    await waitFor(() =>
      expect(llamadasAlLibro('/reportes/libro-iva-compras?')).toContain(
        '/reportes/libro-iva-compras?idEmpresa=2&desde=2026-05-01&hasta=2026-05-31',
      ),
    )
  })

  it('la pestaña Ventas pide libro-iva-ventas con los mismos filtros, cambia los rótulos y no muestra percepciones', async () => {
    mockearRutas()
    const usuario = userEvent.setup()
    renderLibroIva()
    await screen.findByText('Proveedor Uno SA')

    await usuario.click(screen.getByRole('tab', { name: 'Ventas' }))

    await screen.findByText('Cliente Uno SRL')
    expect(llamadasAlLibro('/reportes/libro-iva-ventas?')).toEqual([RUTA_VENTAS])
    expect(screen.getByRole('tab', { name: 'Ventas' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.queryByText('Proveedor Uno SA')).not.toBeInTheDocument()

    const encabezados = screen.getAllByRole('columnheader').map((c) => c.textContent)
    expect(encabezados).toEqual([
      'Fecha', 'Tipo', 'Número', 'Cliente', 'Documento', 'Neto 21%', 'IVA 21%', 'No gravado', 'Exento', 'Total',
    ])
    const fila = screen.getByRole('row', { name: /0005-00000042/ })
    expect(within(fila).getAllByRole('cell').map((c) => c.textContent)).toEqual([
      '10/05/2026', 'FA', '0005-00000042', 'Cliente Uno SRL', 'CUIT 30711111118',
      '$ 100,00', '$ 21,00', '$ 0,00', '$ 0,00', '$ 121,00',
    ])
  })

  it('un cambio de empresa o de período viaja a ambas pestañas', async () => {
    mockearRutas()
    const usuario = userEvent.setup()
    renderLibroIva()
    const selector = screen.getByLabelText('Empresa')
    await waitFor(() => expect(within(selector).getAllByRole('option')).toHaveLength(3))
    await screen.findByText('Proveedor Uno SA')

    await usuario.selectOptions(selector, '1')
    fireEvent.change(screen.getByLabelText('Desde'), { target: { value: '05/05/2026' } })
    await usuario.click(screen.getByRole('tab', { name: 'Ventas' }))

    await screen.findByText('Cliente Uno SRL')
    const ultima = llamadasAlLibro('/reportes/libro-iva-ventas?').at(-1)
    expect(ultima).toBe('/reportes/libro-iva-ventas?idEmpresa=1&desde=2026-05-05&hasta=2026-05-31')
  })

  it('mientras llega el libro de la pestaña nueva no queda a la vista la tabla de la anterior', async () => {
    const ventas = diferido<LibroIvaRespuesta>()
    mockearRutas((ruta) => (ruta.startsWith('/reportes/libro-iva-ventas?') ? ventas.promesa : undefined))
    const usuario = userEvent.setup()
    renderLibroIva()
    await screen.findByText('Proveedor Uno SA')

    await usuario.click(screen.getByRole('tab', { name: 'Ventas' }))

    expect(screen.queryByText('Proveedor Uno SA')).not.toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()

    await act(async () => {
      ventas.resolver(libroDeVentas())
      await ventas.promesa
    })
    expect(await screen.findByText('Cliente Uno SRL')).toBeInTheDocument()
  })

  it('una respuesta vieja de otra pestaña, resuelta tarde, no pisa la tabla actual', async () => {
    const compras = diferido<LibroIvaRespuesta>()
    mockearRutas((ruta) => (ruta.startsWith('/reportes/libro-iva-compras?') ? compras.promesa : undefined))
    const usuario = userEvent.setup()
    renderLibroIva()
    await waitFor(() => expect(llamadasAlLibro('/reportes/libro-iva-compras?')).toHaveLength(1))

    await usuario.click(screen.getByRole('tab', { name: 'Ventas' }))
    await screen.findByText('Cliente Uno SRL')

    await act(async () => {
      compras.resolver(libroDeCompras())
      await compras.promesa
    })

    expect(screen.getByText('Cliente Uno SRL')).toBeInTheDocument()
    expect(screen.queryByText('Proveedor Uno SA')).not.toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'Ventas' })).toHaveAttribute('aria-selected', 'true')
  })

  it('una respuesta vieja de otro período, resuelta tarde, no pisa la del período vigente', async () => {
    const vieja = diferido<LibroIvaRespuesta>()
    mockearRutas((ruta) => (ruta === RUTA_COMPRAS ? vieja.promesa : undefined))
    renderLibroIva()
    await waitFor(() => expect(llamadasAlLibro('/reportes/libro-iva-compras?')).toHaveLength(1))

    fireEvent.change(screen.getByLabelText('Hasta'), { target: { value: '20/05/2026' } })
    await screen.findByText('Proveedor Uno SA')
    expect(llamadasAlLibro('/reportes/libro-iva-compras?')).toHaveLength(2)

    await act(async () => {
      vieja.resolver(
        libroDeCompras({ filas: [filaFixture({ contraparte: 'Proveedor Viejo SA', numero: '9999-00000009' })] }),
      )
      await vieja.promesa
    })

    expect(screen.getByText('Proveedor Uno SA')).toBeInTheDocument()
    expect(screen.queryByText('Proveedor Viejo SA')).not.toBeInTheDocument()
  })

  it('con "desde" posterior a "hasta" no consulta, avisa y deshabilita la descarga', async () => {
    mockearRutas()
    renderLibroIva()
    await screen.findByText('Proveedor Uno SA')
    const antes = llamadasAlLibro('/reportes/libro-iva-compras?').length

    fireEvent.change(screen.getByLabelText('Desde'), { target: { value: '01/06/2026' } })

    expect(await screen.findByText(/“desde” no puede ser posterior a “hasta”/)).toBeInTheDocument()
    expect(llamadasAlLibro('/reportes/libro-iva-compras?')).toHaveLength(antes)
    expect(screen.queryByText('Proveedor Uno SA')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Descargar' })).toBeDisabled()
  })

  it('los comprobantes que no cierran se señalan con un aviso y la columna Dif.', async () => {
    const descuadrada = filaFixture({ numero: '0003-00000003', contraparte: 'Proveedor Tres SA', diferencia: 153 })
    mockearRutas((ruta) =>
      ruta.startsWith('/reportes/libro-iva-compras?')
        ? Promise.resolve(
            libroDeCompras({
              filas: [descuadrada, filaFixture({ numero: '0004-00000004', contraparte: 'Proveedor Cuatro SA' })],
              totales: { ...libroDeCompras().totales, diferencia: 153 },
            }),
          )
        : undefined,
    )
    renderLibroIva()

    expect(await screen.findByText('1 comprobante(s) no cierran contra sus importes: revisá la columna Dif.')).toBeInTheDocument()
    expect(screen.getAllByRole('columnheader').map((c) => c.textContent)).toContain('Dif.')
    const filaMala = screen.getByRole('row', { name: /0003-00000003/ })
    expect(within(filaMala).getAllByRole('cell').at(-1)).toHaveTextContent('$ 153,00')
    const filaBuena = screen.getByRole('row', { name: /0004-00000004/ })
    expect(within(filaBuena).getAllByRole('cell').at(-1)).toHaveTextContent('—')
  })

  it('una venta anulada sin nota de crédito o sin número fiscal se señala con una insignia en Observaciones', async () => {
    const anulada = filaFixture({
      tipoComprobante: 'FA',
      numero: 's/PV-00000042',
      contraparte: 'Cliente Uno SRL',
      advertencias: ['anulado_sin_nc', 'sin_numero_fiscal', 'otro_codigo_nuevo'],
    })
    const normal = filaFixture({ tipoComprobante: 'FA', numero: '0005-00000001', contraparte: 'Cliente Dos SRL' })
    mockearRutas((ruta) =>
      ruta.startsWith('/reportes/libro-iva-ventas?')
        ? Promise.resolve({ ...libroDeVentas(), filas: [anulada, normal] })
        : undefined,
    )
    const usuario = userEvent.setup()
    renderLibroIva()
    await screen.findByText('Proveedor Uno SA')

    await usuario.click(screen.getByRole('tab', { name: 'Ventas' }))

    const filaAnulada = await screen.findByRole('row', { name: /PV-00000042/ })
    expect(screen.getAllByRole('columnheader').map((c) => c.textContent)).toContain('Observaciones')
    expect(within(filaAnulada).getByText('Anulado sin NC')).toBeInTheDocument()
    expect(within(filaAnulada).getByText('Sin número fiscal de PV')).toBeInTheDocument()
    expect(within(filaAnulada).getByText('otro_codigo_nuevo')).toBeInTheDocument()
    const filaNormal = screen.getByRole('row', { name: /0005-00000001/ })
    expect(within(filaNormal).queryByText('Anulado sin NC')).not.toBeInTheDocument()
  })

  it('un libro bien formado no muestra la columna Dif. ni el aviso', async () => {
    mockearRutas()
    renderLibroIva()

    await screen.findByText('Proveedor Uno SA')
    expect(screen.getAllByRole('columnheader').map((c) => c.textContent)).not.toContain('Dif.')
    expect(screen.getAllByRole('columnheader').map((c) => c.textContent)).not.toContain('Observaciones')
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('un período sin comprobantes muestra el estado vacío y ninguna fila de totales', async () => {
    mockearRutas((ruta) =>
      ruta.startsWith('/reportes/libro-iva-compras?')
        ? Promise.resolve(
            libroDeCompras({
              filas: [],
              totales: { ...libroDeCompras().totales, porAlicuota: [], total: 0, noGravado: 0, exento: 0, percepcionIva: 0, percepcionIibb: 0 },
            }),
          )
        : undefined,
    )
    renderLibroIva()

    const vacio = await screen.findByText('No hay comprobantes en el período.')
    expect(vacio).toHaveAttribute('colspan', '10')
    expect(screen.queryByRole('row', { name: /Totales/ })).not.toBeInTheDocument()
  })

  it('un error muestra el mensaje y Reintentar vuelve a pedir el mismo libro', async () => {
    const { ErrorApi } = await import('../api/cliente')
    let fallar = true
    mockearRutas((ruta) => {
      if (ruta.startsWith('/reportes/libro-iva-compras?') && fallar) {
        return Promise.reject(new ErrorApi(500, 'error', 'Falló el libro'))
      }
      return undefined
    })
    const usuario = userEvent.setup()
    renderLibroIva()

    expect(await screen.findByText('Falló el libro')).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()

    fallar = false
    await usuario.click(screen.getByRole('button', { name: 'Reintentar' }))

    await screen.findByText('Proveedor Uno SA')
    expect(screen.queryByText('Falló el libro')).not.toBeInTheDocument()
  })

  it('Descargar baja el export de la pestaña activa con los filtros vigentes', async () => {
    mockearRutas()
    const usuario = userEvent.setup()
    renderLibroIva()
    await screen.findByText('Proveedor Uno SA')

    await usuario.click(screen.getByRole('button', { name: 'Descargar' }))
    await waitFor(() => expect(apiDescargarMock).toHaveBeenCalledTimes(1))
    expect(apiDescargarMock).toHaveBeenLastCalledWith(
      '/reportes/libro-iva-compras/export?desde=2026-05-01&hasta=2026-05-31&formato=xlsx',
    )

    await usuario.click(screen.getByRole('tab', { name: 'Ventas' }))
    await screen.findByText('Cliente Uno SRL')
    await usuario.click(screen.getByRole('button', { name: 'Descargar' }))
    await waitFor(() => expect(apiDescargarMock).toHaveBeenCalledTimes(2))
    expect(apiDescargarMock).toHaveBeenLastCalledWith(
      '/reportes/libro-iva-ventas/export?desde=2026-05-01&hasta=2026-05-31&formato=xlsx',
    )
  })

  it('si no cargan las empresas, el libro igual se consulta con "Todas" y se avisa del error', async () => {
    const { ErrorApi } = await import('../api/cliente')
    mockearRutas((ruta) => (ruta === '/empresas' ? Promise.reject(new ErrorApi(500, 'error', 'Sin empresas')) : undefined))
    renderLibroIva()

    expect(await screen.findByText('Sin empresas')).toBeInTheDocument()
    await screen.findByText('Proveedor Uno SA')
    expect(within(screen.getByLabelText('Empresa')).getAllByRole('option')).toHaveLength(1)
  })

  it('un Vendedor es redirigido por el mismo gate de rol que usa App.tsx', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor' })
    mockearRutas()
    render(
      <MemoryRouter initialEntries={['/reportes/libro-iva']}>
        <Routes>
          <Route
            path="/reportes/libro-iva"
            element={
              <RutaProtegida rolesPermitidos={[ROL.Supervisor, ROL.Admin]}>
                <LibroIva />
              </RutaProtegida>
            }
          />
          <Route path="/" element={<div>Inicio (redirigido)</div>} />
        </Routes>
      </MemoryRouter>,
    )

    expect(await screen.findByText('Inicio (redirigido)')).toBeInTheDocument()
    expect(llamadasAlLibro('/reportes/libro-iva')).toHaveLength(0)
  })
})
