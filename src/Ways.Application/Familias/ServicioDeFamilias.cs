using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Precios;

namespace Ways.Application.Familias;

/// <summary>
/// Gestión de familias de artículos (doc 10 §3, "Familias de artículos"): por ahora, las lecturas —listar las
/// familias y leer una—. Autorización: <c>Politicas.GestionDeCatalogo</c> aplicada en la capa de API, la misma
/// puerta que el alta y la edición de artículos.
///
/// <para>Las lecturas no toman locks: la familia, sus miembros y los precios de la referencia salen de consultas
/// separadas, así que una escritura concurrente puede mostrarlos en momentos distintos. Lo que sostiene la
/// invariante de la familia son los escritores, no estas lecturas.</para>
/// </summary>
public class ServicioDeFamilias(IWaysDbContext db, IRelojDelSistema reloj)
{
    /// <summary>Las familias vivas del tenant, ordenadas por nombre. Cada una con la cantidad de miembros vivos:
    /// el filtro de baja lógica de <c>articulos</c> deja afuera a los dados de baja.</summary>
    public async Task<IReadOnlyList<FamiliaListado>> ListarAsync(CancellationToken ct = default) =>
        await db.Familias
            .OrderBy(f => f.Nombre)
            .Select(f => new FamiliaListado(f.Id, f.Nombre, f.Activo, db.Articulos.Count(a => a.IdFamilia == f.Id)))
            .ToListAsync(ct);

    /// <summary>La familia, sus miembros vivos, los trece valores compartidos del artículo de referencia (el
    /// miembro vivo de menor id) y su estado de precios en cada lista fija: lo que el alta de un artículo dentro
    /// de la familia necesita prellenar. <c>404</c> si la familia no existe, está dada de baja o es de otro tenant.</summary>
    public async Task<FamiliaDetalle> ObtenerAsync(int id, CancellationToken ct = default)
    {
        var familia = await db.Familias.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la familia {id}.");

        var articulos = await db.Articulos.AsNoTracking().Where(a => a.IdFamilia == id).OrderBy(a => a.Id).ToListAsync(ct);

        if (articulos.Count == 0)
        {
            return new FamiliaDetalle(familia.Id, familia.Nombre, familia.Activo, [], Valores: null, Precios: []);
        }

        var referencia = articulos[0];

        var idsDeMarca = articulos.Where(a => a.IdMarca is not null).Select(a => a.IdMarca!.Value).Distinct().ToList();
        var marcasVisibles = await db.Marcas
            .Where(m => idsDeMarca.Contains(m.Id))
            .Select(m => m.Id)
            .ToHashSetAsync(ct);

        var miembros = articulos
            .Select(a => new MiembroDeFamilia(
                a.Id, a.CodigoInterno, a.Nombre,
                a.IdMarca is { } idMarca && marcasVisibles.Contains(idMarca) ? idMarca : null,
                a.Activo))
            .ToList();

        var valores = new ValoresCompartidosDeLaFamilia(
            IdArea: await db.Areas.AnyAsync(x => x.Id == referencia.IdArea, ct) ? referencia.IdArea : null,
            IdCategoria: referencia.IdCategoria is { } idCategoria
                && await db.Categorias.AnyAsync(x => x.Id == idCategoria, ct) ? idCategoria : null,
            IdGrupo: referencia.IdGrupo is { } idGrupo
                && await db.Grupos.AnyAsync(x => x.Id == idGrupo, ct) ? idGrupo : null,
            IdProveedorHabitual: referencia.IdProveedorHabitual is { } idProveedor
                && await db.Proveedores.AnyAsync(x => x.Id == idProveedor, ct) ? idProveedor : null,
            IdAlicuotaIva: referencia.IdAlicuotaIva,
            UnidadVenta: referencia.UnidadVenta,
            UnidadesPorBulto: referencia.UnidadesPorBulto,
            EsProducto: referencia.EsProducto,
            ControlaLote: referencia.ControlaLote,
            AcumulaEnVenta: referencia.AcumulaEnVenta,
            CostoLista: referencia.CostoLista,
            DescuentoProveedor: referencia.DescuentoProveedor,
            CostoNominal: referencia.CostoNominal);

        return new FamiliaDetalle(
            familia.Id, familia.Nombre, familia.Activo, miembros, valores, await EstadoDePreciosDeLaReferenciaAsync(referencia.Id, ct));
    }

    private async Task<IReadOnlyList<EstadoDePreciosDeLista>> EstadoDePreciosDeLaReferenciaAsync(
        int idArticuloReferencia, CancellationToken ct)
    {
        var ahora = reloj.Ahora;

        var listas = await db.ListasPrecio
            .Where(l => l.Modo == ModoLista.Fija)
            .OrderBy(l => l.Id)
            .Select(l => l.Id)
            .ToListAsync(ct);

        var filas = await db.Precios
            .AsNoTracking()
            .Where(p => p.IdArticulo == idArticuloReferencia)
            .Select(p => new { p.IdListaPrecio, p.Monto, p.VigenteDesde, p.VigenteHasta })
            .ToListAsync(ct);

        var filasPorLista = filas.ToLookup(f => f.IdListaPrecio);

        return
        [
            .. listas.Select(idLista => new EstadoDePreciosDeLista(
                idLista,
                EstadoDePrecios.De(
                    [.. filasPorLista[idLista].Select(f => new FilaDePrecio(f.Monto, f.VigenteDesde, f.VigenteHasta))],
                    ahora)))
        ];
    }
}
