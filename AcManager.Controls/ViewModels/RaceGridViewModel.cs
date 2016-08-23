using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;
using AcManager.Tools.Data;
using AcManager.Tools.Filters;
using AcManager.Tools.Helpers;
using AcManager.Tools.Managers;
using AcManager.Tools.Miscellaneous;
using AcManager.Tools.Objects;
using AcTools.Processes;
using AcTools.Utils;
using AcTools.Utils.Helpers;
using FirstFloor.ModernUI;
using FirstFloor.ModernUI.Helpers;
using FirstFloor.ModernUI.Presentation;
using FirstFloor.ModernUI.Windows.Controls;
using JetBrains.Annotations;
using MoonSharp.Interpreter;
using Newtonsoft.Json;

namespace AcManager.Controls.ViewModels {
    public class RaceGridViewModel : NotifyPropertyChanged, IDisposable, IComparer, IUserPresetable {
        public static bool OptionNfsPorscheNames = false;

        #region Loading and saving
        public const string PresetableKeyValue = "Race Grids";
        private const string KeySaveable = "__RaceGrid";

        private readonly ISaveHelper _saveable;

        private void SaveLater() {
            _saveable.SaveLater();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public bool CanBeSaved => true;

        public string PresetableCategory => PresetableKeyValue;

        public string PresetableKey => PresetableKeyValue;

        public string DefaultPreset => null;

        public string ExportToPresetData() {
            return _saveable.ToSerializedString();
        }

        public event EventHandler Changed;

        public void ImportFromPresetData(string data) {
            _saveable.FromSerializedString(data);
        }

        private class SaveableData {
            public string ModeId;
            public string FilterValue;

            [CanBeNull]
            public string[] CarIds;

            [CanBeNull]
            public int[] CandidatePriorities;

            public bool? AiLevelFixed, AiLevelArrangeRandomly, AiLevelArrangeReverse;
            public int? AiLevel, AiLevelMin, OpponentsNumber, StartingPosition;
        }
        #endregion

        public RaceGridViewModel() {
            _saveable = new SaveHelper<SaveableData>(KeySaveable, () => {
                var data = new SaveableData {
                    ModeId = Mode.Id,
                    FilterValue = FilterValue,
                    AiLevelFixed = AiLevelFixed,
                    AiLevelArrangeRandomly = AiLevelArrangeRandomly,
                    AiLevelArrangeReverse = AiLevelArrangeReverse,
                    AiLevel = AiLevel,
                    AiLevelMin = AiLevelMin,
                    OpponentsNumber = OpponentsNumber,
                    StartingPosition = StartingPosition,
                };

                if (Mode == BuiltInGridMode.CandidatesManual) {
                    var priority = false;
                    data.CarIds = NonfilteredList.Select(x => {
                        if (x.CandidatePriority != 1) priority = true;
                        return x.Car.Id;
                    }).ToArray();

                    if (priority) {
                        data.CandidatePriorities = NonfilteredList.Select(x => x.CandidatePriority).ToArray();
                    }
                } else if (Mode == BuiltInGridMode.Custom) {
                    data.CarIds = NonfilteredList.ApartFrom(_playerEntry).Select(x => x.Car.Id).ToArray();
                }

                return data;
            }, data => {
                AiLevelFixed = data.AiLevelFixed ?? false;
                AiLevelArrangeRandomly = data.AiLevelArrangeRandomly ?? false;
                AiLevelArrangeReverse = data.AiLevelArrangeReverse ?? false;
                AiLevel = data.AiLevel ?? 95;
                AiLevelMin = data.AiLevelMin ?? 85;

                FilterValue = data.FilterValue;
                Mode = Modes.HierarchicalGetById<IRaceGridMode>(data.ModeId);

                if (Mode == BuiltInGridMode.Custom) {
                    NonfilteredList.ReplaceEverythingBy(data.CarIds?.Select(x => CarsManager.Instance.GetById(x)).Select(x => new RaceGridEntry(x)) ??
                            new RaceGridEntry[0]);
                    SetOpponentsNumberInternal(NonfilteredList.Count);
                    UpdateOpponentsNumber();
                } else {
                    if (Mode == BuiltInGridMode.CandidatesManual && data.CarIds != null) {
                        // TODO: Async?
                        NonfilteredList.ReplaceEverythingBy(data.CarIds.Select(x => CarsManager.Instance.GetById(x)).Select((x, i) => new RaceGridEntry(x) {
                            CandidatePriority = data.CandidatePriorities?.ElementAtOr(i, 1) ?? 1
                        }));
                    } else {
                        NonfilteredList.Clear();
                    }

                    SetOpponentsNumberInternal(data.OpponentsNumber ?? 7);
                    UpdateViewFilter();
                }

                StartingPosition = data.StartingPosition ?? 7;
                UpdatePlayerEntry();
            }, () => {
                AiLevelFixed = false;
                AiLevelArrangeRandomly = false;
                AiLevelArrangeReverse = false;
                AiLevel = 95;
                AiLevelMin = 85;

                FilterValue = "";
                Mode = BuiltInGridMode.SameCar;
                SetOpponentsNumberInternal(7);
                StartingPosition = 7;
            });

            _randomGroup = new HierarchicalGroup("Random");
            _presetsGroup = new HierarchicalGroup("Presets");
            UpdateRandomModes();

            Modes = new BetterObservableCollection<object> {
                BuiltInGridMode.SameCar,
                _randomGroup,
                BuiltInGridMode.Custom,
                _presetsGroup,
            };

            NonfilteredList.CollectionChanged += OnCollectionChanged;
            FilteredView = new BetterListCollectionView(NonfilteredList) { CustomSort = this };

            _saveable.Initialize();
            FilesStorage.Instance.Watcher(ContentCategory.GridTypes).Update += OnGridTypesUpdate;
        }

        #region FS watching
        private void OnGridTypesUpdate(object sender, EventArgs e) {
            UpdateRandomModes();
        }

        public void Dispose() {
            Logging.Debug("Dispose()");
            FilesStorage.Instance.Watcher(ContentCategory.GridTypes).Update -= OnGridTypesUpdate;
        }
        #endregion

        #region Non-filtered list
        public BetterObservableCollection<RaceGridEntry> NonfilteredList { get; } = new BetterObservableCollection<RaceGridEntry>();

        private void OnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e) {
            switch (e.Action) {
                case NotifyCollectionChangedAction.Reset:
                    foreach (var item in NonfilteredList) {
                        item.PropertyChanged += Entry_PropertyChanged;
                        item.Deleted += Entry_Deleted;
                    }
                    break;

                case NotifyCollectionChangedAction.Add:
                case NotifyCollectionChangedAction.Remove:
                case NotifyCollectionChangedAction.Replace:
                    if (e.OldItems != null) {
                        foreach (RaceGridEntry item in e.OldItems) {
                            item.PropertyChanged -= Entry_PropertyChanged;
                            item.Deleted -= Entry_Deleted;
                        }
                    }
                    if (e.NewItems != null) {
                        foreach (RaceGridEntry item in e.NewItems) {
                            item.PropertyChanged += Entry_PropertyChanged;
                            item.Deleted += Entry_Deleted;
                        }
                    }
                    break;

                case NotifyCollectionChangedAction.Move:
                    break;
            }
        }

