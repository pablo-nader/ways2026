import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ConciliacionMercadoPago } from './ConciliacionMercadoPago'
import { Toaster } from '../toasts/Toaster'
import { ROL } from '../api/tipos'
import type { EmpresaListado, PuntoVentaListado, UsuarioAutenticado } from '../api/tipos'
import type { CuentaTesoreriaListado } from '../api/cuentasTesoreria'
import type {
  CandidatoDeConfirmacion,
  CobroSinVenta,
  ConciliacionImportada,
  ConciliacionListado,
  DetalleDeConciliacion,
  FilaDeConciliacion,
  PagosSinVerificar,
  PropuestaDeTesoreria,
} from '../api/conciliacionMercadoPago'
import { TAMANO_MAXIMO_DE_ARCHIVO } from '../api/conciliacionMercadoPago'
import { fechaHoraATexto } from '../formato/fechas'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()
const apiPostFormularioMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...args),
    post: (...args: unknown[]) => apiPostMock(...args),
    postFormulario: (...args: unknown[]) => apiPostFormularioMock(...args),
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
  ErrorDeRed: class ErrorDeRedMock extends Error {},
}))

const { ErrorApi } = await import('../api/cliente')

function usuarioFixture(rolId: number): UsuarioAutenticado {
  return { id: 9, usuario: 'usuario', mail: 'u@ways.test', rolId, rol: 'Rol', ultimaConexion: null, idTenant: 1 }
}

let usuarioActual: UsuarioAutenticado | null = usuarioFixture(ROL.Admin)

vi.mock('../auth/useAuth', () => ({
  useAuth: () => ({ usuario: usuarioActual, cargando: false, iniciarSesion: vi.fn(), cerrarSesion: vi.fn() }),
}))

afterEach(cleanup)

function diferido<T>() {
  let resolver!: (valor: T) => void
  let rechazar!: (motivo: unknown) => void
  const promesa = new Promise<T>((res, rej) => {
    resolver = res
    rechazar = rej
  })
  return { promesa, resolver, rechazar }
}

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

function puntosVentaDe(lista: EmpresaListado[]): PuntoVentaListado[] {
  return lista.map((e) => ({
    id: e.id * 10,
    idTenant: e.idTenant,
    idEmpresa: e.id,
    nombre: `PV de ${e.razonSocial}`,
    domicilio: null,
    horario: null,
    whatsapp: null,
    instagram: null,
    facebook: null,
    web: null,
    nombreTenant: e.nombreTenant,
    razonSocialEmpresa: e.razonSocial,
    modo: 'Web',
  }))
}

function conciliacion(sobrescribir: Partial<ConciliacionListado> = {}): ConciliacionListado {
  return {
    id: 3,
    idEmpresa: 1,
    nombreArchivo: 'reporte-octubre.csv',
    fechaDesde: '2026-10-01T03:00:00Z',
    fechaHasta: '2026-10-31T03:00:00Z',
    idUsuario: 9,
    usuario: 'admin',
    subidaEl: '2026-11-01T12:00:00Z',
    cantidadFilas: 12,
    cantidadInvalidas: 1,
    resumen: [
      { resultado: 'conciliado', cantidad: 8, importe: 8000, comision: -400, neto: 7600 },
      { resultado: 'sin_cobro_en_ways', cantidad: 3, importe: 1500, comision: -75, neto: 1425 },
      { resultado: 'invalida', cantidad: 1, importe: 0, comision: 0, neto: 0 },
    ],
    ...sobrescribir,
  }
}

function candidato(sobrescribir: Partial<CandidatoDeConfirmacion> = {}): CandidatoDeConfirmacion {
  return {
    idCobro: 90,
    idComprobanteVenta: 4,
    numeroVisible: '0001-00000042',
    fecha: '2026-10-30T14:00:00Z',
    importe: 1500,
    referencia: null,
    ...sobrescribir,
  }
}

function fila(sobrescribir: Partial<FilaDeConciliacion> = {}): FilaDeConciliacion {
  return {
    id: 5,
    numeroLinea: 12,
    resultado: 'sin_cobro_en_ways',
    sourceId: 'SRC-777',
    externalReference: 'EXT-1',
    tipoTransaccion: 'SETTLEMENT',
    importe: 1500,
    comision: -75,
    neto: 1425,
    fechaTransaccion: '2026-10-30T15:00:00Z',
    fechaLiberacion: null,
    externalPosId: null,
    motivo: null,
    idCobroMercadoPago: null,
    idCobro: null,
    candidatos: [candidato()],
    aplicadaAlCobro: false,
    comisionGuardada: null,
    netoGuardado: null,
    ...sobrescribir,
  }
}

function detalle(filas: FilaDeConciliacion[], extra: Partial<DetalleDeConciliacion> = {}): DetalleDeConciliacion {
  return { conciliacion: conciliacion(), filas, total: filas.length, pagina: 1, tamano: 100, ...extra }
}

function importada(sobrescribir: Partial<ConciliacionImportada> = {}): ConciliacionImportada {
  return { conciliacion: conciliacion(), invalidas: [], invalidasTruncado: false, retirosYaPropuestos: 0, retirosCancelados: 0, ...sobrescribir }
}

let empresas: EmpresaListado[]
let lista: ConciliacionListado[]
let detalles: (ruta: string) => Promise<DetalleDeConciliacion>
let cobrosSinVenta: CobroSinVenta[]
let pagosSinVerificar: PagosSinVerificar
let propuestas: PropuestaDeTesoreria[]
let cuentas: CuentaTesoreriaListado[]

