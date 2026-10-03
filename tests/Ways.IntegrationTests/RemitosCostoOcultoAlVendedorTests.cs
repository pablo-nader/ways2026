using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// El costo congelado del item de remito (<c>CostoUnitario</c>) lo ven admin y supervisor: el grupo
/// <c>/api/remitos</c> exige <c>OperacionDePos</c>, así que el vendedor recibe <c>null</c> tanto en
/// la respuesta de emitir como en el detalle.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class RemitosCostoOcultoAlVendedorTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOperador = "una-contraseña-larga";
    private const decimal CostoNominal = 61.5m;

    private static readonly DateTimeOffset InicioDeVigenciaFijo = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed record Escenario(HttpClient Admin, HttpClient Operador, SolicitudDeRemito Solicitud);

    private async Task<Escenario> PrepararAsync(string nombre, RolConocido rolOperador)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var respuesta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var hasheador = new HasheadorPbkdf2();
        var mailOperador = $"{nombre.ToLowerInvariant()}-operador@ways.test";
        int idArticulo;
        int idCliente;
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;
            var area = new Area { IdTenant = resultado.IdTenant, Nombre = $"{nombre}-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
            var lista = new ListaPrecio
            {
                IdTenant = resultado.IdTenant, Nombre = $"{nombre}-lista", EsDefault = false, Modo = ModoLista.Fija,
                Activo = true, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Areas.Add(area);
            db.ListasPrecio.Add(lista);
            await db.SaveChangesAsync();

            var articulo = new Articulo
            {
                IdTenant = resultado.IdTenant,
                CodigoInterno = "REM-COSTO-1",
                Nombre = "Artículo con costo",
                IdArea = area.Id,
                IdAlicuotaIva = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync(),
                UnidadVenta = UnidadVenta.Unidad,
                EsProducto = true,
                CostoNominal = CostoNominal,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };
            db.Articulos.Add(articulo);
            db.Usuarios.Add(new Usuario
            {
                IdTenant = resultado.IdTenant,
                NombreUsuario = "operador",
                Mail = mailOperador,
                RolId = (int)rolOperador,
                PasswordHash = hasheador.Hashear(PasswordOperador),
                PasswordAlgoritmo = hasheador.Algoritmo,
                PasswordActualizadoEl = ahora,
                CreatedAt = ahora,
                UpdatedAt = ahora
            });
            await db.SaveChangesAsync();

            db.Precios.Add(new Precio
            {
                IdTenant = resultado.IdTenant, IdArticulo = articulo.Id, IdListaPrecio = lista.Id, Monto = 100m,
                VigenteDesde = InicioDeVigenciaFijo, VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
            idArticulo = articulo.Id;

            var cliente = new Cliente
            {
                IdTenant = resultado.IdTenant, Numero = 2000 + Random.Shared.Next(1, 100_000), Nombre = $"{nombre}-cliente",
                IdCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync(), IdListaPrecio = lista.Id,
                LimiteCredito = 0, CreditoIlimitado = true, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Clientes.Add(cliente);
            await db.SaveChangesAsync();
            idCliente = cliente.Id;
        }

        var admin = fixture.CreateClient();
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal))).StatusCode);
        var operador = fixture.CreateClient();
        Assert.Equal(
            HttpStatusCode.OK,
            (await operador.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailOperador, PasswordOperador))).StatusCode);

        var solicitud = new SolicitudDeRemito(resultado.IdPuntoVenta, idCliente, null, null, [new LineaDeRemito(idArticulo, 2m, null)]);
        return new Escenario(admin, operador, solicitud);
    }

    private static async Task<RemitoDetalle> EmitirAsync(HttpClient cliente, SolicitudDeRemito solicitud)
    {
        var creado = await cliente.PostAsJsonAsync("/api/remitos", solicitud);
        Assert.Equal(HttpStatusCode.Created, creado.StatusCode);
        var borrador = (await creado.Content.ReadFromJsonAsync<RemitoDetalle>(OpcionesJson))!;

        var emitido = await cliente.PostAsync($"/api/remitos/{borrador.Id}/emitir", null);
        Assert.Equal(HttpStatusCode.OK, emitido.StatusCode);
        return (await emitido.Content.ReadFromJsonAsync<RemitoDetalle>(OpcionesJson))!;
    }

    [Theory]
    [InlineData(RolConocido.Admin)]
    [InlineData(RolConocido.Supervisor)]
    public async Task UnRolDeBackOfficeVeElCostoCongeladoAlEmitirYEnElDetalle(RolConocido rol)
    {
        var escenario = await PrepararAsync($"RemCostoVisible{rol}", RolConocido.Supervisor);
        var cliente = rol == RolConocido.Admin ? escenario.Admin : escenario.Operador;

        var emitido = await EmitirAsync(cliente, escenario.Solicitud);
        Assert.Equal(CostoNominal, Assert.Single(emitido.Items).CostoUnitario);

        var detalle = await cliente.GetFromJsonAsync<RemitoDetalle>($"/api/remitos/{emitido.Id}", OpcionesJson);
        Assert.Equal(CostoNominal, Assert.Single(detalle!.Items).CostoUnitario);
    }

    [Fact]
    public async Task ElVendedorRecibeElCostoCongeladoEnNull()
    {
        var escenario = await PrepararAsync(nameof(ElVendedorRecibeElCostoCongeladoEnNull), RolConocido.Vendedor);

        var emitido = await EmitirAsync(escenario.Operador, escenario.Solicitud);
        Assert.Null(Assert.Single(emitido.Items).CostoUnitario);

        var detalle = await escenario.Operador.GetFromJsonAsync<RemitoDetalle>($"/api/remitos/{emitido.Id}", OpcionesJson);
        Assert.Null(Assert.Single(detalle!.Items).CostoUnitario);

        var vistoPorAdmin = await escenario.Admin.GetFromJsonAsync<RemitoDetalle>($"/api/remitos/{emitido.Id}", OpcionesJson);
        Assert.Equal(CostoNominal, Assert.Single(vistoPorAdmin!.Items).CostoUnitario);
    }
}
