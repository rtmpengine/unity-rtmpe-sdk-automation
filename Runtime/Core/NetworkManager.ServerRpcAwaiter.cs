// RTMPE SDK — Runtime/Core/NetworkManager.ServerRpcAwaiter.cs
//
// Awaiter pattern for Enhanced RPCs that target the server
// (RpcTarget.Server, 0x02): callers obtain a Task<RpcResponse> from
// SendEnhancedRpcAsync, the inbound RpcResponse path is correlated by
// request_id, and the awaiting continuation runs on the synchronization
// context captured at the `await` boundary.
//
// Lifecycle:
//   1. SendEnhancedRpcAsync allocates a CSPRNG-backed request_id, stores a
//      TaskCompletionSource keyed by that id, registers the matching
//      timeout entry with the shared RequestIdAllocator, and sends the
//      Enhanced RPC packet.
//   2. The gateway forwards the call to the Room Service, which dispatches
//      the bound handler and publishes a 21-byte RpcResponse back through
//      the gateway's BroadcastReceiver.  The receive path in
//      NetworkManager.ReceivePath.cs unpacks the response and calls
//      [TryCompleteServerRpc].
//   3. On match the TCS completes with the parsed [RpcResponse]; on
//      timeout (via [RequestIdAllocator.PurgeExpired]) the registered
//      onTimeout callback cancels the same TCS.
//   4. On session teardown [ClearSessionData] (already invokes
//      [RequestIdAllocator.DropPending]) fires every pending timeout
//      callback so the awaiter throws OperationCanceledException to its
//      caller instead of dangling indefinitely.
//
// Threading: the dictionary is guarded by a dedicated lock; TaskCompletion-
// Sources are constructed with [TaskCreationOptions.RunContinuationsAsynchronously]
// so continuation work never inlines onto the receive path and cannot stall
// further inbound packets.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

using RTMPE.Core;
using RTMPE.Rpc;

namespace RTMPE.Core
{
    // All partial declarations of a sealed class must carry the sealed modifier;
    // the C# compiler enforces consistency across translation units.
    public sealed partial class NetworkManager
    {
        /// <summary>
        /// 30 seconds: the timeout <see cref="SendEnhancedRpcAsync"/> uses when you pass none.
        /// </summary>
        public static readonly TimeSpan DefaultServerRpcTimeout = TimeSpan.FromSeconds(30);

        // pendingServerRpcEntry bundles the TaskCompletionSource together with
        // any external resources that must be released when the awaiter ends —
        // currently the CancellationTokenRegistration produced by the
        // caller-supplied CancellationToken.  Without disposing the
        // registration, a long-lived token (e.g. a session-scoped CTS feeding
        // thousands of RPCs) accumulates callback entries inside the token's
        // internal list — every awaiter that completes naturally leaves its
        // registration behind to be cleaned up only when the token itself is
        // disposed.
        private struct pendingServerRpcEntry
        {
            public TaskCompletionSource<RpcResponse> Tcs;
            public CancellationTokenRegistration Registration;
        }

        // request_id → pending entry bound to a single inbound RpcResponse.
        // Keyed by the wire-level identifier so the receive path can complete
        // the awaiter without any caller-side bookkeeping.
        //
        // Threading: every read or write acquires _pendingServerRpcsLock.
        // Continuations themselves run asynchronously (RunContinuations-
        // Asynchronously option set at construction) so this lock is held
        // only across a hash-map mutation.
        private readonly Dictionary<uint, pendingServerRpcEntry>
            _pendingServerRpcs = new Dictionary<uint, pendingServerRpcEntry>();

        private readonly object _pendingServerRpcsLock = new object();

