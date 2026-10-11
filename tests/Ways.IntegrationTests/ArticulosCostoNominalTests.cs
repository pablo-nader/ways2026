using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Articulos;
using Ways.Domain.Articulos;
using Ways.Infrastructure.Multitenancy;
using static Ways.IntegrationTests.ApoyoDeFamilias;

namespace Ways.IntegrationTests;

/// <summary>
/// El costo nominal lo calcula el servidor al crear y al editar (<see cref="CalculadorDeCostoNominal"/>) a partir del
/// costo de lista tal como se cargó —por unidad o por bulto, con o sin IVA— y del descuento del proveedor. Estas
/// pruebas fijan el cableado contra Postgres real: el cálculo en el alta y en la edición, la base del costo que una
/// edición no manda, el 400 de un bulto sin unidades, el ordinal fuera del enum y el CHECK de la columna. Las
/// combinaciones del cálculo en sí están en <c>CalculadorDeCostoNominalTests</c>.
///
/// <para>Todas usan la primera alícuota sembrada, 21%.</para>
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ArticulosCostoNominalTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeFamilias apoyo = new(fixture);

    private static JsonObject Cuerpo(Entorno e, string nombre) => new()
    {
        ["nombre"] = nombre,
        ["idArea"] = e.Areas[0],
        ["idAlicuotaIva"] = e.Alicuotas[0],
        ["unidadVenta"] = "Unidad",
        ["esProducto"] = true,
        ["disponibleParaTodas"] = true,
        ["activo"] = true,
        ["controlaLote"] = false
    };

    private static Task<HttpResponseMessage> PostAsync(Entorno e, JsonObject cuerpo) =>
        e.Admin.PostAsJsonAsync("/api/articulos", cuerpo);

    private static Task<HttpResponseMessage> PutAsync(Entorno e, int id, JsonObject cuerpo) =>
        e.Admin.PutAsJsonAsync($"/api/articulos/{id}", cuerpo);

    private static async Task<ArticuloListado> LeerListadoAsync(HttpResponseMessage respuesta) =>
        (await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;

    [Fact]
    public async Task ElAltaCalculaElCostoNominalDeUnCostoPorUnidadSinIvaConDescuento()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElAltaCalculaElCostoNominalDeUnCostoPorUnidadSinIvaConDescuento));
        var cuerpo = Cuerpo(e, "sin iva con descuento");
        cuerpo["costoLista"] = 100m;
        cuerpo["costoListaPor"] = "Unidad";
        cuerpo["costoListaIncluyeIva"] = false;
        cuerpo["descuentoProveedor"] = 10m;

        var respuesta = await PostAsync(e, cuerpo);

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var creado = await LeerListadoAsync(respuesta);
        Assert.Equal(108.9m, creado.CostoNominal);
        Assert.Equal((100m, CostoListaPor.Unidad, false), (creado.CostoLista, creado.CostoListaPor, creado.CostoListaIncluyeIva));

        var guardado = await apoyo.LeerAsync(creado.Id);
        Assert.Equal(
            (100m, CostoListaPor.Unidad, false, 108.9m),
            (guardado.CostoLista, guardado.CostoListaPor, guardado.CostoListaIncluyeIva, guardado.CostoNominal));
    }

    [Fact]
    public async Task ElAltaCalculaElCostoNominalDeUnCostoPorBultoConIvaIncluido()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElAltaCalculaElCostoNominalDeUnCostoPorBultoConIvaIncluido));
        var cuerpo = Cuerpo(e, "bulto con iva");
        cuerpo["costoLista"] = 120m;
        cuerpo["costoListaPor"] = "Bulto";
        cuerpo["unidadesPorBulto"] = 12m;

        var respuesta = await PostAsync(e, cuerpo);

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var creado = await LeerListadoAsync(respuesta);
        Assert.Equal(10m, creado.CostoNominal);
        Assert.Equal((CostoListaPor.Bulto, true), (creado.CostoListaPor, creado.CostoListaIncluyeIva));
    }

    [Fact]
    public async Task ElAltaSinCostoDeListaDejaElCostoNominalEnNulo()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElAltaSinCostoDeListaDejaElCostoNominalEnNulo));

        var respuesta = await PostAsync(e, Cuerpo(e, "sin costo"));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var creado = await LeerListadoAsync(respuesta);
        Assert.Null(creado.CostoNominal);
        Assert.Equal((CostoListaPor.Unidad, true), (creado.CostoListaPor, creado.CostoListaIncluyeIva));
    }

    /// <summary>El pedido ya no tiene <c>costoNominal</c>: un cliente que todavía lo manda no lo fija.</summary>
    [Fact]
    public async Task UnCostoNominalEnElCuerpoDelAltaSeIgnora()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnCostoNominalEnElCuerpoDelAltaSeIgnora));
        var cuerpo = Cuerpo(e, "con costo nominal enviado");
        cuerpo["costoLista"] = 80m;
        cuerpo["costoNominal"] = 999m;

        var respuesta = await PostAsync(e, cuerpo);

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        Assert.Equal(80m, (await LeerListadoAsync(respuesta)).CostoNominal);
    }

    [Fact]
    public async Task LaEdicionRecalculaElCostoNominalConLoQueLlegaEnElPedido()
    {
        using var e = await apoyo.PrepararAsync(nameof(LaEdicionRecalculaElCostoNominalConLoQueLlegaEnElPedido));
        var alta = Cuerpo(e, "a editar");
        alta["costoLista"] = 50m;
        var creado = await LeerListadoAsync(await PostAsync(e, alta));
        Assert.Equal(50m, creado.CostoNominal);

        var edicion = Cuerpo(e, "a editar");
        edicion["costoLista"] = 300m;
        edicion["costoListaPor"] = "Bulto";
        edicion["costoListaIncluyeIva"] = false;
        edicion["unidadesPorBulto"] = 10m;
        edicion["descuentoProveedor"] = 5m;
        edicion["costoNominal"] = 999m;

        var respuesta = await PutAsync(e, creado.Id, edicion);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var editado = await LeerListadoAsync(respuesta);
        Assert.Equal(34.49m, editado.CostoNominal);
        Assert.Equal((300m, CostoListaPor.Bulto, false), (editado.CostoLista, editado.CostoListaPor, editado.CostoListaIncluyeIva));
        Assert.Equal(34.49m, (await apoyo.LeerAsync(creado.Id)).CostoNominal);
    }

    /// <summary>Una edición que no manda la base del costo conserva la guardada, igual que <c>acumulaEnVenta</c>: un
    /// cliente que no conoce los campos no pisa un costo por bulto sin IVA con el valor por defecto.</summary>
    [Fact]
    public async Task UnaEdicionQueOmiteLaBaseDelCostoConservaLaGuardada()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaEdicionQueOmiteLaBaseDelCostoConservaLaGuardada));
        var alta = Cuerpo(e, "base guardada");
        alta["costoLista"] = 120m;
        alta["costoListaPor"] = "Bulto";
        alta["costoListaIncluyeIva"] = false;
        alta["unidadesPorBulto"] = 12m;
        var creado = await LeerListadoAsync(await PostAsync(e, alta));
        Assert.Equal(12.1m, creado.CostoNominal);

        var edicion = Cuerpo(e, "base guardada editada");
        edicion["costoLista"] = 240m;
        edicion["unidadesPorBulto"] = 12m;

        var respuesta = await PutAsync(e, creado.Id, edicion);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var editado = await LeerListadoAsync(respuesta);
        Assert.Equal((CostoListaPor.Bulto, false, 24.2m), (editado.CostoListaPor, editado.CostoListaIncluyeIva, editado.CostoNominal));
    }

    [Fact]
    public async Task UnAltaPorBultoSinUnidadesPorBultoDa400YNoEscribeNada()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnAltaPorBultoSinUnidadesPorBultoDa400YNoEscribeNada));
        var cuerpo = Cuerpo(e, "bulto sin unidades");
        cuerpo["costoLista"] = 120m;
        cuerpo["costoListaPor"] = "Bulto";

        var respuesta = await PostAsync(e, cuerpo);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("unidades_por_bulto_requerido", (await ProblemaAsync(respuesta)).Codigo);
        Assert.Empty(await ArticulosDelTenantAsync(e));
    }

    [Fact]
    public async Task UnAltaPorBultoConCeroUnidadesPorBultoDa400()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnAltaPorBultoConCeroUnidadesPorBultoDa400));
        var cuerpo = Cuerpo(e, "bulto con cero");
        cuerpo["costoLista"] = 120m;
        cuerpo["costoListaPor"] = "Bulto";
        cuerpo["unidadesPorBulto"] = 0m;

        var respuesta = await PostAsync(e, cuerpo);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("unidades_por_bulto_requerido", (await ProblemaAsync(respuesta)).Codigo);
    }

    [Fact]
    public async Task UnaEdicionPorBultoSinUnidadesPorBultoDa400YNoCambiaNada()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaEdicionPorBultoSinUnidadesPorBultoDa400YNoCambiaNada));
        var alta = Cuerpo(e, "edicion bulto sin unidades");
        alta["costoLista"] = 50m;
        var creado = await LeerListadoAsync(await PostAsync(e, alta));
        var antes = await apoyo.LeerAsync(creado.Id);

        var edicion = Cuerpo(e, "edicion bulto sin unidades");
        edicion["costoLista"] = 120m;
        edicion["costoListaPor"] = "Bulto";

        var respuesta = await PutAsync(e, creado.Id, edicion);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("unidades_por_bulto_requerido", (await ProblemaAsync(respuesta)).Codigo);
        var despues = await apoyo.LeerAsync(creado.Id);
        Assert.Equal(
            (antes.CostoLista, antes.CostoListaPor, antes.CostoNominal, antes.UpdatedAt),
            (despues.CostoLista, despues.CostoListaPor, despues.CostoNominal, despues.UpdatedAt));
    }

    /// <summary>El conversor JSON del servidor acepta el ordinal además del nombre: uno fuera del enum se rechaza
    /// con 400 antes de llegar al CHECK de la columna.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    [InlineData(99)]
    public async Task UnOrdinalDeCostoListaPorFueraDelEnumDa400(int ordinal)
    {
        using var e = await apoyo.PrepararAsync(nameof(UnOrdinalDeCostoListaPorFueraDelEnumDa400));
        var cuerpo = Cuerpo(e, "ordinal invalido");
        cuerpo["costoLista"] = 10m;
        cuerpo["costoListaPor"] = ordinal;

        var respuesta = await PostAsync(e, cuerpo);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("costo_lista_por_invalido", (await ProblemaAsync(respuesta)).Codigo);
        Assert.Empty(await ArticulosDelTenantAsync(e));
    }

    /// <summary>Con IVA el costo nominal puede superar el tope de <c>numeric(14,2)</c> aunque el costo de lista lo
    /// cumpla: es un 400 y no un desborde de la base.</summary>
    [Fact]
    public async Task UnCostoNominalCalculadoQueDesbordaLaColumnaDa400()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnCostoNominalCalculadoQueDesbordaLaColumnaDa400));
        var cuerpo = Cuerpo(e, "desborde");
        cuerpo["costoLista"] = 999_999_999_999m;
        cuerpo["costoListaIncluyeIva"] = false;

        var respuesta = await PostAsync(e, cuerpo);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("costo_nominal_invalido", (await ProblemaAsync(respuesta)).Codigo);
    }

    /// <summary>El CHECK <c>ck_articulos_costo_lista_por</c> es el respaldo de la base: una escritura cruda con un
    /// valor fuera de Unidad/Bulto la rechaza la propia tabla con 23514.</summary>
    [Fact]
    public async Task LaBaseRechazaUnCostoListaPorQueNoEsUnidadNiBulto()
    {
        using var e = await apoyo.PrepararAsync(nameof(LaBaseRechazaUnCostoListaPorQueNoEsUnidadNiBulto));
        var creado = await LeerListadoAsync(await PostAsync(e, Cuerpo(e, "check")));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText = "UPDATE articulos SET costo_lista_por = 'Caja' WHERE id_articulo = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = creado.Id });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_articulos_costo_lista_por", excepcion.ConstraintName);
    }

    /// <summary>Un costo nominal que no sale de la fórmula (el efectivo que dejó una compra) sobrevive a la edición de
    /// un campo que no entra en el cálculo.</summary>
    [Fact]
    public async Task UnaEdicionDeUnCampoAjenoAlCalculoConservaUnCostoNominalDistintoDeLaFormula()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaEdicionDeUnCampoAjenoAlCalculoConservaUnCostoNominalDistintoDeLaFormula));
        var id = await apoyo.SembrarArticuloAsync(e, "con costo de compra", ValoresBase(e) with { CostoNominal = 12.34m });
        var antes = await apoyo.LeerAsync(id);

        var respuesta = await PutArticuloAsync(e.Admin, id, EdicionIgualA(antes) with { Nombre = "renombrado" });

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal(12.34m, (await LeerListadoAsync(respuesta)).CostoNominal);
        var despues = await apoyo.LeerAsync(id);
        Assert.Equal(("renombrado", 12.34m), (despues.Nombre, despues.CostoNominal));
    }

    /// <summary>Lo mismo en una familia cuyo costo nominal dejó una compra: editar un campo propio de un miembro no
    /// pide alcance (no hay cambio compartido) ni toca el costo.</summary>
    [Fact]
    public async Task UnaEdicionPropiaDeUnMiembroDeFamiliaConCostoDeCompraNoPideAlcanceNiTocaElCosto()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaEdicionPropiaDeUnMiembroDeFamiliaConCostoDeCompraNoPideAlcanceNiTocaElCosto));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Con costo de compra");
        var compartidos = ValoresBase(e) with { CostoNominal = 200m };
        var m1 = await apoyo.SembrarArticuloAsync(e, "m1", compartidos, familia);
        var m2 = await apoyo.SembrarArticuloAsync(e, "m2", compartidos, familia);
        var antes = await apoyo.LeerAsync(m2);

        var respuesta = await PutArticuloAsync(e.Admin, m2, EdicionIgualA(antes) with { Nombre = "m2 renombrado" });

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal(200m, (await apoyo.LeerAsync(m1)).CostoNominal);
        Assert.Equal(200m, (await apoyo.LeerAsync(m2)).CostoNominal);
    }

    [Fact]
    public async Task UnAltaEnUnaFamiliaCuyoCostoNominalDejoUnaCompraLoHeredaYEntraConCreated()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnAltaEnUnaFamiliaCuyoCostoNominalDejoUnaCompraLoHeredaYEntraConCreated));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Con costo de compra");
        var compartidos = ValoresBase(e) with { CostoNominal = 200m };
        await apoyo.SembrarArticuloAsync(e, "m1", compartidos, familia);

        var cuerpo = Cuerpo(e, "miembro nuevo");
        cuerpo["costoLista"] = compartidos.CostoLista;
        cuerpo["descuentoProveedor"] = compartidos.DescuentoProveedor;
        cuerpo["unidadesPorBulto"] = compartidos.UnidadesPorBulto;
        cuerpo["idCategoria"] = compartidos.IdCategoria;
        cuerpo["idGrupo"] = compartidos.IdGrupo;
        cuerpo["idProveedorHabitual"] = compartidos.IdProveedorHabitual;
        cuerpo["idFamilia"] = familia;

        var respuesta = await PostAsync(e, cuerpo);

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var creado = await LeerListadoAsync(respuesta);
        Assert.Equal(200m, creado.CostoNominal);
        Assert.Equal(200m, (await apoyo.LeerAsync(creado.Id)).CostoNominal);
    }

    private async Task<List<Articulo>> ArticulosDelTenantAsync(Entorno e)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        return await db.Articulos.IgnoreQueryFilters().AsNoTracking().Where(a => a.IdTenant == e.IdTenant).ToListAsync();
    }
}
