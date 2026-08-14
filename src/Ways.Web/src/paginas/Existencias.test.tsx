import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Existencias } from './Existencias'
import { RutaProtegida } from '../auth/RutaProtegida'
import { ROL } from '../api/tipos'
import type { Existencias as ExistenciasRespuesta, FilaExistencia, MinimosDeStock, PuntoVentaListado, UsuarioAutenticado } from '../api/tipos'

const apiGetMock = vi.fn()
const apiPutMock = vi.fn()
const apiDescargarMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: vi.fn(),
    put: (...args: unknown[]) => apiPutMock(...(args as [string, unknown])),
    delete: vi.fn(),
    descargar: (...args: unknown[]) => apiDescargarMock(...(args as [string])),
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
    usuario: 'supervisor',
    mail: 'supervisor@ways.test',
    rolId: ROL.Supervisor,
    rol: 'Supervisor',
    ultimaConexion: null,
    idTenant: 1,
    ...sobrescribir,
  }
}

let usuarioActual: UsuarioAutenticado | null = usuarioFixture()

vi.mock('../auth/useAuth', () => ({
  useAuth: () => ({ usuario: usuarioActual, cargando: false, iniciarSesion: vi.fn(), cerrarSesion: vi.fn() }),
}))

const puntoVentaCentro: PuntoVentaListado = {
  id: 10,
  idTenant: 1,
  idEmpresa: 1,
  nombre: 'PV Centro',
  domicilio: null,
  horario: null,
  whatsapp: null,
  instagram: null,
  facebook: null,
  web: null,
}

const puntoVentaNorte: PuntoVentaListado = {
  id: 11,
  idTenant: 1,
  idEmpresa: 1,
  nombre: 'PV Norte',
  domicilio: null,
  horario: null,
  whatsapp: null,
  instagram: null,
  facebook: null,
  web: null,
}

function filaFixture(sobrescribir: Partial<FilaExistencia> = {}): FilaExistencia {
  return { idArticulo: 100, nombre: 'Yerba mate 1kg', cantidad: 42.5, minimo: null, reposicion: null, estado: 'SinMinimo', ...sobrescribir }
}

function existenciasFixture(filas: FilaExistencia[] = [filaFixture()], idPuntoVenta = 10): ExistenciasRespuesta {
  return { idPuntoVenta, filas }
}

function minimosFixture(sobrescribir: Partial<MinimosDeStock> = {}): MinimosDeStock {
  return { idPuntoVenta: 10, idArticulo: 1, cantidad: 12, minimo: 5, reposicion: null, estado: 'Bajo', ...sobrescribir }
}

