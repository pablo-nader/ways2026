import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { PuertaDePuntoVenta } from './PuertaDePuntoVenta'
import { usePuntoVenta } from './usePuntoVenta'
import { CLAVE_PUNTO_VENTA_DE_SESION } from './almacenDePuntoVenta'
import { ErrorApi } from '../api/cliente'
import { ROL } from '../api/tipos'
import type { PuntoVentaListado, UsuarioAutenticado } from '../api/tipos'

const apiGetMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: vi.fn(),
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
    id: 9,
    usuario: 'ana',
    mail: 'ana@ways.test',
    rolId: ROL.Vendedor,
    rol: 'Vendedor',
    ultimaConexion: null,
    idTenant: 2,
    ...sobrescribir,
  }
}

let usuarioActual: UsuarioAutenticado | null = usuarioFixture()
const cerrarSesionMock = vi.fn()

vi.mock('../auth/useAuth', () => ({
  useAuth: () => ({
    usuario: usuarioActual,
    cargando: false,
    iniciarSesion: vi.fn(),
    cerrarSesion: cerrarSesionMock,
  }),
}))

function pvFixture(sobrescribir: Partial<PuntoVentaListado> = {}): PuntoVentaListado {
  return {
    id: 100,
    idTenant: 2,
    idEmpresa: 20,
    nombre: 'Local Centro',
    domicilio: null,
    horario: null,
    whatsapp: null,
    instagram: null,
    facebook: null,
    web: null,
    nombreTenant: 'Comercio Sur',
    razonSocialEmpresa: 'Sur SRL',
    ...sobrescribir,
  }
}

const centro = pvFixture()
const norte = pvFixture({ id: 101, nombre: 'Local Norte' })
const sur = pvFixture({ id: 102, nombre: 'Local Sur' })

type Respuesta = () => Promise<PuntoVentaListado[]>

/** Respuestas de `GET /puntos-venta` en orden de llegada; una lectura sin respuesta encolada falla. */
let respuestas: Respuesta[] = []

function encolar(...nuevas: Respuesta[]) {
  respuestas.push(...nuevas)
}

function listado(items: PuntoVentaListado[]): Respuesta {
  return () => Promise.resolve(items)
}

function fallo(error: Error): Respuesta {
  return () => Promise.reject(error)
}

function diferido<T>() {
  let resolver!: (valor: T) => void
  let rechazar!: (motivo: unknown) => void
  const promesa = new Promise<T>((res, rej) => {
    resolver = res
    rechazar = rej
  })

  return { promesa, resolver, rechazar }
}

function guardado(): unknown {
  const crudo = localStorage.getItem(CLAVE_PUNTO_VENTA_DE_SESION)

  return crudo ? JSON.parse(crudo) : null
}

/** Consumidor mínimo: expone el contexto como texto y como botones para accionarlo. */
function Sonda() {
  const { puntoVenta, puntosVenta, elegir, recargar } = usePuntoVenta()
  const [resultado, setResultado] = useState('')

  return (
    <div>
      <p>{`Punto de venta: ${puntoVenta?.nombre ?? 'ninguno'}`}</p>
      <p>{`Cantidad: ${puntosVenta.length}`}</p>
      {puntosVenta.map((p) => (
        <button key={p.id} type="button" onClick={() => elegir(p.id)}>
          {`Elegir ${p.nombre}`}
        </button>
      ))}
      <button type="button" onClick={() => elegir(999)}>
        Elegir inexistente
      </button>
      <button
        type="button"
        onClick={() => {
          recargar().then(
            () => setResultado('recarga ok'),
            () => setResultado('recarga falló'),
          )
        }}
      >
        Recargar
      </button>
      <output>{resultado}</output>
    </div>
  )
}

function arbol() {
  return (
    <MemoryRouter initialEntries={['/']}>
      <Routes>
        <Route
          path="/"
          element={
            <PuertaDePuntoVenta>
              <Sonda />
            </PuertaDePuntoVenta>
          }
        />
        <Route path="/login" element={<div>Login (redirigido)</div>} />
      </Routes>
    </MemoryRouter>
  )
}

