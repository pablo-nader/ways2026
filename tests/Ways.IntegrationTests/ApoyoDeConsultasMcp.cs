using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Siembra y llamadas de las pruebas de las herramientas de ventas del conector MCP: un tenant
/// aprovisionado como lo hace la plataforma (empresa, punto de venta, medios de pago y cliente),
/// usuarios por rol con la contraseña de <see cref="ApoyoDeConectorMcp"/>, comprobantes y pagos
/// sembrados directo con fecha explícita, y la llamada a una herramienta con su texto y su marca de error.
/// </summary>
internal sealed class ApoyoDeConsultasMcp(WaysApiFixture fixture, ApoyoDeConectorMcp conector)
{
    private static long _numeroSecuencial = 5_000_000;

    public sealed record TenantDeVentas(
        int IdTenant, string NombreTenant, int IdEmpresa, int IdPuntoVenta, int IdCliente,
        int IdTipoTx, int IdTipoNcx, int IdMedioEfectivo, string NombreEfectivo, int IdMedioTransferencia,
        string NombreTransferencia);

    public sealed record ResultadoDeHerramienta(bool EsError, string Texto);

    public sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    public async Task<TenantDeVentas> SembrarTenantAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(ApoyoDeConectorMcp.MailRoot, ApoyoDeConectorMcp.PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var unico = $"{nombre}-{Guid.NewGuid().ToString("N")[..8]}".ToLowerInvariant();
        var solicitud = new SolicitudDeAprovisionamiento(unico, $"{unico} SA", "Local 1", $"{unico}@ways.test", ModoPuntoVenta.Web);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        await using var dbTenant = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idCliente = await dbTenant.Clientes.Select(c => c.Id).FirstAsync();
        var medios = await dbTenant.MediosPago.OrderBy(m => m.Orden).Select(m => new { m.Id, m.Nombre }).ToListAsync();

        await using var dbPlataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idTx = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).SingleAsync();
        var idNcx = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "NCX").Select(t => t.Id).SingleAsync();

        return new TenantDeVentas(
            resultado.IdTenant, unico, resultado.IdEmpresa, resultado.IdPuntoVenta, idCliente, idTx, idNcx,
            medios[0].Id, medios[0].Nombre, medios[1].Id, medios[1].Nombre);
    }

    /// <summary>Un usuario del tenant con el rol dado y la contraseña del conector, para autorizar por OAuth.</summary>
    public async Task<ApoyoDeConectorMcp.UsuarioDeTenant> SembrarUsuarioAsync(TenantDeVentas tenant, RolConocido rol)
    {
        using var _ = fixture.CreateClient();
        var unico = $"{rol}-{Guid.NewGuid().ToString("N")[..8]}".ToLowerInvariant();
        var ahora = DateTimeOffset.UtcNow;
        var hasheador = new HasheadorPbkdf2();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var usuario = new Usuario
        {
            IdTenant = tenant.IdTenant,
            NombreUsuario = $"{unico}",
            Mail = $"{unico}@ways.test",
            RolId = (int)rol,
            PasswordHash = hasheador.Hashear(ApoyoDeConectorMcp.Password),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Usuarios.Add(usuario);
        await db.SaveChangesAsync();

        return new ApoyoDeConectorMcp.UsuarioDeTenant(tenant.IdTenant, tenant.NombreTenant, usuario.Id, usuario.Mail);
    }

    public async Task<int> SembrarEmpresaAsync(TenantDeVentas tenant, string razonSocial)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, tenant.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var empresa = new Empresa { IdTenant = tenant.IdTenant, RazonSocial = razonSocial, CreatedAt = ahora, UpdatedAt = ahora };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();
        return empresa.Id;
    }

    public async Task<int> SembrarPuntoVentaAsync(TenantDeVentas tenant, string nombre, int? idEmpresa = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, tenant.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var punto = new PuntoVenta
        {
            IdTenant = tenant.IdTenant,
            IdEmpresa = idEmpresa ?? tenant.IdEmpresa,
            Nombre = nombre,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.PuntosVenta.Add(punto);
        await db.SaveChangesAsync();
        return punto.Id;
    }

    /// <summary>Siembra directo, sin pasar por el servicio de ventas. Un NCX lleva el total negativo, como
    /// lo guarda el sistema.</summary>
    public async Task<int> SembrarComprobanteAsync(
        TenantDeVentas tenant, DateTimeOffset fecha, decimal total, int idEmpleado, int? idPuntoVenta = null,
        bool esNcx = false, EstadoComprobante estado = EstadoComprobante.Emitido)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, tenant.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var comprobante = new ComprobanteVenta
        {
            IdTenant = tenant.IdTenant,
            IdTipoComprobante = esNcx ? tenant.IdTipoNcx : tenant.IdTipoTx,
            Numero = Interlocked.Increment(ref _numeroSecuencial),
            Fecha = fecha,
            IdPuntoVenta = idPuntoVenta ?? tenant.IdPuntoVenta,
            IdEmpleado = idEmpleado,
            IdCliente = tenant.IdCliente,
            Subtotal = total,
            DescuentoTotal = 0m,
            Total = total,
            Estado = estado,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();
        return comprobante.Id;
    }

    public sealed record LineaSembrada(int? IdArticulo, string Descripcion, int IdArea, decimal Cantidad, decimal Total);

    private static TenantActualFijo DeTenant(TenantDeVentas tenant) => new(ModoDeAcceso.Tenant, tenant.IdTenant);

    public async Task<int> SembrarAreaAsync(TenantDeVentas tenant, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(DeTenant(tenant));
        var ahora = DateTimeOffset.UtcNow;
        var area = new Area { IdTenant = tenant.IdTenant, Nombre = nombre, Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();
        return area.Id;
    }

    public async Task<int> SembrarCategoriaAsync(TenantDeVentas tenant, string nombre, int? idEmpresa = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(DeTenant(tenant));
        var ahora = DateTimeOffset.UtcNow;
        var categoria = new Categoria
        {
            IdTenant = tenant.IdTenant, Nombre = nombre, Orden = 1, IdEmpresa = idEmpresa, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Categorias.Add(categoria);
        await db.SaveChangesAsync();
        return categoria.Id;
    }

    public async Task<int> SembrarMarcaAsync(TenantDeVentas tenant, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(DeTenant(tenant));
        var ahora = DateTimeOffset.UtcNow;
        var marca = new Marca { IdTenant = tenant.IdTenant, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Marcas.Add(marca);
        await db.SaveChangesAsync();
        return marca.Id;
    }

    public async Task<int> SembrarProveedorAsync(TenantDeVentas tenant, string razonSocial)
    {
        await using var db = fixture.CrearContextoDeAplicacion(DeTenant(tenant));
        var ahora = DateTimeOffset.UtcNow;
        var proveedor = new Proveedor
        {
            IdTenant = tenant.IdTenant,
            RazonSocial = razonSocial,
            IdCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync(),
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();
        return proveedor.Id;
    }

    public async Task<int> SembrarArticuloAsync(
        TenantDeVentas tenant, string nombre, string codigo, int idArea, int? idCategoria = null, int? idMarca = null,
        int? idProveedor = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(DeTenant(tenant));
        var ahora = DateTimeOffset.UtcNow;
        var articulo = new Articulo
        {
            IdTenant = tenant.IdTenant,
            CodigoInterno = codigo,
            Nombre = nombre,
            IdArea = idArea,
            IdCategoria = idCategoria,
            IdMarca = idMarca,
            IdProveedorHabitual = idProveedor,
            IdAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync(),
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();
        return articulo.Id;
    }

    /// <summary>Baja lógica de una categoría que los artículos siguen referenciando: es el dato heredado que
    /// los listados tienen que tolerar (el borrado por la API lo rechaza con 409).</summary>
    public async Task DarDeBajaCategoriaAsync(TenantDeVentas tenant, int idCategoria)
    {
        await using var db = fixture.CrearContextoDeAplicacion(DeTenant(tenant));
        var ahora = DateTimeOffset.UtcNow;
        var filas = await db.Categorias.Where(c => c.Id == idCategoria).ExecuteUpdateAsync(c => c.SetProperty(x => x.DeletedAt, ahora));
        Assert.Equal(1, filas);
    }

    public async Task CambiarCategoriaDeArticuloAsync(TenantDeVentas tenant, int idArticulo, int idCategoria)
    {
        await using var db = fixture.CrearContextoDeAplicacion(DeTenant(tenant));
        var filas = await db.Articulos.Where(a => a.Id == idArticulo).ExecuteUpdateAsync(a => a.SetProperty(x => x.IdCategoria, (int?)idCategoria));
        Assert.Equal(1, filas);
    }

    public async Task DarDeBajaArticuloAsync(TenantDeVentas tenant, int idArticulo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(DeTenant(tenant));
        var ahora = DateTimeOffset.UtcNow;
        var filas = await db.Articulos.Where(a => a.Id == idArticulo).ExecuteUpdateAsync(a => a.SetProperty(x => x.DeletedAt, ahora));
        Assert.Equal(1, filas);
    }

    /// <summary>Un comprobante con las líneas dadas, cuyo total es la suma de los totales de las líneas.
    /// Cantidad y total de cada línea llevan el signo (negativos en una NCX).</summary>
    public async Task SembrarComprobanteConLineasAsync(
        TenantDeVentas tenant, DateTimeOffset fecha, int idEmpleado, IReadOnlyList<LineaSembrada> lineas,
        bool esNcx = false, EstadoComprobante estado = EstadoComprobante.Emitido, int? idPuntoVenta = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(DeTenant(tenant));
        var ahora = DateTimeOffset.UtcNow;
        var total = lineas.Sum(l => l.Total);

        var comprobante = new ComprobanteVenta
        {
            IdTenant = tenant.IdTenant,
            IdTipoComprobante = esNcx ? tenant.IdTipoNcx : tenant.IdTipoTx,
            Numero = Interlocked.Increment(ref _numeroSecuencial),
            Fecha = fecha,
            IdPuntoVenta = idPuntoVenta ?? tenant.IdPuntoVenta,
            IdEmpleado = idEmpleado,
            IdCliente = tenant.IdCliente,
            Subtotal = total,
            DescuentoTotal = 0m,
            Total = total,
            Estado = estado,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        var idListaPrecio = await db.ListasPrecio.Select(l => l.Id).FirstAsync();
        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();
        var orden = 0;
        foreach (var linea in lineas)
        {
            db.ItemsComprobanteVenta.Add(new ItemComprobanteVenta
            {
                IdTenant = tenant.IdTenant,
                IdComprobanteVenta = comprobante.Id,
                Orden = ++orden,
                IdArticulo = linea.IdArticulo,
                Descripcion = linea.Descripcion,
                IdArea = linea.IdArea,
                IdListaPrecio = idListaPrecio,
                IdAlicuotaIva = idAlicuotaIva,
                PorcentajeIva = 0m,
                Cantidad = linea.Cantidad,
                PrecioUnitario = linea.Total / linea.Cantidad,
                Descuento = 0m,
                Total = linea.Total,
                CreatedAt = ahora,
                UpdatedAt = ahora
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary><c>importe</c> es lo entregado por el cliente (nunca negativo) y <c>vuelto</c> lo devuelto;
    /// el signo de una NCX lo aporta el tipo del comprobante.</summary>
    public async Task SembrarPagoAsync(TenantDeVentas tenant, int idComprobante, int idMedioPago, decimal importe, decimal vuelto = 0m)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, tenant.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        db.PagosComprobante.Add(new PagoComprobante
        {
            IdTenant = tenant.IdTenant,
            IdComprobanteVenta = idComprobante,
            IdMedioPago = idMedioPago,
            Importe = importe,
            Vuelto = vuelto,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    public async Task<int> IdDeLaListaGeneralAsync(TenantDeVentas tenant)
    {
        await using var db = fixture.CrearContextoDeAplicacion(DeTenant(tenant));
        return await db.ListasPrecio.Where(l => l.EsDefault).Select(l => l.Id).SingleAsync();
    }

    public async Task<int> SembrarListaDePreciosAsync(
        TenantDeVentas tenant, string nombre, ModoLista modo = ModoLista.Fija, int? idListaBase = null,
        decimal? porcentaje = null, bool activa = true, int? idEmpresa = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(DeTenant(tenant));
        var ahora = DateTimeOffset.UtcNow;
        var lista = new ListaPrecio
        {
            IdTenant = tenant.IdTenant,
            Nombre = nombre,
            Activo = activa,
            Modo = modo,
            IdListaBase = idListaBase,
            Porcentaje = porcentaje,
            IdEmpresa = idEmpresa,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();
        return lista.Id;
    }

    /// <summary>Una fila del historial de precios tal cual: <paramref name="hasta"/> nulo es la fila abierta.</summary>
    public async Task SembrarPrecioAsync(
        TenantDeVentas tenant, int idArticulo, int idLista, decimal monto, DateTimeOffset desde, DateTimeOffset? hasta = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(DeTenant(tenant));
        var ahora = DateTimeOffset.UtcNow;
        db.Precios.Add(new Precio
        {
            IdTenant = tenant.IdTenant,
            IdArticulo = idArticulo,
            IdListaPrecio = idLista,
            Monto = monto,
            VigenteDesde = desde,
            VigenteHasta = hasta,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    public async Task SembrarStockAsync(
        TenantDeVentas tenant, int idArticulo, int idPuntoVenta, decimal cantidad, decimal? minimo = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(DeTenant(tenant));
        db.Stock.Add(new Ways.Domain.Stock.Stock
        {
            IdTenant = tenant.IdTenant,
            IdArticulo = idArticulo,
            IdPuntoVenta = idPuntoVenta,
            Cantidad = cantidad,
            Minimo = minimo
        });
        await db.SaveChangesAsync();
    }

    public sealed record LineaDeCompraSembrada(
        int IdArticulo, decimal Cantidad, decimal CostoUnitario, decimal Descuento = 0m, decimal PorcentajeIva = 0m);

    /// <summary>Una compra con tipo C-FA. Un borrador no lleva número ni fechas, como lo exige el esquema; el resto
    /// los lleva todos. El total de cada línea es <c>cantidad × costo − descuento</c>.</summary>
    public async Task<int> SembrarCompraAsync(
        TenantDeVentas tenant, int idProveedor, int idEmpleado, EstadoCompra estado, DateTimeOffset recepcion,
        DateOnly fechaComprobante, string numero, IReadOnlyList<LineaDeCompraSembrada> lineas, int? idPuntoVenta = null,
        bool discriminaIva = false, bool preciosIncluyenIva = false)
    {
        await using var dbPlataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idTipo = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();

        await using var db = fixture.CrearContextoDeAplicacion(DeTenant(tenant));
        var ahora = DateTimeOffset.UtcNow;
        var esBorrador = estado == EstadoCompra.Borrador;
        var bruto = lineas.Sum(l => l.Cantidad * l.CostoUnitario);
        var descuento = lineas.Sum(l => l.Descuento);

        var compra = new ComprobanteCompra
        {
            IdTenant = tenant.IdTenant,
            IdProveedor = idProveedor,
            IdTipoComprobante = idTipo,
            NumeroExterno = esBorrador ? null : numero,
            FechaComprobante = esBorrador ? null : fechaComprobante,
            FechaRecepcion = esBorrador ? null : recepcion,
            IdPuntoVenta = idPuntoVenta ?? tenant.IdPuntoVenta,
            IdEmpleado = idEmpleado,
            Subtotal = bruto,
            DescuentoTotal = descuento,
            Total = bruto - descuento,
            DiscriminaIva = discriminaIva,
            PreciosIncluyenIva = preciosIncluyenIva,
            Estado = estado,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesCompra.Add(compra);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();
        var orden = 0;
        foreach (var linea in lineas)
        {
            db.ItemsComprobanteCompra.Add(new ItemComprobanteCompra
            {
                IdTenant = tenant.IdTenant,
                IdComprobanteCompra = compra.Id,
                Orden = ++orden,
                IdArticulo = linea.IdArticulo,
                Descripcion = "Línea de compra",
                Cantidad = linea.Cantidad,
                CostoUnitario = linea.CostoUnitario,
                Descuento = linea.Descuento,
                IdAlicuotaIva = idAlicuotaIva,
                PorcentajeIva = linea.PorcentajeIva,
                Total = linea.Cantidad * linea.CostoUnitario - linea.Descuento,
                ActualizaCosto = false,
                CreatedAt = ahora,
                UpdatedAt = ahora
            });
        }

        await db.SaveChangesAsync();
        return compra.Id;
    }

    /// <summary>Baja lógica de un punto de venta que todavía tiene stock: el dato heredado que la ficha tiene que tolerar.</summary>
    public async Task DarDeBajaPuntoVentaAsync(TenantDeVentas tenant, int idPuntoVenta)
    {
        await using var db = fixture.CrearContextoDeAplicacion(DeTenant(tenant));
        var ahora = DateTimeOffset.UtcNow;
        var filas = await db.PuntosVenta.Where(p => p.Id == idPuntoVenta).ExecuteUpdateAsync(p => p.SetProperty(x => x.DeletedAt, ahora));
        Assert.Equal(1, filas);
    }

    /// <summary>Host del conector con un reloj fijo y los mails dados habilitados.</summary>
    public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> HostConReloj(
        DateTimeOffset ahora, params string[] mails) =>
        conector.HostConConector(
            string.Join(';', mails),
            configurarMas: builder => builder.ConfigureServices(
                servicios => servicios.AddSingleton<IRelojDelSistema>(new RelojFijo(ahora))));

    public static async Task<string> AccessTokenAsync(HttpClient cliente, string mail) =>
        (await ApoyoDeConectorMcp.ObtenerTokensAsync(cliente, mail)).GetProperty("access_token").GetString()!;

    public static async Task<ResultadoDeHerramienta> LlamarHerramientaAsync(
        HttpClient cliente, string accessToken, string herramienta, object argumentos)
    {
        var (estado, mensaje) = await ApoyoDeConectorMcp.LlamarMcpAsync(
            cliente, accessToken, "tools/call", new { name = herramienta, arguments = argumentos });
        Assert.Equal(HttpStatusCode.OK, estado);

        var resultado = mensaje!.Value.GetProperty("result");
        var esError = resultado.TryGetProperty("isError", out var marca) && marca.ValueKind == JsonValueKind.True;
        return new ResultadoDeHerramienta(esError, resultado.GetProperty("content")[0].GetProperty("text").GetString()!);
    }
}
