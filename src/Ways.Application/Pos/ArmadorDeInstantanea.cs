using Ways.Application.Ofertas;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Combos;

namespace Ways.Application.Pos;

/// <summary>
/// Parte pura del armado de <see cref="InstantaneaDePos"/>: todo lo que no lee la base vive acá
/// para poder probarse sin ella. <see cref="ServicioDeInstantaneaDePos"/> hace las consultas y
/// delega en estas funciones.
/// </summary>
public static class ArmadorDeInstantanea
{
    /// <summary>Una lista de precios visible (no dada de baja) del tenant.</summary>
    public sealed record ListaVisible(int Id, int? IdEmpresa, bool Activo, ModoLista Modo, int? IdListaBase, decimal? Porcentaje);

    /// <summary>Un artículo activo, antes de resolverle precios.</summary>
    public sealed record ArticuloAResolver(int Id, string CodigoInterno, string Nombre, int IdAlicuotaIva, bool AcumulaEnVenta, UnidadVenta UnidadVenta, int? IdFamilia = null, TeclaAtajo? TeclaAtajo = null);

    /// <summary>
    /// Listas a resolver, ascendentes por id: las activas compartidas o de la empresa del punto de
    /// venta, más cualquier lista visible a la que apunte un cliente de la instantánea (así el
    /// Consumidor Final y un cliente con una lista inactiva siguen cotizando como online).
    ///
    /// Se descarta la derivada sin porcentaje o cuya base no es una lista fija visible: el lote de
    /// precios (<c>ServicioDePrecios.PreciosVigentesEnLoteAsync</c>) rechaza esos casos, y una sola
    /// lista mal configurada no puede dejar sin instantánea al punto de venta entero. Los clientes
    /// de esa lista quedan sin precio local y cotizan online.
    /// </summary>
    public static IReadOnlyList<int> ListasAResolver(
        IReadOnlyList<ListaVisible> visibles, int idEmpresa, IEnumerable<int?> idsListaDeClientes)
    {
        var porId = visibles.ToDictionary(l => l.Id);
        var referenciadas = idsListaDeClientes.OfType<int>().ToHashSet();

        return visibles
            .Where(l => (l.Activo && (l.IdEmpresa is null || l.IdEmpresa == idEmpresa)) || referenciadas.Contains(l.Id))
            .Where(l => l.Modo != ModoLista.Derivada
                || (l.Porcentaje is not null && l.IdListaBase is { } idBase && porId.TryGetValue(idBase, out var b) && b.Modo == ModoLista.Fija))
            .Select(l => l.Id)
            .Order()
            .ToList();
    }

    /// <summary>Las listas de <see cref="InstantaneaDePos.Listas"/>: las mismas <paramref name="idsLista"/> que se
    /// resolvieron, con su modo y porcentaje. Un porcentaje solo viaja en una derivada.</summary>
    public static IReadOnlyList<ListaDeInstantanea>? ArmarListas(
        IReadOnlyList<ListaVisible> visibles, IReadOnlyList<int> idsLista)
    {
        var porId = visibles.ToDictionary(l => l.Id);
        var listas = idsLista
            .Select(id => porId[id])
            .Select(l => new ListaDeInstantanea(l.Id, l.Modo, l.Modo == ModoLista.Derivada ? l.Porcentaje : null))
            .ToList();

        return listas.Count == 0 ? null : listas;
    }

    /// <summary>Un texto de contacto del cliente tal como viaja: sin espacios sobrantes y <c>null</c> si queda vacío, así la
    /// clave se omite del JSON y la regla del envío (domicilio no vacío) decide igual que el servidor.</summary>
    public static string? TextoOpcional(string? valor)
    {
        var limpio = valor?.Trim();
        return string.IsNullOrEmpty(limpio) ? null : limpio;
    }

    /// <summary>Proyecta la lista de un cliente: una FK a una lista dada de baja (fuera de
    /// <paramref name="idsListaVisibles"/>) se devuelve <c>null</c>, igual que una FK nula.</summary>
    public static int? ListaEfectiva(int idListaPrecio, IReadOnlySet<int> idsListaVisibles) =>
        idsListaVisibles.Contains(idListaPrecio) ? idListaPrecio : null;


