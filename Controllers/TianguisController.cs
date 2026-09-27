using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Npgsql;
using RedAJP.Globales;
using RedAJP.Hubs;
using RedAJP.Models;
using RedAJP.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace RedAJP.Controllers
{
    public class TianguisController : GlobalController
    {
        private readonly string _cadenaConexion;
        private readonly IHubContext<ChatTianguisHub> _hubContext;
        private Parametros.Modulo Modulo = Parametros.Modulos.Tianguis;
        private readonly IWebHostEnvironment _env;
        private readonly IMemoryCache _cache; 
        private readonly IConfiguration _configuration;
        private readonly Cloudinary _cloudinary;

        public TianguisController(IConfiguration configuration, IWebHostEnvironment env, IMemoryCache cache, IHubContext<ChatTianguisHub> hubContext)
        {
            _cadenaConexion = configuration.GetConnectionString("MiConexion");
            _env = env;
            _cache = cache;
            _hubContext = hubContext;
            _configuration = configuration;

            //Esto para almacenar las imagenes en Cloudinary
            Account account = new Account(
                _configuration["Cloudinary:CloudName"],
                _configuration["Cloudinary:ApiKey"],
                _configuration["Cloudinary:ApiSecret"]
            );
            _cloudinary = new Cloudinary(account);
            _cloudinary.Api.Secure = true;
        }

        // =========================================================
        // 1. CATÁLOGO PRINCIPAL (PÚBLICO)
        // =========================================================
        [AllowAnonymous]
        public async Task<IActionResult> Index(string sCat, [FromServices] ISolicitudesService solicitudesService)
        {
            // Creamos la variable cat para no romper el código interno
            int? cat = string.IsNullOrEmpty(sCat) ? (int?)null : Funciones.DesencriptarId(sCat);
            var modelo = new TianguisIndexViewModel { CategoriaActual = cat };


            // Alertas personales (Protegido: solo si el usuario tiene sesión)
            if (User.Identity != null && User.Identity.IsAuthenticated)
            {
                int idUser = int.Parse(User.FindFirst("IdUsuario").Value);
                ViewBag.AlertasTianguis = await solicitudesService.ObtenerContadorTianguisAlertasAsync(idUser);
            }
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // Cargar Categorías Activas QUE TENGAN PUBLICACIONES (ACT o RES)
                    string sqlCat = @"
                        SELECT c.""IdCategoria"", c.""Nombre"" 
                        FROM ""Tianguis_Categorias"" c
                        WHERE c.""Activo"" = TRUE 
                        AND EXISTS (
                            SELECT 1 FROM ""Tianguis_Publicaciones"" p 
                            WHERE p.""IdCategoria"" = c.""IdCategoria"" 
                            AND p.""IdEstado"" IN ('ACT', 'RES')
                            AND p.""Activo"" = TRUE
                        )
                        ORDER BY c.""Nombre""";

                    using (var cmdCat = new NpgsqlCommand(sqlCat, conexion))
                    using (var rCat = await cmdCat.ExecuteReaderAsync())
                    {
                        while (await rCat.ReadAsync())
                            modelo.Categorias.Add(new CategoriaTianguis
                            {
                                IdCategoria = (int)rCat["IdCategoria"],
                                sIdCategoria = Funciones.EncriptarId((int)rCat["IdCategoria"]), 
                                Nombre = rCat["Nombre"].ToString()
                            });
                    }

                    string sql = @"
    SELECT p.""IdPublicacion"", p.""Titulo"", p.""PrecioBase"", p.""IdTipoVenta"", p.""IdEstado"", c.""Nombre"" as ""NomCat"",
           (SELECT MAX(""OfertaActual"") FROM ""Tianguis_Interesados"" WHERE ""IdPublicacion"" = p.""IdPublicacion"") as ""MejorOferta"",
           (SELECT ""UrlImagen"" FROM ""Tianguis_Imagenes"" WHERE ""IdPublicacion"" = p.""IdPublicacion"" ORDER BY ""Orden"" ASC LIMIT 1) as ""ImgUrl""
    FROM ""Tianguis_Publicaciones"" p
    JOIN ""Tianguis_Categorias"" c ON p.""IdCategoria"" = c.""IdCategoria""
    WHERE p.""IdEstado"" IN ('ACT', 'RES') AND p.""Activo"" = TRUE";

                    // ¡Esto se queda intacto!
                    if (cat.HasValue && cat.Value > 0) sql += " AND p.\"IdCategoria\" = @idCat";
                    sql += " ORDER BY p.\"FechaPublicacion\" DESC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        // Esto también se queda intacto
                        if (cat.HasValue && cat.Value > 0) cmd.Parameters.AddWithValue("@idCat", cat.Value);

                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                decimal precioBase = (decimal)r["PrecioBase"];
                                decimal mejorOferta = r["MejorOferta"] != DBNull.Value ? (decimal)r["MejorOferta"] : 0;

                                modelo.Productos.Add(new TianguisItemViewModel
                                {
                                    IdPublicacion = (int)r["IdPublicacion"],
                                    sIdPublicacion = Funciones.EncriptarId((int)r["IdPublicacion"]),
                                    Titulo = r["Titulo"].ToString(),
                                    Categoria = r["NomCat"].ToString(),
                                    PrecioBase = precioBase,
                                    PrecioActual = (r["IdTipoVenta"].ToString() == "SUB" && mejorOferta > precioBase) ? mejorOferta : precioBase, 
                                    TipoVenta = r["IdTipoVenta"].ToString(),
                                    Estado = r["IdEstado"].ToString(),
                                    ImagenUrl = r["ImgUrl"]?.ToString() ?? "/Images/default-tianguis.jpg"
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }
            return View(modelo);
        }

        // =========================================================
        // 2. DETALLE DE PUBLICACIÓN
        // =========================================================
        [AllowAnonymous]
        public async Task<IActionResult> Detalle(string sId, string sChatId = null) // Cambiamos los parámetros
        {
            int id = Funciones.DesencriptarId(sId);
            int? chatId = string.IsNullOrEmpty(sChatId) ? (int?)null : Funciones.DesencriptarId(sChatId);
            var modelo = new TianguisDetalleViewModel
            {
                IdPublicacion = id,
                sIdPublicacion = sId, 
                EstaAutenticado = User.Identity.IsAuthenticated
            };
            int idUser = modelo.EstaAutenticado ? int.Parse(User.FindFirst("IdUsuario").Value) : 0;

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    string sqlPub = @"SELECT p.*, c.""Nombre"" as ""CatNombre"" 
                                      FROM ""Tianguis_Publicaciones"" p 
                                      JOIN ""Tianguis_Categorias"" c ON p.""IdCategoria""=c.""IdCategoria"" 
                                      WHERE p.""IdPublicacion""=@id";
                    using (var cmd = new NpgsqlCommand(sqlPub, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                modelo.Titulo = r["Titulo"].ToString();
                                modelo.Descripcion = r["Descripcion"]?.ToString();
                                modelo.PrecioActual = (decimal)r["PrecioBase"];
                                modelo.TipoVenta = r["IdTipoVenta"].ToString();
                                modelo.Estado = r["IdEstado"].ToString();
                                modelo.Categoria = r["CatNombre"].ToString();
                                modelo.FechaExpiracion = r["FechaExpiracion"] as DateTime?;
                                modelo.EsMio = modelo.EstaAutenticado && (int)r["IdUsuarioVendedor"] == idUser;
                                modelo.VendedorAlias = modelo.EsMio ? "Tú (Vendedor)" : "Vendedor Anónimo";
                            }
                            else return RedirectToAction("Index");
                        }
                    }

                    using (var cmdImg = new NpgsqlCommand("SELECT \"UrlImagen\" FROM \"Tianguis_Imagenes\" WHERE \"IdPublicacion\"=@id ORDER BY \"Orden\"", conexion))
                    {
                        cmdImg.Parameters.AddWithValue("@id", id);
                        using (var r = await cmdImg.ExecuteReaderAsync())
                            while (await r.ReadAsync()) modelo.ImagenesUrls.Add(r["UrlImagen"].ToString());
                    }
                    if (modelo.ImagenesUrls.Count == 0) modelo.ImagenesUrls.Add("/Images/default-tianguis.jpg");

                    if (modelo.TipoVenta == "TIE")
                    {
                        using (var cmdT = new NpgsqlCommand("SELECT \"IdTalla\", \"NombreTalla\", \"Stock\" FROM \"Tianguis_Tallas\" WHERE \"IdPublicacion\"=@id ORDER BY \"IdTalla\"", conexion))
                        {
                            cmdT.Parameters.AddWithValue("@id", id);
                            using (var r = await cmdT.ExecuteReaderAsync())
                                while (await r.ReadAsync()) modelo.Tallas.Add(new TallaTianguis { IdTalla = (int)r["IdTalla"], NombreTalla = r["NombreTalla"].ToString(), Stock = (int)r["Stock"] });
                        }
                    }

                    if (modelo.EstaAutenticado)
                    {
                        // 1. CARGAMOS TODOS LOS INTERESADOS (Indispensable para el .Count en la vista de subasta)
                        string sqlInt = modelo.EsMio
                        ? @"SELECT i.*, 
                                   t.""NombreTalla"",
                                   (SELECT ""Texto"" FROM ""Tianguis_Mensajes"" WHERE ""IdInteresado""=i.""IdInteresado"" ORDER BY ""FechaEnvio"" DESC LIMIT 1) as ""UltimoMsj""
                            FROM ""Tianguis_Interesados"" i 
                            LEFT JOIN ""Tianguis_Tallas"" t ON i.""IdTallaSolicitada"" = t.""IdTalla""
                            WHERE i.""IdPublicacion""=@id"
                        : @"SELECT i.""IdInteresado"", i.""AliasAnonimo"", i.""OfertaActual"", i.""IdEstadoTrato"", i.""EsMejorOferta"", i.""CantidadSolicitada"", i.""IdTallaSolicitada"", t.""NombreTalla""
                            FROM ""Tianguis_Interesados"" i 
                            LEFT JOIN ""Tianguis_Tallas"" t ON i.""IdTallaSolicitada"" = t.""IdTalla""
                            WHERE i.""IdPublicacion""=@id";

                        using (var cmdI = new NpgsqlCommand(sqlInt, conexion))
                        {
                            cmdI.Parameters.AddWithValue("@id", id);
                            using (var r = await cmdI.ExecuteReaderAsync())
                            {
                                while (await r.ReadAsync())
                                {
                                    modelo.Interesados.Add(new InteresadoTianguis
                                    {
                                        IdInteresado = (int)r["IdInteresado"],
                                        sIdInteresado = Funciones.EncriptarId((int)r["IdInteresado"]),
                                        AliasAnonimo = r["AliasAnonimo"].ToString(),
                                        Oferta = (decimal)r["OfertaActual"],
                                        EstadoTrato = r["IdEstadoTrato"].ToString(),
                                        EsMejorOferta = (bool)r["EsMejorOferta"],
                                        UltimoMensaje = modelo.EsMio ? (r["UltimoMsj"]?.ToString() ?? "Sin mensajes") : "",
                                        Cantidad = r["CantidadSolicitada"] != DBNull.Value ? (int)r["CantidadSolicitada"] : 1,
                                        IdTalla = r["IdTallaSolicitada"] as int ?,
                                        Talla = r["NombreTalla"]?.ToString() ?? "Única"
                                    });
                                }
                            }
                        }

                        // 2. LÓGICA ESPECÍFICA SEGÚN EL ROL
                        if (modelo.EsMio)
                        {
                            // --- DUEÑO: Gestión de chats seleccionados ---
                            if (chatId.HasValue)
                            {
                                ViewBag.ChatActivo = chatId.Value;

                                using (var cmdAlias = new NpgsqlCommand(@"SELECT i.""AliasAnonimo"", i.""IdEstadoTrato"", i.""OfertaActual"", i.""CantidadSolicitada"", t.""NombreTalla"" 
                                          FROM ""Tianguis_Interesados"" i 
                                          LEFT JOIN ""Tianguis_Tallas"" t ON i.""IdTallaSolicitada"" = t.""IdTalla""
                                          WHERE i.""IdInteresado""=@idInt", conexion))
                                {
                                    cmdAlias.Parameters.AddWithValue("@idInt", chatId.Value);
                                    using (var rAlias = await cmdAlias.ExecuteReaderAsync())
                                    {
                                        if (await rAlias.ReadAsync())
                                        {
                                            ViewBag.ChatAlias = rAlias["AliasAnonimo"].ToString();
                                            ViewBag.ChatEstado = rAlias["IdEstadoTrato"].ToString();
                                            ViewBag.ChatOferta = (decimal)rAlias["OfertaActual"];
                                            ViewBag.ChatCantidad = rAlias["CantidadSolicitada"] != DBNull.Value ? (int)rAlias["CantidadSolicitada"] : 1;
                                            ViewBag.ChatTalla = rAlias["NombreTalla"]?.ToString() ?? "Única";
                                        }
                                    }
                                }

                                using (var cmdMsj = new NpgsqlCommand("SELECT * FROM \"Tianguis_Mensajes\" WHERE \"IdInteresado\"=@idInt ORDER BY \"FechaEnvio\" ASC", conexion))
                                {
                                    cmdMsj.Parameters.AddWithValue("@idInt", chatId.Value);
                                    using (var r = await cmdMsj.ExecuteReaderAsync())
                                    {
                                        while (await r.ReadAsync())
                                        {
                                            bool esMio = (int)r["IdUsuarioRemitente"] == idUser;
                                            modelo.Mensajes.Add(new MensajeTianguis
                                            {
                                                Autor = esMio ? "Tú" : ViewBag.ChatAlias,
                                                Texto = r["Texto"].ToString(),
                                                Fecha = (DateTime)r["FechaEnvio"],
                                                EsMio = esMio
                                            });
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            string sqlMisDatos = chatId.HasValue
    ? @"SELECT i.""IdInteresado"", i.""OfertaActual"", i.""EsMejorOferta"", i.""IdEstadoTrato"", i.""CantidadSolicitada"", t.""NombreTalla"" 
        FROM ""Tianguis_Interesados"" i 
        LEFT JOIN ""Tianguis_Tallas"" t ON i.""IdTallaSolicitada"" = t.""IdTalla""
        WHERE i.""IdInteresado""=@chatId AND i.""IdUsuarioComprador""=@uid LIMIT 1"
    : @"SELECT i.""IdInteresado"", i.""OfertaActual"", i.""EsMejorOferta"", i.""IdEstadoTrato"", i.""CantidadSolicitada"", t.""NombreTalla"" 
        FROM ""Tianguis_Interesados"" i 
        LEFT JOIN ""Tianguis_Tallas"" t ON i.""IdTallaSolicitada"" = t.""IdTalla""
        WHERE i.""IdPublicacion""=@id AND i.""IdUsuarioComprador""=@uid AND i.""IdEstadoTrato"" IN ('INT', 'RES') ORDER BY i.""FechaActualizacion"" DESC LIMIT 1";

                            using (var cmdMy = new NpgsqlCommand(sqlMisDatos, conexion))
                            {
                                cmdMy.Parameters.AddWithValue("@uid", idUser);
                                if (chatId.HasValue) cmdMy.Parameters.AddWithValue("@chatId", chatId.Value);
                                else cmdMy.Parameters.AddWithValue("@id", id);

                                using (var r = await cmdMy.ExecuteReaderAsync())
                                {
                                    if (await r.ReadAsync())
                                    {
                                        modelo.Participando = true;
                                        modelo.MiIdInteresado = (int)r["IdInteresado"];
                                        modelo.sMiIdInteresado = Funciones.EncriptarId((int)r["IdInteresado"]);
                                        modelo.MiOferta = (decimal)r["OfertaActual"];
                                        modelo.EsMejorOfertaMia = (bool)r["EsMejorOferta"];
                                        modelo.MiEstadoTrato = r["IdEstadoTrato"].ToString();
                                        ViewBag.MiCantidad = r["CantidadSolicitada"] != DBNull.Value ? (int)r["CantidadSolicitada"] : 1;
                                        ViewBag.MiTalla = r["NombreTalla"]?.ToString() ?? "Única";
                                    }
                                }
                            }

                            // ¿Ya compró antes?
                            string sqlComprado = @"SELECT COUNT(*) FROM ""Tianguis_Interesados"" WHERE ""IdPublicacion""=@id AND ""IdUsuarioComprador""=@uid AND ""IdEstadoTrato"" = 'COM'";
                            using (var cmdComp = new NpgsqlCommand(sqlComprado, conexion))
                            {
                                cmdComp.Parameters.AddWithValue("@id", id);
                                cmdComp.Parameters.AddWithValue("@uid", idUser);
                                modelo.YaComproAntes = (long)await cmdComp.ExecuteScalarAsync() > 0;
                            }

                            if (modelo.Participando)
                            {
                                using (var cmdMsj = new NpgsqlCommand("SELECT * FROM \"Tianguis_Mensajes\" WHERE \"IdInteresado\"=@idInt ORDER BY \"FechaEnvio\" ASC", conexion))
                                {
                                    cmdMsj.Parameters.AddWithValue("@idInt", modelo.MiIdInteresado.Value);
                                    using (var r = await cmdMsj.ExecuteReaderAsync())
                                    {
                                        while (await r.ReadAsync())
                                        {
                                            bool esMio = (int)r["IdUsuarioRemitente"] == idUser;
                                            modelo.Mensajes.Add(new MensajeTianguis
                                            {
                                                Autor = esMio ? "Tú" : modelo.VendedorAlias,
                                                Texto = r["Texto"].ToString(),
                                                Fecha = (DateTime)r["FechaEnvio"],
                                                EsMio = esMio
                                            });
                                        }
                                    }
                                }
                            }
                        }

                        // Identificar al ganador (para todos)
                        if (modelo.Estado == "VEN" || modelo.Estado == "AGO")
                        {
                            using (var cmdWin = new NpgsqlCommand("SELECT \"AliasAnonimo\", \"IdUsuarioComprador\" FROM \"Tianguis_Interesados\" WHERE \"IdPublicacion\"=@id AND \"IdEstadoTrato\"='COM' LIMIT 1", conexion))
                            {
                                cmdWin.Parameters.AddWithValue("@id", id);
                                using (var r = await cmdWin.ExecuteReaderAsync())
                                {
                                    if (await r.ReadAsync())
                                    {
                                        if (!modelo.EsMio && (int)r["IdUsuarioComprador"] == idUser) modelo.CompradorFinalAlias = "Tú";
                                        else modelo.CompradorFinalAlias = modelo.EsMio ? r["AliasAnonimo"].ToString() : "Otro Interesado";
                                    }
                                }
                            }
                        }
                    }

                    // === OBTENER OFERTA MÁXIMA PARA AVISAR SI FUE SUPERADO ===
                    if (modelo.TipoVenta == "SUB")
                    {
                        using (var cmdMax = new NpgsqlCommand(@"SELECT MAX(""OfertaActual"") FROM ""Tianguis_Interesados"" WHERE ""IdPublicacion""=@id", conexion))
                        {
                            cmdMax.Parameters.AddWithValue("@id", id);
                            var maxObj = await cmdMax.ExecuteScalarAsync();

                            if (maxObj != DBNull.Value)
                            {
                                decimal ofertaMax = (decimal)maxObj;
                                ViewBag.OfertaMaxima = ofertaMax;

                                // Actualizar el precio de la vista
                                if (modelo.Participando && (modelo.MiEstadoTrato == "RES" || modelo.MiEstadoTrato == "COM"))
                                {
                                    modelo.PrecioActual = modelo.MiOferta; // (Respeta el precio si ya le cerraron el trato)
                                }
                                else if (ofertaMax > modelo.PrecioBase)
                                {
                                    modelo.PrecioActual = ofertaMax;
                                }
                            }
                            else
                            {
                                ViewBag.OfertaMaxima = modelo.PrecioActual;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); return RedirectToAction("Index"); }

            return View(modelo);
        }

        // =========================================================
        // AUMENTAR OFERTA EN SUBASTA EXISTENTE
        // =========================================================
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AumentarOferta(int idInteresado, int idPublicacion, decimal nuevoMonto)
        {
            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 1. Validar propiedad del chat y estado
                            string sqlVal = @"SELECT i.""IdUsuarioComprador"", p.""IdEstado"" 
                                              FROM ""Tianguis_Interesados"" i
                                              JOIN ""Tianguis_Publicaciones"" p ON i.""IdPublicacion"" = p.""IdPublicacion""
                                              WHERE i.""IdInteresado""=@int AND i.""IdPublicacion""=@pub FOR UPDATE";

                            using (var cmdVal = new NpgsqlCommand(sqlVal, conexion, trans))
                            {
                                cmdVal.Parameters.AddWithValue("@int", idInteresado);
                                cmdVal.Parameters.AddWithValue("@pub", idPublicacion);
                                using (var r = await cmdVal.ExecuteReaderAsync())
                                {
                                    if (await r.ReadAsync())
                                    {
                                        if ((int)r["IdUsuarioComprador"] != idUser) throw new UnauthorizedAccessException("No tienes permiso.");
                                        if (r["IdEstado"].ToString() != "ACT") throw new InvalidOperationException("La publicación ya no está disponible para ofertas.");
                                    }
                                    else throw new Exception("Negociación no encontrada.");
                                }
                            }

                            // 2. Obtener oferta máxima actual para que no hagan trampa
                            decimal ofertaMaxima = 0;
                            using (var cmdMax = new NpgsqlCommand(@"SELECT MAX(""OfertaActual"") FROM ""Tianguis_Interesados"" WHERE ""IdPublicacion""=@pub", conexion, trans))
                            {
                                cmdMax.Parameters.AddWithValue("@pub", idPublicacion);
                                var objMax = await cmdMax.ExecuteScalarAsync();
                                if (objMax != DBNull.Value) ofertaMaxima = (decimal)objMax;
                            }

                            if (nuevoMonto <= ofertaMaxima)
                                throw new Exception($"Tu oferta llegó tarde, alguien ya ofertó ${ofertaMaxima:N2}. Debes superar esa cantidad.");

                            // 3. Quitar bandera de Mejor Oferta a todos los demás
                            using (var cmdUpdAll = new NpgsqlCommand(@"UPDATE ""Tianguis_Interesados"" SET ""EsMejorOferta""=FALSE WHERE ""IdPublicacion""=@pub", conexion, trans))
                            {
                                cmdUpdAll.Parameters.AddWithValue("@pub", idPublicacion);
                                await cmdUpdAll.ExecuteNonQueryAsync();
                            }

                            // 4. Actualizar mi oferta
                            using (var cmdUpd = new NpgsqlCommand(@"UPDATE ""Tianguis_Interesados"" SET ""OfertaActual""=@monto, ""EsMejorOferta""=TRUE, ""FechaActualizacion""=NOW() WHERE ""IdInteresado""=@int", conexion, trans))
                            {
                                cmdUpd.Parameters.AddWithValue("@monto", nuevoMonto);
                                cmdUpd.Parameters.AddWithValue("@int", idInteresado);
                                await cmdUpd.ExecuteNonQueryAsync();
                            }

                            // 5. Inyectar mensaje automático al chat
                            string msg = $"¡He aumentado mi oferta a ${nuevoMonto:N2}!";
                            using (var cmdMsj = new NpgsqlCommand(@"INSERT INTO ""Tianguis_Mensajes"" (""IdInteresado"", ""IdUsuarioRemitente"", ""Texto"") VALUES (@int, @usr, @txt)", conexion, trans))
                            {
                                cmdMsj.Parameters.AddWithValue("@int", idInteresado);
                                cmdMsj.Parameters.AddWithValue("@usr", idUser);
                                cmdMsj.Parameters.AddWithValue("@txt", msg);
                                await cmdMsj.ExecuteNonQueryAsync();
                            }

                            await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Editar, $"Aumentó oferta a {nuevoMonto} en pub {idPublicacion}", ip, trans);
                            await trans.CommitAsync();

                            // 6. Avisar por SignalR a la sala para que el vendedor lo vea en vivo
                            if (_hubContext != null)
                            {
                                await _hubContext.Clients.Group($"Chat_{idInteresado}").SendAsync("RecibirMensaje", msg, idUser, idInteresado);
                            }

                            MostrarMensaje("Oferta Mejorada", "Has lanzado una nueva oferta máxima.", TipoMensaje.Exito);
                        }
                        catch (Exception ex)
                        {
                            await trans.RollbackAsync();
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Detalle", new { sId = Funciones.EncriptarId(idPublicacion), sChatId = Funciones.EncriptarId(idInteresado) });
        }
        // =========================================================
        // 11. ENVIAR MENSAJE DE CHAT (VERSIÓN SIGNALR / AJAX)
        // =========================================================
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EnviarMensaje(int idInteresado, int idPublicacion, string texto, bool soyDueno)
        {
            if (string.IsNullOrWhiteSpace(texto)) return Json(new { success = false, message = "Texto vacío" });

            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 1. VALIDACIÓN ZERO TRUST (Prevención de IDOR)
                            // Verificamos cruzando datos que el usuario logueado realmente participe en ESTE trato
                            bool esParticipanteValido = false;
                            string sqlVal = @"
                        SELECT p.""IdUsuarioVendedor"", i.""IdUsuarioComprador""
                        FROM ""Tianguis_Interesados"" i
                        JOIN ""Tianguis_Publicaciones"" p ON i.""IdPublicacion"" = p.""IdPublicacion""
                        WHERE i.""IdInteresado"" = @int AND i.""IdPublicacion"" = @pub";

                            using (var cmdVal = new NpgsqlCommand(sqlVal, conexion, trans))
                            {
                                cmdVal.Parameters.AddWithValue("@int", idInteresado);
                                cmdVal.Parameters.AddWithValue("@pub", idPublicacion);
                                using (var r = await cmdVal.ExecuteReaderAsync())
                                {
                                    if (await r.ReadAsync())
                                    {
                                        int idVendedor = (int)r["IdUsuarioVendedor"];
                                        int idComprador = (int)r["IdUsuarioComprador"];

                                        if (idUser == idVendedor || idUser == idComprador)
                                        {
                                            esParticipanteValido = true;
                                        }
                                    }
                                }
                            }

                            if (!esParticipanteValido)
                                throw new UnauthorizedAccessException("No tienes permiso para participar en este chat.");

                            // 2. Insertar el mensaje en BD
                            string sqlMsj = @"INSERT INTO ""Tianguis_Mensajes"" (""IdInteresado"", ""IdUsuarioRemitente"", ""Texto"") VALUES (@int, @usr, @txt)";
                            using (var cmd = new NpgsqlCommand(sqlMsj, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@int", idInteresado);
                                cmd.Parameters.AddWithValue("@usr", idUser);
                                cmd.Parameters.AddWithValue("@txt", texto.Trim());
                                await cmd.ExecuteNonQueryAsync();
                            }

                            // 3. Actualizar fecha para que suba al inicio de la lista en el Panel
                            using (var cmdUpd = new NpgsqlCommand(@"UPDATE ""Tianguis_Interesados"" SET ""FechaActualizacion"" = NOW() WHERE ""IdInteresado"" = @int", conexion, trans))
                            {
                                cmdUpd.Parameters.AddWithValue("@int", idInteresado);
                                await cmdUpd.ExecuteNonQueryAsync();
                            }

                            await trans.CommitAsync();

                            // 4. BROADCAST EN TIEMPO REAL A LA SALA DE CHAT
                            await _hubContext.Clients.Group($"Chat_{idInteresado}").SendAsync("RecibirMensaje", texto.Trim(), idUser, idInteresado);
                        }
                        catch (Exception ex)
                        {
                            await trans.RollbackAsync();
                            return Json(new { success = false, message = ex.Message });
                        }
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
        // CREAR / EDITAR PUBLICACIÓN (VISTA UNIFICADA)
        // =========================================================
        [Authorize]
        [HttpGet]
        public async Task<IActionResult> Formulario(string sId = null) // Cambiamos int? id por string sId
        {
            // Creamos la variable original id
            int? id = string.IsNullOrEmpty(sId) ? (int?)null : Funciones.DesencriptarId(sId);
            ViewBag.Categorias = await ObtenerCategoriasActivas();

            if (!id.HasValue)
            {
                if (!User.TienePermiso(Modulo, PermisoLeer))
                {
                    MostrarMensaje("Sin Permiso de Acceso", "Lo sentimos, no tienes permiso de acceder a esta opción.", TipoMensaje.Alerta);
                    return RedirectToAction("Index");
                }

                // AQUÍ LE DAS EL VALOR POR DEFECTO
                var nuevoModelo = new TianguisCrearViewModel { TipoVenta = "SUB" };

                return View(nuevoModelo);
            }

            if (!User.TienePermiso(Modulo, PermisoEditar)) return RedirectToAction("Index");

            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            var modelo = new TianguisCrearViewModel();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // Verificamos si ya tiene ventas concretadas para saber si se puede eliminar
                    int totalVentas = 0;
                    using (var cmdVentas = new NpgsqlCommand(@"SELECT COUNT(*) FROM ""Tianguis_Ventas"" WHERE ""IdPublicacion"" = @pubId", conexion))
                    {
                        cmdVentas.Parameters.AddWithValue("@pubId", id.Value);
                        totalVentas = Convert.ToInt32(await cmdVentas.ExecuteScalarAsync());
                    }
                    ViewBag.PuedeEliminar = totalVentas == 0;

                    string sql = @"SELECT * FROM ""Tianguis_Publicaciones"" WHERE ""IdPublicacion"" = @id AND ""IdUsuarioVendedor"" = @uid";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id.Value);
                        cmd.Parameters.AddWithValue("@uid", idUser);

                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                if (r["IdEstado"].ToString() == "VEN" || r["IdEstado"].ToString() == "AGO")
                                {
                                    MostrarMensaje("Bloqueado", "No puedes editar publicaciones finalizadas.", TipoMensaje.Alerta);
                                    return RedirectToAction("Detalle", new { sId = Funciones.EncriptarId(id.Value) });
                                }

                                modelo.Titulo = r["Titulo"].ToString();
                                modelo.Descripcion = r["Descripcion"]?.ToString();
                                modelo.IdCategoria = (int)r["IdCategoria"];
                                modelo.PrecioBase = (decimal)r["PrecioBase"];
                                modelo.TipoVenta = r["IdTipoVenta"].ToString();
                                if (modelo.TipoVenta == "TIE" && modelo.PrecioBase == 0)
                                {
                                    modelo.TipoVenta = "REG";
                                }

                                // Pasamos el estatus de Activo al ViewBag para el botón de Pausar
                                ViewBag.PublicacionActiva = (bool)r["Activo"];
                                ViewBag.IdPublicacionEdicion = id.Value;
                            }
                            else
                            {
                                MostrarMensaje("Error", "Publicación no encontrada o sin permisos.", TipoMensaje.Error);
                                return RedirectToAction("Panel");
                            }
                        }
                    }
                    // NUEVO: Obtener fotos actuales para que la vista las renderice
                    List<string> fotosActuales = new List<string>();
                    using (var cmdImg = new NpgsqlCommand("SELECT \"UrlImagen\" FROM \"Tianguis_Imagenes\" WHERE \"IdPublicacion\"=@id ORDER BY \"Orden\"", conexion))
                    {
                        cmdImg.Parameters.AddWithValue("@id", id.Value);
                        using (var rImg = await cmdImg.ExecuteReaderAsync())
                            while (await rImg.ReadAsync()) fotosActuales.Add(rImg["UrlImagen"].ToString());
                    }
                    ViewBag.ImagenesExistentes = fotosActuales;
                }
                return View(modelo);
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); return RedirectToAction("Panel"); }
        }


        // =========================================================
        // NUEVO: PAUSAR / ACTIVAR PUBLICACIÓN
        // =========================================================
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AlternarEstadoActivo(int id)
        {
            // 1. VALIDACIÓN DE PERMISOS
            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Bloqueado", "No tienes permisos para modificar publicaciones.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 2. VALIDAR ESTADO CON BLOQUEO (FOR UPDATE)
                            string estadoPub = "";
                            bool estadoActual = false;

                            string sqlVal = @"SELECT ""IdEstado"", ""Activo"" FROM ""Tianguis_Publicaciones"" 
                                      WHERE ""IdPublicacion"" = @id AND ""IdUsuarioVendedor"" = @uid FOR UPDATE";

                            using (var cmdVal = new NpgsqlCommand(sqlVal, conexion, trans))
                            {
                                cmdVal.Parameters.AddWithValue("@id", id);
                                cmdVal.Parameters.AddWithValue("@uid", idUser);
                                using (var r = await cmdVal.ExecuteReaderAsync())
                                {
                                    if (await r.ReadAsync())
                                    {
                                        estadoPub = r["IdEstado"].ToString();
                                        estadoActual = (bool)r["Activo"];
                                    }
                                    else throw new Exception("Publicación no encontrada o sin permisos.");
                                }
                            }

                            // 3. REGLA DE NEGOCIO: No reactivar ni pausar si ya se vendió o agotó
                            if (estadoPub == "VEN" || estadoPub == "AGO")
                                throw new InvalidOperationException("No se puede cambiar la visibilidad de una publicación finalizada.");

                            bool nuevoEstado = !estadoActual;

                            // 4. ACTUALIZACIÓN SEGURA
                            string sqlUpd = @"UPDATE ""Tianguis_Publicaciones"" SET ""Activo"" = @nuevo WHERE ""IdPublicacion"" = @id";
                            using (var cmdUpd = new NpgsqlCommand(sqlUpd, conexion, trans))
                            {
                                cmdUpd.Parameters.AddWithValue("@nuevo", nuevoEstado);
                                cmdUpd.Parameters.AddWithValue("@id", id);
                                await cmdUpd.ExecuteNonQueryAsync();
                            }

                            // 5. REGISTRO EN BITÁCORA
                            string accionMsg = nuevoEstado ? "Reactivó visualización" : "Pausó visualización";
                            await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Editar, $"{accionMsg} de pub ID: {id}", ip, trans);

                            await trans.CommitAsync();
                            MostrarMensaje("Actualizado", nuevoEstado ? "La publicación vuelve a ser visible." : "La publicación se ha ocultado del tianguis.", TipoMensaje.Exito);
                        }
                        catch (Exception)
                        {
                            await trans.RollbackAsync();
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "No se pudo cambiar el estado: " + ex.Message, TipoMensaje.Error);
            }
            return RedirectToAction("Formulario", new { sId = Funciones.EncriptarId(id) });
        }


        // =========================================================
        // 5. EDITAR PUBLICACIÓN (GET)
        // =========================================================
        [Authorize]
        [HttpGet]
        public async Task<IActionResult> Editar(string sId) // Cambiamos int id por string sId
        {
            int id = Funciones.DesencriptarId(sId);

            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Sin Permiso de Acceso", "Lo sentimos, no tienes permiso de acceder a esta opción.", TipoMensaje.Alerta);
                return RedirectToAction("Index");
            }
            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);

            var modelo = new TianguisCrearViewModel();
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    string sql = @"SELECT * FROM ""Tianguis_Publicaciones"" WHERE ""IdPublicacion"" = @id AND ""IdUsuarioVendedor"" = @uid";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        cmd.Parameters.AddWithValue("@uid", idUser);

                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            if (await r.ReadAsync())
                            {
                                if (r["IdEstado"].ToString() != "ACT")
                                {
                                    MostrarMensaje("Bloqueado", "No puedes editar publicaciones reservadas o finalizadas.", TipoMensaje.Alerta);
                                    return RedirectToAction("Detalle", new { sId = Funciones.EncriptarId(id) });
                                }

                                modelo.Titulo = r["Titulo"].ToString();
                                modelo.Descripcion = r["Descripcion"]?.ToString();
                                modelo.IdCategoria = (int)r["IdCategoria"];
                                modelo.PrecioBase = (decimal)r["PrecioBase"];
                                modelo.TipoVenta = r["IdTipoVenta"].ToString();
                                if (modelo.TipoVenta == "TIE" && modelo.PrecioBase == 0)
                                {
                                    modelo.TipoVenta = "REG";
                                }
                                ViewBag.IdPublicacionEdicion = id;
                            }
                            else
                            {
                                MostrarMensaje("Error", "Publicación no encontrada o sin permisos.", TipoMensaje.Error);
                                return RedirectToAction("Panel");
                            }
                        }
                    }

                    // Obtener las fotos actuales para la "Edición Inteligente"
                    List<string> fotosActuales = new List<string>();
                    using (var cmdImg = new NpgsqlCommand("SELECT \"UrlImagen\" FROM \"Tianguis_Imagenes\" WHERE \"IdPublicacion\"=@id ORDER BY \"Orden\"", conexion))
                    {
                        cmdImg.Parameters.AddWithValue("@id", id);
                        using (var rImg = await cmdImg.ExecuteReaderAsync())
                            while (await rImg.ReadAsync()) fotosActuales.Add(rImg["UrlImagen"].ToString());
                    }
                    ViewBag.ImagenesExistentes = fotosActuales;
                }
                ViewBag.Categorias = await ObtenerCategoriasActivas();
                return View("Editar", modelo);
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                return RedirectToAction("Panel");
            }
        }

        // =========================================================
        // ELIMINAR PUBLICACIÓN (Y SUS ARCHIVOS FÍSICOS)
        // =========================================================
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EliminarPublicacion(int id)
        {
            if (!User.TienePermiso(Modulo, PermisoBorrar))
            {
                MostrarMensaje("Bloqueado", "No tienes los permisos necesarios para eliminar publicaciones.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    // Se requiere transacción para asegurar que la bitácora y el borrado ocurran juntos
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 2. VALIDAR EXISTENCIA, PROPIEDAD Y ESTADO CON BLOQUEO ANTI-CONCURRENCIA (Zero Trust)
                            string estadoPub = "";
                            string sqlVal = @"SELECT ""IdEstado"", ""IdUsuarioVendedor"" 
                                      FROM ""Tianguis_Publicaciones"" 
                                      WHERE ""IdPublicacion"" = @id FOR UPDATE";

                            using (var cmdVal = new NpgsqlCommand(sqlVal, conexion, trans))
                            {
                                cmdVal.Parameters.AddWithValue("@id", id);
                                using (var r = await cmdVal.ExecuteReaderAsync())
                                {
                                    if (await r.ReadAsync())
                                    {
                                        // Prevención de IDOR: Si existe, primero verificamos que sea el dueño
                                        if ((int)r["IdUsuarioVendedor"] != idUser)
                                            throw new UnauthorizedAccessException("No tienes permisos sobre esta publicación.");

                                        estadoPub = r["IdEstado"].ToString();
                                    }
                                    else
                                    {
                                        throw new Exception("La publicación no existe o ya fue eliminada.");
                                    }
                                }
                            }

                            // 3. REGLA DE NEGOCIO: No borrar si está reservada o vendida
                            if (estadoPub == "VEN" || estadoPub == "AGO" || estadoPub == "RES")
                            {
                                throw new InvalidOperationException("No puedes eliminar un artículo que se encuentra reservado, vendido o agotado.");
                            }

                            // 4. VALIDACIÓN DE HISTÓRICO DE VENTAS (Sin interpolación de strings)
                            using (var cmdVentas = new NpgsqlCommand(@"SELECT COUNT(*) FROM ""Tianguis_Ventas"" WHERE ""IdPublicacion"" = @id", conexion, trans))
                            {
                                cmdVentas.Parameters.AddWithValue("@id", id);
                                int ventas = Convert.ToInt32(await cmdVentas.ExecuteScalarAsync());
                                if (ventas > 0) throw new InvalidOperationException("No puedes eliminar un artículo que ya tiene ventas en su histórico.");
                            }

                            // 5. REGISTRO DE BITÁCORA ANTES DE DESTRUIR EL DATO
                            await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Borrar, $"Eliminó publicación física (ID: {id})", ip, trans);

                            // 6. BORRADO EN BASE DE DATOS
                            using (var cmdDel = new NpgsqlCommand(@"DELETE FROM ""Tianguis_Publicaciones"" WHERE ""IdPublicacion"" = @id AND ""IdUsuarioVendedor"" = @uid", conexion, trans))
                            {
                                cmdDel.Parameters.AddWithValue("@id", id);
                                cmdDel.Parameters.AddWithValue("@uid", idUser);
                                await cmdDel.ExecuteNonQueryAsync();
                            }

                            await trans.CommitAsync();

                            // 7. BORRADO FÍSICO EN CLOUDINARY (Fuera de la transacción de BD)
                            try
                            {
                                string folderPath = $"{sAmbiente}/Imágenes/Tianguis/{id}";
                                // Primero borramos todas las imágenes dentro de la carpeta
                                await _cloudinary.DeleteResourcesByPrefixAsync($"{folderPath}/");

                                // CORRECCIÓN: Se llama directo desde _cloudinary, sin el .Api
                                await _cloudinary.DeleteFolderAsync(folderPath);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error al borrar imágenes de Cloudinary: {ex.Message}");
                                // No lanzamos la excepción para no interrumpir al usuario, la BD ya se limpió.
                            }

                            MostrarMensaje("Eliminada", "La publicación y sus fotos fueron borradas permanentemente.", TipoMensaje.Exito);
                        }
                        catch (UnauthorizedAccessException uex)
                        {
                            await trans.RollbackAsync();
                            // Para evitar enumeración, mostramos un error genérico o redirigimos silenciosamente
                            MostrarMensaje("Bloqueado", uex.Message, TipoMensaje.Error);
                            return RedirectToAction("Panel");
                        }
                        catch (Exception ex)
                        {
                            await trans.RollbackAsync();
                            throw ex;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
                return RedirectToAction("Formulario", new { sId = Funciones.EncriptarId(id) });
            }

            return RedirectToAction("Panel");
        }

        // =========================================================
        // GUARDAR UNIFICADO (CREAR / EDITAR)
        // =========================================================
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Guardar(int? idPublicacion, TianguisCrearViewModel form, List<string> imagenesAEliminar)
        {
            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
            bool esEdicion = idPublicacion.HasValue && idPublicacion.Value > 0;

            if (esEdicion && !User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Sin Permiso", "No tienes permiso para editar publicaciones.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }
            if (!esEdicion && !User.TienePermiso(Modulo, PermisoCrear))
            {
                MostrarMensaje("Sin Permiso", "No tienes permiso para crear publicaciones.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }

            // Interceptar donación de la vista y convertirla en Venta Directa para la BD
            if (form.TipoVenta == "REG")
            {
                form.TipoVenta = "TIE";
                form.PrecioBase = 0;
            }

            // Validar que el precio sea mayor a 0 SOLO si no es una Venta Directa (ya que cubre las donaciones)
            if (form.PrecioBase <= 0 && form.TipoVenta != "TIE")
            {
                ModelState.AddModelError("PrecioBase", "El precio debe ser mayor a $0.");
            }

            // Las fotos solo son obligatorias si es una creación nueva
            if (!esEdicion && (form.Fotos == null || form.Fotos.Count == 0))
                ModelState.AddModelError("Fotos", "Debes subir al menos una foto.");
            // NUEVO: Validar peso, integridad y formato de las fotos ANTES de tocar la base de datos
            if (form.Fotos != null && form.Fotos.Count > 0)
            {
                // Lista de extensiones permitidas
                string[] extensionesValidas = { ".jpg", ".jpeg", ".png", ".webp" };
                foreach (var foto in form.Fotos)
                {
                    if (foto.Length == 0)
                    {
                        ModelState.AddModelError("Fotos", $"La imagen '{foto.FileName}' está vacía o corrupta.");
                        break;
                    }
                    if (foto.Length > 10 * 1024 * 1024)
                    {
                        ModelState.AddModelError("Fotos", $"La imagen '{foto.FileName}' pesa más de 10MB. Por favor, comprímela o elige otra.");
                        break;
                    }

                    //Bloqueo de HEIC y formatos no soportados
                    string extension = Path.GetExtension(foto.FileName).ToLower();
                    if (!extensionesValidas.Contains(extension))
                    {
                        ModelState.AddModelError("Fotos", $"El archivo '{foto.FileName}' tiene un formato no válido ({extension}).");
                        break;
                    }
                }
            }

            if (!ModelState.IsValid)
            {
                ViewBag.Categorias = await ObtenerCategoriasActivas();
                if (esEdicion) ViewBag.IdPublicacionEdicion = idPublicacion;
                var listaErrores = string.Join(" | ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
                MostrarMensaje("Datos Inválidos", $"Verifica la información: {listaErrores}", TipoMensaje.Error);
                return View("Formulario", form);
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
                            int idActual = 0;

                            if (esEdicion)
                            {
                                idActual = idPublicacion.Value;

                                // Parametrizado y con FOR UPDATE para prevenir Race Conditions
                                string sqlVal = @"SELECT ""IdEstado"", ""PrecioBase"" FROM ""Tianguis_Publicaciones"" WHERE ""IdPublicacion"" = @id AND ""IdUsuarioVendedor"" = @uid FOR UPDATE";

                                using (var cmdVal = new NpgsqlCommand(sqlVal, conexion, trans))
                                {
                                    cmdVal.Parameters.AddWithValue("@id", idActual);
                                    cmdVal.Parameters.AddWithValue("@uid", idUser);

                                    using (var r = await cmdVal.ExecuteReaderAsync())
                                    {
                                        if (await r.ReadAsync())
                                        {
                                            if (r["IdEstado"].ToString() != "ACT")
                                                throw new Exception("La publicación no puede ser editada porque su estado cambió (posible reserva en progreso).");

                                            // Prevenir el cambio entre Venta y Donación
                                            decimal precioEnBD = (decimal)r["PrecioBase"];

                                            if (precioEnBD == 0 && form.PrecioBase > 0)
                                                throw new Exception("No puedes asignarle un precio a un artículo que originalmente publicaste como donación.");

                                            if (precioEnBD > 0 && form.PrecioBase == 0)
                                                throw new Exception("No puedes convertir una venta existente en una donación (precio $0). Crea una nueva publicación.");
                                        }
                                        else
                                        {
                                            throw new Exception("Publicación no encontrada o sin permisos.");
                                        }
                                    }
                                }

                                string sqlUpd = @"UPDATE ""Tianguis_Publicaciones"" 
                                          SET ""Titulo"" = @tit, ""Descripcion"" = @desc, ""IdCategoria"" = @cat, ""PrecioBase"" = @prec 
                                          WHERE ""IdPublicacion"" = @id AND ""IdUsuarioVendedor"" = @uid";
                                using (var cmd = new NpgsqlCommand(sqlUpd, conexion, trans))
                                {
                                    cmd.Parameters.AddWithValue("@tit", form.Titulo);
                                    cmd.Parameters.AddWithValue("@desc", form.Descripcion ?? "");
                                    cmd.Parameters.AddWithValue("@cat", form.IdCategoria);
                                    cmd.Parameters.AddWithValue("@prec", form.PrecioBase);
                                    cmd.Parameters.AddWithValue("@id", idActual);
                                    cmd.Parameters.AddWithValue("@uid", idUser);
                                    await cmd.ExecuteNonQueryAsync();
                                }

                                // Eliminar fotos viejas en BD y Cloudinary
                                if (imagenesAEliminar != null && imagenesAEliminar.Count > 0)
                                {
                                    foreach (var urlImg in imagenesAEliminar)
                                    {
                                        using (var cmdDelImg = new NpgsqlCommand("DELETE FROM \"Tianguis_Imagenes\" WHERE \"IdPublicacion\" = @id AND \"UrlImagen\" = @url", conexion, trans))
                                        {
                                            cmdDelImg.Parameters.AddWithValue("@id", idActual);
                                            cmdDelImg.Parameters.AddWithValue("@url", urlImg);
                                            await cmdDelImg.ExecuteNonQueryAsync();
                                        }

                                        // Destruir archivo individual en Cloudinary
                                        try
                                        {
                                            var uri = new Uri(urlImg);
                                            var segments = uri.Segments;
                                            int uploadIndex = Array.IndexOf(segments, "upload/");

                                            // Extraemos la ruta interna (Public ID) ignorando el dominio y la versión
                                            if (uploadIndex >= 0 && segments.Length > uploadIndex + 2)
                                            {
                                                string publicIdWithExtension = string.Join("", segments.Skip(uploadIndex + 2));
                                                string publicId = Path.ChangeExtension(publicIdWithExtension, null).Replace("%20", " ").Trim('/');

                                                await _cloudinary.DestroyAsync(new DeletionParams(publicId));
                                            }
                                        }
                                        catch { /* Ignoramos si falla para no detener la edición */ }
                                    }
                                }

                                await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Editar, $"Editó publicación {idActual}", ip, trans);
                            }
                            else
                            {
                                string sqlPub = @"INSERT INTO ""Tianguis_Publicaciones"" 
                            (""IdUsuarioVendedor"", ""IdCategoria"", ""Titulo"", ""Descripcion"", ""IdTipoVenta"", ""PrecioBase"", ""IdEstado"", ""TieneTallas"", ""FechaExpiracion"", ""Activo"")
                            VALUES (@uid, @cat, @tit, @desc, @tipo, @prec, 'ACT', @tallas, @exp, TRUE) RETURNING ""IdPublicacion""";

                                using (var cmd = new NpgsqlCommand(sqlPub, conexion, trans))
                                {
                                    cmd.Parameters.AddWithValue("@uid", idUser);
                                    cmd.Parameters.AddWithValue("@cat", form.IdCategoria);
                                    cmd.Parameters.AddWithValue("@tit", form.Titulo);
                                    cmd.Parameters.AddWithValue("@desc", form.Descripcion ?? "");
                                    cmd.Parameters.AddWithValue("@tipo", form.TipoVenta);
                                    cmd.Parameters.AddWithValue("@prec", form.PrecioBase);
                                    cmd.Parameters.AddWithValue("@tallas", form.TieneTallas);
                                    cmd.Parameters.AddWithValue("@exp", (object)form.FechaExpiracion ?? DBNull.Value);
                                    idActual = (int)await cmd.ExecuteScalarAsync();
                                }

                                if (form.TipoVenta == "TIE" && !string.IsNullOrEmpty(form.TallasJson))
                                {
                                    var listaTallas = JsonConvert.DeserializeObject<List<TallaInput>>(form.TallasJson);
                                    foreach (var t in listaTallas)
                                    {
                                        string sqlTal = @"INSERT INTO ""Tianguis_Tallas"" (""IdPublicacion"", ""NombreTalla"", ""Stock"") VALUES (@id, @nom, @stk)";
                                        using (var cmdT = new NpgsqlCommand(sqlTal, conexion, trans))
                                        {
                                            cmdT.Parameters.AddWithValue("@id", idActual);
                                            cmdT.Parameters.AddWithValue("@nom", string.IsNullOrWhiteSpace(t.Nombre) ? "N/A" : t.Nombre);
                                            cmdT.Parameters.AddWithValue("@stk", t.Cantidad > 0 ? t.Cantidad : 1);
                                            await cmdT.ExecuteNonQueryAsync();
                                        }
                                    }
                                }
                                else if (form.TipoVenta == "SUB")
                                {
                                    using (var cmdT = new NpgsqlCommand(@"INSERT INTO ""Tianguis_Tallas"" (""IdPublicacion"", ""NombreTalla"", ""Stock"") VALUES (@id, 'Única', 1)", conexion, trans))
                                    {
                                        cmdT.Parameters.AddWithValue("@id", idActual);
                                        await cmdT.ExecuteNonQueryAsync();
                                    }
                                }

                                await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Crear, $"Publicó en Tianguis: {form.Titulo} (ID: {idActual})", ip, trans);
                            }

                            if (form.Fotos != null && form.Fotos.Count > 0)
                            {
                                int fotosActualesDB = 0;
                                short maxOrden = 0;

                                using (var cmdCount = new NpgsqlCommand(@"SELECT COUNT(*), COALESCE(MAX(""Orden""), 0) FROM ""Tianguis_Imagenes"" WHERE ""IdPublicacion"" = @id", conexion, trans))
                                {
                                    cmdCount.Parameters.AddWithValue("@id", idActual);
                                    using (var r = await cmdCount.ExecuteReaderAsync())
                                    {
                                        if (await r.ReadAsync())
                                        {
                                            fotosActualesDB = Convert.ToInt32(r[0]);
                                            maxOrden = Convert.ToInt16(r[1]);
                                        }
                                    }
                                }

                                foreach (var foto in form.Fotos)
                                {
                                    if (foto.Length == 0 || foto.Length > 10 * 1024 * 1024)
                                    {
                                        throw new InvalidDataException($"El archivo {foto.FileName} es inválido o excede los 10MB. Operación abortada por seguridad.");
                                    }
                                    if (fotosActualesDB >= 5) break;

                                    fotosActualesDB++;
                                    maxOrden++;

                                    string urlFinal = "";

                                    // Comprimir en memoria RAM sin tocar el disco duro
                                    using (var memoryStream = new MemoryStream())
                                    {
                                        using (var image = await Image.LoadAsync(foto.OpenReadStream()))
                                        {
                                            const int MaxWidth = 1200;
                                            if (image.Width > MaxWidth)
                                            {
                                                int newHeight = (int)((double)image.Height / image.Width * MaxWidth);
                                                image.Mutate(x => x.Resize(MaxWidth, newHeight));
                                            }
                                            var encoder = new JpegEncoder { Quality = 75 };
                                            await image.SaveAsync(memoryStream, encoder);
                                        }

                                        // Reiniciar la posición del stream para que Cloudinary lo lea desde el principio
                                        memoryStream.Position = 0;

                                        // Subir a Cloudinary
                                        var uploadParams = new ImageUploadParams()
                                        {
                                            File = new FileDescription(foto.FileName, memoryStream),
                                            Folder = $"{sAmbiente}/Imágenes/Tianguis/{idActual}",
                                            Transformation = new Transformation().FetchFormat("auto") // Sirve en WebP automáticamente
                                        };

                                        var uploadResult = await _cloudinary.UploadAsync(uploadParams);
                                        urlFinal = uploadResult.SecureUrl.ToString();
                                    }

                                    // Guardar URL en Base de Datos
                                    using (var cmdImg = new NpgsqlCommand("INSERT INTO \"Tianguis_Imagenes\" (\"IdPublicacion\", \"UrlImagen\", \"Orden\") VALUES (@id, @url, @ord)", conexion, trans))
                                    {
                                        cmdImg.Parameters.AddWithValue("@id", idActual);
                                        cmdImg.Parameters.AddWithValue("@url", urlFinal);
                                        cmdImg.Parameters.AddWithValue("@ord", maxOrden);
                                        await cmdImg.ExecuteNonQueryAsync();
                                    }
                                }
                            }

                            await trans.CommitAsync();
                            MostrarMensaje(esEdicion ? "Actualizado" : "Publicado", esEdicion ? "La publicación se guardó correctamente." : "Tu artículo ya está disponible.", TipoMensaje.Exito);
                            return RedirectToAction("Detalle", new { sId = Funciones.EncriptarId(idActual) });
                        }
                        catch (Exception ex) { await trans.RollbackAsync(); throw ex; }
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", "Ocurrió un error: " + ex.Message, TipoMensaje.Error);
                ViewBag.Categorias = await ObtenerCategoriasActivas();
                if (esEdicion) ViewBag.IdPublicacionEdicion = idPublicacion;
                return View("Formulario", form);
            }
        }

        // =========================================================
        // 7. INICIAR INTERÉS
        // =========================================================
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> IniciarInteres(int idPublicacion, decimal? montoOferta, int? idTalla, int? cantidad)
        {
            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";
            decimal ofertaReal = montoOferta ?? 0;
            int cantidadReal = cantidad ?? 1;

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 1. Validar la publicación de forma exhaustiva 
                            string tipoVenta = ""; int vendedorId = 0; decimal precioBase = 0;
                            string emailVendedor = ""; string nombreVendedor = ""; string tituloPub = "";

                            string sqlPub = @"SELECT p.""IdTipoVenta"", p.""IdUsuarioVendedor"", p.""PrecioBase"", p.""Titulo"", 
                         u.""Email"", u.""NombreCompleto""
                  FROM ""Tianguis_Publicaciones"" p 
                  JOIN ""Sist_Usuarios"" u ON p.""IdUsuarioVendedor"" = u.""Id_Usuario""
                  WHERE p.""IdPublicacion""=@id AND p.""IdEstado""='ACT' AND p.""Activo"" = TRUE";

                            using (var cmdPub = new NpgsqlCommand(sqlPub, conexion, trans))
                            {
                                cmdPub.Parameters.AddWithValue("@id", idPublicacion);
                                using (var r = await cmdPub.ExecuteReaderAsync())
                                {
                                    if (await r.ReadAsync())
                                    {
                                        tipoVenta = r["IdTipoVenta"].ToString();
                                        vendedorId = (int)r["IdUsuarioVendedor"];
                                        precioBase = (decimal)r["PrecioBase"];
                                        tituloPub = r["Titulo"].ToString();
                                        emailVendedor = r["Email"] != DBNull.Value ? r["Email"].ToString() : "";
                                        nombreVendedor = r["NombreCompleto"] != DBNull.Value ? r["NombreCompleto"].ToString() : "Vendedor";
                                    }
                                    else throw new Exception("La publicación no está activa o no existe.");
                                }
                            }

                            if (vendedorId == idUser) throw new Exception("No puedes ofertar en tu propia publicación.");

                            // 2. REGLA DE NEGOCIO: Validar Oferta en Subastas
                            if (tipoVenta == "SUB")
                            {
                                decimal ofertaMinima = precioBase;
                                bool hayOfertasPrevias = false; // Bandera para saber si alguien ya ofertó

                                using (var cmdMax = new NpgsqlCommand(@"SELECT MAX(""OfertaActual"") FROM ""Tianguis_Interesados"" WHERE ""IdPublicacion""=@id", conexion, trans))
                                {
                                    cmdMax.Parameters.AddWithValue("@id", idPublicacion);
                                    var objMax = await cmdMax.ExecuteScalarAsync();
                                    if (objMax != DBNull.Value)
                                    {
                                        ofertaMinima = (decimal)objMax;
                                        hayOfertasPrevias = true;
                                    }
                                }

                                // Si ya hay alguien peleando, debes superar la oferta
                                if (hayOfertasPrevias)
                                {
                                    if (ofertaReal <= ofertaMinima)
                                    {
                                        throw new Exception($"Ya existen interesados. Tu oferta debe ser estrictamente mayor a ${ofertaMinima:N2}.");
                                    }
                                }
                                else
                                {
                                    // Si eres el primero, puedes ofertar exactamente el precio base
                                    if (ofertaReal < ofertaMinima)
                                    {
                                        throw new Exception($"Al ser el primero, tu oferta debe ser de al menos el precio base de ${ofertaMinima:N2}.");
                                    }
                                }

                            }

                            // 3. REGLA DE NEGOCIO: Validar Stock real en Venta Directa
                            if (tipoVenta == "TIE" && idTalla.HasValue)
                            {
                                using (var cmdStock = new NpgsqlCommand(@"SELECT ""Stock"" FROM ""Tianguis_Tallas"" WHERE ""IdTalla""=@idt AND ""IdPublicacion""=@pub FOR UPDATE", conexion, trans))
                                {
                                    cmdStock.Parameters.AddWithValue("@idt", idTalla.Value);
                                    cmdStock.Parameters.AddWithValue("@pub", idPublicacion); // Cross-Reference check
                                    var stockActual = await cmdStock.ExecuteScalarAsync();

                                    if (stockActual == null || Convert.ToInt32(stockActual) < cantidadReal)
                                        throw new Exception("No hay stock suficiente para procesar tu solicitud.");
                                }
                            }

                            int numInteresados = 0;
                            using (var cmdCount = new NpgsqlCommand(@"SELECT COUNT(*) FROM ""Tianguis_Interesados"" WHERE ""IdPublicacion"" = @pub", conexion, trans))
                            {
                                cmdCount.Parameters.AddWithValue("@pub", idPublicacion);
                                numInteresados = Convert.ToInt32(await cmdCount.ExecuteScalarAsync());
                            }
                            string alias = $"Interesado #{numInteresados + 1}";

                            string sqlInt = @"INSERT INTO ""Tianguis_Interesados"" 
                        (""IdPublicacion"", ""IdUsuarioComprador"", ""AliasAnonimo"", ""OfertaActual"", ""EsMejorOferta"", ""IdTallaSolicitada"", ""CantidadSolicitada"", ""IdEstadoTrato"")
                        VALUES (@pub, @uid, @alias, @ofert, @mejor, @idt, @cant, 'INT') RETURNING ""IdInteresado""";

                            int idInteresado;
                            using (var cmd = new NpgsqlCommand(sqlInt, conexion, trans))
                            {
                                cmd.Parameters.AddWithValue("@pub", idPublicacion);
                                cmd.Parameters.AddWithValue("@uid", idUser);
                                cmd.Parameters.AddWithValue("@alias", alias);
                                cmd.Parameters.AddWithValue("@ofert", tipoVenta == "SUB" ? ofertaReal : 0);
                                cmd.Parameters.AddWithValue("@mejor", tipoVenta == "SUB");
                                cmd.Parameters.AddWithValue("@idt", (object)idTalla ?? DBNull.Value);
                                cmd.Parameters.AddWithValue("@cant", cantidadReal);
                                idInteresado = (int)await cmd.ExecuteScalarAsync();
                            }

                            // Usamos las variables seguras para el texto del mensaje
                            string msgInicial = tipoVenta == "SUB" ? $"¡Hola! He lanzado una oferta de ${ofertaReal}." : $"¡Hola! Me interesa este artículo (Cant: {cantidadReal}).";
                            using (var cmdMsj = new NpgsqlCommand("INSERT INTO \"Tianguis_Mensajes\" (\"IdInteresado\", \"IdUsuarioRemitente\", \"Texto\") VALUES (@int, @usr, @txt)", conexion, trans))
                            {
                                cmdMsj.Parameters.AddWithValue("@int", idInteresado);
                                cmdMsj.Parameters.AddWithValue("@usr", idUser);
                                cmdMsj.Parameters.AddWithValue("@txt", msgInicial);
                                await cmdMsj.ExecuteNonQueryAsync();
                            }

                            await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Editar, $"Inició trato en publicación {idPublicacion}", ip, trans);
                            await trans.CommitAsync();

                            if (!string.IsNullOrEmpty(emailVendedor))
                            {
                                string asunto = "¡Tienes un nuevo interesado en el Tianguis!";
                                string msjHtml = $@"
        <h2 style='color:#00B8D4;'>¡Buenas noticias, {nombreVendedor}!</h2>
        <p>Alguien está interesado y ha iniciado una negociación por tu artículo: <strong>{tituloPub}</strong>.</p>
        <p>Ingresa al panel del sistema para revisar su oferta y responderle en el chat anónimo.</p>";
                                try { await Funciones.EnviarCorreo(_configuration, emailVendedor, asunto, msjHtml); } catch { /* Se ignora si falla el SMTP */ }
                            }

                            MostrarMensaje("¡Éxito!", "Has abierto el chat con el vendedor.", TipoMensaje.Exito);
                        }
                        catch (Exception ex) { await trans.RollbackAsync(); throw ex; }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            return RedirectToAction("Detalle", new { sId = Funciones.EncriptarId(idPublicacion) });
        }

        // =========================================================
        // 7. RESERVAR STOCK (Con Auto-Cancelación Inteligente)
        // =========================================================
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReservarStock(int idInteresado, int idPublicacion, int? nuevaTalla, int? nuevaCantidad)
        {
            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "Necesitas permisos de edición para realizar ésta acción.", TipoMensaje.Error);
                return RedirectToAction("Index");
            }
            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            try
            {
                List<int> chatsNotificar = new List<int> { idInteresado };

                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 1. Bloqueamos la publicación y validamos que soy el DUEÑO
                            string tipoVenta = "";
                            string sqlPub = @"SELECT ""IdUsuarioVendedor"", ""IdTipoVenta"" FROM ""Tianguis_Publicaciones"" WHERE ""IdPublicacion""=@pub FOR UPDATE";
                            using (var cmdPub = new NpgsqlCommand(sqlPub, conexion, trans))
                            {
                                cmdPub.Parameters.AddWithValue("@pub", idPublicacion);
                                using (var r = await cmdPub.ExecuteReaderAsync())
                                {
                                    if (await r.ReadAsync())
                                    {
                                        if ((int)r["IdUsuarioVendedor"] != idUser)
                                            throw new UnauthorizedAccessException("No tienes permiso sobre esta publicación.");
                                        tipoVenta = r["IdTipoVenta"].ToString();
                                    }
                                    else throw new Exception("Publicación no encontrada.");
                                }
                            }

                            // 2. Validamos que el interesado PERTENECE a esta publicación y extraemos datos
                            int? idTalla = null;
                            int cant = 1;
                            decimal oferta = 0; // Extraemos la oferta para los correos
                            string estadoActual = "";
                            string emailComprador = ""; string nombreComprador = ""; string tituloPub = "";

                            string sqlInt = @"SELECT i.""IdTallaSolicitada"", i.""CantidadSolicitada"", i.""IdEstadoTrato"", i.""OfertaActual"", 
                            u.""Email"", u.""NombreCompleto"", p.""Titulo""
                    FROM ""Tianguis_Interesados"" i 
                    JOIN ""Sist_Usuarios"" u ON i.""IdUsuarioComprador"" = u.""Id_Usuario""
                    JOIN ""Tianguis_Publicaciones"" p ON i.""IdPublicacion"" = p.""IdPublicacion""
                    WHERE i.""IdInteresado""=@int AND i.""IdPublicacion""=@pub FOR UPDATE";

                            using (var cmdI = new NpgsqlCommand(sqlInt, conexion, trans))
                            {
                                cmdI.Parameters.AddWithValue("@int", idInteresado);
                                cmdI.Parameters.AddWithValue("@pub", idPublicacion);
                                using (var r = await cmdI.ExecuteReaderAsync())
                                {
                                    if (await r.ReadAsync())
                                    {
                                        estadoActual = r["IdEstadoTrato"].ToString();
                                        idTalla = r["IdTallaSolicitada"] as int?;
                                        cant = r["CantidadSolicitada"] != DBNull.Value ? (int)r["CantidadSolicitada"] : 1;
                                        oferta = (decimal)r["OfertaActual"]; 
                                        emailComprador = r["Email"] != DBNull.Value ? r["Email"].ToString() : "";
                                        nombreComprador = r["NombreCompleto"] != DBNull.Value ? r["NombreCompleto"].ToString() : "Comprador";
                                        tituloPub = r["Titulo"].ToString();
                                    }
                                    else throw new Exception("El interesado no corresponde a esta publicación.");
                                }
                            }

                            if (estadoActual == "RES" || estadoActual == "COM")
                                throw new InvalidOperationException("Este trato ya está reservado o cerrado.");

                            // ==========================================
                            // NUEVO FLUJO DE AJUSTE MANUAL DE RESERVA
                            // ==========================================
                            if (nuevaCantidad.HasValue && nuevaCantidad.Value <= 0)
                                throw new Exception("La cantidad debe ser mayor a cero.");

                            if (nuevaTalla.HasValue) idTalla = nuevaTalla.Value;
                            if (nuevaCantidad.HasValue && nuevaCantidad.Value > 0) cant = nuevaCantidad.Value;

                            // Validar que la talla pertenezca a la publicación (Seguridad de Integridad)
                            if (idTalla.HasValue)
                            {
                                using (var cmdCheck = new NpgsqlCommand(@"SELECT 1 FROM ""Tianguis_Tallas"" WHERE ""IdTalla""=@idt AND ""IdPublicacion""=@pub", conexion, trans))
                                {
                                    cmdCheck.Parameters.AddWithValue("@idt", idTalla.Value);
                                    cmdCheck.Parameters.AddWithValue("@pub", idPublicacion);
                                    if (await cmdCheck.ExecuteScalarAsync() == null)
                                        throw new Exception("La talla seleccionada no es válida para este producto.");
                                }

                                // Intentar descontar stock (Validación de existencia de piezas)
                                using (var cmdS = new NpgsqlCommand(@"UPDATE ""Tianguis_Tallas"" 
                                  SET ""Stock"" = ""Stock"" - @cant 
                                  WHERE ""IdTalla""=@idt 
                                  AND ""Stock"" >= @cant", conexion, trans))
                                {
                                    cmdS.Parameters.AddWithValue("@cant", cant);
                                    cmdS.Parameters.AddWithValue("@idt", idTalla.Value);

                                    int filasAfectadas = await cmdS.ExecuteNonQueryAsync();

                                    if (filasAfectadas == 0)
                                        throw new InvalidOperationException($"Stock insuficiente. No es posible apartar {cant} unidades de esta talla.");
                                }
                            }
                            else if (tipoVenta == "TIE")
                            {
                                throw new Exception("Es necesario seleccionar una talla para productos de venta directa.");
                            }

                            // Actualizar la tabla Tianguis_Interesados ANTES de descontar stock
                            if (tipoVenta == "TIE" && (nuevaTalla.HasValue || nuevaCantidad.HasValue))
                            {
                                string sqlUpdIntDatos = @"UPDATE ""Tianguis_Interesados"" SET ""IdTallaSolicitada"" = @idT, ""CantidadSolicitada"" = @cant WHERE ""IdInteresado"" = @int";
                                using (var cmdUpdIntDatos = new NpgsqlCommand(sqlUpdIntDatos, conexion, trans))
                                {
                                    cmdUpdIntDatos.Parameters.AddWithValue("@idT", (object)idTalla ?? DBNull.Value);
                                    cmdUpdIntDatos.Parameters.AddWithValue("@cant", cant);
                                    cmdUpdIntDatos.Parameters.AddWithValue("@int", idInteresado);
                                    await cmdUpdIntDatos.ExecuteNonQueryAsync();
                                }
                            }

                            // 3. AUTO-CANCELACIÓN: Si es Subasta, quitamos las reservas a los demás automáticamente
                            if (tipoVenta == "SUB")
                            {
                                string sqlBuscarOtros = @"SELECT ""IdInteresado"", ""IdTallaSolicitada"", ""CantidadSolicitada"" 
                                          FROM ""Tianguis_Interesados"" 
                                          WHERE ""IdPublicacion""=@pub AND ""IdEstadoTrato""='RES' AND ""IdInteresado""!=@int";

                                var otrosReservados = new List<(int idInt, int? idT, int cant)>();
                                using (var cmdBus = new NpgsqlCommand(sqlBuscarOtros, conexion, trans))
                                {
                                    cmdBus.Parameters.AddWithValue("@pub", idPublicacion);
                                    cmdBus.Parameters.AddWithValue("@int", idInteresado);
                                    using (var r = await cmdBus.ExecuteReaderAsync())
                                    {
                                        while (await r.ReadAsync())
                                        {
                                            otrosReservados.Add((
                                                (int)r["IdInteresado"],
                                                r["IdTallaSolicitada"] as int?,
                                                r["CantidadSolicitada"] != DBNull.Value ? (int)r["CantidadSolicitada"] : 1
                                            ));
                                        }
                                    }
                                }

                                // Revertimos a todos los que tenían reserva previa
                                foreach (var otro in otrosReservados)
                                {
                                    // Devolvemos el stock a la BD
                                    if (otro.idT.HasValue)
                                    {
                                        using (var cmdR = new NpgsqlCommand(@"UPDATE ""Tianguis_Tallas"" SET ""Stock"" = ""Stock"" + @cant WHERE ""IdTalla"" = @idT", conexion, trans))
                                        {
                                            cmdR.Parameters.AddWithValue("@cant", otro.cant);
                                            cmdR.Parameters.AddWithValue("@idT", otro.idT.Value);
                                            await cmdR.ExecuteNonQueryAsync();
                                        }
                                    }
                                    // Les regresamos su estado a 'INT' (Interesados normales)
                                    using (var cmdU = new NpgsqlCommand(@"UPDATE ""Tianguis_Interesados"" SET ""IdEstadoTrato"" = 'INT' WHERE ""IdInteresado"" = @int", conexion, trans))
                                    {
                                        cmdU.Parameters.AddWithValue("@int", otro.idInt);
                                        await cmdU.ExecuteNonQueryAsync();
                                    }
                                    // Agregamos a la lista de SignalR para que su pantalla se recargue
                                    chatsNotificar.Add(otro.idInt);
                                }
                            }

                            // 4. ACTUALIZAR ESTADO DEL TRATO DEL NUEVO A 'RES'
                            using (var cmdUpdI = new NpgsqlCommand(@"UPDATE ""Tianguis_Interesados"" SET ""IdEstadoTrato""='RES' WHERE ""IdInteresado""=@int", conexion, trans))
                            {
                                cmdUpdI.Parameters.AddWithValue("@int", idInteresado);
                                await cmdUpdI.ExecuteNonQueryAsync();
                            }

                            // 5. ACTUALIZAR ESTADO DE LA PUBLICACIÓN
                            string estadoFinalPub = "ACT";
                            if (tipoVenta == "SUB")
                            {
                                estadoFinalPub = "RES"; // Subastas bloquean la publicación entera
                            }
                            else
                            {
                                int stockRestante = 0;
                                using (var cmdStock = new NpgsqlCommand(@"SELECT COALESCE(SUM(""Stock""), 0) FROM ""Tianguis_Tallas"" WHERE ""IdPublicacion""=@pub", conexion, trans))
                                {
                                    cmdStock.Parameters.AddWithValue("@pub", idPublicacion);
                                    stockRestante = Convert.ToInt32(await cmdStock.ExecuteScalarAsync());
                                }
                                estadoFinalPub = stockRestante > 0 ? "ACT" : "RES"; // Tiendas solo se bloquean si el stock llega a cero
                            }

                            using (var cmdUpdP = new NpgsqlCommand(@"UPDATE ""Tianguis_Publicaciones"" SET ""IdEstado""=@est WHERE ""IdPublicacion""=@pub", conexion, trans))
                            {
                                cmdUpdP.Parameters.AddWithValue("@est", estadoFinalPub);
                                cmdUpdP.Parameters.AddWithValue("@pub", idPublicacion);
                                await cmdUpdP.ExecuteNonQueryAsync();
                            }

                            // 6. REGISTRO DE BITÁCORA CON EL AJUSTE MANUAL
                            string msjBitacora = $"Reservó trato {idInteresado}";
                            if (nuevaTalla.HasValue || nuevaCantidad.HasValue) msjBitacora += $" (Ajuste: Talla {idTalla}, Cant: {cant})";

                            await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Editar, msjBitacora, ip, trans);

                            await trans.CommitAsync();

                            if (!string.IsNullOrEmpty(emailComprador))
                            {
                                // Texto de correo dinámico basado en modalidad
                                string asunto = tipoVenta == "TIE" && oferta == 0 ? "¡Donación Reservada para ti!" : "¡Artículo Reservado en el Tianguis!";
                                string textoCoordinacion = tipoVenta == "TIE" && oferta == 0 ? "coordina la entrega" : "coordina el pago y la entrega";

                                string msjHtml = $@"
<h2 style='color:#ffc107;'>¡Reserva confirmada, {nombreComprador}!</h2>
<p>El vendedor ha aceptado tu trato y ha reservado el artículo: <strong>{tituloPub}</strong> exclusivamente para ti.</p>
<p>Por favor, {textoCoordinacion} directamente a través del chat en la plataforma.</p>";
                                try { await Funciones.EnviarCorreo(_configuration, emailComprador, asunto, msjHtml); } catch { }
                            }

                            MostrarMensaje("Reservado", "Artículo apartado temporalmente para este usuario.", TipoMensaje.Exito);
                        }
                        catch (UnauthorizedAccessException uex)
                        {
                            await trans.RollbackAsync();
                            MostrarMensaje("Bloqueado", uex.Message, TipoMensaje.Error);
                            return RedirectToAction("Index"); // Expulsamos al que intentó inyectar un ID ajeno
                        }
                        catch (InvalidOperationException ioe)
                        {
                            await trans.RollbackAsync();
                            MostrarMensaje("Stock Insuficiente", ioe.Message, TipoMensaje.Alerta);
                            return RedirectToAction("Detalle", new { sId = Funciones.EncriptarId(idPublicacion), sChatId = Funciones.EncriptarId(idInteresado) });
                        }
                        catch (Exception)
                        {
                            await trans.RollbackAsync();
                            throw;
                        }
                    }
                }

                // 7. AVISAR A TODOS LOS AFECTADOS POR SIGNALR
                if (_hubContext != null)
                {
                    foreach (var chatId in chatsNotificar)
                    {
                        await _hubContext.Clients.Group($"Chat_{chatId}").SendAsync("RecargarPagina");
                    }
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Detalle", new { sId = Funciones.EncriptarId(idPublicacion), sChatId = Funciones.EncriptarId(idInteresado) });
        }

        // =========================================================
        // 8. REABRIR PUBLICACIÓN (Avisando por SignalR)
        // =========================================================
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReabrirPublicacion(int idInteresado, int idPublicacion)
        {
            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "Necesitas permisos de edición para realizar ésta acción.", TipoMensaje.Error);
                return RedirectToAction("Index"); 
            }
            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 1. ZERO TRUST: Validar propiedad ANTES de hacer nada
                            using (var cmdPub = new NpgsqlCommand(@"SELECT 1 FROM ""Tianguis_Publicaciones"" WHERE ""IdPublicacion""=@pub AND ""IdUsuarioVendedor""=@uid FOR UPDATE", conexion, trans))
                            {
                                cmdPub.Parameters.AddWithValue("@pub", idPublicacion);
                                cmdPub.Parameters.AddWithValue("@uid", idUser);
                                if (await cmdPub.ExecuteScalarAsync() == null)
                                    throw new UnauthorizedAccessException("No eres el dueño de esta publicación.");
                            }
                            int? idT = null;
                            int cant = 1;
                            string estadoTratoActual = "";

                            // 1. Enlazamos IdInteresado e IdPublicacion para evitar ataques cruzados, FOR UPDATE para evitar Race Conditions.
                            string sqlInt = @"SELECT ""IdTallaSolicitada"", ""CantidadSolicitada"", ""IdEstadoTrato"" 
                                              FROM ""Tianguis_Interesados"" 
                                              WHERE ""IdInteresado"" = @int AND ""IdPublicacion"" = @pub FOR UPDATE";

                            using (var cmdI = new NpgsqlCommand(sqlInt, conexion, trans))
                            {
                                cmdI.Parameters.AddWithValue("@int", idInteresado);
                                cmdI.Parameters.AddWithValue("@pub", idPublicacion);
                                using (var r = await cmdI.ExecuteReaderAsync())
                                {
                                    if (await r.ReadAsync())
                                    {
                                        estadoTratoActual = r["IdEstadoTrato"].ToString();
                                        if (r["IdTallaSolicitada"] != DBNull.Value)
                                        {
                                            idT = (int)r["IdTallaSolicitada"];
                                            cant = r["CantidadSolicitada"] != DBNull.Value ? (int)r["CantidadSolicitada"] : 1;
                                        }
                                    }
                                    else throw new Exception("Negociación no válida o no corresponde a esta publicación.");
                                }
                            }

                            // 3. VALIDACIÓN ESTRICTA: Solo devolver stock si estaba realmente RESERVADO
                            if (estadoTratoActual != "RES")
                                throw new Exception("La negociación no se encuentra reservada, el stock no puede ser modificado.");

                            if (idT.HasValue)
                            {
                                string sqlRestore = @"UPDATE ""Tianguis_Tallas"" SET ""Stock"" = ""Stock"" + @cant WHERE ""IdTalla"" = @idT";
                                using (var cmdR = new NpgsqlCommand(sqlRestore, conexion, trans))
                                {
                                    cmdR.Parameters.AddWithValue("@cant", cant);
                                    cmdR.Parameters.AddWithValue("@idT", idT.Value);
                                    await cmdR.ExecuteNonQueryAsync();
                                }
                            }

                            string sqlUpdateInt = @"UPDATE ""Tianguis_Interesados"" SET ""IdEstadoTrato"" = 'INT' WHERE ""IdInteresado"" = @int";
                            using (var cmdUpdInt = new NpgsqlCommand(sqlUpdateInt, conexion, trans))
                            {
                                cmdUpdInt.Parameters.AddWithValue("@int", idInteresado);
                                await cmdUpdInt.ExecuteNonQueryAsync();
                            }

                            string sqlUpdatePub = @"UPDATE ""Tianguis_Publicaciones"" SET ""IdEstado"" = 'ACT' WHERE ""IdPublicacion"" = @pub";
                            using (var cmdUpdPub = new NpgsqlCommand(sqlUpdatePub, conexion, trans))
                            {
                                cmdUpdPub.Parameters.AddWithValue("@pub", idPublicacion);
                                await cmdUpdPub.ExecuteNonQueryAsync();
                            }

                            await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Editar, $"Reabrió pub {idPublicacion}, canceló reserva {idInteresado}", ip, trans);

                            await trans.CommitAsync();
                            MostrarMensaje("Reabierta", "La publicación vuelve a estar activa y el stock se restauró.", TipoMensaje.Exito);
                        }
                        catch (Exception)
                        {
                            await trans.RollbackAsync();
                            throw;
                        }
                    }
                }

                // NUEVO: Avisarle a la sala de chat que hubo un cambio (SignalR)
                if (_hubContext != null)
                {
                    await _hubContext.Clients.Group($"Chat_{idInteresado}").SendAsync("RecargarPagina");
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Detalle", new { sId = Funciones.EncriptarId(idPublicacion), sChatId = Funciones.EncriptarId(idInteresado) });
        }

        // =========================================================
        // 9. CONFIRMAR VENTA (Avisando por SignalR)
        // =========================================================
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmarVenta(int idInteresado, int idPublicacion)
        {
            if (!User.TienePermiso(Modulo, PermisoEditar))
            {
                MostrarMensaje("Error", "Necesitas permisos de edición para realizar ésta acción.", TipoMensaje.Error);
                return RedirectToAction("Index"); 
            }
            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "::1";

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    using (var trans = await conexion.BeginTransactionAsync())
                    {
                        try
                        {
                            // 1. Validamos propiedad y bloqueamos (Evita robo de ventas)
                            string tipoVenta = "";
                            string sqlPub = @"SELECT ""IdTipoVenta"" FROM ""Tianguis_Publicaciones"" WHERE ""IdPublicacion""=@pub AND ""IdUsuarioVendedor""=@uid FOR UPDATE";
                            using (var cmdPub = new NpgsqlCommand(sqlPub, conexion, trans))
                            {
                                cmdPub.Parameters.AddWithValue("@pub", idPublicacion);
                                cmdPub.Parameters.AddWithValue("@uid", idUser);
                                var res = await cmdPub.ExecuteScalarAsync();

                                // Si es null, alguien intentó confirmar una venta en una tienda ajena
                                if (res == null) throw new UnauthorizedAccessException("Operación denegada. No eres el dueño de esta publicación.");
                                tipoVenta = res.ToString();
                            }

                            // 2. BLOQUEO DEL TRATO
                            int idComprador = 0; int? idTalla = null; int cant = 1; decimal oferta = 0; string estadoPrevio = "";
                            string emailComprador = ""; string nombreComprador = ""; string tituloPub = "";

                            string sqlInt = @"SELECT i.""IdUsuarioComprador"", i.""IdTallaSolicitada"", i.""CantidadSolicitada"", i.""OfertaActual"", i.""IdEstadoTrato"", 
                         u.""Email"", u.""NombreCompleto"", p.""Titulo""
                  FROM ""Tianguis_Interesados"" i 
                  JOIN ""Sist_Usuarios"" u ON i.""IdUsuarioComprador"" = u.""Id_Usuario""
                  JOIN ""Tianguis_Publicaciones"" p ON i.""IdPublicacion"" = p.""IdPublicacion""
                  WHERE i.""IdInteresado""=@int AND i.""IdPublicacion""=@pub FOR UPDATE";

                            using (var cmdI = new NpgsqlCommand(sqlInt, conexion, trans))
                            {
                                cmdI.Parameters.AddWithValue("@int", idInteresado);
                                cmdI.Parameters.AddWithValue("@pub", idPublicacion);
                                using (var r = await cmdI.ExecuteReaderAsync())
                                {
                                    if (await r.ReadAsync())
                                    {
                                        idComprador = (int)r["IdUsuarioComprador"];
                                        idTalla = r["IdTallaSolicitada"] as int?;
                                        cant = r["CantidadSolicitada"] != DBNull.Value ? (int)r["CantidadSolicitada"] : 1;
                                        oferta = (decimal)r["OfertaActual"];
                                        estadoPrevio = r["IdEstadoTrato"].ToString();

                                        emailComprador = r["Email"] != DBNull.Value ? r["Email"].ToString() : "";
                                        nombreComprador = r["NombreCompleto"] != DBNull.Value ? r["NombreCompleto"].ToString() : "Comprador";
                                        tituloPub = r["Titulo"].ToString();
                                    }
                                    else throw new Exception("Negociación inválida o manipulada.");
                                }
                            }

                            // 3. DESCONTAR STOCK (Solo si confirmaron directo sin reservar antes)
                            if (estadoPrevio != "RES" && idTalla.HasValue)
                            {
                                using (var cmdS = new NpgsqlCommand(@"UPDATE ""Tianguis_Tallas"" SET ""Stock"" = ""Stock"" - @cant WHERE ""IdTalla""=@idt AND ""Stock"" >= @cant", conexion, trans))
                                {
                                    cmdS.Parameters.AddWithValue("@cant", cant);
                                    cmdS.Parameters.AddWithValue("@idt", idTalla.Value);
                                    int affected = await cmdS.ExecuteNonQueryAsync();

                                    // PROTECCIÓN DE VENTA DIRECTA CONTRA RACE CONDITIONS
                                    if (affected == 0) throw new InvalidOperationException($"Stock insuficiente para confirmar la venta. Quedan menos de {cant} piezas.");
                                }
                            }

                            // 4. REGISTRO FÍSICO DE LA VENTA
                            string sqlVenta = @"INSERT INTO ""Tianguis_Ventas"" 
                        (""IdPublicacion"", ""IdInteresado"", ""IdUsuarioVendedor"", ""IdUsuarioComprador"", ""IdTalla"", ""Cantidad"", ""PrecioAcordado"", ""Total"")
                        VALUES (@pub, @int, @uv, @uc, @idt, @cant, @prec, @tot)";
                            using (var cmdV = new NpgsqlCommand(sqlVenta, conexion, trans))
                            {
                                cmdV.Parameters.AddWithValue("@pub", idPublicacion);
                                cmdV.Parameters.AddWithValue("@int", idInteresado);
                                cmdV.Parameters.AddWithValue("@uv", idUser);
                                cmdV.Parameters.AddWithValue("@uc", idComprador);
                                cmdV.Parameters.AddWithValue("@idt", (object)idTalla ?? DBNull.Value);
                                cmdV.Parameters.AddWithValue("@cant", cant);
                                cmdV.Parameters.AddWithValue("@prec", oferta);
                                cmdV.Parameters.AddWithValue("@tot", oferta * cant);
                                await cmdV.ExecuteNonQueryAsync();
                            }

                            // 5. MARCAR TRATO COMO COMPLETADO
                            using (var cmdUpdInt = new NpgsqlCommand(@"UPDATE ""Tianguis_Interesados"" SET ""IdEstadoTrato""='COM' WHERE ""IdInteresado""=@int", conexion, trans))
                            {
                                cmdUpdInt.Parameters.AddWithValue("@int", idInteresado);
                                await cmdUpdInt.ExecuteNonQueryAsync();
                            }

                            // 6. ACTUALIZAR ESTADO FINAL DE LA PUBLICACIÓN
                            string estadoFinalPub = "ACT";

                            if (tipoVenta == "SUB")
                            {
                                // Las subastas son de 1 solo artículo. Si se vende, se cierra.
                                estadoFinalPub = "VEN";
                            }
                            else
                            {
                                // Si es Tienda (TIE), dependemos del stock restante total
                                int stockRestante = 0;
                                using (var cmdStock = new NpgsqlCommand(@"SELECT COALESCE(SUM(""Stock""), 0) FROM ""Tianguis_Tallas"" WHERE ""IdPublicacion""=@pub", conexion, trans))
                                {
                                    cmdStock.Parameters.AddWithValue("@pub", idPublicacion);
                                    stockRestante = Convert.ToInt32(await cmdStock.ExecuteScalarAsync());
                                }

                                estadoFinalPub = stockRestante > 0 ? "ACT" : "AGO";
                            }

                            using (var cmdUpdPub = new NpgsqlCommand(@"UPDATE ""Tianguis_Publicaciones"" SET ""IdEstado""=@est WHERE ""IdPublicacion""=@pub", conexion, trans))
                            {
                                cmdUpdPub.Parameters.AddWithValue("@est", estadoFinalPub);
                                cmdUpdPub.Parameters.AddWithValue("@pub", idPublicacion);
                                await cmdUpdPub.ExecuteNonQueryAsync();
                            }

                            await Funciones.RegistrarBitacora(conexion, idUser, Modulo, Parametros.AccionesBitacora.Editar, $"Venta confirmada pub {idPublicacion} a int {idInteresado}", ip, trans);
                            await trans.CommitAsync();

                            if (!string.IsNullOrEmpty(emailComprador))
                            {
                                string asunto = "¡Compra Completada en el Tianguis!";
                                string msjHtml = $@"
        <h2 style='color:#198754;'>¡Trato cerrado, {nombreComprador}!</h2>
        <p>El vendedor ha confirmado la entrega exitosa del artículo: <strong>{tituloPub}</strong>.</p>
        <p>¡Muchas gracias por utilizar el Tianguis de nuestra plataforma!</p>";
                                try { await Funciones.EnviarCorreo(_configuration, emailComprador, asunto, msjHtml); } catch { }
                            }

                            MostrarMensaje("¡Trato Cerrado!", "Venta registrada exitosamente.", TipoMensaje.Exito);
                        }
                        catch (UnauthorizedAccessException uex)
                        {
                            await trans.RollbackAsync();
                            MostrarMensaje("Bloqueado", uex.Message, TipoMensaje.Error);
                            return RedirectToAction("Index"); // Expulsar al que intentó robar el trato
                        }
                        catch (InvalidOperationException ioe)
                        {
                            await trans.RollbackAsync();
                            MostrarMensaje("Error de Inventario", ioe.Message, TipoMensaje.Alerta);
                            return RedirectToAction("Detalle", new { sId = Funciones.EncriptarId(idPublicacion), sChatId = Funciones.EncriptarId(idInteresado) });
                        }
                        catch (Exception)
                        {
                            await trans.RollbackAsync();
                            throw;
                        }
                    }
                }

                // 7. AVISAR A LA SALA DE CHAT QUE SE COMPLETÓ LA VENTA
                if (_hubContext != null)
                {
                    await _hubContext.Clients.Group($"Chat_{idInteresado}").SendAsync("RecargarPagina");
                }
            }
            catch (Exception ex)
            {
                MostrarMensaje("Error", ex.Message, TipoMensaje.Error);
            }

            return RedirectToAction("Detalle", new { sId = Funciones.EncriptarId(idPublicacion), sChatId = Funciones.EncriptarId(idInteresado) });
        }

        // =========================================================
        // 11. PANEL DE CONTROL (MIS PUBLICACIONES Y OFERTAS)
        // =========================================================
        [Authorize]
        public async Task<IActionResult> Panel()
        {
            if (!User.TienePermiso(Modulo, PermisoLeer))
            {
                MostrarMensaje("Sin Permiso de Acceso", "Lo sentimos, no tienes permiso de acceder a esta opción.", TipoMensaje.Alerta);
                return RedirectToAction("Index"); 
            }
            var idUser = int.Parse(User.FindFirst("IdUsuario").Value);
            var modelo = new TianguisPanelViewModel();

            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();

                    string sqlPub = @"
                        SELECT p.""IdPublicacion"", p.""Titulo"", p.""PrecioBase"", p.""IdEstado"", p.""IdTipoVenta"", p.""FechaPublicacion"",
                               (SELECT COUNT(*) FROM ""Tianguis_Interesados"" WHERE ""IdPublicacion"" = p.""IdPublicacion"") as ""CantInt"",
                               (SELECT ""UrlImagen"" FROM ""Tianguis_Imagenes"" WHERE ""IdPublicacion"" = p.""IdPublicacion"" ORDER BY ""Orden"" ASC LIMIT 1) as ""ImgUrl""
                        FROM ""Tianguis_Publicaciones"" p
                        WHERE p.""IdUsuarioVendedor"" = @uid ORDER BY p.""FechaPublicacion"" DESC";

                    using (var cmd = new NpgsqlCommand(sqlPub, conexion))
                    {
                        cmd.Parameters.AddWithValue("@uid", idUser);
                        using (var r = await cmd.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                modelo.MisPublicaciones.Add(new MisPublicacionesItem
                                {
                                    IdPublicacion = (int)r["IdPublicacion"],
                                    sIdPublicacion = Funciones.EncriptarId((int)r["IdPublicacion"]),
                                    Titulo = r["Titulo"].ToString(),
                                    PrecioBase = (decimal)r["PrecioBase"],
                                    Estado = r["IdEstado"].ToString(),
                                    TipoVenta = r["IdTipoVenta"].ToString(),
                                    FechaPublicacion = (DateTime)r["FechaPublicacion"],
                                    CantidadInteresados = Convert.ToInt32(r["CantInt"]),
                                    ImagenUrl = r["ImgUrl"]?.ToString() ?? "/Images/default-tianguis.jpg"
                                });
                            }
                        }
                    }

                    string sqlOfe = @"
                        SELECT i.""IdInteresado"", i.""IdPublicacion"", i.""OfertaActual"", i.""IdEstadoTrato"", i.""CantidadSolicitada"", t.""NombreTalla"",
                               p.""Titulo"", p.""IdEstado"" as ""EstadoPub"", p.""PrecioBase"", p.""IdTipoVenta"",
                               (SELECT ""UrlImagen"" FROM ""Tianguis_Imagenes"" WHERE ""IdPublicacion"" = p.""IdPublicacion"" ORDER BY ""Orden"" ASC LIMIT 1) as ""ImgUrl""
                        FROM ""Tianguis_Interesados"" i
                        JOIN ""Tianguis_Publicaciones"" p ON i.""IdPublicacion"" = p.""IdPublicacion""
                        LEFT JOIN ""Tianguis_Tallas"" t ON i.""IdTallaSolicitada"" = t.""IdTalla""
                        WHERE i.""IdUsuarioComprador"" = @uid ORDER BY i.""FechaActualizacion"" DESC";

                    using (var cmdOfe = new NpgsqlCommand(sqlOfe, conexion))
                    {
                        cmdOfe.Parameters.AddWithValue("@uid", idUser);
                        using (var r = await cmdOfe.ExecuteReaderAsync())
                        {
                            while (await r.ReadAsync())
                            {
                                decimal miOferta = (decimal)r["OfertaActual"];
                                string tipoVenta = r["IdTipoVenta"].ToString();

                                if (tipoVenta == "TIE" && miOferta == 0)
                                {
                                    miOferta = (decimal)r["PrecioBase"];
                                }

                                modelo.MisOfertas.Add(new MisOfertasItem
                                {
                                    IdInteresado = (int)r["IdInteresado"],
                                    sIdInteresado = Funciones.EncriptarId((int)r["IdInteresado"]),
                                    IdPublicacion = (int)r["IdPublicacion"],
                                    sIdPublicacion = Funciones.EncriptarId((int)r["IdPublicacion"]),
                                    Titulo = r["Titulo"].ToString(),
                                    VendedorAlias = "Vendedor Anónimo",
                                    MiOferta = miOferta,
                                    EstadoTrato = r["IdEstadoTrato"].ToString(),
                                    EstadoPublicacion = r["EstadoPub"].ToString(),
                                    ImagenUrl = r["ImgUrl"]?.ToString() ?? "/Images/default-tianguis.jpg",
                                    TipoVenta = tipoVenta,
                                    Cantidad = r["CantidadSolicitada"] != DBNull.Value ? (int)r["CantidadSolicitada"] : 1,
                                    Talla = r["NombreTalla"]?.ToString() ?? "Única"
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { MostrarMensaje("Error", ex.Message, TipoMensaje.Error); }

            return View(modelo);
        }

        // =========================================================
        // HELPER: OBTENER CATEGORÍAS PARA LOS SELECTS
        // =========================================================
        private async Task<List<CategoriaTianguis>> ObtenerCategoriasActivas()
        {
            const string cacheKey = "CategoriasTianguisCache";

            // Si la caché existe, la retorna inmediatamente
            if (_cache.TryGetValue(cacheKey, out List<CategoriaTianguis> listaCacheada))
            {
                return listaCacheada;
            }

            var lista = new List<CategoriaTianguis>();
            try
            {
                using (var conexion = new NpgsqlConnection(_cadenaConexion))
                {
                    await conexion.OpenAsync();
                    string sql = @"SELECT ""IdCategoria"", ""Nombre"" FROM ""Tianguis_Categorias"" WHERE ""Activo"" = TRUE ORDER BY ""Nombre"" ASC";

                    using (var cmd = new NpgsqlCommand(sql, conexion))
                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            lista.Add(new CategoriaTianguis
                            {
                                IdCategoria = (int)r["IdCategoria"],
                                Nombre = r["Nombre"].ToString()
                            });
                        }
                    }
                }

                // Guardar en caché por 1 hora
                var cacheOptions = new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromHours(1));
                _cache.Set(cacheKey, lista, cacheOptions);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error al obtener categorías: " + ex.Message);
            }

            return lista;
        }
    }
}