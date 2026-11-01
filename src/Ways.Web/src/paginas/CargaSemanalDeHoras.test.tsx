import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, useLocation } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { CargaSemanalDeHoras } from './CargaSemanalDeHoras'
import { ErrorApi } from '../api/cliente'
import { Toaster } from '../toasts/Toaster'
import type { DiaDeCargaSemanal, FilaDeCargaSemanal, SemanaDeCargaDeHoras } from '../api/tipos'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown])),
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

const RUTA_ACTUAL = '/personal/horas/semana'

function fechasDe(desde: string): string[] {
  const base = Date.UTC(Number(desde.slice(0, 4)), Number(desde.slice(5, 7)) - 1, Number(desde.slice(8, 10)))

  return Array.from({ length: 7 }, (_, i) => new Date(base + i * 86_400_000).toISOString().slice(0, 10))
}

function dia(fecha: string, parche: Partial<DiaDeCargaSemanal> = {}): DiaDeCargaSemanal {
  return { fecha, horasCargadas: 0, confirmado: false, horas: null, precargado: false, futuro: false, ...parche }
}

/** Semana del lunes 26/10 con hoy = miércoles 28/10: lunes confirmado, martes y miércoles precargados del
 * horario habitual de Ana, el resto futuro. Beto no tiene horario habitual. */
function semanaDe(desde: string, hoy = '2026-10-28'): SemanaDeCargaDeHoras {
  const fechas = fechasDe(desde)
  const ana: FilaDeCargaSemanal = {
    idPersonal: 1,
    nombre: 'Ana Gómez',
    valorHora: 1000,
    horarioHabitual: [8, 8, 8, 8, 8, 0, 0],
    dias: fechas.map((f, i) =>
      i === 0
        ? dia(f, { horas: 6, confirmado: true, horasCargadas: 6 })
        : i < 3
          ? dia(f, { horas: 8, precargado: true })
          : dia(f, { futuro: true }),
    ),
  }
  const beto: FilaDeCargaSemanal = {
    idPersonal: 2,
    nombre: 'Beto Ruiz',
    valorHora: 2000,
    horarioHabitual: null,
    dias: fechas.map((f, i) => dia(f, { futuro: i >= 3 })),
  }

  return { desde, hasta: fechas[6], hoy, filas: [ana, beto] }
}

const SEMANA_ACTUAL = '2026-10-26'

function mockearLecturas(respuesta: (desde: string | null) => Promise<SemanaDeCargaDeHoras> = (d) => Promise.resolve(semanaDe(d ?? SEMANA_ACTUAL))) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (!ruta.startsWith(RUTA_ACTUAL)) return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    const desde = new URL(ruta, 'http://x').searchParams.get('desde')

    return respuesta(desde)
  })
}

function SondaDeRuta() {
  const { pathname } = useLocation()

  return <span data-testid="ruta">{pathname}</span>
}

function renderizar() {
  return render(
    <MemoryRouter initialEntries={['/personal/horas']}>
      <CargaSemanalDeHoras />
      <SondaDeRuta />
      <Toaster />
    </MemoryRouter>,
  )
}

/** El resultado de una escritura se avisa con un toast de éxito, nunca con un banner. */
async function toastDeExito(texto: string | RegExp) {
  const aviso = await screen.findByText(texto)
  expect(aviso.closest('.ws-toaster')).not.toBeNull()
  expect(aviso.closest('[role="status"]')).not.toBeNull()
  expect(document.body.querySelector('.alert-success')).toBeNull()
}

/** Un aviso que no se puede perder queda en un banner de la pantalla: ni es un toast ni hay uno con su texto. */
async function bannerPersistente(texto: string | RegExp) {
  const aviso = await screen.findByText(texto)
  expect(aviso.closest('.alert-warning')).not.toBeNull()
  expect(aviso.closest('.ws-toaster')).toBeNull()
  expect(screen.getAllByText(texto)).toHaveLength(1)
}

