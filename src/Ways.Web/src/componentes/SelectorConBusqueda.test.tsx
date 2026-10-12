import { useState } from 'react'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { combinarResultados, filtrarOpciones, SelectorConBusqueda } from './SelectorConBusqueda'
import type { OpcionDeSelector } from './SelectorConBusqueda'

type Opcion = OpcionDeSelector & { origen?: string }

const OPCIONES: Opcion[] = [
  { id: 1, etiqueta: '#1 — Consumidor Final' },
  { id: 2, etiqueta: '#2 — José Pérez' },
  { id: 3, etiqueta: '#3 — Ana Gómez' },
  { id: 4, etiqueta: '#4 — Perez Hermanos S.A.' },
]

type PropsDePrueba = Partial<Parameters<typeof SelectorConBusqueda<Opcion>>[0]> & { inicial?: number | null }

/** Contenedor con el estado del valor, como lo usaría una pantalla: el selector no guarda el valor. */
function Montaje({ inicial = 1, onChange, ...resto }: PropsDePrueba) {
  const [valor, setValor] = useState<number | string | null>(inicial)
  return (
    <SelectorConBusqueda<Opcion>
      nombreAccesible="Cliente"
      opciones={OPCIONES}
      valor={valor}
      onChange={(o) => {
        setValor(o.id)
        onChange?.(o)
      }}
      {...resto}
    />
  )
}

function caja() {
  return screen.getByRole('combobox', { name: 'Cliente' })
}

function etiquetasVisibles() {
  return screen.queryAllByRole('option').map((o) => o.textContent)
}

describe('combinarResultados / filtrarOpciones', () => {
  it('filtrarOpciones: contiene, sin distinguir mayúsculas ni acentos; vacío devuelve todo', () => {
    expect(filtrarOpciones(OPCIONES, 'jose').map((o) => o.id)).toEqual([2])
    expect(filtrarOpciones(OPCIONES, 'PEREZ').map((o) => o.id)).toEqual([2, 4])
    expect(filtrarOpciones(OPCIONES, 'ez her').map((o) => o.id)).toEqual([4])
    expect(filtrarOpciones(OPCIONES, '   ')).toHaveLength(4)
  })

  it('combinarResultados: las remotas van primero y, para un mismo id, gana la remota', () => {
    const remotas: Opcion[] = [{ id: 2, etiqueta: 'remoto 2', origen: 'servidor' }]
    const locales: Opcion[] = [
      { id: 2, etiqueta: 'local 2' },
      { id: 3, etiqueta: 'local 3' },
      { id: 3, etiqueta: 'local 3 repetido' },
    ]
    expect(combinarResultados(remotas, locales)).toEqual([
      { id: 2, etiqueta: 'remoto 2', origen: 'servidor' },
      { id: 3, etiqueta: 'local 3' },
    ])
  })
})

