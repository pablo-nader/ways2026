using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenIddict.EntityFrameworkCore.Models;

namespace Ways.Api.ConectorMcp;

/// <summary>
/// Almacén de OpenIddict (cliente, autorizaciones, alcances y tokens) en la misma base de Ways, con
/// migraciones y tabla de historial propias: no forma parte del modelo ni del snapshot de
/// <c>WaysDbContext</c>. Las tablas son de plataforma (doc 09, categoría global): sin tenant ni RLS.
/// Los nombres de tablas, columnas, claves e índices siguen la convención del doc 10.
/// </summary>
public sealed class ContextoOAuthDelConector(DbContextOptions<ContextoOAuthDelConector> opciones) : DbContext(opciones)
{
    public const string TablaDeHistorial = "historial_migraciones_conector_mcp";

    public const string TablaDeAplicaciones = "aplicaciones_oauth";
    public const string TablaDeAutorizaciones = "autorizaciones_oauth";
    public const string TablaDeAlcances = "alcances_oauth";
    public const string TablaDeTokens = "tokens_oauth";

    // Propiedades sombra que OpenIddict declara como claves foráneas de sus navegaciones.
    private const string ClaveForaneaDeAplicacion = "ApplicationId";
    private const string ClaveForaneaDeAutorizacion = "AuthorizationId";

    /// <summary>Sin estrategia de reintentos: los stores de OpenIddict abren sus propias
    /// transacciones, que la estrategia con reintentos de Npgsql rechaza.</summary>
    public static void Configurar(DbContextOptionsBuilder opciones, string cadena) =>
        opciones.UseNpgsql(cadena, npgsql => npgsql.MigrationsHistoryTable(TablaDeHistorial));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.UseOpenIddict();

        modelBuilder.Entity<OpenIddictEntityFrameworkCoreApplication>(aplicacion =>
        {
            Tabla(aplicacion, TablaDeAplicaciones, "id_aplicacion_oauth");
            aplicacion.Property(x => x.ApplicationType).HasColumnName("tipo_aplicacion");
            aplicacion.Property(x => x.ClientId).HasColumnName("id_cliente");
            aplicacion.Property(x => x.ClientSecret).HasColumnName("secreto_cliente");
            aplicacion.Property(x => x.ClientType).HasColumnName("tipo_cliente");
            aplicacion.Property(x => x.ConsentType).HasColumnName("tipo_consentimiento");
            aplicacion.Property(x => x.DisplayName).HasColumnName("nombre_visible");
            aplicacion.Property(x => x.DisplayNames).HasColumnName("nombres_visibles");
            aplicacion.Property(x => x.JsonWebKeySet).HasColumnName("claves_json_web");
            aplicacion.Property(x => x.Permissions).HasColumnName("permisos");
            aplicacion.Property(x => x.PostLogoutRedirectUris).HasColumnName("uris_redireccion_cierre_sesion");
            aplicacion.Property(x => x.RedirectUris).HasColumnName("uris_redireccion");
            aplicacion.Property(x => x.Requirements).HasColumnName("requisitos");
            aplicacion.Property(x => x.Settings).HasColumnName("ajustes");
            aplicacion.HasIndex(x => x.ClientId).HasDatabaseName("ux_aplicaciones_oauth_id_cliente");
        });

