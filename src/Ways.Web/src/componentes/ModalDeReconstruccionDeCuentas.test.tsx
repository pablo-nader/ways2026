import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ModalDeReconstruccionDeCuentas } from './ModalDeReconstruccionDeCuentas'
import { ErrorApi } from '../api/cliente'
import { rangoDeReconstruccionPorDefecto, type PlanDeReconstruccion } from '../api/cuentasTesoreria'
import type { EmpresaListado } from '../api/tipos'
import { fechaHoraATexto } from '../formato/fechas'

const apiPostMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: vi.fn(),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown])),
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

afterEach(cleanup)

const RUTA_VISTA_PREVIA = '/cuentas-tesoreria/reconstruccion/vista-previa'
const RUTA_APLICAR = '/cuentas-tesoreria/reconstruccion'

function empresa(id: number, razonSocial: string): EmpresaListado {
  return {
    id,
    idTenant: 1,
    razonSocial,
    nombreFantasia: null,
    cuit: null,
    nombreTenant: 'Tenant',
    alicuotaPercepcionIibb: null,
    alicuotaPercepcionIva: null,
  }
}

function diferido<T>() {
  let resolver!: (valor: T) => void
  let rechazar!: (motivo: unknown) => void
  const promesa = new Promise<T>((res, rej) => {
    resolver = res
    rechazar = rej
  })

  return { promesa, resolver, rechazar }
}

function plan(sobrescribir: Partial<PlanDeReconstruccion> = {}): PlanDeReconstruccion {
  return {
    idEmpresa: 1,
    desde: '2026-10-01',
    hasta: '2026-10-31',
    turnos: 1,
    pagos: 0,
    filas: 3,
    acreditaciones: [
      {
        idTurnoCaja: 41,
        fechaCierre: '2026-10-05T20:00:00Z',
        idMedioPago: 2,
        nombreMedio: 'Mercado Pago QR',
        idCuentaTesoreria: 9,
        nombreCuenta: 'Cuenta MP',
        baseBruta: 1000,
        neto: 950,
        componentes: [
          { tipo: 'Cobro', ingreso: 1000, egreso: 0, porcentajeAplicado: null },
          { tipo: 'Comision', ingreso: 0, egreso: 40, porcentajeAplicado: 4 },
          { tipo: 'RetencionIibb', ingreso: 0, egreso: 10, porcentajeAplicado: 1 },
        ],
      },
    ],
    debitos: [],
    cuentas: [{ idCuentaTesoreria: 9, nombre: 'Cuenta MP', filas: 3, neto: 950, saldoAntes: 100, saldoDespues: 1050 }],
    conciliadoHasta: null,
    congeladas: [],
    debitosCongelados: [],
    ...sobrescribir,
  }
}

const planVacio = () => plan({ turnos: 0, filas: 0, acreditaciones: [], cuentas: [] })

const debito = {
  idPago: 77,
  fecha: '2026-10-07T15:00:00Z',
  categoria: 'Servicios' as const,
  concepto: 'Luz',
  idMedioPago: 5,
  nombreMedio: 'Transferencia',
  idCuentaTesoreria: 9,
  nombreCuenta: 'Cuenta MP',
  importe: 300,
}

const planConDebito = () =>
  plan({
    pagos: 1,
    filas: 4,
    debitos: [debito],
    cuentas: [{ idCuentaTesoreria: 9, nombre: 'Cuenta MP', filas: 4, neto: 650, saldoAntes: 100, saldoDespues: 750 }],
  })

const MARCA = '2026-10-06T03:00:00Z'
const TITULO_CONGELADO = `Congelado por la conciliación (hasta ${fechaHoraATexto(MARCA)})`

const acreditacionCongelada = {
  idTurnoCaja: 12,
  fechaCierre: '2026-10-02T21:00:00Z',
  idMedioPago: 3,
  nombreMedio: 'Posnet',
  idCuentaTesoreria: 9,
  nombreCuenta: 'Cuenta MP',
  baseBruta: 500,
  neto: 480,
  componentes: [
    { tipo: 'Cobro' as const, ingreso: 500, egreso: 0, porcentajeAplicado: null },
    { tipo: 'Comision' as const, ingreso: 0, egreso: 20, porcentajeAplicado: 4 },
  ],
}

