import type { CSSProperties } from 'react'
import type { OfertaAplicada } from '../api/tipos'
import type { DescriptorDeFormato } from './formatos'
import '../estilos/etiquetas.css'

/**
 * Espejo de `Ways.Application.Etiquetas.Contratos.FilaDeEtiqueta` (design.md:193-195). No existe
 * consumidor de API todavía en este slice (slice 3 crea `api/etiquetas.ts` con el mirror completo
 * de `SolicitudDeEtiquetas`/`DatosDeEtiquetas`); este tipo es el contrato mínimo que el renderer
 * PURO necesita y queda disponible para que slice 3 lo reutilice en vez de duplicarlo.
 */
export type FilaDeEtiqueta = {
  readonly idArticulo: number
  readonly codigoInterno: string
  readonly codigoBarra: string | null
  readonly nombre: string
  readonly unidadVenta: string
  readonly precioOriginal: number
  readonly precioFinal: number
  readonly ofertas: readonly OfertaAplicada[]
}

type Props = {
  descriptor: DescriptorDeFormato
  /** Ya multiplicadas por copias (decisión 4, `expandirCeldas` en slice 3) — este componente no
   * conoce el concepto de "copias". */
  celdas: readonly FilaDeEtiqueta[]
  /** Decisión 11: el nombre de la lista SIEMPRE viene del servidor, nunca del selector en pantalla. */
  nombreDeLista: string
  /** El spike usa el MISMO componente que la hoja real — una grilla dibujada con otros números
   * no prueba nada (design.md:73-75, mutation target 6). */
  modo?: 'normal' | 'calibracion'
}

