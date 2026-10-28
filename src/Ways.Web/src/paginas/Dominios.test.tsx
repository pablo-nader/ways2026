import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { StrictMode } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Dominios } from './Dominios'
import { ErrorApi } from '../api/cliente'
import type { DominioListado, TenantListado } from '../api/tipos'

// Dominios propios de un tenant (solo plataforma): lista, alta, verificación y baja.

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()
const apiDeleteMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown])),
    put: vi.fn(),
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

function tenant(id: number, nombre: string): TenantListado {
  return {
    id,
    nombre,
    estado: 'Activo',
    createdAt: '2026-01-15T10:00:00-03:00',
    cantidadEmpresas: 1,
    cantidadPuntosVenta: 1,
    cantidadUsuarios: 1,
  }
}

function dominio(sobrescribir: Partial<DominioListado> & Pick<DominioListado, 'id' | 'idTenant' | 'host'>): DominioListado {
  return { esPrincipal: false, verificadoEn: null, createdAt: '2026-10-01T10:00:00Z', ...sobrescribir }
}

const tenantUno = tenant(1, 'Comercio Sur')
const tenantDos = tenant(2, 'Almacén Este')

const principalVerificado = dominio({
  id: 11,
  idTenant: 1,
  host: 'tienda.sur.com',
  esPrincipal: true,
  verificadoEn: '2026-10-11T12:00:00Z',
})
const secundarioSinVerificar = dominio({ id: 12, idTenant: 1, host: 'www.sur.com' })
const dominioDelOtro = dominio({ id: 21, idTenant: 2, host: 'este.example.com', esPrincipal: true })

type Listas = Record<number, DominioListado[]>

/** Las listas se leen al momento de la llamada: un test que quiere que el refresco traiga otra cosa
 * muta `listas` antes de resolver la escritura. */
function montar(listas: Listas = { 1: [principalVerificado, secundarioSinVerificar], 2: [dominioDelOtro] }) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/plataforma/tenants') return Promise.resolve([tenantUno, tenantDos])

    const coincidencia = /^\/plataforma\/tenants\/(\d+)\/dominios$/.exec(ruta)
    if (coincidencia) return Promise.resolve(listas[Number(coincidencia[1])] ?? [])

    return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
  })

  return { listas, ...render(<Dominios />) }
}

function diferida<T>() {
  let resolver: (valor: T) => void = () => {}
  let rechazar: (motivo: unknown) => void = () => {}
  const promesa = new Promise<T>((res, rej) => {
    resolver = res
    rechazar = rej
  })

  return { promesa, resolver, rechazar }
}

/** El selector existe recién cuando llegaron los tenants (`Cargando` antes), así que encontrarlo ya
 * prueba que el dato aterrizó. */
async function elegirTenant(usuario: ReturnType<typeof userEvent.setup>, id: number) {
  await usuario.selectOptions(await screen.findByLabelText('Tenant'), String(id))
}

function llamadasA(mock: ReturnType<typeof vi.fn>, ruta: string) {
  return mock.mock.calls.filter((c) => c[0] === ruta)
}

const RUTA_UNO = '/plataforma/tenants/1/dominios'

/** Abre el modal de alta con el botón de la grilla y devuelve el diálogo. */
async function abrirAlta(usuario: ReturnType<typeof userEvent.setup>) {
  await usuario.click(screen.getByRole('button', { name: 'Agregar dominio' }))

  return screen.findByRole('dialog', { name: 'Nuevo dominio' })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiDeleteMock.mockReset()
  apiPostMock.mockResolvedValue(undefined)
  apiDeleteMock.mockResolvedValue(undefined)
})

