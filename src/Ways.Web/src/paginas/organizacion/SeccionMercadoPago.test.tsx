import { StrictMode } from 'react'
import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { SeccionMercadoPago } from './SeccionMercadoPago'
import { ErrorApi } from '../../api/cliente'
import type { CuentaTesoreriaListado } from '../../api/cuentasTesoreria'
import type { EstadoDeConexionMercadoPago } from '../../api/mercadoPago'
import type { EmpresaListado } from '../../api/tipos'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()
const apiDeleteMock = vi.fn()

vi.mock('../../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...args),
    post: (...args: unknown[]) => apiPostMock(...args),
    delete: (...args: unknown[]) => apiDeleteMock(...args),
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

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
  window.history.replaceState(null, '', '/')
})

function empresa(id: number, razonSocial: string): EmpresaListado {
  return {
    id,
    idTenant: 2,
    razonSocial,
    nombreFantasia: null,
    cuit: null,
    nombreTenant: 'Comercio Sur',
    alicuotaPercepcionIibb: null,
    alicuotaPercepcionIva: null,
    ingresosBrutos: null,
    inicioActividades: null,
  }
}

function cuenta(id: number, idEmpresa: number, nombre: string, activo = true): CuentaTesoreriaListado {
  return {
    id,
    idEmpresa,
    nombreEmpresa: null,
    nombre,
    saldoInicial: 0,
    saldoActual: 0,
    activo,
    saldoInicialEditable: true,
  }
}

const SIN_CONEXION: EstadoDeConexionMercadoPago = { conectada: false }

const ACTIVA: EstadoDeConexionMercadoPago = {
  conectada: true,
  estado: 'activa',
  idCuentaTesoreria: 31,
  nombreCuenta: 'Mercado Pago',
  idUsuarioMercadoPago: 123456789,
  fechaConexion: '2026-10-31T15:00:00Z',
  accessTokenExpiraEn: '2027-04-29T15:00:00Z',
}

const REQUIERE_RECONEXION: EstadoDeConexionMercadoPago = {
  conectada: false,
  estado: 'requiere_reconexion',
  idCuentaTesoreria: 31,
  nombreCuenta: 'Mercado Pago',
  idUsuarioMercadoPago: 123456789,
  fechaConexion: '2026-10-31T15:00:00Z',
}

/** Estado de la conexión por empresa; las pruebas lo cambian para simular lo que el servidor guardó. */
let conexiones: Record<number, EstadoDeConexionMercadoPago>
let cuentas: CuentaTesoreriaListado[]
let rechazoDeCuentas: boolean

const EMPRESA = empresa(10, 'Sur SRL')

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiDeleteMock.mockReset()
  conexiones = { 10: SIN_CONEXION }
  cuentas = [cuenta(31, 10, 'Mercado Pago'), cuenta(32, 10, 'Cuenta vieja', false), cuenta(33, 11, 'De otra empresa')]
  rechazoDeCuentas = false

  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/cuentas-tesoreria') {
      return rechazoDeCuentas ? Promise.reject(new ErrorApi(500, 'error_interno', 'x')) : Promise.resolve(cuentas)
    }
    const coincidencia = /^\/empresas\/(\d+)\/mercadopago\/conexion$/.exec(ruta)
    if (coincidencia) return Promise.resolve(conexiones[Number(coincidencia[1])] ?? SIN_CONEXION)

    return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
  })
})

/** Espera a que lleguen las cuentas y la conexión: el selector o el estado, no el título de la tarjeta. */
async function esperarDatos(razonSocial = 'Sur SRL') {
  const tarjeta = await screen.findByRole('region', { name: `Mercado Pago de ${razonSocial}` })
  await waitFor(() => expect(within(tarjeta).queryByText('Cargando…')).not.toBeInTheDocument())
  await waitFor(() => expect(apiGetMock).toHaveBeenCalledWith('/cuentas-tesoreria'))

  return tarjeta
}

