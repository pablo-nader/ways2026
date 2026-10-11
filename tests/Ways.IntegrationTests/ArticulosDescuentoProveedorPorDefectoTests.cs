using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Ways.Application.Articulos;
using Ways.Infrastructure.Persistencia.Migraciones;
using static Ways.IntegrationTests.ApoyoDeFamilias;

namespace Ways.IntegrationTests;

/// <summary>
/// El descuento del proveedor de un artículo vale 0 cuando no se carga, nunca nulo: el servidor lo fuerza en el alta y en
/// la edición, la columna lo defaultea y la migración <c>DescuentoProveedorPorDefectoCero</c> completa lo que ya estaba
/// guardado (descuento nulo → 0 y costo nominal faltante, calculado donde hay costo de lista).
///
/// <para>El backfill se prueba sobre <c>ways_app</c> (NOSUPERUSER NOBYPASSRLS) con el SQL de la propia migración
/// (<see cref="DescuentoProveedorPorDefectoCero.SqlDelBackfill"/>): <c>articulos</c> corre bajo FORCE ROW LEVEL SECURITY
/// y, sin el <c>SET LOCAL app.acceso = 'plataforma'</c> del bloque, los UPDATE tocan cero filas sin avisar.</para>
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ArticulosDescuentoProveedorPorDefectoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PrefijoDePlataforma = "SET LOCAL app.acceso = 'plataforma';";

    private readonly ApoyoDeFamilias apoyo = new(fixture);

    private static JsonObject Cuerpo(Entorno e, string nombre, int indiceAlicuota = 0) => new()
    {
        ["nombre"] = nombre,
        ["idArea"] = e.Areas[0],
        ["idAlicuotaIva"] = e.Alicuotas[indiceAlicuota],
        ["unidadVenta"] = "Unidad",
        ["esProducto"] = true,
        ["disponibleParaTodas"] = true,
        ["activo"] = true,
        ["controlaLote"] = false
    };

    private static async Task<ArticuloListado> LeerListadoAsync(HttpResponseMessage respuesta) =>
        (await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;

    [Fact]
    public async Task ElAltaSinDescuentoGuardaYDevuelveCeroYCalculaElCostoNominal()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElAltaSinDescuentoGuardaYDevuelveCeroYCalculaElCostoNominal));
        var cuerpo = Cuerpo(e, "sin descuento");
        cuerpo["costoLista"] = 100m;
        cuerpo["costoListaIncluyeIva"] = false;

        var respuesta = await e.Admin.PostAsJsonAsync("/api/articulos", cuerpo);

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var creado = await LeerListadoAsync(respuesta);
        Assert.Equal((0m, 121m), (creado.DescuentoProveedor, creado.CostoNominal));
        var guardado = await apoyo.LeerAsync(creado.Id);
        Assert.Equal((0m, 121m), (guardado.DescuentoProveedor, guardado.CostoNominal));
    }

    [Fact]
    public async Task ElAltaConDescuentoNuloExplicitoGuardaCero()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElAltaConDescuentoNuloExplicitoGuardaCero));
        var cuerpo = Cuerpo(e, "descuento nulo");
        cuerpo["descuentoProveedor"] = null;

        var respuesta = await e.Admin.PostAsJsonAsync("/api/articulos", cuerpo);

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var creado = await LeerListadoAsync(respuesta);
        Assert.Equal(0m, creado.DescuentoProveedor);
        Assert.Equal(0m, (await apoyo.LeerAsync(creado.Id)).DescuentoProveedor);
    }

    [Fact]
    public async Task LaEdicionConDescuentoNuloGuardaCeroYRecalculaElCostoNominal()
    {
        using var e = await apoyo.PrepararAsync(nameof(LaEdicionConDescuentoNuloGuardaCeroYRecalculaElCostoNominal));
        var alta = Cuerpo(e, "con descuento");
        alta["costoLista"] = 100m;
        alta["descuentoProveedor"] = 10m;
        var creado = await LeerListadoAsync(await e.Admin.PostAsJsonAsync("/api/articulos", alta));
        Assert.Equal((10m, 90m), (creado.DescuentoProveedor, creado.CostoNominal));

        var edicion = Cuerpo(e, "con descuento");
        edicion["costoLista"] = 100m;
        edicion["descuentoProveedor"] = null;

        var respuesta = await e.Admin.PutAsJsonAsync($"/api/articulos/{creado.Id}", edicion);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var editado = await LeerListadoAsync(respuesta);
        Assert.Equal((0m, 100m), (editado.DescuentoProveedor, editado.CostoNominal));
        var guardado = await apoyo.LeerAsync(creado.Id);
        Assert.Equal((0m, 100m), (guardado.DescuentoProveedor, guardado.CostoNominal));
    }

    /// <summary>El descuento forzado a 0 también cuenta en la comparación de los campos compartidos: un alta en una
    /// familia cuya referencia guardó 0 y que no manda descuento es el mismo valor, no un <c>familia_valores_distintos</c>.</summary>
    [Fact]
    public async Task UnAltaEnUnaFamiliaSinDescuentoCoincideConUnaReferenciaQueGuardoCero()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnAltaEnUnaFamiliaSinDescuentoCoincideConUnaReferenciaQueGuardoCero));
        var referencia = Cuerpo(e, "referencia");
        referencia["nombreFamiliaNueva"] = "familia del descuento";
        var creada = await LeerListadoAsync(await e.Admin.PostAsJsonAsync("/api/articulos", referencia));
        Assert.Equal(0m, creada.DescuentoProveedor);
        Assert.NotNull(creada.IdFamilia);

        var miembro = Cuerpo(e, "miembro");
        miembro["idFamilia"] = creada.IdFamilia;

        var respuesta = await e.Admin.PostAsJsonAsync("/api/articulos", miembro);

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var nuevo = await LeerListadoAsync(respuesta);
        Assert.Equal((creada.IdFamilia, 0m), (nuevo.IdFamilia, nuevo.DescuentoProveedor));
    }

    /// <summary>Respaldo de la columna: una inserción que no mande el descuento queda en 0.</summary>
    [Fact]
    public async Task LaColumnaDeclaraElDefaultCero()
    {
        using var _ = fixture.CreateClient();
        await using var cruda = new NpgsqlConnection(fixture.OwnerConnectionString);
        await cruda.OpenAsync();
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "SELECT column_default FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = 'articulos' AND column_name = 'descuento_proveedor'";

        var valorPorDefecto = (string)(await comando.ExecuteScalarAsync())!;

        Assert.Equal(0m, decimal.Parse(valorPorDefecto, System.Globalization.CultureInfo.InvariantCulture));
    }

    // ---- backfill sobre ways_app -------------------------------------------------------------------

    private sealed record Sembrado(
        int Id, decimal? DescuentoSembrado, decimal? NominalSembrado, decimal? DescuentoEsperado, decimal? NominalEsperado);

    private async Task<NpgsqlConnection> AbrirDuenoAsync()
    {
        var cruda = new NpgsqlConnection(fixture.OwnerConnectionString);
        await cruda.OpenAsync();
        return cruda;
    }

    /// <summary>Crea el artículo por la API y después lo deja como estaba antes de la migración: descuento y costo
    /// nominal sembrados con SQL crudo del dueño (el servicio ya no puede producir un descuento nulo).</summary>
    private async Task<Sembrado> SembrarAsync(
        Entorno e, JsonObject cuerpo, decimal? descuentoSembrado, decimal? nominalSembrado,
        decimal? descuentoEsperado, decimal? nominalEsperado, string ajusteSql = "")
    {
        var respuesta = await e.Admin.PostAsJsonAsync("/api/articulos", cuerpo);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var id = (await LeerListadoAsync(respuesta)).Id;

        await using var cruda = await AbrirDuenoAsync();
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            $"UPDATE articulos SET descuento_proveedor = $1, costo_nominal = $2{ajusteSql} WHERE id_articulo = $3";
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)descuentoSembrado ?? DBNull.Value });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)nominalSembrado ?? DBNull.Value });
        comando.Parameters.Add(new NpgsqlParameter { Value = id });
        Assert.Equal(1, await comando.ExecuteNonQueryAsync());

        return new Sembrado(id, descuentoSembrado, nominalSembrado, descuentoEsperado, nominalEsperado);
    }

    private async Task<Dictionary<int, (decimal? Descuento, decimal? Nominal)>> LeerComoDuenoAsync(IEnumerable<Sembrado> filas)
    {
        await using var cruda = await AbrirDuenoAsync();
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "SELECT id_articulo, descuento_proveedor, costo_nominal FROM articulos WHERE id_articulo = ANY($1)";
        comando.Parameters.Add(new NpgsqlParameter { Value = filas.Select(f => f.Id).ToArray() });

        var leidas = new Dictionary<int, (decimal?, decimal?)>();
        await using var lector = await comando.ExecuteReaderAsync();

        while (await lector.ReadAsync())
        {
            leidas[lector.GetInt32(0)] = (
                lector.IsDBNull(1) ? null : lector.GetDecimal(1),
                lector.IsDBNull(2) ? null : lector.GetDecimal(2));
        }

        return leidas;
    }

    private async Task<int> EjecutarSobreWaysAppAsync(string sql)
    {
        await using var cruda = new NpgsqlConnection(fixture.AppConnectionString);
        await cruda.OpenAsync();
        await using var comando = cruda.CreateCommand();
        comando.CommandText = sql;

        return await comando.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ElBackfillSoloAlcanzaFilasEnModoPlataformaCompletaDescuentoYCostoNominalSinPisarElGuardadoYEsIdempotente()
    {
        using var a = await apoyo.PrepararAsync("backfill-descuento-a");
        using var b = await apoyo.PrepararAsync("backfill-descuento-b");

        var unidadConIva = Cuerpo(a, "unidad con iva");
        unidadConIva["costoLista"] = 100m;
        var unidadSinIva = Cuerpo(a, "unidad sin iva");
        unidadSinIva["costoLista"] = 100m;
        unidadSinIva["costoListaIncluyeIva"] = false;
        var bultoSinIva = Cuerpo(a, "bulto sin iva 10,5", indiceAlicuota: 1);
        bultoSinIva["costoLista"] = 120m;
        bultoSinIva["costoListaPor"] = "Bulto";
        bultoSinIva["unidadesPorBulto"] = 12m;
        bultoSinIva["costoListaIncluyeIva"] = false;

        var conDescuento = Cuerpo(b, "con descuento y redondeo");
        conDescuento["costoLista"] = 10.05m;
        var nominalGuardado = Cuerpo(b, "nominal guardado");
        nominalGuardado["costoLista"] = 50m;
        var sinCostoDeLista = Cuerpo(b, "sin costo de lista");
        var bultoSinUnidades = Cuerpo(b, "bulto sin unidades");
        bultoSinUnidades["costoLista"] = 90m;

        var filas = new List<Sembrado>
        {
            await SembrarAsync(a, unidadConIva, null, null, 0m, 100m),
            await SembrarAsync(a, unidadSinIva, null, null, 0m, 121m),
            await SembrarAsync(a, bultoSinIva, null, null, 0m, 11.05m),
            // 10,05 * (1 - 50/100) = 5,025: la mitad se aleja de cero, como Math.Round(..., AwayFromZero) del calculador.
            await SembrarAsync(b, conDescuento, 50m, null, 50m, 5.03m),
            await SembrarAsync(b, nominalGuardado, null, 77.77m, 0m, 77.77m),
            await SembrarAsync(b, sinCostoDeLista, null, null, 0m, null),
            // Un bulto con 0 unidades dividiría por cero: se excluye, no aborta el backfill.
            await SembrarAsync(
                b, bultoSinUnidades, null, null, 0m, null,
                ajusteSql: ", costo_lista_por = 'Bulto', unidades_por_bulto = 0")
        };

        // Sin el SET LOCAL, bajo NOSUPERUSER NOBYPASSRLS y sin GUC, el UPDATE no ve nada: cero filas, sin error.
        var sinGuc = DescuentoProveedorPorDefectoCero.SqlDelBackfill.Replace(PrefijoDePlataforma, string.Empty);
        Assert.DoesNotContain("SET LOCAL", sinGuc);
        Assert.Equal(0, await EjecutarSobreWaysAppAsync(sinGuc));
        var intactas = await LeerComoDuenoAsync(filas);
        foreach (var fila in filas)
        {
            Assert.Equal((fila.DescuentoSembrado, fila.NominalSembrado), intactas[fila.Id]);
        }

        // Con el bloque exacto de la migración alcanza a los dos tenants de una pasada: 6 descuentos nulos pasan a 0 y
        // se calculan 4 costos nominales (unidad con y sin IVA, bulto, y el que tiene descuento).
        Assert.Equal(10, await EjecutarSobreWaysAppAsync(DescuentoProveedorPorDefectoCero.SqlDelBackfill));
        var completadas = await LeerComoDuenoAsync(filas);
        foreach (var fila in filas)
        {
            Assert.Equal((fila.DescuentoEsperado, fila.NominalEsperado), completadas[fila.Id]);
        }

        // Una segunda corrida no encuentra nada que completar.
        Assert.Equal(0, await EjecutarSobreWaysAppAsync(DescuentoProveedorPorDefectoCero.SqlDelBackfill));
        var repetidas = await LeerComoDuenoAsync(filas);
        foreach (var fila in filas)
        {
            Assert.Equal((fila.DescuentoEsperado, fila.NominalEsperado), repetidas[fila.Id]);
        }
    }
}