describe('Dominios — lista', () => {
  it('sin tenant elegido no ofrece alta ni lista', async () => {
    montar()

    expect(await screen.findByLabelText('Tenant')).toHaveValue('')
    expect(screen.queryByRole('button', { name: 'Agregar dominio' })).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Host')).not.toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
  })

  it('al elegir un tenant rinde host, principal y verificación de cada dominio', async () => {
    const usuario = userEvent.setup()
    montar()

    await elegirTenant(usuario, 1)
    await screen.findByText('tienda.sur.com')

    const verificado = within(screen.getByRole('row', { name: /tienda\.sur\.com/ }))
    expect(verificado.getByText('Principal')).toBeInTheDocument()
    expect(verificado.getByText(/^Verificado el /)).toBeInTheDocument()
    expect(verificado.queryByRole('button', { name: /Marcar/ })).not.toBeInTheDocument()

    const sinVerificar = within(screen.getByRole('row', { name: /www\.sur\.com/ }))
    expect(sinVerificar.queryByText('Principal')).not.toBeInTheDocument()
    expect(sinVerificar.getByText('Sin verificar')).toBeInTheDocument()
    expect(sinVerificar.getByRole('button', { name: 'Marcar verificado: www.sur.com' })).toBeEnabled()

    expect(llamadasA(apiGetMock, RUTA_UNO)).toHaveLength(1)
  })

  it('explica que el DNS y el panel del hosting van antes de verificar', async () => {
    const usuario = userEvent.setup()
    montar()

    await elegirTenant(usuario, 1)

    expect(await screen.findByText(/su DNS debe apuntar al servidor/)).toBeInTheDocument()
    expect(screen.getByText(/panel del hosting/)).toBeInTheDocument()
  })

  it('un tenant sin dominios lo dice', async () => {
    const usuario = userEvent.setup()
    montar({ 1: [] })

    await elegirTenant(usuario, 1)

    expect(await screen.findByText('Este tenant todavía no tiene dominios.')).toBeInTheDocument()
  })

  it('si fallan los tenants lo dice', async () => {
    apiGetMock.mockRejectedValue(new ErrorApi(500, 'error_interno', 'Falló la carga.'))
    render(<Dominios />)

    expect(await screen.findByText('Falló la carga.')).toBeInTheDocument()
  })

  it('si falla la lista ofrece reintentar y el reintento la pide de nuevo', async () => {
    const usuario = userEvent.setup()
    const { listas } = montar()
    const original = apiGetMock.getMockImplementation()!
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === RUTA_UNO ? Promise.reject(new ErrorApi(400, 'solicitud_invalida', 'No hay datos.')) : original(ruta),
    )

    await elegirTenant(usuario, 1)
    expect(await screen.findByText('No hay datos.')).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()

    apiGetMock.mockImplementation(original)
    listas[1] = [principalVerificado]
    await usuario.click(screen.getByRole('button', { name: 'Reintentar' }))

    expect(await screen.findByText('tienda.sur.com')).toBeInTheDocument()
    expect(screen.queryByText('No hay datos.')).not.toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: la respuesta de un tenant ya superado no pisa la lista del tenant
   * vigente. Gana la última lectura iniciada, no la última resuelta.
   */
  it('una lista vieja que llega tarde no pisa la del tenant elegido después', async () => {
    const usuario = userEvent.setup()
    const pendientes = new Map<number, (filas: DominioListado[]) => void>()
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/plataforma/tenants') return Promise.resolve([tenantUno, tenantDos])

      const id = Number(/^\/plataforma\/tenants\/(\d+)\/dominios$/.exec(ruta)![1])

      return new Promise<DominioListado[]>((resolver) => pendientes.set(id, resolver))
    })
    render(<Dominios />)

    await elegirTenant(usuario, 1)
    await waitFor(() => expect(pendientes.has(1)).toBe(true))
    await elegirTenant(usuario, 2)
    await waitFor(() => expect(pendientes.has(2)).toBe(true))

    await act(async () => pendientes.get(2)!([dominioDelOtro]))
    expect(await screen.findByText('este.example.com')).toBeInTheDocument()

    await act(async () => pendientes.get(1)!([principalVerificado]))

    expect(screen.queryByText('tienda.sur.com')).not.toBeInTheDocument()
    expect(screen.getByText('este.example.com')).toBeInTheDocument()
    expect(screen.getByLabelText('Tenant')).toHaveValue('2')
  })

  it('bajo StrictMode la lectura de tenants más nueva gana sobre la repetida', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === '/plataforma/tenants' ? Promise.resolve([tenantUno, tenantDos]) : Promise.resolve([]),
    )
    render(
      <StrictMode>
        <Dominios />
      </StrictMode>,
    )

    await elegirTenant(usuario, 2)

    expect(await screen.findByText('Este tenant todavía no tiene dominios.')).toBeInTheDocument()
  })
})

