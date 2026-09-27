using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using RedAJP.Globales; 

namespace RedAJP.Controllers
{
    [Authorize]
    public class VinculacionController : GlobalController
    {
        private readonly string _cadenaConexion;
        private Parametros.Modulo Modulo = Parametros.Modulos.Reuniones;

        public VinculacionController(IConfiguration configuration)
        {
            _cadenaConexion = configuration.GetConnectionString("MiConexion");
        }

        [HttpGet]
        public async Task<IActionResult> ObtenerCatalogos()
        {
            var iglesias = new List<object>();
            var municipios = new List<object>();

            using (var conexion = new NpgsqlConnection(_cadenaConexion))
            {
                await conexion.OpenAsync();

                // 1. Unimos Iglesias Existentes + Solicitudes Pendientes (Principales)
                string sqlIglesias = @"
            SELECT i.id::text as id, i.nombre, i.localidad as ciudad, m.nombre as municipio, 'EXT' as tipo
            FROM iciar_iglesias i 
            LEFT JOIN iciar_municipios m ON i.municipio_id = m.id 
            
            UNION ALL
            
            SELECT 'PEN_' || s.id::text as id, s.nombre, s.localidad as ciudad, m.nombre as municipio, 'PEN' as tipo
            FROM iciar_iglesias_solicitudes s
            LEFT JOIN iciar_municipios m ON s.municipio_id = m.id
            WHERE s.estado = 'PEN' AND s.iglesia_existente_id IS NULL AND s.solicitud_relacionada_id IS NULL
            
            ORDER BY nombre";

                using (var cmd = new NpgsqlCommand(sqlIglesias, conexion))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        string nombreMuni = reader["municipio"]?.ToString() ?? "Sin municipio";
                        string ciudad = reader["ciudad"]?.ToString();
                        string tipo = reader["tipo"].ToString();
                        string sufijo = tipo == "PEN" ? " ⏳ (En Aprobación)" : "";

                        // Armamos el texto. Si hay ciudad, mostramos "Ciudad, Municipio". 
                        // Si la ciudad está vacía, solo mostramos el "Municipio".
                        string ubicacion = string.IsNullOrWhiteSpace(ciudad) ? nombreMuni : $"{ciudad}, {nombreMuni}";

                        iglesias.Add(new
                        {
                            id = reader["id"].ToString(),
                            text = $"{reader["nombre"]} ({ubicacion}){sufijo}"
                        });
                    }
                }

