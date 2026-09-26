import { afterEach, beforeEach, describe, expect, it, vi, type MockInstance } from 'vitest'

const fetchMock = vi.fn()
vi.stubGlobal('fetch', fetchMock)

const { alPerderLaSesion, api, ErrorApi, ErrorDeRed, nombreDeArchivo } = await import('./cliente')
const { establecerTokenDeSesionBearer, inicializarUrlServidor, restaurarSesionDeCajeroPersistida, tokenDeSesionBearerActual } =
  await import('./entornoTauri')

type GlobalConTauri = typeof globalThis & { __TAURI__?: unknown }

/** `invokeImpl` opcional para los tests que necesitan que `info_app` (`inicializarUrlServidor`)
 * devuelva algo específico — por defecto resuelve `undefined`, suficiente para los tests que solo
 * necesitan que `corriendoEnTauri()` sea `true`. */
function instalarPuenteTauri(invokeImpl: (comando: string) => unknown = () => Promise.resolve(undefined)) {
  ;(globalThis as GlobalConTauri).__TAURI__ = { core: { invoke: vi.fn(invokeImpl) } }
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

type ClickDeDescarga = { href: string; download: string; enElDocumento: boolean }

/**
 * `descargar` termina haciéndole click a un `<a download>` sintético. jsdom no implementa
 * descargas: ese click sale por su camino de navegación real y emite "Not implemented: navigation
 * to another Document" en la consola del proceso —fuera de todo test, porque jsdom escribe en la
 * consola de Node, no en la que Vitest intercepta—, así que ni siquiera queda atribuido a este
 * archivo. Interceptar el click acá es lo que hace que jsdom nunca vea una navegación, y de paso
 * deja asertable el enlace que arma la descarga (hasta ahora nada probaba que `nombreDeArchivo`
 * llegara al atributo `download`).
 */
const clicksDeDescarga: ClickDeDescarga[] = []
let espiaDeClick: MockInstance<() => void>

beforeEach(() => {
  clicksDeDescarga.length = 0
  espiaDeClick = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
    clicksDeDescarga.push({
      href: this.getAttribute('href') ?? '',
      download: this.download,
      enElDocumento: this.isConnected,
    })
  })
})

