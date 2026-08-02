using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="Proveedor"/> (design decision 1, Table Shapes): entidad dedicada, dedupe
/// por <c>cuit</c> tenant-wide, no por <c>nombre</c>/empresa como
/// <c>ConfiguracionDeCatalogo&lt;T&gt;</c>.
/// </summary>
public class ProveedorConfiguration : IEntityTypeConfiguration<Proveedor>
{
    public void Configure(EntityTypeBuilder<Proveedor> builder)
    {
        builder.ToTable("proveedores");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id)
            .HasColumnName("id_proveedor")
            .UseIdentityByDefaultColumn();

        builder.Property(p => p.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        builder.Property(p => p.IdEmpresa)
            .HasColumnName("id_empresa");

        builder.Property(p => p.RazonSocial)
            .HasColumnName("razon_social")
            .HasColumnType("citext")
            .HasMaxLength(150)
            .IsRequired();

        builder.Property(p => p.NombreFantasia)
            .HasColumnName("nombre_fantasia")
            .HasColumnType("citext")
            .HasMaxLength(150);

        // Sin citext (a diferencia del resto de los campos de texto de esta tabla): el cuit
        // es un valor formateado, no un texto buscado case-insensitive (mismo criterio que
        // Empresa.Cuit).
        builder.Property(p => p.Cuit)
            .HasColumnName("cuit")
            .HasMaxLength(13);

        builder.Property(p => p.IdCondicionFiscal)
            .HasColumnName("id_condicion_fiscal")
            .IsRequired();

        builder.Property(p => p.Domicilio)
            .HasColumnName("domicilio")
            .HasColumnType("citext")
            .HasMaxLength(255);

        builder.Property(p => p.Telefono)
            .HasColumnName("telefono")
            .HasColumnType("citext")
            .HasMaxLength(50);

        builder.Property(p => p.Email)
            .HasColumnName("email")
            .HasColumnType("citext")
            .HasMaxLength(255);

        builder.Property(p => p.Vendedor)
            .HasColumnName("vendedor")
            .HasColumnType("citext")
            .HasMaxLength(150);

        builder.Property(p => p.CelularVendedor)
            .HasColumnName("celular_vendedor")
            .HasColumnType("citext")
            .HasMaxLength(50);

        builder.Property(p => p.Supervisor)
            .HasColumnName("supervisor")
            .HasColumnType("citext")
            .HasMaxLength(150);

        builder.Property(p => p.CelularSupervisor)
            .HasColumnName("celular_supervisor")
            .HasColumnType("citext")
            .HasMaxLength(50);

        builder.Property(p => p.Margen)
            .HasColumnName("margen")
            .HasColumnType("numeric(5,2)");

        builder.Property(p => p.Observaciones)
            .HasColumnName("observaciones")
            .HasColumnType("text");

        builder.Property(p => p.Activo)
            .HasColumnName("activo")
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(p => p.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(p => p.EstaEliminada);

        // Spec "cuit Uniqueness Is Scoped Per Tenant": único por tenant, sin id_empresa en
        // la clave (a propósito — el mismo proveedor puede repetirse entre empresas del
        // mismo tenant), NULL permitido y no comparado.
        builder.HasIndex(p => new { p.IdTenant, p.Cuit })
            .HasDatabaseName("ux_proveedores_cuit")
            .HasFilter("deleted_at IS NULL AND cuit IS NOT NULL")
            .IsUnique();

        builder.HasIndex(p => p.IdTenant).HasDatabaseName("ix_proveedores_tenant");
        builder.HasIndex(p => new { p.IdEmpresa, p.IdTenant }).HasDatabaseName("ix_proveedores_empresa");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(p => p.IdTenant)
            .HasConstraintName("fk_proveedores_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>()
            .WithMany()
            .HasForeignKey(p => new { p.IdEmpresa, p.IdTenant })
            .HasPrincipalKey(e => new { e.Id, e.IdTenant })
            .HasConstraintName("fk_proveedores_empresa")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<CondicionFiscal>()
            .WithMany()
            .HasForeignKey(p => p.IdCondicionFiscal)
            .HasConstraintName("fk_proveedores_condicion_fiscal")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
