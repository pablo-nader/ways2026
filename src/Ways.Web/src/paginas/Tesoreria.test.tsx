import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Tesoreria } from './Tesoreria'
import { ErrorApi } from '../api/cliente'
import { ROL } from '../api/tipos'
import type { EmpresaListado, FilaDelLibro, LibroDeTesoreria, UsuarioAutenticado } from '../api/tipos'
import { fechaHoraATexto } from '../formato/fechas'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown])),
    put: vi.fn(),
    delete: vi.fn(),
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

function usuarioFixture(rolId: number): UsuarioAutenticado {
  return { id: 9, usuario: 'usuario', mail: 'u@ways.test', rolId, rol: 'Rol', ultimaConexion: null, idTenant: 1 }
}

let usuarioActual: UsuarioAutenticado | null = usuarioFixture(ROL.Admin)

vi.mock('../auth/useAuth', () => ({
  useAuth: () => ({ usuario: usuarioActual, cargando: false, iniciarSesion: vi.fn(), cerrarSesion: vi.fn() }),
}))

afterEach(cleanup)

function empresa(id: number, razonSocial: string): EmpresaListado {
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

const empresaUnica = empresa(1, 'Empresa Demo')

function diferido<T>() {
  let resolver!: (valor: T) => void
  let rechazar!: (motivo: unknown) => void
  const promesa = new Promise<T>((res, rej) => {
    resolver = res
    rechazar = rej
  })
  return { promesa, resolver, rechazar }
}

const FECHA_APERTURA = '2026-10-28T12:00:00Z'
const FECHA_CIERRE = '2026-10-28T20:00:00Z'

function filaDeCaja(sobrescribir: Partial<FilaDelLibro> = {}): FilaDelLibro {
  return {
    fecha: FECHA_CIERRE,
    tipo: 'Caja',
    efectivo: 3000,
    cuentas: [{ idCuentaTesoreria: 1, importe: 950 }],
    sinCuenta: null,
    caja: {
      idTurnoCaja: 137,
      numero: 137,
      fechaApertura: FECHA_APERTURA,
      fechaCierre: FECHA_CIERRE,
      idPuntoVenta: 7,
      puntoVenta: 'Local Centro',
      diferenciaArqueo: -1500,
      desglose: [
        {
          idCuentaTesoreria: 1,
          neto: 950,
          medios: [
            { idMedioPago: 4, medio: 'Tarjeta', importe: 1000, comision: 50, retenciones: 0, neto: 950 },
            { idMedioPago: 5, medio: 'Billetera', importe: 200, comision: 0, retenciones: 0, neto: 200 },
          ],
        },
      ],
    },
    pago: null,
    ajuste: null,
    ...sobrescribir,
  }
}

const filaDePago: FilaDelLibro = {
  fecha: '2026-10-29T10:00:00Z',
  tipo: 'Pago',
  efectivo: -400,
  cuentas: [],
  sinCuenta: null,
  caja: null,
  pago: { idPago: 7, detalle: 'Proveedor: Flete', categoria: 'Proveedor', origenFondos: 'Tesoreria', idTurnoCaja: null },
  ajuste: null,
}

const filaDeAjusteDeEfectivo: FilaDelLibro = {
  fecha: '2026-10-29T11:00:00Z',
  tipo: 'AjusteEfectivo',
  efectivo: 200,
  cuentas: [],
  sinCuenta: null,
  caja: null,
  pago: null,
  ajuste: { concepto: 'Conteo' },
}

const filaDeAjusteDeCuenta: FilaDelLibro = {
  fecha: '2026-10-29T12:00:00Z',
  tipo: 'AjusteCuenta',
  efectivo: null,
  cuentas: [{ idCuentaTesoreria: 2, importe: -300 }],
  sinCuenta: null,
  caja: null,
  pago: null,
  ajuste: { concepto: 'Comisión bancaria' },
}

function libroFixture(sobrescribir: Partial<LibroDeTesoreria> = {}): LibroDeTesoreria {
  return {
    idEmpresa: 1,
    desde: '2026-10-25T00:00:00-03:00',
    hasta: '2026-10-31T23:59:59.999-03:00',
    cuentas: [
      { idCuentaTesoreria: 1, nombre: 'Mercado Pago', activo: true },
      { idCuentaTesoreria: 2, nombre: 'Banco', activo: true },
    ],
    saldoAnterior: { efectivo: 10000, porCuenta: [{ idCuentaTesoreria: 1, importe: 500 }] },
    saldoActual: {
      efectivo: 8500,
      porCuenta: [
        { idCuentaTesoreria: 1, importe: 2000 },
        { idCuentaTesoreria: 2, importe: -300 },
      ],
    },
    filas: [filaDeCaja(), filaDePago, filaDeAjusteDeEfectivo, filaDeAjusteDeCuenta],
    conciliadoHasta: null,
    ...sobrescribir,
  }
}

function mockearRutas(
  opciones: {
    empresas?: EmpresaListado[]
    libro?: (ruta: string) => Promise<LibroDeTesoreria>
  } = {},
) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/empresas') return Promise.resolve(opciones.empresas ?? [empresaUnica])
    if (ruta.startsWith('/reportes/libro-tesoreria?')) return (opciones.libro ?? (() => Promise.resolve(libroFixture())))(ruta)
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

function renderTesoreria() {
  return render(
    <MemoryRouter>
      <Tesoreria />
    </MemoryRouter>,
  )
}

function llamadasAlLibro(): string[] {
  return apiGetMock.mock.calls.map((c) => c[0] as string).filter((r) => r.startsWith('/reportes/libro-tesoreria?'))
}

function celdasDeLaFila(fila: HTMLElement): HTMLElement[] {
  return within(fila).getAllByRole('cell')
}

/** Espera al DATO: la tarjeta de efectivo solo existe cuando el libro llegó. */
async function esperarElLibro() {
  await screen.findByRole('group', { name: 'Efectivo' })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  usuarioActual = usuarioFixture(ROL.Admin)
})

