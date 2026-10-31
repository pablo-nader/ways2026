import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { HistoricoDeCajas } from './HistoricoDeCajas'
import { RutaProtegida } from '../auth/RutaProtegida'
import { ROL } from '../api/tipos'
import type { FilaDeHistoricoDeCajas, PaginaDeHistoricoDeCajas, PuntoVentaListado, UsuarioAutenticado } from '../api/tipos'
import { fechaHoraATexto } from '../formato/fechas'

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
  nombreTenant: 'Tenant Demo',
  razonSocialEmpresa: 'Empresa Demo',
  modo: 'Web',
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
  nombreTenant: 'Tenant Demo',
  razonSocialEmpresa: 'Empresa Demo',
  modo: 'Web',
}

function filaFixture(sobrescribir: Partial<FilaDeHistoricoDeCajas> = {}): FilaDeHistoricoDeCajas {
  return {
    idTurnoCaja: 100,
    idPuntoVenta: 10,
    fechaApertura: '2026-08-05T08:00:00Z',
    fechaCierre: '2026-08-05T20:00:00Z',
    esperado: 1000,
    declarado: 950,
    diferencia: -50,
    egresos: { porCategoria: [], porArea: [], retiros: 0 },
    fechaRecalculo: null,
    idEmpleadoRecalculo: null,
    ...sobrescribir,
  }
}

function paginaFixture(items: FilaDeHistoricoDeCajas[] = [filaFixture()], sobrescribir: Partial<PaginaDeHistoricoDeCajas> = {}): PaginaDeHistoricoDeCajas {
  return { items, total: items.length, pagina: 1, tamanio: 25, ...sobrescribir }
}

