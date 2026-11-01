import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Sucursales } from './Sucursales'
import { ErrorApi } from '../api/cliente'
import { ROL } from '../api/tipos'
import type { EmpresaListado, SucursalListado, UsuarioAutenticado } from '../api/tipos'

const apiGetMock = vi.fn()
const apiPutMock = vi.fn()
const apiPostMock = vi.fn()
const apiDeleteMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown])),
    put: (...args: unknown[]) => apiPutMock(...(args as [string, unknown])),
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

function usuarioFixture(sobrescribir: Partial<UsuarioAutenticado> = {}): UsuarioAutenticado {
  return {
    id: 4,
    usuario: 'admin',
    mail: 'admin@ways.test',
    rolId: ROL.Admin,
    rol: 'Admin',
    ultimaConexion: null,
    idTenant: 2,
    ...sobrescribir,
  }
}

let usuarioActual: UsuarioAutenticado | null = usuarioFixture()

vi.mock('../auth/useAuth', () => ({
  useAuth: () => ({ usuario: usuarioActual, cargando: false, iniciarSesion: vi.fn(), cerrarSesion: vi.fn() }),
}))

function sucursalFixture(sobrescribir: Partial<SucursalListado> = {}): SucursalListado {
  return {
    id: 500,
    idTenant: 2,
    idEmpresa: 20,
    nombre: 'Sucursal Centro',
    razonSocialEmpresa: 'Sur SRL',
    cantidadPuntosVenta: 2,
    ...sobrescribir,
  }
}

const DIRECCION_NULA = {
  calle: null,
  altura: null,
  ciudad: null,
  provincia: null,
  codigoPostal: null,
  latitud: null,
  longitud: null,
  referencia: null,
}

const centro = sucursalFixture()
const deposito = sucursalFixture({ id: 501, nombre: 'Depósito', cantidadPuntosVenta: 0 })
const empresaSur: EmpresaListado = {
  id: 20,
  idTenant: 2,
  razonSocial: 'Sur SRL',
  nombreFantasia: null,
  cuit: null,
  nombreTenant: 'Comercio Sur',
  alicuotaPercepcionIibb: null,
  alicuotaPercepcionIva: null,
}

function montar(items: SucursalListado[] = [centro, deposito]) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/sucursales') return Promise.resolve(items)
    if (ruta === '/empresas') return Promise.resolve([empresaSur])

    return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
  })

  return render(<Sucursales />)
}

async function esperarFilas() {
  await waitFor(() => expect(screen.getByText('Sucursal Centro')).toBeInTheDocument())
}

function botonDeFila(nombreFila: string, boton: 'Editar' | 'Eliminar') {
  return within(screen.getByRole('row', { name: new RegExp(nombreFila) })).getByRole('button', { name: boton })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPutMock.mockReset()
  apiPostMock.mockReset()
  apiDeleteMock.mockReset()
  usuarioActual = usuarioFixture()
})

describe('Sucursales — lectura', () => {
  it('rinde empresa, nombre y cantidad de puntos de venta', async () => {
    montar()
    await esperarFilas()

    const celdas = within(screen.getByRole('row', { name: /Sucursal Centro/ })).getAllByRole('cell')
    expect(celdas.map((c) => c.textContent).slice(0, 4)).toEqual(['0500', 'Sur SRL', 'Sucursal Centro', '2'])
  })

  it('sin sucursales dice que no hay, no que los filtros no coinciden', async () => {
    montar([])

    expect(await screen.findByText('No hay sucursales cargadas.')).toBeInTheDocument()
  })

  it('un fallo de la lectura muestra el error', async () => {
    apiGetMock.mockRejectedValue(new ErrorApi(500, 'error_interno', 'Se cayó la lectura.'))
    render(<Sucursales />)

    expect(await screen.findByText('Se cayó la lectura.')).toBeInTheDocument()
  })

  it('el filtro por nombre angosta las filas', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFilas()

    await usuario.type(screen.getByLabelText('Filtrar por nombre'), 'depó')

    expect(screen.queryByText('Sucursal Centro')).not.toBeInTheDocument()
    expect(screen.getByText('Depósito')).toBeInTheDocument()
  })
})