describe('Tesoreria — libro: carga y filtros', () => {
  it('con empresa única la preselecciona y pide el libro con el rango de siete días y el offset local', async () => {
    mockearRutas()
    renderTesoreria()

    await esperarElLibro()
    expect(screen.getByLabelText('Empresa')).toHaveValue('1')
    const ruta = decodeURIComponent(llamadasAlLibro()[0])
    expect(ruta).toMatch(
      /^\/reportes\/libro-tesoreria\?idEmpresa=1&desde=\d{4}-\d{2}-\d{2}T00:00:00[+-]\d{2}:\d{2}&hasta=\d{4}-\d{2}-\d{2}T23:59:59\.999[+-]\d{2}:\d{2}$/,
    )
  })

  it('con varias empresas no pide nada hasta elegir una', async () => {
    mockearRutas({ empresas: [empresaUnica, empresa(2, 'Otra Empresa')] })
    renderTesoreria()

    await screen.findByText('Elegí una empresa para ver su libro de tesorería.')
    expect(llamadasAlLibro()).toHaveLength(0)

    await userEvent.selectOptions(screen.getByLabelText('Empresa'), '2')
    await esperarElLibro()
    expect(llamadasAlLibro()[0]).toContain('idEmpresa=2')
  })

  it('un rango invertido avisa y no consulta', async () => {
    mockearRutas()
    renderTesoreria()
    await esperarElLibro()
    const antes = llamadasAlLibro().length

    const desde = screen.getByLabelText('Desde')
    await userEvent.clear(desde)
    await userEvent.type(desde, '31/12/2099')

    expect(await screen.findByText(/no puede ser posterior/)).toBeInTheDocument()
    expect(llamadasAlLibro()).toHaveLength(antes)
    expect(screen.queryByRole('group', { name: 'Efectivo' })).not.toBeInTheDocument()
  })

  it('si la lectura falla muestra el mensaje y Reintentar vuelve a pedir', async () => {
    let intento = 0
    mockearRutas({
      libro: () =>
        ++intento === 1
          ? Promise.reject(new ErrorApi(400, 'rango_invalido', 'El rango es demasiado grande.'))
          : Promise.resolve(libroFixture()),
    })
    renderTesoreria()

    expect(await screen.findByText('El rango es demasiado grande.')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Reintentar' }))

    await esperarElLibro()
    expect(screen.queryByText('El rango es demasiado grande.')).not.toBeInTheDocument()
  })

  it('una respuesta tardía de otra empresa no pisa la de la empresa elegida después', async () => {
    const primera = diferido<LibroDeTesoreria>()
    const segunda = diferido<LibroDeTesoreria>()
    mockearRutas({
      empresas: [empresaUnica, empresa(2, 'Otra Empresa')],
      libro: (ruta) => (ruta.includes('idEmpresa=1&') ? primera.promesa : segunda.promesa),
    })
    renderTesoreria()
    await screen.findByRole('option', { name: 'Otra Empresa' })

    await userEvent.selectOptions(screen.getByLabelText('Empresa'), '1')
    await waitFor(() => expect(llamadasAlLibro()).toHaveLength(1))
    await userEvent.selectOptions(screen.getByLabelText('Empresa'), '2')
    await waitFor(() => expect(llamadasAlLibro()).toHaveLength(2))

    await act(async () => {
      segunda.resolver(libroFixture({ idEmpresa: 2, saldoActual: { efectivo: 222, porCuenta: [] }, cuentas: [] }))
    })
    expect(within(await screen.findByRole('group', { name: 'Efectivo' })).getByText('$ 222,00')).toBeInTheDocument()

    await act(async () => {
      primera.resolver(libroFixture({ idEmpresa: 1, saldoActual: { efectivo: 111, porCuenta: [] }, cuentas: [] }))
    })

    expect(within(screen.getByRole('group', { name: 'Efectivo' })).getByText('$ 222,00')).toBeInTheDocument()
    expect(screen.queryByText('$ 111,00')).not.toBeInTheDocument()
  })

  it('cambiar de empresa limpia el libro anterior mientras llega el nuevo', async () => {
    const pendiente = diferido<LibroDeTesoreria>()
    mockearRutas({
      empresas: [empresaUnica, empresa(2, 'Otra Empresa')],
      libro: (ruta) => (ruta.includes('idEmpresa=2&') ? pendiente.promesa : Promise.resolve(libroFixture())),
    })
    renderTesoreria()
    await screen.findByRole('option', { name: 'Otra Empresa' })
    await userEvent.selectOptions(screen.getByLabelText('Empresa'), '1')
    await esperarElLibro()

    await userEvent.selectOptions(screen.getByLabelText('Empresa'), '2')

    expect(screen.queryByRole('group', { name: 'Efectivo' })).not.toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
  })
})

describe('Tesoreria — libro: liquidez y saldo anterior', () => {
  it('muestra una tarjeta por efectivo y por cuenta con su saldo actual, y pinta de rojo el negativo', async () => {
    mockearRutas()
    renderTesoreria()
    await esperarElLibro()

    const efectivo = screen.getByRole('group', { name: 'Efectivo' })
    expect(within(efectivo).getByText('$ 8.500,00')).not.toHaveClass('libro-importe--negativo')
    expect(within(efectivo).getByText('Disponible ahora')).toBeInTheDocument()
    expect(within(screen.getByRole('group', { name: 'Mercado Pago' })).getByText('$ 2.000,00')).toBeInTheDocument()
    expect(within(screen.getByRole('group', { name: 'Banco' })).getByText('-$ 300,00')).toHaveClass('libro-importe--negativo')
  })

  it('la primera fila de la tabla es el saldo anterior por columna, con cero para la cuenta que no figura', async () => {
    mockearRutas()
    renderTesoreria()
    await esperarElLibro()

    const encabezados = within(screen.getByRole('table'))
      .getAllByRole('columnheader')
      .map((h) => h.textContent)
    expect(encabezados).toEqual(['Fecha', 'Detalle', 'Efectivo', 'Mercado Pago', 'Banco'])

    const primera = screen.getByRole('table').querySelector('tbody tr') as HTMLElement
    const celdas = celdasDeLaFila(primera).map((c) => c.textContent)
    expect(celdas[1]).toBe('Saldo anterior')
    expect(celdas[2]).toMatch(/^\$\s10\.000,00$/)
    expect(celdas[3]).toMatch(/^\$\s500,00$/)
    expect(celdas[4]).toMatch(/^\$\s0,00$/)
  })

  it('la columna "Sin cuenta" solo aparece cuando alguna fila trae importe sin cuenta', async () => {
    mockearRutas()
    const { unmount } = renderTesoreria()
    await esperarElLibro()
    expect(screen.queryByRole('columnheader', { name: 'Sin cuenta' })).not.toBeInTheDocument()
    unmount()

    apiGetMock.mockReset()
    mockearRutas({ libro: () => Promise.resolve(libroFixture({ filas: [{ ...filaDePago, efectivo: null, sinCuenta: -80 }] })) })
    renderTesoreria()
    await esperarElLibro()

    expect(screen.getByRole('columnheader', { name: 'Sin cuenta' })).toBeInTheDocument()
    const fila = screen.getByText('Proveedor: Flete').closest('tr') as HTMLElement
    const celdas = celdasDeLaFila(fila)
    expect(celdas[celdas.length - 1]).toHaveTextContent(/-\$\s80,00/)
    expect(within(celdas[celdas.length - 1]).getByText(/-\$\s80,00/)).toHaveClass('libro-importe--negativo')
  })
})

describe('Tesoreria — libro: filas', () => {
  it('Caja: enlace al Z en pestaña nueva con la diferencia del arqueo, y celdas vacías donde la fila no toca', async () => {
    mockearRutas()
    renderTesoreria()
    await esperarElLibro()

    const enlace = screen.getByRole('link', { name: 'Caja #137 ($ -1.500)' })
    expect(enlace).toHaveAttribute('href', '/caja/turnos/137/z')
    expect(enlace).toHaveAttribute('target', '_blank')
    expect(enlace).toHaveAttribute('rel', 'noopener noreferrer')

    const celdas = celdasDeLaFila(enlace.closest('tr') as HTMLElement)
    expect(celdas[2]).toHaveTextContent(/^\$\s3\.000,00$/)
    expect(celdas[3]).toHaveTextContent(/^\$\s950,00$/)
    expect(celdas[4].textContent).toBe('')
  })

  it('Caja: la diferencia positiva lleva signo y sin diferencia se muestra solo el número', async () => {
    const sinDiferencia = filaDeCaja()
    sinDiferencia.caja = { ...sinDiferencia.caja!, numero: 150, idTurnoCaja: 150, diferenciaArqueo: null }
    const sobrante = filaDeCaja()
    sobrante.caja = { ...sobrante.caja!, numero: 151, idTurnoCaja: 151, diferenciaArqueo: 1300 }
    mockearRutas({ libro: () => Promise.resolve(libroFixture({ filas: [sinDiferencia, sobrante] })) })
    renderTesoreria()
    await esperarElLibro()

    expect(screen.getByRole('link', { name: 'Caja #150' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Caja #151 ($ +1.300)' })).toBeInTheDocument()
  })

  it('Caja: el tooltip muestra apertura, cierre y punto de venta con el mouse y con el foco, y se cierra con Escape', async () => {
    mockearRutas()
    renderTesoreria()
    await esperarElLibro()
    const usuario = userEvent.setup()
    const enlace = screen.getByRole('link', { name: /Caja #137/ })
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument()

    await usuario.hover(enlace)
    const globo = await screen.findByRole('tooltip')
    expect(globo).toHaveTextContent(`Apertura: ${fechaHoraATexto(FECHA_APERTURA)}`)
    expect(globo).toHaveTextContent(`Cierre: ${fechaHoraATexto(FECHA_CIERRE)}`)
    expect(globo).toHaveTextContent('Punto de venta: Local Centro')
    expect(enlace).toHaveAttribute('aria-describedby', globo.id)

    await usuario.keyboard('{Escape}')
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument()

    await usuario.unhover(enlace)
    act(() => enlace.focus())
    expect(await screen.findByRole('tooltip')).toHaveTextContent('Punto de venta: Local Centro')
    act(() => enlace.blur())
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument()
  })

  it('Caja: el importe de una cuenta muestra Medio | Importe | Comisión | Neto por medio, sin Retenciones si no hay', async () => {
    mockearRutas()
    renderTesoreria()
    await esperarElLibro()
    const usuario = userEvent.setup()
    const celda = celdasDeLaFila(screen.getByRole('link', { name: /Caja #137/ }).closest('tr') as HTMLElement)[3]

    await usuario.hover(within(celda).getByText(/950,00/))

    const globo = await screen.findByRole('tooltip')
    const tabla = within(globo).getByRole('table')
    expect(
      within(tabla)
        .getAllByRole('columnheader')
        .map((h) => h.textContent),
    ).toEqual(['Medio', 'Importe', 'Comisión', 'Neto'])
    const filas = within(tabla).getAllByRole('row').slice(1)
    expect(filas).toHaveLength(2)
    expect(filas[0].textContent).toMatch(/^Tarjeta\$\s1\.000,00\$\s50,00\$\s950,00$/)
    expect(filas[1].textContent).toMatch(/^Billetera\$\s200,00\$\s0,00\$\s200,00$/)
  })

  it('Caja: agrega la columna Retenciones solo cuando algún medio las tiene', async () => {
    const fila = filaDeCaja()
    fila.caja!.desglose[0].medios[0] = { idMedioPago: 4, medio: 'Tarjeta', importe: 1000, comision: 50, retenciones: 30, neto: 920 }
    mockearRutas({ libro: () => Promise.resolve(libroFixture({ filas: [fila] })) })
    renderTesoreria()
    await esperarElLibro()
    const celda = celdasDeLaFila(screen.getByRole('link', { name: /Caja #137/ }).closest('tr') as HTMLElement)[3]

    act(() =>
      (
        within(celda)
          .getByText(/950,00/)
          .closest('[tabindex]') as HTMLElement
      ).focus(),
    )

    const tabla = within(await screen.findByRole('tooltip')).getByRole('table')
    expect(
      within(tabla)
        .getAllByRole('columnheader')
        .map((h) => h.textContent),
    ).toEqual(['Medio', 'Importe', 'Comisión', 'Retenciones', 'Neto'])
    expect(within(tabla).getAllByRole('row')[1].textContent).toMatch(/^Tarjeta\$\s1\.000,00\$\s50,00\$\s30,00\$\s920,00$/)
  })

  it('Pago: el detalle es un enlace a /pagos?id=N en pestaña nueva solo para el Admin', async () => {
    mockearRutas()
    const { unmount } = renderTesoreria()
    await esperarElLibro()

    const enlace = screen.getByRole('link', { name: 'Proveedor: Flete' })
    expect(enlace).toHaveAttribute('href', '/pagos?id=7')
    expect(enlace).toHaveAttribute('target', '_blank')
    expect(enlace).toHaveAttribute('rel', 'noopener noreferrer')
    unmount()

    usuarioActual = usuarioFixture(ROL.Supervisor)
    renderTesoreria()
    await esperarElLibro()

    expect(screen.getByText('Proveedor: Flete')).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Proveedor: Flete' })).not.toBeInTheDocument()
  })

  it('Pago: el efectivo negativo lleva la clase de rojo y el positivo no', async () => {
    mockearRutas()
    renderTesoreria()
    await esperarElLibro()

    const filaPago = screen.getByText('Proveedor: Flete').closest('tr') as HTMLElement
    expect(within(celdasDeLaFila(filaPago)[2]).getByText(/-\$\s400,00/)).toHaveClass('libro-importe--negativo')

    const filaAjuste = screen.getByText('Ajuste: Conteo').closest('tr') as HTMLElement
    expect(within(celdasDeLaFila(filaAjuste)[2]).getByText(/200,00/)).not.toHaveClass('libro-importe--negativo')
  })

  it('los ajustes se rotulan "Ajuste: concepto" y el de cuenta cae en su columna', async () => {
    mockearRutas()
    renderTesoreria()
    await esperarElLibro()

    const fila = screen.getByText('Ajuste: Comisión bancaria').closest('tr') as HTMLElement
    const celdas = celdasDeLaFila(fila)
    expect(celdas[2].textContent).toBe('')
    expect(within(celdas[4]).getByText(/-\$\s300,00/)).toHaveClass('libro-importe--negativo')
  })

  it('sin movimientos en el rango deja el saldo anterior y lo avisa', async () => {
    mockearRutas({ libro: () => Promise.resolve(libroFixture({ filas: [] })) })
    renderTesoreria()
    await esperarElLibro()

    expect(screen.getByText('Saldo anterior')).toBeInTheDocument()
    expect(screen.getByText('No hay movimientos en el rango elegido.')).toBeInTheDocument()
  })
})

describe('Tesoreria — libro: ajustes (Admin)', () => {
  it('el Supervisor no ve ningún botón de ajuste', async () => {
    usuarioActual = usuarioFixture(ROL.Supervisor)
    mockearRutas()
    renderTesoreria()
    await esperarElLibro()

    expect(screen.queryByRole('button', { name: 'Ajustar efectivo' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /^Ajustar Mercado Pago/ })).not.toBeInTheDocument()
  })

  it('ajustar efectivo manda el POST, cierra el modal, confirma y recarga el libro', async () => {
    let lecturas = 0
    mockearRutas({
      libro: () =>
        Promise.resolve(++lecturas === 1 ? libroFixture() : libroFixture({ saldoActual: { efectivo: 9999, porCuenta: [] }, cuentas: [] })),
    })
    apiPostMock.mockResolvedValue({ idMovimiento: 3, fecha: '2026-10-31T10:00:00Z', concepto: 'Conteo', importe: 1499 })
    renderTesoreria()
    await esperarElLibro()
    const usuario = userEvent.setup()

    await usuario.click(screen.getByRole('button', { name: 'Ajustar efectivo' }))
    const dialogo = within(await screen.findByRole('dialog', { name: 'Ajustar efectivo' }))
    await usuario.type(dialogo.getByLabelText(/Importe/), '1499')
    await usuario.type(dialogo.getByLabelText('Concepto'), 'Conteo')
    await usuario.click(dialogo.getByRole('button', { name: 'Registrar ajuste' }))

    expect(await screen.findByText('Ajuste de efectivo registrado.')).toBeInTheDocument()
    expect(apiPostMock).toHaveBeenCalledWith('/tesoreria/efectivo/ajustes', { idEmpresa: 1, concepto: 'Conteo', importe: 1499 })
    expect(screen.queryByRole('dialog', { name: 'Ajustar efectivo' })).not.toBeInTheDocument()
    expect(await screen.findByText('$ 9.999,00')).toBeInTheDocument()
    expect(llamadasAlLibro()).toHaveLength(2)
  })

  it('un resultado incierto cierra el modal, recarga y pide verificar antes de reintentar', async () => {
    mockearRutas()
    apiPostMock.mockRejectedValue(new ErrorApi(503, 'resultado_incierto', 'x'))
    renderTesoreria()
    await esperarElLibro()
    const usuario = userEvent.setup()

    await usuario.click(screen.getByRole('button', { name: 'Ajustar efectivo' }))
    const dialogo = within(await screen.findByRole('dialog', { name: 'Ajustar efectivo' }))
    await usuario.type(dialogo.getByLabelText(/Importe/), '100')
    await usuario.type(dialogo.getByLabelText('Concepto'), 'Conteo')
    await usuario.click(dialogo.getByRole('button', { name: 'Registrar ajuste' }))

    expect(await screen.findByText(/No se pudo confirmar el ajuste de efectivo/)).toBeInTheDocument()
    expect(screen.queryByRole('dialog', { name: 'Ajustar efectivo' })).not.toBeInTheDocument()
    await waitFor(() => expect(llamadasAlLibro()).toHaveLength(2))
  })

  it('cada tarjeta de cuenta ofrece "Ajustar" y abre el modal de ajuste de esa cuenta', async () => {
    mockearRutas()
    renderTesoreria()
    await esperarElLibro()

    await userEvent.click(screen.getByRole('button', { name: 'Ajustar Mercado Pago' }))

    expect(await screen.findByRole('dialog', { name: 'Ajustar saldo de Mercado Pago' })).toBeInTheDocument()
  })

  it('ajustar una cuenta desde el libro manda un importe con signo, sin saldoObjetivo ni releer la cadena, y recarga el libro', async () => {
    let lecturas = 0
    mockearRutas({
      libro: () =>
        Promise.resolve(
          ++lecturas === 1
            ? libroFixture()
            : libroFixture({ saldoActual: { efectivo: 8500, porCuenta: [{ idCuentaTesoreria: 1, importe: 1940 }] } }),
        ),
    })
    apiPostMock.mockResolvedValue({ saldoActual: 1940, movimiento: {} })
    renderTesoreria()
    await esperarElLibro()
    const usuario = userEvent.setup()

    await usuario.click(screen.getByRole('button', { name: 'Ajustar Mercado Pago' }))
    const dialogo = within(await screen.findByRole('dialog', { name: 'Ajustar saldo de Mercado Pago' }))
    expect(dialogo.queryByRole('radio')).not.toBeInTheDocument()
    expect(dialogo.getByText(/Saldo actual de la cuenta/)).toHaveTextContent('$ 2.000,00')
    await usuario.type(dialogo.getByLabelText(/Importe/), '-60')
    await usuario.type(dialogo.getByLabelText('Concepto'), 'Retiro')
    await usuario.click(dialogo.getByRole('button', { name: 'Registrar ajuste' }))

    expect(await screen.findByText('Ajuste de Mercado Pago registrado.')).toBeInTheDocument()
    expect(apiPostMock).toHaveBeenCalledWith('/cuentas-tesoreria/1/ajustes', { concepto: 'Retiro', importe: -60 })
    expect(apiGetMock.mock.calls.every((c) => c[0] === '/empresas' || (c[0] as string).startsWith('/reportes/libro-tesoreria?'))).toBe(true)
    expect(await screen.findByText('$ 1.940,00')).toBeInTheDocument()
    expect(llamadasAlLibro()).toHaveLength(2)
  })

  it('mientras un modal está abierto no se puede abrir otro ajuste', async () => {
    mockearRutas()
    renderTesoreria()
    await esperarElLibro()

    await userEvent.click(screen.getByRole('button', { name: 'Ajustar efectivo' }))
    await screen.findByRole('dialog', { name: 'Ajustar efectivo' })

    expect(screen.getByRole('button', { name: 'Ajustar efectivo', hidden: true })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Ajustar Banco', hidden: true })).toBeDisabled()
  })
})

describe('Tesoreria — libro: conciliación', () => {
  const MARCA = '2026-10-29T10:00:00Z'

  function filasDeLaTabla(): HTMLElement[] {
    return Array.from(screen.getByRole('table').querySelectorAll<HTMLElement>('tbody tr'))
  }

  async function abrirConciliar(usuario: ReturnType<typeof userEvent.setup>) {
    await usuario.click(screen.getByRole('button', { name: 'Conciliar' }))
    return within(await screen.findByRole('dialog', { name: 'Conciliar tesorería' }))
  }

  it('sin marca muestra "Sin conciliar" y ninguna fila lleva candado', async () => {
    mockearRutas()
    renderTesoreria()
    await esperarElLibro()

    expect(screen.getByRole('status')).toHaveTextContent('Sin conciliar')
    expect(screen.queryByRole('img', { name: 'Conciliado' })).not.toBeInTheDocument()
  })

  it('con marca muestra "Conciliado hasta" con la fecha y la hora locales', async () => {
    mockearRutas({ libro: () => Promise.resolve(libroFixture({ conciliadoHasta: MARCA })) })
    renderTesoreria()
    await esperarElLibro()

    expect(screen.getByRole('status')).toHaveTextContent(`Conciliado hasta ${fechaHoraATexto(MARCA)}`)
  })

  it('congela con candado las filas hasta la marca inclusive y el saldo anterior si el rango empieza antes', async () => {
    mockearRutas({ libro: () => Promise.resolve(libroFixture({ conciliadoHasta: MARCA })) })
    renderTesoreria()
    await esperarElLibro()

    const [saldo, caja, pago, ajusteEfectivo, ajusteCuenta] = filasDeLaTabla()
    for (const congelada of [saldo, caja, pago]) {
      expect(within(congelada).getByRole('img', { name: 'Conciliado' })).toBeInTheDocument()
      expect(congelada).toHaveClass('libro-fila--conciliada')
    }
    for (const abierta of [ajusteEfectivo, ajusteCuenta]) {
      expect(within(abierta).queryByRole('img', { name: 'Conciliado' })).not.toBeInTheDocument()
      expect(abierta).not.toHaveClass('libro-fila--conciliada')
    }
  })

  it('una marca un segundo antes de la fila la deja abierta; una marca anterior al rango no congela nada', async () => {
    mockearRutas({ libro: () => Promise.resolve(libroFixture({ conciliadoHasta: '2026-10-29T09:59:59Z' })) })
    const { unmount } = renderTesoreria()
    await esperarElLibro()
    const [, , pago] = filasDeLaTabla()
    expect(within(pago).queryByRole('img', { name: 'Conciliado' })).not.toBeInTheDocument()
    unmount()

    mockearRutas({ libro: () => Promise.resolve(libroFixture({ conciliadoHasta: '2026-10-24T00:00:00Z' })) })
    renderTesoreria()
    await esperarElLibro()
    expect(screen.queryByRole('img', { name: 'Conciliado' })).not.toBeInTheDocument()
  })

  it('el botón Conciliar es solo del Admin', async () => {
    usuarioActual = usuarioFixture(ROL.Supervisor)
    mockearRutas()
    renderTesoreria()
    await esperarElLibro()

    expect(screen.queryByRole('button', { name: 'Conciliar' })).not.toBeInTheDocument()
  })

  it('conciliar manda el POST, cierra el modal, confirma y recarga el libro con la marca nueva', async () => {
    let lecturas = 0
    mockearRutas({ libro: () => Promise.resolve(libroFixture({ conciliadoHasta: ++lecturas === 1 ? null : MARCA })) })
    apiPostMock.mockResolvedValue({ idConciliacion: 1, idEmpresa: 1, hasta: MARCA, nota: null, creadoEl: MARCA, idUsuario: 9 })
    renderTesoreria()
    await esperarElLibro()
    const usuario = userEvent.setup()

    const dialogo = await abrirConciliar(usuario)
    await usuario.click(dialogo.getByLabelText('Entiendo que no se puede deshacer'))
    await usuario.click(dialogo.getByRole('button', { name: 'Conciliar' }))

    expect(await screen.findByText('Conciliación registrada.')).toBeInTheDocument()
    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(apiPostMock.mock.calls[0][0]).toBe('/tesoreria/conciliaciones')
    expect(apiPostMock.mock.calls[0][1]).toMatchObject({ idEmpresa: 1 })
    expect(screen.queryByRole('dialog', { name: 'Conciliar tesorería' })).not.toBeInTheDocument()
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Conciliado hasta'))
    expect(llamadasAlLibro()).toHaveLength(2)
  })

  it('el rechazo por una fecha anterior nombra la marca que mostraba el libro y no recarga', async () => {
    mockearRutas({ libro: () => Promise.resolve(libroFixture({ conciliadoHasta: MARCA })) })
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'conciliacion_anterior', 'x'))
    renderTesoreria()
    await esperarElLibro()
    const usuario = userEvent.setup()

    const dialogo = await abrirConciliar(usuario)
    await usuario.click(dialogo.getByLabelText('Entiendo que no se puede deshacer'))
    await usuario.click(dialogo.getByRole('button', { name: 'Conciliar' }))

    expect(await dialogo.findByText(`Ya está conciliado hasta ${fechaHoraATexto(MARCA)}; elegí un instante posterior.`)).toBeInTheDocument()
    expect(llamadasAlLibro()).toHaveLength(1)
  })

  it('un resultado incierto cierra el modal, recarga y pide verificar antes de reintentar', async () => {
    mockearRutas()
    apiPostMock.mockRejectedValue(new ErrorApi(503, 'resultado_incierto', 'x'))
    renderTesoreria()
    await esperarElLibro()
    const usuario = userEvent.setup()

    const dialogo = await abrirConciliar(usuario)
    await usuario.click(dialogo.getByLabelText('Entiendo que no se puede deshacer'))
    await usuario.click(dialogo.getByRole('button', { name: 'Conciliar' }))

    expect(await screen.findByText(/No se pudo confirmar la conciliación/)).toBeInTheDocument()
    expect(screen.queryByRole('dialog', { name: 'Conciliar tesorería' })).not.toBeInTheDocument()
    await waitFor(() => expect(llamadasAlLibro()).toHaveLength(2))
  })

  it('un doble clic en Conciliar manda un solo POST', async () => {
    const pendiente = diferido<unknown>()
    mockearRutas()
    apiPostMock.mockReturnValue(pendiente.promesa)
    renderTesoreria()
    await esperarElLibro()
    const usuario = userEvent.setup()

    const dialogo = await abrirConciliar(usuario)
    await usuario.click(dialogo.getByLabelText('Entiendo que no se puede deshacer'))
    await usuario.dblClick(dialogo.getByRole('button', { name: 'Conciliar' }))

    expect(apiPostMock).toHaveBeenCalledTimes(1)
    await act(async () => {
      pendiente.resolver({})
    })
    await screen.findByText('Conciliación registrada.')
  })

  it('mientras el modal de conciliación está abierto no se ofrece otro ajuste', async () => {
    mockearRutas()
    renderTesoreria()
    await esperarElLibro()

    await userEvent.click(screen.getByRole('button', { name: 'Conciliar' }))
    await screen.findByRole('dialog', { name: 'Conciliar tesorería' })

    expect(screen.getByRole('button', { name: 'Ajustar efectivo', hidden: true })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Ajustar Banco', hidden: true })).toBeDisabled()
  })

  it('un ajuste de efectivo sobre un período conciliado muestra el mensaje del servidor y deja el modal abierto', async () => {
    mockearRutas()
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'periodo_conciliado', 'El período ya está conciliado.'))
    renderTesoreria()
    await esperarElLibro()
    const usuario = userEvent.setup()

    await usuario.click(screen.getByRole('button', { name: 'Ajustar efectivo' }))
    const dialogo = within(await screen.findByRole('dialog', { name: 'Ajustar efectivo' }))
    await usuario.type(dialogo.getByLabelText(/Importe/), '100')
    await usuario.type(dialogo.getByLabelText('Concepto'), 'Conteo')
    await usuario.click(dialogo.getByRole('button', { name: 'Registrar ajuste' }))

    expect(await dialogo.findByText('El período ya está conciliado.')).toBeInTheDocument()
  })

  it('un ajuste de cuenta sobre un período conciliado muestra el mensaje del servidor y deja el modal abierto', async () => {
    mockearRutas()
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'periodo_conciliado', 'El período ya está conciliado.'))
    renderTesoreria()
    await esperarElLibro()
    const usuario = userEvent.setup()

    await usuario.click(screen.getByRole('button', { name: 'Ajustar Banco' }))
    const dialogo = within(await screen.findByRole('dialog', { name: 'Ajustar saldo de Banco' }))
    await usuario.type(dialogo.getByLabelText(/Importe/), '100')
    await usuario.type(dialogo.getByLabelText('Concepto'), 'Conteo')
    await usuario.click(dialogo.getByRole('button', { name: 'Registrar ajuste' }))

    expect(await dialogo.findByText('El período ya está conciliado.')).toBeInTheDocument()
  })
})