function montar() {
  return render(arbol())
}

const TEXTO_ELEGIR = 'Elegí el punto de venta'

beforeEach(() => {
  localStorage.clear()
  respuestas = []
  usuarioActual = usuarioFixture()
  cerrarSesionMock.mockReset()
  cerrarSesionMock.mockResolvedValue(undefined)
  apiGetMock.mockReset()
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta !== '/puntos-venta') return Promise.reject(new Error(`ruta inesperada: ${ruta}`))

    const siguiente = respuestas.shift()

    return siguiente ? siguiente() : Promise.reject(new Error('listado sin respuesta encolada'))
  })
})

describe('PuertaDePuntoVenta — root', () => {
  it('no pide el listado, deja pasar con el contexto vacío y recargar resuelve sin pedir nada', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Root, rol: 'Root', idTenant: null })
    montar()

    expect(screen.getByText('Punto de venta: ninguno')).toBeInTheDocument()
    expect(screen.getByText('Cantidad: 0')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Recargar' }))

    expect(await screen.findByText('recarga ok')).toBeInTheDocument()
    expect(apiGetMock).not.toHaveBeenCalled()
  })
})

describe('PuertaDePuntoVenta — carga inicial', () => {
  it('muestra el cargando mientras el listado está en vuelo, sin renderizar a los hijos', () => {
    encolar(() => new Promise(() => undefined))
    montar()

    expect(screen.getByText('Cargando puntos de venta…')).toBeInTheDocument()
    expect(screen.queryByText(/Punto de venta:/)).not.toBeInTheDocument()
    expect(apiGetMock).toHaveBeenCalledTimes(1)
  })

  it('sin puntos de venta deja pasar con la selección vacía', async () => {
    encolar(listado([]))
    montar()

    expect(await screen.findByText('Punto de venta: ninguno')).toBeInTheDocument()
    expect(screen.getByText('Cantidad: 0')).toBeInTheDocument()
    expect(guardado()).toBeNull()
  })

  it('con uno solo lo elige sin preguntar y lo guarda', async () => {
    encolar(listado([centro]))
    montar()

    expect(await screen.findByText('Punto de venta: Local Centro')).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: TEXTO_ELEGIR })).not.toBeInTheDocument()
    await waitFor(() => expect(guardado()).toEqual({ idUsuario: 9, idPuntoVenta: 100 }))
  })

  it('con varios y un guardado vigente lo elige sin preguntar', async () => {
    localStorage.setItem(CLAVE_PUNTO_VENTA_DE_SESION, JSON.stringify({ idUsuario: 9, idPuntoVenta: 101 }))
    encolar(listado([centro, norte]))
    montar()

    expect(await screen.findByText('Punto de venta: Local Norte')).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: TEXTO_ELEGIR })).not.toBeInTheDocument()
  })

  it('con varios y nada guardado pregunta a pantalla completa, sin renderizar a los hijos', async () => {
    encolar(listado([centro, norte]))
    montar()

    expect(await screen.findByRole('heading', { level: 1, name: TEXTO_ELEGIR })).toBeInTheDocument()
    expect(screen.queryByText(/Punto de venta:/)).not.toBeInTheDocument()
  })

  it('con varios y un guardado de otro usuario pregunta igual', async () => {
    localStorage.setItem(CLAVE_PUNTO_VENTA_DE_SESION, JSON.stringify({ idUsuario: 77, idPuntoVenta: 101 }))
    encolar(listado([centro, norte]))
    montar()

    expect(await screen.findByRole('heading', { name: TEXTO_ELEGIR })).toBeInTheDocument()
  })

  it('con varios y un guardado que ya no existe pregunta, y al elegir deja pasar y guarda', async () => {
    localStorage.setItem(CLAVE_PUNTO_VENTA_DE_SESION, JSON.stringify({ idUsuario: 9, idPuntoVenta: 555 }))
    encolar(listado([centro, norte]))
    montar()

    await screen.findByRole('heading', { name: TEXTO_ELEGIR })
    await waitFor(() => expect(guardado()).toBeNull())

    await userEvent.click(screen.getByRole('button', { name: 'Local Norte' }))

    expect(await screen.findByText('Punto de venta: Local Norte')).toBeInTheDocument()
    expect(screen.getByText('Cantidad: 2')).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: TEXTO_ELEGIR })).not.toBeInTheDocument()
    await waitFor(() => expect(guardado()).toEqual({ idUsuario: 9, idPuntoVenta: 101 }))
  })

  it('una respuesta que llega después de desmontar se ignora', async () => {
    const lectura = diferido<PuntoVentaListado[]>()
    encolar(() => lectura.promesa)
    const { unmount } = montar()

    unmount()
    await act(async () => {
      lectura.resolver([centro])
    })

    expect(guardado()).toBeNull()
    expect(screen.queryByText(/Punto de venta:/)).not.toBeInTheDocument()
  })

  it('al cambiar de usuario descarta la lectura de la cuenta anterior y pide la propia', async () => {
    const lecturaDeAna = diferido<PuntoVentaListado[]>()
    encolar(() => lecturaDeAna.promesa, listado([norte]))
    const { rerender } = montar()

    usuarioActual = usuarioFixture({ id: 10, usuario: 'beto', mail: 'beto@ways.test' })
    rerender(arbol())

    expect(await screen.findByText('Punto de venta: Local Norte')).toBeInTheDocument()
    expect(apiGetMock).toHaveBeenCalledTimes(2)

    await act(async () => {
      lecturaDeAna.resolver([centro])
    })

    expect(screen.getByText('Punto de venta: Local Norte')).toBeInTheDocument()
    expect(guardado()).toEqual({ idUsuario: 10, idPuntoVenta: 101 })
  })
})

