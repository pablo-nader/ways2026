export function etiquetaDeProveedor(proveedor: { razonSocial: string; nombreFantasia: string | null }): string {
  const fantasia = proveedor.nombreFantasia?.trim()
  return fantasia ? fantasia : proveedor.razonSocial
}