function formatearMoneda(valor: number): string {
  return `$${valor.toLocaleString('es-AR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
}

/** Geometría emitida como custom properties en mm sobre `.hoja-de-etiquetas` — la única
 * proyección que jsdom PUEDE medir (design.md:172-176). Compartida por ambos modos: mutation
 * target 6 exige que la calibración emita EXACTAMENTE la misma geometría que el modo normal para
 * el mismo descriptor. */
function estiloDeGeometria(descriptor: DescriptorDeFormato): CSSProperties {
  const pitchX = descriptor.celdaMm.ancho + descriptor.medianilHorizontalMm
  const pitchY = descriptor.celdaMm.alto + descriptor.medianilVerticalMm

  return {
    '--pagina-ancho': `${descriptor.paginaMm.ancho}mm`,
    '--pagina-alto': `${descriptor.paginaMm.alto}mm`,
    '--margen-sup': `${descriptor.margenSuperiorMm}mm`,
    '--margen-izq': `${descriptor.margenIzquierdoMm}mm`,
    '--celda-ancho': `${descriptor.celdaMm.ancho}mm`,
    '--celda-alto': `${descriptor.celdaMm.alto}mm`,
    '--pitch-x': `${pitchX}mm`,
    '--pitch-y': `${pitchY}mm`,
    '--medianil-h': `${descriptor.medianilHorizontalMm}mm`,
    '--medianil-v': `${descriptor.medianilVerticalMm}mm`,
    '--columnas': descriptor.columnas,
    '--filas': descriptor.filas,
    '--pad-externo': `${descriptor.padExternoMm}mm`,
  } as CSSProperties
}

/** Bloque de instrucciones de impresión (design.md:84, mutation target 8): A4, 100% de escala,
 * "ajustar a la página" apagado, sin márgenes, gráficos de fondo activados. Solo aparece en modo
 * calibración — es el spike quien lo necesita en pantalla para guiar la corrida física. */
function InstruccionesDeImpresion() {
  return (
    <div className="d-print-none alert alert-warning rounded-0" data-testid="instrucciones-de-impresion">
      <strong>Configuración de impresión requerida:</strong>
      <ul className="mb-0">
        <li>Tamaño de papel: A4</li>
        <li>Escala: 100% (nunca "ajustar a la página")</li>
        <li>Márgenes: ninguno</li>
        <li>Gráficos de fondo: activados</li>
      </ul>
    </div>
  )
}

function CuadradoDeEscala() {
  return (
    <div className="cuadrado-de-escala" data-testid="cuadrado-de-escala">
      <span data-testid="etiqueta-cuadrado-de-escala">100.0 × 100.0 mm</span>
    </div>
  )
}

/** design.md:82: "1 mm ticks, 10 mm labels" — una marca menor por milímetro, una marca mayor
 * (con el número) cada 10 mm. La distinción visual entre menor/mayor es la clase, no el texto:
 * ambas comparten `regla-tick`, solo la mayor suma `regla-tick-mayor` y el label. */
function ReglaHorizontal() {
  const marcas = Array.from({ length: 201 }, (_, i) => i) // 0..200 mm, cada 1 mm
  return (
    <div className="regla-horizontal" data-testid="regla-horizontal">
      {marcas.map((mm) => {
        const esMayor = mm % 10 === 0
        return (
          <span
            key={mm}
            className={esMayor ? 'regla-tick regla-tick-mayor' : 'regla-tick regla-tick-menor'}
            data-testid={`regla-horizontal-${mm}`}
            style={{ left: `${mm}mm` } as CSSProperties}
          >
            {esMayor ? mm : null}
          </span>
        )
      })}
    </div>
  )
}

function ReglaVertical() {
  const marcas = Array.from({ length: 281 }, (_, i) => i) // 0..280 mm, cada 1 mm
  return (
    <div className="regla-vertical" data-testid="regla-vertical">
      {marcas.map((mm) => {
        const esMayor = mm % 10 === 0
        return (
          <span
            key={mm}
            className={esMayor ? 'regla-tick regla-tick-mayor' : 'regla-tick regla-tick-menor'}
            data-testid={`regla-vertical-${mm}`}
            style={{ top: `${mm}mm` } as CSSProperties}
          >
            {esMayor ? mm : null}
          </span>
        )
      })}
    </div>
  )
}

/** Grilla de calibración (design.md:67-85, task 1.1): un hairline de 0.2 mm por celda nominal, una
 * cruz de registro de 6 mm en el origen (esquina superior-izquierda) de cada celda, la etiqueta
 * `f{row}c{col}` centrada, más las reglas y el cuadrado de escala — todo dibujado con el MISMO
 * descriptor que la hoja real usa. */
function GrillaDeCalibracion({ descriptor }: { descriptor: DescriptorDeFormato }) {
  const filas = Array.from({ length: descriptor.filas }, (_, i) => i)
  const columnas = Array.from({ length: descriptor.columnas }, (_, i) => i)

  return (
    <>
      <ReglaHorizontal />
      <ReglaVertical />
      <div className="grilla-de-celdas" data-testid="grilla-de-calibracion">
        {filas.map((fila) =>
          columnas.map((columna) => (
            <div key={`f${fila}c${columna}`} className="celda celda-calibracion" data-testid={`celda-calibracion-f${fila}c${columna}`}>
              <span className="cruz-registro" aria-hidden="true" data-testid={`cruz-registro-f${fila}c${columna}`} />
              <span>{`f${fila}c${columna}`}</span>
            </div>
          )),
        )}
      </div>
      <CuadradoDeEscala />
      <InstruccionesDeImpresion />
    </>
  )
}

/** Hoja de producción (design.md:162-176, task 1.9): renderer puro props-only — sin fetch, sin
 * estado, sin reloj. La regla de tachado es `ofertas.length > 0`, NUNCA
 * `precioOriginal !== precioFinal` (mutation target 7): producción jamás emite ese par porque el
 * servidor ya resuelve el precio final, así que solo un DTO construido a mano puede probar la
 * diferencia. */
function GrillaDeProduccion({
  descriptor,
  celdas,
  nombreDeLista,
}: {
  descriptor: DescriptorDeFormato
  celdas: readonly FilaDeEtiqueta[]
  nombreDeLista: string
}) {
  return (
    <div className="grilla-de-celdas" data-testid="grilla-de-produccion" data-lista={nombreDeLista}>
      {celdas.map((celda, indice) => {
        const conOferta = celda.ofertas.length > 0
        return (
          <div key={`${celda.idArticulo}-${indice}`} className="celda" data-testid={`celda-${celda.idArticulo}-${indice}`}>
            {descriptor.campos.includes('nombre') && <div className="celda-nombre">{celda.nombre}</div>}
            {conOferta && descriptor.campos.includes('precioOriginal') && (
              <div className="celda-precio-original" data-testid={`precio-original-tachado-${celda.idArticulo}-${indice}`} style={{ textDecoration: 'line-through' }}>
                {formatearMoneda(celda.precioOriginal)}
              </div>
            )}
            {descriptor.campos.includes('precioFinal') && (
              <div className="celda-precio-final" style={{ fontSize: `${descriptor.escalaDePrecio}em` }}>
                {formatearMoneda(celda.precioFinal)}
              </div>
            )}
            {descriptor.campos.includes('codigo') && <div className="celda-codigo">{celda.codigoBarra ?? celda.codigoInterno}</div>}
            {descriptor.campos.includes('unidadVenta') && <div className="celda-unidad">{celda.unidadVenta}</div>}
          </div>
        )
      })}
    </div>
  )
}

/** Componente de impresión PURO (design.md:162-176): props = descriptor + filas ya expandidas por
 * copias. Sin fetch, sin estado, sin reloj — el spike (modo="calibracion") y la pantalla de
 * producción (slice 3, modo="normal") comparten este MISMO componente y el MISMO tuple. */
export function HojaDeEtiquetas({ descriptor, celdas, nombreDeLista, modo = 'normal' }: Props) {
  return (
    <div className="hoja-de-etiquetas" data-modo={modo} style={estiloDeGeometria(descriptor)}>
      {modo === 'calibracion' ? (
        <GrillaDeCalibracion descriptor={descriptor} />
      ) : (
        <GrillaDeProduccion descriptor={descriptor} celdas={celdas} nombreDeLista={nombreDeLista} />
      )}
    </div>
  )
}