        private void Entry_PropertyChanged(object sender, PropertyChangedEventArgs e) {
            switch (e.PropertyName) {
                case nameof(RaceGridEntry.CandidatePriority):
                    if (Mode.CandidatesMode) {
                        // TODO: Presets will require different behavior
                        Mode = BuiltInGridMode.CandidatesManual;
                    }
                    break;
            }
        }

        private void Entry_Deleted(object sender, EventArgs e) {
            DeleteEntry((RaceGridEntry)sender);
        }
        #endregion

        #region Active mode
        private IRaceGridMode _mode;
        private bool _modeKeepOrder;

        [NotNull]
        public IRaceGridMode Mode {
            get { return _mode; }
            set {
                if (Equals(value, _mode)) return;

                var previousMode = _mode;
                _mode = value;
                OnPropertyChanged();
                SaveLater();

                FilteredView.CustomSort = value.CandidatesMode ? this : null;
                if (_saveable.IsLoading) return;

                if (!value.CandidatesMode == previousMode?.CandidatesMode) {
                    NonfilteredList.ReplaceEverythingBy(value.CandidatesMode
                            ? CombinePriorities(NonfilteredList.ApartFrom(_playerEntry))
                            : _modeKeepOrder ? FlattenPriorities(NonfilteredList) : FlattenPriorities(NonfilteredList).Sort(Compare));
                }

                if (value.CandidatesMode) {
                    RebuildGridAsync().Forget();
                }

                UpdateViewFilter();
                UpdateOpponentsNumber();
                UpdatePlayerEntry();
                UpdateExceeded();
            }
        }