        /// <summary>
        /// Calls a <see cref="RpcTarget.Server"/> method of <paramref name="sender"/>'s type and
        /// completes with the server's answer.
        /// </summary>
        /// <remarks>
        /// <para>The task completes with an <see cref="RpcResponse"/> when the answer arrives. A
        /// refusal by your server function is an answer with <see cref="RpcResponse.Success"/>
        /// <see langword="false"/>, not an exception. Only answers from the server complete the
        /// task; an answer from another player is ignored and counted in
        /// <see cref="ServerRpcAnswersRefusedCount"/>.</para>
        /// <para>The task is cancelled when the timeout has passed, when
        /// <paramref name="cancellationToken"/> is cancelled, and when the session ends;
        /// awaiting it then throws <see cref="TaskCanceledException"/>. The timeout is checked
        /// every 5 seconds, so the task can be cancelled up to about 5 seconds after it;
        /// <paramref name="cancellationToken"/> takes effect at once. A token that is already
        /// cancelled returns a cancelled task and sends nothing.</para>
        /// </remarks>
        /// <param name="sender">The component whose object the call addresses.</param>
        /// <param name="methodName">The name of the <c>[RtmpeRpc(RpcTarget.Server)]</c> method.</param>
        /// <param name="args">The arguments, in the method's parameter order.</param>
        /// <param name="timeout">How long to wait for the answer; <see cref="DefaultServerRpcTimeout"/>
        /// when <see langword="null"/>.</param>
        /// <param name="cancellationToken">Cancels the wait.</param>
        /// <returns>A task that completes with the server's answer.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown at the call, before anything is sent: not in a room, a <see langword="null"/>
        /// <paramref name="sender"/>, an unknown method name, a method whose target is not
        /// <see cref="RpcTarget.Server"/>, or a declared caller this client does not satisfy.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown at the call, before anything is sent: arguments that cannot be encoded or are
        /// too large.
        /// </exception>
        public Task<RpcResponse> SendEnhancedRpcAsync(
            NetworkBehaviour sender,
            string methodName,
            object[] args,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsInRoom)
            {
                throw new InvalidOperationException(
                    "[RTMPE] NetworkManager.SendEnhancedRpcAsync: must be in a room.");
            }

            if (sender == null)
            {
                throw new InvalidOperationException(
                    "[RTMPE] NetworkManager.SendEnhancedRpcAsync: sender is null.");
            }

            if (!RpcRegistry.TryGetMethodId(sender.GetType(), methodName, out uint methodId))
            {
                throw new InvalidOperationException(
                    $"[RTMPE] NetworkManager.SendEnhancedRpcAsync: no [RtmpeRpc] method named " +
                    $"'{methodName}' on {sender.GetType().Name}.  Ensure the method is public " +
                    "and decorated with [RtmpeRpc].");
            }

            // Resolve the target up front so this path can refuse silently
            // when the attribute is bound to a non-server target — the
            // synchronous SendEnhancedRpc remains the appropriate entry
            // point for fire-and-forget targets and would dispatch the
            // packet on the same wire.
            RpcRegistry.TryFindMethod(sender.GetType(), methodId, out _, out var attr);
            var target = attr?.Target ?? RpcTarget.All;
            if (target != RpcTarget.Server)
            {
                throw new InvalidOperationException(
                    $"[RTMPE] NetworkManager.SendEnhancedRpcAsync: method " +
                    $"'{sender.GetType().Name}.{methodName}' is bound to RpcTarget.{target}; " +
                    "use SendEnhancedRpc for fire-and-forget targets.");
            }

            // A declared caller is checked here as the fire-and-forget path
            // checks it, before anything is registered: no client receives a
            // server call, so this is the only place the declaration is read —
            // the server function decides by the facts it is given.
            var declared = attr?.Caller ?? RpcCaller.Anyone;
            string callerRefusal = EnhancedRpcVerifier.CallerSendRefusal(
                declared, target, _attestedRpcCaller, IsMasterClient, sender.IsOwner);
            if (callerRefusal != null)
            {
                throw new InvalidOperationException(
                    $"[RTMPE] NetworkManager.SendEnhancedRpcAsync: " +
                    $"'{sender.GetType().Name}.{methodName}' declares Caller = {declared}, " +
                    $"and {callerRefusal}.");
            }

            // RunContinuationsAsynchronously keeps the receive-path call
            // site (TryCompleteServerRpc) free of caller-supplied
            // continuation work — long-running awaiters cannot stall the
            // inbound packet pipeline.
            var tcs = new TaskCompletionSource<RpcResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            uint requestId = RequestIdAllocator.Next();

            byte[] rpcPayload;
            try
            {
                rpcPayload = EnhancedRpcPacketBuilder.Build(
                    methodId, LocalPlayerId, requestId,
                    sender.NetworkObjectId, target, args);
            }
            catch (ArgumentException ex)
            {
                // ⚠️ S4-32. An argument the caller got wrong stays an
                // ArgumentException, as the framing catch below already keeps
                // it. Every throw EnhancedRpcPacketBuilder.Build makes is one —
                // an oversized payload among them — and collapsing them into
                // InvalidOperationException contradicted this method's own
                // documented contract forty lines above, silently: an
                // integrator's `catch (ArgumentException)` compiled, ran, and
                // caught nothing on the one path most likely to fire.
                throw new ArgumentException(
                    $"[RTMPE] NetworkManager.SendEnhancedRpcAsync: cannot build the payload " +
                    $"for '{sender.GetType().Name}.{methodName}' — {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                // Anything else is a fault of ours rather than of the call. The
                // payload builder failed before the awaiter was registered, so
                // no cleanup of the pending map is required; surface it
                // synchronously to mirror the precondition checks above.
                throw new InvalidOperationException(
                    $"[RTMPE] NetworkManager.SendEnhancedRpcAsync: failed to build packet " +
                    $"for '{sender.GetType().Name}.{methodName}': {ex.Message}", ex);
            }

            // An already-cancelled token must surface immediately rather than
            // burn the full request_id allocation and ride the timeout sweep
            // out to the 30 s ceiling.  The synchronous-cancel short-circuit
            // also avoids the subtler hazard that
            // CancellationToken.Register invokes its callback inline when the
            // token is already in the cancelled state — that callback would
            // run BEFORE the entry could be inserted into _pendingServerRpcs,
            // leaving the entry orphaned until the TTL sweep.
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return tcs.Task;
            }