describe('Sucursales — alta', () => {
  /** Abre el formulario y espera al DATO (las empresas), no al elemento: el select existe antes y queda inerte. */
  async function abrirAlta(usuario: ReturnType<typeof userEvent.setup>) {
    await esperarFilas()
    await usuario.click(screen.getByRole('button', { name: 'Nueva sucursal' }))
    await waitFor(() => expect(within(screen.getByLabelText('Empresa')).getAllByRole('option').map((o) => o.textContent)).toContain('Sur SRL'))
  }

  it('crea la sucursal con la empresa elegida y refresca', async () => {
    const usuario = userEvent.setup()
    let listados = 0
    apiPostMock.mockResolvedValue(sucursalFixture({ id: 502, nombre: 'Norte' }))
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/empresas') return Promise.resolve([empresaSur])
      if (ruta !== '/sucursales') return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
      listados += 1

      return Promise.resolve(listados === 1 ? [centro] : [centro, sucursalFixture({ id: 502, nombre: 'Norte' })])
    })
    render(<Sucursales />)
    await abrirAlta(usuario)

    await usuario.selectOptions(screen.getByLabelText('Empresa'), '20')
    await usuario.type(screen.getByLabelText('Nombre'), '  Norte  ')
    await usuario.click(screen.getByRole('button', { name: 'Crear' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/sucursales', { idEmpresa: 20, nombre: 'Norte', ...DIRECCION_NULA }))
    expect(await screen.findByText('Se creó la sucursal "Norte".')).toBeInTheDocument()
    await waitFor(() => expect(screen.getByText('Norte')).toBeInTheDocument())
    expect(screen.queryByLabelText('Empresa')).not.toBeInTheDocument()
  })

  it('sin empresa no llama a la API y lo dice', async () => {
    const usuario = userEvent.setup()
    montar()
    await abrirAlta(usuario)

    await usuario.type(screen.getByLabelText('Nombre'), 'Norte')
    // `required` frena el submit nativo; se manda el formulario directo para probar el rechazo propio de la pantalla.
    const formulario = screen.getByLabelText('Nombre').closest('form') as HTMLFormElement
    await act(async () => {
      formulario.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    })

    expect(await screen.findByText('Elegí la empresa y completá el nombre.')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('un segundo click sobre "Crear" en vuelo se descarta', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockImplementation(() => new Promise(() => {}))
    montar()
    await abrirAlta(usuario)
    await usuario.selectOptions(screen.getByLabelText('Empresa'), '20')
    await usuario.type(screen.getByLabelText('Nombre'), 'Norte')

    const crear = screen.getByRole('button', { name: 'Crear' })
    await act(async () => {
      crear.click()
      crear.click()
      await Promise.resolve()
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  it('un rechazo del servidor rinde su mensaje y deja el formulario abierto', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'No existe la empresa 20.'))
    montar()
    await abrirAlta(usuario)
    await usuario.selectOptions(screen.getByLabelText('Empresa'), '20')
    await usuario.type(screen.getByLabelText('Nombre'), 'Norte')
    await usuario.click(screen.getByRole('button', { name: 'Crear' }))

    expect(await screen.findByText('No existe la empresa 20.')).toBeInTheDocument()
    expect(screen.getByLabelText('Nombre')).toBeInTheDocument()
  })
})

describe('Sucursales — renombre', () => {
  it('edita solo el nombre: la empresa no se ofrece y el PUT lleva el nombre nuevo', async () => {
    const usuario = userEvent.setup()
    apiPutMock.mockResolvedValue(sucursalFixture({ nombre: 'Centro Norte' }))
    montar()
    await esperarFilas()

    await usuario.click(botonDeFila('Sucursal Centro', 'Editar'))
    expect(screen.queryByLabelText('Empresa')).not.toBeInTheDocument()
    const nombre = screen.getByLabelText('Nombre')
    expect(nombre).toHaveValue('Sucursal Centro')
    await usuario.clear(nombre)
    await usuario.type(nombre, 'Centro Norte')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() =>
      expect(apiPutMock).toHaveBeenCalledWith('/sucursales/500', { nombre: 'Centro Norte', ...DIRECCION_NULA }),
    )
    expect(await screen.findByText('Se actualizó "Centro Norte".')).toBeInTheDocument()
  })
})

describe('Sucursales — dirección', () => {
  it('la edición precarga la dirección guardada y el PUT la lleva completa', async () => {
    const usuario = userEvent.setup()
    apiPutMock.mockResolvedValue(centro)
    montar([
      sucursalFixture({
        calle: 'Colón',
        altura: '1234 bis',
        ciudad: 'Córdoba',
        provincia: 'Córdoba',
        codigoPostal: '5000',
        latitud: -31.4201,
        longitud: 0,
        referencia: 'Frente a la plaza',
      }),
    ])
    await esperarFilas()

    await usuario.click(botonDeFila('Sucursal Centro', 'Editar'))
    expect(screen.getByLabelText('Calle')).toHaveValue('Colón')
    expect(screen.getByLabelText('Provincia')).toHaveValue('Córdoba')
    expect(screen.getByLabelText('Latitud')).toHaveValue('-31.4201')
    expect(screen.getByLabelText('Longitud')).toHaveValue('0')

    await usuario.clear(screen.getByLabelText('Calle'))
    await usuario.type(screen.getByLabelText('Calle'), 'Mitre')
    await usuario.selectOptions(screen.getByLabelText('Provincia'), 'Santa Fe')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() =>
      expect(apiPutMock).toHaveBeenCalledWith('/sucursales/500', {
        nombre: 'Sucursal Centro',
        calle: 'Mitre',
        altura: '1234 bis',
        ciudad: 'Córdoba',
        provincia: 'Santa Fe',
        codigoPostal: '5000',
        latitud: -31.4201,
        longitud: 0,
        referencia: 'Frente a la plaza',
      }),
    )
  })

  it('ofrece las 24 jurisdicciones más la opción sin informar', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFilas()

    await usuario.click(botonDeFila('Sucursal Centro', 'Editar'))

    expect(within(screen.getByLabelText('Provincia')).getAllByRole('option')).toHaveLength(25)
  })

  it('una coordenada a medias se rechaza en el formulario, sin llamar a la API', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFilas()

    await usuario.click(botonDeFila('Sucursal Centro', 'Editar'))
    await usuario.type(screen.getByLabelText('Latitud'), '-31,4')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('Completá la latitud y la longitud, o dejá las dos vacías.')).toBeInTheDocument()
    expect(apiPutMock).not.toHaveBeenCalled()
  })

  it('un rechazo del servidor por la dirección se rinde dentro del modal', async () => {
    const usuario = userEvent.setup()
    apiPutMock.mockRejectedValue(
      new ErrorApi(400, 'provincia_invalida', 'La provincia tiene que ser una de las 24 jurisdicciones argentinas.'),
    )
    montar()
    await esperarFilas()

    await usuario.click(botonDeFila('Sucursal Centro', 'Editar'))
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(
      await screen.findByText('La provincia tiene que ser una de las 24 jurisdicciones argentinas.'),
    ).toBeInTheDocument()
    expect(screen.getByLabelText('Calle')).toBeInTheDocument()
  })
})