describe('SeccionMercadoPago — estados', () => {
  it('sin conexión ofrece conectar con las cuentas ACTIVAS de la empresa y nada más', async () => {
    cuentas = [...cuentas, cuenta(35, 10, 'Otra cuenta')]
    render(<SeccionMercadoPago empresas={[EMPRESA]} />)
    const tarjeta = await esperarDatos()

    const selector = await within(tarjeta).findByRole('combobox', { name: /cuenta de tesorería/i })
    await waitFor(() =>
      expect(within(selector).getAllByRole('option').map((o) => o.textContent)).toEqual(['Elegí una cuenta', 'Mercado Pago', 'Otra cuenta']),
    )
    expect(within(tarjeta).getByText('Mercado Pago no está conectado.')).toBeInTheDocument()
    expect(within(tarjeta).getByRole('button', { name: 'Conectar Mercado Pago' })).toBeDisabled()
  })

  it('con una conexión activa muestra la cuenta de Mercado Pago, las fechas y el botón Desconectar, sin ofrecer conectar', async () => {
    conexiones[10] = ACTIVA
    render(<SeccionMercadoPago empresas={[EMPRESA]} />)
    const tarjeta = await esperarDatos()

    await within(tarjeta).findByText('Conectada')
    expect(within(tarjeta).getByText('123456789')).toBeInTheDocument()
    expect(within(tarjeta).getByText(/cuenta de tesorería «Mercado Pago»/)).toBeInTheDocument()
    expect(within(tarjeta).getByText(/Conectada el /)).toBeInTheDocument()
    expect(within(tarjeta).getByText(/El acceso vence el /)).toBeInTheDocument()
    expect(within(tarjeta).getByRole('button', { name: 'Desconectar' })).toBeEnabled()
    expect(within(tarjeta).queryByRole('button', { name: 'Conectar Mercado Pago' })).not.toBeInTheDocument()
  })

  it('si requiere reconexión avisa que hay que volver a conectar y ofrece el botón de conectar', async () => {
    conexiones[10] = REQUIERE_RECONEXION
    render(<SeccionMercadoPago empresas={[EMPRESA]} />)
    const tarjeta = await esperarDatos()

    expect(await within(tarjeta).findByRole('alert')).toHaveTextContent('Hay que volver a conectar Mercado Pago')
    expect(within(tarjeta).queryByText('Conectada')).not.toBeInTheDocument()
    expect(within(tarjeta).getByRole('button', { name: 'Conectar Mercado Pago' })).toBeInTheDocument()
  })

  it('si requiere reconexión ofrece Desconectar con la misma confirmación, y desconectar vuelve a leer el estado', async () => {
    conexiones[10] = REQUIERE_RECONEXION
    apiDeleteMock.mockImplementation(() => {
      conexiones[10] = SIN_CONEXION

      return Promise.resolve(undefined)
    })
    const usuario = userEvent.setup()
    render(<SeccionMercadoPago empresas={[EMPRESA]} />)
    const tarjeta = await esperarDatos()

    await usuario.click(await within(tarjeta).findByRole('button', { name: 'Desconectar' }))
    const puerta = await screen.findByRole('alertdialog')
    expect(apiDeleteMock).not.toHaveBeenCalled()
    await usuario.click(within(puerta).getByRole('button', { name: 'Desconectar' }))

    await waitFor(() => expect(apiDeleteMock).toHaveBeenCalledWith('/empresas/10/mercadopago/conexion'))
    expect(await within(tarjeta).findByText('Mercado Pago no está conectado.')).toBeInTheDocument()
    expect(within(tarjeta).queryByRole('button', { name: 'Desconectar' })).not.toBeInTheDocument()
  })

  it('sin cuentas de tesorería activas explica que hay que crear una y enlaza a su pantalla, sin selector', async () => {
    cuentas = [cuenta(32, 10, 'Cuenta vieja', false)]
    render(<SeccionMercadoPago empresas={[EMPRESA]} />)
    const tarjeta = await esperarDatos()

    expect(await within(tarjeta).findByText(/no tiene cuentas de tesorería activas/)).toBeInTheDocument()
    expect(within(tarjeta).getByRole('link', { name: 'cuentas de tesorería' })).toHaveAttribute('href', '/caja/cuentas-tesoreria')
    expect(within(tarjeta).queryByRole('combobox')).not.toBeInTheDocument()
    expect(within(tarjeta).queryByRole('button', { name: 'Conectar Mercado Pago' })).not.toBeInTheDocument()
  })

  it('si las cuentas no cargan lo dice en vez de afirmar que no hay ninguna', async () => {
    rechazoDeCuentas = true
    render(<SeccionMercadoPago empresas={[EMPRESA]} />)
    const tarjeta = await esperarDatos()

    expect(await within(tarjeta).findByText('No se pudieron cargar las cuentas de tesorería.')).toBeInTheDocument()
    expect(within(tarjeta).queryByText(/no tiene cuentas de tesorería activas/)).not.toBeInTheDocument()
  })

  it('con una sola cuenta activa la deja elegida y habilita conectar', async () => {
    render(<SeccionMercadoPago empresas={[EMPRESA]} />)
    const tarjeta = await esperarDatos()

    await waitFor(() => expect(within(tarjeta).getByRole('button', { name: 'Conectar Mercado Pago' })).toBeEnabled())
    expect(within(tarjeta).getByRole('combobox', { name: /cuenta de tesorería/i })).toHaveValue('31')
  })

  it('cada empresa tiene su tarjeta y su propio estado', async () => {
    conexiones[11] = ACTIVA
    render(<SeccionMercadoPago empresas={[EMPRESA, empresa(11, 'Sur Anexo SA')]} />)
    const propia = await esperarDatos()
    const anexo = await esperarDatos('Sur Anexo SA')

    await within(anexo).findByText('Conectada')
    expect(within(propia).queryByText('Conectada')).not.toBeInTheDocument()
  })
})

