import type { CSSProperties } from 'react'
import type { FilaDeEtiqueta } from '../api/tipos'
import { presupuestoVertical } from './ajusteDeTexto'
import { contenidoDeGondola, formatearPrecioDeGondola } from './contenidoDeGondola'
import { dividirEnHojas, disposicionDeHoja, type DisposicionDeHoja, type EtiquetasPorHoja } from './disposicionDeHojas'
import { rutaDelLogoDeGondola, type PlantillaDeGondola } from './plantillasDeGondola'
import '../estilos/etiquetas-gondola.css'

type PropsDeEtiqueta = {
  fila: FilaDeEtiqueta
  plantilla: PlantillaDeGondola
  disposicion: DisposicionDeHoja
  conOfertas: boolean
}

function EtiquetaDeGondola({ fila, plantilla, disposicion, conOfertas }: PropsDeEtiqueta) {
  const celda = { anchoMm: disposicion.celdaAnchoMm, altoMm: disposicion.celdaAltoMm }
  const contenido = contenidoDeGondola(fila, conOfertas)
  const textoDelPrecio = formatearPrecioDeGondola(contenido.precio)
  const conOferta = contenido.precioTachado !== null || contenido.promocion !== null
  const medidas = presupuestoVertical(fila.nombre, textoDelPrecio, celda, conOferta)

  return (
    <article className={`gondola-etiqueta gondola-deco-${plantilla.decoracion}`} data-testid="gondola-etiqueta">
      <img className="gondola-logo" src={rutaDelLogoDeGondola(plantilla.tono)} alt="Ways" style={{ height: `${medidas.logoMm}mm` }} />
      <h3 className="gondola-nombre" style={{ fontSize: `${medidas.nombreMm}mm` }}>
        {fila.nombre}
      </h3>
      <div className="gondola-bloque-precio">
        {contenido.precioTachado !== null && (
          <div className="gondola-precio-tachado" data-testid="gondola-precio-tachado" style={{ fontSize: `${medidas.tachadoMm}mm` }}>
            {formatearPrecioDeGondola(contenido.precioTachado)}
          </div>
        )}
        <div className="gondola-precio" data-testid="gondola-precio" style={{ fontSize: `${medidas.precioMm}mm` }}>
          {textoDelPrecio}
        </div>
      </div>
      {contenido.porcentaje !== null && (
        <span className="gondola-descuento" data-testid="gondola-descuento">
          {contenido.porcentaje}
        </span>
      )}
      {contenido.promocion !== null && (
        <div className="gondola-promocion" data-testid="gondola-promocion" style={{ fontSize: `${medidas.promocionMm}mm` }}>
          {contenido.promocion}
        </div>
      )}
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
    '--et-fondo': plantilla.colores.fondo,
    '--et-texto': plantilla.colores.texto,
    '--et-precio': plantilla.colores.precio,
    '--et-acento': plantilla.colores.acento,
    '--et-sobre-acento': plantilla.colores.textoSobreAcento,
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
