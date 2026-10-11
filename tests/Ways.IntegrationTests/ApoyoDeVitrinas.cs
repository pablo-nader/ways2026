using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Siembra y clientes compartidos por las pruebas de la vitrina pública. Un comercio de prueba es un
/// tenant con su admin, una empresa y un dominio verificado propio; todo se inserta directo en la base
/// (en modo plataforma), porque es el estado de partida de cada prueba y no lo que se prueba.
/// </summary>
internal sealed class ApoyoDeVitrinas(WaysApiFixture fixture)
{
    public sealed record Comercio(ApoyoDeDominios.TenantSembrado Tenant, int IdEmpresa, string Host)
    {
        public int IdTenant => Tenant.IdTenant;
    }

    public ApoyoDeDominios Dominios { get; } = new(fixture);

    /// <summary>Un comercio con su admin, una empresa y un dominio propio ya verificado.</summary>
    public async Task<Comercio> SembrarComercioAsync(
        string prefijo, string razonSocial = "Razón Social SA", string? nombreFantasia = "Fantasía")
    {
        var tenant = await Dominios.SembrarTenantAsync(prefijo);
        var idEmpresa = await SembrarEmpresaAsync(tenant.IdTenant, razonSocial, nombreFantasia);
        var host = ApoyoDeDominios.HostUnico(prefijo);
        await Dominios.SembrarDominioAsync(tenant.IdTenant, host);

        return new Comercio(tenant, idEmpresa, host);
    }

    public async Task<int> SembrarEmpresaAsync(
        int idTenant, string razonSocial, string? nombreFantasia = null, bool eliminada = false)
    {
        var ahora = DateTimeOffset.UtcNow;

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var empresa = new Empresa
        {
            IdTenant = idTenant,
            RazonSocial = razonSocial,
            NombreFantasia = nombreFantasia,
            Cuit = "30-12345678-9",
            DeletedAt = eliminada ? ahora : null,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        return empresa.Id;
    }

    public async Task<int> SembrarPuntoVentaAsync(
        int idTenant,
        int idEmpresa,
        string nombre,
        string? domicilio = null,
        string? horario = null,
        string? whatsapp = null,
        string? instagram = null,
        string? facebook = null,
        string? web = null,
        bool eliminado = false)
    {
        var ahora = DateTimeOffset.UtcNow;

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var puntoVenta = new PuntoVenta
        {
            IdTenant = idTenant,
            IdEmpresa = idEmpresa,
            Nombre = nombre,
            Domicilio = domicilio,
            Horario = horario,
            Whatsapp = whatsapp,
            Instagram = instagram,
            Facebook = facebook,
            Web = web,
            Modo = ModoPuntoVenta.Web,
            DeletedAt = eliminado ? ahora : null,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();

        return puntoVenta.Id;
    }

    public async Task<int> SembrarVitrinaAsync(
        int idTenant,
        int idEmpresa,
        bool publicada,
        string? tagline = null,
        string? descripcion = null,
        string? colorAcento = null,
        string tema = Vitrina.TemaOscuro,
        byte[]? logo = null,
        bool eliminada = false)
    {
        var ahora = DateTimeOffset.UtcNow;

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var vitrina = new Vitrina
        {
            IdTenant = idTenant,
            IdEmpresa = idEmpresa,
            Publicada = publicada,
            Tagline = tagline,
            Descripcion = descripcion,
            ColorAcento = colorAcento,
            Tema = tema,
            Logo = logo,
            LogoContentType = logo is null ? null : Vitrina.TipoPng,
            LogoActualizadoEn = logo is null ? null : ahora,
            DeletedAt = eliminada ? ahora : null,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Vitrinas.Add(vitrina);
        await db.SaveChangesAsync();

        return vitrina.Id;
    }

    /// <summary>Un usuario más del tenant, con el rol pedido. Devuelve su mail y su contraseña.</summary>
    public async Task<(string Mail, string Password)> SembrarUsuarioAsync(int idTenant, RolConocido rol, string usuario)
    {
        var ahora = DateTimeOffset.UtcNow;
        var hasheador = new HasheadorPbkdf2();
        var mail = $"{usuario}-{Guid.NewGuid().ToString("N")[..8]}@ways.test";

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        db.Usuarios.Add(new Usuario
        {
            IdTenant = idTenant,
            NombreUsuario = usuario,
            Mail = mail,
            RolId = (int)rol,
            PasswordHash = hasheador.Hashear(ApoyoDeDominios.PasswordPorDefecto),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return (mail, ApoyoDeDominios.PasswordPorDefecto);
    }

    /// <summary>Cliente con la sesión del admin del tenant, en el host de la plataforma (una sesión de
    /// tenant también vale ahí).</summary>
    public async Task<HttpClient> ClienteAdminAsync(Comercio comercio) =>
        await ClienteConSesionAsync(comercio.Tenant.Mail, comercio.Tenant.Password);

    public async Task<HttpClient> ClienteConSesionAsync(string mail, string password)
    {
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new { mail, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        return cliente;
    }

    /// <summary>La fila tal como quedó en la base, incluida una dada de baja.</summary>
    public async Task<Vitrina?> LeerVitrinaAsync(int idEmpresa)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        return await db.Vitrinas
            .IgnoreQueryFilters(["BajaLogica"])
            .AsNoTracking()
            .SingleOrDefaultAsync(v => v.IdEmpresa == idEmpresa);
    }

    public async Task<int> ContarVitrinasAsync(int idTenant)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        return await db.Vitrinas
            .IgnoreQueryFilters(["BajaLogica"])
            .CountAsync(v => v.IdTenant == idTenant);
    }

    /// <summary>Bytes que empiezan con la firma real del formato y siguen con relleno distinguible: lo
    /// único que se mira del contenido son sus primeros bytes y sus bytes de vuelta.</summary>
    public static byte[] Imagen(string tipo, int largo = 64, byte relleno = 7)
    {
        byte[] cabecera = tipo switch
        {
            Vitrina.TipoPng => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
            Vitrina.TipoJpeg => [0xFF, 0xD8, 0xFF, 0xE0],
            Vitrina.TipoWebp => [.. "RIFF"u8, 0x24, 0x00, 0x00, 0x00, .. "WEBP"u8],
            _ => throw new ArgumentOutOfRangeException(nameof(tipo))
        };

        var imagen = new byte[Math.Max(largo, cabecera.Length)];
        Array.Fill(imagen, relleno);
        cabecera.CopyTo(imagen, 0);

        return imagen;
    }

    public static ByteArrayContent Contenido(byte[] bytes, string? tipo)
    {
        var contenido = new ByteArrayContent(bytes);

        if (tipo is not null)
        {
            contenido.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(tipo);
        }

        return contenido;
    }
}
