import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Auditoria, etiquetaDeAccion } from './Auditoria'
import type { FilaDeAuditoria, PaginaDeAuditoria, PuntoVentaListado } from '../api/tipos'

const apiGetMock = vi.fn()
const apiDescargarMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: vi.fn(),
    put: vi.fn(),
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
  nombreTenant: 'Tenant Demo',
  razonSocialEmpresa: 'Empresa Demo',
}

function filaFixture(sobrescribir: Partial<FilaDeAuditoria> = {}): FilaDeAuditoria {
  return {
    idAuditoria: 500,
    creadoEl: '2026-08-14T12:00:00Z',
    accion: 'precio.cambio',
    entidad: 'articulo',
    idEntidad: 41,
    idActor: 2,
    actor: 'admin',
    idPuntoVenta: 10,
    valorAnterior: { monto: 100 },
    valorNuevo: { monto: 150 },
    ...sobrescribir,
  }
}

function paginaFixture(items: FilaDeAuditoria[] = [filaFixture()], sobrescribir: Partial<PaginaDeAuditoria> = {}): PaginaDeAuditoria {
  return { items, total: items.length, pagina: 1, tamanio: 25, ...sobrescribir }
}

function renderAuditoria() {
  return render(<Auditoria />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

function mockearRutasBase(sobrescribir?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/puntos-venta') return Promise.resolve([puntoVentaCentro])
    const propia = sobrescribir?.(ruta)
    if (propia) return propia
    if (ruta.startsWith('/auditoria?')) return Promise.resolve(paginaFixture())
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiDescargarMock.mockReset()
  apiDescargarMock.mockResolvedValue(undefined)
})

// ---- etiquetaDeAccion: helper puro, sin DOM (web-descriptor-tests) ----------------------------

describe('etiquetaDeAccion', () => {
  it('una acción del catálogo devuelve su etiqueta en español', () => {
    expect(etiquetaDeAccion('precio.cambio')).toBe('Cambio de precio')
  })

  // judgment-day ronda 2, juez A: el fallback `?? accion` (design decisión 15 — "una acción
  // retirada deja rastro consultable") no tenía test. Mutation target: `?? accion` → `?? '—'` (o
  // cualquier valor fijo) debe hacer fallar este test.
  it('una acción NO catalogada (retirada) devuelve el código crudo, no un placeholder', () => {
    expect(etiquetaDeAccion('modulo.retirado')).toBe('modulo.retirado')
  })
})

describe('Auditoria (stage-14-auditoria-trazabilidad, Slice 7)', () => {
  it('cambiar cualquier filtro resetea la página a 1', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/auditoria?')) {
        return Promise.resolve(paginaFixture([filaFixture()], { total: 60, pagina: 1, tamanio: 25 }))
      }
      return undefined
    })
    const usuario = userEvent.setup()
    renderAuditoria()

    await screen.findByText('#41')
    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))

    await waitFor(() => {
      const llamadas = apiGetMock.mock.calls.filter((call: unknown[]) => (call[0] as string).startsWith('/auditoria?'))
      expect(llamadas.some((call: unknown[]) => (call[0] as string).includes('pagina=2'))).toBe(true)
    })

    apiGetMock.mockClear()
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/auditoria?')) return Promise.resolve(paginaFixture([filaFixture()], { total: 60, pagina: 1, tamanio: 25 }))
      return undefined
    })
    await usuario.selectOptions(screen.getByLabelText('Acción'), 'venta.anulacion')

    await waitFor(() => {
      const llamadas = apiGetMock.mock.calls.filter((call: unknown[]) => (call[0] as string).startsWith('/auditoria?'))
      expect(
        llamadas.some(
          (call: unknown[]) => (call[0] as string).includes('accion=venta.anulacion') && (call[0] as string).includes('pagina=1'),
        ),
      ).toBe(true)
    })
  })

  // react-async-state regla 7 / mutation-proof-tests regla 7: la promesa desactualizada se
  // resuelve DENTRO de act y se asserta sincrónicamente después del flush.
  it('una respuesta desactualizada nunca pisa la más reciente (generación)', async () => {
    let resolverPrimera: (valor: PaginaDeAuditoria) => void = () => {}
    const primera = new Promise<PaginaDeAuditoria>((resolve) => {
      resolverPrimera = resolve
    })
    let cantidadDeLlamadas = 0

    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/auditoria?')) {
        cantidadDeLlamadas += 1
        if (cantidadDeLlamadas === 1) return primera
        return Promise.resolve(paginaFixture([filaFixture({ idAuditoria: 202 })]))
      }
      return undefined
    })

    const usuario = userEvent.setup()
    renderAuditoria()
    await screen.findByLabelText('Acción')

    await usuario.selectOptions(screen.getByLabelText('Acción'), 'venta.anulacion')
    expect(await screen.findByText('#41')).toBeInTheDocument()

    // El flush del microtask va DENTRO de act (mutation-proof-tests regla 7, mismo patrón que
    // Vencimientos.test.tsx): un waitFor solo pasaría en su primer tick, antes de que el .then
    // stale aterrice — envolver en act y assertar sincrónicamente después SÍ discrimina.
    const { act } = await import('@testing-library/react')
    await act(async () => {
      resolverPrimera(paginaFixture([filaFixture({ idAuditoria: 909, idEntidad: 999 })]))
      await primera
    })
    expect(screen.queryByText('#999')).not.toBeInTheDocument()
    expect(screen.getByText('#41')).toBeInTheDocument()
  })

  it('el pager está deshabilitado en ambos bordes (página 1 y última página)', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/auditoria?')) return Promise.resolve(paginaFixture([filaFixture()], { total: 1, pagina: 1, tamanio: 25 }))
      return undefined
    })
    renderAuditoria()

    await screen.findByText('#41')
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()
  })

  // judgment-day ronda 1, finding 3: `total: 1` colapsa página 1 y última página en el mismo
  // fixture — `Math.ceil`→`Math.floor` en `totalPaginas` sobrevivía al único test previo. Fixture
  // multi-página (60 eventos, tamaño 25 ⇒ 3 páginas, la última parcial con 10) navegando hasta la
  // última página discrimina el mutante: con `Math.floor`, `total: 60 / tamanio: 25` da
  // `totalPaginas = 2`, no 3, y "Siguiente" quedaría deshabilitado en la página 2 (falso borde).
  it('en la última página parcial (total 60, tamaño 25 ⇒ 3 páginas), la etiqueta y el disabled de Siguiente son correctos', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/auditoria?')) {
        const paginaSolicitada = ruta.includes('pagina=3') ? 3 : ruta.includes('pagina=2') ? 2 : 1
        return Promise.resolve(paginaFixture([filaFixture()], { total: 60, pagina: paginaSolicitada, tamanio: 25 }))
      }
      return undefined
    })
    const usuario = userEvent.setup()
    renderAuditoria()

    await screen.findByText(/Página 1 de 3/)
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeEnabled()

    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    await screen.findByText(/Página 2 de 3/)
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeEnabled()

    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    await screen.findByText(/Página 3 de 3/)
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()
  })

  it('actor null renderiza #idActor; punto de venta null renderiza — con el título tenant-wide', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/auditoria?')) {
        return Promise.resolve(
          paginaFixture([filaFixture({ idAuditoria: 501, actor: null, idActor: 7, idPuntoVenta: null })]),
        )
      }
      return undefined
    })
    renderAuditoria()

    expect(await screen.findByText('#7')).toBeInTheDocument()
    const celdaPv = screen.getByTitle('Evento de todo el tenant')
    expect(within(celdaPv).getByText('—')).toBeInTheDocument()
    // Sugerencia 1 (judgment-day ronda 1): `etiquetaDeAccion` es reducible a `return accion` sin
    // fallar ningún test previo — la celda de acción debe mostrar la etiqueta en español del
    // catálogo, nunca el código crudo `precio.cambio` (scoped al `<td>`, "Cambio de precio"
    // también existe como `<option>` del filtro).
    expect(screen.getByRole('cell', { name: 'Cambio de precio' })).toBeInTheDocument()
  })

  // judgment-day ronda 2, juez A: el fallback de `etiquetaDeAccion` (`?? accion`, design decisión
  // 15 — "una acción retirada deja rastro consultable") no tenía cobertura de componente. Una fila
  // con una acción no catalogada debe mostrar el código crudo, no desaparecer ni romper el render.
  it('una fila con una acción retirada del catálogo muestra el código crudo en la celda de Acción', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/auditoria?')) {
        return Promise.resolve(paginaFixture([filaFixture({ idAuditoria: 502, accion: 'modulo.retirado' })]))
      }
      return undefined
    })
    renderAuditoria()

    expect(await screen.findByRole('cell', { name: 'modulo.retirado' })).toBeInTheDocument()
  })

  // judgment-day ronda 1, finding 1: `/auditoria/export` exige desde/hasta no vacíos
  // (AuditoriaEndpoints.cs: DateTimeOffset sin `?`) — con cualquiera de las dos fechas vacía, el
  // botón queda deshabilitado con el motivo visible en vez de mandar una descarga que el servidor
  // rechaza.
  it('con Desde o Hasta vacíos, el botón de descarga queda deshabilitado con el motivo visible', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderAuditoria()

    await screen.findByText('#41')
    expect(screen.getByRole('button', { name: 'Descargar' })).toBeEnabled()

    await usuario.clear(screen.getByLabelText('Desde'))

    expect(screen.getByRole('button', { name: 'Descargar' })).toBeDisabled()
    expect(screen.getByText('Completá Desde y Hasta para descargar.')).toBeInTheDocument()

    await usuario.type(screen.getByLabelText('Desde'), '2026-08-05')
    await usuario.clear(screen.getByLabelText('Hasta'))

    expect(screen.getByRole('button', { name: 'Descargar' })).toBeDisabled()
  })

  // judgment-day ronda 1, finding 6: `cambiarEntidad` limpia `idEntidad` cuando se vacía `entidad`
  // (`idEntidad` sin `entidad` es 400 `entidad_requerida` del servidor) — el guard no tenía test;
  // borrar `idEntidad: null` de esa rama sobrevivía.
  it('limpiar Entidad limpia también #Id: el input queda vacío y el request siguiente no manda idEntidad', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderAuditoria()

    await screen.findByText('#41')
    await usuario.type(screen.getByLabelText('Entidad'), 'articulo')
    await usuario.type(screen.getByLabelText('#Id'), '41')
    await waitFor(() => {
      const llamadas = apiGetMock.mock.calls.filter((call: unknown[]) => (call[0] as string).startsWith('/auditoria?'))
      expect(llamadas.some((call: unknown[]) => (call[0] as string).includes('idEntidad=41'))).toBe(true)
    })

    apiGetMock.mockClear()
    mockearRutasBase()
    await usuario.clear(screen.getByLabelText('Entidad'))

    await waitFor(() => {
      const llamadas = apiGetMock.mock.calls.filter((call: unknown[]) => (call[0] as string).startsWith('/auditoria?'))
      expect(llamadas.length).toBeGreaterThan(0)
      expect(llamadas.every((call: unknown[]) => !(call[0] as string).includes('idEntidad='))).toBe(true)
    })
    expect(screen.getByLabelText('#Id')).toHaveValue(null)
  })

  it('el botón de descarga llama a rutasDeExportacion.auditoria con el filtro actual', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderAuditoria()

    await screen.findByText('#41')
    await usuario.selectOptions(screen.getByLabelText('Acción'), 'venta.anulacion')
    await waitFor(() => {
      const llamadas = apiGetMock.mock.calls.filter((call: unknown[]) => (call[0] as string).startsWith('/auditoria?'))
      expect(llamadas.some((call: unknown[]) => (call[0] as string).includes('accion=venta.anulacion'))).toBe(true)
    })

    await usuario.click(screen.getByRole('button', { name: 'Descargar' }))

    expect(apiDescargarMock).toHaveBeenCalledTimes(1)
    const rutaDescargada = apiDescargarMock.mock.calls[0][0] as string
    expect(rutaDescargada).toMatch(/^\/auditoria\/export\?/)
    expect(rutaDescargada).toContain('accion=venta.anulacion')
    expect(rutaDescargada).toContain('formato=xlsx')
  })
})
