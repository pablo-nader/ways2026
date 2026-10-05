import { useEffect, useState } from 'react'
import { clienteDeCatalogo, clienteDeCatalogosFiscales } from '../../api/catalogos'
import { api } from '../../api/cliente'
import type {
  AreaAlta,
  AreaListado,
  CategoriaAlta,
  CategoriaListado,
  GrupoAlta,
  GrupoListado,
  ListaPrecioAlta,
  ListaPrecioListado,
  MarcaAlta,
  MarcaListado,
  PaginaDe,
  ProveedorListado,
} from '../../api/tipos'
import { etiquetaDeProveedor } from '../articulos/helpers'
import { mapaDeNombres, nombresDeCatalogoVacios, type NombresDeCatalogo } from './valoresCompartidos'

const clienteAreas = clienteDeCatalogo<AreaListado, AreaAlta>('areas')
const clienteCategorias = clienteDeCatalogo<CategoriaListado, CategoriaAlta>('categorias')
const clienteGrupos = clienteDeCatalogo<GrupoListado, GrupoAlta>('grupos')
const clienteMarcas = clienteDeCatalogo<MarcaListado, MarcaAlta>('marcas')
const clienteListas = clienteDeCatalogo<ListaPrecioListado, ListaPrecioAlta>('listas-precio')

export type CatalogosDeFamilias = {
  nombres: NombresDeCatalogo
  marcas: ReadonlyMap<number, string>
  listas: ReadonlyMap<number, string>
  /** Los catálogos que no se pudieron cargar. Vacío cuando cargaron todos. */
  aviso: string
}

/**
 * Los nombres de las filas de catálogo que las pantallas de familias muestran en vez de un id: área, categoría,
 * grupo, proveedor habitual, alícuota de IVA, marca y lista de precios. Se piden todos juntos y con
 * `incluirInactivos`: un valor compartido puede apuntar a una fila desactivada, que sigue teniendo nombre. Cada
 * catálogo que no carga deja su mapa vacío y se avisa, y esos ids se muestran con su código (`nombreDeCatalogo`):
 * nunca se bloquea la pantalla por un nombre.
 */
export function useCatalogosDeFamilias(): CatalogosDeFamilias {
  const [nombres, setNombres] = useState<NombresDeCatalogo>(nombresDeCatalogoVacios)
  const [marcas, setMarcas] = useState<ReadonlyMap<number, string>>(() => new Map())
  const [listas, setListas] = useState<ReadonlyMap<number, string>>(() => new Map())
  const [fallidos, setFallidos] = useState<string[]>([])

  useEffect(() => {
    let cancelado = false

    function cargar<T>(catalogo: string, pedir: () => Promise<T>, aplicar: (resultado: T) => void) {
      pedir()
        .then((resultado) => {
          if (!cancelado) aplicar(resultado)
        })
        .catch(() => {
          if (!cancelado) setFallidos((previos) => [...previos, catalogo])
        })
    }

    cargar('áreas', () => clienteAreas.listar(true), (items) =>
      setNombres((previos) => ({ ...previos, areas: mapaDeNombres(items, (a) => a.nombre) })),
    )
    cargar('categorías', () => clienteCategorias.listar(true), (items) =>
      setNombres((previos) => ({ ...previos, categorias: mapaDeNombres(items, (c) => c.nombre) })),
    )
    cargar('grupos', () => clienteGrupos.listar(true), (items) =>
      setNombres((previos) => ({ ...previos, grupos: mapaDeNombres(items, (g) => g.nombre) })),
    )
    cargar('proveedores', () => api.get<PaginaDe<ProveedorListado>>('/proveedores?tamanio=200'), (pagina) =>
      setNombres((previos) => ({ ...previos, proveedores: mapaDeNombres(pagina.items, etiquetaDeProveedor) })),
    )
    cargar('alícuotas de IVA', () => clienteDeCatalogosFiscales.alicuotasIva(), (items) =>
      setNombres((previos) => ({ ...previos, alicuotas: mapaDeNombres(items, (a) => a.nombre) })),
    )
    cargar('marcas', () => clienteMarcas.listar(true), (items) => setMarcas(mapaDeNombres(items, (m) => m.nombre)))
    cargar('listas de precio', () => clienteListas.listar(true), (items) => setListas(mapaDeNombres(items, (l) => l.nombre)))

    return () => {
      cancelado = true
    }
  }, [])

  return {
    nombres,
    marcas,
    listas,
    aviso: fallidos.length === 0 ? '' : `No se pudieron cargar: ${fallidos.join(', ')}. Sus valores se muestran con el código en vez del nombre.`,
  }
}