function renderHistoricoDeCajas() {
  return render(<HistoricoDeCajas />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

/** Monta detrás del mismo gate de rol que `App.tsx` usa para `/caja/historico`
 * (`Politicas.LecturaDeReportes`: Supervisor + Admin). */
function renderHistoricoDeCajasProtegido() {
  return render(
    <MemoryRouter initialEntries={['/caja/historico']}>
      <Routes>
        <Route
          path="/caja/historico"
          element={
            <RutaProtegida rolesPermitidos={[ROL.Supervisor, ROL.Admin]}>
              <HistoricoDeCajas />
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
    if (ruta.startsWith('/reportes/cajas?')) return Promise.resolve(paginaFixture())
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiDescargarMock.mockReset()
  apiDescargarMock.mockResolvedValue(undefined)
  usuarioActual = usuarioFixture()
})

describe('HistoricoDeCajas — listado (stage-11-exportacion-reportes, Slice 6a)', () => {
  it('un listado vacío muestra un estado vacío, no un re-query', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/cajas?')) return Promise.resolve(paginaFixture([]))
      return undefined
    })
    renderHistoricoDeCajas()

    expect(await screen.findByText('No hay turnos cerrados que coincidan con los filtros.')).toBeInTheDocument()
    const llamadas = apiGetMock.mock.calls.filter((call: unknown[]) => (call[0] as string).startsWith('/reportes/cajas?'))
    expect(llamadas).toHaveLength(1)
  })

  // mutation-proof-tests rule 6: dos filas con valores DISTINTOS en cada columna — una fila
  // rotada tiene que fallar esta prueba.
  it('renderiza cada columna de cada fila (PV, apertura, cierre, esperado, declarado, diferencia)', async () => {
    const filaUno = filaFixture({
      idTurnoCaja: 100,
      idPuntoVenta: 10,
      fechaApertura: '2026-08-05T08:00:00Z',
      fechaCierre: '2026-08-05T20:00:00Z',
      esperado: 1000,
      declarado: 950,
      diferencia: -50,
    })
    const filaDos = filaFixture({
      idTurnoCaja: 101,
      idPuntoVenta: 11,
      fechaApertura: '2026-08-06T08:00:00Z',
      fechaCierre: '2026-08-06T20:00:00Z',
      esperado: 2500,
      declarado: 2550,
      diferencia: 50,
    })
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/cajas?')) return Promise.resolve(paginaFixture([filaUno, filaDos]))
      return undefined
    })
    renderHistoricoDeCajas()

    await screen.findByText('#100')
    const filaUnoDom = screen.getByRole('row', { name: /#100/ })
    expect(within(filaUnoDom).getByText('PV Centro')).toBeInTheDocument()
    expect(within(filaUnoDom).getByText('$ 1.000,00')).toBeInTheDocument()
    expect(within(filaUnoDom).getByText('$ 950,00')).toBeInTheDocument()
    expect(within(filaUnoDom).getByText('-$ 50,00')).toBeInTheDocument()

    const filaDosDom = screen.getByRole('row', { name: /#101/ })
    expect(within(filaDosDom).getByText('PV Norte')).toBeInTheDocument()
    expect(within(filaDosDom).getByText('$ 2.500,00')).toBeInTheDocument()
    expect(within(filaDosDom).getByText('$ 2.550,00')).toBeInTheDocument()
    expect(within(filaDosDom).getByText('$ 50,00')).toBeInTheDocument()
  })

  it('cada fila enlaza al detalle de SU turno y el enlace navega ahí', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/cajas?')) {
        return Promise.resolve(paginaFixture([filaFixture({ idTurnoCaja: 100 }), filaFixture({ idTurnoCaja: 101 })]))
      }
      return undefined
    })
    render(
      <MemoryRouter initialEntries={['/caja/historico']}>
        <Routes>
          <Route path="/caja/historico" element={<HistoricoDeCajas />} />
          <Route path="/caja/turnos/:id/z" element={<div>Detalle del turno</div>} />
        </Routes>
      </MemoryRouter>,
    )

    const enlaceUno = await screen.findByRole('link', { name: '#100' })
    expect(enlaceUno).toHaveAttribute('href', '/caja/turnos/100/z')
    expect(screen.getByRole('link', { name: '#101' })).toHaveAttribute('href', '/caja/turnos/101/z')

    await userEvent.click(enlaceUno)
    expect(await screen.findByText('Detalle del turno')).toBeInTheDocument()
  })

  it('marca como Recalculado, con la fecha en el tooltip, solo los turnos con fechaRecalculo', async () => {
    const recalculada = filaFixture({ idTurnoCaja: 100, fechaRecalculo: '2026-09-20T15:30:00Z', idEmpleadoRecalculo: 4 })
    const intacta = filaFixture({ idTurnoCaja: 101 })
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/cajas?')) return Promise.resolve(paginaFixture([recalculada, intacta]))
      return undefined
    })
    renderHistoricoDeCajas()

    await screen.findByText('#100')
    const filaRecalculada = screen.getByRole('row', { name: /#100/ })
    const insignia = within(filaRecalculada).getByText('Recalculado')
    expect(insignia).toHaveAttribute('title', `Arqueo recalculado el ${fechaHoraATexto('2026-09-20T15:30:00Z')}`)
    expect(within(screen.getByRole('row', { name: /#101/ })).queryByText('Recalculado')).not.toBeInTheDocument()
  })

  it('cambiar el filtro de punto de venta dispara una nueva consulta con idPuntoVenta', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderHistoricoDeCajas()

    await screen.findByText('#100')
    apiGetMock.mockClear()
    mockearRutasBase()
    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '11')

    await waitFor(() => {
      const llamadas = apiGetMock.mock.calls.filter((call: unknown[]) => (call[0] as string).startsWith('/reportes/cajas?'))
      expect(llamadas.some((call: unknown[]) => (call[0] as string).includes('idPuntoVenta=11'))).toBe(true)
    })
  })

  it('el botón de descarga apunta a /reportes/cajas/export y limpia el error previo al reintentar', async () => {
    mockearRutasBase()
    apiDescargarMock.mockRejectedValueOnce(new Error('no se pudo descargar'))
    const usuario = userEvent.setup()
    renderHistoricoDeCajas()

    await screen.findByText('#100')
    await usuario.click(screen.getByRole('button', { name: 'Descargar' }))

    expect(await screen.findByText('No se pudo descargar el archivo.')).toBeInTheDocument()
    expect(apiDescargarMock.mock.calls[0][0]).toMatch(/^\/reportes\/cajas\/export\?/)

    apiDescargarMock.mockResolvedValueOnce(undefined)
    await usuario.click(screen.getByRole('button', { name: 'Descargar' }))
    await waitFor(() => expect(screen.queryByText('No se pudo descargar el archivo.')).not.toBeInTheDocument())
  })

  it('una respuesta de listado desactualizada nunca pisa la más reciente (generación)', async () => {
    let resolverPrimera: (valor: PaginaDeHistoricoDeCajas) => void = () => {}
    const primera = new Promise<PaginaDeHistoricoDeCajas>((resolve) => {
      resolverPrimera = resolve
    })
    let cantidadDeLlamadas = 0

    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/cajas?')) {
        cantidadDeLlamadas += 1
        if (cantidadDeLlamadas === 1) return primera
        return Promise.resolve(paginaFixture([filaFixture({ idTurnoCaja: 202 })]))
      }
      return undefined
    })

    const usuario = userEvent.setup()
    renderHistoricoDeCajas()
    await screen.findByLabelText('Punto de venta')

    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '10')
    expect(await screen.findByText('#202')).toBeInTheDocument()

    resolverPrimera(paginaFixture([filaFixture({ idTurnoCaja: 909 })]))
    await waitFor(() => expect(screen.queryByText('#909')).not.toBeInTheDocument())
    expect(screen.getByText('#202')).toBeInTheDocument()
  })
})

