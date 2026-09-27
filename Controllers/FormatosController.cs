using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using DocumentFormat.OpenXml.Presentation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Npgsql;
using NpgsqlTypes;
using RedAJP.Globales;
using RedAJP.Models;
using System.IO;
using System.Security.Claims;
using System.Text.RegularExpressions;
using static RedAJP.Globales.Parametros;

namespace RedAJP.Controllers
{
    [Authorize]
    public class FormatosController : GlobalController
    {
        private readonly string _cadenaConexion;
        private readonly Modulo _modulo = Modulos.Material;
        private readonly Cloudinary _cloudinary; // Nueva instancia de Cloudinary

        public FormatosController(IConfiguration configuration)
        {
            _cadenaConexion = configuration.GetConnectionString("MiConexion");

            // Configuración de Cloudinary
            Account account = new Account(
                configuration["Cloudinary:CloudName"],
                configuration["Cloudinary:ApiKey"],
                configuration["Cloudinary:ApiSecret"]
            );
            _cloudinary = new Cloudinary(account);
            _cloudinary.Api.Secure = true;
        }

        public async Task<IActionResult> Index()
        {
            if (!User.TienePermiso(_modulo, PermisoLeer))
            {
                MostrarMensaje("Error", "No tienes permisos de lectura en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Home");
            }

            var lista = new List<FormatoViewModel>();
            int idUsuarioActual = int.Parse(User.FindFirst("IdUsuario").Value);
            bool esEditorMódulo = User.TienePermiso(_modulo, PermisoEditar);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    string sql = @"
        SELECT a.""Id_Archivo"", a.""Titulo"", a.""Descripcion"", a.""Tipo"", a.""Fecha_Creacion"", a.""Fecha_Modificacion"", a.""Id_Usuario_Carga"",
               a.""Es_Enlace"", a.""Url_Enlace"", a.""Categoria"", a.""Id_Grupo_Acceso"", a.""Origen"", a.""Url_Portada"",
               u.""NombreCompleto"",
               g.""Nombre_Grupo""
        FROM ""Rec_Archivos"" a
        LEFT JOIN ""Sist_Usuarios"" u ON a.""Id_Usuario_Carga"" = u.""Id_Usuario""
        LEFT JOIN ""Sist_Grupos_Whatsapp"" g ON a.""Id_Grupo_Acceso"" = g.""Id_Grupo""
        WHERE a.""Origen"" IN ('Formatos', 'Recursos', 'Terminos_Eventos', 'Aviso_Privacidad') ";

                    if (!esEditorMódulo)
                    {
                        sql += @" AND (
                            a.""Categoria"" = 'Publico' 
                            OR 
                            (a.""Categoria"" = 'Grupos' AND a.""Id_Grupo_Acceso"" IN (
                                SELECT ""Id_Grupo"" FROM ""Sist_Grupos_Miembros"" WHERE ""Id_Usuario"" = @uidActual
                            ))
                          )";
                    }

                    sql += @" ORDER BY a.""Categoria"" DESC, a.""Titulo"" ASC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        if (!esEditorMódulo) cmd.Parameters.AddWithValue("@uidActual", idUsuarioActual);

                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                bool esEnlace = r["Es_Enlace"] != DBNull.Value && (bool)r["Es_Enlace"];
                                string tipoMime = r["Tipo"]?.ToString().ToLower() ?? "";

                                var estilo = esEnlace ? (Icono: "fa-link", Color: "text-success") : ObtenerEstiloPorMime(tipoMime);

                                DateTime fechaMostrada = r["Fecha_Modificacion"] != DBNull.Value
                                    ? (DateTime)r["Fecha_Modificacion"]
                                    : (DateTime)r["Fecha_Creacion"];

