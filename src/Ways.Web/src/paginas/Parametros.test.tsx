import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Parametros } from './Parametros'
import { Toaster } from '../toasts/Toaster'
import type { EmpresaListado, ParametroListado, PuntoVentaListado, SucursalListado } from '../api/tipos'

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
  alicuotaPercepcionIibb: null,
  alicuotaPercepcionIva: null,
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
  modo: 'Web',
}

const puntoVentaDos: PuntoVentaListado = { ...puntoVentaUno, id: 2, nombre: 'Local 2' }

const sucursalCentro: SucursalListado = {
  id: 10,
  idTenant: 1,
  idEmpresa: 1,
  nombre: 'Centro',
  razonSocialEmpresa: 'Empresa Uno SA',
  cantidadPuntosVenta: 2,
}

const sucursalNorte: SucursalListado = { ...sucursalCentro, id: 11, nombre: 'Norte', cantidadPuntosVenta: 0 }

function mockearRutasBase(items: ParametroListado[] = []) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/empresas') return Promise.resolve([empresaUno])
    if (ruta === '/puntos-venta') return Promise.resolve([puntoVentaUno, puntoVentaDos])
    if (ruta === '/sucursales') return Promise.resolve([sucursalCentro, sucursalNorte])
    if (ruta.startsWith('/parametros?idEmpresa=')) return Promise.resolve(items)
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPutMock.mockReset()
  apiPutMock.mockResolvedValue({ id: 1, clave: 'x', valor: '"x"', idPuntoVenta: null, idSucursal: null })
})

describe('Parametros — clave zona_horaria (stage-10, design decisión 12)', () => {
  it('renderiza un <select> de zonas ofrecidas y envía el valor JSON-quoteado', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()

    render(<><Parametros /><Toaster /></>)
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

    render(<><Parametros /><Toaster /></>)
    await screen.findByText('Empresa Uno SA')

    await usuario.selectOptions(screen.getByLabelText('Clave'), 'zona_horaria')

    expect(screen.queryByRole('spinbutton', { name: 'Valor' })).not.toBeInTheDocument()
  })
})

describe('Parametros — claves numéricas existentes (flujo sin cambios)', () => {
  it('sigue enviando tolerancia_pago como un número JSON, no un string', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()

    render(<><Parametros /><Toaster /></>)
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

    render(<><Parametros /><Toaster /></>)
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

    render(<><Parametros /><Toaster /></>)
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

    render(<><Parametros /><Toaster /></>)
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

    render(<><Parametros /><Toaster /></>)
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

    render(<><Parametros /><Toaster /></>)
    await screen.findByText('Empresa Uno SA')

    await usuario.selectOptions(screen.getByLabelText('Clave'), 'lotes_habilitado')
    await usuario.click(screen.getByLabelText('Valor'))
    await usuario.selectOptions(screen.getByLabelText('Clave'), 'dias_alerta_vencimiento')

    expect(screen.getByLabelText('Valor').tagName).toBe('INPUT')
    expect((screen.getByLabelText('Valor') as HTMLInputElement).type).toBe('text')
    expect(screen.getByLabelText('Valor')).toHaveValue('')
  })
})