describe('Sucursales — baja', () => {
  it('el botón de baja no llama a la API hasta que se confirma', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockResolvedValue(undefined)
    montar()
    await esperarFilas()

    await usuario.click(botonDeFila('Depósito', 'Eliminar'))
    expect(apiDeleteMock).not.toHaveBeenCalled()

    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() => expect(apiDeleteMock).toHaveBeenCalledWith('/sucursales/501'))
    expect(await screen.findByText('Se dio de baja la sucursal "Depósito".')).toBeInTheDocument()
  })

  it('un 409 con cajas rinde el mensaje del servidor y su guía dentro de la puerta abierta', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockRejectedValue(
      new ErrorApi(409, 'sucursal_con_puntos_venta', 'No se puede dar de baja la sucursal porque tiene puntos de venta.'),
    )
    montar()
    await esperarFilas()

    await usuario.click(botonDeFila('Sucursal Centro', 'Eliminar'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    expect(
      await screen.findByText(
        'No se puede dar de baja la sucursal porque tiene puntos de venta. Movelas a otra sucursal desde la pantalla de Puntos de venta primero.',
      ),
    ).toBeInTheDocument()
  })

  it('un 409 por uso rinde su guía propia', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockRejectedValue(
      new ErrorApi(409, 'sucursal_en_uso', 'No se puede dar de baja la sucursal porque tiene stock.'),
    )
    montar()
    await esperarFilas()

    await usuario.click(botonDeFila('Depósito', 'Eliminar'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    expect(
      await screen.findByText(
        'No se puede dar de baja la sucursal porque tiene stock. Dá de baja o reasigná esos datos antes de eliminar la sucursal.',
      ),
    ).toBeInTheDocument()
  })

  it('cancelar cierra la puerta y no llama nunca a la API', async () => {
    const usuario = userEvent.setup()
    montar()
    await esperarFilas()

    await usuario.click(botonDeFila('Depósito', 'Eliminar'))
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(apiDeleteMock).not.toHaveBeenCalled()
  })

  it('un segundo click sobre la confirmación en vuelo se descarta', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockImplementation(() => new Promise(() => {}))
    montar()
    await esperarFilas()

    await usuario.click(botonDeFila('Depósito', 'Eliminar'))
    const confirmar = screen.getByRole('button', { name: 'Confirmar baja' })
    await act(async () => {
      confirmar.click()
      confirmar.click()
      await Promise.resolve()
    })

    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })
})

