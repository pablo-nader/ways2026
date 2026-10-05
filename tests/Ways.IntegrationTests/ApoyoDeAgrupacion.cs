using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ways.Application.Familias;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Precios;
using Ways.Infrastructure.Persistencia;
using static Ways.IntegrationTests.ApoyoDeFamilias;

namespace Ways.IntegrationTests;

/// <summary>
/// Siembra, pedidos y lecturas compartidos por las pruebas de agrupar artículos en una familia (crear una familia,
/// agregarle artículos): el escenario común, los pedidos HTTP y la "foto" de la base que permite afirmar que un rechazo
/// no escribió nada.
///
/// <para>El escenario tiene un artículo de referencia con precios en dos listas fijas, tres destinos —uno con dos tipos
/// de diferencia en campos y en precios, uno ya idéntico y uno con muchas diferencias y un precio programado propio—, un
/// artículo suelto que ningún pedido toca, uno dado de baja, uno que pertenece a otra familia y, además de las dos
/// listas con precios, una lista fija sin precios y una derivada. Los ids de los artículos son explícitos y ascendentes
/// en el orden de <see cref="Escenario"/>: la referencia NO es el de menor id, así que elegir el menor entre los
/// involucrados es observable. Con <c>enFamilia</c> la referencia y su gemelo (idénticos en todo) son los miembros vivos
/// de una familia que además tiene un miembro dado de baja con el id MÁS bajo de todos y valores y precios distintos.</para>
/// </summary>
internal static class ApoyoDeAgrupacion
{
    /// <summary>Ids de artículo explícitos en una zona que el identity no alcanza: la clave primaria es global a todos
    /// los tenants de la base. Los ids de dos tenants sembrados por la misma prueba no se pisan.</summary>
    private static int siguienteIdDeArticulo = 6_500_000;

    public static int[] IdsAscendentes(int cantidad)
    {
        var primero = Interlocked.Add(ref siguienteIdDeArticulo, cantidad + 1) - cantidad;

        return [.. Enumerable.Range(primero, cantidad)];
    }

    /// <summary><c>timestamptz</c> guarda microsegundos y <see cref="DateTimeOffset"/> tiene 100 ns de resolución.</summary>
    public static DateTimeOffset AMicrosegundos(DateTimeOffset instante) =>
        new(instante.Ticks - (instante.Ticks % (TimeSpan.TicksPerMillisecond / 1000)), instante.Offset);

    /// <param name="DeBaja">Dado de baja; en un escenario con familia, su miembro dado de baja, de id más bajo.</param>
    /// <param name="D1">Dos campos de diferencia, un vigente propio en la general y nada en la mayorista.</param>
    /// <param name="D2">Idéntico a la referencia en todo.</param>
    /// <param name="Referencia">Con precios: general vigente 100 y pendiente 130 desde <c>V</c>; mayorista vigente 80.</param>
    /// <param name="Gemelo">Idéntico a la referencia en valores y precios.</param>
    /// <param name="D3">Muchas diferencias, nada en la general y un vigente con pendiente propio en la mayorista.</param>
    /// <param name="Suelto">Ningún pedido lo incluye.</param>
    /// <param name="DeOtraFamilia">Miembro vivo de <paramref name="FamiliaOtra"/>.</param>
    /// <param name="Familia">La familia de la referencia y el gemelo, o <c>null</c> en un escenario sin familia.</param>
    public sealed record Escenario(
        Entorno E, int DeBaja, int D1, int D2, int Referencia, int Gemelo, int D3, int Suelto, int DeOtraFamilia,
        int FamiliaOtra, int? Familia, int Minorista, int Derivada, DateTimeOffset V, DateTimeOffset W)
    {
        public ValoresCompartidosDeFamilia ValoresDeLaReferencia => ApoyoDeAgrupacion.ValoresDeLaReferencia(E);

        public int[] ListasFijas => [E.IdListaGeneral, E.IdListaMayorista, Minorista];

        public int[] TodosLosArticulos =>
            [DeBaja, D1, D2, Referencia, Gemelo, D3, Suelto, DeOtraFamilia];
    }