        [CanBeNull]
        private RaceGridPlayerEntry _playerEntry;

        private void UpdatePlayerEntry() {
            if (_playerCar != _playerEntry?.Car) {
                if (_playerEntry != null) {
                    NonfilteredList.Remove(_playerEntry);
                    _playerEntry = null;
                }

                if (Mode != BuiltInGridMode.Custom) return;
                _playerEntry = _playerCar == null ? null : new RaceGridPlayerEntry(_playerCar);
            }

            if (_playerEntry == null) return;
            if (Mode == BuiltInGridMode.Custom) {
                var index = NonfilteredList.IndexOf(_playerEntry);
                var pos = StartingPosition - 1;

                if (index == -1) {
                    if (pos >= 0) {
                        // TODO: add protection
                        NonfilteredList.Insert(pos, _playerEntry);
                    }
                } else {
                    if (pos < 0) {
                        NonfilteredList.RemoveAt(index);
                    } else if (pos != index) {
                        NonfilteredList.Move(index, pos);
                    }
                }
            } else if (NonfilteredList.Contains(_playerEntry)) {
                NonfilteredList.Remove(_playerEntry);
                _playerEntry = null;
            }
        }

        private void UpdateOpponentsNumber() {
            if (!Mode.CandidatesMode) {
                SetOpponentsNumberInternal(NonfilteredList.ApartFrom(_playerEntry).Count());
            }
        }

        private IEnumerable<RaceGridEntry> FlattenPriorities(IEnumerable<RaceGridEntry> candidates) {
            foreach (var entry in candidates) {
                var p = entry.CandidatePriority;
                entry.CandidatePriority = 1;

                yield return entry;
                for (var i = 1; i < p && i < 6; i++) {
                    yield return entry.Clone();
                }
            }
        }

        private IEnumerable<RaceGridEntry> CombinePriorities(IEnumerable<RaceGridEntry> entries) {
            var list = entries.ToList();
            var combined = new List<RaceGridEntry>();
            for (var i = 0; i < list.Count; i++) {
                var entry = list[i];
                if (combined.Contains(entry)) continue;

                var priority = 1;
                for (var j = i + 1; j < list.Count; j++) {
                    var next = list[j];
                    if (entry.Same(next)) {
                        priority++;
                        combined.Add(next);
                    }
                }

                entry.CandidatePriority = priority;
                yield return entry;
            }
        }
        #endregion

        #region Modes list
        private readonly HierarchicalGroup _randomGroup;
        private HierarchicalGroup _presetsGroup;

        public BetterObservableCollection<object> Modes { get; }

        private ICommand _switchModeCommand;

        public ICommand SetModeCommand => _switchModeCommand ?? (_switchModeCommand = new RelayCommand(o => {
            var mode = o as BuiltInGridMode;
            if (mode != null) {
                Mode = mode;
            }
        }, o => o is BuiltInGridMode));