describe('PuertaDePuntoVenta — error de carga', () => {
  it('muestra el mensaje de la API en un alert con Reintentar y Salir habilitados', async () => {
    encolar(fallo(new ErrorApi(500, 'error', 'Falló la base')))
    montar()

    const alerta = await screen.findByRole('alert')

    expect(alerta).toHaveTextContent('Falló la base')
    expect(within(alerta).getByRole('button', { name: 'Reintentar' })).toBeEnabled()
    expect(within(alerta).getByRole('button', { name: 'Salir' })).toBeEnabled()
    expect(screen.queryByText(/Punto de venta:/)).not.toBeInTheDocument()
  })

  it('usa el mensaje genérico ante un error que no es de la API', async () => {
    encolar(fallo(new Error('boom')))
    montar()

    expect(await screen.findByRole('alert')).toHaveTextContent('No se pudieron cargar los puntos de venta.')
  })

  it('Reintentar vuelve a pedir el listado, deshabilita los dos botones en vuelo y deja pasar al resolver', async () => {
    const reintento = diferido<PuntoVentaListado[]>()
    encolar(fallo(new ErrorApi(500, 'error', 'Falló')), () => reintento.promesa)
    montar()
    await screen.findByRole('alert')

    await userEvent.click(screen.getByRole('button', { name: 'Reintentar' }))

    expect(apiGetMock).toHaveBeenCalledTimes(2)
    expect(screen.getByRole('button', { name: 'Reintentar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Salir' })).toBeDisabled()

    await act(async () => {
      reintento.resolver([centro])
    })

    expect(await screen.findByText('Punto de venta: Local Centro')).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('un segundo clic en Reintentar en el mismo tick no dispara otra lectura', async () => {
    const reintento = diferido<PuntoVentaListado[]>()
    encolar(fallo(new ErrorApi(500, 'error', 'Falló')), () => reintento.promesa)
    montar()
    const boton = await screen.findByRole('button', { name: 'Reintentar' })

    await act(async () => {
      boton.click()
      boton.click()
    })

    expect(apiGetMock).toHaveBeenCalledTimes(2)

    await act(async () => {
      reintento.resolver([centro])
    })

    expect(await screen.findByText('Punto de venta: Local Centro')).toBeInTheDocument()
  })

  it('un reintento fallido vuelve a mostrar el error nuevo con los botones habilitados', async () => {
    encolar(fallo(new ErrorApi(500, 'error', 'Primero')), fallo(new ErrorApi(503, 'error', 'Segundo')))
    montar()
    await screen.findByRole('alert')

    await userEvent.click(screen.getByRole('button', { name: 'Reintentar' }))

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Segundo'))
    expect(screen.getByRole('button', { name: 'Reintentar' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Salir' })).toBeEnabled()
  })

  it('Salir cierra la sesión con los botones deshabilitados en vuelo y navega a /login', async () => {
    const salida = diferido<void>()
    cerrarSesionMock.mockReturnValue(salida.promesa)
    encolar(fallo(new ErrorApi(500, 'error', 'Falló')))
    montar()
    await screen.findByRole('alert')

    await userEvent.click(screen.getByRole('button', { name: 'Salir' }))

    expect(cerrarSesionMock).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('button', { name: 'Reintentar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Salir' })).toBeDisabled()

    await act(async () => {
      salida.resolver()
    })

    expect(await screen.findByText('Login (redirigido)')).toBeInTheDocument()
  })
})

describe('PuertaDePuntoVenta — elegir y recargar', () => {
  /** Dos puntos de venta con el Centro guardado y ya activo. */
  async function montarConDos() {
    localStorage.setItem(CLAVE_PUNTO_VENTA_DE_SESION, JSON.stringify({ idUsuario: 9, idPuntoVenta: 100 }))
    encolar(listado([centro, norte]))
    const resultado = montar()
    await screen.findByText('Punto de venta: Local Centro')

    return resultado
  }

  it('elegir cambia el activo y lo guarda', async () => {
    await montarConDos()

    await userEvent.click(screen.getByRole('button', { name: 'Elegir Local Norte' }))

    expect(screen.getByText('Punto de venta: Local Norte')).toBeInTheDocument()
    await waitFor(() => expect(guardado()).toEqual({ idUsuario: 9, idPuntoVenta: 101 }))
  })

  it('elegir con un id que no está en la lista se ignora', async () => {
    await montarConDos()

    await userEvent.click(screen.getByRole('button', { name: 'Elegir inexistente' }))

    expect(screen.getByText('Punto de venta: Local Centro')).toBeInTheDocument()
    expect(guardado()).toEqual({ idUsuario: 9, idPuntoVenta: 100 })
  })

  it('recargar reemplaza la lista y refresca la fila del activo', async () => {
    await montarConDos()
    encolar(listado([{ ...centro, nombre: 'Local Centro Renombrado' }, norte, sur]))

    await userEvent.click(screen.getByRole('button', { name: 'Recargar' }))

    expect(await screen.findByText('recarga ok')).toBeInTheDocument()
    expect(screen.getByText('Punto de venta: Local Centro Renombrado')).toBeInTheDocument()
    expect(screen.getByText('Cantidad: 3')).toBeInTheDocument()
    expect(guardado()).toEqual({ idUsuario: 9, idPuntoVenta: 100 })
  })

  it('si el activo desapareció y quedan varios, vacía la selección, olvida el guardado y vuelve a preguntar', async () => {
    await montarConDos()
    encolar(listado([norte, sur]))

    await userEvent.click(screen.getByRole('button', { name: 'Recargar' }))

    expect(await screen.findByRole('heading', { name: TEXTO_ELEGIR })).toBeInTheDocument()
    expect(screen.queryByText(/Punto de venta:/)).not.toBeInTheDocument()
    await waitFor(() => expect(guardado()).toBeNull())
  })

  it('si el activo desapareció y queda uno solo, lo elige y lo guarda', async () => {
    await montarConDos()
    encolar(listado([norte]))

    await userEvent.click(screen.getByRole('button', { name: 'Recargar' }))

    expect(await screen.findByText('Punto de venta: Local Norte')).toBeInTheDocument()
    expect(screen.getByText('Cantidad: 1')).toBeInTheDocument()
    await waitFor(() => expect(guardado()).toEqual({ idUsuario: 9, idPuntoVenta: 101 }))
  })

  it('si no queda ninguno deja pasar con la selección vacía y olvida el guardado', async () => {
    await montarConDos()
    encolar(listado([]))

    await userEvent.click(screen.getByRole('button', { name: 'Recargar' }))

    expect(await screen.findByText('Punto de venta: ninguno')).toBeInTheDocument()
    expect(screen.getByText('Cantidad: 0')).toBeInTheDocument()
    await waitFor(() => expect(guardado()).toBeNull())
  })

  it('ante un fallo conserva el estado anterior, no toca la pantalla de error y rechaza al llamador', async () => {
    await montarConDos()
    encolar(fallo(new ErrorApi(500, 'error', 'Falló')))

    await userEvent.click(screen.getByRole('button', { name: 'Recargar' }))

    expect(await screen.findByText('recarga falló')).toBeInTheDocument()
    expect(screen.getByText('Punto de venta: Local Centro')).toBeInTheDocument()
    expect(screen.getByText('Cantidad: 2')).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(guardado()).toEqual({ idUsuario: 9, idPuntoVenta: 100 })
  })

  it('entre dos recargas en vuelo gana la última que arrancó, y la superada ni aplica ni rechaza', async () => {
    await montarConDos()
    const primera = diferido<PuntoVentaListado[]>()
    const segunda = diferido<PuntoVentaListado[]>()
    encolar(() => primera.promesa, () => segunda.promesa)

    await userEvent.click(screen.getByRole('button', { name: 'Recargar' }))
    await userEvent.click(screen.getByRole('button', { name: 'Recargar' }))
    expect(apiGetMock).toHaveBeenCalledTimes(3)

    await act(async () => {
      segunda.resolver([centro, norte, sur])
    })

    expect(screen.getByText('Cantidad: 3')).toBeInTheDocument()
    expect(screen.getByText('recarga ok')).toBeInTheDocument()

    // La primera llega tarde y con una lista que, de aplicarse, elegiría sola al único restante.
    await act(async () => {
      primera.rechazar(new ErrorApi(500, 'error', 'Tarde'))
    })

    expect(screen.getByText('Cantidad: 3')).toBeInTheDocument()
    expect(screen.getByText('Punto de venta: Local Centro')).toBeInTheDocument()
    expect(screen.getByText('recarga ok')).toBeInTheDocument()
    expect(screen.queryByText('recarga falló')).not.toBeInTheDocument()
  })

  it('una recarga superada que resuelve tarde tampoco pisa la lista vigente', async () => {
    await montarConDos()
    const primera = diferido<PuntoVentaListado[]>()
    const segunda = diferido<PuntoVentaListado[]>()
    encolar(() => primera.promesa, () => segunda.promesa)

    await userEvent.click(screen.getByRole('button', { name: 'Recargar' }))
    await userEvent.click(screen.getByRole('button', { name: 'Recargar' }))

    await act(async () => {
      segunda.resolver([centro, norte, sur])
    })
    await act(async () => {
      primera.resolver([norte])
    })

    expect(screen.getByText('Cantidad: 3')).toBeInTheDocument()
    expect(screen.getByText('Punto de venta: Local Centro')).toBeInTheDocument()
    expect(guardado()).toEqual({ idUsuario: 9, idPuntoVenta: 100 })
  })
})

describe('usePuntoVenta', () => {
  it('fuera del proveedor lanza', () => {
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => undefined)

    expect(() => render(<Sonda />)).toThrow('usePuntoVenta tiene que usarse dentro de una PuertaDePuntoVenta.')

    errorSpy.mockRestore()
  })
})