const celda = (nombre: string, etiqueta: string) => screen.getByLabelText(`Horas de ${nombre} el ${etiqueta}`) as HTMLInputElement

/** Espera a los DATOS (una celda con su valor), no al contenedor que se rinde antes. */
async function esperarSemana() {
  await waitFor(() => expect(celda('Ana Gómez', 'Lun 26/10')).toHaveValue('6'))
}

type Deferred<T> = { promesa: Promise<T>; resolver: (v: T) => void; rechazar: (e: unknown) => void }

function diferido<T>(): Deferred<T> {
  let resolver!: (v: T) => void
  let rechazar!: (e: unknown) => void
  const promesa = new Promise<T>((res, rej) => {
    resolver = res
    rechazar = rej
  })

  return { promesa, resolver, rechazar }
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  mockearLecturas()
})

describe('CargaSemanalDeHoras — lectura', () => {
  it('muestra confirmado, precargado y futuro con su estado propio', async () => {
    renderizar()
    await esperarSemana()

    const lunes = celda('Ana Gómez', 'Lun 26/10')
    const martes = celda('Ana Gómez', 'Mar 27/10')
    const jueves = celda('Ana Gómez', 'Jue 29/10')

    expect(lunes).toBeEnabled()
    expect(lunes).not.toHaveClass('fst-italic')
    expect(martes).toHaveValue('8')
    expect(martes).toHaveClass('fst-italic')
    expect(martes).toHaveAttribute('title', 'Precargado desde el horario habitual')
    expect(jueves).toBeDisabled()
    expect(jueves).toHaveValue('')
    expect(celda('Beto Ruiz', 'Lun 26/10')).toHaveValue('')
    expect(screen.getByText('26/10 – 01/11')).toBeInTheDocument()
  })

  it('totales por empleado y por día con lo que se ve', async () => {
    renderizar()
    await esperarSemana()

    const ana = screen.getByText('Ana Gómez').closest('tr') as HTMLElement
    expect(within(ana).getByText('22')).toBeInTheDocument()
    expect(within(ana).getByText('$ 22.000,00')).toBeInTheDocument()
  })

  it('un fallo de lectura se muestra y permite reintentar', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockRejectedValueOnce(new ErrorApi(500, 'error', 'Falló la lectura'))
    renderizar()

    expect(await screen.findByText('Falló la lectura')).toBeInTheDocument()
    await usuario.click(screen.getByRole('button', { name: 'Reintentar' }))

    await esperarSemana()
    expect(screen.queryByText('Falló la lectura')).not.toBeInTheDocument()
  })
})

