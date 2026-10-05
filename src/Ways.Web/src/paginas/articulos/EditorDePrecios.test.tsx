import type { ComponentProps } from 'react'
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ErrorApi } from '../../api/cliente'
import type { HistorialDePrecio, ListaPrecioListado, PrecioVigente } from '../../api/tipos'
import { EditorDePrecios } from './EditorDePrecios'
import type { FamiliaDelArticulo } from './familia'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()

vi.mock('../../api/cliente', () => ({
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

const listaGeneral: ListaPrecioListado = {
  id: 2,
  nombre: 'General',
  activo: true,
  idEmpresa: null,
  esDefault: true,
  modo: 'Fija',
  idListaBase: null,
  porcentaje: null,
}

const sabores: FamiliaDelArticulo = { id: 7, nombre: 'Sabores', cantidad: 3 }

const PREGUNTA_SABORES = 'Este artículo es parte de la familia "Sabores" (3 artículos). ¿Aplicar el cambio a toda la familia?'

let vigentes: PrecioVigente[]
let historial: HistorialDePrecio[]

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  vigentes = [{ idArticulo: 31, idListaPrecio: 2, precio: 1000, fecha: '2026-10-01T00:00:00Z' }]
  historial = []
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/articulos/31/precios') return Promise.resolve(vigentes)
    if (ruta === '/articulos/31/precios/2/historial') return Promise.resolve(historial)
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
  apiPostMock.mockResolvedValue({ idArticulo: 31, idListaPrecio: 2, precio: 1500, fecha: '2026-10-05T00:00:00Z' })
})

function montar(props: Partial<ComponentProps<typeof EditorDePrecios>> = {}) {
  const alDeEscribir = vi.fn()
  const alSalirDeLaFamilia = vi.fn()
  const alCambiarLaFamilia = vi.fn()
  const resultado = render(
    <EditorDePrecios
      idArticulo={31}
      listasPrecio={[listaGeneral]}
      bloqueadoPorPadre={false}
      alDeEscribir={alDeEscribir}
      familia={sabores}
      alSalirDeLaFamilia={alSalirDeLaFamilia}
      alCambiarLaFamilia={alCambiarLaFamilia}
      {...props}
    />,
  )
  return { ...resultado, alDeEscribir, alSalirDeLaFamilia, alCambiarLaFamilia }
}

/** Espera la carga inicial de los precios vigentes (el editor muestra "Cargando precios…" hasta que llegan) y
 * abre el panel de la lista fija. */
async function abrirPanelDeLaLista() {
  const gestionar = await screen.findByRole('button', { name: 'Gestionar' })
  await userEvent.click(gestionar)
  await screen.findByRole('button', { name: 'Establecer ahora' })
}

async function escribirPrecio(texto: string) {
  await userEvent.type(screen.getByLabelText('Precio'), texto)
}

function programar(valor: string) {
  fireEvent.click(screen.getByLabelText('Programar a futuro'))
  fireEvent.change(screen.getByLabelText('Vigente desde'), { target: { value: valor } })
}

/** Una promesa que el test resuelve cuando quiere. */
function diferida<T>() {
  let resolver!: (valor: T) => void
  const promesa = new Promise<T>((resolve) => {
    resolver = resolve
  })
  return { promesa, resolver }
}

/** El POST del precio y el refresco que lo sigue (la segunda lectura de los precios vigentes) quedan en manos del test:
 * así se puede mirar el panel en cada una de las dos ventanas. */
function escrituraYRefrescoDiferidos() {
  const escritura = diferida<unknown>()
  const refresco = diferida<PrecioVigente[]>()
  let lecturasDeVigentes = 0
  apiPostMock.mockImplementation(() => escritura.promesa)
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/articulos/31/precios') return ++lecturasDeVigentes === 1 ? Promise.resolve(vigentes) : refresco.promesa
    if (ruta === '/articulos/31/precios/2/historial') return Promise.resolve(historial)
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
  return {
    terminarLaEscritura: () =>
      act(async () => {
        escritura.resolver({ idArticulo: 31, idListaPrecio: 2, precio: 1500, fecha: '2026-10-05T00:00:00Z' })
      }),
    terminarElRefresco: () =>
      act(async () => {
        refresco.resolver(vigentes)
      }),
  }
}

describe('EditorDePrecios — un artículo sin familia escribe como siempre', () => {
  it('"Establecer ahora" manda el precio directo, sin preguntar nada y sin alcance en el cuerpo', async () => {
    montar({ familia: null })
    await abrirPanelDeLaLista()

    await escribirPrecio('1500')
    await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    expect(apiPostMock).toHaveBeenCalledWith('/articulos/31/precios', { idListaPrecio: 2, precio: 1500, confirmarReemplazo: false })
    expect(screen.queryByRole('group', { name: 'Alcance del precio' })).not.toBeInTheDocument()
  })

  it('"Programar" manda el precio y la fecha directo, sin alcance en el cuerpo', async () => {
    montar({ familia: null })
    await abrirPanelDeLaLista()

    await escribirPrecio('1500')
    programar('2026-12-01T10:00')
    await userEvent.click(screen.getByRole('button', { name: 'Programar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    expect(apiPostMock).toHaveBeenCalledWith('/articulos/31/precios/programados', {
      idListaPrecio: 2,
      precio: 1500,
      vigenteDesde: new Date('2026-12-01T10:00').toISOString(),
      confirmarReemplazo: false,
    })
  })

  it('no muestra la nota de familia', async () => {
    montar({ familia: null })
    await screen.findByRole('button', { name: 'Gestionar' })

    expect(screen.queryByText(/Este artículo es parte/)).not.toBeInTheDocument()
  })
})

describe('EditorDePrecios — un miembro de una familia pregunta el alcance antes de escribir', () => {
  it('la nota dice que se va a preguntar, con el nombre y la cantidad de la familia', async () => {
    montar()

    expect(await screen.findByText(/Este artículo es parte de la familia "Sabores" \(3 artículos\): al cambiar un precio se pregunta/)).toBeInTheDocument()
  })

  it('"Establecer ahora" no escribe: abre la pregunta con la familia, y las tres respuestas', async () => {
    montar()
    await abrirPanelDeLaLista()

    await escribirPrecio('1500')
    await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))

    const pregunta = screen.getByRole('group', { name: 'Alcance del precio' })
    expect(pregunta).toHaveTextContent(PREGUNTA_SABORES)
    expect(pregunta).not.toHaveTextContent('Campos compartidos que cambian')
    expect(pregunta).not.toHaveTextContent('Los campos propios')
    expect(within(pregunta).getByRole('button', { name: 'Toda la familia' })).toBeEnabled()
    expect(within(pregunta).getByRole('button', { name: 'Solo este artículo (sale de la familia)' })).toBeEnabled()
    expect(within(pregunta).getByRole('button', { name: 'Cancelar' })).toBeEnabled()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('con un nombre que no se conoce pregunta igual, sin nombrar la familia', async () => {
    montar({ familia: { id: 7, nombre: null, cantidad: null } })
    await abrirPanelDeLaLista()

    await escribirPrecio('1500')
    await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))

    expect(screen.getByRole('group', { name: 'Alcance del precio' })).toHaveTextContent(
      'Este artículo es parte de una familia. ¿Aplicar el cambio a toda la familia?',
    )
  })

  it('"Cancelar" cierra la pregunta sin escribir nada y deja el borrador como estaba', async () => {
    montar()
    await abrirPanelDeLaLista()
    await escribirPrecio('1500')
    await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))

    await userEvent.click(within(screen.getByRole('group', { name: 'Alcance del precio' })).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('group', { name: 'Alcance del precio' })).not.toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
    expect(screen.getByLabelText('Precio')).toHaveValue('1.500,00')
    expect(screen.getByRole('button', { name: 'Establecer ahora' })).toBeEnabled()
  })

  it('"Toda la familia" escribe con alcance Familia, avisa que llegó a toda la familia y refresca el precio', async () => {
    const { alDeEscribir, alSalirDeLaFamilia } = montar()
    await abrirPanelDeLaLista()
    await escribirPrecio('1500')
    await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))

    vigentes = [{ idArticulo: 31, idListaPrecio: 2, precio: 1500, fecha: '2026-10-05T00:00:00Z' }]
    await userEvent.click(screen.getByRole('button', { name: 'Toda la familia' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    expect(apiPostMock).toHaveBeenCalledWith('/articulos/31/precios', {
      idListaPrecio: 2,
      precio: 1500,
      confirmarReemplazo: false,
      alcance: 'Familia',
    })
    expect(await screen.findByText('El precio se aplicó a toda la familia "Sabores".')).toBeInTheDocument()
    expect(await screen.findByText('$ 1.500,00')).toBeInTheDocument()
    expect(alSalirDeLaFamilia).not.toHaveBeenCalled()
    expect(alDeEscribir.mock.calls).toEqual([[true], [false]])
    expect(screen.queryByRole('group', { name: 'Alcance del precio' })).not.toBeInTheDocument()
  })

  it('"Solo este artículo" escribe con alcance SoloEste y le avisa al padre que el artículo salió de la familia', async () => {
    const { alSalirDeLaFamilia } = montar()
    await abrirPanelDeLaLista()
    await escribirPrecio('1500')
    await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))

    await userEvent.click(screen.getByRole('button', { name: 'Solo este artículo (sale de la familia)' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    expect(apiPostMock).toHaveBeenCalledWith('/articulos/31/precios', {
      idListaPrecio: 2,
      precio: 1500,
      confirmarReemplazo: false,
      alcance: 'SoloEste',
    })
    await waitFor(() => expect(alSalirDeLaFamilia).toHaveBeenCalledExactlyOnceWith('Sabores'))
  })

  it('"Programar" también pregunta, y escribe en la ruta de programados con el alcance elegido', async () => {
    montar()
    await abrirPanelDeLaLista()
    await escribirPrecio('1500')
    programar('2026-12-01T10:00')
    await userEvent.click(screen.getByRole('button', { name: 'Programar' }))
    expect(apiPostMock).not.toHaveBeenCalled()

    await userEvent.click(screen.getByRole('button', { name: 'Toda la familia' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    expect(apiPostMock).toHaveBeenCalledWith('/articulos/31/precios/programados', {
      idListaPrecio: 2,
      precio: 1500,
      vigenteDesde: new Date('2026-12-01T10:00').toISOString(),
      confirmarReemplazo: false,
      alcance: 'Familia',
    })
  })

  /** Cláusula bajo prueba: `estado.programado` en el aviso de `guardarPrecio`: al escribir un precio programado no se
   * aplicó ningún precio, así que el aviso no puede decir que se aplicó. Evidencia de mutación (mutation-proof-tests):
   * pasar `false` en vez de `estado.programado` a `mensajeDePrecioParaToda` hace fallar este test; revertido, vuelve a
   * verde. */
  it('"Programar" con "Toda la familia" avisa que el precio se programó para toda la familia, no que se aplicó', async () => {
    montar()
    await abrirPanelDeLaLista()
    await escribirPrecio('1500')
    programar('2026-12-01T10:00')
    await userEvent.click(screen.getByRole('button', { name: 'Programar' }))

    await userEvent.click(screen.getByRole('button', { name: 'Toda la familia' }))

    expect(await screen.findByText('El precio se programó para toda la familia "Sabores".')).toBeInTheDocument()
    expect(screen.queryByText(/se aplicó/)).not.toBeInTheDocument()
  })

  it('un precio inválido se rechaza ANTES de preguntar: no abre la pregunta ni escribe', async () => {
    montar()
    await abrirPanelDeLaLista()

    await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))

    expect(screen.getByText('Ingresá un precio válido.')).toBeInTheDocument()
    expect(screen.queryByRole('group', { name: 'Alcance del precio' })).not.toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('programar sin fecha se rechaza ANTES de preguntar, sin tocar el estado de escritura del padre', async () => {
    const { alDeEscribir } = montar()
    await abrirPanelDeLaLista()
    await escribirPrecio('1500')
    fireEvent.click(screen.getByLabelText('Programar a futuro'))

    await userEvent.click(screen.getByRole('button', { name: 'Programar' }))

    expect(screen.getByText('Elegí la fecha de vigencia.')).toBeInTheDocument()
    expect(screen.queryByRole('group', { name: 'Alcance del precio' })).not.toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
    expect(alDeEscribir).not.toHaveBeenCalled()
  })

  /** Cláusula bajo prueba: `estado.preguntaDeAlcance !== null` dentro de `bloqueado` de `PanelDeLista`: el borrador
   * sobre el que se pregunta no puede cambiar mientras se decide. Evidencia de mutación (mutation-proof-tests):
   * dejar `bloqueado = enVuelo` hace fallar este test; revertido, vuelve a verde. */
  it('con la pregunta abierta, el resto del panel queda inerte', async () => {
    montar()
    await abrirPanelDeLaLista()
    await escribirPrecio('1500')
    await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))

    expect(screen.getByLabelText('Precio')).toBeDisabled()
    expect(screen.getByLabelText('Programar a futuro')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Establecer ahora' })).toBeDisabled()
  })

  /** Cláusula bajo prueba: `refrescando: true` del estado que deja `guardarPrecio` al confirmarse el precio, y su
   * `bloqueado = enVuelo || …` en el panel: la ventana inerte cubre desde la respuesta hasta que el refresco posterior
   * terminó, no solo la escritura. La pregunta ya se cerró al responder, así que en esa ventana no hay respuestas que
   * alcanzar: lo inerte es el borrador y el botón. Evidencia de mutación (mutation-proof-tests): sacar `refrescando: true`
   * de ese estado hace fallar este test (con el refresco pendiente no hay botón "Actualizando…"); revertido, vuelve a
   * verde. */
  it('desde la respuesta hasta que el refresco termina, el panel queda inerte: primero con la escritura y después con el refresco', async () => {
    const { terminarLaEscritura, terminarElRefresco } = escrituraYRefrescoDiferidos()
    montar()
    await abrirPanelDeLaLista()
    await escribirPrecio('1500')
    await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))

    await userEvent.click(screen.getByRole('button', { name: 'Toda la familia' }))

    expect(screen.queryByRole('group', { name: 'Alcance del precio' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Guardando…' })).toBeDisabled()
    expect(screen.getByLabelText('Precio')).toBeDisabled()

    await terminarLaEscritura()

    expect(screen.getByRole('button', { name: 'Actualizando…' })).toBeDisabled()
    expect(screen.getByLabelText('Precio')).toBeDisabled()
    expect(screen.getByLabelText('Programar a futuro')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cerrar' })).toBeDisabled()

    await terminarElRefresco()

    await waitFor(() => expect(screen.getByRole('button', { name: 'Establecer ahora' })).toBeEnabled())
    expect(screen.getByLabelText('Precio')).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Cerrar' })).toBeEnabled()
  })

  /** Cláusula bajo prueba: `ocupado={enVuelo}` de la pregunta. Evidencia de mutación (mutation-proof-tests):
   * reemplazarlo por `ocupado={false}` hace fallar este test; revertido, vuelve a verde. */
  it('con el padre ocupado, las respuestas de la pregunta ya abierta quedan inertes', async () => {
    const { rerender, alDeEscribir, alSalirDeLaFamilia, alCambiarLaFamilia } = montar()
    await abrirPanelDeLaLista()
    await escribirPrecio('1500')
    await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))

    rerender(
      <EditorDePrecios
        idArticulo={31}
        listasPrecio={[listaGeneral]}
        bloqueadoPorPadre
        alDeEscribir={alDeEscribir}
        familia={sabores}
        alSalirDeLaFamilia={alSalirDeLaFamilia}
        alCambiarLaFamilia={alCambiarLaFamilia}
      />,
    )

    const pregunta = screen.getByRole('group', { name: 'Alcance del precio' })
    for (const boton of within(pregunta).getAllByRole('button')) expect(boton).toBeDisabled()
  })

  /** Cláusula bajo prueba: `escribiendoRef`, el espejo sincrónico de `guardarPrecio` (react-async-state regla 11):
   * dos clics en la misma respuesta, antes de que el re-render la deshabilite, emiten una sola request. La guarda
   * de estado (`estado.guardando`) sola no alcanza: este test fallaba con ella. Evidencia de mutación
   * (mutation-proof-tests): sacar `if (escribiendoRef.current) return` hace fallar este test; revertido, vuelve
   * a verde. */
  it('dos clics sincrónicos en "Toda la familia" escriben una sola vez', async () => {
    apiPostMock.mockImplementation(() => new Promise(() => {}))
    montar()
    await abrirPanelDeLaLista()
    await escribirPrecio('1500')
    await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))

    const toda = screen.getByRole('button', { name: 'Toda la familia' })
    await act(async () => {
      toda.click()
      toda.click()
      await Promise.resolve()
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })
})

describe('EditorDePrecios — el reemplazo de un precio programado', () => {
  it('tras elegir "Toda la familia", Reemplazar reenvía con confirmarReemplazo y el MISMO alcance, sin volver a preguntar', async () => {
    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'precio_pendiente_existe', 'Ya hay un precio pendiente.'))
    montar()
    await abrirPanelDeLaLista()
    await escribirPrecio('1500')
    await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))
    await userEvent.click(screen.getByRole('button', { name: 'Toda la familia' }))

    expect(
      await screen.findByText('Ya existe un precio programado para esta lista en algún artículo de la familia. ¿Confirmás el reemplazo?'),
    ).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Reemplazar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(2))
    expect(apiPostMock.mock.calls[1]).toEqual([
      '/articulos/31/precios',
      { idListaPrecio: 2, precio: 1500, confirmarReemplazo: true, alcance: 'Familia' },
    ])
    expect(screen.queryByRole('group', { name: 'Alcance del precio' })).not.toBeInTheDocument()
  })

  it('con "Solo este artículo" el aviso del reemplazo es el de siempre: el pendiente es de este artículo', async () => {
    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'precio_pendiente_existe', 'Ya hay un precio pendiente.'))
    montar()
    await abrirPanelDeLaLista()
    await escribirPrecio('1500')
    await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))
    await userEvent.click(screen.getByRole('button', { name: 'Solo este artículo (sale de la familia)' }))

    expect(await screen.findByText('Ya existe un precio programado para esta lista. ¿Confirmás el reemplazo?')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Reemplazar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(2))
    expect(apiPostMock.mock.calls[1][1]).toMatchObject({ confirmarReemplazo: true, alcance: 'SoloEste' })
  })

  it('cancelar el reemplazo y empezar de nuevo: el próximo intento vuelve a preguntar el alcance', async () => {
    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'precio_pendiente_existe', 'Ya hay un precio pendiente.'))
    montar()
    await abrirPanelDeLaLista()
    await escribirPrecio('1500')
    await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))
    await userEvent.click(screen.getByRole('button', { name: 'Toda la familia' }))
    await screen.findByRole('button', { name: 'Reemplazar' })

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))
    await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))

    expect(screen.getByRole('group', { name: 'Alcance del precio' })).toBeInTheDocument()
    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })
})

