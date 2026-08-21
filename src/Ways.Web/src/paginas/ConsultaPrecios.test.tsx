import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { aResultadoDeConsulta, ConsultaPrecios } from './ConsultaPrecios'
import { ErrorApi } from '../api/cliente'
import type { ArticuloEscaneado, ListaPrecioAsignable, PuntoVentaListado, ResultadoDeResolucion } from '../api/tipos'

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

function puntoVentaFixture(sobrescribir: Partial<PuntoVentaListado> = {}): PuntoVentaListado {
  return {
    id: 7,
    idTenant: 1,
    idEmpresa: 1,
    nombre: 'Local Centro',
    domicilio: null,
    horario: null,
    whatsapp: null,
    instagram: null,
    facebook: null,
    web: null,
    ...sobrescribir,
  }
}

function listaFixture(sobrescribir: Partial<ListaPrecioAsignable> = {}): ListaPrecioAsignable {
  return { id: 3, nombre: 'Lista Mostrador', esDefault: true, ...sobrescribir }
}

function articuloEscaneadoFixture(sobrescribir: Partial<ArticuloEscaneado> = {}): ArticuloEscaneado {
  return { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: '7790001234567', cantidad: 1, ...sobrescribir }
}

function resolucionFixture(sobrescribir: Partial<ResultadoDeResolucion> = {}): ResultadoDeResolucion {
  return {
    idArticulo: 1,
    idListaPrecio: 3,
    precioOriginal: 100,
    precioFinal: 100,
    descuentoUnitario: 0,
    aplicadas: [],
    ...sobrescribir,
  }
}

const puntoVentaCentro = puntoVentaFixture()
const listaMostrador = listaFixture()