describe('HistoricoDeCajas — role gating (spec historico-de-cajas: Role Split)', () => {
  it('un Supervisor llega a /caja/historico', async () => {
    mockearRutasBase()
    renderHistoricoDeCajasProtegido()

    await screen.findByText('#100')
    expect(screen.queryByText('Inicio (redirigido)')).not.toBeInTheDocument()
  })

  it('un Vendedor nunca llega a /caja/historico: redirige a Inicio', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor' })
    mockearRutasBase()

    renderHistoricoDeCajasProtegido()

    expect(await screen.findByText('Inicio (redirigido)')).toBeInTheDocument()
    await waitFor(() => expect(screen.queryByText('Histórico de cajas')).not.toBeInTheDocument())
  })
})

describe('HistoricoDeCajas — orden en el servidor', () => {
  const llamadasAlListado = () =>
    apiGetMock.mock.calls.map((c: unknown[]) => c[0] as string).filter((ruta) => ruta.startsWith('/reportes/cajas?'))

  it('el clic en un encabezado pide el orden al servidor, recorre ascendente, descendente y el orden de siempre', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderHistoricoDeCajas()
    await screen.findByText('#100')
    expect(llamadasAlListado().every((ruta) => !ruta.includes('orden='))).toBe(true)

    await usuario.click(screen.getByRole('button', { name: /^Esperado/ }))
    await waitFor(() => expect(llamadasAlListado().at(-1)).toContain('orden=esperado&direccion=ascendente'))
    expect(screen.getByRole('columnheader', { name: /Esperado/ })).toHaveAttribute('aria-sort', 'ascending')

    await usuario.click(screen.getByRole('button', { name: /^Esperado/ }))
    await waitFor(() => expect(llamadasAlListado().at(-1)).toContain('orden=esperado&direccion=descendente'))

    await usuario.click(screen.getByRole('button', { name: /^Esperado/ }))
    await waitFor(() => expect(llamadasAlListado().at(-1)).not.toContain('orden='))
  })

  it.each([
    ['Turno', 'turno'],
    ['Punto de venta', 'puntoVenta'],
    ['Apertura', 'apertura'],
    ['Cierre', 'cierre'],
    ['Esperado', 'esperado'],
    ['Declarado', 'declarado'],
    ['Diferencia', 'diferencia'],
  ])('la columna %s ordena por la clave %s', async (titulo, clave) => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderHistoricoDeCajas()
    await screen.findByText('#100')

    await usuario.click(screen.getByRole('button', { name: new RegExp(`^${titulo}`) }))

    await waitFor(() => expect(llamadasAlListado().at(-1)).toContain(`orden=${clave}&direccion=ascendente`))
  })

  it('cambiar el orden desde la página 2 vuelve a la página 1 y conserva los filtros', async () => {
    mockearRutasBase((ruta) =>
      ruta.startsWith('/reportes/cajas?')
        ? Promise.resolve(paginaFixture([filaFixture()], { total: 50, pagina: ruta.includes('pagina=2') ? 2 : 1 }))
        : undefined,
    )
    const usuario = userEvent.setup()
    renderHistoricoDeCajas()
    await screen.findByText(/Página 1 de 2/)
    await userEvent.selectOptions(screen.getByLabelText('Punto de venta'), '11')
    await screen.findByText(/Página 1 de 2/)
    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    await screen.findByText(/Página 2 de 2/)

    await usuario.click(screen.getByRole('button', { name: /^Esperado/ }))

    await waitFor(() => {
      const ultima = llamadasAlListado().at(-1)!
      expect(ultima).toContain('orden=esperado&direccion=ascendente')
      expect(ultima).toContain('pagina=1')
      expect(ultima).toContain('idPuntoVenta=11')
    })
    await screen.findByText(/Página 1 de 2/)
  })

  it('una respuesta stale de un orden anterior no pisa la del orden más nuevo', async () => {
    let resolverPorEsperado: (v: PaginaDeHistoricoDeCajas) => void = () => {}
    let promesaPorEsperado: Promise<PaginaDeHistoricoDeCajas> = Promise.resolve(paginaFixture())
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/cajas?') && ruta.includes('orden=esperado')) {
        promesaPorEsperado = new Promise((resolve) => (resolverPorEsperado = resolve))
        return promesaPorEsperado
      }
      if (ruta.startsWith('/reportes/cajas?') && ruta.includes('orden=declarado')) {
        return Promise.resolve(paginaFixture([filaFixture({ idTurnoCaja: 200 })]))
      }
      return undefined
    })
    const usuario = userEvent.setup()
    renderHistoricoDeCajas()
    await screen.findByText('#100')

    await usuario.click(screen.getByRole('button', { name: /^Esperado/ }))
    await usuario.click(screen.getByRole('button', { name: /^Declarado/ }))
    await screen.findByText('#200')

    await act(async () => {
      resolverPorEsperado(paginaFixture([filaFixture({ idTurnoCaja: 300 })]))
      await promesaPorEsperado
    })

    expect(screen.getByText('#200')).toBeInTheDocument()
    expect(screen.queryByText('#300')).not.toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: /Declarado/ })).toHaveAttribute('aria-sort', 'ascending')
  })

  it('si la consulta con orden falla, la tabla y su encabezado desaparecen: nunca queda un orden que las filas no tienen', async () => {
    mockearRutasBase((ruta) =>
      ruta.startsWith('/reportes/cajas?') && ruta.includes('orden=esperado') ? Promise.reject(new Error('caída')) : undefined,
    )
    const usuario = userEvent.setup()
    renderHistoricoDeCajas()
    await screen.findByText('#100')

    await usuario.click(screen.getByRole('button', { name: /^Esperado/ }))

    await screen.findByText('No se pudo cargar el histórico de cajas.')
    expect(screen.queryByRole('columnheader', { name: /Esperado/ })).not.toBeInTheDocument()
    expect(screen.queryByText('#100')).not.toBeInTheDocument()
  })

  it('la descarga no lleva el orden: la exportación conserva el suyo', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderHistoricoDeCajas()
    await screen.findByText('#100')
    await usuario.click(screen.getByRole('button', { name: /^Esperado/ }))
    await waitFor(() => expect(llamadasAlListado().at(-1)).toContain('orden=esperado'))

    await usuario.click(screen.getByRole('button', { name: 'Descargar' }))

    await waitFor(() => expect(apiDescargarMock).toHaveBeenCalled())
    expect(apiDescargarMock.mock.calls[0][0]).toMatch(/^\/reportes\/cajas\/export\?/)
    expect(apiDescargarMock.mock.calls[0][0]).not.toContain('orden')
    expect(apiDescargarMock.mock.calls[0][0]).not.toContain('direccion')
  })
})
