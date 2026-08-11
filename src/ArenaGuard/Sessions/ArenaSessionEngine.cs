using System;
using System.Collections.Generic;
using System.Linq;
using ArenaGuard.Domain;

namespace ArenaGuard.Sessions
{
    public sealed class ArenaSessionOptions
    {
        public TimeSpan QueueAcceptanceTimeout = TimeSpan.FromSeconds(30);
        public TimeSpan StagingTimeout = TimeSpan.FromSeconds(60);
        public TimeSpan BoundaryGracePeriod = TimeSpan.FromSeconds(5);
        public TimeSpan VictoryDisplayDuration = TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Deterministic arena queue/session state. The engine only emits ArenaEffect
    /// commands; applying those commands to game objects is the runtime's job.
    /// </summary>
    public sealed class ArenaSessionEngine
    {
        private readonly Func<ChallengeRequest, ChallengePlan> _planFactory;
        private readonly Func<DateTime> _utcNow;
        private readonly ArenaSessionOptions _options;
        private readonly List<QueueEntry> _queue = new List<QueueEntry>();
        private readonly Dictionary<string, ArenaSession> _activeByArena =
            new Dictionary<string, ArenaSession>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ArenaSession> _sessionsById =
            new Dictionary<string, ArenaSession>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<long, DateTime> _boundaryDeadlines = new Dictionary<long, DateTime>();
        private readonly Dictionary<string, ChallengePlan> _recoveredPlansByRequestId =
            new Dictionary<string, ChallengePlan>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _originGateBySessionId =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<ArenaEffect> _effects = new List<ArenaEffect>();
        private int _nextQueueSequence = 1;

        public ArenaSessionEngine(
            Func<ChallengeRequest, ChallengePlan> planFactory,
            Func<DateTime> utcNow = null,
            ArenaSessionOptions options = null)
        {
            _planFactory = planFactory ?? throw new ArgumentNullException(nameof(planFactory));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _options = options ?? new ArenaSessionOptions();
            ValidateOptions(_options);
        }

        public void Enqueue(QueueEntry entry)
        {
            ValidateQueueEntry(entry);
            if (ContainsPlayer(entry.Request.PlayerId))
            {
                throw new InvalidOperationException("A player can only occupy one arena queue or session at a time.");
            }

            if (ContainsRequest(entry.Request.RequestId))
            {
                throw new InvalidOperationException("That challenge request is already queued or active.");
            }

            var queued = CloneQueueEntry(entry);
            queued.QueueSequence = _nextQueueSequence++;
            queued.EnqueuedUtc = Now();
            _queue.Add(queued);

            Emit(ArenaEffectType.SetArenaRole, queued.Request.ArenaId, null, queued.Request.PlayerId, null, ArenaRole.Queued.ToString());
            Emit(ArenaEffectType.PersistState, queued.Request.ArenaId, null, queued.Request.PlayerId, null, "Queue joined.");
            TryCallNext(queued.Request.ArenaId, Now());
        }

        public bool AcceptTurn(long playerId)
        {
            var session = FindActiveByPlayer(playerId);
            if (session == null || session.Phase != SessionPhase.Called)
            {
                return false;
            }

            var now = Now();
            if (now >= session.PhaseDeadlineUtc)
            {
                HandleCallTimeout(session, now);
                return false;
            }

            session.Phase = SessionPhase.Staging;
            session.PhaseStartedUtc = now;
            session.PhaseDeadlineUtc = now.Add(_options.StagingTimeout);
            Emit(ArenaEffectType.SetArenaRole, session, null, ArenaRole.Combatant.ToString());
            Emit(ArenaEffectType.PersistState, session, null, "Queue turn accepted.");
            Emit(ArenaEffectType.MoveToStaging, session, null, "Move called combatant to staging.");
            Emit(ArenaEffectType.SendLocalMessage, session, null,
                "Preparing your combat teleport. The arena will start when your loadout is secured.");
            return true;
        }

        public bool EnterCombatFloor(long playerId)
        {
            return EnterCombatFloor(playerId, null);
        }

        public bool EnterCombatFloor(long playerId, PlayerResourceSnapshot resourceSnapshot)
        {
            var session = FindActiveByPlayer(playerId);
            if (session == null || session.Phase != SessionPhase.Staging)
            {
                return false;
            }

            var now = Now();
            if (now >= session.PhaseDeadlineUtc)
            {
                HandleStagingTimeout(session, now);
                return false;
            }

            if (resourceSnapshot != null && resourceSnapshot.PlayerId != playerId)
            {
                throw new ArgumentException("The resource snapshot belongs to a different player.", nameof(resourceSnapshot));
            }

            session.ResourceSnapshot = CloneResourceSnapshot(resourceSnapshot);
            session.EncounterIndex = 0;
            session.LivingArenaEnemyCount = 0;
            session.SpawnedEnemyIds.Clear();
            session.Outcome = SessionOutcome.None;
            session.SessionStartedUtc = now;
            StartCountdown(session, SessionPhase.Countdown, now);
            _boundaryDeadlines.Remove(playerId);
            _recoveredPlansByRequestId.Remove(session.Request.RequestId);

            Emit(ArenaEffectType.PersistState, session, CurrentEncounter(session), "Challenge started; persist the snapshot before spawning.");
            Emit(ArenaEffectType.StartCountdown, session, CurrentEncounter(session), CountdownMessage(session));
            EmitAnnouncement(session, "started " + DescribeMode(session.Plan.Mode) + ".");
            return true;
        }