        private void UpdateRandomModes() {
            Logging.Debug("UpdateRandomModes()");

            var items = new List<object> {
                BuiltInGridMode.CandidatesSameGroup,
                BuiltInGridMode.CandidatesFiltered,
                BuiltInGridMode.CandidatesManual
            };

            var dataAdded = false;
            foreach (var entry in FilesStorage.Instance.GetContentDirectory(ContentCategory.GridTypes)) {
                var list = JsonConvert.DeserializeObject<List<CandidatesGridMode>>(FileUtils.ReadAllText(entry.Filename));
                if (list.Any() && !dataAdded) {
                    items.Add(new Separator());
                    dataAdded = true;
                }

                if (entry.Name == "GridTypes") {
                    items.AddRange(list);
                } else {
                    items.Add(new HierarchicalGroup(entry.Name, list));
                }
            }

            _randomGroup.ReplaceEverythingBy(items);
        }
        #endregion

        #region Candidates building
        public bool IsBusy => _rebuildingTask != null;

        private Task _rebuildingTask;

        private Task RebuildGridAsync() {
            if (_rebuildingTask == null) {
                _rebuildingTask = RebuildGridAsyncInner();
                OnPropertyChanged(nameof(IsBusy));
            }

            return _rebuildingTask;
        }

        private async Task RebuildGridAsyncInner() {
            Logging.Debug("RebuildGridAsync(): start");
            try {
                var mode = Mode;
                await Task.Delay(50);

                OnPropertyChanged(nameof(IsBusy));

                var candidates = await FindCandidates();
                if (candidates == null || mode != Mode) return;

                NonfilteredList.ReplaceEverythingBy(candidates);
                Logging.Debug("RebuildGridAsync(): list updated");
            } catch (Exception e) {
                NonfatalError.Notify("Can�t update race grid", e);
            } finally {
                _rebuildingTask = null;
                OnPropertyChanged(nameof(IsBusy));
                Logging.Debug("RebuildGridAsync(): finished");
            }
        }

        [ItemCanBeNull]
        private async Task<IReadOnlyList<RaceGridEntry>> FindCandidates(CancellationToken cancellation = default(CancellationToken)) {
            var mode = Mode;

            // Don�t change anything in Fixed or Manual mode
            if (mode == BuiltInGridMode.Custom || mode == BuiltInGridMode.CandidatesManual) {
                return null;
            }

            // Basic mode, just one car
            if (mode == BuiltInGridMode.SameCar) {
                return _playerCar == null ? new RaceGridEntry[0] : new[] { new RaceGridEntry(_playerCar) };
            }

            // Other modes require cars list to be loaded
            if (!CarsManager.Instance.IsLoaded) {
                await CarsManager.Instance.EnsureLoadedAsync();
            }

            // Another simple mode
            if (mode == BuiltInGridMode.CandidatesFiltered) {
                return CarsManager.Instance.EnabledOnly.Select(x => new RaceGridEntry(x)).ToArray();
            }

            // Same group mode
            if (mode == BuiltInGridMode.CandidatesSameGroup) {
                if (_playerCar == null) return new RaceGridEntry[0];

                var parent = _playerCar.Parent ?? _playerCar;
                return parent.Children.Prepend(parent).Where(x => x.Enabled).Select(x => new RaceGridEntry(x)).ToArray();
            }

            // Entry from a JSON-file
            if (mode.AffectedByCar && _playerCar == null || mode.AffectedByTrack && _track == null) {
                return new RaceGridEntry[0];
            }

            var candidatesMode = mode as CandidatesGridMode;
            if (candidatesMode != null) {
                return await Task.Run(() => {
                    var carsEnumerable = (IEnumerable<CarObject>)CarsManager.Instance.EnabledOnly.ToList();

                    if (!string.IsNullOrWhiteSpace(candidatesMode.Filter)) {
                        var filter = StringBasedFilter.Filter.Create(CarObjectTester.Instance, candidatesMode.Filter);
                        carsEnumerable = carsEnumerable.Where(filter.Test);
                    }

                    if (!string.IsNullOrWhiteSpace(candidatesMode.Script)) {
                        var state = LuaHelper.GetExtended();
                        if (state == null) throw new Exception("Can�t initialize Lua");

                        if (mode.AffectedByCar) {
                            state.Globals[@"selected"] = _playerCar;
                        }

                        if (mode.AffectedByTrack) {
                            state.Globals[@"track"] = _track;
                        }

                        var result = state.DoString(PrepareScript(candidatesMode.Script)); // TODO: errors handling
                        if (result.Type == DataType.Boolean && !result.Boolean) return new RaceGridEntry[0];

                        var fn = result.Function;
                        if (fn == null) throw new InformativeException("AppStrings.Drive_InvalidScript", "Script should return filtering function");

                        carsEnumerable = carsEnumerable.Where(x => fn.Call(x).Boolean);
                    }

                    return carsEnumerable.Select(x => new RaceGridEntry(x)).ToArray();
                }, cancellation);
            }

            Logging.Error($"[RaceGridViewModel] Not supported mode: {mode.Id} ({mode.GetType().Name})");
            return new RaceGridEntry[0];
        }