describe('EditorDePrecios — los rechazos del servidor sobre la familia', () => {
  /** El editor no sabía que el artículo es miembro (`familia: null`): el servidor lo frena y la pregunta que no
   * se hizo antes se hace ahora, con el texto del servidor. */
  it('409 alcance_requerido: abre la pregunta con el texto del servidor y, al elegir, escribe con ese alcance', async () => {
    const mensaje =
      'El artículo pertenece a la familia "Sabores" (3 artículos): el cambio de precio tiene que indicar si se aplica a toda la familia o solo a este artículo.'
    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'alcance_requerido', mensaje))
    montar({ familia: null })
    await abrirPanelDeLaLista()
    await escribirPrecio('1500')
    await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))

    const pregunta = await screen.findByRole('group', { name: 'Alcance del precio' })
    expect(pregunta).toHaveTextContent(`${mensaje} ¿Aplicar el cambio a toda la familia?`)
    expect(apiPostMock.mock.calls[0][1]).not.toHaveProperty('alcance')
    expect(screen.queryByText(mensaje, { selector: '.alert-danger' })).not.toBeInTheDocument()

    await userEvent.click(within(pregunta).getByRole('button', { name: 'Toda la familia' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(2))
    expect(apiPostMock.mock.calls[1][1]).toMatchObject({ alcance: 'Familia' })
  })

  it('409 familia_cambio: le pide al padre que recargue el artículo y no deja el panel trabado', async () => {
    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'familia_cambio', 'La pertenencia cambió.'))
    const { alCambiarLaFamilia, alDeEscribir } = montar()
    await abrirPanelDeLaLista()
    await escribirPrecio('1500')
    await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))
    await userEvent.click(screen.getByRole('button', { name: 'Toda la familia' }))

    await waitFor(() => expect(alCambiarLaFamilia).toHaveBeenCalledTimes(1))
    expect(alDeEscribir.mock.calls).toEqual([[true], [false]])
    expect(screen.getByRole('button', { name: 'Establecer ahora' })).toBeEnabled()
    expect(screen.queryByText('La pertenencia cambió.')).not.toBeInTheDocument()
  })

  it('cualquier otro rechazo se muestra en el panel, cierra la pregunta y deja volver a intentar', async () => {
    apiPostMock.mockRejectedValueOnce(new ErrorApi(422, 'precio_invalido', 'El precio no es válido.'))
    const { alSalirDeLaFamilia } = montar()
    await abrirPanelDeLaLista()
    await escribirPrecio('1500')
    await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))
    await userEvent.click(screen.getByRole('button', { name: 'Solo este artículo (sale de la familia)' }))

    expect(await screen.findByText('El precio no es válido.')).toBeInTheDocument()
    expect(screen.queryByRole('group', { name: 'Alcance del precio' })).not.toBeInTheDocument()
    expect(alSalirDeLaFamilia).not.toHaveBeenCalled()
    expect(screen.getByRole('button', { name: 'Establecer ahora' })).toBeEnabled()
  })
})

