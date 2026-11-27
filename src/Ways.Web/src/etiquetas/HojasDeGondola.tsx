import type { CSSProperties } from 'react'
import type { FilaDeEtiqueta } from '../api/tipos'
import { contenidoDeGondola, formatearPrecioDeGondola, partirPrecio } from './contenidoDeGondola'
import { dividirEnHojas, disposicionDeHoja, type DisposicionDeHoja, type EtiquetasPorHoja } from './disposicionDeHojas'
import { ajustarEtiqueta, ESCALA_DEL_DESCUENTO, ESCALA_DEL_SUPERINDICE, MODELOS_DE_GONDOLA, type Caja } from './modelosDeGondola'
import { RUTA_DEL_LOGO_DE_GONDOLA, type PlantillaDeGondola } from './plantillasDeGondola'
import '@fontsource-variable/bricolage-grotesque/standard.css'
import '@fontsource-variable/instrument-sans/standard.css'
import '../estilos/etiquetas-gondola.css'
import '../estilos/modelos-de-gondola.css'

function posicion(caja: Caja): CSSProperties {
  return { left: `${caja.x}mm`, top: `${caja.y}mm`, width: `${caja.ancho}mm`, height: `${caja.alto}mm` }
}

function PrecioPartido({ texto }: { texto: string }) {
  const partes = partirPrecio(texto)
  const chico = { fontSize: `${ESCALA_DEL_SUPERINDICE}em` }
  return (
    <>
      {partes.prefijo && (
        <span className="gondola-precio-prefijo" style={chico}>
          {partes.prefijo}
        </span>
      )}
      <span className="gondola-precio-entero">{partes.entero}</span>
      {partes.centavos !== null && (
        <span className="gondola-precio-centavos" style={chico}>
          {partes.centavos}
        </span>
      )}
    </>
  )
}

type PropsDeEtiqueta = {
  fila: FilaDeEtiqueta
  plantilla: PlantillaDeGondola
  disposicion: DisposicionDeHoja
  conOfertas: boolean
}

function EtiquetaDeGondola({ fila, plantilla, disposicion, conOfertas }: PropsDeEtiqueta) {
  const celda = { anchoMm: disposicion.celdaAnchoMm, altoMm: disposicion.celdaAltoMm }
  const modelo = MODELOS_DE_GONDOLA[plantilla.modelo]
  const contenido = contenidoDeGondola(fila, conOfertas)
  const precioNormal = formatearPrecioDeGondola(contenido.precio)
  // Con oferta por cantidad, la oferta va en grande y el precio normal en chico.
  const textoDelPrecio = contenido.promocion ?? precioNormal
  const tachado = contenido.precioTachado !== null ? formatearPrecioDeGondola(contenido.precioTachado) : null
  const lineaChica = contenido.promocion !== null ? precioNormal : tachado
  const ajuste = ajustarEtiqueta(modelo.id, celda, { nombre: fila.nombre, precio: textoDelPrecio, lineaChica, descuento: contenido.porcentaje })
  const { disenio } = ajuste
  const fuenteChica = ajuste.lineaChica ? `${ajuste.lineaChica.fuenteMm}mm` : undefined

  return (
    <article
      className={`gondola-etiqueta gondola-modelo gondola-modelo-${modelo.id} gondola-ancho-nombre-${modelo.anchoDelNombre} gondola-ancho-precio-${modelo.anchoDelPrecio}`}
      data-testid="gondola-etiqueta"
      data-modelo={modelo.id}
      style={{ '--g-subrayado': `${modelo.subrayadoMm}mm` } as CSSProperties}
    >
      <svg className="gondola-trazos" viewBox={`0 0 ${celda.anchoMm} ${celda.altoMm}`} style={{ width: `${celda.anchoMm}mm`, height: `${celda.altoMm}mm` }} aria-hidden="true">
        {disenio.trazos.map((trazo, indice) => (
          <path
            key={indice}
            d={trazo.d}
            fill="none"
            stroke={plantilla.colores[trazo.color]}
            strokeWidth={trazo.grosorMm}
            strokeDasharray={trazo.discontinuo ? `${trazo.grosorMm * 3} ${trazo.grosorMm * 2}` : undefined}
            strokeLinecap="round"
            strokeLinejoin="round"
          />
        ))}
      </svg>
      <img className="gondola-logo" src={RUTA_DEL_LOGO_DE_GONDOLA} alt="Ways" style={posicion(disenio.logo)} />
      <div className="gondola-caja-nombre" style={posicion(disenio.nombre)}>
        <h3 className="gondola-nombre" style={{ fontSize: `${ajuste.nombre.fuenteMm}mm` }}>
          {fila.nombre}
        </h3>
      </div>
      <div className="gondola-bloque-precio" style={{ ...posicion(disenio.precio), gap: `${ajuste.separacionMm}mm` }}>
        {tachado !== null && (
          <div className="gondola-linea-chica" style={{ fontSize: fuenteChica }}>
            <span className="gondola-precio-tachado" data-testid="gondola-precio-tachado">
              {tachado}
            </span>
            {contenido.porcentaje !== null && (
              <span className="gondola-descuento" data-testid="gondola-descuento" style={{ fontSize: `${ESCALA_DEL_DESCUENTO}em` }}>
                {contenido.porcentaje}
              </span>
            )}
          </div>
        )}
        <div
          className="gondola-precio"
          data-testid={contenido.promocion !== null ? 'gondola-promocion' : 'gondola-precio'}
          style={{ fontSize: `${ajuste.precio.fuenteMm}mm` }}
        >
          {modelo.precioPartido ? <PrecioPartido texto={textoDelPrecio} /> : textoDelPrecio}
        </div>
        {contenido.promocion !== null && (
          <div className="gondola-linea-chica gondola-precio-normal" data-testid="gondola-precio-normal" style={{ fontSize: fuenteChica }}>
            {precioNormal}
          </div>
        )}
      </div>
    </article>
  )
}

type Props = {
  filas: readonly FilaDeEtiqueta[]
  porHoja: EtiquetasPorHoja
  plantilla: PlantillaDeGondola
  conOfertas: boolean
}

/** Hojas A4 apaisadas de etiquetas: renderer puro, cada fila sale una vez y las hojas se parten con un salto de página. */
export function HojasDeGondola({ filas, porHoja, plantilla, conOfertas }: Props) {
  const disposicion = disposicionDeHoja(porHoja)
  const hojas = dividirEnHojas(filas, porHoja)
  const estilo = {
    '--et-texto': plantilla.colores.texto,
    '--et-precio': plantilla.colores.precio,
    '--et-acento': plantilla.colores.acento,
    '--et-borde': plantilla.colores.borde,
    '--et-columnas': disposicion.columnas,
    '--et-filas': disposicion.filas,
    '--et-celda-ancho': `${disposicion.celdaAnchoMm}mm`,
    '--et-celda-alto': `${disposicion.celdaAltoMm}mm`,
  } as CSSProperties

  return (
    <div className="gondola-hojas" style={estilo} data-plantilla={plantilla.id} data-por-hoja={porHoja}>
      {hojas.map((hoja, indice) => (
        <section key={indice} className="gondola-hoja" data-testid="gondola-hoja" aria-label={`Hoja ${indice + 1} de ${hojas.length}`}>
          {hoja.map((fila) => (
            <EtiquetaDeGondola key={fila.idArticulo} fila={fila} plantilla={plantilla} disposicion={disposicion} conOfertas={conOfertas} />
          ))}
        </section>
      ))}
    </div>
  )
}