        private static string PrepareScript(string script) {
            script = script.Trim();
            return script.Contains('\n') ? script : $"return function(tested)\nreturn {script}\nend";
        }
        #endregion

        #region Filtering
        public BetterListCollectionView FilteredView { get; }

        private string _filterValue;

        public string FilterValue {
            get { return _filterValue; }
            set {
                value = value?.Trim();
                if (Equals(value, _filterValue)) return;
                _filterValue = value;
                OnPropertyChanged();

                if (_saveable.IsLoading) return;
                UpdateViewFilter();

                SaveLater();
            }
        }

        private void UpdateViewFilter() {
            using (FilteredView.DeferRefresh()) {
                if (string.IsNullOrEmpty(FilterValue) || Mode == BuiltInGridMode.SameCar || Mode == BuiltInGridMode.Custom) {
                    FilteredView.Filter = null;
                } else {
                    var filter = StringBasedFilter.Filter.Create(CarObjectTester.Instance, FilterValue);
                    FilteredView.Filter = o => filter.Test(((RaceGridEntry)o).Car);
                }
            }
        }

        public int Compare(object x, object y) {
            return (x as RaceGridEntry)?.Car.CompareTo((y as RaceGridEntry)?.Car) ?? 0;
        }
        #endregion

        #region Car and track
        [CanBeNull]
        private CarObject _playerCar;

        public void SetPlayerCar(CarObject car) {
            _playerCar = car;
            if (Mode.AffectedByCar) {
                RebuildGridAsync().Forget();
            }

            if (!Mode.CandidatesMode && StartingPosition > 0) {
                UpdatePlayerEntry();
            }
        }

        [CanBeNull]
        private TrackObjectBase _track;

        public void SetTrack(TrackObjectBase track) {
            if (_track != null) {
                _track.PropertyChanged -= Track_OnPropertyChanged;
            }

            _track = track;
            if (Mode.AffectedByTrack) {
                RebuildGridAsync().Forget();
            }

            if (track != null) {
                TrackPitsNumber = FlexibleParser.ParseInt(track.SpecsPitboxes, 2);
                track.PropertyChanged += Track_OnPropertyChanged;
            }
        }

        private void Track_OnPropertyChanged(object sender, PropertyChangedEventArgs e) {
            if (_track != null && e.PropertyName == nameof(TrackObjectBase.SpecsPitboxes)) {
                TrackPitsNumber = FlexibleParser.ParseInt(_track.SpecsPitboxes, 2);
            }
        }

        private int _trackPitsNumber;

        public int TrackPitsNumber {
            get { return _trackPitsNumber; }
            set {
                if (Equals(value, _trackPitsNumber)) return;
                _trackPitsNumber = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(OpponentsNumberLimit));
                OnPropertyChanged(nameof(OpponentsNumberLimited));
                UpdateExceeded();
            }
        }

        public int OpponentsNumberLimit => TrackPitsNumber - 1;

