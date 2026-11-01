import { api } from '../../api/cliente'
import { clienteDeFamilias } from '../../api/familias'
import { clienteDeOrganizacion } from '../../api/organizacion'
import { clienteDePrecios } from '../../api/precios'
import type {
  CategoriaListado,
  EmpresaListado,
  FamiliaListado,
  GrupoListado,
  ListaPrecioListado,
} from '../../api/tipos'

export type CatalogosDeOferta = {
  grupos: GrupoListado[]
  categorias: CategoriaListado[]
  familias: FamiliaListado[]
  empresas: EmpresaListado[]
  listasPrecio: ListaPrecioListado[]
  /** Un catálogo que no cargó queda vacío y deja su aviso; la oferta se puede abrir igual. */
  avisos: string[]
}

async function oVacio<T>(lectura: Promise<T[]>, aviso: string, avisos: string[]): Promise<T[]> {
  try {
    return await lectura
  } catch {
    avisos.push(aviso)
    return []
  }
}

/** Catálogos que el modal de oferta necesita para sus selectores de alcance, empresa y listas. */
export async function cargarCatalogosDeOferta(): Promise<CatalogosDeOferta> {
  const avisos: string[] = []
  const [grupos, categorias, familias, empresas, listasPrecio] = await Promise.all([
    oVacio(api.get<GrupoListado[]>('/catalogos/grupos'), 'No se pudieron cargar los grupos.', avisos),
    oVacio(api.get<CategoriaListado[]>('/catalogos/categorias'), 'No se pudieron cargar las categorías.', avisos),
    oVacio(clienteDeFamilias.listar(), 'No se pudieron cargar las familias.', avisos),
    oVacio(clienteDeOrganizacion.listarEmpresas(), 'No se pudieron cargar las empresas.', avisos),
    oVacio(clienteDePrecios.listasDePrecio(), 'No se pudieron cargar las listas de precio.', avisos),
  ])

  return { grupos, categorias, familias, empresas, listasPrecio, avisos }
}