describe('SeccionMercadoPago — conectar', () => {
  it('inicia la conexión con la cuenta elegida y manda el navegador a Mercado Pago', async () => {
    const assign = vi.fn()
    vi.stubGlobal('location', { hash: '', pathname: '/organizacion/empresas', search: '', assign })
    cuentas = [cuenta(31, 10, 'Mercado Pago'), cuenta(34, 10, 'Mercado Pago 2')]
    apiPostMock.mockResolvedValue({ urlAutorizacion: 'https://auth.mercadopago.com/authorization?state=abc' })
    const usuario = userEvent.setup()
    render(<SeccionMercadoPago empresas={[EMPRESA]} />)
    const tarjeta = await esperarDatos()

    const selector = await within(tarjeta).findByRole('combobox', { name: /cuenta de tesorería/i })
    await waitFor(() => expect(within(selector).getAllByRole('option')).toHaveLength(3))
    expect(within(tarjeta).getByRole('button', { name: 'Conectar Mercado Pago' })).toBeDisabled()
    await usuario.selectOptions(selector, '34')
    await usuario.click(within(tarjeta).getByRole('button', { name: 'Conectar Mercado Pago' }))

    await waitFor(() => expect(assign).toHaveBeenCalledWith('https://auth.mercadopago.com/authorization?state=abc'))
    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(apiPostMock).toHaveBeenCalledWith('/empresas/10/mercadopago/conexion/inicio', { idCuentaTesoreria: 34 })
    expect(within(tarjeta).getByRole('button', { name: 'Conectando…' })).toBeDisabled()
  })

  describe('al volver de Mercado Pago con "Atrás" (caché de ida y vuelta)', () => {
    function restaurarPagina(persisted: boolean) {
      const evento = new Event('pageshow')
      Object.defineProperty(evento, 'persisted', { value: persisted })
      act(() => {
        window.dispatchEvent(evento)
      })
    }

    async function conectarYSalir() {
      vi.stubGlobal('location', { hash: '', pathname: '/organizacion/empresas', search: '', assign: vi.fn() })
      apiPostMock.mockResolvedValue({ urlAutorizacion: 'https://auth.mercadopago.com/authorization?state=abc' })
      const usuario = userEvent.setup()
      render(<SeccionMercadoPago empresas={[EMPRESA]} />)
      const tarjeta = await esperarDatos()
      await usuario.click(await within(tarjeta).findByRole('button', { name: 'Conectar Mercado Pago' }))
      expect(await within(tarjeta).findByRole('button', { name: 'Conectando…' })).toBeDisabled()

      return tarjeta
    }

    const lecturasDeConexion = () => apiGetMock.mock.calls.filter(([ruta]) => /conexion$/.test(String(ruta))).length

    it('una restauración libera la tarjeta y vuelve a leer el estado', async () => {
      const tarjeta = await conectarYSalir()
      const lecturasAntes = lecturasDeConexion()

      restaurarPagina(true)

      await waitFor(() => expect(within(tarjeta).getByRole('button', { name: 'Conectar Mercado Pago' })).toBeEnabled())
      await waitFor(() => expect(lecturasDeConexion()).toBe(lecturasAntes + 1))
    })

    it('un pageshow que no es una restauración no toca la tarjeta', async () => {
      const tarjeta = await conectarYSalir()
      const lecturasAntes = lecturasDeConexion()

      restaurarPagina(false)

      expect(within(tarjeta).getByRole('button', { name: 'Conectando…' })).toBeDisabled()
      expect(lecturasDeConexion()).toBe(lecturasAntes)
    })
  })

  it('un doble clic en el mismo tick inicia UNA sola conexión', async () => {
    const assign = vi.fn()
    vi.stubGlobal('location', { hash: '', pathname: '/organizacion/empresas', search: '', assign })
    apiPostMock.mockReturnValue(new Promise(() => undefined))
    render(<SeccionMercadoPago empresas={[EMPRESA]} />)
    const tarjeta = await esperarDatos()
    const boton = await within(tarjeta).findByRole('button', { name: 'Conectar Mercado Pago' })
    await waitFor(() => expect(boton).toBeEnabled())

    boton.click()
    boton.click()

    await waitFor(() => expect(apiPostMock).toHaveBeenCalled())
    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  it('503 mercadopago_no_configurado: dice que Mercado Pago no está configurado en el servidor y libera el botón', async () => {
    const assign = vi.fn()
    vi.stubGlobal('location', { hash: '', pathname: '/organizacion/empresas', search: '', assign })
    apiPostMock.mockRejectedValue(new ErrorApi(503, 'mercadopago_no_configurado', 'no configurado'))
    const usuario = userEvent.setup()
    render(<SeccionMercadoPago empresas={[EMPRESA]} />)
    const tarjeta = await esperarDatos()
    const boton = await within(tarjeta).findByRole('button', { name: 'Conectar Mercado Pago' })
    await waitFor(() => expect(boton).toBeEnabled())

    await usuario.click(boton)

    expect(await within(tarjeta).findByText(/no está configurado en el servidor/)).toBeInTheDocument()
    expect(assign).not.toHaveBeenCalled()
    await waitFor(() => expect(within(tarjeta).getByRole('button', { name: 'Conectar Mercado Pago' })).toBeEnabled())
  })

  it('una URL de autorización que no es http(s) no se navega', async () => {
    const assign = vi.fn()
    vi.stubGlobal('location', { hash: '', pathname: '/organizacion/empresas', search: '', assign })
    apiPostMock.mockResolvedValue({ urlAutorizacion: 'javascript:alert(1)' })
    const usuario = userEvent.setup()
    render(<SeccionMercadoPago empresas={[EMPRESA]} />)
    const tarjeta = await esperarDatos()
    const boton = await within(tarjeta).findByRole('button', { name: 'Conectar Mercado Pago' })
    await waitFor(() => expect(boton).toBeEnabled())

    await usuario.click(boton)

    expect(await within(tarjeta).findByText('No se pudo iniciar la conexión con Mercado Pago.')).toBeInTheDocument()
    expect(assign).not.toHaveBeenCalled()
  })
})

describe('SeccionMercadoPago — desconectar', () => {
  it('pide confirmación nombrando la acción, desconecta y vuelve a leer el estado', async () => {
    conexiones[10] = ACTIVA
    apiDeleteMock.mockImplementation(() => {
      conexiones[10] = SIN_CONEXION

      return Promise.resolve(undefined)
    })
    const usuario = userEvent.setup()
    render(<SeccionMercadoPago empresas={[EMPRESA]} />)
    const tarjeta = await esperarDatos()

    await usuario.click(await within(tarjeta).findByRole('button', { name: 'Desconectar' }))
    const puerta = await screen.findByRole('alertdialog')
    expect(apiDeleteMock).not.toHaveBeenCalled()
    expect(puerta).toHaveTextContent('Sur SRL')
    await usuario.click(within(puerta).getByRole('button', { name: 'Desconectar' }))

    await waitFor(() => expect(apiDeleteMock).toHaveBeenCalledWith('/empresas/10/mercadopago/conexion'))
    expect(await within(tarjeta).findByText('Mercado Pago no está conectado.')).toBeInTheDocument()
    expect(screen.getByText('Se desconectó Mercado Pago.')).toBeInTheDocument()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
  })

  it('cancelar la confirmación no desconecta nada', async () => {
    conexiones[10] = ACTIVA
    const usuario = userEvent.setup()
    render(<SeccionMercadoPago empresas={[EMPRESA]} />)
    const tarjeta = await esperarDatos()

    await usuario.click(await within(tarjeta).findByRole('button', { name: 'Desconectar' }))
    await usuario.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()
    expect(within(tarjeta).getByText('Conectada')).toBeInTheDocument()
  })

  it('si el servidor rechaza la desconexión, el motivo queda dentro de la confirmación y la conexión sigue', async () => {
    conexiones[10] = ACTIVA
    apiDeleteMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'sin conexión activa'))
    const usuario = userEvent.setup()
    render(<SeccionMercadoPago empresas={[EMPRESA]} />)
    const tarjeta = await esperarDatos()

    await usuario.click(await within(tarjeta).findByRole('button', { name: 'Desconectar' }))
    const puerta = await screen.findByRole('alertdialog')
    await usuario.click(within(puerta).getByRole('button', { name: 'Desconectar' }))

    expect(await within(puerta).findByText(/ya no existe/)).toBeInTheDocument()
    expect(screen.getByRole('alertdialog')).toBeInTheDocument()
  })
})

