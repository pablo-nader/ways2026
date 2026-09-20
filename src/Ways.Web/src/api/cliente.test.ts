import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const fetchMock = vi.fn()
vi.stubGlobal('fetch', fetchMock)

const { alPerderLaSesion, api, ErrorApi, nombreDeArchivo } = await import('./cliente')
const { establecerTokenDeSesionBearer, tokenDeSesionBearerActual } = await import('./entornoTauri')

type GlobalConTauri = typeof globalThis & { __TAURI__?: unknown }

function instalarPuenteTauri() {
  ;(globalThis as GlobalConTauri).__TAURI__ = { core: { invoke: vi.fn() } }
}

function quitarPuenteTauri() {
  delete (globalThis as GlobalConTauri).__TAURI__
}

function respuestaMock(init: {
  status: number
  ok: boolean
  headers?: Record<string, string>
  blob?: () => Promise<Blob>
  json?: () => Promise<unknown>
}): Response {
  return {
    status: init.status,
    ok: init.ok,
    headers: new Headers(init.headers ?? {}),
    blob: init.blob ?? (() => Promise.resolve(new Blob())),
    json: init.json ?? (() => Promise.resolve({})),
  } as unknown as Response
}

describe('header Authorization bajo Tauri (slice bearer)', () => {
  beforeEach(() => {
    fetchMock.mockReset()
    quitarPuenteTauri()
    establecerTokenDeSesionBearer(null)
  })

  afterEach(() => {
    quitarPuenteTauri()
    establecerTokenDeSesionBearer(null)
  })

  it('fuera de Tauri nunca adjunta Authorization, aunque haya un token guardado', async () => {
    establecerTokenDeSesionBearer('un-token')
    fetchMock.mockResolvedValue(respuestaMock({ status: 200, ok: true, json: () => Promise.resolve({ ok: true }) }))

    await api.get('/algo')

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect((init.headers as Record<string, string>).Authorization).toBeUndefined()
  })

  it('bajo Tauri sin token guardado tampoco adjunta Authorization', async () => {
    instalarPuenteTauri()
    fetchMock.mockResolvedValue(respuestaMock({ status: 200, ok: true, json: () => Promise.resolve({ ok: true }) }))

    await api.get('/algo')

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect((init.headers as Record<string, string>).Authorization).toBeUndefined()
  })

  it('bajo Tauri CON token guardado adjunta Authorization: Bearer <token> en pedir (api.get/post)', async () => {
    instalarPuenteTauri()
    establecerTokenDeSesionBearer('un-token-de-sesion')
    fetchMock.mockResolvedValue(respuestaMock({ status: 200, ok: true, json: () => Promise.resolve({ ok: true }) }))

    await api.get('/algo')

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect((init.headers as Record<string, string>).Authorization).toBe('Bearer un-token-de-sesion')
  })

  it('bajo Tauri CON token guardado adjunta Authorization: Bearer <token> en descargar', async () => {
    instalarPuenteTauri()
    establecerTokenDeSesionBearer('un-token-de-sesion')
    fetchMock.mockResolvedValue(
      respuestaMock({ status: 200, ok: true, blob: () => Promise.resolve(new Blob()) }),
    )

    await api.descargar('/algo/export')

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect((init.headers as Record<string, string>).Authorization).toBe('Bearer un-token-de-sesion')
  })

  it('un 401 limpia el token bearer guardado (ya no sirve)', async () => {
    instalarPuenteTauri()
    establecerTokenDeSesionBearer('un-token-vencido')
    fetchMock.mockResolvedValue(respuestaMock({ status: 401, ok: false }))

    await expect(api.get('/algo')).rejects.toBeInstanceOf(ErrorApi)
    expect(tokenDeSesionBearerActual()).toBeNull()
  })
})

