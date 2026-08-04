using Ways.Domain.Common;

namespace Ways.Domain.Ofertas;

/// <summary>
/// Reglas de negocio puras, sin dependencias (design decisions 1, 2) — se testean sin base de
/// datos (mismo criterio que <see cref="Articulos.ReglaDeArticulos"/>/<see
/// cref="Catalogos.ReglaDeCategorias"/>). Todo camino de escritura de <see cref="Oferta"/>
/// (Slice 2: <c>ServicioDeOfertas</c>) tiene que pasar por acá antes de tocar la fila; las
/// cuatro CHECKs de esquema (<c>ck_ofertas_alcance_exclusivo</c>/
/// <c>ck_ofertas_beneficio_exclusivo</c>/<c>ck_ofertas_ventana_valida</c>/
/// <c>ck_ofertas_dias_semana</c>) son defensa en profundidad, alcanzables solo por una
/// escritura cruda/fuera de banda (design: Backstop Map, reachability note) — esta clase
/// pre-valida los cuatro invariantes.
/// </summary>
public static class ReglaDeOfertas
{
    /// <summary>Proyecta el alcance de <paramref name="oferta"/> (spec: Ofertas Schema At
    /// Rest, "Scope CHECK rejects zero or multiple scope columns" — acá es la validación de
    /// dominio que corre ANTES de que la CHECK sea siquiera alcanzable, spec: "Domain guard
    /// rejects invalid shapes before the database"). Total: siempre devuelve un valor o lanza,
    /// nunca deja pasar un estado ambiguo.</summary>
    public static AlcanceDeOferta LeerAlcance(Oferta oferta)
    {
        var cantidadDeColumnasSeteadas =
            (oferta.IdArticulo is not null ? 1 : 0) +
            (oferta.IdGrupo is not null ? 1 : 0) +
            (oferta.IdCategoria is not null ? 1 : 0);

        if (cantidadDeColumnasSeteadas != 1)
        {
            throw new ErrorDominio(
                "oferta_alcance_invalido",
                "La oferta tiene que apuntar a exactamente un artículo, grupo o categoría.",
                400);
        }

        return oferta.IdArticulo is { } idArticulo ? AlcanceDeOferta.DeArticulo(idArticulo)
            : oferta.IdGrupo is { } idGrupo ? AlcanceDeOferta.DeGrupo(idGrupo)
            : AlcanceDeOferta.DeCategoria(oferta.IdCategoria!.Value);
    }

    /// <summary>Proyecta el beneficio de <paramref name="oferta"/> (spec: "Benefit CHECK
    /// rejects zero or multiple benefit columns") y valida en el mismo paso el rango del valor
    /// seteado (design: Protection Rules — <c>porcentaje ∈ (0,100]</c>, <c>importe_fijo ≥
    /// 0</c>, <c>precio_unitario ≥ 0</c>): un beneficio con exclusividad correcta pero un valor
    /// sin sentido de negocio (0% de descuento, un importe negativo) no es una proyección
    /// válida tampoco.</summary>
    public static BeneficioDeOferta LeerBeneficio(Oferta oferta)
    {
        var cantidadDeColumnasSeteadas =
            (oferta.PrecioUnitario is not null ? 1 : 0) +
            (oferta.Porcentaje is not null ? 1 : 0) +
            (oferta.ImporteFijo is not null ? 1 : 0);

        if (cantidadDeColumnasSeteadas != 1)
        {
            throw new ErrorDominio(
                "oferta_beneficio_invalido",
                "La oferta tiene que definir exactamente un beneficio: precio unitario, porcentaje o importe fijo.",
                400);
        }

        if (oferta.PrecioUnitario is { } precioUnitario)
        {
            if (precioUnitario < 0)
            {
                throw new ErrorDominio(
                    "oferta_precio_unitario_invalido", "El precio unitario de la oferta no puede ser negativo.", 400);
            }

            return BeneficioDeOferta.DePrecioUnitario(precioUnitario);
        }

        if (oferta.Porcentaje is { } porcentaje)
        {
            if (porcentaje <= 0 || porcentaje > 100)
            {
                throw new ErrorDominio(
                    "oferta_porcentaje_invalido", "El porcentaje de la oferta tiene que estar entre 0 (exclusivo) y 100.", 400);
            }

            return BeneficioDeOferta.DePorcentaje(porcentaje);
        }

        var importeFijo = oferta.ImporteFijo!.Value;
        if (importeFijo < 0)
        {
            throw new ErrorDominio(
                "oferta_importe_fijo_invalido", "El importe fijo de la oferta no puede ser negativo.", 400);
        }

        return BeneficioDeOferta.DeImporteFijo(importeFijo);
    }

