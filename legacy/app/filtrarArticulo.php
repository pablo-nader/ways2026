<?php
// Conectando, seleccionando la base de datos
// Este archivo tenía sus propias credenciales apuntando a otra base (c1890978_ways).
// Ahora usa la misma conexión por variables de entorno que el resto del sistema.
require_once __DIR__ . '/conexion.php';
$conexion = mysqli_connect(HOST, USER, PASSWORD, DATABASE) or die('No se pudo conectar a la base de datos');

//Variable de búsqueda
$consultaBusqueda = $_POST['valorBusqueda'];
$filtrado = $_POST['filtrado'];

//Filtro anti-XSS
$caracteres_malos = array("<", ">", "\"", "'", "/", "<", ">", "'", "/");
$caracteres_buenos = array("& lt;", "& gt;", "& quot;", "& #x27;", "& #x2F;", "& #060;", "& #062;", "& #039;", "& #047;");
$consultaBusqueda = str_replace($caracteres_malos, $caracteres_buenos, $consultaBusqueda);

	$filtrado='general'; 
	$consulta = "SELECT * FROM articulos WHERE 	proveedor LIKE '%$consultaBusqueda%' OR
																	nombre LIKE '%$consultaBusqueda%' OR
																	barra LIKE '%$consultaBusqueda%' OR 
																	grupo LIKE '%$consultaBusqueda%' OR
																	marca LIKE '%$consultaBusqueda%' AND activo='1' ORDER BY nombre DESC LIMIT 30"; 

//Variable vacía (para evitar los E_NOTICE)
$mensaje = '<table class="table table-striped responsive-table table-hover table-bordered">
				<thead>
					<tr>
						<th>ID</th>
						<th>Nombre</th>
						<th>Lista</th>
						<th>Venta</th>
						<th>Proveedor</th>
						<th>Grupo</th>
						<th>Caja</th>
					</tr>
				</thead>
				<tbody>';

//Comprueba si $consultaBusqueda está seteado
if (isset($consultaBusqueda)) {

	$consulta2 = mysqli_query($conexion, $consulta);

	//Obtiene la cantidad de filas que hay en la consulta
	$filas = mysqli_num_rows($consulta2);

	//Si no existe ninguna fila que sea igual a $consultaBusqueda, entonces mostramos el siguiente mensaje
	if ($filas === 0) {
		$mensaje.= '</tbody></table></div>';
	} 
	else {
		//Si existe alguna fila que sea igual a $consultaBusqueda, entonces mostramos el siguiente mensaje

		//La variable $resultado contiene el array que se genera en la consulta, así que obtenemos los datos y los mostramos en un bucle
		while($mostrarArticulos = mysqli_fetch_assoc($consulta2)) {
			if($mostrarArticulos['existencia']==0) { $color='red'; }
			elseif($mostrarArticulos['existencia']<0) { $color='red;font-weight:bold'; }
			elseif($mostrarArticulos['existencia']<$mostrarArticulos['existenciaMinima']) { $color='orange'; }
			else { $color='green;font-weight:bold'; }
			//Output
			$prov=$mostrarArticulos['proveedor'];
			$b_prov=mysqli_fetch_array(mysqli_query($conexion,"SELECT nombre FROM proveedores WHERE id='$prov'"));
			$grup=$mostrarArticulos['grupo'];
			$b_grup=mysqli_fetch_array(mysqli_query($conexion,"SELECT nombre FROM grupos WHERE id='$grup'"));
			$caja=$mostrarArticulos['caja'];
			$b_caja=mysqli_fetch_array(mysqli_query($conexion,"SELECT nombre FROM caja WHERE id='$caja'"));
			$mensaje.= '
					<tr>
						<td style="color:'.$color.';"><a href="index.php?menu=articulos&opc=nuevo&id='.@$mostrarArticulos['barra'].'" target="_blank">'.str_pad(@$mostrarArticulos['ID'],4,"0",STR_PAD_LEFT).'</a></td>
						<td style="color:'.$color.';">['.$mostrarArticulos['existencia'].'] '.$mostrarArticulos['nombre'].'</td>
						<td style="color:'.$color.';">'.$mostrarArticulos['lista'].'</td>
						<td style="color:'.$color.';">'.$mostrarArticulos['precio'].'</td>
						<td style="color:'.$color.';">'.$b_prov[0].'</td>
						<td style="color:'.$color.';">'.$b_grup[0].'</td>
						<td style="color:'.$color.';">'.$b_caja[0].'</td>
					</tr>
			';
		};//Fin while $resultados
		$mensaje.= '</tbody></table></div>
		';
	}; //Fin else $filas

};//Fin isset $consultaBusqueda

//Devolvemos el mensaje que tomará jQuery
echo $mensaje;
?>