        public bool SetResourceSnapshot(long playerId, PlayerResourceSnapshot resourceSnapshot)
        {
            if (resourceSnapshot == null)
            {
                throw new ArgumentNullException(nameof(resourceSnapshot));
            }

            if (resourceSnapshot.PlayerId != playerId)
            {
                throw new ArgumentException("The resource snapshot belongs to a different player.", nameof(resourceSnapshot));
            }

            var session = FindActiveByPlayer(playerId);
            if (session == null || session.Phase != SessionPhase.Staging || session.ResourceSnapshot != null)
            {
                return false;
            }

            session.ResourceSnapshot = CloneResourceSnapshot(resourceSnapshot);
            Emit(ArenaEffectType.PersistState, session, CurrentEncounterOrNull(session), "Player resource snapshot captured.");
            return true;
        }

        public void Tick(DateTime utcNow)
        {
            if (utcNow.Kind == DateTimeKind.Local)
            {
                utcNow = utcNow.ToUniversalTime();
            }

            var sessions = _activeByArena.Values.ToList();
            foreach (var session in sessions)
            {
                UpdateElapsed(session, utcNow);

                DateTime boundaryDeadline;
                if (_boundaryDeadlines.TryGetValue(session.Request.PlayerId, out boundaryDeadline) && utcNow >= boundaryDeadline)
                {
                    EndRun(session, SessionOutcome.BoundaryForfeit, SessionPhase.Forfeit, utcNow,
                        "left the combat boundary and forfeited.", true);
                    continue;
                }

                if (session.PhaseDeadlineUtc == DateTime.MinValue || utcNow < session.PhaseDeadlineUtc)
                {
                    continue;
                }

                switch (session.Phase)
                {
                    case SessionPhase.Called:
                        HandleCallTimeout(session, utcNow);
                        break;
                    case SessionPhase.Staging:
                        HandleStagingTimeout(session, utcNow);
                        break;
                    case SessionPhase.Countdown:
                    case SessionPhase.Intermission:
                        BeginEncounter(session, utcNow);
                        break;
                    case SessionPhase.Victory:
                        FinalizeTerminalSession(session, utcNow, true);
                        break;
                }
            }
        }

        public void Tick()
        {
            Tick(Now());
        }

        public bool ReportEncounterCleared(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return false;
            }

            ArenaSession session;
            if (!_sessionsById.TryGetValue(sessionId, out session) ||
                session.Phase != SessionPhase.Fighting ||
                !IsActive(session))
            {
                return false;
            }

            var now = Now();
            session.LivingArenaEnemyCount = 0;
            session.SpawnedEnemyIds.Clear();
            UpdateElapsed(session, now);

            if (session.EncounterIndex >= session.Plan.Encounters.Count - 1)
            {
                session.Outcome = SessionOutcome.Victory;
                session.Phase = SessionPhase.Victory;
                session.PhaseStartedUtc = now;
                session.PhaseDeadlineUtc = now.Add(_options.VictoryDisplayDuration);
                _boundaryDeadlines.Remove(session.Request.PlayerId);
                Emit(ArenaEffectType.UpdateLeaderboard, session, CurrentEncounter(session), "Record completed challenge.");
                Emit(ArenaEffectType.PersistState, session, CurrentEncounter(session), "Challenge won.");
                EmitAnnouncement(session, "completed " + DescribeMode(session.Plan.Mode) + ".");
                return true;
            }

            session.EncounterIndex++;
            StartCountdown(session, SessionPhase.Intermission, now);
            Emit(ArenaEffectType.PersistState, session, CurrentEncounter(session), "Encounter cleared.");
            Emit(ArenaEffectType.StartCountdown, session, CurrentEncounter(session), CountdownMessage(session));
            return true;
        }

        public bool ReportBoundaryState(long playerId, bool isOutside)
        {
            var session = FindActiveByPlayer(playerId);
            if (session == null || !IsChallengeInProgress(session))
            {
                _boundaryDeadlines.Remove(playerId);
                return false;
            }

            if (!isOutside)
            {
                if (_boundaryDeadlines.Remove(playerId))
                {
                    Emit(ArenaEffectType.SendLocalMessage, session, CurrentEncounter(session), "Boundary warning cleared.");
                }

                return true;
            }

            if (!_boundaryDeadlines.ContainsKey(playerId))
            {
                _boundaryDeadlines[playerId] = Now().Add(_options.BoundaryGracePeriod);
                Emit(ArenaEffectType.SendLocalMessage, session, CurrentEncounter(session),
                    "Return to the combat floor within " + (int)_options.BoundaryGracePeriod.TotalSeconds + " seconds or forfeit.");
            }

            return true;
        }

