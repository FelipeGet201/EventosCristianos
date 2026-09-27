using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using RedAJP.Globales;
using RedAJP.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace RedAJP.Controllers
{
    [Authorize]
    public class BibliotecaController : GlobalController
    {
        private readonly string _cadenaConexion;
        private readonly IWebHostEnvironment _env;
        private Parametros.Modulo Modulo = Parametros.Modulos.Biblioteca;

        public BibliotecaController(IConfiguration configuration, IWebHostEnvironment env)
        {
            _cadenaConexion = configuration.GetConnectionString("MiConexion");
            _env = env;
        }

        /// <summary>
        /// Carga la vista principal de la biblioteca
        /// </summary>
        [HttpGet]
        public IActionResult Index()
        {
            if (!User.TienePermiso(Modulo, PermisoLeer)) {
                MostrarMensaje("Error", "No tiene permisos de lectura en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Home"); 
            }

            ViewBag.PuedeCrear = User.TienePermiso(Modulo, PermisoCrear);
            ViewBag.PuedeEditar = User.TienePermiso(Modulo, PermisoEditar);
            ViewBag.PuedeBorrar = User.TienePermiso(Modulo, PermisoBorrar);

            var listaLibros = ObtenerListaLibros();
            return View(listaLibros);
        }

        /// <summary>
        /// Método privado para escanear directorios y construir la lista (Soporta PDF y EPUB)
        /// </summary>
        private List<LibroViewModel> ObtenerListaLibros()
        {
            var lista = new List<LibroViewModel>();
            string rutaBase = Path.Combine(_env.WebRootPath, "libros");

            if (!Directory.Exists(rutaBase))
            {
                Directory.CreateDirectory(rutaBase);
                return lista;
            }

            var carpetas = Directory.GetDirectories(rutaBase);

            foreach (var carpeta in carpetas)
            {
                var dirInfo = new DirectoryInfo(carpeta);
                string idCarpeta = dirInfo.Name;
                string rutaMetadata = Path.Combine(carpeta, "metadata.json");

                var archivos = dirInfo.GetFiles();

                // Buscar PDF o EPUB
                var archivoLibro = archivos.FirstOrDefault(f =>
                    f.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
                    f.Extension.Equals(".epub", StringComparison.OrdinalIgnoreCase));

                // Buscar Portada
                var archivoImg = archivos.FirstOrDefault(f =>
                    f.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                    f.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                    f.Extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase));

                if (archivoLibro == null) continue; // Si no hay libro válido, omitir carpeta

                var libro = new LibroViewModel
                {
                    IdCarpeta = idCarpeta,
                    RutaArchivo = $"/libros/{idCarpeta}/{archivoLibro.Name}", // Mantenemos la propiedad en el modelo aunque sea epub
                    RutaPortada = archivoImg != null ? $"/libros/{idCarpeta}/{archivoImg.Name}" : "/img/default-book.png"
                };

                if (System.IO.File.Exists(rutaMetadata))
                {
                    string json = System.IO.File.ReadAllText(rutaMetadata);
                    var meta = JsonSerializer.Deserialize<LibroMetadata>(json);
                    libro.Titulo = meta?.Titulo ?? Path.GetFileNameWithoutExtension(archivoLibro.Name);
                    libro.Autor = meta?.Autor ?? "Desconocido";
                    libro.Descripcion = meta?.Descripcion ?? "";
                    libro.Descargas = meta?.Descargas ?? 0; 
                }
                else
                {
                    libro.Titulo = Path.GetFileNameWithoutExtension(archivoLibro.Name);
                    libro.Autor = "Desconocido";
                    libro.Descripcion = "";
                    libro.Descargas = 0; // NUEVO
                }

                lista.Add(libro);
            }

            return lista.OrderBy(l => l.Titulo).ToList();
        }

        /// <summary>
        /// Endpoint para recargar la galería por AJAX
        /// </summary>
        [HttpGet]
        public IActionResult ObtenerGaleria()
        {
            if (!User.TienePermiso(Modulo, PermisoLeer)) return Unauthorized();
            return Json(ObtenerListaLibros());
        }

        /// <summary>
        /// Descarga el archivo del libro (PDF o EPUB) y registra la descarga
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Descargar(string id) // CAMBIO: Ahora es async Task<IActionResult>
        {
            if (!User.TienePermiso(Modulo, PermisoLeer)) {
                MostrarMensaje("Error", "No tiene permisos de lectura en ésta página", TipoMensaje.Alerta);
                return RedirectToAction("Index", "Home"); 
            }

            string rutaCarpeta = Path.Combine(_env.WebRootPath, "libros", id);
            if (!Directory.Exists(rutaCarpeta)) return NotFound("La carpeta del libro no existe.");

            var dirInfo = new DirectoryInfo(rutaCarpeta);

            var archivoLibro = dirInfo.GetFiles().FirstOrDefault(f =>
                f.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
                f.Extension.Equals(".epub", StringComparison.OrdinalIgnoreCase));

            if (archivoLibro == null) return NotFound("El archivo del libro no se encontró en el servidor.");

            // --- ACTUALIZAR CONTADOR Y BITÁCORA ---
            string tituloLibro = "Desconocido";
            string rutaMetadata = Path.Combine(rutaCarpeta, "metadata.json");

            if (System.IO.File.Exists(rutaMetadata))
            {
                var meta = JsonSerializer.Deserialize<LibroMetadata>(System.IO.File.ReadAllText(rutaMetadata));
                if (meta != null)
                {
                    tituloLibro = meta.Titulo;
                    meta.Descargas++; // Sumar 1 al contador

                    // Guardar el JSON actualizado
                    string json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
                    System.IO.File.WriteAllText(rutaMetadata, json);
                }
            }

            try
            {
                int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
                string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Leer, $"Descargó el libro '{tituloLibro}'", ip);
                }
            }
            catch { /* Opcional: Ignorar fallos de bitácora para no bloquear la descarga al usuario */ }

            string contentType = archivoLibro.Extension.Equals(".epub", StringComparison.OrdinalIgnoreCase)
                ? "application/epub+zip"
                : "application/pdf";

            return PhysicalFile(archivoLibro.FullName, contentType, archivoLibro.Name);
        }

        /// <summary>
        /// Guarda un libro nuevo o edita uno existente
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Guardar(LibroFormViewModel modelo)
        {
            bool esNuevo = string.IsNullOrEmpty(modelo.IdCarpeta);

            if (esNuevo && !User.TienePermiso(Modulo, PermisoCrear)) return Json(new { success = false, message = "Sin permiso para crear." });
            if (!esNuevo && !User.TienePermiso(Modulo, PermisoEditar)) return Json(new { success = false, message = "Sin permiso para editar." });

            if (esNuevo && (modelo.ArchivoPdf == null || modelo.ArchivoPortada == null))
            {
                return Json(new { success = false, message = "Para un libro nuevo, el archivo y la portada son obligatorios." });
            }

            try
            {
                string idCarpeta = esNuevo ? Guid.NewGuid().ToString() : modelo.IdCarpeta;
                string rutaCarpeta = Path.Combine(_env.WebRootPath, "libros", idCarpeta);

                if (esNuevo) Directory.CreateDirectory(rutaCarpeta);

                // Guardar Archivo del Libro (PDF o EPUB)
                if (modelo.ArchivoPdf != null)
                {
                    if (!esNuevo)
                    {
                        var librosAntiguos = new DirectoryInfo(rutaCarpeta).GetFiles()
                            .Where(f => f.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
                                        f.Extension.Equals(".epub", StringComparison.OrdinalIgnoreCase));
                        foreach (var f in librosAntiguos) f.Delete();
                    }

                    string rutaLibro = Path.Combine(rutaCarpeta, modelo.ArchivoPdf.FileName);
                    using (var stream = new FileStream(rutaLibro, FileMode.Create))
                    {
                        await modelo.ArchivoPdf.CopyToAsync(stream);
                    }
                }

                // Guardar Portada
                if (modelo.ArchivoPortada != null)
                {
                    if (!esNuevo)
                    {
                        var imgsAntiguas = new DirectoryInfo(rutaCarpeta).GetFiles()
                            .Where(f => f.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                        f.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                                        f.Extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase));
                        foreach (var f in imgsAntiguas) f.Delete();
                    }

                    string rutaImg = Path.Combine(rutaCarpeta, modelo.ArchivoPortada.FileName);
                    using (var stream = new FileStream(rutaImg, FileMode.Create))
                    {
                        await modelo.ArchivoPortada.CopyToAsync(stream);
                    }
                }

                // Generar metadata.json
                var metadata = new LibroMetadata
                {
                    Titulo = modelo.Titulo,
                    Autor = modelo.Autor,
                    Descripcion = modelo.Descripcion
                };

                string json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
                System.IO.File.WriteAllText(Path.Combine(rutaCarpeta, "metadata.json"), json);

                // Registro en Bitácora
                int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
                string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    var accion = esNuevo ? Parametros.AccionesBitacora.Crear : Parametros.AccionesBitacora.Editar;
                    string msj = esNuevo ? $"Agregó el libro '{modelo.Titulo}' a la biblioteca" : $"Editó el libro '{modelo.Titulo}'";

                    await Funciones.RegistrarBitacora(conexion, idUser, Modulo, accion, msj, ip);
                }

                return Json(new { success = true, message = "Libro guardado exitosamente." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Elimina un libro (Borra la carpeta completa)
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Eliminar(string id)
        {
            if (!User.TienePermiso(Modulo, PermisoBorrar)) return Json(new { success = false, message = "Sin permiso para borrar." });

            try
            {
                string rutaCarpeta = Path.Combine(_env.WebRootPath, "libros", id);
                string tituloLibro = "Desconocido";

                // Leer el título antes de borrar para la bitácora
                string rutaMetadata = Path.Combine(rutaCarpeta, "metadata.json");
                if (System.IO.File.Exists(rutaMetadata))
                {
                    var meta = JsonSerializer.Deserialize<LibroMetadata>(System.IO.File.ReadAllText(rutaMetadata));
                    if (meta != null) tituloLibro = meta.Titulo;
                }

                if (Directory.Exists(rutaCarpeta))
                {
                    Directory.Delete(rutaCarpeta, true); // true para borrar todo el contenido
                }

                // Registro en Bitácora
                int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
                string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Borrar, $"Eliminó el libro '{tituloLibro}' de la biblioteca", ip);
                }

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }
    }
}