afterEach(() => {
  espiaDeClick.mockRestore()
})

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

  // judgment-day ronda 1 (FIX 5, WARNING "worsened"): una request en vuelo con un token viejo que
  // resuelve TARDE (después de un relogueo exitoso con un token nuevo) no debe pisar la sesión
  // fresca. Se simula sin `vi.useFakeTimers` — alcanza con no resolver el primer `fetch` hasta
  // después de instalar el token nuevo, para reproducir el orden real de eventos.
  it('un 401 de una request vieja (con un token YA superado por un relogueo) nunca limpia el token nuevo ni dispara los observadores', async () => {
    instalarPuenteTauri()
    establecerTokenDeSesionBearer('token-viejo')
    const observador = vi.fn()
    const dejarDeEscuchar = alPerderLaSesion(observador)

    let resolverFetchViejo: (r: Response) => void = () => {}
    const fetchViejoPendiente = new Promise<Response>((resolve) => {
      resolverFetchViejo = resolve
    })
    fetchMock.mockReturnValueOnce(fetchViejoPendiente)

    const promesaVieja = api.get('/algo')

    // Mientras la request vieja sigue en vuelo, un relogueo exitoso instala un token NUEVO.
    establecerTokenDeSesionBearer('token-nuevo')

    // Ahora la request vieja resuelve, tarde, con un 401 — construida con 'token-viejo', que ya
    // no es el vigente.
    resolverFetchViejo(respuestaMock({ status: 401, ok: false }))

    await expect(promesaVieja).rejects.toBeInstanceOf(ErrorApi)
    // El token nuevo sigue instalado: el 401 tardío no lo pisó, ni disparó la limpieza global
    // (que en `pos/main.tsx` también borraría la sesión persistida en disco del login fresco).
    expect(tokenDeSesionBearerActual()).toBe('token-nuevo')
    expect(observador).not.toHaveBeenCalled()

    dejarDeEscuchar()
  })

  it('un 401 de la request CORRIENTE (mismo token todavía vigente) sigue limpiando la sesión, sin regresión', async () => {
    instalarPuenteTauri()
    establecerTokenDeSesionBearer('token-actual')
    fetchMock.mockResolvedValue(respuestaMock({ status: 401, ok: false }))

    await expect(api.get('/algo')).rejects.toBeInstanceOf(ErrorApi)

    expect(tokenDeSesionBearerActual()).toBeNull()
  })

  // stage-pos-sesion-offline: prueba de composición de punta a punta (sin mockear
  // `entornoTauri.ts`, a diferencia de `AppPos.test.tsx`) — una sesión persistida válida,
  // restaurada por `restaurarSesionDeCajeroPersistida`, tiene que llegar hasta el header
  // `Authorization` de la PRÓXIMA request real de `pedir`, exactamente igual que un token recién
  // logueado. Esto es lo que hace que `AppPos.tsx` no necesite ningún cambio propio: para esta
  // capa, un token restaurado y uno recién logueado son indistinguibles.
  it('un token restaurado desde la sesión persistida se adjunta igual que uno recién logueado', async () => {
    instalarPuenteTauri((comando) =>
      comando === 'leer_sesion_de_cajero' ? Promise.resolve({ token: 'token-restaurado', expira_el: '2099-01-01T00:00:00Z' }) : Promise.resolve(undefined),
    )
    await restaurarSesionDeCajeroPersistida()
    fetchMock.mockResolvedValue(respuestaMock({ status: 200, ok: true, json: () => Promise.resolve({ ok: true }) }))

    await api.get('/algo')

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect((init.headers as Record<string, string>).Authorization).toBe('Bearer token-restaurado')
  })

  it('una sesión persistida vencida NUNCA se adjunta — la request sale sin Authorization', async () => {
    instalarPuenteTauri((comando) =>
      comando === 'leer_sesion_de_cajero' ? Promise.resolve({ token: 'token-vencido', expira_el: '2020-01-01T00:00:00Z' }) : Promise.resolve(undefined),
    )
    await restaurarSesionDeCajeroPersistida()
    fetchMock.mockResolvedValue(respuestaMock({ status: 200, ok: true, json: () => Promise.resolve({ ok: true }) }))

    await api.get('/algo')

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect((init.headers as Record<string, string>).Authorization).toBeUndefined()
  })
})

describe('ErrorDeRed (stage-pos-venta-offline-backend, Parte C: "el servidor dijo que no" vs. "no hubo servidor")', () => {
  beforeEach(() => {
    fetchMock.mockReset()
    quitarPuenteTauri()
    establecerTokenDeSesionBearer(null)
  })

  it('un fetch que rechaza (sin red) en api.get lanza ErrorDeRed, nunca ErrorApi', async () => {
    const fallaDeRed = new TypeError('Failed to fetch')
    fetchMock.mockRejectedValue(fallaDeRed)

    const error = await api.get('/algo').catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ErrorDeRed)
    expect(error).not.toBeInstanceOf(ErrorApi)
  })

  it('ErrorDeRed conserva la excepción cruda de fetch en causa, sin asumir su forma', async () => {
    const fallaDeRed = new TypeError('Failed to fetch')
    fetchMock.mockRejectedValue(fallaDeRed)

    const error = (await api.get('/algo').catch((e: unknown) => e)) as InstanceType<typeof ErrorDeRed>

    expect(error.causa).toBe(fallaDeRed)
    expect(error.message).toBe('No se pudo contactar al servidor. Revisá tu conexión.')
  })

  it('una falla de red NUNCA dispara alPerderLaSesion — a diferencia de un 401 real, el servidor ni participó', async () => {
    const observador = vi.fn()
    const dejarDeEscuchar = alPerderLaSesion(observador)
    fetchMock.mockRejectedValue(new TypeError('Failed to fetch'))

    await expect(api.get('/algo')).rejects.toBeInstanceOf(ErrorDeRed)
    expect(observador).not.toHaveBeenCalled()

    dejarDeEscuchar()
  })

  it('api.post/put/delete también relanzan una falla de red como ErrorDeRed', async () => {
    fetchMock.mockRejectedValue(new TypeError('Failed to fetch'))

    await expect(api.post('/algo', { x: 1 })).rejects.toBeInstanceOf(ErrorDeRed)
    await expect(api.put('/algo', { x: 1 })).rejects.toBeInstanceOf(ErrorDeRed)
    await expect(api.delete('/algo')).rejects.toBeInstanceOf(ErrorDeRed)
  })

  it('api.descargar también relanza una falla de red como ErrorDeRed, sin crear ningún object URL', async () => {
    const crearUrlMock = vi.fn(() => 'blob:mock-url')
    URL.createObjectURL = crearUrlMock as unknown as typeof URL.createObjectURL
    fetchMock.mockRejectedValue(new TypeError('Failed to fetch'))

    await expect(api.descargar('/algo/export')).rejects.toBeInstanceOf(ErrorDeRed)
    expect(crearUrlMock).not.toHaveBeenCalled()
  })

  it('una respuesta del servidor (aunque sea 500) sigue siendo ErrorApi, nunca ErrorDeRed — el servidor SÍ participó', async () => {
    fetchMock.mockResolvedValue(respuestaMock({ status: 500, ok: false, json: () => Promise.resolve({ codigo: 'error_interno' }) }))

    const error = await api.get('/algo').catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ErrorApi)
    expect(error).not.toBeInstanceOf(ErrorDeRed)
  })
})