            // Framed here, before anything is registered.  ⛔ Not merely
            // guarded: on this path a throw from BuildPacket used to happen
            // AFTER the awaiter was in _pendingServerRpcs and after the request
            // id was registered with the timeout sweep, so an oversized call
            // both threw synchronously and left a TaskCompletionSource nobody
            // would ever complete — resolved thirty seconds later by the sweep,
            // as a timeout, for a request that was never sent.  Building first
            // removes the window rather than reporting from inside it.
            //
            // ⚠️ This does not weaken the ordering the registration comment
            // below relies on: that one is about SEND, which still happens
            // after the awaiter exists. And it sits AFTER the already-cancelled
            // short-circuit, so a caller passing a cancelled token still gets a
            // cancelled Task rather than paying the framing — moving it above
            // that check would have quietly changed the cancel contract.
            byte[] packet;
            try
            {
                packet = BuildPacket(
                    PacketType.Rpc,
                    PacketFlags.Reliable | PacketFlags.EnhancedRpc,
                    rpcPayload);
            }
            catch (ArgumentException ex)
            {
                // ⚠️ Rethrown as ArgumentException, which is what this path
                // threw before the framing moved above the registration. An
                // integrator's `catch (ArgumentException)` around an RPC call
                // keeps working; changing the type would have broken their
                // error handling with no compile error to warn them, and an
                // InnerException does not help a type-based catch.
                throw new ArgumentException(
                    $"[RTMPE] NetworkManager.SendEnhancedRpcAsync: cannot frame the packet " +
                    $"for '{sender.GetType().Name}.{methodName}' — {ex.Message}", ex);
            }

            // Register the awaiter BEFORE the packet leaves the host so a
            // very-fast in-process response (e.g. integration tests with a
            // loopback gateway) cannot reach TryCompleteServerRpc before
            // the entry exists.  The insertion also precedes the
            // cancellation-token registration so any future change to the
            // token-registration timing cannot resurrect the orphan-entry
            // race surfaced by an inline-firing callback.
            lock (_pendingServerRpcsLock)
            {
                _pendingServerRpcs[requestId] = new pendingServerRpcEntry
                {
                    Tcs          = tcs,
                    Registration = default,
                };
            }

            // Caller-supplied cancellation chain.  CanBeCanceled is false
            // for CancellationToken.None — register only when the caller
            // actually supplied a source.  The registration is stored on
            // the pending entry so its Dispose runs alongside the awaiter's
            // terminal state, preventing a long-lived token from
            // accumulating callback entries across thousands of completed
            // awaiters.
            if (cancellationToken.CanBeCanceled)
            {
                var registration = cancellationToken.Register(
                    () => CancelPendingServerRpc(requestId));
                lock (_pendingServerRpcsLock)
                {
                    if (_pendingServerRpcs.TryGetValue(requestId, out var existing))
                    {
                        existing.Registration = registration;
                        _pendingServerRpcs[requestId] = existing;
                    }
                    else
                    {
                        // Cancellation already raced past us and removed the
                        // entry — release the registration ourselves rather
                        // than leaking it on the token.
                        registration.Dispose();
                    }
                }
            }

            // Wire the deadline into RequestIdAllocator so the existing
            // PurgeExpired sweep on Update() (and DropPending on session
            // teardown) drive cancellation without a per-call coroutine.
            RequestIdAllocator.RegisterPending(
                requestId,
                timeout ?? DefaultServerRpcTimeout,
                () => CancelPendingServerRpc(requestId));

            Send(packet, reliable: true);

            return tcs.Task;
        }

