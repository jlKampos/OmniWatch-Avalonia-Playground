using Avalonia.Controls;
using BruTile.Predefined;
using BruTile.Web;
using CommunityToolkit.Mvvm.ComponentModel;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling.Layers;
using OmniWatch.Data;
using OmniWatch.Integrations.Interfaces;
using OmniWatch.Interfaces;
using OmniWatch.Localization;
using OmniWatch.Mapping;
using OmniWatch.Mapping.Seismic;
using OmniWatch.Models.IPMA.Seismic;
using OmniWatch.ViewModels.ProgressControl;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static OmniWatch.ViewModels.MessageDialog.MessageDialogBoxViewModel;

namespace OmniWatch.ViewModels
{
    public partial class SeismologyPageViewModel : PageViewModel, IAsyncPage
    {
        #region Fields

        private readonly IIpmaService _apiClient;
        private readonly IMessageService _messageService;

        private TileLayer? _baseLayer;
        private TileLayer? _labelLayer;

        private bool _isDarkTheme = true;

        private List<SeismicActivityDto> SeismicActivities { get; set; } = new();

        #endregion

        #region Localization Helper

        private string Translation(string key) =>
            LanguageManager.Instance[key];

        #endregion

        #region Properties

        [ObservableProperty]
        private Mapsui.Map _map;

        [ObservableProperty]
        private DateTime _maxDate = DateTime.Now;

        [ObservableProperty]
        private DateTime _minDate = DateTime.Now.AddDays(-30);

        [ObservableProperty]
        private DateTime _selectedDate = DateTime.Now;

        [ObservableProperty]
        private ProgressControlViewModel _progressControl;

        public bool IsDarkTheme
        {
            get => _isDarkTheme;
            set
            {
                if (SetProperty(ref _isDarkTheme, value))
                    ApplyMapTheme();
            }
        }

        #endregion

        #region Constructor

        public SeismologyPageViewModel(
            ProgressControlViewModel progressControl,
            IMessageService messageService,
            IIpmaService apiClient)
        {
            PageName = ApplicationPageNames.Seismology;
            _progressControl = progressControl;
            _messageService = messageService;
            _apiClient = apiClient;

            Map = new Mapsui.Map();
        }

        public SeismologyPageViewModel()
        {
            if (Design.IsDesignMode)
            {
                Map = new Mapsui.Map();
                PageName = ApplicationPageNames.Seismology;
            }
        }

        #endregion

        #region Initialization

        public async Task LoadAsync()
        {
            try
            {
                ProgressControl.IsVisible = true;
                ProgressControl.Title = Translation("Seismo_Loading");
                ProgressControl.Message = Translation("Seismo_Initializing");

                await InitializeMapAsync();
                await LoadSeismologyDataAsync();
                UpdateMapFilter();
            }
            catch (Exception ex)
            {
                await _messageService.ShowAsync(
                    string.Format(Translation("Seismo_StartupError"), ex.Message),
                    MessageDialogType.Error);
            }
            finally
            {
                ProgressControl.IsVisible = false;
            }
        }

        private async Task InitializeMapAsync()
        {
            try
            {
                ApplyMapTheme();

                var centerAll = new Mapsui.MPoint(-1800000, 4500000);

                Map.Navigator.CenterOnAndZoomTo(centerAll, Map.Navigator.Resolutions[5]);

                var portugalAllExtent = new Mapsui.MRect(
                    -3200000,
                    3300000,
                    -200000,
                    5600000
                );

                Map.Navigator.OverridePanBounds = portugalAllExtent;

                Map.RefreshGraphics();
            }
            catch (Exception ex)
            {
                await _messageService.ShowAsync(
                    string.Format(Translation("Seismo_MapError"), ex.Message),
                    MessageDialogType.Error);
            }
        }

        #endregion

        #region Unload
        public Task UnloadAsync()
        {
            return Task.CompletedTask;
        }

        #endregion

        #region Theme

        private void ApplyMapTheme()
        {
            if (_baseLayer != null) Map.Layers.Remove(_baseLayer);
            if (_labelLayer != null) Map.Layers.Remove(_labelLayer);

            if (IsDarkTheme)
            {
                _baseLayer = new TileLayer(new HttpTileSource(
                    new GlobalSphericalMercator(),
                    "https://basemaps.cartocdn.com/dark_all/{z}/{x}/{y}.png",
                    name: "Carto Dark Matter"));

                _labelLayer = new TileLayer(new HttpTileSource(
                    new GlobalSphericalMercator(),
                    "https://basemaps.cartocdn.com/dark_only_labels/{z}/{x}/{y}.png",
                    name: "Carto Dark Labels"));
            }
            else
            {
                _baseLayer = Mapsui.Tiling.OpenStreetMap.CreateTileLayer();
                _baseLayer.Name = "OSM Light";
                _labelLayer = null;
            }

            Map.Layers.Insert(0, _baseLayer);
            if (_labelLayer != null)
                Map.Layers.Insert(1, _labelLayer);

            Map.RefreshGraphics();
        }