function mockearRutas() {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/puntos-venta') return Promise.resolve(puntosVentaDe(empresas))
    if (ruta.startsWith('/mercadopago/conciliaciones?')) return Promise.resolve(lista)
    if (ruta.endsWith('/propuestas')) return Promise.resolve(propuestas)
    if (ruta === '/cuentas-tesoreria') return Promise.resolve(cuentas)
    if (ruta.startsWith('/mercadopago/conciliaciones/')) return detalles(ruta)
    if (ruta.startsWith('/mercadopago/cobros-sin-venta')) return Promise.resolve(cobrosSinVenta)
    if (ruta.startsWith('/mercadopago/pagos-sin-verificar')) return Promise.resolve(pagosSinVerificar)
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

function renderPagina() {
  return render(
    <>
      <ConciliacionMercadoPago />
      <Toaster />
    </>,
    { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> },
  )
}

const llamadasA = (prefijo: string) => apiGetMock.mock.calls.filter((c) => String(c[0]).startsWith(prefijo))

beforeEach(() => {
  usuarioActual = usuarioFixture(ROL.Admin)
  empresas = [empresa(1, 'Empresa Demo')]
  lista = [conciliacion()]
  detalles = () => Promise.resolve(detalle([fila()]))
  cobrosSinVenta = []
  pagosSinVerificar = { items: [], truncado: false }
  propuestas = []
  cuentas = []
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPostFormularioMock.mockReset()
  mockearRutas()
})

function csv(nombre = 'reporte.csv', tamano = 10): File {
  const archivo = new File(['x'], nombre, { type: 'text/csv' })
  Object.defineProperty(archivo, 'size', { value: tamano })
  return archivo
}

async function elegirArchivo(archivo: File) {
  const entrada = await screen.findByLabelText('Archivo del reporte')
  await userEvent.setup().upload(entrada, archivo)
}

async function irAPestana(nombre: string) {
  await userEvent.setup().click(await screen.findByRole('tab', { name: nombre }))
}

describe('ConciliacionMercadoPago — importar', () => {
  it('sube el archivo con el encabezado de origen web, muestra el resumen con totales y avisa', async () => {
    apiPostFormularioMock.mockResolvedValue(importada())
    renderPagina()

    await elegirArchivo(csv())
    await userEvent.setup().click(screen.getByRole('button', { name: 'Importar' }))

    const region = await screen.findByRole('region', { name: 'Resultado de la importación' })
    expect(apiPostFormularioMock).toHaveBeenCalledTimes(1)
    const [ruta, formulario, encabezados] = apiPostFormularioMock.mock.calls[0] as [string, FormData, Record<string, string>]
    expect(ruta).toBe('/mercadopago/conciliaciones?idEmpresa=1')
    expect((formulario.get('archivo') as File).name).toBe('reporte.csv')
    expect(encabezados).toEqual({ 'X-Ways-Origen': 'web' })
    expect(within(region).getByText('Conciliado')).toBeInTheDocument()
    expect(within(region).getByText('Sin cobro en Ways')).toBeInTheDocument()
    // Totales: 12 filas, $ 9.500 de importe, comisión -475 y neto 9.025.
    const total = within(region).getByText('Total').closest('tr') as HTMLElement
    expect(within(total).getByText('12')).toBeInTheDocument()
    expect(within(total).getByText(/9\.500/)).toBeInTheDocument()
    expect(within(total).getByText(/9\.025/)).toBeInTheDocument()
    expect(await screen.findByText('Reporte importado.')).toBeInTheDocument()
    await waitFor(() => expect(llamadasA('/mercadopago/conciliaciones?').length).toBeGreaterThanOrEqual(2))
  })

  it('avisa los retiros que un reporte anterior ya proponía y no se volvieron a proponer', async () => {
    apiPostFormularioMock.mockResolvedValue(importada({ retirosYaPropuestos: 2 }))
    renderPagina()

    await elegirArchivo(csv())
    await userEvent.setup().click(screen.getByRole('button', { name: 'Importar' }))

    const region = await screen.findByRole('region', { name: 'Resultado de la importación' })
    expect(within(region).getByText(/2 retiro\(s\) ya estaban propuestos por un reporte anterior/)).toBeInTheDocument()
  })

  it('avisa los retiros que el reporte cancela', async () => {
    apiPostFormularioMock.mockResolvedValue(importada({ retirosCancelados: 3 }))
    renderPagina()

    await elegirArchivo(csv())
    await userEvent.setup().click(screen.getByRole('button', { name: 'Importar' }))

    const region = await screen.findByRole('region', { name: 'Resultado de la importación' })
    expect(within(region).getByText(/3 retiro\(s\) cancelado\(s\) por Mercado Pago/)).toBeInTheDocument()
  })

  it('lista las filas inválidas con su línea y su motivo', async () => {
    apiPostFormularioMock.mockResolvedValue(
      importada({ invalidas: [{ numeroLinea: 7, motivo: 'Importe ilegible' }], invalidasTruncado: true }),
    )
    renderPagina()

    await elegirArchivo(csv())
    await userEvent.setup().click(screen.getByRole('button', { name: 'Importar' }))

    expect(await screen.findByText(/Línea 7: Importe ilegible/)).toBeInTheDocument()
    expect(screen.getByText(/Hay más filas inválidas/)).toBeInTheDocument()
  })

  it('un archivo repetido (409) muestra la copia propia y no deja resumen', async () => {
    apiPostFormularioMock.mockRejectedValue(new ErrorApi(409, 'conciliacion_ya_importada', 'x'))
    renderPagina()

    await elegirArchivo(csv())
    await userEvent.setup().click(screen.getByRole('button', { name: 'Importar' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Este archivo ya se importó para esta empresa')
    expect(screen.queryByRole('region', { name: 'Resultado de la importación' })).toBeNull()
    expect(screen.getByRole('button', { name: 'Importar' })).toBeEnabled()
  })

  it('un 403 de solicitud no permitida y un 400 de reporte inválido muestran su copia', async () => {
    apiPostFormularioMock.mockRejectedValueOnce(new ErrorApi(403, 'solicitud_no_permitida', 'x'))
    renderPagina()
    await elegirArchivo(csv())
    await userEvent.setup().click(screen.getByRole('button', { name: 'Importar' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('solo se acepta desde la web de Ways')

    apiPostFormularioMock.mockRejectedValueOnce(new ErrorApi(400, 'conciliacion_reporte_invalido', 'x'))
    await userEvent.setup().click(screen.getByRole('button', { name: 'Importar' }))
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('reporte de transacciones de Mercado Pago válido'))
  })

  it('un archivo de más de 10 MB se rechaza antes de subirlo', async () => {
    renderPagina()

    await elegirArchivo(csv('grande.csv', TAMANO_MAXIMO_DE_ARCHIVO + 1))
    await userEvent.setup().click(screen.getByRole('button', { name: 'Importar' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('supera los 10 MB')
    expect(apiPostFormularioMock).not.toHaveBeenCalled()
  })

  it('dos clics seguidos suben una sola vez y el botón queda inerte mientras sube', async () => {
    const subida = diferido<ConciliacionImportada>()
    apiPostFormularioMock.mockReturnValue(subida.promesa)
    renderPagina()

    await elegirArchivo(csv())
    const boton = screen.getByRole('button', { name: 'Importar' })
    act(() => {
      fireEvent.click(boton)
      fireEvent.click(boton)
    })

    expect(apiPostFormularioMock).toHaveBeenCalledTimes(1)
    expect(await screen.findByRole('button', { name: 'Importando…' })).toBeDisabled()
    expect(screen.getByLabelText('Empresa')).toBeDisabled()
    expect(screen.getByRole('tab', { name: 'Conciliaciones' })).toBeDisabled()

    await act(async () => subida.resolver(importada()))
    await screen.findByRole('region', { name: 'Resultado de la importación' })
  })
})

describe('ConciliacionMercadoPago — conciliaciones', () => {
  it('lista los reportes con su período y su conteo por resultado', async () => {
    renderPagina()

    await irAPestana('Conciliaciones')

    const filaDeLista = (await screen.findByText('reporte-octubre.csv')).closest('tr') as HTMLElement
    expect(within(filaDeLista).getByText('Conciliado 8 · Sin cobro en Ways 3 · Fila inválida 1')).toBeInTheDocument()
    expect(within(filaDeLista).getByText('admin')).toBeInTheDocument()
  })

  it('abre el detalle, filtra por resultado y pagina', async () => {
    detalles = (ruta) => Promise.resolve(detalle([fila()], { total: 250, pagina: ruta.includes('pagina=2') ? 2 : 1 }))
    renderPagina()
    const usuario = userEvent.setup()

    await irAPestana('Conciliaciones')
    await usuario.click(await screen.findByRole('button', { name: /Ver reporte-octubre/ }))
    expect(await screen.findByText(/Filas 1-100 de 250 · Página 1 de 3/)).toBeInTheDocument()
    expect(screen.getByText('SRC-777')).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    expect(await screen.findByText(/Filas 101-200 de 250 · Página 2 de 3/)).toBeInTheDocument()
    expect(llamadasA('/mercadopago/conciliaciones/3').map((c) => c[0])).toContain('/mercadopago/conciliaciones/3?pagina=2&tamano=100')

    await usuario.selectOptions(screen.getByLabelText('Resultado'), 'retiro')
    await waitFor(() =>
      expect(llamadasA('/mercadopago/conciliaciones/3').map((c) => c[0])).toContain('/mercadopago/conciliaciones/3?resultado=retiro&pagina=1&tamano=100'),
    )
  })

  it('muestra lo guardado en una fila ya conciliada y el vínculo a su cobro', async () => {
    detalles = () =>
      Promise.resolve(
        detalle([
          fila({
            id: 8,
            resultado: 'ya_conciliado',
            candidatos: [],
            idCobroMercadoPago: 21,
            comisionGuardada: 70,
            netoGuardado: 1430,
          }),
        ]),
      )
    renderPagina()

    await irAPestana('Conciliaciones')
    await userEvent.setup().click(await screen.findByRole('button', { name: /Ver reporte-octubre/ }))

    expect(await within(await screen.findByRole('table')).findByText('Ya conciliado antes')).toBeInTheDocument()
    expect(screen.getByText(/Cobro MP #21/)).toBeInTheDocument()
    expect(screen.getByText(/Guardado: comisión/)).toBeInTheDocument()
  })

  it('una empresa dada de baja (su punto de venta llega sin razón social) no se ofrece', async () => {
    empresas = [empresa(1, 'Empresa Demo'), empresa(2, 'Baja SA')]
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/puntos-venta')
        return Promise.resolve(puntosVentaDe(empresas).map((pv) => (pv.idEmpresa === 2 ? { ...pv, razonSocialEmpresa: null } : pv)))
      if (ruta.startsWith('/mercadopago/conciliaciones')) return Promise.resolve([])
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    renderPagina()

    const selector = await screen.findByLabelText('Empresa')
    expect(within(selector).queryByRole('option', { name: /Empresa 2|Baja SA/ })).toBeNull()
    expect(within(selector).getByRole('option', { name: 'Empresa Demo' })).toBeInTheDocument()
  })

  it('una respuesta tardía de otra empresa no se pinta (generación)', async () => {
    empresas = [empresa(1, 'Empresa Demo'), empresa(2, 'Otra SA')]
    const tardia = diferido<ConciliacionListado[]>()
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/puntos-venta') return Promise.resolve(puntosVentaDe(empresas))
      if (ruta === '/mercadopago/conciliaciones?idEmpresa=1') return tardia.promesa
      if (ruta === '/mercadopago/conciliaciones?idEmpresa=2') return Promise.resolve([conciliacion({ id: 4, nombreArchivo: 'de-otra-sa.csv' })])
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    renderPagina()
    const usuario = userEvent.setup()

    await usuario.selectOptions(await screen.findByLabelText('Empresa'), '1')
    await usuario.click(await screen.findByRole('tab', { name: 'Conciliaciones' }))
    await usuario.selectOptions(screen.getByLabelText('Empresa'), '2')
    await screen.findByText('de-otra-sa.csv')

    await act(async () => tardia.resolver([conciliacion({ id: 3, nombreArchivo: 'de-empresa-demo.csv' })]))

    expect(screen.queryByText('de-empresa-demo.csv')).toBeNull()
    expect(screen.getByText('de-otra-sa.csv')).toBeInTheDocument()
  })
})

describe('ConciliacionMercadoPago — para revisar', () => {
  it('lista cobros sin venta, pagos sin verificar con su conflicto y el aviso de truncado', async () => {
    cobrosSinVenta = [
      {
        idCobro: 31,
        idPuntoVenta: 2,
        idMedioPago: 5,
        numeroPreasignado: 1234,
        importe: 2000,
        importePagado: null,
        idPagoMercadoPago: 'MP-99',
        idOrdenMercadoPago: null,
        fechaAprobacion: null,
        fechaApartado: '2026-10-29T10:00:00Z',
        pagoInformadoSinDatos: true,
      },
    ]
    pagosSinVerificar = {
      truncado: true,
      items: [
        {
          idPago: 70,
          idComprobanteVenta: 4,
          numeroVisible: '0001-00000050',
          idPuntoVenta: 2,
          fecha: '2026-10-28T10:00:00Z',
          idMedioPago: 5,
          importe: 800,
          referencia: null,
          idEmpleado: 3,
          empleado: 'Marta',
          anulada: true,
          idsCobroEnConflicto: [31, 32],
        },
      ],
    }
    renderPagina()

    await irAPestana('Para revisar')

    expect(await screen.findByText('Pago informado sin datos')).toBeInTheDocument()
    expect(screen.getByText('MP-99')).toBeInTheDocument()
    expect(await screen.findByText('0001-00000050')).toBeInTheDocument()
    expect(screen.getByText('Anulada')).toBeInTheDocument()
    expect(screen.getByText(/Chocó con los cobros de Mercado Pago #31, #32/)).toBeInTheDocument()
    expect(screen.getByText(/Hay más pagos que los listados/)).toBeInTheDocument()
    expect(llamadasA('/mercadopago/pagos-sin-verificar')[0][0]).toMatch(/^\/mercadopago\/pagos-sin-verificar\?idEmpresa=1&desde=\d{4}-\d{2}-\d{2}&hasta=\d{4}-\d{2}-\d{2}$/)
  })

  async function abrirDialogo() {
    const usuario = userEvent.setup()
    await irAPestana('Para revisar')
    await usuario.click(await screen.findByRole('button', { name: /Confirmar la fila 12 con la venta 0001-00000042/ }))
    return { usuario, dialogo: await screen.findByRole('alertdialog') }
  }

  it('el diálogo de confirmación muestra importes y fechas de la fila y del pago', async () => {
    renderPagina()

    const { dialogo } = await abrirDialogo()

    expect(within(dialogo).getByText('Línea 12 · SRC-777')).toBeInTheDocument()
    expect(within(dialogo).getAllByText(/1\.500/)).toHaveLength(2)
    expect(within(dialogo).getByText(fechaHoraATexto('2026-10-30T15:00:00Z'))).toBeInTheDocument()
    expect(within(dialogo).getByText(fechaHoraATexto('2026-10-30T14:00:00Z'))).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('confirma contra el pago elegido, cierra el diálogo, avisa y vuelve a leer', async () => {
    apiPostMock.mockResolvedValue(undefined)
    renderPagina()
    const { usuario, dialogo } = await abrirDialogo()
    const lecturasAntes = llamadasA('/mercadopago/conciliaciones/3').length

    await usuario.click(within(dialogo).getByRole('button', { name: 'Confirmar' }))

    await waitFor(() => expect(screen.queryByRole('alertdialog')).toBeNull())
    expect(apiPostMock).toHaveBeenCalledWith('/mercadopago/conciliaciones/3/filas/5/confirmar', { idCobro: 90 })
    expect(await screen.findByText(/Fila confirmada/)).toBeInTheDocument()
    await waitFor(() => expect(llamadasA('/mercadopago/conciliaciones/3').length).toBeGreaterThan(lecturasAntes))
  })

  it.each([
    ['venta_anulada', 'La venta de ese pago está anulada'],
    ['importe_distinto', 'El importe del pago no coincide'],
    ['conciliacion_fila_ya_confirmada', 'ya fue confirmada'],
  ])('un 409 %s deja el diálogo abierto con su copia', async (codigo, copia) => {
    apiPostMock.mockRejectedValue(new ErrorApi(409, codigo, 'x'))
    renderPagina()
    const { usuario, dialogo } = await abrirDialogo()

    await usuario.click(within(dialogo).getByRole('button', { name: 'Confirmar' }))

    expect(await within(dialogo).findByRole('alert')).toHaveTextContent(copia)
    expect(screen.getByRole('alertdialog')).toBeInTheDocument()
    expect(within(dialogo).getByRole('button', { name: 'Confirmar' })).toBeEnabled()
  })

  it('dos clics seguidos en Confirmar escriben una sola vez y el diálogo queda inerte', async () => {
    const escritura = diferido<undefined>()
    apiPostMock.mockReturnValue(escritura.promesa)
    renderPagina()
    const { dialogo } = await abrirDialogo()

    const boton = within(dialogo).getByRole('button', { name: 'Confirmar' })
    act(() => {
      fireEvent.click(boton)
      fireEvent.click(boton)
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(await within(dialogo).findByRole('button', { name: 'Confirmando…' })).toBeDisabled()
    expect(within(dialogo).getByRole('button', { name: 'Cancelar' })).toBeDisabled()

    await act(async () => escritura.resolver(undefined))
    await waitFor(() => expect(screen.queryByRole('alertdialog')).toBeNull())
  })
})

describe('ConciliacionMercadoPago — Supervisor', () => {
  beforeEach(() => {
    usuarioActual = usuarioFixture(ROL.Supervisor)
  })

  it('no ve la solapa Importar ni el archivo; arranca en Conciliaciones', async () => {
    renderPagina()

    expect(await screen.findByRole('tab', { name: 'Conciliaciones' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.queryByRole('tab', { name: 'Importar' })).toBeNull()
    expect(screen.queryByLabelText('Archivo del reporte')).toBeNull()
    expect(screen.queryByRole('button', { name: 'Importar' })).toBeNull()
  })

  it('arma el selector de empresa con los puntos de venta y no pide /empresas (solo Root/Admin)', async () => {
    empresas = [empresa(2, 'Otra SA'), empresa(1, 'Empresa Demo')]
    renderPagina()

    const selector = await screen.findByLabelText('Empresa')
    expect(within(selector).getAllByRole('option').map((o) => o.textContent)).toEqual(['Elegir…', 'Empresa Demo', 'Otra SA'])
    expect(llamadasA('/empresas')).toHaveLength(0)
    await userEvent.setup().selectOptions(selector, '1')
    expect(await screen.findByRole('tab', { name: 'Conciliaciones' })).toBeInTheDocument()
    expect(screen.queryByRole('tab', { name: 'Importar' })).toBeNull()
  })

  it('ve los candidatos pero ningún botón Confirmar', async () => {
    renderPagina()

    await irAPestana('Para revisar')

    expect(await screen.findByText(/Venta 0001-00000042/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Confirmar/ })).toBeNull()
  })
})

describe('ConciliacionMercadoPago — cambio de empresa y de pestaña', () => {
  it('al cambiar de empresa con un detalle abierto no queda el detalle ni el Confirmar de la anterior', async () => {
    empresas = [empresa(1, 'Empresa Demo'), empresa(2, 'Otra SA')]
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/puntos-venta') return Promise.resolve(puntosVentaDe(empresas))
      if (ruta === '/mercadopago/conciliaciones?idEmpresa=1') return Promise.resolve([conciliacion()])
      if (ruta === '/mercadopago/conciliaciones?idEmpresa=2') return Promise.resolve([conciliacion({ id: 4, idEmpresa: 2, nombreArchivo: 'de-otra-sa.csv' })])
      if (ruta.endsWith('/propuestas')) return Promise.resolve([])
      if (ruta === '/cuentas-tesoreria') return Promise.resolve([])
      if (ruta.startsWith('/mercadopago/conciliaciones/3')) return Promise.resolve(detalle([fila()]))
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    renderPagina()
    const usuario = userEvent.setup()

    await usuario.selectOptions(await screen.findByLabelText('Empresa'), '1')
    await usuario.click(await screen.findByRole('tab', { name: 'Conciliaciones' }))
    await usuario.click(await screen.findByRole('button', { name: /Ver reporte-octubre/ }))
    expect(await screen.findByText('SRC-777')).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: /Confirmar/ }).length).toBeGreaterThan(0)

    await usuario.selectOptions(screen.getByLabelText('Empresa'), '2')

    expect(await screen.findByText('de-otra-sa.csv')).toBeInTheDocument()
    expect(screen.queryByText('SRC-777')).toBeNull()
    expect(screen.queryByRole('button', { name: /Confirmar/ })).toBeNull()
  })

  it('el archivo elegido no sobrevive a una vuelta por otra pestaña: no hay nada que subir', async () => {
    renderPagina()
    const usuario = userEvent.setup()

    await elegirArchivo(csv())
    expect(screen.getByRole('button', { name: 'Importar' })).toBeEnabled()

    await irAPestana('Conciliaciones')
    await irAPestana('Importar')

    expect(screen.getByRole('button', { name: 'Importar' })).toBeDisabled()
    await usuario.click(screen.getByRole('button', { name: 'Importar' }))
    expect(apiPostFormularioMock).not.toHaveBeenCalled()
  })

  it('avisa que el rango de pagos sin verificar se interpreta en la zona horaria de la empresa', async () => {
    renderPagina()

    await irAPestana('Para revisar')

    expect(await screen.findByText('Las fechas se interpretan en la zona horaria de la empresa.')).toBeInTheDocument()
  })
})

function propuesta(sobrescribir: Partial<PropuestaDeTesoreria> = {}): PropuestaDeTesoreria {
  return {
    id: 11,
    tipo: 'diferencia_comision',
    estado: 'pendiente',
    importe: -1.5,
    idCuentaOrigen: 70,
    nombreCuentaOrigen: 'Mercado Pago',
    idCuentaDestino: null,
    nombreCuentaDestino: null,
    numeroLinea: null,
    sourceId: null,
    fechaTransaccion: null,
    detalle: {
      cantidadDeCobros: 4,
      comisionRealTotal: 26,
      comisionEstimadaTotal: 24.5,
      cobrosSinCierre: 2,
      cobrosSinComisionReal: 1,
      lineas: [],
      lineasTruncado: false,
    },
    idMovimientoCuenta: null,
    idMovimientoCuentaDestino: null,
    resueltaEl: null,
    resueltaPor: null,
    origenesPosibles: [],
    motivo: null,
    ...sobrescribir,
  }
}

function retiro(sobrescribir: Partial<PropuestaDeTesoreria> = {}): PropuestaDeTesoreria {
  return propuesta({
    id: 12,
    tipo: 'retiro',
    importe: 500,
    detalle: null,
    numeroLinea: 9,
    sourceId: 'WD-1',
    fechaTransaccion: '2026-10-30T15:00:00Z',
    ...sobrescribir,
  })
}

function cuenta(sobrescribir: Partial<CuentaTesoreriaListado>): CuentaTesoreriaListado {
  return {
    id: 1,
    idEmpresa: 1,
    nombreEmpresa: 'Empresa Demo',
    nombre: 'Cuenta',
    saldoInicial: 0,
    saldoActual: 0,
    activo: true,
    saldoInicialEditable: true,
    ...sobrescribir,
  }
}

async function abrirDetalle() {
  const usuario = userEvent.setup()
  await irAPestana('Conciliaciones')
  await usuario.click(await screen.findByRole('button', { name: /Ver reporte-octubre/ }))
  return { usuario, seccion: await screen.findByRole('region', { name: 'Movimientos a tesorería' }) }
}

describe('ConciliacionMercadoPago — movimientos a tesorería', () => {
  beforeEach(() => {
    cuentas = [
      cuenta({ id: 70, nombre: 'Mercado Pago' }),
      cuenta({ id: 71, nombre: 'Banco Nación' }),
      cuenta({ id: 72, nombre: 'Banco inactivo', activo: false }),
      cuenta({ id: 73, nombre: 'Banco de otra empresa', idEmpresa: 2 }),
    ]
  })

  it('lista la diferencia de comisión y el retiro con su efecto, el cálculo y lo que quedó fuera', async () => {
    propuestas = [propuesta(), retiro()]
    renderPagina()

    const { seccion } = await abrirDetalle()

    expect(await within(seccion).findByText(/Debita \$\s?1,50 de Mercado Pago/)).toBeInTheDocument()
    expect(within(seccion).getByText(/4 cobro\(s\): comisión real/)).toBeInTheDocument()
    expect(within(seccion).getByText(/2 cobro\(s\) de turnos que todavía no acreditaron el medio/)).toBeInTheDocument()
    expect(within(seccion).getByText(/1 cobro\(s\) vinieron sin comisión en el reporte/)).toBeInTheDocument()
    expect(within(seccion).getByText(/Pasa \$\s?500,00 de Mercado Pago a la cuenta que elijas/)).toBeInTheDocument()
    expect(within(seccion).getByText(/Línea 9 · WD-1/)).toBeInTheDocument()
  })

  it('aplica la diferencia tras confirmarla: manda destino nulo, avisa y vuelve a leer', async () => {
    propuestas = [propuesta()]
    apiPostMock.mockResolvedValue(propuesta({ estado: 'aplicada' }))
    renderPagina()
    const { usuario, seccion } = await abrirDetalle()
    const lecturasAntes = llamadasA('/mercadopago/conciliaciones/3/propuestas').length

    await usuario.click(await within(seccion).findByRole('button', { name: 'Aplicar diferencia de comisión' }))
    const dialogo = await screen.findByRole('alertdialog')
    expect(apiPostMock).not.toHaveBeenCalled()
    await usuario.click(within(dialogo).getByRole('button', { name: 'Aplicar' }))

    await waitFor(() => expect(screen.queryByRole('alertdialog')).toBeNull())
    expect(apiPostMock).toHaveBeenCalledWith('/mercadopago/conciliaciones/3/propuestas/11/aplicar', { idCuentaDestino: null, idCuentaOrigen: null })
    expect(await screen.findByText('Movimiento aplicado en la tesorería.')).toBeInTheDocument()
    await waitFor(() => expect(llamadasA('/mercadopago/conciliaciones/3/propuestas').length).toBeGreaterThan(lecturasAntes))
  })

  it('un retiro no se aplica sin cuenta y ofrece solo cuentas activas de la empresa distintas del origen', async () => {
    propuestas = [retiro()]
    apiPostMock.mockResolvedValue(retiro({ estado: 'aplicada' }))
    renderPagina()
    const { usuario, seccion } = await abrirDetalle()

    const selector = await within(seccion).findByLabelText('Cuenta de destino del retiro de la línea 9')
    const boton = within(seccion).getByRole('button', { name: 'Aplicar retiro de la línea 9' })
    expect(boton).toBeDisabled()
    await within(selector).findByRole('option', { name: 'Banco Nación' })
    expect(within(selector).getAllByRole('option').map((o) => o.textContent)).toEqual(['Elegir…', 'Banco Nación'])

    await usuario.selectOptions(selector, '71')
    expect(boton).toBeEnabled()
    await usuario.click(boton)
    const dialogo = await screen.findByRole('alertdialog')
    expect(within(dialogo).getByText(/Pasa \$\s?500,00 de Mercado Pago a Banco Nación/)).toBeInTheDocument()
    await usuario.click(within(dialogo).getByRole('button', { name: 'Aplicar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/mercadopago/conciliaciones/3/propuestas/12/aplicar', { idCuentaDestino: 71, idCuentaOrigen: null }))
  })

  it('un retiro sin cuenta de origen ni opciones avisa y no se puede aplicar, pero sí descartar', async () => {
    propuestas = [retiro({ idCuentaOrigen: null, nombreCuentaOrigen: null })]
    apiPostMock.mockResolvedValue(retiro({ estado: 'descartada' }))
    renderPagina()
    const { usuario, seccion } = await abrirDetalle()

    expect(await within(seccion).findByText(/Ningún medio de pago de Mercado Pago de la empresa acredita en una cuenta/)).toBeInTheDocument()
    expect(within(seccion).getByRole('button', { name: 'Aplicar retiro de la línea 9' })).toBeDisabled()

    await usuario.click(within(seccion).getByRole('button', { name: 'Descartar retiro de la línea 9' }))
    await usuario.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Descartar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/mercadopago/conciliaciones/3/propuestas/12/descartar', {}))
    expect(await screen.findByText('Propuesta descartada.')).toBeInTheDocument()
  })

  it('un retiro sin origen fijo pide elegirlo entre las cuentas de los medios y lo manda con el destino', async () => {
    propuestas = [
      retiro({
        idCuentaOrigen: null,
        nombreCuentaOrigen: null,
        origenesPosibles: [
          { id: 70, nombre: 'Mercado Pago' },
          { id: 74, nombre: 'Mercado Pago 2' },
        ],
      }),
    ]
    cuentas = [...cuentas, cuenta({ id: 74, nombre: 'Mercado Pago 2' })]
    apiPostMock.mockResolvedValue(retiro({ estado: 'aplicada' }))
    renderPagina()
    const { usuario, seccion } = await abrirDetalle()

    const origen = await within(seccion).findByLabelText('Cuenta de origen del retiro de la línea 9')
    const destino = within(seccion).getByLabelText('Cuenta de destino del retiro de la línea 9')
    const boton = within(seccion).getByRole('button', { name: 'Aplicar retiro de la línea 9' })
    await within(destino).findByRole('option', { name: 'Banco Nación' })
    await usuario.selectOptions(destino, '71')
    expect(boton).toBeDisabled()

    await usuario.selectOptions(origen, '74')
    expect(boton).toBeEnabled()
    await usuario.click(boton)
    const dialogo = await screen.findByRole('alertdialog')
    expect(within(dialogo).getByText(/Pasa \$\s?500,00 de Mercado Pago 2 a Banco Nación/)).toBeInTheDocument()
    await usuario.click(within(dialogo).getByRole('button', { name: 'Aplicar' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith('/mercadopago/conciliaciones/3/propuestas/12/aplicar', { idCuentaDestino: 71, idCuentaOrigen: 74 }),
    )
  })

  it('una cuenta de destino elegida que deja de ofrecerse al cambiar el origen se descarta y Aplicar queda inerte', async () => {
    propuestas = [
      retiro({
        idCuentaOrigen: null,
        nombreCuentaOrigen: null,
        origenesPosibles: [
          { id: 70, nombre: 'Mercado Pago' },
          { id: 71, nombre: 'Banco Nación' },
        ],
      }),
    ]
    renderPagina()
    const { usuario, seccion } = await abrirDetalle()
    const origen = await within(seccion).findByLabelText('Cuenta de origen del retiro de la línea 9')
    const destino = within(seccion).getByLabelText('Cuenta de destino del retiro de la línea 9')
    const boton = within(seccion).getByRole('button', { name: 'Aplicar retiro de la línea 9' })
    await within(destino).findByRole('option', { name: 'Banco Nación' })
    await usuario.selectOptions(origen, '70')
    await usuario.selectOptions(destino, '71')
    expect(boton).toBeEnabled()

    await usuario.selectOptions(origen, '71')

    await waitFor(() => expect(destino).toHaveValue(''))
    expect(boton).toBeDisabled()
    await usuario.selectOptions(origen, '70')
    expect(destino).toHaveValue('')
    expect(boton).toBeDisabled()
  })

  it('una propuesta descartada se puede reabrir: confirma, llama al endpoint y vuelve a leer', async () => {
    propuestas = [retiro({ estado: 'descartada', resueltaEl: '2026-11-01T12:00:00Z', resueltaPor: 'admin', motivo: 'cancelado por Mercado Pago' })]
    apiPostMock.mockResolvedValue(retiro())
    renderPagina()
    const { usuario, seccion } = await abrirDetalle()

    expect(await within(seccion).findByText(/Descartada el .* por admin · cancelado por Mercado Pago/)).toBeInTheDocument()
    expect(within(seccion).queryByRole('button', { name: /Aplicar|Descartar/ })).toBeNull()
    const lecturasAntes = llamadasA('/mercadopago/conciliaciones/3/propuestas').length

    await usuario.click(within(seccion).getByRole('button', { name: 'Reabrir retiro de la línea 9' }))
    const dialogo = await screen.findByRole('alertdialog')
    expect(within(dialogo).getByText(/vuelve a quedar pendiente/)).toBeInTheDocument()
    await usuario.click(within(dialogo).getByRole('button', { name: 'Reabrir' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/mercadopago/conciliaciones/3/propuestas/12/reabrir', {}))
    expect(await screen.findByText('Propuesta reabierta.')).toBeInTheDocument()
    await waitFor(() => expect(llamadasA('/mercadopago/conciliaciones/3/propuestas').length).toBeGreaterThan(lecturasAntes))
  })

  it('un rechazo 409 al reabrir deja el diálogo con el error y vuelve a leer', async () => {
    propuestas = [propuesta({ estado: 'descartada', resueltaEl: '2026-11-01T12:00:00Z', resueltaPor: 'admin' })]
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'propuesta_no_descartada', 'x'))
    renderPagina()
    const { usuario, seccion } = await abrirDetalle()
    const lecturasAntes = llamadasA('/mercadopago/conciliaciones/3/propuestas').length

    await usuario.click(await within(seccion).findByRole('button', { name: 'Reabrir diferencia de comisión' }))
    await usuario.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Reabrir' }))

    expect(await within(await screen.findByRole('alertdialog')).findByText('La propuesta ya no está descartada: no se puede reabrir.')).toBeInTheDocument()
    await waitFor(() => expect(llamadasA('/mercadopago/conciliaciones/3/propuestas').length).toBeGreaterThan(lecturasAntes))
  })

  it('la reversión de un retiro cancelado dice a dónde vuelve el dinero y se aplica sin elegir cuentas', async () => {
    propuestas = [
      retiro({ tipo: 'retiro_cancelado', idCuentaOrigen: 70, nombreCuentaOrigen: 'Mercado Pago', idCuentaDestino: 71, nombreCuentaDestino: 'Banco Nación' }),
    ]
    apiPostMock.mockResolvedValue(retiro({ tipo: 'retiro_cancelado', estado: 'aplicada' }))
    renderPagina()
    const { usuario, seccion } = await abrirDetalle()

    expect(await within(seccion).findByText(/Devuelve \$\s?500,00 de Banco Nación a Mercado Pago/)).toBeInTheDocument()
    expect(within(seccion).queryByLabelText(/Cuenta de (origen|destino)/)).toBeNull()
    await usuario.click(within(seccion).getByRole('button', { name: 'Aplicar reversión del retiro de la línea 9' }))
    await usuario.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Aplicar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/mercadopago/conciliaciones/3/propuestas/12/aplicar', { idCuentaDestino: null, idCuentaOrigen: null }))
  })

  it('el Supervisor ve una propuesta descartada sin el botón de reabrir', async () => {
    usuarioActual = usuarioFixture(ROL.Supervisor)
    propuestas = [propuesta({ estado: 'descartada', resueltaEl: '2026-11-01T12:00:00Z', resueltaPor: 'admin' })]
    renderPagina()

    const { seccion } = await abrirDetalle()

    expect(await within(seccion).findByText(/Descartada el/)).toBeInTheDocument()
    expect(within(seccion).queryByRole('button', { name: /Reabrir/ })).toBeNull()
  })

  it('una propuesta ya resuelta no ofrece botones y dice cuándo y quién', async () => {
    propuestas = [propuesta({ estado: 'aplicada', resueltaEl: '2026-11-01T12:00:00Z', resueltaPor: 'admin', idMovimientoCuenta: 5 })]
    renderPagina()

    const { seccion } = await abrirDetalle()

    expect(await within(seccion).findByText(/Aplicada el .* por admin/)).toBeInTheDocument()
    expect(within(seccion).queryByRole('button', { name: /Aplicar|Descartar/ })).toBeNull()
  })

  it.each([
    ['propuesta_ya_aplicada', 'La propuesta ya se aplicó.'],
    ['periodo_conciliado', 'período de tesorería ya conciliado'],
    ['cuenta_destino_invalida', 'cuenta activa de la misma empresa'],
  ])('un 409 %s deja el diálogo abierto con su copia y vuelve a leer', async (codigo, copia) => {
    propuestas = [propuesta()]
    apiPostMock.mockRejectedValue(new ErrorApi(409, codigo, 'x'))
    renderPagina()
    const { usuario, seccion } = await abrirDetalle()
    await usuario.click(await within(seccion).findByRole('button', { name: 'Aplicar diferencia de comisión' }))
    const dialogo = await screen.findByRole('alertdialog')
    const lecturasAntes = llamadasA('/mercadopago/conciliaciones/3/propuestas').length

    await usuario.click(within(dialogo).getByRole('button', { name: 'Aplicar' }))

    expect(await within(dialogo).findByRole('alert')).toHaveTextContent(copia)
    expect(screen.getByRole('alertdialog')).toBeInTheDocument()
    await waitFor(() => expect(llamadasA('/mercadopago/conciliaciones/3/propuestas').length).toBeGreaterThan(lecturasAntes))
  })

  it('dos clics seguidos en Aplicar escriben una sola vez y toda la pantalla queda inerte mientras tanto', async () => {
    propuestas = [propuesta()]
    const escritura = diferido<PropuestaDeTesoreria>()
    apiPostMock.mockReturnValue(escritura.promesa)
    renderPagina()
    const { usuario, seccion } = await abrirDetalle()
    await usuario.click(await within(seccion).findByRole('button', { name: 'Aplicar diferencia de comisión' }))
    const dialogo = await screen.findByRole('alertdialog')

    const boton = within(dialogo).getByRole('button', { name: 'Aplicar' })
    act(() => {
      fireEvent.click(boton)
      fireEvent.click(boton)
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(await within(dialogo).findByRole('button', { name: 'Aplicando…' })).toBeDisabled()
    expect(within(dialogo).getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByLabelText('Empresa')).toBeDisabled()
    expect(within(seccion).getByRole('button', { name: 'Descartar diferencia de comisión' })).toBeDisabled()

    await act(async () => escritura.resolver(propuesta({ estado: 'aplicada' })))
    await waitFor(() => expect(screen.queryByRole('alertdialog')).toBeNull())
  })

  it('el Supervisor ve las propuestas pero ni los botones ni las cuentas: no las pide', async () => {
    usuarioActual = usuarioFixture(ROL.Supervisor)
    propuestas = [propuesta(), retiro()]
    renderPagina()

    const { seccion } = await abrirDetalle()

    expect(await within(seccion).findByText(/Debita/)).toBeInTheDocument()
    expect(within(seccion).queryByRole('button', { name: /Aplicar|Descartar/ })).toBeNull()
    expect(within(seccion).queryByLabelText(/Cuenta de destino/)).toBeNull()
    expect(llamadasA('/cuentas-tesoreria')).toHaveLength(0)
  })

  it('una lectura que falla muestra el error con Reintentar', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.endsWith('/propuestas')) return Promise.reject(new ErrorApi(500, 'x', 'caído'))
      if (ruta === '/cuentas-tesoreria') return Promise.resolve(cuentas)
      if (ruta === '/puntos-venta') return Promise.resolve(puntosVentaDe(empresas))
      if (ruta.startsWith('/mercadopago/conciliaciones?')) return Promise.resolve(lista)
      return detalles(ruta)
    })
    renderPagina()

    const { seccion } = await abrirDetalle()

    expect(await within(seccion).findByRole('alert')).toBeInTheDocument()
    expect(within(seccion).getByRole('button', { name: 'Reintentar' })).toBeInTheDocument()
  })
})
