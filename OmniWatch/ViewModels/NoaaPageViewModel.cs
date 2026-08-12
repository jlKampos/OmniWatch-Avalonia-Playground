using Avalonia.Threading;
using BruTile.Predefined;
using BruTile.Web;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Limiting;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling.Layers;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using OmniWatch.Data;
using OmniWatch.Helpers;
using OmniWatch.Integrations.Exceptions;
using OmniWatch.Integrations.Interfaces;
using OmniWatch.Interfaces;
using OmniWatch.Localization;
using OmniWatch.Mapping.Noaa;
using OmniWatch.Models.Noaa;
using OmniWatch.Models.Noaa.ActiveStorms;
using OmniWatch.Models.Noaa.ArchiveStorms;
using OmniWatch.ViewModels.MessageDialog;
using OmniWatch.ViewModels.ProgressControl;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static OmniWatch.ViewModels.MessageDialog.MessageDialogBoxViewModel;

namespace OmniWatch.ViewModels
{
    public partial class NoaaPageViewModel : PageViewModel, IAsyncPage
    {
        private readonly INoaaService _apiClient;
        private readonly ILogger<NoaaPageViewModel> _logger;
        private readonly IMessageService _messageService;
        private CancellationTokenSource? _cancellationToken;

        // Animation state
        private readonly List<Coordinate> _currentTrailPoints = new();
        private int _segmentIndex = 0;
        private double _t = 0;
        private DispatcherTimer? _animationTimer;
        private float _activeRotation = 0;
        private DispatcherTimer? _activeStormsRotationTimer;

        private int _currentRotation = 0;
        private StormTrackDto? _lastStorm;

        // Map layer
        private MemoryLayer? _stormHeadLayer;
        private TileLayer? _baseLayer;
        private TileLayer? _labelLayer;

        private List<StormTrackDto> _stormDtos = new();

        public List<int> Years { get; }

        [ObservableProperty]
        public string _activeStormsMessage = "";

        [ObservableProperty]
        public bool _anyActiveStorms = false;

        [ObservableProperty]
        private int? _selectedYear;

        [ObservableProperty]
        private List<HurricaneOption>? _hurricanes;

        [ObservableProperty]
        private HurricaneOption? _selectedHurricane;

        [ObservableProperty]
        private Map _map;

        [ObservableProperty]
        private ProgressControlViewModel _progressControl;

        [ObservableProperty]
        private bool _reanimate;

        private bool _isDarkTheme = true;
        public bool IsDarkTheme
        {
            get => _isDarkTheme;
            set
            {
                if (SetProperty(ref _isDarkTheme, value))
                    ApplyMapTheme();
            }
        }

        private string Translation(string key) =>
            LanguageManager.Instance[key];

        public NoaaPageViewModel(
            INoaaService noaaService,
            IMessageService messageService,
            ILogger<NoaaPageViewModel> logger,
            ProgressControlViewModel progressControl)
        {
            PageName = ApplicationPageNames.Noaa;
            _apiClient = noaaService;
            _logger = logger;
            _messageService = messageService;
            _progressControl = progressControl;

            Map = new Map();

            int currentYear = DateTime.Now.Year;
            Years = Enumerable.Range(1980, currentYear - 1980 + 1).Reverse().ToList();
            SelectedYear = currentYear;
        }

