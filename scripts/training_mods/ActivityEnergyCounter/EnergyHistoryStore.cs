using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ActivityEnergyCounter
{
    /// <summary>
    /// Canonical, display-independent energy totals.  History is always stored
    /// in kilo-joules; kcal conversion belongs to the presentation layer.
    /// </summary>
    public class EnergyTotals
    {
        public double ActiveKiloJoules { get; set; }
        public double GrossKiloJoules { get; set; }
        public double EquivalentSeconds { get; set; }
        public double ValidPlayingSeconds { get; set; }

        internal EnergyTotals Copy()
        {
            return new EnergyTotals
            {
                ActiveKiloJoules = ActiveKiloJoules,
                GrossKiloJoules = GrossKiloJoules,
                EquivalentSeconds = EquivalentSeconds,
                ValidPlayingSeconds = ValidPlayingSeconds
            };
        }

    }

    /// <summary>
    /// Owns the durable activity history for the energy counter.  Mutations are
    /// made in memory under one lock; a single background thread is the only
    /// writer of history.json.
    /// </summary>
    public sealed class EnergyHistoryStore : IDisposable
    {
        private const int CurrentSchemaVersion = 1;
        private const string CurrentGameBuild = "1.45.2_28848";
        private const long MaximumFileBytes = 8L * 1024L * 1024L;
        private const int MaximumSessions = 10000;
        private const int MaximumPlaysPerSession = 100000;
        private const int MaximumDaysPerRecord = 10000;
        private const double PlayingDurationToleranceSeconds = 0.001;
        private static readonly TimeSpan DefaultDisposeTimeout = TimeSpan.FromSeconds(2);

        private sealed class HistoryDocument
        {
            public int SchemaVersion { get; set; }
            public string GameBuild { get; set; }
            public Dictionary<string, SessionRecord> Sessions { get; set; }
        }

        private sealed class SessionRecord
        {
            public string SessionId { get; set; }
            public string StartedUtc { get; set; }
            public Dictionary<string, PlayRecord> Plays { get; set; }
        }

        private sealed class PlayRecord
        {
            public string PlayId { get; set; }
            public string BeatmapKey { get; set; }
            public string ModelVersion { get; set; }
            public double BodyMassKg { get; set; }
            public double ReferenceHandSpeedMps { get; set; }
            public double CalibrationFactor { get; set; }
            public string StartedUtc { get; set; }
            public string EndedUtc { get; set; }
            public string Status { get; set; }
            public string Outcome { get; set; }
            public long LastSequence { get; set; }
            public string LastEndUtc { get; set; }
            public Dictionary<string, EnergyTotals> Days { get; set; }
        }

        private sealed class FlushWaiter
        {
            public long Version;
            public readonly TaskCompletionSource<bool> Completion =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class UnsupportedHistoryException : Exception
        {
            public UnsupportedHistoryException(string message) : base(message) { }
        }

        private sealed class DaySlice
        {
            public string Day;
            public double Fraction;
        }

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            MissingMemberHandling = MissingMemberHandling.Error,
            TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Double,
            MaxDepth = 32,
            Formatting = Formatting.Indented,
            Culture = CultureInfo.InvariantCulture
        };

        private readonly object _gate = new object();
        private readonly string _historyPath;
        private readonly string _backupPath;
        private readonly string _historyDirectory;
        private readonly Action<string, Exception> _report;
        private readonly Func<DateTimeOffset> _clock;
        private readonly TimeZoneInfo _timeZone;
        private readonly AutoResetEvent _saveSignal = new AutoResetEvent(false);
        private readonly ManualResetEvent _stopSignal = new ManualResetEvent(false);
        private readonly List<FlushWaiter> _waiters = new List<FlushWaiter>();
        private Thread _worker;

        private HistoryDocument _document;
        private string _sessionId;
        private bool _dirty;
        private bool _saveRequested;
        private bool _saveBlocked;
        private bool _workerUnavailable;
        private bool _isAvailable = true;
        private bool _disposed;
        private long _changeVersion;
        private long _persistedVersion;
        private string _lastError;

        public EnergyHistoryStore(string gameRoot, Action<string, Exception> report = null,
            Func<DateTimeOffset> clock = null, TimeZoneInfo timeZone = null)
        {
            string root = Path.GetFullPath(String.IsNullOrWhiteSpace(gameRoot)
                ? Environment.CurrentDirectory : gameRoot);
            _historyDirectory = Path.Combine(root, "UserData", "ActivityEnergyCounter");
            _historyPath = Path.Combine(_historyDirectory, "history.json");
            _backupPath = _historyPath + ".bak";
            _report = report;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _timeZone = timeZone ?? TimeZoneInfo.Local;
            _document = NewDocument();

            try
            {
                bool recovered;
                _document = LoadDocument(out recovered);
                if (recovered)
                {
                    _dirty = true;
                    _changeVersion++;
                }
            }
            catch (Exception error)
            {
                _saveBlocked = true;
                _isAvailable = false;
                _dirty = true;
                _changeVersion++;
                _lastError = "Activity energy history is unavailable.";
                Report("history-read", error);
            }

            lock (_gate)
            {
                MarkPlayingRecordsInterruptedLocked();
                StartNewSessionLocked();
            }

            try
            {
                _worker = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "ActivityEnergyCounter history writer"
                };
                _worker.Start();
            }
            catch (Exception error)
            {
                lock (_gate)
                {
                    _workerUnavailable = true;
                    _lastError = "Activity energy history writer is unavailable.";
                }
                Report("history-worker", error);
            }
        }

        public string SessionId
        {
            get { lock (_gate) return _sessionId; }
        }

        public bool IsAvailable
        {
            get { lock (_gate) return _isAvailable; }
        }

        public EnergyTotals SessionTotals
        {
            get
            {
                lock (_gate)
                {
                    if (!_isAvailable) return null;
                    SessionRecord session;
                    if (_sessionId == null || !_document.Sessions.TryGetValue(_sessionId, out session))
                        return new EnergyTotals();
                    return SumPlayDays(session);
                }
            }
        }

        public EnergyTotals TodayTotals
        {
            get
            {
                lock (_gate)
                {
                    if (!_isAvailable) return null;
                    string today = DayKey(NowUtc());
                    EnergyTotals result = new EnergyTotals();
                    foreach (SessionRecord session in _document.Sessions.Values)
                    {
                        foreach (PlayRecord play in session.Plays.Values)
                        {
                            EnergyTotals totals;
                            if (play.Days != null && play.Days.TryGetValue(today, out totals))
                                AddTotals(result, totals);
                        }
                    }
                    return result;
                }
            }
        }

        public string LastError
        {
            get { lock (_gate) return _lastError; }
        }

        public bool HasUnsavedChanges
        {
            get { lock (_gate) return _dirty; }
        }

        public void StartNewSession()
        {
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                MarkPlayingRecordsInterruptedLocked();
                StartNewSessionLocked();
            }
        }

        public string BeginPlay(string beatmapKey, string modelVersion, double bodyMassKg,
            double referenceHandSpeedMps, double calibrationFactor)
        {
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                if (_saveBlocked) throw new InvalidOperationException("Activity energy history is read-only because its source data could not be validated.");
                ValidatePlayArguments(beatmapKey, modelVersion, bodyMassKg, referenceHandSpeedMps, calibrationFactor);

                SessionRecord session = _document.Sessions[_sessionId];
                if (session.Plays.Count >= MaximumPlaysPerSession)
                    throw new InvalidOperationException("Activity energy session contains too many plays.");
                string playId = Guid.NewGuid().ToString("N");
                string started = FormatUtc(NowUtc());
                session.Plays[playId] = new PlayRecord
                {
                    PlayId = playId,
                    BeatmapKey = beatmapKey,
                    ModelVersion = modelVersion,
                    BodyMassKg = bodyMassKg,
                    ReferenceHandSpeedMps = referenceHandSpeedMps,
                    CalibrationFactor = calibrationFactor,
                    StartedUtc = started,
                    EndedUtc = string.Empty,
                    Status = "playing",
                    Outcome = string.Empty,
                    LastSequence = -1,
                    LastEndUtc = string.Empty,
                    Days = new Dictionary<string, EnergyTotals>(StringComparer.Ordinal)
                };
                MarkDirtyLocked();
                return playId;
            }
        }

        public bool AddInterval(string playId, long sequence, DateTimeOffset startUtc,
            DateTimeOffset endUtc, EnergyTotals delta)
        {
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                string validationError;
                if (_saveBlocked)
                    return RejectLocked("Activity energy history is read-only because its source data could not be validated.");
                if (!IsValidId(playId))
                    return RejectLocked("Activity energy interval has an invalid play id.");
                if (sequence < 0)
                    return RejectLocked("Activity energy interval sequence must be non-negative.");
                if (!TryValidateTotals(delta, out validationError))
                    return RejectLocked(validationError);

                SessionRecord session;
                PlayRecord play;
                if (!_document.Sessions.TryGetValue(_sessionId, out session) ||
                    !session.Plays.TryGetValue(playId, out play))
                    return RejectLocked("Activity energy interval refers to an unknown play.");
                if (play.Status != "playing")
                    return RejectLocked("Activity energy interval belongs to a finished play.");
                if (sequence <= play.LastSequence)
                    return RejectLocked("Activity energy interval sequence is duplicate or older than the last accepted sample.");

                DateTimeOffset start;
                DateTimeOffset end;
                try
                {
                    start = startUtc.ToUniversalTime();
                    end = endUtc.ToUniversalTime();
                }
                catch (Exception error)
                {
                    Report("history-validation", error);
                    return RejectLocked("Activity energy interval timestamps are invalid.");
                }
                if (!IsValidUtc(start) || !IsValidUtc(end) || end <= start)
                    return RejectLocked("Activity energy interval timestamps must be finite UTC instants with end after start.");
                TimeSpan interval;
                try { interval = end - start; }
                catch (Exception error)
                {
                    Report("history-validation", error);
                    return RejectLocked("Activity energy interval timestamps are invalid.");
                }
                double intervalSeconds = interval.TotalSeconds;
                if (Double.IsNaN(intervalSeconds) || Double.IsInfinity(intervalSeconds))
                    return RejectLocked("Activity energy interval timestamps are invalid.");
                if (intervalSeconds > TimeSpan.FromDays(7).TotalSeconds)
                    return RejectLocked("Activity energy interval is too long.");
                if (delta.ValidPlayingSeconds > intervalSeconds + PlayingDurationToleranceSeconds)
                    return RejectLocked("Activity energy playing duration exceeds the interval.");
                if (!String.IsNullOrEmpty(play.LastEndUtc))
                {
                    DateTimeOffset lastEnd;
                    if (!TryParseUtc(play.LastEndUtc, out lastEnd) || start < lastEnd)
                        return RejectLocked("Activity energy interval is older than the last accepted timestamp.");
                }

                List<DaySlice> slices;
                try { slices = SplitByLocalDay(start, end); }
                catch (Exception error)
                {
                    Report("history-validation", error);
                    return RejectLocked("Activity energy interval could not be mapped to local time.");
                }
                if (slices.Count == 0)
                    return RejectLocked("Activity energy interval could not be split into local calendar days.");

                // Validate every candidate before mutating either the play or
                // session aggregates, so a failed overflow cannot partially add.
                foreach (DaySlice slice in slices)
                {
                    EnergyTotals contribution = Scale(delta, slice.Fraction);
                    EnergyTotals playDay;
                    if (!play.Days.TryGetValue(slice.Day, out playDay)) playDay = new EnergyTotals();
                    if (!CanAdd(playDay, contribution))
                        return RejectLocked("Activity energy totals would become non-finite.");
                }

                foreach (DaySlice slice in slices)
                {
                    EnergyTotals contribution = Scale(delta, slice.Fraction);
                    EnergyTotals playDay = GetOrCreate(play.Days, slice.Day);
                    AddTotals(playDay, contribution);
                }

                play.LastSequence = sequence;
                play.LastEndUtc = FormatUtc(end);
                MarkDirtyLocked();
                return true;
            }
        }

        public void EndPlay(string playId, string outcome)
        {
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                if (_saveBlocked)
                {
                    SetErrorLocked("Activity energy history is unavailable.");
                    return;
                }
                if (!IsValidId(playId))
                {
                    SetErrorLocked("Activity energy play id is invalid.");
                    return;
                }
                SessionRecord session;
                PlayRecord play;
                if (!_document.Sessions.TryGetValue(_sessionId, out session) ||
                    !session.Plays.TryGetValue(playId, out play))
                {
                    SetErrorLocked("Activity energy play was not found.");
                    return;
                }
                if (play.Status != "playing") return; // Terminal transitions are idempotent.
                string normalizedOutcome = NormalizeOutcome(outcome);
                if (normalizedOutcome == null)
                {
                    SetErrorLocked("Activity energy outcome is invalid.");
                    return;
                }
                play.Status = normalizedOutcome;
                play.Outcome = normalizedOutcome;
                play.EndedUtc = FormatUtc(EndTimeForPlayLocked(play));
                MarkDirtyLocked();
            }
        }

        public void RequestSave()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    SetErrorLocked("Activity energy history is closed.");
                    return;
                }
                if (_saveBlocked || _workerUnavailable || !_dirty) return;
                _saveRequested = true;
            }
            _saveSignal.Set();
        }

        public bool Flush(TimeSpan timeout)
        {
            if (timeout < TimeSpan.Zero || Double.IsNaN(timeout.TotalMilliseconds)
                || Double.IsInfinity(timeout.TotalMilliseconds))
                throw new ArgumentOutOfRangeException(nameof(timeout));
            FlushWaiter waiter;
            lock (_gate)
            {
                if (_disposed) return false;
                if (_saveBlocked) return false;
                if (!_dirty) return true;
                if (_workerUnavailable) return false;
                waiter = new FlushWaiter { Version = _changeVersion };
                _waiters.Add(waiter);
                _saveRequested = true;
            }
            _saveSignal.Set();

            int milliseconds = timeout.TotalMilliseconds >= Int32.MaxValue
                ? Int32.MaxValue
                : (int)Math.Max(0, Math.Ceiling(timeout.TotalMilliseconds));
            if (!waiter.Completion.Task.Wait(milliseconds)) return false;
            return waiter.Completion.Task.Result;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
            }

            Flush(DefaultDisposeTimeout);
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                CompleteAllWaitersLocked(false);
            }
            _stopSignal.Set();
            bool workerStopped = _worker == null || !_worker.IsAlive;
            if (_worker != null && _worker != Thread.CurrentThread)
            {
                try
                {
                    if (_worker.Join((int)DefaultDisposeTimeout.TotalMilliseconds)) workerStopped = true;
                }
                catch (Exception error) { Report("history-worker", error); }
            }
            // A blocked filesystem call can outlive Dispose's bounded wait.  In
            // that case leave the wait handles alive for the background writer
            // rather than disposing them underneath it.
            if (workerStopped)
            {
                _saveSignal.Dispose();
                _stopSignal.Dispose();
            }
        }

        private void WorkerLoop()
        {
            try
            {
                WaitHandle[] handles = { _saveSignal, _stopSignal };
                while (true)
                {
                    int signaled = WaitHandle.WaitAny(handles);
                    if (signaled == 1) break;
                    ProcessSave();
                }
            }
            catch (Exception error)
            {
                lock (_gate)
                {
                    _workerUnavailable = true;
                    _lastError = "Activity energy history writer is unavailable.";
                    CompleteAllWaitersLocked(false);
                }
                Report("history-worker", error);
            }
        }

        private void ProcessSave()
        {
            HistoryDocument snapshot;
            long version;
            lock (_gate)
            {
                if (_disposed || _saveBlocked || _workerUnavailable || !_saveRequested || !_dirty)
                {
                    CompleteWaitersLocked(false, _persistedVersion);
                    return;
                }
                _saveRequested = false;
                snapshot = CloneDocument(_document);
                version = _changeVersion;
            }

            try
            {
                ValidateDocument(snapshot);
                WriteDocument(snapshot);
                lock (_gate)
                {
                    _persistedVersion = Math.Max(_persistedVersion, version);
                    if (_changeVersion == version) _dirty = false;
                    _lastError = null;
                    CompleteWaitersLocked(true, _persistedVersion);
                }
            }
            catch (Exception error)
            {
                lock (_gate)
                {
                    _lastError = "Activity energy history could not be saved.";
                    CompleteWaitersLocked(false, version);
                }
                Report("history-write", error);
            }
        }

        private void CompleteWaitersLocked(bool success, long persistedVersion)
        {
            for (int index = _waiters.Count - 1; index >= 0; index--)
            {
                FlushWaiter waiter = _waiters[index];
                if (persistedVersion >= waiter.Version)
                {
                    _waiters.RemoveAt(index);
                    waiter.Completion.TrySetResult(success);
                }
            }
        }

        private void CompleteAllWaitersLocked(bool result)
        {
            foreach (FlushWaiter waiter in _waiters) waiter.Completion.TrySetResult(result);
            _waiters.Clear();
        }

        private HistoryDocument LoadDocument(out bool recovered)
        {
            recovered = false;
            if (File.Exists(_historyPath))
            {
                try { return ReadDocument(_historyPath); }
                catch (UnsupportedHistoryException) { throw; }
                catch (Exception primaryError)
                {
                    if (!File.Exists(_backupPath)) throw new InvalidDataException(primaryError.Message, primaryError);
                    HistoryDocument backup;
                    try { backup = ReadDocument(_backupPath); }
                    catch (Exception backupError)
                    {
                        throw new InvalidDataException("The primary history and its last-good backup are invalid. " + backupError.Message, primaryError);
                    }
                    PreserveCorruptPrimary();
                    recovered = true;
                    _lastError = "Activity energy history recovered from backup.";
                    Report("history-recovery", primaryError);
                    return backup;
                }
            }

            if (File.Exists(_backupPath))
            {
                HistoryDocument backup = ReadDocument(_backupPath);
                recovered = true;
                _lastError = "Activity energy history recovered from backup.";
                return backup;
            }
            return NewDocument();
        }

        private void PreserveCorruptPrimary()
        {
            string preserved = _historyPath + ".corrupt-" + Guid.NewGuid().ToString("N") + ".json";
            try
            {
                File.Move(_historyPath, preserved);
            }
            catch (Exception error)
            {
                _saveBlocked = true;
                throw new IOException("The damaged primary history could not be preserved.", error);
            }
        }

        private static HistoryDocument ReadDocument(string path)
        {
            FileInfo info = new FileInfo(path);
            if (info.Length > MaximumFileBytes) throw new InvalidDataException("Activity energy history file is too large.");
            string json = File.ReadAllText(path, new UTF8Encoding(false, true));
            JObject root;
            using (StringReader textReader = new StringReader(json))
            using (JsonTextReader jsonReader = new JsonTextReader(textReader)
            {
                DateParseHandling = DateParseHandling.None,
                MaxDepth = 32
            })
            {
                root = JObject.Load(jsonReader, new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                    LineInfoHandling = LineInfoHandling.Load
                });
            }
            RejectMetadataProperties(root);
            if (root.Property("SchemaVersion") == null || root.Property("GameBuild") == null || root.Property("Sessions") == null)
                throw new InvalidDataException("Activity energy history is missing required schema fields.");
            if (root["SchemaVersion"].Type == JTokenType.Integer
                && root["SchemaVersion"].Value<int>() != CurrentSchemaVersion)
                throw new UnsupportedHistoryException("Unsupported activity energy history schema.");
            if (root["GameBuild"].Type == JTokenType.String
                && !String.Equals(root["GameBuild"].Value<string>(), CurrentGameBuild, StringComparison.Ordinal))
                throw new UnsupportedHistoryException("Unsupported activity energy history game build.");
            ValidateJsonShape(root);
            HistoryDocument document = JsonConvert.DeserializeObject<HistoryDocument>(root.ToString(Formatting.None), JsonSettings);
            ValidateDocument(document);
            return document;
        }

        private static void ValidateJsonShape(JObject root)
        {
            RequireProperty(root, "SchemaVersion", JTokenType.Integer);
            RequireProperty(root, "GameBuild", JTokenType.String);
            JObject sessions = RequireObject(root, "Sessions");
            foreach (JProperty sessionProperty in sessions.Properties())
            {
                JObject session = RequireObjectValue(sessionProperty.Value, "session");
                RequireProperty(session, "SessionId", JTokenType.String);
                RequireProperty(session, "StartedUtc", JTokenType.String);
                JObject plays = RequireObject(session, "Plays");
                foreach (JProperty playProperty in plays.Properties())
                {
                    JObject play = RequireObjectValue(playProperty.Value, "play");
                    RequireProperty(play, "PlayId", JTokenType.String);
                    RequireProperty(play, "BeatmapKey", JTokenType.String);
                    RequireProperty(play, "ModelVersion", JTokenType.String);
                    RequireNumber(play, "BodyMassKg");
                    RequireNumber(play, "ReferenceHandSpeedMps");
                    RequireNumber(play, "CalibrationFactor");
                    RequireProperty(play, "StartedUtc", JTokenType.String);
                    RequireProperty(play, "EndedUtc", JTokenType.String);
                    RequireProperty(play, "Status", JTokenType.String);
                    RequireProperty(play, "Outcome", JTokenType.String);
                    RequireProperty(play, "LastSequence", JTokenType.Integer);
                    RequireProperty(play, "LastEndUtc", JTokenType.String);
                    JObject days = RequireObject(play, "Days");
                    foreach (JProperty dayProperty in days.Properties())
                        ValidateTotalsShape(RequireObjectValue(dayProperty.Value, "day"));
                }
            }
        }

        private static void ValidateTotalsShape(JObject totals)
        {
            RequireNumber(totals, "ActiveKiloJoules");
            RequireNumber(totals, "GrossKiloJoules");
            RequireNumber(totals, "EquivalentSeconds");
            RequireNumber(totals, "ValidPlayingSeconds");
        }

        private static JObject RequireObject(JToken parent, string name)
        {
            JObject parentObject = parent as JObject;
            JToken value = parentObject == null ? null : parentObject[name];
            return RequireObjectValue(value, name);
        }

        private static JObject RequireObjectValue(JToken value, string name)
        {
            if (!(value is JObject)) throw new InvalidDataException("Activity energy history field is not an object: " + name);
            return (JObject)value;
        }

        private static JToken RequireProperty(JObject parent, string name, JTokenType type)
        {
            JToken value = parent[name];
            if (value == null || value.Type != type)
                throw new InvalidDataException("Activity energy history field is missing or invalid: " + name);
            return value;
        }

        private static void RequireNumber(JObject parent, string name)
        {
            JToken value = parent[name];
            if (value == null || (value.Type != JTokenType.Integer && value.Type != JTokenType.Float))
                throw new InvalidDataException("Activity energy history number is missing or invalid: " + name);
        }

        private static void RejectMetadataProperties(JToken token)
        {
            JObject objectToken = token as JObject;
            if (objectToken != null)
            {
                foreach (JProperty property in objectToken.Properties())
                {
                    if (property.Name.StartsWith("$", StringComparison.Ordinal))
                        throw new InvalidDataException("Activity energy history contains an unsupported metadata property.");
                    RejectMetadataProperties(property.Value);
                }
                return;
            }
            JArray arrayToken = token as JArray;
            if (arrayToken != null)
                foreach (JToken child in arrayToken) RejectMetadataProperties(child);
        }

        private void WriteDocument(HistoryDocument document)
        {
            string json = JsonConvert.SerializeObject(document, JsonSettings);
            if (Encoding.UTF8.GetByteCount(json) > MaximumFileBytes)
                throw new InvalidDataException("Activity energy history would exceed the supported file size.");

            Directory.CreateDirectory(_historyDirectory);
            string temporary = Path.Combine(_historyDirectory, ".history-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(json);
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(_historyPath)) File.Replace(temporary, _historyPath, _backupPath, true);
                else File.Move(temporary, _historyPath);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    try { File.Delete(temporary); }
                    catch (Exception error)
                    {
                        Trace.TraceWarning("Activity energy history temporary cleanup failed for {0}: {1}", temporary, error);
                    }
                }
            }
        }

        private void StartNewSessionLocked()
        {
            if (_document.Sessions.Count >= MaximumSessions)
                throw new InvalidOperationException("Activity energy history contains too many sessions.");
            string id;
            do { id = Guid.NewGuid().ToString("N"); } while (_document.Sessions.ContainsKey(id));
            SessionRecord session = new SessionRecord
            {
                SessionId = id,
                StartedUtc = FormatUtc(NowUtc()),
                Plays = new Dictionary<string, PlayRecord>(StringComparer.Ordinal)
            };
            _document.Sessions.Add(id, session);
            _sessionId = id;
            MarkDirtyLocked();
        }

        private void MarkPlayingRecordsInterruptedLocked()
        {
            foreach (SessionRecord session in _document.Sessions.Values)
            {
                foreach (PlayRecord play in session.Plays.Values)
                {
                    if (play.Status == "playing")
                    {
                        play.Status = "interrupted";
                        play.Outcome = "interrupted";
                        play.EndedUtc = FormatUtc(EndTimeForPlayLocked(play));
                        _dirty = true;
                    }
                }
            }
        }

        private DateTimeOffset EndTimeForPlayLocked(PlayRecord play)
        {
            DateTimeOffset ended = NowUtc();
            DateTimeOffset floor;
            if (TryParseUtc(play.StartedUtc, out floor) && ended < floor) ended = floor;
            if (TryParseUtc(play.LastEndUtc, out floor) && ended < floor) ended = floor;
            return ended;
        }

        private void MarkDirtyLocked()
        {
            _dirty = true;
            if (_changeVersion == Int64.MaxValue) _changeVersion = 1;
            else _changeVersion++;
        }

        private DateTimeOffset NowUtc()
        {
            DateTimeOffset value = _clock();
            if (!IsValidUtc(value.ToUniversalTime())) throw new InvalidDataException("Clock returned an invalid timestamp.");
            return value.ToUniversalTime();
        }

        private string DayKey(DateTimeOffset utc)
        {
            DateTime local = TimeZoneInfo.ConvertTime(utc, _timeZone).DateTime;
            return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private List<DaySlice> SplitByLocalDay(DateTimeOffset start, DateTimeOffset end)
        {
            double totalSeconds = (end - start).TotalSeconds;
            List<DaySlice> slices = new List<DaySlice>();
            DateTimeOffset cursor = start;
            int guard = 0;
            while (cursor < end && guard++ < 10000)
            {
                string day = DayKey(cursor);
                DateTime localDate = TimeZoneInfo.ConvertTime(cursor, _timeZone).Date;
                DateTimeOffset boundary = LocalMidnightUtc(localDate.AddDays(1));
                if (boundary <= cursor || boundary > end) boundary = end;
                double seconds = (boundary - cursor).TotalSeconds;
                if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) return new List<DaySlice>();
                slices.Add(new DaySlice { Day = day, Fraction = seconds / totalSeconds });
                cursor = boundary;
            }
            if (cursor != end) return new List<DaySlice>();
            double sum = slices.Sum(item => item.Fraction);
            if (slices.Count == 0 || double.IsNaN(sum) || double.IsInfinity(sum)) return new List<DaySlice>();
            // Assign any floating-point remainder to the final real-time slice.
            slices[slices.Count - 1].Fraction += 1.0 - sum;
            return slices;
        }

        private DateTimeOffset LocalMidnightUtc(DateTime localMidnight)
        {
            DateTime candidate = DateTime.SpecifyKind(localMidnight, DateTimeKind.Unspecified);
            int attempts = 0;
            while (_timeZone.IsInvalidTime(candidate) && attempts++ < 240) candidate = candidate.AddMinutes(1);
            DateTime utc = TimeZoneInfo.ConvertTimeToUtc(candidate, _timeZone);
            return new DateTimeOffset(utc, TimeSpan.Zero);
        }

        private static EnergyTotals Scale(EnergyTotals value, double fraction)
        {
            return new EnergyTotals
            {
                ActiveKiloJoules = value.ActiveKiloJoules * fraction,
                GrossKiloJoules = value.GrossKiloJoules * fraction,
                EquivalentSeconds = value.EquivalentSeconds * fraction,
                ValidPlayingSeconds = value.ValidPlayingSeconds * fraction
            };
        }

        private static bool CanAdd(EnergyTotals existing, EnergyTotals contribution)
        {
            double active = existing.ActiveKiloJoules + contribution.ActiveKiloJoules;
            double gross = existing.GrossKiloJoules + contribution.GrossKiloJoules;
            double equivalent = existing.EquivalentSeconds + contribution.EquivalentSeconds;
            double playing = existing.ValidPlayingSeconds + contribution.ValidPlayingSeconds;
            return IsFinite(active) && IsFinite(gross) && IsFinite(equivalent) && IsFinite(playing)
                && active >= 0 && gross >= active && equivalent >= 0 && playing >= 0;
        }

        private static void AddTotals(EnergyTotals destination, EnergyTotals contribution)
        {
            destination.ActiveKiloJoules += contribution.ActiveKiloJoules;
            destination.GrossKiloJoules += contribution.GrossKiloJoules;
            destination.EquivalentSeconds += contribution.EquivalentSeconds;
            destination.ValidPlayingSeconds += contribution.ValidPlayingSeconds;
        }

        private static EnergyTotals GetOrCreate(Dictionary<string, EnergyTotals> map, string key)
        {
            EnergyTotals value;
            if (!map.TryGetValue(key, out value))
            {
                value = new EnergyTotals();
                map.Add(key, value);
            }
            return value;
        }

        private static EnergyTotals SumPlayDays(SessionRecord session)
        {
            EnergyTotals result = new EnergyTotals();
            foreach (PlayRecord play in session.Plays.Values)
                if (play.Days != null)
                    foreach (EnergyTotals totals in play.Days.Values) AddTotals(result, totals);
            return result;
        }

        private bool RejectLocked(string message)
        {
            _lastError = message;
            return false;
        }

        private void ThrowIfDisposedLocked()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(EnergyHistoryStore));
        }

        private void SetErrorLocked(string message)
        {
            _lastError = message;
        }

        private void Report(string category, Exception error)
        {
            if (_report == null)
            {
                Trace.TraceError("Activity energy history {0}: {1}", category, error);
                return;
            }
            try { _report(category, error); }
            catch (Exception callbackError)
            {
                Trace.TraceError("Activity energy history report callback failed for {0}: {1}", category, callbackError);
            }
        }

        private static void ValidatePlayArguments(string beatmapKey, string modelVersion,
            double bodyMassKg, double referenceHandSpeedMps, double calibrationFactor)
        {
            if (String.IsNullOrWhiteSpace(beatmapKey) || beatmapKey.Length > 1024)
                throw new ArgumentException("Beatmap key must be non-empty and at most 1024 characters.", nameof(beatmapKey));
            if (String.IsNullOrWhiteSpace(modelVersion) || modelVersion.Length > 128)
                throw new ArgumentException("Model version must be non-empty and at most 128 characters.", nameof(modelVersion));
            if (!IsFinite(bodyMassKg) || bodyMassKg <= 0)
                throw new ArgumentOutOfRangeException(nameof(bodyMassKg));
            if (!IsFinite(referenceHandSpeedMps) || referenceHandSpeedMps <= 0)
                throw new ArgumentOutOfRangeException(nameof(referenceHandSpeedMps));
            if (!IsFinite(calibrationFactor) || calibrationFactor <= 0)
                throw new ArgumentOutOfRangeException(nameof(calibrationFactor));
        }

        private static bool TryValidateTotals(EnergyTotals totals, out string error)
        {
            error = null;
            if (totals == null) { error = "Activity energy interval totals are required."; return false; }
            if (!IsFinite(totals.ActiveKiloJoules) || !IsFinite(totals.GrossKiloJoules)
                || !IsFinite(totals.EquivalentSeconds) || !IsFinite(totals.ValidPlayingSeconds))
            {
                error = "Activity energy interval totals must be finite.";
                return false;
            }
            if (totals.ActiveKiloJoules < 0 || totals.GrossKiloJoules < 0
                || totals.EquivalentSeconds < 0 || totals.ValidPlayingSeconds < 0)
            {
                error = "Activity energy interval totals must be non-negative.";
                return false;
            }
            if (totals.GrossKiloJoules < totals.ActiveKiloJoules)
            {
                error = "Activity energy gross total cannot be below active total.";
                return false;
            }
            return true;
        }

        private static bool IsFinite(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }

        private static bool IsValidUtc(DateTimeOffset value)
        {
            return value >= DateTimeOffset.MinValue.AddDays(1) && value <= DateTimeOffset.MaxValue.AddDays(-1);
        }

        private static string FormatUtc(DateTimeOffset value)
        {
            // Json.NET may reinterpret a +00:00 string as local time while
            // binding string properties.  A literal Z keeps the persisted
            // representation unambiguous across machines and restarts.
            return value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
        }

        private static bool TryParseUtc(string value, out DateTimeOffset parsed)
        {
            string[] formats =
            {
                "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                "yyyy-MM-dd'T'HH:mm:sszzz",
                "o"
            };
            return DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed)
                && IsValidUtc(parsed.ToUniversalTime());
        }

        private static string NormalizeOutcome(string outcome)
        {
            if (String.IsNullOrWhiteSpace(outcome) || outcome.Length > 32) return null;
            string value = outcome.Trim().ToLowerInvariant();
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (!(character >= 'a' && character <= 'z') && !(character >= '0' && character <= '9')
                    && character != '_' && character != '-') return null;
            }
            return value;
        }

        private static bool IsValidId(string value)
        {
            Guid ignored;
            return !String.IsNullOrEmpty(value) && Guid.TryParseExact(value, "N", out ignored)
                && value.Length == 32;
        }

        private static bool IsValidDay(string value)
        {
            DateTime parsed;
            return !String.IsNullOrEmpty(value) && value.Length == 10
                && DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out parsed);
        }

        private static void ValidateDocument(HistoryDocument document)
        {
            if (document == null || document.SchemaVersion != CurrentSchemaVersion
                || document.GameBuild != CurrentGameBuild || document.Sessions == null)
                throw new UnsupportedHistoryException("Unsupported activity energy history schema or game build.");
            if (document.Sessions.Count > MaximumSessions)
                throw new InvalidDataException("Activity energy history contains too many sessions.");
            foreach (KeyValuePair<string, SessionRecord> pair in document.Sessions)
            {
                SessionRecord session = pair.Value;
                if (!IsValidId(pair.Key) || session == null || pair.Key != session.SessionId
                    || !IsValidId(session.SessionId) || !TryParseUtc(session.StartedUtc, out _)
                    || session.Plays == null || session.Plays.Count > MaximumPlaysPerSession)
                    throw new InvalidDataException("Activity energy history contains an invalid session.");
                foreach (KeyValuePair<string, PlayRecord> playPair in session.Plays)
                {
                    PlayRecord play = playPair.Value;
                    if (!IsValidId(playPair.Key) || play == null || playPair.Key != play.PlayId
                        || !IsValidId(play.PlayId) || String.IsNullOrWhiteSpace(play.BeatmapKey)
                        || play.BeatmapKey.Length > 1024 || String.IsNullOrWhiteSpace(play.ModelVersion)
                        || play.ModelVersion.Length > 128 || !IsFinite(play.BodyMassKg) || play.BodyMassKg <= 0
                        || !IsFinite(play.ReferenceHandSpeedMps) || play.ReferenceHandSpeedMps <= 0
                        || !IsFinite(play.CalibrationFactor) || play.CalibrationFactor <= 0
                        || !TryParseUtc(play.StartedUtc, out _) || play.Days == null
                        || play.Days.Count > MaximumDaysPerRecord || play.LastSequence < -1
                        || (play.Status == "playing" && !String.IsNullOrEmpty(play.EndedUtc))
                        || (play.Status == "playing" && !String.IsNullOrEmpty(play.Outcome))
                        || (play.Status != "playing" && NormalizeOutcome(play.Status) == null)
                        || (play.Status != "playing" && String.IsNullOrEmpty(play.EndedUtc))
                        || (play.Status != "playing" && !String.Equals(play.Status, play.Outcome, StringComparison.Ordinal)))
                        throw new InvalidDataException("Activity energy history contains an invalid play.");
                    if (!String.IsNullOrEmpty(play.EndedUtc) && !TryParseUtc(play.EndedUtc, out _))
                        throw new InvalidDataException("Activity energy history contains an invalid play end time.");
                    if (!String.IsNullOrEmpty(play.LastEndUtc) && !TryParseUtc(play.LastEndUtc, out _))
                        throw new InvalidDataException("Activity energy history contains an invalid interval end time.");
                    ValidateTotalsMap(play.Days);
                }
            }
        }

        private static void ValidateTotalsMap(Dictionary<string, EnergyTotals> map)
        {
            foreach (KeyValuePair<string, EnergyTotals> pair in map)
            {
                string error;
                if (!IsValidDay(pair.Key) || !TryValidateTotals(pair.Value, out error))
                    throw new InvalidDataException("Activity energy history contains invalid day totals.");
            }
        }

        private static HistoryDocument NewDocument()
        {
            return new HistoryDocument
            {
                SchemaVersion = CurrentSchemaVersion,
                GameBuild = CurrentGameBuild,
                Sessions = new Dictionary<string, SessionRecord>(StringComparer.Ordinal)
            };
        }

        private static HistoryDocument CloneDocument(HistoryDocument source)
        {
            HistoryDocument result = new HistoryDocument
            {
                SchemaVersion = source.SchemaVersion,
                GameBuild = source.GameBuild,
                Sessions = new Dictionary<string, SessionRecord>(StringComparer.Ordinal)
            };
            foreach (KeyValuePair<string, SessionRecord> sessionPair in source.Sessions)
            {
                SessionRecord sourceSession = sessionPair.Value;
                SessionRecord session = new SessionRecord
                {
                    SessionId = sourceSession.SessionId,
                    StartedUtc = sourceSession.StartedUtc,
                    Plays = new Dictionary<string, PlayRecord>(StringComparer.Ordinal)
                };
                foreach (KeyValuePair<string, PlayRecord> playPair in sourceSession.Plays)
                {
                    PlayRecord sourcePlay = playPair.Value;
                    session.Plays.Add(playPair.Key, new PlayRecord
                    {
                        PlayId = sourcePlay.PlayId,
                        BeatmapKey = sourcePlay.BeatmapKey,
                        ModelVersion = sourcePlay.ModelVersion,
                        BodyMassKg = sourcePlay.BodyMassKg,
                        ReferenceHandSpeedMps = sourcePlay.ReferenceHandSpeedMps,
                        CalibrationFactor = sourcePlay.CalibrationFactor,
                        StartedUtc = sourcePlay.StartedUtc,
                        EndedUtc = sourcePlay.EndedUtc,
                        Status = sourcePlay.Status,
                        Outcome = sourcePlay.Outcome,
                        LastSequence = sourcePlay.LastSequence,
                        LastEndUtc = sourcePlay.LastEndUtc,
                        Days = CloneTotalsMap(sourcePlay.Days)
                    });
                }
                result.Sessions.Add(sessionPair.Key, session);
            }
            return result;
        }

        private static Dictionary<string, EnergyTotals> CloneTotalsMap(Dictionary<string, EnergyTotals> source)
        {
            Dictionary<string, EnergyTotals> result = new Dictionary<string, EnergyTotals>(StringComparer.Ordinal);
            if (source != null)
                foreach (KeyValuePair<string, EnergyTotals> pair in source) result.Add(pair.Key, pair.Value.Copy());
            return result;
        }
    }
}