describe('SelectorConBusqueda', () => {
  it('cerrado muestra la etiqueta de la opción elegida y no dibuja la lista', () => {
    render(<Montaje inicial={2} />)
    expect(caja()).toHaveValue('#2 — José Pérez')
    expect(caja()).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
  })

  it('se ve como un select de Bootstrap', () => {
    render(<Montaje />)
    expect(caja()).toHaveClass('form-select')
  })

  it('un clic abre la lista completa con la opción elegida como activa', async () => {
    render(<Montaje inicial={3} />)
    await userEvent.click(caja())
    expect(screen.getByRole('listbox')).toBeInTheDocument()
    expect(caja()).toHaveAttribute('aria-expanded', 'true')
    expect(etiquetasVisibles()).toEqual(OPCIONES.map((o) => o.etiqueta))
    const activa = screen.getByRole('option', { name: '#3 — Ana Gómez' })
    expect(activa).toHaveAttribute('aria-selected', 'true')
    expect(caja()).toHaveAttribute('aria-activedescendant', activa.id)
  })

  it('tipear filtra por "contiene", sin distinguir mayúsculas ni acentos, y no elige nada por sí solo', async () => {
    const onChange = vi.fn()
    render(<Montaje onChange={onChange} />)
    await userEvent.click(caja())
    await userEvent.keyboard('JOSE')
    expect(etiquetasVisibles()).toEqual(['#2 — José Pérez'])
    await userEvent.clear(caja())
    await userEvent.keyboard('rez her')
    expect(etiquetasVisibles()).toEqual(['#4 — Perez Hermanos S.A.'])
    expect(onChange).not.toHaveBeenCalled()
  })

  it('al enfocar queda todo el texto seleccionado: lo que se tipea reemplaza la etiqueta', async () => {
    render(<Montaje inicial={2} />)
    await userEvent.click(caja())
    await userEvent.keyboard('gom')
    expect(caja()).toHaveValue('gom')
    expect(etiquetasVisibles()).toEqual(['#3 — Ana Gómez'])
  })

  it('Enter elige la opción activa; tras tipear, la primera coincidencia', async () => {
    const onChange = vi.fn()
    render(<Montaje onChange={onChange} />)
    await userEvent.click(caja())
    await userEvent.keyboard('perez')
    await userEvent.keyboard('{Enter}')
    expect(onChange).toHaveBeenCalledTimes(1)
    expect(onChange).toHaveBeenCalledWith(OPCIONES[1])
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    expect(caja()).toHaveValue('#2 — José Pérez')
  })

  it('las flechas mueven la opción activa con vuelta, y Home y End saltan a los extremos', async () => {
    const onChange = vi.fn()
    render(<Montaje inicial={1} onChange={onChange} />)
    await userEvent.click(caja())
    const activa = () => screen.getByRole('option', { selected: true }).textContent
    expect(activa()).toBe('#1 — Consumidor Final')
    await userEvent.keyboard('{ArrowDown}')
    expect(activa()).toBe('#2 — José Pérez')
    await userEvent.keyboard('{ArrowUp}{ArrowUp}')
    expect(activa()).toBe('#4 — Perez Hermanos S.A.')
    await userEvent.keyboard('{Home}')
    expect(activa()).toBe('#1 — Consumidor Final')
    await userEvent.keyboard('{End}')
    expect(activa()).toBe('#4 — Perez Hermanos S.A.')
    await userEvent.keyboard('{ArrowDown}')
    expect(activa()).toBe('#1 — Consumidor Final')
    expect(onChange).not.toHaveBeenCalled()
  })

  it('con la lista cerrada, ArrowDown la abre sin elegir', async () => {
    const onChange = vi.fn()
    render(<Montaje onChange={onChange} />)
    caja().focus()
    await userEvent.keyboard('{ArrowDown}')
    expect(screen.getByRole('listbox')).toBeInTheDocument()
    expect(onChange).not.toHaveBeenCalled()
  })

  it('Escape cierra, restaura la etiqueta elegida, no cambia el valor y avisa con alCancelar', async () => {
    const onChange = vi.fn()
    const alCancelar = vi.fn()
    render(<Montaje inicial={2} onChange={onChange} alCancelar={alCancelar} />)
    await userEvent.click(caja())
    await userEvent.keyboard('ana')
    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    expect(caja()).toHaveValue('#2 — José Pérez')
    expect(onChange).not.toHaveBeenCalled()
    expect(alCancelar).toHaveBeenCalledTimes(1)
  })

  it('perder el foco sin elegir cierra y restaura la etiqueta, sin avisar con alCancelar', async () => {
    const onChange = vi.fn()
    const alCancelar = vi.fn()
    render(
      <>
        <Montaje inicial={2} onChange={onChange} alCancelar={alCancelar} />
        <button type="button">Afuera</button>
      </>,
    )
    await userEvent.click(caja())
    await userEvent.keyboard('ana')
    await userEvent.click(screen.getByRole('button', { name: 'Afuera' }))
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    expect(caja()).toHaveValue('#2 — José Pérez')
    expect(onChange).not.toHaveBeenCalled()
    expect(alCancelar).not.toHaveBeenCalled()
  })

  it('un clic fuera de cualquier control cierra la lista', async () => {
    render(<Montaje />)
    await userEvent.click(caja())
    expect(screen.getByRole('listbox')).toBeInTheDocument()
    await userEvent.click(document.body)
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
  })

  it('un clic sobre una opción la elige', async () => {
    const onChange = vi.fn()
    render(<Montaje onChange={onChange} />)
    await userEvent.click(caja())
    await userEvent.click(screen.getByRole('option', { name: '#3 — Ana Gómez' }))
    expect(onChange).toHaveBeenCalledWith(OPCIONES[2])
    expect(caja()).toHaveValue('#3 — Ana Gómez')
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
  })

  it('sin coincidencias muestra el texto de "sin resultados"', async () => {
    render(<Montaje textoSinResultados={(t) => `Nadie coincide con "${t}"`} />)
    await userEvent.click(caja())
    await userEvent.keyboard('zzz')
    expect(screen.queryAllByRole('option')).toHaveLength(0)
    expect(screen.getByText('Nadie coincide con "zzz"')).toBeInTheDocument()
  })

  it('deshabilitado no abre la lista', async () => {
    render(<Montaje disabled />)
    expect(caja()).toBeDisabled()
    await userEvent.click(caja())
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
  })

  it('un cambio del valor desde afuera se refleja en la caja cerrada', () => {
    const { rerender } = render(<SelectorConBusqueda<Opcion> nombreAccesible="Cliente" opciones={OPCIONES} valor={1} onChange={() => {}} />)
    expect(caja()).toHaveValue('#1 — Consumidor Final')
    rerender(<SelectorConBusqueda<Opcion> nombreAccesible="Cliente" opciones={OPCIONES} valor={4} onChange={() => {}} />)
    expect(caja()).toHaveValue('#4 — Perez Hermanos S.A.')
  })

  describe('posición y tamaño de la lista', () => {
    function simularCaja(rect: Partial<DOMRect>) {
      const completo = { top: 0, bottom: 0, left: 0, right: 0, width: 0, height: 0, x: 0, y: 0, toJSON: () => ({}), ...rect }
      vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue(completo as DOMRect)
    }

    it('con lugar abajo, se ancla bajo la caja y su alto máximo entra en la ventana', async () => {
      vi.stubGlobal('innerHeight', 768)
      simularCaja({ top: 40, bottom: 70, left: 20, width: 200 })
      render(<Montaje />)
      await userEvent.click(caja())
      const lista = screen.getByRole('listbox').parentElement as HTMLElement
      expect(lista.style.top).toBe('70px')
      expect(lista.style.bottom).toBe('')
      expect(Number.parseFloat(lista.style.maxHeight)).toBeLessThanOrEqual(768 - 70)
      expect(lista).toHaveStyle({ overflowY: 'auto' })
      vi.restoreAllMocks()
      vi.unstubAllGlobals()
    })

    it('sin lugar abajo y con más arriba, se abre hacia arriba sin pasarse del borde superior', async () => {
      vi.stubGlobal('innerHeight', 400)
      simularCaja({ top: 330, bottom: 360, left: 20, width: 200 })
      render(<Montaje />)
      await userEvent.click(caja())
      const lista = screen.getByRole('listbox').parentElement as HTMLElement
      expect(lista.style.top).toBe('')
      expect(lista.style.bottom).toBe('70px')
      expect(Number.parseFloat(lista.style.maxHeight)).toBeLessThanOrEqual(330)
      vi.restoreAllMocks()
      vi.unstubAllGlobals()
    })

    it('un scroll dentro de la lista no la cierra, pero uno de la página sí', async () => {
      render(<Montaje />)
      await userEvent.click(caja())
      fireEvent.scroll(screen.getByRole('listbox'))
      expect(screen.getByRole('listbox')).toBeInTheDocument()
      fireEvent.scroll(document.body)
      expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    })
  })

  describe('búsqueda remota', () => {
    const ESPERA = 20

    function respuestaControlable() {
      const pedidos: { texto: string; resolver: (o: Opcion[]) => void; rechazar: (e: Error) => void }[] = []
      const alBuscar = vi.fn(
        (texto: string) =>
          new Promise<Opcion[]>((resolver, rechazar) => {
            pedidos.push({ texto, resolver, rechazar })
          }),
      )
      return { alBuscar, pedidos }
    }

    it('espera a que se deje de tipear y consulta una sola vez con el texto final', async () => {
      const { alBuscar } = respuestaControlable()
      render(<Montaje alBuscar={alBuscar} esperaMs={400} />)
      await userEvent.click(caja())
      await userEvent.keyboard('pere')
      expect(alBuscar).not.toHaveBeenCalled()
      await waitFor(() => expect(alBuscar).toHaveBeenCalledTimes(1))
      expect(alBuscar).toHaveBeenCalledWith('pere')
    })

    it('sin texto no consulta', async () => {
      const { alBuscar } = respuestaControlable()
      render(<Montaje alBuscar={alBuscar} esperaMs={ESPERA} />)
      await userEvent.click(caja())
      await new Promise((r) => setTimeout(r, ESPERA * 3))
      expect(alBuscar).not.toHaveBeenCalled()
    })

    it('mientras consulta muestra "Buscando…" y sigue ofreciendo las coincidencias locales', async () => {
      const { alBuscar, pedidos } = respuestaControlable()
      render(<Montaje alBuscar={alBuscar} esperaMs={ESPERA} />)
      await userEvent.click(caja())
      await userEvent.keyboard('perez')
      expect(screen.getByText('Buscando…')).toBeInTheDocument()
      expect(etiquetasVisibles()).toEqual(['#2 — José Pérez', '#4 — Perez Hermanos S.A.'])
      await waitFor(() => expect(pedidos).toHaveLength(1))
      pedidos[0].resolver([])
      await waitFor(() => expect(screen.queryByText('Buscando…')).not.toBeInTheDocument())
    })

    it('suma los resultados remotos delante de los locales y, para el mismo id, gana el remoto', async () => {
      const { alBuscar, pedidos } = respuestaControlable()
      render(<Montaje alBuscar={alBuscar} esperaMs={ESPERA} />)
      await userEvent.click(caja())
      await userEvent.keyboard('perez')
      await waitFor(() => expect(pedidos).toHaveLength(1))
      pedidos[0].resolver([
        { id: 9, etiqueta: '#9 — Perez Lejano' },
        { id: 4, etiqueta: '#4 — Perez Hermanos (servidor)' },
      ])
      await waitFor(() =>
        expect(etiquetasVisibles()).toEqual(['#9 — Perez Lejano', '#4 — Perez Hermanos (servidor)', '#2 — José Pérez']),
      )
    })

    it('buscarLocal aporta coincidencias que la etiqueta no tiene, al instante y sin esperar al servidor', async () => {
      const { alBuscar } = respuestaControlable()
      const buscarLocal = (t: string): Opcion[] => (t === '2030' ? [{ id: 3, etiqueta: '#3 — Ana Gómez' }] : [])
      render(<Montaje alBuscar={alBuscar} buscarLocal={buscarLocal} esperaMs={ESPERA} />)
      await userEvent.click(caja())
      await userEvent.keyboard('2030')
      expect(etiquetasVisibles()).toEqual(['#3 — Ana Gómez'])
    })

    it('una respuesta que llega tarde para un texto anterior se descarta', async () => {
      const { alBuscar, pedidos } = respuestaControlable()
      render(<Montaje alBuscar={alBuscar} esperaMs={ESPERA} />)
      await userEvent.click(caja())
      await userEvent.keyboard('gom')
      await waitFor(() => expect(pedidos).toHaveLength(1))
      await userEvent.keyboard('ez')
      await waitFor(() => expect(pedidos).toHaveLength(2))
      pedidos[1].resolver([{ id: 7, etiqueta: '#7 — Gomez Nuevo' }])
      await waitFor(() => expect(etiquetasVisibles()).toContain('#7 — Gomez Nuevo'))
      pedidos[0].resolver([{ id: 8, etiqueta: '#8 — Gomi Viejo' }])
      await new Promise((r) => setTimeout(r, 30))
      expect(etiquetasVisibles()).not.toContain('#8 — Gomi Viejo')
      expect(etiquetasVisibles()).toContain('#7 — Gomez Nuevo')
    })

    it('una respuesta que llega con la lista ya cerrada no la reabre ni aparece al volver a abrir', async () => {
      const { alBuscar, pedidos } = respuestaControlable()
      render(<Montaje alBuscar={alBuscar} esperaMs={ESPERA} />)
      await userEvent.click(caja())
      await userEvent.keyboard('perez')
      await waitFor(() => expect(pedidos).toHaveLength(1))
      await userEvent.keyboard('{Escape}')
      pedidos[0].resolver([{ id: 9, etiqueta: '#9 — Perez Lejano' }])
      await new Promise((r) => setTimeout(r, 30))
      expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
      await userEvent.click(caja())
      expect(etiquetasVisibles()).not.toContain('#9 — Perez Lejano')
    })

    it('si la consulta falla y no hay coincidencias locales, muestra el mensaje del error', async () => {
      const { alBuscar, pedidos } = respuestaControlable()
      render(<Montaje alBuscar={alBuscar} esperaMs={ESPERA} />)
      await userEvent.click(caja())
      await userEvent.keyboard('zzz')
      await waitFor(() => expect(pedidos).toHaveLength(1))
      pedidos[0].rechazar(new Error('No se pudo buscar.'))
      expect(await screen.findByText('No se pudo buscar.')).toBeInTheDocument()
    })

    it('si la consulta falla pero hay coincidencias locales, las deja y no muestra el error', async () => {
      const { alBuscar, pedidos } = respuestaControlable()
      render(<Montaje alBuscar={alBuscar} esperaMs={ESPERA} />)
      await userEvent.click(caja())
      await userEvent.keyboard('perez')
      await waitFor(() => expect(pedidos).toHaveLength(1))
      pedidos[0].rechazar(new Error('No se pudo buscar.'))
      await waitFor(() => expect(screen.queryByText('Buscando…')).not.toBeInTheDocument())
      expect(screen.queryByText('No se pudo buscar.')).not.toBeInTheDocument()
      expect(etiquetasVisibles()).toEqual(['#2 — José Pérez', '#4 — Perez Hermanos S.A.'])
    })

    it('sin alBuscar, tipear nunca muestra "Buscando…"', async () => {
      render(<Montaje esperaMs={ESPERA} />)
      await userEvent.click(caja())
      await userEvent.keyboard('perez')
      expect(screen.queryByText('Buscando…')).not.toBeInTheDocument()
    })
  })
})
