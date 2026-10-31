using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Plantel;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Plantel;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Plantel;

/// <summary>
/// La lectura de la grilla y las validaciones previas al guardado de <see cref="ServicioDeCargaSemanalDeHoras"/>
/// sobre el proveedor InMemory. La escritura (locks, libro, saldo, jornadas) usa SQL crudo y se prueba contra
/// Postgres en <c>CargaSemanalDeHorasTests</c>.
/// </summary>
public class ServicioDeCargaSemanalDeHorasTests
{
    private const int Tenant = 1;
    private const int OtroTenant = 2;

    // Miércoles 28/10/2026 12:00 en Buenos Aires: la semana va del 26/10 al 01/11.
    private static readonly DateTimeOffset Ahora = new(2026, 10, 28, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Lunes = new(2026, 10, 26);

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private sealed class ContextoFijo(int? idTenant) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => 999;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant { get; } = idTenant;
    }

    private static WaysDbContext CrearContexto(string baseDeDatos, ITenantActual tenantActual) =>
        new(new DbContextOptionsBuilder<WaysDbContext>().UseInMemoryDatabase(baseDeDatos).Options, tenantActual);

    private static ServicioDeCargaSemanalDeHoras CrearServicio(string baseDeDatos, DateTimeOffset? ahora = null)
    {
        var db = CrearContexto(baseDeDatos, new TenantActualFijo(ModoDeAcceso.Tenant, Tenant));
        return new ServicioDeCargaSemanalDeHoras(db, new RelojFijo(ahora ?? Ahora), new ContextoFijo(Tenant));
    }

    private static decimal[] Horario(decimal lunes = 8, decimal martes = 8, decimal miercoles = 8, decimal jueves = 8,
        decimal viernes = 8, decimal sabado = 0, decimal domingo = 0) =>
        [lunes, martes, miercoles, jueves, viernes, sabado, domingo];

    private static async Task<int> SembrarPersonalAsync(
        string baseDeDatos, string nombre, decimal[]? horario = null, int tenant = Tenant, bool deBaja = false,
        int? idEmpresa = null, decimal valorHora = 1000m)
    {
        await using var db = CrearContexto(baseDeDatos, TenantActualFijo.Plataforma);
        var personal = new Personal
        {
            IdTenant = tenant, Nombre = nombre, ValorHora = valorHora, HorarioHabitual = horario, IdEmpresa = idEmpresa,
            CreatedAt = Ahora, UpdatedAt = Ahora, DeletedAt = deBaja ? Ahora : null
        };
        db.Personal.Add(personal);
        await db.SaveChangesAsync();
        return personal.Id;
    }

