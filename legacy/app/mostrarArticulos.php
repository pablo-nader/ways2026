<?php
	require_once './conexion.php';
	$conexion = mysqli_connect(HOST, USER, PASSWORD, DATABASE);

	$consultaBusqueda = $_POST['valorBusqueda'];

	//Filtro anti-XSS
	$caracteres_malos = array("<", ">", "\"", "'", "/", "<", ">", "'", "/");
	$caracteres_buenos = array("& lt;", "& gt;", "& quot;", "& #x27;", "& #x2F;", "& #060;", "& #062;", "& #039;", "& #047;");
	$consultaBusqueda = trim(str_replace($caracteres_malos, $caracteres_buenos, $consultaBusqueda));
	$consulta = "SELECT * 
				 FROM articulos 
				 WHERE nombre LIKE '%$consultaBusqueda%' and activo = 1";
	$palabras = explode(" ", $consultaBusqueda);
	$limite = count($palabras);

	for ($i = 0; $i < $limite; $i++) {
		if ($i == 0) {
			$consulta .= " OR (nombre LIKE '%".$palabras[$i]."%'";
		} else {
			$consulta .= " AND nombre LIKE '%".$palabras[$i]."%'";
		} 

		if ($i == ($limite - 1)) {
			$consulta .= ")";
		}
	}
	$consulta .= " AND activo=1 
				 ORDER BY nombre 
				 LIMIT 100"; 
	
	$mensaje = '<table class="table table-striped responsive-table table-hover table-bordered">
					<thead>
						<tr>
							<th>ID</th>
							<th>Nombre</th>
							<th>Venta</th>
							<th>Oferta</th>
							<th>Cantidad</th>
							<th style="text-align:center;"><i class="fa fa-cart-plus"></i></th>
						</tr>
					</thead>
					<tbody>';


	if (isset($consultaBusqueda)) {

		$consulta2 = mysqli_query($conexion, $consulta);
		$filas = mysqli_num_rows($consulta2);

		if ($filas === 0) {
			$mensaje.= '</tbody></table></div>';
		} else {
			while($mostrarArticulos = mysqli_fetch_assoc($consulta2)) {
				//Output
				$precioCant=number_format(round($mostrarArticulos['precioCant']*$mostrarArticulos['OfertaCantN'],0),"2",".","");
				if($mostrarArticulos['existencia']<0) { $color='style="color:red;"'; }
				elseif($mostrarArticulos['existencia']<$mostrarArticulos['existenciaMinima']) { $color='style="color:#337ab7;"'; }
				else { $color='style="color:green;font-weight:bold;"'; }
				$mensaje.= '
						<tr '.$color.'>
							<td>'.str_pad(@$mostrarArticulos['ID'],4,"0",STR_PAD_LEFT).'</td>
							<td>['.$mostrarArticulos['existencia'].'] '.$mostrarArticulos['nombre'].'</td>
							<td>'.$mostrarArticulos['precio'].'</td>
							<td>'.$mostrarArticulos['precioOferta'].'</td>
							<td>'.$precioCant.' ('.$mostrarArticulos['OfertaCantN'].')</td>
							<td style="text-align:center;"><a href="index.php?menu=facturacion&opc=ventas&agregar='.$mostrarArticulos['ID'].'"><i style="color:green;" class="fa fa-cart-plus"></i></a></td>
							</a>
						</tr>';
			}
			$mensaje.= '</tbody></table></div>';
		}
	}
	echo $mensaje;