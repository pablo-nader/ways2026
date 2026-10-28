import { useState } from 'react'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { MAXIMO_DE_LINEAS_DE_PAGO, type LineaDePagoEditable } from '../api/lineasDePago'
import type { MedioPagoListado } from '../api/tipos'
import { EditorDeLineasDePago } from './EditorDeLineasDePago'

function medio(id: number, nombre: string): MedioPagoListado {
  return {
    id,
    nombre,
    activo: true,
    idEmpresa: null,
    orden: id,
    comportamiento: 'Efectivo',
    admiteVuelto: false,
    requiereReferencia: false,
    recargoPorcentaje: null,
    porcentajeFacturacion: 0,
  }
}

const medios = [medio(1, 'Efectivo'), medio(2, 'Transferencia')]

function linea(sobrescribir: Partial<LineaDePagoEditable> = {}): LineaDePagoEditable {
  return { clave: 1, idMedioPago: '', importe: null, referencia: '', ...sobrescribir }
}

/** Envoltorio mínimo que guarda el estado como lo hace una pantalla real, y lo deja a la vista del test. */
function Controlado({
  inicial,
  objetivo,
  deshabilitado,
  alCambiar,
}: {
  inicial: LineaDePagoEditable[]
  objetivo?: number | null
  deshabilitado?: boolean
  alCambiar?: (lineas: LineaDePagoEditable[]) => void
}) {
  const [lineas, setLineas] = useState(inicial)
  alCambiar?.(lineas)
  return <EditorDeLineasDePago lineas={lineas} medios={medios} onCambio={setLineas} objetivo={objetivo} deshabilitado={deshabilitado} />
}

describe('EditorDeLineasDePago', () => {
  it('rotula cada fila con su número y ofrece los medios recibidos', () => {
    render(<Controlado inicial={[linea({ clave: 1 }), linea({ clave: 2 })]} />)

    expect(screen.getByLabelText('Medio de la línea 1')).toBeInTheDocument()
    expect(screen.getByLabelText('Importe de la línea 1')).toBeInTheDocument()
    expect(screen.getByLabelText('Referencia de la línea 1')).toBeInTheDocument()
    expect(screen.getByLabelText('Medio de la línea 2')).toBeInTheDocument()
    expect(screen.getAllByRole('option', { name: 'Transferencia' })).toHaveLength(2)
  })

  it('editar el medio, el importe y la referencia de una línea cambia solo esa línea', async () => {
    const usuario = userEvent.setup()
    let ultimas: LineaDePagoEditable[] = []
    render(<Controlado inicial={[linea({ clave: 1 }), linea({ clave: 2 })]} alCambiar={(l) => (ultimas = l)} />)

    await usuario.selectOptions(screen.getByLabelText('Medio de la línea 2'), 'Transferencia')
    await usuario.type(screen.getByLabelText('Importe de la línea 2'), '400,5')
    await usuario.type(screen.getByLabelText('Referencia de la línea 2'), 'Op 9')

    expect(ultimas[0]).toEqual({ clave: 1, idMedioPago: '', importe: null, referencia: '' })
    expect(ultimas[1]).toEqual({ clave: 2, idMedioPago: 2, importe: 400.5, referencia: 'Op 9' })
  })

  it('"Agregar medio" suma una línea vacía y "Quitar línea" saca la elegida', async () => {
    const usuario = userEvent.setup()
    let ultimas: LineaDePagoEditable[] = []
    render(<Controlado inicial={[linea({ clave: 1, idMedioPago: 1, importe: 10 })]} alCambiar={(l) => (ultimas = l)} />)

    await usuario.click(screen.getByRole('button', { name: 'Agregar medio' }))
    expect(screen.getByLabelText('Medio de la línea 2')).toBeInTheDocument()
    expect(ultimas).toHaveLength(2)

    await usuario.click(screen.getByRole('button', { name: 'Quitar línea 1' }))
    expect(ultimas).toEqual([{ clave: 2, idMedioPago: '', importe: null, referencia: '' }])
    expect(screen.queryByLabelText('Medio de la línea 2')).not.toBeInTheDocument()
  })

  it('la única línea no se puede quitar', () => {
    render(<Controlado inicial={[linea()]} />)
    expect(screen.getByRole('button', { name: 'Quitar línea 1' })).toBeDisabled()
  })

  it('muestra el total de las líneas', () => {
    render(<Controlado inicial={[linea({ clave: 1, importe: 600 }), linea({ clave: 2, importe: 400 })]} />)
    expect(screen.getByText(/Total: \$\s1\.000,00/)).toBeInTheDocument()
  })

  it('con objetivo informa lo que falta, y la línea nueva arranca con ese faltante', async () => {
    const usuario = userEvent.setup()
    render(<Controlado inicial={[linea({ clave: 1, idMedioPago: 1, importe: 600 })]} objetivo={1000} />)

    expect(screen.getByText(/Falta \$\s400,00/)).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Agregar medio' }))
    expect(screen.getByLabelText('Importe de la línea 2')).toHaveValue('400,00')
    expect(screen.getByText('Sin diferencia')).toBeInTheDocument()
  })

  it('con objetivo informa lo que sobra', () => {
    render(<Controlado inicial={[linea({ clave: 1, idMedioPago: 1, importe: 1200 })]} objetivo={1000} />)
    expect(screen.getByText(/Sobra \$\s200,00/)).toBeInTheDocument()
  })

  it('sin objetivo no muestra faltante ni sobrante', () => {
    render(<Controlado inicial={[linea({ clave: 1, importe: 5 })]} />)
    expect(screen.queryByText(/Falta|Sobra|Sin diferencia/)).not.toBeInTheDocument()
  })

  it('con el máximo de líneas no deja agregar más', () => {
    const llena = Array.from({ length: MAXIMO_DE_LINEAS_DE_PAGO }, (_, i) => linea({ clave: i + 1 }))
    render(<Controlado inicial={llena} />)
    expect(screen.getByRole('button', { name: 'Agregar medio' })).toBeDisabled()
  })

  it('deshabilitado deja inertes todos los controles', () => {
    render(<Controlado inicial={[linea({ clave: 1 }), linea({ clave: 2 })]} deshabilitado />)

    expect(screen.getByLabelText('Medio de la línea 1')).toBeDisabled()
    expect(screen.getByLabelText('Importe de la línea 2')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Agregar medio' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Quitar línea 1' })).toBeDisabled()
  })

  it('conserva como "no disponible" el medio de una línea que ya no está en el catálogo', () => {
    render(<Controlado inicial={[linea({ clave: 1, idMedioPago: 99, importe: 5 })]} />)

    expect(screen.getByRole('option', { name: 'Medio #99 (no disponible)' })).toBeInTheDocument()
    expect(screen.getByLabelText('Medio de la línea 1')).toHaveValue('99')
  })
})
