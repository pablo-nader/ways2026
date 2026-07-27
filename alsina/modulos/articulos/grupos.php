<?php
	if (isset($_POST['id'])) {
			if (@$_POST['crear'] == 'crear') {
				if (!empty($_POST['nombre']) && !empty($_POST['margen'])) {
					$nombre = ucwords($_POST['nombre']);
					$margen = number_format($_POST['margen'], 2);
					if ($crearGrupo = mysqli_query($conexion, "INSERT INTO grupos (nombre, margen) VALUES ('$nombre', '$margen')")) {
						$id = mysqli_insert_id($conexion);
						$mensaje = '
						<div class="alert alert-success rounded-0">
							El Grupo ID: '.str_pad($id, 4, "0", STR_PAD_LEFT).' ('.$nombre.') ha sido creado correctamente.
						</div>';
					} else {
						$mensaje = '
						<div class="alert alert-danger rounded-0">
							Ocurrió un error al crear el Grupo.
						</div>';
					}
				}
			} elseif (@$_POST['accion'] == 'Editar Grupo') {
				$id = $_POST['id'];
				$nombre = ucwords($_POST['nombre']);
				$margen = number_format($_POST['margen'], 2);
				if ($editarGrupo = mysqli_query($conexion, "UPDATE grupos SET nombre = '$nombre', margen = '$margen' WHERE id = '$id'")) {
					$mensaje = '
					<div class="alert alert-success rounded-0">
						El Grupo ID: '.str_pad($id, 4, "0", STR_PAD_LEFT).' ('.$nombre.') ha sido editado correctamente.
					</div>';
				} else {
					$mensaje = '
					<div class="alert alert-danger rounded-0">
						Ocurrió un error al editar el Grupo ID: '.str_pad($mostrarGrupo['id'],4,"0",STR_PAD_LEFT).' ('.$mostrarGrupo['nombre'].').
					</div>';
				}
			}
		}
		if (isset($_GET['id'])) {
			$id = $_GET['id'];
			$buscarGrupo = mysqli_query($conexion, "SELECT * FROM grupos WHERE id = '$id' ORDER BY nombre");
			if (mysqli_num_rows($buscarGrupo) == 1) {
				if (@$_POST['accion'] == 'Editar Grupo') {
					$id = $_POST['id'];
					$nombre = $_POST['nombre'];
					$margen = $_POST['margen'];
					if ($editarGrupo = mysqli_query($conexion, "UPDATE grupos SET nombre = '$nombre', margen = '$margen' WHERE id = '$id'")) {
						$mensaje = '
						<div class="alert alert-success rounded-0">
							El Grupo ID: '.str_pad($id, 4, "0", STR_PAD_LEFT).' ('.$nombre.') ha sido editado correctamente.
						</div>';
					} else {
						$mensaje = '
						<div class="alert alert-danger rounded-0">
							Ocurrió un error al editar el Grupo ID: '.str_pad($mostrarGrupo['id'],4,"0",STR_PAD_LEFT).' ('.$mostrarGrupo['nombre'].').
						</div>';
					}
				}
				$mostrarGrupo = mysqli_fetch_assoc($buscarGrupo);
				$boton = '
					<div class="col-lg-12">
						<input name="accion" id="accion" type="submit" class="form-control btn btn-success rounded-0" value="Editar Grupo">
					</div>';
				$editar = true;
				$mensaje = '
					<div class="alert alert-warning rounded-0">
						Estás a punto de editar el Grupo '.$mostrarGrupo['nombre'].' ('.str_pad($mostrarGrupo['id'], 4, "0", STR_PAD_LEFT).').
					</div>';
			}
			else { $editar=FALSE; }
		}
		if (@!$editar) { 
			$boton = '
					<div class="col-lg-12">
						<input name="crear" id="crear" type="hidden" class="form-control rounded-0 mb-3" value="crear">
						<input name="accion" id="accion" type="submit" class="form-control btn btn-success rounded-0 mb-3" value="Crear Grupo">
					</div>';
		}
		$contenido .= '
		<div class="col-lg-4">
			'.$mensaje.'
			<form class="row mb-3" method="post" action="" autocomplete="off">
				<div class="row">
					<label for="id" class="control-label mb-3 col-lg-4">ID</label>
					<div class="col-lg-8">
						<input type="text" id="id" name="id" value="'.@$mostrarGrupo['id'].'" readonly class="form-control rounded-0 mb-3">
					</div>
				</div>
				<div class="row">
					<label for="nombre" class="control-label mb-3 col-lg-4">Nombre</label>
					<div class="col-lg-8">
						<input class="form-control rounded-0 mb-3" type="text" value="'.@$mostrarGrupo['nombre'].'" id="nombre" name="nombre" autofocus required>
					</div>
				</div>
				<div class="row">
					<label for="margen" class="control-label mb-3 col-lg-4">Margen %</label>
					<div class="col-lg-8">
						<input type="text" name="margen" value="'.@$mostrarGrupo['margen'].'" id="margen" class="form-control rounded-0 mb-3" required>
					</div>
				</div>
				<div class="row">
					'.$boton.'
				</div>
			</form>
			<table class="table table-striped responsive-table table-hover table-bordered">
				<thead>
					<tr>
						<th>ID</th>
						<th>Nombre</th>
						<th>Margen %</th>
						<th style="text-align:center;"><i class="fa fa-search-dollar"></i></th>
						<th style="text-align:center;"><i class="fa fa-times"></i></th>
						<th style="text-align:center;"><i class="fa fa-trash"></i></th>
					</tr>
				</thead>
				<tbody>';
		$buscarGrupos = mysqli_query($conexion, "SELECT * FROM grupos ORDER BY nombre");
		while ($mostrarGrupos = mysqli_fetch_assoc($buscarGrupos)) {
			$contenido .= '
					<tr>
						<td><a href="index.php?menu=articulos&opc=grupos&id='.$mostrarGrupos['id'].'">'.str_pad($mostrarGrupos['id'],4,"0",STR_PAD_LEFT).'</a></td>
						<td>'.$mostrarGrupos['nombre'].'</td>
						<td>'.$mostrarGrupos['margen'].'</td>';
						if ($mostrarGrupos['ofertaCantidad'] == 1 || $mostrarGrupos['ofertaDirecta'] == 1) {
							$contenido .= '
							<td style="text-align:center;"><a style="color:orange;" title="Ver Oferta" href="index.php?menu=articulos&opc=grupos&grupo='.$mostrarGrupos['id'].'"><i class="fa fa-search-dollar"></i></a></td>
							<td style="text-align:center;"><a style="color:purple;" title="Eliminar Oferta" href="index.php?menu=articulos&opc=grupos&eliminarOfertaGrupo='.$mostrarGrupos['id'].'"><i class="fa fa-times"></i></a></td>';
						} else {
							$contenido .= '
							<td style="text-align:center;"><i class="fa fa-search-dollar"></i></td>
							<td style="text-align:center;"><i class="fa fa-times"></i></td>';
						}
						$contenido .= '
						<td style="text-align:center;"><a style="color:red;" title="Eliminar Grupo" href="index.php?menu=articulos&opc=grupos&eliminarGrupo='.$mostrarGrupos['id'].'"><i class="fa fa-trash"></i></a></td>
					</tr>';
		}
		$contenido.='
				</tbody>
			</table>
		</div>
		<div class="col-lg-8">';
		
		if (isset($_GET['eliminarOfertaGrupo'])) {
			$id = $_GET['eliminarOfertaGrupo'];
			$consulta = "UPDATE grupos 
						 SET 	ofertaCantidad = '0', 
						 		ofertaDirecta = '0', 
								dias = '0', 
								horas = '0', 
								cantidad = '0', 
								precio = '0', 
								descuento = '0', 
								descripcion = '0', 
								dDesde = '0', 
								dHasta = '0', 
								hDesde = '0', 
								hHasta = '0' 
						WHERE 	id = '$id'";
			if (mysqli_query($conexion, $consulta)) {
				$contenido .= '<div class="col-lg-12"><div class="alert alert-success rounded-0">Todos los datos de oferta del grupo ID: '.$id.' han sido reseteados.</div></div>';
			} else {
				$contenido .= '<div class="col-lg-12"><div class="alert alert-danger rounded-0">Ocurrio un error al procesar la consulta</div></div>';
			}
		}
		if (isset($_GET['eliminarGrupo'])) {
			$id = $_GET['eliminarGrupo'];
			$nombre=mysqli_fetch_assoc(mysqli_query($conexion,"SELECT nombre FROM grupos WHERE id = '$id'"))['nombre'];
			$consulta = "DELETE FROM grupos WHERE id = '$id'";
			$consulta2 = "UPDATE articulos SET grupo = '0' WHERE grupo = '$id'";
			if (mysqli_query($conexion, $consulta)) {
				mysqli_query($conexion, $consulta2);
				$contenido .= '<div class="col-lg-12"><div class="alert alert-success rounded-0">El Grupo ID: '.$id.' fue eliminado correctamente.</div></div>';
			} else {
				$contenido.='<div class="col-lg-12"><div class="alert alert-danger rounded-0">Ocurrio un error al procesar la consulta</div></div>';
			}
		}
		if (isset($_POST['idGrupo'])) {
			$idGrupo = $_POST['idGrupo'];
			$buscar = mysqli_query($conexion, "SELECT * FROM grupos WHERE id = '$idGrupo'");
			$descripcion = $_POST['descripcion'];
			if (mysqli_num_rows($buscar) != 1) {
				$error = true;
				$mensaje = 'El Grupo seleccionado no existe en la Base de Datos: '.$idGrupo;
			} else {
				if (@$_POST['tipoOferta'] == 1) {
					$ofertaCantidad = '1';
					$cantidad = $_POST['cantidad'];
					$precio = $_POST['precio'];
					$ofertaDirecta = '0';
					$descuento = '0';
				} elseif(@$_POST['tipoOferta'] == 2) { 
					$ofertaCantidad = '0';
					$cantidad = '0';
					$precio = '0';
					$ofertaDirecta = '1';
					$descuento = $_POST['descuento'];
				} else {
					$error = true;
					$mensaje = 'Debe elegirse algun tipo de Oferta';
				}

				if (isset($_POST['dias'])) {
					$dias = '1';
					$dDesde = $_POST['dDesde'];
					$dHasta = $_POST['dHasta'];
				} else {
					$dias = '0';
					$dDesde = '0';
					$dHasta = '0';
				}

				if (isset($_POST['horas'])) {
					$horas = '1';
					$hDesde = $_POST['hDesde'];
					$hHasta = $_POST['hHasta'];
				} else {
					$horas = '0';
					$hDesde = '0';
					$hHasta = '0';
				}
				
				if (@$error) {
					$contenido .= '<div class="col-lg-12"><div class="alert alert-danger rounded-0">'.$mensaje.'</div></div>';
				} else {
					$consulta = "UPDATE grupos 
								 SET 	descripcion = '$descripcion', 
								 		ofertaCantidad = '$ofertaCantidad', 
										cantidad = '$cantidad', 
										precio = '$precio', 
										ofertaDirecta = '$ofertaDirecta', 
										descuento = '$descuento', 
										dias = '$dias', 
										horas = '$horas', 
										dDesde = '$dDesde', 
										dHasta = '$dHasta', 
										hDesde = '$hDesde', 
										hHasta = '$hHasta' 
								WHERE 	id = '$idGrupo'";
					if (mysqli_query($conexion, $consulta)) {
						$mensaje = 'La oferta de Grupo ha sido cargada exitosamente. ID: '.$idGrupo;
						$contenido.='<div class="col-lg-12"><div class="alert alert-success rounded-0">'.$mensaje.'</div></div>';
					} else {
						$mensaje='Ocurrio un error al cargar la oferta de Grupo. ID: '.$idGrupo;
						$contenido.='<div class="col-lg-12"><div class="alert alert-success rounded-0">'.$mensaje.'</div></div>';
					}
				}
			}
		}
		$contenido .= '
			<div class="row">
				<div class="col-lg-12"><h3 style="margin-left:25px;">Nueva Oferta</h3></div>
				<form method="get" name="grupo" class="col-lg-4">					
					<input type="hidden" name="menu" value="articulos">
					<input type="hidden" name="opc" value="grupos">
					<div class="col-lg-12">
						<select name="grupo" class="form-select rounded-0 mb-3" onchange="this.form.submit()">
							<option>Seleccionar Grupo...</option>';
							$bGrupos = mysqli_query($conexion, "SELECT * FROM grupos ORDER BY nombre");
							while ($mGrupos = mysqli_fetch_assoc($bGrupos)) {
								if (@$_GET['grupo'] == $mGrupos['id']) {
									$contenido.='<option value="'.$mGrupos['id'].'" selected>'.$mGrupos['nombre'].'</option>';
								} else {
									$contenido.='<option value="'.$mGrupos['id'].'">'.$mGrupos['nombre'].'</option>';
								}
							}	
							if(isset($_GET['grupo'])) {
								$idG=$_GET['grupo'];
								$bDatos=mysqli_query($conexion,"SELECT * FROM grupos WHERE id='$idG'");
								if(mysqli_num_rows($bDatos)==1) {
									$ofertaGrupo=mysqli_fetch_assoc($bDatos);
								}
							}
						$contenido.='
						</select>
					</div>
				</form>
				<form method="post" name="nuevaOferta" action="" class="col-lg-8">
					<input type="hidden" name="idGrupo" value="'.@$_GET['grupo'].'">
					<div class="col-lg-12"><input type="text" class="form-control rounded-0 mb-3" name="descripcion" value="'.@$ofertaGrupo['descripcion'].'" placeholder="Descripcion de la Oferta"></div>';
					
					if (@$ofertaGrupo['ofertaCantidad'] == 1) {
						$contenido.='
					<div class="col-lg-1"><input type="radio" class="form-control rounded-0 mb-3" name="tipoOferta" value="1" onclick="ofertaGrupo(1)" checked></div>
					<div class="col-lg-4"><label style="line-height:38px;">Oferta Cantidad:</label></div>
					<div class="col-lg-3">
						<div class="input-group">
							<input type="text" class="form-control rounded-0 mb-3" id="cantidad" name="cantidad" value="'.@$ofertaGrupo['cantidad'].'" placeholder="cantidad">
							<span class="input-group-addon"><span style="font-weight:bold;">U</span></span>
						</div>
					</div>
					<div class="col-lg-1"><label style="line-height:38px;">x</label></div>
					<div class="col-lg-3">
						<div class="input-group">
							<input type="text" class="form-control rounded-0 mb-3" id="precio" name="precio" value="'.@$ofertaGrupo['precio'].'" placeholder="precio">
							<span class="input-group-addon"><span style="font-weight:bold;">$</span></span>
						</div>
					</div>
					
					<div class="col-lg-12"></div>';
						
					}
					else {
						$contenido.='
					<div class="col-lg-1"><input type="radio" class="form-control rounded-0 mb-3" name="tipoOferta" value="1" onclick="ofertaGrupo(1)"></div>
					<div class="col-lg-4"><label style="line-height:38px;">Oferta Cantidad:</label></div>
					<div class="col-lg-3">
						<div class="input-group">
							<input type="text" class="form-control rounded-0 mb-3" id="cantidad" name="cantidad" value="'.@$ofertaGrupo['cantidad'].'" placeholder="cantidad" disabled>
							<span class="input-group-addon"><span style="font-weight:bold;">U</span></span>
						</div>
					</div>
					<div class="col-lg-1"><label style="line-height:38px;">x</label></div>
					<div class="col-lg-3">
						<div class="input-group">
							<input type="text" class="form-control rounded-0 mb-3" id="precio" name="precio" value="'.@$ofertaGrupo['precio'].'" placeholder="precio" disabled>
							<span class="input-group-addon"><span style="font-weight:bold;">$</span></span>
						</div>
					</div>
					
					<div class="col-lg-12"></div>';
					}
					if(@$ofertaGrupo['ofertaDirecta']==1) {
						$contenido.='
					<div class="col-lg-1"><input type="radio" class="form-control rounded-0 mb-3" name="tipoOferta" value="2" onclick="ofertaGrupo(2)" checked></div>
					<div class="col-lg-4"><label style="line-height:38px;">Oferta Directa:</label></div>
					<div class="col-lg-3">
						<div class="input-group">
							<input type="text" class="form-control rounded-0 mb-3" id="descuento" name="descuento" value="'.@$ofertaGrupo['descuento'].'" placeholder="descuento">
							<span class="input-group-addon"><span style="font-weight:bold;">%</span></span>
						</div>
					</div>

					<div class="col-lg-12"></div>';
					}
					else {
						$contenido.='
					<div class="col-lg-1"><input type="radio" class="form-control rounded-0 mb-3" name="tipoOferta" value="2" onclick="ofertaGrupo(2)"></div>
					<div class="col-lg-4"><label style="line-height:38px;">Oferta Directa:</label></div>
					<div class="col-lg-3">
						<div class="input-group">
							<input type="text" class="form-control rounded-0 mb-3" id="descuento" name="descuento" value="'.@$ofertaGrupo['descuento'].'" placeholder="descuento" disabled>
							<span class="input-group-addon"><span style="font-weight:bold;">%</span></span>
						</div>
					</div>

					<div class="col-lg-12"></div>';
					}
					if(@$ofertaGrupo['dias']==1) {
						$contenido.='
					<div class="col-lg-1"><input type="checkbox" class="form-control rounded-0 mb-3" id="dias" name="dias" onclick="ofertaGrupo(3)" checked></div>
					<div class="col-lg-4"><label style="line-height:38px;">Restriccion Dias:</label></div>
					<div class="col-lg-3">
						<div class="input-group">
							<input type="date" class="form-control rounded-0 mb-3" id="dDesde" name="dDesde" value="'.@$ofertaGrupo['dDesde'].'">
							<span class="input-group-addon"><span style="font-weight:bold;"><i class="fa fa-calendar"></i></span></span>
						</div>
					</div>
					<div class="col-lg-3">
						<div class="input-group">
							<input type="date" class="form-control rounded-0 mb-3" id="dHasta" name="dHasta" value="'.@$ofertaGrupo['dHasta'].'">
							<span class="input-group-addon"><span style="font-weight:bold;"><i class="fa fa-calendar"></i></span></span>
						</div>
					</div>
					
					<div class="col-lg-12"></div>';
					}
					else {
						$contenido.='
					<div class="col-lg-1"><input type="checkbox" class="form-control rounded-0 mb-3" id="dias" name="dias" onclick="ofertaGrupo(3)"></div>
					<div class="col-lg-4"><label style="line-height:38px;">Restriccion Dias:</label></div>
					<div class="col-lg-3">
						<div class="input-group">
							<input type="date" class="form-control rounded-0 mb-3" id="dDesde" name="dDesde" value="'.@$ofertaGrupo['dDesde'].'" disabled>
							<span class="input-group-addon"><span style="font-weight:bold;"><i class="fa fa-calendar"></i></span></span>
						</div>
					</div>
					<div class="col-lg-3">
						<div class="input-group">
							<input type="date" class="form-control rounded-0 mb-3" id="dHasta" name="dHasta" value="'.@$ofertaGrupo['dHasta'].'" disabled>
							<span class="input-group-addon"><span style="font-weight:bold;"><i class="fa fa-calendar"></i></span></span>
						</div>
					</div>
					
					<div class="col-lg-12"></div>';
					}
					if(@$ofertaGrupo['horas']==1) {
						$contenido.='
					<div class="col-lg-1"><input type="checkbox" class="form-control rounded-0 mb-3" id="horas" name="horas" onclick="ofertaGrupo(4)" checked></div>
					<div class="col-lg-4"><label style="line-height:38px;">Restriccion Horas:</label></div>
					<div class="col-lg-3">
						<div class="input-group">
							<input type="time" class="form-control rounded-0 mb-3" id="hDesde" name="hDesde" value="'.@$ofertaGrupo['hDesde'].'" >
							<span class="input-group-addon"><span style="font-weight:bold;"><i class="fa fa-clock"></i></span></span>
						</div>
					</div>
					<div class="col-lg-3">
						<div class="input-group">
							<input type="time" class="form-control rounded-0 mb-3" id="hHasta" name="hHasta" value="'.@$ofertaGrupo['hHasta'].'" >
							<span class="input-group-addon"><span style="font-weight:bold;"><i class="fa fa-clock"></i></span></span>
						</div>
					</div>
					
					<div class="col-lg-12"></div>';
					}
					else {
						$contenido.='
					<div class="col-lg-1"><input type="checkbox" class="form-control rounded-0 mb-3" id="horas" name="horas" onclick="ofertaGrupo(4)"></div>
					<div class="col-lg-4"><label style="line-height:38px;">Restriccion Horas:</label></div>
					<div class="col-lg-3">
						<div class="input-group">
							<input type="time" class="form-control rounded-0 mb-3" id="hDesde" name="hDesde" value="'.@$ofertaGrupo['hDesde'].'" disabled>
							<span class="input-group-addon"><span style="font-weight:bold;"><i class="fa fa-clock"></i></span></span>
						</div>
					</div>
					<div class="col-lg-3">
						<div class="input-group">
							<input type="time" class="form-control rounded-0 mb-3" id="hHasta" name="hHasta" value="'.@$ofertaGrupo['hHasta'].'" disabled>
							<span class="input-group-addon"><span style="font-weight:bold;"><i class="fa fa-clock"></i></span></span>
						</div>
					</div>
					
					<div class="col-lg-12"></div>';
					}
					$contenido.='
					<div class="col-lg-4"></div>
					<div class="col-lg-4"><input type="submit" class="form-control btn-success" style="font-weight:bold;" value="Cargar Oferta"></div>
					<div class="col-lg-4"></div>
				</form>
			</div>
		</div>';		