import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Existencias } from './Existencias'
import { RutaProtegida } from '../auth/RutaProtegida'
import { ROL } from '../api/tipos'
import type { ArticuloListado, Existencias as ExistenciasRespuesta, FilaExistencia, MinimosDeStock, PuntoVentaListado, UsuarioAutenticado } from '../api/tipos'

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

// Judgment-day round A (FINDING 2): el default es Admin porque la mayoría de los tests de este
// archivo ejercitan las acciones de escritura (Editar/Guardar/agregar fila), gateadas por
// `puedeEscribir` — mismo criterio que `CompraEditor.test.tsx`. Los tests de rol (`describe`
// "role gating") sobrescriben `usuarioActual` explícitamente cuando necesitan otro rol.
function usuarioFixture(sobrescribir: Partial<UsuarioAutenticado> = {}): UsuarioAutenticado {
  return {
    id: 9,
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

function articuloFixture(sobrescribir: Partial<ArticuloListado> = {}): ArticuloListado {
  return {
    id: 50,
    codigoInterno: 'ART-50',
    nombre: 'Arroz largo fino 1kg',
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
    controlaLote: false,
    ...sobrescribir,
  }
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

  it('dos clicks en Guardar en el MISMO tick mandan un solo PUT — el guard de reentrancia de `guardarFila` sobrevive al re-render del `disabled` (judgment-day round 2, FINDING 3)', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/existencias?')) return Promise.resolve(existenciasFixture(dosFilasFixture()))
      return undefined
    })
    apiPutMock.mockImplementation(() => new Promise<MinimosDeStock>(() => {})) // nunca resuelve — no importa para este test
    const usuario = userEvent.setup()
    renderExistencias()

    await screen.findByText('Aceite de girasol 900ml')
    await usuario.click(screen.getAllByRole('button', { name: 'Editar' })[0])
    await usuario.type(screen.getByLabelText('Mínimo de Aceite de girasol 900ml'), '5')

    const botonGuardar = screen.getByRole('button', { name: 'Guardar' })
    // Mismo patrón del mutation target 3.6 ("abrir la fila B..."): dos `fireEvent.click` dentro de
    // UN solo `act()`, sin `await` (ni render) entre medio — el atributo `disabled` de Guardar
    // TODAVÍA no reflejó `guardando` cuando se despacha el segundo click, así que solo el guard
    // síncrono de primera línea de `guardarFila` (no el atributo, que un `userEvent.click` awaited
    // no-opea sobre un botón ya disabled) puede evitar el segundo PUT.
    act(() => {
      fireEvent.click(botonGuardar)
      fireEvent.click(botonGuardar)
    })

    expect(apiPutMock).toHaveBeenCalledTimes(1)
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

  it('mientras una fila guarda, TODA la ventana queda attribute-disabled — no solo la fila en edición (decisión 15 / tarea 3.5)', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/articulos')) return Promise.resolve({ items: [articuloFixture()], total: 1, pagina: 1, tamanio: 25 })
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
    // Deja resultados del picker visibles (sin elegir ninguno) ANTES de disparar el guardado —
    // judgment-day round A, FINDING 3: el `disabled` de esos botones de resultado, no solo el del
    // input de búsqueda.
    await usuario.type(screen.getByLabelText('Buscar artículo para agregar'), 'arroz')
    const botonResultado = await screen.findByText('ART-50 — Arroz largo fino 1kg')

    await usuario.click(screen.getAllByRole('button', { name: 'Editar' })[0]) // abre A
    await usuario.type(screen.getByLabelText('Mínimo de Aceite de girasol 900ml'), '5')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    // El PUT de la fila A quedó en vuelo (nunca resuelto todavía) — TODA superficie gobernada por
    // `guardando` tiene que quedar attribute-disabled, no solo la fila que se está guardando
    // (react-async-state regla 10: el mismo patrón de bloqueo se replica en cada superficie
    // hermana de la ventana).
    expect(screen.getByRole('button', { name: 'Editar' })).toBeDisabled() // única "Editar" visible: fila B
    expect(screen.getByLabelText('Punto de venta')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Descargar' })).toBeDisabled()
    expect(screen.getByLabelText('Buscar artículo para agregar')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Guardando…' })).toBeDisabled() // el propio Guardar en vuelo
    expect(botonResultado.closest('button')).toBeDisabled() // FINDING 3: el atributo, no solo el guard de `agregarFila`

    resolverPut(minimosFixture({ idArticulo: 1, cantidad: 12, minimo: 5, reposicion: null, estado: 'Bajo' }))
    await screen.findByText('Bajo')

    expect(screen.getAllByRole('button', { name: 'Editar' })[0]).not.toBeDisabled()
    expect(screen.getByLabelText('Punto de venta')).not.toBeDisabled()
    expect(screen.getByRole('button', { name: 'Descargar' })).not.toBeDisabled()
    expect(screen.getByLabelText('Buscar artículo para agregar')).not.toBeDisabled()
  })

  it('guardar la fila A y después editar y guardar la fila B aplica la respuesta a B — el `finally` token-gated reabre la ventana (tarea 3.5/3.10)', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/existencias?')) return Promise.resolve(existenciasFixture(dosFilasFixture()))
      return undefined
    })
    apiPutMock
      .mockResolvedValueOnce(minimosFixture({ idArticulo: 1, cantidad: 12, minimo: 5, reposicion: null, estado: 'Bajo' }))
      .mockResolvedValueOnce(minimosFixture({ idArticulo: 2, cantidad: 87.5, minimo: 10, reposicion: null, estado: 'Bajo' }))
    const usuario = userEvent.setup()
    renderExistencias()

    await screen.findByText('Aceite de girasol 900ml')

    // Guarda la fila A (idArticulo 1) de punta a punta.
    await usuario.click(screen.getAllByRole('button', { name: 'Editar' })[0])
    await usuario.type(screen.getByLabelText('Mínimo de Aceite de girasol 900ml'), '5')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))
    await screen.findAllByText('Bajo') // A ya resolvió — si el `finally` quedara colgado, esto nunca abriría B

    // Ahora edita y guarda la fila B (idArticulo 2) — la SEGUNDA fila, no la primera: si el updater
    // aplicara siempre a la primera fila (o si la ventana siguiera bloqueada por un `finally` sin
    // `setGuardando(null)`), este segundo guardado fallaría o pisaría la fila equivocada.
    await usuario.click(screen.getAllByRole('button', { name: 'Editar' })[1]) // fila B — A ya volvió a mostrar "Editar" tras guardar
    await usuario.type(screen.getByLabelText('Mínimo de Fideos guiseros 500g'), '10')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(2))
    expect(apiPutMock).toHaveBeenNthCalledWith(2, '/stock/minimos', { idPuntoVenta: 10, idArticulo: 2, minimo: 10, reposicion: null })

    const filas = await screen.findAllByRole('row')
    const filaB = within(filas[2]) // [0] encabezado, [1] fila A, [2] fila B
    expect(filaB.getByText('Bajo')).toBeInTheDocument()
    expect(filaB.getByText('10')).toBeInTheDocument()
  })

  it('el indicador "Buscando…" del picker de alta desaparece si el término vuelve a quedar corto antes de que la búsqueda resuelva', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderExistencias()

    await screen.findByText('Yerba mate 1kg')
    const buscador = screen.getByLabelText('Buscar artículo para agregar')

    await usuario.type(buscador, 'ye')
    expect(screen.getByText('Buscando…')).toBeInTheDocument()

    await usuario.type(buscador, '{Backspace}') // vuelve a 1 carácter, antes de que resuelva el debounce de 300ms
    expect(screen.queryByText('Buscando…')).not.toBeInTheDocument()
  })

  it('cancelar una fila agregada por el picker y nunca guardada la saca de la grilla (fila fantasma, tarea 3.4)', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/articulos')) return Promise.resolve({ items: [articuloFixture()], total: 1, pagina: 1, tamanio: 25 })
      return undefined
    })
    const usuario = userEvent.setup()
    renderExistencias()

    await screen.findByText('Yerba mate 1kg')
    await usuario.type(screen.getByLabelText('Buscar artículo para agregar'), 'arroz')
    await usuario.click(await screen.findByText('ART-50 — Arroz largo fino 1kg'))

    expect(await screen.findByText('Arroz largo fino 1kg')).toBeInTheDocument()
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByText('Arroz largo fino 1kg')).not.toBeInTheDocument()
  })

  it('cambiar de punto de venta sin guardar una fila agregada por el picker no deja el ref fantasma corrompiendo la grilla nueva (judgment-day round 2, FINDING 1, MAJOR fix-caused)', async () => {
    const idArticuloFantasma = articuloFixture().id // 50 — coincide a propósito con la fila PERSISTIDA de PV Norte
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/articulos')) return Promise.resolve({ items: [articuloFixture()], total: 1, pagina: 1, tamanio: 25 })
      if (ruta.startsWith('/reportes/stock/existencias?idPuntoVenta=11'))
        return Promise.resolve(existenciasFixture([filaFixture({ idArticulo: idArticuloFantasma, nombre: 'Arroz persistido en Norte' })], 11))
      if (ruta.startsWith('/reportes/stock/existencias?')) return Promise.resolve(existenciasFixture())
      return undefined
    })
    const usuario = userEvent.setup()
    renderExistencias()

    await screen.findByText('Yerba mate 1kg')
    await usuario.type(screen.getByLabelText('Buscar artículo para agregar'), 'arroz')
    await usuario.click(await screen.findByText('ART-50 — Arroz largo fino 1kg'))
    // Fila local sin guardar en PV Centro (idPuntoVenta 10) — `filaLocalSinGuardarRef.current := 50`.
    expect(await screen.findByText('Arroz largo fino 1kg')).toBeInTheDocument()

    // Cambia de PV SIN guardar: `cargar()` reemplaza la grilla entera con la de PV Norte, que trae
    // una fila PERSISTIDA con el MISMO idArticulo (50) que el ref fantasma todavía recuerda.
    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '11')
    expect(await screen.findByText('Arroz persistido en Norte')).toBeInTheDocument()

    // Editar + Cancelar sobre esa fila PERSISTIDA: si el ref no se limpió al recargar, `cancelarEdicion`
    // la matchea por idArticulo y la borra — corrompiendo una fila real del nuevo punto de venta.
    await usuario.click(screen.getByRole('button', { name: 'Editar' }))
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.getByText('Arroz persistido en Norte')).toBeInTheDocument()
  })

  it('cancelar Editar sobre una fila agregada por el picker y YA GUARDADA no la borra de la grilla (judgment-day round 2, FINDING 2)', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/articulos')) return Promise.resolve({ items: [articuloFixture()], total: 1, pagina: 1, tamanio: 25 })
      return undefined
    })
    apiPutMock.mockResolvedValue(minimosFixture({ idArticulo: articuloFixture().id, cantidad: 0, minimo: null, reposicion: null, estado: 'SinMinimo' }))
    const usuario = userEvent.setup()
    renderExistencias()

    await screen.findByText('Yerba mate 1kg')
    await usuario.type(screen.getByLabelText('Buscar artículo para agregar'), 'arroz')
    await usuario.click(await screen.findByText('ART-50 — Arroz largo fino 1kg'))
    await screen.findByText('Arroz largo fino 1kg')

    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))
    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    await waitFor(() => expect(screen.getAllByRole('button', { name: 'Editar' })).toHaveLength(2)) // vuelve a modo lectura tras guardar

    const filas = screen.getAllByRole('row')
    const filaArroz = within(filas.find((f) => within(f).queryByText('Arroz largo fino 1kg') !== null)!)
    await usuario.click(filaArroz.getByRole('button', { name: 'Editar' }))
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.getByText('Arroz largo fino 1kg')).toBeInTheDocument()
  })

  it('agregar una fila fantasma X y guardar una fila Y preexistente distinta no la huerfaniza — Cancelar todavía la saca (judgment-day round A, FINDING 1 CRITICAL)', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/articulos')) return Promise.resolve({ items: [articuloFixture()], total: 1, pagina: 1, tamanio: 25 })
      return undefined
    })
    apiPutMock.mockResolvedValue(minimosFixture({ idArticulo: 100, cantidad: 42.5, minimo: 5, reposicion: null, estado: 'Bajo' }))
    const usuario = userEvent.setup()
    renderExistencias()

    await screen.findByText('Yerba mate 1kg')

    // Agrega X (idArticulo 50, "Arroz largo fino 1kg") por el picker — fila fantasma, se abre sola
    // para edición: `filaLocalSinGuardarRef.current := 50`.
    await usuario.type(screen.getByLabelText('Buscar artículo para agregar'), 'arroz')
    await usuario.click(await screen.findByText('ART-50 — Arroz largo fino 1kg'))
    expect(await screen.findByText('Arroz largo fino 1kg')).toBeInTheDocument()

    // Con X todavía en edición (y sin guardar), abre Y — la fila PREEXISTENTE "Yerba mate 1kg"
    // (idArticulo 100) — y la guarda de punta a punta. `abrirFila` no exige que ninguna otra fila
    // esté cerrada, solo que no haya un guardado en vuelo.
    await usuario.click(screen.getByRole('button', { name: 'Editar' })) // única "Editar" visible: X muestra Guardar/Cancelar
    await usuario.type(screen.getByLabelText('Mínimo de Yerba mate 1kg'), '5')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    expect(apiPutMock).toHaveBeenCalledWith('/stock/minimos', { idPuntoVenta: 10, idArticulo: 100, minimo: 5, reposicion: null })
    await screen.findByText('Bajo')

    // El fantasma X NO se lo llevó el clear del success-path de Y: sigue renderizado.
    expect(screen.getByText('Arroz largo fino 1kg')).toBeInTheDocument()

    // Reabre X y Cancelar: como nunca se guardó, tiene que desaparecer de la grilla.
    const filas = screen.getAllByRole('row')
    const filaArroz = within(filas.find((f) => within(f).queryByText('Arroz largo fino 1kg') !== null)!)
    await usuario.click(filaArroz.getByRole('button', { name: 'Editar' }))
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByText('Arroz largo fino 1kg')).not.toBeInTheDocument()
  })
})