    /// <summary>De las listas a resolver, solo la lista efectiva del Consumidor Final (vacío si no tiene
    /// una visible): el formato original no muestra ninguna otra.</summary>
    public static IReadOnlyList<int> SoloListaDelConsumidorFinal(
        IReadOnlyList<int> idsLista, IReadOnlyList<ClienteDeInstantanea> clientes)
    {
        var idListaConsumidorFinal = clientes.FirstOrDefault(c => c.EsConsumidorFinal)?.IdListaPrecio;
        return idsLista.Where(id => id == idListaConsumidorFinal).ToList();
    }

    /// <summary>
    /// Líneas de resolución en el orden que <see cref="ArmarArticulos"/> espera: artículo por
    /// artículo y, dentro de cada uno, lista por lista. Todas a cantidad 1 contra la empresa del
    /// punto de venta, en UNA sola llamada al motor (sus consultas no dependen de la cantidad de
    /// líneas).
    /// </summary>
    public static IReadOnlyList<LineaDeResolucion> LineasDeResolucion(
        IReadOnlyList<ArticuloAResolver> articulos, IReadOnlyList<int> idsLista, int idEmpresa)
    {
        var lineas = new List<LineaDeResolucion>(articulos.Count * idsLista.Count);
        foreach (var articulo in articulos)
        {
            foreach (var idLista in idsLista)
            {
                lineas.Add(new LineaDeResolucion(articulo.Id, idEmpresa, idLista, 1m));
            }
        }

        return lineas;
    }

    /// <summary>
    /// Arma los artículos a partir de la resolución de <see cref="LineasDeResolucion"/> (mismo
    /// orden). Una lista sin precio vigente para el artículo no genera entrada; un artículo sin
    /// precio en ninguna lista no viaja, salvo el que se vende por importe: ese viaja siempre y sin precios.
    /// </summary>
    public static IReadOnlyList<ArticuloDeInstantanea> ArmarArticulos(
        IReadOnlyList<ArticuloAResolver> articulos,
        IReadOnlyList<int> idsLista,
        IReadOnlyList<ResultadoDeResolucionConEscalones> resolucion,
        IReadOnlyDictionary<int, IReadOnlyList<string>> codigosPorArticulo,
        IReadOnlyDictionary<int, decimal> porcentajePorAlicuota)
    {
        if (resolucion.Count != articulos.Count * idsLista.Count)
        {
            throw new InvalidOperationException(
                $"La resolución trajo {resolucion.Count} líneas para {articulos.Count} artículos × {idsLista.Count} listas.");
        }

        var resultado = new List<ArticuloDeInstantanea>(articulos.Count);
        for (var i = 0; i < articulos.Count; i++)
        {
            var precios = new List<PrecioDeListaDeInstantanea>(idsLista.Count);
            var porImporte = articulos[i].UnidadVenta == UnidadVenta.Importe;

            // Un artículo por importe se vende por el monto que tipea el cajero: viaja sin precios aunque conserve
            // alguno de lista, que el dispositivo no debe usar.
            for (var j = 0; j < idsLista.Count && !porImporte; j++)
            {
                var linea = resolucion[(i * idsLista.Count) + j];
                if (linea.Resultado.PrecioOriginal is not { } precioOriginal)
                {
                    continue;
                }

                var plano = PrecioParaInstantanea.Separar(
                    precioOriginal, linea.Resultado.PrecioFinal ?? precioOriginal, linea.Resultado.DescuentoUnitario,
                    linea.Resultado.Aplicadas);

                precios.Add(new PrecioDeListaDeInstantanea(
                    idsLista[j],
                    precioOriginal,
                    plano.PrecioFinal,
                    plano.DescuentoUnitario,
                    plano.Aplicadas,
                    linea.Escalones.Count == 0 ? null : linea.Escalones,
                    linea.EscalonesDeFamilia is { Count: > 0 } ? linea.EscalonesDeFamilia : null,
                    plano.AplicadasCompletas));
            }

            if (precios.Count == 0 && !porImporte)
            {
                continue;
            }

            var articulo = articulos[i];
            resultado.Add(new ArticuloDeInstantanea(
                articulo.Id,
                articulo.CodigoInterno,
                articulo.Nombre,
                codigosPorArticulo.GetValueOrDefault(articulo.Id, []),
                articulo.IdAlicuotaIva,
                porcentajePorAlicuota[articulo.IdAlicuotaIva],
                articulo.AcumulaEnVenta,
                articulo.UnidadVenta,
                precios,
                articulo.IdFamilia,
                articulo.TeclaAtajo));
        }

        return resultado;
    }

