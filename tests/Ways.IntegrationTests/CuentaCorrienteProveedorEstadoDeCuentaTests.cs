using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.CuentaCorriente;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Catalogos;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-15-cc-proveedores-ledger, Slice 4: `GET /api/proveedores/{id}/cuenta-corriente`
/// PAGINADO (tasks 4.6-4.9, design decisión 10 / `state.yaml` OD9 — reconciliación de tasks.md
/// decisión 5) y su autorización (task 4.14). Mutation targets #25/#26 (tasks 4.16-4.17) —
/// evidencia de mutación registrada en el PR body, no en este archivo; los tests 4.8/4.7 son sus
/// discriminadores.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CuentaCorrienteProveedorEstadoDeCuentaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordVendedor = "vendedor-password-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, HttpClient Admin, HttpClient Vendedor, int IdProveedor, int IdEmpleadoAdmin);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var proveedor = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = nombre, IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        var mailVendedor = $"{nombre.ToLowerInvariant()}-vend@ways.test";
        var altaVendedor = await admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario("vendedor-cc-proveedor", mailVendedor, (int)RolConocido.Vendedor, PasswordVendedor));
        Assert.Equal(HttpStatusCode.Created, altaVendedor.StatusCode);

        var vendedor = fixture.CreateClient();
        var loginVendedor = await vendedor.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailVendedor, PasswordVendedor));
        Assert.Equal(HttpStatusCode.OK, loginVendedor.StatusCode);

        return new Contexto(resultado.IdTenant, resultado.IdPuntoVenta, admin, vendedor, proveedor.Id, resultado.IdUsuarioAdmin);
    }

    /// <summary>Seedea directo por EF un movimiento del ledger — bypassea
    /// <c>EscriturasDeCuentaCorrienteProveedor</c> a propósito: estos tests ejercitan el LADO DE
    /// LECTURA (filtros, orden, paginación), no la escritura (ya cubierta por Slices 2/3). Mantiene
    /// el invariante <c>proveedores.saldo == saldo_resultante</c> de la última fila escrita, para
    /// que el header también sea consistente.</summary>
    private Task<int> SembrarMovimientoAsync(
        Contexto ctx, TipoMovimientoCcProveedor tipo, DateTimeOffset fecha, decimal importe, string? detalle = null,
        int? idComprobanteCompra = null, int? idGasto = null) =>
        SembrarMovimientoAsync(ctx, ctx.IdProveedor, tipo, fecha, importe, detalle, idComprobanteCompra, idGasto);

    /// <summary>Sobrecarga con <paramref name="idProveedor"/> explícito — permite sembrar movimientos
    /// para UN SEGUNDO proveedor del MISMO tenant (judgment-day round 1, CRITICAL 3), algo que la
    /// sobrecarga atada a <c>ctx.IdProveedor</c> no puede expresar.</summary>
    private async Task<int> SembrarMovimientoAsync(
        Contexto ctx, int idProveedor, TipoMovimientoCcProveedor tipo, DateTimeOffset fecha, decimal importe,
        string? detalle = null, int? idComprobanteCompra = null, int? idGasto = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var proveedor = await db.Proveedores.FirstAsync(p => p.Id == idProveedor);
        var nuevoSaldo = proveedor.Saldo + importe;

        var movimiento = new MovimientoCuentaCorrienteProveedor
        {
            IdTenant = ctx.IdTenant,
            IdProveedor = idProveedor,
            Fecha = fecha,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdEmpleado = ctx.IdEmpleadoAdmin,
            Tipo = tipo,
            IdComprobanteCompra = idComprobanteCompra,
            IdGasto = idGasto,
            Importe = importe,
            SaldoResultante = nuevoSaldo,
            Detalle = detalle
        };
        db.MovimientosCuentaCorrienteProveedor.Add(movimiento);
        proveedor.Saldo = nuevoSaldo;
        await db.SaveChangesAsync();
        return movimiento.Id;
    }

    /// <summary>Crea un SEGUNDO proveedor en el MISMO tenant que <paramref name="ctx"/> — necesario
    /// para discriminar <c>Where(m => m.IdProveedor == idProveedor)</c> (judgment-day round 1,
    /// CRITICAL 3): todo el resto de este archivo siembra UN proveedor por tenant, por lo que borrar
    /// ese filtro sobrevive sin este test.</summary>
    private async Task<int> CrearSegundoProveedorEnElMismoTenantAsync(Contexto ctx, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var condicionFiscal = new CondicionFiscal
        {
            Codigo = $"{nombre}-CF-B", Nombre = $"{nombre}-B", CreatedAt = ahora, UpdatedAt = ahora
        };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var proveedorB = new Proveedor
        {
            IdTenant = ctx.IdTenant, RazonSocial = $"{nombre}-B", IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedorB);
        await db.SaveChangesAsync();
        return proveedorB.Id;
    }

    private static async Task<PaginaDeEstadoDeCuentaDeProveedor> ObtenerEstadoDeCuentaAsync(
        Contexto ctx, string query = "", HttpClient? cliente = null)
    {
        var respuesta = await (cliente ?? ctx.Admin).GetAsync(
            $"/api/proveedores/{ctx.IdProveedor}/cuenta-corriente{query}");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<PaginaDeEstadoDeCuentaDeProveedor>(cuerpo, OpcionesJson)!;
    }

    private static readonly DateTimeOffset Mediodia = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    // ---- task 4.7: filtros con seeds asimétricos — orden como SECUENCIA -----------------------

    [Fact]
    public async Task LosFiltrosDeFechaDevuelvenElSubconjuntoEnOrdenDeFechaDescendente()
    {
        var ctx = await PrepararAsync(nameof(LosFiltrosDeFechaDevuelvenElSubconjuntoEnOrdenDeFechaDescendente));

        var idViejo = await SembrarMovimientoAsync(ctx, TipoMovimientoCcProveedor.Ajuste, Mediodia.AddDays(-40), 100m, "fuera de rango (viejo)");
        var idDentroTemprano = await SembrarMovimientoAsync(ctx, TipoMovimientoCcProveedor.Ajuste, Mediodia.AddDays(-5), 200m, "dentro, temprano");
        var idDentroTarde = await SembrarMovimientoAsync(ctx, TipoMovimientoCcProveedor.Ajuste, Mediodia.AddDays(-1), 300m, "dentro, tarde");
        var idFuturo = await SembrarMovimientoAsync(ctx, TipoMovimientoCcProveedor.Ajuste, Mediodia.AddDays(5), 400m, "fuera de rango (futuro)");

        var pagina = await ObtenerEstadoDeCuentaAsync(
            ctx, $"?desde={Uri.EscapeDataString(Mediodia.AddDays(-10).ToString("O"))}&hasta={Uri.EscapeDataString(Mediodia.ToString("O"))}");

        Assert.Equal(2, pagina.Total);
        Assert.Collection(
            pagina.Items,
            m => Assert.Equal(idDentroTarde, m.IdMovimiento),
            m => Assert.Equal(idDentroTemprano, m.IdMovimiento));
        Assert.DoesNotContain(pagina.Items, m => m.IdMovimiento == idViejo || m.IdMovimiento == idFuturo);
    }

    // ---- task 4.8 / mutation target #25: fecha EMPATADA — el desempate id_movimiento DESC -------

    [Fact]
    public async Task ConFechaEmpatadaLaPaginacionDesempataPorIdMovimientoDescendenteSinDuplicarNiSaltear()
    {
        var ctx = await PrepararAsync(nameof(ConFechaEmpatadaLaPaginacionDesempataPorIdMovimientoDescendenteSinDuplicarNiSaltear));

        // Las tres filas comparten EXACTAMENTE la misma fecha (RelojFijo) — cobertura de spec
        // (task 4.8): la paginación no puede duplicar ni saltear filas bajo un empate. La prueba
        // de mutación real del target #25 es de texto fuente (ver deviación registrada en
        // tasks.md / apply-progress) — el orden de un empate SQL sin desempate explícito no es
        // determinista de forma observable en este entorno de test (Postgres puede resolverlo
        // por plan/estrategia de sort, no por garantía del estándar).
        var id1 = await SembrarMovimientoAsync(ctx, TipoMovimientoCcProveedor.Ajuste, Mediodia, 100m, "primero");
        var id2 = await SembrarMovimientoAsync(ctx, TipoMovimientoCcProveedor.Ajuste, Mediodia, 100m, "segundo");
        var id3 = await SembrarMovimientoAsync(ctx, TipoMovimientoCcProveedor.Ajuste, Mediodia, 100m, "tercero");

        var pagina1 = await ObtenerEstadoDeCuentaAsync(ctx, "?historico=true&pagina=1&tamanio=2");
        Assert.Equal(3, pagina1.Total);
        Assert.Collection(
            pagina1.Items,
            m => Assert.Equal(id3, m.IdMovimiento),
            m => Assert.Equal(id2, m.IdMovimiento));

        var pagina2 = await ObtenerEstadoDeCuentaAsync(ctx, "?historico=true&pagina=2&tamanio=2");
        Assert.Equal(3, pagina2.Total);
        var unica = Assert.Single(pagina2.Items);
        Assert.Equal(id1, unica.IdMovimiento);
    }

    // ---- task 4.9: histórico gana sobre desde/hasta; sin filtro ⇒ último mes; ledger vacío -------

    [Fact]
    public async Task HistoricoIgnoraDesdeYHastaYDevuelveTodo()
    {
        var ctx = await PrepararAsync(nameof(HistoricoIgnoraDesdeYHastaYDevuelveTodo));
        await SembrarMovimientoAsync(ctx, TipoMovimientoCcProveedor.Ajuste, Mediodia.AddYears(-2), 500m, "muy viejo");

        // desde/hasta acotan a un rango que EXCLUYE el movimiento — historico=true los ignora.
        var pagina = await ObtenerEstadoDeCuentaAsync(
            ctx, $"?historico=true&desde={Uri.EscapeDataString(Mediodia.ToString("O"))}&hasta={Uri.EscapeDataString(Mediodia.ToString("O"))}");

        Assert.Equal(1, pagina.Total);
        Assert.True(pagina.Historico);
        Assert.Null(pagina.Desde);
        Assert.Null(pagina.Hasta);
    }

    [Fact]
    public async Task SinFiltroAplicaElDefaultDeUltimoMes()
    {
        var ctx = await PrepararAsync(nameof(SinFiltroAplicaElDefaultDeUltimoMes));
        var idReciente = await SembrarMovimientoAsync(ctx, TipoMovimientoCcProveedor.Ajuste, DateTimeOffset.UtcNow.AddDays(-2), 100m, "reciente");
        await SembrarMovimientoAsync(ctx, TipoMovimientoCcProveedor.Ajuste, DateTimeOffset.UtcNow.AddMonths(-3), 200m, "viejo");

        var pagina = await ObtenerEstadoDeCuentaAsync(ctx);

        Assert.False(pagina.Historico);
        Assert.NotNull(pagina.Desde);
        Assert.Null(pagina.Hasta);
        var unica = Assert.Single(pagina.Items);
        Assert.Equal(idReciente, unica.IdMovimiento);
    }

    [Fact]
    public async Task UnProveedorSinMovimientosDevuelveUnaPaginaVaciaConElHeaderPoblado()
    {
        var ctx = await PrepararAsync(nameof(UnProveedorSinMovimientosDevuelveUnaPaginaVaciaConElHeaderPoblado));

        var pagina = await ObtenerEstadoDeCuentaAsync(ctx);

        Assert.Equal(0, pagina.Total);
        Assert.Empty(pagina.Items);
        Assert.Equal(ctx.IdProveedor, pagina.Header.IdProveedor);
        Assert.Equal(0m, pagina.Header.Saldo);
    }

    // ---- regla 10 de mutation-proof-tests: offset real -03:00 en el filtro de fecha, nunca Z -----

    [Fact]
    public async Task UnFiltroHastaConOffsetRealMenosTresIncluyeUnMovimientoQueUnFiltroEnUtcExcluiria()
    {
        var ctx = await PrepararAsync(nameof(UnFiltroHastaConOffsetRealMenosTresIncluyeUnMovimientoQueUnFiltroEnUtcExcluiria));

        // 2026-08-17T23:30:00-03:00 == 2026-08-18T02:30:00Z (spec: A date-boundary filter uses
        // the client's real offset, not UTC).
        var fechaDelMovimiento = new DateTimeOffset(2026, 8, 17, 23, 30, 0, TimeSpan.FromHours(-3));
        Assert.Equal(new DateTimeOffset(2026, 8, 18, 2, 30, 0, TimeSpan.Zero), fechaDelMovimiento);
        var idMovimiento = await SembrarMovimientoAsync(ctx, TipoMovimientoCcProveedor.Ajuste, fechaDelMovimiento, 100m, "borde -03:00");

        // hasta = el mismo día calendario en -03:00, offset REAL del cliente, nunca `Z`.
        var hastaConOffsetReal = "2026-08-17T23:59:59-03:00";
        var pagina = await ObtenerEstadoDeCuentaAsync(
            ctx, $"?historico=true&desde=2026-08-01T00:00:00-03:00&hasta={Uri.EscapeDataString(hastaConOffsetReal)}");

        var unica = Assert.Single(pagina.Items);
        Assert.Equal(idMovimiento, unica.IdMovimiento);
    }

    // ---- task 4.14: autorización — Vendedor lee, tenant B nunca ve al proveedor de A -------------

    [Fact]
    public async Task UnVendedorLeeElEstadoDeCuentaDeUnProveedor()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorLeeElEstadoDeCuentaDeUnProveedor));

        var respuesta = await ctx.Vendedor.GetAsync($"/api/proveedores/{ctx.IdProveedor}/cuenta-corriente");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnProveedorDeOtroTenantDevuelve404EnElEstadoDeCuenta()
    {
        var ctxA = await PrepararAsync(nameof(UnProveedorDeOtroTenantDevuelve404EnElEstadoDeCuenta) + "-A");
        var ctxB = await PrepararAsync(nameof(UnProveedorDeOtroTenantDevuelve404EnElEstadoDeCuenta) + "-B");

        var respuesta = await ctxA.Admin.GetAsync($"/api/proveedores/{ctxB.IdProveedor}/cuenta-corriente");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    // ---- judgment-day round 1, CRITICAL 2: SaldoResultante por fila, corrida discriminante --------

    /// <summary>
    /// Ningún otro test assertea <c>MovimientoDeCuentaDeProveedor.SaldoResultante</c> — corrida con
    /// deuda previa ≠ 0 y VARIOS movimientos cuyos <c>saldo_resultante</c> son distintos entre sí y
    /// de sus propios importes (mutation-proof-tests rule 6, lección del slice 2): una fila con
    /// <c>SaldoResultante = 0m</c> hardcodeado, o la corrida acumulada, quedaría sin detectar.
    /// </summary>
    [Fact]
    public async Task LosItemsDevuelvenElSaldoResultanteAcumuladoPorFila()
    {
        var ctx = await PrepararAsync(nameof(LosItemsDevuelvenElSaldoResultanteAcumuladoPorFila));

        // Deuda previa != 0, fuera de la corrida assertada.
        await SembrarMovimientoAsync(ctx, TipoMovimientoCcProveedor.Ajuste, Mediodia.AddDays(-10), 850m, "deuda previa");

        // Corrida bajo prueba: saldo_resultante acumulado 850 -> 1190 -> 1070 -> 1680, todos
        // distintos entre sí y distintos de sus propios importes (340, -120, 610).
        var idMov1 = await SembrarMovimientoAsync(ctx, TipoMovimientoCcProveedor.Ajuste, Mediodia.AddDays(-3), 340m, "incrementa");
        var idMov2 = await SembrarMovimientoAsync(ctx, TipoMovimientoCcProveedor.Ajuste, Mediodia.AddDays(-2), -120m, "reduce");
        var idMov3 = await SembrarMovimientoAsync(ctx, TipoMovimientoCcProveedor.Ajuste, Mediodia.AddDays(-1), 610m, "incrementa de nuevo");

        var pagina = await ObtenerEstadoDeCuentaAsync(ctx, "?historico=true&pagina=1&tamanio=10");

        Assert.Equal(1190m, pagina.Items.Single(m => m.IdMovimiento == idMov1).SaldoResultante);
        Assert.Equal(1070m, pagina.Items.Single(m => m.IdMovimiento == idMov2).SaldoResultante);
        Assert.Equal(1680m, pagina.Items.Single(m => m.IdMovimiento == idMov3).SaldoResultante);
        Assert.Equal(1680m, pagina.Header.Saldo);
    }

    // ---- judgment-day round 1, CRITICAL 3: un SEGUNDO proveedor del MISMO tenant no se filtra ----

    /// <summary>
    /// Borrar <c>Where(m => m.IdProveedor == idProveedor)</c> sobrevive a todo el resto de este
    /// archivo porque cada test siembra UN proveedor por tenant. Este test siembra un SEGUNDO
    /// proveedor del MISMO tenant con sus propios movimientos y assertea que el estado de cuenta
    /// del proveedor A trae EXACTAMENTE los de A — conteo exacto, identificación de filas, y el
    /// total de B es distinto y detectable si se filtrara de más (o de menos).
    /// </summary>
    [Fact]
    public async Task UnSegundoProveedorDelMismoTenantNoContaminaElEstadoDeCuenta()
    {
        var ctx = await PrepararAsync(nameof(UnSegundoProveedorDelMismoTenantNoContaminaElEstadoDeCuenta));
        var idProveedorB = await CrearSegundoProveedorEnElMismoTenantAsync(
            ctx, nameof(UnSegundoProveedorDelMismoTenantNoContaminaElEstadoDeCuenta));

        var idA1 = await SembrarMovimientoAsync(ctx, ctx.IdProveedor, TipoMovimientoCcProveedor.Ajuste, Mediodia.AddDays(-3), 111m, "A-uno");
        var idA2 = await SembrarMovimientoAsync(ctx, ctx.IdProveedor, TipoMovimientoCcProveedor.Ajuste, Mediodia.AddDays(-2), 222m, "A-dos");

        // Proveedor B: DOS movimientos también, con un total (555) distinto del de A (333) — si el
        // filtro se pierde, Total pasaría de 2 a 4 y el saldo del header dejaría de coincidir.
        await SembrarMovimientoAsync(ctx, idProveedorB, TipoMovimientoCcProveedor.Ajuste, Mediodia.AddDays(-3), 333m, "B-uno");
        await SembrarMovimientoAsync(ctx, idProveedorB, TipoMovimientoCcProveedor.Ajuste, Mediodia.AddDays(-2), 222m, "B-dos");

        var pagina = await ObtenerEstadoDeCuentaAsync(ctx, "?historico=true&pagina=1&tamanio=10");

        Assert.Equal(2, pagina.Total);
        Assert.Equal(2, pagina.Items.Count);
        Assert.Collection(
            pagina.Items,
            m => Assert.Equal(idA2, m.IdMovimiento),
            m => Assert.Equal(idA1, m.IdMovimiento));
        Assert.Equal(333m, pagina.Header.Saldo);
        Assert.DoesNotContain(pagina.Items, m => m.Detalle != null && m.Detalle.StartsWith("B-"));
    }
}
