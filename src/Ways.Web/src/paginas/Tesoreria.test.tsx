import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Tesoreria } from './Tesoreria'
import { RutaProtegida } from '../auth/RutaProtegida'
import { ROL } from '../api/tipos'
import type { EmpresaListado, MovimientoTesoreriaListado, PaginaDeMovimientosTesoreria, PuntoVentaListado, UsuarioAutenticado } from '../api/tipos'

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

const empresaUnica: EmpresaListado = {
  id: 1,
  idTenant: 1,
  razonSocial: 'Empresa Demo',
  nombreFantasia: null,
  cuit: null,
  nombreTenant: 'Tenant Demo',
  alicuotaPercepcionIibb: null,
  alicuotaPercepcionIva: null,
}

const empresaOtra: EmpresaListado = {
  id: 2,
  idTenant: 1,
  razonSocial: 'Otra Empresa',
  nombreFantasia: null,
  cuit: null,
  nombreTenant: 'Tenant Demo',
  alicuotaPercepcionIibb: null,
  alicuotaPercepcionIva: null,
}

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

function movimientoFixture(sobrescribir: Partial<MovimientoTesoreriaListado> = {}): MovimientoTesoreriaListado {
  return {
    id: 60,
    idEmpresa: 1,
    idPuntoVenta: 10,
    nombrePuntoVenta: 'PV Centro',
    fecha: '2026-08-05T08:00:00Z',
    tipo: 'Deposito',
    idTurnoCaja: 412,
    idGasto: null,
    gastoCategoria: null,
    gastoConcepto: null,
    concepto: 'Apertura de turno',
    inicio: 0,
    ingreso: 60,
    egreso: 0,
    final: 60,
    idEmpleado: 4,
    ...sobrescribir,
  }
}

function paginaFixture(items: MovimientoTesoreriaListado[] = [movimientoFixture()], sobrescribir: Partial<PaginaDeMovimientosTesoreria> = {}): PaginaDeMovimientosTesoreria {
  return { items, total: items.length, pagina: 1, tamanio: 25, ...sobrescribir }
}

function renderTesoreria() {
  return render(<Tesoreria />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

/** Monta detrás del mismo gate de rol que `App.tsx` usa para `/caja/tesoreria`
 * (`Politicas.LecturaDeReportes`). */
function renderTesoreriaProtegido() {
  return render(
    <MemoryRouter initialEntries={['/caja/tesoreria']}>
      <Routes>
        <Route
          path="/caja/tesoreria"
          element={
            <RutaProtegida rolesPermitidos={[ROL.Supervisor, ROL.Admin]}>
              <Tesoreria />
            </RutaProtegida>
          }
        />
        <Route path="/" element={<div>Inicio (redirigido)</div>} />
      </Routes>
    </MemoryRouter>,
  )
}

function mockearRutasBase(
  sobrescribir?: (ruta: string) => Promise<unknown> | undefined,
  empresas: EmpresaListado[] = [empresaUnica],
) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/empresas') return Promise.resolve(empresas)
    if (ruta === '/puntos-venta') return Promise.resolve([puntoVentaCentro, puntoVentaNorte])
    const propia = sobrescribir?.(ruta)
    if (propia) return propia
    if (ruta.startsWith('/reportes/tesoreria?')) return Promise.resolve(paginaFixture())
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiDescargarMock.mockReset()
  apiDescargarMock.mockResolvedValue(undefined)
  usuarioActual = usuarioFixture()
})

