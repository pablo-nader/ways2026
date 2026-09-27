import 'fake-indexeddb/auto'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useContext, useState } from 'react'
import type { ReactNode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { BorradorDeTicketContext, ProveedorDeBorradoresDeTicket, claveIndexedDbDeBorrador } from './BorradorDeTicketContext'
import type { BorradorDeTicket } from './BorradorDeTicketContext'
import type { AlmacenDeClavesMultiples, EntradaDeAlmacen } from './almacenPos'
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

/** Mismo `Consumidor` de arriba, pero exponiendo además `almacen.listo` (JD-2) — un componente
 * aparte en vez de agregarle esto al de arriba para no ensuciar los asserts existentes que no les
 * importa el estado de hidratación. */
function ConsumidorListo({ clave }: { clave: string }) {
  const almacen = useContext(BorradorDeTicketContext)
  const [, forzarRerender] = useState(0)
  if (!almacen) return <span>sin-provider</span>

  const encontrado = almacen.obtener(clave)

  return (
    <div>
      <span data-testid="listo">{almacen.listo ? 'listo' : 'cargando'}</span>
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

  it('dos claves distintas no se pisan entre sí (aislamiento por clave, en memoria)', async () => {
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

  it('JD-2: los children se renderizan aunque la hidratación siga en vuelo (una lectura que nunca resuelve no bloquea el árbol)', () => {
    const promesaQueNuncaResuelve = new Promise<Array<EntradaDeAlmacen<unknown>>>(() => {})
    const almacenLento: AlmacenDeClavesMultiples = {
      leer: () => Promise.resolve(null),
      escribir: () => Promise.resolve(true),
      eliminar: () => Promise.resolve(true),
      leerPrefijo: <T,>() => promesaQueNuncaResuelve as Promise<Array<EntradaDeAlmacen<T>>>,
    }

    render(
      <ConAuth usuario={usuarioFixture()}>
        <ProveedorDeBorradoresDeTicket almacen={almacenLento}>
          <span>contenido ajeno al borrador (ej. el resto de Layout)</span>
        </ProveedorDeBorradoresDeTicket>
      </ConAuth>,
    )

    // Sin `await` a propósito: si el `Provider` todavía bloqueara el render mientras la
    // hidratación está en vuelo, este `span` (montado en el MISMO render que el `Provider`)
    // no aparecería nunca en este assert síncrono.
    expect(screen.getByText('contenido ajeno al borrador (ej. el resto de Layout)')).toBeInTheDocument()
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
    await almacen.escribir(claveIndexedDbDeBorrador(usuario, CLAVE), {
      version: 1,
      borrador: { ...borradorDeEjemplo, proximoIdFilaPago: 9 },
    })

    render(
      <ConAuth usuario={usuario}>
        <ProveedorDeBorradoresDeTicket almacen={almacen}>
          <Consumidor clave={CLAVE} />
        </ProveedorDeBorradoresDeTicket>
      </ConAuth>,
    )

    // `waitFor` (no `findByTestId` + assert): el `<span data-testid="estado">` existe desde el
    // PRIMER render ("vacio", JD-2 — los `children` nunca esperan la hidratación), así que hay que
    // esperar a que su CONTENIDO cambie, no solo a que el nodo exista.
    await waitFor(() => expect(screen.getByTestId('estado')).toHaveTextContent('proximoId:9'))
  })

  it('JD-1: un borrador guardado bajo la clave de OTRO usuario nunca se restaura bajo esta sesión (aislamiento estructural por prefijo de clave)', async () => {
    const almacen = crearAlmacenIndexedDb()
    const usuario = usuarioFixture({ id: 10 })
    const otroUsuario = usuarioFixture({ id: 99 })
    await almacen.escribir(claveIndexedDbDeBorrador(otroUsuario, CLAVE), {
      version: 1,
      borrador: { ...borradorDeEjemplo, proximoIdFilaPago: 9 },
    })

    render(
      <ConAuth usuario={usuario}>
        <ProveedorDeBorradoresDeTicket almacen={almacen}>
          <Consumidor clave={CLAVE} />
        </ProveedorDeBorradoresDeTicket>
      </ConAuth>,
    )

    // El borrador de `otroUsuario` NUNCA aparece bajo la sesión de `usuario` — la hidratación solo
    // escanea el prefijo (`claveIndexedDbDeBorrador`) del usuario en curso, así que una clave con
    // otro id de usuario queda directamente fuera del rango que se lee.
    expect(await screen.findByTestId('estado')).toHaveTextContent('vacio')
  })

  it('JD-1: un borrador de otro tenant con el mismo id de usuario tampoco se restaura (el prefijo también incluye el tenant)', async () => {
    const almacen = crearAlmacenIndexedDb()
    const usuario = usuarioFixture({ id: 10, idTenant: 1 })
    const usuarioDeOtroTenant = usuarioFixture({ id: 10, idTenant: 2 })
    await almacen.escribir(claveIndexedDbDeBorrador(usuarioDeOtroTenant, CLAVE), {
      version: 1,
      borrador: { ...borradorDeEjemplo, proximoIdFilaPago: 9 },
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
    await almacen.escribir(claveIndexedDbDeBorrador(usuario, CLAVE), {
      version: 2,
      borrador: { ...borradorDeEjemplo, proximoIdFilaPago: 9 },
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
    await almacen.escribir(claveIndexedDbDeBorrador(usuario, CLAVE), {
      version: 1,
      // El tipo persistido (`BorradorPersistido`) no tiene `precios` — se fuerza el campo acá
      // para simular una escritura que SÍ lo hubiera incluido (el escenario que el discard
      // previene) y probar que el `Provider`, del lado de la LECTURA, nunca lo confía.
      borrador: { ...borradorDeEjemplo, precios: preciosViejos, proximoIdFilaPago: 9 },
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
    const almacenQueFalla: AlmacenDeClavesMultiples = {
      leer: () => Promise.resolve(null),
      escribir: () => Promise.resolve(false),
      eliminar: () => Promise.resolve(false),
      leerPrefijo: () => Promise.reject(new Error('boom')),
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
  function crearAlmacenEnMemoria(): AlmacenDeClavesMultiples & { datos: Map<string, unknown> } {
    const datos = new Map<string, unknown>()
    return {
      datos,
      leer: <T,>(clave: string) => Promise.resolve((datos.get(clave) as T | undefined) ?? null),
      escribir: (clave, valor) => {
        datos.set(clave, valor)
        return Promise.resolve(true)
      },
      eliminar: (clave) => {
        datos.delete(clave)
        return Promise.resolve(true)
      },
      leerPrefijo: <T,>(prefijo: string) =>
        Promise.resolve(
          Array.from(datos.entries())
            .filter(([clave]) => clave.startsWith(prefijo))
            .map(([clave, valor]) => ({ clave, valor: valor as T })),
        ),
    }
  }

  it('guardar persiste después del debounce, no en el mismo tick', async () => {
    vi.useFakeTimers()
    try {
      const almacen = crearAlmacenEnMemoria()
      const usuario = usuarioFixture()
      const claveDb = claveIndexedDbDeBorrador(usuario, CLAVE)

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
      expect(almacen.datos.has(claveDb)).toBe(false)

      await vi.advanceTimersByTimeAsync(400)
      const persistido = almacen.datos.get(claveDb) as { borrador: BorradorDeTicket }
      expect(persistido.borrador.proximoIdFilaPago).toBe(3)
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
      const claveDb = claveIndexedDbDeBorrador(usuario, CLAVE)

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

      const persistido = almacen.datos.get(claveDb) as { borrador: BorradorDeTicket }
      expect(persistido.borrador.proximoIdFilaPago).toBe(3)
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
      expect(almacen.datos.size).toBe(0)
    } finally {
      vi.useRealTimers()
    }
  })
})

describe('BorradorDeTicketContext — aislamiento entre pestañas (JD-1)', () => {
  const CLAVE_A = 'libre:1'
  const CLAVE_B = 'libre:2'

  beforeEach(() => borrarAlmacenOffline())
  afterEach(() => borrarAlmacenOffline())

  it('dos Provider concurrentes del mismo usuario (dos pestañas), cada uno guardando una clave distinta, no se pisan: ambos borradores sobreviven a un restart', async () => {
    const usuario = usuarioFixture()

    function DosPestanas() {
      return (
        <>
          <div data-testid="pestana-a">
            <ProveedorDeBorradoresDeTicket>
              <Consumidor clave={CLAVE_A} />
            </ProveedorDeBorradoresDeTicket>
          </div>
          <div data-testid="pestana-b">
            <ProveedorDeBorradoresDeTicket>
              <Consumidor clave={CLAVE_B} />
            </ProveedorDeBorradoresDeTicket>
          </div>
        </>
      )
    }

    const { unmount } = render(
      <ConAuth usuario={usuario}>
        <DosPestanas />
      </ConAuth>,
    )

    const pestanaA = within(await screen.findByTestId('pestana-a'))
    const pestanaB = within(screen.getByTestId('pestana-b'))

    // Cada "pestaña" guarda SU propia clave — si ambas escribieran el mismo registro entero (el
    // bug de JD-1), la segunda escritura pisaría lo que la primera acababa de guardar.
    await userEvent.click(pestanaA.getByRole('button', { name: 'guardar' }))
    await userEvent.click(pestanaB.getByRole('button', { name: 'guardar' }))

    expect(pestanaA.getByTestId('estado')).toHaveTextContent('proximoId:3')
    expect(pestanaB.getByTestId('estado')).toHaveTextContent('proximoId:3')

    // Desmonta ambas juntas (flush del cleanup de cada una) y remonta — simula "las dos pestañas
    // se cerraron y la app volvió a abrir" — para verificar contra IndexedDB real, no memoria.
    unmount()

    render(
      <ConAuth usuario={usuario}>
        <ProveedorDeBorradoresDeTicket>
          <div data-testid="a">
            <Consumidor clave={CLAVE_A} />
          </div>
          <div data-testid="b">
            <Consumidor clave={CLAVE_B} />
          </div>
        </ProveedorDeBorradoresDeTicket>
      </ConAuth>,
    )

    // `waitFor`, no `findByTestId` + assert: los contenedores existen desde el primer render
    // (JD-2), así que hay que esperar a que el `Provider` termine de hidratar para que el
    // contenido dentro de cada uno deje de ser "vacio".
    await waitFor(() => expect(screen.getByTestId('a')).toHaveTextContent('proximoId:3'))
    await waitFor(() => expect(screen.getByTestId('b')).toHaveTextContent('proximoId:3'))
  })

  it('limpiar en una pestaña borra solo esa clave — la otra clave, escrita por otra pestaña, sobrevive', async () => {
    const usuario = usuarioFixture()

    // Simula dos pestañas ya con un borrador guardado cada una (estado previo a este test).
    const almacenA = crearAlmacenIndexedDb()
    await almacenA.escribir(claveIndexedDbDeBorrador(usuario, CLAVE_A), { version: 1, borrador: borradorDeEjemplo })
    await almacenA.escribir(claveIndexedDbDeBorrador(usuario, CLAVE_B), { version: 1, borrador: borradorDeEjemplo })

    const { unmount } = render(
      <ConAuth usuario={usuario}>
        <ProveedorDeBorradoresDeTicket>
          <Consumidor clave={CLAVE_A} />
        </ProveedorDeBorradoresDeTicket>
      </ConAuth>,
    )

    await waitFor(() => expect(screen.getByTestId('estado')).toHaveTextContent('proximoId:3'))
    await userEvent.click(screen.getByRole('button', { name: 'limpiar' }))
    unmount()

    render(
      <ConAuth usuario={usuario}>
        <ProveedorDeBorradoresDeTicket>
          <div data-testid="a">
            <Consumidor clave={CLAVE_A} />
          </div>
          <div data-testid="b">
            <Consumidor clave={CLAVE_B} />
          </div>
        </ProveedorDeBorradoresDeTicket>
      </ConAuth>,
    )

    await waitFor(() => expect(screen.getByTestId('b')).toHaveTextContent('proximoId:3'))
    expect(screen.getByTestId('a')).toHaveTextContent('vacio')
  })
})

describe('BorradorDeTicketContext — timeout de hidratación (JD-2)', () => {
  const CLAVE = 'libre:7'

  it('hidratación que nunca resuelve: pasado el timeout, listo pasa a true con el almacén vacío', async () => {
    vi.useFakeTimers()
    try {
      const promesaQueNuncaResuelve = new Promise<Array<EntradaDeAlmacen<unknown>>>(() => {})
      const almacenLento: AlmacenDeClavesMultiples = {
        leer: () => Promise.resolve(null),
        escribir: () => Promise.resolve(true),
        eliminar: () => Promise.resolve(true),
        leerPrefijo: <T,>() => promesaQueNuncaResuelve as Promise<Array<EntradaDeAlmacen<T>>>,
      }
      const usuario = usuarioFixture()

      render(
        <ConAuth usuario={usuario}>
          <ProveedorDeBorradoresDeTicket almacen={almacenLento}>
            <ConsumidorListo clave={CLAVE} />
          </ProveedorDeBorradoresDeTicket>
        </ConAuth>,
      )

      expect(screen.getByTestId('listo')).toHaveTextContent('cargando')

      await vi.advanceTimersByTimeAsync(2000)

      await vi.waitFor(() => expect(screen.getByTestId('listo')).toHaveTextContent('listo'))
      expect(screen.getByTestId('estado')).toHaveTextContent('vacio')
    } finally {
      vi.useRealTimers()
    }
  })

  it('una lectura que llega DESPUÉS del timeout no pisa un borrador guardado mientras tanto', async () => {
    vi.useFakeTimers()
    try {
      const usuario = usuarioFixture()
      const claveDb = claveIndexedDbDeBorrador(usuario, CLAVE)
      let resolverLectura: ((entradas: Array<EntradaDeAlmacen<unknown>>) => void) | null = null
      const lecturaControlada = new Promise<Array<EntradaDeAlmacen<unknown>>>((resolve) => {
        resolverLectura = resolve
      })
      const almacenLento: AlmacenDeClavesMultiples = {
        leer: () => Promise.resolve(null),
        escribir: () => Promise.resolve(true),
        eliminar: () => Promise.resolve(true),
        leerPrefijo: <T,>() => lecturaControlada as Promise<Array<EntradaDeAlmacen<T>>>,
      }

      render(
        <ConAuth usuario={usuario}>
          <ProveedorDeBorradoresDeTicket almacen={almacenLento}>
            <ConsumidorListo clave={CLAVE} />
          </ProveedorDeBorradoresDeTicket>
        </ConAuth>,
      )

      await vi.advanceTimersByTimeAsync(2000)
      await vi.waitFor(() => expect(screen.getByTestId('listo')).toHaveTextContent('listo'))

      // El cajero arma un ticket nuevo DESPUÉS de que el timeout ya dio por vencida la
      // hidratación — `mapaRef` ya no está vacío cuando la lectura vieja llega.
      screen.getByRole('button', { name: 'guardar' }).click()
      await vi.advanceTimersByTimeAsync(0)
      expect(screen.getByTestId('estado')).toHaveTextContent('proximoId:3')

      // La lectura vieja llega tarde con un valor (potencialmente desactualizado) para la MISMA
      // clave — si se aplicara igual, pisaría el borrador recién guardado.
      resolverLectura!([{ clave: claveDb, valor: { version: 1, borrador: { ...borradorDeEjemplo, proximoIdFilaPago: 99 } } }])
      await vi.advanceTimersByTimeAsync(0)

      expect(screen.getByTestId('estado')).toHaveTextContent('proximoId:3')
    } finally {
      vi.useRealTimers()
    }
  })
})