                                lista.Add(new FormatoViewModel
                                {
                                    Id = (int)r["Id_Archivo"],
                                    Titulo = r["Titulo"].ToString(),
                                    Descripcion = r["Descripcion"].ToString(),
                                    IconoClase = estilo.Icono,
                                    ColorClase = estilo.Color,
                                    UsuarioCarga = r["NombreCompleto"]?.ToString() ?? "Sistema",
                                    Fecha = fechaMostrada,
                                    EsEnlace = esEnlace,
                                    UrlEnlace = r["Url_Enlace"]?.ToString(),
                                    Categoria = r["Categoria"]?.ToString() ?? "Publico",
                                    IdGrupoAcceso = r["Id_Grupo_Acceso"] as int?,
                                    NombreGrupoAcceso = r["Nombre_Grupo"]?.ToString(),
                                    IdUsuarioCarga = r["Id_Usuario_Carga"] != DBNull.Value ? Convert.ToInt32(r["Id_Usuario_Carga"]) : 0,
                                    TipoMime = tipoMime,
                                    Origen = r["Origen"].ToString(),
                                    UrlPortada = r["Url_Portada"]?.ToString()
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            ViewBag.EsEditorModulo = esEditorMódulo;
            return View(lista);
        }

        public async Task<IActionResult> Editor(int? id, string origen = "Formatos")
        {
            var modelo = new EditorFormatoViewModel();
            modelo.Categoria = "Publico";
            modelo.Origen = origen;

            bool puedeCrear = User.TienePermiso(_modulo, PermisoCrear);
            bool puedeEditarTodo = User.TienePermiso(_modulo, PermisoEditar);

            if (id == null || id == 0)
            {
                if (!puedeCrear)
                {
                    MostrarMensaje("Acceso Denegado", "No tienes permiso para subir nuevos formatos.", TipoMensaje.Alerta);
                    return RedirectToAction("Index");
                }
            }
            else
            {
                if (!puedeEditarTodo && !puedeCrear)
                {
                    MostrarMensaje("Acceso Denegado", "No tienes permiso para editar archivos.", TipoMensaje.Alerta);
                    return RedirectToAction("Index");
                }
            }

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    string sqlGrupos = @"SELECT ""Id_Grupo"", ""Nombre_Grupo"" FROM ""Sist_Grupos_Whatsapp"" WHERE ""Activo""=TRUE ORDER BY ""Nombre_Grupo""";
                    using (var cmdG = new NpgsqlCommand(sqlGrupos, conexion))
                    using (var rG = await cmdG.ExecuteReaderAsync())
                    {
                        while (await rG.ReadAsync())
                        {
                            modelo.ListaGrupos.Add(new SelectListItem
                            {
                                Value = rG["Id_Grupo"].ToString(),
                                Text = rG["Nombre_Grupo"].ToString()
                            });
                        }
                    }

                    if (id > 0)
                    {
                        string sql = @"SELECT * FROM ""Rec_Archivos"" WHERE ""Id_Archivo"" = @id AND ""Origen"" IN ('Formatos', 'Recursos', 'Terminos_Eventos', 'Aviso_Privacidad')";
                        using (var cmd = new NpgsqlCommand(sql, conexion))
                        {
                            cmd.Parameters.AddWithValue("@id", id);
                            using (var r = await cmd.ExecuteReaderAsync())
                            {
                                if (await r.ReadAsync())
                                {
                                    modelo.Id_Archivo = id.Value;
                                    modelo.Titulo = r["Titulo"].ToString();
                                    modelo.Descripcion = r["Descripcion"].ToString();
                                    modelo.EsEnlace = r["Es_Enlace"] != DBNull.Value && (bool)r["Es_Enlace"];
                                    modelo.UrlEnlace = r["Url_Enlace"]?.ToString();
                                    modelo.Categoria = r["Categoria"]?.ToString() ?? "Publico";
                                    modelo.IdGrupoAcceso = r["Id_Grupo_Acceso"] as int?;

                                    modelo.NombreArchivoActual = modelo.EsEnlace
                                        ? "Enlace web actual: " + modelo.UrlEnlace
                                        : "Archivo físico (Cloudinary)";
                                    int idPropietario = r["Id_Usuario_Carga"] != DBNull.Value ? Convert.ToInt32(r["Id_Usuario_Carga"]) : 0;
                                    int idUsuarioActual = int.Parse(User.FindFirst("IdUsuario").Value);

                                    if (!puedeEditarTodo && idPropietario != idUsuarioActual)
                                    {
                                        MostrarMensaje("Acceso Denegado", "Solo puedes modificar los archivos que tú mismo has subido.", TipoMensaje.Alerta);
                                        return RedirectToAction("Index");
                                    }
                                }
                                else
                                {
                                    MostrarMensaje("No Encontrado", "El formato solicitado no existe.", TipoMensaje.Error);
                                    return RedirectToAction("Index");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "No se pudo cargar el editor: " + ex.Message, TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            return View(modelo);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Guardar(EditorFormatoViewModel form)
        {
            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            bool esNuevo = form.Id_Archivo == 0;

            bool puedeEditarTodo = User.TienePermiso(_modulo, PermisoEditar);
            bool puedeCrear = User.TienePermiso(_modulo, PermisoCrear);

            if (!puedeEditarTodo && !puedeCrear)
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos para editar recursos.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            if (esNuevo && !puedeCrear)
            {
                MostrarMensaje("Acceso Denegado", "No tienes permiso para registrar recursos.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            if (form.Categoria == "Grupos" && form.IdGrupoAcceso == null)
            {
                MostrarMensaje("Atención", "Debe seleccionar un grupo si la categoría es 'Grupos de Usuarios'.", TipoMensaje.Alerta);
                await RecargarGruposEnModelo(form);
                return View("Editor", form);
            }

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            string mime = null;
                            string urlFisicaCloudinary = null;
                            string urlPortadaCloudinary = null; // Variable para la URL de la portada

                            // 1. VALIDACIÓN DEL ARCHIVO PRINCIPAL O ENLACE
                            if (form.EsEnlace)
                            {
                                if (string.IsNullOrWhiteSpace(form.UrlEnlace)) throw new Exception("Debe ingresar la URL del enlace.");
                                mime = "text/url";
                            }
                            else
                            {
                                if (esNuevo && (form.Archivo == null || form.Archivo.Length == 0))
                                {
                                    throw new Exception("Debes seleccionar un archivo para subir.");
                                }

                                if (form.Archivo != null)
                                {
                                    // --- VALIDACIÓN DE TAMAÑO MÁXIMO (10 MB) ---
                                    const long tamañoMaximoBytes = 10 * 1024 * 1024;
                                    if (form.Archivo.Length > tamañoMaximoBytes)
                                    {
                                        throw new Exception("El archivo supera el límite de 10 MB. Por favor, usa una herramienta para comprimirlo antes de subirlo.");
                                    }

                                    mime = form.Archivo.ContentType;
                                    form.UrlEnlace = null; // Se llenará con Cloudinary más adelante
                                }
                            }

                            if (form.Categoria == "Publico") form.IdGrupoAcceso = null;
                            string origenFinal = string.IsNullOrEmpty(form.Origen) ? "Formatos" : form.Origen;

                            // 2. LÓGICA DE LA PORTADA (Solo aplica si el Origen es "Recursos" y es Video)
                            if (origenFinal == "Recursos")
                            {
                                if (form.EsVideo)
                                {
                                    if (form.Portada != null && form.Portada.Length > 0)
                                    {
                                        using (var msPortada = new MemoryStream())
                                        {
                                            await form.Portada.CopyToAsync(msPortada);
                                            msPortada.Position = 0;

                                            // Usamos un GUID para asegurar un nombre único temporal en Cloudinary
                                            string folderPortada = $"{sAmbiente}/Documentos/Portadas/{Guid.NewGuid()}";

                                            var uploadParams = new ImageUploadParams()
                                            {
                                                File = new FileDescription(form.Portada.FileName, msPortada),
                                                Folder = folderPortada,
                                                Transformation = new Transformation().Crop("fill").Width(800).Height(450)
                                            };
                                            var resultPortada = await _cloudinary.UploadAsync(uploadParams);
                                            urlPortadaCloudinary = resultPortada.SecureUrl.ToString();
                                        }
                                    }
                                    else if (string.IsNullOrEmpty(form.UrlPortadaActual))
                                    {
                                        // Validación en servidor: si es video y no tiene portada previa, es obligatoria
                                        throw new Exception("Has marcado este recurso como Video, por lo tanto la imagen de portada es obligatoria.");
                                    }
                                }
                            }
                            else
                            {
                                // Si no es un "Recurso", forzamos EsVideo a false por seguridad
                                form.EsVideo = false;
                            }

                            // Validación de Unicidad para Documentos Legales de Eventos
                            if (origenFinal == "Terminos_Eventos" || origenFinal == "Aviso_Privacidad")
                            {
                                string sqlExiste = @"SELECT COUNT(1) FROM ""Rec_Archivos"" WHERE ""Origen"" = @origen" + (esNuevo ? "" : @" AND ""Id_Archivo"" <> @id");
                                using (var cmdCheckVal = new NpgsqlCommand(sqlExiste, conexion, trans))
                                {
                                    cmdCheckVal.Parameters.AddWithValue("@origen", origenFinal);
                                    if (!esNuevo) cmdCheckVal.Parameters.AddWithValue("@id", form.Id_Archivo);

                                    long countVal = Convert.ToInt64(await cmdCheckVal.ExecuteScalarAsync());
                                    if (countVal > 0)
                                    {
                                        string nombreDoc = origenFinal == "Terminos_Eventos" ? "Términos y Condiciones de Participación" : "Aviso de Privacidad Integral";
                                        throw new Exception($"Ya existe un documento cargado para '{nombreDoc}'. Solo se permite un documento de este tipo. Puedes editar el archivo existente desde el módulo.");
                                    }
                                }
                            }

                            int idGeneradoOActual = form.Id_Archivo;

                            // 3. INSERCIÓN INICIAL O VERIFICACIÓN DE PERMISOS DE EDICIÓN
                            if (esNuevo)
                            {
                                string sqlInsert = @"INSERT INTO ""Rec_Archivos"" 
                            (""Titulo"", ""Descripcion"", ""Tipo"", ""Contenido_Binario"", ""Descargas"", ""Origen"", ""Fecha_Creacion"", ""Id_Usuario_Carga"",
                             ""Es_Enlace"", ""Url_Enlace"", ""Categoria"", ""Id_Grupo_Acceso"", ""Url_Portada"", ""Es_Video"")
                            VALUES (@tit, @desc, @mime, NULL, 0, @origen, NOW(), @uid, @esenl, @urlenl, @cat, @gid, @urlportada, @esvideo)
                            RETURNING ""Id_Archivo""";

                                using (var cmd = new NpgsqlCommand(sqlInsert, conexion, trans))
                                {
                                    cmd.Parameters.AddWithValue("@tit", form.Titulo);
                                    cmd.Parameters.AddWithValue("@desc", form.Descripcion ?? "");
                                    cmd.Parameters.AddWithValue("@mime", (object)mime ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@origen", origenFinal);
                                    cmd.Parameters.AddWithValue("@uid", idUsuario);
                                    cmd.Parameters.AddWithValue("@esenl", form.EsEnlace);
                                    cmd.Parameters.AddWithValue("@urlenl", (object)form.UrlEnlace ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@cat", form.Categoria);
                                    cmd.Parameters.AddWithValue("@gid", (object)form.IdGrupoAcceso ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@esvideo", form.EsVideo);

                                    // Si es video, guardamos la URL de la portada subida. Si no, mandamos NULL.
                                    if (form.EsVideo && urlPortadaCloudinary != null)
                                    {
                                        cmd.Parameters.AddWithValue("@urlportada", urlPortadaCloudinary);
                                    }
                                    else
                                    {
                                        cmd.Parameters.AddWithValue("@urlportada", DBNull.Value);
                                    }

                                    idGeneradoOActual = (int)await cmd.ExecuteScalarAsync();
                                }
                                await Funciones.RegistrarBitacora(conexion, idUsuario, _modulo, AccionesBitacora.Crear, $"Subió {origenFinal}: {form.Titulo}", ip, trans);
                            }
                            else
                            {
                                if (!puedeEditarTodo)
                                {
                                    string sqlCheck = "SELECT \"Id_Usuario_Carga\" FROM \"Rec_Archivos\" WHERE \"Id_Archivo\" = @id";
                                    using (var cmdCheck = new NpgsqlCommand(sqlCheck, conexion, trans))
                                    {
                                        cmdCheck.Parameters.AddWithValue("@id", idGeneradoOActual);
                                        var result = await cmdCheck.ExecuteScalarAsync();

                                        if (result == null || Convert.ToInt32(result) != idUsuario)
                                        {
                                            MostrarMensaje("Acceso Denegado", "No tienes permiso para modificar este archivo.", TipoMensaje.Alerta);
                                            return RedirectToAction("Index");
                                        }
                                    }
                                }
                            }

                            // 4. SUBIDA A CLOUDINARY DEL ARCHIVO PRINCIPAL (Si aplica)
                            if (!form.EsEnlace && form.Archivo != null)
                            {
                                using (var ms = new MemoryStream())
                                {
                                    await form.Archivo.CopyToAsync(ms);
                                    ms.Position = 0;

                                    bool esImagen = form.Archivo.ContentType.ToLower().StartsWith("image/");
                                    string folderDestino = $"{sAmbiente}/Documentos/Formatos/{idGeneradoOActual}";

                                    if (!esNuevo)
                                    {
                                        try
                                        {
                                            await _cloudinary.DeleteResourcesAsync(new DelResParams() { Prefix = $"{folderDestino}/", ResourceType = ResourceType.Image });
                                            await _cloudinary.DeleteResourcesAsync(new DelResParams() { Prefix = $"{folderDestino}/", ResourceType = ResourceType.Raw });
                                        }
                                        catch { }
                                    }

                                    if (esImagen)
                                    {
                                        var uploadParams = new ImageUploadParams()
                                        {
                                            File = new FileDescription(form.Archivo.FileName, ms),
                                            Folder = folderDestino,
                                            Transformation = new Transformation().FetchFormat("auto")
                                        };
                                        var result = await _cloudinary.UploadAsync(uploadParams);
                                        urlFisicaCloudinary = result.SecureUrl.ToString();
                                    }
                                    else
                                    {
                                        var uploadParams = new RawUploadParams()
                                        {
                                            File = new FileDescription(form.Archivo.FileName, ms),
                                            Folder = folderDestino
                                        };
                                        var result = await _cloudinary.UploadAsync(uploadParams);
                                        urlFisicaCloudinary = result.SecureUrl.ToString();
                                    }
                                }
                            }

                            // 5. ACTUALIZACIÓN FINAL DEL REGISTRO (Si es edición o si subió un archivo físico nuevo)
                            if (!esNuevo || urlFisicaCloudinary != null || urlPortadaCloudinary != null)
                            {
                                string sqlUpdate = @"UPDATE ""Rec_Archivos"" SET 
                            ""Titulo"" = @tit, ""Descripcion"" = @desc,
                            ""Fecha_Modificacion"" = NOW(), ""Id_Usuario_Carga"" = @uid,
                            ""Es_Enlace"" = @esenl, ""Categoria"" = @cat, ""Id_Grupo_Acceso"" = @gid,
                            ""Origen"" = @origen, ""Es_Video"" = @esvideo";

                                // Construcción condicional para la portada
                                if (!form.EsVideo)
                                {
                                    // Si NO es video, limpiamos la portada por completo
                                    sqlUpdate += @", ""Url_Portada"" = NULL";
                                }
                                else if (urlPortadaCloudinary != null)
                                {
                                    // Si ES video y subió una nueva, la actualizamos
                                    sqlUpdate += @", ""Url_Portada"" = @urlportada";
                                }

                                // Construcción condicional para el archivo principal / enlace
                                if (form.EsEnlace)
                                {
                                    sqlUpdate += @", ""Url_Enlace"" = @urlenl, ""Tipo"" = @mime, ""Contenido_Binario"" = NULL";
                                }
                                else if (urlFisicaCloudinary != null)
                                {
                                    sqlUpdate += @", ""Url_Enlace"" = @urlenl, ""Tipo"" = @mime, ""Contenido_Binario"" = NULL";
                                }

                                sqlUpdate += @" WHERE ""Id_Archivo"" = @id";

                                using (var cmd = new NpgsqlCommand(sqlUpdate, conexion, trans))
                                {
                                    cmd.Parameters.AddWithValue("@tit", form.Titulo);
                                    cmd.Parameters.AddWithValue("@desc", form.Descripcion ?? "");
                                    cmd.Parameters.AddWithValue("@uid", idUsuario);
                                    cmd.Parameters.AddWithValue("@esenl", form.EsEnlace);
                                    cmd.Parameters.AddWithValue("@cat", form.Categoria);
                                    cmd.Parameters.AddWithValue("@gid", (object)form.IdGrupoAcceso ?? DBNull.Value);
                                    cmd.Parameters.AddWithValue("@origen", origenFinal);
                                    cmd.Parameters.AddWithValue("@esvideo", form.EsVideo);
                                    cmd.Parameters.AddWithValue("@id", idGeneradoOActual);

                                    if (form.EsVideo && urlPortadaCloudinary != null)
                                    {
                                        cmd.Parameters.AddWithValue("@urlportada", urlPortadaCloudinary);
                                    }

                                    if (form.EsEnlace)
                                    {
                                        cmd.Parameters.AddWithValue("@urlenl", form.UrlEnlace);
                                        cmd.Parameters.AddWithValue("@mime", mime);
                                    }
                                    else if (urlFisicaCloudinary != null)
                                    {
                                        cmd.Parameters.AddWithValue("@urlenl", urlFisicaCloudinary);
                                        cmd.Parameters.AddWithValue("@mime", mime);
                                    }

                                    await cmd.ExecuteNonQueryAsync();
                                }

                                if (!esNuevo)
                                {
                                    await Funciones.RegistrarBitacora(conexion, idUsuario, _modulo, AccionesBitacora.Editar, $"Editó {origenFinal}: {form.Titulo}", ip, trans);
                                }
                            }

                            await trans.CommitAsync();
                            MostrarMensaje("Éxito", $"{origenFinal} guardado correctamente.", TipoMensaje.Exito);
                        }
                        catch (Exception)
                        {
                            await trans.RollbackAsync();
                            throw;
                        }
                    }
                }
                return RedirectToAction("Index");
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                await RecargarGruposEnModelo(form);
                return View("Editor", form);
            }
        }

        private async Task RecargarGruposEnModelo(EditorFormatoViewModel modelo)
        {
            using (var conexion = new NpgsqlConnection(_cadenaConexion))
            {
                await conexion.OpenAsync();
                string sqlGrupos = @"SELECT ""Id_Grupo"", ""Nombre_Grupo"" FROM ""Sist_Grupos_Whatsapp"" WHERE ""Activo""=TRUE ORDER BY ""Nombre_Grupo""";
                using (var cmdG = new NpgsqlCommand(sqlGrupos, conexion))
                using (var rG = await cmdG.ExecuteReaderAsync())
                {
                    while (await rG.ReadAsync())
                    {
                        modelo.ListaGrupos.Add(new SelectListItem
                        {
                            Value = rG["Id_Grupo"].ToString(),
                            Text = rG["Nombre_Grupo"].ToString()
                        });
                    }
                }
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Eliminar(int id)
        {
            bool puedeBorrarTodo = User.TienePermiso(_modulo, PermisoBorrar);
            bool puedeCrear = User.TienePermiso(_modulo, PermisoCrear);

            if (!puedeBorrarTodo && !puedeCrear)
            {
                MostrarMensaje("Acceso Denegado", "No tienes permisos para eliminar recursos.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }

            int idUsuarioActual = int.Parse(User.FindFirst("IdUsuario").Value);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    if (!puedeBorrarTodo)
                    {
                        string sqlCheck = "SELECT \"Id_Usuario_Carga\" FROM \"Rec_Archivos\" WHERE \"Id_Archivo\" = @id";
                        using (var cmdCheck = new NpgsqlCommand(sqlCheck, conexion))
                        {
                            cmdCheck.Parameters.AddWithValue("@id", id);
                            var result = await cmdCheck.ExecuteScalarAsync();

                            if (result == null || Convert.ToInt32(result) != idUsuarioActual)
                            {
                                MostrarMensaje("Acceso Denegado", "No puedes eliminar un archivo que no has subido tú.", TipoMensaje.Alerta);
                                return RedirectToAction("Index");
                            }
                        }
                    }

                    // Eliminar registro de BD
                    string sqlDelete = "DELETE FROM \"Rec_Archivos\" WHERE \"Id_Archivo\" = @id";
                    using (var cmd = new NpgsqlCommand(sqlDelete, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        int filasAfectadas = await cmd.ExecuteNonQueryAsync();

                        if (filasAfectadas > 0)
                        {
                            // Destrucción física de Cloudinary
                            try
                            {
                                string folderPath = $"{sAmbiente}/Documentos/Formatos/{id}";

                                // Corrección: Borramos explícitamente tanto imágenes como archivos crudos (PDF, Excel, etc.)
                                await _cloudinary.DeleteResourcesAsync(new DelResParams() { Prefix = $"{folderPath}/", ResourceType = ResourceType.Image });
                                await _cloudinary.DeleteResourcesAsync(new DelResParams() { Prefix = $"{folderPath}/", ResourceType = ResourceType.Raw });

                                // Ahora la carpeta debe estar vacía y se puede borrar
                                await _cloudinary.DeleteFolderAsync(folderPath);
                            }
                            catch { /* Se ignora el fallo externo de Cloudinary si la DB ya borró el dato */ }

                            MostrarMensaje("Éxito", "El recurso ha sido eliminado correctamente.", TipoMensaje.Exito);
                        }
                        else
                        {
                            MostrarMensaje("Error", "No se encontró el archivo a eliminar.", TipoMensaje.Error);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error Crítico", "Ocurrió un error al intentar eliminar: " + ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Index");
        }

        public async Task<IActionResult> Descargar(int id)
        {
            if (id <= 0) return RedirectToAction("Index");
            if (!User.TienePermiso(_modulo, PermisoLeer)) return RedirectToAction("Index", "Home");

            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Desconocida";

            try
            {
                string nombreArchivo = "";
                bool esEnlace = false;
                string urlEnlace = "";

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            string sqlSelect = @"SELECT ""Titulo"", ""Es_Enlace"", ""Url_Enlace""
                                         FROM ""Rec_Archivos"" 
                                         WHERE ""Id_Archivo"" = @id AND ""Origen"" IN ('Formatos', 'Recursos', 'Terminos_Eventos', 'Aviso_Privacidad')";

                            using (var cmd = new NpgsqlCommand(sqlSelect, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@id", id);
                                using (var r = await cmd.ExecuteReaderAsync())
                                {
                                    if (await r.ReadAsync())
                                    {
                                        nombreArchivo = r["Titulo"].ToString();
                                        esEnlace = r["Es_Enlace"] != DBNull.Value && (bool)r["Es_Enlace"];
                                        urlEnlace = r["Url_Enlace"]?.ToString();
                                    }
                                }
                            }

                            if (string.IsNullOrWhiteSpace(urlEnlace))
                            {
                                await trans.RollbackAsync();
                                MostrarMensaje("Error", "El archivo o enlace solicitado está vacío.", TipoMensaje.Error);
                                return RedirectToAction("Index");
                            }

                            string sqlUpdate = @"UPDATE ""Rec_Archivos"" SET ""Descargas"" = ""Descargas"" + 1 WHERE ""Id_Archivo"" = @id";
                            using (var cmdUpd = new NpgsqlCommand(sqlUpdate, conexion, trans))
                            {
                                cmdUpd.Parameters.AddWithValue("@id", id);
                                await cmdUpd.ExecuteNonQueryAsync();
                            }

                            await Funciones.RegistrarBitacora(conexion, idUsuario, _modulo, AccionesBitacora.Leer,
                                $"Acceso a recurso: {nombreArchivo} (ID: {id}, Tipo: {(esEnlace ? "Enlace" : "Archivo físico")})", ip, trans);

                            await trans.CommitAsync();
                        }
                        catch { await trans.RollbackAsync(); throw; }
                    }
                }

                // Ahora todo es una redirección a la web (sea URL propia de Cloudinary o enlace externo)
                return Redirect(urlEnlace);
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "Ocurrió un problema: " + ex.Message, TipoMensaje.Error);
                return RedirectToAction("Index");
            }
        }

        public async Task<IActionResult> Ver(int id)
        {
            if (id <= 0) return NotFound();
            if (!User.TienePermiso(_modulo, PermisoLeer)) return Forbid();

            try
            {
                string urlEnlace = "";

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    string sqlSelect = @"SELECT ""Url_Enlace""
                                         FROM ""Rec_Archivos"" 
                                         WHERE ""Id_Archivo"" = @id AND ""Origen"" IN ('Formatos', 'Recursos', 'Terminos_Eventos', 'Aviso_Privacidad')";

                    using (var cmd = new NpgsqlCommand(sqlSelect, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                urlEnlace = r["Url_Enlace"]?.ToString();
                            }
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(urlEnlace)) return NotFound();

                // Redirigir a la URL (Permite ver PDFs e imágenes directamente en el navegador)
                return Redirect(urlEnlace);
            }
            catch { return StatusCode(500); }
        }

        private (string Icono, string Color) ObtenerEstiloPorMime(string mime)
        {
            if (string.IsNullOrEmpty(mime)) return ("fa-file", "text-secondary");
            mime = mime.ToLower();
            if (mime.Contains("pdf")) return ("fa-file-pdf", "text-danger");
            if (mime.Contains("sheet") || mime.Contains("excel") || mime.Contains("csv")) return ("fa-file-excel", "text-success");
            if (mime.Contains("word") || mime.Contains("officedocument.textpro")) return ("fa-file-word", "text-primary");
            if (mime.Contains("image")) return ("fa-file-image", "text-info");
            if (mime.Contains("zip") || mime.Contains("rar") || mime.Contains("tar")) return ("fa-file-zipper", "text-warning");
            if (mime.Contains("video")) return ("fa-file-video", "text-dark");
            if (mime.Contains("text/url")) return ("fa-link", "text-success");

            return ("fa-file-lines", "text-secondary");
        }

        private string ObtenerExtensionPorMime(string mime)
        {
            if (string.IsNullOrEmpty(mime)) return ".dat";
            mime = mime.ToLower();
            if (mime.Contains("pdf")) return ".pdf";
            if (mime.Contains("excel") || mime.Contains("sheet")) return ".xlsx";
            if (mime.Contains("word")) return ".docx";
            if (mime.Contains("png")) return ".png";
            if (mime.Contains("jpeg") || mime.Contains("jpg")) return ".jpg";
            if (mime.Contains("gif")) return ".gif";
            if (mime.Contains("zip")) return ".zip";
            if (mime.Contains("csv")) return ".csv";
            return "";
        }
    }
}