describe('Existencias — role gating (spec: A Supervisor Exports Existencias)', () => {
  it('un Supervisor llega a /reportes/existencias', async () => {
    usuarioActual = usuarioFixture({ id: 9, usuario: 'supervisor', mail: 'supervisor@ways.test', rolId: ROL.Supervisor, rol: 'Supervisor' })
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

describe('Existencias — gate de escritura (judgment-day round A, FINDING 2)', () => {
  it('un Supervisor lee las columnas pero no ve ninguna acción de escritura: ni "Editar" ni el picker de alta', async () => {
    usuarioActual = usuarioFixture({ id: 9, usuario: 'supervisor', mail: 'supervisor@ways.test', rolId: ROL.Supervisor, rol: 'Supervisor' })
    mockearRutasBase()
    renderExistencias()

    await screen.findByText('Yerba mate 1kg')
    expect(screen.getByText('42,5')).toBeInTheDocument() // la lectura de columnas sigue intacta
    expect(screen.queryByRole('button', { name: 'Editar' })).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Buscar artículo para agregar')).not.toBeInTheDocument()
  })

  it('un Admin sí ve "Editar" y el picker de alta', async () => {
    usuarioActual = usuarioFixture({ id: 1, usuario: 'admin', mail: 'admin@ways.test', rolId: ROL.Admin, rol: 'Admin' })
    mockearRutasBase()
    renderExistencias()

    await screen.findByText('Yerba mate 1kg')
    expect(screen.getByRole('button', { name: 'Editar' })).toBeInTheDocument()
    expect(screen.getByLabelText('Buscar artículo para agregar')).toBeInTheDocument()
  })
})
