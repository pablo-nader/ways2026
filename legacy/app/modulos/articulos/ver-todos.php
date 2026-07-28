<?php
    $contenido .= '
		<div class="col-lg-12 mb-4">
			<form name="filtro" id="filtro" method="get">
				<input type="hidden" name="menu" value="articulos">
				<input type="hidden" name="opc" value="ver-todos">
				<div class="col-lg-1"><strong>Filtrar: </strong></div>
				<div class="col-lg-4">
					<select name="proveedor" id="proveedor" class="form-select rounded-0" onchange="this.form.submit()">
						<option>Seleccionar...</option>';
							$obtenerProveedor = mysqli_query($conexion, "SELECT id, nombre FROM proveedores ORDER BY nombre");
							while ($mostrarProveedor = mysqli_fetch_assoc($obtenerProveedor)) {
								if (@$_GET['proveedor'] == $mostrarProveedor['id']) {
									$contenido .= '<option selected value="'.$mostrarProveedor['id'].'">'.$mostrarProveedor['nombre'].'</option>';
								} else {
									$contenido .= '<option value="'.$mostrarProveedor['id'].'">'.$mostrarProveedor['nombre'].'</option>';
								}	
							}
	$contenido .= '
					</select>
				</div>
			</form>
		</div>
		<div class="col-lg-12">
					<input type="hidden" name="menu" value="articulos">
					<input type="hidden" name="opc" value="editarMasivo">
			<table id="dataTable" class="table table-bordered table-condensed table-hover table-striped">
				<thead>
					<tr>
						<th>ID</th>
						<th>Nombre</th>
						<th>UxBulto</th>
						<th>Costo sin IVA</th>
						<th>Costo Final</th>
						<th>Bulto sin IVA</th>
						<th>Bulto Final</th>
						<th>Venta</th>
						<th style="min-width:95px;">Acciones</th>
					</tr>
				</thead>
				<tbody>
 					';
		if (isset($_GET['proveedor'])) {
			$proveedor = $_GET['proveedor'];
			$buscarArticulos="SELECT ID, nombre, lista, precio, uBulto, activo  FROM articulos WHERE id_proveedor = '$proveedor'";
		} else { 
            $buscarArticulos="SELECT ID, nombre, lista, precio, uBulto, activo FROM articulos WHERE activo = 1"; 
        }
		$ejecutar = mysqli_query($conexion, $buscarArticulos);
		while ($mostrar = mysqli_fetch_assoc($ejecutar)) {
			$color = $mostrar['activo'] == 0 ? 'style="background-color:orange;"' : "";
			
			$contenido .= '
					<tr '.$color.'>
						<td>'.str_pad(@$mostrar['ID'], 4, "0", STR_PAD_LEFT).'</td>
						<td><a href="index.php?menu=articulos&opc=editar&id='.$mostrar['ID'].'">'.$mostrar['nombre'].'</a></td>
						<td>'.$mostrar['uBulto'].'</td>
						<td>$'.round(($mostrar['lista']/1.21), 2).'</td>
						<td>$'.$mostrar['lista'].'</td>
						<td>$'.round(($mostrar['lista']*$mostrar['uBulto']/1.21), 2).'</td>
						<td>$'.round(($mostrar['lista']*$mostrar['uBulto']), 2).'</td>
						<td style="text-align:right;">$'.$mostrar['precio'].'</td>
						<td>
                            <a href="index.php?menu=articulos&opc=editar&id='.$mostrar['ID'].'" style="color:blue; text-decoration:none" title="Editar Articulo">
                                <i class="fas fa-edit"></i>
                            </a> &nbsp;';
			$contenido .= $mostrar['activo'] == 1 ? 
                           '<a href="index.php?menu=articulos&opc=eliminar&id='.$mostrar['ID'].'" style="color:red; text-decoration:none" title="Eliminar Articulo"><i class="fas fa-trash"></i></a>' :
                           '<a href="index.php?menu=articulos&opc=restaurar&id='.$mostrar['ID'].'" style="color:green; text-decoration:none" title="Restaurar Articulo"><i class="fas fa-undo-alt"></i></a>';
			$contenido .=  '
						</td>
					</tr>';
		}
					
			$contenido .= '</tbody>
			</table>
		</div>';