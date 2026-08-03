/**
 * Cliente de precios (stage-3-articulos-y-precios): motor de historial, nunca expone un
 * "editar" — solo abrir una fila nueva (`establecer`/`programar`), igual que el backend
 * (`ServicioDePrecios`, design decision 3). También expone `listasDePrecio` (referencia con
 * `modo`, `/api/catalogos/listas-precio`): el editor de precios del artículo necesita saber si
 * una lista es `Fija` (admite alta propia) o `Derivada` (solo lectura, se resuelve sola) — el
 * ABM propio de `listas_precio` llega recién en la Slice 6.
 */
import { api } from './cliente'
import type { AltaPrecio, HistorialDePrecio, ListaPrecioListado, PrecioVigente, ProgramarPrecio } from './tipos'

export const clienteDePrecios = {
  vigentes: (idArticulo: number) => api.get<PrecioVigente[]>(`/articulos/${idArticulo}/precios`),
  establecer: (idArticulo: number, datos: AltaPrecio) =>
    api.post<PrecioVigente>(`/articulos/${idArticulo}/precios`, datos),
  programar: (idArticulo: number, datos: ProgramarPrecio) =>
    api.post<PrecioVigente>(`/articulos/${idArticulo}/precios/programados`, datos),
  historial: (idArticulo: number, idListaPrecio: number) =>
    api.get<HistorialDePrecio[]>(`/articulos/${idArticulo}/precios/${idListaPrecio}/historial`),
  listasDePrecio: () => api.get<ListaPrecioListado[]>('/catalogos/listas-precio'),
}
