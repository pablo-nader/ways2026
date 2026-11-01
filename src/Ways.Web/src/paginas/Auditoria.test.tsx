import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { Toaster } from '../toasts/Toaster'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Auditoria, etiquetaDeAccion } from './Auditoria'
import { ErrorApi } from '../api/cliente'
import { CATALOGO_DE_ACCIONES_AUDITADAS } from '../api/tipos'
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
  modo: 'Web',
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
  return render(<Auditoria />, { wrapper: ({ children }) => (
      <MemoryRouter>
        {children}
        <Toaster />
      </MemoryRouter>
    ) })
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

/** Una descarga fallida se avisa con un toast de error, nunca con un banner. */
async function toastDeErrorDeDescarga(texto: string) {
  const aviso = await screen.findByText(texto)
  expect(aviso.closest('.ws-toaster [role="alert"]')).not.toBeNull()
  expect(document.body.querySelector('.alert-danger')).toBeNull()
  expect(screen.getAllByText(texto)).toHaveLength(1)
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

  // judgment-day ronda 2 (etapa 20 slice 4, R2-3): el catálogo del front había quedado corto — la
  // pantalla renderizaba `tenant.baja` en crudo y el `<select>` no podía filtrar por ninguna de
  // las tres bajas de organización. El front lista un subconjunto del catálogo del back (que tiene
  // más entradas): esta prueba congela el que se ofrece en el `<select>`.
  // Mutation target: sacar cualquiera de las 22 entradas.
  it('el catálogo del front ofrece las 22 acciones esperadas, las bajas, las de pagos y las de cobros incluidas', () => {
    expect(CATALOGO_DE_ACCIONES_AUDITADAS).toHaveLength(22)

    expect(CATALOGO_DE_ACCIONES_AUDITADAS.map((a) => a.valor)).toEqual([
      'precio.cambio',
      'venta.anulacion',
      'compra.anulacion',
      'compra.edicion',
      'compra.redondeo',
      'pago.aplicacion',
      'pago.desaplicacion',
      'pago.redondeo',
      'cobro.aplicacion',
      'cobro.desaplicacion',
      'stock.ajuste',
      'stock.decomiso',
      'stock.conteo',
      'cc.reliquidacion',
      'usuario.alta',
      'usuario.actualizacion',
      'usuario.baja',
      'usuario.desbloqueo',
      'usuario.password',
      'tenant.baja',
      'empresa.baja',
      'pv.baja',
    ])

    expect(etiquetaDeAccion('tenant.baja')).toBe('Baja de tenant')
    expect(etiquetaDeAccion('empresa.baja')).toBe('Baja de empresa')
    expect(etiquetaDeAccion('pv.baja')).toBe('Baja de punto de venta')
    expect(etiquetaDeAccion('compra.edicion')).toBe('Edición de compra')
    expect(etiquetaDeAccion('compra.redondeo')).toBe('Ajuste de redondeo de compra')
    expect(etiquetaDeAccion('pago.aplicacion')).toBe('Aplicación de pago a compras')
    expect(etiquetaDeAccion('pago.desaplicacion')).toBe('Desaplicación de pago de una compra')
    expect(etiquetaDeAccion('pago.redondeo')).toBe('Ajuste de redondeo de pago')
    expect(etiquetaDeAccion('cobro.aplicacion')).toBe('Aplicación de cobro a ventas')
    expect(etiquetaDeAccion('cobro.desaplicacion')).toBe('Desaplicación de cobro de una venta')
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
    const celdaPv = screen.getByTitle('Evento de toda la cuenta')
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
    expect(screen.getByLabelText('#Id')).toHaveValue('')
  })

  it('una descarga fallida avisa con un toast de error', async () => {
    mockearRutasBase()
    apiDescargarMock.mockRejectedValueOnce(new Error('no se pudo descargar'))
    const usuario = userEvent.setup()
    renderAuditoria()

    await screen.findByText('#41')
    await usuario.click(screen.getByRole('button', { name: 'Descargar' }))

    await toastDeErrorDeDescarga('No se pudo descargar el archivo.')
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

describe('Auditoria — orden en el servidor', () => {
  function consultasDelLog(): string[] {
    return apiGetMock.mock.calls.map((c) => c[0] as string).filter((ruta) => ruta.startsWith('/auditoria?'))
  }

  function parametrosDeLaUltimaConsulta(): URLSearchParams {
    return new URLSearchParams(consultasDelLog().at(-1)!.split('?')[1])
  }

  async function cargada() {
    renderAuditoria()
    await screen.findByText('#41')
  }

  /**
   * Cláusula bajo prueba: cada encabezado de dato manda su propia clave en `orden`. Mutation-proof-
   * tests: cruzar dos claves (por ejemplo `entidad` en la columna #Id) rompe el caso de esa columna.
   */
  it.each([
    ['Fecha', 'fecha'],
    ['Acción', 'accion'],
    ['Entidad', 'entidad'],
    ['#Id', 'idEntidad'],
    ['Actor', 'actor'],
    ['PV', 'puntoVenta'],
  ])('el encabezado "%s" pide orden=%s ascendente', async (titulo, clave) => {
    mockearRutasBase()
    await cargada()

    const encabezado = screen.getByRole('columnheader', { name: new RegExp(`^${titulo}`) })
    await userEvent.click(within(encabezado).getByRole('button'))

    await waitFor(() => expect(parametrosDeLaUltimaConsulta().get('orden')).toBe(clave))
    expect(parametrosDeLaUltimaConsulta().get('direccion')).toBe('ascendente')
  })

  it('la columna de detalle no es ordenable', async () => {
    mockearRutasBase()
    await cargada()

    expect(screen.queryByRole('button', { name: /^Detalle/ })).not.toBeInTheDocument()
  })

  it('el clic recorre ascendente, descendente y sin orden, con aria-sort en cada paso', async () => {
    mockearRutasBase()
    await cargada()
    const encabezado = screen.getByRole('columnheader', { name: /^Fecha/ })
    const boton = within(encabezado).getByRole('button')
    expect(encabezado).toHaveAttribute('aria-sort', 'none')
    expect(parametrosDeLaUltimaConsulta().has('orden')).toBe(false)

    await userEvent.click(boton)
    await waitFor(() => expect(encabezado).toHaveAttribute('aria-sort', 'ascending'))
    expect(parametrosDeLaUltimaConsulta().get('direccion')).toBe('ascendente')

    await userEvent.click(boton)
    await waitFor(() => expect(encabezado).toHaveAttribute('aria-sort', 'descending'))
    expect(parametrosDeLaUltimaConsulta().get('direccion')).toBe('descendente')

    await userEvent.click(boton)
    await waitFor(() => expect(encabezado).toHaveAttribute('aria-sort', 'none'))
    expect(parametrosDeLaUltimaConsulta().has('orden')).toBe(false)
    expect(parametrosDeLaUltimaConsulta().has('direccion')).toBe(false)
  })

  /**
   * Cláusula bajo prueba: `pagina: 1` al cambiar el orden y los filtros conservados. Mutation-proof-
   * tests: quitar el reseteo deja `pagina=2` en la consulta; reiniciar los filtros pierde `accion`.
   */
  it('cambiar el orden desde la página 2 vuelve a la página 1 y conserva los filtros', async () => {
    mockearRutasBase((ruta) =>
      ruta.startsWith('/auditoria?') ? Promise.resolve(paginaFixture([filaFixture()], { total: 60 })) : undefined,
    )
    await cargada()
    await userEvent.selectOptions(screen.getByLabelText('Acción'), 'venta.anulacion')
    await userEvent.click(await screen.findByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(parametrosDeLaUltimaConsulta().get('pagina')).toBe('2'))

    await userEvent.click(screen.getByRole('button', { name: /^Entidad/ }))

    await waitFor(() => expect(parametrosDeLaUltimaConsulta().get('orden')).toBe('entidad'))
    expect(parametrosDeLaUltimaConsulta().get('pagina')).toBe('1')
    expect(parametrosDeLaUltimaConsulta().get('accion')).toBe('venta.anulacion')
  })

  it('un cambio de orden dispara una sola consulta', async () => {
    mockearRutasBase((ruta) =>
      ruta.startsWith('/auditoria?') ? Promise.resolve(paginaFixture([filaFixture()], { total: 60 })) : undefined,
    )
    await cargada()
    await userEvent.click(screen.getByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(parametrosDeLaUltimaConsulta().get('pagina')).toBe('2'))
    const antes = consultasDelLog().length

    await userEvent.click(screen.getByRole('button', { name: /^Entidad/ }))
    await waitFor(() => expect(parametrosDeLaUltimaConsulta().get('orden')).toBe('entidad'))

    expect(consultasDelLog().length - antes).toBe(1)
  })

  it('el orden se conserva al cambiar un filtro', async () => {
    mockearRutasBase()
    await cargada()
    await userEvent.click(screen.getByRole('button', { name: /^Actor/ }))
    await waitFor(() => expect(parametrosDeLaUltimaConsulta().get('orden')).toBe('actor'))

    await userEvent.selectOptions(screen.getByLabelText('Acción'), 'venta.anulacion')

    await waitFor(() => expect(parametrosDeLaUltimaConsulta().get('accion')).toBe('venta.anulacion'))
    expect(parametrosDeLaUltimaConsulta().get('orden')).toBe('actor')
  })

  it('el export no lleva el orden: sale siempre lo más reciente primero', async () => {
    mockearRutasBase()
    await cargada()
    await userEvent.click(screen.getByRole('button', { name: /^Actor/ }))
    await waitFor(() => expect(parametrosDeLaUltimaConsulta().get('orden')).toBe('actor'))

    await userEvent.click(screen.getByRole('button', { name: 'Descargar' }))

    const rutaDescargada = apiDescargarMock.mock.calls[0][0] as string
    expect(rutaDescargada).not.toContain('orden=')
    expect(rutaDescargada).not.toContain('direccion=')
  })

  /**
   * Cláusula bajo prueba: el gateo de generación frente a un cambio de orden. Mutation-proof-tests:
   * sacar el `if (generacionRef.current !== miGeneracion) return` del `.then` deja que la respuesta
   * del orden viejo, que llega tarde, pise las filas del orden vigente.
   */
  it('la respuesta de un orden viejo que llega tarde nunca pisa a la del orden vigente', async () => {
    let resolverPrimera: (valor: PaginaDeAuditoria) => void = () => {}
    const primera = new Promise<PaginaDeAuditoria>((resolve) => {
      resolverPrimera = resolve
    })
    let llamadas = 0
    mockearRutasBase((ruta) => {
      if (!ruta.startsWith('/auditoria?')) return undefined
      llamadas += 1
      if (llamadas === 1) return Promise.resolve(paginaFixture([filaFixture({ idEntidad: 41 })]))
      if (llamadas === 2) return primera
      return Promise.resolve(paginaFixture([filaFixture({ idAuditoria: 2, idEntidad: 7777 })]))
    })
    renderAuditoria()
    await screen.findByText('#41')

    await userEvent.click(screen.getByRole('button', { name: /^Entidad/ }))
    await userEvent.click(screen.getByRole('button', { name: /^Entidad/ }))
    await screen.findByText('#7777')

    resolverPrimera(paginaFixture([filaFixture({ idAuditoria: 3, idEntidad: 9999 })]))
    await waitFor(() => expect(screen.queryByText('#9999')).not.toBeInTheDocument())
    expect(screen.getByText('#7777')).toBeInTheDocument()
  })

  /**
   * Decisión del patrón de S5c: si la consulta de un orden nuevo falla, la tabla se va con su encabezado
   * (no queda un encabezado declarando un orden que las filas no tienen) y Reintentar repite ese mismo
   * orden. Mutation-proof-tests: dejar `setPagina` sin vaciar en el `.catch` deja las filas del orden
   * anterior bajo un encabezado del nuevo.
   */
  it('si falla la consulta de un orden nuevo, no queda un encabezado que mienta y Reintentar repite ese orden', async () => {
    let llamadas = 0
    mockearRutasBase((ruta) => {
      if (!ruta.startsWith('/auditoria?')) return undefined
      llamadas += 1
      return llamadas === 2
        ? Promise.reject(new ErrorApi(400, 'orden_invalido', 'orden no válido.'))
        : Promise.resolve(paginaFixture())
    })
    await cargada()

    await userEvent.click(screen.getByRole('button', { name: /^Entidad/ }))

    expect(await screen.findByText('orden no válido.')).toBeInTheDocument()
    expect(screen.queryByRole('columnheader', { name: /^Entidad/ })).not.toBeInTheDocument()
    expect(screen.queryByText('#41')).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Reintentar' }))

    const encabezado = await screen.findByRole('columnheader', { name: /^Entidad/ })
    expect(encabezado).toHaveAttribute('aria-sort', 'ascending')
    expect(parametrosDeLaUltimaConsulta().get('orden')).toBe('entidad')
    expect(screen.getByText('#41')).toBeInTheDocument()
  })
})
