using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

namespace RedAJP.Hubs
{
    public class EventosHub : Hub
    {
        // El cliente llama a este método cuando entra a la página del evento
        public async Task UnirseGrupoEvento(string idEventoStr)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"Evento_{idEventoStr}");
        }

        // El cliente llama a este método cuando sale de la página
        public async Task SalirGrupoEvento(string idEventoStr)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"Evento_{idEventoStr}");
        }
    }
}