    public static ValoresCompartidosDeFamilia ValoresDeLaReferencia(Entorno e) =>
        ValoresBase(e) with
        {
            IdCategoria = e.Categorias[1], UnidadVenta = UnidadVenta.Peso, CostoLista = 77m, DescuentoProveedor = 5m
        };

    /// <summary>Campos de <see cref="Escenario.D1"/> que difieren de la referencia, en el orden del record.</summary>
    public static readonly string[] CamposDeD1 = ["id_categoria", "unidad_venta", "costo_lista", "descuento_proveedor"];

    /// <summary>Campos de <see cref="Escenario.D3"/> que difieren de la referencia, en el orden del record.</summary>
    public static readonly string[] CamposDeD3 =
    [
        "id_area", "id_categoria", "id_alicuota_iva", "unidad_venta", "es_producto", "acumula_en_venta", "costo_lista",
        "descuento_proveedor"
    ];

    public static async Task<Escenario> SembrarAsync(ApoyoDeFamilias apoyo, string nombre, bool enFamilia = false)
    {
        var e = await apoyo.PrepararAsync(nombre);
        var minorista = await apoyo.SembrarListaFijaAsync(e, "Minorista");
        var derivada = await apoyo.SembrarListaDerivadaAsync(e, "Derivada", e.IdListaGeneral);
        var familiaOtra = await apoyo.SembrarFamiliaAsync(e, "Otra familia");
        int? familia = enFamilia ? await apoyo.SembrarFamiliaAsync(e, "Gaseosas") : null;

        var @base = ValoresBase(e);
        var deLaReferencia = ValoresDeLaReferencia(e);
        var ids = IdsAscendentes(8);
        var (deBaja, d1, d2, referencia, gemelo, d3, suelto, deOtraFamilia) =
            (ids[0], ids[1], ids[2], ids[3], ids[4], ids[5], ids[6], ids[7]);

        await apoyo.SembrarArticuloAsync(
            e, "de-baja", @base with { CostoLista = 999m }, familia, dadoDeBaja: true, id: deBaja);
        await apoyo.SembrarArticuloAsync(
            e, "d1", @base, idMarca: e.Marcas[0], descripcion: "primero", activo: false, disponibleParaTodas: false, id: d1);
        await apoyo.SembrarArticuloAsync(e, "d2", deLaReferencia, idMarca: e.Marcas[1], descripcion: "segundo", id: d2);
        await apoyo.SembrarArticuloAsync(
            e, "ref", deLaReferencia, familia, idMarca: e.Marcas[0], descripcion: "referencia", id: referencia);
        await apoyo.SembrarArticuloAsync(e, "gemelo", deLaReferencia, familia, descripcion: "gemelo", id: gemelo);
        await apoyo.SembrarArticuloAsync(
            e, "d3",
            @base with { IdArea = e.Areas[1], IdAlicuotaIva = e.Alicuotas[1], EsProducto = false, AcumulaEnVenta = false },
            id: d3);
        await apoyo.SembrarArticuloAsync(e, "suelto", @base with { CostoLista = 11m }, id: suelto);
        await apoyo.SembrarArticuloAsync(e, "de-otra", @base, familiaOtra, id: deOtraFamilia);

        var v = DateTimeOffset.UtcNow.AddDays(3);
        var w = DateTimeOffset.UtcNow.AddDays(5);

        foreach (var conElEstadoDeLaReferencia in new[] { referencia, gemelo, d2 })
        {
            await apoyo.SembrarPrecioPendienteAsync(e, conElEstadoDeLaReferencia, e.IdListaGeneral, 100m, 130m, v);
            await apoyo.SembrarPrecioVigenteAsync(e, conElEstadoDeLaReferencia, e.IdListaMayorista, 80m);
        }

        await apoyo.SembrarPrecioVigenteAsync(e, d1, e.IdListaGeneral, 90m);
        await apoyo.SembrarPrecioPendienteAsync(e, d3, e.IdListaMayorista, 70m, 85m, w);
        await apoyo.SembrarPrecioVigenteAsync(e, suelto, e.IdListaGeneral, 55m);
        await apoyo.SembrarPrecioVigenteAsync(e, deBaja, e.IdListaGeneral, 999m);

        return new Escenario(
            e, deBaja, d1, d2, referencia, gemelo, d3, suelto, deOtraFamilia, familiaOtra, familia, minorista, derivada,
            AMicrosegundos(v), AMicrosegundos(w));
    }