describe('Sucursales — fusión', () => {
  const otraEmpresa = sucursalFixture({ id: 600, idEmpresa: 30, nombre: 'Ajena', razonSocialEmpresa: 'Norte SA' })

  async function abrirFusion(usuario: ReturnType<typeof userEvent.setup>, fila: string) {
    await esperarFilas()
    await usuario.click(within(screen.getByRole('row', { name: new RegExp(fila) })).getByRole('button', { name: 'Fusionar' }))
  }

  it('el destino ofrece solo las otras sucursales de la misma empresa', async () => {
    const usuario = userEvent.setup()
    montar([centro, deposito, otraEmpresa])
    await abrirFusion(usuario, 'Depósito')

    const opciones = within(screen.getByLabelText('Sucursal destino')).getAllByRole('option').map((o) => o.textContent)
    expect(opciones).toEqual(['Elegir…', 'Sucursal Centro'])
  })

  it('explica qué pasa y no deja fusionar sin destino y sin confirmar', async () => {
    const usuario = userEvent.setup()
    montar()
    await abrirFusion(usuario, 'Depósito')

    expect(
      screen.getByText(/Las cajas de "Depósito" y todo su stock .* pasan a la sucursal destino, y "Depósito" se elimina\./),
    ).toBeInTheDocument()
    const fusionar = within(screen.getByRole('dialog')).getByRole('button', { name: 'Fusionar' })
    expect(fusionar).toBeDisabled()

    await usuario.selectOptions(screen.getByLabelText('Sucursal destino'), '500')
    expect(fusionar).toBeDisabled()

    await usuario.click(
      screen.getByLabelText(
        'Confirmo que las cajas y el stock de "Depósito" pasan a "Sucursal Centro" y que "Depósito" se elimina. No se puede deshacer.',
      ),
    )
    expect(fusionar).toBeEnabled()
  })

  it('fusiona con el destino elegido, cierra el modal y refresca', async () => {
    const usuario = userEvent.setup()
    let listados = 0
    apiPostMock.mockResolvedValue(sucursalFixture({ cantidadPuntosVenta: 3 }))
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta !== '/sucursales') return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
      listados += 1

      return Promise.resolve(listados === 1 ? [centro, deposito] : [sucursalFixture({ cantidadPuntosVenta: 3 })])
    })
    render(<Sucursales />)
    await abrirFusion(usuario, 'Depósito')

    await usuario.selectOptions(screen.getByLabelText('Sucursal destino'), '500')
    await usuario.click(screen.getByRole('checkbox'))
    await usuario.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Fusionar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/sucursales/501/fusionar', { idSucursalDestino: 500 }))
    expect(await screen.findByText('Se fusionó "Depósito" en "Sucursal Centro".')).toBeInTheDocument()
    await waitFor(() => expect(screen.queryByRole('row', { name: /Depósito/ })).not.toBeInTheDocument())
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('un rechazo del servidor rinde su mensaje y deja el modal abierto', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'sucursal_de_otra_empresa', 'La sucursal pertenece a otra empresa.'))
    montar()
    await abrirFusion(usuario, 'Depósito')
    await usuario.selectOptions(screen.getByLabelText('Sucursal destino'), '500')
    await usuario.click(screen.getByRole('checkbox'))
    await usuario.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Fusionar' }))

    expect(await screen.findByText('La sucursal pertenece a otra empresa.')).toBeInTheDocument()
    expect(screen.getByRole('dialog')).toBeInTheDocument()
  })

  it('mientras la fusión está en vuelo todo queda inerte y un segundo envío se descarta', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockImplementation(() => new Promise(() => {}))
    montar()
    await abrirFusion(usuario, 'Depósito')
    await usuario.selectOptions(screen.getByLabelText('Sucursal destino'), '500')
    await usuario.click(screen.getByRole('checkbox'))

    const fusionar = within(screen.getByRole('dialog')).getByRole('button', { name: 'Fusionar' })
    await act(async () => {
      fusionar.click()
      fusionar.click()
      await Promise.resolve()
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(screen.getByLabelText('Sucursal destino')).toBeDisabled()
    expect(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(within(screen.getByRole('row', { name: /Sucursal Centro/ })).getByRole('button', { name: 'Fusionar' })).toBeDisabled()
  })
})
