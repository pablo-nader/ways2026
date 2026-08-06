import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Transferencias } from './Transferencias'
import { RutaProtegida } from '../auth/RutaProtegida'
import { ErrorApi } from '../api/cliente'
import { ROL } from '../api/tipos'
import type { ArticuloListado, PuntoVentaListado, ResultadoTransferencia, UsuarioAutenticado } from '../api/tipos'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
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

let usuarioActual: UsuarioAutenticado | null = usuarioFixture()

vi.mock('../auth/useAuth', () => ({
  useAuth: () => ({ usuario: usuarioActual, cargando: false, iniciarSesion: vi.fn(), cerrarSesion: vi.fn() }),
}))

function puntoVentaFixture(sobrescribir: Partial<PuntoVentaListado> = {}): PuntoVentaListado {
  return {
    id: 1,
    idTenant: 1,
    idEmpresa: 1,
    nombre: 'Casa Central',
    domicilio: null,
    horario: null,
    whatsapp: null,
    instagram: null,
    facebook: null,
    web: null,
    ...sobrescribir,
  }
}

function articuloFixture(sobrescribir: Partial<ArticuloListado> = {}): ArticuloListado {
  return {
    id: 10,
    codigoInterno: 'ART-10',
    nombre: 'Fideos 500g',
    descripcion: null,
    idArea: 1,
    idCategoria: null,
    idMarca: null,
    idGrupo: null,
    idProveedorHabitual: null,
    idAlicuotaIva: 3,
    unidadVenta: 'Unidad',
    unidadesPorBulto: null,
    esProducto: true,
    costoLista: null,
    descuentoProveedor: null,
    costoNominal: null,
    disponibleParaTodas: true,
    idsEmpresas: [],
    activo: true,
    ...sobrescribir,
  }
}

function renderTransferencias() {
  return render(<Transferencias />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

function renderTransferenciasProtegido() {
  return render(
    <MemoryRouter initialEntries={['/stock/transferencias']}>
      <Routes>
        <Route
          path="/stock/transferencias"
          element={
            <RutaProtegida rolesPermitidos={[ROL.Admin]}>
              <Transferencias />
            </RutaProtegida>
          }
        />
        <Route path="/" element={<div>Inicio (redirigido)</div>} />
      </Routes>
    </MemoryRouter>,
  )
}

function mockearPuntosVenta(puntos: PuntoVentaListado[] = [puntoVentaFixture({ id: 1, nombre: 'Casa Central' }), puntoVentaFixture({ id: 2, nombre: 'Sucursal Norte' })]) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/puntos-venta') return Promise.resolve(puntos)
    if (ruta.startsWith('/articulos')) return Promise.resolve({ items: [articuloFixture()], total: 1, pagina: 1, tamanio: 25 })
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

async function completarLinea(usuario: ReturnType<typeof userEvent.setup>) {
  await usuario.type(screen.getByPlaceholderText('Buscar artículo…'), 'fideos')
  await screen.findByText('ART-10 — Fideos 500g')
  await usuario.click(screen.getByText('ART-10 — Fideos 500g'))
  await usuario.type(screen.getByLabelText('Cantidad'), '8')
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  usuarioActual = usuarioFixture()
})

describe('Transferencias — flujo feliz', () => {
  it('transferir: doble click manda un solo POST y muestra los movimientos espejados', async () => {
    mockearPuntosVenta()
    let resolverTransferir: (valor: ResultadoTransferencia) => void = () => {}
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/stock/transferencias') return new Promise((resolve) => (resolverTransferir = resolve))
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    const usuario = userEvent.setup()

    renderTransferencias()
    await screen.findByLabelText('Origen')

    await usuario.selectOptions(screen.getByLabelText('Origen'), '1')
    await usuario.selectOptions(screen.getByLabelText('Destino'), '2')
    await usuario.type(screen.getByLabelText('Observaciones'), 'Reposición de sucursal')
    await completarLinea(usuario)
    await usuario.click(screen.getByLabelText(/Confirmo que quiero mover este stock/))

    const boton = screen.getByRole('button', { name: 'Transferir' })
    await usuario.click(boton)
    await usuario.click(boton)

    resolverTransferir({
      idPuntoVentaOrigen: 1,
      idPuntoVentaDestino: 2,
      lineas: [{ idArticulo: 10, cantidadOrigen: 12, cantidadDestino: 13 }],
    })

    expect(await screen.findByText(/Transferencia registrada: Casa Central → Sucursal Norte/)).toBeInTheDocument()
    expect(screen.getByText('12')).toBeInTheDocument()
    expect(screen.getByText('13')).toBeInTheDocument()

    const llamadas = apiPostMock.mock.calls.filter((call: unknown[]) => call[0] === '/stock/transferencias')
    expect(llamadas).toHaveLength(1)
    const [, cuerpo] = llamadas[0] as [string, Record<string, unknown>]
    expect(cuerpo).toEqual({
      idPuntoVentaOrigen: 1,
      idPuntoVentaDestino: 2,
      observaciones: 'Reposición de sucursal',
      lineas: [{ idArticulo: 10, cantidad: 8 }],
    })
  })
})

describe('Transferencias — validaciones cliente', () => {
  it('origen igual a destino bloquea el botón y avisa antes de mandar nada', async () => {
    mockearPuntosVenta()
    const usuario = userEvent.setup()

    renderTransferencias()
    await screen.findByLabelText('Origen')

    await usuario.selectOptions(screen.getByLabelText('Origen'), '1')
    await usuario.selectOptions(screen.getByLabelText('Destino'), '1')

    expect(screen.getByText('El origen y el destino tienen que ser puntos de venta distintos.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Transferir' })).toBeDisabled()
  })

  it('un artículo repetido se marca en rojo y bloquea el botón', async () => {
    mockearPuntosVenta()
    const usuario = userEvent.setup()

    renderTransferencias()
    await screen.findByLabelText('Origen')
    await usuario.selectOptions(screen.getByLabelText('Origen'), '1')
    await usuario.selectOptions(screen.getByLabelText('Destino'), '2')
    await usuario.type(screen.getByLabelText('Observaciones'), 'obs')
    await completarLinea(usuario)

    await usuario.click(screen.getByRole('button', { name: '+ Agregar línea' }))
    const buscadores = screen.getAllByPlaceholderText('Buscar artículo…')
    await usuario.type(buscadores[1], 'fideos')
    await screen.findByText('ART-10 — Fideos 500g')
    await usuario.click(screen.getByText('ART-10 — Fideos 500g'))
    const cantidades = screen.getAllByLabelText('Cantidad')
    await usuario.type(cantidades[1], '3')

    expect(screen.getAllByText('Artículo repetido en la transferencia.')).toHaveLength(2)
    await usuario.click(screen.getByLabelText(/Confirmo que quiero mover este stock/))
    expect(screen.getByRole('button', { name: 'Transferir' })).toBeDisabled()
  })
})

