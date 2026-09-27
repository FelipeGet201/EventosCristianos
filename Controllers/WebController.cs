using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using RedAJP.Models;
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using RedAJP.Globales;
using System.Text.Json; // Importante para leer el diseño modular

namespace RedAJP.Controllers
{
    [Route("web")]
    public class WebController : Controller
    {
        private readonly string _cadenaConexion;

        public WebController(IConfiguration configuration)
        {
            _cadenaConexion = configuration.GetConnectionString("MiConexion");
        }

        // =========================================================
        // 1. RUTA PÚBLICA (Acceso para todo el mundo) -> /web/{slug}
        // =========================================================
        [AllowAnonymous]
        [HttpGet("{slug}")]
        public async Task<IActionResult> Index(string slug)
        {
            if (string.IsNullOrWhiteSpace(slug)) return NotFound();

            var modelo = await ExtraerDatosIglesiaAsync(slug: slug, idIglesia: null, estado: "PUB");

            if (modelo == null) return View("Error404");

            return View($"Plantillas/{modelo.PlantillaVista}", modelo);
        }

        // =========================================================
        // 2. RUTA DE VISTA PREVIA (Solo Administradores) -> /web/preview/{sId}
        // =========================================================
        [Authorize]
        [HttpGet("preview/{sId}")]
        public async Task<IActionResult> Preview(string sId)
        {
            if (string.IsNullOrWhiteSpace(sId)) return NotFound();

            int idIglesia = Funciones.DesencriptarId(sId);
            int idUsuario = int.Parse(User.FindFirst("IdUsuario").Value);

            if (!await EsAdministradorValido(idIglesia, idUsuario))
            {
                return Unauthorized("No tienes permiso para previsualizar este borrador.");
            }

            var modelo = await ExtraerDatosIglesiaAsync(slug: null, idIglesia: idIglesia, estado: "BOR");

            if (modelo == null)
            {
                modelo = await ExtraerDatosIglesiaAsync(slug: null, idIglesia: idIglesia, estado: "PUB");
                if (modelo == null) return Content("Aún no tienes información guardada para generar una vista previa.");
            }

            ViewBag.ModoPrevia = true;

            return View($"Plantillas/{modelo.PlantillaVista}", modelo);
        }