                using (var cmd = new NpgsqlCommand("SELECT id, nombre FROM iciar_municipios ORDER BY nombre", conexion))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        municipios.Add(new { id = reader["id"], text = reader["nombre"].ToString() });
                    }
                }
            }

            return Json(new { iglesias, municipios });
        }

        [HttpPost]
        public async Task<IActionResult> GuardarSolicitud([FromForm] string iglesia_seleccionada_id, [FromForm] string nombre, [FromForm] int? municipio_id, [FromForm] string localidad, [FromForm] string colonia, [FromForm] string calle, [FromForm] string numero, [FromForm] string referencia, [FromForm] string horarios, [FromForm] string mapa_url)
        {
            string urlRetorno = Request.Headers["Referer"].ToString();
            if (string.IsNullOrEmpty(urlRetorno)) urlRetorno = Url.Action("Index", "Home");

            int? idExistente = null;
            int? idRelacionada = null;

            if (!string.IsNullOrWhiteSpace(iglesia_seleccionada_id))
            {
                if (iglesia_seleccionada_id.StartsWith("PEN_"))
                    idRelacionada = int.Parse(iglesia_seleccionada_id.Replace("PEN_", ""));
                else
                    idExistente = int.Parse(iglesia_seleccionada_id);
            }

            // --- 1. VALIDACIONES ---
            if (idExistente == null && idRelacionada == null)
            {
                if (string.IsNullOrWhiteSpace(nombre) || !municipio_id.HasValue || municipio_id.Value <= 0 || string.IsNullOrWhiteSpace(mapa_url) || string.IsNullOrWhiteSpace(horarios))
                {
                    TempData["ErrorAsignacion"] = "Faltan datos obligatorios.";
                    return Redirect(urlRetorno);
                }

                if (!Uri.TryCreate(mapa_url, UriKind.Absolute, out Uri uriResult) || (uriResult.Scheme != Uri.UriSchemeHttp && uriResult.Scheme != Uri.UriSchemeHttps))
                {
                    TempData["ErrorAsignacion"] = "El formato del enlace de Google Maps no es válido.";
                    return Redirect(urlRetorno);
                }
            }

            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            string ipUsuario = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    // VALIDAMOS QUE EL MUNICIPIO EXISTA (El bloque que acabamos de hacer)
                    if (idExistente == null && idRelacionada == null && municipio_id.HasValue)
                    {
                        using (var cmdMun = new NpgsqlCommand("SELECT COUNT(1) FROM iciar_municipios WHERE id = @id", conexion))
                        {
                            cmdMun.Parameters.AddWithValue("@id", municipio_id.Value);
                            if (Convert.ToInt64(await cmdMun.ExecuteScalarAsync()) == 0)
                            {
                                TempData["ErrorAsignacion"] = "El municipio seleccionado no existe en nuestro catálogo.";
                                return Redirect(urlRetorno);
                            }
                        }
                    }
                    // VALIDACIÓN DE NOMBRE DUPLICADO EN EL MISMO MUNICIPIO PARA NUEVAS
                    if (idExistente == null && idRelacionada == null)
                    {
                        using (var cmdVal = new NpgsqlCommand(@"SELECT 
        (SELECT COUNT(1) FROM iciar_iglesias WHERE nombre ILIKE @n AND municipio_id = @m) +
        (SELECT COUNT(1) FROM iciar_iglesias_solicitudes WHERE nombre ILIKE @n AND municipio_id = @m AND estado = 'PEN')", conexion))
                        {
                            cmdVal.Parameters.AddWithValue("@n", nombre.Trim());
                            cmdVal.Parameters.AddWithValue("@m", municipio_id.Value); // Agregamos el ID del municipio

                            long count = Convert.ToInt64(await cmdVal.ExecuteScalarAsync());
                            if (count > 0)
                            {
                                TempData["ErrorAsignacion"] = "Ya existe una iglesia o solicitud en curso con ese nombre en este municipio.";
                                return Redirect(urlRetorno);
                            }
                        }
                    }

                    using (var transaccion = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            using (var cmdDel = new NpgsqlCommand("DELETE FROM iciar_iglesias_solicitudes WHERE usuario_id = @u", conexion, transaccion))
                            {
                                cmdDel.Parameters.AddWithValue("@u", idUsuario);
                                await cmdDel.ExecuteNonQueryAsync();
                            }

                            string sql = @"INSERT INTO iciar_iglesias_solicitudes 
                                (usuario_id, iglesia_existente_id, solicitud_relacionada_id, nombre, municipio_id, localidad, colonia, calle, numero, referencia, horarios, mapa_url, estado) 
                                VALUES (@uid, @iex, @srel, @nom, @mun, @loc, @col, @cal, @num, @ref, @hor, @map, 'PEN')";

                            using (var cmd = new NpgsqlCommand(sql, conexion, transaccion))
                            {
                                cmd.Parameters.AddWithValue("@uid", idUsuario);
                                cmd.Parameters.AddWithValue("@iex", (object)idExistente ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@srel", (object)idRelacionada ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@nom", (object)nombre ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@mun", (object)municipio_id ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@loc", string.IsNullOrWhiteSpace(localidad) ? DBNull.Value : (object)localidad);
                                cmd.Parameters.AddWithValue("@col", string.IsNullOrWhiteSpace(colonia) ? DBNull.Value : (object)colonia);
                                cmd.Parameters.AddWithValue("@cal", string.IsNullOrWhiteSpace(calle) ? DBNull.Value : (object)calle);
                                cmd.Parameters.AddWithValue("@num", string.IsNullOrWhiteSpace(numero) ? DBNull.Value : (object)numero);
                                cmd.Parameters.AddWithValue("@ref", string.IsNullOrWhiteSpace(referencia) ? DBNull.Value : (object)referencia);
                                cmd.Parameters.AddWithValue("@hor", string.IsNullOrWhiteSpace(horarios) ? DBNull.Value : (object)horarios);
                                cmd.Parameters.AddWithValue("@map", (object)mapa_url ?? DBNull.Value);

                                await cmd.ExecuteNonQueryAsync();
                            }

                            string detalleAccion = idExistente.HasValue ? $"Solicitó vinculación a iglesia ID: {idExistente}" :
                                                   idRelacionada.HasValue ? $"Se unió a solicitud pendiente ID: {idRelacionada}" :
                                                   $"Solicitó registro de nueva iglesia: {nombre}";

                            await Funciones.RegistrarBitacora(conexion, idUsuario, Modulo, Parametros.AccionesBitacora.Crear, detalleAccion, ipUsuario, transaccion);
                            await transaccion.CommitAsync();
                        }
                        catch (Exception ex)
                        {
                            TempData["ErrorAsignacion"] = "Ocurrió un error al guardar la solicitud: " + ex.Message;
                            await transaccion.RollbackAsync();
                            throw;
                        }
                    }
                }

                // ACTUALIZAR SESIÓN
                var identity = (ClaimsIdentity)User.Identity;
                var claimEstadoAntiguo = identity.FindFirst("EstadoSolIglesia");
                if (claimEstadoAntiguo != null) identity.RemoveClaim(claimEstadoAntiguo);
                identity.AddClaim(new Claim("EstadoSolIglesia", "PEN"));

                var claimMotivoAntiguo = identity.FindFirst("MotivoRchIglesia");
                if (claimMotivoAntiguo != null) identity.RemoveClaim(claimMotivoAntiguo);

                await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

                MostrarMensaje("¡Gracias por tu apoyo!", "Tu solicitud ha sido enviada correctamente y se encuentra en revisión.", TipoMensaje.Exito);
                return Redirect(urlRetorno);
            }
            catch (Exception ex)
            {
                TempData["ErrorAsignacion"] = "Ocurrió un error al procesar tu solicitud: " + ex.Message;
                return Redirect(urlRetorno);
            }
        }

        [HttpGet]
        public async Task<IActionResult> Autorizaciones()
        {
            // Validación estricta de permisos
            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "No tienes permiso de edicion en esta ventana", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Home");
            }

            var listaSolicitudes = new List<dynamic>();
            var iglesias = new List<dynamic>();
            var municipios = new List<dynamic>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. Cargar Solicitudes Pendientes
                    string sql = @"
                    SELECT s.*, u.""NombreCompleto"", u.""Email"", 
                           i.nombre as nombre_existente, m.nombre as municipio_existente,
                           sr.nombre as nombre_relacionada, mr.nombre as municipio_relacionada
                    FROM iciar_iglesias_solicitudes s
                    INNER JOIN ""Sist_Usuarios"" u ON s.usuario_id = u.""Id_Usuario""
                    LEFT JOIN iciar_iglesias i ON s.iglesia_existente_id = i.id
                    LEFT JOIN iciar_municipios m ON i.municipio_id = m.id
                    LEFT JOIN iciar_iglesias_solicitudes sr ON s.solicitud_relacionada_id = sr.id
                    LEFT JOIN iciar_municipios mr ON sr.municipio_id = mr.id
                    WHERE s.estado = 'PEN'
                    ORDER BY s.fecha_solicitud ASC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            listaSolicitudes.Add(new
                            {
                                Id = reader["id"],
                                IdUsuario = reader["usuario_id"],
                                NombreUsuario = reader["NombreCompleto"].ToString(),
                                Email = reader["Email"].ToString(),
                                Fecha = Convert.ToDateTime(reader["fecha_solicitud"]),

                                IdIglesiaExistente = reader["iglesia_existente_id"] as int?,
                                NombreExistente = reader["nombre_existente"]?.ToString(),
                                MunicipioExistente = reader["municipio_existente"]?.ToString(),

                                // Agregamos los campos de la solicitud relacionada
                                IdRelacionada = reader["solicitud_relacionada_id"] as int?,
                                NombreRelacionada = reader["nombre_relacionada"]?.ToString(),
                                MunicipioRelacionada = reader["municipio_relacionada"]?.ToString(),

                                NombreNueva = reader["nombre"]?.ToString(),
                                MunicipioId = reader["municipio_id"] as int?,
                                MapaUrl = reader["mapa_url"]?.ToString(),
                                Localidad = reader["localidad"]?.ToString(),
                                Colonia = reader["colonia"]?.ToString(),
                                Calle = reader["calle"]?.ToString(),
                                Numero = reader["numero"]?.ToString(),
                                Referencia = reader["referencia"]?.ToString(),
                                Horarios = reader["horarios"]?.ToString()
                            });
                        }
                    }

                    // 2. Cargar Catálogo de Municipios para el Formulario de Edición
                    using (var cmdM = new NpgsqlCommand("SELECT id, nombre FROM iciar_municipios ORDER BY nombre", conexion))
                    using (var readerM = await cmdM.ExecuteReaderAsync())
                    {
                        while (await readerM.ReadAsync())
                        {
                            municipios.Add(new { Id = readerM["id"], Nombre = readerM["nombre"].ToString() });
                        }
                    }

                    // 3. Cargar Catálogo de Iglesias para el Dropdown de "Corregir Existente"
                    string sqlIglesias = @"
                SELECT i.id, i.nombre, m.nombre as municipio 
                FROM iciar_iglesias i 
                LEFT JOIN iciar_municipios m ON i.municipio_id = m.id 
                ORDER BY m.nombre, i.nombre";
                    using (var cmdI = new NpgsqlCommand(sqlIglesias, conexion))
                    using (var readerI = await cmdI.ExecuteReaderAsync())
                    {
                        while (await readerI.ReadAsync())
                        {
                            string nombreMuni = readerI["municipio"]?.ToString() ?? "Sin municipio";
                            iglesias.Add(new { Id = readerI["id"], Nombre = $"{readerI["nombre"]} ({nombreMuni})" });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "No se pudieron cargar los datos: " + ex.Message, TipoMensaje.Error);
            }

            ViewBag.Municipios = municipios;
            ViewBag.Iglesias = iglesias;
            return View(listaSolicitudes);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcesarSolicitud(
            int idSolicitud, string accion, string motivoRechazo,
            int? iglesia_existente_id, string Nombre, int? MunicipioId,
            string Localidad, string Colonia, string Calle, string Numero,
            string Referencia, string Horarios, string MapaUrl,
            decimal? Latitud, decimal? Longitud)
        {
            // --- 1. VALIDACIÓN DE PERMISOS ---
            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Sin Permisos", "No tienes autorización para esta acción.", TipoMensaje.Error);
                return RedirectToAction("Autorizaciones");
            }

            // --- 2. VALIDACIÓN DE DATOS (NO CONFÍES EN EL NAVEGADOR) ---
            if (accion == "AprobarNueva")
            {
                if (string.IsNullOrWhiteSpace(Nombre)) return RetornarError("El nombre es obligatorio.");
                if (string.IsNullOrWhiteSpace(Localidad)) return RetornarError("La localidad es obligatoria.");
                if (string.IsNullOrWhiteSpace(Calle)) return RetornarError("La calle es obligatoria.");
                if (string.IsNullOrWhiteSpace(MapaUrl)) return RetornarError("El link de Maps es obligatorio.");
                if (!Latitud.HasValue || !Longitud.HasValue)
                    return RetornarError("Debes asignar las coordenadas geográficas (Latitud y Longitud).");

                // Validación de Municipio
                if (!MunicipioId.HasValue || MunicipioId <= 0) return RetornarError("Selecciona un municipio válido.");
                if (string.IsNullOrWhiteSpace(Horarios)) return RetornarError("Debes registrar al menos un horario de reunión válido.");
                // Validación de formato de URL
                if (!Uri.TryCreate(MapaUrl, UriKind.Absolute, out Uri uriResult) ||
                    (uriResult.Scheme != Uri.UriSchemeHttp && uriResult.Scheme != Uri.UriSchemeHttps))
                {
                    return RetornarError("El formato del link de Google Maps es inválido.");
                }

                // --- VALIDACIÓN DE EXISTENCIA REAL EN BD ---
                // Evitamos la inyección de IDs de municipios inexistentes
                using (var conValidar = new NpgsqlConnection(_cadenaConexion))
                {
                    await conValidar.OpenAsync();
                    using (var cmdM = new NpgsqlCommand("SELECT COUNT(1) FROM iciar_municipios WHERE id = @mid", conValidar))
                    {
                        cmdM.Parameters.AddWithValue("@mid", MunicipioId.Value);
                        if (Convert.ToInt64(await cmdM.ExecuteScalarAsync()) == 0)
                        {
                            return RetornarError("El municipio seleccionado no existe en el catálogo oficial.");
                        }
                    }
                }
            }

            // Función local para simplificar el retorno de errores en Postback
            IActionResult RetornarError(string msj)
            {
                MostrarMensaje("Validación Fallida", msj, TipoMensaje.Alerta);
                return RedirectToAction("Autorizaciones");
            }

            int idAdmin = int.Parse(User.FindFirst("IdUsuario").Value);
            string ipAdmin = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            // --- 3. Autorización
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var transaccion = await conexion.BeginTransactionAsync())
                    {
                        // Obtener el ID del usuario que hizo la solicitud
                        int idUsuarioSolicitante = 0;
                        using (var cmdGet = new NpgsqlCommand("SELECT usuario_id FROM iciar_iglesias_solicitudes WHERE id = @id", conexion, transaccion))
                        {
                            cmdGet.Parameters.AddWithValue("@id", idSolicitud);
                            var result = await cmdGet.ExecuteScalarAsync();
                            if (result == null) throw new Exception("La solicitud no existe.");
                            idUsuarioSolicitante = Convert.ToInt32(result);
                        }

                        if (idUsuarioSolicitante == 0) throw new Exception("No se pudo identificar al usuario solicitante.");
                                
                        if (accion == "Rechazar")
                        {
                            string sqlRechazo = "UPDATE iciar_iglesias_solicitudes SET estado = 'RCH', motivo_rechazo = @motivo, admin_autoriza_id = @admin WHERE (id = @id OR solicitud_relacionada_id = @id) AND estado = 'PEN'";
                            using (var cmd = new NpgsqlCommand(sqlRechazo, conexion, transaccion))
                            {
                                cmd.Parameters.AddWithValue("@motivo", string.IsNullOrWhiteSpace(motivoRechazo) ? "No cumple con los requisitos." : motivoRechazo);
                                cmd.Parameters.AddWithValue("@admin", idAdmin);
                                cmd.Parameters.AddWithValue("@id", idSolicitud);
                                await cmd.ExecuteNonQueryAsync();
                            }
                        }
                        else if (accion == "AprobarExistente")
                        {
                            if (!iglesia_existente_id.HasValue) throw new Exception("Debes seleccionar una iglesia existente.");

                            // 1. Actualizar usuario
                            string sqlUpdateUser = @"UPDATE ""Sist_Usuarios"" SET ""Id_Iglesia_Asignada"" = @idIglesia WHERE ""Id_Usuario"" = @idUsu";
                            using (var cmdUpdUser = new NpgsqlCommand(sqlUpdateUser, conexion, transaccion))
                            {
                                cmdUpdUser.Parameters.AddWithValue("@idIglesia", iglesia_existente_id.Value);
                                cmdUpdUser.Parameters.AddWithValue("@idUsu", idUsuarioSolicitante);
                                await cmdUpdUser.ExecuteNonQueryAsync();
                            }

                            // 2. Marcar solicitud
                            string sqlUpdateSol = "UPDATE iciar_iglesias_solicitudes SET estado = 'APR', motivo_rechazo = NULL, iglesia_existente_id = @iex, admin_autoriza_id = @admin WHERE (id = @id OR solicitud_relacionada_id = @id) AND estado = 'PEN'";
                            using (var cmdUpdSol = new NpgsqlCommand(sqlUpdateSol, conexion, transaccion))
                            {
                                cmdUpdSol.Parameters.AddWithValue("@iex", iglesia_existente_id.Value);
                                cmdUpdSol.Parameters.AddWithValue("@admin", idAdmin);
                                cmdUpdSol.Parameters.AddWithValue("@id", idSolicitud);
                                await cmdUpdSol.ExecuteNonQueryAsync();
                            }
                        }
                        else if (accion == "AprobarNueva")
                        {
                            if (string.IsNullOrWhiteSpace(Nombre) || !MunicipioId.HasValue)
                                throw new Exception("Faltan datos obligatorios para registrar la nueva iglesia.");

                            // 1. Insertar nueva iglesia en el catálogo oficial
                            string sqlInsertIglesia = @"
                        INSERT INTO iciar_iglesias 
                        (nombre, municipio_id, localidad, colonia, calle, numero, referencia, horarios, mapa_url, latitud, longitud) 
                        VALUES (@nom, @mun, @loc, @col, @cal, @num, @ref, @hor, @map, @lat, @lon)
                        RETURNING id;";

                            int idIglesiaFinal;
                            using (var cmdIns = new NpgsqlCommand(sqlInsertIglesia, conexion, transaccion))
                            {
                                cmdIns.Parameters.AddWithValue("@nom", Nombre);
                                cmdIns.Parameters.AddWithValue("@mun", MunicipioId.Value);
                                cmdIns.Parameters.AddWithValue("@loc", (object)Localidad ?? DBNull.Value);
                                cmdIns.Parameters.AddWithValue("@col", (object)Colonia ?? DBNull.Value);
                                cmdIns.Parameters.AddWithValue("@cal", (object)Calle ?? DBNull.Value);
                                cmdIns.Parameters.AddWithValue("@num", (object)Numero ?? DBNull.Value);
                                cmdIns.Parameters.AddWithValue("@ref", (object)Referencia ?? DBNull.Value);
                                cmdIns.Parameters.AddWithValue("@hor", (object)Horarios ?? DBNull.Value);
                                cmdIns.Parameters.AddWithValue("@map", (object)MapaUrl ?? DBNull.Value);
                                cmdIns.Parameters.AddWithValue("@lat", (object)Latitud ?? DBNull.Value);
                                cmdIns.Parameters.AddWithValue("@lon", (object)Longitud ?? DBNull.Value);
                                idIglesiaFinal = Convert.ToInt32(await cmdIns.ExecuteScalarAsync());
                            }

                            // 2. Actualizar usuario
                            string sqlUpdateUser = @"UPDATE ""Sist_Usuarios"" SET ""Id_Iglesia_Asignada"" = @idIglesia WHERE ""Id_Usuario"" = @idUsu OR ""Id_Usuario"" IN (SELECT usuario_id FROM iciar_iglesias_solicitudes WHERE solicitud_relacionada_id = @idSol)";
                            using (var cmdUpdUser = new NpgsqlCommand(sqlUpdateUser, conexion, transaccion))
                            {
                                cmdUpdUser.Parameters.AddWithValue("@idIglesia", idIglesiaFinal);
                                cmdUpdUser.Parameters.AddWithValue("@idUsu", idUsuarioSolicitante);
                                cmdUpdUser.Parameters.AddWithValue("@idSol", idSolicitud);
                                await cmdUpdUser.ExecuteNonQueryAsync();
                            }

                            // 3. Marcar solicitud y guardar lo que el admin corrigió
                            string sqlUpdateSol = @"UPDATE iciar_iglesias_solicitudes 
SET estado = 'APR', motivo_rechazo = NULL, 
nombre = @nom, municipio_id = @mun, localidad = @loc,
colonia = @col, calle = @cal, numero = @num, referencia = @ref, 
horarios = @hor, mapa_url = @map, admin_autoriza_id = @admin, iglesia_existente_id = @idIglesiaFinal 
WHERE (id = @id OR solicitud_relacionada_id = @id) AND estado = 'PEN'";
                            using (var cmdUpdSol = new NpgsqlCommand(sqlUpdateSol, conexion, transaccion))
                            {
                                cmdUpdSol.Parameters.AddWithValue("@id", idSolicitud);
                                cmdUpdSol.Parameters.AddWithValue("@nom", Nombre);
                                cmdUpdSol.Parameters.AddWithValue("@mun", MunicipioId.Value);
                                cmdUpdSol.Parameters.AddWithValue("@loc", (object)Localidad ?? DBNull.Value);
                                cmdUpdSol.Parameters.AddWithValue("@col", (object)Colonia ?? DBNull.Value);
                                cmdUpdSol.Parameters.AddWithValue("@cal", (object)Calle ?? DBNull.Value);
                                cmdUpdSol.Parameters.AddWithValue("@num", (object)Numero ?? DBNull.Value);
                                cmdUpdSol.Parameters.AddWithValue("@ref", (object)Referencia ?? DBNull.Value);
                                cmdUpdSol.Parameters.AddWithValue("@hor", (object)Horarios ?? DBNull.Value);
                                cmdUpdSol.Parameters.AddWithValue("@map", (object)MapaUrl ?? DBNull.Value);
                                cmdUpdSol.Parameters.AddWithValue("@admin", idAdmin);
                                cmdUpdSol.Parameters.AddWithValue("@idIglesiaFinal", idIglesiaFinal);
                                await cmdUpdSol.ExecuteNonQueryAsync();
                            }
                        }

                        string detalleBitacora = $"Procesó solicitud vinculación {idSolicitud} (y dependientes). Acción: {accion}.";
                        await Funciones.RegistrarBitacora(conexion, idAdmin, Modulo, Parametros.AccionesBitacora.Editar, detalleBitacora, ipAdmin, transaccion);

                        await transaccion.CommitAsync();
                        MostrarMensaje("¡Éxito!", $"La solicitud ha sido procesada correctamente ({accion}).", TipoMensaje.Exito);
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "Ocurrió un error: " + ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Autorizaciones");
        }
    }
}