        public async Task LoadAsync()
        {
            _cancellationToken?.Cancel();
            _cancellationToken = new CancellationTokenSource();
            try
            {
                ProgressControl.IsVisible = true;
                ProgressControl.Title = Translation("Noaa_Startup");

                await InitializeMapAsync().ConfigureAwait(false);
                await CheckActiveStormsAsync().ConfigureAwait(false);
                await LoadHistoricalStormsAsync(_cancellationToken.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                var apiEx = ex.FindDeepestInner<ApiException>();
                var exMsg = apiEx?.ResponseContent ?? ex.GetBaseException().Message;

                await _messageService.ShowAsync(
                    string.Format(Translation("Noaa_StartupError"), exMsg),
                    MessageDialogType.Error);
            }
        }

        public Task UnloadAsync()
        {
            _animationTimer?.Stop();
            _animationTimer = null;
            _activeStormsRotationTimer?.Stop();
            _activeStormsRotationTimer = null;

            ClearStormLayers(clearActiveStorms: true);

            _cancellationToken?.Cancel();
            _cancellationToken?.Dispose();
            _cancellationToken = null;
            return Task.CompletedTask;
        }

        private async Task InitializeMapAsync()
        {
            try
            {
                ProgressControl.IsVisible = true;
                ProgressControl.Title = Translation("Noaa_InitMap");
                ProgressControl.Message = Translation("Noaa_SetupLayers");

                ApplyMapTheme();

                // Initial zoom on Atlantic/Europe
                var initialExtent = new MRect(-12000000, 0, 4000000, 8000000);
                Map.Navigator.ZoomToBox(initialExtent);

                Map.RefreshGraphics();
            }
            finally
            {
                ProgressControl.IsVisible = false;
            }
        }

        private async Task CheckActiveStormsAsync()
        {
            ProgressControl.IsVisible = true;
            ProgressControl.Title = Translation("Noaa_Loading");
            ProgressControl.Message = Translation("Noaa_CheckingActiveStorms");

            try
            {
                var result = await _apiClient.GetActiveStormTracksAsync();

                if (result != null)
                {
                    var activeSorms = ActiveStormMapper.Map(result.ActiveStorms);
                    AnyActiveStorms = activeSorms.Any();
                    ActiveStormsMessage = AnyActiveStorms
                        ? string.Format(Translation("Noaa_ActiveStorms"), activeSorms.Count)
                        : "";

                    if (AnyActiveStorms)
                    {
                        ShowCurrentActiveStormsOnMap(Map, activeSorms);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Active storms error");

                await _messageService.ShowAsync(
                    string.Format(Translation("Noaa_Error"), ex.Message),
                    MessageDialogType.Error);
            }
            finally
            {
                ProgressControl.IsVisible = false;
            }
        }

        private async Task LoadHistoricalStormsAsync(CancellationToken cancellationToken)
        {
            ProgressControl.IsVisible = true;
            ProgressControl.Title = Translation("Noaa_IBTracs");
            ProgressControl.Message = string.Format(Translation("Noaa_LoadingYear"), SelectedYear);

            try
            {
                var yearToLoad = SelectedYear ?? DateTime.Now.Year;
                var storms = await _apiClient.GetHistoricalStormTracksAsync(yearToLoad, cancellationToken);

                if (storms == null || !storms.Any())
                {
                    await _messageService.ShowAsync(
                        Translation("Noaa_NoData"),
                        MessageDialogType.Warning);
                    return;
                }

                _stormDtos = NoaaStormMapper.Map(storms);
                Hurricanes = _stormDtos
                    .Select(s => new HurricaneOption { Name = s.Name, Id = s.Id })
                    .OrderBy(h => h.Name)
                    .ToList();
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Historical Load Error");
                await _messageService.ShowAsync(
                    string.Format(Translation("Noaa_Error"), ex.Message),
                    MessageDialogType.Error);
                ProgressControl.IsVisible = false;
            }
            finally
            {
                ProgressControl.IsVisible = false;
            }
        }

        private void ShowCurrentActiveStormsOnMap(Map map, List<ActiveStormDto> activeStorms)
        {
            if (map == null || activeStorms == null || !activeStorms.Any()) return;

            var activeLayer = new MemoryLayer
            {
                Name = "Active Storms Layer",
                Features = new List<IFeature>()
            };

            var imagePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Images", "Noaa", "activeHurricane.svg");
            var uriPath = new Uri(imagePath).AbsoluteUri;
            var hurricaneImage = new Mapsui.Styles.Image { Source = uriPath };

            var features = new List<IFeature>();

            foreach (var storm in activeStorms)
            {
                var (x, y) = SphericalMercator.FromLonLat(storm.Longitude, storm.Latitude);
                var stormFeature = new PointFeature(new MPoint(x, y));

                float scale = Math.Clamp(0.5f + (storm.Intensity / 120f), 0.6f, 1.5f);

                var imageStyle = new ImageStyle
                {
                    Image = hurricaneImage,
                    SymbolScale = scale,
                    SymbolRotation = _activeRotation
                };

                var labelStyle = new LabelStyle
                {
                    Text = $"{storm.Name} ({storm.Classification})\n" +
                           $"Wind: {storm.WindSpeedKM:F1} km/h - {storm.Intensity} kt\n" +
                           $"Pres: {storm.Pressure} hPa\n" +
                           $"Mov: {storm.Movement}",

                    BackColor = new Brush(Color.FromArgb(191, 255, 165, 0)),
                    BorderColor = Color.FromArgb(255, 255, 140, 0),
                    BorderThickness = 1,
                    ForeColor = Color.FromArgb(255, 25, 16, 0),
                    Font = new Font { Size = 11, Bold = true },
                    HorizontalAlignment = LabelStyle.HorizontalAlignmentEnum.Left,
                    VerticalAlignment = LabelStyle.VerticalAlignmentEnum.Center,
                    Offset = new Offset(30, 0),
                    CollisionDetection = false
                };

                stormFeature.Styles.Add(imageStyle);
                stormFeature.Styles.Add(labelStyle);
                features.Add(stormFeature);
            }

            activeLayer.Features = features;

            activeLayer.Style = null;
            map.Layers.Add(activeLayer);

            _activeStormsRotationTimer?.Stop();
            _activeStormsRotationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _activeStormsRotationTimer.Tick += (s, e) =>
            {
                _activeRotation = (_activeRotation - 10) % 360;

                var layer = map.Layers.FirstOrDefault(l => l.Name == "Active Storms Layer") as MemoryLayer;
                if (layer != null)
                {
                    foreach (var feature in layer.Features.Cast<PointFeature>())
                    {
                        var style = feature.Styles.OfType<ImageStyle>().FirstOrDefault();
                        if (style != null) style.SymbolRotation = _activeRotation;
                    }
                    map.RefreshGraphics();
                }
            };

            _activeStormsRotationTimer.Start();
        }

        public void StartCycloneAnimation(Map map, StormTrackDto storm, Func<bool> reanimateProvider)
        {
            if (map == null || storm?.Track == null || storm.Track.Count < 2) return;

            _animationTimer?.Stop();

            var layersToRemove = map.Layers
                .Where(l => l.Name is "Storm Track" or "Storm Head" or "Storm Trail")
                .ToList();
            foreach (var layer in layersToRemove) map.Layers.Remove(layer);

            var stormData = storm.Track.Select(p =>
            {
                var (x, y) = SphericalMercator.FromLonLat(p.Longitude, p.Latitude);
                return new { X = x, Y = y, p.Wind, p.Pressure, p.Category, p.Time, p.Basin, p.Nature, p.WindSpeedKM };
            }).ToList();

            _segmentIndex = 0;
            _t = 0;
            _currentTrailPoints.Clear();

            var trackLayer = new MemoryLayer
            {
                Name = "Storm Track",
                Features = new List<IFeature> {
                    new GeometryFeature { Geometry = new LineString(stormData.Select(p => new Coordinate(p.X, p.Y)).ToArray()) }
                },
                Style = new VectorStyle { Line = new Pen(Color.FromArgb(120, 143, 170, 0), 2) }
            };

            var trailLayer = new MemoryLayer
            {
                Name = "Storm Trail",
                Style = new VectorStyle { Line = new Pen(Color.FromArgb(255, 128, 255, 0), 4) }
            };

            _stormHeadLayer = new MemoryLayer { Name = "Storm Head" };

            map.Layers.Add(trackLayer);
            map.Layers.Add(trailLayer);
            map.Layers.Add(_stormHeadLayer);

            var imagePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Images", "Noaa", "hurricane.svg");

            var uriPath = new Uri(imagePath).AbsoluteUri;
            var hurricaneImage = new Mapsui.Styles.Image { Source = uriPath };

            _animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _animationTimer.Tick += (s, e) =>
            {
                if (_segmentIndex >= stormData.Count - 1)
                {
                    _animationTimer.Stop();
                    if (reanimateProvider()) StartCycloneAnimation(map, storm, reanimateProvider);
                    return;
                }

                var a = stormData[_segmentIndex];
                var b = stormData[_segmentIndex + 1];

                _t += 0.1;
                if (_t >= 1) { _t = 0; _segmentIndex++; return; }

                var pos = Lerp(new MPoint(a.X, a.Y), new MPoint(b.X, b.Y), _t);

                _currentRotation = (_currentRotation - 10) % 360;
                float wind = (float)(a.Wind + (b.Wind - a.Wind) * _t);
                float scale = Math.Clamp(0.5f + (wind / 120f), 0.6f, 1.5f);

                // Update Feature
                var stormFeature = new PointFeature(pos);

                stormFeature.Styles.Add(new ImageStyle
                {
                    Image = hurricaneImage,
                    SymbolScale = scale,
                    SymbolRotation = _currentRotation
                });

                // Label 
                stormFeature.Styles.Add(new LabelStyle
                {
                    Text = $"{a.Time:yyyy-MM-dd HH:mm}\n" +
                            $"Name: {storm.Name}\n" +
                            $"Wind: {a.WindSpeedKM:F1} km/h - {a.Wind} kt\n" +
                            $"Pressure: {a.Pressure} hPa\n" +
                            $"Cat: {a.Category}\n" +
                            $"Basin: {a.Basin}\n" +
                            $"Nature: {a.Nature}",

                    BackColor = new Brush(Color.FromArgb(191, 143, 170, 0)),
                    BorderColor = Color.FromArgb(255, 60, 100, 0),
                    BorderThickness = 1,
                    ForeColor = Color.FromArgb(255, 25, 16, 0),
                    Font = new Font { Size = 12, Bold = true },
                    HorizontalAlignment = LabelStyle.HorizontalAlignmentEnum.Left,
                    VerticalAlignment = LabelStyle.VerticalAlignmentEnum.Center,
                    Offset = new Offset(35, 0),
                    CollisionDetection = false
                });

                // Update Trail 
                _currentTrailPoints.Clear();
                for (int i = 0; i <= _segmentIndex; i++)
                    _currentTrailPoints.Add(new Coordinate(stormData[i].X, stormData[i].Y));
                _currentTrailPoints.Add(new Coordinate(pos.X, pos.Y));

                trailLayer.Features = new List<IFeature> {
                    new GeometryFeature { Geometry = new LineString(_currentTrailPoints.ToArray()) }
                };

                _stormHeadLayer.Features = new List<IFeature> { stormFeature };
                _stormHeadLayer.Style = null;
                map.RefreshGraphics();
            };

            _animationTimer.Start();
        }

        private void ApplyMapTheme()
        {
            var layers = Map.Layers.ToList();
            if (_baseLayer != null) Map.Layers.Remove(_baseLayer);
            if (_labelLayer != null) Map.Layers.Remove(_labelLayer);

            if (_isDarkTheme)
            {
                _baseLayer = new TileLayer(new HttpTileSource(new GlobalSphericalMercator(), "https://basemaps.cartocdn.com/dark_all/{z}/{x}/{y}.png"));
                _labelLayer = new TileLayer(new HttpTileSource(new GlobalSphericalMercator(), "https://basemaps.cartocdn.com/dark_only_labels/{z}/{x}/{y}.png"));
            }
            else
            {
                _baseLayer = Mapsui.Tiling.OpenStreetMap.CreateTileLayer();
                _labelLayer = null;
            }

            Map.Layers.Insert(0, _baseLayer);
            if (_labelLayer != null) Map.Layers.Insert(1, _labelLayer);

            Map.RefreshGraphics();
        }

        private void ClearStormLayers(bool clearActiveStorms = false)
        {
            _animationTimer?.Stop();
            if (Map == null) return;
            var layers = Map.Layers.Where(l => l.Name is "Storm Track" or "Storm Head" or "Storm Trail").ToList();

            if (clearActiveStorms)
            {
                _activeStormsRotationTimer?.Stop();
                layers.AddRange(Map.Layers.Where(l => l.Name == "Active Storms Layer"));
            }

            foreach (var l in layers) Map.Layers.Remove(l);
            Map.RefreshGraphics();
        }

        private static MPoint Lerp(MPoint a, MPoint b, double t) =>
            new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

        partial void OnSelectedYearChanged(int? value)
        {
            if (value == null) return;
            _cancellationToken?.Cancel();
            ClearStormLayers();
            Hurricanes?.Clear();
            _ = LoadHistoricalStormsAsync(new CancellationTokenSource().Token);
        }

        partial void OnSelectedHurricaneChanged(HurricaneOption? value)
        {
            if (value == null) { ClearStormLayers(); return; }
            var hurricane = _stormDtos.FirstOrDefault(x => x.Id.Equals(value.Id, StringComparison.OrdinalIgnoreCase));
            if (hurricane == null) return;

            _lastStorm = hurricane;
            ZoomToStormArea(Map, hurricane);
            StartCycloneAnimation(Map, hurricane, () => Reanimate);
        }

        private void ZoomToStormArea(Map map, StormTrackDto storm)
        {
            var coords = storm.Track.Select(p => SphericalMercator.FromLonLat(p.Longitude, p.Latitude)).ToList();
            if (!coords.Any()) return;

            var box = new MRect(coords.Min(c => c.x), coords.Min(c => c.y), coords.Max(c => c.x), coords.Max(c => c.y));
            map.Navigator.ZoomToBox(box.Grow(1.5));
        }

        partial void OnReanimateChanged(bool value)
        {
            if (value && _lastStorm != null && (_animationTimer == null || !_animationTimer.IsEnabled))
                StartCycloneAnimation(Map, _lastStorm, () => Reanimate);
        }

        [RelayCommand]
        public async Task ForceRefreshAsync()
        {
            var result = await _messageService.ShowAsync(
                Translation("Noaa_ForceRefreshConfirm"),
                MessageDialogType.Warning);

            if (result == MessageDialogResult.Ok)
            {
                try
                {
                    ProgressControl.IsVisible = true;

                    _cancellationToken?.Cancel();
                    _cancellationToken = new CancellationTokenSource();

                    await _apiClient.ClearCacheAsync(_cancellationToken.Token);

                    await LoadHistoricalStormsAsync(_cancellationToken.Token);
                }
                catch (Exception ex)
                {
                    await _messageService.ShowAsync(
                        string.Format(Translation("Noaa_ForceRefreshError"), ex.Message),
                        MessageDialogType.Error);
                }
                finally
                {
                    ProgressControl.IsVisible = false;
                }
            }
        }
    }
}