    // =================================================================================================
    // Pedidos
    // =================================================================================================

    public static Task<HttpResponseMessage> PostCrearAsync(
        HttpClient cliente, string? nombre, int idReferencia, params int[]? ids) =>
        cliente.PostAsJsonAsync("/api/familias", new AltaDeFamilia(nombre, idReferencia, ids), OpcionesJson);

    public static Task<HttpResponseMessage> PostAgregarAsync(HttpClient cliente, int idFamilia, params int[]? ids) =>
        cliente.PostAsJsonAsync($"/api/familias/{idFamilia}/articulos", new AgregadoDeArticulos(ids), OpcionesJson);

    public static Task<HttpResponseMessage> PostPrevisualizarAsync(HttpClient cliente, int idReferencia, params int[]? ids) =>
        cliente.PostAsJsonAsync(
            "/api/familias/previsualizacion", new SolicitudDePrevisualizacion(idReferencia, ids), OpcionesJson);

    public static async Task<ResultadoDeAgrupacion> LeerResultadoAsync(HttpResponseMessage respuesta, HttpStatusCode esperado)
    {
        Assert.True(
            respuesta.StatusCode == esperado,
            $"Esperaba {(int)esperado} y recibió {(int)respuesta.StatusCode}: {await respuesta.Content.ReadAsStringAsync()}");

        return (await respuesta.Content.ReadFromJsonAsync<ResultadoDeAgrupacion>(OpcionesJson))!;
    }

    public static async Task<PrevisualizacionDeAgrupacion> LeerPrevisualizacionAsync(HttpResponseMessage respuesta)
    {
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        return (await respuesta.Content.ReadFromJsonAsync<PrevisualizacionDeAgrupacion>(OpcionesJson))!;
    }

    // =================================================================================================
    // Lectura de la base
    // =================================================================================================

    /// <summary>Lo que el artículo guarda y una agrupación no tiene que cambiar: su identidad, sus campos propios y sus
    /// sellos de alta y de baja.</summary>
    public static object Propios(Articulo a) => new
    {
        a.Id, a.CodigoInterno, a.Nombre, a.Descripcion, a.IdMarca, a.Activo, a.DisponibleParaTodas, a.CreatedAt, a.DeletedAt
    };

    /// <summary>Todo lo que el artículo guarda: lo propio, los trece campos compartidos, la pertenencia y
    /// <c>updated_at</c>.</summary>
    public static object Huella(Articulo a) => new
    {
        Propios = Propios(a), Compartidos = ValoresCompartidosDeFamilia.De(a), a.IdFamilia, a.UpdatedAt
    };

    public static List<(decimal Monto, DateTimeOffset Desde, DateTimeOffset? Hasta)> Filas(IEnumerable<Precio> precios) =>
        [.. precios.Select(p => (p.Monto, p.VigenteDesde, p.VigenteHasta))];

    /// <summary>El valor serializado como lo ve el cliente: los registros con listas se comparan por referencia, y dos
    /// respuestas iguales no serían iguales.</summary>
    public static string Json<T>(T valor) => JsonSerializer.Serialize(valor, OpcionesJson);

    /// <summary>Las filas de un par después de escribirlo, con UN solo "ahora": las que ya existían se cerraron —o se
    /// re-cerraron— en ese instante y lo llevan de <c>updated_at</c>, y las nuevas nacieron en él, con el mismo sello de
    /// alta y de modificación.</summary>
    public static void AfirmarUnSoloAhora(IReadOnlyList<Precio> antes, IReadOnlyList<Precio> despues, DateTimeOffset ahora)
    {
        foreach (var fila in despues)
        {
            if (antes.Any(a => a.Id == fila.Id))
            {
                Assert.Equal(ahora, fila.UpdatedAt);
            }
            else
            {
                Assert.Equal((ahora, ahora), (fila.CreatedAt, fila.UpdatedAt));
            }
        }
    }