    /// <summary>
    /// Los combos que el dispositivo puede completar sin red con los <paramref name="articulos"/> de esta misma
    /// instantánea. Un combo solo viaja ENTERO: si algún componente no se puede vender, el combo completo se descarta
    /// (uno parcial haría creer al dispositivo que puede armarlo). Un componente artículo es vendible si figura en la
    /// instantánea (activo, disponible a la venta y con precio en alguna lista; la instantánea ya dejó afuera los de uso
    /// interno, así que un combo con un componente que no se vende no viaja) y su unidad de venta entra a combos y
    /// admite la cantidad del componente (<see cref="ReglaDeCombos.AdmiteComoComponente"/>: entera por unidad, hasta
    /// tres decimales por peso), el mismo criterio con el que la venta online elige las líneas candidatas
    /// (<c>ResolvedorDeCombos</c>). Un artículo por importe figura en la instantánea sin precios y nunca es un
    /// componente vendible. Un componente familia es vendible si algún artículo de la instantánea tiene esa familia y
    /// una unidad de venta que admite la cantidad; una familia dada de baja deja de contar cuando sus artículos dejan
    /// de figurar. Una cantidad no positiva también descarta el combo. <c>null</c> si no queda ninguno, para que la
    /// clave se omita en el JSON.
    /// </summary>
    public static IReadOnlyList<ComboDeInstantanea>? ArmarCombos(
        IReadOnlyList<ComboCandidato> candidatos, IReadOnlyList<ArticuloDeInstantanea> articulos)
    {
        var unidadesPorArticulo = articulos.ToLookup(a => a.IdArticulo, a => a.UnidadVenta);
        var unidadesPorFamilia = articulos
            .Where(a => a.IdFamilia.HasValue)
            .ToLookup(a => a.IdFamilia!.Value, a => a.UnidadVenta);

        bool AlgunaAdmite(IEnumerable<UnidadVenta> unidades, decimal cantidad) =>
            unidades.Any(u => ReglaDeCombos.AdmiteComoComponente(u, cantidad));

        var combos = candidatos
            .Where(c => c.Componentes.Count > 0
                && c.Componentes.All(m =>
                    m.Cantidad > 0
                    && ((m.IdArticulo is { } idArticulo && AlgunaAdmite(unidadesPorArticulo[idArticulo], m.Cantidad))
                        || (m.IdFamilia is { } idFamilia && AlgunaAdmite(unidadesPorFamilia[idFamilia], m.Cantidad)))))
            .OrderBy(c => c.Id)
            .Select(c => new ComboDeInstantanea(
                c.Id,
                c.Codigo,
                c.Nombre,
                c.Precio,
                [.. c.Componentes
                    .OrderBy(m => m.IdArticulo is null)
                    .ThenBy(m => m.IdArticulo)
                    .ThenBy(m => m.IdFamilia)
                    .Select(m => new ComponenteDeComboDeInstantanea(m.IdArticulo, m.IdFamilia, m.Cantidad))]))
            .ToList();

        return combos.Count == 0 ? null : combos;
    }

    /// <summary>
    /// Formato original de la instantánea, derivado de la nueva: cada artículo con el precio plano
    /// de la lista del Consumidor Final, y solo los que tienen precio en esa lista — lo mismo que
    /// la instantánea congelaba antes de traer todas las listas. Sin lista efectiva del Consumidor
    /// Final no hay artículos que ofrecer.
    /// </summary>
    public static InstantaneaLegadaDePos ProyectarLegada(InstantaneaDePos instantanea)
    {
        var idListaConsumidorFinal = instantanea.Clientes.FirstOrDefault(c => c.EsConsumidorFinal)?.IdListaPrecio;

        var articulos = new List<ArticuloDeInstantaneaLegada>();
        foreach (var articulo in instantanea.Articulos)
        {
            var precio = articulo.PreciosPorLista.FirstOrDefault(p => p.IdListaPrecio == idListaConsumidorFinal);
            if (precio is null)
            {
                continue;
            }

            articulos.Add(new ArticuloDeInstantaneaLegada(
                articulo.IdArticulo,
                articulo.CodigoInterno,
                articulo.Nombre,
                articulo.CodigosBarra,
                precio.PrecioOriginal,
                precio.PrecioFinal,
                precio.DescuentoUnitario,
                precio.Aplicadas,
                articulo.IdAlicuotaIva,
                articulo.PorcentajeIva,
                precio.Escalones));
        }

        return new InstantaneaLegadaDePos(
            instantanea.Momento, instantanea.IdPuntoVenta, articulos, instantanea.MediosDePago, instantanea.ToleranciaPago);
    }
}