        private void UpdateExceeded() {
            if (Mode.CandidatesMode) {
                foreach (var entry in NonfilteredList.Where(x => x.ExceedsLimit)) {
                    entry.ExceedsLimit = false;
                }
            } else {
                var left = OpponentsNumberLimit;
                foreach (var entry in NonfilteredList.ApartFrom(_playerEntry)) {
                    entry.ExceedsLimit = --left < 0;
                }
            }
        }
        #endregion

        #region Simple properties
        public int AiLevelMinimum => SettingsHolder.Drive.QuickDriveExpandBounds ? 30 : 70;

        public int AiLevelMinimumLimited => Math.Max(AiLevelMinimum, 50);

        private int _aiLevel;

        public int AiLevel {
            get { return _aiLevel; }
            set {
                value = value.Clamp(SettingsHolder.Drive.AiLevelMinimum, 100);
                if (Equals(value, _aiLevel)) return;
                _aiLevel = value;
                OnPropertyChanged();
                SaveLater();

                if (!AiLevelFixed && value >= AiLevelMin) return;
                _aiLevelMin = value;
                OnPropertyChanged(nameof(AiLevelMin));
            }
        }

        private int _aiLevelMin;

        public int AiLevelMin {
            get { return _aiLevelMin; }
            set {
                if (AiLevelFixed) return;

                value = value.Clamp(SettingsHolder.Drive.AiLevelMinimum, 100);
                if (Equals(value, _aiLevelMin)) return;
                _aiLevelMin = value;
                OnPropertyChanged();
                SaveLater();

                if (value > AiLevel) {
                    _aiLevel = value;
                    OnPropertyChanged(nameof(AiLevel));
                }
            }
        }

        private bool _aiLevelFixed;

        public bool AiLevelFixed {
            get { return _aiLevelFixed; }
            set {
                if (Equals(value, _aiLevelFixed)) return;
                _aiLevelFixed = value;
                OnPropertyChanged();
                SaveLater();

                if (value && _aiLevelMin != _aiLevel) {
                    _aiLevelMin = _aiLevel;
                    OnPropertyChanged(nameof(AiLevelMin));
                    OnPropertyChanged(nameof(AiLevelInDriverName));
                }
            }
        }

        private bool _aiLevelArrangeRandomly;

        public bool AiLevelArrangeRandomly {
            get { return _aiLevelArrangeRandomly; }
            set {
                if (Equals(value, _aiLevelArrangeRandomly)) return;
                _aiLevelArrangeRandomly = value;
                OnPropertyChanged();
                SaveLater();

                if (value) {
                    AiLevelArrangeReverse = false;
                }
            }
        }

        private bool _aiLevelArrangeReverse;

        public bool AiLevelArrangeReverse {
            get { return _aiLevelArrangeReverse; }
            set {
                if (Equals(value, _aiLevelArrangeReverse)) return;
                _aiLevelArrangeReverse = value;
                OnPropertyChanged();
                SaveLater();

                if (value) {
                    AiLevelArrangeRandomly = false;
                }
            }
        }

        private const string KeyAiLevelInDriverName = "QuickDrive_GridTest.AiLevelInDriverName";

        private bool _aiLevelInDriverName = ValuesStorage.GetBool(KeyAiLevelInDriverName);

        public bool AiLevelInDriverName {
            get { return _aiLevelInDriverName && !AiLevelFixed; }
            set {
                if (Equals(value, _aiLevelInDriverName)) return;
                _aiLevelInDriverName = value;
                OnPropertyChanged();
                ValuesStorage.Set(KeyAiLevelInDriverName, value);
            }
        }
        #endregion

        #region Grid methods (addind, deleting)
        public void AddEntry([NotNull] CarObject car) {
            AddEntry(new RaceGridEntry(car));
        }

        public void AddEntry([NotNull] RaceGridEntry entry) {
            InsertEntry(-1, entry);
        }

        public void InsertEntry(int index, [NotNull] CarObject car) {
            InsertEntry(index, new RaceGridEntry(car));
        }