        /// <summary>
        /// Look up the pending awaiter for <paramref name="response"/> and,
        /// when present, complete it with the parsed value.  Returns true
        /// when an awaiter consumed the response so the receive path can
        /// short-circuit the per-method dispatch table.
        ///
        /// Idempotent under concurrent completion — duplicate responses or
        /// late arrivals after cancellation collapse onto the existing
        /// terminal state of the TCS.
        /// </summary>
        internal bool TryCompleteServerRpc(in RpcResponse response)
        {
            pendingServerRpcEntry entry;
            lock (_pendingServerRpcsLock)
            {
                if (!_pendingServerRpcs.TryGetValue(response.RequestId, out entry))
                    return false;

                // ⛔ Only the server answers a server call.  The server's
                // answer carries sender id zero, and every answer a client
                // sends is stamped with that client's session by the gateway —
                // so an answer naming a session is a peer's, and a peer
                // answering a server call is a verdict no server gave: a
                // purchase "confirmed", a hit "validated".  It is consumed
                // without completing anything, and the awaiter stays open for
                // the server's own answer or its deadline.
                if (!IsServerAnswer(response))
                {
                    _serverRpcAnswersRefused++;
                    if (WarnGate.ShouldEmit(ref _lastServerRpcForgedAnswerWarnTicks))
                        Debug.LogWarning(
                            "[RTMPE] NetworkManager: an answer to a server call arrived from " +
                            $"session {LogRedaction.Redact(response.SenderId)}, not from the server " +
                            $"(method 0x{response.MethodId:X8}); ignored.");
                    return true;
                }

                _pendingServerRpcs.Remove(response.RequestId);
            }
            // Release the caller's cancellation registration first.  Dispose
            // is idempotent and safe even when the caller passed
            // CancellationToken.None (default Registration is a no-op).
            entry.Registration.Dispose();
            // Mark the id as resolved so the RequestIdAllocator sweep does
            // not later fire the timeout callback against a stale entry —
            // Resolve is a no-op if the entry has already been pruned, so
            // this is safe to call unconditionally.
            RequestIdAllocator.Resolve(response.RequestId);
            entry.Tcs.TrySetResult(response);
            return true;
        }

        /// <summary>
        /// Whether <paramref name="response"/> is the server's answer rather
        /// than a peer's: the server writes sender id zero, and the gateway
        /// stamps every answer a client sends with that client's session.
        /// </summary>
        internal static bool IsServerAnswer(in RpcResponse response) => response.SenderId == 0UL;

        /// <summary>
        /// Answers to this client's server calls that came from another player instead of the
        /// server; they are ignored. <c>0</c> on a healthy session.
        /// </summary>
        /// <remarks>Reset when the session ends.</remarks>
        public long ServerRpcAnswersRefusedCount => _serverRpcAnswersRefused;

        private long _serverRpcAnswersRefused;
        private long _lastServerRpcForgedAnswerWarnTicks;

        // CancelPendingServerRpc completes the TCS bound to `requestId`
        // with cancellation, releases the caller's cancellation
        // registration, and drops the dictionary entry.  Invoked by the
        // timeout sweep, the caller's CancellationToken registration, and
        // the session-teardown path indirectly via RequestIdAllocator.
        // Idempotent — repeated calls after the first cancel are no-ops.
        private void CancelPendingServerRpc(uint requestId)
        {
            pendingServerRpcEntry entry;
            lock (_pendingServerRpcsLock)
            {
                if (!_pendingServerRpcs.TryGetValue(requestId, out entry))
                    return;
                _pendingServerRpcs.Remove(requestId);
            }
            entry.Registration.Dispose();
            entry.Tcs.TrySetCanceled();
        }

        /// <summary>
        /// Drain every pending server-RPC awaiter, cancelling each in turn.
        /// Invoked from <see cref="ClearSessionData(bool)"/> so a
        /// disconnect, reconnect, or domain reload cannot leave continuations
        /// dangling against torn-down NetworkManager state — every caller
        /// that was awaiting a response observes
        /// <see cref="OperationCanceledException"/> instead of a hanging
        /// task that never resolves.
        ///
        /// Returns the count of awaiters drained, for diagnostics.
        /// </summary>
        internal int DrainPendingServerRpcs()
        {
            List<pendingServerRpcEntry> snapshot;
            lock (_pendingServerRpcsLock)
            {
                if (_pendingServerRpcs.Count == 0) return 0;
                snapshot = new List<pendingServerRpcEntry>(_pendingServerRpcs.Values);
                _pendingServerRpcs.Clear();
            }
            foreach (var entry in snapshot)
            {
                entry.Registration.Dispose();
                entry.Tcs.TrySetCanceled();
            }
            return snapshot.Count;
        }

        /// <summary>
        /// Pending awaiter count — exposed for diagnostics and tests.  Safe
        /// for concurrent reads relative to ongoing Send / Receive work.
        /// </summary>
        internal int PendingServerRpcCount
        {
            get
            {
                lock (_pendingServerRpcsLock) return _pendingServerRpcs.Count;
            }
        }
    }
}