    private static async Task SembrarHorasAsync(string baseDeDatos, int idPersonal, DateOnly fecha, params decimal[] horas)
    {
        await using var db = CrearContexto(baseDeDatos, TenantActualFijo.Plataforma);
        foreach (var h in horas)
        {
            db.MovimientosPersonal.Add(new MovimientoPersonal
            {
                IdTenant = Tenant, IdPersonal = idPersonal, IdEmpleado = 1, Fecha = fecha, Tipo = TipoMovimientoPersonal.Horas,
                Horas = h, ValorHora = 1000m, Importe = h * 1000m, SaldoResultante = 0, CreatedAt = Ahora
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task SembrarJornadaAsync(string baseDeDatos, int idPersonal, DateOnly fecha, decimal horas)
    {
        await using var db = CrearContexto(baseDeDatos, TenantActualFijo.Plataforma);
        db.JornadasPersonal.Add(new JornadaPersonal
        {
            IdTenant = Tenant, IdPersonal = idPersonal, IdEmpleado = 1, Fecha = fecha, Horas = horas,
            CreatedAt = Ahora, UpdatedAt = Ahora
        });
        await db.SaveChangesAsync();
    }

    private static string Base(string nombre) => $"{nameof(ServicioDeCargaSemanalDeHorasTests)}-{nombre}";

    [Fact]
    public async Task SinDesdeLaSemanaEsLaDelLunesDeHoyConFilasPorNombreSinBajasNiOtrosTenants()
    {
        var bd = Base(nameof(SinDesdeLaSemanaEsLaDelLunesDeHoyConFilasPorNombreSinBajasNiOtrosTenants));
        await SembrarPersonalAsync(bd, "Zoe");
        await SembrarPersonalAsync(bd, "Ana");
        await SembrarPersonalAsync(bd, "Baja", deBaja: true);
        await SembrarPersonalAsync(bd, "Ajeno", tenant: OtroTenant);

        var semana = await CrearServicio(bd).ObtenerSemanaAsync(null);

        Assert.Equal(Lunes, semana.Desde);
        Assert.Equal(new DateOnly(2026, 11, 1), semana.Hasta);
        Assert.Equal(new DateOnly(2026, 10, 28), semana.Hoy);
        Assert.Equal(["Ana", "Zoe"], semana.Filas.Select(f => f.Nombre));
        Assert.All(semana.Filas, f => Assert.Equal(7, f.Dias.Count));
        Assert.Equal(
            Enumerable.Range(0, 7).Select(i => Lunes.AddDays(i)), semana.Filas[0].Dias.Select(d => d.Fecha));
    }

    [Fact]
    public async Task UnDiaSinConfirmarMuestraElHorarioHabitualComoPrecarga()
    {
        var bd = Base(nameof(UnDiaSinConfirmarMuestraElHorarioHabitualComoPrecarga));
        await SembrarPersonalAsync(bd, "Ana", Horario(lunes: 6, martes: 7.5m, miercoles: 0));

        var dias = (await CrearServicio(bd).ObtenerSemanaAsync(Lunes)).Filas[0].Dias;

        Assert.Equal(new DiaDeCargaSemanal(Lunes, 0m, false, 6m, true, false), dias[0]);
        Assert.Equal(7.5m, dias[1].Horas);
        Assert.True(dias[1].Precargado);
        Assert.Equal(0m, dias[2].Horas);
        Assert.True(dias[2].Precargado);
    }

    [Fact]
    public async Task LosDiasFuturosNoSePrecarganYSeMarcanComoFuturos()
    {
        var bd = Base(nameof(LosDiasFuturosNoSePrecarganYSeMarcanComoFuturos));
        await SembrarPersonalAsync(bd, "Ana", Horario());

        var dias = (await CrearServicio(bd).ObtenerSemanaAsync(Lunes)).Filas[0].Dias;

        Assert.False(dias[2].Futuro);
        Assert.Equal(8m, dias[2].Horas);
        Assert.All(dias.Skip(3), d =>
        {
            Assert.True(d.Futuro);
            Assert.Null(d.Horas);
            Assert.False(d.Precargado);
        });
    }

    [Fact]
    public async Task SinHorarioHabitualNoSePrecargaNada()
    {
        var bd = Base(nameof(SinHorarioHabitualNoSePrecargaNada));
        await SembrarPersonalAsync(bd, "Ana");

        var dias = (await CrearServicio(bd).ObtenerSemanaAsync(Lunes)).Filas[0].Dias;

        Assert.All(dias, d =>
        {
            Assert.Null(d.Horas);
            Assert.False(d.Precargado);
            Assert.False(d.Confirmado);
        });
    }

    [Fact]
    public async Task UnaJornadaConfirmadaEnCeroNoSeVuelveAPrecargar()
    {
        var bd = Base(nameof(UnaJornadaConfirmadaEnCeroNoSeVuelveAPrecargar));
        var id = await SembrarPersonalAsync(bd, "Ana", Horario());
        await SembrarJornadaAsync(bd, id, Lunes, 0m);

        var dia = (await CrearServicio(bd).ObtenerSemanaAsync(Lunes)).Filas[0].Dias[0];

        Assert.True(dia.Confirmado);
        Assert.Equal(0m, dia.Horas);
        Assert.Equal(0m, dia.HorasCargadas);
        Assert.False(dia.Precargado);
    }

    [Fact]
    public async Task UnDiaConHorasEnElLibroSinJornadaSeCuentaComoConfirmadoYSumaTodosLosAsientos()
    {
        var bd = Base(nameof(UnDiaConHorasEnElLibroSinJornadaSeCuentaComoConfirmadoYSumaTodosLosAsientos));
        var id = await SembrarPersonalAsync(bd, "Ana", Horario());
        await SembrarHorasAsync(bd, id, Lunes.AddDays(1), 3m, 2.5m, -1m);

        var dia = (await CrearServicio(bd).ObtenerSemanaAsync(Lunes)).Filas[0].Dias[1];

        Assert.Equal(new DiaDeCargaSemanal(Lunes.AddDays(1), 4.5m, true, 4.5m, false, false), dia);
    }

    [Fact]
    public async Task LasHorasDeOtraSemanaOdeOtroPersonalNoEntran()
    {
        var bd = Base(nameof(LasHorasDeOtraSemanaOdeOtroPersonalNoEntran));
        var ana = await SembrarPersonalAsync(bd, "Ana", Horario());
        var beto = await SembrarPersonalAsync(bd, "Beto", Horario());
        await SembrarHorasAsync(bd, ana, Lunes.AddDays(-1), 5m);
        await SembrarHorasAsync(bd, beto, Lunes, 4m);

        var filas = (await CrearServicio(bd).ObtenerSemanaAsync(Lunes)).Filas;

        Assert.False(filas[0].Dias[0].Confirmado);
        Assert.Equal(4m, filas[1].Dias[0].HorasCargadas);
    }

    [Theory]
    [InlineData("2026-10-27")]
    [InlineData("2026-11-01")]
    public async Task UnDesdeQueNoEsLunesSeRechazaConSemanaInvalida(string desde)
    {
        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => CrearServicio(Base(nameof(UnDesdeQueNoEsLunesSeRechazaConSemanaInvalida))).ObtenerSemanaAsync(DateOnly.Parse(desde)));

        Assert.Equal("semana_invalida", error.Codigo);
    }

    [Fact]
    public async Task HoyEsLaFechaLocalDeBuenosAiresNoLaUtc()
    {
        // 29/10 01:30 UTC es 28/10 22:30 en Buenos Aires: el jueves todavía es futuro.
        var bd = Base(nameof(HoyEsLaFechaLocalDeBuenosAiresNoLaUtc));
        await SembrarPersonalAsync(bd, "Ana", Horario());

        var semana = await CrearServicio(bd, new DateTimeOffset(2026, 10, 29, 1, 30, 0, TimeSpan.Zero)).ObtenerSemanaAsync(null);

        Assert.Equal(new DateOnly(2026, 10, 28), semana.Hoy);
        Assert.True(semana.Filas[0].Dias[3].Futuro);
    }

    [Fact]
    public async Task LaZonaDeLaEmpresaDelPersonalDecideCuandoEsFuturo()
    {
        var bd = Base(nameof(LaZonaDeLaEmpresaDelPersonalDecideCuandoEsFuturo));
        await using (var db = CrearContexto(bd, TenantActualFijo.Plataforma))
        {
            db.Parametros.Add(new Parametro
            {
                IdTenant = Tenant, IdEmpresa = 5, Clave = ParametroConocido.ZonaHoraria.Clave,
                Valor = "\"Pacific/Auckland\"", CreatedAt = Ahora, UpdatedAt = Ahora
            });
            await db.SaveChangesAsync();
        }

        await SembrarPersonalAsync(bd, "Ana", Horario(), idEmpresa: 5);
        await SembrarPersonalAsync(bd, "Beto", Horario());

        // 28/10 15:00 UTC es 29/10 04:00 en Auckland (jueves ya es hoy) y 28/10 12:00 en Buenos Aires.
        var filas = (await CrearServicio(bd).ObtenerSemanaAsync(Lunes)).Filas;

        Assert.False(filas[0].Dias[3].Futuro);
        Assert.Equal(8m, filas[0].Dias[3].Horas);
        Assert.True(filas[1].Dias[3].Futuro);
    }

    private static DiaACargarSemanal Dia(int idPersonal, string fecha, decimal? horas) =>
        new(idPersonal, DateOnly.Parse(fecha), horas);

    private static Task<ErrorDominio> RechazoAsync(string baseDeDatos, SolicitudDeCargaSemanal? solicitud) =>
        Assert.ThrowsAsync<ErrorDominio>(() => CrearServicio(baseDeDatos).GuardarSemanaAsync(solicitud));

    [Fact]
    public async Task UnaSolicitudSinDiasSeRechaza()
    {
        var bd = Base(nameof(UnaSolicitudSinDiasSeRechaza));

        Assert.Equal("solicitud_invalida", (await RechazoAsync(bd, null)).Codigo);
        Assert.Equal("solicitud_invalida", (await RechazoAsync(bd, new SolicitudDeCargaSemanal(null))).Codigo);
        Assert.Equal("solicitud_invalida", (await RechazoAsync(bd, new SolicitudDeCargaSemanal([]))).Codigo);
    }

    [Fact]
    public async Task MasDeMilDiasSeRechazanYMilExactosPasanLaValidacionDeTamanio()
    {
        var bd = Base(nameof(MasDeMilDiasSeRechazanYMilExactosPasanLaValidacionDeTamanio));
        var id = await SembrarPersonalAsync(bd, "Ana");
        var mil = Enumerable.Range(0, 1000).Select(i => Dia(id, "2026-01-01", 1m) with { Fecha = new DateOnly(2025, 1, 1).AddDays(i) }).ToList();

        var excedida = await RechazoAsync(bd, new SolicitudDeCargaSemanal([.. mil, Dia(id, "2026-10-26", 1m)]));
        Assert.Equal("solicitud_invalida", excedida.Codigo);

        // Con 1000 exactos el rechazo ya no es de tamaño: los días de 2025 son pasados y válidos, y recién la
        // escritura (SQL crudo, solo en Postgres) falla bajo InMemory.
        var error = await Record.ExceptionAsync(() => CrearServicio(bd).GuardarSemanaAsync(new SolicitudDeCargaSemanal(mil)));
        Assert.True(error is not ErrorDominio e || e.Codigo != "solicitud_invalida");
    }

    [Fact]
    public async Task UnPersonalInexistenteOBajaOAjenoResponde404AntesDeValidarElResto()
    {
        var bd = Base(nameof(UnPersonalInexistenteOBajaOAjenoResponde404AntesDeValidarElResto));
        var ana = await SembrarPersonalAsync(bd, "Ana");
        var baja = await SembrarPersonalAsync(bd, "Baja", deBaja: true);
        var ajeno = await SembrarPersonalAsync(bd, "Ajeno", tenant: OtroTenant);

        foreach (var id in new[] { 9999, baja, ajeno })
        {
            // La fecha futura y las horas inválidas de las otras entradas no se llegan a evaluar.
            var error = await RechazoAsync(bd, new SolicitudDeCargaSemanal(
                [Dia(ana, "2030-01-01", 99m), Dia(id, "2026-10-26", 8m)]));

            Assert.Equal(404, error.EstadoHttp);
        }
    }

    [Fact]
    public async Task UnDiaRepetidoParaElMismoPersonalSeRechaza()
    {
        var bd = Base(nameof(UnDiaRepetidoParaElMismoPersonalSeRechaza));
        var id = await SembrarPersonalAsync(bd, "Ana");

        var error = await RechazoAsync(bd, new SolicitudDeCargaSemanal(
            [Dia(id, "2026-10-26", 8m), Dia(id, "2026-10-26", 4m)]));

        Assert.Equal("dia_duplicado", error.Codigo);
    }

    [Fact]
    public async Task UnDiaFuturoSeRechazaConFechaFutura()
    {
        var bd = Base(nameof(UnDiaFuturoSeRechazaConFechaFutura));
        var id = await SembrarPersonalAsync(bd, "Ana");

        var error = await RechazoAsync(bd, new SolicitudDeCargaSemanal([Dia(id, "2026-10-29", 8m)]));

        Assert.Equal("fecha_futura", error.Codigo);
    }

    [Theory]
    [InlineData("24.01")]
    [InlineData("-1")]
    [InlineData("8.123")]
    [InlineData(null)]
    public async Task LasHorasFueraDeRangoOSinValorSeRechazanConHorasInvalidas(string? horas)
    {
        var bd = Base(nameof(LasHorasFueraDeRangoOSinValorSeRechazanConHorasInvalidas));
        var id = await SembrarPersonalAsync(bd, "Ana");
        decimal? valor = horas is null ? null : decimal.Parse(horas, System.Globalization.CultureInfo.InvariantCulture);

        var error = await RechazoAsync(bd, new SolicitudDeCargaSemanal([Dia(id, "2026-10-26", valor)]));

        Assert.Equal("horas_invalidas", error.Codigo);
    }

    [Fact]
    public async Task UnDiaSinFechaSeRechazaConFechaRequerida()
    {
        var bd = Base(nameof(UnDiaSinFechaSeRechazaConFechaRequerida));
        var id = await SembrarPersonalAsync(bd, "Ana");

        var error = await RechazoAsync(bd, new SolicitudDeCargaSemanal([new DiaACargarSemanal(id, null, 8m)]));

        Assert.Equal("fecha_requerida", error.Codigo);
    }
}
