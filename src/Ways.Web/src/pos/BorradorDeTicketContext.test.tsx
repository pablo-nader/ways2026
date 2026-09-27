import 'fake-indexeddb/auto'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useContext, useState } from 'react'
import type { ReactNode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { BorradorDeTicketContext, ProveedorDeBorradoresDeTicket } from './BorradorDeTicketContext'
import type { BorradorDeTicket } from './BorradorDeTicketContext'
import type { AlmacenClaveValor } from './almacenPos'
import { crearAlmacenIndexedDb } from './almacenPos'
import { AuthContext } from '../auth/AuthContext'
import type { UsuarioAutenticado } from '../api/tipos'

const borradorDeEjemplo: BorradorDeTicket = {
  lineas: [],
  precios: {},
  cantidadesEnEdicion: {},
  filasPago: [],
  proximoIdFilaPago: 3,
  clienteSeleccionado: null,
}

function usuarioFixture(sobrescribir: Partial<UsuarioAutenticado> = {}): UsuarioAutenticado {
  return {
    id: 10,
    usuario: 'cajera1',
    mail: 'cajera1@ways.test',
    rolId: 3,
    rol: 'Vendedor',
    ultimaConexion: null,
    idTenant: 1,
    ...sobrescribir,
  }
}

/** Envuelve con un `AuthContext.Provider` fijo — mismo criterio que `ShellPos`/`Layout` (el
 * `Provider` de borradores SIEMPRE cuelga de un `AuthContext` ya resuelto, ver su doc-comment). */
function ConAuth({ usuario, children }: { usuario: UsuarioAutenticado | null; children: ReactNode }) {
  return (
    <AuthContext.Provider
      value={{ usuario, cargando: false, iniciarSesion: () => Promise.reject(new Error('no usado en este test')), cerrarSesion: () => Promise.resolve() }}
    >
      {children}
    </AuthContext.Provider>
  )
}

/** Consumidor mínimo del contexto — expone `obtener`/`guardar`/`limpiar` por botones para poder
 * ejercitar el almacén desde tests sin depender de `Pos.tsx`. El almacén vive en un `ref` a
 * propósito (nunca fuerza un re-render por sí solo — ver el doc-comment del `Provider`), así que
 * este consumidor se re-renderiza con un contador local cada vez que muta el store, para poder
 * leerlo de nuevo. */
function Consumidor({ clave }: { clave: string }) {
  const almacen = useContext(BorradorDeTicketContext)
  const [, forzarRerender] = useState(0)
  if (!almacen) return <span>sin-provider</span>

  const encontrado = almacen.obtener(clave)

  return (
    <div>
      <span data-testid="estado">{encontrado ? `proximoId:${encontrado.proximoIdFilaPago}` : 'vacio'}</span>
      <button
        type="button"
        onClick={() => {
          almacen.guardar(clave, borradorDeEjemplo)
          forzarRerender((n) => n + 1)
        }}
      >
        guardar
      </button>
      <button
        type="button"
        onClick={() => {
          almacen.limpiar(clave)
          forzarRerender((n) => n + 1)
        }}
      >
        limpiar
      </button>
    </div>
  )
}

describe('BorradorDeTicketContext', () => {
  it('sin Provider en el árbol, el contexto es null (no-op)', () => {
    render(<Consumidor clave="libre:1" />)
    expect(screen.getByText('sin-provider')).toBeInTheDocument()
  })

  it('guardar y luego obtener devuelve exactamente el borrador guardado', async () => {
    render(
      <ProveedorDeBorradoresDeTicket>
        <Consumidor clave="libre:1" />
      </ProveedorDeBorradoresDeTicket>,
    )
    expect(await screen.findByTestId('estado')).toHaveTextContent('vacio')

    await userEvent.click(screen.getByRole('button', { name: 'guardar' }))
    expect(screen.getByTestId('estado')).toHaveTextContent('proximoId:3')
  })

  it('limpiar borra el borrador de esa clave', async () => {
    render(
      <ProveedorDeBorradoresDeTicket>
        <Consumidor clave="libre:1" />
      </ProveedorDeBorradoresDeTicket>,
    )
    await screen.findByTestId('estado')
    await userEvent.click(screen.getByRole('button', { name: 'guardar' }))
    expect(screen.getByTestId('estado')).toHaveTextContent('proximoId:3')

    await userEvent.click(screen.getByRole('button', { name: 'limpiar' }))
    expect(screen.getByTestId('estado')).toHaveTextContent('vacio')
  })

  it('dos claves distintas no se pisan entre sí (aislamiento por clave)', async () => {
    function DosConsumidores() {
      return (
        <>
          <div data-testid="a">
            <Consumidor clave="libre:1" />
          </div>
          <div data-testid="b">
            <Consumidor clave="libre:2" />
          </div>
        </>
      )
    }

    render(
      <ProveedorDeBorradoresDeTicket>
        <DosConsumidores />
      </ProveedorDeBorradoresDeTicket>,
    )

    const contenedorA = await screen.findByTestId('a')
    const contenedorB = screen.getByTestId('b')

    await userEvent.click(contenedorA.querySelector('button')!)

    expect(contenedorA.querySelector('[data-testid="estado"]')).toHaveTextContent('proximoId:3')
    expect(contenedorB.querySelector('[data-testid="estado"]')).toHaveTextContent('vacio')
  })
})

/** Borra la base fake entre tests — sin esto, un borrador persistido por un test filtraría al
 * siguiente (el mismo `fake-indexeddb` vive para todo el archivo). Mismo criterio que
 * `Pos.test.tsx`/`CierreDeCaja.test.tsx`. */
function borrarAlmacenOffline(): Promise<void> {
  return new Promise((resolve) => {
    const solicitud = indexedDB.deleteDatabase('ways-pos-offline')
    solicitud.onsuccess = () => resolve()
    solicitud.onerror = () => resolve()
    solicitud.onblocked = () => resolve()
  })
}

describe('BorradorDeTicketContext — persistencia en IndexedDB (stage-pos-borrador-persistente)', () => {
  const CLAVE = 'libre:7'

  beforeEach(() => borrarAlmacenOffline())
  afterEach(() => borrarAlmacenOffline())

  it('hidrata desde IndexedDB antes de renderizar los hijos: un borrador guardado por una sesión previa aparece en el primer render útil', async () => {
    const almacen = crearAlmacenIndexedDb()
    const usuario = usuarioFixture()
    await almacen.escribir('borradores-ticket', {
      version: 1,
      idUsuario: usuario.id,
      idTenant: usuario.idTenant,
      borradores: { [CLAVE]: { ...borradorDeEjemplo, proximoIdFilaPago: 9 } },
    })

    render(
      <ConAuth usuario={usuario}>
        <ProveedorDeBorradoresDeTicket almacen={almacen}>
          <Consumidor clave={CLAVE} />
        </ProveedorDeBorradoresDeTicket>
      </ConAuth>,
    )

    expect(await screen.findByTestId('estado')).toHaveTextContent('proximoId:9')
  })

  it('mutación: sin el chequeo de usuario, un borrador guardado por OTRO usuario se restauraría igual', async () => {
    const almacen = crearAlmacenIndexedDb()
    const usuario = usuarioFixture({ id: 10 })
    const otroUsuario = usuarioFixture({ id: 99 })
    await almacen.escribir('borradores-ticket', {
      version: 1,
      idUsuario: otroUsuario.id,
      idTenant: otroUsuario.idTenant,
      borradores: { [CLAVE]: { ...borradorDeEjemplo, proximoIdFilaPago: 9 } },
    })

    render(
      <ConAuth usuario={usuario}>
        <ProveedorDeBorradoresDeTicket almacen={almacen}>
          <Consumidor clave={CLAVE} />
        </ProveedorDeBorradoresDeTicket>
      </ConAuth>,
    )

    // El borrador de `otroUsuario` NUNCA aparece bajo la sesión de `usuario` — si el chequeo
    // `guardado.idUsuario === usuario.id` se borrara, este assert (`vacio`) pasaría a fallar con
    // `proximoId:9`, confirmando que la mutación mata este test (mutation-proof-tests).
    expect(await screen.findByTestId('estado')).toHaveTextContent('vacio')
  })

  it('mutación: sin el chequeo de tenant, un borrador de otro tenant con el mismo id de usuario se restauraría igual', async () => {
    const almacen = crearAlmacenIndexedDb()
    const usuario = usuarioFixture({ id: 10, idTenant: 1 })
    await almacen.escribir('borradores-ticket', {
      version: 1,
      idUsuario: usuario.id,
      idTenant: 2,
      borradores: { [CLAVE]: { ...borradorDeEjemplo, proximoIdFilaPago: 9 } },
    })

    render(
      <ConAuth usuario={usuario}>
        <ProveedorDeBorradoresDeTicket almacen={almacen}>
          <Consumidor clave={CLAVE} />
        </ProveedorDeBorradoresDeTicket>
      </ConAuth>,
    )

    expect(await screen.findByTestId('estado')).toHaveTextContent('vacio')
  })

  it('mutación: sin el chequeo de versión, un payload de un esquema futuro/desconocido se restauraría igual', async () => {
    const almacen = crearAlmacenIndexedDb()
    const usuario = usuarioFixture()
    await almacen.escribir('borradores-ticket', {
      version: 2,
      idUsuario: usuario.id,
      idTenant: usuario.idTenant,
      borradores: { [CLAVE]: { ...borradorDeEjemplo, proximoIdFilaPago: 9 } },
    })

    render(
      <ConAuth usuario={usuario}>
        <ProveedorDeBorradoresDeTicket almacen={almacen}>
          <Consumidor clave={CLAVE} />
        </ProveedorDeBorradoresDeTicket>
      </ConAuth>,
    )

    expect(await screen.findByTestId('estado')).toHaveTextContent('vacio')
  })

  it('mutación: sin el discard de precios al restaurar, un precio persistido (potencialmente viejo) sobreviviría a la restauración', async () => {
    const almacen = crearAlmacenIndexedDb()
    const usuario = usuarioFixture()
    const preciosViejos = { 1: { idArticulo: 1, idListaPrecio: 1, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [] } }
    await almacen.escribir('borradores-ticket', {
      version: 1,
      idUsuario: usuario.id,
      idTenant: usuario.idTenant,
      // El tipo persistido (`BorradorPersistido`) no tiene `precios` — se fuerza el campo acá
      // para simular una escritura que SÍ lo hubiera incluido (el escenario que el discard
      // previene) y probar que el `Provider`, del lado de la LECTURA, nunca lo confía.
      borradores: { [CLAVE]: { ...borradorDeEjemplo, precios: preciosViejos, proximoIdFilaPago: 9 } },
    })

    function ConsumidorDePrecios({ clave }: { clave: string }) {
      const contexto = useContext(BorradorDeTicketContext)
      const encontrado = contexto?.obtener(clave)
      return <span data-testid="precios">{encontrado ? JSON.stringify(encontrado.precios) : 'sin-cargar'}</span>
    }

    render(
      <ConAuth usuario={usuario}>
        <ProveedorDeBorradoresDeTicket almacen={almacen}>
          <ConsumidorDePrecios clave={CLAVE} />
        </ProveedorDeBorradoresDeTicket>
      </ConAuth>,
    )

    // `{}`: el precio persistido en el fixture (potencialmente viejo) nunca sobrevive la
    // restauración — si el spread `{ ...borrador, precios: {} }` se reemplazara por
    // `{ ...borrador }` a secas (heredando lo que venga en el payload), este assert pasaría a
    // fallar con el objeto `preciosViejos` completo.
    expect(await screen.findByText('{}')).toBeInTheDocument()
  })

  it('lectura fallida degrada a almacén vacío en vez de romper el montaje', async () => {
    const usuario = usuarioFixture()
    const almacenQueFalla: AlmacenClaveValor = {
      leer: () => Promise.resolve(null),
      escribir: () => Promise.resolve(false),
    }

    render(
      <ConAuth usuario={usuario}>
        <ProveedorDeBorradoresDeTicket almacen={almacenQueFalla}>
          <Consumidor clave={CLAVE} />
        </ProveedorDeBorradoresDeTicket>
      </ConAuth>,
    )

    expect(await screen.findByTestId('estado')).toHaveTextContent('vacio')
  })

  /** Fake en memoria (nunca real IndexedDB) para los tests de timing del debounce — combinar
   * `vi.useFakeTimers()` con `fake-indexeddb` real cuelga: sus `IDBRequest` resuelven via un
   * `setTimeout` capturado ANTES de que `vi.useFakeTimers()` reemplace el global, así que
   * `vi.advanceTimersByTimeAsync` nunca lo destraba. Este fake resuelve por microtarea pura
   * (`Promise.resolve()`), que sí avanza con timers falsos — el propio debounce (un `setTimeout`
   * de este módulo) es lo único que hace falta destrabar acá. */
  function crearAlmacenEnMemoria(): AlmacenClaveValor & { datos: Map<string, unknown> } {
    const datos = new Map<string, unknown>()
    return {
      datos,
      leer: <T,>(clave: string) => Promise.resolve((datos.get(clave) as T | undefined) ?? null),
      escribir: (clave, valor) => {
        datos.set(clave, valor)
        return Promise.resolve(true)
      },
    }
  }

  it('guardar persiste después del debounce, no en el mismo tick', async () => {
    vi.useFakeTimers()
    try {
      const almacen = crearAlmacenEnMemoria()
      const usuario = usuarioFixture()

      render(
        <ConAuth usuario={usuario}>
          <ProveedorDeBorradoresDeTicket almacen={almacen}>
            <Consumidor clave={CLAVE} />
          </ProveedorDeBorradoresDeTicket>
        </ConAuth>,
      )
      await vi.waitFor(() => expect(screen.getByTestId('estado')).toHaveTextContent('vacio'))

      screen.getByRole('button', { name: 'guardar' }).click()
      // Todavía no venció el debounce: nada persistido.
      await vi.advanceTimersByTimeAsync(100)
      expect(almacen.datos.has('borradores-ticket')).toBe(false)

      await vi.advanceTimersByTimeAsync(400)
      const persistido = almacen.datos.get('borradores-ticket') as { borradores: Record<string, BorradorDeTicket> }
      expect(persistido.borradores[CLAVE]?.proximoIdFilaPago).toBe(3)
    } finally {
      vi.useRealTimers()
    }
  })

  it('varios guardar seguidos dentro de la ventana de debounce coalescen en una sola escritura', async () => {
    vi.useFakeTimers()
    try {
      const almacen = crearAlmacenEnMemoria()
      const escribirSpy = vi.spyOn(almacen, 'escribir')
      const usuario = usuarioFixture()

      render(
        <ConAuth usuario={usuario}>
          <ProveedorDeBorradoresDeTicket almacen={almacen}>
            <Consumidor clave={CLAVE} />
          </ProveedorDeBorradoresDeTicket>
        </ConAuth>,
      )
      await vi.waitFor(() => expect(screen.getByTestId('estado')).toHaveTextContent('vacio'))

      const boton = screen.getByRole('button', { name: 'guardar' })
      boton.click()
      await vi.advanceTimersByTimeAsync(100)
      boton.click()
      await vi.advanceTimersByTimeAsync(100)
      boton.click()

      await vi.advanceTimersByTimeAsync(400)
      expect(escribirSpy).toHaveBeenCalledTimes(1)
    } finally {
      vi.useRealTimers()
    }
  })

  it('limpiar persiste la baja: un borrador guardado y luego limpiado no reaparece después de un remount (restart simulado)', async () => {
    vi.useFakeTimers()
    try {
      const almacen = crearAlmacenEnMemoria()
      const usuario = usuarioFixture()

      const { unmount } = render(
        <ConAuth usuario={usuario}>
          <ProveedorDeBorradoresDeTicket almacen={almacen}>
            <Consumidor clave={CLAVE} />
          </ProveedorDeBorradoresDeTicket>
        </ConAuth>,
      )
      await vi.waitFor(() => expect(screen.getByTestId('estado')).toHaveTextContent('vacio'))
      screen.getByRole('button', { name: 'guardar' }).click()
      await vi.advanceTimersByTimeAsync(400)
      screen.getByRole('button', { name: 'limpiar' }).click()
      await vi.advanceTimersByTimeAsync(400)
      unmount()

      render(
        <ConAuth usuario={usuario}>
          <ProveedorDeBorradoresDeTicket almacen={almacen}>
            <Consumidor clave={CLAVE} />
          </ProveedorDeBorradoresDeTicket>
        </ConAuth>,
      )
      await vi.waitFor(() => expect(screen.getByTestId('estado')).toHaveTextContent('vacio'))
    } finally {
      vi.useRealTimers()
    }
  })

  it('desmontar el Provider con una escritura pendiente la vuelca igual (flush en el cleanup)', async () => {
    vi.useFakeTimers()
    try {
      const almacen = crearAlmacenEnMemoria()
      const usuario = usuarioFixture()

      const { unmount } = render(
        <ConAuth usuario={usuario}>
          <ProveedorDeBorradoresDeTicket almacen={almacen}>
            <Consumidor clave={CLAVE} />
          </ProveedorDeBorradoresDeTicket>
        </ConAuth>,
      )
      await vi.waitFor(() => expect(screen.getByTestId('estado')).toHaveTextContent('vacio'))
      screen.getByRole('button', { name: 'guardar' }).click()
      // Se desmonta ANTES de que el debounce (400ms) venza — sin el flush del cleanup, esta
      // escritura se perdería enteramente.
      unmount()
      await vi.advanceTimersByTimeAsync(0)

      const persistido = almacen.datos.get('borradores-ticket') as { borradores: Record<string, BorradorDeTicket> }
      expect(persistido.borradores[CLAVE]?.proximoIdFilaPago).toBe(3)
    } finally {
      vi.useRealTimers()
    }
  })

  it('sin usuario autenticado en el AuthContext, el almacén funciona en memoria pero nunca persiste', async () => {
    vi.useFakeTimers()
    try {
      const almacen = crearAlmacenEnMemoria()

      render(
        <ConAuth usuario={null}>
          <ProveedorDeBorradoresDeTicket almacen={almacen}>
            <Consumidor clave={CLAVE} />
          </ProveedorDeBorradoresDeTicket>
        </ConAuth>,
      )
      await vi.waitFor(() => expect(screen.getByTestId('estado')).toHaveTextContent('vacio'))
      screen.getByRole('button', { name: 'guardar' }).click()
      await vi.waitFor(() => expect(screen.getByTestId('estado')).toHaveTextContent('proximoId:3'))

      await vi.advanceTimersByTimeAsync(400)
      expect(almacen.datos.has('borradores-ticket')).toBe(false)
    } finally {
      vi.useRealTimers()
    }
  })
})