describe('SeccionMercadoPago — lectura posterior a una escritura', () => {
  it('si la lectura que sigue a desconectar falla, la tarjeta muestra el error y no el estado viejo', async () => {
    conexiones[10] = ACTIVA
    apiDeleteMock.mockResolvedValue(undefined)
    const usuario = userEvent.setup()
    render(<SeccionMercadoPago empresas={[EMPRESA]} />)
    const tarjeta = await esperarDatos()

    await usuario.click(await within(tarjeta).findByRole('button', { name: 'Desconectar' }))
    const puerta = await screen.findByRole('alertdialog')
    apiGetMock.mockImplementation((ruta: string) =>
      /conexion$/.test(ruta) ? Promise.reject(new ErrorApi(500, 'error_interno', 'x')) : Promise.resolve(cuentas),
    )
    await usuario.click(within(puerta).getByRole('button', { name: 'Desconectar' }))

    expect(await within(tarjeta).findByText('No se pudo leer el estado de Mercado Pago.')).toBeInTheDocument()
    expect(within(tarjeta).queryByText('Conectada')).not.toBeInTheDocument()
  })

  it('el error de lectura ofrece Reintentar, que vuelve a leer', async () => {
    let falla = true
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/cuentas-tesoreria') return Promise.resolve(cuentas)
      if (falla) return Promise.reject(new ErrorApi(500, 'error_interno', 'x'))

      return Promise.resolve(ACTIVA)
    })
    const usuario = userEvent.setup()
    render(<SeccionMercadoPago empresas={[EMPRESA]} />)
    const tarjeta = await screen.findByRole('region', { name: 'Mercado Pago de Sur SRL' })

    falla = false
    await usuario.click(await within(tarjeta).findByRole('button', { name: 'Reintentar' }))

    expect(await within(tarjeta).findByText('Conectada')).toBeInTheDocument()
  })
})