        public bool ReportLethalDamage(long playerId)
        {
            var session = FindActiveByPlayer(playerId);
            if (session == null || !IsChallengeInProgress(session))
            {
                return false;
            }

            EndRun(session, SessionOutcome.Defeat, SessionPhase.Defeat, Now(),
                "was defeated in the arena.", true);
            return true;
        }

        public bool Forfeit(long playerId)
        {
            var session = FindActiveByPlayer(playerId);
            if (session == null ||
                (session.Phase != SessionPhase.Staging && !IsChallengeInProgress(session)))
            {
                return false;
            }

            var challengeStarted = session.SessionStartedUtc != DateTime.MinValue;
            EndRun(session, SessionOutcome.Forfeit, SessionPhase.Forfeit, Now(),
                "forfeited the arena challenge.", challengeStarted);
            return true;
        }

        public int ClearQueue(string arenaId)
        {
            if (string.IsNullOrWhiteSpace(arenaId))
            {
                return 0;
            }

            List<QueueEntry> removed = _queue.Where(entry =>
                    string.Equals(entry.Request.ArenaId, arenaId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (removed.Count == 0)
            {
                return 0;
            }

            _queue.RemoveAll(entry =>
                string.Equals(entry.Request.ArenaId, arenaId, StringComparison.OrdinalIgnoreCase));
            foreach (QueueEntry entry in removed)
            {
                Emit(ArenaEffectType.SetArenaRole, arenaId, null, entry.Request.PlayerId, null,
                    ArenaRole.Spectator.ToString());
            }
            Emit(ArenaEffectType.PersistState, arenaId, null, 0L, null,
                "Administrator cleared the arena queue.");
            return removed.Count;
        }

        public bool AbortArena(string arenaId)
        {
            if (string.IsNullOrWhiteSpace(arenaId) ||
                !_activeByArena.TryGetValue(arenaId, out ArenaSession session))
            {
                return false;
            }

            DateTime now = Now();
            UpdateElapsed(session, now);
            session.Outcome = SessionOutcome.Forfeit;
            session.Phase = SessionPhase.Forfeit;
            session.PhaseStartedUtc = now;
            session.PhaseDeadlineUtc = DateTime.MinValue;
            _boundaryDeadlines.Remove(session.Request.PlayerId);
            EmitAnnouncement(session, "had the arena challenge stopped by an administrator.");
            FinalizeTerminalSession(session, now, false);
            return true;
        }

        public bool Disconnect(long playerId)
        {
            var removed = _queue.RemoveAll(entry => entry.Request.PlayerId == playerId) > 0;
            if (removed)
            {
                Emit(ArenaEffectType.SetArenaRole, null, null, playerId, null, ArenaRole.Spectator.ToString());
                Emit(ArenaEffectType.PersistState, null, null, playerId, null, "Disconnected player removed from queue.");
            }

            var session = FindActiveByPlayer(playerId);
            if (session == null)
            {
                return removed;
            }

            var challengeStarted = session.SessionStartedUtc != DateTime.MinValue;
            EndRun(session, SessionOutcome.Disconnect, SessionPhase.Recovering, Now(),
                "disconnected; the challenge was cancelled.", challengeStarted);
            return true;
        }

        public void RecoverAfterRestart(PersistedWorldState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            _queue.Clear();
            _activeByArena.Clear();
            _sessionsById.Clear();
            _boundaryDeadlines.Clear();
            _recoveredPlansByRequestId.Clear();
            _originGateBySessionId.Clear();
            _effects.Clear();
            _nextQueueSequence = 1;

            var seenPlayers = new HashSet<long>();
            var seenRequests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var arenas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var interrupted in state.InterruptedSessions ?? new List<ArenaSession>())
            {
                if (!IsRecoverable(interrupted) ||
                    !seenPlayers.Add(interrupted.Request.PlayerId) ||
                    !seenRequests.Add(interrupted.Request.RequestId))
                {
                    continue;
                }

                var oldSession = CloneSession(interrupted);
                oldSession.Outcome = SessionOutcome.ServerRestart;
                oldSession.Phase = SessionPhase.Recovering;
                oldSession.PhaseDeadlineUtc = DateTime.MinValue;
                _sessionsById[oldSession.SessionId] = oldSession;

                Emit(ArenaEffectType.DespawnSessionEnemies, oldSession, CurrentEncounterOrNull(oldSession),
                    "Remove enemies left by the interrupted session.");
                if (oldSession.ResourceSnapshot != null)
                {
                    Emit(ArenaEffectType.RestorePlayerState, oldSession, CurrentEncounterOrNull(oldSession),
                        "Restore the pre-restart resource snapshot.");
                }
                Emit(ArenaEffectType.MoveToStaging, oldSession, CurrentEncounterOrNull(oldSession),
                    "Return the interrupted combatant to staging.");
                Emit(ArenaEffectType.SetArenaRole, oldSession, null, ArenaRole.Queued.ToString());
                Emit(ArenaEffectType.SendLocalMessage, oldSession, null,
                    "The server restarted. Your challenge will restart from the beginning.");

                _recoveredPlansByRequestId[oldSession.Request.RequestId] = ClonePlan(oldSession.Plan);
                _queue.Add(new QueueEntry
                {
                    Request = CloneRequest(oldSession.Request),
                    OriginGateId = string.Empty,
                    QueueSequence = _nextQueueSequence++,
                    EnqueuedUtc = Now()
                });
                arenas.Add(oldSession.ArenaId);
            }

            foreach (var entry in (state.Queue ?? new List<QueueEntry>())
                .OrderBy(item => item.QueueSequence)
                .ThenBy(item => item.EnqueuedUtc))
            {
                try
                {
                    ValidateQueueEntry(entry);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (!seenPlayers.Add(entry.Request.PlayerId) || !seenRequests.Add(entry.Request.RequestId))
                {
                    continue;
                }

                var queued = CloneQueueEntry(entry);
                queued.QueueSequence = _nextQueueSequence++;
                _queue.Add(queued);
                arenas.Add(queued.Request.ArenaId);
            }

            Emit(ArenaEffectType.PersistState, null, null, 0, null, "Restart recovery queue rebuilt.");
            foreach (var arenaId in arenas)
            {
                TryCallNext(arenaId, Now());
            }
        }

        public void ApplyEffectResult(ArenaEffect effect, bool succeeded)
        {
            if (effect == null)
            {
                throw new ArgumentNullException(nameof(effect));
            }

            if (succeeded)
            {
                if (effect.Type == ArenaEffectType.SpawnEncounter)
                {
                    ArenaSession spawnedSession;
                    if (_sessionsById.TryGetValue(effect.SessionId ?? string.Empty, out spawnedSession) &&
                        spawnedSession.Phase == SessionPhase.Fighting &&
                        effect.Encounter != null)
                    {
                        spawnedSession.LivingArenaEnemyCount = effect.Encounter.Quantity;
                    }
                }

                return;
            }

            ArenaSession session;
            if (string.IsNullOrEmpty(effect.SessionId) || !_sessionsById.TryGetValue(effect.SessionId, out session))
            {
                return;
            }

            if (effect.Type == ArenaEffectType.SpawnEncounter && IsActive(session))
            {
                EndRun(session, SessionOutcome.RuntimeError, SessionPhase.Defeat, Now(),
                    "could not start an arena encounter because spawning failed.", true);
            }
            else if (effect.Type == ArenaEffectType.RestorePlayerState)
            {
                session.Phase = SessionPhase.Recovering;
                Emit(ArenaEffectType.SendLocalMessage, session, null,
                    "ArenaGuard could not restore player state automatically; an administrator must retry recovery.");
                Emit(ArenaEffectType.PersistState, session, null, "Player restoration requires recovery.");
            }
        }

        public IList<ArenaEffect> DrainEffects()
        {
            var result = _effects.Select(CloneEffect).ToList();
            _effects.Clear();
            return result;
        }

        public IList<QueueEntry> GetQueueSnapshot()
        {
            return _queue.Select(CloneQueueEntry).ToList();
        }

        public IList<ArenaSession> GetActiveSessions()
        {
            return _activeByArena.Values.Select(CloneSession).ToList();
        }

        public bool TryGetActiveSession(string arenaId, out ArenaSession session)
        {
            ArenaSession active;
            if (!string.IsNullOrWhiteSpace(arenaId) && _activeByArena.TryGetValue(arenaId, out active))
            {
                session = CloneSession(active);
                return true;
            }

            session = null;
            return false;
        }

        public bool TryGetSession(string sessionId, out ArenaSession session)
        {
            ArenaSession found;
            if (!string.IsNullOrWhiteSpace(sessionId) && _sessionsById.TryGetValue(sessionId, out found))
            {
                session = CloneSession(found);
                return true;
            }

            session = null;
            return false;
        }

        private void TryCallNext(string arenaId, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(arenaId) || _activeByArena.ContainsKey(arenaId))
            {
                return;
            }

            while (true)
            {
                var queueIndex = _queue.FindIndex(entry =>
                    string.Equals(entry.Request.ArenaId, arenaId, StringComparison.OrdinalIgnoreCase));
                if (queueIndex < 0)
                {
                    return;
                }

                var entry = _queue[queueIndex];
                _queue.RemoveAt(queueIndex);

                ChallengePlan plan;
                try
                {
                    if (!_recoveredPlansByRequestId.TryGetValue(entry.Request.RequestId, out plan))
                    {
                        plan = _planFactory(CloneRequest(entry.Request));
                    }

                    ValidatePlan(plan, entry.Request);
                    plan = ClonePlan(plan);
                }
                catch (Exception exception)
                {
                    Emit(ArenaEffectType.SetArenaRole, arenaId, null, entry.Request.PlayerId, null, ArenaRole.Spectator.ToString());
                    Emit(ArenaEffectType.SendLocalMessage, arenaId, null, entry.Request.PlayerId, null,
                        "ArenaGuard rejected the challenge plan: " + exception.Message);
                    Emit(ArenaEffectType.PersistState, arenaId, null, entry.Request.PlayerId, null, "Invalid challenge request removed.");
                    continue;
                }

                var session = new ArenaSession
                {
                    SessionId = NewId(),
                    ArenaId = entry.Request.ArenaId,
                    Request = CloneRequest(entry.Request),
                    Plan = plan,
                    Phase = SessionPhase.Called,
                    Outcome = SessionOutcome.None,
                    EncounterIndex = 0,
                    LivingArenaEnemyCount = 0,
                    SpawnedEnemyIds = new List<string>(),
                    ResourceSnapshot = null,
                    PhaseStartedUtc = now,
                    PhaseDeadlineUtc = now.Add(_options.QueueAcceptanceTimeout),
                    SessionStartedUtc = DateTime.MinValue,
                    ElapsedMilliseconds = 0
                };

                _activeByArena[arenaId] = session;
                _sessionsById[session.SessionId] = session;
                _originGateBySessionId[session.SessionId] = entry.OriginGateId ?? string.Empty;
                Emit(ArenaEffectType.CallQueuedPlayer, session, null,
                    "Your arena turn is ready. Accept within " + (int)_options.QueueAcceptanceTimeout.TotalSeconds + " seconds.");
                Emit(ArenaEffectType.PersistState, session, null, "Queued player called.");
                return;
            }
        }

        private void HandleCallTimeout(ArenaSession session, DateTime now)
        {
            if (!IsActive(session) || session.Phase != SessionPhase.Called)
            {
                return;
            }

            session.Outcome = SessionOutcome.StartTimeout;
            session.Phase = SessionPhase.Closed;
            session.PhaseStartedUtc = now;
            session.PhaseDeadlineUtc = DateTime.MinValue;
            _activeByArena.Remove(session.ArenaId);

            string originGateId;
            _originGateBySessionId.TryGetValue(session.SessionId, out originGateId);
            _originGateBySessionId.Remove(session.SessionId);
            _queue.Add(new QueueEntry
            {
                Request = CloneRequest(session.Request),
                OriginGateId = originGateId ?? string.Empty,
                QueueSequence = _nextQueueSequence++,
                EnqueuedUtc = now
            });
            Emit(ArenaEffectType.SetArenaRole, session, null, ArenaRole.Queued.ToString());
            Emit(ArenaEffectType.SendLocalMessage, session, null, "Queue acceptance expired; you were moved to the back of the queue.");
            Emit(ArenaEffectType.PersistState, session, null, "Queue acceptance timed out.");
            TryCallNext(session.ArenaId, now);
        }

        private void HandleStagingTimeout(ArenaSession session, DateTime now)
        {
            if (!IsActive(session) || session.Phase != SessionPhase.Staging)
            {
                return;
            }

            session.Outcome = SessionOutcome.StartTimeout;
            session.Phase = SessionPhase.Closed;
            session.PhaseStartedUtc = now;
            session.PhaseDeadlineUtc = DateTime.MinValue;
            _activeByArena.Remove(session.ArenaId);
            _originGateBySessionId.Remove(session.SessionId);
            _recoveredPlansByRequestId.Remove(session.Request.RequestId);
            if (session.ResourceSnapshot != null)
            {
                Emit(ArenaEffectType.RestorePlayerState, session, null,
                    "Restore the timed-out arena food loadout.");
            }
            Emit(ArenaEffectType.PersistState, session, null, "Staging start timed out.");
            Emit(ArenaEffectType.MoveToSpectatorArea, session, null, "Return timed-out player to spectators.");
            Emit(ArenaEffectType.SetArenaRole, session, null, ArenaRole.Spectator.ToString());
            Emit(ArenaEffectType.SendLocalMessage, session, null, "Challenge start timed out.");
            TryCallNext(session.ArenaId, now);
        }

        private void BeginEncounter(ArenaSession session, DateTime now)
        {
            if (!IsActive(session) ||
                (session.Phase != SessionPhase.Countdown && session.Phase != SessionPhase.Intermission))
            {
                return;
            }

            session.Phase = SessionPhase.Fighting;
            session.PhaseStartedUtc = now;
            session.PhaseDeadlineUtc = DateTime.MinValue;
            session.LivingArenaEnemyCount = CurrentEncounter(session).Quantity;
            Emit(ArenaEffectType.PersistState, session, CurrentEncounter(session), "Encounter ready to spawn.");
            Emit(ArenaEffectType.SpawnEncounter, session, CurrentEncounter(session), "Spawn current encounter.");
        }

        private void StartCountdown(ArenaSession session, SessionPhase phase, DateTime now)
        {
            session.Phase = phase;
            session.PhaseStartedUtc = now;
            session.PhaseDeadlineUtc = now.AddSeconds(CurrentEncounter(session).PreparationSeconds);
        }

        private void EndRun(
            ArenaSession session,
            SessionOutcome outcome,
            SessionPhase terminalPhase,
            DateTime now,
            string announcement,
            bool recordResult)
        {
            if (!IsActive(session))
            {
                return;
            }

            UpdateElapsed(session, now);
            session.Outcome = outcome;
            session.Phase = terminalPhase;
            session.PhaseStartedUtc = now;
            session.PhaseDeadlineUtc = DateTime.MinValue;
            _boundaryDeadlines.Remove(session.Request.PlayerId);

            if (recordResult)
            {
                Emit(ArenaEffectType.UpdateLeaderboard, session, CurrentEncounterOrNull(session), "Record challenge result.");
            }

            EmitAnnouncement(session, announcement);
            FinalizeTerminalSession(session, now, false);
        }

        private void FinalizeTerminalSession(ArenaSession session, DateTime now, bool wasVictory)
        {
            if (!IsActive(session))
            {
                return;
            }

            UpdateElapsed(session, now);
            session.PhaseDeadlineUtc = DateTime.MinValue;
            _activeByArena.Remove(session.ArenaId);
            _boundaryDeadlines.Remove(session.Request.PlayerId);
            _originGateBySessionId.Remove(session.SessionId);
            _recoveredPlansByRequestId.Remove(session.Request.RequestId);
            Emit(ArenaEffectType.DespawnSessionEnemies, session, CurrentEncounterOrNull(session), "Remove all arena enemies.");
            if (session.ResourceSnapshot != null)
            {
                Emit(ArenaEffectType.RestorePlayerState, session, CurrentEncounterOrNull(session),
                    "Restore food, ammunition, and allowed consumables.");
            }
            Emit(ArenaEffectType.MoveToStaging, session, CurrentEncounterOrNull(session),
                wasVictory ? "Return victor to staging after results." : "Return combatant to staging.");
            Emit(ArenaEffectType.SetArenaRole, session, null, ArenaRole.Spectator.ToString());
            Emit(ArenaEffectType.PersistState, session, CurrentEncounterOrNull(session), "Finalize terminal session after restoration is durable.");
            TryCallNext(session.ArenaId, now);
        }

        private void EmitAnnouncement(ArenaSession session, string action)
        {
            var name = string.IsNullOrWhiteSpace(session.Request.PlayerName)
                ? "A combatant"
                : session.Request.PlayerName.Trim();
            var message = name + " " + action;
            var type = session.Plan.Mode == ChallengeMode.CustomEncounter
                ? ArenaEffectType.SendLocalMessage
                : ArenaEffectType.SendGlobalMessage;
            Emit(type, session, CurrentEncounterOrNull(session), message);
        }

        private void Emit(
            ArenaEffectType type,
            ArenaSession session,
            EncounterDefinition encounter,
            string message)
        {
            Emit(type, session.ArenaId, session.SessionId, session.Request.PlayerId, encounter, message);
        }

        private void Emit(
            ArenaEffectType type,
            string arenaId,
            string sessionId,
            long playerId,
            EncounterDefinition encounter,
            string message)
        {
            _effects.Add(new ArenaEffect
            {
                Type = type,
                ArenaId = arenaId,
                SessionId = sessionId,
                PlayerId = playerId,
                Encounter = CloneEncounter(encounter),
                Message = message
            });
        }

        private ArenaSession FindActiveByPlayer(long playerId)
        {
            return _activeByArena.Values.FirstOrDefault(session => session.Request.PlayerId == playerId);
        }

        private bool IsActive(ArenaSession session)
        {
            ArenaSession active;
            return session != null &&
                _activeByArena.TryGetValue(session.ArenaId, out active) &&
                ReferenceEquals(active, session);
        }

        private bool ContainsPlayer(long playerId)
        {
            return _queue.Any(entry => entry.Request.PlayerId == playerId) || FindActiveByPlayer(playerId) != null;
        }

        private bool ContainsRequest(string requestId)
        {
            return _queue.Any(entry => string.Equals(entry.Request.RequestId, requestId, StringComparison.OrdinalIgnoreCase)) ||
                _activeByArena.Values.Any(session =>
                    string.Equals(session.Request.RequestId, requestId, StringComparison.OrdinalIgnoreCase));
        }

        private DateTime Now()
        {
            var now = _utcNow();
            return now.Kind == DateTimeKind.Local ? now.ToUniversalTime() : now;
        }

        private static bool IsChallengeInProgress(ArenaSession session)
        {
            return session.Phase == SessionPhase.Countdown ||
                session.Phase == SessionPhase.Fighting ||
                session.Phase == SessionPhase.Intermission;
        }

        private static bool IsRecoverable(ArenaSession session)
        {
            return session != null &&
                !string.IsNullOrWhiteSpace(session.SessionId) &&
                !string.IsNullOrWhiteSpace(session.ArenaId) &&
                session.Request != null &&
                !string.IsNullOrWhiteSpace(session.Request.RequestId) &&
                session.Request.PlayerId != 0 &&
                session.Plan != null &&
                session.Plan.Encounters != null &&
                session.Plan.Encounters.Count > 0 &&
                (session.ResourceSnapshot != null ||
                    session.Phase == SessionPhase.Called ||
                    session.Phase == SessionPhase.Staging);
        }

        private static void ValidateQueueEntry(QueueEntry entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            if (entry.Request == null)
            {
                throw new ArgumentException("Queue entry requires a challenge request.", nameof(entry));
            }

            if (string.IsNullOrWhiteSpace(entry.Request.RequestId) ||
                string.IsNullOrWhiteSpace(entry.Request.ArenaId) ||
                entry.Request.PlayerId == 0)
            {
                throw new ArgumentException("Queue request requires request ID, arena ID, and player ID.", nameof(entry));
            }
        }

        private static void ValidatePlan(ChallengePlan plan, ChallengeRequest request)
        {
            if (plan == null || string.IsNullOrWhiteSpace(plan.PlanId) ||
                plan.Encounters == null || plan.Encounters.Count == 0)
            {
                throw new InvalidOperationException("Challenge planning returned an empty or malformed plan.");
            }

            if (plan.Mode != request.Mode || plan.CapMode != request.CapMode ||
                request.CapMode == ProgressionCapMode.Biome && plan.HighestBiome != request.SelectedBiome)
            {
                throw new InvalidOperationException("Challenge plan does not match its request.");
            }

            for (var index = 0; index < plan.Encounters.Count; index++)
            {
                var encounter = plan.Encounters[index];
                if (encounter == null || encounter.Sequence != index ||
                    string.IsNullOrWhiteSpace(encounter.CreatureKey) || encounter.Quantity < 1 ||
                    encounter.PreparationSeconds < 0)
                {
                    throw new InvalidOperationException("Challenge plan contains a malformed encounter.");
                }
            }
        }

        private static void ValidateOptions(ArenaSessionOptions options)
        {
            if (options.QueueAcceptanceTimeout <= TimeSpan.Zero ||
                options.StagingTimeout <= TimeSpan.Zero ||
                options.BoundaryGracePeriod <= TimeSpan.Zero ||
                options.VictoryDisplayDuration < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Session timeouts must be positive (victory display may be zero).");
            }
        }

        private static EncounterDefinition CurrentEncounter(ArenaSession session)
        {
            return session.Plan.Encounters[session.EncounterIndex];
        }

        private static EncounterDefinition CurrentEncounterOrNull(ArenaSession session)
        {
            if (session == null || session.Plan == null || session.Plan.Encounters == null ||
                session.EncounterIndex < 0 || session.EncounterIndex >= session.Plan.Encounters.Count)
            {
                return null;
            }

            return CurrentEncounter(session);
        }

        private static void UpdateElapsed(ArenaSession session, DateTime now)
        {
            if (session.SessionStartedUtc == DateTime.MinValue || session.Phase != SessionPhase.Fighting)
            {
                return;
            }

            if (session.PhaseStartedUtc == DateTime.MinValue || now <= session.PhaseStartedUtc)
            {
                session.PhaseStartedUtc = now;
                return;
            }

            long fightingMilliseconds = (long)(now - session.PhaseStartedUtc).TotalMilliseconds;
            session.ElapsedMilliseconds = Math.Max(0L, session.ElapsedMilliseconds + fightingMilliseconds);
            // ElapsedMilliseconds is the persisted accumulator. Advancing the anchor prevents
            // fixed-update calls from counting the same fighting interval more than once.
            session.PhaseStartedUtc = now;
        }

        private static string CountdownMessage(ArenaSession session)
        {
            return "Encounter " + (session.EncounterIndex + 1) + " of " + session.Plan.Encounters.Count +
                " begins in " + CurrentEncounter(session).PreparationSeconds + " seconds.";
        }

        private static string DescribeMode(ChallengeMode mode)
        {
            switch (mode)
            {
                case ChallengeMode.BiomeLadder:
                    return "the biome ladder";
                case ChallengeMode.StarLadder:
                    return "the star ladder";
                case ChallengeMode.CustomEncounter:
                    return "a custom encounter";
                default:
                    return "an arena challenge";
            }
        }

        private static string NewId()
        {
            return Guid.NewGuid().ToString("N").ToLowerInvariant();
        }

        private static ArenaEffect CloneEffect(ArenaEffect effect)
        {
            return new ArenaEffect
            {
                Type = effect.Type,
                ArenaId = effect.ArenaId,
                SessionId = effect.SessionId,
                PlayerId = effect.PlayerId,
                Encounter = CloneEncounter(effect.Encounter),
                Message = effect.Message
            };
        }

        private static QueueEntry CloneQueueEntry(QueueEntry entry)
        {
            return new QueueEntry
            {
                Request = CloneRequest(entry.Request),
                OriginGateId = entry.OriginGateId,
                QueueSequence = entry.QueueSequence,
                EnqueuedUtc = entry.EnqueuedUtc
            };
        }

        private static ChallengeRequest CloneRequest(ChallengeRequest request)
        {
            if (request == null)
            {
                return null;
            }

            return new ChallengeRequest
            {
                RequestId = request.RequestId,
                ArenaId = request.ArenaId,
                PlayerId = request.PlayerId,
                PlayerName = request.PlayerName,
                Mode = request.Mode,
                CapMode = request.CapMode,
                SelectedBiome = request.SelectedBiome,
                CustomSelection = request.CustomSelection == null
                    ? null
                    : new CustomEncounterSelection
                    {
                        CreatureKey = request.CustomSelection.CreatureKey,
                        Stars = request.CustomSelection.Stars,
                        Quantity = request.CustomSelection.Quantity
                    },
                RequestedUtc = request.RequestedUtc
            };
        }

        private static ChallengePlan ClonePlan(ChallengePlan plan)
        {
            if (plan == null)
            {
                return null;
            }

            return new ChallengePlan
            {
                PlanId = plan.PlanId,
                Mode = plan.Mode,
                CapMode = plan.CapMode,
                HighestBiome = plan.HighestBiome,
                RosterRevision = plan.RosterRevision,
                Encounters = plan.Encounters == null
                    ? null
                    : plan.Encounters.Select(CloneEncounter).ToList()
            };
        }

        private static EncounterDefinition CloneEncounter(EncounterDefinition encounter)
        {
            if (encounter == null)
            {
                return null;
            }

            return new EncounterDefinition
            {
                Sequence = encounter.Sequence,
                CreatureKey = encounter.CreatureKey,
                Biome = encounter.Biome,
                Stars = encounter.Stars,
                Quantity = encounter.Quantity,
                IsMiniboss = encounter.IsMiniboss,
                PreparationSeconds = encounter.PreparationSeconds
            };
        }

        private static PlayerResourceSnapshot CloneResourceSnapshot(PlayerResourceSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return null;
            }

            return new PlayerResourceSnapshot
            {
                PlayerId = snapshot.PlayerId,
                Foods = snapshot.Foods == null
                    ? null
                    : snapshot.Foods.Select(food => new FoodStateSnapshot
                    {
                        ItemPrefabName = food.ItemPrefabName,
                        RemainingSeconds = food.RemainingSeconds,
                        Health = food.Health,
                        Stamina = food.Stamina,
                        Eitr = food.Eitr
                    }).ToList(),
                RestedRemainingSeconds = snapshot.RestedRemainingSeconds,
                ArenaFoodPrefabNames = snapshot.ArenaFoodPrefabNames == null
                    ? new List<string>()
                    : new List<string>(snapshot.ArenaFoodPrefabNames),
                AllowedConsumableCounts = snapshot.AllowedConsumableCounts == null
                    ? null
                    : new Dictionary<string, int>(snapshot.AllowedConsumableCounts, StringComparer.Ordinal),
                AmmunitionCounts = snapshot.AmmunitionCounts == null
                    ? null
                    : new Dictionary<string, int>(snapshot.AmmunitionCounts, StringComparer.Ordinal),
                EquipmentDurabilityBySlot = snapshot.EquipmentDurabilityBySlot == null
                    ? null
                    : new Dictionary<string, float>(snapshot.EquipmentDurabilityBySlot, StringComparer.Ordinal),
                CapturedUtc = snapshot.CapturedUtc
            };
        }

        private static ArenaSession CloneSession(ArenaSession session)
        {
            if (session == null)
            {
                return null;
            }

            return new ArenaSession
            {
                SessionId = session.SessionId,
                ArenaId = session.ArenaId,
                Request = CloneRequest(session.Request),
                Plan = ClonePlan(session.Plan),
                Phase = session.Phase,
                Outcome = session.Outcome,
                EncounterIndex = session.EncounterIndex,
                LivingArenaEnemyCount = session.LivingArenaEnemyCount,
                SpawnedEnemyIds = session.SpawnedEnemyIds == null
                    ? new List<string>()
                    : new List<string>(session.SpawnedEnemyIds),
                ResourceSnapshot = CloneResourceSnapshot(session.ResourceSnapshot),
                PhaseStartedUtc = session.PhaseStartedUtc,
                PhaseDeadlineUtc = session.PhaseDeadlineUtc,
                SessionStartedUtc = session.SessionStartedUtc,
                ElapsedMilliseconds = session.ElapsedMilliseconds
            };
        }
    }
}