describe('Tesoreria — libro (stage-11-exportacion-reportes, Slice 7 — web)', () => {
  it('empresa única: se preselecciona y el punto de venta arranca en "Todos"', async () => {
    mockearRutasBase()
    renderTesoreria()

    await screen.findByLabelText('Punto de venta')
    expect(screen.getByLabelText('Empresa')).toHaveValue(String(empresaUnica.id))
    expect(screen.getByLabelText('Punto de venta')).toHaveValue('')
    expect(screen.getByLabelText('Punto de venta')).not.toBeDisabled()
  })

  it('más de una empresa: no se preselecciona ninguna, el libro no se pide hasta elegir', async () => {
    mockearRutasBase(undefined, [empresaUnica, empresaOtra])
    renderTesoreria()

    await screen.findByLabelText('Empresa')
    expect(screen.getByLabelText('Empresa')).toHaveValue('')
    expect(screen.getByText('Elegí una empresa para ver su libro de tesorería.')).toBeInTheDocument()
    expect(apiGetMock.mock.calls.some((call: unknown[]) => (call[0] as string).startsWith('/reportes/tesoreria?'))).toBe(false)
  })

  it('elegir una empresa dispara la primera consulta con idEmpresa y sin idPuntoVenta ("Todos")', async () => {
    mockearRutasBase(undefined, [empresaUnica, empresaOtra])
    const usuario = userEvent.setup()
    renderTesoreria()

    await screen.findByLabelText('Empresa')
    await usuario.selectOptions(screen.getByLabelText('Empresa'), String(empresaUnica.id))

    await waitFor(() => {
      const llamadas = apiGetMock.mock.calls.filter((call: unknown[]) => (call[0] as string).startsWith('/reportes/tesoreria?'))
      expect(llamadas.some((call: unknown[]) => (call[0] as string).includes(`idEmpresa=${empresaUnica.id}`))).toBe(true)
      expect(llamadas.some((call: unknown[]) => (call[0] as string).includes('idPuntoVenta='))).toBe(false)
    })
  })

  it('un libro vacío muestra un estado vacío, no un re-query', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/tesoreria?')) return Promise.resolve(paginaFixture([]))
      return undefined
    })
    renderTesoreria()

    expect(await screen.findByText('No hay movimientos que coincidan con los filtros.')).toBeInTheDocument()
    const llamadas = apiGetMock.mock.calls.filter((call: unknown[]) => (call[0] as string).startsWith('/reportes/tesoreria?'))
    expect(llamadas).toHaveLength(1)
  })

  // Las filas se renderizan en el MISMO orden que llegan del backend (OrderBy(m => m.Id), spec
  // tesoreria: Book Preserves Chain Order) — sin ningún sort del lado del cliente. Tres filas
  // encadenadas con `final` values 60, 100, 145 (mismo fixture que el test de backend).
  it('renderiza las filas en el orden de cadena que devuelve el backend, con cada columna', async () => {
    // stage-gastos-origen-fondos-pos (PR2): `concepto` se elige DISTINTO de toda etiqueta de
    // `Tipo` ("Retiro de caja"/"Depósito"/"Gasto"/"Ajuste") — la columna Tipo nueva reusa esas
    // mismas palabras, así que un concepto homónimo sería ambiguo para `getByText`.
    const filaUno = movimientoFixture({ id: 60, inicio: 5, ingreso: 55, egreso: 0, final: 60, concepto: 'Apertura de caja', idEmpleado: 4 })
    const filaDos = movimientoFixture({ id: 61, inicio: 60, ingreso: 40, egreso: 0, final: 100, concepto: 'Carga de fondos', idEmpleado: 5 })
    const filaTres = movimientoFixture({ id: 62, inicio: 100, ingreso: 0, egreso: 45, final: 55, concepto: 'Retiro físico', idEmpleado: 4 })
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/tesoreria?')) return Promise.resolve(paginaFixture([filaUno, filaDos, filaTres]))
      return undefined
    })
    renderTesoreria()

    await screen.findByText('Apertura de caja')
    const filas = screen.getAllByRole('row').slice(1) // sin la fila de encabezado
    expect(within(filas[0]).getByText('Apertura de caja')).toBeInTheDocument()
    expect(within(filas[0]).getByText('$ 60,00')).toBeInTheDocument()
    expect(within(filas[1]).getByText('Carga de fondos')).toBeInTheDocument()
    expect(within(filas[1]).getByText('$ 100,00')).toBeInTheDocument()
    expect(within(filas[2]).getByText('Retiro físico')).toBeInTheDocument()
    expect(within(filas[2]).getByText('$ 55,00')).toBeInTheDocument()
  })

  // Cláusula bajo prueba: `etiquetaDeTipoMovimiento` — mapea las cuatro variantes de
  // `TipoMovimientoTesoreria` a su etiqueta en español (stage-gastos-origen-fondos-pos, PR2: la
  // columna Tipo empieza a mostrarse; antes se traía pero nunca se renderizaba).
  it('la columna Tipo muestra la etiqueta en español de cada TipoMovimientoTesoreria', async () => {
    const filaRetiro = movimientoFixture({ id: 70, tipo: 'RetiroCaja', concepto: 'Cierre de turno' })
    const filaDeposito = movimientoFixture({ id: 71, tipo: 'Deposito', concepto: 'Ingreso manual' })
    const filaGasto = movimientoFixture({ id: 72, tipo: 'Gasto', concepto: 'Flete de mercadería' })
    const filaAjuste = movimientoFixture({ id: 73, tipo: 'Ajuste', concepto: 'Ajuste manual' })
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/tesoreria?')) {
        return Promise.resolve(paginaFixture([filaRetiro, filaDeposito, filaGasto, filaAjuste]))
      }
      return undefined
    })
    renderTesoreria()

    const filaDeRetiro = await screen.findByRole('row', { name: /Cierre de turno/ })
    expect(within(filaDeRetiro).getByText('Retiro de caja')).toBeInTheDocument()

    expect(within(screen.getByRole('row', { name: /Ingreso manual/ })).getByText('Depósito')).toBeInTheDocument()
    expect(within(screen.getByRole('row', { name: /Flete de mercadería/ })).getByText('Gasto')).toBeInTheDocument()
    expect(within(screen.getByRole('row', { name: /Ajuste manual/ })).getByText('Ajuste')).toBeInTheDocument()
  })

  // stage-tesoreria-por-empresa (PR5): una fila `Gasto` muestra la categoría/concepto del gasto de
  // origen en la columna Concepto, no el `concepto` genérico del movimiento.
  it('una fila de tipo Gasto muestra la categoría y el concepto del gasto de origen', async () => {
    const filaGasto = movimientoFixture({
      id: 80,
      tipo: 'Gasto',
      idPuntoVenta: null,
      nombrePuntoVenta: null,
      idGasto: 500,
      gastoCategoria: 'Otros',
      gastoConcepto: 'Alquiler de depósito',
      concepto: 'Gasto de administración',
    })
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/tesoreria?')) return Promise.resolve(paginaFixture([filaGasto]))
      return undefined
    })
    renderTesoreria()

    const fila = await screen.findByRole('row', { name: /Alquiler de depósito/ })
    expect(within(fila).getByText('—')).toBeInTheDocument() // sin punto de venta
    expect(within(fila).queryByText('Gasto de administración')).not.toBeInTheDocument()
  })

  it('el punto de venta dado de baja lógica (nombrePuntoVenta null) se muestra como "(no disponible)"', async () => {
    const fila = movimientoFixture({ id: 90, idPuntoVenta: 10, nombrePuntoVenta: null, concepto: 'Cierre de turno' })
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/tesoreria?')) return Promise.resolve(paginaFixture([fila]))
      return undefined
    })
    renderTesoreria()

    const filaRenderizada = await screen.findByRole('row', { name: /Cierre de turno/ })
    expect(within(filaRenderizada).getByText('(no disponible)')).toBeInTheDocument()
  })

  it('cambiar el punto de venta dispara una nueva consulta con el idPuntoVenta elegido', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderTesoreria()

    await screen.findByText('Apertura de turno')
    apiGetMock.mockClear()
    mockearRutasBase()
    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '11')

    await waitFor(() => {
      const llamadas = apiGetMock.mock.calls.filter((call: unknown[]) => (call[0] as string).startsWith('/reportes/tesoreria?'))
      expect(llamadas.some((call: unknown[]) => (call[0] as string).includes('idPuntoVenta=11'))).toBe(true)
    })
  })

  // stage-tesoreria-por-empresa (PR5): un filtro puntual de PV muestra un SUBCONJUNTO de la
  // cadena de la empresa — la pantalla avisa que los saldos pertenecen a la cadena completa.
  it('elegir un punto de venta puntual muestra la nota de subconjunto; "Todos" no', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderTesoreria()

    await screen.findByText('Apertura de turno')
    expect(screen.queryByText(/cadena completa de la empresa/)).not.toBeInTheDocument()

    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '11')
    expect(await screen.findByText(/cadena completa de la empresa/)).toBeInTheDocument()

    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '')
    await waitFor(() => expect(screen.queryByText(/cadena completa de la empresa/)).not.toBeInTheDocument())
  })

  it('el botón de descarga apunta a /reportes/tesoreria/export con idEmpresa y sin idPuntoVenta por defecto', async () => {
    mockearRutasBase()
    apiDescargarMock.mockRejectedValueOnce(new Error('no se pudo descargar'))
    const usuario = userEvent.setup()
    renderTesoreria()

    await screen.findByText('Apertura de turno')
    await usuario.click(screen.getByRole('button', { name: 'Descargar' }))

    expect(await screen.findByText('No se pudo descargar el archivo.')).toBeInTheDocument()
    const ruta = apiDescargarMock.mock.calls[0][0] as string
    expect(ruta).toMatch(/^\/reportes\/tesoreria\/export\?idEmpresa=1/)
    expect(ruta).not.toContain('idPuntoVenta=')
  })

  // judgment-day PR5, hallazgo #5 (react-async-state): deseleccionar la empresa mientras un
  // fetch sigue en vuelo tiene que bumpear la generación. El síntoma NO es visible con la empresa
  // en null (la sección de tabla queda gateada por `filtros === null` de todos modos) — el síntoma
  // real es que el estado `pagina` queda contaminado con la respuesta vieja, y esa contaminación
  // se filtra a la SIGUIENTE selección de empresa (aparece un instante antes de que la fetch nueva
  // resuelva). Por eso el assert decisivo es DESPUÉS de volver a elegir una empresa, con la
  // segunda fetch todavía sin resolver.
  it('deseleccionar la empresa con un fetch en vuelo no contamina la siguiente selección', async () => {
    let resolverPrimera: (valor: PaginaDeMovimientosTesoreria) => void = () => {}
    const primera = new Promise<PaginaDeMovimientosTesoreria>((resolve) => {
      resolverPrimera = resolve
    })
    const segunda = new Promise<PaginaDeMovimientosTesoreria>(() => {}) // nunca resuelve en este test
    let cantidadDeLlamadas = 0

    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/tesoreria?')) {
        cantidadDeLlamadas += 1
        return cantidadDeLlamadas === 1 ? primera : segunda
      }
      return undefined
    })

    const usuario = userEvent.setup()
    renderTesoreria()

    await screen.findByLabelText('Punto de venta')
    // El fetch inicial (disparado por la preselección de empresa única) sigue en vuelo acá.

    await usuario.selectOptions(screen.getByLabelText('Empresa'), '')
    expect(screen.getByText('Elegí una empresa para ver su libro de tesorería.')).toBeInTheDocument()

    resolverPrimera(paginaFixture([movimientoFixture({ id: 777, concepto: 'respuesta-tardia-tras-deseleccionar' })]))
    await waitFor(() => expect(screen.queryByText('respuesta-tardia-tras-deseleccionar')).not.toBeInTheDocument())

    // Vuelve a elegir la empresa: dispara la SEGUNDA fetch, que queda pendiente para siempre en
    // este test. Sin el fix, la fila de la respuesta vieja ya vive en `pagina` desde el paso
    // anterior y aparece ACÁ, antes de que la segunda fetch resuelva.
    await usuario.selectOptions(screen.getByLabelText('Empresa'), String(empresaUnica.id))
    expect(screen.queryByText('respuesta-tardia-tras-deseleccionar')).not.toBeInTheDocument()
  })

  it('una respuesta de listado desactualizada nunca pisa la más reciente (generación)', async () => {
    let resolverPrimera: (valor: PaginaDeMovimientosTesoreria) => void = () => {}
    const primera = new Promise<PaginaDeMovimientosTesoreria>((resolve) => {
      resolverPrimera = resolve
    })
    let cantidadDeLlamadas = 0

    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/tesoreria?')) {
        cantidadDeLlamadas += 1
        if (cantidadDeLlamadas === 1) return primera
        return Promise.resolve(paginaFixture([movimientoFixture({ id: 999, concepto: 'segunda-respuesta' })]))
      }
      return undefined
    })

    const usuario = userEvent.setup()
    renderTesoreria()
    await screen.findByLabelText('Punto de venta')

    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), '11')
    expect(await screen.findByText('segunda-respuesta')).toBeInTheDocument()

    resolverPrimera(paginaFixture([movimientoFixture({ id: 1, concepto: 'primera-respuesta-vieja' })]))
    await waitFor(() => expect(screen.queryByText('primera-respuesta-vieja')).not.toBeInTheDocument())
    expect(screen.getByText('segunda-respuesta')).toBeInTheDocument()
  })
})

describe('Tesoreria — role gating (spec: A Supervisor Reads The G2 Listing And The G3 Book)', () => {
  it('un Supervisor llega a /caja/tesoreria', async () => {
    mockearRutasBase()
    renderTesoreriaProtegido()

    await screen.findByText('Apertura de turno')
    expect(screen.queryByText('Inicio (redirigido)')).not.toBeInTheDocument()
  })

  it('un Vendedor nunca llega a /caja/tesoreria: redirige a Inicio', async () => {
    usuarioActual = usuarioFixture({ id: 4, usuario: 'vendedor', rolId: ROL.Vendedor, rol: 'Vendedor' })
    mockearRutasBase()

    renderTesoreriaProtegido()

    expect(await screen.findByText('Inicio (redirigido)')).toBeInTheDocument()
    await waitFor(() => expect(screen.queryByText('Tesorería')).not.toBeInTheDocument())
  })
})