        public void InsertEntry(int index, [NotNull] RaceGridEntry entry) {
            var oldIndex = NonfilteredList.IndexOf(entry);

            var count = NonfilteredList.Count;
            var isNew = oldIndex == -1;
            var limit = isNew ? count : count - 1;
            if (index < 0 || index > limit) {
                index = limit;
            }

            if (oldIndex == index) return;
            if (Mode.CandidatesMode) {
                if (isNew) {
                    Mode = BuiltInGridMode.CandidatesManual;

                    var existed = NonfilteredList.FirstOrDefault(x => x.Same(entry));
                    if (existed != null) {
                        existed.CandidatePriority++;
                    } else {
                        NonfilteredList.Add(entry);
                    }

                    return;
                }

                NonfilteredList.ReplaceEverythingBy(NonfilteredList.Sort(Compare));
                NonfilteredList.Move(oldIndex, index);

                try {
                    _modeKeepOrder = true;
                    Mode = BuiltInGridMode.Custom;
                } finally {
                    _modeKeepOrder = false;
                }
            } else if (isNew) {
                Logging.Debug("Index: " + index + ", count: " + count);
                if (index == count) {
                    NonfilteredList.Add(entry);
                } else {
                    NonfilteredList.Insert(index, entry);
                }

                UpdateOpponentsNumber();
            } else if (index != oldIndex) {
                NonfilteredList.Move(oldIndex, index);
            }

            if (StartingPosition != 0) {
                StartingPositionLimited = NonfilteredList.IndexOf(_playerEntry) + 1;
            }

            UpdateExceeded();
        }

        public void DeleteEntry(RaceGridEntry entry) {
            if (entry is RaceGridPlayerEntry) {
                StartingPosition = 0;
                return;
            }

            NonfilteredList.Remove(entry);
            if (Mode == BuiltInGridMode.Custom) {
                UpdateOpponentsNumber();
            } else {
                Mode = BuiltInGridMode.CandidatesManual;
            }

            UpdateExceeded();
        }
        #endregion

        #region Opponents number and starting position
        private int _opponentsNumber;

        private void SetOpponentsNumberInternal(int value) {
            if (Equals(value, _opponentsNumber)) return;

            var last = Mode.CandidatesMode && _startingPosition == StartingPositionLimit;
            _opponentsNumber = value;

            OnPropertyChanged(nameof(OpponentsNumber));
            OnPropertyChanged(nameof(OpponentsNumberLimited));
            OnPropertyChanged(nameof(StartingPositionLimit));

            if (last || _startingPosition > StartingPositionLimit) {
                StartingPositionLimited = StartingPositionLimit;
            } else {
                OnPropertyChanged(nameof(StartingPositionLimited));
                SaveLater();
            }
        }

        public int OpponentsNumber {
            get { return _opponentsNumber; }
            set {
                if (!Mode.CandidatesMode) return;
                if (value < 1) value = 1;
                SetOpponentsNumberInternal(value);
            }
        }

        public int OpponentsNumberLimited {
            get { return _opponentsNumber.Clamp(0, OpponentsNumberLimit); }
            set { OpponentsNumber = value.Clamp(1, OpponentsNumberLimit); }
        }

        public int StartingPositionLimit => OpponentsNumberLimited + 1;

        private int _startingPosition;

        public int StartingPosition {
            get { return _startingPosition; }
            set {
                if (value < 0) value = 0;
                if (Equals(value, _startingPosition)) return;
                _startingPosition = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StartingPositionLimited));
                SaveLater();

