import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { LoginDeDispositivo } from './LoginDeDispositivo'
import { ErrorApi } from '../api/cliente'
import { ROL } from '../api/tipos'
import type { PuntoVentaListado, UsuarioAutenticado } from '../api/tipos'
import type { DispositivoActual } from '../api/dispositivos'

vi.mock('../api/cliente', () => ({
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

const iniciarSesionMock = vi.fn()
vi.mock('../api/dispositivos', () => ({
  clienteDeDispositivos: { iniciarSesion: (...args: unknown[]) => iniciarSesionMock(...args) },
}))

const listarPuntosVentaMock = vi.fn()
vi.mock('../api/organizacion', () => ({
  clienteDeOrganizacion: { listarPuntosVenta: (...args: unknown[]) => listarPuntosVentaMock(...args) },
}))

const DISPOSITIVO: DispositivoActual = {
  id: 1,
  nombre: 'Caja 1',
  idPuntoVenta: 7,
  puntoVenta: { numero: 1, nombre: 'Local Centro' },
  empresa: { nombre: 'Empresa Demo' },
}

function usuarioFixture(sobrescribir: Partial<UsuarioAutenticado> = {}): UsuarioAutenticado {
  return {
    id: 4,
    usuario: 'jperez',
    mail: 'jperez@ways.test',
    rolId: ROL.Vendedor,
    rol: 'Vendedor',
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
  iniciarSesionMock.mockReset()
  listarPuntosVentaMock.mockReset()
})

async function completarFormulario() {
  await userEvent.type(screen.getByPlaceholderText('Usuario'), 'jperez')
  await userEvent.type(screen.getByPlaceholderText('Contraseña'), 'secreta123')
  await userEvent.click(screen.getByRole('button', { name: 'Ingresar' }))
}

describe('LoginDeDispositivo', () => {
  it('muestra la empresa, el PV y el nombre del dispositivo', () => {
    render(<LoginDeDispositivo dispositivo={DISPOSITIVO} onSesion={vi.fn()} onDispositivoInvalido={vi.fn()} />)

    expect(screen.getByText('Empresa Demo')).toBeInTheDocument()
    expect(screen.getByText(/PV 1 — Local Centro · Caja 1/)).toBeInTheDocument()
  })

  it('envía usuario + password a login-dispositivo y, con el PV resuelto, avisa onSesion', async () => {
    iniciarSesionMock.mockResolvedValue(usuarioFixture())
    listarPuntosVentaMock.mockResolvedValue([puntoVentaFixture()])
    const onSesion = vi.fn()
    render(<LoginDeDispositivo dispositivo={DISPOSITIVO} onSesion={onSesion} onDispositivoInvalido={vi.fn()} />)

    await completarFormulario()

    expect(iniciarSesionMock).toHaveBeenCalledWith({ usuario: 'jperez', password: 'secreta123' })
    await waitFor(() => expect(onSesion).toHaveBeenCalledWith(usuarioFixture(), puntoVentaFixture()))
  })

  it('si el PV del dispositivo ya no existe en la lista, muestra un error y no avisa onSesion', async () => {
    iniciarSesionMock.mockResolvedValue(usuarioFixture())
    listarPuntosVentaMock.mockResolvedValue([puntoVentaFixture({ id: 999 })])
    const onSesion = vi.fn()
    render(<LoginDeDispositivo dispositivo={DISPOSITIVO} onSesion={onSesion} onDispositivoInvalido={vi.fn()} />)

    await completarFormulario()

    expect(await screen.findByText('El punto de venta de este dispositivo ya no existe. Contactá a un administrador.')).toBeInTheDocument()
    expect(onSesion).not.toHaveBeenCalled()
  })

  it('con dispositivo_no_vinculado (revocado entre que se cargó y este intento) vuelve a la pantalla de vinculación', async () => {
    iniciarSesionMock.mockRejectedValue(new ErrorApi(404, 'dispositivo_no_vinculado', 'El dispositivo no está vinculado.'))
    const onDispositivoInvalido = vi.fn()
    render(<LoginDeDispositivo dispositivo={DISPOSITIVO} onSesion={vi.fn()} onDispositivoInvalido={onDispositivoInvalido} />)

    await completarFormulario()

    await waitFor(() => expect(onDispositivoInvalido).toHaveBeenCalledTimes(1))
  })

  it('con credenciales inválidas muestra el mensaje y limpia la contraseña', async () => {
    iniciarSesionMock.mockRejectedValue(new ErrorApi(401, 'credenciales_invalidas', 'Usuario o contraseña incorrectos.'))
    render(<LoginDeDispositivo dispositivo={DISPOSITIVO} onSesion={vi.fn()} onDispositivoInvalido={vi.fn()} />)

    await completarFormulario()

    expect(await screen.findByText('Usuario o contraseña incorrectos.')).toBeInTheDocument()
    expect(screen.getByPlaceholderText('Contraseña')).toHaveValue('')
  })

  it('doble click en "Ingresar" dispara exactamente un login-dispositivo (react-async-state regla 9)', async () => {
    let resolver: (usuario: UsuarioAutenticado) => void = () => {}
    const pendiente = new Promise<UsuarioAutenticado>((resolve) => {
      resolver = resolve
    })
    iniciarSesionMock.mockReturnValue(pendiente)
    listarPuntosVentaMock.mockResolvedValue([puntoVentaFixture()])
    render(<LoginDeDispositivo dispositivo={DISPOSITIVO} onSesion={vi.fn()} onDispositivoInvalido={vi.fn()} />)

    await userEvent.type(screen.getByPlaceholderText('Usuario'), 'jperez')
    await userEvent.type(screen.getByPlaceholderText('Contraseña'), 'secreta123')
    const boton = screen.getByRole('button', { name: 'Ingresar' })
    fireEvent.click(boton)
    fireEvent.click(boton)

    resolver(usuarioFixture())
    await waitFor(() => expect(iniciarSesionMock).toHaveBeenCalledTimes(1))
  })
})
