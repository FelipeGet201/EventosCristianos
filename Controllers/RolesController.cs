using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using RedAJP.Models;
using RedAJP.Globales;
using static RedAJP.Globales.Parametros; // ¡Truco! Para usar Modulos.Roles directo

namespace RedAJP.Controllers
{
    [Authorize]
    public class RolesController : GlobalController
    {
        private readonly string _cadenaConexion;
        private Parametros.Modulo Modulo = Parametros.Modulos.Roles;

        public RolesController(IConfiguration configuration)
        {
            _cadenaConexion = configuration.GetConnectionString("MiConexion");
        }

        public async Task<IActionResult> Index()
        {
            // Permiso: LEER
            if (!User.TienePermiso(Modulos.Roles, PermisoLeer))
            {
                MostrarMensaje("Error", "Necesitas permisos de lectura para realizar ésta acción.", TipoMensaje.Error);
                return RedirectToAction("Index", "Home");
            }

            var lista = new List<RolItem>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // Trae datos, cuenta usuarios y concatena nombres de módulos activos
                    string sql = @"
                    SELECT r.*, 
                           (SELECT COUNT(*) FROM ""Sist_Usuarios"" u WHERE u.""Id_Rol"" = r.""Id_Rol"") as ""TotalUsuarios"",
                           (
                               SELECT STRING_AGG(m.""Nombre_Visible"", ', ')
                               FROM ""Sist_Permisos"" p
                               JOIN ""Sist_Modulos"" m ON p.""Id_Modulo"" = m.""Id_Modulo""
                               WHERE p.""Id_Rol"" = r.""Id_Rol"" 
                               AND m.""VisibleRoles"" = TRUE  -- <--- NUEVO FILTRO AQUÍ
                               AND (p.""P_Leer"" OR p.""P_Crear"" OR p.""P_Editar"" OR p.""P_Borrar"" OR p.""P_Admin"")
                           ) as ""ModulosActivos""
                    FROM ""Sist_Roles"" r
                    ORDER BY r.""Id_Rol"" ASC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (reader.Read())
                        {
                            var modulosStr = reader["ModulosActivos"] as string;

                            lista.Add(new RolItem
                            {
                                Id_Rol = (int)reader["Id_Rol"],
                                Nombre = reader["Nombre"].ToString(),
                                Descripcion = reader["Descripcion"]?.ToString(),
                                Activo = (bool)reader["Activo"],
                                Es_Predeterminado = reader["Es_Predeterminado"] != DBNull.Value && (bool)reader["Es_Predeterminado"],
                                UsuariosCount = Convert.ToInt32(reader["TotalUsuarios"]),
                                Icono = reader["Icono"]?.ToString() ?? "fa-shield-halved",
                                ModulosAcceso = string.IsNullOrEmpty(modulosStr) ? new List<string>() : modulosStr.Split(',').Select(x => x.Trim()).ToList()
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return View(lista);
        }

        public async Task<IActionResult> Editor(int id)
        {
            bool bNuevo = (id == 0);
            if (!User.TienePermiso(Modulos.Roles, bNuevo ? PermisoCrear : PermisoEditar))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permiso para realizar esta acción.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            var modelo = new EditorRolViewModel { Id_Rol = id, Activo = true, IconoSeleccionado = "fa-shield-halved" };

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // A. Cargar Datos del Rol
                    if (id > 0)
                    {
                        using (var cmd = new NpgsqlCommand("SELECT * FROM \"Sist_Roles\" WHERE \"Id_Rol\"=@id", conexion))
                        {
                            cmd.Parameters.AddWithValue("@id", id);
                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                if (r.Read())
                                {
                                    // Asignación directa
                                    modelo.Nombre = r["Nombre"].ToString();
                                    modelo.Descripcion = r["Descripcion"].ToString();
                                    modelo.Activo = (bool)r["Activo"];
                                    modelo.IconoSeleccionado = r["Icono"]?.ToString() ?? "fa-shield-halved";

                                    // Leemos si es default para bloquearlo en la vista
                                    if (r["Es_Predeterminado"] != DBNull.Value)
                                    {
                                        modelo.Es_Predeterminado = (bool)r["Es_Predeterminado"];
                                    }
                                }
                            }
                        }
                    }

                    // B. Cargar Matriz de Permisos
                    string sqlPermisos = @"
                    SELECT m.""Id_Modulo"", m.""Nombre_Visible"", 
                           COALESCE(p.""P_Leer"", FALSE) as ""L"",
                           COALESCE(p.""P_Crear"", FALSE) as ""C"",
                           COALESCE(p.""P_Editar"", FALSE) as ""E"",
                           COALESCE(p.""P_Borrar"", FALSE) as ""B"",
                           COALESCE(p.""P_Admin"", FALSE) as ""A""
                    FROM ""Sist_Modulos"" m
                    LEFT JOIN ""Sist_Permisos"" p ON m.""Id_Modulo"" = p.""Id_Modulo"" AND p.""Id_Rol"" = @idRol
                    WHERE m.""VisibleRoles"" = TRUE -- <--- NUEVO FILTRO AQUÍ
                    ORDER BY m.""Orden_Visual""";

                    using (var cmd = new NpgsqlCommand(sqlPermisos, conexion))
                    {
                        cmd.Parameters.AddWithValue("@idRol", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (r.Read())
                            {
                                modelo.Permisos.Add(new PermisoItem
                                {
                                    Id_Modulo = (int)r["Id_Modulo"],
                                    Nombre_Modulo = r["Nombre_Visible"].ToString(),
                                    P_Leer = (bool)r["L"],
                                    P_Crear = (bool)r["C"],
                                    P_Editar = (bool)r["E"],
                                    P_Borrar = (bool)r["B"],
                                    P_Admin = (bool)r["A"]
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            return View(modelo);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Guardar(EditorRolViewModel modelo)
        {
            // Validar permiso según si es Nuevo (0) o Edición (>0)
            bool bNuevo = (modelo.Id_Rol == 0);
            if (!User.TienePermiso(Modulos.Roles, bNuevo ? PermisoCrear : PermisoEditar))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos suficientes.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            var idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            // 1. SOLUCIÓN: Quitamos el icono de la validación automática
            // porque si viene vacío, nosotros le pondremos uno default abajo.
            ModelState.Remove("IconoSeleccionado");

            // 2. AHORA SÍ revisamos si es válido
            if (!ModelState.IsValid)
            {
                var errores = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToList();
                MostrarMensaje("Datos Incompletos", "Corrige: " + string.Join(" | ", errores), TipoMensaje.Alerta);
                return View("Editor", modelo);
            }

            // Aquí tu lógica asigna el default si venía vacío
            if (string.IsNullOrEmpty(modelo.IconoSeleccionado)) modelo.IconoSeleccionado = "fa-shield-halved";

            if (string.IsNullOrEmpty(modelo.IconoSeleccionado)) modelo.IconoSeleccionado = "fa-shield-halved";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // TRANSACCIÓN
                    using (var transaccion = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 1. ROLES (INSERT O UPDATE)
                            if (modelo.Id_Rol == 0)
                            {
                                // INSERT
                                string sqlIns = @"INSERT INTO ""Sist_Roles"" (""Nombre"", ""Descripcion"", ""Activo"", ""Icono"") 
                                                  VALUES (@n, @d, @a, @ico) RETURNING ""Id_Rol""";
                                using (var cmd = new NpgsqlCommand(sqlIns, conexion, transaccion))
                                {
                                    cmd.Parameters.AddWithValue("@n", modelo.Nombre);
                                    cmd.Parameters.AddWithValue("@d", modelo.Descripcion);
                                    cmd.Parameters.AddWithValue("@a", modelo.Activo);
                                    cmd.Parameters.AddWithValue("@ico", modelo.IconoSeleccionado);
                                    // Recuperamos el ID generado
                                    modelo.Id_Rol = (int)await cmd.ExecuteScalarAsync();
                                }
                            }
                            else
                            {
                                // UPDATE (Aquí usamos el modelo.Id_Rol explícitamente)
                                string sqlUpd = @"UPDATE ""Sist_Roles"" 
                                                  SET ""Nombre""=@n, ""Descripcion""=@d, ""Activo""=@a, ""Icono""=@ico 
                                                  WHERE ""Id_Rol""=@id";
                                using (var cmd = new NpgsqlCommand(sqlUpd, conexion, transaccion))
                                {
                                    cmd.Parameters.AddWithValue("@n", modelo.Nombre);
                                    cmd.Parameters.AddWithValue("@d", modelo.Descripcion);
                                    cmd.Parameters.AddWithValue("@a", modelo.Activo);
                                    cmd.Parameters.AddWithValue("@ico", modelo.IconoSeleccionado);
                                    cmd.Parameters.AddWithValue("@id", modelo.Id_Rol); // <--- ID PARA EL UPDATE
                                    await cmd.ExecuteNonQueryAsync();
                                }
                            }

                            // 2. PERMISOS (Borrar anteriores e Insertar nuevos)
                            var cmdDel = new NpgsqlCommand("DELETE FROM \"Sist_Permisos\" WHERE \"Id_Rol\"=@id", conexion, transaccion);
                            cmdDel.Parameters.AddWithValue("@id", modelo.Id_Rol);
                            await cmdDel.ExecuteNonQueryAsync();

                            string sqlPerm = @"INSERT INTO ""Sist_Permisos"" 
                                (""Id_Rol"", ""Id_Modulo"", ""P_Leer"", ""P_Crear"", ""P_Editar"", ""P_Borrar"", ""P_Admin"")
                                VALUES (@rol, @mod, @l, @c, @e, @b, @a)";

                            foreach (var p in modelo.Permisos)
                            {
                                if (p.P_Leer || p.P_Crear || p.P_Editar || p.P_Borrar || p.P_Admin)
                                {
                                    using (var cmdP = new NpgsqlCommand(sqlPerm, conexion, transaccion))
                                    {
                                        cmdP.Parameters.AddWithValue("@rol", modelo.Id_Rol);
                                        cmdP.Parameters.AddWithValue("@mod", p.Id_Modulo);
                                        cmdP.Parameters.AddWithValue("@l", p.P_Leer);
                                        cmdP.Parameters.AddWithValue("@c", p.P_Crear);
                                        cmdP.Parameters.AddWithValue("@e", p.P_Editar);
                                        cmdP.Parameters.AddWithValue("@b", p.P_Borrar);
                                        cmdP.Parameters.AddWithValue("@a", p.P_Admin);
                                        await cmdP.ExecuteNonQueryAsync();
                                    }
                                }
                            }

                            // 3. [NUEVO] INVALIDAR SESIONES DE USUARIOS DE ESTE ROL
                            // Al cambiar el sello, el GlobalController los expulsará en su siguiente petición.
                            string sqlInvalidar = @"UPDATE ""Sist_Usuarios"" 
                                                    SET ""Sello_Seguridad"" = gen_random_uuid() 
                                                    WHERE ""Id_Rol"" = @rol";

                            using (var cmdInv = new NpgsqlCommand(sqlInvalidar, conexion, transaccion))
                            {
                                cmdInv.Parameters.AddWithValue("@rol", modelo.Id_Rol);
                                await cmdInv.ExecuteNonQueryAsync();
                            }

                            // 4. BITÁCORA
                            await Funciones.RegistrarBitacora(conexion, idUsuario, Modulo, bNuevo ? Parametros.AccionesBitacora.Crear : Parametros.AccionesBitacora.Editar,
                                $"Gestión de Rol: {modelo.Nombre} (ID {modelo.Id_Rol})", ip, transaccion);

                            // 5. CONFIRMAR
                            await transaccion.CommitAsync();

                            // MENSAJE DE ÉXITO
                            MostrarMensaje("Éxito", "El rol y sus permisos se han guardado. Los usuarios afectados deberán reingresar.", TipoMensaje.Exito);
                        }
                        catch (Exception ex)
                        {
                            await transaccion.RollbackAsync();
                            // MENSAJE DE ERROR DETALLADO EN EXCEPCIÓN SQL
                            MostrarMensaje("Error en BD", ex.Message, TipoMensaje.Error);
                            return View("Editor", modelo);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error de Conexión", ex.Message, TipoMensaje.Error);
                return View("Editor", modelo);
            }

            return RedirectToAction("Index");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Eliminar(int id)
        {
            // Permiso: BORRAR
            if (!User.TienePermiso(Modulos.Roles, PermisoBorrar))
            {
                MostrarMensaje("Acceso Denegado", "No tienes permiso para eliminar roles.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            var idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. VALIDAR SI TIENE USUARIOS 
                    var cmdCheck = new NpgsqlCommand("SELECT COUNT(*) FROM \"Sist_Usuarios\" WHERE \"Id_Rol\" = @id", conexion);
                    cmdCheck.Parameters.AddWithValue("@id", id);
                    if ((long)await cmdCheck.ExecuteScalarAsync() > 0)
                    {
                        MostrarMensaje("No se puede eliminar", "Hay usuarios usando este Rol.", TipoMensaje.Alerta);
                        return RedirectToAction("Index");
                    }

                    // 2. VALIDAR SI ES PREDETERMINADO 
                    var cmdDef = new NpgsqlCommand("SELECT \"Es_Predeterminado\" FROM \"Sist_Roles\" WHERE \"Id_Rol\" = @id", conexion);
                    cmdDef.Parameters.AddWithValue("@id", id);
                    var esDef = await cmdDef.ExecuteScalarAsync();
                    if (esDef != null && (bool)esDef)
                    {
                        MostrarMensaje("Bloqueado", "No se puede eliminar el rol predeterminado del sistema.", TipoMensaje.Alerta);
                        return RedirectToAction("Index");
                    }

                    using (var transaccion = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // A. Borrar Permisos asociados
                            var cmdP = new NpgsqlCommand("DELETE FROM \"Sist_Permisos\" WHERE \"Id_Rol\"=@id", conexion, transaccion);
                            cmdP.Parameters.AddWithValue("@id", id);
                            await cmdP.ExecuteNonQueryAsync();

                            // B. Borrar Rol
                            var cmdR = new NpgsqlCommand("DELETE FROM \"Sist_Roles\" WHERE \"Id_Rol\"=@id", conexion, transaccion);
                            cmdR.Parameters.AddWithValue("@id", id);
                            await cmdR.ExecuteNonQueryAsync();

                            // C. Bitácora
                            await Funciones.RegistrarBitacora(conexion, idUsuario, Modulo, Parametros.AccionesBitacora.Borrar,
                                $"Eliminó el rol ID {id}", ip, transaccion);

                            await transaccion.CommitAsync();
                            MostrarMensaje("Eliminado", "Rol eliminado correctamente.", TipoMensaje.Exito);
                        }
                        catch { await transaccion.RollbackAsync(); throw; }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            return RedirectToAction("Index");
        }
    }
}