function renderExistencias() {
  return render(<Existencias />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

/** Monta detrás del mismo gate de rol que `App.tsx` usa para `/reportes/existencias`
 * (`Politicas.LecturaDeReportes`). */
function renderExistenciasProtegido() {
  return render(
    <MemoryRouter initialEntries={['/reportes/existencias']}>
      <Routes>
        <Route
          path="/reportes/existencias"
          element={
            <RutaProtegida rolesPermitidos={[ROL.Supervisor, ROL.Admin]}>
              <Existencias />
            </RutaProtegida>
          }
        />
        <Route path="/" element={<div>Inicio (redirigido)</div>} />
      </Routes>
    </MemoryRouter>,
  )
}

function mockearRutasBase(sobrescribir?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/puntos-venta') return Promise.resolve([puntoVentaCentro, puntoVentaNorte])
    const propia = sobrescribir?.(ruta)
    if (propia) return propia
    if (ruta.startsWith('/reportes/stock/existencias?')) return Promise.resolve(existenciasFixture())
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPutMock.mockReset()
  apiDescargarMock.mockReset()
  apiDescargarMock.mockResolvedValue(undefined)
  usuarioActual = usuarioFixture()
})

describe('Existencias — reporte (stage-11-exportacion-reportes, Slice 9 — web)', () => {
  it('arranca con el primer punto de venta cargado', async () => {
    mockearRutasBase()
    renderExistencias()

    await screen.findByLabelText('Punto de venta')
    expect(screen.getByLabelText('Punto de venta')).toHaveValue('10')
  })

  it('sin stock cargado muestra un estado vacío, no un re-query', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/existencias?')) return Promise.resolve(existenciasFixture([]))
      return undefined
    })
    renderExistencias()

    expect(await screen.findByText('No hay stock cargado para este punto de venta.')).toBeInTheDocument()
    const llamadas = apiGetMock.mock.calls.filter((call: unknown[]) => (call[0] as string).startsWith('/reportes/stock/existencias?'))
    expect(llamadas).toHaveLength(1)
  })

  it('renderiza las filas devueltas por el backend, sin idArticulo faltante en la consulta', async () => {
    const filaUno = filaFixture({ idArticulo: 1, nombre: 'Aceite de girasol 900ml', cantidad: 12 })
    const filaDos = filaFixture({ idArticulo: 2, nombre: 'Fideos guiseros 500g', cantidad: 87.5 })
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/existencias?')) return Promise.resolve(existenciasFixture([filaUno, filaDos]))
      return undefined
    })
    renderExistencias()

    await screen.findByText('Aceite de girasol 900ml')
    const filas = screen.getAllByRole('row').slice(1) // sin la fila de encabezado
    expect(within(filas[0]).getByText('Aceite de girasol 900ml')).toBeInTheDocument()
    expect(within(filas[0]).getByText('12')).toBeInTheDocument()
    expect(within(filas[1]).getByText('Fideos guiseros 500g')).toBeInTheDocument()
    expect(within(filas[1]).getByText('87,5')).toBeInTheDocument()

    const llamada = apiGetMock.mock.calls.find((call: unknown[]) => (call[0] as string).startsWith('/reportes/stock/existencias?'))!
    expect(llamada[0] as string).not.toContain('idArticulo')
  })

  it('cambiar el punto de venta dispara una nueva consulta con el idPuntoVenta elegido', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderExistencias()

    await screen.findByText('Yerba mate 1kg')
    apiGetMock.mockClear()
    mockearRutasBase()
    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '11')

    await waitFor(() => {
      const llamadas = apiGetMock.mock.calls.filter((call: unknown[]) => (call[0] as string).startsWith('/reportes/stock/existencias?'))
      expect(llamadas.some((call: unknown[]) => (call[0] as string).includes('idPuntoVenta=11'))).toBe(true)
    })
  })

  it('el botón de descarga apunta a /reportes/stock/existencias/export con el idPuntoVenta elegido', async () => {
    mockearRutasBase()
    apiDescargarMock.mockRejectedValueOnce(new Error('no se pudo descargar'))
    const usuario = userEvent.setup()
    renderExistencias()

    await screen.findByText('Yerba mate 1kg')
    await usuario.click(screen.getByRole('button', { name: 'Descargar' }))

    expect(await screen.findByText('No se pudo descargar el archivo.')).toBeInTheDocument()
    expect(apiDescargarMock.mock.calls[0][0]).toMatch(/^\/reportes\/stock\/existencias\/export\?idPuntoVenta=10/)
  })

  it('una respuesta desactualizada nunca pisa la más reciente (generación)', async () => {
    let resolverPrimera: (valor: ExistenciasRespuesta) => void = () => {}
    const primera = new Promise<ExistenciasRespuesta>((resolve) => {
      resolverPrimera = resolve
    })
    let cantidadDeLlamadas = 0

    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/existencias?')) {
        cantidadDeLlamadas += 1
        if (cantidadDeLlamadas === 1) return primera
        return Promise.resolve(existenciasFixture([filaFixture({ idArticulo: 999, nombre: 'segunda-respuesta' })]))
      }
      return undefined
    })

    const usuario = userEvent.setup()
    renderExistencias()
    await screen.findByLabelText('Punto de venta')

    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '11')
    expect(await screen.findByText('segunda-respuesta')).toBeInTheDocument()

    // El flush del microtask va DENTRO de act: un waitFor solo pasaría en su primer tick,
    // antes de que el .then stale aterrice, y saldría verde sin probar nada.
    await act(async () => {
      resolverPrimera(existenciasFixture([filaFixture({ idArticulo: 1, nombre: 'primera-respuesta-vieja' })]))
      await primera
    })
    expect(screen.queryByText('primera-respuesta-vieja')).not.toBeInTheDocument()
    expect(screen.getByText('segunda-respuesta')).toBeInTheDocument()
  })
})