describe('nombreDeArchivo', () => {
  it('filename* (RFC 5987, UTF-8) gana sobre filename cuando ambos están presentes', () => {
    const respuesta = respuestaMock({
      status: 200,
      ok: true,
      headers: {
        'Content-Disposition': "attachment; filename=\"reporte.xlsx\"; filename*=UTF-8''reporte%20especial.xlsx",
      },
    })

    expect(nombreDeArchivo(respuesta)).toBe('reporte especial.xlsx')
  })

  it('usa filename plano cuando no hay filename*', () => {
    const respuesta = respuestaMock({
      status: 200,
      ok: true,
      headers: { 'Content-Disposition': 'attachment; filename="ventas_resumen_pv3_2026-08-01_2026-08-12.xlsx"' },
    })

    expect(nombreDeArchivo(respuesta)).toBe('ventas_resumen_pv3_2026-08-01_2026-08-12.xlsx')
  })

  it('cae a un nombre genérico sin ningún header (design: Open Questions, API no same-origin)', () => {
    const respuesta = respuestaMock({ status: 200, ok: true })

    expect(nombreDeArchivo(respuesta)).toBe('descarga.xlsx')
  })
})

describe('api.descargar', () => {
  const crearUrlMock = vi.fn(() => 'blob:mock-url')
  const revocarUrlMock = vi.fn()

  beforeEach(() => {
    fetchMock.mockReset()
    crearUrlMock.mockClear()
    revocarUrlMock.mockClear()
    URL.createObjectURL = crearUrlMock as unknown as typeof URL.createObjectURL
    URL.revokeObjectURL = revocarUrlMock as unknown as typeof URL.revokeObjectURL
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('camino feliz: crea el object URL y lo revoca tras flushear los timers (revoke no es sincrónico)', async () => {
    vi.useFakeTimers()
    const blob = new Blob(['contenido'])
    fetchMock.mockResolvedValue(
      respuestaMock({
        status: 200,
        ok: true,
        headers: { 'Content-Disposition': 'attachment; filename="reporte.xlsx"' },
        blob: () => Promise.resolve(blob),
      }),
    )

    const promesa = api.descargar('/reportes/ventas/resumen/export?formato=xlsx')
    await vi.runAllTimersAsync()
    await promesa

    // headers: {} porque el test corre fuera de Tauri (jsdom, sin window.__TAURI__) — ver
    // 'adjunta el header Authorization…' más abajo para el camino CON Tauri.
    expect(fetchMock).toHaveBeenCalledWith('/api/reportes/ventas/resumen/export?formato=xlsx', {
      credentials: 'include',
      headers: {},
    })
    expect(crearUrlMock).toHaveBeenCalledWith(blob)
    expect(revocarUrlMock).toHaveBeenCalledWith('blob:mock-url')
  })

  it('403 → funnel: no crea object URL, lanza ErrorApi con el mensaje del servidor, no navega la SPA', async () => {
    fetchMock.mockResolvedValue(
      respuestaMock({
        status: 403,
        ok: false,
        json: () => Promise.resolve({ title: 'No tenés permiso para exportar este reporte.', codigo: 'prohibido' }),
      }),
    )

    await expect(api.descargar('/reportes/ventas/resumen/export?formato=xlsx')).rejects.toMatchObject({
      estado: 403,
      message: 'No tenés permiso para exportar este reporte.',
    })
    expect(crearUrlMock).not.toHaveBeenCalled()
  })

  it('401 → funnel: dispara el observador de alPerderLaSesion, igual que pedir', async () => {
    const observador = vi.fn()
    const dejarDeEscuchar = alPerderLaSesion(observador)
    fetchMock.mockResolvedValue(respuestaMock({ status: 401, ok: false }))

    await expect(api.descargar('/reportes/ventas/resumen/export?formato=xlsx')).rejects.toBeInstanceOf(ErrorApi)
    expect(observador).toHaveBeenCalledTimes(1)
    expect(crearUrlMock).not.toHaveBeenCalled()

    dejarDeEscuchar()
  })

  it('400 (tope de filas) → funnel: ErrorApi con el código del dominio, sin object URL', async () => {
    fetchMock.mockResolvedValue(
      respuestaMock({
        status: 400,
        ok: false,
        json: () => Promise.resolve({ title: 'El export supera el tope de filas.', codigo: 'exportacion_demasiado_grande' }),
      }),
    )

    const error = await api.descargar('/reportes/ventas/resumen/export?formato=xlsx').catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ErrorApi)
    expect((error as InstanceType<typeof ErrorApi>).codigo).toBe('exportacion_demasiado_grande')
    expect(crearUrlMock).not.toHaveBeenCalled()
  })
})