describe('Transferencias — errores del servidor', () => {
  it('el 409 stock_insuficiente_para_transferencia se muestra tal cual, nombrando el artículo', async () => {
    mockearPuntosVenta()
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/stock/transferencias') {
        return Promise.reject(
          new ErrorApi(409, 'stock_insuficiente_para_transferencia', 'No hay stock suficiente del artículo 10 en el punto de venta de origen para transferir.'),
        )
      }
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    const usuario = userEvent.setup()

    renderTransferencias()
    await screen.findByLabelText('Origen')
    await usuario.selectOptions(screen.getByLabelText('Origen'), '1')
    await usuario.selectOptions(screen.getByLabelText('Destino'), '2')
    await usuario.type(screen.getByLabelText('Observaciones'), 'obs')
    await completarLinea(usuario)
    await usuario.click(screen.getByLabelText(/Confirmo que quiero mover este stock/))
    await usuario.click(screen.getByRole('button', { name: 'Transferir' }))

    expect(
      await screen.findByText('No hay stock suficiente del artículo 10 en el punto de venta de origen para transferir.'),
    ).toBeInTheDocument()
  })

  it('un fallo al cargar puntos de venta muestra un aviso y bloquea el envío', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/puntos-venta') return Promise.reject(new Error('caído'))
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })

    renderTransferencias()

    expect(await screen.findByText(/No se pudieron cargar los puntos de venta\./)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Transferir' })).toBeDisabled()
  })
})

describe('Transferencias — role gating', () => {
  it('un Vendedor es redirigido a "/" (ruta Admin-only, sin contraparte de lectura)', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor' })
    mockearPuntosVenta()

    renderTransferenciasProtegido()

    await waitFor(() => expect(screen.getByText('Inicio (redirigido)')).toBeInTheDocument())
  })

  it('un Admin llega a la pantalla', async () => {
    mockearPuntosVenta()
    renderTransferenciasProtegido()

    await screen.findByLabelText('Origen')
    expect(screen.queryByText('Inicio (redirigido)')).not.toBeInTheDocument()
  })
})