describe('Dominios — alta', () => {
  it('agrega el host sin espacios con su marca de principal, refresca y limpia el formulario', async () => {
    const usuario = userEvent.setup()
    const { listas } = montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('tienda.sur.com')

    const nuevo = dominio({ id: 13, idTenant: 1, host: 'nuevo.sur.com', esPrincipal: true })
    apiPostMock.mockImplementation(() => {
      listas[1] = [principalVerificado, secundarioSinVerificar, nuevo]

      return Promise.resolve(nuevo)
    })

    const dialogo = await abrirAlta(usuario)
    await usuario.type(within(dialogo).getByLabelText('Host'), '  nuevo.sur.com ')
    await usuario.click(within(dialogo).getByLabelText('Principal'))
    await usuario.click(within(dialogo).getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByRole('row', { name: /nuevo\.sur\.com/ })).toBeInTheDocument()
    expect(apiPostMock).toHaveBeenCalledWith(RUTA_UNO, { host: 'nuevo.sur.com', esPrincipal: true })
    expect(screen.getByText(/Se agregó el dominio "nuevo\.sur\.com"/)).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    const reabierto = await abrirAlta(usuario)
    expect(within(reabierto).getByLabelText('Host')).toHaveValue('')
    expect(within(reabierto).getByLabelText('Principal')).not.toBeChecked()
  })

  it('el botón de agregar abre el formulario en un modal y no en la página', async () => {
    const usuario = userEvent.setup()
    montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('tienda.sur.com')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    const dialogo = await abrirAlta(usuario)

    expect(within(dialogo).getByLabelText('Host')).toBeInTheDocument()
  })

  it('cancelar descarta lo escrito sin tocar la API y el próximo alta abre vacío', async () => {
    const usuario = userEvent.setup()
    montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('tienda.sur.com')
    const dialogo = await abrirAlta(usuario)
    await usuario.type(within(dialogo).getByLabelText('Host'), 'nuevo.sur.com')
    await usuario.click(within(dialogo).getByLabelText('Principal'))

    await usuario.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
    const reabierto = await abrirAlta(usuario)
    expect(within(reabierto).getByLabelText('Host')).toHaveValue('')
    expect(within(reabierto).getByLabelText('Principal')).not.toBeChecked()
  })

  it('tras un alta exitosa el foco vuelve al botón de agregar', async () => {
    const usuario = userEvent.setup()
    montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('tienda.sur.com')
    apiPostMock.mockResolvedValue(dominio({ id: 13, idTenant: 1, host: 'nuevo.sur.com' }))
    const dialogo = await abrirAlta(usuario)
    await usuario.type(within(dialogo).getByLabelText('Host'), 'nuevo.sur.com')

    await usuario.click(within(dialogo).getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    await screen.findByText(/Se agregó el dominio "nuevo\.sur\.com"/)
    expect(screen.getByRole('button', { name: 'Agregar dominio' })).toHaveFocus()
  })

  it('Escape cierra el modal sin guardar y devuelve el foco al disparador', async () => {
    const usuario = userEvent.setup()
    montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('tienda.sur.com')
    const disparador = screen.getByRole('button', { name: 'Agregar dominio' })
    await abrirAlta(usuario)

    await usuario.keyboard('{Escape}')

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
    expect(disparador).toHaveFocus()
  })

  it('un host duplicado se explica junto al campo y el formulario conserva lo escrito', async () => {
    const usuario = userEvent.setup()
    montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('tienda.sur.com')
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'host_duplicado', 'texto crudo'))

    const dialogo = await abrirAlta(usuario)
    await usuario.type(within(dialogo).getByLabelText('Host'), 'www.sur.com')
    await usuario.click(within(dialogo).getByLabelText('Principal'))
    await usuario.click(within(dialogo).getByRole('button', { name: 'Guardar' }))

    await waitFor(() =>
      expect(within(dialogo).getByLabelText('Host')).toHaveAccessibleDescription('Ese host ya está cargado en otro dominio.'),
    )
    expect(within(dialogo).getByLabelText('Host')).toHaveValue('www.sur.com')
    expect(within(dialogo).getByLabelText('Principal')).toBeChecked()
    expect(within(dialogo).getByRole('button', { name: 'Guardar' })).toBeEnabled()
    expect(screen.getByRole('dialog', { name: 'Nuevo dominio' })).toBeInTheDocument()
    expect(llamadasA(apiGetMock, RUTA_UNO)).toHaveLength(1)
  })

  it('un host inválido se explica junto al campo', async () => {
    const usuario = userEvent.setup()
    montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('tienda.sur.com')
    apiPostMock.mockRejectedValue(new ErrorApi(400, 'host_invalido', 'texto crudo'))

    const dialogo = await abrirAlta(usuario)
    await usuario.type(within(dialogo).getByLabelText('Host'), 'https://x.com/ruta')
    await usuario.click(within(dialogo).getByRole('button', { name: 'Guardar' }))

    await waitFor(() =>
      expect(within(dialogo).getByLabelText('Host')).toHaveAccessibleDescription(/sin https:\/\/ ni rutas/),
    )
    expect(within(dialogo).getByLabelText('Host')).toHaveValue('https://x.com/ruta')
  })

  it('un fallo incierto del alta relee la lista y deja el mensaje a la vista', async () => {
    const usuario = userEvent.setup()
    const { listas } = montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('tienda.sur.com')
    const nuevo = dominio({ id: 13, idTenant: 1, host: 'nuevo.sur.com' })
    apiPostMock.mockImplementation(() => {
      listas[1] = [principalVerificado, secundarioSinVerificar, nuevo]

      return Promise.reject(new ErrorApi(503, 'resultado_incierto', 'No se pudo confirmar.'))
    })

    const dialogo = await abrirAlta(usuario)
    await usuario.type(within(dialogo).getByLabelText('Host'), 'nuevo.sur.com')
    await usuario.click(within(dialogo).getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByRole('row', { name: /nuevo\.sur\.com/ })).toBeInTheDocument()
    expect(llamadasA(apiGetMock, RUTA_UNO)).toHaveLength(2)
    expect(within(dialogo).getByLabelText('Host')).toHaveAccessibleDescription(/No se pudo agregar el dominio\. No se pudo confirmar\./)
  })

  it('si el alta commiteó pero el refresco falla, no se informa como fallida', async () => {
    const usuario = userEvent.setup()
    montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('tienda.sur.com')
    apiPostMock.mockResolvedValue(dominio({ id: 13, idTenant: 1, host: 'nuevo.sur.com' }))
    const original = apiGetMock.getMockImplementation()!
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === RUTA_UNO ? Promise.reject(new ErrorApi(500, 'error_interno', 'Falló.')) : original(ruta),
    )

    const dialogo = await abrirAlta(usuario)
    await usuario.type(within(dialogo).getByLabelText('Host'), 'nuevo.sur.com')
    await usuario.click(within(dialogo).getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText(/Se agregó el dominio "nuevo\.sur\.com".*no se pudo actualizar la vista/)).toBeInTheDocument()
    expect(screen.queryByText(/No se pudo agregar/)).not.toBeInTheDocument()
  })

  it('dos envíos del mismo tick salen como una sola request', async () => {
    const usuario = userEvent.setup()
    montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('tienda.sur.com')
    apiPostMock.mockReturnValue(new Promise(() => {}))
    const dialogo = await abrirAlta(usuario)
    await usuario.type(within(dialogo).getByLabelText('Host'), 'nuevo.sur.com')
    const boton = within(dialogo).getByRole('button', { name: 'Guardar' })

    act(() => {
      boton.click()
      boton.click()
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)
    await waitFor(() => expect(within(dialogo).getByRole('button', { name: 'Guardando…' })).toBeDisabled())
    expect(within(dialogo).getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    await usuario.keyboard('{Escape}')
    expect(screen.getByRole('dialog', { name: 'Nuevo dominio' })).toBeInTheDocument()
  })
})