describe('EditorDePrecios — el foco de la pregunta de alcance', () => {
  /** Advertencia (react-async-state regla 12): jsdom no implementa la regla de "focus fixup" del navegador (deshabilitar
   * el elemento enfocado no lo desenfoca), así que estas pruebas ejercitan el movimiento explícito del foco, no el
   * comportamiento de un navegador real. */
  async function abrirLaPregunta() {
    await abrirPanelDeLaLista()
    await escribirPrecio('1500')
    const disparador = screen.getByRole('button', { name: 'Establecer ahora' })
    await userEvent.click(disparador)
    return { disparador, pregunta: screen.getByRole('group', { name: 'Alcance del precio' }) }
  }

  /** Cláusula bajo prueba: el efecto de `PanelDeLista` que enfoca "Cancelar" cuando la pregunta se abre. Evidencia de
   * mutación (mutation-proof-tests): sacar ese efecto hace fallar este test; revertido, vuelve a verde. */
  it('al abrirse la pregunta el foco va a "Cancelar", la respuesta que no escribe nada', async () => {
    montar()

    const { pregunta } = await abrirLaPregunta()

    expect(within(pregunta).getByRole('button', { name: 'Cancelar' })).toHaveFocus()
  })

  /** Cláusula bajo prueba: el efecto de `PanelDeLista` que devuelve el foco al botón que abrió la pregunta. Evidencia de
   * mutación (mutation-proof-tests): sacar la llamada a `disparadorRef.current?.focus()` hace fallar este test y el
   * siguiente; revertido, vuelven a verde. */
  it('"Cancelar" devuelve el foco al botón que abrió la pregunta', async () => {
    montar()
    const { disparador, pregunta } = await abrirLaPregunta()

    await userEvent.click(within(pregunta).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('group', { name: 'Alcance del precio' })).not.toBeInTheDocument()
    expect(disparador).toBeEnabled()
    expect(disparador).toHaveFocus()
  })

  /** Cláusula bajo prueba: `!enVuelo` del efecto que devuelve el foco. Al responder, el botón sigue deshabilitado (no
   * recibe el foco) mientras duran la escritura y su refresco: el foco vuelve recién cuando terminan. Evidencia de
   * mutación (mutation-proof-tests): sacar `&& !enVuelo` hace fallar este test (el foco se intenta en el botón todavía
   * deshabilitado y no se vuelve a intentar); revertido, vuelve a verde. */
  it('tras una respuesta el foco vuelve al botón recién cuando terminaron la escritura y su refresco', async () => {
    const { terminarLaEscritura, terminarElRefresco } = escrituraYRefrescoDiferidos()
    montar()
    const { pregunta } = await abrirLaPregunta()

    await userEvent.click(within(pregunta).getByRole('button', { name: 'Toda la familia' }))
    await terminarLaEscritura()
    expect(screen.getByRole('button', { name: 'Actualizando…' })).not.toHaveFocus()
    await terminarElRefresco()

    await waitFor(() => expect(screen.getByRole('button', { name: 'Establecer ahora' })).toHaveFocus())
  })
})

