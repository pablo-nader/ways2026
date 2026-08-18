import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { CuentaCorrienteDeProveedor } from './CuentaCorrienteDeProveedor'
import { ErrorApi } from '../api/cliente'
import { ROL } from '../api/tipos'
import type {
  MovimientoDeCuentaDeProveedor,
  PaginaDeEstadoDeCuentaDeProveedor,
  ProveedorListado,
  PuntoVentaListado,
  UsuarioAutenticado,
} from '../api/tipos'

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

function proveedorFixture(sobrescribir: Partial<ProveedorListado> = {}): ProveedorListado {
  return {
    id: 1,
    razonSocial: 'Proveedor Uno SA',
    nombreFantasia: null,
    cuit: null,
    idCondicionFiscal: 1,
    domicilio: null,
    telefono: null,
    email: null,
    vendedor: null,
    celularVendedor: null,
    supervisor: null,
    celularSupervisor: null,
    margen: null,
    observaciones: null,
    activo: true,
    idEmpresa: null,
    ...sobrescribir,
  }
}

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

// `importe`/`saldoResultante` deliberadamente distintos del `saldo` del header (500) y entre sí
// (mutation-proof-tests regla 11), para que ningún assert de "$500,00" choque con una fila.
function movimientoFixture(sobrescribir: Partial<MovimientoDeCuentaDeProveedor> = {}): MovimientoDeCuentaDeProveedor {
  return {
    idMovimiento: 1,
    fecha: '2026-08-01T12:00:00Z',
    tipo: 'Compra',
    importe: 300,
    saldoResultante: 200,
    detalle: null,
    idComprobanteCompra: 10,
    idGasto: null,
    etiqueta: null,
    ...sobrescribir,
  }
}

function paginaFixture(sobrescribir: Partial<PaginaDeEstadoDeCuentaDeProveedor> = {}): PaginaDeEstadoDeCuentaDeProveedor {
  return {
    header: { idProveedor: 1, saldo: 500 },
    items: [movimientoFixture()],
    total: 1,
    pagina: 1,
    tamanio: 25,
    historico: false,
    desde: '2026-07-01T00:00:00Z',
    hasta: null,
    ...sobrescribir,
  }
}

function renderPantalla(idProveedor: number | string = 1, state?: { proveedor: ProveedorListado }) {
  return render(
    <MemoryRouter initialEntries={[{ pathname: `/proveedores/${idProveedor}/cuenta-corriente`, state }]}>
      <Routes>
        <Route path="/proveedores/:id/cuenta-corriente" element={<CuentaCorrienteDeProveedor />} />
      </Routes>
    </MemoryRouter>,
  )
}

function mockearRutasBase(sobrescribirGet?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    const propia = sobrescribirGet?.(ruta)
    if (propia) return propia
    if (/^\/proveedores\/\d+$/.test(ruta)) return Promise.resolve<ProveedorListado>(proveedorFixture())
    if (ruta === '/puntos-venta') return Promise.resolve<PuntoVentaListado[]>([puntoVentaFixture()])
    if (ruta.includes('/cuenta-corriente')) return Promise.resolve<PaginaDeEstadoDeCuentaDeProveedor>(paginaFixture())
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  usuarioActual = usuarioFixture()
})

async function abrirModalDeAjuste() {
  mockearRutasBase()
  renderPantalla(1, { proveedor: proveedorFixture() })

  await screen.findByText('$500,00')
  await userEvent.click(screen.getByRole('button', { name: 'Ajuste manual' }))
  await screen.findByText('Ajuste manual de cuenta corriente')
}

describe('CuentaCorrienteDeProveedor — header y ledger', () => {
  it('muestra el saldo, la fila del movimiento y "Página N de M"', async () => {
    mockearRutasBase()
    renderPantalla(1, { proveedor: proveedorFixture() })

    expect(await screen.findByText('$500,00')).toBeInTheDocument()
    expect(screen.getByText('Compra')).toBeInTheDocument()
    expect(screen.getByText('Compra #10')).toBeInTheDocument()
    expect(screen.getByText(/Página 1 de 1/)).toBeInTheDocument()
  })

  it('un saldo negativo en el header y en una fila muestra "(saldo a favor)", nunca clampeado a cero', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.includes('/cuenta-corriente')) {
        return Promise.resolve<PaginaDeEstadoDeCuentaDeProveedor>(
          paginaFixture({
            header: { idProveedor: 1, saldo: -500 },
            items: [movimientoFixture({ saldoResultante: -200 })],
          }),
        )
      }
      return undefined
    })
    renderPantalla(1, { proveedor: proveedorFixture() })

    expect(await screen.findByText('-$500,00 (saldo a favor)')).toBeInTheDocument()
    expect(screen.getByText('-$200,00 (saldo a favor)')).toBeInTheDocument()
  })

  it('un período sin movimientos muestra el estado vacío', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.includes('/cuenta-corriente')) {
        return Promise.resolve<PaginaDeEstadoDeCuentaDeProveedor>(paginaFixture({ items: [], total: 0 }))
      }
      return undefined
    })
    renderPantalla(1, { proveedor: proveedorFixture() })

    expect(await screen.findByText('No hay movimientos en el período seleccionado.')).toBeInTheDocument()
  })
})