                if (!_saveable.IsLoading) {
                    UpdatePlayerEntry();
                }
            }
        }

        public int StartingPositionLimited {
            get { return _startingPosition.Clamp(0, StartingPositionLimit); }
            set { StartingPosition = value.Clamp(0, StartingPositionLimit); }
        }
        #endregion

        #region Generation
        [ItemCanBeNull]
        public async Task<IList<Game.AiCar>> GenerateGameEntries(CancellationToken cancellation = default(CancellationToken)) {
            if (IsBusy) {
                await RebuildGridAsync();
                if (cancellation.IsCancellationRequested) return null;
            }

            var opponentsNumber = OpponentsNumberLimited;
            if (FilteredView.Count == 0 || opponentsNumber == 0) {
                return new Game.AiCar[0];
            }

            var skins = new Dictionary<string, GoodShuffle<CarSkinObject>>();
            foreach (var car in FilteredView.OfType<RaceGridEntry>().Where(x => x.CarSkin == null).Select(x => x.Car).Distinct()) {
                await car.SkinsManager.EnsureLoadedAsync();
                if (cancellation.IsCancellationRequested) return null;

                skins[car.Id] = GoodShuffle.Get(car.EnabledOnlySkins);
            }

            NameNationality[] nameNationalities;
            if (opponentsNumber == 7 && OptionNfsPorscheNames) {
                nameNationalities = new[] {
                        new NameNationality { Name = "Dylan", Nationality = "Wales" },
                        new NameNationality { Name = "Parise", Nationality = "Italy" },
                        new NameNationality { Name = "Steele", Nationality = "United States" },
                        new NameNationality { Name = "Wingnut", Nationality = "England" },
                        new NameNationality { Name = "Leadfoot", Nationality = "Australia" },
                        new NameNationality { Name = "Amazon", Nationality = "United States" },
                        new NameNationality { Name = "Backlash", Nationality = "United States" }
                    };
            } else if (DataProvider.Instance.NationalitiesAndNames.Any()) {
                nameNationalities = GoodShuffle.Get(DataProvider.Instance.NationalitiesAndNamesList).Take(opponentsNumber).ToArray();
            } else {
                nameNationalities = null;
            }

            List<int> aiLevels;
            if (AiLevelFixed) {
                aiLevels = null;
            } else {
                var aiLevelsInner = from i in Enumerable.Range(0, opponentsNumber)
                                    select AiLevelMin + (int)((opponentsNumber < 2 ? 1f : (float)i / (opponentsNumber - 1)) * (AiLevel - AiLevelMin));
                if (AiLevelArrangeRandomly) {
                    aiLevelsInner = GoodShuffle.Get(aiLevelsInner);
                } else if (!AiLevelArrangeReverse) {
                    aiLevelsInner = aiLevelsInner.Reverse();
                }

                aiLevels = AiLevelFixed ? null : aiLevelsInner.Take(opponentsNumber).ToList();
            }

            IEnumerable<RaceGridEntry> final;
            if (Mode.CandidatesMode) {
                var list = FilteredView.OfType<RaceGridEntry>().SelectMany(x => new[] { x }.Repeat(x.CandidatePriority)).ToList();
                var shuffled = GoodShuffle.Get(list);

                if (_playerCar != null) {
                    var same = list.FirstOrDefault(x => x.Car == _playerCar);
                    if (same != null) {
                        shuffled.IgnoreOnce(same);
                    }
                }

                final = shuffled.Take(opponentsNumber);
            } else {
                final = NonfilteredList.ApartFrom(_playerEntry);
            }

            if (_playerCar != null) {
                skins.GetValueOrDefault(_playerCar.Id)?.IgnoreOnce(_playerCar.SelectedSkin);
            }

            return final.Take(OpponentsNumberLimited).Select((entry, i) => {
                var level = entry.AiLevel ?? aiLevels?[i] ?? 100;
                var name = entry.Name ?? nameNationalities?[i].Name ?? @"AI #" + i;
                var nationality = entry.Nationality ?? nameNationalities?[i].Nationality ?? @"Italy";
                var skinId = entry.CarSkin?.Id ?? skins.GetValueOrDefault(entry.Car.Id)?.Next?.Id;

                return new Game.AiCar {
                    AiLevel = level,
                    CarId = entry.Car.Id,
                    DriverName = AiLevelInDriverName ? $"{name} ({level}%)" : name,
                    Nationality = nationality,
                    Setup = "",
                    SkinId = skinId
                };
            }).ToList();
        }
        #endregion
    }
}