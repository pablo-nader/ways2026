<?php
	$contenido = '';
	$editar = false;
	$mensaje = '';

	switch(@$_GET['opc']) {
		case 'nuevo': require_once 'modulos/articulos/nuevo.php'; 
			break;
		case 'editar': require_once 'modulos/articulos/editar.php';
			break;
		case 'ver-todos': require_once 'modulos/articulos/ver-todos.php';
			break;
		case 'proveedores': require_once 'modulos/articulos/proveedores.php';
			break;
		case 'stock': require_once 'modulos/articulos/stock.php';
			break;
		case 'grupos': require_once 'modulos/articulos/grupos.php';
			break;
		case 'marcas': require_once 'modulos/articulos/marcas.php';
			break;
		case 'restaurar': 
			$id = @$_GET['id'];
			if ($id != 0 && $id != '') {
				$restaurar = mysqli_query($conexion, "UPDATE articulos SET activo = 1 WHERE ID = '$id'");

				echo '	<script>
							history.back();
						</script>';
			}
			break;
		case 'eliminar': 
			$id = @$_GET['id'];
			if ($id != 0 && $id != '') {
				$eliminar = mysqli_query($conexion, "UPDATE articulos SET activo = 0 WHERE ID = '$id'");
		
				echo '	<script>
							history.back();
						</script>';
			}
			break;
		default: require_once 'modulos/articulos/index.php';
	}
?>
<div class="box">
	<header>
		<div class="icons iconsW">
			<a style="color:#333" title="Ver Articulos" class="btn-lg" href="index.php?menu=articulos">
				<i class="fa fa-home"></i>
				<span class="menuW">Inicio</span>
			</a>
		</div>
		<div class="icons iconsW" style="width:84px;">
			<a style="color:#333" title="Ver Articulos" class="btn-lg" href="index.php?menu=articulos&opc=ver-todos">
				<i class="fa fa-search"></i>
				<span class="menuW">Ver Artículos</span>
			</a>
		</div>
		<div class="icons iconsW">
			<a style="color:#333" title="Crear Nuevo" class="btn-lg" href="index.php?menu=articulos&opc=nuevo">
				<i class="fa fa-calendar-plus"></i>
				<span class="menuW">Crear Artículo</span>
			</a>
		</div>
		<div class="icons iconsW">
			<a style="color:#333" title="Crear Marcas" class="btn-lg" href="index.php?menu=articulos&opc=marcas">
				<i class="fa fa-registered"></i>
				<span class="menuW">Marcas</span>
			</a>
		</div>
		<div class="icons iconsW">
			<a style="color:#333" title="Crear Grupos" class="btn-lg" href="index.php?menu=articulos&opc=grupos">
				<i class="fa fa-boxes"></i>
				<span class="menuW">Grupos</span>
			</a>
		</div>
		<div class="icons iconsW">
			<a style="color:#333" title="Stock" class="btn-lg" href="index.php?menu=articulos&opc=stock">
				<i class="fa fa-list-ol"></i>
				<span class="menuW">Stock</span>
			</a>
		</div>
		<div class="icons iconsW">
			<a style="color:#333" title="Crear Proveedores" class="btn-lg" href="index.php?menu=articulos&opc=proveedores">
				<i class="far fa-user"></i>
				<span class="menuW">Proveedores</span>
			</a>
		</div>
		
	</header>
	<div class="body" style="min-height:400px;">
		<div class="row">		
			<?php echo $contenido; ?>
		</div>
	</div>
	<div class="modal fade" id="add-code" data-bs-backdrop="static" data-bs-keyboard="false" tabindex="-1" aria-labelledby="staticBackdropLabel" aria-hidden="true">
		<div class="modal-dialog">
			<div class="modal-content rounded-0">
				<div class="modal-header">
					<h5 class="modal-title" id="staticBackdropLabel">Código</h5>
					<button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button>
				</div>
				<div class="modal-body">
					<form method="post" id="barcode-form">
						<input type="number" step="1" class="form-control" name="barcode" id="barcode" required/>						
					</form>
				</div>
				<div class="modal-footer">
					<button type="button" class="btn btn-secondary rounded-0" data-bs-dismiss="modal">Cerrar</button>
					<button type="submit" class="btn btn-success rounded-0" form="barcode-form">Agregar</button>
				</div>
			</div>
		</div>
	</div>
</div>

<script>
	const codigo = document.querySelector("#barcode");

	document.querySelector("#barcode-form").addEventListener("submit", evt => {
		evt.preventDefault()
		const art_id = document.querySelector("#id").value;
		if (validateCodigo(codigo.value))
		{
			$.post("cargarCodigo.php", { barcode: codigo.value, id: art_id }, function(response) {
				let res = response?.split(":");
				if (res && res[0] == "ERROR") {
					alert(res[1])
				} else {
					let selector = document.querySelector("#barra");
					selector.innerHTML = selector.innerHTML + "<option value='0'>"+codigo.value+"</option>";
					$('#add-code').modal('hide');
					alert(res[1])
					setTimeout(() => {
						document.querySelector("#nombre").focus()					
					}, 250);
				}
			})
		}
	})

	document.getElementById('add-code').addEventListener('shown.bs.modal', () => {
		codigo.focus()
	})

	const validateCodigo = (codigo) => {
		if (codigo.length < 7 || codigo.length > 13) {
			alert("El código de barras no puede tener menos de 7 números ni más de 13");
			return false;
		}
		return true;
	}
</script>