describe('Dominios — verificación', () => {
  it('marca el dominio como verificado y la fila se actualiza con lo que devuelve el refresco', async () => {
    const usuario = userEvent.setup()
    const { listas } = montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('www.sur.com')
    const verificado = { ...secundarioSinVerificar, verificadoEn: '2026-10-12T12:00:00Z' }
    apiPostMock.mockImplementation(() => {
      listas[1] = [principalVerificado, verificado]

      return Promise.resolve(verificado)
    })

    await usuario.click(screen.getByRole('button', { name: 'Marcar verificado: www.sur.com' }))

    await waitFor(() => {
      const fila = within(screen.getByRole('row', { name: /www\.sur\.com/ }))
      expect(fila.getByText(/^Verificado el /)).toBeInTheDocument()
    })
    expect(apiPostMock).toHaveBeenCalledWith(`${RUTA_UNO}/12/verificar`)
    expect(screen.queryByRole('button', { name: /Marcar/ })).not.toBeInTheDocument()
    expect(screen.getByText('Se marcó como verificado "www.sur.com".')).toBeInTheDocument()
  })

  it('un rechazo se rinde y la fila sigue sin verificar', async () => {
    const usuario = userEvent.setup()
    montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('www.sur.com')
    apiPostMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'texto crudo'))

    await usuario.click(screen.getByRole('button', { name: 'Marcar verificado: www.sur.com' }))

    expect(await screen.findByText(/No se pudo marcar "www\.sur\.com" como verificado\./)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Marcar verificado: www.sur.com' })).toBeEnabled()
  })

  it('un 404 relee la lista, que ya no trae la fila, y el mensaje queda a la vista', async () => {
    const usuario = userEvent.setup()
    const { listas } = montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('www.sur.com')
    apiPostMock.mockImplementation(() => {
      listas[1] = [principalVerificado]

      return Promise.reject(new ErrorApi(404, 'no_encontrado', 'texto crudo'))
    })

    await usuario.click(screen.getByRole('button', { name: 'Marcar verificado: www.sur.com' }))

    await waitFor(() => expect(screen.queryByText('www.sur.com')).not.toBeInTheDocument())
    expect(llamadasA(apiGetMock, RUTA_UNO)).toHaveLength(2)
    expect(screen.getByText(/No se pudo marcar "www\.sur\.com" como verificado\./)).toBeInTheDocument()
  })

  it('dos clics del mismo tick salen como una sola request', async () => {
    const usuario = userEvent.setup()
    montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('www.sur.com')
    apiPostMock.mockReturnValue(new Promise(() => {}))
    const boton = screen.getByRole('button', { name: 'Marcar verificado: www.sur.com' })

    act(() => {
      boton.click()
      boton.click()
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  it('mientras la escritura está en vuelo toda la pantalla queda inerte', async () => {
    const usuario = userEvent.setup()
    montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('www.sur.com')
    const pendiente = diferida<DominioListado>()
    apiPostMock.mockReturnValue(pendiente.promesa)

    await usuario.click(screen.getByRole('button', { name: 'Marcar verificado: www.sur.com' }))

    expect(screen.getByLabelText('Tenant')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Agregar dominio' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Marcar verificado: www.sur.com' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Eliminar tienda.sur.com' })).toBeDisabled()

    await act(async () => pendiente.resolver(secundarioSinVerificar))

    await waitFor(() => expect(screen.getByLabelText('Tenant')).toBeEnabled())
  })
})

describe('Dominios — baja', () => {
  it('pide confirmación y recién al confirmar elimina; la fila desaparece con el refresco', async () => {
    const usuario = userEvent.setup()
    const { listas } = montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('www.sur.com')

    await usuario.click(screen.getByRole('button', { name: 'Eliminar www.sur.com' }))

    expect(screen.getByRole('alertdialog', { name: 'Confirmar eliminación' })).toBeInTheDocument()
    expect(screen.getByText(/su host queda libre/)).toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()

    listas[1] = [principalVerificado]
    await usuario.click(screen.getByRole('button', { name: 'Confirmar eliminación' }))

    await waitFor(() => expect(screen.queryByText('www.sur.com')).not.toBeInTheDocument())
    expect(apiDeleteMock).toHaveBeenCalledWith(`${RUTA_UNO}/12`)
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByText('Se eliminó el dominio "www.sur.com".')).toBeInTheDocument()
  })

  it('cancelar cierra la puerta sin tocar la API', async () => {
    const usuario = userEvent.setup()
    montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('www.sur.com')

    await usuario.click(screen.getByRole('button', { name: 'Eliminar www.sur.com' }))
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()
  })

  it('con la puerta abierta el resto de la pantalla queda inerte', async () => {
    const usuario = userEvent.setup()
    montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('www.sur.com')

    await usuario.click(screen.getByRole('button', { name: 'Eliminar www.sur.com' }))

    expect(screen.getByLabelText('Tenant')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Agregar dominio' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Marcar verificado: www.sur.com' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Eliminar tienda.sur.com' })).toBeDisabled()
  })

  it('un rechazo deja la puerta abierta con el motivo al lado', async () => {
    const usuario = userEvent.setup()
    montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('www.sur.com')
    apiDeleteMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'texto crudo'))

    await usuario.click(screen.getByRole('button', { name: 'Eliminar www.sur.com' }))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar eliminación' }))

    expect(await screen.findByText(/No se pudo dar de baja el dominio\./)).toBeInTheDocument()
    expect(screen.getByRole('alertdialog')).toBeInTheDocument()
  })

  it('un 404 relee la lista, que ya no trae la fila, y el motivo queda junto a la puerta', async () => {
    const usuario = userEvent.setup()
    const { listas } = montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('www.sur.com')
    apiDeleteMock.mockImplementation(() => {
      listas[1] = [principalVerificado]

      return Promise.reject(new ErrorApi(404, 'no_encontrado', 'texto crudo'))
    })

    await usuario.click(screen.getByRole('button', { name: 'Eliminar www.sur.com' }))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar eliminación' }))

    await waitFor(() => expect(llamadasA(apiGetMock, RUTA_UNO)).toHaveLength(2))
    await waitFor(() => expect(screen.queryByRole('row', { name: /www\.sur\.com/ })).not.toBeInTheDocument())
    expect(await screen.findByText(/No se pudo dar de baja el dominio\./)).toBeInTheDocument()
    expect(screen.getByRole('alertdialog')).toBeInTheDocument()
  })

  it('dos confirmaciones del mismo tick salen como un solo DELETE', async () => {
    const usuario = userEvent.setup()
    montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('www.sur.com')
    apiDeleteMock.mockReturnValue(new Promise(() => {}))
    await usuario.click(screen.getByRole('button', { name: 'Eliminar www.sur.com' }))
    const confirmar = screen.getByRole('button', { name: 'Confirmar eliminación' })

    act(() => {
      confirmar.click()
      confirmar.click()
    })

    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })
})

describe('Dominios — cada acción borra lo que dijo la anterior', () => {
  it('cancelar el modal borra el rechazo del alta: el próximo alta abre sin error', async () => {
    const usuario = userEvent.setup()
    montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('www.sur.com')
    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'host_duplicado', 'texto crudo'))
    const dialogo = await abrirAlta(usuario)
    await usuario.type(within(dialogo).getByLabelText('Host'), 'www.sur.com')
    await usuario.click(within(dialogo).getByRole('button', { name: 'Guardar' }))
    await waitFor(() =>
      expect(within(dialogo).getByLabelText('Host')).toHaveAccessibleDescription('Ese host ya está cargado en otro dominio.'),
    )

    await usuario.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))
    const reabierto = await abrirAlta(usuario)

    expect(within(reabierto).getByLabelText('Host')).not.toHaveAccessibleDescription()
  })

  it('agregar borra el aviso de éxito de una acción anterior', async () => {
    const usuario = userEvent.setup()
    montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('www.sur.com')
    await usuario.click(screen.getByRole('button', { name: 'Marcar verificado: www.sur.com' }))
    expect(await screen.findByText('Se marcó como verificado "www.sur.com".')).toBeInTheDocument()

    apiPostMock.mockReturnValue(new Promise(() => {}))
    const dialogo = await abrirAlta(usuario)
    await usuario.type(within(dialogo).getByLabelText('Host'), 'nuevo.sur.com')
    await usuario.click(within(dialogo).getByRole('button', { name: 'Guardar' }))

    expect(screen.queryByText('Se marcó como verificado "www.sur.com".')).not.toBeInTheDocument()
  })

  it('elegir otro tenant borra el aviso aunque su lista siga pendiente', async () => {
    const usuario = userEvent.setup()
    montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('www.sur.com')
    await usuario.click(screen.getByRole('button', { name: 'Marcar verificado: www.sur.com' }))
    expect(await screen.findByText('Se marcó como verificado "www.sur.com".')).toBeInTheDocument()
    const original = apiGetMock.getMockImplementation()!
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === '/plataforma/tenants/2/dominios' ? new Promise(() => {}) : original(ruta),
    )

    await elegirTenant(usuario, 2)

    expect(screen.queryByText('Se marcó como verificado "www.sur.com".')).not.toBeInTheDocument()
  })

  it('abrir la puerta de baja borra el aviso de éxito de una acción anterior', async () => {
    const usuario = userEvent.setup()
    montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('www.sur.com')
    await usuario.click(screen.getByRole('button', { name: 'Marcar verificado: www.sur.com' }))
    expect(await screen.findByText('Se marcó como verificado "www.sur.com".')).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Eliminar www.sur.com' }))

    expect(screen.getByRole('alertdialog')).toBeInTheDocument()
    expect(screen.queryByText('Se marcó como verificado "www.sur.com".')).not.toBeInTheDocument()
  })

  it('reintentar la baja borra el rechazo anterior', async () => {
    const usuario = userEvent.setup()
    montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('www.sur.com')
    apiDeleteMock.mockRejectedValueOnce(new ErrorApi(404, 'no_encontrado', 'texto crudo'))
    await usuario.click(screen.getByRole('button', { name: 'Eliminar www.sur.com' }))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar eliminación' }))
    expect(await screen.findByText(/No se pudo dar de baja el dominio\./)).toBeInTheDocument()

    apiDeleteMock.mockReturnValue(new Promise(() => {}))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar eliminación' }))

    expect(screen.queryByText(/No se pudo dar de baja el dominio\./)).not.toBeInTheDocument()
  })

  it('cancelar la baja borra el rechazo que quedó junto a la puerta', async () => {
    const usuario = userEvent.setup()
    montar()
    await elegirTenant(usuario, 1)
    await screen.findByText('www.sur.com')
    apiDeleteMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'texto crudo'))
    await usuario.click(screen.getByRole('button', { name: 'Eliminar www.sur.com' }))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar eliminación' }))
    expect(await screen.findByText(/No se pudo dar de baja el dominio\./)).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByText(/No se pudo dar de baja el dominio\./)).not.toBeInTheDocument()
  })
})