        modelBuilder.Entity<OpenIddictEntityFrameworkCoreAuthorization>(autorizacion =>
        {
            Tabla(autorizacion, TablaDeAutorizaciones, "id_autorizacion_oauth");
            autorizacion.Property(x => x.CreationDate).HasColumnName("fecha_creacion");
            autorizacion.Property(x => x.Scopes).HasColumnName("alcances");
            autorizacion.Property(x => x.Status).HasColumnName("estado");
            autorizacion.Property(x => x.Subject).HasColumnName("sujeto");
            autorizacion.Property(x => x.Type).HasColumnName("tipo");
            autorizacion.Property<string>(ClaveForaneaDeAplicacion).HasColumnName("id_aplicacion_oauth");
            autorizacion.HasOne(x => x.Application).WithMany(x => x.Authorizations)
                .HasConstraintName("fk_autorizaciones_oauth_aplicacion");
            autorizacion.HasIndex(ClaveForaneaDeAplicacion, nameof(OpenIddictEntityFrameworkCoreAuthorization.Status),
                    nameof(OpenIddictEntityFrameworkCoreAuthorization.Subject), nameof(OpenIddictEntityFrameworkCoreAuthorization.Type))
                .HasDatabaseName("ix_autorizaciones_oauth_aplicacion_estado_sujeto_tipo");
        });

        modelBuilder.Entity<OpenIddictEntityFrameworkCoreScope>(alcance =>
        {
            Tabla(alcance, TablaDeAlcances, "id_alcance_oauth");
            alcance.Property(x => x.Description).HasColumnName("descripcion");
            alcance.Property(x => x.Descriptions).HasColumnName("descripciones");
            alcance.Property(x => x.DisplayName).HasColumnName("nombre_visible");
            alcance.Property(x => x.DisplayNames).HasColumnName("nombres_visibles");
            alcance.Property(x => x.Name).HasColumnName("nombre");
            alcance.Property(x => x.Resources).HasColumnName("recursos");
            alcance.HasIndex(x => x.Name).HasDatabaseName("ux_alcances_oauth_nombre");
        });

        modelBuilder.Entity<OpenIddictEntityFrameworkCoreToken>(token =>
        {
            Tabla(token, TablaDeTokens, "id_token_oauth");
            token.Property(x => x.CreationDate).HasColumnName("fecha_creacion");
            token.Property(x => x.ExpirationDate).HasColumnName("fecha_vencimiento");
            token.Property(x => x.Payload).HasColumnName("contenido");
            token.Property(x => x.RedemptionDate).HasColumnName("fecha_canje");
            token.Property(x => x.ReferenceId).HasColumnName("id_referencia");
            token.Property(x => x.Status).HasColumnName("estado");
            token.Property(x => x.Subject).HasColumnName("sujeto");
            token.Property(x => x.Type).HasColumnName("tipo");
            token.Property<string>(ClaveForaneaDeAplicacion).HasColumnName("id_aplicacion_oauth");
            token.Property<string>(ClaveForaneaDeAutorizacion).HasColumnName("id_autorizacion_oauth");
            token.HasOne(x => x.Application).WithMany(x => x.Tokens)
                .HasConstraintName("fk_tokens_oauth_aplicacion");
            token.HasOne(x => x.Authorization).WithMany(x => x.Tokens)
                .HasConstraintName("fk_tokens_oauth_autorizacion");
            token.HasIndex(ClaveForaneaDeAplicacion, nameof(OpenIddictEntityFrameworkCoreToken.Status),
                    nameof(OpenIddictEntityFrameworkCoreToken.Subject), nameof(OpenIddictEntityFrameworkCoreToken.Type))
                .HasDatabaseName("ix_tokens_oauth_aplicacion_estado_sujeto_tipo");
            token.HasIndex(ClaveForaneaDeAutorizacion).HasDatabaseName("ix_tokens_oauth_autorizacion");
            token.HasIndex(x => x.ReferenceId).HasDatabaseName("ux_tokens_oauth_id_referencia");
        });
    }

    private static void Tabla<T>(EntityTypeBuilder<T> entidad, string tabla, string columnaId)
        where T : class
    {
        entidad.ToTable(tabla);
        entidad.HasKey("Id").HasName($"pk_{tabla}");
        entidad.Property("Id").HasColumnName(columnaId);
        entidad.Property("ConcurrencyToken").HasColumnName("token_concurrencia");
        entidad.Property("Properties").HasColumnName("propiedades");
    }
}