describe('CargaSemanalDeHoras — guardar', () => {
  it('manda lo editado y lo precargado, relee la semana y avisa', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockResolvedValue({ movimientosCreados: 3, jornadasConfirmadas: 3 })
    renderizar()
    await esperarSemana()

    await usuario.clear(celda('Beto Ruiz', 'Lun 26/10'))
    await usuario.type(celda('Beto Ruiz', 'Lun 26/10'), '4,5')
    await usuario.click(screen.getByRole('button', { name: 'Guardar semana' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    expect(apiPostMock).toHaveBeenCalledWith(RUTA_ACTUAL, {
      dias: [
        { idPersonal: 1, fecha: '2026-10-27', horas: 8 },
        { idPersonal: 1, fecha: '2026-10-28', horas: 8 },
        { idPersonal: 2, fecha: '2026-10-26', horas: 4.5 },
      ],
    })
    await toastDeExito(/Se guardó la semana: 3 jornadas confirmadas y 3 movimientos registrados/)
    expect(apiGetMock).toHaveBeenCalledTimes(2)
  })

  it('con nada editado y sin precargados el botón está inerte', async () => {
    mockearLecturas((d) => {
      const s = semanaDe(d ?? SEMANA_ACTUAL)
      s.filas[0].dias = s.filas[0].dias.map((x) => ({ ...x, precargado: false, horas: x.horas }))

      return Promise.resolve(s)
    })
    renderizar()
    await esperarSemana()

    expect(screen.getByRole('button', { name: 'Guardar semana' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Descartar cambios' })).toBeDisabled()
  })

  it('un 400 del servidor muestra su mensaje y conserva lo tipeado', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockRejectedValue(new ErrorApi(400, 'horas_del_dia_excedidas', 'x'))
    renderizar()
    await esperarSemana()

    await usuario.type(celda('Beto Ruiz', 'Lun 26/10'), '5')
    await usuario.click(screen.getByRole('button', { name: 'Guardar semana' }))

    expect(await screen.findByText(/no puede superar las 24 horas en un día/)).toBeInTheDocument()
    expect(celda('Beto Ruiz', 'Lun 26/10')).toHaveValue('5')
    expect(apiGetMock).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('button', { name: 'Guardar semana' })).toBeEnabled()
  })

  it('un fallo sin respuesta del servidor relee la semana y avisa que no se pudo confirmar', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockRejectedValue(new Error('Failed to fetch'))
    renderizar()
    await esperarSemana()

    await usuario.type(celda('Beto Ruiz', 'Lun 26/10'), '5')
    await usuario.click(screen.getByRole('button', { name: 'Guardar semana' }))

    expect(await screen.findByText(/No se pudo confirmar si se guardó la semana/)).toBeInTheDocument()
    await waitFor(() => expect(apiGetMock).toHaveBeenCalledTimes(2))
    expect(celda('Beto Ruiz', 'Lun 26/10')).toHaveValue('')
  })

  it('un éxito cuya relectura falla avisa con un toast y deja un banner de que la vista no se actualizó', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockResolvedValue({ movimientosCreados: 2, jornadasConfirmadas: 2 })
    renderizar()
    await esperarSemana()
    apiGetMock.mockRejectedValue(new ErrorApi(500, 'error', 'Cayó la lectura'))

    await usuario.click(screen.getByRole('button', { name: 'Guardar semana' }))

    await toastDeExito(/Se guardó la semana: 2 jornadas confirmadas y 2 movimientos registrados/)
    await bannerPersistente(/No se pudo actualizar la vista: hasta que la semana/)
  })

  describe('si la relectura posterior a guardar falla', () => {
    async function afirmarBloqueada() {
      expect(await screen.findByRole('button', { name: 'Reintentar' })).toBeEnabled()
      expect(screen.getByRole('button', { name: 'Guardar semana' })).toBeDisabled()
      expect(screen.getByRole('button', { name: 'Descartar cambios' })).toBeDisabled()
      expect(celda('Ana Gómez', 'Lun 26/10')).toBeDisabled()
      expect(celda('Beto Ruiz', 'Lun 26/10')).toBeDisabled()
    }

    async function afirmarRecuperada(usuario: ReturnType<typeof userEvent.setup>) {
      apiGetMock.mockImplementation(() => Promise.resolve(semanaDe(SEMANA_ACTUAL)))
      await usuario.click(screen.getByRole('button', { name: 'Reintentar' }))
      await waitFor(() => expect(celda('Beto Ruiz', 'Lun 26/10')).toBeEnabled())
      expect(celda('Beto Ruiz', 'Lun 26/10')).toHaveValue('')
      expect(screen.queryByRole('button', { name: 'Reintentar' })).not.toBeInTheDocument()
      await usuario.type(celda('Beto Ruiz', 'Lun 26/10'), '3')
      expect(screen.getByRole('button', { name: 'Guardar semana' })).toBeEnabled()
    }

    it('un guardado incierto (500) deja la pantalla bloqueada hasta releer', async () => {
      const usuario = userEvent.setup()
      apiPostMock.mockRejectedValue(new ErrorApi(500, 'error', 'Cayó'))
      renderizar()
      await esperarSemana()
      await usuario.type(celda('Beto Ruiz', 'Lun 26/10'), '5')
      apiGetMock.mockRejectedValue(new ErrorApi(500, 'error', 'Cayó la lectura'))

      await usuario.click(screen.getByRole('button', { name: 'Guardar semana' }))

      await bannerPersistente(/tampoco se pudo actualizar la vista/)
      await afirmarBloqueada()
      await afirmarRecuperada(usuario)
    })

    it('un éxito deja la pantalla bloqueada hasta releer', async () => {
      const usuario = userEvent.setup()
      apiPostMock.mockResolvedValue({ movimientosCreados: 1, jornadasConfirmadas: 1 })
      renderizar()
      await esperarSemana()
      await usuario.type(celda('Beto Ruiz', 'Lun 26/10'), '5')
      apiGetMock.mockRejectedValue(new ErrorApi(500, 'error', 'Cayó la lectura'))

      await usuario.click(screen.getByRole('button', { name: 'Guardar semana' }))

      await toastDeExito(/Se guardó la semana: 1 jornada confirmada y 1 movimiento registrado/)
      await bannerPersistente(/No se pudo actualizar la vista: hasta que la semana/)
      await afirmarBloqueada()
      await afirmarRecuperada(usuario)
    })
  })

  it('dos clics sincrónicos guardan una sola vez', async () => {
    const usuario = userEvent.setup()
    const respuesta = diferido<{ movimientosCreados: number; jornadasConfirmadas: number }>()
    apiPostMock.mockReturnValue(respuesta.promesa)
    renderizar()
    await esperarSemana()
    await usuario.type(celda('Beto Ruiz', 'Lun 26/10'), '5')

    const boton = screen.getByRole('button', { name: 'Guardar semana' })
    act(() => {
      boton.click()
      boton.click()
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(celda('Beto Ruiz', 'Lun 26/10')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Semana siguiente »' })).toBeDisabled()

    await act(async () => respuesta.resolver({ movimientosCreados: 1, jornadasConfirmadas: 1 }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Guardar semana' })).toBeInTheDocument())
  })
})

describe('CargaSemanalDeHoras — validación', () => {
  it('más de 24 horas marca la celda y bloquea guardar', async () => {
    const usuario = userEvent.setup()
    renderizar()
    await esperarSemana()

    await usuario.type(celda('Beto Ruiz', 'Lun 26/10'), '25')

    expect(celda('Beto Ruiz', 'Lun 26/10')).toBeInvalid()
    expect(screen.getByText(/Beto Ruiz, Lun 26\/10: Un día no puede superar las 24 horas/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Guardar semana' })).toBeDisabled()
  })
})

describe('CargaSemanalDeHoras — semanas', () => {
  it('Descartar cambios vuelve a lo que trajo el servidor', async () => {
    const usuario = userEvent.setup()
    renderizar()
    await esperarSemana()

    await usuario.type(celda('Beto Ruiz', 'Lun 26/10'), '5')
    await usuario.click(screen.getByRole('button', { name: 'Descartar cambios' }))

    expect(celda('Beto Ruiz', 'Lun 26/10')).toHaveValue('')
  })

  it('pide la semana siguiente desde la que se ve y "Hoy" vuelve a pedir la actual', async () => {
    const usuario = userEvent.setup()
    renderizar()
    await esperarSemana()

    await usuario.click(screen.getByRole('button', { name: 'Semana siguiente »' }))
    expect(await screen.findByText('02/11 – 08/11')).toBeInTheDocument()
    expect(apiGetMock).toHaveBeenLastCalledWith(`${RUTA_ACTUAL}?desde=2026-11-02`)

    await usuario.click(screen.getByRole('button', { name: 'Hoy' }))
    expect(await screen.findByText('26/10 – 01/11')).toBeInTheDocument()
    expect(apiGetMock).toHaveBeenLastCalledWith(RUTA_ACTUAL)
  })

  it('con horas sin guardar pregunta antes de cambiar de semana; cancelar no pide nada', async () => {
    const usuario = userEvent.setup()
    renderizar()
    await esperarSemana()
    await usuario.type(celda('Beto Ruiz', 'Lun 26/10'), '5')

    await usuario.click(screen.getByRole('button', { name: '« Semana anterior' }))
    const dialogo = await screen.findByRole('alertdialog')
    await usuario.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))

    expect(apiGetMock).toHaveBeenCalledTimes(1)
    expect(celda('Beto Ruiz', 'Lun 26/10')).toHaveValue('5')
  })

  it('al confirmar el descarte pide la otra semana y borra el rechazo anterior', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockRejectedValue(new ErrorApi(400, 'fecha_futura', 'x'))
    renderizar()
    await esperarSemana()
    await usuario.type(celda('Beto Ruiz', 'Lun 26/10'), '5')
    await usuario.click(screen.getByRole('button', { name: 'Guardar semana' }))
    expect(await screen.findByText('No se pueden cargar horas de días futuros.')).toBeInTheDocument()

    const pendiente = diferido<SemanaDeCargaDeHoras>()
    mockearLecturas(() => pendiente.promesa)
    await usuario.click(screen.getByRole('button', { name: '« Semana anterior' }))
    await usuario.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Descartar cambios' }))

    expect(screen.queryByText('No se pueden cargar horas de días futuros.')).not.toBeInTheDocument()
    expect(apiGetMock).toHaveBeenLastCalledWith(`${RUTA_ACTUAL}?desde=2026-10-19`)
    await act(async () => pendiente.resolver(semanaDe('2026-10-19')))
    expect(await screen.findByText('19/10 – 25/10')).toBeInTheDocument()
  })

  it('cambios de semana rápidos: una respuesta tardía de una semana anterior se descarta', async () => {
    const usuario = userEvent.setup()
    renderizar()
    await esperarSemana()

    const siguiente = diferido<SemanaDeCargaDeHoras>()
    const vuelta = diferido<SemanaDeCargaDeHoras>()
    const pedidos = [siguiente, vuelta]
    mockearLecturas(() => pedidos.shift()!.promesa)

    await usuario.click(screen.getByRole('button', { name: 'Semana siguiente »' }))
    await usuario.click(screen.getByRole('button', { name: '« Semana anterior' }))
    expect(apiGetMock).toHaveBeenLastCalledWith(`${RUTA_ACTUAL}?desde=2026-10-26`)

    await act(async () => vuelta.resolver(semanaDe('2026-10-26')))
    await act(async () => siguiente.resolver(semanaDe('2026-11-02')))

    expect(screen.getByText('26/10 – 01/11')).toBeInTheDocument()
    expect(screen.queryByText('02/11 – 08/11')).not.toBeInTheDocument()
  })

  it('el enlace al empleado con horas sin guardar pregunta antes de salir', async () => {
    const usuario = userEvent.setup()
    renderizar()
    await esperarSemana()
    await usuario.type(celda('Beto Ruiz', 'Lun 26/10'), '5')

    await usuario.click(screen.getByRole('link', { name: 'Ana Gómez' }))
    expect(screen.getByTestId('ruta')).toHaveTextContent('/personal/horas')
    await usuario.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Descartar cambios' }))

    expect(screen.getByTestId('ruta')).toHaveTextContent('/personal/1/cuenta')
  })

  it('sin cambios el enlace al empleado navega directo', async () => {
    const usuario = userEvent.setup()
    renderizar()
    await esperarSemana()

    await usuario.click(screen.getByRole('link', { name: 'Beto Ruiz' }))

    expect(screen.getByTestId('ruta')).toHaveTextContent('/personal/2/cuenta')
  })
})