describe('EditorDePrecios — cada acción que empieza borra lo que dijo la anterior', () => {
  /** Cláusula bajo prueba: `setAviso('')` de `iniciarGuardado`. Abrir la pregunta no pasa por `guardarPrecio` (que ya
   * limpia el aviso), así que esa línea es la única que lo saca. Evidencia de mutación (mutation-proof-tests): sacarla
   * hace fallar este test (el aviso del precio anterior sigue en pantalla con la pregunta abierta); revertido, vuelve a
   * verde. */
  it('abrir la pregunta saca de pantalla el aviso del precio anterior para toda la familia', async () => {
    montar()
    await abrirPanelDeLaLista()
    await escribirPrecio('1500')
    await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))
    await userEvent.click(screen.getByRole('button', { name: 'Toda la familia' }))
    await screen.findByText('El precio se aplicó a toda la familia "Sabores".')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Establecer ahora' })).toBeEnabled())

    await escribirPrecio('1600')
    await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))

    expect(screen.getByRole('group', { name: 'Alcance del precio' })).toBeInTheDocument()
    expect(screen.queryByText('El precio se aplicó a toda la familia "Sabores".')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `error: ''` del estado con el que `iniciarGuardado` abre la pregunta. Evidencia de mutación
   * (mutation-proof-tests): sacarlo hace fallar este test (el rechazo anterior sigue en pantalla con la pregunta
   * abierta); revertido, vuelve a verde. */
  it('abrir la pregunta saca de pantalla el rechazo de la acción anterior', async () => {
    apiPostMock.mockRejectedValueOnce(new ErrorApi(422, 'precio_invalido', 'El precio no es válido.'))
    montar()
    await abrirPanelDeLaLista()
    await escribirPrecio('1500')
    await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))
    await userEvent.click(screen.getByRole('button', { name: 'Solo este artículo (sale de la familia)' }))
    await screen.findByText('El precio no es válido.')

    await userEvent.click(screen.getByRole('button', { name: 'Establecer ahora' }))

    expect(screen.getByRole('group', { name: 'Alcance del precio' })).toBeInTheDocument()
    expect(screen.queryByText('El precio no es válido.')).not.toBeInTheDocument()
  })
})
