using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using RedAJP.Globales;
using RedAJP.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace RedAJP.Controllers
{
    [Authorize]
    public class IglesiasWebController : GlobalController
    {
        private readonly string _cadenaConexion;
        private readonly Cloudinary _cloudinary;
        private Parametros.Modulo Modulo = Parametros.Modulos.Reuniones;
        private readonly IConfiguration _configuration;

        public IglesiasWebController(IConfiguration configuration)
        {
            _configuration = configuration; 
            _cadenaConexion = configuration.GetConnectionString("MiConexion");

            // Configuración Cloudinary
            Account account = new Account(
                configuration["Cloudinary:CloudName"],
                configuration["Cloudinary:ApiKey"],
                configuration["Cloudinary:ApiSecret"]
            );
            _cloudinary = new Cloudinary(account);
            _cloudinary.Api.Secure = true;
        }

        // =========================================================
        // 0. SEGURIDAD
        // =========================================================
        private async Task<bool> EsAdministradorValido(int idIglesia, int idUsuario)
        {
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    string sql = "SELECT 1 FROM iciar_iglesias_administradores WHERE iglesia_id = @idIglesia AND usuario_id = @idUsuario";
                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@idIglesia", idIglesia);
                        cmd.Parameters.AddWithValue("@idUsuario", idUsuario);
                        return await cmd.ExecuteScalarAsync() != null;
                    }
                }
            }
            catch { return false; }
        }

        // =========================================================
        // 1. PANEL PRINCIPAL: MIS PÁGINAS WEB
        // =========================================================
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            var modelo = new List<MisIglesiasPanelViewModel>();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    // Agregamos la subconsulta para extraer 'es_personalizable' de la tabla de plantillas.
                    // ORDER BY b.estado ASC asegura que si hay un 'BOR', lo lea primero antes del 'PUB'.
                    string sql = @"
                    SELECT i.id, i.nombre, i.slug, m.nombre as municipio, 
                           COALESCE(w.activa, FALSE) as activa, w.fecha_actualizacion,
                           EXISTS(SELECT 1 FROM iciar_iglesias_web b WHERE b.iglesia_id = i.id AND b.estado = 'BOR') as tiene_borrador_datos,
                           EXISTS(SELECT 1 FROM iciar_iglesias_web_diseno d WHERE d.iglesia_id = i.id AND d.estado = 'BOR') as tiene_borrador_diseno,
                           COALESCE((
                               SELECT p.es_personalizable 
                               FROM iciar_iglesias_web wb 
                               JOIN iciar_plantillas_web p ON wb.plantilla_id = p.id 
                               WHERE wb.iglesia_id = i.id 
                               ORDER BY wb.estado ASC LIMIT 1
                           ), FALSE) as es_modular
                    FROM iciar_iglesias_administradores a
                    JOIN iciar_iglesias i ON a.iglesia_id = i.id
                    JOIN iciar_municipios m ON i.municipio_id = m.id
                    LEFT JOIN iciar_iglesias_web w ON i.id = w.iglesia_id AND w.estado = 'PUB'
                    WHERE a.usuario_id = @uid ORDER BY i.nombre ASC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@uid", idUsuario);
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                modelo.Add(new MisIglesiasPanelViewModel
                                {
                                    IdIglesia = Convert.ToInt32(reader["id"]),
                                    sIdIglesia = Funciones.EncriptarId(Convert.ToInt32(reader["id"])),
                                    Nombre = reader["nombre"].ToString(),
                                    Slug = reader["slug"].ToString(),
                                    Municipio = reader["municipio"].ToString(),
                                    EstaActiva = Convert.ToBoolean(reader["activa"]),
                                    TieneBorradorDatos = Convert.ToBoolean(reader["tiene_borrador_datos"]),
                                    TieneBorradorDiseno = Convert.ToBoolean(reader["tiene_borrador_diseno"]),
                                    TieneBorradorPendiente = Convert.ToBoolean(reader["tiene_borrador_datos"]) || Convert.ToBoolean(reader["tiene_borrador_diseno"]),
                                    UltimaActualizacion = reader["fecha_actualizacion"] != DBNull.Value ? Convert.ToDateTime(reader["fecha_actualizacion"]) : null,

                                    // ASIGNAMOS LA NUEVA VARIABLE DESDE SQL
                                    EsModular = Convert.ToBoolean(reader["es_modular"])
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }
            return View(modelo);
        }

        [HttpPost]
        [Authorize]
        public async Task<IActionResult> ToggleEstadoWeb(string sId, bool activa)
        {
            int idIglesia = Funciones.DesencriptarId(sId);
            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);

            // Validar seguridad
            if (!await EsAdministradorValido(idIglesia, idUsuario))
                return Json(new { success = false, message = "No tienes permisos para esta acción." });

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // Actualizamos el estado en todos los registros de la iglesia (BOR y PUB) 
                    // para que el cambio sea persistente y no se revierta al publicar.
                    string sql = "UPDATE iciar_iglesias_web SET activa = @activa WHERE iglesia_id = @id";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idIglesia);
                        cmd.Parameters.AddWithValue("@activa", activa);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }
        // =========================================================
        // 2. EDITOR WEB (GET) - LLENA EL FORMULARIO
        // =========================================================
        [HttpGet]
        public async Task<IActionResult> Editar(string sId)
        {
            if (string.IsNullOrEmpty(sId))
            {
                MostrarMensaje("Error", "No se ha encontrado la página a editar.", TipoMensaje.Error);
                return RedirectToAction("Index"); 
            }

            int idIglesia = Funciones.DesencriptarId(sId);
            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);

            // 1. Validar seguridad del administrador
            if (!await EsAdministradorValido(idIglesia, idUsuario))
            {
                MostrarMensaje("Bloqueado", "No tienes permisos para editar esta página web.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            var modelo = new IglesiaWebEdicionViewModel
            {
                IdIglesia = idIglesia,
                sIdIglesia = sId
            };

            string estadoLectura = "PUB"; // Por defecto busca la versión pública si no hay borrador

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // Variables temporales para generar el slug si hace falta
                    string tempNombre = "";
                    string tempMunicipio = "";

                    // 2. CARGAR DATOS DE LA TABLA PRINCIPAL (Añadimos 'municipio' a la consulta SQL)
                    using (var cmdNom = new NpgsqlCommand("SELECT i.\"nombre\" as Iglesia, m.\"nombre\" as Municipio, slug, horarios" +
                        " FROM \"iciar_iglesias\" i" +
                        " LEFT JOIN \"iciar_municipios\" m on m.Id = i.municipio_id " +
                        " WHERE i.id = @id", conexion))
                    {
                        cmdNom.Parameters.AddWithValue("@id", idIglesia);
                        using (var reader = await cmdNom.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                tempNombre = reader["Iglesia"].ToString();
                                tempMunicipio = reader["Municipio"]?.ToString() ?? "";

                                modelo.NombreIglesia = tempNombre;
                                modelo.Slug = reader["slug"]?.ToString();
                                modelo.HorariosIglesia = reader["horarios"]?.ToString();
                            }
                        }
                    }

                    // Verificar si ya existe una versión publicada previamente
                    using (var cmdP = new NpgsqlCommand("SELECT 1 FROM iciar_iglesias_web WHERE iglesia_id = @id AND estado = 'PUB'", conexion))
                    {
                        cmdP.Parameters.AddWithValue("@id", idIglesia);
                        ViewBag.TienePublicada = await cmdP.ExecuteScalarAsync() != null;
                    }

                    // 3. CARGAR CONFIGURACIÓN WEB (Priorizando explícitamente el Borrador sobre lo Público)
                    string sqlWeb = @"
                        SELECT * FROM iciar_iglesias_web 
                        WHERE iglesia_id = @id AND estado IN ('BOR', 'PUB') 
                        ORDER BY CASE WHEN estado = 'BOR' THEN 1 ELSE 2 END 
                        LIMIT 1";

                    using (var cmdWeb = new NpgsqlCommand(sqlWeb, conexion))
                    {
                        cmdWeb.Parameters.AddWithValue("@id", idIglesia);
                        using (var reader = await cmdWeb.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                // Actualizamos el estado para que Avisos, Eventos, Galería, etc., carguen lo correcto.
                                estadoLectura = reader["estado"].ToString();

                                // Si el registro (BOR o PUB) ya tiene un slug trabajado, sobreescribe al original
                                if (reader["slug"] != DBNull.Value && !string.IsNullOrWhiteSpace(reader["slug"].ToString()))
                                {
                                    modelo.Slug = reader["slug"].ToString();
                                }

                                modelo.FacebookUrl = reader["facebook_url"]?.ToString();
                                modelo.InstagramUrl = reader["instagram_url"]?.ToString();
                                modelo.YoutubeUrl = reader["youtube_url"]?.ToString();
                                modelo.Whatsapp = reader["whatsapp"]?.ToString();
                                modelo.Telefono = reader["telefono"]?.ToString();
                                modelo.Historia = reader["historia"]?.ToString();

                                if (reader["plantilla_id"] != DBNull.Value)
                                    modelo.PlantillaId = Convert.ToInt32(reader["plantilla_id"]);

                                if (reader["activa"] != DBNull.Value)
                                    modelo.Activa = Convert.ToBoolean(reader["activa"]);
                            }
                        }
                    }

                    // 4. NUEVO: GENERACIÓN AUTOMÁTICA DEL SLUG
                    // Si tras consultar ambas tablas (oficial y borrador) el slug sigue vacío:
                    if (string.IsNullOrWhiteSpace(modelo.Slug))
                    {
                        modelo.Slug = await GenerarSlugUnicoAsync(tempNombre, tempMunicipio, idIglesia, conexion);
                    }

                    // AVISOS
                    using (var cmd = new NpgsqlCommand("SELECT * FROM iciar_iglesias_avisos WHERE iglesia_id = @id AND estado = @est ORDER BY fecha_inicio ASC", conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idIglesia);
                        cmd.Parameters.AddWithValue("@est", estadoLectura);
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                modelo.Avisos.Add(new AvisoWebViewModel
                                {
                                    Id = Convert.ToInt32(reader["id"]),
                                    Titulo = reader["titulo"].ToString(),
                                    Descripcion = reader["descripcion"]?.ToString(),
                                    FechaInicio = Convert.ToDateTime(reader["fecha_inicio"]),
                                    FechaExpiracion = Convert.ToDateTime(reader["fecha_expiracion"])
                                });
                            }
                        }
                    }

                    // EVENTOS
                    using (var cmd = new NpgsqlCommand("SELECT * FROM iciar_iglesias_eventos WHERE iglesia_id = @id AND estado = @est ORDER BY fecha ASC", conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idIglesia);
                        cmd.Parameters.AddWithValue("@est", estadoLectura);
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                modelo.Eventos.Add(new EventoWebViewModel
                                {
                                    Id = Convert.ToInt32(reader["id"]),
                                    Titulo = reader["titulo"].ToString(),
                                    Fecha = Convert.ToDateTime(reader["fecha"]),
                                    Horarios = reader["horarios"].ToString(),
                                    Descripcion = reader["descripcion"]?.ToString(),
                                    ImagenUrl = reader["imagen_url"]?.ToString()
                                });
                            }
                        }
                    }

                    // GALERÍA (Incluye el título guardado)
                    using (var cmd = new NpgsqlCommand("SELECT id, url_imagen, orden, titulo FROM iciar_iglesias_galeria WHERE iglesia_id = @id AND estado = @est ORDER BY orden ASC", conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idIglesia);
                        cmd.Parameters.AddWithValue("@est", estadoLectura);
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                modelo.Galeria.Add(new GaleriaWebViewModel
                                {
                                    Id = Convert.ToInt32(reader["id"]),
                                    UrlImagen = reader["url_imagen"].ToString(),
                                    Orden = Convert.ToInt16(reader["orden"]),
                                    Titulo = reader["titulo"]?.ToString()
                                });
                            }
                        }
                    }

                    // MULTIMEDIA (Incluye el título guardado)
                    using (var cmd = new NpgsqlCommand("SELECT id, url_embed, orden, titulo FROM iciar_iglesias_multimedia WHERE iglesia_id = @id AND estado = @est ORDER BY orden ASC", conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idIglesia);
                        cmd.Parameters.AddWithValue("@est", estadoLectura);
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                modelo.Multimedia.Add(new MultimediaWebViewModel
                                {
                                    Id = Convert.ToInt32(reader["id"]),
                                    UrlEmbed = reader["url_embed"].ToString(),
                                    Orden = Convert.ToInt16(reader["orden"]),
                                    Titulo = reader["titulo"]?.ToString()
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "Error al cargar los datos: " + ex.Message, TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            // 5. Cargar plantillas disponibles para el selector visual
            await CargarPlantillasParaViewBag();
            return View(modelo);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DescartarBorrador(string sId)
        {
            if (string.IsNullOrEmpty(sId))
            {
                MostrarMensaje("Error", "No se ha encontrado la página a editar.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }
            int idIglesia = Funciones.DesencriptarId(sId);
            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);

            if (!await EsAdministradorValido(idIglesia, idUsuario))
            {
                MostrarMensaje("Error", "No eres administrador de ésta página.", TipoMensaje.Error);
                return RedirectToAction("Index"); 
            }

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. Limpiar imágenes subidas a Cloudinary en este borrador
                    var urlsBorrador = new List<string>();
                    using (var cmd = new NpgsqlCommand("SELECT imagen_url FROM iciar_iglesias_eventos WHERE iglesia_id = @id AND estado = 'BOR' AND imagen_url IS NOT NULL", conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idIglesia);
                        using (var reader = await cmd.ExecuteReaderAsync()) while (await reader.ReadAsync()) urlsBorrador.Add(reader[0].ToString());
                    }
                    using (var cmd = new NpgsqlCommand("SELECT url_imagen FROM iciar_iglesias_galeria WHERE iglesia_id = @id AND estado = 'BOR' AND url_imagen IS NOT NULL", conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idIglesia);
                        using (var reader = await cmd.ExecuteReaderAsync()) while (await reader.ReadAsync()) urlsBorrador.Add(reader[0].ToString());
                    }

                    foreach (var url in urlsBorrador)
                    {
                        if (url.Contains("res.cloudinary.com") && url.Contains("/BOR/"))
                            await BorrarArchivoCloudinary(url);
                    }

                    // 2. Eliminar registros de la base de datos (Solo los 'BOR')
                    using (var transaccion = await conexion.BeginTransactionAsync())
                    {
                        string[] tablas = { "iciar_iglesias_web", "iciar_iglesias_avisos", "iciar_iglesias_eventos", "iciar_iglesias_galeria", "iciar_iglesias_multimedia", "iciar_iglesias_web_diseno" };

                        foreach (var tabla in tablas)
                        {
                            using (var cmdD = new NpgsqlCommand($"DELETE FROM {tabla} WHERE iglesia_id = @id AND estado = 'BOR'", conexion, transaccion))
                            {
                                cmdD.Parameters.AddWithValue("@id", idIglesia);
                                await cmdD.ExecuteNonQueryAsync();
                            }
                        }
                        await transaccion.CommitAsync();
                    }
                }

                MostrarMensaje("Borrador Descartado", "Se han eliminado los cambios no guardados. Volviendo a la versión publicada.", TipoMensaje.Info);
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "No se pudo descartar el borrador: " + ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Index");
        }

        // =========================================================
        // 3. POSTBACK: GUARDAR BORRADOR
        // =========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GuardarBorrador(IglesiaWebEdicionViewModel model, bool salir)
        {
            if (model == null || model.IdIglesia <= 0)
            {
                MostrarMensaje("Error Crítico", "Los datos enviados no son válidos.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            model.Avisos ??= new List<AvisoWebViewModel>();
            model.Eventos ??= new List<EventoWebViewModel>();
            model.Galeria ??= new List<GaleriaWebViewModel>();
            model.Multimedia ??= new List<MultimediaWebViewModel>();

            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            string ipUsuario = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            if (!await EsAdministradorValido(model.IdIglesia, idUsuario))
            {
                MostrarMensaje("Bloqueado", "No tienes permisos para editar esta página web.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            // =========================================================
            // BLOQUE DE VALIDACIONES ESTRICTAS DE SERVIDOR RESTAURADO
            // =========================================================

            // A. Validar Dominios de Redes Sociales
            if (!string.IsNullOrWhiteSpace(model.FacebookUrl))
            {
                string fb = model.FacebookUrl.ToLower();
                if (!fb.Contains("facebook.com") && !fb.Contains("fb.me") && !fb.Contains("fb.com"))
                    ModelState.AddModelError("FacebookUrl", "El enlace de Facebook debe pertenecer a facebook.com");
            }

            if (!string.IsNullOrWhiteSpace(model.InstagramUrl))
            {
                if (!model.InstagramUrl.ToLower().Contains("instagram.com"))
                    ModelState.AddModelError("InstagramUrl", "El enlace de Instagram debe pertenecer a instagram.com");
            }

            if (!string.IsNullOrWhiteSpace(model.YoutubeUrl))
            {
                string yt = model.YoutubeUrl.ToLower();
                if (!yt.Contains("youtube.com") && !yt.Contains("youtu.be"))
                    ModelState.AddModelError("YoutubeUrl", "El enlace de YouTube debe pertenecer a youtube.com o youtu.be");
            }

            // B. Validar Avisos
            for (int i = 0; i < model.Avisos.Count; i++)
            {
                var aviso = model.Avisos[i];
                if (string.IsNullOrWhiteSpace(aviso.Titulo))
                    ModelState.AddModelError("", $"El Aviso #{i + 1} no tiene Título.");

                if (aviso.FechaInicio == default)
                    ModelState.AddModelError("", $"El Aviso '{aviso.Titulo ?? $"#{i + 1}"}' requiere Fecha de Inicio.");

                if (aviso.FechaExpiracion == default)
                    ModelState.AddModelError("", $"El Aviso '{aviso.Titulo ?? $"#{i + 1}"}' requiere Fecha de Expiración.");

                if (aviso.FechaInicio != default && aviso.FechaExpiracion != default && aviso.FechaExpiracion < aviso.FechaInicio)
                    ModelState.AddModelError("", $"En el Aviso '{aviso.Titulo}', la fecha de expiración no puede ser menor a la de inicio.");
            }

            // C. Validar Eventos
            string[] extValidas = { ".jpg", ".jpeg", ".png", ".webp" };
            for (int i = 0; i < model.Eventos.Count; i++)
            {
                var ev = model.Eventos[i];
                if (string.IsNullOrWhiteSpace(ev.Titulo)) ModelState.AddModelError("", $"El Evento #{i + 1} no tiene Título.");
                if (string.IsNullOrWhiteSpace(ev.Horarios)) ModelState.AddModelError("", $"El Evento '{ev.Titulo ?? $"#{i + 1}"}' requiere Horarios.");
                if (ev.Fecha == default) ModelState.AddModelError("", $"El Evento '{ev.Titulo ?? $"#{i + 1}"}' requiere Fecha.");

                if (ev.NuevaImagen != null)
                {
                    if (ev.NuevaImagen.Length > 10 * 1024 * 1024) ModelState.AddModelError("", $"El flyer del evento '{ev.Titulo}' excede 10MB.");
                    if (!extValidas.Contains(Path.GetExtension(ev.NuevaImagen.FileName).ToLower())) ModelState.AddModelError("", $"El flyer del evento '{ev.Titulo}' tiene formato inválido.");
                }
            }

            // D. Validar Galería (Límites, Formatos y TÍTULOS OBLIGATORIOS)
            var nuevasUrlsVal = Request.Form["NuevasUrls"];
            int totalFotosGaleria = model.Galeria.Count(g => !g.Eliminar) + (model.NuevasImagenesGaleria?.Count ?? 0) + nuevasUrlsVal.Count(u => !string.IsNullOrWhiteSpace(u));

            if (totalFotosGaleria > 10)
                ModelState.AddModelError("", "La Galería no puede exceder el límite de 10 imágenes.");

            // D.1 Títulos de Fotos Existentes
            for (int i = 0; i < model.Galeria.Count; i++)
            {
                if (!model.Galeria[i].Eliminar && string.IsNullOrWhiteSpace(model.Galeria[i].Titulo))
                {
                    ModelState.AddModelError("", $"La imagen guardada #{i + 1} en la galería requiere un título obligatorio.");
                }
            }

            // D.2 Archivos Nuevos Físicos (Formatos y Títulos)
            if (model.NuevasImagenesGaleria != null)
            {
                var titulosNuevosVal = Request.Form["NuevasImagenesTitulos"];
                for (int i = 0; i < model.NuevasImagenesGaleria.Count; i++)
                {
                    var foto = model.NuevasImagenesGaleria[i];
                    if (foto.Length > 10 * 1024 * 1024) ModelState.AddModelError("", $"La imagen '{foto.FileName}' excede 10MB.");
                    if (!extValidas.Contains(Path.GetExtension(foto.FileName).ToLower())) ModelState.AddModelError("", $"El formato de '{foto.FileName}' es inválido.");

                    if (string.IsNullOrWhiteSpace(titulosNuevosVal.Count > i ? titulosNuevosVal[i] : null))
                        ModelState.AddModelError("", $"La nueva imagen subida '{foto.FileName}' requiere un título obligatorio.");
                }
            }

            // D.3 URLs Directas Nuevas (Títulos)
            var nuevasUrlsTitulosVal = Request.Form["NuevasUrlsTitulos"];
            for (int i = 0; i < nuevasUrlsVal.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(nuevasUrlsVal[i]))
                {
                    if (string.IsNullOrWhiteSpace(nuevasUrlsTitulosVal.Count > i ? nuevasUrlsTitulosVal[i] : null))
                        ModelState.AddModelError("", $"El enlace de imagen directo #{i + 1} requiere un título obligatorio.");
                }
            }

            // E. Validar Multimedia (Embeds y TÍTULOS OBLIGATORIOS)
            for (int i = 0; i < model.Multimedia.Count; i++)
            {
                var m = model.Multimedia[i];
                if (string.IsNullOrWhiteSpace(m.UrlEmbed))
                {
                    ModelState.AddModelError("", $"El enlace Multimedia #{i + 1} está vacío.");
                }
                else if (!Uri.TryCreate(m.UrlEmbed, UriKind.Absolute, out Uri uriResult) || (uriResult.Scheme != Uri.UriSchemeHttp && uriResult.Scheme != Uri.UriSchemeHttps))
                {
                    ModelState.AddModelError("", $"El enlace Multimedia #{i + 1} no es una URL válida.");
                }
                else
                {
                    string host = uriResult.Host.ToLower();
                    if (!host.Contains("youtube.com") && !host.Contains("youtu.be") && !host.Contains("facebook.com") && !host.Contains("instagram.com"))
                        ModelState.AddModelError("", $"El enlace Multimedia '{m.UrlEmbed}' no es de un proveedor autorizado.");
                }

                if (string.IsNullOrWhiteSpace(m.Titulo))
                    ModelState.AddModelError("", $"El video o transmisión #{i + 1} requiere un título obligatorio.");
            }

            // F. Revisión Final del ModelState
            if (!ModelState.IsValid)
            {
                var listaErrores = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).Where(msg => !string.IsNullOrEmpty(msg)).Distinct().ToList();
                string mensajeLimpio = "Por favor corrige lo siguiente:\n• " + string.Join("\n• ", listaErrores);
                MostrarMensaje("Datos Inválidos", mensajeLimpio, TipoMensaje.Error);
                ViewBag.EsBorrador = true;
                await CargarPlantillasParaViewBag();
                return View("Editar", model);
            }

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    // --- VALIDACIÓN DEL SLUG EN AMBAS TABLAS ---
                    string sqlValidarSlug = @"
                    SELECT 1 FROM iciar_iglesias WHERE slug = @slug AND id != @id
                    UNION
                    SELECT 1 FROM iciar_iglesias_web WHERE slug = @slug AND iglesia_id != @id
                    LIMIT 1";

                    using (var cmdValidar = new NpgsqlCommand(sqlValidarSlug, conexion))
                    {
                        cmdValidar.Parameters.AddWithValue("@slug", model.Slug.ToLower().Trim());
                        cmdValidar.Parameters.AddWithValue("@id", model.IdIglesia);
                        if (await cmdValidar.ExecuteScalarAsync() != null)
                        {
                            MostrarMensaje("Error", "Este enlace (slug) ya está siendo usado o reservado por otra iglesia. Elige uno diferente.", TipoMensaje.Error);
                            return RedirectToAction("Editar", new { sId = model.sIdIglesia });
                        }
                    }
                    using (var transaccion = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 0. ACTUALIZAR HORARIOS EN LA TABLA PRINCIPAL (iciar_iglesias)
                            string sqlUpdateHorarios = "UPDATE iciar_iglesias SET horarios = @horarios WHERE id = @id";
                            using (var cmdH = new NpgsqlCommand(sqlUpdateHorarios, conexion, transaccion))
                            {
                                cmdH.Parameters.AddWithValue("@id", model.IdIglesia);
                                cmdH.Parameters.AddWithValue("@horarios", (object)model.HorariosIglesia ?? DBNull.Value);
                                await cmdH.ExecuteNonQueryAsync();
                            }

                            // A. UPSERT WEB BÁSICO
                            string sqlUpsert = @"
                            INSERT INTO iciar_iglesias_web 
                            (iglesia_id, estado, slug, activa, plantilla_id, facebook_url, instagram_url, youtube_url, whatsapp, telefono, historia, fecha_actualizacion) 
                            VALUES (@id, 'BOR', @slug, @act, @plant, @fb, @ig, @yt, @wa, @tel, @hist, NOW())
                            ON CONFLICT (iglesia_id, estado) DO UPDATE SET 
                            slug = EXCLUDED.slug,
                            activa = EXCLUDED.activa, plantilla_id = EXCLUDED.plantilla_id,
                            facebook_url = EXCLUDED.facebook_url, instagram_url = EXCLUDED.instagram_url,
                            youtube_url = EXCLUDED.youtube_url, whatsapp = EXCLUDED.whatsapp,
                            telefono = EXCLUDED.telefono, historia = EXCLUDED.historia, fecha_actualizacion = NOW()";

                            using (var cmd = new NpgsqlCommand(sqlUpsert, conexion, transaccion))
                            {
                                cmd.Parameters.AddWithValue("@id", model.IdIglesia);

                                // Guardamos el slug en minúsculas y sin espacios al inicio/final por seguridad
                                cmd.Parameters.AddWithValue("@slug", (object)model.Slug?.ToLower().Trim() ?? DBNull.Value);

                                cmd.Parameters.AddWithValue("@act", model.Activa);
                                cmd.Parameters.AddWithValue("@plant", model.PlantillaId);
                                cmd.Parameters.AddWithValue("@fb", (object)model.FacebookUrl ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@ig", (object)model.InstagramUrl ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@yt", (object)model.YoutubeUrl ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@wa", (object)model.Whatsapp ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@tel", (object)model.Telefono ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@hist", (object)model.Historia ?? DBNull.Value);
                                await cmd.ExecuteNonQueryAsync();
                            }

                            // B. AVISOS
                            using (var cmdD = new NpgsqlCommand("DELETE FROM iciar_iglesias_avisos WHERE iglesia_id = @id AND estado = 'BOR'", conexion, transaccion)) { cmdD.Parameters.AddWithValue("@id", model.IdIglesia); await cmdD.ExecuteNonQueryAsync(); }
                            if (model.Avisos.Any())
                            {
                                string sqlA = "INSERT INTO iciar_iglesias_avisos (iglesia_id, estado, titulo, descripcion, fecha_inicio, fecha_expiracion) VALUES (@id, 'BOR', @tit, @desc, @fi, @fe)";
                                foreach (var a in model.Avisos)
                                {
                                    using (var cmdI = new NpgsqlCommand(sqlA, conexion, transaccion))
                                    {
                                        cmdI.Parameters.AddWithValue("@id", model.IdIglesia);
                                        cmdI.Parameters.AddWithValue("@tit", a.Titulo);
                                        cmdI.Parameters.AddWithValue("@desc", (object)a.Descripcion ?? DBNull.Value);
                                        cmdI.Parameters.AddWithValue("@fi", a.FechaInicio);
                                        cmdI.Parameters.AddWithValue("@fe", a.FechaExpiracion);
                                        await cmdI.ExecuteNonQueryAsync();
                                    }
                                }
                            }

                            // C. EVENTOS
                            using (var cmdD = new NpgsqlCommand("DELETE FROM iciar_iglesias_eventos WHERE iglesia_id = @id AND estado = 'BOR'", conexion, transaccion)) { cmdD.Parameters.AddWithValue("@id", model.IdIglesia); await cmdD.ExecuteNonQueryAsync(); }
                            if (model.Eventos.Any())
                            {
                                string sqlE = "INSERT INTO iciar_iglesias_eventos (iglesia_id, estado, titulo, fecha, horarios, descripcion, imagen_url) VALUES (@id, 'BOR', @tit, @f, @h, @desc, @img)";
                                foreach (var e in model.Eventos)
                                {
                                    string urlFlyer = e.ImagenUrl;

                                    if (e.NuevaImagen != null && e.NuevaImagen.Length > 0)
                                    {
                                        // Si subió archivo nuevo, va a BOR directamente
                                        urlFlyer = await ProcesarSubidaCloudinary(e.NuevaImagen, $"{sAmbiente}/Iglesias/{model.IdIglesia}/BOR/Eventos");
                                    }
                                    else if (!string.IsNullOrEmpty(urlFlyer) && urlFlyer.Contains("/PUB/"))
                                    {
                                        // CLONACIÓN: Si arrastró una foto pública al borrador, la copiamos a BOR (Sin borrar la original de PUB)
                                        urlFlyer = await AsegurarArchivoEnCarpeta(urlFlyer, $"{sAmbiente}/Iglesias/{model.IdIglesia}/BOR/Eventos");
                                    }

                                    using (var cmdI = new NpgsqlCommand(sqlE, conexion, transaccion))
                                    {
                                        cmdI.Parameters.AddWithValue("@id", model.IdIglesia);
                                        cmdI.Parameters.AddWithValue("@tit", e.Titulo);
                                        cmdI.Parameters.AddWithValue("@f", e.Fecha);
                                        cmdI.Parameters.AddWithValue("@h", e.Horarios);
                                        cmdI.Parameters.AddWithValue("@desc", (object)e.Descripcion ?? DBNull.Value);
                                        cmdI.Parameters.AddWithValue("@img", (object)urlFlyer ?? DBNull.Value);
                                        await cmdI.ExecuteNonQueryAsync();
                                    }
                                }
                            }

                            // D. GALERÍA (INCLUYE FOTOS NUEVAS Y URLs EXTERNAS)
                            using (var cmdD = new NpgsqlCommand("DELETE FROM iciar_iglesias_galeria WHERE iglesia_id = @id AND estado = 'BOR'", conexion, transaccion)) { cmdD.Parameters.AddWithValue("@id", model.IdIglesia); await cmdD.ExecuteNonQueryAsync(); }

                            short ordenGlobal = 1;
                            string sqlG = "INSERT INTO iciar_iglesias_galeria (iglesia_id, estado, url_imagen, orden, titulo) VALUES (@id, 'BOR', @url, @ord, @tit)";

                            // D.1 Fotos Existentes
                            // D.1 Fotos Existentes
                            foreach (var foto in model.Galeria.Where(g => !g.Eliminar).OrderBy(g => g.Orden))
                            {
                                string urlFoto = foto.UrlImagen;

                                // CLONACIÓN: Si arrastró una foto pública al borrador, la copiamos a BOR
                                if (!string.IsNullOrEmpty(urlFoto) && urlFoto.Contains("/PUB/"))
                                {
                                    urlFoto = await AsegurarArchivoEnCarpeta(urlFoto, $"{sAmbiente}/Iglesias/{model.IdIglesia}/BOR/Galeria");
                                }

                                using (var cmdI = new NpgsqlCommand(sqlG, conexion, transaccion))
                                {
                                    cmdI.Parameters.AddWithValue("@id", model.IdIglesia);
                                    cmdI.Parameters.AddWithValue("@url", urlFoto); // <- USAMOS LA URL CLONADA
                                    cmdI.Parameters.AddWithValue("@ord", ordenGlobal++);
                                    cmdI.Parameters.AddWithValue("@tit", (object)foto.Titulo ?? DBNull.Value);
                                    await cmdI.ExecuteNonQueryAsync();
                                }
                            }

                            // D.2 Fotos Nuevas por Archivo (Con sus Títulos)
                            if (model.NuevasImagenesGaleria != null && model.NuevasImagenesGaleria.Any())
                            {
                                var titulosNuevos = Request.Form["NuevasImagenesTitulos"];
                                for (int i = 0; i < model.NuevasImagenesGaleria.Count; i++)
                                {
                                    string urlCloudinary = await ProcesarSubidaCloudinary(model.NuevasImagenesGaleria[i], $"{sAmbiente}/Iglesias/{model.IdIglesia}/BOR/Galeria");
                                    using (var cmdI = new NpgsqlCommand(sqlG, conexion, transaccion))
                                    {
                                        cmdI.Parameters.AddWithValue("@id", model.IdIglesia);
                                        cmdI.Parameters.AddWithValue("@url", urlCloudinary);
                                        cmdI.Parameters.AddWithValue("@ord", ordenGlobal++);
                                        cmdI.Parameters.AddWithValue("@tit", (object)(titulosNuevos.Count > i ? titulosNuevos[i] : null) ?? DBNull.Value);
                                        await cmdI.ExecuteNonQueryAsync();
                                    }
                                }
                            }

                            // D.3 URLs Externas Directas
                            var nuevasUrls = Request.Form["NuevasUrls"];
                            var nuevasUrlsTitulos = Request.Form["NuevasUrlsTitulos"];
                            if (nuevasUrls.Count > 0)
                            {
                                for (int i = 0; i < nuevasUrls.Count; i++)
                                {
                                    if (!string.IsNullOrWhiteSpace(nuevasUrls[i]))
                                    {
                                        using (var cmdI = new NpgsqlCommand(sqlG, conexion, transaccion))
                                        {
                                            cmdI.Parameters.AddWithValue("@id", model.IdIglesia);
                                            cmdI.Parameters.AddWithValue("@url", nuevasUrls[i]);
                                            cmdI.Parameters.AddWithValue("@ord", ordenGlobal++);
                                            cmdI.Parameters.AddWithValue("@tit", (object)(nuevasUrlsTitulos.Count > i ? nuevasUrlsTitulos[i] : null) ?? DBNull.Value);
                                            await cmdI.ExecuteNonQueryAsync();
                                        }
                                    }
                                }
                            }

                            // E. MULTIMEDIA
                            using (var cmdD = new NpgsqlCommand("DELETE FROM iciar_iglesias_multimedia WHERE iglesia_id = @id AND estado = 'BOR'", conexion, transaccion)) { cmdD.Parameters.AddWithValue("@id", model.IdIglesia); await cmdD.ExecuteNonQueryAsync(); }

                            if (model.Multimedia.Any())
                            {
                                short ordenMulti = 1;
                                string sqlM = "INSERT INTO iciar_iglesias_multimedia (iglesia_id, estado, url_embed, orden, titulo) VALUES (@id, 'BOR', @url, @ord, @tit)";
                                foreach (var m in model.Multimedia.OrderBy(x => x.Orden))
                                {
                                    if (!string.IsNullOrWhiteSpace(m.UrlEmbed))
                                    {
                                        using (var cmdI = new NpgsqlCommand(sqlM, conexion, transaccion))
                                        {
                                            cmdI.Parameters.AddWithValue("@id", model.IdIglesia);
                                            cmdI.Parameters.AddWithValue("@url", m.UrlEmbed);
                                            cmdI.Parameters.AddWithValue("@ord", ordenMulti++);
                                            cmdI.Parameters.AddWithValue("@tit", (object)m.Titulo ?? DBNull.Value);
                                            await cmdI.ExecuteNonQueryAsync();
                                        }
                                    }
                                }
                            }

                            // 5. BITÁCORA
                            await Funciones.RegistrarBitacora(conexion, idUsuario, Modulo, Parametros.AccionesBitacora.Editar, $"Guardó borrador WEB de iglesia ID {model.IdIglesia}", ipUsuario, transaccion);

                            await transaccion.CommitAsync();

                            MostrarMensaje("Borrador Guardado", "Tus cambios se guardaron exitosamente. Recuerda que no son públicos hasta que los publiques.", TipoMensaje.Exito);
                        }
                        catch
                        {
                            await transaccion.RollbackAsync();
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error Servidor", "Hubo un problema al procesar los datos: " + ex.Message, TipoMensaje.Error);
            }

            if (salir)
            {
                return RedirectToAction("Index");
            }
            else
            {
                // Redirige a la misma página para refrescar los datos guardados
                return RedirectToAction("Editar", new { sId = model.sIdIglesia });
            }
        }

        private async Task<string> ProcesarSubidaCloudinary(Microsoft.AspNetCore.Http.IFormFile archivo, string folderPath)
        {
            using (var memoryStream = new MemoryStream())
            {
                using (var image = await Image.LoadAsync(archivo.OpenReadStream()))
                {
                    const int MaxWidth = 1200;
                    if (image.Width > MaxWidth)
                    {
                        int newHeight = (int)((double)image.Height / image.Width * MaxWidth);
                        image.Mutate(x => x.Resize(MaxWidth, newHeight));
                    }
                    var encoder = new JpegEncoder { Quality = 80 };
                    await image.SaveAsync(memoryStream, encoder);
                }

                memoryStream.Position = 0;
                var uploadParams = new ImageUploadParams()
                {
                    File = new FileDescription(archivo.FileName, memoryStream),
                    Folder = folderPath,
                    Transformation = new Transformation().FetchFormat("auto")
                };

                var uploadResult = await _cloudinary.UploadAsync(uploadParams);
                return uploadResult.SecureUrl.ToString();
            }
        }

        // =========================================================
        // 5. PUBLICAR DIRECTO DESDE EL PANEL
        // =========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Publicar(string sId)
        {
            if (string.IsNullOrEmpty(sId))
            {
                MostrarMensaje("Error", "No se ha encontrado la página a publicar.", TipoMensaje.Error);
                return RedirectToAction("Index"); 
            }

            int idIglesia = Funciones.DesencriptarId(sId);
            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);
            string ipUsuario = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            if (!await EsAdministradorValido(idIglesia, idUsuario))
            {
                MostrarMensaje("Bloqueado", "No tienes permisos.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // =========================================================================
                    // FASE 0: Hacer lista de todo lo que hay en PUB
                    // =========================================================================
                    var URLsViejasParaBorrar = new List<string>();

                    // Rescatar los flyers de Eventos que están públicos actualmente
                    using (var cmd = new NpgsqlCommand("SELECT imagen_url FROM iciar_iglesias_eventos WHERE iglesia_id = @id AND estado = 'PUB' AND imagen_url IS NOT NULL", conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idIglesia);
                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync()) URLsViejasParaBorrar.Add(reader[0].ToString());
                    }

                    // Rescatar las imágenes de Galería que están públicas actualmente
                    using (var cmd = new NpgsqlCommand("SELECT url_imagen FROM iciar_iglesias_galeria WHERE iglesia_id = @id AND estado = 'PUB' AND url_imagen IS NOT NULL", conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idIglesia);
                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync()) URLsViejasParaBorrar.Add(reader[0].ToString());
                    }


                    // =========================================================================
                    // FASE 1: COPIAR ARCHIVOS A 'PUB' EN CLOUDINARY (Genera IDs nuevos)
                    // =========================================================================
                    var eventosBor = new List<dynamic>();
                    using (var cmd = new NpgsqlCommand("SELECT id, imagen_url FROM iciar_iglesias_eventos WHERE iglesia_id = @id AND estado = 'BOR' AND imagen_url IS NOT NULL", conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idIglesia);
                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync()) eventosBor.Add(new { Id = Convert.ToInt32(reader["id"]), Url = reader["imagen_url"].ToString() });
                    }

                    var mapEventos = new Dictionary<int, string>();
                    foreach (var ev in eventosBor)
                    {
                        // false = NO BORRAR EL ORIGINAL TODAVÍA (Protección contra fallos)
                        string nuevaUrl = await AsegurarArchivoEnCarpeta(ev.Url, $"{sAmbiente}/Iglesias/{idIglesia}/PUB/Eventos");
                        mapEventos.Add(ev.Id, nuevaUrl);
                    }

                    // B. Procesar Galería
                    var galeriaBor = new List<dynamic>();
                    using (var cmd = new NpgsqlCommand("SELECT id, url_imagen FROM iciar_iglesias_galeria WHERE iglesia_id = @id AND estado = 'BOR' AND url_imagen IS NOT NULL", conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idIglesia);
                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync()) galeriaBor.Add(new { Id = Convert.ToInt32(reader["id"]), Url = reader["url_imagen"].ToString() });
                    }

                    var mapGaleria = new Dictionary<int, string>();
                    foreach (var gal in galeriaBor)
                    {
                        string nuevaUrl = await AsegurarArchivoEnCarpeta(gal.Url, $"{sAmbiente}/Iglesias/{idIglesia}/PUB/Galeria");
                        mapGaleria.Add(gal.Id, nuevaUrl);
                    }


                    // =========================================================================
                    // FASE 2: TRANSACCIÓN DE BASE DE DATOS (ATÓMICA Y SEGURA)
                    // =========================================================================
                    bool transaccionExitosa = false;

                    // 0. VERIFICAR QUÉ BORRADORES EXISTEN ANTES DE BORRAR NADA
                    bool hayBorradorDatos = false;
                    bool hayBorradorDiseno = false;

                    using (var cmd = new NpgsqlCommand("SELECT 1 FROM iciar_iglesias_web WHERE iglesia_id = @id AND estado = 'BOR'", conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idIglesia);
                        hayBorradorDatos = await cmd.ExecuteScalarAsync() != null;
                    }

                    using (var cmd = new NpgsqlCommand("SELECT 1 FROM iciar_iglesias_web_diseno WHERE iglesia_id = @id AND estado = 'BOR'", conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idIglesia);
                        hayBorradorDiseno = await cmd.ExecuteScalarAsync() != null;
                    }

                    using (var transaccion = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 1. PUBLICAR DATOS (Solo si se editaron los datos generales)
                            if (hayBorradorDatos)
                            {
                                // Actualizamos URLs en BOR
                                foreach (var kvp in mapEventos)
                                {
                                    using (var cmdU = new NpgsqlCommand("UPDATE iciar_iglesias_eventos SET imagen_url = @url WHERE id = @id", conexion, transaccion))
                                    {
                                        cmdU.Parameters.AddWithValue("@id", kvp.Key);
                                        cmdU.Parameters.AddWithValue("@url", kvp.Value);
                                        await cmdU.ExecuteNonQueryAsync();
                                    }
                                }

                                foreach (var kvp in mapGaleria)
                                {
                                    using (var cmdU = new NpgsqlCommand("UPDATE iciar_iglesias_galeria SET url_imagen = @url WHERE id = @id", conexion, transaccion))
                                    {
                                        cmdU.Parameters.AddWithValue("@id", kvp.Key);
                                        cmdU.Parameters.AddWithValue("@url", kvp.Value);
                                        await cmdU.ExecuteNonQueryAsync();
                                    }
                                }

                                string[] tablas = { "iciar_iglesias_web", "iciar_iglesias_avisos", "iciar_iglesias_eventos", "iciar_iglesias_galeria", "iciar_iglesias_multimedia" };

                                // Borramos PUB actual de datos
                                foreach (var tabla in tablas)
                                {
                                    using (var cmdD = new NpgsqlCommand($"DELETE FROM {tabla} WHERE iglesia_id = @id AND estado = 'PUB'", conexion, transaccion))
                                    {
                                        cmdD.Parameters.AddWithValue("@id", idIglesia);
                                        await cmdD.ExecuteNonQueryAsync();
                                    }
                                }

                                // Insertamos desde BOR
                                string sqlCopyWeb = @"INSERT INTO iciar_iglesias_web (iglesia_id, estado, activa, plantilla_id, slug, facebook_url, instagram_url, youtube_url, whatsapp, telefono, historia, fecha_actualizacion) SELECT iglesia_id, 'PUB', TRUE, plantilla_id, slug, facebook_url, instagram_url, youtube_url, whatsapp, telefono, historia, NOW() FROM iciar_iglesias_web WHERE iglesia_id = @id AND estado = 'BOR'";
                                using (var cmd = new NpgsqlCommand(sqlCopyWeb, conexion, transaccion)) { cmd.Parameters.AddWithValue("@id", idIglesia); await cmd.ExecuteNonQueryAsync(); }

                                string sqlCopyAvisos = @"INSERT INTO iciar_iglesias_avisos (iglesia_id, estado, titulo, descripcion, fecha_inicio, fecha_expiracion) SELECT iglesia_id, 'PUB', titulo, descripcion, fecha_inicio, fecha_expiracion FROM iciar_iglesias_avisos WHERE iglesia_id = @id AND estado = 'BOR'";
                                using (var cmd = new NpgsqlCommand(sqlCopyAvisos, conexion, transaccion)) { cmd.Parameters.AddWithValue("@id", idIglesia); await cmd.ExecuteNonQueryAsync(); }

                                string sqlCopyEventos = @"INSERT INTO iciar_iglesias_eventos (iglesia_id, estado, titulo, fecha, horarios, descripcion, imagen_url) SELECT iglesia_id, 'PUB', titulo, fecha, horarios, descripcion, imagen_url FROM iciar_iglesias_eventos WHERE iglesia_id = @id AND estado = 'BOR'";
                                using (var cmd = new NpgsqlCommand(sqlCopyEventos, conexion, transaccion)) { cmd.Parameters.AddWithValue("@id", idIglesia); await cmd.ExecuteNonQueryAsync(); }

                                string sqlCopyGaleria = @"INSERT INTO iciar_iglesias_galeria (iglesia_id, estado, url_imagen, orden, titulo) SELECT iglesia_id, 'PUB', url_imagen, orden, titulo FROM iciar_iglesias_galeria WHERE iglesia_id = @id AND estado = 'BOR'";
                                using (var cmd = new NpgsqlCommand(sqlCopyGaleria, conexion, transaccion)) { cmd.Parameters.AddWithValue("@id", idIglesia); await cmd.ExecuteNonQueryAsync(); }

                                string sqlCopyMulti = @"INSERT INTO iciar_iglesias_multimedia (iglesia_id, estado, url_embed, orden, titulo) SELECT iglesia_id, 'PUB', url_embed, orden, titulo FROM iciar_iglesias_multimedia WHERE iglesia_id = @id AND estado = 'BOR'";
                                using (var cmd = new NpgsqlCommand(sqlCopyMulti, conexion, transaccion)) { cmd.Parameters.AddWithValue("@id", idIglesia); await cmd.ExecuteNonQueryAsync(); }

                                // Limpiamos los rastros del BOR en la Base de Datos
                                foreach (var tabla in tablas)
                                {
                                    using (var cmdD = new NpgsqlCommand($"DELETE FROM {tabla} WHERE iglesia_id = @id AND estado = 'BOR'", conexion, transaccion))
                                    {
                                        cmdD.Parameters.AddWithValue("@id", idIglesia);
                                        await cmdD.ExecuteNonQueryAsync();
                                    }
                                }
                            }

                            // 2. PUBLICAR DISEÑO (NUEVO: Solo si se editó el diseño modular)
                            if (hayBorradorDiseno)
                            {
                                // Borrar el diseño público anterior
                                using (var cmdD = new NpgsqlCommand("DELETE FROM iciar_iglesias_web_diseno WHERE iglesia_id = @id AND estado = 'PUB'", conexion, transaccion))
                                {
                                    cmdD.Parameters.AddWithValue("@id", idIglesia);
                                    await cmdD.ExecuteNonQueryAsync();
                                }

                                // Insertar el borrador de diseño como público (Ajusta 'configuracion_json' por el nombre real de tu columna donde guardas el JSON)
                                string sqlCopyDiseno = @"INSERT INTO iciar_iglesias_web_diseno (iglesia_id, estado, configuracion_json, fecha_actualizacion) 
                                                 SELECT iglesia_id, 'PUB', configuracion_json, NOW() 
                                                 FROM iciar_iglesias_web_diseno 
                                                 WHERE iglesia_id = @id AND estado = 'BOR'";
                                using (var cmd = new NpgsqlCommand(sqlCopyDiseno, conexion, transaccion))
                                {
                                    cmd.Parameters.AddWithValue("@id", idIglesia);
                                    await cmd.ExecuteNonQueryAsync();
                                }

                                // Borrar el borrador de diseño
                                using (var cmdD = new NpgsqlCommand("DELETE FROM iciar_iglesias_web_diseno WHERE iglesia_id = @id AND estado = 'BOR'", conexion, transaccion))
                                {
                                    cmdD.Parameters.AddWithValue("@id", idIglesia);
                                    await cmdD.ExecuteNonQueryAsync();
                                }
                            }

                            // --- ACTUALIZAR EL SLUG EN LA TABLA PRINCIPAL AL PUBLICAR ---
                            string sqlUpdateMain = @"
                                UPDATE iciar_iglesias 
                                SET slug = COALESCE(
                                    NULLIF((SELECT slug FROM iciar_iglesias_web WHERE iglesia_id = @id AND estado = 'PUB'), ''), 
                                    slug -- Si es nulo o vacío, se queda con el que ya tenía
                                ) 
                                WHERE id = @id";

                            using (var cmdMain = new NpgsqlCommand(sqlUpdateMain, conexion, transaccion))
                            {
                                cmdMain.Parameters.AddWithValue("@id", idIglesia);
                                await cmdMain.ExecuteNonQueryAsync();
                            }

                            await Funciones.RegistrarBitacora(conexion, idUsuario, Modulo, Parametros.AccionesBitacora.Editar, $"Página WEB de iglesia ID {idIglesia} publicada", ipUsuario, transaccion);
                            await transaccion.CommitAsync();

                            // ========================================================================
                            // --- ALERTA POR CORREO: WEB PUBLICADA (MODERACIÓN) ---
                            // ========================================================================
                            try
                            {
                                string nombreIglesia = "";
                                string slugIglesia = "";
                                // Aquí usamos ClaimTypes.Name
                                string nombreUsuario = User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "Un administrador";

                                // Obtenemos el nombre y slug final para armar el link directo
                                using (var cmdName = new NpgsqlCommand("SELECT nombre, slug FROM iciar_iglesias WHERE id = @id", conexion))
                                {
                                    cmdName.Parameters.AddWithValue("@id", idIglesia);
                                    using (var reader = await cmdName.ExecuteReaderAsync())
                                    {
                                        if (await reader.ReadAsync())
                                        {
                                            nombreIglesia = reader["nombre"].ToString();
                                            slugIglesia = reader["slug"]?.ToString() ?? "";
                                        }
                                    }
                                }

                                // Ruta directa apuntando al WebController y su método Index
                                string urlPublica = Url.Action("Index", "Web", new { slug = slugIglesia }, Request.Scheme);

                                string htmlPublicada = $@"
                                    <div style='font-family: Arial, Helvetica, sans-serif; max-width: 600px; margin: 0 auto; border: 1px solid #e0e0e0; border-radius: 8px; overflow: hidden; box-shadow: 0 4px 6px rgba(0,0,0,0.05);'>
                                        <div style='background-color: #198754; padding: 20px; text-align: center; color: #ffffff;'>
                                            <h2 style='margin: 0; font-size: 22px; font-weight: 600;'>🌐 Nueva Página Publicada</h2>
                                        </div>
                                        <div style='padding: 30px; background-color: #ffffff; color: #333333;'>
                                            <p style='font-size: 16px; margin-top: 0;'>Hola, <strong>Equipo Moderador</strong>:</p>
                                            <p style='font-size: 16px; line-height: 1.6;'>Una iglesia acaba de publicar su página web y ya es visible en el directorio público.</p>

                                            <div style='background-color: #f8f9fa; border-left: 5px solid #198754; padding: 18px; margin: 25px 0; border-radius: 4px;'>
                                                <ul style='margin: 0; padding-left: 20px; line-height: 1.8; font-size: 15px;'>
                                                    <li><strong>Iglesia:</strong> {nombreIglesia}</li>
                                                    <li><strong>ID Iglesia:</strong> #{idIglesia}</li>
                                                    <li><strong>Publicado por:</strong> {nombreUsuario} (ID: {idUsuario})</li>
                                                </ul>
                                            </div>
            
                                            <p style='font-size: 15px; color: #555555; line-height: 1.5;'>
                                                Como medida de seguridad, te sugerimos realizar una auditoría rápida para verificar que los textos, imágenes e información cumplan con los lineamientos de la institución.
                                            </p>

                                            <div style='text-align: center; margin-top: 30px; margin-bottom: 10px;'>
                                                <a href='{urlPublica}' target='_blank' style='background-color: #0d6efd; color: #ffffff; padding: 14px 30px; text-decoration: none; border-radius: 6px; font-weight: bold; font-size: 15px; display: inline-block;'>
                                                    👀 Auditar Página Pública
                                                </a>
                                            </div>
                                        </div>
                                    </div>";

                                await Funciones.EnviarAlertaPorBaseDatos(_configuration, "WEB_IGLESIA_PUBLICADA", $"Auditoría Requerida: Web Publicada ({nombreIglesia})", htmlPublicada);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error al enviar alerta de web publicada: {ex.Message}");
                            }

                            transaccionExitosa = true;
                        }
                        catch
                        {
                            await transaccion.RollbackAsync();
                            throw;
                        }
                    }

                    // =========================================================================
                    // FASE 3: LIMPIEZA TOTAL (Sólo si la BD se guardó bien)
                    // =========================================================================
                    if (transaccionExitosa)
                    {
                        // 1. Borrar carpeta BOR masivamente (Esto se queda igual, está correcto)
                        try
                        {
                            string prefijoBor = $"{sAmbiente}/Iglesias/{idIglesia}/BOR/";
                            await _cloudinary.DeleteResourcesAsync(new DelResParams() { Prefix = prefijoBor, Type = "upload" });
                            try { await _cloudinary.DeleteFolderAsync(prefijoBor); } catch { }
                        }
                        catch { }

                        // 2. Borrar las fotos viejas usando el método seguro individual
                        foreach (var urlVieja in URLsViejasParaBorrar)
                        {
                            if (urlVieja.Contains("res.cloudinary.com") && urlVieja.Contains("/PUB/"))
                            {
                                await BorrarArchivoCloudinary(urlVieja);
                            }
                        }

                        MostrarMensaje("¡Página Publicada!", "Los cambios se movieron al entorno público exitosamente.", TipoMensaje.Exito);
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "Ocurrió un error al publicar: " + ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Index");
        }
        private async Task<string> AsegurarArchivoEnCarpeta(string urlOriginal, string carpetaDestino)
        {
            // Si no es un enlace de Cloudinary o está vacío, lo dejamos intacto
            if (string.IsNullOrEmpty(urlOriginal) || !urlOriginal.Contains("res.cloudinary.com"))
                return urlOriginal;

            // Si ya se encuentra en la carpeta de destino correcta, no hacemos nada extra
            if (urlOriginal.Contains($"/{carpetaDestino}/"))
                return urlOriginal;

            try
            {
                using (var httpClient = new System.Net.Http.HttpClient())
                {
                    var response = await httpClient.GetAsync(urlOriginal);

                    if (!response.IsSuccessStatusCode)
                        throw new Exception($"HTTP {response.StatusCode} al descargar imagen original.");

                    using (var networkStream = await response.Content.ReadAsStreamAsync())
                    using (var memoryStream = new MemoryStream())
                    {
                        // CRUCIAL: Pasamos el archivo a la RAM para que Cloudinary pueda medirlo
                        await networkStream.CopyToAsync(memoryStream);
                        memoryStream.Position = 0;

                        // Intentar mantener la extensión original (.jpg, .png)
                        string nombreArchivo = "imagen_copiada";
                        string ext = Path.GetExtension(new Uri(urlOriginal).AbsolutePath);
                        if (!string.IsNullOrEmpty(ext)) nombreArchivo += ext;

                        var uploadParams = new ImageUploadParams()
                        {
                            File = new FileDescription(nombreArchivo, memoryStream),
                            Folder = carpetaDestino,
                            Overwrite = true
                        };

                        var uploadResult = await _cloudinary.UploadAsync(uploadParams);

                        if (uploadResult.Error != null)
                            throw new Exception(uploadResult.Error.Message);

                        if (uploadResult.StatusCode == System.Net.HttpStatusCode.OK)
                            return uploadResult.SecureUrl.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                // SI FALLA LA COPIA, DETENEMOS TODO EL PROCESO LANZANDO UN ERROR CRÍTICO
                throw new Exception($"Fallo al procesar imagen en Cloudinary: {ex.Message}");
            }

            throw new Exception("Error desconocido al copiar la imagen a " + carpetaDestino);
        }
        private async Task BorrarArchivoCloudinary(string urlOriginal)
        {
            if (string.IsNullOrEmpty(urlOriginal)) return;

            try
            {
                var uri = new Uri(urlOriginal);
                var segments = uri.Segments;
                int uploadIndex = Array.IndexOf(segments, "upload/");

                if (uploadIndex >= 0 && segments.Length > uploadIndex + 2)
                {
                    int startIndex = uploadIndex + 1;
                    if (segments[startIndex].StartsWith("v") && segments.Length > startIndex + 1) startIndex++;

                    string publicIdWithExtension = string.Join("", segments.Skip(startIndex));
                    string publicId = Uri.UnescapeDataString(Path.ChangeExtension(publicIdWithExtension, null).Trim('/'));

                    await _cloudinary.DestroyAsync(new DeletionParams(publicId));
                }
            }
            catch (Exception ex) { Console.WriteLine($"Error Cloudinary: {ex.Message}"); }
        }

        private async Task CargarPlantillasParaViewBag()
        {
            var lista = new List<dynamic>();
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    // 1. Agregamos es_personalizable a la consulta SQL
                    string sql = "SELECT id, nombre, descripcion, ruta_vista, es_personalizable FROM iciar_plantillas_web WHERE activa = TRUE ORDER BY id ASC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            lista.Add(new
                            {
                                Id = Convert.ToInt32(reader["id"]),
                                Nombre = reader["nombre"].ToString(),
                                Imagen = reader["ruta_vista"].ToString().ToLower(),
                                Descripcion = reader["descripcion"]?.ToString(),
                                // 2. Agregamos la propiedad al objeto anónimo (manejando DBNull por si acaso)
                                EsPersonalizable = reader["es_personalizable"] != DBNull.Value && Convert.ToBoolean(reader["es_personalizable"])
                            });
                        }
                    }
                }
            }
            catch { }
            ViewBag.Plantillas = lista;
        }

        private async Task<string> PromoverArchivoCloudinary(string urlOriginal, string estadoDestino)
        {
            if (string.IsNullOrEmpty(urlOriginal) || !urlOriginal.Contains("/BOR/")) return urlOriginal;

            try
            {
                var uri = new Uri(urlOriginal);
                var segments = uri.Segments;
                int uploadIndex = Array.IndexOf(segments, "upload/");

                if (uploadIndex >= 0 && segments.Length > uploadIndex + 2)
                {
                    int startIndex = uploadIndex + 1;
                    if (segments[startIndex].StartsWith("v") && segments.Length > startIndex + 1) startIndex++;

                    string publicIdWithExtension = string.Join("", segments.Skip(startIndex));
                    string oldPublicId = Uri.UnescapeDataString(Path.ChangeExtension(publicIdWithExtension, null).Trim('/'));
                    string newPublicId = oldPublicId.Replace("/BOR/", $"/{estadoDestino}/");

                    var renameParams = new RenameParams(oldPublicId, newPublicId) { Overwrite = true };
                    var renameResult = await _cloudinary.RenameAsync(renameParams);

                    if (renameResult.StatusCode == System.Net.HttpStatusCode.OK) return renameResult.SecureUrl.ToString();
                }
            }
            catch (Exception ex) { Console.WriteLine($"Error renombrando en Cloudinary: {ex.Message}"); }

            return urlOriginal;
        }
        // =========================================================
        // 6. CONSTRUCTOR VISUAL MODULAR (WIX-LIKE)
        // =========================================================

        [HttpGet]
        public async Task<IActionResult> Constructor(string sId)
        {
            if (string.IsNullOrEmpty(sId))
            {
                MostrarMensaje("Error", "No se ha encontrado la página a diseñar.", TipoMensaje.Error);
                return RedirectToAction("Index"); 
            }

            int idIglesia = Funciones.DesencriptarId(sId);
            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);

            if (!await EsAdministradorValido(idIglesia, idUsuario))
            {
                MostrarMensaje("Bloqueado", "No tienes permisos para acceder al constructor.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            ViewBag.sIdIglesia = sId;
            ViewBag.IdIglesia = idIglesia;

            // 1. CREAMOS EL MODELO QUE LA VISTA ESTÁ ESPERANDO
            var modelo = new IglesiaWebPublicaViewModel();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 👇 2. OBTENEMOS EL NOMBRE DE LA IGLESIA PARA LA BARRA SUPERIOR 👇
                    using (var cmdNom = new NpgsqlCommand("SELECT nombre FROM iciar_iglesias WHERE id = @id", conexion))
                    {
                        cmdNom.Parameters.AddWithValue("@id", idIglesia);
                        var res = await cmdNom.ExecuteScalarAsync();
                        modelo.NombreIglesia = res?.ToString() ?? "Constructor Visual";
                    }

                    // 3. Obtener todos los diseños disponibles
                    string sqlDis = "SELECT * FROM iciar_iglesias_secciones_disenos WHERE activa = TRUE ORDER BY id ASC";
                    var todosLosDisenos = new List<dynamic>();
                    using (var cmdDis = new NpgsqlCommand(sqlDis, conexion))
                    using (var readerDis = await cmdDis.ExecuteReaderAsync())
                    {
                        while (await readerDis.ReadAsync())
                        {
                            todosLosDisenos.Add(new
                            {
                                SeccionId = Convert.ToInt32(readerDis["seccion_id"]),
                                Nombre = readerDis["nombre"].ToString(),
                                Vista = readerDis["ruta_vista"].ToString(),
                                Descripcion = readerDis["descripcion"].ToString()
                            });
                        }
                    }

                    // 4. Obtener el Catálogo de Secciones y anidar los diseños
                    var catalogo = new List<object>();
                    string sqlCat = "SELECT * FROM iciar_iglesias_secciones WHERE activa = TRUE ORDER BY orden_sugerido ASC";
                    using (var cmdCat = new NpgsqlCommand(sqlCat, conexion))
                    using (var readerCat = await cmdCat.ExecuteReaderAsync())
                    {
                        while (await readerCat.ReadAsync())
                        {
                            int secId = Convert.ToInt32(readerCat["id"]);
                            catalogo.Add(new
                            {
                                Id = secId,
                                Nombre = readerCat["nombre"].ToString(),
                                Identificador = readerCat["identificador"].ToString(),
                                Disenos = todosLosDisenos.Where(d => d.SeccionId == secId).ToList()
                            });
                        }
                    }

                    ViewBag.CatalogoJson = System.Text.Json.JsonSerializer.Serialize(catalogo);

                    // 5. Extraer la configuración actual (Borrador si existe, sino Público, sino default)
                    string jsonConfig = null;
                    string sqlConfig = "SELECT configuracion_json FROM iciar_iglesias_web_diseno WHERE iglesia_id = @id ORDER BY CASE WHEN estado = 'BOR' THEN 1 ELSE 2 END LIMIT 1";
                    using (var cmdConf = new NpgsqlCommand(sqlConfig, conexion))
                    {
                        cmdConf.Parameters.AddWithValue("@id", idIglesia);
                        var result = await cmdConf.ExecuteScalarAsync();
                        if (result != null) jsonConfig = result.ToString();
                    }

                    ViewBag.ConfiguracionActual = string.IsNullOrEmpty(jsonConfig) ? "{}" : jsonConfig;
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "No se pudo inicializar el constructor: " + ex.Message, TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            // 6. ENVIAMOS EL MODELO A LA VISTA
            return View(modelo);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GuardarConstructor(string sId, string configuracionJson)
        {
            if (string.IsNullOrEmpty(sId) || string.IsNullOrEmpty(configuracionJson))
                return Json(new { success = false, message = "Datos inválidos." });

            int idIglesia = Funciones.DesencriptarId(sId);
            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);

            if (!await EsAdministradorValido(idIglesia, idUsuario))
                return Json(new { success = false, message = "Permiso denegado." });

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // PASO 1: Intentamos actualizar el Borrador (BOR) si es que ya existe
                    string sqlUpdate = @"
                UPDATE iciar_iglesias_web_diseno 
                SET configuracion_json = @json::jsonb, 
                    fecha_actualizacion = NOW() 
                WHERE iglesia_id = @id AND estado = 'BOR'";

                    int filasAfectadas = 0;
                    using (var cmdUpdate = new NpgsqlCommand(sqlUpdate, conexion))
                    {
                        cmdUpdate.Parameters.AddWithValue("@id", idIglesia);
                        cmdUpdate.Parameters.AddWithValue("@json", configuracionJson);
                        filasAfectadas = await cmdUpdate.ExecuteNonQueryAsync();
                    }

                    // PASO 2: Si 0 filas fueron afectadas, significa que no existía el BOR. Entonces lo insertamos.
                    if (filasAfectadas == 0)
                    {
                        string sqlInsert = @"
                    INSERT INTO iciar_iglesias_web_diseno (iglesia_id, estado, configuracion_json, fecha_actualizacion) 
                    VALUES (@id, 'BOR', @json::jsonb, NOW())";

                        using (var cmdInsert = new NpgsqlCommand(sqlInsert, conexion))
                        {
                            cmdInsert.Parameters.AddWithValue("@id", idIglesia);
                            cmdInsert.Parameters.AddWithValue("@json", configuracionJson);
                            await cmdInsert.ExecuteNonQueryAsync();
                        }
                    }
                }

                return Json(new { success = true, message = "Diseño guardado en borrador correctamente." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DescartarDatos(string sId)
        {
            if (string.IsNullOrEmpty(sId))
            {
                MostrarMensaje("Error", "No se ha encontrado la página a descartar cambios.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            int idIglesia = Funciones.DesencriptarId(sId);
            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);

            if (!await EsAdministradorValido(idIglesia, idUsuario))
            {
                MostrarMensaje("Bloqueado", "No tienes permisos.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var transaccion = await conexion.BeginTransactionAsync())
                    {
                        // Solo borramos los BOR de las tablas de contenido/datos
                        string[] tablas = { "iciar_iglesias_web", "iciar_iglesias_avisos", "iciar_iglesias_eventos", "iciar_iglesias_galeria", "iciar_iglesias_multimedia" };

                        foreach (var tabla in tablas)
                        {
                            using (var cmdD = new NpgsqlCommand($"DELETE FROM {tabla} WHERE iglesia_id = @id AND estado = 'BOR'", conexion, transaccion))
                            {
                                cmdD.Parameters.AddWithValue("@id", idIglesia);
                                await cmdD.ExecuteNonQueryAsync();
                            }
                        }

                        await transaccion.CommitAsync();
                    }
                }

                // Limpieza de imágenes huérfanas en Cloudinary (Carpeta BOR)
                try
                {
                    // Nota: Asegúrate de que la variable sAmbiente esté disponible aquí, igual que en tu método Publicar()
                    string prefijoBor = $"{sAmbiente}/Iglesias/{idIglesia}/BOR/";
                    await _cloudinary.DeleteResourcesAsync(new DelResParams() { Prefix = prefijoBor, Type = "upload" });
                    try { await _cloudinary.DeleteFolderAsync(prefijoBor); } catch { }
                }
                catch { }

                MostrarMensaje("Borrador Descartado", "Se descartaron los cambios de contenido.", TipoMensaje.Exito);
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "Ocurrió un error al descartar datos: " + ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Index");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DescartarDiseno(string sId)
        {
            if (string.IsNullOrEmpty(sId)) return RedirectToAction("Index");

            int idIglesia = Funciones.DesencriptarId(sId);
            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);

            if (!await EsAdministradorValido(idIglesia, idUsuario))
            {
                MostrarMensaje("Bloqueado", "No tienes permisos.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // Eliminamos SOLAMENTE el borrador del diseño
                    using (var cmd = new NpgsqlCommand("DELETE FROM iciar_iglesias_web_diseno WHERE iglesia_id = @id AND estado = 'BOR'", conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idIglesia);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                MostrarMensaje("Diseño Descartado", "Se descartaron los cambios de personalización visual.", TipoMensaje.Exito);
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "Ocurrió un error al descartar el diseño: " + ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Index");
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> RenderizarModuloDemo(string sId, string identificador, string vista)
        {
            // Volvemos a hacer la función asíncrona para poder consultar la BD real
            int idIglesia = Funciones.DesencriptarId(sId);

            // Obtenemos los datos reales de la iglesia
            var modelo = await ObtenerModeloParaPreview(idIglesia);

            try
            {
                return PartialView($"~/Views/web/Plantillas/Modulos/{identificador}/{vista}.cshtml", modelo);
            }
            catch (Exception ex)
            {
                return Content($"<div class='alert alert-danger m-3'>Error al cargar módulo <b>{vista}</b>: {ex.Message}</div>");
            }
        }

        // Método Auxiliar Híbrido: Trae datos reales y rellena los vacíos con demos
        private async Task<IglesiaWebPublicaViewModel> ObtenerModeloParaPreview(int idIglesia)
        {
            var modelo = new IglesiaWebPublicaViewModel();
            string estadoLectura = "PUB"; // Prioridad a Borrador, luego Público

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // 1. DATOS BASE REALES (Nombre, Municipio, Horarios)
                    string sqlBase = "SELECT i.nombre, i.horarios, m.nombre as municipio FROM iciar_iglesias i LEFT JOIN iciar_municipios m ON i.municipio_id = m.id WHERE i.id = @id";
                    using (var cmdNom = new NpgsqlCommand(sqlBase, conexion))
                    {
                        cmdNom.Parameters.AddWithValue("@id", idIglesia);
                        using (var reader = await cmdNom.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                modelo.NombreIglesia = reader["nombre"].ToString();
                                modelo.Horarios = reader["horarios"]?.ToString();
                                modelo.Municipio = reader["municipio"]?.ToString();
                            }
                        }
                    }

                    // 2. DATOS WEB REALES (Historia, Redes, Teléfonos)
                    string sqlWeb = "SELECT * FROM iciar_iglesias_web WHERE iglesia_id = @id ORDER BY CASE WHEN estado = 'BOR' THEN 1 ELSE 2 END LIMIT 1";
                    using (var cmdWeb = new NpgsqlCommand(sqlWeb, conexion))
                    {
                        cmdWeb.Parameters.AddWithValue("@id", idIglesia);
                        using (var reader = await cmdWeb.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                estadoLectura = reader["estado"].ToString();
                                modelo.FacebookUrl = reader["facebook_url"]?.ToString();
                                modelo.InstagramUrl = reader["instagram_url"]?.ToString();
                                modelo.YoutubeUrl = reader["youtube_url"]?.ToString();
                                modelo.Whatsapp = reader["whatsapp"]?.ToString();
                                modelo.Telefono = reader["telefono"]?.ToString();
                                modelo.Historia = reader["historia"]?.ToString();
                            }
                        }
                    }

                    // 3. LISTAS REALES (Avisos, Eventos, Galería, Multimedia)
                    using (var cmd = new NpgsqlCommand("SELECT * FROM iciar_iglesias_avisos WHERE iglesia_id = @id AND estado = @est", conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idIglesia); cmd.Parameters.AddWithValue("@est", estadoLectura);
                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync()) modelo.Avisos.Add(new AvisoWebViewModel { Titulo = reader["titulo"].ToString(), Descripcion = reader["descripcion"]?.ToString(), FechaExpiracion = Convert.ToDateTime(reader["fecha_expiracion"]) });
                    }

                    using (var cmd = new NpgsqlCommand("SELECT * FROM iciar_iglesias_eventos WHERE iglesia_id = @id AND estado = @est", conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idIglesia); cmd.Parameters.AddWithValue("@est", estadoLectura);
                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync()) modelo.Eventos.Add(new EventoWebViewModel { Titulo = reader["titulo"].ToString(), Fecha = Convert.ToDateTime(reader["fecha"]), Horarios = reader["horarios"].ToString(), Descripcion = reader["descripcion"]?.ToString(), ImagenUrl = reader["imagen_url"]?.ToString() });
                    }

                    using (var cmd = new NpgsqlCommand("SELECT * FROM iciar_iglesias_galeria WHERE iglesia_id = @id AND estado = @est ORDER BY orden", conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idIglesia); cmd.Parameters.AddWithValue("@est", estadoLectura);
                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync()) modelo.Galeria.Add(new GaleriaWebViewModel { Titulo = reader["titulo"]?.ToString(), UrlImagen = reader["url_imagen"].ToString() });
                    }

                    using (var cmd = new NpgsqlCommand("SELECT * FROM iciar_iglesias_multimedia WHERE iglesia_id = @id AND estado = @est ORDER BY orden", conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idIglesia); cmd.Parameters.AddWithValue("@est", estadoLectura);
                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync()) modelo.Multimedia.Add(new MultimediaWebViewModel { Titulo = reader["titulo"]?.ToString(), UrlEmbed = reader["url_embed"].ToString() });
                    }
                }
            }
            catch { } // Si algo falla en BD, se ignora y se usan los fallbacks de abajo

            // ====================================================================
            // RELLENO DE SEGURIDAD (FALLBACKS)
            // Solo si el usuario NO tiene datos registrados en una sección, 
            // inyectamos un elemento demo para que el módulo en pantalla no se vea invisible.
            // ====================================================================

            if (string.IsNullOrWhiteSpace(modelo.Historia))
                modelo.Historia = "<span class='text-muted fst-italic'>Tu historia se mostrará aquí. (Puedes editarla desde el panel principal).</span>";

            if (!modelo.Avisos.Any())
            {
                modelo.Avisos.Add(new AvisoWebViewModel { Titulo = "Ayuno Congregacional", Descripcion = "Iniciamos nuestro ayuno mensual. Trae tu petición escrita.", FechaInicio = DateTime.Now, FechaExpiracion = DateTime.Now.AddDays(3) });
                modelo.Avisos.Add(new AvisoWebViewModel { Titulo = "Reunión de Células", Descripcion = "Este jueves nos reunimos en las casas. Contacta a tu líder de red.", FechaInicio = DateTime.Now, FechaExpiracion = DateTime.Now.AddDays(7) });
            }

            if (!modelo.Eventos.Any())
            {
                modelo.Eventos.Add(new EventoWebViewModel { Titulo = "Noche de Adoración", Fecha = DateTime.Now.AddDays(15), Horarios = "Viernes 6:00 PM", Descripcion = "Un tiempo especial diseñado para exaltar el nombre de Jesús. Tendremos bandas invitadas.", ImagenUrl = "/Images/plantillas/demo/demo-evento-1.jpg" });
                modelo.Eventos.Add(new EventoWebViewModel { Titulo = "Seminario de Liderazgo", Fecha = DateTime.Now.AddDays(30), Horarios = "Sábado 9:00 AM a 2:00 PM", Descripcion = "Equipando a la próxima generación de servidores. Aprende principios bíblicos para liderar.", ImagenUrl = "/Images/plantillas/demo/demo-evento-2.jpg" });
            }

            if (!modelo.Galeria.Any())
            {
                modelo.Galeria.Add(new GaleriaWebViewModel { Titulo = "Nuestro Servicio Dominical", UrlImagen = "/Images/plantillas/demo/demo-galeria-1.jpg", Orden = 1 });
                modelo.Galeria.Add(new GaleriaWebViewModel { Titulo = "Bautizos en Agua", UrlImagen = "/Images/plantillas/demo/demo-galeria-2.jpg", Orden = 2 });
                modelo.Galeria.Add(new GaleriaWebViewModel { Titulo = "Generación de Niños", UrlImagen = "/Images/plantillas/demo/demo-galeria-3.jpg", Orden = 3 });
                modelo.Galeria.Add(new GaleriaWebViewModel { Titulo = "Ministerio de Alabanza", UrlImagen = "/Images/plantillas/demo/demo-galeria-4.jpg", Orden = 4 });
            }

            if (!modelo.Multimedia.Any())
            {
                modelo.Multimedia.Add(new MultimediaWebViewModel { Titulo = "Última Predicación Dominical", UrlEmbed = "https://www.youtube.com/embed/ScMzIvxBSi4", Orden = 1 });
            }

            if (string.IsNullOrWhiteSpace(modelo.NombreIglesia))
                modelo.NombreIglesia = "Nombre de Tu Iglesia";

            if (string.IsNullOrWhiteSpace(modelo.Municipio))
                modelo.Municipio = "Ciudad de Prueba";

            if (string.IsNullOrWhiteSpace(modelo.DireccionCompleta))
                modelo.DireccionCompleta = "Av. Principal #123, Col. Centro, C.P. 00000";

            if (string.IsNullOrWhiteSpace(modelo.Horarios))
                modelo.Horarios = "Domingos 10:00 AM y 6:00 PM | Miércoles 7:00 PM";

            if (string.IsNullOrWhiteSpace(modelo.MapaUrl))
                modelo.MapaUrl = "https://www.google.com/maps/embed?pb=!1m14!1m12!1m3!1d15053.585502693952!2d-99.133208!3d19.4326077!2m3!1f0!2f0!3f0!3m2!1i1024!2i768!4f13.1!5e0!3m2!1ses-419!2smx!4v1700000000000!5m2!1ses-419!2smx";

            if (!modelo.Latitud.HasValue)
                modelo.Latitud = 19.4326077m; // Coordenadas genéricas por defecto

            if (!modelo.Longitud.HasValue)
                modelo.Longitud = -99.133208m;

            // --- Datos Web y Redes Sociales ---
            if (string.IsNullOrWhiteSpace(modelo.FacebookUrl))
                modelo.FacebookUrl = "https://facebook.com/";

            if (string.IsNullOrWhiteSpace(modelo.InstagramUrl))
                modelo.InstagramUrl = "https://instagram.com/";

            if (string.IsNullOrWhiteSpace(modelo.YoutubeUrl))
                modelo.YoutubeUrl = "https://youtube.com/";

            if (string.IsNullOrWhiteSpace(modelo.Whatsapp))
                modelo.Whatsapp = "5500000000";

            if (string.IsNullOrWhiteSpace(modelo.Telefono))
                modelo.Telefono = "(55) 1234 5678";

            if (string.IsNullOrWhiteSpace(modelo.Historia))
                modelo.Historia = "<p class='text-muted'>Aquí se mostrará la historia de la iglesia. <em>(Este es un texto de prueba para el constructor visual. Puedes editarlo desde tu panel de control).</em></p>";

            if (string.IsNullOrWhiteSpace(modelo.PlantillaVista))
                modelo.PlantillaVista = "Default";

            return modelo;
        }
        // =========================================================
        // MÉTODOS AUXILIARES PARA EL SLUG
        // =========================================================
        private string NormalizarParaSlug(string texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return "";

            // Quitar acentos
            var textoNormalizado = texto.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder();
            foreach (var c in textoNormalizado)
            {
                var categoria = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
                if (categoria != System.Globalization.UnicodeCategory.NonSpacingMark)
                {
                    if (char.IsLetterOrDigit(c))
                        sb.Append(char.ToLowerInvariant(c));
                    else if (c == ' ') // Cambiar espacios por guiones
                        sb.Append('-');
                }
            }

            // Evitar guiones dobles y limpiar bordes
            string slug = sb.ToString();
            while (slug.Contains("--")) slug = slug.Replace("--", "-");
            return slug.Trim('-');
        }

        private async Task<string> GenerarSlugUnicoAsync(string nombre, string municipio, int idIglesia, NpgsqlConnection conexion)
        {
            // Regla 1: Solo el nombre (ej: "getsemani")
            string slugBase = NormalizarParaSlug(nombre);
            if (!await ExisteSlugAcaAsync(slugBase, idIglesia, conexion))
                return slugBase;

            // Regla 2: Nombre + Municipio (ej: "getsemani-mpio-actopan")
            string mpioLimpio = NormalizarParaSlug(municipio);
            string slugConMpio = $"{slugBase}-mpio-{mpioLimpio}";
            if (!await ExisteSlugAcaAsync(slugConMpio, idIglesia, conexion))
                return slugConMpio;

            // Regla 3: Nombre + Municipio + Número secuencial (ej: "getsemani-mpio-actopan-2")
            int contador = 2;
            while (true)
            {
                string slugNumerico = $"{slugConMpio}-{contador}";
                if (!await ExisteSlugAcaAsync(slugNumerico, idIglesia, conexion))
                    return slugNumerico;
                contador++;
            }
        }

        private async Task<bool> ExisteSlugAcaAsync(string slug, int idIglesia, NpgsqlConnection conexion)
        {
            // Verificamos colisiones en ambas tablas como ya lo habíamos planteado
            string sql = @"
        SELECT 1 FROM iciar_iglesias WHERE slug = @slug AND id != @id
        UNION
        SELECT 1 FROM iciar_iglesias_web WHERE slug = @slug AND iglesia_id != @id
        LIMIT 1";
            using (var cmd = new NpgsqlCommand(sql, conexion))
            {
                cmd.Parameters.AddWithValue("@slug", slug);
                cmd.Parameters.AddWithValue("@id", idIglesia);
                return (await cmd.ExecuteScalarAsync()) != null;
            }
        }
    }
}