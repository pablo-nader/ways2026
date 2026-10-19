using System.Text.RegularExpressions;
using Ways.Application.Abstracciones;
using Ways.Application.Parametros;
using Ways.Application.Reportes;
using Ways.Application.Stock;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using static Ways.IntegrationTests.ApoyoDeConsultasMcp;

namespace Ways.IntegrationTests;

/// <summary>
/// Las herramientas <c>ventas_resumen</c> y <c>ventas_agrupadas</c> de punta a punta: token OAuth real,
/// <c>POST /mcp</c>, Postgres real como <c>ways_app</c> (RLS activo) y un reloj fijo. Los importes están
/// calculados a mano en cada prueba. El reloj fijo es 2026-10-18T12:00Z, las 09:00 del 18 en Buenos Aires
/// (la zona por defecto de las empresas aprovisionadas).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public partial class ConectorMcpVentasTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private static readonly DateTimeOffset RelojDelMediodia = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly ApoyoDeConectorMcp _conector = new(fixture);
    private readonly ApoyoDeConsultasMcp _apoyo = new(fixture, new ApoyoDeConectorMcp(fixture));

    private sealed record Escenario(
        TenantDeVentas Tenant, ApoyoDeConectorMcp.UsuarioDeTenant Admin, ApoyoDeConectorMcp.UsuarioDeTenant Supervisor,
        ApoyoDeConectorMcp.UsuarioDeTenant Vendedor, int IdPuntoVentaSucursal);

    private static string NombreDeUsuario(ApoyoDeConectorMcp.UsuarioDeTenant usuario) => usuario.Mail.Split('@')[0];

    private static string[] Lineas(string texto) => texto.Split(Environment.NewLine);

    private static string[] FilasDeFecha(string texto) =>
        [.. Lineas(texto).Where(l => FilaConFecha().IsMatch(l))];

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} \| ")]
    private static partial Regex FilaConFecha();

    private async Task<Escenario> SembrarEscenarioAsync(string nombre)
    {
        var tenant = await _apoyo.SembrarTenantAsync(nombre);
        var admin = await _apoyo.SembrarUsuarioAsync(tenant, RolConocido.Admin);
        var supervisor = await _apoyo.SembrarUsuarioAsync(tenant, RolConocido.Supervisor);
        var vendedor = await _apoyo.SembrarUsuarioAsync(tenant, RolConocido.Vendedor);
        var sucursal = await _apoyo.SembrarPuntoVentaAsync(tenant, "Sucursal");
        return new Escenario(tenant, admin, supervisor, vendedor, sucursal);
    }

    /// <summary>Hoy (18/10 local): 1000 + 500 + 300 − 100 = 1700 en 3 comprobantes y una NC, más una venta
    /// anulada y una del día anterior que no cuentan.</summary>
    private async Task SembrarVentasDeHoyAsync(Escenario e)
    {
        var t = e.Tenant;
        var s1 = await _apoyo.SembrarComprobanteAsync(t, new(2026, 10, 18, 10, 0, 0, TimeSpan.Zero), 1000m, e.Admin.IdUsuario);
        await _apoyo.SembrarPagoAsync(t, s1, t.IdMedioEfectivo, 1000m);
        var s2 = await _apoyo.SembrarComprobanteAsync(t, new(2026, 10, 18, 11, 0, 0, TimeSpan.Zero), 500m, e.Supervisor.IdUsuario);
        await _apoyo.SembrarPagoAsync(t, s2, t.IdMedioTransferencia, 500m);
        var s3 = await _apoyo.SembrarComprobanteAsync(
            t, new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero), 300m, e.Admin.IdUsuario, e.IdPuntoVentaSucursal);
        await _apoyo.SembrarPagoAsync(t, s3, t.IdMedioEfectivo, 300m);
        var ncx = await _apoyo.SembrarComprobanteAsync(
            t, new(2026, 10, 18, 11, 30, 0, TimeSpan.Zero), -100m, e.Supervisor.IdUsuario, esNcx: true);
        await _apoyo.SembrarPagoAsync(t, ncx, t.IdMedioEfectivo, 100m);
        var anulada = await _apoyo.SembrarComprobanteAsync(
            t, new(2026, 10, 18, 10, 30, 0, TimeSpan.Zero), 9999m, e.Admin.IdUsuario, estado: EstadoComprobante.Anulado);
        await _apoyo.SembrarPagoAsync(t, anulada, t.IdMedioEfectivo, 9999m);
        await _apoyo.SembrarComprobanteAsync(t, new(2026, 10, 17, 15, 0, 0, TimeSpan.Zero), 777m, e.Admin.IdUsuario);
    }

    [Fact]
    public async Task VentasResumenSumaLoVendidoDelTenantYNuncaMuestraLasVentasDeOtro()
    {
        var a = await SembrarEscenarioAsync(nameof(VentasResumenSumaLoVendidoDelTenantYNuncaMuestraLasVentasDeOtro) + "-a");
        await SembrarVentasDeHoyAsync(a);

        // Otro tenant con una venta enorme hoy, el mismo nombre de punto de venta ("Local 1") y su propio efectivo.
        var b = await _apoyo.SembrarTenantAsync(nameof(VentasResumenSumaLoVendidoDelTenantYNuncaMuestraLasVentasDeOtro) + "-b");
        var adminB = await _apoyo.SembrarUsuarioAsync(b, RolConocido.Admin);
        var ventaB = await _apoyo.SembrarComprobanteAsync(b, new(2026, 10, 18, 10, 0, 0, TimeSpan.Zero), 123456m, adminB.IdUsuario);
        await _apoyo.SembrarPagoAsync(b, ventaB, b.IdMedioEfectivo, 123456m);

        await using var host = _apoyo.HostConReloj(RelojDelMediodia, a.Supervisor.Mail);
        using var cliente = ApoyoDeConectorMcp.Cliente(host);
        var token = await AccessTokenAsync(cliente, a.Supervisor.Mail);

        var resultado = await LlamarHerramientaAsync(cliente, token, "ventas_resumen", new { periodo = "hoy" });

        Assert.False(resultado.EsError, resultado.Texto);
        var lineas = Lineas(resultado.Texto);
        Assert.StartsWith("Alcance: empresa ", lineas[1]);
        Assert.Equal(
            [
                "Ventas hoy (2026-10-18), zona horaria: America/Argentina/Buenos_Aires",
                lineas[1],
                "Total vendido: 1.700,00",
                "Comprobantes: 3",
                "Ticket promedio: 600,00",
                "Notas de crédito: 1 por 100,00 (ya restadas del total)",
                "",
                "Por medio de pago (lo cobrado neto del vuelto):",
                "medio | importe | pagos",
                $"{a.Tenant.NombreEfectivo} | 1.200,00 | 3",
                $"{a.Tenant.NombreTransferencia} | 500,00 | 1",
                "",
                "Por punto de venta:",
                "punto de venta | importe | comprobantes",
                "Local 1 | 1.400,00 | 2",
                "Sucursal | 300,00 | 1",
                "",
                "Por día:",
                "fecha | importe | comprobantes",
                "2026-10-18 | 1.700,00 | 3"
            ],
            lineas);
    }

    private sealed class ContextoFijo(int idUsuario, int idTenant) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId { get; } = idUsuario;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant { get; } = idTenant;
    }

    /// <summary>Cláusula bajo prueba: el filtro global de EF de <c>db.Empresas</c> en
    /// <c>ResolverAlcanceAsync</c>. Como <c>ways_app</c> el RLS ya oculta las empresas de otro tenant y
    /// el filtro de EF queda tapado, así que este caso baja por debajo de ese confound: el servicio corre
    /// sobre un contexto de owner (superusuario, sin RLS) en modo tenant A, y solo el filtro de EF separa
    /// a los tenants al listar las empresas.</summary>
    [Fact]
    public async Task SinRlsElFiltroDeEmpresasDelTenantAlcanzaParaNoSumarOtroTenant()
    {
        var a = await SembrarEscenarioAsync(nameof(SinRlsElFiltroDeEmpresasDelTenantAlcanzaParaNoSumarOtroTenant) + "-a");
        await SembrarVentasDeHoyAsync(a);
        var b = await _apoyo.SembrarTenantAsync(nameof(SinRlsElFiltroDeEmpresasDelTenantAlcanzaParaNoSumarOtroTenant) + "-b");
        var adminB = await _apoyo.SembrarUsuarioAsync(b, RolConocido.Admin);
        await _apoyo.SembrarComprobanteAsync(b, new(2026, 10, 18, 10, 0, 0, TimeSpan.Zero), 123456m, adminB.IdUsuario);

        await using var db = fixture.CrearContextoDeOwner(new TenantActualFijo(ModoDeAcceso.Tenant, a.Tenant.IdTenant));
        var reloj = new RelojFijo(RelojDelMediodia);
        var parametros = new ServicioDeParametros(db, reloj, new ServicioDeLotes(db, reloj, new ContextoFijo(a.Admin.IdUsuario, a.Tenant.IdTenant)));
        var servicio = new ServicioDeConsultasDeVentas(
            db, reloj, parametros, new ServicioDeReportesDeVentas(db, new LectorDeSerieTemporal(db), parametros),
            new ServicioDeReportesDeArticulos(db, parametros));

        var resumen = await servicio.ResumirAsync(new SolicitudDeConsultaDeVentas("hoy", null, null, null, null));

        Assert.Equal(1700m, resumen.NetoVendido);
        Assert.Equal(a.Tenant.IdEmpresa, Assert.Single(resumen.Empresas).IdEmpresa);
    }

    /// <summary>Cláusula bajo prueba: <c>PeriodoDeConsulta.HoyLocal(reloj.Ahora, zona)</c> en
    /// <c>ServicioDeConsultasDeVentas</c>. Con el reloj en 2026-10-18T02:00Z son las 23:00 del 17 en Buenos
    /// Aires; "hoy" tiene que ser el 17. Hay filas a un minuto de cada lado de la medianoche local.</summary>
    [Fact]
    public async Task HoyYAyerSeResuelvenEnLaZonaDeLaEmpresaYNoEnUtc()
    {
        var e = await SembrarEscenarioAsync(nameof(HoyYAyerSeResuelvenEnLaZonaDeLaEmpresaYNoEnUtc));
        var t = e.Tenant;
        var admin = e.Admin.IdUsuario;
        await _apoyo.SembrarComprobanteAsync(t, new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero), 100m, admin);
        await _apoyo.SembrarComprobanteAsync(t, new(2026, 10, 18, 1, 0, 0, TimeSpan.Zero), 20m, admin);

        // 02:59Z son las 23:59 del 17 local (entra en hoy); 03:00Z son las 00:00 del 18 local (no entra).
        await _apoyo.SembrarComprobanteAsync(t, new(2026, 10, 18, 2, 59, 0, TimeSpan.Zero), 3m, admin);
        await _apoyo.SembrarComprobanteAsync(t, new(2026, 10, 18, 3, 0, 0, TimeSpan.Zero), 5000m, admin);

        // 02:00Z del 17 son las 23:00 del 16 local: es "ayer", y con "hoy" tomado de UTC caería en el 17.
        await _apoyo.SembrarComprobanteAsync(t, new(2026, 10, 17, 2, 0, 0, TimeSpan.Zero), 4000m, admin);

        await using var host = _apoyo.HostConReloj(new(2026, 10, 18, 2, 0, 0, TimeSpan.Zero), e.Admin.Mail);
        using var cliente = ApoyoDeConectorMcp.Cliente(host);
        var token = await AccessTokenAsync(cliente, e.Admin.Mail);

        var hoy = await LlamarHerramientaAsync(cliente, token, "ventas_resumen", new { periodo = "hoy" });
        var ayer = await LlamarHerramientaAsync(cliente, token, "ventas_resumen", new { periodo = "ayer" });

        Assert.False(hoy.EsError, hoy.Texto);
        Assert.Contains("Ventas hoy (2026-10-17), zona horaria: America/Argentina/Buenos_Aires", Lineas(hoy.Texto));
        Assert.Contains("Total vendido: 123,00", Lineas(hoy.Texto));
        Assert.Contains("Comprobantes: 3", Lineas(hoy.Texto));
        Assert.Contains("Ventas ayer (2026-10-16), zona horaria: America/Argentina/Buenos_Aires", Lineas(ayer.Texto));
        Assert.Contains("Total vendido: 4.000,00", Lineas(ayer.Texto));
    }

    /// <summary>Cláusula bajo prueba: la evaluación de <c>Politicas.LecturaDeReportes</c> en
    /// <c>HerramientasDeVentas.NegacionDeRolAsync</c> (vía <c>PermisoDelConector.CumpleAsync</c>) para
    /// <c>ventas_resumen</c> y <c>ventas_agrupadas</c>; la de <c>ventas_por_articulo</c> la fija
    /// <c>ConectorMcpVentasPorArticuloTests.SoloSupervisorYAdminVenVentasPorArticulo</c>.</summary>
    [Theory]
    [InlineData("ventas_resumen")]
    [InlineData("ventas_agrupadas")]
    public async Task SoloSupervisorYAdminConsultanVentas(string herramienta)
    {
        var e = await SembrarEscenarioAsync(nameof(SoloSupervisorYAdminConsultanVentas) + herramienta.Replace("_", string.Empty, StringComparison.Ordinal));
        await SembrarVentasDeHoyAsync(e);
        object argumentos = herramienta == "ventas_resumen"
            ? new { periodo = "hoy" }
            : new { periodo = "hoy", agrupar_por = "dia" };

        await using var host = _apoyo.HostConReloj(RelojDelMediodia, e.Vendedor.Mail, e.Supervisor.Mail, e.Admin.Mail);
        using var cliente = ApoyoDeConectorMcp.Cliente(host);

        var delVendedor = await LlamarHerramientaAsync(cliente, await AccessTokenAsync(cliente, e.Vendedor.Mail), herramienta, argumentos);
        var delSupervisor = await LlamarHerramientaAsync(cliente, await AccessTokenAsync(cliente, e.Supervisor.Mail), herramienta, argumentos);
        var delAdmin = await LlamarHerramientaAsync(cliente, await AccessTokenAsync(cliente, e.Admin.Mail), herramienta, argumentos);

        Assert.False(delVendedor.EsError);
        Assert.Equal("Tu rol (Vendedor) no puede ver reportes de ventas: requiere Supervisor o Admin.", delVendedor.Texto);
        foreach (var conDatos in new[] { delSupervisor, delAdmin })
        {
            Assert.False(conDatos.EsError, conDatos.Texto);
            Assert.Contains("1.700,00", conDatos.Texto);
        }
    }

    [Fact]
    public async Task ElLimiteCortaLasFilasYAvisaCuantasFaltan()
    {
        var e = await SembrarEscenarioAsync(nameof(ElLimiteCortaLasFilasYAvisaCuantasFaltan));
        await SembrarVentasDeHoyAsync(e);

        await using var host = _apoyo.HostConReloj(RelojDelMediodia, e.Admin.Mail);
        using var cliente = ApoyoDeConectorMcp.Cliente(host);
        var token = await AccessTokenAsync(cliente, e.Admin.Mail);

        // Diez días (ocho sin ventas, que igual aparecen), del más reciente al más antiguo: límite 3 => los tres
        // últimos días y "… y 7 más".
        var porDia = await LlamarHerramientaAsync(
            cliente, token, "ventas_agrupadas", new { agrupar_por = "dia", desde = "2026-10-09", hasta = "2026-10-18", limite = 3 });
        Assert.False(porDia.EsError, porDia.Texto);
        Assert.Equal(["2026-10-18 | 1.700,00 | 3 | 68,6%", "2026-10-17 | 777,00 | 1 | 31,4%", "2026-10-16 | 0,00 | 0 | 0,0%"], FilasDeFecha(porDia.Texto));
        Assert.Contains("… y 7 más", Lineas(porDia.Texto));
        Assert.Contains("Total: 2.477,00 en 10 grupos", Lineas(porDia.Texto));

        // Dos vendedores (admin 1300 en 2, supervisor 400 en 1): límite 1 => el mayor y "… y 1 más".
        var porVendedor = await LlamarHerramientaAsync(
            cliente, token, "ventas_agrupadas", new { agrupar_por = "vendedor", periodo = "hoy", limite = 1 });
        Assert.False(porVendedor.EsError, porVendedor.Texto);
        Assert.Equal(
            [
                "Ventas por vendedor hoy (2026-10-18), zona horaria: America/Argentina/Buenos_Aires",
                Lineas(porVendedor.Texto)[1],
                "vendedor | importe | comprobantes | % del total",
                $"{NombreDeUsuario(e.Admin)} | 1.300,00 | 2 | 76,5%",
                "… y 1 más",
                "Total: 1.700,00 en 2 grupos"
            ],
            Lineas(porVendedor.Texto));

        // Sin tope de por medio, las dos filas completas.
        var todos = await LlamarHerramientaAsync(
            cliente, token, "ventas_agrupadas", new { agrupar_por = "vendedor", periodo = "hoy" });
        Assert.Contains($"{NombreDeUsuario(e.Supervisor)} | 400,00 | 1 | 23,5%", Lineas(todos.Texto));
        Assert.DoesNotContain(Lineas(todos.Texto), l => l.StartsWith("… y", StringComparison.Ordinal));
    }

    /// <summary>Cláusula bajo prueba: <c>(Importe - Vuelto)</c> en <c>ConsultarMediosDePagoAsync</c>.</summary>
    [Fact]
    public async Task ElMedioDePagoSeMuestraNetoDelVuelto()
    {
        var e = await SembrarEscenarioAsync(nameof(ElMedioDePagoSeMuestraNetoDelVuelto));
        var t = e.Tenant;
        var venta = await _apoyo.SembrarComprobanteAsync(t, new(2026, 10, 18, 10, 0, 0, TimeSpan.Zero), 800m, e.Admin.IdUsuario);
        await _apoyo.SembrarPagoAsync(t, venta, t.IdMedioEfectivo, importe: 1000m, vuelto: 200m);

        await using var host = _apoyo.HostConReloj(RelojDelMediodia, e.Admin.Mail);
        using var cliente = ApoyoDeConectorMcp.Cliente(host);
        var token = await AccessTokenAsync(cliente, e.Admin.Mail);

        var agrupada = await LlamarHerramientaAsync(
            cliente, token, "ventas_agrupadas", new { agrupar_por = "medio_pago", periodo = "hoy" });
        var resumen = await LlamarHerramientaAsync(cliente, token, "ventas_resumen", new { periodo = "hoy" });

        Assert.Contains($"{t.NombreEfectivo} | 800,00 | 1 | 100,0%", Lineas(agrupada.Texto));
        Assert.Contains("Total (lo cobrado neto del vuelto): 800,00 en 1 grupos", Lineas(agrupada.Texto));
        Assert.Contains($"{t.NombreEfectivo} | 800,00 | 1", Lineas(resumen.Texto));
    }

    [Theory]
    [InlineData("ventas_resumen", """{"periodo":"anteayer"}""", "anteayer", "esta_semana")]
    [InlineData("ventas_resumen", """{"periodo":"hoy","desde":"2026-10-01","hasta":"2026-10-02"}""", "no ambos", null)]
    [InlineData("ventas_resumen", """{"desde":"2026-10-01"}""", "van juntos", null)]
    [InlineData("ventas_resumen", """{}""", "Indicá 'periodo'", null)]
    [InlineData("ventas_resumen", """{"desde":"2026-01-01","hasta":"2027-06-01"}""", "366 días", null)]
    [InlineData("ventas_resumen", """{"periodo":"hoy","empresa":"no-existe"}""", "No encontré la empresa 'no-existe'", null)]
    [InlineData("ventas_agrupadas", """{"periodo":"hoy","agrupar_por":"color"}""", "punto_venta, medio_pago, vendedor, dia", null)]
    [InlineData("ventas_agrupadas", """{"periodo":"hoy","agrupar_por":"dia","limite":0}""", "entre 1 y 50", null)]
    [InlineData("ventas_agrupadas", """{"periodo":"hoy","agrupar_por":"dia","limite":51}""", "entre 1 y 50", null)]
    public async Task UnaEntradaInvalidaDevuelveUnResultadoDeErrorConElMotivo(
        string herramienta, string argumentosJson, string fragmento, string? otroFragmento)
    {
        var e = await SembrarEscenarioAsync(nameof(UnaEntradaInvalidaDevuelveUnResultadoDeErrorConElMotivo));
        await using var host = _apoyo.HostConReloj(RelojDelMediodia, e.Admin.Mail);
        using var cliente = ApoyoDeConectorMcp.Cliente(host);
        var token = await AccessTokenAsync(cliente, e.Admin.Mail);
        var argumentos = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(argumentosJson);

        var resultado = await LlamarHerramientaAsync(cliente, token, herramienta, argumentos);

        Assert.True(resultado.EsError, resultado.Texto);
        Assert.Contains(fragmento, resultado.Texto);
        if (otroFragmento is not null)
        {
            Assert.Contains(otroFragmento, resultado.Texto);
        }
    }

    [Fact]
    public async Task SinEmpresaSumaTodasLasDelTenantYConEmpresaOPuntoDeVentaAcotaElAlcance()
    {
        var e = await SembrarEscenarioAsync(nameof(SinEmpresaSumaTodasLasDelTenantYConEmpresaOPuntoDeVentaAcotaElAlcance));
        var t = e.Tenant;
        var idEmpresa2 = await _apoyo.SembrarEmpresaAsync(t, "Segunda Empresa SRL");
        var idPuntoEmpresa2 = await _apoyo.SembrarPuntoVentaAsync(t, "Local 1", idEmpresa2);
        await _apoyo.SembrarComprobanteAsync(t, new(2026, 10, 18, 10, 0, 0, TimeSpan.Zero), 1000m, e.Admin.IdUsuario);
        await _apoyo.SembrarComprobanteAsync(t, new(2026, 10, 18, 10, 0, 0, TimeSpan.Zero), 250m, e.Admin.IdUsuario, idPuntoEmpresa2);

        await using var host = _apoyo.HostConReloj(RelojDelMediodia, e.Admin.Mail);
        using var cliente = ApoyoDeConectorMcp.Cliente(host);
        var token = await AccessTokenAsync(cliente, e.Admin.Mail);

        var todas = await LlamarHerramientaAsync(cliente, token, "ventas_resumen", new { periodo = "hoy" });
        var soloSegunda = await LlamarHerramientaAsync(cliente, token, "ventas_resumen", new { periodo = "hoy", empresa = "segunda" });
        var porId = await LlamarHerramientaAsync(
            cliente, token, "ventas_resumen", new { periodo = "hoy", empresa = idEmpresa2.ToString(), punto_venta = idPuntoEmpresa2.ToString() });
        var puntoAjeno = await LlamarHerramientaAsync(
            cliente, token, "ventas_resumen", new { periodo = "hoy", empresa = "segunda", punto_venta = t.IdPuntoVenta.ToString() });
        var porNombreDeLaEmpresa = await LlamarHerramientaAsync(
            cliente, token, "ventas_resumen", new { periodo = "hoy", empresa = "segunda", punto_venta = "local 1" });

        Assert.Contains("Total vendido: 1.250,00", Lineas(todas.Texto));
        Assert.Contains(
            "empresa | período | importe | comprobantes", Lineas(todas.Texto));
        Assert.Contains("Segunda Empresa SRL | 2026-10-18 | 250,00 | 1", Lineas(todas.Texto));
        Assert.Contains($"Local 1 (Segunda Empresa SRL) | 250,00 | 1", Lineas(todas.Texto));

        Assert.Contains("Total vendido: 250,00", Lineas(soloSegunda.Texto));
        Assert.Contains("Alcance: empresa Segunda Empresa SRL", Lineas(soloSegunda.Texto));
        Assert.DoesNotContain("Por empresa:", Lineas(soloSegunda.Texto));

        Assert.Contains("Alcance: empresa Segunda Empresa SRL, punto de venta Local 1", Lineas(porId.Texto));
        Assert.Contains("Total vendido: 250,00", Lineas(porId.Texto));
        Assert.DoesNotContain("Por punto de venta:", Lineas(porId.Texto));

        Assert.True(puntoAjeno.EsError, puntoAjeno.Texto);
        Assert.Contains("no pertenece a la empresa 'Segunda Empresa SRL'", puntoAjeno.Texto);

        // Con la empresa dada, el nombre del punto de venta se busca solo entre los de esa empresa
        // ("Local 1" existe en las dos).
        Assert.Contains("Total vendido: 250,00", Lineas(porNombreDeLaEmpresa.Texto));
    }

    /// <summary>Las herramientas de ventas se anuncian de solo lectura y con los argumentos en snake_case
    /// que sus descripciones prometen; <c>agrupar_por</c> es el único obligatorio de <c>ventas_agrupadas</c>.</summary>
    [Fact]
    public async Task ToolsListAnunciaLasHerramientasDeVentasDeSoloLecturaYConSusArgumentos()
    {
        var e = await SembrarEscenarioAsync(nameof(ToolsListAnunciaLasHerramientasDeVentasDeSoloLecturaYConSusArgumentos));
        await using var host = _apoyo.HostConReloj(RelojDelMediodia, e.Admin.Mail);
        using var cliente = ApoyoDeConectorMcp.Cliente(host);
        var token = await AccessTokenAsync(cliente, e.Admin.Mail);

        var (_, mensaje) = await ApoyoDeConectorMcp.LlamarMcpAsync(cliente, token, "tools/list");

        var herramientas = mensaje!.Value.GetProperty("result").GetProperty("tools").EnumerateArray()
            .ToDictionary(h => h.GetProperty("name").GetString()!);
        var deSoloLectura = new HashSet<string> { "articulo_info", "quien_soy", "ventas_agrupadas", "ventas_por_articulo", "ventas_resumen" };
        Assert.Subset(herramientas.Keys.ToHashSet(), deSoloLectura);
        foreach (var herramienta in deSoloLectura.Select(nombre => herramientas[nombre]))
        {
            var anotaciones = herramienta.GetProperty("annotations");
            Assert.True(anotaciones.GetProperty("readOnlyHint").GetBoolean());
            Assert.False(anotaciones.GetProperty("destructiveHint").GetBoolean());
            Assert.True(anotaciones.GetProperty("idempotentHint").GetBoolean());
            Assert.False(anotaciones.GetProperty("openWorldHint").GetBoolean());
        }

        var agrupadas = herramientas["ventas_agrupadas"].GetProperty("inputSchema");
        Assert.Equal(
            ["agrupar_por", "desde", "empresa", "hasta", "limite", "periodo", "punto_venta"],
            agrupadas.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(["agrupar_por"], agrupadas.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToArray());
        Assert.Equal(
            ["desde", "empresa", "hasta", "periodo", "punto_venta"],
            herramientas["ventas_resumen"].GetProperty("inputSchema").GetProperty("properties")
                .EnumerateObject().Select(p => p.Name).Order().ToArray());
    }

    /// <summary>Cláusula bajo prueba: el tope de 31 días de la tabla "Por día" de <c>ventas_resumen</c>.</summary>
    [Fact]
    public async Task LaTablaPorDiaSoloSeMuestraHastaLos31DiasDeRango()
    {
        var e = await SembrarEscenarioAsync(nameof(LaTablaPorDiaSoloSeMuestraHastaLos31DiasDeRango));
        await _apoyo.SembrarComprobanteAsync(e.Tenant, new(2026, 10, 18, 10, 0, 0, TimeSpan.Zero), 10m, e.Admin.IdUsuario);

        await using var host = _apoyo.HostConReloj(RelojDelMediodia, e.Admin.Mail);
        using var cliente = ApoyoDeConectorMcp.Cliente(host);
        var token = await AccessTokenAsync(cliente, e.Admin.Mail);

        var treintaYUno = await LlamarHerramientaAsync(
            cliente, token, "ventas_resumen", new { desde = "2026-10-01", hasta = "2026-10-31" });
        var treintaYDos = await LlamarHerramientaAsync(
            cliente, token, "ventas_resumen", new { desde = "2026-10-01", hasta = "2026-11-01" });

        Assert.Equal(31, FilasDeFecha(treintaYUno.Texto).Length);
        Assert.Contains("Por día:", Lineas(treintaYUno.Texto));
        Assert.DoesNotContain("Por día:", Lineas(treintaYDos.Texto));
        Assert.Contains("Ventas (2026-10-01 a 2026-11-01), zona horaria: America/Argentina/Buenos_Aires", Lineas(treintaYDos.Texto));
    }
}