describe('SeccionMercadoPago — retorno de Mercado Pago en el fragmento', () => {
  const HASH_COMPLETAR = '#mercadopago=completar&empresa=10&code=TG-codigo&state=estado-protegido'

  function ponerHash(hash: string) {
    window.history.replaceState(null, '', `/organizacion/empresas${hash}`)
  }

  it('borra el fragmento ANTES del POST, completa el canje con code y state y refresca la conexión', async () => {
    ponerHash(HASH_COMPLETAR)
    let hashAlPostear: string | null = null
    apiPostMock.mockImplementation(() => {
      hashAlPostear = window.location.hash
      conexiones[10] = ACTIVA

      return Promise.resolve(ACTIVA)
    })
    render(<SeccionMercadoPago empresas={[EMPRESA]} />)

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith('/empresas/10/mercadopago/conexion/completar', {
        code: 'TG-codigo',
        state: 'estado-protegido',
      }),
    )
    expect(hashAlPostear).toBe('')
    expect(window.location.pathname).toBe('/organizacion/empresas')

    const tarjeta = await screen.findByRole('region', { name: 'Mercado Pago de Sur SRL' })
    expect(await within(tarjeta).findByText('Conectada')).toBeInTheDocument()
    expect(screen.getByText('Mercado Pago quedó conectado.')).toBeInTheDocument()
  })

  it('bajo StrictMode el canje de un code de un solo uso se hace UNA vez', async () => {
    ponerHash(HASH_COMPLETAR)
    apiPostMock.mockResolvedValue(ACTIVA)
    render(
      <StrictMode>
        <SeccionMercadoPago empresas={[EMPRESA]} />
      </StrictMode>,
    )

    await screen.findByText('Mercado Pago quedó conectado.')
    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  it.each([
    ['mercadopago_ya_conectada', /ya tiene Mercado Pago conectado/],
    ['mercadopago_cuenta_en_uso', /ya está conectada a otra empresa/],
    ['mercadopago_intercambio_fallido', /no aceptó la conexión/],
    ['mercadopago_estado_ajeno', /otra sesión o otra empresa/],
    ['mercadopago_estado_invalido', /venció o no es válido/],
  ])('con %s muestra el mensaje mapeado y vuelve a leer la conexión', async (codigo, mensaje) => {
    ponerHash(HASH_COMPLETAR)
    apiPostMock.mockRejectedValue(new ErrorApi(409, codigo, 'texto del servidor'))
    render(<SeccionMercadoPago empresas={[EMPRESA]} />)

    expect(await screen.findByText(mensaje)).toBeInTheDocument()
    expect(window.location.hash).toBe('')
    const tarjeta = await screen.findByRole('region', { name: 'Mercado Pago de Sur SRL' })
    await within(tarjeta).findByText('Mercado Pago no está conectado.')
    expect(screen.queryByText('texto del servidor')).not.toBeInTheDocument()
  })

  it('mientras el canje está en vuelo la tarjeta queda inerte y avisa', async () => {
    ponerHash(HASH_COMPLETAR)
    let resolver: (valor: EstadoDeConexionMercadoPago) => void = () => undefined
    apiPostMock.mockReturnValue(new Promise<EstadoDeConexionMercadoPago>((r) => (resolver = r)))
    render(<SeccionMercadoPago empresas={[EMPRESA]} />)

    expect(await screen.findByText('Completando la conexión con Mercado Pago…')).toBeInTheDocument()
    const tarjeta = await screen.findByRole('region', { name: 'Mercado Pago de Sur SRL' })
    const boton = await within(tarjeta).findByRole('button', { name: 'Conectar Mercado Pago' })
    expect(boton).toBeDisabled()

    conexiones[10] = ACTIVA
    resolver(ACTIVA)
    await screen.findByText('Mercado Pago quedó conectado.')
  })

  it('mercadopago=cancelado informa que no se guardó nada y no llama a la API de completar', async () => {
    ponerHash('#mercadopago=cancelado&empresa=10')
    render(<SeccionMercadoPago empresas={[EMPRESA]} />)

    expect(await screen.findByText('Se canceló la conexión con Mercado Pago. No se guardó nada.')).toBeInTheDocument()
    expect(window.location.hash).toBe('')
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('un fragmento incompleto no canjea nada y se deja como está', async () => {
    ponerHash('#mercadopago=completar&empresa=10&code=solo-el-code')
    render(<SeccionMercadoPago empresas={[EMPRESA]} />)
    await esperarDatos()

    expect(apiPostMock).not.toHaveBeenCalled()
  })
})
