using System;
using System.Runtime.InteropServices;
using System.Threading;
using Prometheus;
using Robust.Shared.Enums;
using Robust.Shared.Log;
using Robust.Shared.Network.Messages;
using Robust.Shared.Player;
using Robust.Shared.Threading;
using Robust.Shared.Utility;

namespace Robust.Server.GameStates;

// Partial class for handling entities leaving a player's pvs range.
internal sealed partial class PvsSystem
{
    private WaitHandle? _leaveTask;

    private void ProcessLeavePvs(ICommonSession[] sessions)
    {
        if (!CullingEnabled || sessions.Length == 0)
            return;

        DebugTools.AssertNull(_leaveTask);
        _leaveJob.Setup(sessions);

        if (_async)
        {
            _leaveTask = _parallelMgr.Process(_leaveJob, _leaveJob.Count);
            return;
        }

        using var _ = Histogram.WithLabels("Process Leave").NewTimer();
        _parallelMgr.ProcessNow(_leaveJob, _leaveJob.Count);
    }

    /// <summary>
    /// Figure out what entities are no longer visible to the client. These entities are sent reliably to the client
    /// in a separate net message. This has to be called after EntityData.LastSent is updated.
    /// </summary>
    private void ProcessLeavePvs(PvsSession session)
    {
        if (session.DisableCulling || session.Session.Status != SessionStatus.InGame)
            return;

        if (session.LastSent == null)
            return;

        var (toTick, lastSent) = session.LastSent.Value;
        foreach (var data in CollectionsMarshal.AsSpan(lastSent))
        {
            if (data.LastSeen == toTick)
                continue;

            session.LeftView.Add(data.NetEntity);
            data.LastLeftView = toTick;
        }

        if (session.LeftView.Count == 0)
            return;

        var pvsMessage = new MsgStateLeavePvs {Entities = session.LeftView, Tick = toTick};

        // PVS benchmarks use dummy sessions.
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (session.Session.Status == SessionStatus.InGame && session.Channel != null)
            _netMan.ServerSendMessage(pvsMessage, session.Channel);

        session.LeftView.Clear();
    }

    private record struct PvsLeaveJob(PvsSystem _pvs) : IParallelRobustJob
    {
        public int BatchSize => 2;
        private PvsSystem _pvs = _pvs;
        public int Count => _sessions.Length;
        private PvsSession[] _sessions;

        public void Execute(int index)
        {
            try
            {
                _pvs.ProcessLeavePvs(_sessions[index]);
            }
            catch (Exception e)
            {
                _pvs.Log.Log(LogLevel.Error, e, $"Caught exception while processing pvs-leave messages.");
            }
        }

        public void Setup(ICommonSession[] sessions)
        {
            // Copy references to PvsSession, in case players disconnect while the job is running.
            Array.Resize(ref _sessions, sessions.Length);
            for (var i = 0; i < sessions.Length; i++)
            {
                _sessions[i] = _pvs.PlayerData[sessions[i]];
            }
        }
    }
}
