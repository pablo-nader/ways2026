import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Parametros } from './Parametros'
import type { EmpresaListado, ParametroListado, PuntoVentaListado } from '../api/tipos'

const apiGetMock = vi.fn()
const apiPutMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: vi.fn(),
    put: (...args: unknown[]) => apiPutMock(...(args as [string, unknown])),
    delete: vi.fn(),
  },
  ErrorApi: class ErrorApiMock extends Error {},
}))

const empresaUno: EmpresaListado = {
  id: 1,
  idTenant: 1,
  razonSocial: 'Empresa Uno SA',
  nombreFantasia: null,
  cuit: null,
  nombreTenant: 'Tenant Demo',
}

const puntoVentaUno: PuntoVentaListado = {
  id: 1,
  idTenant: 1,
  idEmpresa: 1,
  nombre: 'Local 1',
  domicilio: null,
  horario: null,
  whatsapp: null,
  instagram: null,
  facebook: null,
  web: null,
  nombreTenant: 'Tenant Demo',
  razonSocialEmpresa: 'Empresa Demo',
}

function mockearRutasBase(items: ParametroListado[] = []) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/empresas') return Promise.resolve([empresaUno])
    if (ruta === '/puntos-venta') return Promise.resolve([puntoVentaUno])
    if (ruta.startsWith('/parametros?idEmpresa=')) return Promise.resolve(items)
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPutMock.mockReset()
  apiPutMock.mockResolvedValue({ id: 1, clave: 'x', valor: '"x"', idPuntoVenta: null })
})

describe('Parametros — clave zona_horaria (stage-10, design decisión 12)', () => {
  it('renderiza un <select> de zonas ofrecidas y envía el valor JSON-quoteado', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()

    render(<Parametros />)
    await screen.findByText('Empresa Uno SA')

    await usuario.selectOptions(screen.getByLabelText('Clave'), 'zona_horaria')

    const selectorDeValor = screen.getByLabelText('Valor')
    expect(selectorDeValor.tagName).toBe('SELECT')
    expect(screen.getByRole('option', { name: 'Córdoba' })).toBeInTheDocument()

    await usuario.selectOptions(selectorDeValor, 'America/Argentina/Cordoba')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [, datos] = apiPutMock.mock.calls[0] as [string, { clave: string; valor: string }]
    expect(datos.clave).toBe('zona_horaria')
    expect(datos.valor).toBe('"America/Argentina/Cordoba"')
  })

  it('nunca permite un valor libre: no hay ningún <input> de texto para zona_horaria', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()

    render(<Parametros />)
    await screen.findByText('Empresa Uno SA')

    await usuario.selectOptions(screen.getByLabelText('Clave'), 'zona_horaria')

    expect(screen.queryByRole('spinbutton', { name: 'Valor' })).not.toBeInTheDocument()
  })
})

describe('Parametros — claves numéricas existentes (flujo sin cambios)', () => {
  it('sigue enviando tolerancia_pago como un número JSON, no un string', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()

    render(<Parametros />)
    await screen.findByText('Empresa Uno SA')

    // tolerancia_pago es la clave por defecto: el input numérico ya está montado.
    expect(screen.getByLabelText('Valor').tagName).toBe('INPUT')

    await usuario.type(screen.getByLabelText('Valor'), '15')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [, datos] = apiPutMock.mock.calls[0] as [string, { clave: string; valor: string }]
    expect(datos.clave).toBe('tolerancia_pago')
    expect(datos.valor).toBe('15')
  })

  it('slots_tickets_espera (entero) sigue truncando a un número entero JSON', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()

    render(<Parametros />)
    await screen.findByText('Empresa Uno SA')

    await usuario.selectOptions(screen.getByLabelText('Clave'), 'slots_tickets_espera')
    await usuario.type(screen.getByLabelText('Valor'), '12')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [, datos] = apiPutMock.mock.calls[0] as [string, { clave: string; valor: string }]
    expect(datos.valor).toBe('12')
  })
})

// ---- Claves nuevas de stage-12-lotes-vencimientos (Slice 15) -----------------------------------

describe('Parametros — clave lotes_habilitado (tipo booleano, nuevo en el registro)', () => {
  it('renderiza un checkbox — nunca un <input type="number"> ni un <select> — y arranca deshabilitado', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()

    render(<Parametros />)
    await screen.findByText('Empresa Uno SA')

    await usuario.selectOptions(screen.getByLabelText('Clave'), 'lotes_habilitado')

    const selectorDeValor = screen.getByLabelText('Valor')
    expect(selectorDeValor.tagName).toBe('INPUT')
    expect((selectorDeValor as HTMLInputElement).type).toBe('checkbox')
    expect(selectorDeValor).not.toBeChecked()
  })

  it('coerción boolean: tildarlo y guardar manda el JSON crudo `true`, no `"true"` ni `1`', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()

    render(<Parametros />)
    await screen.findByText('Empresa Uno SA')

    await usuario.selectOptions(screen.getByLabelText('Clave'), 'lotes_habilitado')
    await usuario.click(screen.getByLabelText('Valor'))
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [, datos] = apiPutMock.mock.calls[0] as [string, { clave: string; valor: string }]
    expect(datos.clave).toBe('lotes_habilitado')
    expect(datos.valor).toBe('true')
  })

  it('sin tildar, guarda el JSON crudo `false`', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()

    render(<Parametros />)
    await screen.findByText('Empresa Uno SA')

    await usuario.selectOptions(screen.getByLabelText('Clave'), 'lotes_habilitado')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [, datos] = apiPutMock.mock.calls[0] as [string, { clave: string; valor: string }]
    expect(datos.valor).toBe('false')
  })

  it('cambiar de clave a otra numérica resetea el checkbox — nunca queda pisado un "true" residual', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()

    render(<Parametros />)
    await screen.findByText('Empresa Uno SA')

    await usuario.selectOptions(screen.getByLabelText('Clave'), 'lotes_habilitado')
    await usuario.click(screen.getByLabelText('Valor'))
    await usuario.selectOptions(screen.getByLabelText('Clave'), 'dias_alerta_vencimiento')

    expect(screen.getByLabelText('Valor').tagName).toBe('INPUT')
    expect((screen.getByLabelText('Valor') as HTMLInputElement).type).toBe('number')
    expect(screen.getByLabelText('Valor')).toHaveValue(null)
  })
})

describe('Parametros — clave dias_alerta_vencimiento (entero, nuevo en el registro)', () => {
  it('envía un número entero JSON, con el default 30 en el placeholder', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()

    render(<Parametros />)
    await screen.findByText('Empresa Uno SA')

    await usuario.selectOptions(screen.getByLabelText('Clave'), 'dias_alerta_vencimiento')
    expect(screen.getByPlaceholderText('Default: 30')).toBeInTheDocument()

    await usuario.type(screen.getByLabelText('Valor'), '45')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [, datos] = apiPutMock.mock.calls[0] as [string, { clave: string; valor: string }]
    expect(datos.clave).toBe('dias_alerta_vencimiento')
    expect(datos.valor).toBe('45')
  })
})
