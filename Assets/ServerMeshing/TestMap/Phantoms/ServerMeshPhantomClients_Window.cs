// Server Meshing add-on — rig phantom binder, window part: counts and samples the phantoms and logs a Phantom window line with every Work window.
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class ServerMeshPhantomClients
    {
        private void WindowIfDue(MeshMapNetworkManager mesh, double now)
        {
            int work = mesh.WorkWindowCount;
            if (work == _lastWorkWindow)
                return;
            if (_lastWorkWindow < 0)
            {
                _lastWorkWindow = work;
                StartWindow(now);
                return;
            }
            _lastWorkWindow = work;
            long sampleStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            Sample(mesh, now);
            long sampled = System.Diagnostics.Stopwatch.GetTimestamp() - sampleStarted;
            double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _window.Seconds = now - _windowStart;
            _window.Frames = _windowFrames;
            _window.SamplerMs = sampled * toMs;
            _window.BinderMs += _binderTicks * toMs;
            _log.Info(_window.ToLine());
            if (_settings.FightSet)
                _log.Info(_fighter.TakeLine(_window.Seconds));
            CheckRate(_window.Frames, _window.Seconds);
            StartWindow(now);
        }

        private void StartWindow(double now)
        {
            _window.Reset();
            _windowStart = now;
            _windowFrames = 0;
            _binderTicks = 0;
            if (_viewSampler != null)
                _viewSampler.BeginWindow(_viewRound++);
            if (_transport == null)
                return;
            for (int i = 0; i < ServerMeshPhantomTransport.DeliverySlots; ++i)
            {
                _swallowedCallsBase[i] = _transport.SwallowedCalls(i);
                _swallowedBytesBase[i] = _transport.SwallowedBytes(i);
            }
        }

        private void Sample(MeshMapNetworkManager mesh, double now)
        {
            double seconds = now - _windowStart;
            int boundVirtual = 0, boundSink = 0, ready = 0, eyeSeen = 0;
            for (int k = 0; k < _slots.Count; ++k)
            {
                ServerMeshPhantomSlot slot = _slots[k];
                if (!slot.bound)
                    continue;
                if (slot.sink)
                    ++boundSink;
                else
                    ++boundVirtual;
                if (mesh.TryGetPlayer(slot.connectionId, out LiteNetLibPlayer player) && player.IsReady)
                    ++ready;
                if (slot.eye != null && slot.identity != null && ServerMeshPhantomWindow.IsSeenByOthers(slot.identity.CountSubscribers(), slot.identity.HasSubscriber(slot.connectionId)))
                    ++eyeSeen;
                if (!slot.sink && seconds > 0)
                {
                    long bytes = _transport.PayloadBytesOf(slot.connectionId);
                    _window.AddDown((bytes - slot.lastDownBytes) / 1024.0 / seconds);
                    slot.lastDownBytes = bytes;
                }
            }
            int bound = boundVirtual + boundSink;
            int size = ServerMeshPhantomWindow.SampleSize(bound);
            int skip = ServerMeshPhantomWindow.SampleStart(_sampleRound++, bound);
            for (int k = 0, seen = 0, taken = 0; k < _slots.Count && taken < size; ++k)
            {
                ServerMeshPhantomSlot slot = _slots[k];
                if (!slot.bound || seen++ < skip || !mesh.TryGetPlayer(slot.connectionId, out LiteNetLibPlayer player))
                    continue;
                _window.AddVMove(ServerMeshPhantomWindow.CountMovers(player.GetSubscribingObjectIds(), mesh.EntityMovementDataHandlers, slot.objectId));
                ++taken;
            }
            int filler = 0;
            for (int i = 0; i < _filler.Count; ++i)
            {
                if (_filler[i] != null && !_filler[i].IsDestroyed)
                    ++filler;
            }
            _window.BoundVirtual = boundVirtual;
            _window.BoundSink = boundSink;
            _window.Ready = ready;
            _window.Filler = filler;
            _window.EyeSeen = eyeSeen;
            _window.Fighting = _fighter.Fighting;
            _window.StepIndex = _settings.Steps != null ? _stepIndex + 1 : 0;
            _window.StepCount = _settings.Steps != null ? _settings.Steps.Length : 0;
            _window.Target = _target;
            for (int i = 0; i < ServerMeshPhantomTransport.DeliverySlots; ++i)
                _window.SetSwallowed(i, _transport.SwallowedCalls(i) - _swallowedCallsBase[i], _transport.SwallowedBytes(i) - _swallowedBytesBase[i]);
        }

        private int CountVirtualBound()
        {
            int bound = 0;
            for (int k = 0; k < _slots.Count; ++k)
            {
                if (_slots[k].bound && !_slots[k].sink)
                    ++bound;
            }
            return bound;
        }

        private int CountVirtualActive()
        {
            int active = 0;
            for (int k = 0; k < _slots.Count; ++k)
            {
                if (_slots[k].connectionId >= 0 && !_slots[k].sink)
                    ++active;
            }
            return active;
        }

        private void CheckRate(int frames, double seconds)
        {
            if (_warnedRate || seconds <= 0 || frames <= 0 || _settings.Kind == ServerMeshPhantomKind.Eye)
                return;
            int frameRate = Application.targetFrameRate > 0 ? Application.targetFrameRate : (int)System.Math.Round(frames / seconds);
            if (_settings.UploadHz <= frameRate)
                return;
            _warnedRate = true;
            _log.Warning(System.FormattableString.Invariant($"Phantoms: upload Hz {_settings.UploadHz} above the frame rate {frameRate}: at most {frameRate} delivered"));
        }
    }
}
