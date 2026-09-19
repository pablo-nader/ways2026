/**
 * Helpers puros del formulario de artículos y sus altas rápidas de padrones (Categoría, Marca,
 * Grupo, Proveedor habitual) — separados de `Articulos.tsx` para no engordar más un archivo que
 * ya es el más pesado de la web, y para poder testearlos sin montar la pantalla completa
 * (web-descriptor-tests).
 */
import type { AlicuotaIvaListado, ProveedorListado } from '../../api/tipos'

const PORCENTAJE_ALICUOTA_POR_DEFECTO = 21

/**
 * Alícuota que arranca seleccionada en un artículo NUEVO. El servidor devuelve
 * `GET /catalogos-fiscales/alicuotas-iva` ordenado por porcentaje DESCENDENTE
 * (`ServicioDeCatalogosFiscales`), así que tomar `alicuotasIva[0]` siempre elegía la más alta
 * (27%) en vez de la general (21%) — acá se busca el 21% explícitamente, sin asumir ninguna
 * posición particular en el arreglo, y solo cae a la primera si esa alícuota no existe en el
 * tenant.
 */
export function elegirAlicuotaPorDefecto(alicuotas: AlicuotaIvaListado[]): number | '' {
  const general = alicuotas.find((a) => a.porcentaje === PORCENTAJE_ALICUOTA_POR_DEFECTO)
  return general?.id ?? alicuotas[0]?.id ?? ''
}

/**
 * Etiqueta de un proveedor en los selectores del artículo: el nombre de fantasía (recortado) si
 * hay uno usable, la razón social si no — nunca la combinación "razonSocial (nombreFantasia)" que
 * mostraba el select antes de esta etapa.
 */
export function etiquetaDeProveedor(proveedor: Pick<ProveedorListado, 'razonSocial' | 'nombreFantasia'>): string {
  const fantasia = proveedor.nombreFantasia?.trim()
  return fantasia ? fantasia : proveedor.razonSocial
}

/** Proveedores ordenados alfabéticamente por la MISMA etiqueta que se muestra en el select — nunca
 * por `razonSocial` cruda, que dejaría el orden visual desalineado con lo que se lee en pantalla.
 * Genérico sobre cualquier shape que tenga `razonSocial`/`nombreFantasia` (JD-A1: además del
 * `ProveedorListado` completo del ABM, el selector de `GastosDelTurno` usa la proyección mínima
 * `OpcionDeProveedor` de `GET /api/proveedores/opciones`) — preserva el tipo de entrada en el de
 * salida, sin angostarlo a `ProveedorListado`. */
export function ordenarProveedoresPorEtiqueta<T extends Pick<ProveedorListado, 'razonSocial' | 'nombreFantasia'>>(
  items: T[],
): T[] {
  return [...items].sort((a, b) =>
    etiquetaDeProveedor(a).localeCompare(etiquetaDeProveedor(b), 'es', { sensitivity: 'base' }),
  )
}

/**
 * Inserta un item nuevo en una lista ya ordenada alfabéticamente por `clave`, preservando ese
 * orden — el alta rápida de un padrón (categoría/marca/grupo/proveedor) nunca debe desordenar el
 * select agregando el item creado al final.
 */
export function insertarOrdenadoPor<T>(lista: T[], nuevo: T, clave: (item: T) => string): T[] {
  const etiquetaNueva = clave(nuevo)
  const indice = lista.findIndex((item) => clave(item).localeCompare(etiquetaNueva, 'es', { sensitivity: 'base' }) > 0)
  return indice === -1 ? [...lista, nuevo] : [...lista.slice(0, indice), nuevo, ...lista.slice(indice)]
}

/**
 * Opciones de un select de catálogo/proveedor en el formulario de artículo
 * (fix/articulos-form-catalogos-inactivos): en alta (`idActual` en `''`), solo las activas; en
 * edición, además la que tiene actualmente el artículo aunque esté inactiva — nunca otra inactiva.
 * Este helper solo arma OPCIONES para el render, nunca toca el valor del formulario: un
 * `idActual` que no matchea ningún item del listado del CLIENTE (que puede estar cargando, haber
 * fallado en silencio o venir truncada — nunca es la autoridad, ver `abrirEdicion`) simplemente no
 * suma una opción fantasma para él — el valor real sigue viajando intacto al guardar, y es el
 * servidor quien lo acepta o lo rechaza con 400 `referencia_invalida`.
 */
export function opcionesConValorActual<T extends { id: number; activo: boolean }>(
  listado: T[],
  idActual: number | '',
): T[] {
  return listado.filter((item) => item.activo || item.id === idActual)
}