    /// <summary>Spec: cantidad_minima Trigger Semantics — <c>NULL</c> siempre matchea (oferta
    /// directa); seteada tiene que ser estrictamente positiva (design: Protection Rules,
    /// <c>cantidad_minima &gt; 0</c>) — un umbral cero o negativo no tiene sentido de
    /// negocio.</summary>
    public static void ValidarCantidadMinima(decimal? cantidadMinima)
    {
        if (cantidadMinima is <= 0)
        {
            throw new ErrorDominio(
                "cantidad_minima_invalida", "La cantidad mínima de la oferta tiene que ser mayor a cero.", 400);
        }
    }

    /// <summary>Spec: Vigencia Window Semantics — cada eje de vigencia (fecha/hora) es
    /// independientemente opcional (<c>NULL</c> = sin restricción en ese eje); seteados ambos
    /// extremos de un eje, el "hasta" tiene que ser mayor o igual al "desde" (inclusive, mismo
    /// criterio que <c>ck_ofertas_ventana_valida</c> — la CHECK usa <c>&gt;=</c>, no
    /// <c>&gt;</c>).</summary>
    public static void ValidarVentana(
        DateOnly? fechaDesde, DateOnly? fechaHasta, TimeOnly? horaDesde, TimeOnly? horaHasta)
    {
        if (fechaDesde is { } desde && fechaHasta is { } hasta && hasta < desde)
        {
            throw new ErrorDominio(
                "ventana_de_oferta_invalida",
                "La ventana de vigencia de la oferta es inválida.",
                400);
        }

        if (horaDesde is { } horaDesdeValor && horaHasta is { } horaHastaValor && horaHastaValor < horaDesdeValor)
        {
            throw new ErrorDominio(
                "ventana_de_oferta_invalida",
                "La ventana de vigencia de la oferta es inválida.",
                400);
        }
    }

    /// <summary>Spec: Vigencia Window Semantics ("dias_semana restricts to listed weekdays") —
    /// <c>NULL</c>/vacío = todos los días. Seteado, cada valor tiene que ser un día ISO-8601
    /// válido (1..7) sin duplicados (design: Protection Rules, mismo invariante que
    /// <c>ck_ofertas_dias_semana</c> del lado de esquema — mismo código de dominio
    /// <c>dias_semana_invalidos</c> que su backstop en <c>ManejadorDeErrores</c>, por ser la
    /// misma regla enforced dos veces).</summary>
    public static IReadOnlySet<int> LeerDiasSemana(short[]? diasSemana)
    {
        if (diasSemana is null || diasSemana.Length == 0)
        {
            return new HashSet<int>();
        }

        var conjunto = new HashSet<int>(diasSemana.Length);
        foreach (var dia in diasSemana)
        {
            if (dia is < 1 or > 7 || !conjunto.Add(dia))
            {
                throw new ErrorDominio(
                    "dias_semana_invalidos",
                    "Los días de semana de la oferta tienen que ser valores de 1 a 7 sin repetir.",
                    400);
            }
        }

        return conjunto;
    }

    /// <summary>Spec: resolucion-de-ofertas / Candidate Matching, "Empresa-scoped oferta
    /// excludes other empresas" — <c>NULL</c> ⇒ toda empresa del tenant (design decision 5);
    /// seteado, tiene que coincidir exactamente con la empresa de la línea consultada. Pura,
    /// sin depender de <see cref="LineaAResolver"/> (que no lleva <c>id_empresa</c>, design:
    /// Resolution Contract) — <c>ServicioDeOfertas.ResolverAsync</c> filtra por acá ANTES de
    /// armar la lista de candidatas de cada línea.</summary>
    public static bool CoincideEmpresa(int? idEmpresaOferta, int? idEmpresaLinea) =>
        idEmpresaOferta is null || idEmpresaOferta == idEmpresaLinea;
}