describe('URL base y credentials bajo Tauri (slice 3: pos.html local, cross-site respecto de la API)', () => {
  beforeEach(() => {
    fetchMock.mockReset()
    quitarPuenteTauri()
    establecerTokenDeSesionBearer(null)
  })

  afterEach(() => {
    quitarPuenteTauri()
    establecerTokenDeSesionBearer(null)
  })

  it('bajo Tauri antepone la URL cacheada del servidor a /api${ruta} y usa credentials: "omit" en pedir (api.get)', async () => {
    instalarPuenteTauri(() => Promise.resolve({ url_servidor: 'https://empresa.aipos.site' }))
    await inicializarUrlServidor()
    fetchMock.mockResolvedValue(respuestaMock({ status: 200, ok: true, json: () => Promise.resolve({ ok: true }) }))

    await api.get('/algo')

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toBe('https://empresa.aipos.site/api/algo')
    expect(init.credentials).toBe('omit')
  })

  it('bajo Tauri antepone la URL cacheada y usa credentials: "omit" también en descargar', async () => {
    instalarPuenteTauri(() => Promise.resolve({ url_servidor: 'https://empresa.aipos.site' }))
    await inicializarUrlServidor()
    fetchMock.mockResolvedValue(respuestaMock({ status: 200, ok: true, blob: () => Promise.resolve(new Blob()) }))

    await api.descargar('/algo/export')

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toBe('https://empresa.aipos.site/api/algo/export')
    expect(init.credentials).toBe('omit')
  })

  it('en el navegador normal la URL sigue relativa y credentials sigue "include", aunque haya quedado una URL cacheada de un uso previo bajo Tauri', async () => {
    // Nunca puede haber una regresión donde "omit"/absoluta se filtre al camino del navegador
    // normal: se cachea una URL real bajo Tauri primero para que, si `pedir`/`descargar` alguna
    // vez dejaran de chequear `corriendoEnTauri()` y leyeran el caché directo, este test lo vea.
    instalarPuenteTauri(() => Promise.resolve({ url_servidor: 'https://empresa.aipos.site' }))
    await inicializarUrlServidor()
    quitarPuenteTauri()
    fetchMock.mockResolvedValue(respuestaMock({ status: 200, ok: true, json: () => Promise.resolve({ ok: true }) }))

    await api.get('/algo')

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toBe('/api/algo')
    expect(init.credentials).toBe('include')
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
    // El enlace sintético se clickea con el object URL y el nombre parseado del
    // `Content-Disposition`, estando ya en el documento — y no queda colgado después.
    expect(clicksDeDescarga).toEqual([{ href: 'blob:mock-url', download: 'reporte.xlsx', enElDocumento: true }])
    expect(document.querySelector('a[download]')).toBeNull()
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
