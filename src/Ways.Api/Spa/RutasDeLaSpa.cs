using System.Collections.Frozen;

namespace Ways.Api.Spa;

/// <summary>
/// Cómo se reparte el front que sirve la API: <c>/admin</c> es el back office y el POS web
/// (<c>admin.html</c>), <c>/online</c> la tienda (<c>online.html</c>) y todo lo demás la landing del
/// comercio (<c>index.html</c>). <c>pos.html</c> es un archivo estático más y no pasa por acá.
/// </summary>
public static class RutasDeLaSpa
{
    public const string PrefijoAdmin = "/admin";
    public const string PrefijoOnline = "/online";

    public const string EntradaDeAdmin = "admin.html";
    public const string EntradaDeOnline = "online.html";
    public const string EntradaDeLaLanding = "index.html";

    /// <summary>
    /// Primer segmento de cada ruta de nivel superior que el back office tenía antes de vivir bajo
    /// <c>/admin</c> (más <c>login</c> y <c>descargar</c>, que son públicas). La fuente de verdad es
    /// <c>src/Ways.Web/src/App.tsx</c>: <c>RutasDeLaSpaTests</c> falla si esta lista se desvía de él. Orden
    /// ordinal y sin repetidos.
    /// </summary>
    public static readonly IReadOnlyList<string> RutasHeredadasDelBackOffice =
    [
        "articulos",
        "auditoria",
        "caja",
        "catalogos",
        "catalogos-fiscales",
        "clientes",
        "compras",
        "consulta-precios",
        "descargar",
        "familias",
        "gastos",
        "listas-precio",
        "login",
        "ofertas",
        "ordenes-compra",
        "organizacion",
        "parametros",
        "pos",
        "presupuestos",
        "proveedores",
        "punto-de-venta",
        "remitos",
        "reportes",
        "stock",
        "tablero",
        "usuarios"
    ];

    private static readonly FrozenSet<string> Heredadas =
        RutasHeredadasDelBackOffice.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary><c>true</c> si el primer segmento de la ruta es uno de <see cref="RutasHeredadasDelBackOffice"/>.</summary>
    public static bool EsRutaHeredada(PathString ruta)
    {
        var valor = ruta.Value;

        if (string.IsNullOrEmpty(valor) || valor[0] != '/')
        {
            return false;
        }

        var fin = valor.IndexOf('/', 1);
        var segmento = fin < 0 ? valor.AsSpan(1) : valor.AsSpan(1, fin - 1);

        return Heredadas.GetAlternateLookup<ReadOnlySpan<char>>().Contains(segmento);
    }

    /// <summary>El archivo de <c>wwwroot</c> que resuelve el router de React para esa ruta.</summary>
    public static string EntradaDe(PathString ruta)
    {
        if (ruta.StartsWithSegments(PrefijoAdmin))
        {
            return EntradaDeAdmin;
        }

        return ruta.StartsWithSegments(PrefijoOnline) ? EntradaDeOnline : EntradaDeLaLanding;
    }
}
