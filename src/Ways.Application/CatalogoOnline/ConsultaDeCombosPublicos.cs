using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Articulos;
using Ways.Domain.Dinero;

namespace Ways.Application.CatalogoOnline;

/// <summary>
/// Los combos de la tienda. Corre sobre el contexto del modo de acceso <c>publico</c> (ver
/// <see cref="ConsultaDeCatalogoPublico"/>, de quien toma la lista de la vitrina y la regla de precio vigente), así
/// que las policies de <c>combos</c> (disponible online y activo) y de <c>articulos</c> (disponible online y a la venta
/// y activo) ya dejaron afuera lo que la tienda no puede ver; las cláusulas de acá no las repiten, y un componente
/// que la policy esconde simplemente no aparece y descarta al combo entero.
///
/// <para>Esta clase decide lo que la policy no puede. Un combo se ofrece solo si es de todo el tenant o de la empresa
/// de la vitrina y TODOS sus componentes son artículos (los de familia piden elegir un miembro, algo que la tienda no
/// tiene todavía) que están disponibles para esa empresa, tienen un precio vigente en la lista de la tienda y se
/// venden por <see cref="UnidadVenta.Unidad"/>: es la única unidad que el motor de combos de la venta online acepta,
/// así que un combo con un componente pesable no se ofrece. El precio es el fijo del combo, sin pasar por la lista ni
/// por las ofertas, redondeado al peso entero: es el que cobra el motor de la venta (<c>199,4</c> se lista a <c>199</c>).
/// Un combo sin componentes tampoco figura. Ordenados por nombre.</para>
///
/// <para>Un combo solo se ofrece si ahorra: su precio redondeado al peso tiene que ser estrictamente menor que la suma
/// de los precios de tienda de sus componentes por su cantidad, como exige <see cref="Domain.Combos.ResolvedorDeCombos"/>
/// para aplicarlo (<c>cobro &gt;= costo con ofertas</c> lo descarta). El precio de tienda de un componente es el que
/// muestra el catálogo (lista de la vitrina con la oferta online aplicada, a cantidad 1). Divergencia chica y
/// conocida: el motor de la venta calcula las ofertas con la cantidad real del carrito, así que un combo que gana
/// contra el precio a cantidad 1 puede no ganar contra una oferta escalonada, y al revés.</para>
/// </summary>
public class ConsultaDeCombosPublicos(IWaysDbContext db, IRelojDelSistema reloj, ConsultaDeCatalogoPublico catalogo)
{
    /// <summary><c>null</c> cuando el tenant no tiene una vitrina publicada; vacía cuando la lista de la tienda no está
    /// bien configurada o ningún combo se puede ofrecer.</summary>
    public async Task<IReadOnlyList<ComboPublico>?> ObtenerAsync(CancellationToken ct = default)
    {
        if (await catalogo.EmpresaDeLaVitrinaAsync(ct) is not { } idEmpresa)
        {
            return null;
        }

        var lista = await ListaDeLaTiendaAsync(idEmpresa, ct);
        if (lista is null)
        {
            return [];
        }

        var combos = await db.Combos
            .AsNoTracking()
            .Where(c => c.IdEmpresa == null || c.IdEmpresa == idEmpresa)
            .Select(c => new { c.Id, c.Nombre, c.Precio })
            .ToListAsync(ct);

        if (combos.Count == 0)
        {
            return [];
        }

        var idsCombo = combos.Select(c => c.Id).ToList();
        var componentes = await db.CombosComponentes
            .AsNoTracking()
            .Where(m => idsCombo.Contains(m.IdCombo))
            .OrderBy(m => m.Id)
            .Select(m => new { m.IdCombo, m.IdArticulo, m.Cantidad })
            .ToListAsync(ct);

        var idsArticulo = componentes.Where(m => m.IdArticulo != null).Select(m => m.IdArticulo!.Value).Distinct().ToList();
        var ahora = reloj.Ahora;
        var idsLista = lista.IdListaConPrecios;

        var articulos = (await db.Articulos
                .AsNoTracking()
                .Where(a => idsArticulo.Contains(a.Id) && a.UnidadVenta == UnidadVenta.Unidad)
                .Where(a => a.DisponibleParaTodas
                    || db.ArticulosEmpresas.Any(ae => ae.IdArticulo == a.Id && ae.IdEmpresa == idEmpresa))
                .Where(a => db.Precios.Where(ConsultaDeCatalogoPublico.PrecioVigenteEn(ahora))
                    .Any(p => p.IdArticulo == a.Id && p.IdListaPrecio == idsLista))
                .Select(a => new
                {
                    a.Id,
                    a.Nombre,
                    a.IdCategoria,
                    a.IdGrupo,
                    a.IdFamilia,
                    ImagenActualizadaEn = db.ImagenesArticulo
                        .Where(i => i.IdArticulo == a.Id)
                        .Select(i => (DateTimeOffset?)i.ActualizadoEn)
                        .FirstOrDefault()
                })
                .ToListAsync(ct))
            .ToDictionary(a => a.Id);

        var precios = await catalogo.PreciosFinalesAsync(
            [.. articulos.Values.Select(a => new ConsultaDeCatalogoPublico.DimensionesDeArticulo(a.Id, a.IdCategoria, a.IdGrupo, a.IdFamilia))],
            lista, idEmpresa, ahora, ct);

        var componentesPorCombo = componentes.ToLookup(m => m.IdCombo);

        return
        [
            .. combos
                .Select(c => (Combo: c, Componentes: componentesPorCombo[c.Id].ToList()))
                // Un componente familia tiene IdArticulo nulo (CHECK de exclusividad), así que no figura en el diccionario.
                .Where(x => x.Componentes.Count > 0
                    && x.Componentes.All(m => m.IdArticulo is { } id && articulos.ContainsKey(id))
                    && Ahorra(x.Combo.Precio, x.Componentes.Select(m => (m.IdArticulo!.Value, m.Cantidad)), precios))
                .OrderBy(x => x.Combo.Nombre, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Combo.Id)
                .Select(x => new ComboPublico(
                    x.Combo.Id,
                    x.Combo.Nombre,
                    RedondeoDePeso.APesoEntero(x.Combo.Precio),
                    [
                        .. x.Componentes.Select(m =>
                        {
                            var articulo = articulos[m.IdArticulo!.Value];

                            return new ComponenteDeComboPublico(
                                articulo.Id,
                                articulo.Nombre,
                                m.Cantidad,
                                articulo.ImagenActualizadaEn is { } actualizada ? ImagenDeArticulo.Version(actualizada) : null);
                        })
                    ]))
        ];
    }

    /// <summary>La condición de <see cref="Domain.Combos.ResolvedorDeCombos"/>: lo que cobra el combo (redondeado al peso)
    /// es menor que lo que costarían sus componentes sueltos. Un componente sin precio de tienda descarta al combo.</summary>
    private static bool Ahorra(
        decimal precioDelCombo, IEnumerable<(int IdArticulo, decimal Cantidad)> componentes,
        IReadOnlyDictionary<int, decimal> precios)
    {
        var costo = 0m;
        foreach (var (idArticulo, cantidad) in componentes)
        {
            if (!precios.TryGetValue(idArticulo, out var precio))
            {
                return false;
            }

            costo += precio * cantidad;
        }

        return RedondeoDePeso.APesoEntero(precioDelCombo) < costo;
    }

    private async Task<ConsultaDeCatalogoPublico.ListaDePrecios?> ListaDeLaTiendaAsync(int idEmpresa, CancellationToken ct)
    {
        var vitrina = await db.Vitrinas.AsNoTracking().Select(v => new { v.IdListaPrecio }).FirstOrDefaultAsync(ct);
        if (vitrina is null)
        {
            return null;
        }

        return await catalogo.ResolverListaAsync(idEmpresa, vitrina.IdListaPrecio, ct);
    }
}
