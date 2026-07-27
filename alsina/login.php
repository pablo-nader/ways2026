<?php 
	$mensaje = '';
	$error = false;
	if (@$_POST['login'] == 'login') {

		$user = $_POST['user'];
		$pass = $_POST['pass'];
		if (empty($user)) {
			$mensaje = 'Tenés que elegir un usuario';
			$error = true; 
		} elseif (empty($pass)) { 
			$mensaje = 'Tenés que ingresar una contraseña';
			$error = true; 
		} else {
			$query = "SELECT id, user, pass, tipoUser
					  FROM usuarios
					  WHERE id = '$user'";

			$result = mysqli_query($conexion, $query);
			if (mysqli_num_rows($result) == 1) {
				$user = mysqli_fetch_assoc($result);

				if ($user['pass'] == $pass) {

					$id_usuario = $user['id'];
					$query = "	SELECT 	p.id, p.nombre, p.domicilio, p.horario
								FROM 	puntos_venta p
									JOIN usuario_rol_puntoventa urp ON p.id = urp.id_punto_venta AND urp.id_usuario = $id_usuario";
					$puntos_venta = mysqli_query($conexion, $query);
					$cantLocales = mysqli_num_rows($puntos_venta);
					if ($cantLocales == 0) {
						$mensaje = 'El usuario no tiene locales habilitados';
						$error = true; 
					} else if ($cantLocales == 1) {
						$puntoVenta = mysqli_fetch_assoc($puntos_venta);
						$_SESSION['login'] = array(
							'status' => 'ready', 
							'user' => $user['user'], 
							'id' => $user['id'], 
							'tipoUser' => $user['tipoUser'], 
							'punto_venta' => array(
								'id' => $puntoVenta['id'],
								'nombre' => $puntoVenta['nombre'],
								'domicilio' => $puntoVenta['domicilio'],
								'horario' => $puntoVenta['horario']
							)
						);
						echo '<script language="javascript">window.location="index.php?menu=facturacion&opc=ventas"</script>;';
					} else {
						$_SESSION['login'] = array(
							'status' => 'logged', 
							'user' => $user['user'], 
							'id' => $user['id'], 
							'tipoUser' => $user['tipoUser'], 
							'punto_venta' => []
						);
						echo '<script language="javascript">window.location="index.php?menu=elegirLocal"</script>;';
					}
				} else { 
					$mensaje = 'La contraseña ingresada es incorrecta'; 
					$error = true; 
				}
			} else { 
				$mensaje = 'El usuario ingresado es incorrecto'; 
				$error = true; 
			}
		}
	}
	$buscarUsuarios = mysqli_query($conexion, "SELECT id, user FROM usuarios WHERE tipoUser IN (2, 3, 4) ORDER BY user");
?>
<body class="login">
	<div class="form-signin rounded-0">
		<h1 class="text-center ways-brand">
			Ways
		</h1>
		<hr>
		<div class="tab-content">
			<div id="login" class="tab-pane active">
				<form name="login" action="index.php?menu=login" method="post">
					<p class="text-muted text-center">Ingresar Usuario y Contraseña</p>
					<select name="user" id="user" class="form-select mb-3 rounded-0" required>
						<option value="">Seleccioná el usuario</option>
						<?php 
						while($usuarios = mysqli_fetch_assoc($buscarUsuarios)) {
							echo '<option value="'.$usuarios['id'].'">'.$usuarios['user'].'</option>';
						}
						?>
					</select>
					<input name="pass" id="pass" type="password" placeholder="Contraseña" class="form-control mb-3 rounded-0" required>
					<?php 
					if ($error) { 
						echo '<div class="text-danger text-center mb-3">'.$mensaje.'</div>'; 
					}
					?>
					<input type="hidden" name="login" id="login" value="login">
					<button class="btn btn-lg btn-success form-control rounded-0" type="submit">Continuar</button>
				</form>
			</div>
		</div>
	</div>
	<script src="https://cdn.jsdelivr.net/npm/bootstrap@5.2.0-beta1/dist/js/bootstrap.bundle.min.js" integrity="sha384-pprn3073KE6tl6bjs2QrFaJGz5/SUsLqktiwsUTF55Jfv3qYSDhgCecCxMW52nD2" crossorigin="anonymous"></script>	
</body>