describe('Parametros — clave dias_alerta_vencimiento (entero, nuevo en el registro)', () => {
  it('envía un número entero JSON, con el default 30 en el placeholder', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()

    render(<><Parametros /><Toaster /></>)
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

describe('Parametros — clave redondeo_sugerencia_precio (decimal, default 100)', () => {
  it('se ofrece en el editor, con el default 100 en el placeholder, y envía el decimal', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()

    render(<><Parametros /><Toaster /></>)
    await screen.findByText('Empresa Uno SA')

    await usuario.selectOptions(screen.getByLabelText('Clave'), 'redondeo_sugerencia_precio')
    expect(screen.getByPlaceholderText('Default: 100')).toBeInTheDocument()

    await usuario.type(screen.getByLabelText('Valor'), '50')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [, datos] = apiPutMock.mock.calls[0] as [string, { clave: string; valor: string }]
    expect(datos.clave).toBe('redondeo_sugerencia_precio')
    expect(datos.valor).toBe('50')
  })
})

// ---- T6 sucursales: cada clave se configura en su nivel ------------------------------------------

type CuerpoDeAlta = { clave: string; valor: string; idPuntoVenta: number | null; idSucursal: number | null }

describe('Parametros — alcance por sucursal o por caja', () => {
  it('una clave de caja ofrece el punto de venta y manda idPuntoVenta, nunca idSucursal', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()

    render(<Parametros />)
    await screen.findByText('Empresa Uno SA')

    expect(screen.getByLabelText('Punto de venta')).toBeInTheDocument()
    expect(screen.queryByLabelText('Sucursal')).not.toBeInTheDocument()

    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), 'Local 2')
    await usuario.type(screen.getByLabelText('Valor'), '15')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [, datos] = apiPutMock.mock.calls[0] as [string, CuerpoDeAlta]
    expect(datos).toEqual({ clave: 'tolerancia_pago', valor: '15', idPuntoVenta: 2, idSucursal: null })
  })

  it('una clave de sucursal ofrece la sucursal y manda idSucursal, nunca idPuntoVenta', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()

    render(<Parametros />)
    await screen.findByText('Empresa Uno SA')

    await usuario.selectOptions(screen.getByLabelText('Clave'), 'zona_horaria')

    expect(screen.getByLabelText('Sucursal')).toBeInTheDocument()
    expect(screen.queryByLabelText('Punto de venta')).not.toBeInTheDocument()

    await usuario.selectOptions(screen.getByLabelText('Sucursal'), 'Norte')
    await usuario.selectOptions(screen.getByLabelText('Valor'), 'America/Argentina/Cordoba')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [, datos] = apiPutMock.mock.calls[0] as [string, CuerpoDeAlta]
    expect(datos).toEqual({
      clave: 'zona_horaria',
      valor: '"America/Argentina/Cordoba"',
      idPuntoVenta: null,
      idSucursal: 11,
    })
  })

  it('cada clave de sucursal del registro se ofrece por sucursal y el resto por caja', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()

    render(<Parametros />)
    await screen.findByText('Empresa Uno SA')

    for (const clave of ['zona_horaria', 'lotes_habilitado', 'dias_alerta_vencimiento']) {
      await usuario.selectOptions(screen.getByLabelText('Clave'), clave)
      expect(screen.getByLabelText('Sucursal')).toBeInTheDocument()
    }
    for (const clave of ['tolerancia_pago', 'importe_envio', 'slots_tickets_espera', 'comision_porcentaje']) {
      await usuario.selectOptions(screen.getByLabelText('Clave'), clave)
      expect(screen.getByLabelText('Punto de venta')).toBeInTheDocument()
    }
  })

  it('al pasar de una clave de sucursal a una de caja no arrastra la sucursal elegida', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()

    render(<Parametros />)
    await screen.findByText('Empresa Uno SA')

    await usuario.selectOptions(screen.getByLabelText('Clave'), 'dias_alerta_vencimiento')
    await usuario.selectOptions(screen.getByLabelText('Sucursal'), 'Centro')
    await usuario.selectOptions(screen.getByLabelText('Clave'), 'tolerancia_pago')
    await usuario.type(screen.getByLabelText('Valor'), '12')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    const [, datos] = apiPutMock.mock.calls[0] as [string, CuerpoDeAlta]
    expect(datos.idSucursal).toBeNull()
    expect(datos.idPuntoVenta).toBeNull()
  })

  it('«Probar» una clave de sucursal consulta por idSucursal, y una de caja por idPuntoVenta', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()

    render(<Parametros />)
    await screen.findByText('Empresa Uno SA')
    await screen.findByText(/Esta empresa no tiene parámetros configurados/)

    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/parametros/')) return Promise.resolve({ clave: 'x', valor: '"UTC"' })
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    await usuario.selectOptions(screen.getByLabelText('Clave'), 'zona_horaria')
    await usuario.selectOptions(screen.getByLabelText('Sucursal'), 'Centro')
    await usuario.click(screen.getByRole('button', { name: 'Probar' }))
    expect(await screen.findByText(/Valor resuelto para/)).toBeInTheDocument()
    expect(apiGetMock).toHaveBeenLastCalledWith('/parametros/zona_horaria?idEmpresa=1&idSucursal=10')

    await usuario.selectOptions(screen.getByLabelText('Clave'), 'tolerancia_pago')
    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), 'Local 1')
    await usuario.click(screen.getByRole('button', { name: 'Probar' }))
    await waitFor(() =>
      expect(apiGetMock).toHaveBeenLastCalledWith('/parametros/tolerancia_pago?idEmpresa=1&idPuntoVenta=1'),
    )
  })

  it('el listado nombra la sucursal, el punto de venta o toda la empresa, y no inventa «toda la empresa» para uno ausente', async () => {
    const base = { id: 1, valor: '1', idPuntoVenta: null, idSucursal: null }
    mockearRutasBase([
      { ...base, id: 1, clave: 'dias_alerta_vencimiento', idSucursal: 11 },
      { ...base, id: 2, clave: 'tolerancia_pago', idPuntoVenta: 2 },
      { ...base, id: 3, clave: 'importe_envio' },
      { ...base, id: 4, clave: 'slots_tickets_espera', idPuntoVenta: 99 },
    ])

    render(<Parametros />)
    await screen.findByText('Empresa Uno SA')

    const celda = await screen.findByText('dias_alerta_vencimiento')
    const textoDeFila = (clave: string) => screen.getByText(clave).closest('tr')?.textContent
    expect(celda).toBeInTheDocument()
    expect(textoDeFila('dias_alerta_vencimiento')).toContain('Sucursal Norte')
    expect(textoDeFila('tolerancia_pago')).toContain('Local 2')
    expect(textoDeFila('importe_envio')).toContain('Toda la empresa')
    expect(textoDeFila('slots_tickets_espera')).toContain('Punto de venta 99')
  })

  it('un segundo envío en el mismo tick no duplica la escritura', async () => {
    mockearRutasBase()
    let liberar: (valor: unknown) => void = () => {}
    apiPutMock.mockReturnValue(new Promise((resolver) => (liberar = resolver)))
    const usuario = userEvent.setup()

    render(<Parametros />)
    await screen.findByText('Empresa Uno SA')

    await usuario.type(screen.getByLabelText('Valor'), '15')
    // Dos envíos directos al formulario (como un doble Enter): el botón ya deshabilitado no los frena, la guarda síncrona sí.
    const formulario = screen.getByRole('button', { name: 'Guardar' }).closest('form')!
    fireEvent.submit(formulario)
    fireEvent.submit(formulario)

    expect(apiPutMock).toHaveBeenCalledTimes(1)
    liberar({ id: 1, clave: 'tolerancia_pago', valor: '15', idPuntoVenta: null, idSucursal: null })
    await waitFor(() => expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled())
  })
})

describe('Parametros — el resultado de guardar se avisa con toasts', () => {
  it('un guardado exitoso avisa con un toast de éxito y no deja un alert en la pantalla', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()

    render(<><Parametros /><Toaster /></>)
    await screen.findByText('Empresa Uno SA')

    await usuario.type(screen.getByLabelText('Valor'), '15')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByRole('status')).toHaveTextContent('Se guardó "tolerancia_pago".')
    expect(document.body.querySelector('.alert-success')).toBeNull()
  })

  it('un guardado rechazado avisa con un toast de error y no con un banner', async () => {
    mockearRutasBase()
    apiPutMock.mockRejectedValue(new Error('No se pudo guardar el parámetro.'))
    const usuario = userEvent.setup()

    render(<><Parametros /><Toaster /></>)
    await screen.findByText('Empresa Uno SA')

    await usuario.type(screen.getByLabelText('Valor'), '15')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('No se pudo guardar el parámetro.')
    expect(document.body.querySelector('.alert-danger')).toBeNull()
    expect(screen.queryByRole('status')).not.toBeInTheDocument()
  })
})
