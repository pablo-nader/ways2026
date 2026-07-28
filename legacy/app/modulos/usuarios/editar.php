<?php
	$usuario=$_GET['usuario'];
	$datosUsuario=mysqli_query($conexion,"SELECT * FROM usuarios WHERE id='$usuario'");
	$mostrar=mysqli_fetch_assoc($datosUsuario);
	if(@$_GET['actualizar']=='Actualizar Datos') {
		$nombre=$_GET['nombre'];
		$apellido=$_GET['apellido'];
		if(isset($_GET['dni'])) { 
			$dni=$_GET['dni']; 
			$dni=str_replace(".", "", $dni);
		} else $dni='';	
		if(isset($_GET['nacimiento'])) {
			$nacimiento=$_GET['nacimiento'];
		} else $nacimiento='';
		$domicilio=$_GET['domicilio'];
		if(!empty($_GET['tel'])) {
			$tel=$_GET['tel'];
		} else $tel='';
		if(!empty($_GET['cel'])) {
			$cel=$_GET['cel'];
		} else $cel='';
		$mail=$_GET['mail'];
		$acuerdo=$_GET['acuerdo'];
		$saldo=$_GET['saldo'];
		
		$tipoUser=$_GET['tipoUser'];
		$user=$_GET['user'];
		$pass=$_GET['pass'];
		$lista=$_GET['lista'];

		$editarUsuario="UPDATE usuarios SET tipoUser='$tipoUser', nombre='$nombre', apellido='$apellido', user='$user', pass='$pass', lista='$lista', dni='$dni', nacimiento='$nacimiento', domicilio='$domicilio',	tel='$tel', cel='$cel', mail='$mail', acuerdo='$acuerdo', saldo='$saldo' WHERE id='$usuario'"; 
			if(mysqli_query($conexion,$editarUsuario)) {
				$contenido.='<div class="col-lg-12 alert alert-success rounded-0 me-3 ms-3">Los datos del Usuario se editaron correctamente</div>';
			}
			else { 	$contenido.='<div class="col-lg-12 alert alert-danger rounded-0 me-3 ms-3">Ocurrió un error, la consulta ejecutada fue: '.$editarUsuario.'</div>'; }

	}
	else {
		$contenido.='
	<form class="row" method="get" name="usuarios" id="usuarios" action="" autocomplete="off">
		<input type="hidden" name="menu" value="usuarios">
		<input type="hidden" name="opc" value="editar">
		<input type="hidden" name="usuario" value="'.$usuario.'">

		<div class="col-lg-6">
			<div class="row">
				<div class="col-lg-12">
					<input style="text-align:center;" disabled type="text" id="nombre" name="nombre" value="Datos del Usuario" class="form-control rounded-0 mb-3">
				</div>
				<label for="nombre" class="control-label col-lg-4 mb-3">Nombre</label>
				<div class="col-lg-8 mb-3">
					<input type="text" id="nombre" name="nombre" value="'.$mostrar['nombre'].'" class="form-control rounded-0" autofocus required>
				</div>
				<label for="apellido" class="control-label col-lg-4 mb-3">Apellido</label>
				<div class="col-lg-8 mb-3">
					<input type="text" id="apellido" name="apellido" value="'.$mostrar['apellido'].'" class="form-control rounded-0" required>
				</div>
				<label for="dni" class="control-label col-lg-4 mb-3">DNI</label>
				<div class="col-lg-8 mb-3">
					<input type="text" id="dni" name="dni" value="'.$mostrar['dni'].'" class="form-control rounded-0" required>
				</div>
				<label for="nacimiento" class="control-label col-lg-4 mb-3">F. Nacimiento</label>
				<div class="col-lg-8 mb-3">
					<input type="text" id="nacimiento" name="nacimiento" value="'.$mostrar['nacimiento'].'" class="form-control rounded-0" required>
				</div>
				<label for="domicilio" class="control-label col-lg-4 mb-3">Domicilio</label>
				<div class="col-lg-8 mb-3">
					<input type="text" id="domicilio" name="domicilio" value="'.$mostrar['domicilio'].'" class="form-control rounded-0" required>
				</div>
				<label for="tel" class="control-label col-lg-4 mb-3">Telefono</label>
				<div class="col-lg-8 mb-3">
					<input type="text" id="tel" name="tel" value="'.$mostrar['tel'].'" class="form-control rounded-0">
				</div>
				<label for="cel" class="control-label col-lg-4 mb-3">Celular</label>
				<div class="col-lg-8 mb-3">
					<input type="text" id="cel" name="cel" value="'.$mostrar['cel'].'" class="form-control rounded-0">
				</div>
				<label for="mail" class="control-label col-lg-4 mb-3">E-mail</label>
				<div class="col-lg-8 mb-3">
					<input type="email" id="mail" name="mail" value="'.$mostrar['mail'].'" class="form-control rounded-0" required>
				</div>
				<label for="acuerdo" class="control-label col-lg-4 mb-3">Acuerdo</label>
				<div class="col-lg-8 mb-3">
					<input type="text" id="acuerdo" name="acuerdo" value="'.$mostrar['acuerdo'].'" class="form-control rounded-0" required>
				</div>

				<label for="saldo" class="control-label col-lg-4 mb-3">Saldo</label>
				<div class="col-lg-8 mb-3">
					<input type="text" id="saldo" name="saldo" value="'.$mostrar['saldo'].'" class="form-control rounded-0	" required>
				</div>
			</div>
		</div>
		<div class="col-lg-6">
			<div class="row">
				<div class="col-lg-12">
					<input style="text-align:center;" disabled type="text" id="nombre" name="nombre" value="Datos de Sistema" class="form-control rounded-0 mb-3">
				</div>
				<label class="control-label col-lg-4 mb-3">Tipo de Usuario</label>
				<div class="col-lg-8 mb-3">';
		if($mostrar['tipoUser']==4) {
			$contenido.='<input disabled type="text" value="Super Administrador" class="form-control rounded-0">
						<input type="hidden" name="tipoUser" id="tipoUser" value="4" class="form-control">';
		}
		else {
			if($mostrar['tipoUser']==1) $selected1='selected';
			elseif($mostrar['tipoUser']==2) $selected2='selected';
			elseif($mostrar['tipoUser']==3) $selected3='selected';
			$contenido.='<select name="tipoUser" class="form-select rounded-0">
						<option value="1" '.@$selected1.'>Cliente</option>
						<option value="2" '.@$selected2.'>Vendedor</option>
						<option value="3" '.@$selected3.'>Administrador</option>
					</select>';
		}
				$contenido.='</div>
				<label for="user" class="control-label col-lg-4 mb-3">Usuario</label>
				<div class="col-lg-8 mb-3">
					<input type="text" id="user" name="user" value="'.$mostrar['user'].'" class="form-control rounded-0">
				</div>
				<label for="pass" class="control-label col-lg-4 mb-3">Contraseña</label>
				<div class="col-lg-8 mb-3">
					<input type="password" id="pass" name="pass" value="'.$mostrar['pass'].'" class="form-control rounded-0">
				</div>
				<label for="repass" class="control-label col-lg-4 mb-3">Repetir</label>
				<div class="col-lg-8 mb-3">
					<input type="password" id="repass" name="repass" value="'.$mostrar['pass'].'" class="form-control rounded-0">
				</div>
				<label for="pass" class="control-label col-lg-4 mb-3">Lista de Precios</label>
				<div class="col-lg-8 mb-3">';
			if($mostrar['lista']==1) $lista1='selected';
			elseif($mostrar['lista']==2) $lista2='selected';
			elseif($mostrar['lista']==3) $lista3='selected';
			elseif($mostrar['lista']==4) $lista4='selected';
			$contenido.='<select name="lista" class="form-select rounded-0">
						<option value="1" '.@$lista1.'>Normal</option>
						<option value="2" '.@$lista2.'>Descuento Especial</option>
						<option value="3" '.@$lista3.'>5% Descuento</option>
						<option value="4" '.@$lista4.'>10% Descuento</option>
					</select>';
		$contenido.='		
				</div>	
				<div class="col-lg-12 mb-3"><label class="control-label mb-3">&nbsp;</label></div>
				<div class="col-lg-12 mb-3"><input type="submit" class="form-control btn btn-success rounded-0" name="actualizar" value="Actualizar Datos"></div>
				<div class="col-lg-12 mb-3"><label class="control-label mb-3">&nbsp;</label></div>
				<div class="col-lg-12 mb-3"><input type="reset" class="form-control btn btn-dark rounded-0" value="Restablecer"></div>
			</div>				
		</div>
		
	</form>';
	}