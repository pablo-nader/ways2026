import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { PantallaDeVinculacion } from './PantallaDeVinculacion'
import { ErrorApi } from '../api/cliente'
import { ROL } from '../api/tipos'
import type { PuntoVentaListado, UsuarioAutenticado } from '../api/tipos'

const apiPostMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: vi.fn(),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown?])),
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

const listarPuntosVentaMock = vi.fn()
vi.mock('../api/organizacion', () => ({
  clienteDeOrganizacion: { listarPuntosVenta: (...args: unknown[]) => listarPuntosVentaMock(...args) },
}))

const vincularMock = vi.fn()
vi.mock('../api/dispositivos', () => ({
  clienteDeDispositivos: { vincular: (...args: unknown[]) => vincularMock(...args) },
}))

function usuarioFixture(sobrescribir: Partial<UsuarioAutenticado> = {}): UsuarioAutenticado {
  return {
    id: 1,
    usuario: 'admin',
    mail: 'admin@ways.test',
    rolId: ROL.Admin,
    rol: 'Admin',
    ultimaConexion: null,
    idTenant: 1,
    ...sobrescribir,
  }
}

function puntoVentaFixture(sobrescribir: Partial<PuntoVentaListado> = {}): PuntoVentaListado {
  return {
    id: 7,
    idTenant: 1,
    idEmpresa: 3,
    nombre: 'Local Centro',
    domicilio: null,
    horario: null,
    whatsapp: null,
    instagram: null,
    facebook: null,
    web: null,
    nombreTenant: 'Tenant Demo',
    razonSocialEmpresa: 'Empresa Demo',
    ...sobrescribir,
  }
}

beforeEach(() => {
  apiPostMock.mockReset()
  listarPuntosVentaMock.mockReset()
  vincularMock.mockReset()
})

async function iniciarSesionComo(usuario: UsuarioAutenticado) {
  apiPostMock.mockImplementation((ruta: string) => (ruta === '/auth/login' ? Promise.resolve(usuario) : Promise.resolve(undefined)))
  await userEvent.type(screen.getByPlaceholderText('Correo electrónico'), usuario.mail)
  await userEvent.type(screen.getByPlaceholderText('Contraseña'), 'secreta123')
  await userEvent.click(screen.getByRole('button', { name: 'Continuar' }))
}

describe('PantallaDeVinculacion', () => {
  it('un usuario no-admin ve el error y la sesión se cierra, sin pasar al paso de elegir PV', async () => {
    const alVinculado = vi.fn()
    render(<PantallaDeVinculacion alVinculado={alVinculado} />)

    await iniciarSesionComo(usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor' }))

    expect(await screen.findByText('Se necesita un usuario administrador para vincular el dispositivo.')).toBeInTheDocument()
    expect(screen.queryByLabelText('Punto de venta')).not.toBeInTheDocument()
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/auth/logout'))
  })

  it('un Admin avanza al paso de elegir PV + nombre del equipo', async () => {
    listarPuntosVentaMock.mockResolvedValue([puntoVentaFixture(), puntoVentaFixture({ id: 8, nombre: 'Sucursal Norte' })])
    render(<PantallaDeVinculacion alVinculado={vi.fn()} />)

    await iniciarSesionComo(usuarioFixture())

    expect(await screen.findByLabelText('Punto de venta')).toBeInTheDocument()
    expect(screen.getByRole('option', { name: 'Local Centro' })).toBeInTheDocument()
    expect(screen.getByRole('option', { name: 'Sucursal Norte' })).toBeInTheDocument()
  })

  it('vincular envía idPuntoVenta + nombre, cierra la sesión de admin y avisa con el dispositivo', async () => {
    listarPuntosVentaMock.mockResolvedValue([puntoVentaFixture()])
    const dispositivo = {
      id: 1,
      nombre: 'Caja 1',
      idPuntoVenta: 7,
      puntoVenta: { numero: 1, nombre: 'Local Centro' },
      empresa: { nombre: 'Empresa Demo' },
    }
    vincularMock.mockResolvedValue(dispositivo)
    const alVinculado = vi.fn()
    render(<PantallaDeVinculacion alVinculado={alVinculado} />)

    await iniciarSesionComo(usuarioFixture())
    await screen.findByLabelText('Punto de venta')

    await userEvent.selectOptions(screen.getByLabelText('Punto de venta'), 'Local Centro')
    await userEvent.type(screen.getByLabelText('Nombre del equipo'), 'Caja 1')
    await userEvent.click(screen.getByRole('button', { name: 'Vincular' }))

    await waitFor(() => expect(vincularMock).toHaveBeenCalledWith({ idPuntoVenta: 7, nombre: 'Caja 1' }))
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/auth/logout'))
    await waitFor(() => expect(alVinculado).toHaveBeenCalledWith(dispositivo))
  })

  it('doble click en "Continuar" dispara exactamente un POST de login (react-async-state regla 9)', async () => {
    let resolverLogin: (usuario: UsuarioAutenticado) => void = () => {}
    const pendiente = new Promise<UsuarioAutenticado>((resolve) => {
      resolverLogin = resolve
    })
    apiPostMock.mockImplementation((ruta: string) => (ruta === '/auth/login' ? pendiente : Promise.resolve(undefined)))
    listarPuntosVentaMock.mockResolvedValue([puntoVentaFixture()])
    render(<PantallaDeVinculacion alVinculado={vi.fn()} />)

    await userEvent.type(screen.getByPlaceholderText('Correo electrónico'), 'admin@ways.test')
    await userEvent.type(screen.getByPlaceholderText('Contraseña'), 'secreta123')
    const boton = screen.getByRole('button', { name: 'Continuar' })
    fireEvent.click(boton)
    fireEvent.click(boton)

    resolverLogin(usuarioFixture())
    await waitFor(() => expect(apiPostMock.mock.calls.filter((c) => c[0] === '/auth/login')).toHaveLength(1))
  })

  it('si vincular falla, muestra el error y no avisa nada', async () => {
    listarPuntosVentaMock.mockResolvedValue([puntoVentaFixture()])
    vincularMock.mockRejectedValue(new ErrorApi(409, 'punto_de_venta_ya_vinculado', 'El punto de venta ya tiene un dispositivo vinculado.'))
    const alVinculado = vi.fn()
    render(<PantallaDeVinculacion alVinculado={alVinculado} />)

    await iniciarSesionComo(usuarioFixture())
    await screen.findByLabelText('Punto de venta')
    await userEvent.selectOptions(screen.getByLabelText('Punto de venta'), 'Local Centro')
    await userEvent.type(screen.getByLabelText('Nombre del equipo'), 'Caja 1')
    await userEvent.click(screen.getByRole('button', { name: 'Vincular' }))

    expect(await screen.findByText('El punto de venta ya tiene un dispositivo vinculado.')).toBeInTheDocument()
    expect(alVinculado).not.toHaveBeenCalled()
  })
})
