using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Compras;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>DELETE /api/compras/{id}</c>: descartar un borrador es un borrado físico del encabezado y de
/// todo lo que cuelga de él, con el rastro <c>compra.descarte</c> en la misma transacción. Solo un
/// borrador puede descartarse; una confirmada o anulada conserva su historia.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ComprasDescarteTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordSecundario = "password-secundaria-larga";
    private const string RolApp = "ways_app";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, int IdAdmin, HttpClient Admin, HttpClient Supervisor, HttpClient Vendedor,
        int IdProveedor, int IdArticulo, int IdAlicuotaIva21, int IdTipoCFA);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Descarte-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva21 = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var proveedor = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = nombre, IdCondicionFiscal = condicionFiscal.Id,
            Margen = 50m, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        var articulo = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-1-{Guid.NewGuid():N}", Nombre = "Articulo 1",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            IdProveedorHabitual = proveedor.Id, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        var idTipoCFA = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();

        var supervisor = await CrearUsuarioAsync(db, resultado.IdTenant, nombre, "supervisor", RolConocido.Supervisor, ahora);
        var vendedor = await CrearUsuarioAsync(db, resultado.IdTenant, nombre, "vendedor", RolConocido.Vendedor, ahora);

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, admin, supervisor, vendedor,
            proveedor.Id, articulo.Id, idAlicuotaIva21, idTipoCFA);
    }

    private async Task<HttpClient> CrearUsuarioAsync(
        Ways.Infrastructure.Persistencia.WaysDbContext db, int idTenant, string nombre, string sufijo, RolConocido rol,
        DateTimeOffset ahora)
    {
        var hasheador = new HasheadorPbkdf2();
        var mail = $"{nombre.ToLowerInvariant()}-{sufijo}@ways.test";
        db.Usuarios.Add(new Usuario
        {
            IdTenant = idTenant, NombreUsuario = sufijo, Mail = mail, RolId = (int)rol,
            PasswordHash = hasheador.Hashear(PasswordSecundario), PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordSecundario));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    /// <summary>Un borrador con dos líneas (dos alícuotas) y una percepción: toda tabla hija con filas.</summary>
    private static SolicitudDeCompra SolicitudCompleta(Contexto ctx, string? numeroExterno = "0001-00000001") =>
        new(
            ctx.IdProveedor, ctx.IdTipoCFA, ctx.IdPuntoVenta, numeroExterno, DateOnly.FromDateTime(DateTime.UtcNow), null,
            [
                new LineaDeCompraSolicitada(ctx.IdArticulo, "Item 1", 10m, null, null, 100m, 0m, ctx.IdAlicuotaIva21, true),
                new LineaDeCompraSolicitada(ctx.IdArticulo, "Item 2", 5m, null, null, 40m, 0m, ctx.IdAlicuotaIva21, true)
            ],
            Percepciones: [new PercepcionSolicitada(TiposDePercepcion.Iibb, 1000m, 3m, 30m)]);

    private static async Task<CompraDetalle> CrearBorradorAsync(Contexto ctx, SolicitudDeCompra? solicitud = null)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/compras", solicitud ?? SolicitudCompleta(ctx));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static async Task<CompraDetalle> ConfirmarAsync(Contexto ctx, int id)
    {
        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{id}/confirmar", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private Ways.Infrastructure.Persistencia.WaysDbContext ContextoDelTenant(int idTenant) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));

    private static async Task<string> CodigoDeErrorAsync(HttpResponseMessage respuesta)
    {
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        return problema.GetProperty("codigo").GetString()!;
    }

    private async Task<(int Encabezados, int Items, int Alicuotas, int Percepciones)> ContarAsync(int idTenant, int idCompra)
    {
        await using var db = ContextoDelTenant(idTenant);
        return (
            await db.ComprobantesCompra.CountAsync(c => c.Id == idCompra),
            await db.ItemsComprobanteCompra.CountAsync(i => i.IdComprobanteCompra == idCompra),
            await db.AlicuotasComprobanteCompra.CountAsync(a => a.IdComprobanteCompra == idCompra),
            await db.PercepcionesComprobanteCompra.CountAsync(p => p.IdComprobanteCompra == idCompra));
    }

    // ---- camino feliz --------------------------------------------------------------------------

    /// <summary>El descarte borra el encabezado Y los tres hijos (cada conteo en cero es el kill de su
    /// propio <c>DELETE</c>: sin el de una tabla hija, la FK Restrict rechaza el del encabezado y el
    /// descarte falla) y deja una sola fila de auditoría con el actor, el punto de venta y lo descartado.</summary>
    [Fact]
    public async Task DescartarUnBorradorBorraElEncabezadoYTodosSusHijosYDejaElRastro()
    {
        var ctx = await PrepararAsync(nameof(DescartarUnBorradorBorraElEncabezadoYTodosSusHijosYDejaElRastro));
        var creada = await CrearBorradorAsync(ctx);

        var antes = await ContarAsync(ctx.IdTenant, creada.Id);
        Assert.Equal(1, antes.Encabezados);
        Assert.Equal(2, antes.Items);
        Assert.Equal(1, antes.Alicuotas);
        Assert.Equal(1, antes.Percepciones);

        var respuesta = await ctx.Admin.DeleteAsync($"/api/compras/{creada.Id}");

        Assert.Equal(HttpStatusCode.NoContent, respuesta.StatusCode);
        Assert.Equal((0, 0, 0, 0), await ContarAsync(ctx.IdTenant, creada.Id));

        await using var db = ContextoDelTenant(ctx.IdTenant);
        var fila = await db.Auditoria.SingleAsync(a => a.Accion == "compra.descarte" && a.IdEntidad == creada.Id);
        Assert.Equal("comprobante_compra", fila.Entidad);
        Assert.Equal(ctx.IdAdmin, fila.IdActor);
        Assert.Equal(ctx.IdPuntoVenta, fila.IdPuntoVenta);

        using var valorAnterior = JsonDocument.Parse(fila.ValorAnterior!);
        using var valorNuevo = JsonDocument.Parse(fila.ValorNuevo);
        Assert.True(valorAnterior.RootElement.GetProperty("existe").GetBoolean());
        Assert.False(valorNuevo.RootElement.GetProperty("existe").GetBoolean());
        Assert.Equal(ctx.IdProveedor, valorNuevo.RootElement.GetProperty("id_proveedor").GetInt32());
        Assert.Equal("0001-00000001", valorNuevo.RootElement.GetProperty("numero_externo").GetString());
        Assert.Equal(creada.Total, valorNuevo.RootElement.GetProperty("total").GetDecimal());
        Assert.Equal(2, valorNuevo.RootElement.GetProperty("cantidad_de_items").GetInt32());
    }

    [Fact]
    public async Task DescartarLiberaElNumeroDelProveedorYElMismoBorradorSePuedeVolverACargar()
    {
        var ctx = await PrepararAsync(nameof(DescartarLiberaElNumeroDelProveedorYElMismoBorradorSePuedeVolverACargar));
        var creada = await CrearBorradorAsync(ctx);

        var repetida = await ctx.Admin.PostAsJsonAsync("/api/compras", SolicitudCompleta(ctx));
        Assert.Equal(HttpStatusCode.Conflict, repetida.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"/api/compras/{creada.Id}")).StatusCode);

        var nueva = await CrearBorradorAsync(ctx);
        Assert.NotEqual(creada.Id, nueva.Id);
    }

    [Fact]
    public async Task UnaCompraDescartadaDejaDeExistirEnElDetalleYEnElListado()
    {
        var ctx = await PrepararAsync(nameof(UnaCompraDescartadaDejaDeExistirEnElDetalleYEnElListado));
        var creada = await CrearBorradorAsync(ctx);
        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"/api/compras/{creada.Id}")).StatusCode);

        var detalle = await ctx.Admin.GetAsync($"/api/compras/{creada.Id}");
        Assert.Equal(HttpStatusCode.NotFound, detalle.StatusCode);

        var listado = await ctx.Admin.GetFromJsonAsync<JsonElement>("/api/compras?tamanio=100");
        var ids = listado.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToList();
        Assert.DoesNotContain(creada.Id, ids);
    }

    // ---- estados que no se descartan ------------------------------------------------------------

    [Fact]
    public async Task UnaCompraConfirmadaNoSeDescartaYConservaTodo()
    {
        var ctx = await PrepararAsync(nameof(UnaCompraConfirmadaNoSeDescartaYConservaTodo));
        var creada = await CrearBorradorAsync(ctx);
        await ConfirmarAsync(ctx, creada.Id);

        var respuesta = await ctx.Admin.DeleteAsync($"/api/compras/{creada.Id}");

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("compra_no_descartable", await CodigoDeErrorAsync(respuesta));
        Assert.Equal((1, 2, 1, 1), await ContarAsync(ctx.IdTenant, creada.Id));

        await using var db = ContextoDelTenant(ctx.IdTenant);
        Assert.Equal(0, await db.Auditoria.CountAsync(a => a.Accion == "compra.descarte" && a.IdEntidad == creada.Id));
    }

    [Fact]
    public async Task UnaCompraAnuladaNoSeDescarta()
    {
        var ctx = await PrepararAsync(nameof(UnaCompraAnuladaNoSeDescarta));
        var creada = await CrearBorradorAsync(ctx);
        await ConfirmarAsync(ctx, creada.Id);
        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/anular", null)).StatusCode);

        var respuesta = await ctx.Admin.DeleteAsync($"/api/compras/{creada.Id}");

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("compra_no_descartable", await CodigoDeErrorAsync(respuesta));
        Assert.Equal((1, 2, 1, 1), await ContarAsync(ctx.IdTenant, creada.Id));
    }

    // ---- 404 y autorización ---------------------------------------------------------------------

    [Fact]
    public async Task UnIdInexistenteDevuelve404()
    {
        var ctx = await PrepararAsync(nameof(UnIdInexistenteDevuelve404));

        var respuesta = await ctx.Admin.DeleteAsync("/api/compras/999999999");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task LaCompraDeOtroTenantDevuelve404YNoSeToca()
    {
        var propio = await PrepararAsync(nameof(LaCompraDeOtroTenantDevuelve404YNoSeToca) + "A");
        var ajeno = await PrepararAsync(nameof(LaCompraDeOtroTenantDevuelve404YNoSeToca) + "B");
        var deLaAjena = await CrearBorradorAsync(ajeno);

        var respuesta = await propio.Admin.DeleteAsync($"/api/compras/{deLaAjena.Id}");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Equal((1, 2, 1, 1), await ContarAsync(ajeno.IdTenant, deLaAjena.Id));
    }

    [Fact]
    public async Task SoloElAdminPuedeDescartar()
    {
        var ctx = await PrepararAsync(nameof(SoloElAdminPuedeDescartar));
        var creada = await CrearBorradorAsync(ctx);

        Assert.Equal(HttpStatusCode.Forbidden, (await ctx.Supervisor.DeleteAsync($"/api/compras/{creada.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ctx.Vendedor.DeleteAsync($"/api/compras/{creada.Id}")).StatusCode);

        Assert.Equal((1, 2, 1, 1), await ContarAsync(ctx.IdTenant, creada.Id));
    }

    // ---- una sola transacción -------------------------------------------------------------------

    /// <summary>La auditoria viaja en la misma transacción que el borrado: si no se puede escribir, el
    /// descarte falla entero y el borrador queda intacto con todos sus hijos (mismo <c>REVOKE</c> que
    /// <c>AuditoriaAnulacionCompraTests</c>).</summary>
    [Fact]
    public async Task UnaFallaAlEscribirLaAuditoriaDejaElBorradorIntacto()
    {
        var ctx = await PrepararAsync(nameof(UnaFallaAlEscribirLaAuditoriaDejaElBorradorIntacto));
        var creada = await CrearBorradorAsync(ctx);

        await CambiarPrivilegioAsync("REVOKE", "auditoria", "INSERT", "FROM");
        HttpResponseMessage respuesta;
        try
        {
            respuesta = await ctx.Admin.DeleteAsync($"/api/compras/{creada.Id}");
        }
        finally
        {
            await CambiarPrivilegioAsync("GRANT", "auditoria", "INSERT", "TO");
        }

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);
        Assert.Equal((1, 2, 1, 1), await ContarAsync(ctx.IdTenant, creada.Id));

        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"/api/compras/{creada.Id}")).StatusCode);
    }

    // ---- carreras contra el lock de fila ---------------------------------------------------------

    /// <summary>Un escritor retiene el <c>FOR UPDATE</c> del encabezado, el DELETE espera ese lock y el
    /// escritor lo borra y comitea: al obtener el lock el descarte ya no ve la fila y responde 404, no 409.</summary>
    [Fact]
    public async Task UnDescarteConcurrenteYaComiteadoDejaUn404()
    {
        var ctx = await PrepararAsync(nameof(UnDescarteConcurrenteYaComiteadoDejaUn404));
        var creada = await CrearBorradorAsync(ctx);

        var respuesta = await DescartarMientrasOtroRetieneElLockAsync(ctx, creada.Id, async (conexion, transaccion) =>
        {
            foreach (var tabla in new[] { "items_comprobante_compra", "alicuotas_comprobante_compra", "percepciones_comprobante_compra" })
            {
                await EjecutarAsync(conexion, transaccion, $"DELETE FROM {tabla} WHERE id_comprobante_compra = {creada.Id}");
            }

            await EjecutarAsync(conexion, transaccion, $"DELETE FROM comprobantes_compra WHERE id_comprobante_compra = {creada.Id}");
        });

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    /// <summary>Misma espera, pero el escritor confirma la compra: el descarte obtiene el lock sobre una fila
    /// que ya no es borrador y responde 409 <c>compra_no_descartable</c>, sin borrar nada.</summary>
    [Fact]
    public async Task UnaConfirmacionConcurrenteYaComiteadaDejaUn409()
    {
        var ctx = await PrepararAsync(nameof(UnaConfirmacionConcurrenteYaComiteadaDejaUn409));
        var creada = await CrearBorradorAsync(ctx);

        var respuesta = await DescartarMientrasOtroRetieneElLockAsync(ctx, creada.Id, (conexion, transaccion) =>
            EjecutarAsync(
                conexion, transaccion,
                "UPDATE comprobantes_compra SET estado = 'confirmada'::estado_compra, fecha_recepcion = now() " +
                $"WHERE id_comprobante_compra = {creada.Id}"));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("compra_no_descartable", await CodigoDeErrorAsync(respuesta));
        Assert.Equal((1, 2, 1, 1), await ContarAsync(ctx.IdTenant, creada.Id));
    }

    private async Task<HttpResponseMessage> DescartarMientrasOtroRetieneElLockAsync(
        Contexto ctx, int idCompra, Func<NpgsqlConnection, NpgsqlTransaction, Task> escrituraDelOtro)
    {
        await using var conexionEscritor = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        var pidEscritor = await BackendPidAsync(conexionEscritor);

        await using var transaccionEscritor = await conexionEscritor.BeginTransactionAsync();
        await EjecutarAsync(
            conexionEscritor, transaccionEscritor,
            $"SELECT 1 FROM comprobantes_compra WHERE id_comprobante_compra = {idCompra} FOR UPDATE");

        var descarte = ctx.Admin.DeleteAsync($"/api/compras/{idCompra}");

        await using var conexionPoll = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        var pidPoll = await BackendPidAsync(conexionPoll);
        Assert.True(
            await EsperarBackendBloqueadoAsync(conexionPoll, pidEscritor, pidPoll),
            "El descarte nunca se observó esperando el lock: la prueba no está probando la carrera.");

        await escrituraDelOtro(conexionEscritor, transaccionEscritor);
        await transaccionEscritor.CommitAsync();

        return await descarte;
    }

    private static async Task EjecutarAsync(NpgsqlConnection conexion, NpgsqlTransaction transaccion, string sql)
    {
        await using var comando = new NpgsqlCommand(sql, conexion, transaccion);
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task<int> BackendPidAsync(NpgsqlConnection conexion) =>
        (int)(await new NpgsqlCommand("SELECT pg_backend_pid()", conexion).ExecuteScalarAsync())!;

    private static async Task<bool> EsperarBackendBloqueadoAsync(NpgsqlConnection conexionPoll, int pidExcluido1, int pidExcluido2)
    {
        var limite = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < limite)
        {
            await using var comando = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND pid <> $1 AND pid <> $2",
                conexionPoll);
            comando.Parameters.AddWithValue(pidExcluido1);
            comando.Parameters.AddWithValue(pidExcluido2);

            if ((long)(await comando.ExecuteScalarAsync())! > 0)
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    private async Task CambiarPrivilegioAsync(string verbo, string tabla, string privilegios, string preposicion)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var comando = owner.CreateCommand();
        comando.CommandText = $"{verbo} {privilegios} ON {tabla} {preposicion} {RolApp}";
        await comando.ExecuteNonQueryAsync();
    }
}