describe('CuentaCorrienteDeProveedor — gating de rol (Supervisor+Admin) para el ajuste', () => {
  it('un Vendedor no ve "Ajuste manual"', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor' })
    mockearRutasBase()
    renderPantalla(1, { proveedor: proveedorFixture() })

    await screen.findByText('$500,00')
    expect(screen.queryByRole('button', { name: 'Ajuste manual' })).not.toBeInTheDocument()
  })

  it('un Supervisor ve la acción, habilitada una vez cargados los datos', async () => {
    mockearRutasBase()
    renderPantalla(1, { proveedor: proveedorFixture() })

    await screen.findByText('$500,00')
    expect(screen.getByRole('button', { name: 'Ajuste manual' })).toBeEnabled()
  })

  it('un Admin también ve la acción', async () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Admin, rol: 'Admin' })
    mockearRutasBase()
    renderPantalla(1, { proveedor: proveedorFixture() })

    await screen.findByText('$500,00')
    expect(screen.getByRole('button', { name: 'Ajuste manual' })).toBeEnabled()
  })
})

describe('CuentaCorrienteDeProveedor — filtros (react-async-state regla 2, mutation-proof-tests regla 7)', () => {
  // El flush del microtask va DENTRO de act: un `waitFor` solo pasaría en su primer tick, antes de
  // que el `.then` obsoleto aterrice — envolver en act y assertar sincrónicamente después SÍ
  // discrimina (mutation-proof-tests regla 7).
  it('una respuesta desactualizada nunca pisa la más reciente', async () => {
    let resolverPrimera: (v: PaginaDeEstadoDeCuentaDeProveedor) => void = () => {}
    const primeraPendiente = new Promise<PaginaDeEstadoDeCuentaDeProveedor>((resolve) => {
      resolverPrimera = resolve
    })
    let llamadas = 0

    mockearRutasBase((ruta) => {
      if (!ruta.includes('/cuenta-corriente')) return undefined
      llamadas += 1
      if (llamadas === 1) return Promise.resolve<PaginaDeEstadoDeCuentaDeProveedor>(paginaFixture())
      if (llamadas === 2) return primeraPendiente
      if (llamadas === 3) {
        return Promise.resolve<PaginaDeEstadoDeCuentaDeProveedor>(
          paginaFixture({ items: [movimientoFixture({ idMovimiento: 3, importe: 999, saldoResultante: 111 })] }),
        )
      }
      return Promise.reject(new Error('llamada inesperada'))
    })

    renderPantalla(1, { proveedor: proveedorFixture() })
    await screen.findByLabelText('Ver histórico completo')

    // 1ra: activa histórico (queda pendiente, lenta).
    await userEvent.click(screen.getByLabelText('Ver histórico completo'))
    expect(screen.getByLabelText('Desde')).toBeDisabled()

    // 2da: lo desactiva de nuevo — dispara una generación MÁS NUEVA, que resuelve rápido.
    await userEvent.click(screen.getByLabelText('Ver histórico completo'))

    await waitFor(() => expect(screen.getByText('$999,00')).toBeInTheDocument())

    await act(async () => {
      resolverPrimera(paginaFixture({ items: [movimientoFixture({ idMovimiento: 2, importe: 777, saldoResultante: 888 })] }))
      await primeraPendiente
    })
    expect(screen.getByText('$999,00')).toBeInTheDocument()
    expect(screen.queryByText('$777,00')).not.toBeInTheDocument()
    expect(screen.queryByText('$888,00')).not.toBeInTheDocument()
  })
})

describe('CuentaCorrienteDeProveedor — pager', () => {
  it('está deshabilitado en ambos bordes cuando hay una sola página', async () => {
    mockearRutasBase()
    renderPantalla(1, { proveedor: proveedorFixture() })

    await screen.findByText('$500,00')
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()
  })

  it('navega entre páginas, con "Anterior"/"Siguiente" habilitados solo del lado correcto', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.includes('/cuenta-corriente')) {
        const paginaSolicitada = ruta.includes('pagina=2') ? 2 : 1
        return Promise.resolve<PaginaDeEstadoDeCuentaDeProveedor>(
          paginaFixture({ total: 50, pagina: paginaSolicitada, tamanio: 25 }),
        )
      }
      return undefined
    })
    const usuario = userEvent.setup()
    renderPantalla(1, { proveedor: proveedorFixture() })

    await screen.findByText(/Página 1 de 2/)
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeEnabled()

    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    await screen.findByText(/Página 2 de 2/)
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()
  })
})