describe('Dominios — orden y filtros por columna', () => {
  const zeta = dominio({ id: 31, idTenant: 1, host: 'zeta.com', verificadoEn: '2026-10-11T12:00:00Z' })
  const alfa = dominio({ id: 32, idTenant: 1, host: 'alfa.com', esPrincipal: true })
  const medio = dominio({ id: 33, idTenant: 1, host: 'medio.com', verificadoEn: '2026-12-01T12:00:00Z' })

  /** Las dos primeras filas son el encabezado y los filtros; la celda 0 de cada fila de datos es el host. */
  function hostsEnPantalla() {
    return screen
      .getAllByRole('row')
      .slice(2)
      .map((f) => within(f).getAllByRole('cell')[0]?.textContent)
  }

  async function montarConDominios(usuario: ReturnType<typeof userEvent.setup>) {
    const montado = montar({ 1: [zeta, alfa, medio], 2: [dominioDelOtro] })
    await elegirTenant(usuario, 1)
    await screen.findByText('zeta.com')

    return montado
  }

  it('la fila de filtros tiene una celda por columna del encabezado y limpiar queda bajo Acciones', async () => {
    const usuario = userEvent.setup()
    await montarConDominios(usuario)

    const [encabezados, filtros] = screen.getAllByRole('row')
    const celdasDeEncabezado = within(encabezados).getAllByRole('columnheader')
    const celdasDeFiltros = within(filtros).getAllByRole('columnheader')

    expect(celdasDeFiltros).toHaveLength(celdasDeEncabezado.length)
    expect(celdasDeEncabezado.at(-1)).toHaveTextContent('Acciones')
    expect(within(celdasDeFiltros.at(-1)!).getByRole('button', { name: 'Limpiar filtros' })).toBeInTheDocument()
  })

  it('cada columna de datos se puede ordenar y Acciones no', async () => {
    const usuario = userEvent.setup()
    await montarConDominios(usuario)

    for (const nombre of ['Host', 'Principal', 'Verificación']) {
      expect(screen.getByRole('button', { name: nombre })).toBeInTheDocument()
    }
    expect(screen.getByRole('columnheader', { name: 'Acciones' })).not.toHaveAttribute('aria-sort')
  })

  it('el host se ordena ascendente, descendente y vuelve al orden del servidor', async () => {
    const usuario = userEvent.setup()
    await montarConDominios(usuario)
    const boton = screen.getByRole('button', { name: 'Host' })
    const encabezado = screen.getByRole('columnheader', { name: 'Host' })
    expect(hostsEnPantalla()).toEqual(['zeta.com', 'alfa.com', 'medio.com'])

    await usuario.click(boton)
    expect(hostsEnPantalla()).toEqual(['alfa.com', 'medio.com', 'zeta.com'])
    expect(encabezado).toHaveAttribute('aria-sort', 'ascending')

    await usuario.click(boton)
    expect(hostsEnPantalla()).toEqual(['zeta.com', 'medio.com', 'alfa.com'])
    expect(encabezado).toHaveAttribute('aria-sort', 'descending')

    await usuario.click(boton)
    expect(hostsEnPantalla()).toEqual(['zeta.com', 'alfa.com', 'medio.com'])
    expect(encabezado).toHaveAttribute('aria-sort', 'none')
  })

  it('la verificación se ordena por fecha (11/10 antes que 1/12) y lo sin verificar va al final en ambas direcciones', async () => {
    const usuario = userEvent.setup()
    await montarConDominios(usuario)
    const boton = screen.getByRole('button', { name: 'Verificación' })

    await usuario.click(boton)
    expect(hostsEnPantalla()).toEqual(['zeta.com', 'medio.com', 'alfa.com'])

    await usuario.click(boton)
    expect(hostsEnPantalla()).toEqual(['medio.com', 'zeta.com', 'alfa.com'])
  })

  it('la columna principal se ordena por el texto mostrado', async () => {
    const usuario = userEvent.setup()
    await montarConDominios(usuario)

    await usuario.click(screen.getByRole('button', { name: 'Principal' }))

    expect(hostsEnPantalla()).toEqual(['zeta.com', 'medio.com', 'alfa.com'])
  })

  it('el filtro de host ignora mayúsculas y el de verificación distingue verificados de sin verificar', async () => {
    const usuario = userEvent.setup()
    await montarConDominios(usuario)

    await usuario.type(screen.getByRole('searchbox', { name: 'Filtrar por host' }), 'ALFA')
    expect(hostsEnPantalla()).toEqual(['alfa.com'])

    await usuario.click(screen.getByRole('button', { name: 'Limpiar filtros' }))
    await usuario.type(screen.getByRole('searchbox', { name: 'Filtrar por verificación' }), 'sin verificar')
    expect(hostsEnPantalla()).toEqual(['alfa.com'])

    await usuario.clear(screen.getByRole('searchbox', { name: 'Filtrar por verificación' }))
    await usuario.type(screen.getByRole('searchbox', { name: 'Filtrar por verificación' }), 'verificado')
    expect(hostsEnPantalla()).toEqual(['zeta.com', 'medio.com'])
  })

  it('el filtro de principal es un select y se combina con el de texto; limpiar los quita', async () => {
    const usuario = userEvent.setup()
    await montarConDominios(usuario)

    await usuario.selectOptions(screen.getByRole('combobox', { name: 'Filtrar por principal' }), 'Principal')
    expect(hostsEnPantalla()).toEqual(['alfa.com'])

    await usuario.selectOptions(screen.getByRole('combobox', { name: 'Filtrar por principal' }), 'No')
    expect(hostsEnPantalla()).toEqual(['zeta.com', 'medio.com'])

    await usuario.type(screen.getByRole('searchbox', { name: 'Filtrar por host' }), 'alfa')
    expect(screen.getByText('Ningún registro coincide con los filtros.')).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Limpiar filtros' }))
    expect(hostsEnPantalla()).toEqual(['zeta.com', 'alfa.com', 'medio.com'])
    expect(screen.getByRole('searchbox', { name: 'Filtrar por host' })).toHaveValue('')
  })

  it('el campo de filtro sigue montado y con su valor mientras la lista se recarga tras verificar', async () => {
    const usuario = userEvent.setup()
    const refresco = diferida<DominioListado[]>()
    let lecturas = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/plataforma/tenants') return Promise.resolve([tenantUno, tenantDos])
      if (ruta === RUTA_UNO) return ++lecturas === 1 ? Promise.resolve([zeta, alfa, medio]) : refresco.promesa

      return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
    })
    render(<Dominios />)
    await elegirTenant(usuario, 1)
    await screen.findByText('zeta.com')
    await usuario.type(screen.getByRole('searchbox', { name: 'Filtrar por host' }), 'a')
    const campo = screen.getByRole('searchbox', { name: 'Filtrar por host' })

    await usuario.click(screen.getByRole('button', { name: 'Marcar verificado: alfa.com' }))

    expect(await screen.findByText('Se marcó como verificado "alfa.com".')).toBeInTheDocument()
    expect(screen.getByRole('searchbox', { name: 'Filtrar por host' })).toBe(campo)
    expect(campo).toHaveValue('a')

    await act(async () => {
      refresco.resolver([zeta, { ...alfa, verificadoEn: '2026-11-05T12:00:00Z' }, medio])
      await refresco.promesa
    })

    await waitFor(() => expect(screen.queryByRole('button', { name: 'Marcar verificado: alfa.com' })).not.toBeInTheDocument())
    expect(screen.getByRole('searchbox', { name: 'Filtrar por host' })).toBe(campo)
    expect(campo).toHaveValue('a')
    expect(hostsEnPantalla()).toEqual(['zeta.com', 'alfa.com'])
  })

  it('al elegir otro tenant los filtros del anterior no quedan aplicados', async () => {
    const usuario = userEvent.setup()
    await montarConDominios(usuario)
    await usuario.type(screen.getByRole('searchbox', { name: 'Filtrar por host' }), 'zeta')

    await elegirTenant(usuario, 2)
    await screen.findByText('este.example.com')

    expect(screen.getByRole('searchbox', { name: 'Filtrar por host' })).toHaveValue('')
    expect(hostsEnPantalla()).toEqual(['este.example.com'])
  })

  it('con la puerta de baja abierta los filtros quedan inertes', async () => {
    const usuario = userEvent.setup()
    await montarConDominios(usuario)

    await usuario.click(screen.getByRole('button', { name: 'Eliminar zeta.com' }))

    expect(screen.getByRole('searchbox', { name: 'Filtrar por host' })).toBeDisabled()
    expect(screen.getByRole('combobox', { name: 'Filtrar por principal' })).toBeDisabled()
  })
})