const debitoCongelado = { ...debito, idPago: 33, fecha: '2026-10-03T12:00:00Z', concepto: 'Alquiler', importe: 900 }

function montar(empresas: EmpresaListado[] = [empresa(1, 'Comercio SA')]) {
  const onCerrar = vi.fn()
  const onReconstruido = vi.fn()
  const vista = render(<ModalDeReconstruccionDeCuentas empresas={empresas} onCerrar={onCerrar} onReconstruido={onReconstruido} />)

  return { onCerrar, onReconstruido, ...vista }
}

const botonVistaPrevia = () => screen.getByRole('button', { name: /vista previa|calculando/i })
const botonConfirmar = () => screen.getByRole('button', { name: /confirmar reconstrucción|reconstruyendo/i })

async function cambiarDesde(usuario: ReturnType<typeof userEvent.setup>, texto: string) {
  const campo = screen.getByLabelText('Desde')
  await usuario.clear(campo)
  await usuario.type(campo, texto)
}

beforeEach(() => {
  apiPostMock.mockReset()
})

describe('ModalDeReconstruccionDeCuentas', () => {
  it('preselecciona la única empresa, propone el mes en curso y deja la confirmación inerte antes de la vista previa', () => {
    montar()

    expect(screen.getByLabelText('Empresa')).toHaveValue('1')
    expect(botonConfirmar()).toBeDisabled()
    expect(screen.getByText(/porcentajes de comisión y retenciones que tiene cada medio hoy/i)).toBeInTheDocument()
    expect(screen.getByText(/el día en que se cerró el turno/i)).toBeInTheDocument()
    expect(screen.getByText(/pagos hechos con esos medios que todavía no la debitaron/i)).toBeInTheDocument()
  })

  it('con varias empresas pide elegir y no calcula sin empresa', async () => {
    const usuario = userEvent.setup()
    montar([empresa(1, 'Comercio SA'), empresa(2, 'Otra SRL')])

    expect(screen.getByLabelText('Empresa')).toHaveValue('')
    await usuario.click(botonVistaPrevia())

    expect(await screen.findByRole('alert')).toHaveTextContent('Elegí la empresa.')
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('la vista previa muestra acreditaciones y efecto por cuenta, y habilita la confirmación', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockResolvedValue(plan())
    montar()

    await usuario.click(botonVistaPrevia())

    const acreditaciones = await screen.findByRole('table', { name: 'Acreditaciones' })
    const fila = within(acreditaciones).getByText('#41').closest('tr') as HTMLElement
    expect(within(fila).getByText('Mercado Pago QR')).toBeInTheDocument()
    expect(within(fila).getByText('Cuenta MP')).toBeInTheDocument()
    expect(fila.textContent).toContain('1.000')
    expect(fila.textContent).toContain('40')
    expect(fila.textContent).toContain('950')

    const cuentas = screen.getByRole('table', { name: 'Efecto por cuenta' })
    const filaCuenta = within(cuentas).getByText('Cuenta MP').closest('tr') as HTMLElement
    expect(filaCuenta.textContent).toContain('100')
    expect(filaCuenta.textContent).toContain('1.050')

    expect(apiPostMock).toHaveBeenCalledWith(RUTA_VISTA_PREVIA, { idEmpresa: 1, ...rangoDeReconstruccionPorDefecto() })
    expect(botonConfirmar()).toBeEnabled()
  })

  it('la vista previa muestra los débitos de pagos, los cuenta en el resumen y en el efecto por cuenta', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockResolvedValue(planConDebito())
    montar()

    await usuario.click(botonVistaPrevia())

    const debitos = await screen.findByRole('table', { name: 'Débitos de pagos' })
    const fila = within(debitos).getByText('#77 · Luz').closest('tr') as HTMLElement
    expect(within(fila).getByText('Transferencia')).toBeInTheDocument()
    expect(within(fila).getByText('Cuenta MP')).toBeInTheDocument()
    expect(fila.textContent).toContain('300')
    expect(screen.getByText(/de 1 turno y 1 pago\./)).toBeInTheDocument()

    const cuentas = screen.getByRole('table', { name: 'Efecto por cuenta' })
    const filaCuenta = within(cuentas).getByText('Cuenta MP').closest('tr') as HTMLElement
    expect(filaCuenta.textContent).toContain('650')
    expect(filaCuenta.textContent).toContain('750')
    expect(botonConfirmar()).toBeEnabled()
  })

  it('con solo débitos de pagos no muestra la tabla de acreditaciones y deja confirmar', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockResolvedValue(
      plan({
        turnos: 0,
        pagos: 1,
        filas: 1,
        acreditaciones: [],
        debitos: [debito],
        cuentas: [{ idCuentaTesoreria: 9, nombre: 'Cuenta MP', filas: 1, neto: -300, saldoAntes: 100, saldoDespues: -200 }],
      }),
    )
    montar()

    await usuario.click(botonVistaPrevia())

    await screen.findByRole('table', { name: 'Débitos de pagos' })
    expect(screen.queryByRole('table', { name: 'Acreditaciones' })).not.toBeInTheDocument()
    expect(screen.getByText(/de 1 pago\./)).toBeInTheDocument()
    expect(botonConfirmar()).toBeEnabled()
  })

  it('sin nada pendiente muestra el mensaje vacío y no deja confirmar', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockResolvedValue(planVacio())
    montar()

    await usuario.click(botonVistaPrevia())

    expect(await screen.findByText('No hay turnos sin acreditar ni pagos sin debitar en ese rango.')).toBeInTheDocument()
    expect(screen.queryByRole('table', { name: 'Débitos de pagos' })).not.toBeInTheDocument()
    expect(botonConfirmar()).toBeDisabled()
  })

  it('con todo congelado no dice que no hay nada pendiente, lista lo congelado y no deja confirmar', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockResolvedValue(
      plan({
        turnos: 0,
        filas: 0,
        acreditaciones: [],
        cuentas: [],
        conciliadoHasta: MARCA,
        congeladas: [acreditacionCongelada],
        debitosCongelados: [debitoCongelado],
      }),
    )
    montar()

    await usuario.click(botonVistaPrevia())

    const congelado = await screen.findByRole('region', { name: TITULO_CONGELADO })
    expect(screen.queryByText('No hay turnos sin acreditar ni pagos sin debitar en ese rango.')).not.toBeInTheDocument()
    expect(screen.getByText(/no se puede escribir nada: lo pendiente de ese rango ya está conciliado/i)).toBeInTheDocument()

    const turnos = within(congelado).getByRole('table', { name: 'Acreditaciones congeladas' })
    const filaTurno = within(turnos).getByText('#12').closest('tr') as HTMLElement
    expect(within(filaTurno).getByText('Posnet')).toBeInTheDocument()
    expect(filaTurno.textContent).toContain('480')

    const pagos = within(congelado).getByRole('table', { name: 'Débitos congelados' })
    const filaPago = within(pagos).getByText('#33 · Alquiler').closest('tr') as HTMLElement
    expect(within(filaPago).getByText('Transferencia')).toBeInTheDocument()
    expect(filaPago.textContent).toContain('900')

    expect(screen.queryByRole('table', { name: 'Efecto por cuenta' })).not.toBeInTheDocument()
    expect(botonConfirmar()).toBeDisabled()
  })

  it('con filas para escribir y algo congelado muestra las dos cosas y deja confirmar', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockResolvedValue(plan({ conciliadoHasta: MARCA, congeladas: [], debitosCongelados: [debitoCongelado] }))
    montar()

    await usuario.click(botonVistaPrevia())

    await screen.findByRole('table', { name: 'Acreditaciones' })
    const congelado = screen.getByRole('region', { name: TITULO_CONGELADO })
    expect(within(congelado).getByText('#33 · Alquiler')).toBeInTheDocument()
    expect(within(congelado).queryByRole('table', { name: 'Acreditaciones congeladas' })).not.toBeInTheDocument()
    expect(screen.getByRole('table', { name: 'Efecto por cuenta' })).toBeInTheDocument()
    expect(botonConfirmar()).toBeEnabled()
  })

  it('sin nada congelado no muestra la sección de la conciliación', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockResolvedValue(plan({ conciliadoHasta: MARCA }))
    montar()

    await usuario.click(botonVistaPrevia())

    await screen.findByRole('table', { name: 'Acreditaciones' })
    expect(screen.queryByRole('region', { name: /congelado por la conciliación/i })).not.toBeInTheDocument()
  })

  it('cambiar las fechas invalida la vista previa y la confirmación', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockResolvedValue(plan())
    montar()

    await usuario.click(botonVistaPrevia())
    await screen.findByRole('table', { name: 'Acreditaciones' })
    expect(botonConfirmar()).toBeEnabled()

    await cambiarDesde(usuario, '02/01/2020')

    await waitFor(() => expect(screen.queryByRole('table', { name: 'Acreditaciones' })).not.toBeInTheDocument())
    expect(botonConfirmar()).toBeDisabled()
    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  it('descarta la respuesta de una vista previa vieja si los campos cambiaron mientras tanto', async () => {
    const usuario = userEvent.setup()
    const lectura = diferido<PlanDeReconstruccion>()
    apiPostMock.mockReturnValueOnce(lectura.promesa)
    montar()

    await usuario.click(botonVistaPrevia())
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    await cambiarDesde(usuario, '02/01/2020')

    await act(async () => {
      lectura.resolver(plan())
    })

    expect(screen.queryByRole('table', { name: 'Acreditaciones' })).not.toBeInTheDocument()
    expect(botonConfirmar()).toBeDisabled()
    expect(botonVistaPrevia()).toBeEnabled()
  })

  it('confirmar escribe una sola vez aunque se haga doble clic, bloquea la ventana y avisa al llamador', async () => {
    const usuario = userEvent.setup()
    const escritura = diferido<PlanDeReconstruccion>()
    apiPostMock.mockImplementation((ruta: string) => (ruta === RUTA_VISTA_PREVIA ? Promise.resolve(plan()) : escritura.promesa))
    const { onReconstruido } = montar()

    await usuario.click(botonVistaPrevia())
    await screen.findByRole('table', { name: 'Acreditaciones' })

    await usuario.dblClick(botonConfirmar())

    await waitFor(() => expect(botonConfirmar()).toHaveTextContent('Reconstruyendo…'))
    expect(apiPostMock.mock.calls.filter((c) => c[0] === RUTA_APLICAR)).toHaveLength(1)
    expect(botonConfirmar()).toBeDisabled()
    expect(botonVistaPrevia()).toBeDisabled()
    expect(screen.getByLabelText('Empresa')).toBeDisabled()

    await act(async () => {
      escritura.resolver(plan())
    })

    expect(await screen.findByRole('status')).toHaveTextContent('se escribieron 3 filas de 1 turno.')
    expect(onReconstruido).toHaveBeenCalledTimes(1)
    expect(onReconstruido).toHaveBeenCalledWith(plan())
    expect(screen.getAllByRole('button', { name: 'Cerrar' })).toHaveLength(2)
  })

  it('un 409 concurrente se explica y exige una vista previa nueva', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === RUTA_VISTA_PREVIA
        ? Promise.resolve(plan())
        : Promise.reject(new ErrorApi(409, 'reconstruccion_concurrente', 'x')),
    )
    const { onReconstruido } = montar()

    await usuario.click(botonVistaPrevia())
    await screen.findByRole('table', { name: 'Acreditaciones' })
    await usuario.click(botonConfirmar())

    expect(await screen.findByRole('alert')).toHaveTextContent('Otra reconstrucción está en curso')
    expect(onReconstruido).not.toHaveBeenCalled()
    expect(botonConfirmar()).toBeDisabled()
    expect(botonVistaPrevia()).toBeEnabled()
  })

  it('un 400 de rango en la vista previa muestra el mensaje mapeado', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockRejectedValue(new ErrorApi(400, 'rango_demasiado_amplio', 'x'))
    montar()

    await usuario.click(botonVistaPrevia())

    expect(await screen.findByRole('alert')).toHaveTextContent('El rango no puede superar los 366 días.')
    expect(botonConfirmar()).toBeDisabled()
  })

  it('rechaza localmente un rango invertido sin llamar al servidor', async () => {
    const usuario = userEvent.setup()
    montar()

    await cambiarDesde(usuario, '01/01/2999')
    await usuario.click(botonVistaPrevia())

    expect(await screen.findByRole('alert')).toHaveTextContent('La fecha "desde" no puede ser posterior a "hasta".')
    expect(apiPostMock).not.toHaveBeenCalled()
  })
})