describe('CuentaCorrienteDeProveedor — modal de ajuste manual', () => {
  it('arma el cuerpo del POST con la forma exacta del contrato, detalle recortado, y refresca el ledger', async () => {
    mockearRutasBase()
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/proveedores/1/cuenta-corriente/ajustes'
        ? Promise.resolve<MovimientoDeCuentaDeProveedor>(
            movimientoFixture({ idMovimiento: 55, tipo: 'Ajuste', importe: -200, saldoResultante: 300, detalle: 'nota' }),
          )
        : Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`)),
    )

    await abrirModalDeAjuste()
    await userEvent.type(screen.getByLabelText('Importe'), '-200')
    await userEvent.type(screen.getByLabelText('Detalle (obligatorio)'), '  nota de ajuste  ')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith('/proveedores/1/cuenta-corriente/ajustes', {
        idPuntoVenta: 7,
        importe: -200,
        detalle: 'nota de ajuste',
      }),
    )
    expect(await screen.findByText('Ajuste registrado: -$200,00.')).toBeInTheDocument()
    // el refetch corre tras el 201 — dos GET del ledger (montaje + refresco).
    await waitFor(() => {
      const llamadas = apiGetMock.mock.calls.filter((c: unknown[]) => (c[0] as string).includes('/cuenta-corriente?'))
      expect(llamadas.length).toBeGreaterThanOrEqual(2)
    })
  })

  it('importe cero se rechaza localmente, sin llamar al servidor', async () => {
    await abrirModalDeAjuste()
    await userEvent.type(screen.getByLabelText('Importe'), '0')
    await userEvent.type(screen.getByLabelText('Detalle (obligatorio)'), 'motivo válido')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    expect(await screen.findByText('El importe del ajuste no puede ser cero.')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  // Prueba la guarda de re-entrancia (`if (registrandoRef.current) return`, regla 9): el ref cubre
  // la ventana same-tick antes del re-render de React, `disabled` cubre el resto — son DOS
  // defensas complementarias. Para que el guard del ref sea la ÚNICA defensa observable, los dos
  // clicks viajan DENTRO de un mismo `act()` (no dos `await userEvent.click` separados), así
  // ningún re-render de React corre entre ellos — si no, el segundo click ya vería el `disabled`
  // puesto por el primero y el test probaría el atributo, no la guarda del `ref` (mismo patrón que
  // `BotonDeDescarga.test.tsx`).
  it('doble click en el mismo tick en "Registrar ajuste" dispara exactamente un POST', async () => {
    await abrirModalDeAjuste()
    await userEvent.type(screen.getByLabelText('Importe'), '-200')
    await userEvent.type(screen.getByLabelText('Detalle (obligatorio)'), 'motivo válido')

    let resolverAjuste: (m: MovimientoDeCuentaDeProveedor) => void = () => {}
    const ajustePendiente = new Promise<MovimientoDeCuentaDeProveedor>((resolve) => {
      resolverAjuste = resolve
    })
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/proveedores/1/cuenta-corriente/ajustes' ? ajustePendiente : Promise.reject(new Error(`ruta no mockeada: ${ruta}`)),
    )

    const boton = screen.getByRole('button', { name: 'Registrar ajuste' })
    act(() => {
      boton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
      boton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
    })

    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/proveedores/1/cuenta-corriente/ajustes')).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Registrando…' })).toBeDisabled()

    await act(async () => {
      resolverAjuste(movimientoFixture({ idMovimiento: 55, tipo: 'Ajuste', importe: -200, saldoResultante: 300 }))
      await Promise.resolve()
    })
  })

  it('un ajuste 2xx nunca se reporta como fallo, aunque el refetch del ledger posterior falle', async () => {
    let llamadasLedger = 0
    mockearRutasBase((ruta) => {
      if (ruta.includes('/cuenta-corriente?')) {
        llamadasLedger += 1
        if (llamadasLedger === 1) return Promise.resolve<PaginaDeEstadoDeCuentaDeProveedor>(paginaFixture())
        return Promise.reject(new ErrorApi(500, 'error', 'falló el refresco'))
      }
      return undefined
    })
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/proveedores/1/cuenta-corriente/ajustes'
        ? Promise.resolve<MovimientoDeCuentaDeProveedor>(movimientoFixture({ idMovimiento: 55, tipo: 'Ajuste', importe: -200 }))
        : Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`)),
    )

    renderPantalla(1, { proveedor: proveedorFixture() })
    await screen.findByText('$500,00')
    await userEvent.click(screen.getByRole('button', { name: 'Ajuste manual' }))
    await screen.findByText('Ajuste manual de cuenta corriente')

    await userEvent.type(screen.getByLabelText('Importe'), '-200')
    await userEvent.type(screen.getByLabelText('Detalle (obligatorio)'), 'motivo válido')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    // El ajuste 2xx se reporta como éxito (aviso visible) SIEMPRE — el refetch fallido es un
    // problema DISTINTO, visible aparte, nunca disfrazado de fallo del ajuste (regla 6).
    expect(await screen.findByText('Ajuste registrado: -$200,00.')).toBeInTheDocument()
    expect(await screen.findByText('falló el refresco')).toBeInTheDocument()
  })
})
