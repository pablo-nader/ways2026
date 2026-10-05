import { StrictMode } from 'react'
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Familias } from './Familias'
import { CAMPOS_PROPIOS } from './articulos/familia'
import { ErrorApi } from '../api/cliente'
import type { FamiliaListado } from '../api/tipos'

const apiGetMock = vi.fn()
const apiPutMock = vi.fn()
const apiDeleteMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: vi.fn(),
    put: (...args: unknown[]) => apiPutMock(...(args as [string, unknown])),
    delete: (...args: unknown[]) => apiDeleteMock(...(args as [string])),
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

const sabores: FamiliaListado = { id: 7, nombre: 'Sabores', activo: true, cantidadArticulos: 3 }
const talles: FamiliaListado = { id: 8, nombre: 'Talles', activo: false, cantidadArticulos: 1 }

function diferida<T>() {
  let resolver!: (valor: T) => void
  let rechazar!: (motivo: unknown) => void
  const promesa = new Promise<T>((resolve, reject) => {
    resolver = resolve
    rechazar = reject
  })
  return { promesa, resolver, rechazar }
}

function montar(items: FamiliaListado[] = [sabores, talles]) {
  apiGetMock.mockImplementation((ruta: string) =>
    ruta === '/familias' ? Promise.resolve(items) : Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`)),
  )
  return render(
    <MemoryRouter initialEntries={['/familias']}>
      <Routes>
        <Route path="/familias" element={<Familias />} />
        <Route path="/familias/nueva" element={<p>Pantalla de nueva familia</p>} />
        <Route path="/familias/:id" element={<p>Pantalla de detalle de la familia</p>} />
      </Routes>
    </MemoryRouter>,
  )
}

/** Espera a que el listado esté cargado: el dato, no el título de la pantalla que se rinde antes. */
async function montarYEsperar(items?: FamiliaListado[]) {
  montar(items)
  await screen.findByText((items ?? [sabores, talles])[0]?.nombre ?? 'Todavía no hay familias.')
}

function fila(nombre: string) {
  return screen.getByRole('row', { name: new RegExp(nombre) })
}

function celdas(nombre: string) {
  return within(fila(nombre))
    .getAllByRole('cell')
    .map((c) => c.textContent)
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPutMock.mockReset()
  apiDeleteMock.mockReset()
  apiPutMock.mockResolvedValue(sabores)
  apiDeleteMock.mockResolvedValue(undefined)
})

describe('Familias — listado', () => {
  it('rinde nombre, cantidad de artículos y estado de cada familia', async () => {
    await montarYEsperar()

    expect(apiGetMock).toHaveBeenCalledWith('/familias')
    expect(screen.getAllByRole('columnheader').map((c) => c.textContent)).toEqual(['Nombre', 'Artículos', 'Estado', 'Acciones'])
    expect(celdas('Sabores').slice(0, 3)).toEqual(['Sabores', '3', 'Activa'])
    expect(celdas('Talles').slice(0, 3)).toEqual(['Talles', '1', 'Inactiva'])
  })

  it('sin familias lo dice', async () => {
    montar([])

    expect(await screen.findByText('Todavía no hay familias.')).toBeInTheDocument()
  })

  it('un fallo de carga rinde el mensaje del servidor', async () => {
    apiGetMock.mockRejectedValue(new ErrorApi(500, 'error_interno', 'Se cayó el listado.'))
    render(
      <MemoryRouter>
        <Familias />
      </MemoryRouter>,
    )

    expect(await screen.findByText('Se cayó el listado.')).toBeInTheDocument()
  })

  it('un fallo que no es del servidor rinde el mensaje genérico', async () => {
    apiGetMock.mockRejectedValue(new TypeError('Failed to fetch'))
    render(
      <MemoryRouter>
        <Familias />
      </MemoryRouter>,
    )

    expect(await screen.findByText('No se pudieron cargar las familias.')).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `errorDeCarga === ''` en la fila vacía de la tabla. Un listado que no se pudo cargar no está
   * vacío: decir "Todavía no hay familias." contradice el error que está al lado. Evidencia de mutación
   * (mutation-proof-tests): sacar esa condición hace fallar las dos; revertido, vuelven a verde. */
  it.each<[string, () => unknown, string]>([
    ['un rechazo del servidor', () => new ErrorApi(500, 'error_interno', 'Se cayó el listado.'), 'Se cayó el listado.'],
    ['la red caída', () => new TypeError('Failed to fetch'), 'No se pudieron cargar las familias.'],
  ])('con %s no dice que todavía no hay familias', async (_caso, error, mensaje) => {
    apiGetMock.mockRejectedValue(error())
    render(
      <MemoryRouter>
        <Familias />
      </MemoryRouter>,
    )

    expect(await screen.findByText(mensaje)).toBeInTheDocument()

    expect(screen.queryByText('Todavía no hay familias.')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: la generación de `cargar` (react-async-state regla 2). Bajo StrictMode el efecto de carga corre
   * dos veces: la respuesta tardía de la primera lectura no pisa a la de la segunda. Evidencia de mutación
   * (mutation-proof-tests): sacar el `if (generacion.current !== token) return` de la rama de éxito hace fallar este
   * test; revertido, vuelve a verde. */
  it('bajo StrictMode, la respuesta tardía de la primera lectura no pisa a la de la segunda', async () => {
    const primera = diferida<FamiliaListado[]>()
    apiGetMock.mockReturnValueOnce(primera.promesa).mockResolvedValue([talles])
    render(
      <StrictMode>
        <MemoryRouter>
          <Familias />
        </MemoryRouter>
      </StrictMode>,
    )
    await screen.findByText('Talles')

    await act(async () => {
      primera.resolver([sabores])
    })

    expect(screen.getByText('Talles')).toBeInTheDocument()
    expect(screen.queryByText('Sabores')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: la generación en la rama de fallo de `cargar`. Los tres brazos de la misma compuerta (éxito,
   * fallo y `finally`) llevan cada uno su prueba (mutation-proof-tests regla 15): el fallo de la lectura superada no
   * se muestra mientras la vigente sigue en vuelo. */
  it('bajo StrictMode, el fallo de la primera lectura no se muestra mientras la segunda sigue en vuelo', async () => {
    const segunda = diferida<FamiliaListado[]>()
    apiGetMock.mockRejectedValueOnce(new ErrorApi(500, 'error_interno', 'Se cayó la primera.')).mockReturnValueOnce(segunda.promesa)
    render(
      <StrictMode>
        <MemoryRouter>
          <Familias />
        </MemoryRouter>
      </StrictMode>,
    )
    await act(async () => {})

    expect(screen.queryByText('Se cayó la primera.')).not.toBeInTheDocument()
    await act(async () => {
      segunda.resolver([talles])
    })
    expect(await screen.findByText('Talles')).toBeInTheDocument()
    expect(screen.queryByText('Se cayó la primera.')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: la generación en el `finally` de `cargar` (regla 4 de react-async-state). */
  it('bajo StrictMode, la primera lectura que termina antes no apaga "Cargando…" de la segunda', async () => {
    const segunda = diferida<FamiliaListado[]>()
    apiGetMock.mockResolvedValueOnce([sabores]).mockReturnValueOnce(segunda.promesa)
    render(
      <StrictMode>
        <MemoryRouter>
          <Familias />
        </MemoryRouter>
      </StrictMode>,
    )
    await act(async () => {})

    expect(screen.getByText('Cargando…')).toBeInTheDocument()
    expect(screen.queryByText('Todavía no hay familias.')).not.toBeInTheDocument()
    expect(screen.queryByText('Sabores')).not.toBeInTheDocument()
    await act(async () => {
      segunda.resolver([talles])
    })
    expect(await screen.findByText('Talles')).toBeInTheDocument()
  })

  it('explica qué comparte una familia y qué es propio de cada artículo', async () => {
    await montarYEsperar()

    expect(screen.getByText(new RegExp(`Son propios de cada artículo: ${CAMPOS_PROPIOS}`))).toBeInTheDocument()
    expect(screen.getByText(/en el precio de cada lista de precios fija: lo que se cambia en uno se aplica a todos/)).toBeInTheDocument()
  })

  it('"Nueva familia" lleva a la pantalla de alta', async () => {
    await montarYEsperar()

    await userEvent.click(screen.getByRole('link', { name: 'Nueva familia' }))

    expect(await screen.findByText('Pantalla de nueva familia')).toBeInTheDocument()
  })

  it('"Ver detalle" lleva al detalle de esa familia', async () => {
    await montarYEsperar()

    const enlace = screen.getByRole('link', { name: 'Ver detalle de Sabores' })
    expect(enlace).toHaveAttribute('href', '/familias/7')
    await userEvent.click(enlace)

    expect(await screen.findByText('Pantalla de detalle de la familia')).toBeInTheDocument()
  })
})

describe('Familias — renombrar y activar o desactivar', () => {
  it('"Editar" abre el formulario con el nombre y el estado de la familia; "Cancelar" lo cierra sin escribir', async () => {
    await montarYEsperar()

    await userEvent.click(screen.getByRole('button', { name: 'Editar Talles' }))

    expect(screen.getByText('Editando familia 8')).toBeInTheDocument()
    expect(screen.getByLabelText('Nombre')).toHaveValue('Talles')
    expect(screen.getByLabelText('Nombre')).toHaveAttribute('maxlength', '150')
    expect(screen.getByLabelText('Estado')).toHaveValue('inactiva')
    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))
    expect(screen.queryByText('Editando familia 8')).not.toBeInTheDocument()
    expect(apiPutMock).not.toHaveBeenCalled()
  })

  it('avisa que una familia inactiva no admite artículos nuevos', async () => {
    await montarYEsperar()

    await userEvent.click(screen.getByRole('button', { name: 'Editar Sabores' }))

    expect(screen.getByText('Una familia inactiva no admite artículos nuevos.')).toBeInTheDocument()
  })

  it('guardar manda nombre (recortado) y estado, cierra el formulario, avisa y refresca el listado', async () => {
    let lecturas = 0
    apiGetMock.mockImplementation(() => Promise.resolve(++lecturas === 1 ? [sabores, talles] : [{ ...sabores, nombre: 'Sabores nuevos' }, talles]))
    render(
      <MemoryRouter>
        <Familias />
      </MemoryRouter>,
    )
    await screen.findByText('Sabores')
    await userEvent.click(screen.getByRole('button', { name: 'Editar Sabores' }))

    await userEvent.clear(screen.getByLabelText('Nombre'))
    await userEvent.type(screen.getByLabelText('Nombre'), '  Sabores nuevos  ')
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledExactlyOnceWith('/familias/7', { nombre: 'Sabores nuevos', activo: true }))
    expect(await screen.findByText('Se actualizó la familia "Sabores nuevos".')).toBeInTheDocument()
    expect(screen.queryByText('Editando familia 7')).not.toBeInTheDocument()
    await waitFor(() => expect(screen.getByText('Sabores nuevos')).toBeInTheDocument())
    expect(lecturas).toBe(2)
  })

  it('desactivar manda activo: false', async () => {
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Editar Sabores' }))

    await userEvent.selectOptions(screen.getByLabelText('Estado'), 'inactiva')
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledWith('/familias/7', { nombre: 'Sabores', activo: false }))
  })

  it('activar una inactiva manda activo: true', async () => {
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Editar Talles' }))

    await userEvent.selectOptions(screen.getByLabelText('Estado'), 'activa')
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledWith('/familias/8', { nombre: 'Talles', activo: true }))
  })

  it('un nombre repetido (409) se muestra con su ayuda, el formulario sigue abierto y se puede volver a guardar', async () => {
    apiPutMock.mockRejectedValueOnce(new ErrorApi(409, 'familia_nombre_duplicado', 'Ya existe una familia llamada "Talles" en este tenant.'))
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Editar Sabores' }))
    await userEvent.clear(screen.getByLabelText('Nombre'))
    await userEvent.type(screen.getByLabelText('Nombre'), 'Talles')

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('Ya existe una familia llamada "Talles" en este tenant. Elegí otro nombre.')).toBeInTheDocument()
    expect(screen.getByText('Editando familia 7')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled()
    expect(screen.queryByText(/Se actualizó/)).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `setError('')` de `cancelarEdicion`: el rechazo del guardado pertenece al formulario, y
   * cerrarlo lo saca de pantalla. Evidencia de mutación (mutation-proof-tests): sacar esa línea hace fallar este test;
   * revertido, vuelve a verde. */
  it('"Cancelar" después de un guardado rechazado saca el rechazo de pantalla', async () => {
    apiPutMock.mockRejectedValueOnce(new ErrorApi(409, 'familia_nombre_duplicado', 'Ya existe una familia llamada "Talles" en este tenant.'))
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Editar Sabores' }))
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    await screen.findByText(/Ya existe una familia llamada "Talles"/)

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByText('Editando familia 7')).not.toBeInTheDocument()
    expect(screen.queryByText(/Ya existe una familia llamada "Talles"/)).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: la rama `estado === 404` del `catch` de `guardar`. Un formulario abierto sobre una familia que ya
   * no existe ofrecería guardar de nuevo contra el mismo 404: se cierra y se vuelve a leer el listado. Evidencia de
   * mutación (mutation-proof-tests): sacar la rama hace fallar este test (el formulario sigue abierto y el listado, sin
   * releer); revertido, vuelve a verde. */
  it('un 404 al guardar (la familia ya no existe) rinde el rechazo, cierra el formulario y vuelve a leer el listado', async () => {
    let lecturas = 0
    apiGetMock.mockImplementation(() => Promise.resolve(++lecturas === 1 ? [sabores, talles] : [talles]))
    apiPutMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'No existe la familia 7.'))
    render(
      <MemoryRouter>
        <Familias />
      </MemoryRouter>,
    )
    await screen.findByText('Sabores')
    await userEvent.click(screen.getByRole('button', { name: 'Editar Sabores' }))

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('No existe la familia 7.')).toBeInTheDocument()
    await waitFor(() => expect(screen.queryByText('Sabores')).not.toBeInTheDocument())
    expect(screen.queryByText('Editando familia 7')).not.toBeInTheDocument()
    expect(lecturas).toBe(2)
    expect(screen.getByRole('button', { name: 'Editar Talles' })).toBeEnabled()
  })

  /** Cláusula bajo prueba: `mensajeDeFalloDeEscritura` en `guardar`: con un 5xx o la red caída no se sabe si el PUT llegó
   * a commitear. Evidencia de mutación (mutation-proof-tests): volver a `mensajeDeError` hace fallar estas dos;
   * revertido, vuelven a verde. */
  it.each<[string, () => unknown]>([
    ['un 5xx sin código estable', () => new ErrorApi(500, 'error_interno', 'Se cayó el servidor.')],
    ['la red caída', () => new TypeError('Failed to fetch')],
  ])('con %s no se sabe si se guardó: lo dice, sin el texto del servidor, y el formulario sigue abierto', async (_caso, error) => {
    apiPutMock.mockRejectedValue(error())
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Editar Sabores' }))

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(
      await screen.findByText('No se pudo guardar la familia. No se pudo confirmar el resultado: verificá el listado antes de reintentar.'),
    ).toBeInTheDocument()
    expect(screen.queryByText(/Se cayó el servidor/)).not.toBeInTheDocument()
    expect(screen.getByText('Editando familia 7')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled()
  })

  /** Cláusula bajo prueba: `bloqueado` deja inerte la pantalla entera mientras el PUT está en vuelo (react-async-state
   * regla 5) — el formulario, las acciones de cada fila y el enlace "Nueva familia". */
  it('con el PUT en vuelo, todo queda inerte: formulario, acciones de las filas y "Nueva familia"', async () => {
    const escritura = diferida<FamiliaListado>()
    apiPutMock.mockReturnValue(escritura.promesa)
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Editar Sabores' }))

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(screen.getByRole('button', { name: 'Guardando…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByLabelText('Nombre')).toBeDisabled()
    expect(screen.getByLabelText('Estado')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Editar Talles' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Disolver Talles' })).toBeDisabled()
    expect(screen.getByRole('link', { name: 'Nueva familia' })).toHaveAttribute('aria-disabled', 'true')
    await userEvent.click(screen.getByRole('link', { name: 'Nueva familia' }))
    expect(screen.queryByText('Pantalla de nueva familia')).not.toBeInTheDocument()

    await act(async () => {
      escritura.resolver(sabores)
    })
    await waitFor(() => expect(screen.getByRole('button', { name: 'Editar Talles' })).toBeEnabled())
  })

  /** Cláusula bajo prueba: `ocupadoRef`, el espejo sincrónico (react-async-state regla 11): dos envíos del
   * formulario en el mismo tick pasan la guarda de estado. */
  it('dos envíos sincrónicos del formulario emiten un solo PUT', async () => {
    apiPutMock.mockImplementation(() => new Promise(() => {}))
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Editar Sabores' }))

    const formulario = screen.getByRole('button', { name: 'Guardar' }).closest('form') as HTMLFormElement
    await act(async () => {
      fireEvent.submit(formulario)
      fireEvent.submit(formulario)
      await Promise.resolve()
    })

    expect(apiPutMock).toHaveBeenCalledTimes(1)
  })

  /** Cláusula bajo prueba: el refresco aislado del try/catch de la escritura (react-async-state regla 6). */
  it('un refresco fallido después de guardar no reporta el guardado como fallido', async () => {
    let lecturas = 0
    apiGetMock.mockImplementation(() =>
      ++lecturas === 1 ? Promise.resolve([sabores]) : Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó el refresco.')),
    )
    render(
      <MemoryRouter>
        <Familias />
      </MemoryRouter>,
    )
    await screen.findByText('Sabores')
    await userEvent.click(screen.getByRole('button', { name: 'Editar Sabores' }))

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(
      await screen.findByText('Se actualizó la familia "Sabores". Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.'),
    ).toBeInTheDocument()
    expect(screen.queryByText(/No se pudo guardar/)).not.toBeInTheDocument()
    expect(screen.queryByText('Se cayó el refresco.')).not.toBeInTheDocument()
  })

  it('editar otra familia reemplaza el formulario abierto', async () => {
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Editar Sabores' }))

    await userEvent.click(screen.getByRole('button', { name: 'Editar Talles' }))

    expect(screen.queryByText('Editando familia 7')).not.toBeInTheDocument()
    expect(screen.getByText('Editando familia 8')).toBeInTheDocument()
    expect(screen.getByLabelText('Nombre')).toHaveValue('Talles')
  })
})

/** Cláusulas bajo prueba: cada acción que empieza borra lo que dijo la anterior (el aviso de éxito o el rechazo), para que
 * mientras la nueva está en vuelo —o abierta— no quede en pantalla un mensaje que ya no habla de lo que se está haciendo. */
describe('Familias — los mensajes de la acción anterior no sobreviven a la siguiente', () => {
  const rechazoDeNombre = new ErrorApi(409, 'familia_nombre_duplicado', 'Ya existe una familia llamada "Talles" en este tenant.')

  it('guardar de nuevo borra el rechazo del intento anterior apenas empieza', async () => {
    const segundo = diferida<FamiliaListado>()
    apiPutMock.mockRejectedValueOnce(rechazoDeNombre).mockReturnValueOnce(segundo.promesa)
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Editar Sabores' }))
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    await screen.findByText(/Ya existe una familia llamada "Talles"/)

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(apiPutMock).toHaveBeenCalledTimes(2)
    expect(screen.queryByText(/Ya existe una familia llamada "Talles"/)).not.toBeInTheDocument()
    await act(async () => {
      segundo.resolver(sabores)
    })
  })

  it('guardar borra el aviso de la escritura anterior apenas empieza', async () => {
    const guardado = diferida<FamiliaListado>()
    apiPutMock.mockReturnValue(guardado.promesa)
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Editar Talles' }))
    await userEvent.click(screen.getByRole('button', { name: 'Disolver Sabores' }))
    await userEvent.click(screen.getByRole('button', { name: 'Confirmar disolución' }))
    await screen.findByText(/Se disolvió la familia "Sabores"/)
    await waitFor(() => expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled())

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(apiPutMock).toHaveBeenCalledTimes(1)
    expect(screen.queryByText(/Se disolvió la familia/)).not.toBeInTheDocument()
    await act(async () => {
      guardado.resolver(talles)
    })
  })

  it('confirmar la disolución de nuevo borra el rechazo del intento anterior apenas empieza', async () => {
    const segundo = diferida<void>()
    apiDeleteMock.mockRejectedValueOnce(new ErrorApi(500, 'error_interno', 'Se cayó.')).mockReturnValueOnce(segundo.promesa)
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Disolver Sabores' }))
    await userEvent.click(screen.getByRole('button', { name: 'Confirmar disolución' }))
    await screen.findByText(/No se pudo disolver la familia\./)

    await userEvent.click(screen.getByRole('button', { name: 'Confirmar disolución' }))

    expect(apiDeleteMock).toHaveBeenCalledTimes(2)
    expect(screen.queryByText(/No se pudo disolver la familia\./)).not.toBeInTheDocument()
    await act(async () => {
      segundo.resolver()
    })
  })

  it('abrir la puerta borra el aviso de la escritura anterior', async () => {
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Editar Sabores' }))
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    await screen.findByText('Se actualizó la familia "Sabores".')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Disolver Talles' })).toBeEnabled())

    await userEvent.click(screen.getByRole('button', { name: 'Disolver Talles' }))

    expect(screen.queryByText(/Se actualizó la familia/)).not.toBeInTheDocument()
  })

  it('abrir la puerta borra el rechazo del guardado anterior', async () => {
    apiPutMock.mockRejectedValueOnce(rechazoDeNombre)
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Editar Sabores' }))
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    await screen.findByText(/Ya existe una familia llamada "Talles"/)

    await userEvent.click(screen.getByRole('button', { name: 'Disolver Talles' }))

    expect(screen.queryByText(/Ya existe una familia llamada "Talles"/)).not.toBeInTheDocument()
  })

  it('abrir el formulario borra el aviso de la disolución anterior', async () => {
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Disolver Sabores' }))
    await userEvent.click(screen.getByRole('button', { name: 'Confirmar disolución' }))
    await screen.findByText(/Se disolvió la familia "Sabores"/)
    await waitFor(() => expect(screen.getByRole('button', { name: 'Editar Talles' })).toBeEnabled())

    await userEvent.click(screen.getByRole('button', { name: 'Editar Talles' }))

    expect(screen.queryByText(/Se disolvió la familia/)).not.toBeInTheDocument()
  })

  it('abrir el formulario de otra familia borra el rechazo del guardado anterior', async () => {
    apiPutMock.mockRejectedValueOnce(rechazoDeNombre)
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Editar Sabores' }))
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    await screen.findByText(/Ya existe una familia llamada "Talles"/)

    await userEvent.click(screen.getByRole('button', { name: 'Editar Talles' }))

    expect(screen.queryByText(/Ya existe una familia llamada "Talles"/)).not.toBeInTheDocument()
  })
})

describe('Familias — disolver', () => {
  function puerta() {
    return screen.getByRole('alertdialog', { name: 'Confirmar disolución' })
  }

  it('"Disolver" no escribe: pide confirmar nombrando la familia y diciendo qué pasa con sus artículos', async () => {
    await montarYEsperar()

    await userEvent.click(screen.getByRole('button', { name: 'Disolver Sabores' }))

    expect(puerta()).toHaveTextContent('¿Disolver la familia "Sabores"?')
    expect(puerta()).toHaveTextContent(
      'Sus 3 artículos quedan sin familia y conservan todos sus valores y sus precios. La familia se da de baja y su nombre se puede volver a usar.',
    )
    expect(apiDeleteMock).not.toHaveBeenCalled()
  })

  it.each([
    [0, 'La familia no tiene artículos vivos. La familia se da de baja y su nombre se puede volver a usar.'],
    [1, 'Su único artículo queda sin familia y conserva todos sus valores y sus precios. La familia se da de baja y su nombre se puede volver a usar.'],
  ])('con %i artículos la nota habla en el número que corresponde', async (cantidad, nota) => {
    await montarYEsperar([{ ...sabores, cantidadArticulos: cantidad }])

    await userEvent.click(screen.getByRole('button', { name: 'Disolver Sabores' }))

    expect(puerta()).toHaveTextContent(nota)
  })

  it('"Cancelar" cierra la puerta sin llamar a la API', async () => {
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Disolver Sabores' }))

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()
    expect(screen.getByRole('button', { name: 'Disolver Talles' })).toBeEnabled()
  })

  /** Cláusula bajo prueba: `disolucion !== null` dentro de `bloqueado`. */
  it('con la puerta abierta nada más queda alcanzable', async () => {
    await montarYEsperar()

    await userEvent.click(screen.getByRole('button', { name: 'Disolver Sabores' }))

    expect(screen.getByRole('button', { name: 'Editar Sabores' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Disolver Talles' })).toBeDisabled()
    expect(screen.getByRole('link', { name: 'Nueva familia' })).toHaveAttribute('aria-disabled', 'true')
    expect(screen.getByRole('link', { name: 'Ver detalle de Sabores' })).toHaveAttribute('aria-disabled', 'true')
    await userEvent.click(screen.getByRole('link', { name: 'Ver detalle de Sabores' }))
    expect(screen.queryByText('Pantalla de detalle de la familia')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Confirmar disolución' })).toBeEnabled()
  })

  it('confirmar disuelve esa familia: DELETE, puerta cerrada, aviso y listado refrescado', async () => {
    let lecturas = 0
    apiGetMock.mockImplementation(() => Promise.resolve(++lecturas === 1 ? [sabores, talles] : [talles]))
    render(
      <MemoryRouter>
        <Familias />
      </MemoryRouter>,
    )
    await screen.findByText('Sabores')
    await userEvent.click(screen.getByRole('button', { name: 'Disolver Sabores' }))

    await userEvent.click(screen.getByRole('button', { name: 'Confirmar disolución' }))

    expect(await screen.findByText('Se disolvió la familia "Sabores": sus artículos quedaron sin familia.')).toBeInTheDocument()
    expect(apiDeleteMock).toHaveBeenCalledExactlyOnceWith('/familias/7')
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    await waitFor(() => expect(screen.queryByText('Sabores')).not.toBeInTheDocument())
    expect(screen.getByText('Talles')).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: la ventana inerte cubre el DELETE y también su refresco, y `ocupadoRef` descarta el segundo
   * clic del mismo tick. */
  it('durante el DELETE y su refresco no queda nada alcanzable, y dos clics sincrónicos emiten un solo DELETE', async () => {
    const borrado = diferida<void>()
    const refresco = diferida<FamiliaListado[]>()
    let lecturas = 0
    apiGetMock.mockImplementation(() => (++lecturas === 1 ? Promise.resolve([sabores, talles]) : refresco.promesa))
    apiDeleteMock.mockReturnValue(borrado.promesa)
    render(
      <MemoryRouter>
        <Familias />
      </MemoryRouter>,
    )
    await screen.findByText('Sabores')
    await userEvent.click(screen.getByRole('button', { name: 'Disolver Sabores' }))
    const confirmar = screen.getByRole('button', { name: 'Confirmar disolución' })

    await act(async () => {
      confirmar.click()
      confirmar.click()
      await Promise.resolve()
    })

    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('button', { name: 'Disolviendo…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Editar Talles' })).toBeDisabled()

    await act(async () => {
      borrado.resolver()
    })
    await screen.findByText(/Se disolvió la familia "Sabores"/)
    expect(screen.getByText('Cargando…')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Nueva familia' })).toHaveAttribute('aria-disabled', 'true')

    await act(async () => {
      refresco.resolver([talles])
    })
    await waitFor(() => expect(screen.getByRole('button', { name: 'Editar Talles' })).toBeEnabled())
  })

  it('un refresco fallido después de disolver no reporta la disolución como fallida', async () => {
    let lecturas = 0
    apiGetMock.mockImplementation(() =>
      ++lecturas === 1 ? Promise.resolve([sabores]) : Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó.')),
    )
    render(
      <MemoryRouter>
        <Familias />
      </MemoryRouter>,
    )
    await screen.findByText('Sabores')
    await userEvent.click(screen.getByRole('button', { name: 'Disolver Sabores' }))

    await userEvent.click(screen.getByRole('button', { name: 'Confirmar disolución' }))

    expect(
      await screen.findByText(
        'Se disolvió la familia "Sabores": sus artículos quedaron sin familia. Se disolvió, pero no se pudo actualizar la vista. Recargá la pantalla.',
      ),
    ).toBeInTheDocument()
    expect(screen.queryByText(/No se pudo disolver/)).not.toBeInTheDocument()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: la rama `estado === 404` del `catch` de `confirmarDisolucion`. Una familia que ya no existe no
   * se puede disolver: la puerta no tiene nada que confirmar, así que se cierra y el listado se vuelve a leer. La copia
   * neutra queda a la vista. Evidencia de mutación (mutation-proof-tests): sacar la rama hace fallar este test (la puerta
   * sigue abierta y el listado, sin releer); revertido, vuelve a verde. */
  it('un 404 rinde la copia neutra, cierra la puerta y vuelve a leer el listado', async () => {
    let lecturas = 0
    apiGetMock.mockImplementation(() => Promise.resolve(++lecturas === 1 ? [sabores, talles] : [talles]))
    apiDeleteMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'No existe la familia 7.'))
    render(
      <MemoryRouter>
        <Familias />
      </MemoryRouter>,
    )
    await screen.findByText('Sabores')
    await userEvent.click(screen.getByRole('button', { name: 'Disolver Sabores' }))

    await userEvent.click(screen.getByRole('button', { name: 'Confirmar disolución' }))

    const copia = 'No se pudo disolver la familia. Ya no existe o no está a tu alcance. Actualizá el listado.'
    expect(await screen.findByText(copia)).toBeInTheDocument()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.queryByText(/No existe la familia 7/)).not.toBeInTheDocument()
    await waitFor(() => expect(screen.queryByText('Sabores')).not.toBeInTheDocument())
    expect(lecturas).toBe(2)
    expect(screen.getByRole('button', { name: 'Editar Talles' })).toBeEnabled()
    expect(screen.getByText(copia)).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: el `setFormulario(...)` de esa misma rama: el formulario abierto sobre la familia que ya no
   * existe se cierra. Evidencia de mutación (mutation-proof-tests): sacarlo hace fallar este test; revertido, vuelve a
   * verde. */
  it('un 404 al disolver la familia que se está editando cierra también su formulario', async () => {
    apiDeleteMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'No existe la familia 7.'))
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Editar Sabores' }))
    await userEvent.click(screen.getByRole('button', { name: 'Disolver Sabores' }))

    await userEvent.click(screen.getByRole('button', { name: 'Confirmar disolución' }))

    await screen.findByText(/Ya no existe o no está a tu alcance/)
    expect(screen.queryByText('Editando familia 7')).not.toBeInTheDocument()
  })

  it('disolver la familia que se está editando cierra también su formulario', async () => {
    await montarYEsperar()
    await userEvent.click(screen.getByRole('button', { name: 'Editar Sabores' }))
    expect(screen.getByText('Editando familia 7')).toBeInTheDocument()
    // La puerta deja inerte el formulario, pero lo deja abierto hasta que la disolución se confirma.
    await userEvent.click(screen.getByRole('button', { name: 'Disolver Sabores' }))

    await userEvent.click(screen.getByRole('button', { name: 'Confirmar disolución' }))

    await screen.findByText(/Se disolvió la familia "Sabores"/)
    expect(screen.queryByText('Editando familia 7')).not.toBeInTheDocument()
  })

  /** Advertencia (react-async-state regla 12): jsdom no implementa la regla de "focus fixup" del navegador, así que
   * esto prueba que el disparador se captura en el clic y se le devuelve el foco, no el comportamiento de un navegador
   * real. */
  it('al cancelar, el foco vuelve al botón que abrió la puerta', async () => {
    await montarYEsperar()
    const disparador = screen.getByRole('button', { name: 'Disolver Sabores' })
    await userEvent.click(disparador)

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))

    await waitFor(() => expect(disparador).toHaveFocus())
  })
})