        // =========================================================
        // 2.5 RUTA DE DEMO (Vista previa de plantilla con datos ficticios) -> /web/demo/{id}
        // =========================================================
        [Authorize]
        [HttpGet("demo/{idPlantilla}")]
        public async Task<IActionResult> Demo(int idPlantilla)
        {
            string rutaVistaPlantilla = "";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var cmd = new NpgsqlCommand("SELECT ruta_vista FROM iciar_plantillas_web WHERE id = @id", conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idPlantilla);
                        var result = await cmd.ExecuteScalarAsync();
                        if (result != null) rutaVistaPlantilla = result.ToString();
                    }
                }
            }
            catch (Exception) { return Content("Error al conectar con la base de datos."); }

            if (string.IsNullOrEmpty(rutaVistaPlantilla)) return NotFound("Plantilla no encontrada.");

            var modelo = new IglesiaWebPublicaViewModel
            {
                NombreIglesia = "Nombre de Tu Iglesia Aquí",
                Municipio = "Ciudad Esperanza, Estado",
                DireccionCompleta = "Av. Las Naciones 1024, Col. Centro",
                Horarios = "Domingo | 10:00 AM | Culto de Alabanza y Adoración\nMiércoles | 07:00 PM | Estudio Bíblico General\nViernes | 06:00 PM | Reunión de Jóvenes",
                MapaUrl = "https://maps.google.com",
                Latitud = 19.432608m,
                Longitud = -99.133209m,
                FacebookUrl = "https://facebook.com",
                InstagramUrl = "https://instagram.com",
                YoutubeUrl = "https://youtube.com",
                Whatsapp = "5512345678",
                Telefono = "5512345678",
                Historia = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. Donec mauris ante, commodo non enim in, euismod finibus sem. In vulputate magna dolor, at convallis dui sodales nec. Integer pellentesque sed turpis at condimentum. Suspendisse ac hendrerit ligula. Donec pulvinar libero eget tristique feugiat. Cras nec porta justo. Praesent nibh justo, finibus vitae ligula at, molestie varius metus. Donec id ante lacus. Aliquam et ullamcorper arcu, et vulputate lectus.\r\n\r\nSuspendisse sed sem mollis, scelerisque lacus eu, maximus ipsum. Sed placerat, massa non vehicula dictum, sapien sapien sagittis eros, luctus finibus risus libero sit amet neque. Pellentesque vel porta nulla. Maecenas mollis varius lacus quis porta. Suspendisse consequat mattis sagittis. Mauris pulvinar eget libero id rhoncus. Sed a efficitur justo, id vehicula eros. Aliquam augue diam, vehicula vitae leo sed, molestie lacinia dui. Nullam velit purus, mattis ac ipsum ut, mattis ornare velit.",
                PlantillaVista = rutaVistaPlantilla,
                Diseno = new DisenoModularViewModel
                {
                    TemaVisual = new TemaVisualViewModel
                    {
                        ColorFondo = "#F4F7F9",
                        ColorSuperficie = "#FFFFFF",
                        ColorPrimario = "#0F172A",
                        ColorAcento = "#E11D48",
                        ColorTextoPrincipal = "#334155",
                        ColorTextoSecundario = "#64748B",
                        FuenteTitulos = "'Outfit', sans-serif",
                        FuenteCuerpo = "'Inter', sans-serif",
                        RadioBordes = "16px"
                    },
                    Secciones = new List<SeccionDisenoViewModel>
                    {
                        new SeccionDisenoViewModel { Orden = 1, Identificador = "Navbar", Vista = "_NavbarFlotante" },
                        new SeccionDisenoViewModel { Orden = 2, Identificador = "Hero", Vista = "_HeroSplit" },
                        new SeccionDisenoViewModel { Orden = 3, Identificador = "Avisos", Vista = "_AvisosGrid" },
                        new SeccionDisenoViewModel { Orden = 4, Identificador = "Eventos", Vista = "_EventosHorizontal" },
                        new SeccionDisenoViewModel { Orden = 5, Identificador = "Historia", Vista = "_HistoriaColumnas" },
                        new SeccionDisenoViewModel { Orden = 6, Identificador = "Galeria", Vista = "_GaleriaMasonry" },
                        new SeccionDisenoViewModel { Orden = 7, Identificador = "Multimedia", Vista = "_MultiScroll" },
                        new SeccionDisenoViewModel { Orden = 8, Identificador = "Doctrina", Vista = "_Doctrina" },
                        new SeccionDisenoViewModel { Orden = 9, Identificador = "Footer", Vista = "_FooterCompleto" }
                    }
                },
                Avisos = new List<AvisoWebViewModel>
                {
                    new AvisoWebViewModel { Titulo = "Ayuno Congregacional", Descripcion = "Iniciamos nuestro ayuno mensual. ¡No faltes a este tiempo especial de búsqueda! Trae tu petición escrita.", FechaInicio = DateTime.Now, FechaExpiracion = DateTime.Now.AddDays(3) },
                    new AvisoWebViewModel { Titulo = "Reunión de Células", Descripcion = "Este jueves nos reunimos en las casas. Contacta a tu líder de red para confirmar la ubicación más cercana a tu domicilio.", FechaInicio = DateTime.Now, FechaExpiracion = DateTime.Now.AddDays(7) }
                },

                Eventos = new List<EventoWebViewModel>
                {
                    new EventoWebViewModel {
                        Titulo = "Noche de Adoración",
                        Fecha = DateTime.Now.AddDays(15),
                        Horarios = "Viernes 6:00 PM",
                        Descripcion = "Un tiempo especial diseñado para exaltar el nombre de Jesús. Tendremos bandas invitadas y un mensaje poderoso que transformará tu vida.",
                        ImagenUrl = "/Images/plantillas/demo/demo-evento-1.jpg"
                    },
                    new EventoWebViewModel {
                        Titulo = "Seminario de Liderazgo",
                        Fecha = DateTime.Now.AddDays(30),
                        Horarios = "Sábado 9:00 AM a 2:00 PM",
                        Descripcion = "Equipando a la próxima generación de servidores. Ven y aprende principios bíblicos para liderar con propósito y excelencia.",
                        ImagenUrl = "/Images/plantillas/demo/demo-evento-2.jpg"
                    }
                },
                Galeria = new List<GaleriaWebViewModel>
                {
                    new GaleriaWebViewModel { Titulo = "Nuestro Servicio Dominical", UrlImagen = "/Images/plantillas/demo/demo-galeria-1.jpg", Orden = 1 },
                    new GaleriaWebViewModel { Titulo = "Bautizos en Agua", UrlImagen = "/Images/plantillas/demo/demo-galeria-2.jpg", Orden = 2 },
                    new GaleriaWebViewModel { Titulo = "Generación de Niños", UrlImagen = "/Images/plantillas/demo/demo-galeria-3.jpg", Orden = 3 },
                    new GaleriaWebViewModel { Titulo = "Ministerio de Alabanza", UrlImagen = "/Images/plantillas/demo/demo-galeria-4.jpg", Orden = 4 }
                },
                Multimedia = new List<MultimediaWebViewModel>
                {
                    new MultimediaWebViewModel { Titulo = "Última Predicación Dominical", UrlEmbed = "https://www.youtube.com/embed/ScMzIvxBSi4", Orden = 1 }
                }
            };

            ViewBag.ModoPrevia = true;

            return View($"Plantillas/{modelo.PlantillaVista}", modelo);
        }

        // =========================================================
        // 3. EL "CEREBRO" EXTRACTOR DE DATOS
        // =========================================================
        private async Task<IglesiaWebPublicaViewModel> ExtraerDatosIglesiaAsync(string slug, int? idIglesia, string estado)
        {
            IglesiaWebPublicaViewModel modelo = null;
            int idEncontrado = 0;

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    string filtroPrincipal = slug != null ? "i.slug = @filtro" : "i.id = @filtro";
                    string sqlPrincipal = $@"
                        SELECT i.id, i.nombre as iglesia, i.calle, i.numero, i.colonia, i.horarios, i.mapa_url, i.latitud, i.longitud,
                               m.nombre as municipio,
                               w.facebook_url, w.instagram_url, w.youtube_url, w.whatsapp, w.telefono, w.historia, w.activa,
                               p.ruta_vista
                        FROM iciar_iglesias i
                        JOIN iciar_municipios m ON i.municipio_id = m.id
                        JOIN iciar_iglesias_web w ON i.id = w.iglesia_id
                        JOIN iciar_plantillas_web p ON w.plantilla_id = p.id
                        WHERE {filtroPrincipal} AND w.estado = @estado";

                    using (var cmd = new NpgsqlCommand(sqlPrincipal, conexion))
                    {
                        cmd.Parameters.AddWithValue("@filtro", slug != null ? (object)slug : idIglesia.Value);
                        cmd.Parameters.AddWithValue("@estado", estado);

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                bool estaActiva = Convert.ToBoolean(reader["activa"]);
                                if (estado == "PUB" && !estaActiva) return null;

                                idEncontrado = Convert.ToInt32(reader["id"]);

                                modelo = new IglesiaWebPublicaViewModel
                                {
                                    NombreIglesia = reader["iglesia"].ToString(),
                                    Municipio = reader["municipio"].ToString(),
                                    DireccionCompleta = $"{reader["calle"]} {reader["numero"]}, {reader["colonia"]}",
                                    Horarios = reader["horarios"]?.ToString(),
                                    MapaUrl = reader["mapa_url"]?.ToString(),
                                    Latitud = reader["latitud"] != DBNull.Value ? Convert.ToDecimal(reader["latitud"]) : null,
                                    Longitud = reader["longitud"] != DBNull.Value ? Convert.ToDecimal(reader["longitud"]) : null,
                                    FacebookUrl = reader["facebook_url"]?.ToString(),
                                    InstagramUrl = reader["instagram_url"]?.ToString(),
                                    YoutubeUrl = reader["youtube_url"]?.ToString(),
                                    Whatsapp = reader["whatsapp"]?.ToString(),
                                    Telefono = reader["telefono"]?.ToString(),
                                    Historia = reader["historia"]?.ToString(),
                                    PlantillaVista = reader["ruta_vista"].ToString()
                                };
                            }
                            else { return null; }
                        }
                    }

                    // AVISOS
                    string sqlAvisos = "SELECT titulo, descripcion, fecha_inicio, fecha_expiracion FROM iciar_iglesias_avisos WHERE iglesia_id = @id AND estado = @est AND fecha_expiracion >= CURRENT_DATE ORDER BY fecha_inicio ASC";
                    using (var cmd = new NpgsqlCommand(sqlAvisos, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idEncontrado);
                        cmd.Parameters.AddWithValue("@est", estado);
                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync()) modelo.Avisos.Add(new AvisoWebViewModel { Titulo = reader["titulo"].ToString(), Descripcion = reader["descripcion"]?.ToString(), FechaInicio = Convert.ToDateTime(reader["fecha_inicio"]), FechaExpiracion = Convert.ToDateTime(reader["fecha_expiracion"]) });
                    }

                    // EVENTOS
                    string sqlEventos = "SELECT titulo, fecha, horarios, descripcion, imagen_url FROM iciar_iglesias_eventos WHERE iglesia_id = @id AND estado = @est AND fecha >= CURRENT_DATE ORDER BY fecha ASC";
                    using (var cmd = new NpgsqlCommand(sqlEventos, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idEncontrado);
                        cmd.Parameters.AddWithValue("@est", estado);
                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync()) modelo.Eventos.Add(new EventoWebViewModel { Titulo = reader["titulo"].ToString(), Fecha = Convert.ToDateTime(reader["fecha"]), Horarios = reader["horarios"].ToString(), Descripcion = reader["descripcion"]?.ToString(), ImagenUrl = reader["imagen_url"]?.ToString() });
                    }

                    // GALERÍA
                    string sqlGaleria = "SELECT url_imagen, titulo FROM iciar_iglesias_galeria WHERE iglesia_id = @id AND estado = @est ORDER BY orden ASC";
                    using (var cmd = new NpgsqlCommand(sqlGaleria, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idEncontrado);
                        cmd.Parameters.AddWithValue("@est", estado);
                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync()) modelo.Galeria.Add(new GaleriaWebViewModel
                            {
                                UrlImagen = reader["url_imagen"].ToString(),
                                Titulo = reader["titulo"]?.ToString()
                            });
                    }

                    // MULTIMEDIA
                    string sqlMulti = "SELECT url_embed, titulo FROM iciar_iglesias_multimedia WHERE iglesia_id = @id AND estado = @est ORDER BY orden ASC";
                    using (var cmd = new NpgsqlCommand(sqlMulti, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idEncontrado);
                        cmd.Parameters.AddWithValue("@est", estado);
                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync()) modelo.Multimedia.Add(new MultimediaWebViewModel
                            {
                                UrlEmbed = reader["url_embed"].ToString(),
                                Titulo = reader["titulo"]?.ToString()
                            });
                    }

                    // =======================================================
                    // LECTURA DEL DISEÑO MODULAR (JSON)
                    // =======================================================
                    string sqlDiseno = "SELECT configuracion_json FROM iciar_iglesias_web_diseno WHERE iglesia_id = @id AND estado = @est LIMIT 1";
                    using (var cmd = new NpgsqlCommand(sqlDiseno, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", idEncontrado);
                        cmd.Parameters.AddWithValue("@est", estado);
                        var result = await cmd.ExecuteScalarAsync();

                        if (result != null && result != DBNull.Value)
                        {
                            try
                            {
                                var opciones = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                                modelo.Diseno = JsonSerializer.Deserialize<DisenoModularViewModel>(result.ToString(), opciones);
                            }
                            catch (Exception)
                            {
                                modelo.Diseno = new DisenoModularViewModel();
                            }
                        }
                        else
                        {
                            modelo.Diseno = new DisenoModularViewModel();
                        }
                    }
                }
            }
            catch (Exception) { return null; }

            return modelo;
        }

        // =========================================================
        // 4. SEGURIDAD PARA LA VISTA PREVIA
        // =========================================================
        private async Task<bool> EsAdministradorValido(int idIglesia, int idUsuario)
        {
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var cmd = new NpgsqlCommand("SELECT 1 FROM iciar_iglesias_administradores WHERE iglesia_id = @idIglesia AND usuario_id = @idUsuario", conexion))
                    {
                        cmd.Parameters.AddWithValue("@idIglesia", idIglesia);
                        cmd.Parameters.AddWithValue("@idUsuario", idUsuario);
                        return await cmd.ExecuteScalarAsync() != null;
                    }
                }
            }
            catch { return false; }
        }
    }
}