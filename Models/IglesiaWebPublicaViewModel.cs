using System;
using System.Collections.Generic;

namespace RedAJP.Models
{
    public class IglesiaWebPublicaViewModel
    {
        // Datos de la Iglesia (Vienen de iciar_iglesias)
        public string NombreIglesia { get; set; }
        public string Municipio { get; set; }
        public string DireccionCompleta { get; set; } // Concatenación de calle, núm, colonia
        public string Horarios { get; set; }
        public string MapaUrl { get; set; }
        public decimal? Latitud { get; set; }
        public decimal? Longitud { get; set; }

        // Datos Web (Vienen de iciar_iglesias_web)
        public string FacebookUrl { get; set; }
        public string InstagramUrl { get; set; }
        public string YoutubeUrl { get; set; }
        public string Whatsapp { get; set; }
        public string Telefono { get; set; }
        public string Historia { get; set; }

        // El Enrutador (Viene de iciar_plantillas_web)
        public string PlantillaVista { get; set; }

        // Listas Dinámicas
        public List<AvisoWebViewModel> Avisos { get; set; } = new List<AvisoWebViewModel>();
        public List<EventoWebViewModel> Eventos { get; set; } = new List<EventoWebViewModel>();
        public List<GaleriaWebViewModel> Galeria { get; set; } = new List<GaleriaWebViewModel>();
        public List<MultimediaWebViewModel> Multimedia { get; set; } = new List<MultimediaWebViewModel>();
        public DisenoModularViewModel Diseno { get; set; } = new DisenoModularViewModel();
    }
}