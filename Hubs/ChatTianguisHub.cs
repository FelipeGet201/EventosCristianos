using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System;
using System.Threading.Tasks;

namespace RedAJP.Hubs
{
    [Authorize] // Protege el websocket, si no hay sesión iniciada, rechaza la conexión
    public class ChatTianguisHub : Hub
    {
        private readonly string _cadenaConexion;

        // Inyectamos la configuración para poder leer la cadena de conexión a la BD
        public ChatTianguisHub(IConfiguration configuration)
        {
            _cadenaConexion = configuration.GetConnectionString("MiConexion");
        }

        public async Task UnirseAlChat(string idInteresadoStr)
        {
            // 1. Convertimos el ID que llega desde JavaScript
            if (!int.TryParse(idInteresadoStr, out int idInteresado))
            {
                throw new HubException("ID de chat inválido.");
            }

            // 2. Obtenemos el ID del usuario directamente del token/cookie del WebSocket
            var claimUsuario = Context.User.FindFirst("IdUsuario");
            if (claimUsuario == null)
            {
                throw new HubException("Usuario no identificado.");
            }

            int idUser = int.Parse(claimUsuario.Value);
            bool tienePermiso = false;

            // 3. Validación Zero Trust contra la Base de Datos
            using (var conexion = new NpgsqlConnection(_cadenaConexion))
            {
                await conexion.OpenAsync();

                string sqlVal = @"
                    SELECT p.""IdUsuarioVendedor"", i.""IdUsuarioComprador""
                    FROM ""Tianguis_Interesados"" i
                    JOIN ""Tianguis_Publicaciones"" p ON i.""IdPublicacion"" = p.""IdPublicacion""
                    WHERE i.""IdInteresado"" = @int";

                using (var cmdVal = new NpgsqlCommand(sqlVal, conexion))
                {
                    cmdVal.Parameters.AddWithValue("@int", idInteresado);
                    using (var r = await cmdVal.ExecuteReaderAsync())
                    {
                        if (await r.ReadAsync())
                        {
                            int idVendedor = (int)r["IdUsuarioVendedor"];
                            int idComprador = (int)r["IdUsuarioComprador"];

                            // Si el usuario logueado es el vendedor o el comprador de este trato, le damos pase
                            if (idUser == idVendedor || idUser == idComprador)
                            {
                                tienePermiso = true;
                            }
                        }
                    }
                }
            }

            // 4. Decisión final
            if (tienePermiso)
            {
                // Lo metemos al grupo privado para que reciba los mensajes en tiempo real
                await Groups.AddToGroupAsync(Context.ConnectionId, $"Chat_{idInteresado}");
            }
            else
            {
                // Rechazamos la conexión y disparamos un error en la consola del intruso
                throw new HubException("Acceso denegado: No tienes permiso para espiar esta negociación.");
            }
        }
        public async Task UnirseAMultiplesChats(int[] idsInteresados)
        {
            // Si no mandan nada, no hacemos nada
            if (idsInteresados == null || idsInteresados.Length == 0) return;

            // 1. Obtenemos el ID del usuario del token/cookie
            var claimUsuario = Context.User.FindFirst("IdUsuario");
            if (claimUsuario == null)
            {
                throw new HubException("Usuario no identificado.");
            }

            int idUser = int.Parse(claimUsuario.Value);
            var chatsPermitidos = new List<int>();

            // 2. Validación Zero Trust optimizada (1 sola consulta para todos los IDs)
            using (var conexion = new NpgsqlConnection(_cadenaConexion))
            {
                await conexion.OpenAsync();

                // Npgsql permite pasar un arreglo directamente a PostgreSQL con el operador ANY()
                // Filtramos para que solo devuelva los IDs donde el usuario sea Vendedor o Comprador
                string sqlVal = @"
            SELECT i.""IdInteresado""
            FROM ""Tianguis_Interesados"" i
            JOIN ""Tianguis_Publicaciones"" p ON i.""IdPublicacion"" = p.""IdPublicacion""
            WHERE i.""IdInteresado"" = ANY(@ids)
              AND (p.""IdUsuarioVendedor"" = @uid OR i.""IdUsuarioComprador"" = @uid)";

                using (var cmdVal = new NpgsqlCommand(sqlVal, conexion))
                {
                    cmdVal.Parameters.AddWithValue("@ids", idsInteresados);
                    cmdVal.Parameters.AddWithValue("@uid", idUser);

                    using (var r = await cmdVal.ExecuteReaderAsync())
                    {
                        while (await r.ReadAsync())
                        {
                            chatsPermitidos.Add((int)r["IdInteresado"]);
                        }
                    }
                }
            }

            // 3. Unimos al usuario a los grupos de SignalR (solo a los que pasó la validación)
            foreach (var idChat in chatsPermitidos)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, $"Chat_{idChat}");
            }
        }
    }
}