function mockPorDefecto() {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/puntos-venta') return Promise.resolve<PuntoVentaListado[]>([puntoVentaCentro])
    if (ruta === '/listas-precio') return Promise.resolve<ListaPrecioAsignable[]>([listaMostrador])
    if (ruta.startsWith('/articulos/escaneo?entrada=')) return Promise.resolve(articuloEscaneadoFixture())
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
  apiPostMock.mockImplementation((ruta: string) => {
    if (ruta === '/ofertas/resolver') return Promise.resolve<ResultadoDeResolucion[]>([resolucionFixture()])
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  localStorage.clear()
  mockPorDefecto()
})

async function renderYEsperarSelectores() {
  const resultado = render(<ConsultaPrecios />)
  await screen.findByRole('option', { name: 'Local Centro' })
  await screen.findByRole('option', { name: 'Lista Mostrador' })
  return resultado
}

async function escanear(codigo: string) {
  const entrada = screen.getByLabelText('Código escaneado')
  await userEvent.type(entrada, codigo)
  fireEvent.keyDown(entrada, { key: 'Enter' })
}

/** Variante sin `userEvent` para usar bajo `vi.useFakeTimers()`: `userEvent.type` depende de
 * temporizadores reales para simular tipeo, así que bajo fake timers se cuelga. Los mocks de
 * `api.get`/`api.post` resuelven de inmediato (promesas ya resueltas), así que alcanza con
 * disparar el evento y dejar correr un par de vueltas del microtask queue dentro de `act` — nunca
 * `findBy*`/`waitFor` de RTL bajo fake timers (su polling interno usa `setTimeout`, que con los
 * timers falseados nunca avanza solo y cuelga el test hasta su timeout). */
async function escanearBajoFakeTimers(codigo: string) {
  const entrada = screen.getByLabelText('Código escaneado')
  await act(async () => {
    fireEvent.change(entrada, { target: { value: codigo } })
    fireEvent.keyDown(entrada, { key: 'Enter' })
    await vi.advanceTimersByTimeAsync(0)
    await Promise.resolve()
    await Promise.resolve()
  })
}

describe('ConsultaPrecios — exactamente dos llamados por escaneo (mutation target 37)', () => {
  it('un escaneo resuelto dispara exactamente un GET de escaneo y un POST de resolución, ningún otro llamado', async () => {
    await renderYEsperarSelectores()
    apiGetMock.mockClear()
    apiPostMock.mockClear()

    await escanear('7790001234567')
    await screen.findByTestId('resultado-resuelto')

    const llamadosGet = apiGetMock.mock.calls.map((c) => c[0])
    expect(llamadosGet).toEqual(['/articulos/escaneo?entrada=7790001234567'])
    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(apiPostMock).toHaveBeenCalledWith('/ofertas/resolver', {
      lineas: [{ idArticulo: 1, idEmpresa: 1, idListaPrecio: 3, cantidad: 1 }],
    })
  })

  it('el input queda vacío y con foco después de la resolución', async () => {
    await renderYEsperarSelectores()
    const entrada = screen.getByLabelText('Código escaneado') as HTMLInputElement

    await escanear('7790001234567')
    await screen.findByTestId('resultado-resuelto')

    expect(entrada.value).toBe('')
    expect(entrada).toHaveFocus()
  })
})

describe('ConsultaPrecios — las cuatro ramas de despliegue (guard enumeration)', () => {
  it('código desconocido (404) muestra "no encontrado" y NO dispara ninguna resolución de precio', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/puntos-venta') return Promise.resolve<PuntoVentaListado[]>([puntoVentaCentro])
      if (ruta === '/listas-precio') return Promise.resolve<ListaPrecioAsignable[]>([listaMostrador])
      if (ruta.startsWith('/articulos/escaneo?entrada=')) {
        return Promise.reject(new ErrorApi(404, 'no_encontrado', 'No se encontró un artículo activo para el código 999.'))
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    await renderYEsperarSelectores()

    await escanear('999')

    await screen.findByTestId('resultado-no-encontrado')
    expect(screen.getByText('No encontrado')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('artículo identificado sin precio vigente (PrecioOriginal null) muestra "consultá en caja" y JAMÁS $0', async () => {
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') {
        return Promise.resolve<ResultadoDeResolucion[]>([resolucionFixture({ precioOriginal: null, precioFinal: null })])
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    await renderYEsperarSelectores()

    await escanear('7790001234567')

    const bloque = await screen.findByTestId('resultado-sin-precio')
    expect(bloque).toHaveTextContent('Consultá en caja')
    expect(bloque.textContent).not.toContain('$0')
    expect(screen.queryByTestId('resultado-resuelto')).not.toBeInTheDocument()
  })

  it('artículo con oferta vigente (Aplicadas.Count > 0) tacha PrecioOriginal y destaca PrecioFinal', async () => {
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') {
        return Promise.resolve<ResultadoDeResolucion[]>([
          resolucionFixture({ precioOriginal: 150, precioFinal: 120, aplicadas: [{ idOferta: 1, nombre: '20% OFF', descuentoUnitario: 30 }] }),
        ])
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    await renderYEsperarSelectores()

    await escanear('7790001234567')

    await screen.findByTestId('resultado-resuelto')
    expect(screen.getByTestId('precio-original-tachado')).toHaveTextContent('$150,00')
    expect(screen.getByTestId('precio-final')).toHaveTextContent('$120,00')
  })

  it('artículo sin oferta muestra un único precio, sin tachado', async () => {
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') {
        return Promise.resolve<ResultadoDeResolucion[]>([resolucionFixture({ precioOriginal: 100, precioFinal: 100, aplicadas: [] })])
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    await renderYEsperarSelectores()

    await escanear('7790001234567')

    await screen.findByTestId('resultado-resuelto')
    expect(screen.queryByTestId('precio-original-tachado')).not.toBeInTheDocument()
    expect(screen.getByTestId('precio-final')).toHaveTextContent('$100,00')
  })
})

describe('aResultadoDeConsulta — mapper puro (web-descriptor-tests)', () => {
  it('PrecioOriginal/PrecioFinal null ⇒ sin_precio', () => {
    const r = aResultadoDeConsulta(articuloEscaneadoFixture(), resolucionFixture({ precioOriginal: null, precioFinal: null }))
    expect(r).toEqual({ tipo: 'sin_precio', nombre: 'Coca Cola 1L', codigoInterno: 'A0001' })
  })

  it('Aplicadas no vacía ⇒ resuelto con conOferta true', () => {
    const r = aResultadoDeConsulta(
      articuloEscaneadoFixture(),
      resolucionFixture({ precioOriginal: 150, precioFinal: 120, aplicadas: [{ idOferta: 1, nombre: 'x', descuentoUnitario: 30 }] }),
    )
    expect(r).toEqual({ tipo: 'resuelto', nombre: 'Coca Cola 1L', codigoInterno: 'A0001', precioOriginal: 150, precioFinal: 120, conOferta: true })
  })

  it('Aplicadas vacía ⇒ resuelto con conOferta false', () => {
    const r = aResultadoDeConsulta(articuloEscaneadoFixture(), resolucionFixture({ precioOriginal: 100, precioFinal: 100, aplicadas: [] }))
    expect((r as { conOferta: boolean }).conOferta).toBe(false)
  })
})

describe('ConsultaPrecios — reset por inactividad (design decisión 14, mutation targets 35/36)', () => {
  it('a los 19.9s no hubo reset; a los 20.0s el resultado desaparece y el input recupera el foco', async () => {
    await renderYEsperarSelectores()

    // Los fake timers se instalan ANTES del primer escaneo: el `setTimeout` del efecto de reset
    // tiene que nacer como timer falso desde el vamos — instalarlos DESPUÉS de un escaneo hecho
    // con temporizadores reales deja corriendo un `setTimeout` real que `vi.advanceTimersByTimeAsync`
    // no puede tocar (el bug que este orden evita).
    vi.useFakeTimers()
    try {
      await escanearBajoFakeTimers('7790001234567')
      expect(screen.getByTestId('resultado-resuelto')).toBeInTheDocument()

      await act(async () => {
        await vi.advanceTimersByTimeAsync(19_900)
      })
      expect(screen.getByTestId('resultado-resuelto')).toBeInTheDocument()

      await act(async () => {
        await vi.advanceTimersByTimeAsync(100)
      })
      expect(screen.queryByTestId('resultado-resuelto')).not.toBeInTheDocument()
      expect(screen.getByLabelText('Código escaneado')).toHaveFocus()
    } finally {
      vi.useRealTimers()
    }
  })

  it('un segundo escaneo antes de los 20s reemplaza el resultado y el timer reinicia (target 35: la limpieza del efecto cancela el timer viejo estructuralmente)', async () => {
    await renderYEsperarSelectores()

    vi.useFakeTimers()
    try {
      await escanearBajoFakeTimers('7790001234567')
      expect(screen.getByTestId('resultado-resuelto')).toHaveTextContent('Coca Cola 1L')

      await act(async () => {
        await vi.advanceTimersByTimeAsync(15_000)
      })
      expect(screen.getByTestId('resultado-resuelto')).toBeInTheDocument()

      apiGetMock.mockImplementationOnce((ruta: string) => {
        if (ruta.startsWith('/articulos/escaneo?entrada=')) {
          return Promise.resolve(articuloEscaneadoFixture({ idArticulo: 2, nombre: 'Sprite 1L', codigoInterno: 'A0002' }))
        }
        return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
      })

      await escanearBajoFakeTimers('7790007654321')
      expect(screen.getByTestId('resultado-resuelto')).toHaveTextContent('Sprite 1L')

      // El timer viejo (arrancado en t=0, hubiera disparado en t=20s ⇒ a 5s desde acá) fue
      // reemplazado por el nuevo (re-armado en t=15s ⇒ dispara en t=35s desde el origen, o sea a
      // 20s desde acá) — avanzar 5s más no debe apagar nada: si el timer viejo sobreviviera
      // (mutante: cleanup borrado, target 35), acá SÍ dispararía y este assert fallaría.
      await act(async () => {
        await vi.advanceTimersByTimeAsync(5_000)
      })
      expect(screen.getByTestId('resultado-resuelto')).toHaveTextContent('Sprite 1L')

      // El timer nuevo sí dispara a los 20s desde el segundo escaneo (15s ya consumidos arriba).
      await act(async () => {
        await vi.advanceTimersByTimeAsync(15_000)
      })
      expect(screen.queryByTestId('resultado-resuelto')).not.toBeInTheDocument()
    } finally {
      vi.useRealTimers()
    }
  })

  it('una resolución lenta que llega DESPUÉS de que el reset ya disparó no repinta el precio del cliente anterior (bomba de generación, target 37)', async () => {
    await renderYEsperarSelectores()

    vi.useFakeTimers()
    try {
      // Escaneo A: se muestra de inmediato (mock resuelve rápido).
      await escanearBajoFakeTimers('7790001234567')
      expect(screen.getByTestId('resultado-resuelto')).toHaveTextContent('Coca Cola 1L')

      // Escaneo B arranca ANTES de que el reset de A dispare, pero su resolución de precio queda
      // deliberadamente COLGADA (promesa controlada a mano) — simula una corrida lenta.
      let resolverOfertaB: (r: ResultadoDeResolucion[]) => void = () => {}
      const resolucionBPendiente = new Promise<ResultadoDeResolucion[]>((resolve) => {
        resolverOfertaB = resolve
      })
      apiGetMock.mockImplementationOnce((ruta: string) => {
        if (ruta.startsWith('/articulos/escaneo?entrada=')) {
          return Promise.resolve(articuloEscaneadoFixture({ idArticulo: 2, nombre: 'Sprite 1L', codigoInterno: 'A0002' }))
        }
        return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
      })
      apiPostMock.mockImplementationOnce((ruta: string) => {
        if (ruta === '/ofertas/resolver') return resolucionBPendiente
        return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
      })

      const entrada = screen.getByLabelText('Código escaneado')
      await act(async () => {
        fireEvent.change(entrada, { target: { value: '7790007654321' } })
        fireEvent.keyDown(entrada, { key: 'Enter' })
        await vi.advanceTimersByTimeAsync(0)
      })

      // `resultado` sigue mostrando A: el efecto de reset (keyed en `resultado`) no cambió de
      // valor todavía, así que el timer original de A sigue corriendo sin que el escaneo B lo
      // haya tocado — exactamente la premisa de decisión 14 ("keyed en la resolución").
      expect(screen.getByTestId('resultado-resuelto')).toHaveTextContent('Coca Cola 1L')

      // Avanzar los 20s completos desde el escaneo A: el reset dispara, limpia el resultado y
      // bombea la generación — MIENTRAS el resolver de B sigue colgado.
      await act(async () => {
        await vi.advanceTimersByTimeAsync(20_000)
      })
      expect(screen.queryByTestId('resultado-resuelto')).not.toBeInTheDocument()

      // Ahora la resolución tardía de B finalmente llega — con la bomba de generación intacta,
      // su generación capturada (anterior al reset) ya no coincide con la actual y se descarta.
      await act(async () => {
        resolverOfertaB([resolucionFixture({ idArticulo: 2, precioOriginal: 90, precioFinal: 90 })])
        await Promise.resolve()
        await Promise.resolve()
        await Promise.resolve()
      })

      expect(screen.queryByTestId('resultado-resuelto')).not.toBeInTheDocument()
      expect(screen.queryByText('Sprite 1L')).not.toBeInTheDocument()

      // El finally de esa corrida colgada acaba de correr con la generación ya rota por el reset
      // — si `setBuscando(false)` siguiera gateado por esa misma generación (el bug real, judgment-day
      // slice 4 ronda 2 juez A), `buscando` quedaría pegado en `true` PARA SIEMPRE y la pantalla
      // muerta. Con el fix (`setBuscando(false)` incondicional), input y botón vuelven a habilitarse.
      const entradaTrasReset = screen.getByLabelText('Código escaneado') as HTMLInputElement
      expect(entradaTrasReset).not.toBeDisabled()
      expect(screen.getByRole('button', { name: 'Consultar' })).not.toBeDisabled()

      // Y la prueba de que la pantalla no quedó muerta: un escaneo NUEVO después de esto funciona
      // de punta a punta (1 GET + 1 POST nuevos, con resultado pintado).
      apiGetMock.mockClear()
      apiPostMock.mockClear()
      apiGetMock.mockImplementationOnce((ruta: string) => {
        if (ruta.startsWith('/articulos/escaneo?entrada=')) {
          return Promise.resolve(articuloEscaneadoFixture({ idArticulo: 3, nombre: 'Fanta 1L', codigoInterno: 'A0003' }))
        }
        return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
      })
      apiPostMock.mockImplementationOnce((ruta: string) => {
        if (ruta === '/ofertas/resolver') {
          return Promise.resolve<ResultadoDeResolucion[]>([resolucionFixture({ idArticulo: 3, precioOriginal: 80, precioFinal: 80 })])
        }
        return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
      })

      await escanearBajoFakeTimers('7790009999999')

      expect(apiGetMock).toHaveBeenCalledTimes(1)
      expect(apiPostMock).toHaveBeenCalledTimes(1)
      expect(screen.getByTestId('resultado-resuelto')).toHaveTextContent('Fanta 1L')

      // Los asserts previos siguen vigentes: el escaneo B (descartado por el reset) nunca repintó.
      expect(screen.queryByText('Sprite 1L')).not.toBeInTheDocument()
    } finally {
      vi.useRealTimers()
    }
  })

  it('un GET de escaneo lento cuyo reset dispara ANTES de resolver JAMÁS llega a invocar la resolución de precio (guard intermedio, judgment-day slice 4 ronda 1 juez B)', async () => {
    await renderYEsperarSelectores()

    vi.useFakeTimers()
    try {
      // Escaneo A: se muestra de inmediato y arma el timer de reset keyed en `resultado`.
      await escanearBajoFakeTimers('7790001234567')
      expect(screen.getByTestId('resultado-resuelto')).toHaveTextContent('Coca Cola 1L')

      // Escaneo B bombea la generación pero su GET de escaneo (`clienteDeArticulos.escanear`)
      // queda deliberadamente COLGADO en una promesa controlada a mano — corrida lenta ANTES de
      // siquiera identificar el artículo, no en la resolución de precio (a diferencia del test de
      // arriba, que cuelga el POST).
      let resolverEscaneoB: (a: ArticuloEscaneado) => void = () => {}
      const escaneoBPendiente = new Promise<ArticuloEscaneado>((resolve) => {
        resolverEscaneoB = resolve
      })
      apiGetMock.mockImplementationOnce((ruta: string) => {
        if (ruta.startsWith('/articulos/escaneo?entrada=')) return escaneoBPendiente
        return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
      })

      const entrada = screen.getByLabelText('Código escaneado')
      await act(async () => {
        fireEvent.change(entrada, { target: { value: '7790007654321' } })
        fireEvent.keyDown(entrada, { key: 'Enter' })
        await vi.advanceTimersByTimeAsync(0)
      })

      // El reset de A dispara MIENTRAS el GET de B sigue pendiente: bombea la generación y limpia
      // el resultado antes de que B sepa siquiera qué artículo escaneó.
      await act(async () => {
        await vi.advanceTimersByTimeAsync(20_000)
      })
      expect(screen.queryByTestId('resultado-resuelto')).not.toBeInTheDocument()

      apiPostMock.mockClear()

      // El GET colgado de B finalmente resuelve — con el guard intermedio (entre escanear() y
      // resolver()) intacto, la generación capturada por B ya no coincide con la actual y la
      // corrida se aborta ANTES de invocar `clienteDeOfertas.resolver`: cero llamadas de POST,
      // nunca una silenciosa que el repintado posterior tapa.
      await act(async () => {
        resolverEscaneoB(articuloEscaneadoFixture({ idArticulo: 2, nombre: 'Sprite 1L', codigoInterno: 'A0002' }))
        await Promise.resolve()
        await Promise.resolve()
        await Promise.resolve()
      })

      expect(apiPostMock).not.toHaveBeenCalled()
      expect(screen.queryByTestId('resultado-resuelto')).not.toBeInTheDocument()
      expect(screen.queryByText('Sprite 1L')).not.toBeInTheDocument()

      // Misma carrera que arriba pero con el GET (no el POST) colgado: el finally corre con la
      // generación ya rota por el reset — `setBuscando(false)` incondicional evita que la pantalla
      // quede pegada en "Buscando…" para siempre (judgment-day slice 4 ronda 2 juez A).
      const entradaTrasReset = screen.getByLabelText('Código escaneado') as HTMLInputElement
      expect(entradaTrasReset).not.toBeDisabled()
      expect(screen.getByRole('button', { name: 'Consultar' })).not.toBeDisabled()

      // Prueba de punta a punta de que la pantalla sigue viva: un escaneo nuevo dispara exactamente
      // un GET y un POST nuevos, con el resultado pintado.
      apiGetMock.mockClear()
      apiPostMock.mockClear()
      apiGetMock.mockImplementationOnce((ruta: string) => {
        if (ruta.startsWith('/articulos/escaneo?entrada=')) {
          return Promise.resolve(articuloEscaneadoFixture({ idArticulo: 3, nombre: 'Fanta 1L', codigoInterno: 'A0003' }))
        }
        return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
      })
      apiPostMock.mockImplementationOnce((ruta: string) => {
        if (ruta === '/ofertas/resolver') {
          return Promise.resolve<ResultadoDeResolucion[]>([resolucionFixture({ idArticulo: 3, precioOriginal: 80, precioFinal: 80 })])
        }
        return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
      })

      await escanearBajoFakeTimers('7790009999999')

      expect(apiGetMock).toHaveBeenCalledTimes(1)
      expect(apiPostMock).toHaveBeenCalledTimes(1)
      expect(screen.getByTestId('resultado-resuelto')).toHaveTextContent('Fanta 1L')

      // Los asserts previos siguen vigentes: el escaneo B (descartado por el guard intermedio)
      // nunca disparó el POST ni repintó.
      expect(screen.queryByText('Sprite 1L')).not.toBeInTheDocument()
    } finally {
      vi.useRealTimers()
    }
  })
})

describe('ConsultaPrecios — reentrancia de consultar() (guard `buscando`, judgment-day slice 4 ronda 1 juez B)', () => {
  it('un segundo Enter disparado mientras el primer escaneo sigue en vuelo NO dispara una segunda corrida (exactamente un GET y un POST)', async () => {
    await renderYEsperarSelectores()
    const entrada = screen.getByLabelText('Código escaneado')
    apiGetMock.mockClear()
    apiPostMock.mockClear()

    // Los dos Enter se disparan dentro del mismo `act`: un doble click sobre el botón deshabilitado
    // se no-opea en jsdom (no ejercita el guard real), así que la forma honesta de probar
    // `if (buscando) return` es el doble `keyDown` de Enter, que SÍ llega al handler.
    await act(async () => {
      fireEvent.change(entrada, { target: { value: '7790001234567' } })
      fireEvent.keyDown(entrada, { key: 'Enter' })
      await Promise.resolve()
      fireEvent.keyDown(entrada, { key: 'Enter' })
    })

    await screen.findByTestId('resultado-resuelto')

    expect(apiGetMock).toHaveBeenCalledTimes(1)
    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })
})

describe('ConsultaPrecios — ninguna superficie sin sesión (OD2, consulta-de-precios/spec.md:108-112)', () => {
  it('un 401 en el escaneo (sesión inexistente) se muestra como error, sin reintento ni llamado alternativo, y JAMÁS dispara la resolución', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/puntos-venta') return Promise.resolve<PuntoVentaListado[]>([puntoVentaCentro])
      if (ruta === '/listas-precio') return Promise.resolve<ListaPrecioAsignable[]>([listaMostrador])
      if (ruta.startsWith('/articulos/escaneo?entrada=')) return Promise.reject(new ErrorApi(401, 'no_autenticado', 'Tu sesión expiró.'))
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    await renderYEsperarSelectores()
    apiGetMock.mockClear()

    await escanear('7790001234567')

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Tu sesión expiró.'))
    expect(apiGetMock).toHaveBeenCalledTimes(1)
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('un 401 en la resolución de precio (sesión inexistente) se muestra como error y no repinta ningún precio', async () => {
    apiPostMock.mockImplementation((ruta: string) => {
      if (ruta === '/ofertas/resolver') return Promise.reject(new ErrorApi(401, 'no_autenticado', 'Tu sesión expiró.'))
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    await renderYEsperarSelectores()

    await escanear('7790001234567')

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Tu sesión expiró.'))
    expect(screen.queryByTestId('resultado-resuelto')).not.toBeInTheDocument()
  })
})