    /// <summary>Una foto textual de todo lo que una agrupación puede escribir en el tenant: cada artículo dado, cada fila
    /// de precio de cada lista dada con su id y sus sellos, y los conteos de familias —también dadas de baja— y de
    /// auditoría de precios. Dos fotos iguales son una base idéntica.</summary>
    public static async Task<string> FotoDeLaBaseAsync(
        WaysApiFixture fixture, ApoyoDeFamilias apoyo, Entorno e, IEnumerable<int> idsArticulos, IEnumerable<int> idsListas)
    {
        var partes = new List<string>();

        foreach (var id in idsArticulos)
        {
            var a = await apoyo.LeerAsync(id);
            partes.Add(
                $"{a.Id}|{a.IdFamilia}|{a.UpdatedAt:O}|{a.Nombre}|{a.Descripcion}|{a.IdMarca}|{a.Activo}|{ValoresCompartidosDeFamilia.De(a)}");

            foreach (var lista in idsListas)
            {
                partes.AddRange((await apoyo.FilasDePrecioAsync(id, lista)).Select(
                    p => $"{p.Id}|{lista}|{p.Monto}|{p.VigenteDesde:O}|{p.VigenteHasta:O}|{p.UpdatedAt:O}"));
            }
        }

        await using var db = fixture.CrearContextoDeAplicacion(Ways.Infrastructure.Multitenancy.TenantActualFijo.Plataforma);
        partes.Add($"familias={await db.Familias.IgnoreQueryFilters().CountAsync(f => f.IdTenant == e.IdTenant)}");
        partes.Add($"auditoria={(await apoyo.AuditoriaDePreciosAsync(e.IdTenant)).Count}");

        return string.Join("\n", partes);
    }

    /// <summary>Un valor de la auditoría de un cambio de precio: la lista, el monto y desde cuándo vale.</summary>
    public sealed record ValorDeAuditoria(int IdListaPrecio, decimal? Monto, DateTimeOffset? VigenteDesde);

    public static ValorDeAuditoria? LeerValorDeAuditoria(string? json)
    {
        if (json is null)
        {
            return null;
        }

        var v = JsonDocument.Parse(json).RootElement;

        return new ValorDeAuditoria(
            v.GetProperty("id_lista_precio").GetInt32(),
            v.GetProperty("monto").ValueKind == JsonValueKind.Null ? null : v.GetProperty("monto").GetDecimal(),
            v.GetProperty("vigente_desde").ValueKind == JsonValueKind.Null
                ? null
                : AMicrosegundos(v.GetProperty("vigente_desde").GetDateTimeOffset()));
    }

    // =================================================================================================
    // Un host con un interceptor propio
    // =================================================================================================

    /// <summary>Registra cada <see cref="PostgresException"/> que la base le devolvió a un comando de EF. Es lo que permite
    /// afirmar el SQLSTATE de una carrera aunque la API lo traduzca a un 409.</summary>
    public sealed class RegistroDeFallosDeComandos : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<PostgresException> fallos = new();

        public IReadOnlyList<PostgresException> Fallos => [.. fallos];

        public override Task CommandFailedAsync(
            DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Exception is PostgresException postgres)
            {
                fallos.Enqueue(postgres);
            }

            return base.CommandFailedAsync(command, eventData, cancellationToken);
        }
    }

    /// <summary>Un host derivado de la fixture cuyo <see cref="WaysDbContext"/> suma <paramref name="interceptor"/>, y un
    /// cliente con la sesión del admin abierta contra ESE host. El host y el cliente se liberan con el
    /// <c>await using</c> de la prueba; lo que se mande por <see cref="Entorno.Admin"/> corre en el host sin
    /// interceptor.</summary>
    public static async Task<(WebApplicationFactory<Program> Host, HttpClient Cliente)> ClienteDeUnHostConAsync(
        WaysApiFixture fixture, Entorno e, IInterceptor interceptor)
    {
        var host = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        var cliente = host.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(e.MailAdmin, e.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        return (host, cliente);
    }
}
