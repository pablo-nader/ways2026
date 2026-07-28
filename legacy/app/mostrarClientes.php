<?php
	require_once './conexion.php';

	$conexion = mysqli_connect(HOST, USER, PASSWORD, DATABASE);

	//Variable de búsqueda
	$consultaBusqueda = $_POST['valorBusqueda'];

	//Filtro anti-XSS
	$caracteres_malos = array("<", ">", "\"", "'", "/", "<", ">", "'", "/");
	$caracteres_buenos = array("& lt;", "& gt;", "& quot;", "& #x27;", "& #x2F;", "& #060;", "& #062;", "& #039;", "& #047;");
	$consultaBusqueda = str_replace($caracteres_malos, $caracteres_buenos, $consultaBusqueda);
	$consulta = "SELECT * FROM usuarios WHERE 	user LIKE '%$consultaBusqueda%' OR
																		nombre LIKE '%$consultaBusqueda%' OR
																		apellido LIKE '%$consultaBusqueda%' OR 
																		tel LIKE '%$consultaBusqueda%' OR
																		id LIKE '%$consultaBusqueda%' OR
																		cel LIKE '%$consultaBusqueda%' ORDER BY nombre LIMIT 20"; 
		

	//Variable vacía (para evitar los E_NOTICE)
	$mensaje = '<table class="table table-striped responsive-table table-hover table-bordered">
					<thead>
						<tr>
							<th>Cliente</th>
							<th>Nombre</th>
							<th>Direccion</th>
							<th>Telefono</th>
							<th>Celular</th>
							<th>Saldo</th>
							<th>Acuerdo</th>
							<th style="text-align:center;"><i class="fa fa-user-plus"></i></th>
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
			while($mostrarClientes = mysqli_fetch_assoc($consulta2)) {
				//Output
				$mensaje.= '
						<tr>
							<td>'.str_pad(@$mostrarClientes['id'],4,"0",STR_PAD_LEFT).'</td>
							<td>'.$mostrarClientes['nombre'].' '.$mostrarClientes['apellido'].'</td>
							<td>'.$mostrarClientes['domicilio'].'</td>
							<td>'.$mostrarClientes['tel'].'</td>
							<td>'.$mostrarClientes['cel'].'</td>
							<td>'.$mostrarClientes['saldo'].'</td>
							<td>'.$mostrarClientes['acuerdo'].'</td>
							<td style="text-align:center;"><a href="index.php?menu=facturacion&opc=ventas&cambiarUser='.$mostrarClientes['id'].'"><i style="color:green;" class="fa fa-user-plus"></i></a></td>
						</tr>
				';
			};//Fin while $resultados
			$mensaje.= '</tbody></table></div>
			';
		}; //Fin else $filas

	};//Fin isset $consultaBusqueda

	//Devolvemos el mensaje que tomará jQuery
	echo $mensaje;