        #endregion

        #region Seismic Layer

        private void AddSeismicLayerToMap(List<SeismicActivityDto> events)
        {
            var features = new List<IFeature>();

            foreach (var earthquake in events)
            {
                if (earthquake.MagnitudeValue == null || earthquake.MagnitudeValue <= 0)
                    continue;

                var point = SphericalMercator.FromLonLat(earthquake.Lon, earthquake.Lat).ToMPoint();
                var feature = new PointFeature(point);

                var location = !string.IsNullOrEmpty(earthquake.Local)
                    ? earthquake.Local
                    : earthquake.ObservedRegion;

                var intensity = !string.IsNullOrEmpty(earthquake.Degree)
                    ? $" | Intensity: {earthquake.Degree}"
                    : "";

                var infoText = $"{location}\nMag: {earthquake.Magnitude}{intensity}";

                var scale = Math.Max(0.8, earthquake.MagnitudeValue.Value / 2.5);
                var degree = !string.IsNullOrEmpty(earthquake.Degree)
                    ? earthquake.Degree
                    : GetDegreeFromMagnitude(earthquake.MagnitudeValue.Value);

                var color = GetColorByDegree(degree);

                feature.Styles.Add(new SymbolStyle
                {
                    SymbolScale = scale,
                    SymbolType = SymbolType.Ellipse,
                    Fill = new Brush(color),
                    Outline = new Pen(Color.Black, 0.5)
                });

                feature.Styles.Add(new LabelStyle
                {
                    Text = infoText,
                    ForeColor = Color.FromArgb(255, 25, 16, 0),
                    BorderColor = Color.FromArgb(255, 60, 100, 0),
                    BackColor = new Brush(Color.FromArgb(191, 143, 170, 0)),
                    Font = new Font { Size = 12, Bold = true },
                    HorizontalAlignment = LabelStyle.HorizontalAlignmentEnum.Left,
                    VerticalAlignment = LabelStyle.VerticalAlignmentEnum.Center,
                    Offset = new Offset(25, 0),
                    CollisionDetection = true,
                });

                features.Add(feature);
            }

            var oldLayer = Map.Layers.FirstOrDefault(l => l.Name == "Earthquakes");
            if (oldLayer != null) Map.Layers.Remove(oldLayer);

            Map.Layers.Add(new MemoryLayer { Name = "Earthquakes", Features = features, Style = null });
            Map.RefreshGraphics();
        }

        #endregion

        #region Helpers

        private string GetDegreeFromMagnitude(double mag)
        {
            return mag switch
            {
                < 2.0 => "I",
                < 3.0 => "II",
                < 4.0 => "III",
                < 4.5 => "IV",
                < 5.0 => "V",
                < 5.5 => "VI",
                < 6.0 => "VII",
                < 6.5 => "VIII",
                < 7.0 => "IX",
                _ => "X"
            };
        }

        private Color GetColorByDegree(string? degree)
        {
            if (string.IsNullOrEmpty(degree))
            {
                return Color.FromArgb(180, 255, 0, 0);
            }

            var d = degree.ToUpper().Trim();

            return d switch
            {
                "I" or "1" or "II" or "2" => Color.FromArgb(200, 50, 205, 50),
                "III" or "3" => Color.FromArgb(200, 255, 215, 0),
                "IV" or "4" => Color.FromArgb(200, 255, 140, 0),
                "V" or "5" => Color.FromArgb(220, 255, 69, 0),
                "VI" or "6" or "VII" or "7" => Color.FromArgb(255, 139, 0, 0),
                _ => Color.FromArgb(255, 75, 0, 130)
            };
        }

        partial void OnSelectedDateChanged(DateTime value)
        {
            try
            {
                UpdateMapFilter();
            }
            catch (Exception ex)
            {
                _messageService.ShowAsync(
                    string.Format(Translation("Seismo_ThemeError"), ex.Message),
                    MessageDialogType.Error);
            }
        }

        private void UpdateMapFilter()
        {
            if (SeismicActivities == null) return;

            var filteredEvents = SeismicActivities
                .Where(s => s.Time.Date == SelectedDate.Date)
                .ToList();

            AddSeismicLayerToMap(filteredEvents);
        }

        #endregion

        #region API

        private async Task LoadSeismologyDataAsync()
        {
            SeismicActivities.Clear();

            var tasks = new[]
            {
                _apiClient.GetSeismicAsync(3),
                _apiClient.GetSeismicAsync(7)
            };

            var responses = await Task.WhenAll(tasks);

            foreach (var response in responses)
            {
                if (response?.Data == null)
                    continue;

                foreach (var item in response.Data)
                    SeismicActivities.Add(item.ToDto());
            }
        }

        #endregion
    }
}