describe('Existencias — editor de mínimos y reposición (stage-13-stock-inteligente, Slice 3)', () => {
  function dosFilasFixture() {
    return [
      filaFixture({ idArticulo: 1, nombre: 'Aceite de girasol 900ml', cantidad: 12 }),
      filaFixture({ idArticulo: 2, nombre: 'Fideos guiseros 500g', cantidad: 87.5 }),
    ]
  }

  it('guardar aplica la respuesta autoritativa del PUT sin volver a pedir el reporte (decisión 16)', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/existencias?')) return Promise.resolve(existenciasFixture(dosFilasFixture()))
      return undefined
    })
    apiPutMock.mockResolvedValue(minimosFixture({ idArticulo: 1, cantidad: 12, minimo: 5, reposicion: 20, estado: 'Bajo' }))
    const usuario = userEvent.setup()
    renderExistencias()

    await screen.findByText('Aceite de girasol 900ml')
    apiGetMock.mockClear()

    await usuario.click(screen.getAllByRole('button', { name: 'Editar' })[0])
    await usuario.type(screen.getByLabelText('Mínimo de Aceite de girasol 900ml'), '5')
    await usuario.type(screen.getByLabelText('Reposición de Aceite de girasol 900ml'), '20')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('Bajo')).toBeInTheDocument()
    expect(apiPutMock).toHaveBeenCalledWith('/stock/minimos', { idPuntoVenta: 10, idArticulo: 1, minimo: 5, reposicion: 20 })
    // Ningún GET nuevo desde que se limpió el mock — la fila se aplica desde el RETURNING del
    // propio PUT, nunca desde un refetch del reporte.
    expect(apiGetMock).not.toHaveBeenCalled()
  })

  it('una lectura desactualizada que llega recién después de guardar no pisa el valor recién guardado (regla 7)', async () => {
    let resolverNorte: (valor: ExistenciasRespuesta) => void = () => {}
    const norte = new Promise<ExistenciasRespuesta>((resolve) => {
      resolverNorte = resolve
    })
    let llamadasCentro = 0

    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/existencias?idPuntoVenta=11')) return norte
      if (ruta.startsWith('/reportes/stock/existencias?idPuntoVenta=10')) {
        llamadasCentro += 1
        return Promise.resolve(existenciasFixture(dosFilasFixture(), 10))
      }
      return undefined
    })
    apiPutMock.mockResolvedValue(minimosFixture({ idArticulo: 1, cantidad: 12, minimo: 5, reposicion: null, estado: 'Bajo' }))

    const usuario = userEvent.setup()
    renderExistencias()
    await screen.findByText('Aceite de girasol 900ml')

    // Cambia a Norte (queda pendiente) y vuelve a Centro (resuelve de nuevo, rápido) — deja una
    // generación intermedia obsoleta que todavía no aterrizó.
    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '11')
    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '10')
    await screen.findByText('Aceite de girasol 900ml')
    expect(llamadasCentro).toBe(2)

    await usuario.click(screen.getAllByRole('button', { name: 'Editar' })[0])
    await usuario.type(screen.getByLabelText('Mínimo de Aceite de girasol 900ml'), '5')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))
    await screen.findByText('Bajo')

    // La lectura obsoleta de Norte llega RECIÉN ahora. El flush del microtask va DENTRO de act:
    // un waitFor solo pasaría en su primer tick, antes de que el .then stale aterrice, y saldría
    // verde sin probar nada (mutation-proof-tests regla 7).
    await act(async () => {
      resolverNorte(existenciasFixture([filaFixture({ idArticulo: 1, nombre: 'Aceite de girasol 900ml', cantidad: 999 })], 11))
      await norte
    })

    expect(screen.getByText('Bajo')).toBeInTheDocument()
    expect(screen.queryByText('999')).not.toBeInTheDocument()
  })

  it('doble click en Guardar manda un solo PUT; abrir la fila B queda bloqueado mientras la fila A se guarda (mutation target 3.6)', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/existencias?')) return Promise.resolve(existenciasFixture(dosFilasFixture()))
      return undefined
    })
    let resolverPut: (valor: MinimosDeStock) => void = () => {}
    apiPutMock.mockImplementation(
      () =>
        new Promise<MinimosDeStock>((resolve) => {
          resolverPut = resolve
        }),
    )
    const usuario = userEvent.setup()
    renderExistencias()

    await screen.findByText('Aceite de girasol 900ml')
    await usuario.click(screen.getAllByRole('button', { name: 'Editar' })[0])
    await usuario.type(screen.getByLabelText('Mínimo de Aceite de girasol 900ml'), '5')

    const botonGuardar = screen.getByRole('button', { name: 'Guardar' })
    // Doble click rápido — mismo criterio que CompraEditor.test.tsx: el guard de reentrancia de
    // primera línea evita un segundo PUT.
    await usuario.click(botonGuardar)
    await usuario.click(botonGuardar)
    expect(apiPutMock).toHaveBeenCalledTimes(1)

    resolverPut(minimosFixture({ idArticulo: 1, cantidad: 12, minimo: 5, reposicion: null, estado: 'Bajo' }))
    await screen.findByText('Bajo')
  })

  it('abrir la fila B queda bloqueado en el MISMO tick que se dispara el guardado de la fila A — el guard sobrevive al re-render del `disabled` (mutation target 3.6)', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/existencias?')) return Promise.resolve(existenciasFixture(dosFilasFixture()))
      return undefined
    })
    apiPutMock.mockImplementation(() => new Promise<MinimosDeStock>(() => {})) // nunca resuelve — no importa para este test
    const usuario = userEvent.setup()
    renderExistencias()

    await screen.findByText('Aceite de girasol 900ml')
    await usuario.click(screen.getAllByRole('button', { name: 'Editar' })[0]) // abre A
    await usuario.type(screen.getByLabelText('Mínimo de Aceite de girasol 900ml'), '5')

    const botonGuardar = screen.getByRole('button', { name: 'Guardar' })
    const botonEditarFilaB = screen.getByRole('button', { name: 'Editar' }) // solo B: A ya muestra Guardar/Cancelar

    // Ambos clicks se despachan DENTRO del mismo `act`, sin ningún `await` (ni render) entre
    // medio: el atributo `disabled` de B TODAVÍA no refleja `guardando`, así que solo un guard
    // síncrono (el ref) puede bloquear esta carrera — `react-async-state` regla 9: "un doble
    // click en el mismo tick vence al re-render del atributo `disabled`". Probar esto con clicks
    // AWAITED (userEvent) no ejercitaría el guard removido por la mutación: para entonces el
    // atributo `disabled` ya haría el trabajo por su cuenta.
    act(() => {
      fireEvent.click(botonGuardar)
      fireEvent.click(botonEditarFilaB)
    })

    expect(apiPutMock).toHaveBeenCalledTimes(1)
    expect(screen.queryByLabelText('Mínimo de Fideos guiseros 500g')).not.toBeInTheDocument()
  })

  it('reposición menor que el mínimo deshabilita Guardar con un aviso, sin llegar a mandar el PUT', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/existencias?')) return Promise.resolve(existenciasFixture(dosFilasFixture()))
      return undefined
    })
    const usuario = userEvent.setup()
    renderExistencias()

    await screen.findByText('Aceite de girasol 900ml')
    await usuario.click(screen.getAllByRole('button', { name: 'Editar' })[0])
    await usuario.type(screen.getByLabelText('Mínimo de Aceite de girasol 900ml'), '10')
    await usuario.type(screen.getByLabelText('Reposición de Aceite de girasol 900ml'), '5')

    expect(await screen.findByText('La reposición no puede ser menor que el mínimo.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled()
    expect(apiPutMock).not.toHaveBeenCalled()
  })
})

describe('Existencias — role gating (spec: A Supervisor Exports Existencias)', () => {
  it('un Supervisor llega a /reportes/existencias', async () => {
    mockearRutasBase()
    renderExistenciasProtegido()

    await screen.findByText('Yerba mate 1kg')
    expect(screen.queryByText('Inicio (redirigido)')).not.toBeInTheDocument()
  })

  it('un Vendedor nunca llega a /reportes/existencias: redirige a Inicio', async () => {
    usuarioActual = usuarioFixture({ id: 4, usuario: 'vendedor', rolId: ROL.Vendedor, rol: 'Vendedor' })
    mockearRutasBase()

    renderExistenciasProtegido()

    expect(await screen.findByText('Inicio (redirigido)')).toBeInTheDocument()
    await waitFor(() => expect(screen.queryByText('Existencias')).not.toBeInTheDocument())
  })
})
